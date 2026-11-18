using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using ChatTwo.Code;
using ChatTwo.History;
using ChatTwo.Http.MessageProtocol;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiNotification;
using Lumina.Data.Files;
using Lumina.Text.ReadOnly;
using MessagePack;
using Microsoft.Data.Sqlite;
using MoreLinq;
using Newtonsoft.Json;

namespace ChatTwo.Ui;

internal sealed record HistoryRow(Message Message, long RowId, long Date, string DateText, string ChannelText);

/// <summary>One answered list query; Request is the object it answers, compared by reference.</summary>
internal sealed record HistoryResultPage(object Request, long Total, int TotalPages, int Page, bool Truncated, HistoryRow[] Rows, string Status, bool Failed);

/// <summary>Messages of one character around an anchor, oldest first. First/Last are the outermost rows read, even unparsable ones.</summary>
internal sealed record HistoryContext(int Version, HistoryRow Anchor, byte[]? Channels, HistoryRow[] Rows, HistoryKey? First, HistoryKey? Last, bool MoreOlder, bool MoreNewer, long ScrollTo, bool Failed);

// Threading: Draw runs on the framework thread and never touches the database. Queries run on two HistoryLanes (list
// and context), each on its own read-only connection; a finished query publishes one immutable snapshot that Draw
// picks up by reading a single field. The Cached* fields belong to the list lane and are only touched inside its jobs.
public class DbViewer : Window
{
    public const float RowPerPage = 1000.0f;

    private const int PageSize = 200;
    private const int MatchCap = 100_000;
    private const int ContextSpan = 50;
    private const int ContextMore = 100;
    private const long SearchDebounceMs = 350;
    private const long LoadingTextDelayMs = 250;
    private const string RowDateFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly Plugin Plugin;

    private static readonly DateTime MinimalDate = new(2021, 1, 1);

    private DateTime AfterDate;
    private DateTime BeforeDate;

    private int CurrentPage = 1;
    private bool NewestFirst = true;
    private string SimpleSearchTerm = "";
    private long TermEditedAt;
    private bool OnlyCurrentCharacter = true;
    private readonly Dictionary<ChatType, (ChatSource, ChatSource)> SelectedChannels;
    private int ChannelsVersion;
    private int RefreshVersion;

    private string MinDateString = "";
    private string MaxDateString = "";

    private readonly string DateFormat;
    private readonly string DateTimeFormat;

    private readonly HistoryLane ListLane;
    private readonly HistoryLane ContextLane;

    private (DateTime After, DateTime Before, ulong? Receiver, int Channels, string Term, int Page, bool NewestFirst, int Refresh) Submitted;
    private object? SubmittedRequest;
    private long SubmittedAt;
    private volatile HistoryResultPage? Results;
    private HistoryResultPage? DrawnResults;

    private bool ContextAllChannels;
    private int ContextVersion;
    private volatile HistoryContext? Context;
    private HistoryContext? DrawnContext;
    private long ScrollToRowId = -1;
    private bool SelectContextTab;

    private HistoryFilter? CachedFilter;
    private long CachedRemovals;
    private int CachedRefresh = -1;
    private long CachedCount;
    private HistoryMatches? CachedMatches;

    private bool IsExporting;
    private string InputPath = string.Empty;
    private IActiveNotification Notification = null!;

    public DbViewer(Plugin plugin) : base(Language.DbViewer_Title + "###chat2-dbviewer")
    {
        Plugin = plugin;
        SelectedChannels = TabsUtil.MostlyPlayer;

        DateFormat = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        DateTimeFormat = "ddd, dd MMM yyy HH:mm:ss";

        DateReset();

        ListLane = new HistoryLane(OpenConnection, ex => QueryFailed(ex, "list"));
        ContextLane = new HistoryLane(OpenConnection, ex => QueryFailed(ex, "context"));

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(475, 600),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        Plugin.Commands.Register("/chat2Viewer", Language.DbViewer_Command_Help, true).Execute += Toggle;
    }

    public void Dispose()
    {
        Plugin.Commands.Register("/chat2Viewer").Execute -= Toggle;
        ListLane.Dispose(2_000);
        ContextLane.Dispose(2_000);
    }

    private void Toggle(string _, string __) => Toggle();

    /// <summary>Opens the window showing the given channels, as a tab's own history.</summary>
    public void OpenWithChannels(IEnumerable<ChatType> channels)
    {
        SelectedChannels.Clear();
        foreach (var type in channels)
            SelectedChannels[type] = (ChatSourceExt.All, ChatSourceExt.All);

        ChannelsVersion++;
        IsOpen = true;
        BringToFront();
    }

    // Messages keep arriving while the window is closed; reopening shows them.
    public override void OnOpen() => RefreshVersion++;

