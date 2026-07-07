using System.Runtime.CompilerServices;

namespace Cohesive.AI.Text;

/// <summary>
/// CSR-style token-id to item-id posting-list index.
/// </summary>
public sealed class PostingListIndex : IDisposable
{
    readonly int[] offsets;
    readonly int[] lengths;
    readonly int[] items;

    /// <summary>
    /// Creates a CSR-style posting-list index.
    /// </summary>
    public PostingListIndex(int[] offsets, int[] lengths, int[] items)
    {
        this.offsets = Guard.RequireNotNull(offsets);
        this.lengths = Guard.RequireNotNull(lengths);
        this.items = Guard.RequireNotNull(items);
    }

    /// <summary>
    /// Resolves the item-id slice for one token id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> GetItems(int tokenId)
    {
        if ((uint)tokenId >= (uint)offsets.Length)
            return [];

        return items.AsSpan(offsets[tokenId], lengths[tokenId]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}