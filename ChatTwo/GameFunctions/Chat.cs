using System.Text;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Text.ReadOnly;

using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace ChatTwo.GameFunctions;

public sealed unsafe class Chat : IDisposable
{
    // Functions
    [Signature("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8D B9 ?? ?? ?? ?? 33 C0")]
    private readonly delegate* unmanaged<RaptureLogModule*, ushort, Utf8String*, Utf8String*, ulong, ulong, ushort, byte, int, byte, void> PrintTellNative = null!;

    [Signature("E8 ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8D 8C 24 ?? ?? ?? ?? E8 ?? ?? ?? ?? B0 ?? 48 8B 8C 24")]
    private readonly delegate* unmanaged<NetworkModule*, ulong, ushort, Utf8String*, Utf8String*, ushort, ushort, byte> SendTellNative = null!;

    // Client::UI::AddonChatLog.OnRefresh
    [Signature("40 53 57 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 4D 8B F8", DetourName = nameof(ChatLogRefreshDetour))]
    private Hook<ChatLogRefreshDelegate>? ChatLogRefreshHook = null!;
    private delegate byte ChatLogRefreshDelegate(nint log, ushort eventId, AtkValue* value);

    // Replace with CS version later
    [Signature("48 89 5C 24 ?? 55 56 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 83 B9", DetourName = nameof(ContextMenuTellInForayDetour))]
    private Hook<ContextMenuTellInForayDelegate>? ContextMenuTellInForayHook = null!;
    private delegate void ContextMenuTellInForayDelegate(RaptureShellModule* module, Utf8String* playerName, Utf8String* worldName, ushort worldId, ulong accountId, ulong contentId, ushort reason);

    private readonly Hook<AgentChatLog.Delegates.ChangeChannelName>? ChangeChannelNameHook;
    private readonly Hook<RaptureShellModule.Delegates.ReplyInSelectedChatMode>? ReplyInSelectedChatModeHook;
    private readonly Hook<RaptureShellModule.Delegates.SetContextTellTarget>? SetChatLogTellTargetHook;

    // Pointers
    [Signature("48 8D 35 ?? ?? ?? ?? 8B 05", ScanType = ScanType.StaticAddress)]
    private readonly char* LastTypedCharacter = null!;

    private Plugin Plugin { get; }

    public Chat(Plugin plugin)
    {
        Plugin = plugin;
        Plugin.GameInteropProvider.InitializeFromAttributes(this);

        ChatLogRefreshHook?.Enable();
        ContextMenuTellInForayHook?.Enable();

        ChangeChannelNameHook = Plugin.GameInteropProvider.HookFromAddress<AgentChatLog.Delegates.ChangeChannelName>(AgentChatLog.MemberFunctionPointers.ChangeChannelName, ChangeChannelNameDetour);
        ChangeChannelNameHook.Enable();

        ReplyInSelectedChatModeHook = Plugin.GameInteropProvider.HookFromAddress<RaptureShellModule.Delegates.ReplyInSelectedChatMode>(RaptureShellModule.MemberFunctionPointers.ReplyInSelectedChatMode, ReplyInSelectedChatModeDetour);
        ReplyInSelectedChatModeHook.Enable();

        SetChatLogTellTargetHook = Plugin.GameInteropProvider.HookFromAddress<RaptureShellModule.Delegates.SetContextTellTarget>(RaptureShellModule.MemberFunctionPointers.SetContextTellTarget, SetContextTellTarget);
        SetChatLogTellTargetHook.Enable();

        Plugin.ClientState.Login += Login;
        Login();
    }

    public void Dispose()
    {
        Plugin.ClientState.Login -= Login;

        SetChatLogTellTargetHook?.Dispose();
        ReplyInSelectedChatModeHook?.Dispose();
        ChangeChannelNameHook?.Dispose();
        ChatLogRefreshHook?.Dispose();
        ContextMenuTellInForayHook?.Dispose();
    }

    // Shared throttle state for the resolution guards below. Everything that logs through here runs
    // on the main thread (game detours plus ImGui draw callbacks), so no locking is needed. A
    // module- or signature-resolution failure is permanent for the rest of the session, so an
    // unthrottled Error would repeat on every channel change and every send for as long as the game
    // is running. Error level is deliberate: users run LogLevel 2, so Debug/Verbose would never
    // reach them, and this is exactly the sort of thing we want reported back.
    private static readonly TimeSpan LogThrottleInterval = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<string, (DateTime Last, int Suppressed)> ThrottledLogs = new();

    // internal static rather than instance: ChatBox is a static class and needs the same throttle.
    // Sharing the one dictionary is deliberate - a second throttling mechanism would defeat the
    // point, since the failures being reported are process-wide and permanent for the session.
    internal static void LogErrorThrottled(string key, Exception ex, string message)
    {
        var now = DateTime.UtcNow;
        ThrottledLogs.TryGetValue(key, out var state);
        if (state.Last != default && now - state.Last < LogThrottleInterval)
        {
            ThrottledLogs[key] = (state.Last, state.Suppressed + 1);
            return;
        }

        ThrottledLogs[key] = (now, 0);
        if (state.Suppressed > 0)
            Plugin.Log.Error(ex, $"{message} (+{state.Suppressed} suppressed since the last message)");
        else
            Plugin.Log.Error(ex, message);
    }

