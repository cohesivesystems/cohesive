using Cohesive.Adapters.Sql;

namespace Cohesive.Tests.Sql;

public sealed class SqlParameterSlotsTests
{
    [Fact]
    public void Slots_PreserveFirstUseOrderAndSnapshotOwnership()
    {
        var slots = new SqlParameterSlots<string>();
        Assert.Equal(0, slots.AddConstant(position => $"constant:{position}"));
        Assert.Equal(1, slots.GetOrAddRuntime("value", position => $"runtime:{position}"));
        var snapshot = slots.Snapshot();
        Assert.Equal(1, slots.GetOrAddRuntime("value", _ => throw new InvalidOperationException()));
        Assert.Equal(2, slots.GetOrAddRuntime("VALUE", position => $"runtime:{position}"));
        Assert.Equal(3, slots.AddConstant(position => $"constant:{position}"));
        Assert.Equal<string>(["constant:0", "runtime:1"], snapshot);
        Assert.Equal<string>(["constant:0", "runtime:1", "runtime:2", "constant:3"], slots.Snapshot());
    }

    [Fact]
    public void FailedFactories_DoNotConsumePositionsOrRegisterBindings()
    {
        var slots = new SqlParameterSlots<int>();
        Assert.Throws<FormatException>(() => slots.GetOrAddRuntime("value", _ => throw new FormatException()));
        Assert.Throws<FormatException>(() => slots.AddConstant(_ => throw new FormatException()));
        Assert.Empty(slots.Snapshot());
        Assert.Equal(0, slots.GetOrAddRuntime("value", position => position));
        Assert.Equal<int>([0], slots.Snapshot());
    }

    [Fact]
    public void ReentrantFactories_FailWithoutCorruptingAllocationState()
    {
        var slots = new SqlParameterSlots<int>();
        Assert.Throws<InvalidOperationException>(() => slots.AddConstant(
            _ => slots.AddConstant(position => position)));
        Assert.Throws<InvalidOperationException>(() => slots.GetOrAddRuntime("value",
            _ => slots.GetOrAddRuntime("value", position => position)));
        Assert.Empty(slots.Snapshot());
        Assert.Equal(0, slots.AddConstant(position => position));
        Assert.Equal(1, slots.GetOrAddRuntime("value", position => position));
    }

    [Fact]
    public void InvalidInputs_AreRejectedBeforeAllocation()
    {
        var slots = new SqlParameterSlots<int>();
        Assert.Throws<ArgumentNullException>(() => slots.AddConstant(null!));
        Assert.Throws<ArgumentNullException>(() => slots.GetOrAddRuntime("value", null!));
        Assert.Throws<ArgumentNullException>(() => slots.GetOrAddRuntime(null!, position => position));
        Assert.Throws<ArgumentException>(() => slots.GetOrAddRuntime(" ", position => position));
        Assert.Empty(slots.Snapshot());
    }
}
