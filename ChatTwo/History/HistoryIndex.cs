using System.Buffers;
using System.Text;
using ChatTwo.Util;
using MessagePack;
using Microsoft.Data.Sqlite;

namespace ChatTwo.History;

// Read-only queries behind the history viewer. Everything here takes a connection the caller owns and never touches
// the store's own connection, so it can run on a worker while messages keep being written.
// Rows are addressed by (Date, rowid): rowid breaks ties between messages stamped in the same millisecond.

/// <summary>Date bounds are unix milliseconds, After inclusive and Before exclusive. Null Channels means every channel.</summary>
internal sealed record HistoryFilter(long AfterMs, long BeforeMs, byte[]? Channels, ulong? Receiver, string Term)
{
    public bool SameAs(HistoryFilter? other)
        => other != null
           && AfterMs == other.AfterMs
           && BeforeMs == other.BeforeMs
           && Receiver == other.Receiver
           && Term == other.Term
           && (Channels == null ? other.Channels == null : other.Channels != null && Channels.AsSpan().SequenceEqual(other.Channels));
}

internal readonly record struct HistoryKey(long RowId, long Date, Guid Id);

/// <summary>Matches, newest first. Truncated means more rows matched than were kept, and the oldest were dropped.</summary>
internal sealed record HistoryMatches(long[] RowIds, long[] Dates, bool Truncated);

internal static class HistoryIndex
{
    public const string MatchFunction = "c2_history_match";

    public static long Count(SqliteConnection conn, HistoryFilter filter)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE " + Where(cmd, filter, false);
        cmd.CommandTimeout = 120;
        return (long)cmd.ExecuteScalar()!;
    }

    public static List<HistoryKey> PageKeys(SqliteConnection conn, HistoryFilter filter, long offset, int count, bool newestFirst)
    {
        using var cmd = conn.CreateCommand();
        var order = newestFirst ? "DESC" : "ASC";
        cmd.CommandText = $"SELECT rowid, Date, Id FROM messages WHERE {Where(cmd, filter, false)} ORDER BY Date {order}, rowid {order} LIMIT $Count OFFSET $Offset";
        cmd.Parameters.AddWithValue("$Count", count);
        cmd.Parameters.AddWithValue("$Offset", offset);
        cmd.CommandTimeout = 120;
        return ReadKeys(cmd);
    }

    /// <summary>Every row in the filter whose sender or content text contains the term; scans the whole range.</summary>
    public static HistoryMatches FindMatches(SqliteConnection conn, HistoryFilter filter, int cap, Func<byte[], string?>? fallback = null)
    {
        var matcher = new HistoryTextMatcher(filter.Term, fallback);
        conn.CreateFunction<byte[], byte[], bool>(MatchFunction, (sender, content) => matcher.Matches(sender) || matcher.Matches(content), isDeterministic: true);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT rowid, Date FROM messages WHERE {Where(cmd, filter, true)} ORDER BY Date DESC, rowid DESC";
        cmd.CommandTimeout = 0;

        var rowIds = new List<long>();
        var dates = new List<long>();
        var truncated = false;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (rowIds.Count >= cap)
                {
                    truncated = true;
                    break;
                }

                rowIds.Add(reader.GetInt64(0));
                dates.Add(reader.GetInt64(1));
            }
        }

        return new HistoryMatches(rowIds.ToArray(), dates.ToArray(), truncated);
    }

    /// <summary>Keys of the given rows that still exist and are not deleted, ordered as asked.</summary>
    public static List<HistoryKey> KeysOf(SqliteConnection conn, ReadOnlySpan<long> rowIds, bool newestFirst)
    {
        if (rowIds.IsEmpty)
            return [];

        using var cmd = conn.CreateCommand();
        var order = newestFirst ? "DESC" : "ASC";
        cmd.CommandText = $"SELECT rowid, Date, Id FROM messages WHERE rowid IN ({JoinIds(rowIds)}) AND deleted = false ORDER BY Date {order}, rowid {order}";
        return ReadKeys(cmd);
    }

    /// <summary>
    /// Up to <paramref name="count"/> rows of one receiver next to (date, rowid): older ones newest first, or newer
    /// ones oldest first. <paramref name="inclusive"/> takes the anchor row itself as well.
    /// </summary>
    public static List<HistoryKey> Neighbours(SqliteConnection conn, ulong receiver, byte[]? channels, long date, long rowId, bool older, bool inclusive, int count)
    {
        using var cmd = conn.CreateCommand();
        var where = "deleted = false AND Receiver = $Receiver";
        if (channels != null)
            where += $" AND ChatType IN ({string.Join(", ", channels)})";

        var cmp = older ? (inclusive ? "<=" : "<") : (inclusive ? ">=" : ">");
        var bound = older ? "<=" : ">=";
        var strict = older ? "<" : ">";
        var order = older ? "DESC" : "ASC";
        cmd.CommandText = $@"SELECT rowid, Date, Id FROM messages
            WHERE {where} AND Date {bound} $Date AND (Date {strict} $Date OR rowid {cmp} $RowId)
            ORDER BY Date {order}, rowid {order} LIMIT $Count";
        cmd.Parameters.AddWithValue("$Receiver", receiver);
        cmd.Parameters.AddWithValue("$Date", date);
        cmd.Parameters.AddWithValue("$RowId", rowId);
        cmd.Parameters.AddWithValue("$Count", count);
        cmd.CommandTimeout = 120;
        return ReadKeys(cmd);
    }

    public static string JoinIds(ReadOnlySpan<long> rowIds)
    {
        var sb = new StringBuilder(rowIds.Length * 8);
        for (var i = 0; i < rowIds.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(rowIds[i]);
        }

        return sb.ToString();
    }

    private static string Where(SqliteCommand cmd, HistoryFilter filter, bool withMatch)
    {
        var where = "deleted = false AND Date >= $After AND Date < $Before";
        cmd.Parameters.AddWithValue("$After", filter.AfterMs);
        cmd.Parameters.AddWithValue("$Before", filter.BeforeMs);

        if (filter.Receiver != null)
        {
            where += " AND Receiver = $Receiver";
            cmd.Parameters.AddWithValue("$Receiver", filter.Receiver.Value);
        }

        // Inlined: they are bytes, and a parameter list cannot be bound to IN.
        if (filter.Channels != null)
            where += $" AND ChatType IN ({string.Join(", ", filter.Channels)})";

        // Last, so the cheap terms rule a row out before its blobs are decoded.
        if (withMatch)
            where += $" AND {MatchFunction}(Sender, Content)";

        return where;
    }

    private static List<HistoryKey> ReadKeys(SqliteCommand cmd)
    {
        var keys = new List<HistoryKey>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add(new HistoryKey(reader.GetInt64(0), reader.GetInt64(1), reader.GetGuid(2)));

        return keys;
    }
}

