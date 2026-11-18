# Tender acceptance distillation sketch

These internal source files are compiled directly by `Cohesive.Tests`; they introduce no package or public
framework API. They author candidate machines using `Machine.Define`, plus a bounded effect-order extraction
and an in-memory failure-injection interpretation. They do not reference or execute the archived application.

`TenderReactionModel` adds declarative tender-to-load acknowledgement policies using
`MachineReaction.On(...).Request(...)`. The policies lower to portable Process IR; tests realize them
through the existing durable Process and atomic entity adapters. The load model remains a bounded
acknowledgement episode. See [reaction semantics and runtime boundaries](../../../src/Cohesive.Processes/Machines/README.md).

Each factory returns a declaration with its frozen `Definition` and named `MachineState` and
`MachineMovement` handles. Stable identifiers are declared once in the fluent source; consumers do not
maintain matching string constants or enums:

```csharp
var tender = TenderAcceptanceMachines.Tender();
var decision = tender.Definition.Evaluate(
    tender.Offered.Id,
    tender.SendPending.Resolve().Edge.Action,
    ObservationValue.FromObject(new TenderState("load-1", false, false, false, "")),
    ObservationValue.FromObject(new SendResult(AutoAcknowledged: false)));
// The selected edge targets tender.Pending.Id.
```

An action can select among sibling movements: `SendPending` and `SendConfirmed` share the send-result
action. Resolve the movement's `Edge.Action` when evaluating; its `Id` identifies the individual edge.
Coordination exposes the uncertainty movements through `Uncertain[state]`, keyed by its state handles.
Handles resolve to the declaration's exact definition and cannot mutate it after construction. Runtime
evaluation still accepts identities; the sketch derives them from handles at that boundary.

Read the [extraction dossier](../../../docs/research/tender-acceptance/README.md) and
[coordination model](../../../docs/research/tender-acceptance/coordination.md) before interpreting the model.
In particular, positive receipt validation and reconciliation are proposed improvements, not recovered guarantees.

```sh
dotnet test src/Cohesive.Tests --filter FullyQualifiedName~TenderAcceptanceCharacterizationTests
```

The fluent source is authoritative for the proposal. Tests verify deterministic canonical Machine documents,
fingerprints and serialization. The source-derived trace fixtures pin only the documented clean acceptance
slice; live differential testing against the original application is still required for migration.

Domain clarification D01 assigns customer-ordered stops to the load and actual execution history to trips.
The proposed machines do not require an original trip. The observed trace still includes the legacy trip
notification so that characterization does not silently rewrite the behavior being extracted.

D02 gives the tender a single lifecycle for linkage and acceptance: `AttachLoad` moves from `Unlinked`
to `Offered`, after which acceptance is admitted. The independent linkage machine has been removed.
`LoadAcknowledgement()` returns an explicitly incomplete view of the related load lifecycle;
`Coordination()` describes process progress across participants. Neither is a second binding on the tender.
The tender revision is now `proposal-3` because its graph and initial state changed.

`TenderEntityModel` binds the episode entity to that single authority and authors the concrete link,
acceptance, receipt, and note-edit operations. `EditNote` explicitly preserves machine state; the other
operations realize named movements. `TenderDeclaration` now derives from `MachineModel`, which owns
source and frozen IR without becoming a runtime actor. Existence and duplicate-admission evidence are
explicit command facts; state classification reads only entity fields.

The [entity binding contract](../../../docs/research/tender-acceptance/entity-binding.md) records the target
semantics and current operating boundary. Existing-entity enforcement is implemented for canonical
Transitions; initialization and component composition remain unsupported. The bounded reaction
profile now supports durable Process execution, native or HTTP targets, and Cosmos receipt
observation with durable admission and independently recoverable attempts. The framework
`MachineReactionRecovery` pages the Process authority; see
[infrastructure hosting](hosting.md) for retry and authorization boundaries.
The legacy trace host intentionally preserves observed partial commits.

[Causal coordination](coordination.md) declares the acceptance prerequisite and demonstrates confirmation
arriving first, durable waiting, independent guards, and acknowledgement-loss recovery.

[Reaction lifecycle](lifecycle.md) shows authorized inspection, dependent discovery, explicit abandonment,
and the distinction between replaying uncertain delivery and requesting new business work.

[Explicit episodes](episodes.md) extends the sketch with two acceptance cycles, episode-scoped
prerequisites and target guards that reject late confirmation from an older cycle.

## Reaction analysis and deterministic schedules

`TenderEpisodeScenarios.ConfirmationFirst()` authors the canonical delivery/recovery schedule for two
acceptance cycles. Its initial environment has already acknowledged acceptance 1 and revised the load
to episode 2, ready for acceptance. Confirmation 2 arrives first and waits. Replaying acceptance 1
cannot release it. Acceptance 2 releases its own confirmation; delayed confirmation 1 fails the target
episode guard. The target adapter loses the first acknowledgement for episode 2 after committing, and
recovery reuses the same operation identity. Repeated recovery of the completed instance adds no attempt.

