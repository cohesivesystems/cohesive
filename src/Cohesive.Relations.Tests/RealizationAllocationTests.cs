using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RealizationAllocationTests
{
    [Fact]
    public void SequenceKeysRetainLengthPrefixesAndEnumerateOnce()
    {
        string?[] values = [null, "", "é🙂", "-1:", "3:abc", "\u001e\u001f", new('x', 4096)];
        var expected = PreviousKey(values);
        var source = new SinglePass(values);
        Assert.Equal(expected, RelationQueryRealizationOrdering.SequenceKey(source));
        Assert.Equal(1, source.Enumerations);
    }

    [Fact]
    public void WarmLargeSequenceKeyAllocatesOnlyItsResultAndEnumerator()
    {
        var values = Enumerable.Range(0, 128).Select(i => new string('x', 128) + i).ToArray();
        var expected = PreviousKey(values);
        for (var i = 0; i < 4; i++) RelationQueryRealizationOrdering.SequenceKey(values);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var actual = RelationQueryRealizationOrdering.SequenceKey(values);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        Assert.True(allocated <= expected.Length * 2L + 256, $"Allocated {allocated} for {expected.Length} characters.");
    }

    [Fact]
    public void WarmFingerprintDoesNotRetainACompleteCanonicalByteBuffer()
    {
        var profileName = "compiler/é🙂/" + new string('x', 128 * 1024);
        var plan = new RelationQueryCompiledPlanReference(profileName, "schema/v1",
            new("sha256", "definition/v1", "definition"), new("sha256", "shapes/v1", "shapes"), null,
            new("sha256", "demand/v1", "demand"), [new("input/test")]);
        ImmutableArray<RelationQueryRealizationRequirement> requirements =
            [new(new("requirement/filter"), new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter))];
        var profile = new RelationQueryTargetCapabilityProfile(new("target/test"), new("target/test/v1"),
            [plan.DefinitionSchemaVersion], [profileName]);
        var report = RelationQueryRealizationCompiler.Match(plan, requirements, profile,
            new RelationQueryRealizationPolicy(new("policy/test"), "conventions/v1"));
        var expected = report.Fingerprint;
        for (var i = 0; i < 4; i++) RelationQueryRealizationFingerprinter.Compute(report);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var actual = RelationQueryRealizationFingerprinter.Compute(report);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        Assert.True(allocated < 16 * 1024, $"Fingerprint allocated {allocated} bytes for a large scalar.");
    }

    static string PreviousKey(IEnumerable<string?> values)
    {
        StringBuilder key = new();
        foreach (var value in values)
        {
            if (value is null) key.Append("-1:");
            else key.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }
        return key.ToString();
    }

    sealed class SinglePass(string?[] values) : IEnumerable<string?>
    {
        public int Enumerations { get; private set; }
        public IEnumerator<string?> GetEnumerator()
        {
            if (++Enumerations > 1) throw new InvalidOperationException("Source enumerated twice.");
            return ((IEnumerable<string?>)values).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
