using System.Numerics;
using ChatTwo.Util;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using Lumina.Text.ReadOnly;

namespace ChatTwo.Ui;

public class CommandHelpWindow : Window {
    private ChatLog.ChatLog ChatLogWindow { get; }
    private ReadOnlySeString? CommandDescription { get; set; }

    // Draw thread only. Built from CommandDescription on first draw; the input callback re-sends the same text every frame.
    private List<Chunk>? DescriptionChunks;

    public CommandHelpWindow(ChatLog.ChatLog chatLogWindow) : base("command help##chat2-commandhelp")
    {
        ChatLogWindow = chatLogWindow;

        Flags = ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize;

        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    // Sets IsOpen to true if it should be drawn
    public void UpdateContent(ReadOnlySeString commandDesc)
    {
        if (CommandDescription is not { } shown || !shown.Equals(commandDesc))
        {
            CommandDescription = commandDesc;
            DescriptionChunks = null;
        }

        var width = 350;
        var scaledWidth = width * ImGuiHelpers.GlobalScale;
        var pos = ChatLogWindow.LastWindowPos;
        switch (Plugin.Config.CommandHelpSide) {
            case CommandHelpSide.Right:
                pos.X += ChatLogWindow.LastWindowSize.X;
                break;
            case CommandHelpSide.Left:
                pos.X -= scaledWidth;
                break;
            case CommandHelpSide.None:
            default:
                IsOpen = false;
                return;
        }

        Position = pos;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(width, 0),
            MaximumSize = ChatLogWindow.LastWindowSize with { X = width }
        };

        IsOpen = true;
    }

    public override void Draw()
    {
        if (CommandDescription == null)
            return;

        DescriptionChunks ??= ChunkUtil.ToChunks(CommandDescription.Value.ToDalamudString(), ChunkSource.None, null).ToList();
        ChatLogWindow.InputHandler.ChunkHandler.DrawChunks(DescriptionChunks);
    }
}
