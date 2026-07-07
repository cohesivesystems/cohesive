using System.Runtime.CompilerServices;

namespace Cohesive.AI.Text;

/// <summary>
/// Stores token-id sequences in one flat buffer to avoid per-sequence allocations.
/// </summary>
public sealed class TokenTable
{
    readonly int[] buffer;

    /// <summary>
    /// Creates a token table.
    /// </summary>
    public TokenTable(int[] buffer)
    {
        this.buffer = Guard.RequireNotNull(buffer);
    }

    /// <summary>
    /// Number of token ids stored in the backing buffer.
    /// </summary>
    public int Count => buffer.Length;

    /// <summary>
    /// Returns the token ids referenced by one span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> GetSpan(TokenSpan span) =>
        span.IsEmpty ? [] : buffer.AsSpan(span.Offset, span.Length);

    /// <summary>
    /// Shared empty table.
    /// </summary>
    public static TokenTable Empty { get; } = new([]);
}