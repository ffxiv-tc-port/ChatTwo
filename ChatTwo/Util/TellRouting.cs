namespace ChatTwo.Util;

public enum TellRoute
{
    /// <summary>Send through the game's native tell function, addressed by content ID.</summary>
    Native,
    /// <summary>Send as a <c>/tell name@world message</c> text command and let the game resolve the target.</summary>
    TextCommand,
    /// <summary>The target cannot be addressed either way; nothing may be sent.</summary>
    Unresolvable,
}

// Pure on purpose (no Dalamud, no game state) so the routing can be checked offline.
public static class TellRouting
{
    // The eight live TC worlds. World.IsPublic is false for all of them in the TC client data; the
    // same data centre also holds test/internal worlds, hence a row range rather than the data centre.
    public const uint TaiwanFirstWorldId = 4028;
    public const uint TaiwanLastWorldId = 4035;

    public static bool IsTaiwanWorld(uint worldId)
        => worldId is >= TaiwanFirstWorldId and <= TaiwanLastWorldId;

    /// <summary>Which way a tell to a target that carries a content ID goes out.</summary>
    /// <remarks>
    /// TC worlds deliberately do not take the native route: its argument layout is unverified on the
    /// TC client. A target without a usable name or world name is never guessed at.
    /// </remarks>
    public static TellRoute Decide(bool worldIsPublic, uint worldId, string? name, string? worldName)
    {
        if (worldIsPublic)
            return TellRoute.Native;

        if (IsTaiwanWorld(worldId) && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(worldName))
            return TellRoute.TextCommand;

        return TellRoute.Unresolvable;
    }

    /// <summary>The text command the game documents for tells; the message is appended verbatim.</summary>
    public static string TextCommand(string name, string worldName, string message)
        => $"/tell {name}@{worldName} {message}";
}