    // Resolves a ClientStructs module while guarding BOTH of the failure modes an Instance() can
    // have, returning null if either one fires.
    //
    // FFXIVClientStructs' Instance() getters fail in *opposite* ways, which is exactly why one guard
    // is never enough on its own. Verified against the generator templates in lib/FFXIVClientStructs,
    // not assumed:
    //   - [StaticAddress] and [MemberFunction] generated ones THROW InvalidOperationException when
    //     their signature does not resolve (InteropGenerator.Rendering.cs emits
    //     `if (pointer is null) ThrowHelper.ThrowNullAddress(...)`). A plain [StaticAddress] can
    //     never additionally return null; `isPointer: true` can, because it returns *ppInstance and
    //     the singleton may not be constructed yet.
    //   - [VirtualFunction] ones never throw - they render as `VirtualTable->Name(this)` - but they
    //     dereference `this`, so a null receiver is an access violation with nothing to catch it.
    //   - The hand-written chained ones (UIModule, RaptureShellModule, PronounModule,
    //     RaptureLogModule, AcquaintanceModule, RaptureAtkModule, UIInputData, RaptureTextModule,
    //     ItemFinderModule) RETURN NULL, but they stack BOTH modes onto a single expression: the
    //     null return is their own, and the throw comes from Framework.Instance() - a
    //     [StaticAddress] stub - further down the chain.
    //   - [Agent] and [InfoProxy] generated ones ALSO have BOTH modes. CORRECTION to what an earlier
    //     pass wrote here: the templates really are just
    //     `agentModule == null ? null : (T*)agentModule->GetAgentByInternalId(id)` and
    //     `infoModule  == null ? null : (T*)infoModule->GetInfoProxyById(id)`
    //     (FFXIVClientStructs.Generators/{Agent,InfoProxy}GetterGenerator.cs), so reading only the
    //     template says "returns null, never throws" - but that reads one link of a four-link chain.
    //     AgentModule.Instance()/InfoModule.Instance() are hand-written chained getters that go
    //     through UIModule.Instance() -> Framework.Instance() ([StaticAddress(isPointer: true)],
    //     THROWS) -> framework->GetUIModule() ([MemberFunction], THROWS), and the final
    //     GetAgentByInternalId/GetInfoProxyById are themselves [MemberFunction] (THROWS). Three
    //     throw sites behind a template that contains none. Treat these exactly like the chained
    //     kind: try AND null check.
    //
    // Handling only one of them is fake protection. Dereferencing the null return is an
    // AccessViolationException, a corrupted-state exception that try/catch cannot intercept in .NET
    // Core at all; and a throw that escapes a detour back into native code terminates the process.
    // Callers must therefore both use this instead of Instance() and null-check what comes back.
    //
    // Note that Framework.Instance() itself has both modes too: [StaticAddress(isPointer: true)]
    // generates `if (ppInstance is null) throw; return *ppInstance;`, so it throws on an unresolved
    // signature but still returns null before the game has constructed the Framework. ClientStructs
    // agrees - every chained Instance() above starts with its own `framework == null` check.
    //
    // internal rather than private: Context, GameFunctions, Party, KeybindManager, Message,
    // GlobalParametersCache and the Debugger window all need the same guard, and a hand-copied one
    // is exactly how a copy ends up with only half of it.
    internal static T* ResolveOrNull<T>(delegate*<T*> instance, string logKey, string logMessage) where T : unmanaged
    {
        try
        {
            return instance();
        }
        catch (Exception ex)
        {
            LogErrorThrottled(logKey, ex, logMessage);
            return null;
        }
    }

    private static RaptureShellModule* GetRaptureShellModuleOrNull(string logKey, string logMessage)
        => ResolveOrNull<RaptureShellModule>(&RaptureShellModule.Instance, logKey, logMessage);

    // internal rather than private: ChatBox.SendMessageUnsafe and ChatLog.Window need a guarded
    // UIModule too, and a hand-copied guard is exactly how one copy ends up missing a half.
    internal static UIModule* GetUIModuleOrNull(string logKey, string logMessage)
        => ResolveOrNull<UIModule>(&UIModule.Instance, logKey, logMessage);

    // Both of these resolve through an [InfoProxy] getter. An earlier pass added the null check here
    // and stated in the comment that no try was needed because that generator "never throws"; see
    // the corrected taxonomy above - it throws in three places behind the template. The resolve now
    // goes through ResolveOrNull, and the null check stays because ResolveOrNull also returns null
    // on the catch path (and GetInfoProxyById genuinely can hand back null for a proxy that is not
    // registered yet, i.e. early login).
    //
    // The name-fetching calls themselves are [MemberFunction] too, so they get the same treatment:
    // GetLinkShellName / GetCrossworldLinkshellName throw on an unresolved signature.
    //
    // Degradation: returning null is a value both callers already handle - ChatLog.Window skips the
    // channel in the switcher list (string.IsNullOrWhiteSpace) and renders an empty name in the
    // input line, exactly as it does for an unassigned linkshell.
    public static string? GetLinkshellName(uint idx)
    {
        var proxy = Chat.ResolveOrNull<InfoProxyChat>(&InfoProxyChat.Instance, "GetLinkshellName/proxy", "Could not resolve InfoProxyChat; the linkshell name is unavailable");
        if (proxy == null)
            return null;

        try
        {
            var utf = proxy->GetLinkShellName(idx);
            return utf.HasValue ? utf.ToString() : null;
        }
        catch (Exception ex)
        {
            LogErrorThrottled("GetLinkshellName/call", ex, "Could not read the linkshell name");
            return null;
        }
    }

    public static string? GetCrossLinkshellName(uint idx)
    {
        var proxy = Chat.ResolveOrNull<InfoProxyCrossWorldLinkshell>(&InfoProxyCrossWorldLinkshell.Instance, "GetCrossLinkshellName/proxy", "Could not resolve InfoProxyCrossWorldLinkshell; the cross-world linkshell name is unavailable");
        if (proxy == null)
            return null;

        try
        {
            var utf = proxy->GetCrossworldLinkshellName(idx);
            return utf != null ? utf->ToString() : null;
        }
        catch (Exception ex)
        {
            LogErrorThrottled("GetCrossLinkshellName/call", ex, "Could not read the cross-world linkshell name");
            return null;
        }
    }

    private static int GetRotateIdx(RotateMode mode) => mode switch
    {
        RotateMode.Forward => 1,
        RotateMode.Reverse => -1,
        _ => 0,
    };

