using Dalamud.Game.ClientState.Keys;

namespace ChatTwo.GameFunctions.Types;

// A struct: all ~60 are re-read every 5 seconds, and the default (NO_KEY everywhere) is what an unread one was.
public struct Keybind
{
    public VirtualKey Key1 { get; init; }
    public ModifierFlag Modifier1 { get; init; }

    public VirtualKey Key2 { get; init; }
    public ModifierFlag Modifier2 { get; init; }
}