/// <summary>
/// Whether the text a Chunk[] blob shows (its text chunks joined, as ChunkUtil.ToRawString does) contains the term,
/// ignoring case and the middle dot spelling. Not thread safe: one instance per query.
/// </summary>
internal sealed class HistoryTextMatcher
{
    // Union index of TextChunk and the key of its Content in the stored layout; see Chunk.
    private const int TextChunkUnion = 0;
    private const int ContentKey = 6;

    private readonly string Term;
    private readonly Func<byte[], string?>? Fallback;
    private char[] Buffer = new char[256];

    public int FallbackCount { get; private set; }

    public HistoryTextMatcher(string term, Func<byte[], string?>? fallback = null)
    {
        Term = MiddleDot.Fold(term);
        Fallback = fallback;
    }

    public bool Matches(byte[]? blob)
    {
        if (blob == null || blob.Length == 0)
            return false;

        int length;
        try
        {
            length = Extract(blob);
        }
        catch (Exception)
        {
            // A layout this walker does not know: decode it the slow way, and count it as no match if that fails too.
            FallbackCount++;
            string? text;
            try
            {
                text = Fallback?.Invoke(blob);
            }
            catch (Exception)
            {
                text = null;
            }

            return text != null && MiddleDot.Fold(text).Contains(Term, StringComparison.OrdinalIgnoreCase);
        }

        var span = Buffer.AsSpan(0, length);
        span.Replace(MiddleDot.Legacy, MiddleDot.Current);
        return span.Contains(Term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The joined text, as ChunkUtil.ToRawString would build it, for checking the walker.</summary>
    public string ExtractText(byte[] blob)
    {
        // Extract may replace Buffer, so it has to run before Buffer is read.
        var length = Extract(blob);
        return new string(Buffer, 0, length);
    }

    private int Extract(byte[] blob)
    {
        var reader = new MessagePackReader(new ReadOnlyMemory<byte>(blob));
        if (reader.TryReadNil())
            return 0;

        var length = 0;
        var chunks = reader.ReadArrayHeader();
        for (var i = 0; i < chunks; i++)
        {
            if (reader.TryReadNil())
                continue;

            if (reader.ReadArrayHeader() != 2)
                throw new FormatException("Chunk union is not a pair");

            if (reader.ReadInt32() != TextChunkUnion)
            {
                reader.Skip();
                continue;
            }

            var fields = reader.ReadArrayHeader();
            for (var k = 0; k < fields; k++)
            {
                if (k != ContentKey)
                {
                    reader.Skip();
                    continue;
                }

                if (reader.TryReadNil())
                    continue;

                var bytes = reader.ReadStringSequence() ?? default;
                length = Append(bytes, length);
            }
        }

        return length;
    }

    private int Append(ReadOnlySequence<byte> bytes, int length)
    {
        if (bytes.IsEmpty)
            return length;

        var needed = length + Encoding.UTF8.GetMaxCharCount((int)bytes.Length);
        if (needed > Buffer.Length)
            Array.Resize(ref Buffer, Math.Max(needed, Buffer.Length * 2));

        return length + Encoding.UTF8.GetChars(bytes, Buffer.AsSpan(length));
    }
}
