namespace Cohesive.AI.Text;

/// <summary>
/// Expands input terms into synonymous alternatives.
/// </summary>
public interface ISynonymProvider
{
    /// <summary>
    /// Writes synonym expansions for a term into the caller-provided output buffer.
    /// </summary>
    /// <param name="term">Input term to expand.</param>
    /// <param name="output">Destination span for synonym terms.</param>
    /// <returns>The number of synonym entries written to <paramref name="output"/>.</returns>
    int Expand(ReadOnlySpan<char> term, Span<ReadOnlyMemory<char>> output);
}
