using System.Buffers;
using System.Collections;
using System.Data.Common;
using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Ui;
using ChatTwo.Util;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Microsoft.Data.Sqlite;

using DalamudUtil = Dalamud.Utility.Util;
using Encoding = System.Text.Encoding;

namespace ChatTwo;

public static class DbExtensions
{
    public static void Execute(this DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

public enum PayloadMessagePackType : byte
{
    Achievement,
    PartyFinder,
    Uri,
    Emote,
    Other = 255,
}

public class PayloadMessagePackFormatter : IMessagePackFormatter<Payload?>
{
    public void Serialize(ref MessagePackWriter writer, Payload? value, MessagePackSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteArrayHeader(2);
        switch (value)
        {
            case AchievementPayload achievementPayload:
                writer.WriteUInt8((byte)PayloadMessagePackType.Achievement);
                writer.WriteUInt32(achievementPayload.Id);
                break;
            case PartyFinderPayload partyFinderPayload:
                writer.WriteUInt8((byte)PayloadMessagePackType.PartyFinder);
                writer.WriteUInt32(partyFinderPayload.Id);
                break;
            case UriPayload uriPayload:
                writer.WriteUInt8((byte)PayloadMessagePackType.Uri);
                writer.WriteString(Encoding.UTF8.GetBytes(uriPayload.Uri.ToString()));
                break;
            case EmotePayload emotePayload:
                writer.WriteUInt8((byte)PayloadMessagePackType.Emote);
                writer.WriteString(Encoding.UTF8.GetBytes(emotePayload.Code));
                break;
            default:
                writer.WriteUInt8((byte)PayloadMessagePackType.Other);
                writer.Write(value.Encode());
                break;
        }
    }

    public Payload? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;

        if (reader.ReadArrayHeader() != 2)
            throw new InvalidOperationException("Invalid array count for Payload object");

        var type = (PayloadMessagePackType)reader.ReadByte();
        switch (type)
        {
            case PayloadMessagePackType.Achievement:
                return new AchievementPayload(reader.ReadUInt32());
            case PayloadMessagePackType.PartyFinder:
                return new PartyFinderPayload(reader.ReadUInt32());
            case PayloadMessagePackType.Uri:
                return new UriPayload(new Uri(reader.ReadString() ?? ""));
            case PayloadMessagePackType.Emote:
                return EmotePayload.ResolveEmote(reader.ReadString() ?? "");
            case PayloadMessagePackType.Other:
            default:
                var bytes = reader.ReadBytes() ?? new ReadOnlySequence<byte>();
                var binReader = new BinaryReader(new MemoryStream(bytes.ToArray()));
                return Payload.Decode(binReader);
        }
    }
}

public class SeStringMessagePackFormatter : IMessagePackFormatter<SeString?>
{
    public void Serialize(ref MessagePackWriter writer, SeString? value, MessagePackSerializerOptions options)
    {
        options.Resolver.GetFormatter<List<Payload>>()!.Serialize(ref writer, value?.Payloads ?? [], options);
    }

    public SeString Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        return new SeString(options.Resolver.GetFormatter<List<Payload>>()!.Deserialize(ref reader, options));
    }
}

public class MessageStore : IDisposable
{
    private const int MessageQueryLimit = 10_000;

    private Plugin Plugin;
    private string DbPath { get; }

    private SqliteConnection Connection { get; set; }

    // Built and prepared once instead of per message (that alone was over half of what storing a message
    // allocated). Upserts come from the message thread and the debug injector thread, hence the lock.
    private readonly object UpsertLock = new();
    private SqliteCommand? UpsertCommand;

    private long removals;

    /// <summary>Bumped after every change that takes rows out, so a refill can tell its query window may have moved back.</summary>
    public long Removals => Interlocked.Read(ref removals);

    public static readonly MessagePackSerializerOptions MsgPackOptions = MessagePackSerializerOptions.Standard
        .WithResolver(CompositeResolver.Create([new PayloadMessagePackFormatter(), new SeStringMessagePackFormatter()], [StandardResolver.Instance]));

