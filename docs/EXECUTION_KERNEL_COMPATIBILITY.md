# Execution Kernel Compatibility Inventory

This inventory records the pre-kernel behavior characterized by ARI-153 against the normative EK-01 through EK-09 scenarios in the [Cohesive Execution Kernel Specification](https://app.notion.com/p/3ab8cf7881f981f78ef1e34d7a907c70). It is a migration baseline, not an alternative semantic contract. Missing behavior remains required by the specification.

Status meanings:

- **Pass**: the current model and runtime satisfy the scenario's normative guarantees.
- **Partial**: useful substrate or observable behavior exists, but one or more normative guarantees are missing.
- **Absent**: the scenario's core semantic construct has no current representation.

No scenario fully passes today.

## Scenario matrix

| Scenario | Status | Current compatibility | Missing kernel semantics |
| --- | --- | --- | --- |
| EK-01 — structured DQ branching | Partial | `TransitionDefinition` provides normalized preconditions, field updates, effects, and read/write sets. `DeclarativeEntityRuntime` supports direct deterministic activation. `BranchingNode` provides ordered first-match process branching. | Structured transition bodies, stable branch identity, exhaustive choice, typed outcomes, branch-specific patches/emissions/requests, path-sensitive summaries, and a persisted branch decision. |
| EK-02 — durable human review | Partial | `WaitNode` produces a `Waiting` `ProcessCheckpoint` before the runtime yields. Local and DurableTask runtimes buffer early keyed signals; DurableTask also supplies durable timers. | Closed `AwaitMatch`, durable wait registration plus timer arming, signal identity, admission/claim/consume state, duplicate/late/stale policy, and typed timeout/cancel outcomes. |
| EK-03 — vendor/manual fulfillment | Partial | Typed effect handlers, transient retry, continuation freshness checks, and dead-lettering exist. | Stable request/correlation/idempotency identity across retries and fallbacks, explicit response obligations, vendor/manual arbitration, and protection from late results. |
| EK-04 — parallel gates and join recovery | Absent | A checkpoint has one `CurrentNode` and a LIFO locality continuation stack. Transition batches execute sequentially. | Fork tokens, parallel scheduling, join policy, durable per-branch progress, order-independent recovery, and duplicate-work prevention. |
| EK-05 — capability-safe multi-entity coordination | Partial | `ProcessTransactionScope` can name multi-entity/database scopes, and places expose a coarse transaction capability. | Requirement extraction, adapter capability evidence, proof-directed guarantee matching, independent subject authority, and mandatory authored compensation when atomic scope is unavailable. |
| EK-06 — durable effect crash matrix | Partial | Transition effects can be committed to a storage outbox; process checkpoints distinguish pending and executed effects and retain dead letters. | A durable operation ledger with stable request, attempt, acknowledgement, claim, and completion identities covering every crash boundary. |
| EK-07 — signal arbitration | Partial | Signals can target a DurableTask process instance and are buffered FIFO by key. The local wait adapter also buffers early keyed inputs. | Signal identity, idempotent admission receipts, exclusive winner claims, duplicate prior-result behavior, observable losers, and rules preventing late signals from reopening a choice. |
| EK-08 — index rebuild recovery | Absent | No generation-affine recovery behavior exists in Transitions or Processes. | Process attempt and activation identity; candidate generation affinity; pause/continue retaining the generation; retry policy; restart creating a fresh generation; abandoned-generation exclusion; fenced, idempotent promotion. |
| EK-09 — C# and IR equivalence | Absent | Transition definitions have a serializable normalized subset, and existing entity authoring round-trip tests provide useful migration input. | A canonical normalized execution IR for transitions and processes, schema versioning, semantic revision, fingerprints, provenance, stable node/type identity, and equivalence between C# authoring and direct IR authoring. |

The executable classifications and focused behavioral baselines live in `src/Cohesive.Tests/ExecutionKernel/ExecutionKernelCharacterizationTests.cs` and run as part of the existing `Cohesive.Tests` project.

## Current authority to migrate

### Flat transitions

`Cohesive.Transitions.Model.TransitionDefinition` is a serialized set of parallel collections: `Inputs`, `Preconditions`, `Updates`, and `Effects`. The runtime applies preconditions, sequential assignments, computed fields, invariants, and then every declared effect. Conditional expressions exist inside those collections, but there is no structured body containing branch nodes or stable path identity. Static analysis unions referenced fields; it cannot report must/may/actual access or branch provenance.

Migration disposition: keep the current builders and `DeclarativeEntityRuntime` as compatibility producer/interpreter surfaces, but move semantic authority to the normalized structured Transition IR. Project the legacy `TransitionResult`, generic effects, and dictionary patches from the canonical decision rather than defining kernel behavior through them.

### Delegate-bearing processes

`Cohesive.Processes.Model.ProcessDefinition` stores executable node objects. Semantic choices—including branch predicates, entity references, transition inputs, request construction, waits, computations, and terminal results—are CLR `Func` delegates. DurableTask resolves a definition by process name from a local registry and re-evaluates those delegates during orchestration replay. A changed definition under the same name is accepted because checkpoint compatibility validates only `ProcessName`.

Migration disposition: treat the existing definition, builder, and source-generator output as authoring/compatibility inputs. Effect handlers and transaction gateways remain legitimate adapter mechanisms, but delegates must cease to be persisted semantic authority. Lower authoring into canonical typed nodes and require a definition fingerprint on activation and replay.

### Single execution cursor

`Cohesive.Processes.Runtime.ProcessCheckpoint` persists one `CurrentNode` plus a locality continuation stack. It has no token set, fork/join state, definition fingerprint, process attempt or activation identity, durable wait inbox, operation ledger, control state, compensation state, or index-generation affinity. `ProcessDefinition` also accepts unrestricted control-flow cycles.

Migration disposition: preserve old checkpoints only behind an explicit compatibility reader. New kernel checkpoints should be versioned envelopes whose token set, wait/operation ledgers, control state, and generation binding are derived from canonical Process IR. Do not infer parallelism or generation recovery from the old single cursor.

## Characterized runtime paths

| Area | Current types and runtime paths |
| --- | --- |
| Direct transition activation | `Transition<TEntity,TInput>.Apply` → `Entity.ApplyTransition` → `DeclarativeEntityRuntime.Apply` |
| Flat transition semantics | `TransitionDefinition`, `TransitionBuilder`, `TransitionExpressionBuilder`, `TransitionExpressionAnalyzer`, `TransitionPatchProjector`, `TransitionResult` |
| Process planning and replay | `ProcessDefinition`, `ProcessNode`, `BranchingNode`, `ProcessExecutionPlanner`, `ProcessCheckpoint` |
| Waits and signals | `WaitNode`, `IProcessWaitAdapter`, `IProcessSignalSink`, `InMemoryProcessWaitAdapter`, `DurableTaskProcessOrchestration` |
| Effects and recovery substrate | `EffectRequest`, `ProcessPendingEffect`, `EffectExecution`, `ProcessDeadLetter`, `ProcessNodeExecutor`, `ProcessEntityRepositoryAdapter` |
| Transactions and capabilities | `ProcessTransactionScope`, `ProcessPlace`, `ProcessCapability`, `IProcessTransactionGateway` |

## Characterization policy

These tests intentionally lock observable legacy behavior—including unsafe gaps such as re-evaluated branch delegates, repeatable checkpoint consumption, duplicate signal buffering, unrestricted cycles, and same-name definition replacement. When a kernel implementation closes a gap, update the classification and replace the legacy assertion with the corresponding normative conformance test. Do not preserve a characterized gap merely to keep this inventory green.