    // UIModule.Instance() is the hand-written chained kind, so it needs both halves: the try (from
    // Framework.Instance() deep inside) and the null check. The rotate calls themselves are
    // [VirtualFunction], generated as `VirtualTable->Rotate...(this)`, so they add no throw of their
    // own - but they dereference `this`, which makes a null UIModule an AccessViolationException
    // rather than anything catchable. Degradation: the linkshell cycle is not advanced, so the
    // keybind does nothing this press instead of taking the game down.
    public static void RotateLinkshellHistory(RotateMode mode)
    {
        var uiModule = GetUIModuleOrNull("RotateLinkshellHistory/uiModule", "Could not resolve UIModule; the linkshell history was not rotated");
        if (uiModule == null)
            return;

        if (mode == RotateMode.None)
            uiModule->LinkshellCycle = -1;

        uiModule->RotateLinkshellHistory(GetRotateIdx(mode));
    }

    public static void RotateCrossLinkshellHistory(RotateMode mode)
    {
        var uiModule = GetUIModuleOrNull("RotateCrossLinkshellHistory/uiModule", "Could not resolve UIModule; the cross-world linkshell history was not rotated");
        if (uiModule == null)
            return;

        uiModule->RotateCrossLinkshellHistory(GetRotateIdx(mode));
    }

    // This function looks up a channel's user-defined color.
    // If this function ever returns 0, it returns null instead.
    public uint? GetChannelColor(ChatType type)
    {
        var parent = type.Parent();
        switch (parent)
        {
            case ChatType.Debug:
            case ChatType.Urgent:
            case ChatType.Notice:
                return type.DefaultColor();
        }

        Plugin.GameConfig.TryGet(parent.ToConfigEntry(), out uint color);

        var rgb = color & 0xFFFFFF;
        if (rgb == 0)
            return null;

        return 0xFF | (rgb << 8);
    }

    private void Login()
    {
        // [Agent] getter: both failure modes, see the taxonomy near ResolveOrNull. Degradation is
        // unchanged from the existing null path - the vanilla channel name is not sampled on this
        // login, so the input line keeps whatever channel it already had until the next channel
        // change fires ChangeChannelNameDetour again.
        var agent = ResolveOrNull<AgentChatLog>(&AgentChatLog.Instance, "Login/agent", "Could not resolve AgentChatLog; the current channel was not sampled on login");
        if (agent == null)
            return;

        ChangeChannelNameDetour(agent);

        // Inform all clients that a new login happened
        Plugin.ServerCore.SendNewLogin();
    }