    public MessageStore(Plugin plugin, string dbPath)
    {
        Plugin = plugin;
        DbPath = dbPath;
        Connection = Connect();
        Migrate();
    }

    // Close() releases the file only if no command made on Connection is still waiting for its
    // finalizer, so every command is disposed where it is used (reader ones by MessageEnumerator).
    public void Dispose()
    {
        lock (UpsertLock)
        {
            UpsertCommand?.Dispose();
            UpsertCommand = null;
        }

        Connection.Close();
        Connection.Dispose();
    }

    private SqliteConnection Connect()
    {
        var uriBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            DefaultTimeout = 5,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        var conn = new SqliteConnection(uriBuilder.ToString());
        conn.Open();
        conn.Execute(@"PRAGMA journal_mode=WAL;");
        conn.Execute(@"PRAGMA synchronous=NORMAL;");
        if (DalamudUtil.IsWine())
            conn.Execute(@"PRAGMA cache_size = 32768;");
        return conn;
    }

    private void Migrate()
    {
        // Get current user_version.
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var userVersion = Convert.ToInt32(cmd.ExecuteScalar());

        var migrationsToDo = new List<Action>();
        switch (userVersion)
        {
            case <= 0:
                migrationsToDo.Add(Migrate0);

                // Migration support was only added in version 1. Migrate 0 is
                // idempotent.
                migrationsToDo.Add(Migrate1);
                migrationsToDo.Add(Migrate2);
                migrationsToDo.Add(Migrate3);
                break;
            case 1:
                migrationsToDo.Add(Migrate2);
                migrationsToDo.Add(Migrate3);
                break;
            case 2:
                migrationsToDo.Add(Migrate3);
                break;
        }

        foreach (var migration in migrationsToDo)
            migration();
    }

