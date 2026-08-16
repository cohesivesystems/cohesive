---
kind: decision
status: accepted
authority: cohesive.processes.interpreters.durable-task
owners: [cohesive-core]
applies_to: [cohesive-processes, cohesive-adapters-durable-task]
last_verified: 2026-08-10
supersedes: []
---

# Interpret Canonical Processes Through Azure Durable Task

## Decision

Azure Durable Task Scheduler is a first-class, parallel durable interpreter of canonical
`Cohesive.Processes` definitions. The interpreter consumes an exact `CompiledProcessPlan`; it does
not author another workflow definition, revive runtime delegates, or reduce the Process to one
opaque activity.

The canonical `ExecutionDefinitionDocument`, its exact definition identity, revision and
fingerprint, and the resulting `CompiledProcessPlan` remain semantic authority. Durable Task owns
physical scheduling, durable history, replay and worker dispatch for this target profile. Its
history is attributable physical execution evidence, not a competing definition of Process
meaning.

The Durable Task interpreter is not required to implement `IProcessDurableStore`.
`ProcessDurableRuntime` plus `IProcessDurableStore` remains the native Storage-owned durable
interpretation. Both interpretations must preserve the same canonical semantics inside their
declared capability closures and must fail before execution when they cannot.

## Context

The retired Durable Task adapter executed callback-bearing definitions through one current-node
cursor and resolved definitions by name from a process-local registry. That model could not retain
the canonical Process definition fingerprint, multi-token continuation, wait and inbox evidence,
operation ledger, lifecycle control, child protocol, or attempt and activation lineage. It was
removed rather than treated as a compatibility authority.

The current adapter exposes canonical status through the standalone task-hub client and retains a separate
historical query-client path for records created by the retired adapter. The accepted target uses the standalone
Microsoft Durable Task SDK and Azure Durable Task Scheduler. It should leverage
native orchestrations, activities, durable timers, external events, sub-orchestrations, orchestration
versioning, lifecycle APIs, tags, custom status and the Scheduler dashboard whenever those facilities
preserve the requested canonical semantics.

## Authority and state boundaries

| Concern | Authority in the Durable Task profile |
| --- | --- |
| Process meaning | Canonical `ExecutionDefinitionDocument` and exact `CompiledProcessPlan` |
| Definition compatibility | Canonical definition identity, revision, fingerprint and Process IR schema compatibility |
| Finite decisions | Canonical Process interpreter semantics and normalized decision evidence |
| Lifecycle control | Canonical `ProcessControlState`, `ProcessControlReferenceExecutor`, command receipts and intents |
| Physical scheduling and replay | Exact Durable Task orchestration history for the selected target profile |
| Aggregate state changes | The invoked canonical Transition and its authoritative entity adapter |
| Relation and Query results | The invoked canonical definition and its selected evaluator |
| External obligations | Canonical interaction contracts, envelopes and durable Request recovery policy |
| Operational display | Canonical status, trace and explain projections; Durable Task tags, custom status and dashboard are measured projections |

An adjunct content-addressed definition or payload store may be used when target payload limits
require it. Such a store must be addressed by exact fingerprint and cannot become latest-by-name
definition authority or a second Process checkpoint.

## Interpreter shape

One generic Durable Task orchestration hosts the canonical interpreter. Start admission resolves and
pins the exact definition before physical execution. Orchestrator replay must not perform ambient I/O
or resolve a mutable latest definition. Pure Process decisions execute deterministically inside the
orchestrator; target or domain I/O crosses an attributable activity or external-event boundary and
returns exact portable evidence to the interpreter.

Lifecycle commands use one versioned Durable Task external-event stream, but the event and Scheduler instance are
transport rather than control authority. The orchestration encloses each finite activation with canonical
`BeginActivation` and `ReachSafePoint` observations and realizes only the resulting canonical intent. Native
Scheduler suspend or terminate operations are not substituted for Pause or Terminate because doing so would lose
the exact authorization, expectation, receipt, safe-point, attempt-lineage, and cleanup evidence. Provider event
admission is therefore distinct from canonical command admission, which is observed through custom status or the
final result.

The adapter may optimize this shape only when the optimization retains source-node provenance and
passes the same semantic conformance cases. Generated or hand-authored workflow code per Process is
not an independent authority and is not the default realization.

## Canonical construct inventory

This inventory is defined by the closed `ProcessNode` union in
`Cohesive.Processes.IR.ProcessNodes`. The dispositions below are accepted implementation direction,
not claims about the currently shipped adapter.

