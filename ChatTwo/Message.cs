using System.Numerics;
using System.Text;
using ChatTwo.Code;
using ChatTwo.Util;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using System.Text.RegularExpressions;
using ChatTwo.GameFunctions;
using ChatTwo.GameFunctions.Types;
using Dalamud.Game.Text;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace ChatTwo;

public partial class Message
{
    public Guid Id { get; } = Guid.NewGuid();
    public ulong Receiver { get; }
    public ulong ContentId { get; set; }
    public ulong AccountId { get; set; } // 0 if not set

    public DateTimeOffset Date { get; }
    public ChatCode Code { get; }
    public List<Chunk> Sender { get; }
    public List<Chunk> Content { get; private set; }

    public SeString SenderSource { get; }
    public SeString ContentSource { get; }

    public int SortCodeV2 { get; }
    public Guid ExtraChatChannel { get; }

    // Not stored in the database:
    public int Hash { get; }
    public Dictionary<Guid, float?> Height { get; } = new();
    public Dictionary<Guid, bool> IsVisible { get; } = new();

    // Draw thread only, like Height and IsVisible.
    private MessageTimestamp? CachedTimestamp;

    public MessageTimestamp TimestampFor(DateTimeOffset localTime, bool use24HourClock)
        => CachedTimestamp = MessageTimestamp.For(CachedTimestamp, localTime, use24HourClock);

    // Draw thread only. Per tab, the layout the Height of a collapsed group's first message was measured under;
    // a message that heads no group has no entry.
    private Dictionary<Guid, RowLayout>? RowLayouts;

    public bool HasRowLayout(Guid tab) => RowLayouts?.ContainsKey(tab) == true;

    public bool HeightMeasuredUnder(Guid tab, in RowLayout layout)
        => RowLayouts != null && RowLayouts.TryGetValue(tab, out var measured) && measured == layout;

    public void SetRowLayout(Guid tab, in RowLayout layout)
    {
        if (layout.GroupSize > 1 && layout.Side != 0)
            (RowLayouts ??= new())[tab] = layout;
        else
            RowLayouts?.Remove(tab);
    }

    public Message(ulong receiver, ulong contentId, ulong accountId, ChatCode code, List<Chunk> sender, List<Chunk> content, SeString senderSource, SeString contentSource)
    {
        var extraChatChannel = ExtractExtraChatChannel(contentSource);
        Receiver = receiver;
        ContentId = contentId;
        AccountId = accountId;
        Date = DateTimeOffset.UtcNow;
        Code = code;
        Sender = sender;
        Content = CheckMessageContent(content, extraChatChannel);
        SenderSource = senderSource;
        ContentSource = contentSource;
        SortCodeV2 = Code.ToSortCodeV2();
        ExtraChatChannel = extraChatChannel;
        Hash = GenerateHash();

        foreach (var chunk in sender.Concat(content))
            chunk.Message = this;
    }

    public Message(Guid id, ulong receiver, ulong contentId, DateTimeOffset date, ChatCode code, List<Chunk> sender, List<Chunk> content, SeString senderSource, SeString contentSource, Guid extraChatChannel)
    {
        Id = id;
        Receiver = receiver;
        ContentId = contentId;
        Date = date;
        Code = code;
        Sender = sender;
        // Don't call ReplaceContentURLs here since we're loading the message
        // from the database, and it should already have parsed URL data.
        Content = content;
        SenderSource = senderSource;
        ContentSource = contentSource;
        SortCodeV2 = code.ToSortCodeV2();
        ExtraChatChannel = extraChatChannel;
        Hash = GenerateHash();

        foreach (var chunk in sender.Concat(content))
            chunk.Message = this;
    }

    public static Message FakeMessage(List<Chunk> content, ChatCode code)
    {
        return new Message(0, 0, 0, code, [], content, new SeString(), new SeString());
    }