`MachineReactionSimulationTests` runs that schedule twice from independently built, fixed-clock native
in-memory environments and compares the serialized portable traces. It also runs all 24 permutations
of the four receipts against a load initially ready for episode 2: old acceptance is rejected, its
confirmation cannot dispatch, and episode 2 converges. A separate schedule abandons acceptance 1 and
shows that its dependent stays blocked while episode 2 proceeds.

The example's source remains the fluent machines, reactions and scenario builder. The scenario document
and trace are derived artifacts. Hosts execute the existing canonical Process and Transition definitions;
the simulation layer only selects the next explicit action and time. Static registration diagnostics
are covered in `MachineReactionAnalysisTests`. See the [simulation profile and boundaries](../../../src/Cohesive.Simulation.Transitions/README.md).


### Declarative fault replay

`TenderEpisodeScenarios.ConfirmationFirst()` now declares a failure before confirmation target execution
and a lost acknowledgement after it returns. Both are serialized action inputs; duplicate delivery and
delay remain schedule declarations. `TenderEpisodeRuntime` is the shared native example realization.
`TenderEpisodeScenarios.Environment()` and `Reconstruct(...)` define its versioned portable initial-state
recipe. Capture a run with `ReactionScenarioReplay.Capture(...)`, persist the returned JSON, and replay it
with `ReactionScenarioReplay.ReplayAsync(artifact, TenderEpisodeScenarios.Reconstruct)`.

The regression tests verify fresh-run trace equality, stable operation identity through both failures,
unchanged load concurrency after receipt replay, stale-episode rejection, cross-episode waiting,
invalid fault placement, unexercised faults, and reconstruction mismatch. See the
[replay contract and operating limits](../../../src/Cohesive.Simulation.Transitions/README.md#declare-target-faults-and-replay).


The runtime prepares source receipts through `EntityTransitionClient`, the repository-bound typed facade over
`DirectTransitionExecutor`. Configuration and preparation happen once; invocation accepts the authored
transition handle, entity ID and ordinary command. The canonical document supplies the input contract.
The execution still shares the Process adapter's read/decide/project/atomic-commit mechanism and creates
no Process provenance. The replay recipe stays `v2`: typed and portable calls produce identical receipt identities.

```csharp
var tenders = new EntityTransitionClient(repository, authority,
    authorize: AuthorizeTenderAcceptance, partitionKey: organization);
tenders.Prepare(model.Accept); // Optional eager setup validation; no repository IO.

var execution = await tenders.ExecuteAsync(
    model.Accept,
    tenderId,
    new EpisodeCommand(1),
    operationId: new ActivationId(requestId),
    context: context);
var receipt = execution.Receipt; // Atomic state/decision evidence, or null on execution failure.
```

Preparation is scoped to this configured binding and shared safely for concurrent uses of the same document.
Commands are projected afresh, and authorization is never cached. The existing canonical result retains
its exact typed outcome contract, decision, diagnostics and receipt; this facade introduces no result DTO.

The host's `AuthorizeTenderAcceptance` must authorize scope, resource access and receipt disclosure on
every call, including replay. The isolated example explicitly permits its own fixture operations; that policy
is not application authorization. Direct emissions are retained in the receipt with Transition provenance;
the caller owns delivery. Existing-subject rejected decisions are replayable. Direct creation is rejected
at binding time until a separate durable operation ledger can retain rejection without an entity.
See the [typed execution and direct execution contract](../../../src/Cohesive.Storage/README.md#direct-receipt-backed-transition-execution).

The runtime's repositories and targets are constructor-initialized read-only properties; only the
Process store is replaceable to exercise durable restart.

### Deliver committed tender movements

When the tender repository uses Cosmos, run one change-feed processor for each hosted reaction.
For example, bind the acceptance reaction to the repository that handles the direct tender command:

```csharp
var source = new CosmosMachineReactionSource(tenderRepository, leases, acceptanceHost,
    processorName: "tender-acceptance", instanceName: workerId, fromUtc: initialObservationTime);
await source.RunAsync(createWorkerContext, stoppingToken);
```

Choose an initial observation time that covers the first relevant receipt and retain receipts through
the recovery window. The Cosmos lease records progress for the policy revision and authority. Each batch
decodes immutable receipts and calls `MachineReactionHost.AdmitBatchAsync`. The host authorizes and
durably admits matching movements before invoking the source's checkpoint callback. A supervised recovery
worker then dispatches pending reactions. Confirmation has its own processor and waits for acceptance in
the same episode. Other transports can supply ordered receipt batches and their durable checkpoint callback.

The direct-acceptance restart test in `MachineReactionHostTests` exercises a commit before delivery,
a failed checkpoint after admission, a restarted worker, and duplicate delivery after load acknowledgement
has entered the pending state.