    private byte ChatLogRefreshDetour(nint log, ushort eventId, AtkValue* value)
    {
        if (Plugin.CurrentTab.InputDisabled)
            return ChatLogRefreshHook!.OriginalDisposeSafe(log, eventId, value);

        if (eventId != 0x31 || value == null || value->UInt is not (0x05 or 0x0C))
            return ChatLogRefreshHook!.OriginalDisposeSafe(log, eventId, value);

        if (Plugin.Functions.KeybindManager.DirectChat && LastTypedCharacter != null)
        {
            // FIXME: this whole system sucks
            // FIXME v2: I hate everything about this, but it works
            Plugin.Framework.RunOnTick(() =>
            {
                string? input = null;

                var utf8Bytes = MemoryHelper.ReadRaw((nint)LastTypedCharacter+0x4, 2);
                var chars = Encoding.UTF8.GetString(utf8Bytes).ToCharArray();
                if (chars.Length == 0)
                    return;

                var c = chars[0];
                if (c != '\0' && !char.IsControl(c))
                    input = c.ToString();

                try
                {
                    Plugin.ChatLog.Activated(new ChatActivatedArgs(new ChannelSwitchInfo(null)) { Input = input });
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Error in chat Activated event");
                }
            });
        }

        string? addIfNotPresent = null;

        // 🔴 `value + 2` 是**沒有上界的**陣列索引 —— 這個 detour 的簽章沒有帶 valueCount，所以離線
        //    無從得知第 3 格真的存在。讀到配置外是 AccessViolationException，try/catch 攔不到，
        //    包 try 只會看起來有防護。真正把它擋住的是上面 line 157 的 eventId/UInt 判別式：
        //    只有 eventId==0x31 且 value[0].UInt 是 0x05/0x0C 才會走到這裡。
        //    ⚠️ 這條假設沒有離線證據，維持上游原樣；要改必須先實機量到 valueCount。
        var str = value + 2;
        if (str != null && ((int) str->Type & 0xF) == (int) ValueType.String && str->String.HasValue)
        {
            var add = str->String.ToString();
            if (add.Length > 0)
                addIfNotPresent = add;
        }

        // fail-closed: Original is kept OUTSIDE the try. Everything this try guards is ChatTwo's own
        // code - Plugin.ChatLog.TellSpecial is our own field and Plugin.ChatLog.Activated is our own
        // method (Ui/ChatLog/ChatLog.Window.cs), not a third-party callback - so the exceptions it
        // catches are ours, and swallowing them is right. Original(), by contrast, is the game's own
        // AddonChatLog.OnRefresh: it used to sit inside the try, which meant a throw from it (or from
        // a null ChatLogRefreshHook) would have been swallowed and turned into `return 1`, i.e. the
        // game silently loses the refresh AND the chat log stops being focusable.
        var deferToVanilla = false;
        try
        {
            // We already called this function once, so we skip the duplicated call
            // Also return the original value here so that vanilla chat receives all information
            if (Plugin.ChatLog.TellSpecial)
            {
                Plugin.Log.Information("Return early to prevent duplicated call...");
                deferToVanilla = true;
            }
            else
            {
                Plugin.ChatLog.Activated(new ChatActivatedArgs(new ChannelSwitchInfo(null)) { AddIfNotPresent = addIfNotPresent });
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error in chat Activated event");
        }

        if (deferToVanilla)
            return ChatLogRefreshHook!.OriginalDisposeSafe(log, eventId, value);

        // prevent the game from focusing the chat log
        return 1;
    }

    private CStringPointer ChangeChannelNameDetour(AgentChatLog* agent)
    {
        var ret = ChangeChannelNameHook!.OriginalDisposeSafe(agent);
        if (agent == null)
            return ret;

        // Original was already called above, so everything below is purely ChatTwo's own
        // bookkeeping - whatever we skip here, the game has already done its own work and behaves
        // normally. The degradation for every guard in this method is therefore identical: the
        // recorded channel, its label and the tell target stay at their previous values until the
        // next channel change, so ChatTwo's input line can show a stale channel name. Vanilla chat
        // is unaffected.
        var shellModule = GetRaptureShellModuleOrNull("ChangeChannelName/shellModule", "Could not resolve RaptureShellModule; leaving the recorded channel unchanged");
        if (shellModule == null)
            return ret;

        var channel = (uint) shellModule->ChatType;
        if (channel is 17 or 18)
            channel = (uint) InputChannel.Tell;

        // ChannelLabel is an inline Utf8String, and the implicit Utf8String -> ReadOnlySpan<byte>
        // conversion is `new(StringPtr, Length)` with Length derived from BufUsed. If the agent is
        // half torn down, StringPtr can be null while Length is still non-zero, and SeString.Parse
        // then scans from address 0 looking for a terminator - an AccessViolationException, which
        // try/catch does not intercept. This pointer check is a separate concern from the try below
        // and neither substitutes for the other. Behaviour in the benign case is unchanged: a null
        // StringPtr with Length 0 used to parse to an empty SeString, which the Payloads.Count
        // check already turned into the same early return.
        if (!agent->ChannelLabel.StringPtr.HasValue)
            return ret;

        // fail-closed. Three managed throw sources live in this block, all of them driven by data
        // the game hands us rather than by anything we control:
        //   - SeString.Parse walks the payload stream with a BinaryReader over an
        //     UnmanagedMemoryStream; a truncated or malformed chunk header throws
        //     EndOfStreamException.
        //   - ChunkUtil.ToChunks resolves UIForeground/UIGlow colour keys through
        //     RowRef<UIColor>.Value, which throws when the row is missing from the sheet - a real
        //     possibility on TC, whose sheets are not the ones these payloads were authored against.
        //   - Plugin.CurrentTab reads the static Plugin.Config, and Config.Tabs is a
        //     JSON-deserialised list, so an element can be null. (The property itself never returns
        //     null: it falls back to `new Tab()` when LastTab is out of range.)
        // This detour is called from native code, so any of those escaping terminates the process.
        try
        {
            var name = SeString.Parse(agent->ChannelLabel);
            if (name.Payloads.Count == 0)
                return ret;

            var nameChunks = ChunkUtil.ToChunks(name, ChunkSource.None, null).ToList();
            if (nameChunks.Count > 0 && nameChunks[0] is TextChunk text)
                text.Content = text.Content.TrimStart('\uE01E').TrimStart();

            string? playerName = null;
            ushort worldId = 0;
            if (channel == (uint) InputChannel.Tell)
            {
                // Same null-pointer reasoning as ChannelLabel above. string.Empty rather than null
                // preserves the old behaviour: a zero-length TellPlayerName parsed to an empty
                // SeString whose TextValue is "", which still produced a TellTarget.
                playerName = agent->TellPlayerName.StringPtr.HasValue ? SeString.Parse(agent->TellPlayerName).TextValue : string.Empty;
                worldId = agent->TellWorldId;
                Plugin.Log.Debug($"Detected tell target '{playerName}'@{worldId}");
            }

            Plugin.CurrentTab.CurrentChannel = new UsedChannel
            {
                Channel = (InputChannel) channel,
                Name = nameChunks,
                TellTarget = playerName != null ? new TellTarget(playerName, worldId, 0, 0) : null
            };
        }
        catch (Exception ex)
        {
            LogErrorThrottled("ChangeChannelName/parse", ex, "Could not parse the chat channel label; leaving the recorded channel unchanged");
        }

        return ret;
    }

    private void ReplyInSelectedChatModeDetour(RaptureShellModule* agent)
    {
        // AgentChatLog.Instance() really can be null - Login() above checks for exactly that - and
        // dereferencing it is an AccessViolationException, which try/catch cannot intercept in .NET
        // Core. It can also THROW, which the earlier pass missed because the [Agent] generator
        // template contains no throw: the throw sites are in the chain underneath it (see the
        // taxonomy near ResolveOrNull). This method is the detour itself, so an escaping throw
        // terminates the process. fail-closed: if we cannot read the reply channel, just let the
        // game do its own thing.
        var chatLog = ResolveOrNull<AgentChatLog>(&AgentChatLog.Instance, "ReplyInSelectedChatMode/agent", "Could not resolve AgentChatLog; leaving the reply channel to the game");
        if (chatLog == null)
        {
            ReplyInSelectedChatModeHook!.OriginalDisposeSafe(agent);
            return;
        }

        var replyMode = chatLog->ReplyChannel;
        if (replyMode == -2)
        {
            ReplyInSelectedChatModeHook!.OriginalDisposeSafe(agent);
            return;
        }

        SetChannelWithExtraChat((InputChannel) replyMode);
        ReplyInSelectedChatModeHook!.OriginalDisposeSafe(agent);
    }

    private bool SetContextTellTarget(RaptureShellModule* a1, Utf8String* playerName, Utf8String* worldName, ushort worldId, ulong accountId, ulong contentId, ushort reason, bool setChatType)
    {
        if (playerName != null)
        {
            try
            {
                var target = new TellTarget(playerName->ToString(), worldId, contentId, (TellReason) reason);
                Plugin.ChatLog.Activated(new ChatActivatedArgs(new ChannelSwitchInfo(InputChannel.Tell, permanent: setChatType))
                {
                    TellReason = (TellReason) reason,
                    TellTarget = target,
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error in chat Activated event");
            }
        }

        return SetChatLogTellTargetHook!.OriginalDisposeSafe(a1, playerName, worldName, worldId, accountId, contentId, reason, setChatType);
    }

    private void ContextMenuTellInForayDetour(RaptureShellModule* a1, Utf8String* playerName, Utf8String* worldName, ushort worldId, ulong accountId, ulong contentId, ushort reason)
    {
        if (!Plugin.CurrentTab.CurrentChannel.UseTempChannel)
            Plugin.CurrentTab.CurrentChannel.UseTempChannel = true;

        if (playerName != null)
        {
            try
            {
                var target = new TellTarget(playerName->ToString(), worldId, contentId, (TellReason) reason);
                Plugin.ChatLog.Activated(new ChatActivatedArgs(new ChannelSwitchInfo(InputChannel.Tell))
                {
                    TellReason = (TellReason) reason,
                    TellTarget = target,
                    TellSpecial = Sheets.IsInForay(), // Handle Eureka/Bozja special
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error in chat Activated event");
            }
        }

        ContextMenuTellInForayHook!.OriginalDisposeSafe(a1, playerName, worldName, worldId, accountId, contentId, reason);
    }

    /// <summary>
    /// Returns true if the channel is any non-linkshell channel, or if the
    /// linkshell actually exists.
    /// </summary>
    public static bool IsChannelOrExistingLinkshell(InputChannel channel)
    {
        var idx = channel.LinkshellIndex();
        if (idx == uint.MaxValue || channel.IsExtraChatLinkshell())
            return true;
        if (channel.IsLinkshell() && ValidLinkshell(idx))
            return true;
        if (channel.IsCrossLinkshell() && ValidCrossLinkshell(idx))
            return true;
        return false;
    }

    // These two are the highest-severity consequence of the taxonomy correction above, so spelling
    // it out: IsChannelOrExistingLinkshell sits on the native-reachable path
    // ReplyInSelectedChatModeDetour -> SetChannelWithExtraChat -> SetChannel, and that detour has no
    // try of its own. An earlier pass added the null check but explicitly recorded that the
    // [InfoProxy] getter "returns null rather than throwing", so it left the throw half unguarded -
    // and a throw from InfoProxyLinkshell.Instance() (via Framework.Instance(), GetUIModule() or
    // GetInfoProxyById(), all of which throw on an unresolved signature) escapes back into game code
    // and terminates the process. The null check is still needed for its own sake: GetInfoProxyById
    // returns null for a proxy that is not registered yet, and dereferencing that is an
    // AccessViolationException, which try/catch cannot intercept.
    //
    // Degradation: an unresolvable proxy reports "this linkshell does not exist", which suppresses
    // the channel switch for linkshell channels only; every other channel short-circuits on
    // idx == uint.MaxValue before reaching here.
    public static bool ValidLinkshell(uint idx)
    {
        if (idx > 7)
            return false;

        var proxy = Chat.ResolveOrNull<InfoProxyLinkshell>(&InfoProxyLinkshell.Instance, "ValidLinkshell/proxy", "Could not resolve InfoProxyLinkshell; treating the linkshell as non-existent");
        if (proxy == null)
            return false;

        return proxy->LinkShells[(int) idx].Id != 0;
    }

    public static bool ValidCrossLinkshell(uint idx)
    {
        if (idx > 7)
            return false;

        var proxy = Chat.ResolveOrNull<InfoProxyCrossWorldLinkshell>(&InfoProxyCrossWorldLinkshell.Instance, "ValidCrossLinkshell/proxy", "Could not resolve InfoProxyCrossWorldLinkshell; treating the cross-world linkshell as non-existent");
        if (proxy == null)
            return false;

        return proxy->CrossWorldLinkshells[(int) idx].Name.Length > 0;
    }

    private static uint? RotateLinkshell(uint currentIndex, RotateMode rotate, Func<uint, bool> validFn)
    {
        if (rotate == RotateMode.None)
            return null;

        var delta = rotate switch
        {
            RotateMode.Forward => 1,
            RotateMode.Reverse => -1,
            _ => 1,
        };

        // Iterate up to 8 times to find a valid linkshell.
        for (var i = 0; i < 8; i++)
        {
            currentIndex = (uint) ((8 + currentIndex + delta) % 8);
            if (validFn(currentIndex))
                return currentIndex;
        }

        return null;
    }

    public static InputChannel? ResolveTempInputChannel(InputChannel? currentTempChannel, InputChannel channel, RotateMode rotate)
    {
        switch (channel)
        {
            case InputChannel.Linkshell1 or InputChannel.CrossLinkshell1 when rotate != RotateMode.None:
            {
                // Chained Instance(): try plus null check, see GetUIModuleOrNull. LinkshellCycle is
                // a plain field read at a fixed offset, so a null module here is an
                // AccessViolationException with nothing to catch it. Degradation: fall through to
                // `return channel`, i.e. the unrotated channel the caller asked for - the same
                // value this method returns for every non-linkshell channel.
                var module = GetUIModuleOrNull("ResolveTempInputChannel/uiModule", "Could not resolve UIModule; the temporary linkshell channel was not rotated");
                if (module == null)
                    return channel;

                var currentIndex = channel is InputChannel.Linkshell1 ? (uint) module->LinkshellCycle : (uint) module->CrossWorldLinkshellCycle;
                if (currentTempChannel != null)
                {
                    switch (channel)
                    {
                        case InputChannel.Linkshell1 when currentTempChannel.Value.IsLinkshell():
                        case InputChannel.CrossLinkshell1 when currentTempChannel.Value.IsCrossLinkshell():
                            currentIndex = currentTempChannel.Value.LinkshellIndex();
                            break;
                    }
                }

                var idx = RotateLinkshell(currentIndex, rotate, channel == InputChannel.Linkshell1 ? ValidLinkshell : ValidCrossLinkshell);
                return channel + idx;
            }
            default:
                return channel;
        }
    }

    public void SetChannelWithExtraChat(InputChannel? channel)
    {
        channel ??= InputChannel.Say;
        if (channel != InputChannel.Tell)
        {
            Plugin.CurrentTab.CurrentChannel.TellTarget = null;
            Plugin.CurrentTab.CurrentChannel.TempTellTarget = null;
        }

        // Instead of calling SetChannel(), we ask the ExtraChat plugin to set a
        // channel override by just calling the command directly.
        if (channel.Value.IsExtraChatLinkshell())
        {
            // Check that the command is registered in Dalamud so the game code
            // never sees the command itself.
            if (!Plugin.CommandManager.Commands.ContainsKey(channel.Value.Prefix()))
                return;

            // Send the command through the game chat. We can't call
            // ICommandManager.ProcessCommand() here because ExtraChat only
            // registers stub handlers and actually processes its commands in a
            // SendMessage detour.
            var bytes = Encoding.UTF8.GetBytes(channel.Value.Prefix());
            ChatBox.SendMessageUnsafe(bytes);

            Plugin.CurrentTab.CurrentChannel.Channel = channel.Value;
            return;
        }

        var target = Plugin.CurrentTab.CurrentChannel.TempTellTarget ?? Plugin.CurrentTab.CurrentChannel.TellTarget;
        Plugin.Functions.Chat.SetChannel(channel.Value, target);
    }

    private void SetChannel(InputChannel channel, TellTarget? tellTarget = null)
    {
        // ExtraChat linkshells aren't supported in game so we never want to
        // call the ChangeChatChannel function with them.
        //
        // Callers should call ChatLogWindow.SetChannel() which handles
        // ExtraChat channels
        if (channel.IsExtraChatLinkshell())
            return;

        var idx = channel.LinkshellIndex();
        if (idx == uint.MaxValue)
            idx = 0;

        // SetChannel is reachable from native code (ReplyInSelectedChatModeDetour ->
        // SetChannelWithExtraChat -> here), so both failure modes are fatal: an escaping throw
        // terminates the process from inside the detour, and a null deref is an uncatchable
        // AccessViolationException. Resolving the shell module first is deliberate - both it and
        // IsChannelOrExistingLinkshell are side-effect free, so reordering is safe, and a successful
        // resolve proves Framework's static address is good. Degradation for every guard below: the
        // game's own chat channel is not switched, so the next message the user sends goes to
        // whatever channel vanilla currently has.
        var shellModule = GetRaptureShellModuleOrNull("SetChannel/shellModule", "Could not resolve RaptureShellModule; the game's chat channel was not switched");
        if (shellModule == null || !IsChannelOrExistingLinkshell(channel))
            return;

        // Correcting an assertion the previous pass left here: the shell module resolve was NOT the
        // only thing in this method that can throw. Utf8String.FromString reaches
        // IMemorySpace.GetDefaultSpace(), which is [MemberFunction] and therefore throws
        // InvalidOperationException when its signature does not resolve - and this runs inside a
        // native detour, where that terminates the process. The allocation now happens after the
        // guards above rather than before them; nothing observable depended on the old ordering,
        // since the string was only ever handed to ChangeChatChannel and then freed.
        //
        // The `target == null` check below is defence in depth and cannot fire today:
        // IMemorySpace.Create<T>() does return null on allocation failure, but FromString then
        // dereferences it unconditionally (`newString->SetString(str)`), so the access violation
        // happens inside ClientStructs before the pointer ever reaches us. Keep the check anyway -
        // it costs nothing and stops being dead the moment that upstream code grows a null check.
        Utf8String* target;
        try
        {
            target = Utf8String.FromString(tellTarget?.ToTargetString() ?? "");
        }
        catch (Exception ex)
        {
            LogErrorThrottled("SetChannel/alloc", ex, "Could not allocate the tell target string; the game's chat channel was not switched");
            return;
        }

        if (target == null)
            return;

        try
        {
            // ChangeChatChannel is [MemberFunction], so it throws on an unresolved signature too.
            ChangeChatChannelSafe(shellModule, tellTarget != null ? 17 : (int) channel, idx, target);
        }
        finally
        {
            target->Dtor(true);
        }
    }

    // Split out so the catch cannot accidentally swallow a failure from target->Dtor in the finally
    // above, and so the fail-closed behaviour is stated in one place.
    private static void ChangeChatChannelSafe(RaptureShellModule* shellModule, int channel, uint idx, Utf8String* target)
    {
        try
        {
            shellModule->ChangeChatChannel(channel, idx, target, true);
        }
        catch (Exception ex)
        {
            LogErrorThrottled("SetChannel/changeChannel", ex, "ChangeChatChannel failed; the game's chat channel was not switched");
        }
    }

    public void SetEurekaTellChannel(string name, string worldName, ushort worldId, ulong accountId, ulong objectId, ushort reason, bool setChatType)
    {
        // param6 is 0 for contentId and 1 for objectId
        // param7 is always 0 ?

        if (!Plugin.CurrentTab.CurrentChannel.UseTempChannel)
            Plugin.CurrentTab.CurrentChannel.UseTempChannel = true;

        // Send tell via CommandInner later and let the game handle it
        // Only works because we use the SetTellTargetInForay function to set all required information
        Plugin.ChatLog.TellSpecial = true;

        // Only reachable from PayloadHandler during draw, so an escaping throw would "merely" cost
        // the window for that frame - but the null half is fatal no matter who calls, because
        // dereferencing a null Instance() is an AccessViolationException. Guard both; see
        // GetRaptureShellModuleOrNull. Degradation: the tell target is never handed to the game, so
        // the tell is not pre-filled. Plugin.ChatLog.TellSpecial was already set to true above and
        // stays true until the next ChatLog.Activated overwrites it, which only makes the next chat
        // log refresh defer to vanilla once.
        var shellModule = GetRaptureShellModuleOrNull("SetEurekaTellChannel/shellModule", "Could not resolve RaptureShellModule; the Eureka/Bozja tell target was not set");
        if (shellModule == null)
            return;

        // Allocation moved below the guard for the same reason as in SetChannel: both strings were
        // previously allocated before anything could bail out, and Utf8String.FromString can throw
        // via IMemorySpace.GetDefaultSpace() ([MemberFunction]). Freeing is now in a finally, so an
        // exception out of SetTellTargetInForay no longer leaks two native strings. The null checks
        // are defence in depth and unreachable today - see the note in SetChannel.
        Utf8String* utfName;
        Utf8String* utfWorld;
        try
        {
            utfName = Utf8String.FromString(name);
            utfWorld = Utf8String.FromString(worldName);
        }
        catch (Exception ex)
        {
            LogErrorThrottled("SetEurekaTellChannel/alloc", ex, "Could not allocate the tell target strings; the Eureka/Bozja tell target was not set");
            return;
        }

        if (utfName == null || utfWorld == null)
        {
            if (utfName != null)
                utfName->Dtor(true);
            if (utfWorld != null)
                utfWorld->Dtor(true);
            return;
        }

        try
        {
            shellModule->SetTellTargetInForay(utfName, utfWorld, worldId, accountId, objectId, reason, setChatType);
        }
        finally
        {
            utfName->Dtor(true);
            utfWorld->Dtor(true);
        }
    }

    public TellHistoryInfo? GetTellHistoryInfo(int index)
    {
        // AcquaintanceModule.Instance() is the chained kind (uiModule == null ? null :
        // uiModule->GetAcquaintanceModule()), so it needs the try for the throw from
        // Framework.Instance() and the null check for its own null return. GetTellHistory is
        // [MemberFunction] and dereferences `this`, so calling it on a null module is an
        // AccessViolationException, not something the try would catch. Degradation: returns null,
        // which is what this method already returns for an empty history slot - ChatLog.Window
        // checks `tellInfo != null` and simply leaves the tell target alone.
        var module = ResolveOrNull<AcquaintanceModule>(&AcquaintanceModule.Instance, "GetTellHistoryInfo/acquaintanceModule", "Could not resolve AcquaintanceModule; the tell history is unavailable");
        if (module == null)
            return null;

        var acquaintance = module->GetTellHistory(index);
        if (acquaintance == null || acquaintance->ContentId == 0)
            return null;

        var name = new ReadOnlySeStringSpan(acquaintance->Name.AsSpan()).ExtractText();
        var world = acquaintance->WorldId;
        var contentId = acquaintance->ContentId;

        return new TellHistoryInfo(name, world, contentId);
    }

    public void SendTellUsingCommandInner(byte[] message)
    {
        // The allocation itself can throw (Utf8String.FromSequence -> IMemorySpace.GetDefaultSpace,
        // which is [MemberFunction]); previously that throw happened outside the try, so it escaped
        // uncaught. It is caught here now for consistency with the rest of the file, and because
        // `mes` would otherwise be unassigned when the finally below runs. The null check is
        // defence in depth - see the note in SetChannel for why it cannot fire today.
        Utf8String* mes;
        try
        {
            mes = Utf8String.FromSequence(message.NullTerminate());
        }
        catch (Exception ex)
        {
            LogErrorThrottled("SendTellUsingCommandInner/alloc", ex, "Could not allocate the message string; the tell was not sent");
            return;
        }

        if (mes == null)
            return;

        try
        {
            // Three chained Instance() calls across two statements, all of the hand-written kind
            // that returns null (RaptureShellModule/UIModule/RaptureAtkModule are each
            // `parent == null ? null : parent->GetX()`), and the UIModule one was additionally being
            // passed straight into a native function that dereferences it. Any of them being null is
            // an AccessViolationException, which nothing catches. Degradation: the tell is not sent
            // and vanilla chat may keep focus.
            var shellModule = GetRaptureShellModuleOrNull("SendTellUsingCommandInner/shellModule", "Could not resolve RaptureShellModule; the tell was not sent");
            if (shellModule != null)
            {
                var uiModule = GetUIModuleOrNull("SendTellUsingCommandInner/uiModule", "Could not resolve UIModule; the tell was not sent");
                if (uiModule != null)
                    shellModule->ExecuteCommandInner(mes, uiModule);
            }

            var atkModule = ResolveOrNull<RaptureAtkModule>(&RaptureAtkModule.Instance, "SendTellUsingCommandInner/atkModule", "Could not resolve RaptureAtkModule; vanilla chat may keep keyboard focus");
            if (atkModule != null)
                atkModule->ClearFocus(); // Clear the focus of vanilla chat that was still active
        }
        finally
        {
            // try/finally rather than try/catch: the two resolves inside now swallow their own
            // throws, so what is left to propagate is a throw out of ExecuteCommandInner/ClearFocus
            // themselves. That behaves exactly as it did before (this path is managed-only, reached
            // from SendHandler, so Dalamud catches it and the window skips a frame) - but the native
            // Utf8String no longer leaks when it happens.
            mes->Dtor(true);
        }
    }

    public void SendTell(TellReason reason, ulong contentId, string name, ushort homeWorld, byte[] message, string rawText)
    {
        if (contentId == 0)
        {
            Plugin.ChatGui.PrintError(Language.Chat_SendTell_Error);
            Plugin.Log.Warning("Tried to send a tell with ContentId being 0, sorry this is an internal error.");
            return;
        }

        // Everything native is resolved BEFORE anything is allocated, so a failed resolve has
        // nothing to unwind. This method is only reachable from SendHandler (managed), so a throw
        // costs a frame rather than the process - but every null below would be an
        // AccessViolationException, which is fatal no matter who called.
        //
        // The two [Signature] function pointers get the same treatment. Dalamud leaves them at zero
        // when a signature fails to resolve (it logs and carries on, it does not throw), and calling
        // a null function pointer is an access violation. They were being called unchecked.
        var pronounModule = ResolveOrNull<PronounModule>(&PronounModule.Instance, "SendTell/pronounModule", "Could not resolve PronounModule; the tell was not sent");
        var logModule = ResolveOrNull<RaptureLogModule>(&RaptureLogModule.Instance, "SendTell/logModule", "Could not resolve RaptureLogModule; the tell was not sent");
        var networkModule = GetNetworkModuleOrNull();
        if (pronounModule == null || logModule == null || networkModule == null || SendTellNative == null || PrintTellNative == null)
        {
            Plugin.ChatGui.PrintError(Language.Chat_SendTell_Error);
            return;
        }

        // EncodeMessage resolves PronounModule itself and now returns null when it cannot.
        var encodedText = EncodeMessage(rawText);
        if (encodedText == null)
        {
            Plugin.ChatGui.PrintError(Language.Chat_SendTell_Error);
            return;
        }

        var decoded = encodedText;
        AutoTranslate.ReplaceWithPayload(ref decoded);

        // Allocations move inside a try/finally so the three native strings are freed on every exit
        // path. Previously an exception anywhere between the allocations and the Dtor calls leaked
        // all three, and a null from any of them made the Dtor itself an access violation. As in
        // SetChannel, the null checks cannot fire today (ClientStructs dereferences the allocation
        // before returning it) but cost nothing and stop being dead if upstream adds a check.
        Utf8String* uName = null;
        Utf8String* uMessage = null;
        Utf8String* encoded = null;
        try
        {
            uName = Utf8String.FromString(name);
            uMessage = Utf8String.FromSequence(message.NullTerminate());
            if (uName == null || uMessage == null)
            {
                Plugin.ChatGui.PrintError(Language.Chat_SendTell_Error);
                return;
            }

            // FromUtf8String null-checks its argument, so a null ProcessString result yields an
            // empty string rather than a crash; only the result of the allocation needs checking.
            encoded = Utf8String.FromUtf8String(pronounModule->ProcessString(uMessage, true));
            if (encoded == null)
            {
                Plugin.ChatGui.PrintError(Language.Chat_SendTell_Error);
                return;
            }

            using var decodedUtf8String = new Utf8String(decoded.NullTerminate());

            // // TODO: Remap TellReasons
            if (reason == TellReason.Direct)
                reason = TellReason.Friend;

            var ok = SendTellNative(networkModule, contentId, homeWorld, uName, encoded, (ushort) reason, homeWorld);
            if (ok == 1)
                PrintTellNative(logModule, 33, uName, &decodedUtf8String, 0, contentId, homeWorld, 255, 0, 0);
            else
                Plugin.ChatGui.PrintError(Language.Chat_SendTell_Error);
        }
        finally
        {
            // try/finally, not try/catch: a throw out of the allocations propagates exactly as it
            // did before (managed-only path, Dalamud catches it), but nothing leaks any more.
            if (encoded != null)
                encoded->Dtor(true);
            if (uName != null)
                uName->Dtor(true);
            if (uMessage != null)
                uMessage->Dtor(true);
        }
    }

    // Framework.Instance()->GetNetworkModuleProxy()->NetworkModule had no guard at any level, and
    // each level fails differently. Framework.Instance() is [StaticAddress(isPointer: true)]: the
    // generator emits `if (ppInstance is null) throw; return *ppInstance;`, so it throws on an
    // unresolved signature AND returns null before the game has constructed the Framework.
    // GetNetworkModuleProxy() is [MemberFunction], so it throws on an unresolved signature of its
    // own and can return null. NetworkModule is then a plain field read at +0x08 off that pointer.
    // Only the throws are catchable; both null steps are access violations, so both need a check.
    // Degradation: SendTell reports Chat_SendTell_Error to the user and sends nothing.
    private static NetworkModule* GetNetworkModuleOrNull()
    {
        var framework = ResolveOrNull<Framework>(&Framework.Instance, "SendTell/framework", "Could not resolve Framework; the tell was not sent");
        if (framework == null)
            return null;

        try
        {
            var proxy = framework->GetNetworkModuleProxy();
            return proxy == null ? null : proxy->NetworkModule;
        }
        catch (Exception ex)
        {
            LogErrorThrottled("SendTell/networkModuleProxy", ex, "Could not resolve the network module proxy; the tell was not sent");
            return null;
        }
    }

    // Returns null when PronounModule cannot be resolved, which SendTell treats as "cannot send".
    // Both Instance() calls here were unguarded. ProcessString is [VirtualFunction], generated as
    // `VirtualTable->ProcessString(this, ...)`, so a null module dereferences immediately rather
    // than throwing anything catchable. Copy is [MemberFunction] and dereferences its argument
    // natively, hence the checks on the ProcessString results as well. Resolving once instead of
    // twice is equivalent - both calls walked the same chain and returned the same pointer.
    private static byte[]? EncodeMessage(string str) {
        var pronounModule = ResolveOrNull<PronounModule>(&PronounModule.Instance, "EncodeMessage/pronounModule", "Could not resolve PronounModule; the message could not be encoded");
        if (pronounModule == null)
            return null;

        using var input = new Utf8String(str);
        using var output = new Utf8String();

        var encoded = pronounModule->ProcessString(&input, true);
        if (encoded == null)
            return null;
        input.Copy(encoded);

        var processed = pronounModule->ProcessString(&input, false);
        if (processed == null)
            return null;
        output.Copy(processed);

        return output.AsSpan().ToArray();
    }

    // TC note: this used to call Utf8String.SanitizeString, a native game function
    // resolved via a byte-pattern signature baked into FFXIVClientStructs. On the TC
    // client that signature scan resolves to the wrong address (it doesn't throw at
    // startup, it just points at garbage), so calling it on every keystroke crashed
    // the game as soon as any character was typed. Replaced with a plain managed
    // check since this is only used to filter obviously-invalid characters.
    public bool IsCharValid(char c) => !char.IsControl(c);

    public static bool CheckHideFlags()
    {
        // Only hide the chat in a cutscene when the vanilla chat would've
        // also been hidden. This prevents Chat 2 from hiding for a split
        // second before the cutscene actually starts, because the game sets
        // the cutscene conditions before processing the skip.
        //
        // TC note: RaptureAtkUnitManager.UiFlags sits at FieldOffset 0x9D00 in a
        // struct sized 0x9D18 - right at the tail end. TC runs an older client
        // build than the FFXIVClientStructs layout this offset was measured
        // against, and reading this field crashed the game as soon as a cutscene
        // or event dialogue actually started (the only time this code path runs).
        // Skip the anti-flicker optimization entirely on TC; chat just hides a
        // frame or two earlier during cutscenes instead of crashing. DO NOT
        // restore the native UiFlags read below without a verified TC-API13
        // binary to check the offset against.
        return true;
    }
}
