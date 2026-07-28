using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ChatTwo.Util;

/// <summary>
/// TC note: TC's bundled Dalamud (and the verified true-API13 reference build used to port this
/// repo forward) has no <c>Dalamud.Plugin.Services.IPlayerState</c> at all (a newer Dalamud
/// addition - see the shared skill notes' "recurring old-API-generation compile fixes" list for
/// the general "IPlayerState doesn't exist -> use IClientState" rule). This wraps
/// <see cref="Dalamud.Plugin.Services.IClientState"/>/its <c>LocalPlayer</c> to expose the same
/// shape (<c>IsLoaded</c>/<c>ContentId</c>/<c>CharacterName</c>/<c>HomeWorld</c>) the rest of
/// this repo already calls through <c>Plugin.PlayerState.*</c>, so call sites didn't need
/// per-callsite rewriting. Instantiated once as <see cref="Plugin.PlayerState"/>.
/// </summary>
public sealed class PlayerStateCompatAccessor
{
    public bool IsLoaded
        => Plugin.ClientState.IsLoggedIn && Plugin.ClientState.LocalPlayer is not null;

    public ulong ContentId
        => Plugin.ClientState.LocalContentId;

    public string CharacterName
        => Plugin.ClientState.LocalPlayer?.Name.TextValue ?? string.Empty;

    public RowRef<World> HomeWorld
        => Plugin.ClientState.LocalPlayer?.HomeWorld ?? default;
}
