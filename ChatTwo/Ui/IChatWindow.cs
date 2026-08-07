using System.Numerics;
using ChatTwo.GameFunctions.Types;

namespace ChatTwo.Ui;

public interface IChatWindow
{
    Vector2 LastWindowPos { get; set; }
    Vector2 LastWindowSize { get; set; }
    HideState CurrentHideState { get; set; }

    /// <summary>
    /// The tab a context menu opened in this window should act on, or null when it cannot be
    /// resolved. A popout owns exactly one tab; the main window follows the selected tab.
    /// Unlike Plugin.CurrentTab this never hands back a throwaway Tab that would silently
    /// swallow writes.
    /// </summary>
    Tab? ContextTab { get; }
}