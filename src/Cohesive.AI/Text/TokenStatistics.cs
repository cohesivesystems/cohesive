namespace Cohesive.AI.Text;

/// <summary>
/// Per-token document-frequency statistics.
/// </summary>
public sealed class TokenStatistics
{
    readonly int[] documentFrequencies;
    readonly int documentCount;

    /// <summary>
    /// Creates token statistics.
    /// </summary>
    /// <param name="documentFrequencies">Document frequency by token id.</param>
    /// <param name="documentCount">Total number of documents represented by the statistics.</param>
    public TokenStatistics(int[] documentFrequencies, int documentCount)
    {
        this.documentFrequencies = Guard.RequireNotNull(documentFrequencies);
        this.documentCount = documentCount;
    }

    /// <summary>
    /// Number of documents included in the statistics.
    /// </summary>
    public int DocumentCount => documentCount;

    /// <summary>
    /// Returns document frequency for one token id.
    /// </summary>
    /// <param name="tokenId">The token id to inspect.</param>
    /// <returns>The number of distinct documents containing the token.</returns>
    public int GetDocumentFrequency(int tokenId)
        => (uint)tokenId < (uint)documentFrequencies.Length
            ? documentFrequencies[tokenId]
            : 0;

    /// <summary>
    /// Returns smoothed inverse document frequency for one token id.
    /// </summary>
    /// <param name="tokenId">The token id to inspect.</param>
    /// <returns>The smoothed inverse document frequency weight.</returns>
    public float GetInverseDocumentFrequency(int tokenId)
    {
        var df = GetDocumentFrequency(tokenId);
        if (df <= 0)
            return 0f;

        return MathF.Log((1f + documentCount) / (1f + df)) + 1f;
    }

    /// <summary>
    /// Builds token statistics from documents whose token spans are already distinct per document.
    /// </summary>
    /// <param name="documents">Document token spans with no duplicate token ids within a document.</param>
    /// <param name="tokenTable">The shared token table containing the document spans.</param>
    /// <param name="tokenCount">The size of the token-id vocabulary.</param>
    /// <returns>Document-frequency statistics for the supplied documents.</returns>
    public static TokenStatistics BuildFromDistinctDocuments(ReadOnlySpan<TokenSpan> documents, TokenTable tokenTable, int tokenCount)
    {
        ArgumentNullException.ThrowIfNull(tokenTable);

        if (tokenCount == 0 || documents.IsEmpty)
            return new([], 0);

        var documentFrequencies = new int[tokenCount];
        for (var i = 0; i < documents.Length; i++)
        {
            foreach (var tokenId in tokenTable.GetSpan(documents[i]))
            {
                if ((uint)tokenId < (uint)documentFrequencies.Length)
                    documentFrequencies[tokenId]++;
            }
        }

        return new(documentFrequencies, documents.Length);
    }
}

/// <summary>
/// Weighted scoring helpers over sorted unique token-id sets.
/// </summary>
public static class WeightedTokenScoring
{
    /// <summary>
    /// Sums inverse document frequency weights over the intersection of two sorted token sets.
    /// </summary>
    /// <param name="left">The first sorted unique token set.</param>
    /// <param name="right">The second sorted unique token set.</param>
    /// <param name="statistics">The token statistics used to weight shared tokens.</param>
    /// <returns>The weighted sum across the shared token ids.</returns>
    public static float WeightedIntersectionSum(ReadOnlySpan<int> left, ReadOnlySpan<int> right, TokenStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        var i = 0;
        var j = 0;
        var sum = 0f;

        while (i < left.Length && j < right.Length)
        {
            var a = left[i];
            var b = right[j];

            if (a == b)
            {
                sum += statistics.GetInverseDocumentFrequency(a);
                i++;
                j++;
            }
            else if (a < b)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return sum;
    }
}