`Native` means a Durable Task or deterministic orchestrator facility directly preserves the
construct. `Composed` combines Durable Task facilities with canonical interpreter evidence.
`Constrained` preserves the construct only inside explicit validated operating boundaries.
`Unavailable` requires rejection for the target profile.

| Canonical construct | Required semantics | Intended Durable Task realization | Initial disposition |
| --- | --- | --- | --- |
| `InvokeTransitionProcessNode` | Exact linked definition, subject, input, outcome, transition receipt and emissions | Activity-bound Transition adapter; feed the exact decision and receipt back into canonical interpretation | Composed |
| `EvaluateRelationProcessNode` | Exact linked Relation/Query, input, completeness, result and occurrence replay | Activity-bound evaluator with canonical result and occurrence evidence | Composed |
| `RequestProcessNode` | Typed obligation, terminal outcomes, stable emission identity, recovery and Reply admission | Activity dispatch plus canonical durable Request state and outcome arbitration | Composed |
| `EmitEventProcessNode` | Typed envelope, producer occurrence, ordering and publication obligation | Unchanged canonical envelope through a durable after-origin activity; target deduplication by authority, exact contract, and canonical idempotency identity | Constrained |
| `SendSignalProcessNode` | Typed target, envelope, correlation and delivery policy | Durable Task event or adapter dispatch plus canonical Signal evidence | Composed |
| `ChoiceProcessNode` | Ordered predicates, selection policy, coverage and fallback | Deterministic canonical evaluation inside the orchestrator | Native |
| `MatchProcessNode` | Typed patterns, ordered selection, coverage and fallback | Deterministic canonical evaluation inside the orchestrator | Native |
| `ForkProcessNode` | Stable branch tokens, admission limits, capacity domains and cancellation policy | Durable Task task scheduling plus canonical fork membership and branch evidence | Composed |
| `JoinProcessNode` | Exact fork ownership, completion policy, selected branches and cancellation | Durable Task task arbitration plus canonical join decision and lineage | Composed |
| `AwaitMatchProcessNode` | Typed clauses, priority/tie-break, wait registration, retention and early/late/stale/duplicate/missing-target disposition | External events and durable timers admitted through canonical inbox and arbitration semantics | Composed |
| `TimerProcessNode` | Absolute due instant, stable timer occurrence and durable resumption | Native durable timer plus canonical timer identity and continuation evidence | Composed |
| `ReplyProcessNode` | Exact inbound Request discharge, typed Reply and correlation | Reply envelope publication or child completion plus canonical obligation evidence | Composed |
| `DurableCutProcessNode` | End one finite activation and resume at the exact canonical edge | Durable Task history boundary plus canonical continuation and activation evidence | Composed |
| `InvokeProcessProcessNode` | Exact child definition, start/join protocol, purpose, outcome mapping and cancellation | Sub-orchestration plus canonical child Request/Reply protocol and identity derivation | Composed |
| `ForEachPartitionProcessNode` | Finite partition identity, bounds, capacity, failure and cancellation policy | Bounded sub-orchestration fan-out with validated target and payload limits | Constrained |
| `RepeatAcrossActivationProcessNode` | Durable recurrence, occurrence bound, progress proof, exhausted and stalled outcomes | Durable reactivation or continue-as-new while retaining canonical recurrence evidence | Composed |
| `ReturnProcessNode` | Typed successful terminal result and exact terminal trace | Complete orchestration with canonical result evidence | Native |
| `FailProcessNode` | Typed failed terminal result and exact terminal trace | Fail orchestration with canonical failure evidence | Native |

## Cross-cutting guarantee inventory

Node coverage is necessary but not sufficient. A Durable Task realization ledger must also account
for every applicable guarantee below.

