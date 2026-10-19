using System.Globalization;
using ChatTwo.Util;

namespace ChatTwo;

/// <summary>
/// The time drawn in front of a message, kept and reused every frame until the clock setting, the culture or
/// the local time it was built from changes (the "t" pattern has no seconds, so nothing else moves it).
/// Draw thread only.
/// </summary>
public sealed class MessageTimestamp
{
    private static CultureInfo? twentyFourHourCulture;

    private readonly long LocalTicks;
    private readonly CultureInfo Culture;
    private TextChunk? CachedChunk;

    private MessageTimestamp(long localTicks, CultureInfo culture, string text)
    {
        LocalTicks = localTicks;
        Culture = culture;
        Text = text;
    }

    public string Text { get; }

    /// <summary>The "[time] " chunk drawn when timestamps are not in their own column.</summary>
    public TextChunk Chunk => CachedChunk ??= new TextChunk(ChunkSource.None, null, $"[{Text}] ") { Foreground = 0xFFFFFFFF, Color = ColourUtil.RgbaToVector4(0xFFFFFFFF) };

    /// <summary>Returns <paramref name="cached"/> while it still matches, otherwise a newly formatted one.</summary>
    public static MessageTimestamp For(MessageTimestamp? cached, DateTimeOffset localTime, bool use24HourClock)
    {
        // null makes ToString use the current culture, so that is what the cached text depends on.
        var culture = use24HourClock ? TwentyFourHourCulture : CultureInfo.CurrentCulture;
        if (cached != null && cached.LocalTicks == localTime.Ticks && ReferenceEquals(cached.Culture, culture))
            return cached;

        return new MessageTimestamp(localTime.Ticks, culture, localTime.ToString("t", use24HourClock ? culture : null));
    }

    // One instance: CreateSpecificCulture builds an equal culture on every call.
    private static CultureInfo TwentyFourHourCulture => twentyFourHourCulture ??= CultureInfo.CreateSpecificCulture("de-DE");
}
