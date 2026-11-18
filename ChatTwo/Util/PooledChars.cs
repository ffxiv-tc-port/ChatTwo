using System.Buffers;

namespace ChatTwo.Util;

/// <summary>
/// Appends strings into a caller-supplied span, moving to a rented array when it outgrows it. Call
/// <see cref="Dispose"/> in a finally block, and never on a copy: the rented array is returned to the pool there.
/// </summary>
internal ref struct PooledChars(Span<char> initial)
{
    private Span<char> Chars = initial;
    private char[]? Rented;
    private int Length;

    public readonly Span<char> Written => Chars[..Length];

    public void Append(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        if (Length + value.Length > Chars.Length)
        {
            var bigger = ArrayPool<char>.Shared.Rent(Math.Max(Chars.Length * 2, Length + value.Length));
            Chars[..Length].CopyTo(bigger);
            if (Rented != null)
                ArrayPool<char>.Shared.Return(Rented);
            Chars = Rented = bigger;
        }

        value.CopyTo(Chars[Length..]);
        Length += value.Length;
    }

    public void Dispose()
    {
        if (Rented != null)
            ArrayPool<char>.Shared.Return(Rented);
        Rented = null;
        Chars = default;
        Length = 0;
    }
}
