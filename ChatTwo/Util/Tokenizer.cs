using System.Buffers;
using System.Text.RegularExpressions;

namespace ChatTwo.Util;

// Modified from: https://jack-vanlightly.com/blog/2016/2/24/a-more-efficient-regex-tokenizer
public static class Tokenizer
{
    public enum TokenType
    {
        CloseParenthesis,
        Comma,
        Dot,
        QuestionMark,
        ExclamationMark,
        Semicolon,
        Whitespace,
        Equals,
        OpenParenthesis,
        UrlString,
        StringValue,
        Leftover,
        SequenceTerminator
    }

    public class Token(TokenType tokenType, string value)
    {
        public Token(TokenType tokenType) : this(tokenType, string.Empty) { }

        public TokenType TokenType { get; } = tokenType;
        public string Value { get; } = value;
    }

    /// <summary>A token as a range of the tokenized text; the SequenceTerminator has Start = text length, Length 0.</summary>
    public readonly record struct TokenSpan(TokenType TokenType, int Start, int Length);

    public static class PrecedenceBasedRegexTokenizer
    {
        private static readonly List<TokenDefinition> TokenDefinitions;

        static PrecedenceBasedRegexTokenizer()
        {
            TokenDefinitions =
            [
                new TokenDefinition(TokenType.CloseParenthesis, "\\)", 1),
                new TokenDefinition(TokenType.Comma, ",", 1),
                new TokenDefinition(TokenType.Dot, "\\.", 1),
                new TokenDefinition(TokenType.QuestionMark, "\\?", 1),
                new TokenDefinition(TokenType.ExclamationMark, "!", 1),
                new TokenDefinition(TokenType.Semicolon, ";", 1),
                new TokenDefinition(TokenType.Whitespace, "\\s", 1),
                new TokenDefinition(TokenType.Equals, "=", 1),
                new TokenDefinition(TokenType.OpenParenthesis, "\\(", 1),
                new TokenDefinition(TokenType.UrlString, UrlRegex, 1),
                new TokenDefinition(TokenType.StringValue, "\\p{IsBasicLatin}", 2),
                new TokenDefinition(TokenType.Leftover, ".", 3)
            ];
        }

        public static IEnumerable<Token> Tokenize(string lqlText)
        {
            var tokens = new List<TokenSpan>();
            Tokenize(lqlText, tokens);
            foreach (var t in tokens)
                yield return t.TokenType == TokenType.SequenceTerminator ? new Token(t.TokenType) : new Token(t.TokenType, lqlText.Substring(t.Start, t.Length));
        }

        /// <summary>Replaces the content of <paramref name="tokens"/> with what <see cref="Tokenize(string, Span{TokenSpan})"/> writes.</summary>
        public static void Tokenize(string text, List<TokenSpan> tokens)
        {
            var buffer = new TokenSpan[text.Length + 1];
            var count = Tokenize(text, buffer);
            tokens.Clear();
            for (var i = 0; i < count; i++)
                tokens.Add(buffer[i]);
        }

        /// <summary>
        /// Writes the tokens of <paramref name="text"/> and returns how many: per start index the match of lowest
        /// precedence (on a tie the earliest definition), skipping matches that start inside the previous token, then
        /// the SequenceTerminator. Every token but the terminator is at least one character long, so
        /// <paramref name="tokens"/> needs room for text.Length + 1. Safe to call from any thread.
        /// </summary>
        public static int Tokenize(string text, Span<TokenSpan> tokens)
        {
            var count = 0;
            var length = text.Length;
            if (length > 0)
            {
                var best = ArrayPool<int>.Shared.Rent(length);
                var ends = ArrayPool<int>.Shared.Rent(length);
                try
                {
                    best.AsSpan(0, length).Fill(-1);
                    for (var d = 0; d < TokenDefinitions.Count; d++)
                    {
                        var definition = TokenDefinitions[d];
                        foreach (var match in definition.Regex.EnumerateMatches(text))
                        {
                            var current = best[match.Index];
                            if (current >= 0 && TokenDefinitions[current].Precedence <= definition.Precedence)
                                continue;

                            best[match.Index] = d;
                            ends[match.Index] = match.Index + match.Length;
                        }
                    }

                    var lastEnd = 0;
                    for (var i = 0; i < length; i++)
                    {
                        var d = best[i];
                        if (d < 0 || i < lastEnd)
                            continue;

                        tokens[count++] = new TokenSpan(TokenDefinitions[d].Type, i, ends[i] - i);
                        lastEnd = ends[i];
                    }
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(best);
                    ArrayPool<int>.Shared.Return(ends);
                }
            }

            tokens[count++] = new TokenSpan(TokenType.SequenceTerminator, length, 0);
            return count;
        }
    }

    private class TokenDefinition
    {
        public readonly TokenType Type;
        public readonly int Precedence;
        public readonly Regex Regex;

        public TokenDefinition(TokenType returnsToken, string regexPattern, int precedence)
        {
            Type = returnsToken;
            Precedence = precedence;
            Regex = new Regex(regexPattern, RegexOptions.IgnoreCase|RegexOptions.Compiled);
        }

        public TokenDefinition(TokenType returnsToken, Regex regex, int precedence)
        {
            Type = returnsToken;
            Precedence = precedence;
            Regex = regex;
        }
    }

    /// <summary>
    /// URLRegex returns a regex object that matches URLs like:
    /// - https://example.com
    /// - http://example.com
    /// - www.example.com
    /// - https://sub.example.com
    /// - example.com
    /// - sub.example.com
    ///
    /// It matches URLs with www. or https:// prefix, and also matches URLs
    /// without a prefix on specific TLDs.
    /// </summary>
    private static readonly Regex UrlRegex = new(
        @"(?<URL>((https?:\/\/|www\.)[a-z0-9-]+(\.[a-z0-9-]+)*|([a-z0-9-]+(\.[a-z0-9-]+)*\.(com|net|org|co|io|app)))(:[\d]{1,5})?(\/[^\s]*)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture
    );
}