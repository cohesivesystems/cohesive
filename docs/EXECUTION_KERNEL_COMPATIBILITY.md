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
These protocols are not yet backed by atomic Storage state or integrated with the legacy Process runtime, so they
do not make EK-06 or EK-08 system-runtime Passes. Canonical finite Process IR now provides the persisted semantic
graph, typed bindings, exact references, durable boundaries, and static validation needed by subsequent checkpoint
and runtime work. EK-09 remains Partial: representative Transitions have a typed C# producer that is equivalent to
direct IR, while Processes have direct canonical IR but no C# lowering yet. The remaining scenarios retain the
Partial classifications recorded below.

## Scenario matrix

| Scenario | Status | Current compatibility | Missing kernel semantics |
| --- | --- | --- | --- |
| EK-01 — structured DQ branching | Pass | `Cohesive.Transitions.IR` provides canonical persisted structured definitions with stable nodes, typed contracts and outcomes, ordered branching and matching, algebraic sparse patches, exact interaction-contract emission references, and fingerprint-bound Machine-edge references. `TransitionStaticCompiler` performs target-independent type, flow, exhaustiveness, access/effect, derived-field, invariant, and Machine-link analysis. `TransitionReferenceInterpreter` executes either complete state or finite sparse observations through one deterministic core and returns typed outcomes, committable patches, emission intents, Machine movements, guarantee demands, conflicts, diagnostics, and ordered actual-execution evidence. | None within the EK-01 reference decision. Observation acquisition and authoritative commit remain explicit external interpretations of the returned demands and intents. |
| EK-02 — durable human review | Partial | Canonical Request, Signal, and Reply contracts declare portable payloads, exact semantic continuation addresses, and typed timeout or cancellation outcomes. Canonical Process IR adds a closed typed `AwaitMatch` union with exact interaction references, guarded clauses, absolute timers, stable clause and continuation identities, explicit priority arbitration, and late/stale/duplicate/missing-target policy. The durable-operation reference protocol can classify and disposition Request results at an exact Process-token or Transition-continuation target. | Durable wait registration plus timer arming, early inbox admission and buffering, atomic claim/consume and loser invalidation, runtime arbitration, and integration of canonical result admission with Process/Transition commits. |
| EK-03 — vendor/manual fulfillment | Partial | `DurableRequestBinding` refines an exact Request with bounded attempts, timeout, idempotency evidence, exact Reply mappings, and reconciliation/escalation paths. The reference protocol retains stable Request identity, records attempt and acknowledgement evidence, and applies authored late/stale/duplicate policies. Typed legacy handlers, continuation freshness checks, and dead-lettering remain separate compatibility paths. | Vendor/manual provider arbitration, adapter integration with current Process execution, and physical persistence that atomically couples acknowledgement and target admission to the owning continuation. |
| EK-04 — parallel gates and join recovery | Partial | Canonical Process IR declares stable Fork branches and reciprocal Joins with explicit threshold, branch-failure, remaining-branch cancellation, completion-order observability, and deterministic tie-break policy. Static validation rejects malformed ownership and convergence. The legacy checkpoint still has one `CurrentNode` and a LIFO locality continuation stack. | Multi-token checkpoint state, parallel scheduling, durable per-branch progress, atomic join completion, order-independent recovery, and duplicate-work prevention. |
| EK-05 — capability-safe multi-entity coordination | Partial | `ProcessTransactionScope` can name multi-entity/database scopes, and places expose a coarse transaction capability. | Requirement extraction, adapter capability evidence, proof-directed guarantee matching, independent subject authority, and mandatory authored compensation when atomic scope is unavailable. |
| EK-06 — durable effect crash matrix | Partial | The canonical reference protocol uses the Request `EmissionId` as logical operation identity, derives a scoped deduplication key, and models leased claim/renewal, monotonic fences, ordered attempt snapshots, explicit failure phase/effect evidence, bounded retry, timeout/cancellation, fenced reconciliation and escalation identities, one durable acknowledgement, physical batches with complete per-item evidence, and a separate target admission. Its state transitions represent all three crash cuts without promising physical exactly-once execution. | ARI-166 Storage realization of atomic origin commit/outbox publication, compare-and-swap claims and fences, durable operation-ledger state, acknowledgement persistence, inbox admission, and atomic Process-checkpoint or Transition-commit coupling. Production adapter conformance and integrated crash tests remain required. |
| EK-07 — signal arbitration | Partial | Canonical Signal contracts and envelopes provide typed portable payloads, stable emission and idempotency identity, explicit targets, ordering, and provenance. The current DurableTask and local runtimes still accept raw keyed signals and buffer them FIFO. | Durable admission receipts, inbox deduplication, exclusive winner claims, duplicate prior-result behavior, observable losers, and enforcement preventing late or stale signals from reopening a choice. |
| EK-08 — index rebuild recovery | Partial | `ProcessControlState` retains stable Process instance, attempt, and activation identity, invariant-preserving safe points, ordered attempt lineage, and write-once generic attempt affinities. `ProcessControlReferenceExecutor` makes pause/continue retain the current attempt and affinities, while `RestartAttempt` explicitly abandons the old attempt and starts a stable replacement without inherited affinities. | ARI-166/ARI-168 durable checkpoint and compare-and-swap realization; Storage-owned candidate-generation allocation, binding, cleanup, and abandoned-generation exclusion; retry/recovery integration that retains the attempt and generation; and fenced, idempotent generation promotion plus read/write backend swap. |
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

