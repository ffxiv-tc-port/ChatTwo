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

        // TC note: RaptureLogModule.MemberFunctionPointers.AddMsgSourceEntry is
        // resolved via a byte-pattern signature baked into FFXIVClientStructs
        // itself. On TC that signature can resolve to the wrong address without
        // throwing (same silent-mismatch class of bug as the SanitizeString and
        // RaptureAtkUnitManager.UiFlags issues fixed earlier). Hooking a wrong
        // address is worse than just reading one: it installs a detour that
        // redirects every caller of whatever's actually at that address into
        // this C# delegate with the wrong signature, crashing as soon as that
        // (unrelated) native code path runs - which lines up with the crash
        // reports coming from unrelated actions like casting a skill. Disabled
        // on TC; this only costs us content-ID/account-ID enrichment for chat
        // messages (used for the right-click "target player" context menu).
        // ContentIdResolverHook = Plugin.GameInteropProvider.HookFromAddress<RaptureLogModule.Delegates.AddMsgSourceEntry>(RaptureLogModule.MemberFunctionPointers.AddMsgSourceEntry, ContentIdResolver);
        // ContentIdResolverHook.Enable();

        Plugin.ChatGui.ChatMessageUnhandled += ChatMessage;
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.Logout += Logout;
    }

    public async ValueTask DisposeAsync()
    {
        ContentIdResolverHook?.Dispose();
        Plugin.ClientState.Logout -= Logout;
        Plugin.Framework.Update -= OnFrameworkUpdate;
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

    public void FilterAllTabs()
    {
        DateTimeOffset? since = null;
        if (!Plugin.Config.FilterIncludePreviousSessions)
            since = Plugin.GameStarted;

        // Receiver is the character a message was logged for. Passing null drops the WHERE
        // clause entirely, which is exactly what "show every character's messages" means. Note
        // the query's 10k row budget is then shared between all characters, not per character.
        var receiver = Plugin.Config.CrossCharacterMessages ? (ulong?) null : CurrentContentId;
        using var messages = Store.GetMostRecentMessages(receiver, since);

        // We store the pending messages to be added to the chat log in a
        // temporary list, and apply them all at once after filtering.
        var pendingTabs = Plugin.Config.Tabs.Select(tab => (tab, new List<Message>())).ToList();
        foreach (var message in messages)
            foreach (var (_, pendingMessages) in pendingTabs.Where(ptab => ptab.Item1.Matches(message)))
                pendingMessages.Add(message);

        // Apply the messages to the chat log in one go.
        foreach (var (tab, pendingMessages) in pendingTabs)
            tab.Messages.AddSortPrune(pendingMessages, MessageDisplayLimit);

        if (!messages.DidError) return;

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
    }

    public void FilterAllTabsAsync()
    {
        Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                FilterAllTabs();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error in FilterAllTabs");
            }

            Plugin.Log.Debug($"FilterAllTabs took {stopwatch.ElapsedMilliseconds}ms");
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
    }

    // This hook is called immediately after receiving a message with the
    // message's content ID. If multiple messages are received in the same tick,
    // this will be called for each message immediately after ChatMessage is
    // called for each message.
    private unsafe void ContentIdResolver(RaptureLogModule* agent, ulong contentId, ulong accountId, int messageIndex, ushort worldId, ushort chatType)
    {
        // fail-closed: Original is kept outside the try. It used to be the first statement *inside* it,
        // so a throw from the game's own call would have been swallowed and silently turned into "this
        // message never got resolved". Our own bookkeeping below is what the try is actually for -
        // PendingSync.Last() races with the queue being drained and throws InvalidOperationException.
        ContentIdResolverHook?.Original(agent, contentId, accountId, messageIndex, worldId, chatType);

        try
        {
            if (PendingSync.Count == 0)
                return;

            PendingSync.Last().ContentId = contentId;
            PendingSync.Last().AccountId = accountId;
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

        var isBattle = message.Code.IsBattle();
        var isCraftOrGather = message.Code.IsCraftOrGather();
        var storeByChannel = (!isBattle && !isCraftOrGather) || (isBattle && Plugin.Config.DatabaseBattleMessages) || (isCraftOrGather && Plugin.Config.DatabaseGatherCraftMessages);

        // The text rules gate storage only, exactly like DatabaseBattleMessages above: the message
        // still reaches the tabs for this session, it just will not be there after a refill. That
        // asymmetry is deliberate - it is the established meaning of the options on this screen.
        if (storeByChannel && !MessageFilterSet.Blocks(Plugin.Config.DatabaseMessageFilters, message))
            Store.UpsertMessage(message);

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
