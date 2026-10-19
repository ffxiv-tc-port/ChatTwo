using System.Diagnostics.CodeAnalysis;

namespace ChatTwo.Util;

/// <summary>
/// From TC 7.25 the game writes the middle dot as U+30FB instead of U+00B7, so rules typed the old
/// way stop matching. Matching folds both sides to U+30FB; the user's stored text is never rewritten.
/// </summary>
internal static class MiddleDot
{
    public const char Legacy = (char)0xB7;
    public const char Current = (char)0x30FB;

    /// <summary>Returns the same instance, allocation free, when there is no U+00B7 in it.</summary>
    [return: NotNullIfNotNull(nameof(s))]
    public static string? Fold(string? s)
        => s is null || !s.Contains(Legacy) ? s : s.Replace(Legacy, Current);
}
