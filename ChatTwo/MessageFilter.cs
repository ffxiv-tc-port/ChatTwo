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
            // user is the one who has to fix the pattern, and they run at LogLevel 2.
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
    /// The message's visible text, with sender and icons left out. Deliberately not cached on
    /// Message: Content is rewritten in place by the &lt;item&gt;/&lt;flag&gt; expansion, so a
    /// cache would be stale for exactly the messages that contain links.
    /// </summary>
    private static string TextOf(Message message)
    {
        var builder = new StringBuilder();
        foreach (var chunk in message.Content)
            if (chunk is TextChunk text)
                builder.Append(text.Content);

        return builder.ToString();
    }
}
