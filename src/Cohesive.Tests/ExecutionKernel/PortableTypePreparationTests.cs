using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class PortableTypePreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmSchemaValidationHasConstantAllocation(bool nested)
    {
        var leaf = new EnumTypeRef("Choice", ["yes", "no"]);
        TypeRef field = nested ? new ArrayTypeRef(new ObjectTypeRef([new("choice", leaf)])) : leaf;
        var type = new ObjectTypeRef([.. Enumerable.Range(0, 128).Select(i => new ObjectFieldTypeDef($"f{i}", field))]);
        var contract = new ValueContract(type);
        for (var i = 0; i < 20; i++) Assert.True(PortableExecutionValidator.Validate(contract).IsValid);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = PortableExecutionValidator.Validate(contract);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(result.IsValid);
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 1_024);
    }

    [Fact]
    public void PreparedSchemaStillChecksEveryValueAndContractModifier()
    {
        var type = new ObjectTypeRef([new("choice", new EnumTypeRef("Choice", ["yes", "no"]))]);
        var contract = new ValueContract(type);
        var valid = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["choice"] = ObservationValue.FromString("yes") });
        Assert.True(PortableExecutionValidator.Validate(PortableValue.Concrete(contract, valid)).IsValid);
        var invalid = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["choice"] = ObservationValue.FromString("other") });
        Assert.Contains(PortableExecutionValidator.Validate(PortableValue.Concrete(contract, invalid)).Diagnostics,
            d => d.Code == PortableExecutionDiagnosticCodes.ConcreteTypeMismatch);
        Assert.False(PortableExecutionValidator.Validate(PortableValue.Null(contract)).IsValid);
        Assert.False(PortableExecutionValidator.Validate(PortableValue.Missing(contract)).IsValid);
        Assert.True(PortableExecutionValidator.Validate(PortableValue.Concrete(contract, valid)).IsValid);
        var nullable = new ValueContract(type, nullability: FieldNullability.Nullable);
        Assert.True(PortableExecutionValidator.Validate(PortableValue.Null(nullable)).IsValid);
    }

    [Fact]
    public void NamedReferencesKeepAllAncestorsContextual()
    {
        TypeId id = new("external");
        var type = new ObjectTypeRef([new("nested", new ArrayTypeRef(new ObjectTypeRef([new("value", new NamedTypeRef(id))])))]);
        var good = new ShapeGraph(new("good"), [], [new TypeDefinition.Structural(id, [])]);
        var bad = new ShapeGraph(new("bad"), [], [new TypeDefinition.Structural(id,
            [new(new("opaque"), new OpaqueRuntimeTypeRef("runtime"))])]);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(PortableExecutionValidator.Validate(type, good).IsValid);
            Assert.Contains(PortableExecutionValidator.Validate(type).Diagnostics, d => d.Code == PortableExecutionDiagnosticCodes.UnresolvedType);
            Assert.Contains(PortableExecutionValidator.Validate(type, bad).Diagnostics, d => d.Code == PortableExecutionDiagnosticCodes.OpaqueRuntimeType);
        }
    }

    [Fact]
    public void ShapeResolutionStaysFreshAfterInlineTypePreparation()
    {
        var shape = new Shape(new("s"), []);
        var graph = new ShapeGraph(new("g"), [shape]);
        var contract = new ValueContract(new ObjectTypeRef([]), shape: new(graph.Id, shape.Id));
        Assert.True(PortableExecutionValidator.Validate(contract, graph).IsValid);
        Assert.Contains(PortableExecutionValidator.Validate(contract).Diagnostics, d => d.Code == PortableExecutionDiagnosticCodes.UnresolvedShape);
        Assert.False(PortableExecutionValidator.Validate(contract, new ShapeGraph(new("g"), [])).IsValid);
        Assert.True(PortableExecutionValidator.Validate(contract, graph).IsValid);
    }

    [Fact]
    public void InvalidTypesKeepDiagnosticsAndNewCopiesHaveIndependentEvidence()
    {
        var valid = new ObjectTypeRef([new("value", new ScalarTypeRef(ScalarTypeKind.Bool))]);
        Assert.True(PortableExecutionValidator.Validate(valid).IsValid);
        var invalid = valid with { Fields = [valid.Fields[0], valid.Fields[0], new("unsupported", new UnsupportedType())] };
        var first = PortableExecutionValidator.Validate(invalid).Diagnostics;
        Assert.Equal(2, first.Length);
        Assert.Equal(first.ToArray(), PortableExecutionValidator.Validate(invalid).Diagnostics.ToArray());
        Assert.Equal("/fields/1/name", first[0].Location);
        Assert.Equal("/fields/2/type", first[1].Location);
        var contract = new ValueContract(new ArrayTypeRef(invalid));
        var nested = PortableExecutionValidator.Validate(contract).Diagnostics;
        Assert.Equal(nested.ToArray(), PortableExecutionValidator.Validate(contract).Diagnostics.ToArray());
        Assert.Equal("/type/elementType/fields/1/name", nested[0].Location);
        Assert.True(PortableExecutionValidator.Validate(valid).IsValid);
        var malformed = valid with { Fields = [valid.Fields[0] with { Cardinality = (FieldCardinality)999 }] };
        Assert.False(PortableExecutionValidator.Validate(malformed).IsValid);
        Assert.False(PortableExecutionValidator.Validate(malformed).IsValid);
        Assert.False(PortableExecutionValidator.Validate(new EnumTypeRef("Choice", ["same", "same"])).IsValid);
    }

    [Fact]
    public void ConcurrentFirstValidationPublishesOnlyValidEvidence()
    {
        var type = new ObjectTypeRef([.. Enumerable.Range(0, 128).Select(i => new ObjectFieldTypeDef($"f{i}", new EnumTypeRef("Choice", ["a", "b"])))]);
        Parallel.For(0, 128, _ => Assert.True(PortableExecutionValidator.Validate(type).IsValid));
        Assert.True(PortableExecutionValidator.Validate(type).IsValid);
    }

    [Fact]
    public void SuccessfulPreparationDoesNotKeepTypeAlive()
    {
        var reference = PrepareUnrootedType();
        for (var i = 0; i < 5 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(reference.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference PrepareUnrootedType()
    {
        var type = new ObjectTypeRef([new("value", new EnumTypeRef("Choice", ["a", "b"]))]);
        Assert.True(PortableExecutionValidator.Validate(type).IsValid);
        return new(type);
    }

    sealed record UnsupportedType : TypeRef;
}
