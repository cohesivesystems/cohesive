using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

// Regression ceilings for warm transition preparation over a 13-field entity with one guard and three updates.
// Measured medians and the end-to-end benchmark are in docs/performance/entity-transition-end-to-end.md;
// ceilings leave headroom for runtime variance but fail if the removed per-patch copies or LINQ return.
public sealed class TransitionAllocationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    const long DecideCeiling = 12_800;
    const long ApplyCeiling = 4_800;

    [Fact]
    public void Warm_decision_and_candidate_preparation_stay_within_allocation_ceilings()
    {
        var entity = ObjectEntityDefinition.For<Shipment>(new("allocation-shipment"));
        var authored = TransitionAuthoring.Create<Shipment, Assign, bool>(entity.Shape,
            new(new("transition/allocation/assign"), new("revision/1"), new("body"),
                new(new(TransitionAuthoring.Producer), new("tests/transition-allocation"), DocumentOrigin.Generated)),
            transition => transition
                .Requires(new("not-delivered"), (state, _) => state.Status != "Delivered", (_, _) => false)
                .Set(new("set-carrier"), state => state.Carrier, (_, input) => input.Carrier)
                .Set(new("set-status"), state => state.Status, "Assigned")
                .Set(new("set-updated"), state => state.UpdatedAtUtc, (_, input) => input.UpdatedAtUtc)
                .Return(new("applied"), TransitionOutcomeDisposition.Applied, true));
        var compilation = authored.Compile();
        Assert.True(compilation.IsSuccessful);
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var current = entity.CreateState("shipment-1", new Shipment("shipment-1", "tenant-a", "Booked", "unassigned",
            "Chicago, IL", "Dallas, TX", "PO-1", 1234.56m, 24, false, now.AddDays(1), now.AddDays(3), now)).Snapshot;
        var input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Assign("carrier-north", now)));
        var state = PortableValue.Concrete(plan.Definition.Observation, current.Observation.Value);
        TransitionDecision Decide() => TransitionReferenceInterpreter.DecideFullState(plan, new("activation/allocation"), input, state);
        var decision = Decide();
        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        Assert.Equal("carrier-north",
            TransitionStateProjector.ApplyToEntity(entity, "shipment-1", decision, current).Fields[nameof(Shipment.Carrier)].GetString());

        var decide = MedianAllocated(() => Decide());
        var apply = MedianAllocated(() => TransitionStateProjector.ApplyToEntity(entity, "shipment-1", decision, current));
        output.WriteLine($"decide {decide} B; apply {apply} B");
        Assert.InRange(decide, 0, DecideCeiling);
        Assert.InRange(apply, 0, ApplyCeiling);
    }

    static long MedianAllocated(Func<object> operation)
    {
        for (var warm = 0; warm < 50; warm++) GC.KeepAlive(operation());
        var samples = new long[7];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 16; iteration++) GC.KeepAlive(operation());
            samples[sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / 16;
        }
        Array.Sort(samples);
        return samples[3];
    }

    sealed record Shipment(string Id, string Tenant, string Status, string Carrier, string Origin, string Destination,
        string Reference, decimal WeightKg, long Pieces, bool Hazmat, DateTimeOffset PickupAtUtc,
        DateTimeOffset DeliverByUtc, DateTimeOffset UpdatedAtUtc);

    sealed record Assign(string Carrier, DateTimeOffset UpdatedAtUtc);
}
