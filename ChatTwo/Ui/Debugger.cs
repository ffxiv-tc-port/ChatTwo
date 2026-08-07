using System.Numerics;
using ChatTwo.Code;
using ChatTwo.GameFunctions;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Bindings.ImGui;
using Lumina.Text.ReadOnly;

namespace ChatTwo.Ui;

public class DebuggerWindow : Window
{
    private readonly Plugin Plugin;
    private readonly ChatLog.ChatLog ChatLogWindow;

    public DebuggerWindow(Plugin plugin) : base("Debugger###chat2-debugger")
    {
        Plugin = plugin;
        ChatLogWindow = plugin.ChatLog;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(475, 600),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        Plugin.Commands.Register("/chat2Debugger", showInHelp: false).Execute += Toggle;
    }

    public void Dispose()
    {
        Plugin.Commands.Register("/chat2Debugger", showInHelp: false).Execute -= Toggle;
    }

    private void Toggle(string _, string __) => Toggle();

    public override unsafe void Draw()
    {
        // Both Instance() calls in this window went through the [Agent] getter unguarded. The
        // template contains no throw, but the chain under it does (Framework.Instance(),
        // GetUIModule(), GetAgentByInternalId - see the taxonomy next to Chat.ResolveOrNull), and the
        // ChannelLabel read further down dereferenced the result with no null check at all, which is
        // an AccessViolationException rather than anything a catch could see.
        //
        // This is a debugger window, so the honest degradation is to say so on screen rather than to
        // silently print a plausible-looking value: an unavailable agent renders "?" / "unavailable",
        // never "0" or an empty channel, because a zero here would read as a real address.
        var agent = (nint) Chat.ResolveOrNull<AgentItemDetail>(&AgentItemDetail.Instance, "Debugger/itemDetail", "Could not resolve AgentItemDetail for the debugger window");
        ImGui.TextUnformatted($"Current Cursor Pos: {ChatLogWindow.InputHandler.CursorPos}");
        // "?" rather than "0": an unresolvable agent and an agent that genuinely lives at address 0
        // are different facts, and printing the second when we mean the first sends whoever reads
        // this window looking for a null-pointer bug that is not there.
        if (ImGui.Selectable($"Agent Address: {(agent == 0 ? "?" : agent.ToString("X"))}") && agent != 0)
            ImGui.SetClipboardText(agent.ToString("X"));

        ImGuiHelpers.ScaledDummy(5.0f);

        ImGui.TextUnformatted($"Handle Tooltips: {ChatLogWindow.InputHandler.PayloadHandler.HandleTooltips}");
        ImGui.TextUnformatted($"Hovered Item: {ChatLogWindow.InputHandler.PayloadHandler.HoveredItem}");
        ImGui.TextUnformatted($"Hover Counter: {ChatLogWindow.InputHandler.PayloadHandler.HoverCounter}");
        ImGui.TextUnformatted($"Last Hover Counter: {ChatLogWindow.InputHandler.PayloadHandler.LastHoverCounter}");

        ImGuiHelpers.ScaledDummy(5.0f);

        ImGui.TextColored(ImGuiColors.DalamudOrange, "Current Tab");
        ImGui.TextUnformatted($"Name: {Plugin.CurrentTab.Name}");
        ImGui.TextUnformatted($"Channel: {Plugin.CurrentTab.CurrentChannel.Channel.ToChatType().Name()}");
        ImGui.TextUnformatted($"Tell Target: {Plugin.CurrentTab.CurrentChannel.TellTarget?.ToTargetString() ?? "Null"}");
        ImGui.TextUnformatted($"Use Temp? {Plugin.CurrentTab.CurrentChannel.UseTempChannel}");
        ImGui.TextUnformatted($"Temp Channel: {Plugin.CurrentTab.CurrentChannel.TempChannel.ToChatType().Name()}");
        ImGui.TextUnformatted($"Temp Tell Target: {Plugin.CurrentTab.CurrentChannel.TempTellTarget?.ToTargetString() ?? "Null"}");
        ImGui.TextUnformatted($"Name Set? {Plugin.CurrentTab.CurrentChannel.Name.Count > 0}");
        ImGui.TextUnformatted($"Name {string.Join(" ", Plugin.CurrentTab.CurrentChannel.Name.Select(c => c.StringValue()))}");

        ImGuiHelpers.ScaledDummy(5.0f);

        ImGui.TextColored(ImGuiColors.DalamudOrange, "Vanilla Chat");
        // ChannelLabel is an inline Utf8String and the implicit conversion to ReadOnlySpan<byte> is
        // `new(StringPtr, Length)`. On a half-torn-down agent StringPtr can be null while Length is
        // still non-zero, and ExtractText then reads from address 0 - an AccessViolationException,
        // not something the window's caller can catch. Same guard as Chat.ChangeChannelNameDetour.
        var chatLog = Chat.ResolveOrNull<AgentChatLog>(&AgentChatLog.Instance, "Debugger/chatLog", "Could not resolve AgentChatLog for the debugger window");
        var vanillaChannel = chatLog != null && chatLog->ChannelLabel.StringPtr.HasValue
            ? new ReadOnlySeString(chatLog->ChannelLabel).ExtractText()
            : "(unavailable)";
        ImGui.TextUnformatted($"Channel: {vanillaChannel}");
    }
}
