using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ValidationCursorAllocationTests
{
    [Fact]
    public void Warm_scalar_validation_reuses_its_owner()
    {
        var value = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.String)), ObservationValue.FromString("value"));
        for (var index = 0; index < 100; index++) Assert.True(PortableExecutionValidator.Validate(value).IsValid);
#if !DEBUG // Debug deliberately snapshots location prefixes to detect stale-cursor misuse.
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) PortableExecutionValidator.Validate(value);
        Assert.InRange((GC.GetAllocatedBytesForCurrentThread() - start) / 100, 0, 8);
#endif
    }

    [Fact]
    public void Reuse_does_not_retain_graphs_or_leak_projection_policy()
    {
        var graphReference = ValidateGraph();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(graphReference.IsAlive);

        var bytes = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.Bytes)), ObservationValue.FromBytes(new byte[] { 1 }));
        var scalar = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.Int64)), ObservationValue.FromInt64(1));
        for (var index = 0; index < 16; index++)
        {
            Assert.True(PortableExecutionValidator.ValidateForClrProjection(bytes, 8, out var normalize).IsValid);
            Assert.True(normalize);
            Assert.True(PortableExecutionValidator.ValidateForClrProjection(bytes, 0, out normalize).IsValid);
            Assert.False(normalize);
            Assert.True(PortableExecutionValidator.ValidateForClrProjection(scalar, 8, out normalize).IsValid);
            Assert.False(normalize);
            Assert.True(PortableExecutionValidator.Validate(scalar).IsValid);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference ValidateGraph()
    {
        TypeId id = new("context-owner");
        var graph = new ShapeGraph(new("context-graph"), [], [new TypeDefinition.Structural(id, [])]);
        var type = new NamedTypeRef(id);
        Assert.True(PortableExecutionValidator.Validate(type, graph).IsValid);
        Assert.False(PortableExecutionValidator.Validate(type).IsValid);
        Assert.True(PortableExecutionValidator.Validate(type, graph).IsValid);
        return new WeakReference(graph);
    }

    [Fact]
    public void Nested_leases_are_exclusive_and_exception_cleanup_preserves_results()
    {
        // Canonical validation has no arbitrary callbacks, so exercise nested lease ownership directly.
        var bad = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.String)), ObservationValue.FromInt64(42));
        var good = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.String)), ObservationValue.FromString("ok"));
        using var outer = PortableExecutionValidator.ValidationContext.Rent(graph: null);
        outer.ValidatePortableValue(bad, outer.Root);
        var retained = outer.ToResult();
        Assert.False(retained.IsValid);
        var serialized = System.Text.Json.JsonSerializer.Serialize(retained);
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var inner = PortableExecutionValidator.ValidationContext.Rent(graph: null);
            Assert.NotSame(outer, inner);
            inner.ValidatePortableValue(good, inner.Root);
            Assert.True(inner.ToResult().IsValid);
            throw new InvalidOperationException("exercise lease unwinding");
        }));
        Assert.True(PortableExecutionValidator.Validate(good).IsValid);
        Assert.Equal(serialized, System.Text.Json.JsonSerializer.Serialize(outer.ToResult()));
        Assert.Equal(serialized, System.Text.Json.JsonSerializer.Serialize(retained));
    }

#if DEBUG
    [Fact]
    public void Locations_expire_on_disposal_and_do_not_revive_when_the_owner_is_reused()
    {
        var owner = PortableExecutionValidator.ValidationContext.Rent(graph: null);
        var root = owner.Root;
        var child = root.Append(new("old", 0));
        Assert.True(root.IsCurrentLease);
        Assert.True(child.IsCurrentLease);
        owner.Dispose();
        Assert.False(root.IsCurrentLease);
        Assert.False(child.IsCurrentLease);
        using var next = PortableExecutionValidator.ValidationContext.Rent(graph: null);
        Assert.Same(owner, next);
        var replacement = next.Root.Append(new("new", 0));
        Assert.True(replacement.IsCurrentLease);
        Assert.False(root.IsCurrentLease);
        Assert.False(child.IsCurrentLease);
        Assert.Equal("/new", replacement.ToString());
    }
#endif

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void Diagnostics_own_their_paths_after_pool_reuse_and_deep_growth(int depth)
    {
        TypeRef type = new ScalarTypeRef(ScalarTypeKind.String);
        var invalid = ObservationValue.FromInt64(42);
        var valid = ObservationValue.FromString("ok");
        for (var index = 0; index < depth; index++)
        {
            type = new ObjectTypeRef([new("a/~", type)]);
            invalid = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["a/~"] = invalid });
            valid = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["a/~"] = valid });
        }
        var bad = PortableValue.Concrete(new(type), invalid);
        var good = PortableValue.Concrete(new(type), valid);
        var first = PortableExecutionValidator.Validate(bad);
        Assert.False(first.IsValid);
        var locations = first.Diagnostics.Select(item => item.Location).ToArray();
        Parallel.For(0, 32, _ =>
        {
            Assert.True(PortableExecutionValidator.Validate(good).IsValid);
            Assert.Equivalent(first, PortableExecutionValidator.Validate(bad), strict: true);
        });
        Assert.Equal(locations, first.Diagnostics.Select(item => item.Location));
    }
}
