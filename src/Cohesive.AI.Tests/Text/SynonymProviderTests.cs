using Cohesive.AI.Text;

namespace Cohesive.AI.Tests.Text;

public sealed class SynonymProviderTests
{
    static readonly ISynonymProvider Provider = new SampleSynonymProvider(
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ship"] = ["vessel", "boat", "freighter"],
            ["eta"] = ["estimated", "arrival", "time"]
        });

    [Fact]
    public void Expand_KnownTerm_WritesExpectedSynonyms()
    {
        var (written, synonyms) = ExpandToStrings(Provider, "ship", capacity: 4);

        Assert.Equal(3, written);
        Assert.Equal(["vessel", "boat", "freighter"], synonyms);
    }

    [Fact]
    public void Expand_UnknownTerm_ReturnsZero()
    {
        var (written, synonyms) = ExpandToStrings(Provider, "tracking", capacity: 4);

        Assert.Equal(0, written);
        Assert.Empty(synonyms);
    }

    [Fact]
    public void Expand_BufferSmallerThanSynonymSet_TruncatesToCapacity()
    {
        var (written, synonyms) = ExpandToStrings(Provider, "eta", capacity: 2);

        Assert.Equal(2, written);
        Assert.Equal(["estimated", "arrival"], synonyms);
    }

    [Fact]
    public void Expand_CaseInsensitiveLookup_ReturnsSynonyms()
    {
        var (written, synonyms) = ExpandToStrings(Provider, "ETA", capacity: 4);

        Assert.Equal(3, written);
        Assert.Equal(["estimated", "arrival", "time"], synonyms);
    }

    static (int Written, string[] Synonyms) ExpandToStrings(ISynonymProvider provider, string term, int capacity)
    {
        ReadOnlyMemory<char>[] output = new ReadOnlyMemory<char>[capacity];
        var written = provider.Expand(term.AsSpan(), output);

        var synonyms = new string[written];
        for (var i = 0; i < written; i++)
            synonyms[i] = output[i].ToString();

        return (written, synonyms);
    }

    sealed class SampleSynonymProvider(IReadOnlyDictionary<string, string[]> synonyms) : ISynonymProvider
    {
        public int Expand(ReadOnlySpan<char> term, Span<ReadOnlyMemory<char>> output)
        {
            if (term.IsEmpty || output.IsEmpty)
                return 0;

            if (!synonyms.TryGetValue(term.ToString(), out var entries))
                return 0;

            var written = Math.Min(output.Length, entries.Length);
            for (var i = 0; i < written; i++)
                output[i] = entries[i].AsMemory();

            return written;
        }
    }
}
