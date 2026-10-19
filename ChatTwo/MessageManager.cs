using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Text.Expressions;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace ChatTwo;

public class MessageManager : IAsyncDisposable
{
    public const int MessageDisplayLimit = 10_000;

    private Plugin Plugin { get; }
    public MessageStore Store { get; }

    private Dictionary<ChatType, NameFormatting> Formats { get; } = [];
    private ulong LastContentId { get; set; }

    // Messages go into the PendingSync queue first, which will be consumed one
    // at a time in the main thread. This is to delay the async processing until
    // after we've received the content ID from the ContentIdResolver hook.
    //
    // After that, the message is enqueued in the PendingAsync queue, which will
    // be consumed in a separate thread and perform more processing (emotes,
    // URLs) as well as inserting the message into the database.
    private Queue<PendingMessage> PendingSync { get; } = [];
    private ConcurrentQueue<PendingMessage> PendingAsync { get; } = [];
    private readonly Thread PendingMessageThread;
    private readonly CancellationTokenSource PendingThreadCancellationToken = new();

    private Hook<RaptureLogModule.Delegates.AddMsgSourceEntry>? ContentIdResolverHook { get; init; }

    // The message ChatMessage queued last, while the game's source call for it is still outstanding.
    // Main thread only. Emptied when the IDs are attached, when Dalamud swallows the next message and
    // when the queue is drained, so IDs never land on an earlier message or on one already handed off.
    private AwaitingSlot<PendingMessage> AwaitingSource { get; } = new();
    private bool LoggedFirstResolverCall;
    private bool LoggedFirstResolvedId;

    // Refills run one at a time under RefillLock, which also guards LastRefill. The arrival log has its own lock:
    // the message thread writes it for every stored line and must not wait behind a refill.
    private readonly object RefillLock = new();
    private RefillMark? LastRefill;
    private readonly object ArrivalLock = new();
    private List<(Guid Id, long Date)>? ArrivedSinceRefill = [];

    private sealed record RefillMark(Configuration Config, ulong? Receiver, DateTimeOffset? Since, long StoreRemovals, string? Rules,
                                     Tab[] Tabs, Tab.MessageList[] Lists, long[] Removals);

    public ulong CurrentContentId
    {
        get
        {
            var contentId = Plugin.PlayerState.ContentId;
            return contentId == 0 ? LastContentId : contentId;
        }
    }

    public unsafe MessageManager(Plugin plugin)
    {
        Plugin = plugin;

        try
        {
            Store = new MessageStore(plugin, DatabasePath());
        }
        catch(Exception ex)
        {
            // migration failed, so we create a new database
            if (Plugin.Config.MigrationStatus == MigrationStatus.Failed)
            {
                Plugin.Log.Warning("Migration failed, attempting fresh database");
                Store = new MessageStore(plugin, DatabasePath());

                Plugin.Config.MigrationStatus = MigrationStatus.Finished;
                Plugin.SaveConfig();
            }
            else
            {
                // Something else went wrong, rethrow
                Plugin.Log.Error(ex, "Failed to open database");
                throw;
            }
        }

        PendingMessageThread = new Thread(() => ProcessPendingMessages(PendingThreadCancellationToken.Token));
        PendingMessageThread.Start();

        // Stored before it is enabled: the detour reaches Original through this property, and the game
        // can call the detour the moment the hook goes live.
        ContentIdResolverHook = CreateContentIdResolverHook(out var resolverLocation);

        Plugin.ChatGui.ChatMessageUnhandled += ChatMessage;
        Plugin.ChatGui.ChatMessageHandled += ChatMessageHandled;
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.Logout += Logout;

        EnableContentIdResolverHook(resolverLocation);
    }

