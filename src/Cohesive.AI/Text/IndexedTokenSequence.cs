using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Cohesive.AI.Text;

/// <summary>
/// One ordered token-id sequence backed by a shared token table and vocabulary.
/// </summary>
public readonly record struct IndexedTokenSequence
{
    readonly TokenTable? table;
    readonly TokenVocabulary? vocabulary;

    /// <summary>
    /// Creates an indexed token sequence.
    /// </summary>
    public IndexedTokenSequence(TokenSpan span, TokenTable table, TokenVocabulary vocabulary)
    {
        this.table = Guard.RequireNotNull(table);
        this.vocabulary = Guard.RequireNotNull(vocabulary);
        Span = span;
    }

    /// <summary>
    /// Compact location inside the shared token table.
    /// </summary>
    public TokenSpan Span { get; }

    /// <summary>
    /// Number of token ids in the sequence.
    /// </summary>
    public int Length => Span.Length;

    /// <summary>
    /// Indicates whether the sequence is empty.
    /// </summary>
    public bool IsEmpty => Span.IsEmpty || table is null || vocabulary is null;

    /// <summary>
    /// Shared vocabulary used by this sequence.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public TokenVocabulary Vocabulary => vocabulary ?? throw new InvalidOperationException("The token sequence is empty.");

    /// <summary>
    /// Ordered token ids for this sequence.
    /// </summary>
    public ReadOnlySpan<int> TokenIds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => table is null ? [] : table.GetSpan(Span);
    }

    /// <summary>
    /// Decodes the sequence into normalized token text.
    /// </summary>
    public ImmutableArray<string> ToTokenStrings() =>
        vocabulary?.Decode(TokenIds) ?? [];

    /// <summary>
    /// Shared vocabulary used by this sequence when available.
    /// </summary>
    public TokenVocabulary? VocabularyOrNull => vocabulary;
}