| Guarantee | Required target behavior | Initial disposition |
| --- | --- | --- |
| Exact definition pinning | Bind an instance to canonical kind, schema, definition identity, revision and fingerprint; never resolve mutable latest-by-name state during replay | Composed |
| Stable execution identity | Preserve Process instance, attempt, activation, token, node, occurrence, emission, operation, child and wait-registration identities | Composed |
| Deterministic replay | Keep nondeterministic I/O outside orchestrator decisions and reproduce normalized semantic decisions from retained observations | Composed |
| Input admission and disposition | Retain early, candidate, consumed, buffered, late, stale, duplicate, missing-target, superseded and terminal evidence with authored actions | Composed |
| Lifecycle control | Preserve Inspect, Signal, Pause, Continue, RestartAttempt, Cancel and Terminate meaning, idempotency, fencing and safe points | Composed |
| Durable Request recovery | Preserve claim, dispatch, acknowledgement, Reply admission, timeout, cancellation, retry, ambiguity, reconciliation, escalation and terminal-outcome obligations | Composed |
| External effect delivery | Never claim physical exactly-once delivery; require target idempotency or authored reconciliation for repeatable dispatch | Constrained |
| Fork, join and child lineage | Preserve canonical tokens, branch selection, cancellation, child identity and parent/child outcome attribution | Composed |
| Bounded work and recurrence | Enforce authored item, concurrency, capacity, occurrence and progress bounds before or during execution without truncation | Constrained |
| Definition and worker evolution | Compose canonical definition compatibility with Durable Task orchestration and worker versioning; neither version substitutes for the other | Composed |
| Status, trace and explain | Project normalized canonical artifacts with definition and realization provenance; target dashboard state stays non-authoritative | Composed |
| Sensitive and oversized payloads | Validate target limits; redact or use exact content-addressed references without changing semantic contracts | Constrained |
| Whole-definition multi-resource atomicity | Reject when the Process demands one atomic transaction across boundaries Durable Task cannot commit | Unavailable |
| Arbitrary orchestrator I/O or callbacks | Reject noncanonical host behavior and runtime delegates inside orchestrator replay | Unavailable |
| Unbounded fan-out or recurrence | Reject definitions without the canonical finite bounds required by the target profile | Unavailable |

## Inventory completeness rule

Capability acquisition produces two source inventories: all concrete canonical node kinds present in
the exact compiled plan and all cross-cutting guarantees derived from the plan, linked definitions,
interaction contracts and selected execution policy. The Durable Task target profile declares the
construct kinds and guarantees it can interpret. Planning produces one disposition ledger entry for
every inventory item.

An inventory item absent from the disposition ledger is an error. An unknown canonical node kind is
an error. An adapter cannot claim conformance merely because its recognizer never emitted a
requirement. Adding a canonical node kind or guarantee must therefore fail inventory-completeness
profile construction and tests until the target-neutral collector and Durable Task profile state its disposition.

The target-neutral inventory, profile, realization ledger, and structured diagnostics are implemented in
`Cohesive.Processes.Compilation`. Construct kinds are projected from the canonical persisted-union metadata instead
of another enum. `Cohesive.Adapters.DurableTask` publishes separate versioned planning and executable profiles. Both
must exactly cover the canonical construct and guarantee catalogs. Executable profile v2 constrains domain-event
emission to durable after-origin visibility through an activity and an exact-contract publisher that guarantees
target deduplication by the canonical scoped publication key. Reply discharge and whole-definition multi-resource
atomicity remain unavailable. Only plans compiled successfully against that profile may enter the worker catalog.
There is no independent CLR-type recognizer whose omissions can shrink the qualified protocol.

## Conformance and promotion

The Durable Task profile is promoted construct by construct. A capability may be advertised only
when all of the following evidence exists:

1. positive and negative shared fixtures identify the exact semantic requirement;
2. normalized decisions, continuations, interactions, dispositions, control receipts, terminal
   outcomes, trace and explain evidence are differential-conformant with the reference interpreter;
3. Scheduler-emulator tests cover target construction, serialization and provider limits;
4. crash tests cover boundaries before and after scheduling, activity dispatch, result return,
   event admission, timer firing and terminal completion;
5. replay, duplicate delivery, cancellation and worker-version transitions retain exact identities;
6. unsupported demands fail with stable, source-attributed diagnostics before partial execution;
   and
7. package documentation publishes the implemented closure and known target constraints.

Skipped tests do not confer support. A composed realization must pass the same semantic fixtures as
a native realization.

## Consequences

- ARI can use managed Durable Task infrastructure, local emulation and operational dashboards while
  authoring its workflows only once as canonical Processes.
- Postgres and Cosmos remain valid secondary native `ProcessDurableRuntime` /
  `IProcessDurableStore` profiles rather than dependencies of the Durable Task profile.
- The adapter must maintain an explicit capability plan and more demanding differential tests.
- Some Durable Task facilities have similar names but different semantics; convenience cannot
  justify silently weakening canonical input, retry, control or recovery behavior.
- Target limits such as payload size, history growth, retention and supported deployment topology
  are capability evidence and operating boundaries, not Process semantics.

## Rejected alternatives

- **Restore the callback and single-cursor adapter.** It cannot represent or restore current
  canonical authority.
- **Run the whole Process as one activity.** This hides control flow, waits, recovery and lineage and
  recreates the regression ARI is removing.
- **Require Durable Task to implement `IProcessDurableStore`.** The interface models an atomic
  aggregate store, while Durable Task exposes event-sourced orchestration history. Forcing the
  shape would likely add another store and split physical authority.
