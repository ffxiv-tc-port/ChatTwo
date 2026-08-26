using System.Numerics;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Ui.Handler;
using ChatTwo.Util;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Lumina.Extensions;

namespace ChatTwo.Ui;

public class Popout : Window, IChatWindow
{
    private readonly Plugin Plugin;
    private readonly Tab Tab;
    private readonly int Idx;

    private long FrameTime; // set every frame
    private long LastActivityTime = Environment.TickCount64;

    private readonly string ChatChannelPicker = "chat-popout-channel-picker";

    public readonly InputHandler InputHandler;

    public Vector2 LastWindowPos { get; set; } = Vector2.Zero;
    public Vector2 LastWindowSize { get; set; } = Vector2.Zero;
    public HideState CurrentHideState { get; set; } = HideState.None;
    public Tab? ContextTab => Tab;

    public Popout(Plugin plugin, Tab tab, int idx) : base($"{tab.Name}##popout")
    {
        Plugin = plugin;
        Tab = tab;
        Idx = idx;

        InputHandler = new InputHandler(this, plugin, $"ChatLog{idx}-{tab.Name}");

        Size = new Vector2(350, 350);
        SizeCondition = ImGuiCond.FirstUseEver;

        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        ChatChannelPicker += $"-{idx}-{tab.Name}";
    }

    public override void PreOpenCheck()
    {
        if (!Tab.PopOut)
            IsOpen = false;
    }

    public override bool DrawConditions()
    {
        FrameTime = Environment.TickCount64;

        var isHidden = Tab.IndependentHide
            ? HideStateHelper.HideStateCheck(this, Tab.HideInBattle, Tab.HideDuringCutscenes, Tab.HideWhenNotLoggedIn, false)
            : Plugin.ChatLog.IsHidden;

        if (isHidden)
            return false;

        if (!Plugin.Config.HideWhenInactive || (!Plugin.Config.InactivityHideActiveDuringBattle && Plugin.InBattle) || !Tab.UnhideOnActivity)
        {
            LastActivityTime = FrameTime;
            return true;
        }

        // Activity in the tab, this popout window, or the main chat log window.
        var lastActivityTime = Math.Max(Tab.LastActivity, LastActivityTime);
        lastActivityTime = Math.Max(lastActivityTime, InputHandler.LastActivityTime);
        return FrameTime - lastActivityTime <= 1000 * Plugin.Config.InactivityHideTimeout;
    }

    public override void PreDraw()
    {
        // Dalamud's built-in per-window opacity (right-click title bar -> Opacity,
        // and window presets) is applied by base.PreDraw pushing ImGuiStyleVar.Alpha.
        // Without this call that slider silently does nothing for this window. It goes
        // first so our own pushes below nest inside it, and base.PostDraw pops last.
        // Note this multiplies with ChatTwo's own WindowAlpha - that is intended, they
        // are two independent controls.
        base.PreDraw();

        if (Plugin.Config.KeepInputFocus && InputHandler.Activate)
            ImGui.SetWindowFocus(WindowName);

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Push();

        Flags = ImGuiWindowFlags.None;
        if (!Plugin.Config.ShowPopOutTitleBar)
            Flags |= ImGuiWindowFlags.NoTitleBar;

        if (!Tab.CanMove)
            Flags |= ImGuiWindowFlags.NoMove;

        if (!Tab.CanResize)
            Flags |= ImGuiWindowFlags.NoResize;

        if (!Plugin.ChatLog.PopOutDocked[Idx])
        {
            var alpha = Tab.IndependentOpacity ? Tab.Opacity : Plugin.Config.WindowAlpha;
            BgAlpha = alpha / 100f;

            // BgAlpha only covers ImGuiCol.WindowBg; the title bar and chat input
            // frame use separate style colours that never respected opacity, so
            // scale them the same way here, after the theme override above.
            var alphaScale = alpha / 100f;
            foreach (var col in OpacityScaledColours)
            {
                var c = ImGui.GetStyle().Colors[(int) col];
                ImGui.PushStyleColor(col, new Vector4(c.X, c.Y, c.Z, c.W * alphaScale));
            }
        }
    }

    private static readonly ImGuiCol[] OpacityScaledColours =
    [
        ImGuiCol.TitleBg, ImGuiCol.TitleBgActive, ImGuiCol.FrameBg, ImGuiCol.ChildBg,
        ImGuiCol.Tab, ImGuiCol.TabHovered, ImGuiCol.TabActive, ImGuiCol.TabUnfocused, ImGuiCol.TabUnfocusedActive,
    ];