    private SqliteConnection OpenConnection() => Plugin.MessageManager.Store.OpenReadOnlyConnection();

    private void QueryFailed(Exception ex, string lane)
    {
        Plugin.Log.Warning(ex, $"Chat history {lane} query failed");
        if (lane == "list")
            Results = new HistoryResultPage(SubmittedRequest ?? new object(), 0, 1, 1, false, [], Language.DbViewer_Status_Error, true);
        else if (Context is { } current)
            Context = current with { Version = ContextVersion, Failed = true };
    }

    public override void Draw()
    {
        var spacing = 3.0f * ImGuiHelpers.GlobalScale;

        DrawFilterRow(spacing);
        DrawExportRow(spacing);
        DrawSearchRow(spacing);

        if (DateWidget.Validate(MinimalDate, ref AfterDate, ref BeforeDate))
            DateRefresh();

        SubmitIfChanged();

        ImGuiHelpers.ScaledDummy(5.0f);

        using var tabBar = EndScope.TabBar("##history-tabs");
        if (!tabBar.Success)
            return;

        using (var resultsTab = EndScope.TabItem(Language.DbViewer_Tab_Results, ImGuiTabItemFlags.None))
        {
            if (resultsTab.Success)
                DrawResults();
        }

        var contextFlags = SelectContextTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        SelectContextTab = false;
        using (var contextTab = EndScope.TabItem(Language.DbViewer_Tab_Context, contextFlags))
        {
            if (contextTab.Success)
                DrawContext();
        }
    }

