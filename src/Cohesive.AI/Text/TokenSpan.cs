namespace Cohesive.AI.Text;

/// <summary>
/// Compact reference into a pooled token-id buffer.
/// </summary>
public readonly record struct TokenSpan
{
    /// <summary>
    /// Creates a token span.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public TokenSpan(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length), $"Token span length must be <= {ushort.MaxValue}.");

        Offset = offset;
        Length = (ushort)length;
    }

    /// <summary>
    /// Offset into the backing token buffer.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    /// Number of token ids contained in the span.
    /// </summary>
    public ushort Length { get; }

    /// <summary>
    /// Indicates whether the span contains no token ids.
    /// </summary>
    public bool IsEmpty => Length == 0;

    /// <summary>
    /// Shared empty span.
    /// </summary>
    public static TokenSpan Empty { get; } = new(0, 0);
}