    private void Migrate0()
    {
        Plugin.Log.Information("Running migration 0: Creating tables");
        Connection.Execute(@"
            CREATE TABLE IF NOT EXISTS messages (
                Id BLOB PRIMARY KEY NOT NULL,  -- Guid
                Receiver INTEGER NOT NULL,     -- uint64 (first bits are always 0)
                ContentId INTEGER NOT NULL,    -- uint64 (first bits are always 0)
                Date INTEGER NOT NULL,         -- unix timestamp with millisecond precision
                Code INTEGER NOT NULL,         -- ChatCode encoding
                Sender BLOB NOT NULL,          -- Chunk[] msgpack
                Content BLOB NOT NULL,         -- Chunk[] msgpack
                SenderSource BLOB NOT NULL,    -- SeString
                ContentSource BLOB NOT NULL,   -- SeString
                SortCode INTEGER NOT NULL,     -- SortCode encoding
                ExtraChatChannel BLOB NOT NULL -- Guid
            );

            CREATE INDEX IF NOT EXISTS idx_messages_receiver ON messages (Receiver);
            CREATE INDEX IF NOT EXISTS idx_messages_date ON messages (Date);
        ");

        SetMigrationVersion(0);
    }

    private void Migrate1()
    {
        Plugin.Log.Information("Running migration 1: Adding Deleted column");
        Connection.Execute(@"
            -- Migration 1: Add Deleted column
            ALTER TABLE messages ADD COLUMN Deleted BOOLEAN NOT NULL DEFAULT false;
        ");

        SetMigrationVersion(1);
    }

    private void Migrate2()
    {
        Plugin.Log.Information("Running migration 2: Adding Channel generated column");
        Connection.Execute(@"
            -- Migration 2: Add Channel generated column
            ALTER TABLE messages ADD COLUMN Channel INTEGER GENERATED ALWAYS AS (Code & 0x7f) VIRTUAL;
            CREATE INDEX IF NOT EXISTS idx_messages_channel ON messages (Channel);
        ");

        SetMigrationVersion(2);
    }

    private void Migrate3()
    {
        Plugin.Log.Information("Running migration 3: Fix log kinds to fit the new format");

        // Only set this the first
        if (Plugin.Config.MigrationStatus == MigrationStatus.NotStarted)
        {
            Plugin.Config.MigrationStatus = MigrationStatus.Started;
            Plugin.SaveConfig();
        }

        try
        {
            // Only backup if this is the first time
            if (Plugin.Config.MigrationStatus == MigrationStatus.Started)
            {
                File.Copy(DbPath, $"{DbPath}-migration-bak");

                Plugin.Config.MigrationStatus = MigrationStatus.Copied;
                Plugin.SaveConfig();
            }

            Connection.Execute(@"
                -- Migration 3: Fix log kinds to fit the new format
                -- Add new ChatType, SourceKind, TargetKind (byte), SortCodeV2
                -- Migrate OldChatColumn
                    -- ChatType = OldChatColumn & 0x7f
                    -- SourceKind = log2(1 << ((OldChatColumn >> 11) & 0xF))
                    -- TargetKind = trunc(log2(1 << ((OldChatColumn >> 7) & 0xF)))
                    -- Virtual SortCodeV2 = ChatType << 16 | SourceKind << 8 | TargetKind
                -- Delete OldChatColumn, Virtual Channel

                ALTER TABLE messages ADD COLUMN ChatType INTEGER;
                CREATE INDEX IF NOT EXISTS idx_messages_chat_type ON messages (ChatType);
                ALTER TABLE messages ADD COLUMN SourceKind INTEGER;
                ALTER TABLE messages ADD COLUMN TargetKind INTEGER;

                UPDATE messages SET
                                    ChatType = Code & 0x7f,
                                    SourceKind = trunc(log2(1 << ((Code >> 11) & 0xF))),
                                    TargetKind = trunc(log2(1 << ((Code >> 7) & 0xF)))
                WHERE true;

                DROP INDEX idx_messages_channel;
                ALTER TABLE messages DROP COLUMN Channel;
                ALTER TABLE messages DROP COLUMN Code;
                ALTER TABLE messages DROP COLUMN SortCode;
            ");

            SetMigrationVersion(3);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to migrate database");
            Plugin.Notification.AddNotification(
                new Notification
                {
                    Title = Language.Database_Migration_Error_Title,
                    Content = Language.Database_Migration_Error_Desc,
                    Type = NotificationType.Error,
                    Minimized = false,
                    UserDismissable = true,
                    InitialDuration = TimeSpan.FromSeconds(100000),
                });

            Connection.Close();
            Connection.Dispose();

            File.Delete(DbPath);

            Plugin.Config.MigrationStatus = MigrationStatus.Failed;
            Plugin.SaveConfig();

            throw;
        }

        // Only delete backup if migration has successfully ran the first time
        if (Plugin.Config.MigrationStatus == MigrationStatus.Copied)
        {
            File.Delete($"{DbPath}-migration-bak");

            Plugin.Config.MigrationStatus = MigrationStatus.Finished;
            Plugin.SaveConfig();
        }
    }

    private void SetMigrationVersion(int version)
    {
        Plugin.Log.Information($"Setting version {version}");
        using var cmd = Connection.CreateCommand();
        // Parameters aren't supported for PRAGMA queries, and you can't set the
        // version with a pragma_ function.
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    public void ClearMessages()
    {
        Connection.Execute("DELETE FROM messages;");
        Interlocked.Increment(ref removals);
        PerformMaintenance();
    }

    public void PerformMaintenance()
    {
        Connection.Execute(@"
            VACUUM;
            REINDEX messages;
            ANALYZE;
        ");
    }

    private string LogPath => DbPath + "-wal";
    public long DatabaseSize() => !File.Exists(DbPath) ? 0 : new FileInfo(DbPath).Length;
    public long DatabaseLogSize() => !File.Exists(LogPath) ? 0 : new FileInfo(LogPath).Length;

    public int MessageCount()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpsertMessage(Message message)
    {
        var sender = MessagePackSerializer.Serialize(message.Sender, MsgPackOptions);
        var content = MessagePackSerializer.Serialize(message.Content, MsgPackOptions);
        var senderSource = MessagePackSerializer.Serialize(message.SenderSource, MsgPackOptions);
        var contentSource = MessagePackSerializer.Serialize(message.ContentSource, MsgPackOptions);

        lock (UpsertLock)
        {
            var cmd = UpsertCommand ??= CreateUpsertCommand();
            var parameters = cmd.Parameters;
            parameters[0].Value = message.Id;
            parameters[1].Value = message.Receiver;
            parameters[2].Value = message.ContentId;
            parameters[3].Value = message.Date.ToUnixTimeMilliseconds();
            parameters[4].Value = message.Code.Type;
            parameters[5].Value = message.Code.Source;
            parameters[6].Value = message.Code.Target;
            parameters[7].Value = sender;
            parameters[8].Value = content;
            parameters[9].Value = senderSource;
            parameters[10].Value = contentSource;
            parameters[11].Value = message.ExtraChatChannel;

            try
            {
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // The next message starts from a fresh command, as every message did before the reuse.
                UpsertCommand = null;
                cmd.Dispose();
                throw;
            }
        }
    }

    // Parameter order is the index order UpsertMessage assigns in.
    private SqliteCommand CreateUpsertCommand()
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO messages (
                Id,
                Receiver,
                ContentId,
                Date,
                ChatType,
                SourceKind,
                TargetKind,
                Sender,
                Content,
                SenderSource,
                ContentSource,
                ExtraChatChannel,
                Deleted
            ) VALUES (
                $Id,
                $Receiver,
                $ContentId,
                $Date,
                $ChatType,
                $SourceKind,
                $TargetKind,
                $Sender,
                $Content,
                $SenderSource,
                $ContentSource,
                $ExtraChatChannel,
                false
            )
            ON CONFLICT (id) DO UPDATE SET
                Receiver = excluded.Receiver,
                ContentId = excluded.ContentId,
                Date = excluded.Date,
                ChatType = excluded.ChatType,
                SourceKind = excluded.SourceKind,
                TargetKind = excluded.TargetKind,
                Sender = excluded.Sender,
                Content = excluded.Content,
                SenderSource = excluded.SenderSource,
                ContentSource = excluded.ContentSource,
                ExtraChatChannel = excluded.ExtraChatChannel,
                Deleted = false;
        ";

        foreach (var name in (string[])["$Id", "$Receiver", "$ContentId", "$Date", "$ChatType", "$SourceKind", "$TargetKind", "$Sender", "$Content", "$SenderSource", "$ContentSource", "$ExtraChatChannel"])
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            cmd.Parameters.Add(parameter);
        }

        return cmd;
    }

    /// <summary>
    /// Get the most recent messages.
    /// </summary>
    /// <param name="receiver">The receiver content ID to filter by. If null, no filtering is performed.</param>
    /// <param name="since">Only show messages since this date. If null, no filtering is performed.</param>
    /// <param name="count">The amount to return. Defaults to 10,000.</param>
    /// <param name="only">When set, rows not in it are skipped without being parsed; the rows, their order and the
    /// window they are taken from are the same as without it.</param>
    public MessageEnumerator GetMostRecentMessages(ulong? receiver = null, DateTimeOffset? since = null, int count = MessageQueryLimit, RowSet? only = null)
    {
        List<string> whereClauses = ["deleted = false"];
        if (receiver != null)
            whereClauses.Add("Receiver = $Receiver");
        if (since != null)
            whereClauses.Add("Date >= $Since");

        var whereClause = "WHERE " + string.Join(" AND ", whereClauses);

        var cmd = Connection.CreateCommand();
        // Select last N messages by date DESC, but reverse the order to get
        // them in ascending order.
        cmd.CommandText = @"
            SELECT *
            FROM (
                SELECT
                    Id,
                    Receiver,
                    ContentId,
                    Date,
                    ChatType,
                    SourceKind,
                    TargetKind,
                    Sender,
                    Content,
                    SenderSource,
                    ContentSource,
                    ExtraChatChannel
                FROM messages
                " + whereClause + @"
                ORDER BY Date DESC
                LIMIT $Count
            )
            ORDER BY Date ASC;
        ";
        cmd.CommandTimeout = 120; // this could take a while on slow computers

        if (receiver != null)
            cmd.Parameters.AddWithValue("$Receiver", receiver);
        if (since != null)
            cmd.Parameters.AddWithValue("$Since", since.Value.ToUnixTimeMilliseconds());

        cmd.Parameters.AddWithValue("$Count", count);

        return Enumerate(cmd, only);
    }

    private static MessageEnumerator Enumerate(SqliteCommand cmd, RowSet? only = null)
    {
        try
        {
            return new MessageEnumerator(cmd.ExecuteReader(), cmd) { Only = only };
        }
        catch
        {
            cmd.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Marks a message as deleted so it won't get returned in queries.
    /// </summary>
    public void DeleteMessage(Guid id)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "UPDATE messages SET Deleted = true WHERE Id = $Id;";
        cmd.Parameters.AddWithValue("$Id", id);
        cmd.ExecuteNonQuery();
        Interlocked.Increment(ref removals);
    }

    public long CountDateRange(DateTime after, DateTime before, IEnumerable<byte> channels, ulong? receiver = null)
    {
        List<string> whereClauses = ["deleted = false"];
        if (receiver != null)
            whereClauses.Add("Receiver = $Receiver");

        whereClauses.Add("Date BETWEEN $After AND $Before");
        whereClauses.Add($"ChatType IN ({string.Join(", ", channels)})");

        var whereClause = "WHERE " + string.Join(" AND ", whereClauses);

        using var cmd = Connection.CreateCommand();
        // Select last N messages by date DESC, but reverse the order to get
        // them in ascending order.
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM messages
            " + whereClause;

        if (receiver != null)
            cmd.Parameters.AddWithValue("$Receiver", receiver);

        cmd.Parameters.AddWithValue("$After", ((DateTimeOffset) after).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$Before", ((DateTimeOffset) before).ToUnixTimeMilliseconds());
        cmd.CommandTimeout = 120; // this could take a while on slow computers

        return (long) cmd.ExecuteScalar()!;
    }

    public MessageEnumerator GetDateRange(DateTime after, DateTime before, IEnumerable<byte> channels, ulong? receiver = null)
    {
        List<string> whereClauses = ["deleted = false"];
        if (receiver != null)
            whereClauses.Add("Receiver = $Receiver");

        whereClauses.Add("Date BETWEEN $After AND $Before");
        whereClauses.Add($"ChatType IN ({string.Join(", ", channels)})");

        var whereClause = $"WHERE {string.Join(" AND ", whereClauses)}";

        var cmd = Connection.CreateCommand();
        // Select last N messages by date DESC, but reverse the order to get
        // them in ascending order.
        cmd.CommandText = @"
            SELECT
                Id,
                Receiver,
                ContentId,
                Date,
                ChatType,
                SourceKind,
                TargetKind,
                Sender,
                Content,
                SenderSource,
                ContentSource,
                ExtraChatChannel
            FROM messages
            " + whereClause;
        cmd.CommandTimeout = 120; // this could take a while on slow computers

        if (receiver != null)
            cmd.Parameters.AddWithValue("$Receiver", receiver);

        cmd.Parameters.AddWithValue("$After", ((DateTimeOffset) after).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$Before", ((DateTimeOffset) before).ToUnixTimeMilliseconds());

        return Enumerate(cmd);
    }

    public MessageEnumerator GetPagedDateRange(DateTime after, DateTime before, IEnumerable<byte> channels, ulong? receiver = null, int page = 0)
    {
        List<string> whereClauses = ["deleted = false"];
        if (receiver != null)
            whereClauses.Add("Receiver = $Receiver");

        whereClauses.Add("Date BETWEEN $After AND $Before");
        whereClauses.Add($"ChatType IN ({string.Join(", ", channels)})");

        var whereClause = $"WHERE {string.Join(" AND ", whereClauses)}";

        var cmd = Connection.CreateCommand();
        // Select last N messages by date DESC, but reverse the order to get
        // them in ascending order.
        cmd.CommandText = @"
            SELECT
                Id,
                Receiver,
                ContentId,
                Date,
                ChatType,
                SourceKind,
                TargetKind,
                Sender,
                Content,
                SenderSource,
                ContentSource,
                ExtraChatChannel
            FROM messages
            " + whereClause + @"
            ORDER BY Date
            LIMIT $Offset, $OffsetCount;
        ";
        cmd.CommandTimeout = 120; // this could take a while on slow computers

        if (receiver != null)
            cmd.Parameters.AddWithValue("$Receiver", receiver);

        cmd.Parameters.AddWithValue("$After", ((DateTimeOffset) after).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$Before", ((DateTimeOffset) before).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$Offset", DbViewer.RowPerPage * page);
        cmd.Parameters.AddWithValue("$OffsetCount", DbViewer.RowPerPage);

        return Enumerate(cmd);
    }
}

/// <summary>
/// Rows picked by id. The stored date of each is kept as well: reading a row's id allocates (it is stored as text),
/// its date does not, so nearly every row is ruled out by date alone.
/// </summary>
public sealed class RowSet
{
    private readonly HashSet<Guid> Ids = [];
    private readonly HashSet<long> Dates = [];

    public int Count => Ids.Count;

    public void Add(Guid id, long unixMilliseconds)
    {
        Ids.Add(id);
        Dates.Add(unixMilliseconds);
    }

    public bool MayHold(long unixMilliseconds) => Dates.Contains(unixMilliseconds);

    public bool Holds(Guid id) => Ids.Contains(id);
}

/// <summary>Owns both the reader and the command that produced it; disposing it disposes both.</summary>
public class MessageEnumerator(DbDataReader reader, DbCommand command) : IEnumerable<Message>, IDisposable, IAsyncDisposable
{
    private const int MaxErrorLogs = 10;

    // FailedIds and FailedCount are separate, because messages might fail to
    // even parse the ID field.
    private readonly List<Guid> FailedIds = [];
    private int FailedCount;
    public bool DidError => FailedCount > 0;

    /// <summary>Rows with a readable id and date not in this set are passed over unparsed; null reads every row.</summary>
    public RowSet? Only { get; init; }

    public IEnumerator<Message> GetEnumerator()
    {
        while (reader.Read())
        {
            if (Only != null && !Wanted(Only))
                continue;

            var id = Guid.Empty;
            Message msg;
            try
            {
                id = reader.GetGuid(0);
                msg = new Message(
                    id,
                    (ulong)reader.GetInt64(1),
                    (ulong)reader.GetInt64(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    new ChatCode((byte)reader.GetInt32(4), (byte)reader.GetInt32(5), (byte)reader.GetInt32(6)),
                    MessagePackSerializer.Deserialize<List<Chunk>>(reader.GetFieldValue<byte[]>(7), MessageStore.MsgPackOptions),
                    MessagePackSerializer.Deserialize<List<Chunk>>(reader.GetFieldValue<byte[]>(8), MessageStore.MsgPackOptions),
                    MessagePackSerializer.Deserialize<SeString>(reader.GetFieldValue<byte[]>(9), MessageStore.MsgPackOptions),
                    MessagePackSerializer.Deserialize<SeString>(reader.GetFieldValue<byte[]>(10), MessageStore.MsgPackOptions),
                    reader.GetGuid(11)
                );
            }
            catch (Exception e)
            {
                if (FailedCount < MaxErrorLogs)
                    Plugin.Log.Error($"Exception while reading message '{id}' from database: {e}");
                FailedCount++;
                if (FailedCount == MaxErrorLogs)
                    Plugin.Log.Error("Further parsing errors will not be logged");
                if (id != Guid.Empty)
                    FailedIds.Add(id);

                continue;
            }

            yield return msg;
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    // A read that throws falls through to the parse below, which then fails and counts the row as without the filter.
    private bool Wanted(RowSet only)
    {
        try
        {
            return only.MayHold(reader.GetInt64(3)) && only.Holds(reader.GetGuid(0));
        }
        catch (Exception)
        {
            return true;
        }
    }

    public IReadOnlyList<Guid> FailedMessageIds()
    {
        return FailedIds;
    }

    public void Dispose()
    {
        reader.Dispose();
        command.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();
        await command.DisposeAsync();
    }
}
