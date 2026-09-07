# Deterministic scenarios

A scenario describes activity over one exact generated world without making a runtime, test framework, or application
transport part of the semantic model. Its portable definition covers deterministic scheduled intent:

- a fingerprint-verified `WorldArtifactManifest` is the initial-world authority;
- actors bind stable scenario names to named world exemplars;
- operations declare portable input and output value contracts;
- actions bind an actor, optional target actor, operation, exact portable input, and fixed virtual UTC instant;
- compilation validates all references and types, normalizes declaration order, and fingerprints the resulting plan.

Execution targets, observed action outcomes, state evolution, and stochastic policies are deliberately not embedded
in this definition. They are interpretations of the scenario contract and do not change its source authority.

## Author with CLR types

The typed builder lowers CLR operation contracts and input values immediately. The callback and CLR reflection do not
survive in canonical IR:

```csharp
using Cohesive.Simulation;
using Cohesive.Simulation.Scenarios;

var scenario = Simulation.DefineScenario(
    id: "scenario/freight-dispatch",
    revision: "r1",
    initialWorld: freightArtifact,
    startsAtUtc: new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
    configure: scenario => scenario
        .Operation<AssignLoad, AssignmentReceipt>("freight.assign-load")
        .Actor("dispatcher", "dispatcher-for-scenario")
        .Actor("carrier", "carrier-for-scenario")
        .Action(
            id: "assign-load",
            afterStart: TimeSpan.FromMinutes(5),
            actorId: "dispatcher",
            operationId: "freight.assign-load",
            input: new AssignLoad("load-42"),
            targetActorId: "carrier"));

CompiledScenarioPlan plan = scenario.Compile();
```

`Operation<TInput, TOutput>` projects both CLR types through the core type mapper and wraps them in required,
single-value, non-null contracts. Use the `Operation` overload that accepts `ValueContract` when presence,
cardinality, or nullability is part of the contract. `Action<TInput>` projects the input through `ObservationValue`.
Compilation fails closed if either contract is non-portable, the input does not satisfy the operation input contract,
an actor or operation is unknown, an actor names a missing world exemplar, or an action uses a non-UTC time or
precedes the fixed scenario start.

## Retain for scripts and agents

Persist the normalized document rather than handwritten builder code or a generated execution plan:

```csharp
var document = ScenarioDefinitionDocument.FromDefinition(scenario);
await File.WriteAllTextAsync(
    "freight-dispatch.scenario.json",
    ScenarioDefinitionJsonSerializer.Serialize(document));
```

The current schema is `cohesive-simulation-scenario/v1`. The strict serializer rejects unknown and duplicate
properties, noncanonical operation/actor/action order, unsupported schemas, invalid cross-references, incompatible
inputs, and fingerprint mismatches. Given equivalent declarations, it emits the same canonical document regardless of
authoring order. Actions at the same virtual instant execute in ordinal action-identity order.

## Execute without wall-clock waits

`ScenarioRunner` interprets each action sequentially in canonical virtual-time order. It does not sleep until an
action's timestamp. Application behavior stays behind `IScenarioActionInterpreter`, so unit tests can use an in-memory
model while Playwright setup or assurance agents can call an application or external system through a different
interpreter:

```csharp
using Cohesive.Execution;
using Cohesive.Model;

var retained = ScenarioDefinitionDocument.FromDefinition(scenario);
var initialWorld = ScenarioWorldSnapshot.FromCoreWorld(retained);
ScenarioExecutionTraceDocument trace = await ScenarioRunner.ExecuteAsync(
    initialWorld,
    new FreightInterpreter());

await File.WriteAllTextAsync(
    "freight-dispatch.trace.json",
    ScenarioExecutionTraceJsonSerializer.Serialize(trace));

sealed class FreightInterpreter : IScenarioActionInterpreter
{
    public string Identity => "demo/freight-interpreter/v1";

    public ValueTask<PortableValue> ExecuteAsync(
        ScenarioActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = context.Input.Value?.Deserialize<AssignLoad>()
            ?? throw new InvalidOperationException("A concrete assignment input is required.");
        var receipt = new AssignmentReceipt(Accepted: input.LoadId.Length > 0);
        return ValueTask.FromResult(PortableValue.Concrete(
            context.Operation.Output,
            ObservationValue.FromObject(receipt)));
    }
}
```

`ScenarioWorldSnapshot.FromCoreWorld` deterministically materializes every actor's named exemplar from the retained
artifact's exact seed and core interpreter. For a relationship-aware artifact, use
`RelationshipScenarioWorldSnapshot.Materialize` from `Cohesive.Simulation.Relations`; it produces the same core
snapshot shape after completing relationship-owned fields. Both paths fail closed when the artifact selects a schema,
interpreter, or entropy algorithm they do not own.

The context exposes that complete snapshot, the exact retained scenario, scheduled action, operation contract,
materialized actor and optional target actor, zero-based schedule position, and contract-bearing input. An interpreter
can read `context.ActorSnapshot.Observation`, `EntityId`, and replay evidence without rediscovering how the artifact is
interpreted. It must return a `PortableValue` carrying the declared operation output contract. The runner fails with
structured diagnostics before executing another action when the contract or value is invalid. Exceptions and
cancellation are operational failures and produce no complete trace.

`PortableValue.Failed` and `PortableValue.Unknown` are valid retained outcomes, not hidden control flow, so the runner
continues to later actions. If an interpretation requires fail-fast domain behavior, model that choice explicitly in
the interpreter or its operation result rather than relying on exceptions as semantic output.

The trace schema is `cohesive-simulation-scenario-trace/v1`. A trace embeds the complete fingerprint-verified scenario,
the exact interpreter identity/version, and one contract-validated outcome per action in canonical schedule order. Its
own fingerprint detects changes to scenario coordinates, interpreter identity, action association, output state, or
payload. Strict deserialization rejects incomplete, reordered, unknown, or fingerprint-inconsistent content.

The runner does not yet mutate actor snapshots or invent transition semantics. The snapshot is an immutable initial
state projection; the retained scenario and world artifact remain the replay authorities, so the trace does not
duplicate generated observations. A subsequent state-evolution layer can apply explicit changes between actions while
retaining these same schedule and outcome contracts.

```csharp
sealed record AssignLoad(string LoadId);

sealed record AssignmentReceipt(bool Accepted);
```
