using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ChatTwo.Util;

/// <summary>
/// TC note（2026-08-19 更新）：本 repo 釘的 Dalamud（dalamud-pin-v13.0.0.16）**確實有**
/// <c>Dalamud.Plugin.Services.IPlayerState</c>。原本這段註解寫「API13 沒有 IPlayerState」，
/// 那個前提對本 pin 不成立——已實際讀過 Dalamud/Plugin/Services/IPlayerState.cs 與
/// Game/Player/PlayerState.cs 確認，其註冊屬性與 IObjectTable 相同，可以直接 [PluginService] 注入。
/// 這層 wrapper 保留下來只是為了維持 <c>Plugin.PlayerState.*</c> 這個既有呼叫形狀，
/// 避免動到全 repo 的呼叫點；底下的取值已改為轉發到真正的服務
/// （<c>ContentId</c> → <c>Plugin.DalamudPlayerState</c>、其餘 → <c>Plugin.ObjectTable</c>）。
/// ⚠️ <c>ContentId</c> 千萬不要寫成 <c>Plugin.PlayerState.ContentId</c>：那是這個 accessor 自己，
/// 會變成無窮遞迴。
/// </summary>
public sealed class PlayerStateCompatAccessor
{
    public bool IsLoaded
        => Plugin.ClientState.IsLoggedIn && Plugin.ObjectTable.LocalPlayer is not null;

    public ulong ContentId
        => Plugin.DalamudPlayerState.ContentId;

    public string CharacterName
        => Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty;

    public RowRef<World> HomeWorld
        => Plugin.ObjectTable.LocalPlayer?.HomeWorld ?? default;
}
