using System.Text;
using System.Text.RegularExpressions;

namespace ChatTwo;

/// <summary>
/// One user-authored rule for dropping a message by its text.
/// </summary>
[Serializable]
public class MessageFilter
{
    /// <summary>
    /// Plain substring to look for, or a regular expression when <see cref="IsRegex"/> is set.
    /// Plain text is the default: the things people actually want to mute are fixed sentences
    /// the game emits, and a substring test is both faster and impossible to get wrong.
    /// </summary>
    public string Pattern = string.Empty;

    public bool IsRegex;
    public bool Enabled = true;

    /// <summary>
    /// Compiling on every message would dominate the cost, so the compiled form is cached.
    /// It is a single immutable object held in one field: reference assignment is atomic, so
    /// the message thread can never observe a Regex paired with a different pattern than the
    /// one it was built from. Two threads racing to compile the same pattern is harmless -
    /// they produce equivalent results and one simply wins.
    /// </summary>
    [NonSerialized] private CompiledFilter? Cache;

    public CompiledFilter Compiled
    {
        get
        {
            var cached = Cache; // read the field once; it may be replaced concurrently
            if (cached != null && cached.Pattern == Pattern && cached.IsRegex == IsRegex)
                return cached;

            var built = Compile(Pattern, IsRegex);
            Cache = built;
            return built;
        }
    }

    public bool Blocks(string text)
    {
        if (!Enabled || Pattern.Length == 0 || text.Length == 0)
            return false;

        var compiled = Compiled;
        if (!compiled.IsRegex)
            return text.Contains(Pattern, StringComparison.OrdinalIgnoreCase);

        // A pattern that doesn't compile never matches. The settings window shows the compiler's
        // own error next to the input, so this is visible rather than mysterious.
        if (compiled.Regex == null)
            return false;

        try
        {
            return compiled.Regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Only reachable on the backtracking fallback path. Information level because the
            // user is the one who has to fix the pattern, and they run at LogLevel 1 (Debug is captured, but drowned by 100k+ Debug lines per log file).
            //
            // Null-conditional on purpose: Plugin.Log is an injected static declared `= null!`,
            // so it is genuinely null before the services land and after disposal. A pattern
            // timing out must degrade to "did not match", never turn into a NullReferenceException
            // thrown out of the message thread. (regexguard hit exactly this offline.)
            if (compiled.ReportTimeoutOnce())
                Plugin.Log?.Information($"Chat filter pattern took too long and is being ignored: {Pattern}");

            return false;
        }
    }

    public MessageFilter Clone() => new() { Pattern = Pattern, IsRegex = IsRegex, Enabled = Enabled };

    // Chat lines are short. Anything that takes this long on one is a runaway pattern, not a
    // slow one, and letting it run would stall the message thread (see ProcessPendingMessages)
    // which looks exactly like chat having stopped working.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private const RegexOptions SharedOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static CompiledFilter Compile(string pattern, bool isRegex)
    {
        if (!isRegex || pattern.Length == 0)
            return new CompiledFilter(pattern, isRegex, null, null, false);

        // NonBacktracking is linear in the input by construction, so no user-authored pattern
        // can melt down no matter how it is written - the (a+)+$ family simply cannot blow up.
        // It refuses backreferences, lookaround and atomic groups though, so patterns that need
        // those fall through to the normal engine with a hard match timeout instead.
        try
        {
            return new CompiledFilter(pattern, true, new Regex(pattern, SharedOptions | RegexOptions.NonBacktracking), null, false);
        }
        catch (NotSupportedException)
        {
            // Uses a construct NonBacktracking cannot express. Keep going, but time-boxed.
        }
        catch (Exception ex)
        {
            return new CompiledFilter(pattern, true, null, ex.Message, false);
        }

        try
        {
            return new CompiledFilter(pattern, true, new Regex(pattern, SharedOptions, MatchTimeout), null, true);
        }
        catch (Exception ex)
        {
            // This runs from the settings window, i.e. the ImGui draw path, where an escaping
            // exception takes the whole plugin UI down until the game restarts. Catch everything.
            return new CompiledFilter(pattern, true, null, ex.Message, false);
        }
    }

    public sealed class CompiledFilter(string pattern, bool isRegex, Regex? regex, string? error, bool backtracking)
    {
        public string Pattern { get; } = pattern;
        public bool IsRegex { get; } = isRegex;
        public Regex? Regex { get; } = regex;