- **Author one Durable Task workflow per Process.** Independently maintained target workflow code
  would compete with canonical Process IR and obstruct dynamic or imported definitions.
- **Use Durable Task only as a wake-up service over another Process checkpoint.** This duplicates
  durable progress unless a future composed profile proves a precise, single-authority boundary.

## Implementation status and provenance

This decision remains accepted direction for Durable Task execution. As of 2026-08-11,
`Cohesive.Processes` implements the target-neutral requirement inventory, capability evidence, exhaustive
disposition ledger, structured matching diagnostics, and reference interpreter. `Cohesive.Adapters.DurableTask`
implements historical monitoring, complete planning and executable profiles, and a generic executable slice for Transition,
Relation/Query, Request, Signal send to a Process token, Choice, Match, Fork, Join, AwaitMatch, Timer, child Process,
bounded partition, bounded recurrence, Durable Cut, Return, and Fail constructs. It resolves only exact
definition identity/revision/fingerprint tuples from an immutable worker deployment catalog and uses standalone SDK
activities for bounded host I/O. Signal target resolution uses the existing canonical
`ProcessSignalTargetResolution` and closed `ProcessSignalTargetResult`; the resulting exact `SignalEnvelope` is
routed unchanged as a physical external event, while recipient admission and every disposition remain decisions of
the reference interpreter. Bound Requests reuse the canonical `DurableOperationReferenceExecutor` and ledger
for stable claim, attempt, dispatch, bounded retry, acknowledgement, reconciliation, and Reply-admission semantics.
Because activities are at-least-once, automatic dispatch rejects bindings without target deduplication or natural
idempotency evidence; SDK activity retry does not substitute for the canonical retry policy. Exact typed timeout,
terminal-failure, or escalation evidence that this slice cannot author fails the orchestration closed while retaining
the canonical operation status and recovery intent. Root Inspect, Pause, Continue, RestartAttempt, Cancel, and
Terminate commands are transported through one polymorphic event contract and evaluated by the canonical control
executor. Every ordinary activation carries canonical begin/safe-point evidence; active host work drains before a
deferred action, replacement attempts retain exact lineage, cooperative cancellation produces the canonical terminal
activation, and termination retains its canonical terminal control result instead of invoking the similarly named
physical Scheduler operation. Differential tests cover canonical decisions and evidence; a pinned Scheduler
emulator proves the lifecycle command sequence and replay, completion, bound Request activity dispatch and Reply admission, cross-instance and self-Signal
delivery, child sub-orchestration, recurrence Continue-as-new, authored failure, duplicate start admission, and
worker restart at active host work and at unbound Request, Timer, and AwaitMatch boundaries. Shutdown-attributable
activity cancellation has one adapter-owned physical failure identity and is the only host failure retried by the
orchestrator; it does not author semantic cancellation, failure, or attempt lineage. Restart does not re-invoke an
activity already retained in Scheduler history, change a canonical due instant, or admit an interaction twice.
Re-executed in-flight work remains at-least-once and retains its exact operation occurrence and declared target
idempotency boundary. `ProcessWaitState` and
`ProcessTimerState.DueAtUtc` remain semantic
authority; Durable Task events and timers are replayable physical stimuli. Co-ready AwaitMatch interaction and timer
stimuli enter one canonical activation, where the reference interpreter retains exclusive authority for guard,
priority, tie-break, winner, and input-disposition decisions.

The orchestration publishes the existing protocol-neutral `ExecutionStatus` as custom status at active, safe-point,
waiting, lifecycle, Continue-as-new, and terminal cuts. `ProcessExecutionStatusProjector` is shared by the native
Storage and Durable Task interpretations and derives lifecycle from `ProcessControlState` plus token, wait, progress,
demand, health, and terminal evidence from `ProcessContinuationState` and canonical durable-operation state. The
Durable Task projection always redacts terminal detail and does not copy command receipts, reasons, Signals,
interaction payloads, bindings, operation ledgers, wait keys, input values, or output values. Scheduler custom status
and dashboard history are bounded physical projections, not continuation or control authority.

At each finite activation boundary, the interpreter projects the authoritative `ProcessActivationDecision` through
the shared `ProcessExecutionTraceProjector` and retains the resulting payload-safe `NormalizedExecutionTrace` in the
orchestration result. The result carrier preserves activation order and crosses Continue-as-new boundaries; replay
therefore reproduces the same artifacts without acquiring a second trace model. A projection diagnostic fails the
orchestration instead of omitting the activation. These traces are deliberately not copied into custom status.
Scheduler history describes physical orchestration execution and cannot reconstruct semantic traces that predate
this retention boundary.

