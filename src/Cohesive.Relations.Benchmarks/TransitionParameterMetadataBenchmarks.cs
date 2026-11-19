using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.IR;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm shipment decisions with validation and evidence, excluding compilation and transport.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
public class TransitionParameterMetadataBenchmarks
{
    CompiledTransitionPlan plan = null!;
    PortableValue input = null!;
    PortableValue state = null!;

    /// <summary>Whether the shipment guard rejects the assignment before parameter reads.</summary>
    [Params(false, true)]
    public bool Rejected { get; set; }

    /// <summary>Prepares the existing shipment fixture and checks the decision outcome.</summary>
    [GlobalSetup]
    public void Setup()
    {
        plan = EntityTransitionEndToEnd.CompileAssign(ShipmentEntity.Instance);
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var shipment = EntityTransitionEndToEnd.InitialState(now);
        if (Rejected) shipment = shipment with { Status = "Delivered" };
        state = PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(shipment));
        input = PortableValue.Concrete(plan.Definition.Input,
            ObservationValue.FromObject(new ShipmentEntity.AssignInput("carrier-north", now)));
        var decision = Decide();
        if (decision.Kind != (Rejected ? TransitionDecisionKind.AdmissionRejected : TransitionDecisionKind.Applied))
            throw new InvalidOperationException($"Unexpected shipment decision: {decision.Kind}.");
    }

    /// <summary>Runs a complete validated decision and produces its evidence.</summary>
    /// <returns>The independently owned transition decision.</returns>
    [Benchmark]
    public TransitionDecision Decide() => TransitionReferenceInterpreter.DecideFullState(
        plan, new("benchmark/parameter-metadata"), input, state);
}