This is a reference protocol and conformance substrate, not a hidden in-memory claim that durability already
exists. The reference executor does not own a repository and deliberately leaves every durable cut to its caller.
The v1 state schema identifies the portable reference value; it is not a Storage operation-ledger or checkpoint
wire contract, and ARI-161 adds no bespoke persistence serializer that could imply otherwise.
ARI-166 owns physical checkpoint, inbox/outbox, operation-ledger, lease/fence, and atomic commit realization in
Cohesive.Storage. Until that realization and Process/Transition integration exist, EK-06 remains **Partial**.

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

Signal commands wrap an already-canonical `SignalEnvelope`. Exact contract and target validation precede admission;
active attempts admit Signals for arbitration, paused or pausing attempts buffer them, and retiring or terminal
attempts reject them. Emission and scoped contract/idempotency identity prevent a replayed logical Signal from
creating another admission. The control protocol records only admission evidence and an external realization
intent—it does not yet supply the durable inbox or winner-claim semantics required to make EK-07 Pass.

`ProcessControlJsonSerializer` supplies strict canonical command, state, and versioned decision wires. Catalog-aware
reads link Signals and validate named reason details and attempt-affinity values through the catalog's retained shape
graph. First-time decision intents are admissible only at their exact latest receipt or observation cut; a later
state can retain the receipt for replay without being able to present it again as a fresh side-effecting result.

`ProcessAttemptAffinity` is deliberately generic and write-once. An index-sync Process can use a stable semantic
slot to bind its current attempt to a concrete candidate-generation value, so pause/continue naturally retain that
generation and restart naturally requires a fresh binding. Cohesive.Storage remains the authority for allocating,
persisting, cleaning up, excluding, and promoting physical index generations. The reference executor does not own
a checkpoint repository, atomically persist receipts or inbox entries, fence workers, allocate generations, or
perform promotion. ARI-166 and ARI-168 own those physical cuts and runtime integration; fenced idempotent generation
promotion and backend swap remain subsequent Storage/index-sync work. Consequently, EK-08 is **Partial**.

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

This IR is not yet an execution claim. ARI-165 owns the reference interpreter and multi-token semantics, ARI-166
owns durable checkpoint and inbox/outbox realization, and ARI-170 owns restricted C# lowering and equivalence with
direct IR. The legacy Process runtime remains characterized separately until those paths consume the canonical
definition and exact fingerprint.

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
canonical typed nodes; ARI-165/ARI-166 should pin the exact definition fingerprint on activation and replay.

### Single execution cursor

`Cohesive.Processes.Runtime.ProcessCheckpoint` persists one `CurrentNode` plus a locality continuation stack. It has no token set, fork/join state, definition fingerprint, integrated process attempt or activation identity, durable wait inbox, operation ledger, canonical control state, compensation state, or generation-affinity binding. The separate ARI-162 `ProcessControlState` now represents attempt/activation-aware lifecycle control and generic affinity semantically, but the legacy checkpoint neither embeds nor atomically commits it. `ProcessDefinition` also accepts unrestricted control-flow cycles.

Migration disposition: preserve old checkpoints only behind an explicit compatibility reader. New kernel checkpoints should be versioned envelopes whose token set and wait/operation ledgers compose atomically with canonical Process control state; affinity slots and generation bindings must be derived from canonical Process IR and owning-block contracts. Do not infer parallelism or generation recovery from the old single cursor.

## Characterized runtime paths

