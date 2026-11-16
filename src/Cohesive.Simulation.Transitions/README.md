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
proof of distributed liveness. Target faults are declared on recovery actions as described below. Automatic state-space exploration remains deferred.


## Declare target faults and replay

A recovery action can declare `ReactionScenarioFault.BeforeExecution` or
`ReactionScenarioFault.LoseAcknowledgement` on its `ReactionScenarioInput`. The fault is part of the
canonical scenario input and fingerprint, not a closure or a mutable test counter. For example:

```csharp
scenario.Action("lose-confirmation-ack", TimeSpan.FromMinutes(12), "worker",
    MachineReactionScenarioInterpreter.Recover,
    new ReactionScenarioInput("confirmation", "confirmation-2",
        ReactionScenarioFault.LoseAcknowledgement));
```

Duplicate delivery is another `Deliver` action with the same host/receipt pair. Delay and reordering
are expressed by scheduled virtual times. No additional delivery scheduler or fault DSL is necessary.

The interpreter automatically decorates each registered host's actual target with private, single-run
instrumentation. Callers supply ordinary hosts; there is no Bind/Attach protocol and no separate fault
session to pass. `MachineReactionHost.DecorateTarget` preserves the policy, store, authorization,
recovery options, exact target revision and authority while leaving the original host unchanged.
Prerequisite inspection retains its original host; receipt lookup is unaffected by target faults.
A fault applies only during its recovery action, and ordinary target exceptions retain their
target-owned classification. Pre-execution failure records `PreCall/NotExecuted`; acknowledgement loss records
`PostCommitPreAcknowledgement/Ambiguous`. An acknowledgement may also be lost after a domain rejection:
the wrapper does not assert that every target return committed a mutation. Fault inputs accept only exact
enum names (no numeric or comma-separated aliases). Repeated target execution is recorded outside the
host's failure-classification boundary and fails the action even when the host catches the inner exception.

The interpreter validates aliases, contracts, delivery ordering and fault placement for the complete
schedule before the first admission. A declared fault that never reaches its target (for example a
reaction still waiting for a prerequisite) fails the run explicitly. `ReactionScenarioObservation.Fault`
records an exercised boundary. The v3 interpreter profile (with the fault-bearing input/output contracts) intentionally rejects old
fault-less scenario contracts rather than silently reinterpreting retained traces.

`ReactionScenarioEnvironment` holds a versioned reconstruction profile and concrete `PortableValue`
input. Use its canonical `Identity` as the interpreter's environment identity. Capture and replay use
existing strict scenario/trace and portable storage codecs:

```csharp
var environment = TenderEpisodeScenarios.Environment();
var interpreter = await TenderEpisodeScenarios.Reconstruct(environment, cancellationToken);
var trace = await ScenarioRunner.ExecuteAsync(
    ScenarioWorldSnapshot.FromCoreWorld(TenderEpisodeScenarios.ConfirmationFirst()), interpreter);
var artifact = ReactionScenarioReplay.Capture(environment, interpreter, trace);
var replay = await ReactionScenarioReplay.ReplayAsync(
    artifact, TenderEpisodeScenarios.Reconstruct, cancellationToken);
```

The compiled tender example's versioned recipe starts with episode 1, produces two cycles of source
receipts, completes acceptance 1, and revises the load to episode 2. Its schedule delivers confirmation
2 first, rejects stale confirmation 1, duplicates delivery and injects both target boundaries before
recovering confirmation 2. `TenderEpisodeRuntime` supplies fresh native stores and deterministic source
operation identities; no production service is contacted.

The artifact retains the typed recipe and complete trace (including exact schedule, faults and actor
initial observations). The interpreter identity pins that recipe, host policies,
authorities, source commit fingerprints and commit timestamps. Reconstruction must resolve the exact
versioned profile/code/contracts that produced those references; the artifact is **not** a self-contained
executable, arbitrary repository backup or credential store. The factory owns target state and any
preexisting Process history and receipts; these are reconstructed by the recipe rather than copied into
the scenario's actor world. A changed reconstructed identity is rejected before scheduled execution;
changed execution evidence fails exact trace comparison. A divergence identifies the first zero-based
sequence and action ID; the exception's Data also carries ExpectedOutcome and ActualOutcome for inspection. Hashes provide integrity/comparison, not
authentication, and cannot prove a factory honestly reconstructed hidden state. Always use isolated
stores/targets and a fresh interpreter. Automatic fixture discovery, production snapshot import,
network timing faults and exhaustive schedule search are outside this profile.