    public override void Draw()
    {
        using var id = ImRaii.PushId($"popout-{Tab.Identifier}");

        LastWindowSize = ImGui.GetWindowSize();
        LastWindowPos = ImGui.GetWindowPos();

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            LastActivityTime = FrameTime;

        if (!Plugin.Config.ShowPopOutTitleBar)
        {
            ImGui.TextUnformatted(Tab.Name);
            ImGui.Separator();
        }

        var remainingHeight = Tab.SupportsInput
            ? Plugin.ChatLog.GetRemainingHeightForMessageLog(false)
            : ImGui.GetContentRegionAvail().Y;

        Plugin.ChatLog.DrawMessageLog(Tab, InputHandler.PayloadHandler, remainingHeight, false);

        if (!Tab.SupportsInput)
            return;

        // This tab has a fixed channel, so we force this channel to be always set as current
        if (Tab.Channel is not null)
            Tab.CurrentChannel.SetChannel(Tab.Channel.Value);

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
            Plugin.ChatLog.DrawChannelName(Tab);

        if (ImGuiUtil.IconButton(FontAwesomeIcon.Comment) && Tab.Channel is null)
            ImGui.OpenPopup(ChatChannelPicker);

        if (Tab.Channel is not null && ImGui.IsItemHovered())
            ImGuiUtil.Tooltip(Language.ChatLog_SwitcherDisabled);

        using (var popup = ImRaii.Popup(ChatChannelPicker))
        {
            if (popup)
            {
                foreach (var (name, channel) in GetValidPopupChannels())
                    if (ImGui.Selectable(name))
                        Tab.CurrentChannel.SetChannel(channel);
            }
        }

        ImGui.SameLine();

        var tellSpecial = false;
        InputHandler.DrawInputArea(Tab, ImGui.GetContentRegionAvail().X, ref tellSpecial);
    }

    public override void PostDraw()
    {
        if (!Plugin.ChatLog.PopOutDocked[Idx])
            ImGui.PopStyleColor(OpacityScaledColours.Length);

        Plugin.ChatLog.PopOutDocked[Idx] = ImGui.IsWindowDocked();

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Pop();

        // Pops the ImGuiStyleVar.Alpha that base.PreDraw pushed; must be last so the
        // style stack unwinds in reverse order.
        base.PostDraw();
    }

    public override void OnClose()
    {
        Plugin.ChatLog.PopOutWindows.Remove(Tab.Identifier);
        Plugin.WindowSystem.RemoveWindow(this);

        Tab.PopOut = false;

        // Mirror into the settings window's snapshot so saving settings doesn't pop the window
        // straight back out (see SettingsWindow.MirrorTabEdit) - but only for a real user close.
        // Saving settings replaces every entry of Config.Tabs with a fresh clone and clears PopOut
        // on the old objects, which closes this window as part of rebinding it to the new Tab
        // instance; mirroring that churn would un-pop-out the tab on the next save. Reference
        // identity separates the two: an orphaned Tab is no longer in the live list (Tab does not
        // override Equals, so Contains is a reference check).
        if (Plugin.Config.Tabs.Contains(Tab))
            Plugin.SettingsWindow.MirrorTabEdit(Tab.Identifier, mirror => mirror.PopOut = false);

        Plugin.Config.RecalculateMaxUnhideEligibleTabActivity();
        Plugin.SaveConfig();
    }

    private Dictionary<string, InputChannel> GetValidPopupChannels()
    {
        var channels = new Dictionary<string, InputChannel>();
        foreach (var channel in Enum.GetValues<InputChannel>())
        {
            if (channel is InputChannel.Invalid or InputChannel.Tell)
                continue;

            var name = Sheets.LogFilterSheet.FirstOrNull(row => row.LogKind == (byte) channel.ToChatType())?.Name.ToString() ?? channel.ToChatType().Name();
            if (channel.IsLinkshell())
            {
                var lsName = GameFunctions.Chat.GetLinkshellName(channel.LinkshellIndex());
                if (string.IsNullOrWhiteSpace(lsName))
                    continue;

                name += $": {lsName}";
            }

            if (channel.IsCrossLinkshell())
            {
                var lsName = GameFunctions.Chat.GetCrossLinkshellName(channel.LinkshellIndex());
                if (string.IsNullOrWhiteSpace(lsName))
                    continue;

                name += $": {lsName}";
            }

            // Check if the linkshell with this index is registered in
            // the ExtraChat plugin by seeing if the command is
            // registered. The command gets registered only if a
            // linkshell is assigned (and even gets unassigned if the
            // index changes!).
            if (channel.IsExtraChatLinkshell() && !Plugin.CommandManager.Commands.ContainsKey(channel.Prefix()))
                continue;

            channels.Add(name, channel);
        }

        return channels;
    }
}