    private void DrawFilterRow(float spacing)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ImGuiColors.DalamudViolet, Language.DbViewer_DatePicker_FromTo);
        ImGui.SameLine();

        DateWidget.DatePickerWithInput("##FromDate", 1, ref MinDateString, ref AfterDate, DateFormat);
        DateWidget.DatePickerWithInput("##ToDate", 2, ref MaxDateString, ref BeforeDate, DateFormat, true);

        ImGui.SameLine(0, spacing);

        if (ImGuiUtil.IconButton(FontAwesomeIcon.Recycle))
            DateReset();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Language.DbViewer_Date_Reset_Tooltip);

        QuickRange(Language.DbViewer_Quick_Today, 0, spacing);
        QuickRange(Language.DbViewer_Quick_7Days, 6, spacing);
        QuickRange(Language.DbViewer_Quick_30Days, 29, spacing);
        QuickRange(Language.DbViewer_Quick_All, -1, spacing);

        ImGui.SameLine(0, spacing);

        ChannelSelection();

        ImGui.SameLine(0, spacing * 3);
        ImGui.Checkbox(Language.DbViewer_CharacterOption, ref OnlyCurrentCharacter);
    }

    // Negative days means everything since MinimalDate.
    private void QuickRange(string label, int days, float spacing)
    {
        ImGui.SameLine(0, spacing);
        if (!ImGui.Button(label))
            return;

        BeforeDate = DateTime.Now;
        AfterDate = days < 0 ? MinimalDate : DateTime.Today.AddDays(-days);
        AdjustDates();
        DateRefresh();
    }

    private void DrawExportRow(float spacing)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ImGuiColors.DalamudViolet, Language.DbViewer_Export_Label);
        ImGui.SameLine(0, spacing);
        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() * 0.4f);
        ImGui.InputText("##InputPath", ref InputPath, 255);
        ImGui.SameLine(0, spacing);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.FolderClosed, "##folderPicker"))
            ImGui.OpenPopup("InputPathDialog");

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Language.Folder_Export_Location_Tooltip);

        using (var innerPopup = ImRaii.Popup("InputPathDialog"))
        {
            if (innerPopup.Success)
                Plugin.FileDialogManager.OpenFolderDialog(Language.Folder_Selection_Header, (b, s) => { if (b) InputPath = s; }, null, true);
        }

        ImGui.SameLine(0, spacing);
        using (ImRaii.Disabled(InputPath.Length == 0 || IsExporting))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Save))
            {
                Notification = Plugin.Notification.AddNotification(
                    new Notification
                    {
                        Title = "Chat2 Text Export",
                        Content = Language.ChatExport_Initial,
                        Type = NotificationType.Info,
                        Minimized = false,
                        UserDismissable = false,
                        InitialDuration = TimeSpan.FromSeconds(10000),
                        Progress = 0.0f,
                    });
                CreateTxtBackup();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Language.Export_Txt_Tooltip);

        ImGui.SameLine(0, spacing);
        using (ImRaii.Disabled(InputPath.Length == 0 || IsExporting))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.FileExport))
            {
                Notification = Plugin.Notification.AddNotification(
                    new Notification
                    {
                        Title = "Chat2 Json Export",
                        Content = Language.ChatExport_Initial,
                        Type = NotificationType.Info,
                        Minimized = false,
                        UserDismissable = false,
                        InitialDuration = TimeSpan.FromSeconds(10000),
                        Progress = 0.0f,
                    });
                CreateTempJsonFile();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Language.Export_Json_Tooltip);
    }

    private void DrawSearchRow(float spacing)
    {
        var results = Results;
        var totalPages = results?.TotalPages ?? 1;

        // The worker clamps the page when the results shrank; follow it rather than asking again.
        if (results != null && ReferenceEquals(results.Request, SubmittedRequest) && results.Page != Submitted.Page && CurrentPage == Submitted.Page)
        {
            CurrentPage = results.Page;
            Submitted.Page = results.Page;
        }

        ImGui.SetNextItemWidth(Math.Min(350 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.5f));
        if (ImGui.InputTextWithHint("##searchbar", Language.DbViewer_Search_Hint, ref SimpleSearchTerm, 100))
            TermEditedAt = Environment.TickCount64;

        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip(Language.DbViewer_Search_Tooltip);

        ImGui.SameLine(0, spacing);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Sync, "history-refresh", Language.DbViewer_Refresh_Tooltip))
            RefreshVersion++;

        ImGui.SameLine(0, spacing);
        var sortIcon = NewestFirst ? FontAwesomeIcon.SortAmountDown : FontAwesomeIcon.SortAmountUp;
        if (ImGuiUtil.IconButton(sortIcon, "history-sort", NewestFirst ? Language.DbViewer_Sort_NewestFirst : Language.DbViewer_Sort_OldestFirst))
        {
            NewestFirst = !NewestFirst;
            CurrentPage = 1;
        }

        ImGuiUtil.DrawArrows(ref CurrentPage, 1, Math.Max(totalPages, CurrentPage), spacing, tooltipLeft: Language.Page_ArrowLeft_Tooltip, tooltipRight: Language.Page_ArrowRight_Tooltip);

        ImGui.SameLine(0, spacing * 3);
        ImGui.AlignTextToFramePadding();
        if (results == null)
            ImGui.TextDisabled(Language.DbViewer_Status_PageUnknown);
        else if (results.Failed)
            ImGui.TextColored(ImGuiColors.DalamudRed, results.Status);
        else
            ImGui.TextUnformatted(results.Status);

        if (results is { Truncated: true })
        {
            ImGui.SameLine(0, spacing);
            ImGui.TextColored(ImGuiColors.DalamudOrange, "(!)");
            if (ImGui.IsItemHovered())
                ImGuiUtil.Tooltip(string.Format(Language.DbViewer_Status_Truncated, MatchCap));
        }

        var waited = Environment.TickCount64 - SubmittedAt;
        if (!ReferenceEquals(results?.Request, SubmittedRequest) && waited > LoadingTextDelayMs)
        {
            ImGui.SameLine(0, spacing * 3);
            ImGui.TextColored(ImGuiColors.DalamudYellow, string.Format(Language.DbViewer_Status_Searching, waited / 1000));
        }
    }

    private void SubmitIfChanged()
    {
        AdjustDates();
        ulong? receiver = OnlyCurrentCharacter ? Plugin.MessageManager.CurrentContentId : null;
        var term = SimpleSearchTerm.Trim();
        if (term != Submitted.Term && Environment.TickCount64 - TermEditedAt < SearchDebounceMs)
            term = Submitted.Term ?? "";

        var filterChanged = AfterDate != Submitted.After || BeforeDate != Submitted.Before || receiver != Submitted.Receiver
                            || ChannelsVersion != Submitted.Channels || term != Submitted.Term;
        if (filterChanged)
            CurrentPage = 1;

        if (SubmittedRequest != null && !filterChanged && CurrentPage == Submitted.Page && NewestFirst == Submitted.NewestFirst && RefreshVersion == Submitted.Refresh)
            return;

        Submitted = (AfterDate, BeforeDate, receiver, ChannelsVersion, term, CurrentPage, NewestFirst, RefreshVersion);
        SubmittedAt = Environment.TickCount64;

        var filter = new HistoryFilter(
            new DateTimeOffset(AfterDate.Date).ToUnixTimeMilliseconds(),
            new DateTimeOffset(BeforeDate.Date.AddDays(1)).ToUnixTimeMilliseconds(),
            SelectedChannels.Keys.Select(type => (byte)type).Order().ToArray(),
            receiver,
            term);
        var request = new object();
        var page = CurrentPage;
        var newestFirst = NewestFirst;
        var refresh = RefreshVersion;
        SubmittedRequest = request;
        ListLane.Submit((conn, generation) => RunList(conn, generation, request, filter, page, newestFirst, refresh));
    }

    private void RunList(SqliteConnection conn, long generation, object request, HistoryFilter filter, int page, bool newestFirst, int refresh)
    {
        var removals = Plugin.MessageManager.Store.Removals;
        var searching = filter.Term.Length > 0;
        if (!filter.SameAs(CachedFilter) || refresh != CachedRefresh || removals != CachedRemovals)
        {
            CachedFilter = null;
            CachedMatches = searching ? FindMatches(conn, filter) : null;
            CachedCount = searching ? CachedMatches!.RowIds.Length : HistoryIndex.Count(conn, filter);
            CachedFilter = filter;
            CachedRefresh = refresh;
            CachedRemovals = removals;
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(CachedCount / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);

        List<HistoryKey> keys;
        if (!searching)
        {
            keys = HistoryIndex.PageKeys(conn, filter, (long)(page - 1) * PageSize, PageSize, newestFirst);
        }
        else
        {
            keys = MatchedKeys(conn, CachedMatches!, page, newestFirst);
            // Rows deleted, or renumbered by a vacuum, since the search ran: search again once.
            if (keys.Count != PageSlice(CachedMatches!, page, newestFirst).Length)
            {
                CachedMatches = FindMatches(conn, filter);
                CachedCount = CachedMatches.RowIds.Length;
                totalPages = Math.Max(1, (int)Math.Ceiling(CachedCount / (double)PageSize));
                page = Math.Clamp(page, 1, totalPages);
                keys = MatchedKeys(conn, CachedMatches, page, newestFirst);
            }
        }

        var rows = Materialize(conn, keys);
        if (!ListLane.IsCurrent(generation))
            return;

        var status = string.Format(Language.DbViewer_Status_Page, page, totalPages, CachedCount.ToString("N0"));
        Results = new HistoryResultPage(request, CachedCount, totalPages, page, CachedMatches?.Truncated ?? false, rows, status, false);
    }

    private HistoryMatches FindMatches(SqliteConnection conn, HistoryFilter filter)
        => HistoryIndex.FindMatches(conn, filter, MatchCap, FallbackText);

    // The same text the walker reads, decoded the way the rest of the plugin decodes it.
    private static string? FallbackText(byte[] blob)
        => ChunkUtil.ToRawString(MessagePackSerializer.Deserialize<List<Chunk>>(blob, MessageStore.MsgPackOptions));

    // Matches are newest first; oldest first reads them from the far end.
    private static long[] PageSlice(HistoryMatches matches, int page, bool newestFirst)
    {
        var total = matches.RowIds.Length;
        var start = (page - 1) * PageSize;
        if (start >= total)
            return [];

        var count = Math.Min(PageSize, total - start);
        var slice = new long[count];
        for (var i = 0; i < count; i++)
            slice[i] = matches.RowIds[newestFirst ? start + i : total - 1 - start - i];

        return slice;
    }

    private static List<HistoryKey> MatchedKeys(SqliteConnection conn, HistoryMatches matches, int page, bool newestFirst)
    {
        var slice = PageSlice(matches, page, newestFirst);
        var keys = HistoryIndex.KeysOf(conn, slice, newestFirst);
        var dates = new Dictionary<long, long>(slice.Length);
        var total = matches.RowIds.Length;
        var start = (page - 1) * PageSize;
        for (var i = 0; i < slice.Length; i++)
            dates[slice[i]] = matches.Dates[newestFirst ? start + i : total - 1 - start - i];

        // A row whose date moved is not the row the search found.
        return keys.Count == slice.Length && keys.All(k => dates.TryGetValue(k.RowId, out var d) && d == k.Date) ? keys : [];
    }

    private static HistoryRow[] Materialize(SqliteConnection conn, List<HistoryKey> keys)
    {
        if (keys.Count == 0)
            return [];

        var ids = new long[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            ids[i] = keys[i].RowId;

        var byId = new Dictionary<Guid, Message>(keys.Count);
        using (var messages = MessageStore.GetByRowIds(conn, ids))
        {
            foreach (var message in messages)
                byId[message.Id] = message;
        }

        // Rows that failed to parse are left out; the keys still say where the window ends.
        var rows = new List<HistoryRow>(keys.Count);
        foreach (var key in keys)
        {
            if (!byId.TryGetValue(key.Id, out var message))
                continue;

            rows.Add(new HistoryRow(message, key.RowId, key.Date, message.Date.ToLocalTime().ToString(RowDateFormat, CultureInfo.InvariantCulture), message.Code.Type.Name()));
        }

        return rows.ToArray();
    }

    private void DrawResults()
    {
        var results = Results;
        if (results == null)
            return;

        if (results.Rows.Length == 0)
        {
            ImGui.TextUnformatted(Submitted.Term is null or "" ? Language.DbViewer_Status_NothingFound : Language.DbViewer_Status_NoSearchResult);
            return;
        }

        using var child = ImRaii.Child("##tableChild");
        if (!child.Success)
            return;

        if (!ReferenceEquals(results, DrawnResults))
        {
            DrawnResults = results;
            ImGui.SetScrollY(0.0f);
        }

        DrawTable("##messageHistory", results.Rows, null);
    }

    private void DrawContext()
    {
        var context = Context;
        if (context == null)
        {
            ImGui.TextWrapped(Language.DbViewer_Context_Empty);
            return;
        }

        var pending = context.Version != ContextVersion;
        var spacing = 3.0f * ImGuiHelpers.GlobalScale;

        if (ImGui.Checkbox(Language.DbViewer_Context_AllChannels, ref ContextAllChannels))
            OpenContext(context.Anchor);

        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip(Language.DbViewer_Context_AllChannels_Tooltip);

        ImGui.SameLine(0, spacing * 3);
        if (ImGui.Button(Language.DbViewer_Context_Anchor))
            ScrollToRowId = context.Anchor.RowId;

        if (context.Failed)
        {
            ImGui.SameLine(0, spacing * 3);
            ImGui.TextColored(ImGuiColors.DalamudRed, Language.DbViewer_Status_Error);
        }

        using var child = ImRaii.Child("##contextChild");
        if (!child.Success)
            return;

        if (!ReferenceEquals(context, DrawnContext))
        {
            DrawnContext = context;
            if (context.ScrollTo >= 0)
                ScrollToRowId = context.ScrollTo;
        }

        ContextLoadButton(context, true, pending);
        DrawTable("##contextHistory", context.Rows, context.Anchor.Message.Id);
        ContextLoadButton(context, false, pending);
    }

    private void ContextLoadButton(HistoryContext context, bool older, bool pending)
    {
        var more = older ? context.MoreOlder : context.MoreNewer;
        if (!more)
        {
            ImGui.TextDisabled(older ? Language.DbViewer_Context_NoOlder : Language.DbViewer_Context_NoNewer);
            return;
        }

        using (ImRaii.Disabled(pending))
        {
            var label = string.Format(older ? Language.DbViewer_Context_Older : Language.DbViewer_Context_Newer, ContextMore);
            if (ImGui.Button(label + (older ? "###context-older" : "###context-newer")))
                ExtendContext(context, older);
        }
    }

    private void DrawTable(string id, HistoryRow[] rows, Guid? anchor)
    {
        using var table = EndScope.Table(id, 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn(Language.DbViewer_TableField_Date, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Language.DbViewer_TableField_Type, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Language.DbViewer_TableField_Sender, ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn(Language.DbViewer_TableField_Content, ImGuiTableColumnFlags.WidthStretch, 3.0f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var isAnchor = anchor == row.Message.Id;

            ImGui.TableNextRow();
            if (isAnchor)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(ImGuiCol.TextSelectedBg));

            ImGui.TableNextColumn();
            ImGui.PushID(i);
            try
            {
                if (anchor != null && row.RowId == ScrollToRowId)
                {
                    ImGui.SetScrollHereY(isAnchor ? 0.5f : 0.0f);
                    ScrollToRowId = -1;
                }

                if (ImGui.Selectable(row.DateText, isAnchor))
                    OpenContext(row);

                if (ImGui.IsItemHovered())
                    ImGuiUtil.Tooltip(Language.DbViewer_Row_Tooltip);

                DrawRowMenu(row, anchor == null);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.ChannelText);

                ImGui.TableNextColumn();
                Plugin.ChatLog.InputHandler.ChunkHandler.DrawChunks(row.Message.Sender);

                ImGui.TableNextColumn();
                Plugin.ChatLog.InputHandler.ChunkHandler.DrawChunks(row.Message.Content);
            }
            finally
            {
                ImGui.PopID();
            }
        }
    }

    private void DrawRowMenu(HistoryRow row, bool offerContext)
    {
        using var menu = EndScope.ContextPopupItem("history-row-menu");
        if (!menu.Success)
            return;

        if (offerContext && ImGui.Selectable(Language.DbViewer_Context_Show))
            OpenContext(row);

        var sender = ChunkUtil.ToRawString(row.Message.Sender);
        var content = ChunkUtil.ToRawString(row.Message.Content);
        if (ImGui.Selectable(Language.Context_Copy))
            Copy(sender + content, Language.Context_CopySuccess);

        if (row.Message.Sender.Count > 0 && ImGui.Selectable(Language.Context_CopyContent))
            Copy(content, Language.Context_CopyContentSuccess);

        if (ImGui.Selectable(Language.DbViewer_Copy_Line))
            Copy($"[{row.DateText}][{row.ChannelText}] {sender}{content}", Language.Context_CopySuccess);
    }

    private static void Copy(string text, string notice)
    {
        ImGui.SetClipboardText(text);
        WrapperUtil.AddNotification(notice, NotificationType.Info);
    }

    private void OpenContext(HistoryRow anchor)
    {
        var channels = ContextAllChannels ? null : SelectedChannels.Keys.Select(type => (byte)type).Order().ToArray();
        var version = ++ContextVersion;
        SelectContextTab = true;
        ContextLane.Submit((conn, generation) =>
        {
            var older = HistoryIndex.Neighbours(conn, anchor.Message.Receiver, channels, anchor.Date, anchor.RowId, true, false, ContextSpan);
            var newer = HistoryIndex.Neighbours(conn, anchor.Message.Receiver, channels, anchor.Date, anchor.RowId, false, true, ContextSpan + 1);
            older.Reverse();
            var keys = older.Concat(newer).ToList();
            var rows = Materialize(conn, keys);
            if (!ContextLane.IsCurrent(generation))
                return;

            Context = new HistoryContext(version, anchor, channels, rows,
                keys.Count > 0 ? keys[0] : null, keys.Count > 0 ? keys[^1] : null,
                older.Count == ContextSpan, newer.Count == ContextSpan + 1, anchor.RowId, false);
        });
    }

    private void ExtendContext(HistoryContext current, bool older)
    {
        var edge = older ? current.First : current.Last;
        if (edge is not { } key)
            return;

        var version = ++ContextVersion;
        ContextLane.Submit((conn, generation) =>
        {
            var found = HistoryIndex.Neighbours(conn, current.Anchor.Message.Receiver, current.Channels, key.Date, key.RowId, older, false, ContextMore);
            if (older)
                found.Reverse();

            var rows = Materialize(conn, found);
            if (!ContextLane.IsCurrent(generation))
                return;

            var more = found.Count == ContextMore;
            Context = older
                ? current with { Version = version, Rows = [..rows, ..current.Rows], First = found.Count > 0 ? found[0] : current.First, MoreOlder = more, ScrollTo = current.Rows.Length > 0 ? current.Rows[0].RowId : -1, Failed = false }
                : current with { Version = version, Rows = [..current.Rows, ..rows], Last = found.Count > 0 ? found[^1] : current.Last, MoreNewer = more, ScrollTo = -1, Failed = false };
        });
    }

    private void ChannelSelection()
    {
        const string addTabPopup = "add-channel-popup";
        var spacing = 3.0f * ImGuiHelpers.GlobalScale;

        if (ImGui.Button(Language.DbViewer_Channels_Button))
            ImGui.OpenPopup(addTabPopup);

        using var popup = ImRaii.Popup(addTabPopup);
        if (!popup.Success)
            return;

        using var channelNode = ImRaii.TreeNode(Language.Options_Tabs_Channels);
        if (!channelNode.Success)
            return;

        foreach (var (header, types) in ChatTypeExt.SortOrder)
        {
            using var pushedId = ImRaii.PushId(header);

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Check))
            {
                foreach (var type in types)
                    SelectedChannels.TryAdd(type, (ChatSourceExt.All, ChatSourceExt.All));
                ChannelsVersion++;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Language.DbViewer_Channels_SelectAll);

            ImGui.SameLine(0, spacing);

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
            {
                foreach (var type in types)
                    SelectedChannels.Remove(type);
                ChannelsVersion++;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Language.DbViewer_Channels_UnselectAll);

            ImGui.SameLine(0, spacing);

            using var headerNode = ImRaii.TreeNode(header);
            if (!headerNode.Success)
                continue;

            foreach (var type in types)
            {
                if (type.IsGm())
                    continue;

                var enabled = SelectedChannels.ContainsKey(type);
                if (ImGui.Checkbox($"##{type.Name()}", ref enabled))
                {
                    if (enabled)
                        SelectedChannels[type] = (ChatSourceExt.All, ChatSourceExt.All);
                    else
                        SelectedChannels.Remove(type);
                    ChannelsVersion++;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(type.Name());
            }
        }
    }

    private ConcurrentStack<Message> Filter(Message[] messages)
    {
        // TC note: `messages.Reverse()` (extension-method syntax) resolves to
        // System.MemoryExtensions.Reverse(Span<T>) (in-place, void-returning) instead of
        // System.Linq.Enumerable.Reverse(IEnumerable<T>) on the SDK/language version used to build
        // against this Dalamud pin, since arrays implicitly convert to Span<T>. Call Enumerable.Reverse
        // explicitly to keep the original LINQ (non-mutating) behaviour.
        if (SimpleSearchTerm == "")
            return new ConcurrentStack<Message>(Enumerable.Reverse(messages).OrderByDescending(m => m.Date));

        // The store holds lines from before and after TC 7.25 changed the middle dot; either spelling finds both.
        var term = MiddleDot.Fold(SimpleSearchTerm);
        return new ConcurrentStack<Message>(
            Enumerable.Reverse(messages).Where(m =>
                MiddleDot.Fold(ChunkUtil.ToRawString(m.Sender)).Contains(term, StringComparison.InvariantCultureIgnoreCase) ||
                MiddleDot.Fold(ChunkUtil.ToRawString(m.Content)).Contains(term, StringComparison.InvariantCultureIgnoreCase)
                ).OrderByDescending(m => m.Date));
    }

    private void DateRefresh()
    {
        MinDateString = AfterDate.ToString(DateFormat);
        MaxDateString = BeforeDate.ToString(DateFormat);
    }

    private void AdjustDates()
    {
        AfterDate = new DateTime(AfterDate.Year, AfterDate.Month, AfterDate.Day, 0, 0, 0);
        BeforeDate = new DateTime(BeforeDate.Year, BeforeDate.Month, BeforeDate.Day, 23, 59, 59);
    }

    private void DateReset()
    {
        AfterDate = DateTime.Now.AddDays(-5);
        BeforeDate = DateTime.Now;

        AdjustDates();
        DateRefresh();
    }

    // Called from Draw: the query's inputs are taken here, so the player state is not read off the main thread,
    // and the query runs on a read-only connection of its own instead of the store's shared one.
    private void CreateTxtBackup()
    {
        IsExporting = true;
        ulong? character = OnlyCurrentCharacter ? Plugin.PlayerState.ContentId : null;
        var channels = SelectedChannels.Select(pair => (byte)pair.Key).ToArray();
        var after = AfterDate;
        var before = BeforeDate;
        var store = Plugin.MessageManager.Store;
        Task.Run(async () =>
        {
            try
            {
                Message[] messageHistory;
                using (var conn = store.OpenReadOnlyConnection())
                await using (var rangeMessageEnumerator = MessageStore.GetDateRange(conn, after, before, channels, character))
                    messageHistory = rangeMessageEnumerator.ToArray();

                var filteredHistory = Filter(messageHistory);

                var sb = new StringBuilder();
                await using var stream = new StreamWriter(Path.Join(InputPath, $"Chat2_{DateTime.Now:yyyy_dd_M__HH_mm_ss}.txt"));

                var batch = 0;
                foreach (var messages in filteredHistory.Batch(5000))
                {
                    await Plugin.Framework.RunOnTick(() =>
                    {
                        foreach (var message in messages)
                        {
                            if (!Sheets.LogKindSheet.TryGetRow((uint)message.Code.Type, out var logKind))
                                logKind = Sheets.LogKindSheet.GetRow(10); // default to say

                            var rossSender = new ReadOnlySeString(message.SenderSource.Encode());
                            var rossMessage = new ReadOnlySeString(message.ContentSource.Encode());

                            var timestamp = message.Date.ToLocalTime().ToString(DateTimeFormat);
                            var text = Plugin.Evaluator.Evaluate(logKind.Format, [rossSender, rossMessage]).ToString();
                            sb.AppendLine($"[{timestamp}][{message.Code.Type.Name()}]   {text}");

                            batch++;
                        }
                    }, delayTicks: 5);

                    Notification.Progress = (float)batch / filteredHistory.Count;
                    Notification.Content = $"Exported {batch} of {filteredHistory.Count} messages";
                    await stream.WriteAsync(sb.ToString());
                    sb.Clear();
                }

                await stream.WriteAsync(sb.ToString());
                sb.Clear();

                Notification.Progress = 1.0f;
                Notification.Content = "Done!!!";
                Notification.Type = NotificationType.Success;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed creating txt backup");

                Notification.Content = "Error ...";
                Notification.Type = NotificationType.Error;
            }
            finally
            {
                IsExporting = false;
                Notification.UserDismissable = true;
            }
        });
    }

    // Same split as CreateTxtBackup.
    private void CreateTempJsonFile()
    {
        IsExporting = true;
        var channels = SelectedChannels.Select(pair => (byte)pair.Key).ToArray();
        var after = AfterDate;
        var before = BeforeDate;
        var store = Plugin.MessageManager.Store;
        Task.Run(async () =>
        {
            try
            {
                Message[] messageHistory;
                using (var conn = store.OpenReadOnlyConnection())
                await using (var rangeMessageEnumerator = MessageStore.GetDateRange(conn, after, before, channels))
                    messageHistory = rangeMessageEnumerator.ToArray();

                var filteredHistory = Filter(messageHistory);

                await using var stream = new StreamWriter(Path.Join(InputPath, $"Chat2_{DateTime.Now:yyyy_dd_M__HH_mm_ss}.json"));

                var batch = 0;
                var messageContainer = new Messages();
                List<MessageResponse> templates = [];
                foreach (var messages in filteredHistory.Batch(5000))
                {
                    foreach (var message in messages)
                    {
                        templates.Add(ReadMessageContent(message));
                        batch++;
                    }

                    Notification.Progress = (float)batch / filteredHistory.Count;
                    Notification.Content = $"Exported {batch} of {filteredHistory.Count} messages";

                    await Task.Delay(100);
                }

                messageContainer.Set = templates.ToArray();
                await stream.WriteAsync(JsonConvert.SerializeObject(messageContainer));
                templates.Clear();

                await using (var fileStream = File.Open(Path.Join(InputPath, "gfdata.gfd"), FileMode.OpenOrCreate))
                {
                    await using var byteWriter = new BinaryWriter(fileStream);
                    byteWriter.Write(Plugin.DataManager.GetFile("common/font/gfdata.gfd")!.Data);
                }

                await using (var fileStream = File.Open(Path.Join(InputPath, "fonticon_ps5.tex"), FileMode.OpenOrCreate))
                {
                    await using var byteWriter = new BinaryWriter(fileStream);
                    byteWriter.Write(Plugin.DataManager.GetFile<TexFile>("common/font/fonticon_ps5.tex")!.Data);
                }

                await using (var fileStream = File.Open(Path.Join(InputPath, "FFXIV_Lodestone_SSF.ttf"), FileMode.OpenOrCreate))
                {
                    await using var byteWriter = new BinaryWriter(fileStream);
                    byteWriter.Write(Plugin.FontManager.GameSymFont);
                }

                Notification.Progress = 1.0f;
                Notification.Content = "Done!!!";
                Notification.Type = NotificationType.Success;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed creating txt backup");

                Notification.Content = "Error ...";
                Notification.Type = NotificationType.Error;
            }
            finally
            {
                IsExporting = false;
                Notification.UserDismissable = true;
            }
        });
    }

    private MessageResponse ReadMessageContent(Message message)
    {
        var response = new MessageResponse
        {
            Id = message.Id,
            Timestamp = message.Date.ToLocalTime().ToString("t", !Plugin.Config.Use24HourClock ? null : CultureInfo.CreateSpecificCulture("es-ES"))
        };

        var sender = message.Sender.Select(ProcessChunk);
        var content = message.Content.Select(ProcessChunk);
        response.Templates = sender.Concat(content).ToArray();

        return response;
    }

    private MessageTemplate ProcessChunk(Chunk chunk)
    {
        if (chunk is IconChunk { } icon)
        {
            var iconId = (uint)icon.Icon;
            return IconUtil.GfdFileView.TryGetEntry(iconId, out _) ? new MessageTemplate {PayloadType = WebPayloadType.Icon, IconId = iconId}: MessageTemplate.Empty;
        }

        if (chunk is TextChunk { } text)
        {
            if (chunk.Link is EmotePayload emotePayload && Plugin.Config.ShowEmotes)
            {
                var image = EmoteCache.GetEmote(emotePayload.Code);

                if (image is { Failed: false })
                    return new MessageTemplate { PayloadType = WebPayloadType.CustomEmote, Color = 0, Content = emotePayload.Code };
            }

            var color = text.Foreground;
            if (color == null && text.FallbackColor != null)
            {
                var type = text.FallbackColor.Value;
                color = Plugin.Config.ChatColours.TryGetValue(type, out var col) ? col : type.DefaultColor();
            }

            color ??= 0;

            var userContent = text.Content;
            if (PlayerUtil.ScreenshotMode)
            {
                if (chunk.Link is PlayerPayload playerPayload)
                    userContent = PlayerUtil.HidePlayerInString(userContent, playerPayload.PlayerName, playerPayload.World.RowId);
                else if (Plugin.PlayerState.IsLoaded)
                    userContent = PlayerUtil.HidePlayerInString(userContent, Plugin.PlayerState.CharacterName, Plugin.PlayerState.HomeWorld.RowId);
            }

            var isNotUrl = text.Link is not UriPayload;
            return new MessageTemplate { PayloadType = isNotUrl ? WebPayloadType.RawText : WebPayloadType.CustomUri, Color = color.Value, Content = userContent };
        }

        return MessageTemplate.Empty;
    }
}
