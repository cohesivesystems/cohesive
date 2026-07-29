# Execution Kernel Compatibility Inventory

This inventory records the compatibility behavior characterized by ARI-153 and the kernel substrate introduced since then against the normative EK-01 through EK-09 scenarios in the [Cohesive Execution Kernel Specification](https://app.notion.com/p/3ab8cf7881f981f78ef1e34d7a907c70). It is a migration baseline, not an alternative semantic contract. Missing behavior remains required by the specification.

Status meanings:

- **Pass**: the current model and runtime satisfy the scenario's normative guarantees.
- **Partial**: useful substrate or observable behavior exists, but one or more normative guarantees are missing.
- **Absent**: the scenario's core semantic construct has no current representation.

EK-01 now passes through the canonical Transition compilation and reference-interpretation path. Canonical
interaction contracts and runtime envelopes provide the shared event, Request, Signal, and Reply vocabulary, and a
canonical durable-operation reference protocol now interprets Request attempts, acknowledgement, reconciliation,
and result admission. The protocol is not yet backed by an atomic Storage operation ledger or integrated with the
legacy Process runtime, so it does not make EK-06 a system-runtime Pass. EK-09 is Partial:
representative Transitions have a typed C# producer that is equivalent to direct IR, while Processes still lack a
canonical lowering. The remaining scenarios retain the Partial or Absent classifications recorded below.

## Scenario matrix

| Scenario | Status | Current compatibility | Missing kernel semantics |
| --- | --- | --- | --- |
| EK-01 — structured DQ branching | Pass | `Cohesive.Transitions.IR` provides canonical persisted structured definitions with stable nodes, typed contracts and outcomes, ordered branching and matching, algebraic sparse patches, exact interaction-contract emission references, and fingerprint-bound Machine-edge references. `TransitionStaticCompiler` performs target-independent type, flow, exhaustiveness, access/effect, derived-field, invariant, and Machine-link analysis. `TransitionReferenceInterpreter` executes either complete state or finite sparse observations through one deterministic core and returns typed outcomes, committable patches, emission intents, Machine movements, guarantee demands, conflicts, diagnostics, and ordered actual-execution evidence. | None within the EK-01 reference decision. Observation acquisition and authoritative commit remain explicit external interpretations of the returned demands and intents. |
| EK-02 — durable human review | Partial | Canonical Request, Signal, and Reply contracts declare portable payloads, exact semantic continuation addresses, and typed timeout or cancellation outcomes. The durable-operation reference protocol can classify and disposition eligible, duplicate, late, or stale Request results at an exact Process-token or Transition-continuation target. `WaitNode` still produces the legacy `Waiting` checkpoint, while local and DurableTask runtimes buffer early keyed inputs and DurableTask supplies timers. | Closed `AwaitMatch`, definition/node linking, durable wait registration plus timer arming, signal inbox claim/consume and arbitration, and integration of canonical result admission with Process/Transition commits. |
| EK-03 — vendor/manual fulfillment | Partial | `DurableRequestBinding` refines an exact Request with bounded attempts, timeout, idempotency evidence, exact Reply mappings, and reconciliation/escalation paths. The reference protocol retains stable Request identity, records attempt and acknowledgement evidence, and applies authored late/stale/duplicate policies. Typed legacy handlers, continuation freshness checks, and dead-lettering remain separate compatibility paths. | Vendor/manual provider arbitration, adapter integration with current Process execution, and physical persistence that atomically couples acknowledgement and target admission to the owning continuation. |
| EK-04 — parallel gates and join recovery | Absent | A checkpoint has one `CurrentNode` and a LIFO locality continuation stack. Transition batches execute sequentially. | Fork tokens, parallel scheduling, join policy, durable per-branch progress, order-independent recovery, and duplicate-work prevention. |
| EK-05 — capability-safe multi-entity coordination | Partial | `ProcessTransactionScope` can name multi-entity/database scopes, and places expose a coarse transaction capability. | Requirement extraction, adapter capability evidence, proof-directed guarantee matching, independent subject authority, and mandatory authored compensation when atomic scope is unavailable. |
| EK-06 — durable effect crash matrix | Partial | The canonical reference protocol uses the Request `EmissionId` as logical operation identity, derives a scoped deduplication key, and models leased claim/renewal, monotonic fences, ordered attempt snapshots, explicit failure phase/effect evidence, bounded retry, timeout/cancellation, fenced reconciliation and escalation identities, one durable acknowledgement, physical batches with complete per-item evidence, and a separate target admission. Its state transitions represent all three crash cuts without promising physical exactly-once execution. | ARI-166 Storage realization of atomic origin commit/outbox publication, compare-and-swap claims and fences, durable operation-ledger state, acknowledgement persistence, inbox admission, and atomic Process-checkpoint or Transition-commit coupling. Production adapter conformance and integrated crash tests remain required. |
| EK-07 — signal arbitration | Partial | Canonical Signal contracts and envelopes provide typed portable payloads, stable emission and idempotency identity, explicit targets, ordering, and provenance. The current DurableTask and local runtimes still accept raw keyed signals and buffer them FIFO. | Durable admission receipts, inbox deduplication, exclusive winner claims, duplicate prior-result behavior, observable losers, and enforcement preventing late or stale signals from reopening a choice. |
| EK-08 — index rebuild recovery | Absent | No generation-affine recovery behavior exists in Transitions or Processes. | Process attempt and activation identity; candidate generation affinity; pause/continue retaining the generation; retry policy; restart creating a fresh generation; abandoned-generation exclusion; fenced, idempotent promotion. |
| EK-09 — C# and IR equivalence | Partial | Representative typed C# Transition authoring lowers immediately to the same canonical `Cohesive.Transitions.IR` definitions as direct authoring, with explicit stable definition/revision/node/binding identities, typed contracts, deterministic normalization and fingerprints, strict document round trips, and fingerprint-excluded source maps that reconnect canonical diagnostics to C# call sites. The typed handle retains only the canonical document and validation result, so deserialization and interpretation do not require the producer assembly or callbacks. | `Cohesive.Processes` still persists delegate-bearing executable node objects and has no canonical C#-to-IR lowering or C#/direct-IR equivalence suite. Transition support is intentionally a restricted portable C# subset; broader representative coverage and consumer migration remain follow-on work, but unsupported CLR computation is rejected rather than persisted. |

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

## Compatibility surfaces to migrate

### Flat transitions

`Cohesive.Transitions.Model.TransitionDefinition` is a legacy serialized set of parallel collections: `Inputs`, `Preconditions`, `Updates`, and `Effects`. The runtime applies preconditions, sequential assignments, computed fields, invariants, and then every declared effect. Conditional expressions exist inside those collections, but there is no structured body containing branch nodes or stable path identity. Static analysis unions referenced fields; it cannot report must/may/actual access or branch provenance.

Canonical persisted semantic authority now belongs to `Cohesive.Transitions.IR`. `TransitionAuthoring` and its typed canonical builders are producers of that authority and retain no executable callback. Keep the legacy `Transition<TEntity, TInput>`, `TransitionExpressionBuilder`, and `DeclarativeEntityRuntime` only as temporary compatibility producer/interpreter surfaces pending ARI-185 while consumers migrate. Project the legacy `TransitionResult`, generic effects, and dictionary patches from the canonical decision rather than defining kernel behavior through them.

### Delegate-bearing processes

`Cohesive.Processes.Model.ProcessDefinition` stores executable node objects. Semantic choices—including branch predicates, entity references, transition inputs, request construction, waits, computations, and terminal results—are CLR `Func` delegates. DurableTask resolves a definition by process name from a local registry and re-evaluates those delegates during orchestration replay. A changed definition under the same name is accepted because checkpoint compatibility validates only `ProcessName`.

Migration disposition: treat the existing definition, builder, and source-generator output as authoring/compatibility inputs. Effect handlers and transaction gateways remain legitimate adapter mechanisms, but delegates must cease to be persisted semantic authority. Lower authoring into canonical typed nodes and require a definition fingerprint on activation and replay.

### Single execution cursor

`Cohesive.Processes.Runtime.ProcessCheckpoint` persists one `CurrentNode` plus a locality continuation stack. It has no token set, fork/join state, definition fingerprint, process attempt or activation identity, durable wait inbox, operation ledger, control state, compensation state, or index-generation affinity. `ProcessDefinition` also accepts unrestricted control-flow cycles.

Migration disposition: preserve old checkpoints only behind an explicit compatibility reader. New kernel checkpoints should be versioned envelopes whose token set, wait/operation ledgers, control state, and generation binding are derived from canonical Process IR. Do not infer parallelism or generation recovery from the old single cursor.

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
| Legacy direct transition activation | `Transition<TEntity,TInput>.Apply` → `Entity.ApplyTransition` → `DeclarativeEntityRuntime.Apply` |
| Flat transition compatibility | `Cohesive.Transitions.Model.TransitionDefinition`, `TransitionBuilder`, `TransitionExpressionBuilder`, `TransitionExpressionAnalyzer`, `TransitionPatchProjector`, `TransitionResult` |
| Process planning and replay | `ProcessDefinition`, `ProcessNode`, `BranchingNode`, `ProcessExecutionPlanner`, `ProcessCheckpoint` |
| Legacy waits and signals | `WaitNode`, `IProcessWaitAdapter`, `IProcessSignalSink`, `InMemoryProcessWaitAdapter`, `DurableTaskProcessOrchestration`; these still exchange a key plus raw payload rather than canonical Signal envelopes |
| Legacy effects and recovery substrate | `EffectRequest`, `ProcessPendingEffect`, `EffectExecution`, `ProcessDeadLetter`, `ProcessNodeExecutor`, `ProcessEntityRepositoryAdapter`; these remain runtime migration surfaces and do not yet enforce canonical Request/Reply obligations |
| Transactions and capabilities | `ProcessTransactionScope`, `ProcessPlace`, `ProcessCapability`, `IProcessTransactionGateway` |

## Characterization policy

These tests intentionally lock observable legacy behavior—including unsafe gaps such as re-evaluated branch delegates, repeatable checkpoint consumption, duplicate signal buffering, unrestricted cycles, and same-name definition replacement. When a kernel implementation closes a gap, update the classification and replace the legacy assertion with the corresponding normative conformance test. Do not preserve a characterized gap merely to keep this inventory green.
