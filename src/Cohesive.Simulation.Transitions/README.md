# Cohesive.Simulation.Transitions

`Cohesive.Simulation.Transitions` is the optional semantic composition between deterministic simulation scenarios and
canonical Cohesive Transitions. It lets a scenario operation execute through the pure Transition reference
interpreter and turns an accepted sparse patch into an explicit, validated actor observation replacement. The core
`Cohesive.Simulation` package remains independent of the Transitions language.

## Install

The current alpha targets .NET 10:

```bash
dotnet add package Cohesive.Simulation.Transitions --prerelease
```

## Bind operations to Transitions

Compile and retain the Transition document as its semantic authority, then bind its exact plan to a scenario
operation:

```csharp
CompiledTransitionPlan assignLoad = authoredTransition.Compile(shapes.Graph).Plan
    ?? throw new InvalidOperationException("The Transition did not compile.");

var interpreter = new TransitionScenarioActionInterpreter(
[
    new(
        operationId: "freight.assign-load",
        transition: assignLoad,
        subject: ScenarioTransitionSubject.TargetActor)
]);

ScenarioExecutionTraceDocument trace = await ScenarioRunner.ExecuteAsync(initialWorld, interpreter);
ScenarioWorldSnapshot finalWorld = trace.ToFinalWorldSnapshot();
```

The scenario operation input and output contracts must exactly match the bound Transition. Subject selection is
explicit: `Actor` uses the action actor, while `TargetActor` requires the action to name a target. The interpreter
supplies the subject's complete current observation as both evaluation and fresh commit evidence. Accepted patches are
projected through `TransitionStateProjector`, validated against the Transition's exact Shape graph, and returned to the
runner with the prior observation as concurrency evidence. The next scheduled action therefore sees the evolved
state.

The interpreter identity deterministically pins the adapter profile, operation bindings, subject policies, and exact
Transition definition identities, revisions, and fingerprints. Retain the canonical Transition documents beside the
scenario and its execution trace so that identity remains resolvable and inspectable.

Admission and domain rejections remain authored outputs and do not change state. Invalid or infrastructure decisions
become failed portable outputs. The current profile deliberately fails closed for absent-subject creation, emission
intents, and Machine movements: the scenario result model does not yet retain or atomically commit those effects, so
silently dropping them would weaken Transition semantics.

## Simulate durable reactions

`MachineReactionScenarioInterpreter` interprets existing scenario IR through `MachineReactionHost`.
The integration now also references `Cohesive.Storage`; the core Simulation package still has no
Transition, Process, or Storage dependency. It adds no queue, scheduler, or alternate guard evaluator.

```csharp
MachineReactionScenarioInterpreter.DeclareOperations(scenario)
    .Actor("worker", "worker-exemplar")
    .Action("confirmation-arrives", TimeSpan.FromMinutes(1), "worker",
        MachineReactionScenarioInterpreter.Deliver,
        new ReactionScenarioInput("confirmation", "confirmation-2"))
    .Action("confirmation-waits", TimeSpan.FromMinutes(2), "worker",
        MachineReactionScenarioInterpreter.Recover,
        new ReactionScenarioInput("confirmation", "confirmation-2"));

var interpreter = new MachineReactionScenarioInterpreter(
    environmentIdentity: "tender/load-ready-2/native-targets/v1",
    store: processStore,
    hosts: hostsByAlias,
    receipts: committedReceiptsByAlias);
var trace = await ScenarioRunner.ExecuteAsync(initialWorld, interpreter);
var json = ScenarioExecutionTraceJsonSerializer.Serialize(trace);
```

Registration reuses the recovery catalog checks for a common discoverable store, recovery authorization,
exact prerequisites and authority isolation.

The four operations are `Deliver`, `Recover`, `Inspect`, and `Abandon`. Delivery durably admits without
calling the target. Later operations require an earlier delivery of the same host/receipt alias.
Duplicate delivery exercises the real idempotency boundary. Recovery advances exactly one host call;
a schedule must explicitly include further calls for release, dispatch, or retry. Inspection never
dispatches. Abandonment uses canonical cancellation with fresh expectation evidence and defaults to
denied unless an explicit simulation authorization callback is supplied.

Every call receives the action's virtual UTC time through `OperationContext`. A fresh interpreter is
required for each sequential run. Host policies and source commit fingerprints are pinned into the
interpreter identity, alongside the caller's declared environment identity. Declaration order does not
change that identity. Retain the reaction documents, source receipts, initial-state construction and
fault configuration alongside the scenario for reproducibility.

The portable output retains policy/source fingerprints, episode value, admitted instance, exact
prerequisite, status, diagnostic code, target decision, operation identity, cancellation disposition and attempt count. A recovery
outcome is retained even when subsequent inspection can already resolve the target receipt; a lost
acknowledgement therefore remains visible in the trace. Target guards and cancellation retain their
normal runtime semantics.

This profile requires isolated deterministic stores and targets. It does **not** sandbox supplied hosts,
make HTTP deterministic, or capture arbitrary initial repositories automatically. The environment
identity is an explicit caller-owned assertion covering initial entity state, authorization, adapter
configuration, and injected faults. Source receipt generation must also use a fixed clock and stable
operation identities. The scenario world represents delivery actors; target entities remain owned by
the supplied repositories and are not duplicated into scenario actor state. Consequently
`ToFinalWorldSnapshot()` is not a snapshot of the target repositories.

The compiled [tender example](../../eng/examples/tender-acceptance/TenderEpisodeScenarios.cs) and
[tests](../Cohesive.Tests/Machines/MachineReactionSimulationTests.cs) exercise a late old confirmation,
confirmation-first arrival, duplicate delivery, a lost target acknowledgement, cancellation, and all
24 permutations of four receipts. These are bounded schedules, not exhaustive model checking or a
proof of distributed liveness. Fault behavior currently belongs to the explicit test target adapter;
a portable fault-policy language and automatic state-space exploration are deferred.