Current standalone-client monitoring also implements the provider-neutral `IProcessExecutionTraceRepository`.
It reads a terminal `DurableTaskSequentialProcessResult` internally, validates it against retained start and custom
status affinity, and exposes only `NormalizedExecutionTrace` artifacts plus an exact missing-prefix count. Read state
separates not found, in-progress, available, and terminal-without-artifact outcomes; an empty trace collection alone
never claims completeness. The migration-only Core reader is unsupported because its provider history is not
canonical trace evidence.

Current monitoring retains the start receipt's exact definition reference directly on `ProcessExecutionRecord`,
including the pending admission window before custom status exists. `DurableTaskProcessExecutionExplainRepository`
combines that current repository with the immutable exact deployed plan catalog and returns the shared
`ExecutionExplainArtifact`. It projects static evidence from the already compiled canonical plan, one realization
claim for every source-inventory disposition in the retained target report, safe current state from `ExecutionStatus`,
and at most the latest retained normalized trace for the current attempt. Pending and active observations remain
partial artifacts without fabricated traces. Pre-retention trace prefixes and terminal executions without canonical
results become structured warnings. The repository fails closed when exact plan, definition, instance, attempt,
status, or trace affinity disagrees; Scheduler history is not used to fill an evidence gap.

New schedules also project one versioned immutable Scheduler tag set from the canonical start receipt. It contains
only the logical Process instance ID and the exact definition identity, revision, fingerprint algorithm,
canonicalization, and value. It does not contain authority or tenant, command or idempotency identity, Process input
or output, interaction content, wait keys, failure detail, or mutable lifecycle/location. Each value is checked against
Scheduler's 1,000-byte UTF-8 limit before admission. Recognized partial or conflicting tag sets fail repository reads;
tagless canonical instances created before this projection remain readable. Tags are dashboard discovery metadata,
not semantic authority, and their immutability is why changing Process state remains in canonical custom status.

`DurableTaskProcessExecutionRepository` queries current instances through the standalone `DurableTaskClient`, exposes
the exact `ExecutionStatus`, and does not return the fetched start input, orchestration output, provider failure body,
or raw custom-status JSON. The task-hub ID remains the physical repository key; the status retains the distinct
logical Process identity. The repository validates the physical ID against the retained authority-scoped start and
validates status identity and exact definition affinity against that start receipt. A separate Core query-client
constructor reads the retired adapter's historical wire shapes until those task hubs leave the supported migration
window. Current logical-ID lookup accepts trusted authority scope plus `ProcessInstanceId`, derives the same versioned
opaque physical ID used at scheduling, and performs one exact lookup. Scheduler tags support dashboard discovery, but
the pinned .NET `OrchestrationQuery` has no tag predicate; Cohesive therefore does not emulate a tag index by scanning
task-hub pages. Canonical trace retention, terminal retrieval, and runtime explain composition are implemented;
status, explain, and retained-trace execution-control API bindings are available. Lifecycle mutation bindings, live
trace streaming, richer dashboard presentation, and history-event normalization remain ARI-292 follow-up work.

This is not promotion of the full planning profile. Authored timeout, terminal-failure, escalation, and cancellation
execution paths for general Requests, Reply emission, atomic-with-origin event publication, activation-local and
non-Process Signal targets, external Signal adapters, lifecycle Signal qualification, general attempt-resource/
affinity cleanup, exhaustive
durable Request pause/retry/reconciliation races, complete observability, and the complete target qualification
matrix remain outside the current slice. RestartAttempt and Terminate currently accept only `RetainEvidence`; stronger cleanup
demands fail before canonical admission. Higher-order execution retains
canonical branch selection and lineage, schedules bounded branch work concurrently, maps exact child terminal status
through the authored Request contract, and enforces partition and recurrence bounds without truncation. Parent child
`Propagate` and `Detach` policies are realized explicitly: propagated cancellation is delivered as an exact portable
intent, lowered deterministically at the child to a canonical `CancelProcessCommand`, applied through the same
control receipt and cancellation activation, and awaited, while detached child work remains independently active.
Unsupported node kinds fail catalog construction before execution.

Target facts should be revalidated against the official
[Durable Task documentation](https://learn.microsoft.com/azure/durable-task/),
[Durable Task Scheduler architecture](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler),
[orchestration versioning](https://learn.microsoft.com/azure/durable-task/common/durable-orchestration-versioning),
and [Scheduler tags](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler-tags)
when the executable profile is implemented or upgraded.
