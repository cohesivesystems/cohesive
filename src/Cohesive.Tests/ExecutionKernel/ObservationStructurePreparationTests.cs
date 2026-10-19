using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ObservationStructurePreparationTests
{
    static readonly ValueContract Contract = new(new JsonTypeRef());
    static PortableValue Wrap(ObservationValue value) => PortableValue.Concrete(Contract, value);
    static ObservationValue Object(int fields, ObservationValue value) => ObservationValue.FromObject(
        Enumerable.Range(0, fields).ToDictionary(i => $"f{i}", _ => value));

    [Theory]
    [InlineData("flat", 128)]
    [InlineData("nested", 128)]
    [InlineData("collection", 128)]
    [InlineData("nested", 1024)]
    [InlineData("deep", 128)]
    public void RepeatedStructuralValidationHasConstantAllocation(string shape, int fields)
    {
        var leaf = ObservationValue.FromString("payload");
        if (shape != "flat") leaf = Object(1, leaf);
        if (shape == "collection") leaf = ObservationValue.FromArray([leaf, leaf]);
        if (shape == "deep")
            for (var i = 0; i < 128; i++) leaf = Object(1, leaf);
        var value = Wrap(Object(fields, leaf));
        for (var i = 0; i < 20; i++) Assert.True(PortableExecutionValidator.Validate(value).IsValid);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = PortableExecutionValidator.Validate(value);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(result.IsValid);
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 1024);
    }

    [Fact]
    public void PreparedStorageDoesNotAdmitChangedValuesOrContracts()
    {
        var source = Enumerable.Range(0, 128).ToDictionary(i => $"f{i}", _ => ObservationValue.FromString("valid"));
        var observation = ObservationValue.FromObject(source);
        Assert.True(PortableExecutionValidator.Validate(Wrap(observation)).IsValid);
        source["f0"] = ObservationValue.FromDouble(double.NaN);
        Assert.True(PortableExecutionValidator.Validate(Wrap(observation)).IsValid);
        var changed = ObservationValue.FromObject(source);
        Assert.Contains(PortableExecutionValidator.Validate(Wrap(changed)).Diagnostics,
            d => d.Code == PortableExecutionDiagnosticCodes.NonFiniteNumber);
        var incompatible = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Int64));
        Assert.Contains(PortableExecutionValidator.Validate(PortableValue.Concrete(incompatible, observation)).Diagnostics,
            d => d.Code == PortableExecutionDiagnosticCodes.ConcreteTypeMismatch);
        var unresolved = new ValueContract(new NamedTypeRef(new("not-present")));
        Assert.False(PortableExecutionValidator.Validate(PortableValue.Concrete(unresolved, observation)).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProjectionRequirementsSurviveOrdinaryValidationAndReparenting(int payload)
    {
        var leaf = payload switch
        {
            0 => ObservationValue.FromString("normal"),
            1 => ObservationValue.FromString("\uD800"),
            _ => ObservationValue.FromBytes(new byte[] { 1, 2 })
        };
        var observation = Object(128, leaf);
        Assert.True(PortableExecutionValidator.Validate(Wrap(observation)).IsValid);
        Assert.True(PortableExecutionValidator.ValidateForClrProjection(Wrap(observation), 1, out var original).IsValid);
        Assert.Equal(payload != 0, original);
        var parent = ObservationValue.FromArray([observation]);
        Assert.True(PortableExecutionValidator.ValidateForClrProjection(Wrap(parent), 1, out var nested).IsValid);
        Assert.True(nested);
        Assert.True(PortableExecutionValidator.ValidateForClrProjection(Wrap(parent), 2, out var deeper).IsValid);
        Assert.Equal(payload != 0, deeper);
        PortableExecutionValidator.ValidateForClrProjection(Wrap(parent), 0, out var disabled);
        Assert.False(disabled);
    }

    [Fact]
    public void PreparedInvalidUnicodePropertyNameStillRequiresNormalization()
    {
        var source = Enumerable.Range(0, 128).ToDictionary(i => $"f{i}", _ => ObservationValue.Null);
        source["bad\uD800"] = ObservationValue.Null;
        var input = Wrap(ObservationValue.FromObject(source));
        Assert.True(PortableExecutionValidator.Validate(input).IsValid);
        PortableExecutionValidator.ValidateForClrProjection(input, 64, out var required);
        Assert.True(required);
    }

    [Fact]
    public void InvalidSubtreesKeepTheirDiagnosticsAndLocationsOnEveryVisit()
    {
        var good = Object(128, ObservationValue.Null);
        Assert.True(PortableExecutionValidator.Validate(Wrap(good)).IsValid);
        var bad = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["z"] = ObservationValue.Undefined,
            ["a"] = ObservationValue.FromDouble(double.PositiveInfinity),
            ["shared"] = good,
            ["temporal"] = new(ObservationValueKind.DateOnly, s: "invalid"),
            ["nullString"] = new(ObservationValueKind.String)
        });
        var first = PortableExecutionValidator.Validate(Wrap(bad));
        Assert.Equal(4, first.Diagnostics.Length);
        for (var i = 0; i < 3; i++)
            Assert.Equal(first.Diagnostics.Select(d => (d.Code, d.Location, d.Message)),
                PortableExecutionValidator.Validate(Wrap(bad)).Diagnostics.Select(d => (d.Code, d.Location, d.Message)));
    }

    [Fact]
    public void ArraySnapshotsAndImmutablePatchesRequireFreshEvidence()
    {
        var items = Enumerable.Repeat(ObservationValue.Null, 128).ToArray();
        var array = ObservationValue.FromArray(items);
        Assert.True(PortableExecutionValidator.Validate(Wrap(array)).IsValid);
        items[0] = ObservationValue.FromDouble(double.NaN);
        Assert.True(PortableExecutionValidator.Validate(Wrap(array)).IsValid);
        Assert.False(PortableExecutionValidator.Validate(Wrap(ObservationValue.FromArray(items))).IsValid);
        var original = Object(128, ObservationValue.Null);
        Assert.True(PortableExecutionValidator.Validate(Wrap(original)).IsValid);
        var patch = original.WithField(FieldPath.FromField("f0"), ObservationValue.FromDouble(double.NaN));
        Assert.False(PortableExecutionValidator.Validate(Wrap(patch)).IsValid);
        Assert.True(PortableExecutionValidator.Validate(Wrap(original)).IsValid);
    }

    [Fact]
    public void ADeepSiblingDoesNotContaminateAnotherSubtreesDepthEvidence()
    {
        var shallow = Object(128, ObservationValue.Null);
        var deep = Object(1, Object(1, Object(128, ObservationValue.Null)));
        var siblings = ObservationValue.FromArray([deep, shallow]);
        Assert.True(PortableExecutionValidator.Validate(Wrap(siblings)).IsValid);
        PortableExecutionValidator.ValidateForClrProjection(Wrap(shallow), 1, out var shallowRequirement);
        Assert.False(shallowRequirement);
        PortableExecutionValidator.ValidateForClrProjection(Wrap(deep), 2, out var deepRequirement);
        Assert.True(deepRequirement);
    }

    [Fact]
    public async Task ConcurrentFirstChecksPublishOnlyCompleteEvidence()
    {
        var input = Wrap(Object(1024, Object(1, ObservationValue.FromString("valid"))));
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => PortableExecutionValidator.Validate(input))));
        Assert.All(results, result => Assert.True(result.IsValid));
        PortableExecutionValidator.ValidateForClrProjection(input, 1, out var required);
        Assert.True(required);
    }

    [Fact]
    public void PreparationDoesNotRetainObservationStorage()
    {
        var weak = Prepare();
        for (var i = 0; i < 8 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference Prepare()
    {
        var value = Object(128, ObservationValue.Null);
        Assert.True(PortableExecutionValidator.Validate(Wrap(value)).IsValid);
        return new(value.Fields!);
    }
}