    public bool Matches(Dictionary<ChatType, (ChatSource Source, ChatSource Target)> channels, bool allExtraChatChannels, HashSet<Guid> extraChatChannels)
    {
        if (ExtraChatChannel != Guid.Empty)
            return allExtraChatChannels || extraChatChannels.Contains(ExtraChatChannel);

        var source = (ChatSource)(1 << (int)Code.Source);
        var target = (ChatSource)(1 << (int)Code.Target);
        return Code.Type.IsGm()
               || channels.TryGetValue(Code.Type, out var sources)
               && (Code.Source is 0 || sources.Source.HasFlag(source) || sources.Target.HasFlag(target));
    }

    public bool MatchTellTarget(TellTarget tellTarget, bool allChannelsFromSender)
    {
        if (!allChannelsFromSender && Code.Type != ChatType.TellIncoming && Code.Type != ChatType.TellOutgoing)
            return false;

        return tellTarget.ContentId == ContentId;
    }

    private int GenerateHash()
    {
        var hash = SortCodeV2.GetHashCode()
                   ^ ExtraChatChannel.GetHashCode()
                   ^ string.Join("", Sender.Select(c => c.StringValue())).GetHashCode()
                   ^ string.Join("", Content.Select(c => c.StringValue())).GetHashCode();

        if (Plugin.Config.CollapseKeepUniqueLinks)
        {
            // Hash the link too for something like DeathRecap where the message is the same
            // but the link is different
            hash ^= string.Join("", Content.Select(c => c.Link?.GetHashCode())).GetHashCode();
        }

        return hash;
    }

    private static Guid ExtractExtraChatChannel(SeString contentSource)
    {
        if (contentSource.Payloads.Count > 0 && contentSource.Payloads[0] is RawPayload raw)
        {
            // this does an encode and clone every time it's accessed, so cache
            var data = raw.Data;
            try
            {
                if (data[1] == 0x27 && data[2] == 18 && data[3] == 0x20)
                    return new Guid(data[4..^1]);
            }
            catch (ArgumentException ex)
            {
                Plugin.Log.Error(ex, "Failed to parse extra chat channel GUID");
                Plugin.Log.Error($"Byte Array: ${string.Join(", ", data[4..^1])}");
                return Guid.Empty;
            }
        }

        return Guid.Empty;
    }

    private List<Chunk> CheckMessageContent(List<Chunk> oldChunks, Guid extraChatChannel)
    {
        var newChunks = new List<Chunk>();
        void AddChunkWithMessage(TextChunk chunk)
        {
            if (string.IsNullOrEmpty(chunk.Content))
                return;

            chunk.Message = this;
            newChunks.Add(chunk);
        }

        var nextIsAutoTranslate = false;
        var checkForEmotes = (Code.IsPlayerMessage() || extraChatChannel != Guid.Empty) && Plugin.Config.ShowEmotes;
        var longest = 0;
        foreach (var chunk in oldChunks)
            if (chunk is TextChunk { Content.Length: var length } && length > longest)
                longest = length;

        Span<Tokenizer.TokenSpan> tokens = longest < 256 ? stackalloc Tokenizer.TokenSpan[longest + 1] : new Tokenizer.TokenSpan[longest + 1];
        StringBuilder? wordBuilder = null;
        StringBuilder? sentenceBuilder = null;
        foreach (var chunk in oldChunks)
        {
            // Use as is if it's not a text chunk, it already has a payload, or is auto translate
            if (chunk is not TextChunk text || chunk.Link != null || nextIsAutoTranslate)
            {
                nextIsAutoTranslate = chunk is IconChunk { Icon: BitmapFontIcon.AutoTranslateBegin };

                // No need to call AddChunkWithMessage here since the chunk
                // already has the Message field set.
                newChunks.Add(chunk);
                continue;
            }

            // The word is kept in its builder rather than turned into a string at every token: a string is only
            // needed when it turns out to be an emote, and most lines (every line without emotes) never have one.
            var content = text.Content;
            wordBuilder ??= new StringBuilder();
            sentenceBuilder ??= new StringBuilder(longest);
            wordBuilder.Clear();
            sentenceBuilder.Clear();
            var count = Tokenizer.PrecedenceBasedRegexTokenizer.Tokenize(content, tokens);
            foreach (var token in tokens[..count])
            {
                if (token.TokenType == Tokenizer.TokenType.StringValue)
                {
                    wordBuilder.Append(content, token.Start, token.Length);
                    continue;
                }

                var wordUsed = false;
                var tokenUsed = false;

                if (checkForEmotes && EmoteCache.Exists(wordBuilder))
                {
                    var word = wordBuilder.ToString();
                    if (!Plugin.Config.BlockedEmotes.Contains(word))
                    {
                        // Add the previous sentence before adding the emote
                        AddChunkWithMessage(text.NewWithStyle(chunk, sentenceBuilder.ToString()));
                        AddChunkWithMessage(new TextChunk(chunk.Source, EmotePayload.ResolveEmote(word), word) { FallbackColor = text.FallbackColor });

                        wordUsed = true;
                        sentenceBuilder.Clear();
                    }
                }

                if (token.TokenType == Tokenizer.TokenType.UrlString)
                {
                    // Add the previous sentence before adding the url
                    if (!wordUsed)
                        sentenceBuilder.Append(wordBuilder);
                    AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, sentenceBuilder.ToString()));
                    var url = content.Substring(token.Start, token.Length);
                    try
                    {
                        AddChunkWithMessage(text.NewWithStyle(chunk.Source, UriPayload.ResolveUri(url), url));
                    }
                    catch (UriFormatException)
                    {
                        AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, url));
                        Plugin.Log.Debug($"Invalid URL accepted by Regex but failed URI parsing: '{url}'");
                    }

