using System.Text;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Game.ClientState.Conditions;
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
    // Every [Signature] here is Fallible and every hook may be null: a signature that stops matching
    // after a game patch switches that one feature off (one Warning each, see the constructor)
    // instead of failing the whole plugin load.

    // Functions
    [Signature("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8D B9 ?? ?? ?? ?? 33 C0", Fallibility = Fallibility.Fallible)]
    private readonly delegate* unmanaged<RaptureLogModule*, ushort, Utf8String*, Utf8String*, ulong, ulong, ushort, byte, int, byte, void> PrintTellNative = null!;

    [Signature("E8 ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8D 8C 24 ?? ?? ?? ?? E8 ?? ?? ?? ?? B0 ?? 48 8B 8C 24", Fallibility = Fallibility.Fallible)]
    private readonly delegate* unmanaged<NetworkModule*, ulong, ushort, Utf8String*, Utf8String*, ushort, ushort, byte> SendTellNative = null!;

    // Client::UI::AddonChatLog.OnRefresh
    // ⚠️ 第二個參數是 valueCount（上游命名為 eventId 是誤稱）：這個函式是 AtkUnitBase 的虛擬函式 OnRefresh
    //    （FFXIVClientStructs：[VirtualFunction(51)] bool OnRefresh(uint valueCount, AtkValue* values)）。
    //    📌 型別維持 ushort 不改：宣告成 ushort 等於取低 16 位元，對實際會出現的 valueCount 沒有差別；
    //       改成 uint 會讓下面 `!= 0x31` 的判定變嚴格，那是行為改動。
    [Signature("40 53 57 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 4D 8B F8", DetourName = nameof(ChatLogRefreshDetour), Fallibility = Fallibility.Fallible)]
    private Hook<ChatLogRefreshDelegate>? ChatLogRefreshHook = null!;
    private delegate byte ChatLogRefreshDelegate(nint log, ushort valueCount, AtkValue* value);

    // Replace with CS version later
    [Signature("48 89 5C 24 ?? 55 56 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 83 B9", DetourName = nameof(ContextMenuTellInForayDetour), Fallibility = Fallibility.Fallible)]
    private Hook<ContextMenuTellInForayDelegate>? ContextMenuTellInForayHook = null!;
    private delegate void ContextMenuTellInForayDelegate(RaptureShellModule* module, Utf8String* playerName, Utf8String* worldName, ushort worldId, ulong accountId, ulong contentId, ushort reason);

    private readonly Hook<AgentChatLog.Delegates.ChangeChannelName>? ChangeChannelNameHook;
    private readonly Hook<RaptureShellModule.Delegates.ReplyInSelectedChatMode>? ReplyInSelectedChatModeHook;
    private readonly Hook<RaptureShellModule.Delegates.SetContextTellTarget>? SetChatLogTellTargetHook;

    // Pointers
    [Signature("48 8D 35 ?? ?? ?? ?? 8B 05", ScanType = ScanType.StaticAddress, Fallibility = Fallibility.Fallible)]
    private readonly char* LastTypedCharacter = null!;

    private Plugin Plugin { get; }

    public Chat(Plugin plugin)
    {
        Plugin = plugin;
        try
        {
            Plugin.GameInteropProvider.InitializeFromAttributes(this);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Chat: could not initialise the signatures; the features reported below are off");
        }

        ReportMissing(PrintTellNative == null, nameof(PrintTellNative), "tells cannot be sent through the game's native tell function");
        ReportMissing(SendTellNative == null, nameof(SendTellNative), "tells cannot be sent through the game's native tell function");
        ReportMissing(LastTypedCharacter == null, nameof(LastTypedCharacter), "opening the chat by typing no longer carries over the typed character");
        ReportMissing(ChatLogRefreshHook == null, nameof(ChatLogRefreshHook), "the game's own chat log takes the focus instead of Chat 2");
        ReportMissing(ContextMenuTellInForayHook == null, nameof(ContextMenuTellInForayHook), "the game's send-tell context menu entry in forays is not picked up");

        EnableHook(ChatLogRefreshHook, nameof(ChatLogRefreshHook));
        EnableHook(ContextMenuTellInForayHook, nameof(ContextMenuTellInForayHook));

        // Each hook is stored before it is enabled: its detour reaches Original through the field.
        ChangeChannelNameHook = CreateHook<AgentChatLog.Delegates.ChangeChannelName>((nint) AgentChatLog.MemberFunctionPointers.ChangeChannelName, ChangeChannelNameDetour, "AgentChatLog.ChangeChannelName", "the input line no longer follows the game's chat channel");
        EnableHook(ChangeChannelNameHook, "AgentChatLog.ChangeChannelName");

        ReplyInSelectedChatModeHook = CreateHook<RaptureShellModule.Delegates.ReplyInSelectedChatMode>((nint) RaptureShellModule.MemberFunctionPointers.ReplyInSelectedChatMode, ReplyInSelectedChatModeDetour, "RaptureShellModule.ReplyInSelectedChatMode", "replying in the selected chat mode no longer switches Chat 2's channel");
        EnableHook(ReplyInSelectedChatModeHook, "RaptureShellModule.ReplyInSelectedChatMode");

        SetChatLogTellTargetHook = CreateHook<RaptureShellModule.Delegates.SetContextTellTarget>((nint) RaptureShellModule.MemberFunctionPointers.SetContextTellTarget, SetContextTellTarget, "RaptureShellModule.SetContextTellTarget", "the game's send-tell context menu entry is not picked up");
        EnableHook(SetChatLogTellTargetHook, "RaptureShellModule.SetContextTellTarget");

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

    private static void ReportMissing(bool missing, string name, string lostFeature)
    {
        if (missing)
            Plugin.Log.Warning($"Chat: {name} is not available in this game version (signature not found); {lostFeature}");
    }

    // Null (with one Warning naming what stops working) when the game function was not found or the
    // hook could not be created. Never throws, so one stale signature cannot fail the plugin load.
    private static Hook<T>? CreateHook<T>(nint address, T detour, string name, string lostFeature) where T : Delegate
    {
        if (address == nint.Zero)
        {
            ReportMissing(true, name, lostFeature);
            return null;
        }

        try
        {
            return Plugin.GameInteropProvider.HookFromAddress(address, detour);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Chat: could not hook {name}; {lostFeature}");
            return null;
        }
    }

    private static void EnableHook<T>(Hook<T>? hook, string name) where T : Delegate
    {
        if (hook == null)
            return;

        try
        {
            hook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Chat: could not enable the {name} hook");
        }
    }

    // Shared throttle state for the resolution guards below. Everything that logs through here runs
    // on the main thread (game detours plus ImGui draw callbacks), so no locking is needed. A
    // module- or signature-resolution failure is permanent for the rest of the session, so an
    // unthrottled Error would repeat on every channel change and every send. Error level is
    // deliberate: this is exactly the sort of thing we want reported back.
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

    // 同一套節流，但走 Information：用在「不該發生、發生了要知道」但本身不是錯誤的觀測上。
    // Information 是刻意的——要使用者回報的診斷。
    internal static void LogInfoThrottled(string key, string message)
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
            Plugin.Log.Information($"{message} (+{state.Suppressed} suppressed since the last message)");
        else
            Plugin.Log.Information(message);
    }

    // Resolves a ClientStructs module while guarding BOTH failure modes an Instance() can have (null on either):
    // generated [StaticAddress]/[MemberFunction] getters THROW InvalidOperationException on an unresolved
    // signature; the hand-written chained ones and [Agent]/[InfoProxy] getters RETURN NULL and still have throw
    // sites down the chain (Framework.Instance() does both). Callers must use this AND null-check the result:
    // a null deref is an uncatchable AccessViolationException, a throw escaping a detour kills the process.
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

    // Both resolve through an [InfoProxy] getter (throws AND returns null, see ResolveOrNull); the
    // name-fetching calls are [MemberFunction] and throw on an unresolved signature too.
    // Degradation: null is a value both callers already handle - ChatLog.Window skips the channel in the
    // switcher list and renders an empty name in the input line, as for an unassigned linkshell.
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

    // UIModule.Instance() is the hand-written chained kind, so it needs both halves: the try and the
    // null check. The rotate calls are [VirtualFunction] and dereference `this`, which makes a null
    // UIModule an AccessViolationException rather than anything catchable. Degradation: the linkshell
    // cycle is not advanced, so the keybind does nothing this press instead of taking the game down.
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

        // Without the hook there is no Original to sample the channel through.
        if (ChangeChannelNameHook != null)
            ChangeChannelNameDetour(agent);

        // Inform all clients that a new login happened
        Plugin.ServerCore.SendNewLogin();
    }

    private byte ChatLogRefreshDetour(nint log, ushort valueCount, AtkValue* value)
    {
        if (Plugin.CurrentTab.InputDisabled)
            return ChatLogRefreshHook!.OriginalDisposeSafe(log, valueCount, value);

        // 閘門維持上游原樣：valueCount 必須正好是 0x31＝49。
        if (valueCount != 0x31 || value == null || value->UInt is not (0x05 or 0x0C))
            return ChatLogRefreshHook!.OriginalDisposeSafe(log, valueCount, value);

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

        // `value + AddIndex` 的上界：第二個參數就是 valueCount，上面的閘門 `valueCount != 0x31` 保證走到這裡時
        // valueCount 是 49，而 AddIndex 是 2；遊戲自己的處理常式也無條件讀 values[2]。
        // 仍補一道 fail-safe 上界：AVE 是 try/catch 與 HookSafety 都攔不到的，唯一有效的防護是「解參考之前先擋」。
        // 改版哪天讓 valueCount 不再是 49，這裡就跳過這一筆（不讀、不擲例外）。
        const int AddIndex = 2;
        if (valueCount > AddIndex)
        {
            var str = value + AddIndex;
            if (str != null && ((int) str->Type & 0xF) == (int) ValueType.String && str->String.HasValue)
            {
                var add = str->String.ToString();
                if (add.Length > 0)
                    addIfNotPresent = add;
            }
        }
        else
        {
            // 這一行「不應該」出現：上面的閘門要求 valueCount == 0x31，而 0x31 > 2。
            // 真的印出來就代表閘門假設已經不成立（要使用者回報的診斷）。
            LogInfoThrottled(
                "ChatLogRefresh/valueCount",
                $"[ChatTwo] AddonChatLog.OnRefresh 的 valueCount={valueCount}（原值），"
                + $"小於等於 {AddIndex}，已跳過附加字串的讀取。閘門假設（valueCount==0x31）"
                + "已不成立，請回報這一行。");
        }

        // fail-closed: Original is kept OUTSIDE the try. Everything this try guards is ChatTwo's own
        // code (Plugin.ChatLog.TellSpecial / Plugin.ChatLog.Activated), so swallowing its exceptions is
        // right. Original() is the game's own AddonChatLog.OnRefresh: swallowing a throw from it would
        // turn into `return 1`, i.e. the game silently loses the refresh AND the chat log stops being
        // focusable.
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
            return ChatLogRefreshHook!.OriginalDisposeSafe(log, valueCount, value);

        // prevent the game from focusing the chat log
        return 1;
    }

    private CStringPointer ChangeChannelNameDetour(AgentChatLog* agent)
    {
        var ret = ChangeChannelNameHook!.OriginalDisposeSafe(agent);
        if (agent == null)
            return ret;

        // Original was already called above, so everything below is purely ChatTwo's own bookkeeping.
        // The degradation for every guard in this method is therefore identical: the recorded channel,
        // its label and the tell target stay at their previous values until the next channel change, so
        // ChatTwo's input line can show a stale channel name. Vanilla chat is unaffected.
        var shellModule = GetRaptureShellModuleOrNull("ChangeChannelName/shellModule", "Could not resolve RaptureShellModule; leaving the recorded channel unchanged");
        if (shellModule == null)
            return ret;

        var channel = (uint) shellModule->ChatType;
        if (channel is 17 or 18)
            channel = (uint) InputChannel.Tell;

        // ChannelLabel is an inline Utf8String, and the implicit Utf8String -> ReadOnlySpan<byte>
        // conversion is `new(StringPtr, Length)`. If the agent is half torn down, StringPtr can be null
        // while Length is still non-zero, and SeString.Parse then scans from address 0 - an
        // AccessViolationException, which try/catch does not intercept. This pointer check is a separate
        // concern from the try below and neither substitutes for the other.
        if (!agent->ChannelLabel.StringPtr.HasValue)
            return ret;

        // fail-closed. Three managed throw sources live in this block, all driven by game data:
        // SeString.Parse (EndOfStreamException on a truncated/malformed chunk header), ChunkUtil.ToChunks
        // (RowRef<UIColor>.Value throws when the row is missing from the sheet - a real possibility on TC)
        // and Plugin.CurrentTab (Config.Tabs is JSON-deserialised, so an element can be null).
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
        // dereferencing it is an AccessViolationException. It can also THROW from the chain underneath
        // the [Agent] getter (see ResolveOrNull). This method is the detour itself, so an escaping throw
        // terminates the process. fail-closed: if we cannot read the reply channel, just let the game do
        // its own thing.
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

    // IsChannelOrExistingLinkshell sits on the native-reachable path ReplyInSelectedChatModeDetour ->
    // SetChannelWithExtraChat -> SetChannel, and that detour has no try of its own, so both halves
    // matter: InfoProxyLinkshell.Instance() can throw (see ResolveOrNull) and can return null for a
    // proxy that is not registered yet. Degradation: an unresolvable proxy reports "this linkshell
    // does not exist", which suppresses the channel switch for linkshell channels only.
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
        // SetChannelWithExtraChat -> here), so both failure modes are fatal. Resolving the shell module
        // first is deliberate - it and IsChannelOrExistingLinkshell are side-effect free. Degradation for
        // every guard below: the game's own chat channel is not switched, so the next message the user
        // sends goes to whatever channel vanilla currently has.
        var shellModule = GetRaptureShellModuleOrNull("SetChannel/shellModule", "Could not resolve RaptureShellModule; the game's chat channel was not switched");
        if (shellModule == null || !IsChannelOrExistingLinkshell(channel))
            return;

        // Utf8String.FromString reaches IMemorySpace.GetDefaultSpace(), which is [MemberFunction] and
        // throws InvalidOperationException on an unresolved signature - inside a native detour that
        // terminates the process, so the allocation happens after the guards above and inside a try.
        // The `target == null` check below is defence in depth: FromString dereferences the allocation
        // itself, so it cannot fire today, but it costs nothing.
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

        // Only reachable from PayloadHandler during draw, so an escaping throw would "merely" cost the
        // window for that frame - but dereferencing a null Instance() is fatal no matter who calls. Guard
        // both; see GetRaptureShellModuleOrNull. Degradation: the tell target is never handed to the game,
        // so the tell is not pre-filled; TellSpecial stays true until the next ChatLog.Activated, which
        // only makes the next chat log refresh defer to vanilla once.
        var shellModule = GetRaptureShellModuleOrNull("SetEurekaTellChannel/shellModule", "Could not resolve RaptureShellModule; the Eureka/Bozja tell target was not set");
        if (shellModule == null)
            return;

        // Allocation sits below the guard for the same reason as in SetChannel: Utf8String.FromString can
        // throw via IMemorySpace.GetDefaultSpace() ([MemberFunction]). Freeing is in a finally so an
        // exception out of SetTellTargetInForay does not leak two native strings. The null checks are
        // defence in depth - see the note in SetChannel.
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
        // AcquaintanceModule.Instance() is the chained kind, so it needs the try (throw from
        // Framework.Instance()) and the null check. GetTellHistory is [MemberFunction] and dereferences
        // `this`, so calling it on a null module is an AccessViolationException. Degradation: returns
        // null, which this method already returns for an empty history slot - ChatLog.Window checks
        // `tellInfo != null` and simply leaves the tell target alone.
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
        // which is [MemberFunction]), so it sits inside its own try; `mes` would otherwise be unassigned
        // when the finally below runs. The null check is defence in depth - see the note in SetChannel.
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
            // Three chained Instance() calls of the hand-written kind that returns null
            // (RaptureShellModule/UIModule/RaptureAtkModule are each `parent == null ? null : parent->GetX()`),
            // and the UIModule one is passed straight into a native function that dereferences it. Any of
            // them being null is an AccessViolationException. Degradation: the tell is not sent and vanilla
            // chat may keep focus.
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
            // try/finally rather than try/catch: the two resolves inside swallow their own throws, so what is
            // left to propagate is a throw out of ExecuteCommandInner/ClearFocus themselves (managed-only path
            // reached from SendHandler, so Dalamud catches it and the window skips a frame). The finally keeps
            // the native Utf8String from leaking when that happens.
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
        // costs a frame rather than the process - but every null below would be an AccessViolationException.
        // The two [Signature] function pointers get the same check: they are declared Fallible, so a
        // signature that fails to resolve leaves them null, and calling a null function pointer is an
        // access violation.
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

        // Allocations sit inside a try/finally so the three native strings are freed on every exit
        // path, and a null from any of them never reaches a Dtor. As in SetChannel, the null checks
        // cannot fire today (ClientStructs dereferences the allocation before returning it) but cost nothing.
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
            // try/finally, not try/catch: a throw out of the allocations propagates to Dalamud (managed-only
            // path), but nothing leaks.
            if (encoded != null)
                encoded->Dtor(true);
            if (uName != null)
                uName->Dtor(true);
            if (uMessage != null)
                uMessage->Dtor(true);
        }
    }

    // Framework.Instance()->GetNetworkModuleProxy()->NetworkModule: each level fails differently.
    // Framework.Instance() ([StaticAddress(isPointer: true)]) throws on an unresolved signature AND
    // returns null before the game has constructed the Framework; GetNetworkModuleProxy() is
    // [MemberFunction] (throws, can return null); NetworkModule is a plain field read off that pointer.
    // Only the throws are catchable. Degradation: SendTell reports Chat_SendTell_Error and sends nothing.
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
    // ProcessString is [VirtualFunction], generated as `VirtualTable->ProcessString(this, ...)`, so a
    // null module dereferences immediately rather than throwing anything catchable. Copy is
    // [MemberFunction] and dereferences its argument natively, hence the checks on the ProcessString
    // results as well.
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

    // Deliberately does not call the native Utf8String.SanitizeString. On an earlier TC client its
    // FFXIVClientStructs signature landed on the wrong function and calling it on every keystroke
    // crashed the game. On 7.25 the signature resolves to a single target, but its behaviour there is
    // unverified; a plain managed check is enough, since this only filters obviously-invalid characters.
    public bool IsCharValid(char c) => !char.IsControl(c);

    private static readonly CutsceneFlagWatch CutsceneWatch = new();

    // Contract: main thread, every frame a cutscene condition holds and a window is not hidden yet,
    // so it must not throw or allocate. An unresolvable module answers true: hide right away.
    public static bool CheckHideFlags()
    {
        // Only hide the chat in a cutscene when the vanilla chat would've
        // also been hidden. This prevents Chat 2 from hiding for a split
        // second before the cutscene actually starts, because the game sets
        // the cutscene conditions before processing the skip.
        var atkModule = ResolveOrNull<RaptureAtkModule>(&RaptureAtkModule.Instance, "CheckHideFlags/atkModule", "Could not resolve RaptureAtkModule; hiding the chat as soon as a cutscene condition is set");
        if (atkModule == null)
            return true;

        var flags = (uint) atkModule->RaptureAtkUnitManager.UiFlags;
        var chatHidden = (flags & (uint) UIModule.UiFlags.Chat) != 0;
        if (CutsceneWatch.Observe(Environment.TickCount64, flags, chatHidden, CutsceneContext(), out var report))
            LogCutsceneReport(report);

        return chatHidden;
    }

    // Contract: main thread, every frame no cutscene condition holds.
    public static void CutsceneConditionEnded()
    {
        if (CutsceneWatch.ConditionEnded(out var report))
            LogCutsceneReport(report);
    }

    // Bits 0-2: which cutscene conditions were set; the rest: the territory, to tell cutscene kinds apart.
    private static uint CutsceneContext()
    {
        var context = (uint) Plugin.ClientState.TerritoryType << 8;
        if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            context |= 1;
        if (Plugin.Condition[ConditionFlag.WatchingCutscene78])
            context |= 2;
        if (Plugin.Condition[ConditionFlag.WatchingCutscene])
            context |= 4;
        return context;
    }

    private static void LogCutsceneReport(CutsceneFlagReport report)
    {
        var outcome = report.Appeared
            ? $"the chat-hidden flag appeared after {report.ElapsedMs} ms (UiFlags 0x{report.Flags:X}), chat hidden from then on"
            : $"the chat-hidden flag never appeared in {report.ElapsedMs} ms (last UiFlags 0x{report.Flags:X}), chat was left visible";
        var tail = report.IsLast ? "; no further cutscene lines this session" : string.Empty;
        Plugin.Log.Verbose(
            $"Cutscene hide: UiFlags 0x{report.StartFlags:X} when the cutscene condition was first seen; {outcome} "
            + $"[OccupiedInCutSceneEvent={(report.Context & 1) != 0} WatchingCutscene78={(report.Context & 2) != 0} WatchingCutscene={(report.Context & 4) != 0} territory={report.Context >> 8}]{tail}");
    }
}
