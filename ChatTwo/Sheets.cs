using Dalamud.Game.ClientState.Objects.SubKinds;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ChatTwo;

public static class Sheets
{
    public static readonly ExcelSheet<Item> ItemSheet;
    public static readonly ExcelSheet<World> WorldSheet;
    public static readonly ExcelSheet<Status> StatusSheet;
    public static readonly ExcelSheet<LogKind> LogKindSheet;
    public static readonly ExcelSheet<LogFilter> LogFilterSheet;
    public static readonly ExcelSheet<EventItem> EventItemSheet;
    public static readonly ExcelSheet<Completion> CompletionSheet;
    public static readonly ExcelSheet<TerritoryType> TerritorySheet;
    public static readonly ExcelSheet<TextCommand> TextCommandSheet;
    public static readonly ExcelSheet<EventItemHelp> EventItemHelpSheet;

    static Sheets()
    {
        ItemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        WorldSheet = Plugin.DataManager.GetExcelSheet<World>();
        StatusSheet = Plugin.DataManager.GetExcelSheet<Status>();
        LogKindSheet = Plugin.DataManager.GetExcelSheet<LogKind>();
        LogFilterSheet = Plugin.DataManager.GetExcelSheet<LogFilter>();
        EventItemSheet = Plugin.DataManager.GetExcelSheet<EventItem>();
        CompletionSheet = Plugin.DataManager.GetExcelSheet<Completion>();
        TerritorySheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        TextCommandSheet = Plugin.DataManager.GetExcelSheet<TextCommand>();
        EventItemHelpSheet = Plugin.DataManager.GetExcelSheet<EventItemHelp>();
    }

    public static bool IsInForay() =>
        TerritorySheet.TryGetRow(Plugin.ClientState.TerritoryType, out var row) &&
        row.TerritoryIntendedUse.RowId is 41 or 61;

    // TC note: 台服(繁中服)DataCenter=151(陸行鳥)底下的世界,World.IsPublic 官方資料
    // 就是全部填 false(不是遺漏),單純用 IsPublic 篩選會讓密語分頁的世界下拉選單
    // 在台服變成空集合。這裡額外放行正式的 8 個台服世界(4028 伊弗利特 ~ 4035 泰坦),
    // 同一個 DataCenter 底下還有測試/內部伺服器(4000-4002、402x/403x 系列)要排除掉,
    // 所以用固定的 rowid 範圍而不是整個 DataCenter 一起放行。
    private const uint TaiwanFirstWorldId = 4028;
    private const uint TaiwanLastWorldId = 4035;

    private static bool IsPublicOrTaiwan(World world) =>
        world.IsPublic || world.RowId is >= TaiwanFirstWorldId and <= TaiwanLastWorldId;

    public static IEnumerable<World> WorldsOnDatacenter(IPlayerCharacter character)
    {
        var dcRow = character.HomeWorld.Value.DataCenter.Value.Region;
        return WorldSheet.Where(world => IsPublicOrTaiwan(world) && world.DataCenter.Value.Region == dcRow);
    }
}