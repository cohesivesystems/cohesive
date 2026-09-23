using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Model;

namespace Cohesive.Tests.Model;

public sealed class ValueContractValidationAllocationTests
{
    [Fact]
    public void ValidationDispatchCoversEveryPortableTypeCase()
    {
        var text = ObservationValue.FromString("member");
        var number = ObservationValue.FromInt64(1);
        var array = ObservationValue.FromArray([number]);
        var obj = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = number });
        (TypeRef Type, ObservationValue Valid, ObservationValue? Invalid)[] cases =
        [
            (new NamedTypeRef(new("External")), text, null),
            (new OpaqueRuntimeTypeRef("External"), text, null),
            (new ScalarTypeRef(ScalarTypeKind.Int64), number, text),
            (new EnumTypeRef("Choice", ["member"]), text, ObservationValue.FromString("other")),
            (new EntityReferenceTypeRef(new("Entity")), text, ObservationValue.FromString(" ")),
            (new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.Int64)), array, ObservationValue.FromArray([text])),
            (new ObjectTypeRef([new("value", new ScalarTypeRef(ScalarTypeKind.Int64))]), obj, text),
            (new QuantityTypeRef("Count", ScalarTypeKind.Int64), number, text),
            (new JsonTypeRef(JsonTypeKind.Array), array, text)
        ];
        var declared = typeof(TypeRef).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(item => item.DerivedType).ToHashSet();
        Assert.True(declared.SetEquals(cases.Select(item => item.Type.GetType())), "A portable type case is missing from validation coverage.");
        foreach (var item in cases)
        {
            var contract = new ValueContract(item.Type);
            Assert.True(contract.IsSatisfiedByConstant(item.Valid));
            if (item.Invalid is { } invalid) Assert.False(contract.IsSatisfiedByConstant(invalid));
            else Assert.True(contract.IsSatisfiedByConstant(number)); // External constraints remain unresolved.
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(4096)]
    public void NestedCollectionValidationDoesNotAllocateCallbacksPerValue(int count)
    {
        var item = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["value"] = ObservationValue.FromInt64(1)
        });
        var value = ObservationValue.FromArray([.. Enumerable.Repeat(item, count)]);
        var contract = new ValueContract(new ArrayTypeRef(new ObjectTypeRef(
            [new("value", new ScalarTypeRef(ScalarTypeKind.Int64))])));
        for (var index = 0; index < 64; index++) Assert.True(contract.IsSatisfiedByConstant(value));
        // Measure steady-state validation, excluding occasional runtime bookkeeping on the test
        // thread. A per-value callback regression allocates on every sample, so the minimum
        // still detects it at 4,096 values without increasing the allocation ceiling.
        var minimumAllocated = long.MaxValue;
        for (var sample = 0; sample < 8; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var valid = contract.IsSatisfiedByConstant(value);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(valid);
            minimumAllocated = Math.Min(minimumAllocated, allocated);
        }
        Assert.InRange(minimumAllocated, 0, 512);
    }
}