        /// <summary>The regex compiler's own message, or null if the pattern is usable.</summary>
        public string? Error { get; } = error;

        /// <summary>
        /// True when this pattern needed the backtracking engine, so it is bounded by a timeout
        /// rather than being linear by construction. Surfaced in the UI so a user who wrote a
        /// lookahead knows why their rule is the one that can be slow.
        /// </summary>
        public bool Backtracking { get; } = backtracking;

        private int Reported;

        /// <summary>Returns true exactly once, so a bad pattern logs once instead of per message.</summary>
        public bool ReportTimeoutOnce() => Interlocked.Exchange(ref Reported, 1) == 0;
    }
}

public static class MessageFilterSet
{
    /// <summary>
    /// Whether any enabled rule in the list matches this message's text.
    /// </summary>
    /// <remarks>
    /// Called from the message thread and from FilterAllTabs, never from a draw path, so the
    /// cost here is off the frame loop. The empty-list check comes first because that is the
    /// path every user who never touches this feature takes, and it must stay free.
    /// </remarks>
    public static bool Blocks(List<MessageFilter> filters, Message message)
    {
        if (filters.Count == 0)
            return false;

        var text = TextOf(message);
        if (text.Length == 0)
            return false;

        // Indexed rather than foreach: the settings window builds a whole new list and assigns
        // it over the live one, and an enumerator would be the only thing here that could throw
        // if that ever happened mid-scan.
        for (var i = 0; i < filters.Count; i++)
            if (filters[i].Blocks(text))
                return true;

        return false;
    }

    /// <summary>
    /// The whole visible line - sender first, then content - with icon chunks left out.
    /// </summary>
    /// <remarks>
    /// The sender chunks already carry the game's own separator (LogKind.Format's text around
    /// the name), so this is literally what the line reads as: "Tataru Taru : text" for NPC
    /// dialogue, "Tataru Taru：text" for say, "Tataru Taru &gt;&gt; text" for a tell. That means a
    /// rule can be anchored at a speaker with ^, which is the only way to mute one retainer or
    /// one player without also muting every message that happens to contain their name.
    /// <para>
    /// Including the sender cannot make a plain-substring rule match more than it used to for
    /// the messages people actually write those rules against: system lines ("You catch a fish")
    /// have no sender at all, so their text is unchanged.
    /// </para>
    /// <para>
    /// Deliberately not cached on Message: Content is replaced wholesale by the
    /// &lt;item&gt;/&lt;flag&gt; expansion, so a cache would be stale for exactly the messages
    /// that contain links. Sender is assigned once in the constructor and never reassigned, and
    /// Content is swapped by reference rather than mutated in place, so walking either of them
    /// from another thread cannot throw.
    /// </para>
    /// </remarks>
    public static string TextOf(Message message)
    {
        var builder = new StringBuilder();
        foreach (var chunk in message.Sender)
            if (chunk is TextChunk text)
                builder.Append(text.Content);

        foreach (var chunk in message.Content)
            if (chunk is TextChunk text)
                builder.Append(text.Content);

        return builder.ToString();
    }

    /// <summary>
    /// The last message that arrived with a sender, so the settings window can show a real line
    /// instead of describing one.
    /// </summary>
    /// <remarks>
    /// The separator is not ours: it comes from the game's LogKind sheet and differs per channel
    /// and per client language (say is a fullwidth colon, NPC dialogue is space-colon-space).
    /// Prose cannot tell a user which one to type, and getting it wrong produces a rule that
    /// silently never matches - so the editor shows the genuine article instead.
    /// <para>
    /// Written on the message thread, read on the draw path, with no lock: a reference
    /// assignment is atomic, so a reader sees one whole Message or another, never a torn one.
    /// Holding one message alive costs nothing and the value survives the tab pruning it.
    /// </para>
    /// </remarks>
    private static Message? Sample;

    public static void RememberSample(Message message) => Sample = message;

    public static void ForgetSample() => Sample = null;

    /// <summary>The sample line, or null when no message with a sender has arrived yet.</summary>
    public static string? SampleText()
    {
        var sample = Sample; // read the field once; it may be replaced concurrently
        if (sample == null)
            return null;

        var text = TextOf(sample);
        return text.Length == 0 ? null : text;
    }
}
