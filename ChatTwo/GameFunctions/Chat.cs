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
    private readonly Dictionary<string, (DateTime Last, int Suppressed)> ThrottledLogs = new();

    private void LogErrorThrottled(string key, Exception ex, string message)
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

    // Resolves RaptureShellModule while guarding BOTH of its failure modes, returning null if
    // either one fires.
    //
    // FFXIVClientStructs has two kinds of Instance() and they fail in opposite ways. The ones the
    // [StaticAddress] generator emits throw InvalidOperationException (via
    // ThrowHelper.ThrowNullAddress) when their signature does not resolve. The hand-written chained
    // ones simply return null. RaptureShellModule.Instance() is the chained kind - it is literally
    // `uiModule == null ? null : uiModule->GetRaptureShellModule()` - which means it stacks BOTH
    // modes onto a single expression: the null return is its own, and the throw comes from
    // Framework.Instance() one level further down inside UIModule.Instance().
    //
    // Handling only one of them is fake protection. Dereferencing the null return is an
    // AccessViolationException, a corrupted-state exception that try/catch cannot intercept in .NET
    // Core at all; and a throw that escapes a detour back into native code terminates the process.
    // Callers must therefore both use this instead of Instance() and null-check what comes back.
    private RaptureShellModule* GetRaptureShellModuleOrNull(string logKey, string logMessage)
    {
        try
        {
            return RaptureShellModule.Instance();
        }
        catch (Exception ex)
        {
            LogErrorThrottled(logKey, ex, logMessage);
            return null;
        }
    }

    public static string? GetLinkshellName(uint idx)
    {
        var utf = InfoProxyChat.Instance()->GetLinkShellName(idx);
        return utf.HasValue ? utf.ToString() : null;
    }

    public static string? GetCrossLinkshellName(uint idx)
    {
        var utf = InfoProxyCrossWorldLinkshell.Instance()->GetCrossworldLinkshellName(idx);
        return utf != null ? utf->ToString() : null;
    }

    private static int GetRotateIdx(RotateMode mode) => mode switch
    {
        RotateMode.Forward => 1,
        RotateMode.Reverse => -1,
        _ => 0,
    };

    public static void RotateLinkshellHistory(RotateMode mode)
    {
        var uiModule = UIModule.Instance();
        if (mode == RotateMode.None)
            uiModule->LinkshellCycle = -1;

        uiModule->RotateLinkshellHistory(GetRotateIdx(mode));
    }

    public static void RotateCrossLinkshellHistory(RotateMode mode)
        => UIModule.Instance()->RotateCrossLinkshellHistory(GetRotateIdx(mode));

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
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return;

        ChangeChannelNameDetour(agent);

        // Inform all clients that a new login happened
        Plugin.ServerCore.SendNewLogin();
    }

    private byte ChatLogRefreshDetour(nint log, ushort eventId, AtkValue* value)
    {
        if (Plugin.CurrentTab.InputDisabled)
            return ChatLogRefreshHook!.Original(log, eventId, value);

        if (eventId != 0x31 || value == null || value->UInt is not (0x05 or 0x0C))
            return ChatLogRefreshHook!.Original(log, eventId, value);

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
            return ChatLogRefreshHook!.Original(log, eventId, value);

        // prevent the game from focusing the chat log
        return 1;
    }

    private CStringPointer ChangeChannelNameDetour(AgentChatLog* agent)
    {
        var ret = ChangeChannelNameHook!.Original(agent);
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
        // Core. fail-closed: if we cannot read the reply channel, just let the game do its own thing.
        var chatLog = AgentChatLog.Instance();
        if (chatLog == null)
        {
            ReplyInSelectedChatModeHook!.Original(agent);
            return;
        }

        var replyMode = chatLog->ReplyChannel;
        if (replyMode == -2)
        {
            ReplyInSelectedChatModeHook!.Original(agent);
            return;
        }

        SetChannelWithExtraChat((InputChannel) replyMode);
        ReplyInSelectedChatModeHook!.Original(agent);
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

        return SetChatLogTellTargetHook!.Original(a1, playerName, worldName, worldId, accountId, contentId, reason, setChatType);
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

        ContextMenuTellInForayHook!.Original(a1, playerName, worldName, worldId, accountId, contentId, reason);
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

    // The info-proxy Instance() getters are source-generated as
    // `infoModule == null ? null : (T*)infoModule->GetInfoProxyById(id)`, i.e. the chained kind that
    // returns null rather than throwing, and GetInfoProxyById can itself return null for a proxy
    // that is not registered. Dereferencing that is an AccessViolationException, which try/catch
    // cannot intercept. This matters more than it looks: IsChannelOrExistingLinkshell sits on the
    // native-reachable path ReplyInSelectedChatModeDetour -> SetChannelWithExtraChat -> SetChannel,
    // and it used to be evaluated before SetChannel's own RaptureShellModule guard - so without
    // these checks that guard could never be reached. Degradation: an unresolvable proxy reports
    // "this linkshell does not exist", which suppresses the channel switch for linkshell channels
    // only; every other channel short-circuits on idx == uint.MaxValue before reaching here.
    public static bool ValidLinkshell(uint idx)
    {
        if (idx > 7)
            return false;

        var proxy = InfoProxyLinkshell.Instance();
        if (proxy == null)
            return false;

        return proxy->LinkShells[(int) idx].Id != 0;
    }

    public static bool ValidCrossLinkshell(uint idx)
    {
        if (idx > 7)
            return false;

        var proxy = InfoProxyCrossWorldLinkshell.Instance();
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
                var module = UIModule.Instance();

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

        var target = Utf8String.FromString(tellTarget?.ToTargetString() ?? "");
        var idx = channel.LinkshellIndex();
        if (idx == uint.MaxValue)
            idx = 0;

        // SetChannel is reachable from native code (ReplyInSelectedChatModeDetour ->
        // SetChannelWithExtraChat -> here), so both failure modes are fatal: an escaping throw
        // terminates the process from inside the detour, and a null deref is an uncatchable
        // AccessViolationException. Resolving the shell module first is deliberate - it is the only
        // call left in this method that can throw (Framework.Instance() deep inside the chain), and
        // a successful resolve proves that static address is good, so the
        // IsChannelOrExistingLinkshell call cannot then throw from the same source. Neither call has
        // side effects, so reordering them is safe. Degradation when the guard fires: the game's own
        // chat channel is not switched, so the next message the user sends goes to whatever channel
        // vanilla currently has. target is still freed either way.
        var shellModule = GetRaptureShellModuleOrNull("SetChannel/shellModule", "Could not resolve RaptureShellModule; the game's chat channel was not switched");
        if (shellModule != null && IsChannelOrExistingLinkshell(channel))
            shellModule->ChangeChatChannel(tellTarget != null ? 17 : (int)channel, idx, target, true);

        target->Dtor(true);
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

        var utfName = Utf8String.FromString(name);
        var utfWorld = Utf8String.FromString(worldName);

        // Only reachable from PayloadHandler during draw, so an escaping throw would "merely" cost
        // the window for that frame - but the null half is fatal no matter who calls, because
        // dereferencing a null Instance() is an AccessViolationException. Guard both; see
        // GetRaptureShellModuleOrNull. Degradation: the tell target is never handed to the game, so
        // the tell is not pre-filled. Plugin.ChatLog.TellSpecial was already set to true above and
        // stays true until the next ChatLog.Activated overwrites it, which only makes the next chat
        // log refresh defer to vanilla once.
        var shellModule = GetRaptureShellModuleOrNull("SetEurekaTellChannel/shellModule", "Could not resolve RaptureShellModule; the Eureka/Bozja tell target was not set");
        if (shellModule != null)
            shellModule->SetTellTargetInForay(utfName, utfWorld, worldId, accountId, objectId, reason, setChatType);

        utfName->Dtor(true);
        utfWorld->Dtor(true);
    }

    public TellHistoryInfo? GetTellHistoryInfo(int index)
    {
        var acquaintance = AcquaintanceModule.Instance()->GetTellHistory(index);
        if (acquaintance == null || acquaintance->ContentId == 0)
            return null;

        var name = new ReadOnlySeStringSpan(acquaintance->Name.AsSpan()).ExtractText();
        var world = acquaintance->WorldId;
        var contentId = acquaintance->ContentId;

        return new TellHistoryInfo(name, world, contentId);
    }

    public void SendTellUsingCommandInner(byte[] message)
    {
        var mes = Utf8String.FromSequence(message.NullTerminate());

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
                var uiModule = UIModule.Instance();
                if (uiModule != null)
                    shellModule->ExecuteCommandInner(mes, uiModule);
            }

            var atkModule = RaptureAtkModule.Instance();
            if (atkModule != null)
                atkModule->ClearFocus(); // Clear the focus of vanilla chat that was still active
        }
        finally
        {
            // try/finally, not try/catch: a throw out of UIModule/RaptureAtkModule resolution still
            // propagates exactly as it did before (this path is managed-only, so Dalamud catches it
            // and the window skips a frame), but the native Utf8String no longer leaks when it does.
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

        var uName = Utf8String.FromString(name);
        var uMessage = Utf8String.FromSequence(message.NullTerminate());

        var encoded = Utf8String.FromUtf8String(PronounModule.Instance()->ProcessString(uMessage, true));
        var decoded = EncodeMessage(rawText);
        AutoTranslate.ReplaceWithPayload(ref decoded);

        using var decodedUtf8String = new Utf8String(decoded.NullTerminate());

        var logModule = RaptureLogModule.Instance();
        var networkModule = Framework.Instance()->GetNetworkModuleProxy()->NetworkModule;

        // // TODO: Remap TellReasons
        if (reason == TellReason.Direct)
            reason = TellReason.Friend;

        var ok = SendTellNative(networkModule, contentId, homeWorld, uName, encoded, (ushort) reason, homeWorld);
        if (ok == 1)
            PrintTellNative(logModule, 33, uName, &decodedUtf8String, 0, contentId, homeWorld, 255, 0, 0);
        else
            Plugin.ChatGui.PrintError(Language.Chat_SendTell_Error);

        encoded->Dtor(true);
        uName->Dtor(true);
        uMessage->Dtor(true);
    }

    private static byte[] EncodeMessage(string str) {
        using var input = new Utf8String(str);
        using var output = new Utf8String();

        input.Copy(PronounModule.Instance()->ProcessString(&input, true));
        output.Copy(PronounModule.Instance()->ProcessString(&input, false));
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