                    wordUsed = true;
                    tokenUsed = true;
                    sentenceBuilder.Clear();
                }

                if (!wordUsed)
                    sentenceBuilder.Append(wordBuilder);
                wordBuilder.Clear();

                // Append match if we haven't reached end of string yet
                if (token.TokenType != Tokenizer.TokenType.SequenceTerminator)
                {
                    if (!tokenUsed)
                        sentenceBuilder.Append(content, token.Start, token.Length);
                    continue;
                }

                // End of string reached, we add our leftover. Nothing split off means the leftover is the whole text.
                AddChunkWithMessage(text.NewWithStyle(chunk, sentenceBuilder.Equals(content.AsSpan()) ? content : sentenceBuilder.ToString()));
            }
        }

        return newChunks;
    }

    public unsafe void DecodeTextParam()
    {
        var newChunks = new List<Chunk>();
        void AddChunkWithMessage(TextChunk chunk)
        {
            if (string.IsNullOrEmpty(chunk.Content))
                return;

            chunk.Message = this;
            newChunks.Add(chunk);
        }

        foreach (var chunk in Content)
        {
            // Use as is if it's not a text chunk or it already has a payload.
            if (chunk is not TextChunk text || chunk.Link != null)
            {
                // No need to call AddChunkWithMessage here since the chunk
                // already has the Message field set.
                newChunks.Add(chunk);
                continue;
            }

            var splits = TextParamRegex().Split(text.Content);
            if (splits.Length == 1)
            {
                newChunks.Add(chunk);
                continue;
            }

            var nextIsMatch = false;
            foreach (var split in splits)
            {
                if (split == "" || !nextIsMatch)
                {
                    nextIsMatch = true;
                    AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, split));
                    continue;
                }

                nextIsMatch = false;
                // The surrounding try covers the *throw* half of these three Instance() calls; the null half it
                // cannot cover (a null dereference is an uncatchable AccessViolationException), so the resolves
                // go through Chat.ResolveOrNull, which also logs the throw once per 30s with a useful key.
                // Degradation for all three: the <item>/<status>/<flag> placeholder is emitted as literal text,
                // which is what the catch below and the "no item linked" / "no flag set" branches already do.
                try
                {
                    if (split == "<item>")
                    {
                        var agentChat = Chat.ResolveOrNull<AgentChatLog>(&AgentChatLog.Instance, "Message/linkedItem", "Could not resolve AgentChatLog; <item> was left as literal text");
                        if (agentChat == null)
                        {
                            AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, split));
                            continue;
                        }

                        var item = agentChat->LinkedItem;

                        if (item.ItemId == 0)
                        {
                            AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, split));
                            continue;
                        }

                        var kind = item.ItemId switch
                        {
                            < 500_000 => ItemKind.Normal,
                            < 1_000_000 => ItemKind.Collectible,
                            < 2_000_000 => ItemKind.Hq,
                            _ => ItemKind.EventItem
                        };

                        var name = kind != ItemKind.EventItem
                            ? Sheets.ItemSheet.GetRow(item.ItemId).Name.ToString()
                            : Sheets.EventItemSheet.GetRow(item.ItemId).Name.ToString();

                        var link = new ItemPayload(item.ItemId, kind, $"{SeIconChar.LinkMarker.ToIconChar()}{name}");
                        AddChunkWithMessage(text.NewWithStyle(chunk.Source, link, link.DisplayName ?? "Unknown"));
                    }
                    else if (split == "<status>")
                    {
                        var agentChat = Chat.ResolveOrNull<AgentChatLog>(&AgentChatLog.Instance, "Message/contextStatus", "Could not resolve AgentChatLog; <status> was left as literal text");
                        if (agentChat == null)
                        {
                            AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, split));
                            continue;
                        }

                        var statusId = agentChat->ContextStatusId;
                        if (statusId == 0 || !Sheets.StatusSheet.TryGetRow(statusId, out var statusRow))
                        {
                            AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, split));
                            continue;
                        }

                        var nameValue = statusRow.Name.ToString();
                        var content = statusRow.StatusCategory switch
                        {
                            1 => $"{SeIconChar.Buff.ToIconString()}{nameValue}",
                            2 => $"{SeIconChar.Debuff.ToIconString()}{nameValue}",
                            _ => nameValue
                        };

                        var link = new StatusPayload(statusId);
                        AddChunkWithMessage(text.NewWithStyle(chunk.Source, link, content));
                    }
                    else if (split == "<flag>")
                    {
                        var agentMap = Chat.ResolveOrNull<AgentMap>(&AgentMap.Instance, "Message/flagMarker", "Could not resolve AgentMap; <flag> was left as literal text");
                        if (agentMap == null)
                        {
                            AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, split));
                            continue;
                        }

                        if (agentMap->FlagMarkerCount == 0)
                        {
                            AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, split));
                            continue;
                        }

                        var mapCoords = agentMap->FlagMapMarkers[0];
                        var rawX = (int)(MathF.Round(mapCoords.XFloat, 3, MidpointRounding.AwayFromZero) * 1000);
                        var rawY = (int)(MathF.Round(mapCoords.YFloat, 3, MidpointRounding.AwayFromZero) * 1000);

                        var link = new MapLinkPayload(mapCoords.TerritoryId, mapCoords.MapId, rawX, rawY);
                        AddChunkWithMessage(text.NewWithStyle(chunk.Source, link, $"{SeIconChar.LinkMarker.ToIconChar()}{link.PlaceName} {link.CoordinateString}"));
                    }

                }
                catch (Exception)
                {
                    AddChunkWithMessage(text.NewWithStyle(chunk.Source, chunk.Link, split));
                    Plugin.Log.Debug($"Failed to parse the text param: '{split}'");
                }
            }
        }

        Content = newChunks;
    }

    [GeneratedRegex("(<item>|<flag>|<status>)")]
    private static partial Regex TextParamRegex();
}

/// <summary>
/// What the height of a collapsed group's row depends on besides the message: the group size (its " (Nx)" is part
/// of the row), the width it wraps in, the timestamp it starts with when timestamps have no column of their own, the
/// font and scale, whether player names are hidden or emotes shown, whether icons can be drawn yet, and which side of
/// the screen's top edge the row lies on (1 below, -1 above, 0 across: never kept).
/// </summary>
public readonly record struct RowLayout(int GroupSize, float Width, string? Stamp, Vector2 FontProbe, float Scale, bool ScreenshotMode, bool ShowEmotes, bool IconsReady, int Side);
