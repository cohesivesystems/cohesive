# Deterministic scenarios

A scenario describes activity over one exact generated world without making a runtime, test framework, or application
transport part of the semantic model. The first scenario contract covers deterministic scheduled intent:

- a fingerprint-verified `WorldArtifactManifest` is the initial-world authority;
- actors bind stable scenario names to named world exemplars;
- operations declare portable input and output value contracts;
- actions bind an actor, optional target actor, operation, exact portable input, and fixed virtual UTC instant;
- compilation validates all references and types, normalizes declaration order, and fingerprints the resulting plan.

Execution targets, observed action outcomes, state evolution, stochastic policies, and retained traces are deliberately
not embedded in this definition. They are interpretations of the scenario contract and will build on this schedule
without changing its source authority.

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

This gives agents an inspectable and patchable source contract before an execution seam exists. The next layer can
consume `CompiledScenarioPlan`, interpret operations against an in-memory model or system under test, and retain
typed outcomes and traces attributable to this exact scenario fingerprint.

```csharp
sealed record AssignLoad(string LoadId);

sealed record AssignmentReceipt(bool Accepted);
```