    // Null means the feature is off and the plugin still loads: messages then carry no sender
    // content ID, exactly as when the hook did not exist.
    private unsafe Hook<RaptureLogModule.Delegates.AddMsgSourceEntry>? CreateContentIdResolverHook(out string location)
    {
        location = "unknown";
        try
        {
            var address = (nint) RaptureLogModule.MemberFunctionPointers.AddMsgSourceEntry;
            if (address == nint.Zero)
            {
                Plugin.Log.Information("ContentIdResolver: RaptureLogModule.AddMsgSourceEntry was not found in this game version, so the hook is off; messages carry no sender content ID");
                return null;
            }

            location = DescribeAddress(address);
            return Plugin.GameInteropProvider.HookFromAddress<RaptureLogModule.Delegates.AddMsgSourceEntry>(address, ContentIdResolver);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "ContentIdResolver: could not create the hook; messages carry no sender content ID");
            return null;
        }
    }

    private void EnableContentIdResolverHook(string location)
    {
        if (ContentIdResolverHook == null)
            return;

        try
        {
            ContentIdResolverHook.Enable();
            Plugin.Log.Information($"ContentIdResolver: hooked RaptureLogModule.AddMsgSourceEntry at {location}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"ContentIdResolver: could not enable the hook at {location}; messages carry no sender content ID");
        }
    }

    // Module-relative so the line can be compared against an offline scan of the executable.
    private static string DescribeAddress(nint address)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            if (process.MainModule is { } module && address >= module.BaseAddress)
                return $"{module.ModuleName}+0x{address - module.BaseAddress:X}";
        }
        catch (Exception)
        {
            // Only a log decoration; the absolute address below is still usable.
        }

        return $"0x{address:X}";
    }

    public async ValueTask DisposeAsync()
    {
        ContentIdResolverHook?.Dispose();
        Plugin.ClientState.Logout -= Logout;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.ChatGui.ChatMessageHandled -= ChatMessageHandled;
        Plugin.ChatGui.ChatMessageUnhandled -= ChatMessage;

        await PendingThreadCancellationToken.CancelAsync();
        var timeout = 10_000; // 10s
        while (timeout > 0)
        {
            if (!PendingMessageThread.IsAlive)
                break;

            timeout -= 100;
            await Task.Delay(100);
            Plugin.Log.Debug("Sleeping because PendingMessageThread thread still alive");
        }

        Store.Dispose();
    }

    public static string DatabasePath()
    {
        return Path.Join(Plugin.Interface.ConfigDirectory.FullName, "chat-sqlite.db");
    }

    private void Logout(int _, int __)
    {
        LastContentId = 0;

        // Tabs already drop the previous character's messages on logout unless the user opted
        // into AllSenderMessages, so the settings window must not be the one place a line from
        // the character they just left keeps showing up.
        MessageFilterSet.ForgetSample();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var contentId = Plugin.PlayerState.ContentId;
        if (contentId != 0)
            LastContentId = contentId;

        // Drain the PendingSync queue into the PendingAsync queue.
        while (PendingSync.TryDequeue(out var pending))
            PendingAsync.Enqueue(pending);

        AwaitingSource.Disarm();
    }

    private void ProcessPendingMessages(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (PendingAsync.TryDequeue(out var pendingMessage))
            {
                try
                {
                    ProcessMessage(pendingMessage);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Error processing pending message");
                }
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    public void ClearAllTabs()
    {
        foreach (var tab in Plugin.Config.Tabs)
            tab.Clear();
    }

    /// <summary>Reads the newest stored messages again and adds every one a tab shows but does not hold yet.</summary>
    public void FilterAllTabs() => Refill(false);

    /// <summary>
    /// What a login needs: the tabs end up exactly as after <see cref="FilterAllTabs"/>, but when nothing since the
    /// previous refill can change what that would add, only the rows the tabs may still lack are parsed: lines stored
    /// since then and lines the display limit dropped since then. Returns how it went, for the log.
    /// </summary>
    public string RefillAfterLogin() => Refill(true);

    private string Refill(bool afterLogin)
    {
        lock (RefillLock)
        {
            var config = Plugin.Config;
            DateTimeOffset? since = null;
            if (!config.FilterIncludePreviousSessions)
                since = Plugin.GameStarted;

            // Receiver is the character a message was logged for. Passing null drops the WHERE
            // clause entirely, which is exactly what "show every character's messages" means. Note
            // the query's 10k row budget is then shared between all characters, not per character.
            var receiver = config.CrossCharacterMessages ? (ulong?) null : CurrentContentId;
            var tabs = config.Tabs.ToArray();
            var storeRemovals = Store.Removals;
            var rules = RulesKey(config, tabs);

            // Taken by every refill, as each one starts a new period for the next login to build on.
            var arrived = TakeArrivals();
            var pruned = new List<(Guid Id, long Date)>?[tabs.Length];
            var removals = new long[tabs.Length];
            for (var i = 0; i < tabs.Length; i++)
                pruned[i] = tabs[i].Messages.TakeRefillLog(out removals[i]);

            var whyFull = afterLogin ? WhyFullRefill(config, receiver, since, storeRemovals, rules, tabs, removals, arrived, pruned) : "not a login";
            RowSet? only = null;
            if (whyFull == null)
            {
                only = new RowSet();
                foreach (var (id, date) in arrived!)
                    only.Add(id, date);
                foreach (var rows in pruned)
                    foreach (var (id, date) in rows!)
                        only.Add(id, date);
            }

            // Cleared first: a refill that throws has consumed the logs the next one would need.
            LastRefill = null;
            var failed = FillTabs(tabs, receiver, since, only);

            // Learning a tell tab's target mid-refill changes the rules, and a failed row was just deleted,
            // which moves the query window: either way the next login reads everything.
            LastRefill = !failed && rules != null && rules == RulesKey(config, tabs)
                ? new RefillMark(config, receiver, since, storeRemovals, rules, tabs, tabs.Select(tab => tab.Messages).ToArray(), removals)
                : null;

            return whyFull == null ? $"login: parsed only the {only!.Count} lines stored or dropped since the last refill" : $"read everything: {whyFull}";
        }
    }

    // True when some rows failed to parse (they are marked deleted, as before).
    private bool FillTabs(Tab[] tabs, ulong? receiver, DateTimeOffset? since, RowSet? only)
    {
        // We store the pending messages to be added to the chat log in a
        // temporary list, and apply them all at once after filtering.
        var pendingTabs = tabs.Select(tab => (tab, new List<Message>())).ToList();
        if (only is { Count: 0 })
        {
            foreach (var (tab, pendingMessages) in pendingTabs)
                tab.Messages.AddSortPrune(pendingMessages, MessageDisplayLimit);
            return false;
        }

        using var messages = Store.GetMostRecentMessages(receiver, since, only: only);
        foreach (var message in messages)
        {
            // The same global rules ProcessMessage applies. Messages dropped on arrival were never stored,
            // but a rule added today says nothing about what was written yesterday: without this a rule
            // promoted from a tab would appear to do nothing until the old rows aged out of the query window.
            // Evaluated once per message rather than inside Tab.Matches, which runs once per tab.
            if (MessageFilterSet.Blocks(Plugin.Config.DatabaseMessageFilters, message))
                continue;

            foreach (var (_, pendingMessages) in pendingTabs.Where(ptab => ptab.Item1.Matches(message)))
                pendingMessages.Add(message);
        }

        // Apply the messages to the chat log in one go.
        foreach (var (tab, pendingMessages) in pendingTabs)
            tab.Messages.AddSortPrune(pendingMessages, MessageDisplayLimit);

        if (!messages.DidError) return false;

        WrapperUtil.AddNotification(Language.LoadMessages_Error, NotificationType.Error);

        // Mark the failed messages as deleted so we don't try to load them
        // again.
        var failedIds = messages.FailedMessageIds();
        Plugin.Log.Info($"Marking {failedIds.Count} messages as deleted due to parse failures");
        foreach (var msgId in messages.FailedMessageIds())
        {
            Plugin.Log.Debug($"Marking message '{msgId}' as deleted due to parse failure");
            Store.DeleteMessage(msgId);
        }

        return true;
    }

    // Null when the rows outside the logs are known to change nothing: each was already offered to every tab under
    // the same rules, and whatever it added is still there. Otherwise why everything has to be read.
    private string? WhyFullRefill(Configuration config, ulong? receiver, DateTimeOffset? since, long storeRemovals, string? rules,
                                  Tab[] tabs, long[] removals, List<(Guid Id, long Date)>? arrived, List<(Guid Id, long Date)>?[] pruned)
    {
        var last = LastRefill;
        if (last == null)
            return "no earlier refill to build on";
        if (last.Config != config || last.Tabs.Length != tabs.Length)
            return "the tab list changed";
        if (last.Receiver != receiver || last.Since != since)
            return "the query window changed";
        if (last.StoreRemovals != storeRemovals)
            return "rows were deleted from the database";
        if (rules == null)
            return "the rules were being edited";
        if (last.Rules != rules)
            return "tab or database rules changed";
        if (arrived == null)
            return "too many lines arrived to track";

        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            if (last.Tabs[i] != tab || last.Lists[i] != tab.Messages)
                return "the tab list changed";
            if (last.Removals[i] != removals[i])
                return $"messages were taken out of tab {i}";
            if (pruned[i] == null)
                return $"tab {i} dropped too many lines to track";

            // Tab.Matches writes the target's content id on the first line it recognises, so which line comes
            // first matters while it is unknown.
            if (tab.Channel == InputChannel.Tell && tab.TellTarget?.IsSet() == true && tab.TellTarget.ContentId == 0)
                return $"tell tab {i} has not learned its target yet";
        }

        return null;
    }

    // Everything Tab.Matches and the database rules read: two refills with equal keys judge every line the same way.
    // Null when a collection changed under the enumeration (the context menu edits channels on the main thread).
    private static string? RulesKey(Configuration config, Tab[] tabs)
    {
        try
        {
            return BuildRulesKey(config, tabs);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string BuildRulesKey(Configuration config, Tab[] tabs)
    {
        var key = new StringBuilder();
        AppendRules(key, config.DatabaseMessageFilters);
        foreach (var tab in tabs)
        {
            key.Append(" | tab");
            foreach (var (type, (source, target)) in tab.SelectedChannels.OrderBy(pair => pair.Key))
                key.Append(' ').Append((ushort) type).Append(':').Append((ushort) source).Append(':').Append((ushort) target);
            key.Append(tab.ExtraChatAll ? " extra all" : " extra");
            foreach (var channel in tab.ExtraChatChannels.Order())
                key.Append(' ').Append(channel);
            AppendRules(key, tab.MessageFilters);
            key.Append(" input ").Append(tab.Channel.HasValue ? (long) tab.Channel.Value : -1).Append(tab.AllSenderMessages ? " all senders" : " tells");
            var tell = tab.TellTarget;
            key.Append(" target ").Append(tell?.Name?.Length ?? -1).Append(':').Append(tell?.Name).Append(' ').Append(tell?.World).Append(' ').Append(tell?.ContentId);
        }

        return key.ToString();
    }

    private static void AppendRules(StringBuilder key, List<MessageFilter> filters)
    {
        key.Append(" rules ").Append(filters.Count);
        foreach (var filter in filters)
            key.Append(' ').Append(filter.Enabled ? 'E' : 'e').Append(filter.IsRegex ? 'R' : 'r').Append(filter.Pattern?.Length ?? -1).Append(':').Append(filter.Pattern);
    }

    private List<(Guid Id, long Date)>? TakeArrivals()
    {
        lock (ArrivalLock)
        {
            var arrived = ArrivedSinceRefill;
            ArrivedSinceRefill = [];
            return arrived;
        }
    }

    private void NoteArrival(Message message)
    {
        lock (ArrivalLock)
        {
            if (ArrivedSinceRefill == null)
                return;
            if (ArrivedSinceRefill.Count < MessageDisplayLimit)
                ArrivedSinceRefill.Add((message.Id, message.Date.ToUnixTimeMilliseconds()));
            else
                ArrivedSinceRefill = null;
        }
    }

    public void FilterAllTabsAsync() => RefillAsync(false);

    public void RefillAfterLoginAsync() => RefillAsync(true);

    private void RefillAsync(bool afterLogin)
    {
        Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var how = string.Empty;
            try
            {
                how = $" ({Refill(afterLogin)})";
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error in FilterAllTabs");
            }

            Plugin.Log.Debug($"FilterAllTabs took {stopwatch.ElapsedMilliseconds}ms{how}");
        });
    }

    public (SeString? Sender, SeString? Message) LastMessage = (null, null);

    // TC note: Dalamud.Game.Chat.IChatMessage doesn't exist at true API13 (see
    // XivChatRelationKind.cs) - IChatGui.ChatMessageUnhandled still exists at this API level, but
    // with the old 4-arg delegate shape below (no relation info at all), so Source/Target always
    // report XivChatRelationKind.LocalPlayer.
    private void ChatMessage(XivChatType type, int timestamp, SeString sender, SeString message)
    {
        LastMessage = (sender, message);

        var pendingMessage = new PendingMessage
        {
            ContentId = 0,
            AccountId = 0,
            LogKind = type,
            SourceKind = XivChatRelationKind.LocalPlayer,
            TargetKind = XivChatRelationKind.LocalPlayer,
            Sender = sender,
            Content = message,
        };

        // Update colour codes.
        GlobalParametersCache.Refresh();

        // We delay messages to be handed off to the async processing thread
        // in the next tick, otherwise we can't get the content ID from the hook
        // below.
        PendingSync.Enqueue(pendingMessage);
        AwaitingSource.Arm(pendingMessage);
    }

    // Dalamud swallowed this message, so ChatMessage never ran for it, yet the game still reports its
    // source next. Emptying the slot keeps those IDs off the previous message of the same frame.
    private void ChatMessageHandled(XivChatType type, int timestamp, SeString sender, SeString message)
    {
        AwaitingSource.Disarm();
    }

    // The game calls this right after printing a message that has a sender, with that sender's IDs.
    // Contract: native detour on the main thread. No pointer is dereferenced here - `agent` only
    // travels on to Original - and nothing may throw out of it, hence both try blocks.
    private unsafe void ContentIdResolver(RaptureLogModule* agent, ulong contentId, ulong accountId, int messageIndex, ushort worldId, ushort chatType)
    {
        try
        {
            ContentIdResolverHook?.OriginalDisposeSafe(agent, contentId, accountId, messageIndex, worldId, chatType);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "ContentIdResolver: could not call the original function");
        }

        try
        {
            var pending = AwaitingSource.Take();
            if (!LoggedFirstResolverCall)
            {
                LoggedFirstResolverCall = true;
                Plugin.Log.Information($"ContentIdResolver: first call this session (a message was waiting for it: {pending != null}, it carried a content ID: {contentId != 0})");
            }

            if (pending == null)
                return;

            pending.ContentId = contentId;
            pending.AccountId = accountId;

            if (contentId != 0 && !LoggedFirstResolvedId)
            {
                LoggedFirstResolvedId = true;
                Plugin.Log.Information($"ContentIdResolver: first sender content ID attached this session (log kind {(int) pending.LogKind}, source chat type {chatType})");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error in ContentIdResolver");
        }
    }

    private void ProcessMessage(PendingMessage pendingMessage)
    {
        var chatCode = new ChatCode(pendingMessage.LogKind, pendingMessage.SourceKind, pendingMessage.TargetKind);

        NameFormatting? formatting = null;
        if (pendingMessage.Sender.Payloads.Count > 0)
            formatting = FormatFor(chatCode.Type);

        var senderChunks = new List<Chunk>();
        if (formatting is { IsPresent: true })
        {
            senderChunks.Add(new TextChunk(ChunkSource.None, null, formatting.Before) { FallbackColor = chatCode.Type });
            senderChunks.AddRange(ChunkUtil.ToChunks(pendingMessage.Sender, ChunkSource.Sender, chatCode.Type));
            senderChunks.Add(new TextChunk(ChunkSource.None, null, formatting.After) { FallbackColor = chatCode.Type });
        }

        var contentChunks = ChunkUtil.ToChunks(pendingMessage.Content, ChunkSource.Content, chatCode.Type).ToList();
        var message = new Message(CurrentContentId, pendingMessage.ContentId, pendingMessage.AccountId, chatCode, senderChunks, contentChunks, pendingMessage.Sender, pendingMessage.Content);

        // Keep the newest line that actually has a speaker around for the filter editor to show.
        // Only messages with a sender are useful there - the point of the sample is to display
        // the separator the game puts between name and text, which the others do not have.
        if (senderChunks.Count > 0)
            MessageFilterSet.RememberSample(message);

        // The global text rules drop the message outright: it is neither stored nor shown. They are not
        // the same kind of switch as DatabaseBattleMessages below - that one is a coarse "this whole
        // category is not worth the disk space", while a rule here names one specific thing.
        // Deliberately after RememberSample: the filter editor's sample line is the only place a user can
        // see the separator the game puts between sender and text, including for lines a rule excludes.
        if (MessageFilterSet.Blocks(Plugin.Config.DatabaseMessageFilters, message))
            return;

        var isBattle = message.Code.IsBattle();
        var isCraftOrGather = message.Code.IsCraftOrGather();
        var storeByChannel = (!isBattle && !isCraftOrGather) || (isBattle && Plugin.Config.DatabaseBattleMessages) || (isCraftOrGather && Plugin.Config.DatabaseGatherCraftMessages);

        if (storeByChannel)
        {
            Store.UpsertMessage(message);
            NoteArrival(message);
        }

        var currentTabId = Plugin.CurrentTab.Identifier;
        var currentMatches = Plugin.CurrentTab.Matches(message);
        foreach (var tab in Plugin.Config.Tabs)
        {
            var unread = !(tab.UnreadMode == UnreadMode.Unseen && Plugin.CurrentTab != tab && currentMatches);

            if (tab.Matches(message))
            {
                tab.AddMessage(message, unread);

                if (tab.Identifier == currentTabId)
                    Plugin.ServerCore.SendNewMessage(message);
            }
        }
    }

    public class NameFormatting
    {
        public string Before { get; private set; } = string.Empty;
        public string After { get; private set; } = string.Empty;
        public bool IsPresent { get; private set; } = true;

        public static NameFormatting Empty()
        {
            return new NameFormatting { IsPresent = false, };
        }

        public static NameFormatting Of(string before, string after)
        {
            return new NameFormatting
            {
                Before = before,
                After = after,
            };
        }
    }

    private NameFormatting FormatFor(ChatType type)
    {
        if (Formats.TryGetValue(type, out var cached))
            return cached;

        var formats = Sheets.LogKindSheet.GetRow((uint)type).Format.ToList();
        static bool IsStringParam(ReadOnlySePayload payload, byte num)
        {
            if (payload.MacroCode != MacroCode.String)
                return false;

            return payload.TryGetExpression(out var expr1)
                && expr1.TryGetParameterExpression(out var expressionType, out var operand)
                && expressionType == (byte)ExpressionType.LocalString
                && operand.TryGetInt(out var lstrIndex)
                && lstrIndex == num;
        }

        var firstStringParam = formats.FindIndex(payload => IsStringParam(payload, 1));
        var secondStringParam = formats.FindIndex(payload => IsStringParam(payload, 2));

        if (firstStringParam == -1 || secondStringParam == -1)
            return NameFormatting.Empty();

        var before = formats
            .GetRange(0, firstStringParam)
            .Where(payload => payload.Type == ReadOnlySePayloadType.Text)
            .Select(text => Encoding.UTF8.GetString(text.Body.Span));
        var after = formats
            .GetRange(firstStringParam + 1, secondStringParam - firstStringParam)
            .Where(payload => payload.Type == ReadOnlySePayloadType.Text)
            .Select(text => Encoding.UTF8.GetString(text.Body.Span)); // Can't use `ToString()` as it defaults to macro

        var nameFormatting = NameFormatting.Of(string.Join("", before), string.Join("", after));
        Formats[type] = nameFormatting;

        return nameFormatting;
    }

    private class PendingMessage
    {
        public ulong ContentId; // 0 if unknown
        public ulong AccountId; // 0 if unknown
        public XivChatType LogKind;
        public XivChatRelationKind SourceKind;
        public XivChatRelationKind TargetKind;
        public required SeString Sender;
        public required SeString Content;
    }
}