| Area | Current types and runtime paths |
| --- | --- |
| Canonical transition semantics | `Cohesive.Transitions.IR` structured definitions, validation, and shared execution-definition persistence |
| Canonical Transition C# authoring | `TransitionAuthoring.Create` + `TransitionBuilder<TEntity, TInput, TOutcome>` → canonical `ExecutionDefinitionDocument`; strict unsupported-syntax rejection and `ExecutionSourceMap` attribution |
| Canonical transition compilation | `TransitionStaticCompiler` → `CompiledTransitionPlan`, including path-sensitive requirements, computed-field order, and exact `TransitionMachineEdgeLink` slices |
| Reference transition interpretation | `TransitionReferenceInterpreter.Decide`, `DecideFullState`, and `DecideSparse` → `TransitionDecision` plus `TransitionExecutionEvidence` |
| Canonical transition activation | `ExecutionDefinitionDocument` → `TransitionStaticCompiler` → `TransitionReferenceInterpreter`; no producer assembly or authoring callback is required |
| Canonical interaction contracts | `InteractionContractDefinition`, `InteractionContractDocuments`, and `InteractionContractCatalog` → exact typed domain-event, Request, Signal, and Reply contracts with portable schemas and Request obligations |
| Canonical interaction envelopes | `DomainEventEnvelope`, `RequestEnvelope`, `SignalEnvelope`, and `ReplyEnvelope` → `InteractionEnvelopeValidator` and `InteractionEnvelopeJsonSerializer`; strict portable representation exists, but current Process and Storage runtimes do not yet use it as their durable ledger/inbox/outbox contract |
| Canonical durable Request protocol | `DurableRequestBinding`, `DurableOperationState`, `IDurableOperationAdapter`, `IDurableOperationBatchAdapter`, and `DurableOperationReferenceExecutor` → exact Reply binding, scoped logical deduplication, fenced claim/renewal, attempt/failure evidence, typed timeout/cancellation, recovery identities, acknowledgement, physical-batch item evidence, reconciliation/escalation, and result admission as deterministic reference state; physical persistence and atomic cuts remain deferred to ARI-166 |
| Canonical Process lifecycle control | `ProcessControlCommand`, `ProcessControlState`, `ProcessControlDecision`, `ProcessControlJsonSerializer`, and `ProcessControlReferenceExecutor` → protocol-neutral Inspect/Signal/Pause/Continue/RestartAttempt/Cancel/Terminate, stable command identity and idempotency, exact attempt/revision fencing, replay receipts, strict canonical wires, safe-point deferral, attempt lineage, canonical Signal admission, and write-once generic attempt affinity; physical checkpoint/CAS/inbox/worker-fence realization and Storage-owned index-generation lifecycle remain deferred to ARI-166/ARI-168 and index-sync work |
| Canonical Process semantics | `Cohesive.Processes.IR.ProcessDefinition`, its closed node and AwaitMatch unions, `ProcessDefinitionValidator`, and `ProcessDefinitionDocuments` → normalized finite-activation graph, typed bindings and expressions, exact semantic linking, explicit durable boundaries, deterministic validation, and shared execution-definition fingerprints; execution and checkpoint realization remain deferred to ARI-165/ARI-166 |
| Legacy direct transition activation | `Transition<TEntity,TInput>.Apply` → `Entity.ApplyTransition` → `DeclarativeEntityRuntime.Apply` |
| Flat transition compatibility | `Cohesive.Transitions.Model.TransitionDefinition`, `TransitionBuilder`, `TransitionExpressionBuilder`, `TransitionExpressionAnalyzer`, `TransitionPatchProjector`, `TransitionResult` |
| Legacy Process planning and replay | `Cohesive.Processes.Model.ProcessDefinition`, `ProcessNode`, `BranchingNode`, `ProcessExecutionPlanner`, `ProcessCheckpoint` |
| Legacy waits and signals | `WaitNode`, `IProcessWaitAdapter`, `IProcessSignalSink`, `InMemoryProcessWaitAdapter`, `DurableTaskProcessOrchestration`; these still exchange a key plus raw payload rather than canonical Signal envelopes |
| Legacy effects and recovery substrate | `EffectRequest`, `ProcessPendingEffect`, `EffectExecution`, `ProcessDeadLetter`, `ProcessNodeExecutor`, `ProcessEntityRepositoryAdapter`; these remain runtime migration surfaces and do not yet enforce canonical Request/Reply obligations |
| Transactions and capabilities | `ProcessTransactionScope`, `ProcessPlace`, `ProcessCapability`, `IProcessTransactionGateway` |

## Characterization policy

These tests intentionally lock observable legacy behavior—including unsafe gaps such as re-evaluated branch delegates, repeatable checkpoint consumption, duplicate signal buffering, unrestricted cycles, and same-name definition replacement. When a kernel implementation closes a gap, update the classification and replace the legacy assertion with the corresponding normative conformance test. Do not preserve a characterized gap merely to keep this inventory green.
