---
kind: decision
status: accepted
authority: cohesive.processes.interpreters.durable-task
owners: [cohesive-core]
applies_to: [cohesive-processes, cohesive-adapters-durable-task]
last_verified: 2026-08-09
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

The current adapter intentionally exposes only historical task-hub monitoring. The accepted target
uses the standalone Microsoft Durable Task SDK and Azure Durable Task Scheduler. It should leverage
native orchestrations, activities, durable timers, external events, sub-orchestrations, orchestration
versioning, lifecycle APIs, tags, custom status and the Scheduler dashboard whenever those facilities
preserve the requested canonical semantics.

## Authority and state boundaries

| Concern | Authority in the Durable Task profile |
| --- | --- |
| Process meaning | Canonical `ExecutionDefinitionDocument` and exact `CompiledProcessPlan` |
| Definition compatibility | Canonical definition identity, revision, fingerprint and Process IR schema compatibility |
| Finite decisions | Canonical Process interpreter semantics and normalized decision evidence |
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
| `EmitEventProcessNode` | Typed envelope, producer occurrence, ordering and publication obligation | Canonical outbox/publication activity with stable idempotency identity | Composed |
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
tests until the target-neutral collector and Durable Task profile state its disposition.

The executable target-neutral inventory, profile, realization ledger, and structured diagnostics are implemented in
`Cohesive.Processes.Compilation`. Construct kinds are projected from the canonical persisted-union metadata instead
of another enum. `Cohesive.Adapters.DurableTask` now publishes the versioned planning profile and compiles successful
exhaustive reports into deterministic physical plans that retain the exact canonical plan. These artifacts do not
admit execution; the generic Durable Task interpreter and its conformance evidence remain subsequent work.

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

This decision remains accepted direction for Durable Task execution. As of 2026-08-09,
`Cohesive.Processes` implements the target-neutral requirement inventory, capability evidence, exhaustive
disposition ledger, and structured matching diagnostics. `Cohesive.Adapters.DurableTask` implements historical
monitoring plus a planning-only target profile and physical realization compiler. It still exposes no Process
execution admission or host; the interpreter, conformance suite, and ARI adoption remain tracked from ARI-285.

Target facts should be revalidated against the official
[Durable Task documentation](https://learn.microsoft.com/azure/durable-task/),
[Durable Task Scheduler architecture](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler),
[orchestration versioning](https://learn.microsoft.com/azure/durable-task/common/durable-orchestration-versioning),
and [Scheduler tags](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler-tags)
when the executable profile is implemented or upgraded.
