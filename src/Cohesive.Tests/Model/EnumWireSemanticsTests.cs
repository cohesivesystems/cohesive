using Cohesive.Model;
using Cohesive.Model.Serialization;
using System.Text.Json.Serialization;

namespace Cohesive.Tests.Model;

public sealed class EnumWireSemanticsTests
{
    [Fact]
    public void Wire_equivalence_is_structural_but_document_identity_is_not()
    {
        var lifecycle = new EnumTypeRef("Lifecycle", ["ready", "done"]);
        var phase = new EnumTypeRef("Phase", ["done", "ready"]);
        Assert.True(lifecycle.IsWireEquivalentTo(phase));
        Assert.NotEqual(lifecycle, phase);
        Assert.False(lifecycle.IsWireEquivalentTo(new EnumTypeRef("Lifecycle", ["ready", "DONE"])));
        Assert.False(lifecycle.IsWireEquivalentTo(new EnumTypeRef("Lifecycle", ["ready"])));
        Assert.True(lifecycle.IsWireEquivalentTo(new EnumTypeRef("Phase", ["ready", "done", "done"])));
    }

    [Fact]
    public void Named_enum_uses_serialized_values_and_requires_string_underlying_type()
    {
        var inline = new EnumTypeRef("Lifecycle", ["ready", "done"]);
        var named = new TypeDefinition.Enum(new("phase"), PrimitiveType.String, [new("Ready", "ready"), new("done")]);
        Assert.True(inline.IsWireEquivalentTo(named));
        Assert.False(inline.IsWireEquivalentTo(named with { Underlying = PrimitiveType.Int32 }));
        Assert.Equal("done", named.Values[1].SerializedValue);
    }

    [Fact]
    public void Warm_equivalence_does_not_allocate_sets()
    {
        var left = new EnumTypeRef("Lifecycle", ["ready", "done"]);
        var right = new EnumTypeRef("Phase", ["done", "ready"]);
        for (var i = 0; i < 100; i++) _ = left.IsWireEquivalentTo(right);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var equal = true;
        for (var i = 0; i < 1000; i++) equal &= left.IsWireEquivalentTo(right);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(equal);
        Assert.Equal(0, allocated);
    }

    [JsonConverter(typeof(JsonStringEnumConverter<Code>))]
    public enum Code { [JsonStringEnumMemberName("not-ready")] NotReady }

    [Fact]
    public void Typed_wire_name_uses_shared_catalog_and_rejects_undeclared_values()
    {
        Assert.Equal("not-ready", SerializedEnum<Code>.GetWireName(Code.NotReady));
        Assert.Throws<ArgumentOutOfRangeException>(() => SerializedEnum<Code>.GetWireName((Code)42));
    }
}
