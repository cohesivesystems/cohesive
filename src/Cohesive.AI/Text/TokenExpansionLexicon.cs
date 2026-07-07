namespace Cohesive.AI.Text;

/// <summary>
/// Read-only token-id expansion lexicon.
/// </summary>
public interface ITokenExpansionLexicon
{
    /// <summary>
    /// Returns the expansion token ids for one base token id.
    /// </summary>
    ReadOnlySpan<int> GetExpansionTokenIds(int tokenId);
}

/// <summary>
/// CSR-style token-id expansion lexicon.
/// </summary>
public sealed class TokenExpansionLexicon : ITokenExpansionLexicon
{
    readonly int[] offsets;
    readonly int[] lengths;
    readonly int[] expansionTokenIds;

    /// <summary>
    /// Creates a token expansion lexicon.
    /// </summary>
    public TokenExpansionLexicon(int[] offsets, int[] lengths, int[] expansionTokenIds)
    {
        this.offsets = Guard.RequireNotNull(offsets);
        this.lengths = Guard.RequireNotNull(lengths);
        this.expansionTokenIds = Guard.RequireNotNull(expansionTokenIds);
    }

    /// <inheritdoc />
    public ReadOnlySpan<int> GetExpansionTokenIds(int tokenId)
    {
        if ((uint)tokenId >= (uint)offsets.Length)
            return [];

        return expansionTokenIds.AsSpan(offsets[tokenId], lengths[tokenId]);
    }
}
