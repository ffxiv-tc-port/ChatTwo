namespace ChatTwo.Util;

public enum PlayerMenuScope
{
    /// <summary>Neither a public world nor a TC world: no player actions are offered.</summary>
    None,
    /// <summary>TC world: only the actions that are plain text commands.</summary>
    TextCommandsOnly,
    /// <summary>Public world: the full set, native calls included.</summary>
    Full,
}

public enum TargetMatch
{
    /// <summary>Not the player the payload names.</summary>
    Reject,
    /// <summary>Take this one and stop looking.</summary>
    Take,
    /// <summary>Use this one only if no <see cref="Take"/> turns up.</summary>
    Fallback,
}

// Pure on purpose (no Dalamud, no game state) so the decisions can be checked offline.
public static class PlayerMenuRouting
{
    /// <summary>Which block of player actions the right-click menu draws for a player on this world.</summary>
    /// <remarks>
    /// TC worlds are not flagged public in the client data. They deliberately get only the text-command
    /// entries: the native calls behind the rest of the block have never run on the TC client.
    /// </remarks>
    public static PlayerMenuScope Scope(bool worldIsPublic, uint worldId)
    {
        if (worldIsPublic)
            return PlayerMenuScope.Full;

        return TellRouting.IsTaiwanWorld(worldId) ? PlayerMenuScope.TextCommandsOnly : PlayerMenuScope.None;
    }

    /// <summary>The <c>add</c> form of the game's friend list / blacklist text commands.</summary>
    public static string ListAddCommand(string commandName, string name, string worldName)
        => $"/{commandName} add {name}@{worldName}";

    /// <summary>How a nearby character whose name already matches ranks as the "Target" pick.</summary>
    /// <remarks>
    /// Public worlds require the home world to match. TC worlds prefer it and fall back to the name
    /// alone; any other world keeps taking the first name match.
    /// </remarks>
    public static TargetMatch MatchNamedTarget(bool worldIsPublic, uint worldId, uint candidateHomeWorldId)
    {
        if (candidateHomeWorldId == worldId)
            return TargetMatch.Take;

        if (worldIsPublic)
            return TargetMatch.Reject;

        return TellRouting.IsTaiwanWorld(worldId) ? TargetMatch.Fallback : TargetMatch.Take;
    }
}
