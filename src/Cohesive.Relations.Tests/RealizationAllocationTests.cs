using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;
using Xunit.Abstractions;

namespace Cohesive.Relations.Tests;

public sealed class RealizationAllocationTests(ITestOutputHelper output)
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
    public void NestedKeyBuilderWritesExactlyTheNestedSequenceKeyText()
    {
        string?[] values = [null, "", "é🙂", "-1:", "3:abc", new('y', 700)];
        long?[] numbers = [null, 0, -5, 42, long.MaxValue];
        int[] ordinals = [0, 7, 12345, -3];
        var expected = PreviousKey(
        [
            PreviousKey(values),
            PreviousKey(numbers.Select(static n => n?.ToString(CultureInfo.InvariantCulture))),
            PreviousKey(ordinals.Select(static o => o.ToString("D4", CultureInfo.InvariantCulture))),
            PreviousKey([PreviousKey(values), PreviousKey([])]),
        ]);

        var key = new SequenceKeyBuilder(stackalloc char[16]);
        var part = new SequenceKeyBuilder(stackalloc char[16]);
        var inner = new SequenceKeyBuilder(stackalloc char[16]);
        try
        {
            foreach (var value in values) part.Add(value);
            key.AddNested(ref part);
            foreach (var number in numbers) part.Add(number);
            key.AddNested(ref part);
            foreach (var ordinal in ordinals) part.AddOrdinal(ordinal);
            key.AddNested(ref part);
            foreach (var value in values) inner.Add(value);
            part.AddNested(ref inner);
            part.AddNested(ref inner); // A cleared builder contributes the empty sequence.
            key.AddNested(ref part);
            Assert.Equal(expected, key.ToString());
        }
        finally
        {
            inner.Dispose();
            part.Dispose();
            key.Dispose();
        }
    }

    [Fact]
    public void WarmNestedKeyAllocatesOnlyItsResult()
    {
        var values = Enumerable.Range(0, 64).Select(i => new string('x', 64) + i).ToArray();
        string Build()
        {
            var key = new SequenceKeyBuilder(stackalloc char[256]);
            var part = new SequenceKeyBuilder(stackalloc char[256]);
            try
            {
                foreach (var value in values)
                {
                    part.Add(value);
                    part.AddOrdinal(value.Length);
                    key.AddNested(ref part);
                }
                return key.ToString();
            }
            finally
            {
                part.Dispose();
                key.Dispose();
            }
        }
        for (var i = 0; i < 4; i++) Build();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var actual = Build();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(PreviousKey(values.Select(static value =>
            PreviousKey([value, value.Length.ToString("D4", CultureInfo.InvariantCulture)]))), actual);
        Assert.True(allocated <= actual.Length * 2L + 256, $"Allocated {allocated} for {actual.Length} characters.");
    }

    [Fact]
    public void ProfileAnalysisIsSharedPerImmutableProfileInstance()
    {
        RelationQueryTargetCapabilityProfile Create() => new(new("target/test"), new("target/test/v1"),
            ["schema/v1"], ["compiler/test"],
            [new(new("evidence/filter"), new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter))]);
        var profile = Create();
        var first = RelationQueryTargetCapabilityProfileAnalysis.Analyze(profile);
        Assert.Same(first, RelationQueryTargetCapabilityProfileAnalysis.Analyze(profile));
        // Another instance, even with equal content, is analyzed independently.
        var other = RelationQueryTargetCapabilityProfileAnalysis.Analyze(Create());
        Assert.NotSame(first, other);
        Assert.Equal(first.Evidence.Keys.Order(), other.Evidence.Keys.Order());
        Assert.Empty(first.Issues);
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

    [Fact]
    public void WarmNativeRealizationHasBoundedProofPreparationAllocations()
    {
        var plan = new RelationQueryCompiledPlanReference("compiler/test", "schema/v1",
            new("sha256", "definition/v1", "definition"), new("sha256", "shapes/v1", "shapes"), null,
            new("sha256", "demand/v1", "demand"), [new("input/test")]);
        var requirements = Enumerable.Range(0, 64).Select(i =>
            new RelationQueryRealizationRequirement(new($"requirement/{i:D4}"),
                new ExpressionRelationQueryCapability(new($"expression/{i:D4}"), ExprCapabilityRequirementKind.Operation))).ToImmutableArray();
        var profile = new RelationQueryTargetCapabilityProfile(new("target/test"), new("target/test/v1"),
            [plan.DefinitionSchemaVersion], [plan.CompilerProfile],
            requirements.SelectMany((requirement, i) => new[]
            {
                new RelationQueryTargetCapabilityEvidence(new($"evidence/{i:D4}/a"), requirement.Capability),
                new RelationQueryTargetCapabilityEvidence(new($"evidence/{i:D4}/b"), requirement.Capability)
            }).ToImmutableArray());
        var policy = new RelationQueryRealizationPolicy(new("policy/test"), "conventions/v1");
        var expected = RelationQueryRealizationCompiler.Match(plan, requirements, profile, policy);
        Assert.True(expected.IsRealizable);
        Assert.Equal(64, expected.Decisions.Length);
        Assert.All(expected.Decisions, decision => Assert.Equal(2,
            Assert.IsType<NativeRelationQueryRealizationDecision>(decision).CapabilityEvidence.Length));
        for (var i = 0; i < 4; i++) RelationQueryRealizationCompiler.Match(plan, requirements, profile, policy);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var actual = RelationQueryRealizationCompiler.Match(plan, requirements, profile, policy);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        }
        Array.Sort(samples);
        var median = samples[samples.Length / 2];
        output.WriteLine($"Warm 64-requirement realization: {median} bytes (median of seven).");
        Assert.True(median < 1_150_000, $"Realization allocated {median} bytes.");
    }

    [Fact]
    public void RepeatedGuaranteeRequirementsHaveBoundedPreparationAllocations()
    {
        var plan = new RelationQueryCompiledPlanReference("compiler/test", "schema/v1",
            new("sha256", "definition/v1", "definition"), new("sha256", "shapes/v1", "shapes"), null,
            new("sha256", "demand/v1", "demand"), [new("input/test")]);
        var capability = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Projection);
        var guarantees = new[] { RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
            RelationQueryGuaranteeCapabilityKind.Cardinality, RelationQueryGuaranteeCapabilityKind.OutputIdentity };
        var requirements = Enumerable.Range(0, 128).Select(i => new RelationQueryRealizationRequirement(
            new($"requirement/{i:D4}"), capability, requiredGuarantees: [.. guarantees])).ToImmutableArray();
        var profile = new RelationQueryTargetCapabilityProfile(new("target/test"), new("target/test/v1"),
            [plan.DefinitionSchemaVersion], [plan.CompilerProfile],
            [new(new("evidence/projection"), capability),
                .. guarantees.Select(g => new RelationQueryTargetCapabilityEvidence(
                    new($"evidence/guarantee/{g}"), new GuaranteeRelationQueryCapability(g)))]);
        var policy = new RelationQueryRealizationPolicy(new("policy/test"), "conventions/v1");
        var expected = RelationQueryRealizationCompiler.Match(plan, requirements, profile, policy);
        Assert.True(expected.IsRealizable);
        Assert.Equal(128, expected.Decisions.Length);
        // Recorded from the published alpha.157 implementation before preparation reuse.
        Assert.Equal("b762484114319ff2d72a1d286b9a8eca9996dfb71a53d78760b3d107778d92f5",
            expected.Fingerprint.Value);
        Assert.All(expected.Decisions, d => Assert.Equal(4,
            Assert.IsType<NativeRelationQueryRealizationDecision>(d).CapabilityEvidence.Length));
        for (var i = 0; i < 4; i++) RelationQueryRealizationCompiler.Match(plan, requirements, profile, policy);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var actual = RelationQueryRealizationCompiler.Match(plan, requirements, profile, policy);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        }
        Array.Sort(samples);
        output.WriteLine($"128 repeated guarantee requirements: {samples[3]} bytes (median of seven).");
        Assert.True(samples[3] < 1_400_000, $"Realization allocated {samples[3]} bytes.");
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
