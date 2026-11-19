---
kind: decision
status: proposed
authority: cohesive.processes.scope-cancellation-audit
owners: [cohesive-core]
applies_to: [cohesive-processes, cohesive-storage, cohesive-adapters-durable-task]
last_verified: 2026-11-15
supersedes: []
---

# Execution scopes and cancellation: source audit

Tracks [#572](https://github.com/cohesivesystems/cohesive/issues/572), under
[#571](https://github.com/cohesivesystems/cohesive/issues/571). Delivery is tracked in
[Process scopes and recovery](https://github.com/orgs/cohesivesystems/projects/5).
Source baseline: `ae546131e64730040a639dcc2c62b33c426bde8e`, with unrelated local work present.
This records the audit, proposed design, and first cancellation-recovery implementation slice. It does not establish a general scope guarantee or complete qualification.

## Conclusion

Use an exact child Process as the first nested execution boundary. Its own Fork/Join can own the two
concurrent child operations required by the milestone. Existing authored cancellation finalization supplies
a useful settlement barrier, but **exact-child composition alone does not guarantee transitive settlement**:
a child without a finalizer can become terminal before its propagated descendants settle. Failure and
early-join completion have related gaps. Do not add an embedded-scope node before qualifying this narrower
composition and making the remaining lifecycle requirements explicit.

The first executable fixture should declare finalizers at each owning Process that needs a cancellation
barrier, use propagated children, and use an All join for normal completion. This is an explicit supported
profile, not a convention that inserts cleanup work. It does not establish a general scope guarantee for
arbitrary existing Processes.

## Authorities and observed behavior

Paths below are relative to the repository root. Method names identify the inspected operation.

| Concern | Existing authority and evidence | Consequence for scopes |
| --- | --- | --- |
| Authorship | `ProcessComputationAuthoringTests.CancellationFinalizerComputation_IsByteEquivalentToCanonicalBuilderAuthoring` compares source projections. `IR/ProcessDefinitionValidator.cs` and `Compilation/ProcessRequestRequirements.cs` in `Cohesive.Processes` validate and inventory the exact finalizer dependency. | Reuse builders and derived canonical definitions; no parallel scope registry. |
| Ownership | `Cohesive.Processes/Execution/ProcessReferenceInterpreter.cs`: `DispositionOwnedChildren`, `CancelChild`; retained children carry owner/token, registration, Request emission, exact definition and continuation. | A child Process supplies a distinct attempt and durable boundary. Token ownership is local coordination, not a second nested execution model. |
| Cancellation without finalizer | `ApplyCancellation` closes normal work and immediately sets terminal `Cancelled`. Active propagated children become `CancellationRequested`; pending children become `CancelledBeforeStart`; detached children become `Detached`. | `Propagate` means request propagation, not a universal parent settlement barrier. |
| Cancellation with finalizer | `ApplyCancellation`, `ContinueCancellationFinalization`, `HasUnsettledPropagatedChildren`, `StartCancellationFinalizer`. | Parent remains nonterminal while direct propagated children settle, then starts one exact finalizer through ordinary Request/Reply. |
| Closure admission | `ApplyChildCancellationClosures` matches intent and continuation, bounds observation time, accepts exact retained duplicates, rejects conflicting evidence. Closure input without retained finalization is rejected by the activation entry path. | Closure handling currently belongs to authored finalization, not every exit path. Cancellation plus initial closure evidence in the same activation is rejected before `ApplyCancellation`; adapters must stage these observations. |
| Finalizer result | `ResumeCancellationFinalizerRequest`, `IsExactCancellationAcknowledgement`, `SetCancellationFinalizationFailure`. | Attempt-matching acknowledgement permits cancellation completion. Invalid acknowledgement and unsuccessful finalizer outcomes fail explicitly. |
| Normal and failed joins | `ResolveJoins` counts branch completion; FailFast sets terminal failure and closes remaining work. `CancelRemaining` cancels losing tokens and continues the owner without a canonical child-closure barrier. | Branch termination is not proof that descendant operations settled. Failure must not be advertised as fully settled merely because the parent is terminal. |
| Native runtime | `Cohesive.Storage/Processes/ProcessChildDurableOperationAdapter.cs`: `CancelChildAsync`; `ProcessDurableRuntime.Operations.cs`: `HasOpenOriginAttempt`, `IsCancellationRequestedChild`. | Exact selected adapters supply cancellation capability. Built-in children can be prevented from starting or driven to terminal closure. The open-origin exception for cancelling children is tied to the finalization phase. |
| Durable Task | `Cohesive.Adapters.DurableTask/DurableTaskSequentialProcessInterpreter.cs`: `DispatchChildCancellationAsync`, `AwaitPropagatedChildClosuresAsync`. | Dispatch deduplicates by intent; finalization waits for exact child operation acknowledgements. Ordinary losing-child execution may also be awaited physically without writing canonical settlement. |

The Durable Task distinction is directly asserted by
`DurableTaskSequentialProcessInterpreterTests.ForkJoin_PropagatesExactCancellationAndWaitsForTheCancelledChildToClose`:
the returned parent is completed, the losing operation has a late observed result, and its canonical child
disposition remains `CancellationRequested`. Preserve the observation, but do not infer cross-target
equivalence from that host wait.

## Proposed semantics and smallest design

1. **Nested ownership:** an exact child Process owns its internal work. Parent ownership follows the existing
   Request occurrence and continuation identities. Every settlement-required descendant must participate in
   the supported lifecycle profile; `Detach` explicitly removes the waiting obligation and cannot satisfy a
   requirement that all owned effects be reconciled.
2. **Normal exit:** use All joins over the two children in the first fixture. Child success is interpreted
   through the exact authored outcome mapping. Any/threshold joins and continued background branches remain
   outside the initial settlement profile until their outstanding obligations are explicitly accounted for.
3. **Cancellation:** accepted cancellation stops normal advancement before waits resume or branches are
   admitted. Retain the exact intent and outstanding child occurrences. Wait for required closures, then run
   the declared finalizer once. A completed child racing cancellation is valid terminal closure, not evidence
   that its committed effect was undone.
4. **Failure:** an existing FailFast result is a failure notification, not a settlement guarantee. For the
   first fixture, explicitly model child failure outcomes and route remaining work through an authored
   recovery flow where feasible. A general fail-fast-and-settle guarantee requires extending the existing
   lifecycle authority; cancellation finalization must not silently become a failure handler.
5. **Finalization:** reuse the existing root-input/cancellation-context contract. Reacquire effect evidence
   from its owning ledger through ordinary Process operations. Do not capture branch-local bindings or infer
   compensation registration from `ProcessChildPurpose.Compensation`.
6. **Restart:** recover the same attempt, child occurrence, cancellation intent, and finalizer emission.
   Worker restart is distinct from the lifecycle command that replaces an attempt. Existing authored
   finalization disallows attempt replacement once cancellation has begun.

An embedded scope would additionally need identity, lexical data rules, entry/exit validation, durable state,
capability admission, serialization, and trace interpretation. Those costs are not justified by the two-child
fixture. Exact-child composition costs explicit contracts and separate child checkpoints, but retains known
ownership and recovery boundaries. Introduce embedded scope IR only when a demonstrated requirement, such as
intra-Process interruption with preserved local bindings, cannot be expressed faithfully through composition.

Do not manufacture no-op finalizers as an invisible default merely to obtain a barrier. If cancellation
settlement must be independently selectable without authored cleanup, separate that requirement from optional
finalizer execution within the existing lifecycle model. Avoid a second scope-state machine. The exact public
authoring shape remains deferred until the first fixture establishes which distinction is necessary.

## Effect evidence and target admission

`ProcessChildCancellationClosure` records an intent, exact child continuation, terminal outcome, and observation
time. It is not an external-effect ledger. A `Completed`, `Failed`, `Cancelled`, or `Terminated` closure cannot
prove reversal, exactly-once effects, or absence of an uncertain provider outcome. Preserve operation evidence
and reconcile unknown outcomes separately; process closure and external-effect reconciliation are different
claims. No new effect-status enum or compensation engine is justified by this audit.

Before claiming a settlement-required profile, compilation/admission must establish exact child dependencies,
propagated cancellation support on selected adapters, durable closure/restart support, and the declared
idempotency/reconciliation support for external effects. Existing node capability admission and runtime
adapter checks are foundations, not proof of a transitive guarantee. Unsupported profiles must be rejected
before work starts. Native and Durable Task are not newly qualified by this audit.

## Test evidence and remaining proof obligations

Existing inspected tests cover:

- Invalid finalizer placement/contracts and target inventories: `ProcessCancellationFinalizationTests`.
- Exact acknowledgement, wrong-attempt failure, one propagated child, retained closure conflict, and a
  serialized finalizer-active continuation: `ProcessReferenceInterpreterTests.AuthoredCancellationFinalizer_*`.
- Accepted-cancellation commit failure before/after commit and replay:
  `ProcessDurableRuntimeTests.AuthoredCancellationFinalizer_RecoversAcceptedCancellationCommitWithoutDuplicateWork`.
- Ordinary child finalization and propagated-child waiting in Durable Task, plus scheduler-emulator
  finalization: `DurableTaskSequentialProcessInterpreterTests.AuthoredCancellationFinalizer_*` and
  `SchedulerEmulator_AuthoredCancellationRunsExactFinalizerBeforeTerminalControl`.

Focused verification on 2026-11-15: `dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj --no-restore -m:1`
with filter `FullyQualifiedName~ProcessCancellationFinalizationTests|FullyQualifiedName~AuthoredCancellation|FullyQualifiedName~ForkJoin_PropagatesExactCancellation`
passed 23 tests, failed none, and skipped one scheduler integration test because
`DURABLE_TASK_SCHEDULER_CONNECTION_STRING` was not configured. The initial restricted run failed to create
MSBuild local pipes; the retry with local process communication enabled completed successfully.

These are partial coverage, not the complete #572 matrix. The next implementation slice should add one shared
two-child fixture, then prove:

| Scenario | Required assertion |
| --- | --- |
| Both children active; only one closes | Parent remains nonterminal; finalizer not emitted; no new normal work. |
| Duplicate intent and exact duplicate closure | No duplicate finalizer or operation; occurrence identities unchanged. |
| Wrong child/attempt, conflicting closure, stale/future observation | Structured rejection leaves authoritative continuation unchanged. |
| Completion versus cancellation; buffered and late replies | Committed outcome remains observable; cancelled branch cannot resume. |
| Child with grandchildren, including absent/pre-start child | Transitive settlement is demonstrated or unsupported profile rejected. |
| Finalizer fails, cancels, terminates, or acknowledges wrong attempt | Explicit cancellation failure, never successful cancellation. |
| Restart during waiting, partial closure, finalizer start and acknowledgement commit | Exact identities and evidence survive every phase without duplicate work. |
| FailFast and CancelRemaining | Canonical status and physical settlement are distinguished; unsupported stronger guarantees rejected. |
| Lost external-effect acknowledgement | Unknown outcome remains reconcilable; terminal child status never fabricates rollback. |

Retain existing finalization trace events and child-closure evidence. A later scope projection should explain
requested, settling, finalized, and failed from those authorities and show uncertainty from the operation
ledger. Do not build an independent status catalog.

## First implementation: cancellation during an active Fork

The shared authored `CancellationFinalizerDurabilityTestFixture.CreateTwoChildScope` now supplies two
propagated child invocations, an All join, and an explicit cancellation finalizer. Reference tests, native
durable-store tests, and Durable Task interpreter tests consume that same definition. This fixture observes
all terminal child outcomes at the join; it is not an example of business-success aggregation or settled
fail-fast behavior. Structured Fork branches must converge on their reciprocal Join, so a branch cannot
simply route a failed reply directly to a Process-level Fail node.

The fixture exposed a recovery defect: `ProcessContinuationValidator.ValidateFork` only admitted aborted
Forks when the Process was terminal. Authored cancellation closes the coordinator and branch tokens while
the Process remains nonterminal awaiting propagated children or its finalizer. The resulting continuation
could execute in the interpreter but failed validation on restore and native-store cancellation commit.
Fork validation now recognizes retained cancellation finalization as an aborted-normal-execution state.
The existing finalization validator remains responsible for exact intent, phase, child occurrence, and
closure of normal work; normal Fork ownership, membership, selection, and admission checks remain intact.
Negative tests reject missing finalization evidence and a reopened coordinator.

The new coverage establishes:

- Two active children, partial settlement, and no finalizer emission until both required closures arrive.
- JSON restoration at waiting, partially settled, finalizer-active, and acknowledged phases, preserving
  exact cancellation intents and retained terminal outcomes.
- Exact duplicate intent/closure replay, conflicting retained evidence, wrong intent/attempt, stale/future
  closure timestamps, and rejection of an invalid batch without partial mutation.
- A completed child's result survives sibling cancellation; same-activation and repeated late replies
  cannot resume normal execution after cancellation.
- Native-store worker reconstruction between cancellation and partial-closure commits, with exact activation
  replay and no duplicate finalizer operation. Closures are supplied explicitly in this test; it does not
  qualify physical child adapters or external effects.
- Durable Task dispatches two distinct cancellation intents and waits for both child operation results before
  scheduling the finalizer. This uses controlled child tasks, not a live scheduler.
- Repeated source lowering yields the same exact definition reference and fingerprint.

Verification: the selected Process regression suite passed **285 tests**, with **8 scheduler-dependent tests
skipped** (293 total). It includes reference execution, continuation validation/fingerprints, native runtime,
Durable Task interpretation, canonical IR/authoring, computation authoring, and lifecycle control tests.
No new IR construct, public type, dependency, or lifecycle authority was added. The runtime change is a
validation correction on continuation recovery, not a new hot-path execution strategy; no performance claim
is made.

Issue #572 remains open. Transitive descendant settlement, pre-start cancellation races, full crash injection
around child adapters and finalizer acknowledgement, external-effect uncertainty/reconciliation, and admission
of a declared settlement-required profile still require qualification. This first slice does not weaken the
existing explicit-finalizer requirement or claim that child closure proves rollback.

## Initial audit boundary

The initial audit changed no runtime, public API, IR, or generated artifact. No new type or semantic authority was introduced.
Performance was not a design driver and no performance claim is made. The decision prioritizes semantic
preservation and reuse over a new surface that would hide the identified gaps. Full BPMN conformance,
embedded scopes, scoped event handlers, and automatic compensation remain deferred to their owning issues.
