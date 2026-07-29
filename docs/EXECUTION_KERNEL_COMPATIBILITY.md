# Execution Kernel Compatibility Inventory

This inventory records the compatibility behavior characterized by ARI-153 and the kernel substrate introduced since then against the normative EK-01 through EK-09 scenarios in the [Cohesive Execution Kernel Specification](https://app.notion.com/p/3ab8cf7881f981f78ef1e34d7a907c70). It is a migration baseline, not an alternative semantic contract. Missing behavior remains required by the specification.

Status meanings:

- **Pass**: the current model and runtime satisfy the scenario's normative guarantees.
- **Partial**: useful substrate or observable behavior exists, but one or more normative guarantees are missing.
- **Absent**: the scenario's core semantic construct has no current representation.

EK-01 now passes through the canonical Transition compilation and reference-interpretation path. Canonical
interaction contracts and runtime envelopes provide the shared event, Request, Signal, and Reply vocabulary, and a
canonical durable-operation reference protocol now interprets Request attempts, acknowledgement, reconciliation,
and result admission. Canonical Process control now interprets protocol-neutral lifecycle commands, safe-point
coordination, attempt lineage, and write-once attempt affinity without claiming a durable runtime realization.
The canonical protocols are now composed by a versioned `Cohesive.Storage.Processes` checkpoint and a
copy-on-write atomic reference store with inbox admission, CAS revisions, worker fencing, replay receipts, and
crash-cut injection. They are not yet driven by the production Process recovery loop, so this substrate does not
make EK-06 or EK-08 system-runtime Passes. Canonical Process IR and its pure reference interpreter now
provide the persisted semantic graph, typed bindings, exact references, immutable token/wait state, deterministic
finite activations, and interaction intents needed by subsequent checkpoint work. EK-09 remains Partial:
representative Transitions have a typed C# producer that is equivalent to direct IR, while Processes have direct
canonical IR but no C# lowering yet. The remaining scenarios retain the Partial classifications recorded below.

## Scenario matrix

| Scenario | Status | Current compatibility | Missing kernel semantics |
| --- | --- | --- | --- |
| EK-01 — structured DQ branching | Pass | `Cohesive.Transitions.IR` provides canonical persisted structured definitions with stable nodes, typed contracts and outcomes, ordered branching and matching, algebraic sparse patches, exact interaction-contract emission references, and fingerprint-bound Machine-edge references. `TransitionStaticCompiler` performs target-independent type, flow, exhaustiveness, access/effect, derived-field, invariant, and Machine-link analysis. `TransitionReferenceInterpreter` executes either complete state or finite sparse observations through one deterministic core and returns typed outcomes, committable patches, emission intents, Machine movements, guarantee demands, conflicts, diagnostics, and ordered actual-execution evidence. | None within the EK-01 reference decision. Observation acquisition and authoritative commit remain explicit external interpretations of the returned demands and intents. |
| EK-02 — durable human review | Partial | The canonical Process reference interpreter materializes complete `AwaitMatch` registrations, computed absolute timers, early-input buffers, input dispositions, one deterministic winner, invalidated losers, and typed continuation bindings in immutable state. Every wait occurrence now has a typed replay-stable identity. An interaction may target that exact occurrence, while deliberately unscoped input can still arrive early. The reference store durably admits exact inputs and advances the same aggregate revision used by activation commits, so a racing stale commit reloads instead of losing the input. | ARI-168 recovery-driver integration, production timer and interaction adapters, and end-to-end human-review conformance. |
| EK-03 — vendor/manual fulfillment | Partial | Process Request nodes emit replay-stable canonical Requests targeted to their exact response waits, park only their owning token, retain the exact logical obligation, and admit one contract-linked Reply outcome into the authored continuation. The versioned Storage checkpoint atomically composes continuation, outbox records, durable operation state, acknowledgements, and inbox dispositions. `DurableOperationReferenceExecutor` supplies bounded retry, acknowledgement, reconciliation, and late/stale/duplicate result policy. | ARI-168 orchestration of store, dispatcher, and Process interpreter; provider adapters and full vendor/manual workflow conformance. |
| EK-04 — parallel gates and join recovery | Partial | `ProcessContinuationState` is a complete token set rather than a cursor. The reference interpreter creates stable Fork children, retains reciprocal membership and branch-local bindings, schedules tokens deterministically, records partial progress, and resolves All/Any/RequiredCount Joins under explicit failure, cancellation, completion-order, and tie-break policy. `ProcessDurableCheckpoint` preserves the complete continuation under physical CAS and worker fencing; the in-memory reference store verifies all-or-none before/after crash cuts. | ARI-168 recovery-driver integration, a production durable-store adapter, and end-to-end partial-join recovery conformance. |
| EK-05 — capability-safe multi-entity coordination | Partial | Exact Transition invocations receive independent portable subjects and typed inputs; Process state retains coordination outcomes rather than aggregate snapshots, so multi-entity coordination does not imply one global transaction. | Canonical scope/region, guarantee-demand, capability-evidence, compensation, and reconciliation constructs; proof-directed rejection when an atomic multi-entity demand cannot be realized. The legacy `ProcessTransactionScope` is not canonical authority. |
| EK-06 — durable effect crash matrix | Partial | The canonical reference protocol uses the Request `EmissionId` as logical operation identity, derives a scoped deduplication key, and models leased claim/renewal, monotonic fences, ordered attempt snapshots, explicit failure phase/effect evidence, bounded retry, timeout/cancellation, fenced reconciliation and escalation identities, one durable acknowledgement, physical batches with complete per-item evidence, and a separate target admission. The Storage reference aggregate commits continuation, local mutations, inbox/outbox, host-operation receipts, and durable-operation state all-or-none; duplicate commit identities replay, conflicting reuse fails, and injected pre/post-boundary crashes expose none/all respectively. Physical publication remains at-least-once. | ARI-168 integration across interpreter, dispatcher, acknowledgement, and recovery cuts; production adapter conformance and external-side-effect crash tests. |
| EK-07 — signal arbitration | Partial | Canonical Signal envelopes enter immutable Process input state by logical `EmissionId`. The reference interpreter buffers unscoped early input, deduplicates replay, selects exactly one `AwaitMatch` winner by descending priority, ordinal clause identity, then ordinal emission identity, retains tombstones, and applies authored late/duplicate policy without reopening the wait. Exact `ProcessWaitRegistrationId` targeting prevents stale same-token delivery from routing to a later compatible wait. Durable inbox admission and activation commit share one CAS revision, closing the registration/commit lost-wakeup cut in the reference store. | ARI-168 store/interpreter driver integration, production signal adapters, and end-to-end race conformance. |
| EK-08 — index rebuild recovery | Partial | `ProcessControlState` retains stable Process instance, attempt, and activation identity, invariant-preserving safe points, ordered attempt lineage, and write-once generic attempt affinities. `ProcessControlReferenceExecutor` makes pause/continue retain the current attempt and affinities, while `RestartAttempt` explicitly abandons the old attempt and starts a stable replacement without inherited affinities. The Storage checkpoint now atomically composes this semantic control authority with the exact continuation under CAS and worker fencing. | ARI-168 recovery integration; Storage-owned candidate-generation allocation, binding, cleanup, and abandoned-generation exclusion; retry/recovery integration that retains the attempt and generation; and fenced, idempotent generation promotion plus read/write backend swap. |
| EK-09 — C# and IR equivalence | Partial | Representative typed C# Transition authoring lowers immediately to the same canonical `Cohesive.Transitions.IR` definitions as direct authoring. `Cohesive.Processes.IR` now supports direct canonical authoring with stable node, edge, branch, clause, and binding identities, typed contracts, exact semantic references, deterministic normalization and fingerprints, and strict document round trips. Neither canonical IR requires callbacks or a producer assembly to deserialize and validate. | Canonical Process C# lowering and a Process C#/direct-IR equivalence suite remain ARI-170 work. The legacy Process authoring/runtime path still uses delegate-bearing executable node objects and must migrate without becoming semantic authority. |

The executable classifications and focused behavioral baselines live in `src/Cohesive.Tests/ExecutionKernel/ExecutionKernelCharacterizationTests.cs` and run as part of the existing `Cohesive.Tests` project.

## Canonical durable-operation reference protocol

The ARI-160 interaction vocabulary remains authoritative for Request meaning. `RequestResponseObligation` owns
terminal outcomes, retry preconditions, ambiguous and unresolved resolution, late/stale/duplicate policy, timeout
and cancellation support, and retention. `DurableRequestBinding` only supplies the concrete bounded realization
data needed to interpret one exact Request contract: attempt and lease bounds, an optional timeout trigger,
idempotency evidence, exhaustive exact Reply mappings, and definition/node references for required reconciliation
or escalation. Exact contract linking is validated through `InteractionContractCatalog`; handler registration or a
wire discriminator cannot choose the semantics.

`DurableOperationState` is the versioned portable reference state. It keeps the logical Request, binding, explicit
creation time, monotonically fenced claims and renewals, ordered immutable attempt snapshots, append-only fenced
reconciliation evidence, recovery requirement, one acknowledgement, and one target disposition. Acknowledgements
from reconciliation or escalation retain the exact recovery identity that won. The logical operation identity is
not another generated type or provider identifier; it is the Request `EmissionId`. The target-deduplication key
additionally scopes the stable idempotency value by authority and exact Request contract so unrelated Requests
cannot collide.

`IDurableOperationAdapter` receives an immutable `DurableOperationInvocation` and returns typed outcome or failure
evidence. It has no aggregate mutation surface and declares the exact Request contracts and target guarantees it
supports. `IDurableOperationBatchAdapter` returns complete emission/attempt/fence-keyed evidence for one physical
batch, allowing successful items to acknowledge independently while failed items alone remain retryable.
`DurableOperationReferenceExecutor` consumes that evidence through deterministic replacement-state operations and
validates adapter capabilities against the binding. Semantic timeout and cancellation are explicit typed state
transitions; host cancellation is only operational interruption. The split makes the three EK-06 cuts observable:

1. **Origin committed, dispatch not begun:** initial operation state remains pending and can be claimed; atomically
   creating that state with the origin commit and outbox is a Storage responsibility.
2. **External success possible, acknowledgement absent:** the dispatched attempt is ambiguous. Blind retry is
   forbidden unless stable-identity idempotency evidence admits it; otherwise the authored reconciliation,
   terminal-failure, or escalation path is required.
3. **Acknowledgement durable, target continuation not committed:** replay observes the acknowledgement and skips
   external dispatch. Result admission then accepts once or durably returns the target's prior duplicate, late, or
   stale disposition.

This is a reference protocol and conformance substrate, not a hidden claim that semantic state itself performs
durable I/O. The reference executor does not own a repository and deliberately leaves every durable cut to its
caller. The v1 state schema identifies the portable reference value; it is not a second Storage operation-ledger
or checkpoint authority. `ProcessDurableCheckpoint` now composes this state into a physical aggregate without
copying its fields. Process/Transition driver integration and production adapters remain outstanding, so EK-06
remains **Partial**.

## Canonical durable Process storage

`Cohesive.Storage.Processes.ProcessDurableCheckpoint` is the versioned physical aggregate for one logical Process
instance. It composes, rather than mirrors, the canonical start receipt, complete multi-token continuation,
`ProcessControlState`, committed activation receipts, cached host-operation results, durable inbox, logical
interaction outbox, and `DurableOperationState` ledger. Its outer physical schema, storage revision, worker lease,
and worker fence are persistence coordination evidence; they do not replace the Process definition revision,
semantic control revision, operation fence, or a provider ETag. `ProcessCheckpointCompatibilityValidator` checks
the exact definition identity, revision, fingerprint, restored-continuation and wait topology, inbox-disposition
provenance, and bidirectional trace/host-operation/outbox/Request-operation closure before host execution. Restored
Fork and Join state also proves derived occurrence identities, policy-shaped completion history, canonical winner
selection, and coherent resolved state. Interaction-emission trace evidence includes the canonical envelope content
fingerprint, so matching an `EmissionId` is insufficient to replace the payload, contract, origin, target, or
envelope kind. Cached host-operation results are a closed typed-value-or-error union; failed results cannot retain
emissions. Each attempt's activation receipts form an exact before/after continuation-fingerprint chain: the first
receipt consumes the canonical clean start or restart and the current attempt's final receipt publishes the
checkpoint continuation. A zero-activation current attempt must itself be that exact clean continuation for the
pinned definition and invocation input.

`IProcessDurableStore` exposes one provider-neutral atomic aggregate boundary. A commit replaces the complete
checkpoint and composes eligible local mutations under an expected physical revision and exact live worker fence.
The commit identity and deterministic content fingerprint make an ambiguous exact retry replay its prior result;
reusing that identity for different content is an identity conflict. Activation receipts, operation receipts,
inbox dispositions, outbox history, publication attempts, acknowledgements, and durable Request states are
append-only or monotonic successor evidence. Physical attempt histories append new attempts, while the latest
attempt snapshot may advance only through its legal claim, dispatch, failure, acknowledgement, or resolution
stages; renewal or stage rollback is rejected. Once an attempt closes, no new logical activation, host-operation,
inbox-disposition, outbox, or Request-operation evidence may be attributed to it, while already-retained physical
publication and durable-operation attempts may continue their legal monotonic reconciliation progress. Activation
receipts are scoped by Process attempt and use attempt-local contiguous sequences, so restart resets the canonical
continuation count without erasing prior attempt evidence. Wait indexes and dispatch queues are projections of this
authority, not independent semantic state.

Inbox admission does not require a live worker. It deduplicates exact canonical input by logical `EmissionId` and
increments the same aggregate revision used by activation commits. Therefore an input racing wait registration or
consumption makes the worker's stale commit fail CAS and forces a reload; the input cannot disappear between a
separate registration and commit. The physical inbox receipt is an attributable projection of the canonical
semantic receipt: pending input may become Buffered and Buffered may reach one terminal disposition, but terminal
evidence cannot be rewritten. Terminal continuations still admit late inputs durably so authored late, stale,
observe, reject, or dead-letter policy can classify them in a subsequent activation. `ProcessWaitRegistrationId`
identifies one exact token wait occurrence. A null
target registration remains an intentional early-delivery address, while an exact stale or closed registration
cannot route to a later compatible wait on the same token.

`InMemoryProcessDurableStore` is a copy-on-write semantic oracle. Initialization, inbox admission, worker
acquisition, worker renewal, and aggregate commit each expose pre-boundary and post-boundary crash points. A crash
before publication exposes none of a staged mutation; a crash after publication but before return exposes all of
it, and the exact retry replays. Reclaiming an expired lease allocates a greater worker fence and permanently makes
the prior owner stale. A lease is live only from its inclusive claim time to its exclusive expiry; acquisition,
renewal, and commit observations cannot predate retained aggregate or latest-renewal evidence. This reference
contract promises atomic local persistence and logical idempotency. It does not promise physical exactly-once
external publication, and it is not itself a production durability provider.

## Canonical Process lifecycle control

ARI-162 defines one protocol-neutral lifecycle surface in `Cohesive.Execution`: `Inspect`, `Signal`, `Pause`,
`Continue`, `RestartAttempt`, `Cancel`, and `Terminate`. Every mutating command carries a stable command identity,
logical idempotency key, attributable authorization evidence, provenance, and an expectation for the exact Process
attempt and semantic control revision. `ProcessControlRevision` is the optimistic lifecycle fence; it is distinct
from an external-operation ownership fence and from a Storage record version. Durable receipts for mutating and
Signal-admission commands make exact replay return the original decision before evaluating a now-stale expectation;
read-only Inspect creates no receipt. Conflicting reuse of a command identity or idempotency key and stale
concurrent commands produce structured diagnostics.

`ProcessControlState` is the versioned portable semantic authority for lifecycle mode, attempt lineage, finite
activation position, safe-point evidence, and accepted command receipts; Signal admissions are deterministic
projections of those receipts. Persisted histories and live commands use one pure lifecycle reducer, so state
admission rejects impossible mode, phase, attempt, revision, and chronology combinations. Work already inside an
activation reaches an explicit invariant-preserving safe point before Pause, RestartAttempt, or cooperative Cancel
takes effect. Pause and Continue retain the logical Process instance, current attempt, and every attempt affinity.
RestartAttempt instead records explicit abandonment and cleanup for the prior attempt, creates one caller-selected
stable replacement attempt under the same Process instance, and does not inherit the old attempt's affinities.
Cancel closes cooperatively at a safe point; Terminate is an immediate, irreversible forced stop with explicit
cleanup. Pending cooperative safe-point actions do not silently replace one another; only Terminate may preempt the
pending action immediately. Recovery of the same attempt, replay of an observation, and explicit attempt restart
are therefore not collapsed into one operation.

The physical checkpoint retains prior-attempt activation receipts, host-operation receipts, inbox evidence,
outbox emissions, publication attempts, and durable operation history under their original attempt provenance.
Restart admits a new current attempt only when Control contains the exact causal abandonment and replacement
receipt; the replacement starts with a clean zero-activation continuation and cannot inherit the abandoned
attempt's waits, buffered inputs, Requests, or affinities.

Signal commands wrap an already-canonical `SignalEnvelope`. Exact contract and target validation precede admission;
active attempts admit Signals for arbitration, paused or pausing attempts buffer them, and retiring or terminal
attempts reject them. Emission and scoped contract/idempotency identity prevent a replayed logical Signal from
creating another admission. The control protocol records admission evidence and an external realization intent;
the Storage reference store now supplies durable inbox admission and the shared CAS cut, while ARI-168 must connect
that cut to control and Process interpretation before EK-07 can Pass end to end.

`ProcessControlJsonSerializer` supplies strict canonical command, state, and versioned decision wires. Catalog-aware
reads link Signals and validate named reason details and attempt-affinity values through the catalog's retained shape
graph. First-time decision intents are admissible only at their exact latest receipt or observation cut; a later
state can retain the receipt for replay without being able to present it again as a fresh side-effecting result.

`ProcessAttemptAffinity` is deliberately generic and write-once. An index-sync Process can use a stable semantic
slot to bind its current attempt to a concrete candidate-generation value, so pause/continue naturally retain that
generation and restart naturally requires a fresh binding. Cohesive.Storage remains the authority for allocating,
persisting, cleaning up, excluding, and promoting physical index generations. The lifecycle reference executor does
not own a checkpoint repository or allocate and promote generations. ARI-166 now supplies the physical checkpoint,
atomic receipt/inbox composition, CAS, and worker-fence substrate; ARI-168 owns runtime integration. Fenced
idempotent generation promotion and backend swap remain subsequent Storage/index-sync work. Consequently, EK-08
is **Partial**.

## Canonical finite Process IR

ARI-167 introduces `Cohesive.Processes.IR.ProcessDefinition` as the persisted semantic authority for Process
coordination. It uses the shared execution-definition envelope and fingerprint model rather than defining another
Process document. The normalized graph has stable node and edge identities, one typed invocation input, a typed
terminal result, explicit recovery policy, typed continuation bindings, ordered Choice/Match cases, normalized
Request outcomes, normalized Fork branches, and normalized AwaitMatch clauses. Its closed node union contains
Transition invocation, Relation/Query evaluation, Request, domain-event emission, Signal send, Choice, Match,
Fork, Join, AwaitMatch, Timer, Reply, explicit durable cut, Return, and Fail.

Transition and Relation/Query nodes carry exact `ExecutionDefinitionReference` values. Linking supplies derived
input/result contract evidence through `ProcessDefinitionValidationContext`; the referenced definition remains the
authority and is not copied into Process IR. Request, event, Signal, Reply, and AwaitMatch nodes use exact typed
interaction references resolved through `InteractionContractCatalog`. Expressions can observe only the Process
input and definitely available typed continuation bindings, and Process v1 pins the same explicit pure capability
profile as Transition v1. An inbound Request clause separately binds its application payload and its admitted
logical Request obligation; Reply consumes that definitely visible obligation and must link back to the exact
Request contract. Aggregate state, relation execution artifacts,
interaction definitions, runtime services, delegates, adapters, and compiled plans are outside the canonical
closure.

`ProcessDefinitionValidator` checks portable contracts and expressions, exact link families, interaction payloads
and outcomes, stable construct and edge identity, edge targets, reachability, definite binding flow, Request outcome
coverage, conservative Choice/Match exhaustiveness proof, Fork/Join reciprocity, token-owned ingress and
convergence, AwaitMatch arbitration, Request/Reply obligation continuity, and deterministic policy validity. An
All Join exposes values guaranteed by every completed branch; partial Joins retain only the pre-Fork value scope
until an explicit aggregation construct is introduced. It also
builds a same-activation graph in which Request, AwaitMatch, Timer, and explicit durable cut are barriers. Every
activation path must be acyclic and must reach a terminal node or one of those durable barriers. Recurrence is
therefore explicit and valid only across a persisted continuation boundary; a durable boundary on one branch does
not hide a free cycle on another. A Fork branch may contain durable recurrence only when every finite exit remains
owned by and converges on its reciprocal Join and the branch has a structural Join exit.

`ProcessStaticCompiler` admits only a fully validated exact document and produces an indexed plan without copying
semantic authority. `ProcessReferenceInterpreter` is a pure immutable reducer over that plan: it starts with one
stable root token, schedules ready tokens in ordinal token-identity order, executes one node quantum per scheduling
round, and defers Join and AwaitMatch arbitration to deterministic round boundaries. Fork children, wait
registrations, emissions, and idempotency keys use versioned, purpose-separated deterministic identities. Every
activation ends at the first deterministic durable boundary, terminal outcome, or complete quiescent continuation;
it returns interaction intents and a provenance-bearing trace rather than performing I/O.

Join completion sequence is retained only when the canonical policy declares completion order observable;
validation rejects a completion-order tie-break paired with unobservable order. Inbound Request obligations are
linear: an obligation visible before a Fork cannot be consumed by a Reply inside that parallel region, and a Reply
discharges the logical obligation across every token and retained Fork parent so it cannot be duplicated or
resurrected by a later Join.

The reference continuation retains the complete token set, typed token-local bindings and Request obligations,
Fork/Join membership and branch dispositions, computed timer deadlines, active waits and tombstones, early inputs,
input-disposition receipts, outstanding logical Requests, terminal outcome, and exact Process fingerprint. It is
the semantic input to ARI-166, not a claim of physical durability. The synchronous host port supplies explicit
Transition, Relation/Query, and Signal-target evidence, while cancellation is observed only at an activation safe
point; no `CancellationToken`, task, repository, clock, or provider type enters canonical state.

Presented inputs are grouped by logical emission identity before state mutation, so conflicting same-batch evidence
cannot acquire caller-order authority. Cancellation-bearing activations admit their input evidence before applying
cancellation at the entry safe point, and every token-terminal path dispositions remaining buffered inputs instead
of retaining impossible `Buffered` state. A `RestartAttempt` recovery never resumes the abandoned continuation;
`ProcessReferenceInterpreter.RestartAttempt` creates a clean token set under a controller-supplied replacement
attempt identity while retaining the exact Process definition and invocation input.

One major model limit remains explicit. Process IR v1 has no canonical scope or guarantee-demand construct, so
EK-05 atomic multi-entity capability rejection cannot yet be expressed honestly. Interaction targets now carry an
optional exact `ProcessWaitRegistrationId`: exact targets cannot cross wait occurrences, while a null occurrence is
the explicit early-delivery form. ARI-166 supplies physical checkpoint and inbox/outbox realization; ARI-168 owns
the durable runtime driver, and ARI-170 owns restricted C# lowering and equivalence with direct IR. The legacy
Process runtime remains a separate compatibility path.

## Compatibility surfaces to migrate

### Flat transitions

`Cohesive.Transitions.Model.TransitionDefinition` is a legacy serialized set of parallel collections: `Inputs`, `Preconditions`, `Updates`, and `Effects`. The runtime applies preconditions, sequential assignments, computed fields, invariants, and then every declared effect. Conditional expressions exist inside those collections, but there is no structured body containing branch nodes or stable path identity. Static analysis unions referenced fields; it cannot report must/may/actual access or branch provenance.

Canonical persisted semantic authority now belongs to `Cohesive.Transitions.IR`. `TransitionAuthoring` and its typed canonical builders are producers of that authority and retain no executable callback. Keep the legacy `Transition<TEntity, TInput>`, `TransitionExpressionBuilder`, and `DeclarativeEntityRuntime` only as temporary compatibility producer/interpreter surfaces pending ARI-185 while consumers migrate. Project the legacy `TransitionResult`, generic effects, and dictionary patches from the canonical decision rather than defining kernel behavior through them.

### Delegate-bearing processes

Canonical persisted Process authority now belongs to `Cohesive.Processes.IR.ProcessDefinition`: a normalized,
typed graph with stable node and edge identities, portable expressions, exact Transition, Relation/Query, and
interaction references, explicit Fork/Join and AwaitMatch policies, and durable cuts. Its validator proves exact
reference and binding compatibility, graph integrity, branch/join structure, and finite same-activation execution.
It carries coordination facts only and does not copy aggregate business state, callbacks, suspended host frames,
runtime services, adapter state, or compiled plans.

The compatibility `Cohesive.Processes.Model.ProcessDefinition` still stores executable node objects. Semantic
choices—including branch predicates, entity references, transition inputs, request construction, waits,
computations, and terminal results—are CLR `Func` delegates. DurableTask resolves a definition by process name from
a local registry and re-evaluates those delegates during orchestration replay. A changed definition under the same
name is accepted because checkpoint compatibility validates only `ProcessName`.

Migration disposition: treat the existing definition, builder, and source-generator output as
authoring/compatibility inputs. Effect handlers and transaction gateways remain legitimate adapter mechanisms,
but delegates must cease to be persisted semantic authority. ARI-170 should lower authoring into the existing
canonical typed nodes. ARI-165 pins exact definition identity, revision, and fingerprint on activation evidence;
ARI-166 enforces those values across checkpoint restore and replay admission.

### Single execution cursor

`Cohesive.Processes.Runtime.ProcessCheckpoint` persists one `CurrentNode` plus a locality continuation stack. It has no token set, fork/join state, definition fingerprint, integrated process attempt or activation identity, durable wait inbox, operation ledger, canonical control state, compensation state, or generation-affinity binding. The ARI-166 `ProcessDurableCheckpoint` is the new physical aggregate and composes the canonical continuation, control, interaction, and durable-operation authorities under one atomic store boundary. The legacy checkpoint neither embeds nor atomically commits those authorities. Its `ProcessDefinition` also accepts unrestricted control-flow cycles.

Migration disposition: preserve old checkpoints only behind an explicit compatibility reader. New work targets `ProcessDurableCheckpoint` and `IProcessDurableStore`; ARI-168 should integrate those contracts rather than expand the legacy adapter. Affinity slots and generation bindings must be derived from canonical Process IR and owning-block contracts. Do not infer parallelism or generation recovery from the old single cursor.

## Characterized runtime paths

| Area | Current types and runtime paths |
| --- | --- |
| Canonical transition semantics | `Cohesive.Transitions.IR` structured definitions, validation, and shared execution-definition persistence |
| Canonical Transition C# authoring | `TransitionAuthoring.Create` + `TransitionBuilder<TEntity, TInput, TOutcome>` → canonical `ExecutionDefinitionDocument`; strict unsupported-syntax rejection and `ExecutionSourceMap` attribution |
| Canonical transition compilation | `TransitionStaticCompiler` → `CompiledTransitionPlan`, including path-sensitive requirements, computed-field order, and exact `TransitionMachineEdgeLink` slices |
| Reference transition interpretation | `TransitionReferenceInterpreter.Decide`, `DecideFullState`, and `DecideSparse` → `TransitionDecision` plus `TransitionExecutionEvidence` |
| Canonical transition activation | `ExecutionDefinitionDocument` → `TransitionStaticCompiler` → `TransitionReferenceInterpreter`; no producer assembly or authoring callback is required |
| Canonical interaction contracts | `InteractionContractDefinition`, `InteractionContractDocuments`, and `InteractionContractCatalog` → exact typed domain-event, Request, Signal, and Reply contracts with portable schemas and Request obligations |
| Canonical interaction envelopes | `DomainEventEnvelope`, `RequestEnvelope`, `SignalEnvelope`, and `ReplyEnvelope` → `InteractionEnvelopeValidator` and `InteractionEnvelopeJsonSerializer`; strict portable representation plus optional exact `ProcessWaitRegistrationId` targeting exists, and `ProcessDurableCheckpoint` retains envelopes as the inbox/outbox authority |
| Canonical durable Request protocol | `DurableRequestBinding`, `DurableOperationState`, `IDurableOperationAdapter`, `IDurableOperationBatchAdapter`, and `DurableOperationReferenceExecutor` → exact Reply binding, scoped logical deduplication, fenced claim/renewal, attempt/failure evidence, typed timeout/cancellation, recovery identities, acknowledgement, physical-batch item evidence, reconciliation/escalation, and result admission as deterministic reference state; `ProcessDurableCheckpoint` and `IProcessDurableStore` now atomically compose the physical ledger, while runtime dispatch/recovery integration remains ARI-168 work |
| Canonical Process lifecycle control | `ProcessControlCommand`, `ProcessControlState`, `ProcessControlDecision`, `ProcessControlJsonSerializer`, and `ProcessControlReferenceExecutor` → protocol-neutral Inspect/Signal/Pause/Continue/RestartAttempt/Cancel/Terminate, stable command identity and idempotency, exact attempt/revision fencing, replay receipts, strict canonical wires, safe-point deferral, attempt lineage, canonical Signal admission, and write-once generic attempt affinity; `ProcessDurableCheckpoint` now composes control with physical CAS/inbox/worker-fence state, while ARI-168 and index-sync work own runtime integration and generation lifecycle |
| Canonical Process semantics | `Cohesive.Processes.IR.ProcessDefinition`, `ProcessStaticCompiler`, `ProcessContinuationState`, `ProcessReferenceInterpreter`, `ProcessContinuationValidator`, `ProcessDurableCheckpoint`, and `IProcessDurableStore` → validated exact finite-activation plan, immutable multi-token continuation, deterministic operations/Fork/Join/waits/Requests/interactions, explicit durable cuts, input arbitration and dispositions, restored-state diagnostics, atomic checkpoint/inbox/outbox persistence, and crash-testable CAS/fencing; production runtime integration remains ARI-168 work |
| Legacy direct transition activation | `Transition<TEntity,TInput>.Apply` → `Entity.ApplyTransition` → `DeclarativeEntityRuntime.Apply` |
| Flat transition compatibility | `Cohesive.Transitions.Model.TransitionDefinition`, `TransitionBuilder`, `TransitionExpressionBuilder`, `TransitionExpressionAnalyzer`, `TransitionPatchProjector`, `TransitionResult` |
| Legacy Process planning and replay | `Cohesive.Processes.Model.ProcessDefinition`, `ProcessNode`, `BranchingNode`, `ProcessExecutionPlanner`, `ProcessCheckpoint` |
| Legacy waits and signals | `WaitNode`, `IProcessWaitAdapter`, `IProcessSignalSink`, `InMemoryProcessWaitAdapter`, `DurableTaskProcessOrchestration`; these still exchange a key plus raw payload rather than canonical Signal envelopes |
| Legacy effects and recovery substrate | `EffectRequest`, `ProcessPendingEffect`, `EffectExecution`, `ProcessDeadLetter`, `ProcessNodeExecutor`, `ProcessEntityRepositoryAdapter`; these remain runtime migration surfaces and do not yet enforce canonical Request/Reply obligations |
| Transactions and capabilities | `ProcessTransactionScope`, `ProcessPlace`, `ProcessCapability`, `IProcessTransactionGateway` |

## Characterization policy

These tests intentionally lock observable legacy behavior—including unsafe gaps such as re-evaluated branch delegates, repeatable checkpoint consumption, duplicate signal buffering, unrestricted cycles, and same-name definition replacement. When a kernel implementation closes a gap, update the classification and replace the legacy assertion with the corresponding normative conformance test. Do not preserve a characterized gap merely to keep this inventory green.
