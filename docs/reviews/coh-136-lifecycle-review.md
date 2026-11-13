# COH-136 lifecycle review revision

Review baseline: PR #511, `8549fdcd`. The revised authority remains one controller per stable
consumer, with one lock covering desired-state changes and admission. Process execution and
provider dispatch policies retain their separate authorities.

## Decisions

- `Bind(compilation)` validates catalogs, consumer, exact binding and canonical Process lowering once.
  It returns an opaque controller-owned token. Multiple immutable scopes/bindings can share a consumer
  controller without resetting history. `Admit(request)` has no compilation input or lowering path.
- Pending admissions and completed history are separate indexes. Inspection sorts only pending work;
  completed receipts preserve exact admission replay. Changes/rejections never construct snapshots.
- Change replay retains only the command and resulting revision. Memory grows linearly with changes
  plus admissions, not changes multiplied by pending sessions. History cleanup is still deferred.
- Paused and Deactivated had identical semantics. Keep Paused (the initial state) and Active only.
- Change, Admit and Complete each take one attributable request and return a typed receipt result.
  RevisionConflict and AdmissionStopped are distinct outcomes. Hosts check IsAccepted and use Receipt,
  or branch on Outcome. No nullable out arguments or diagnostic-string matching are needed.
- An authorized second completion recorder receives ReplayedWithOriginalRecorder and the original
  receipt. Original evidence/attribution wins; unauthorized calls still fail before history inspection.

The additional request/result types establish real boundaries: prevalidated controller-owned handle,
operation intent, original receipt and typed disposition. They do not duplicate Process control state
or implement another execution engine. Bind-time validation still uses shared canonical mechanisms;
this change does not optimize the separate IntegrationRuntime dispatch validation path.

## Measurements

Single-process .NET 10 Release smoke measurement on the same machine, baseline and revised working
copy. Setup uses the existing IntegrationModelTests Candidate fixture, outside measured sections.
Each case warms once, then uses Stopwatch and GC.GetAllocatedBytesForCurrentThread around the loop.
Binding/setup is excluded deliberately; the comparison measures repeated lifecycle operations.
Requests, identities and command receipts allocated within the loop are included.

| Workload | Operations | Baseline total ms | Revised total ms | Baseline B/op | Revised B/op |
|---|---:|---:|---:|---:|---:|
| Exact admission replay | 1,000 | 1,824.63 | 0.15 | 683,582 | 97 |
| New admission while paused | 1,000 | 1,485.87 | 0.17 | 683,946 | 199 |
| Unique changes with 1,001 pending sessions | 100 | 29.07 | 0.01 | 73,708 | 265 |

These are local smoke measurements, not statistical throughput/latency guarantees. The important
structural changes are removal of compilation work from Admit and snapshot construction from Change.
Inspect remains O(p log p) time and O(p) output for p pending sessions; completed history is not scanned.
Admit/Change/Complete use expected O(1) dictionary operations, excluding host authorization and normal
hash-table resizing. Immutable compilation validation remains explicit work at Bind.

## Verification and limits

257 selected integration/contract tests and 40 integration examples pass. Tests cover typed stopped
versus stale rejection, exact/conflicting replay, foreign binding tokens, concurrent pause/admission,
actual Process draining, completed-history admission replay and changed-recorder completion replay.
Failed/unresolved Processes intentionally remain pending. Physical durability, distributed fencing,
retention cleanup, health projection and explicit failure resolution remain outside this slice.


## Repeatable allocation regression coverage

`IntegrationLifecycleTests.AdmissionReplayHasBoundedAllocationIndependentOfPendingWork` now locks in
an allocation budget: 1,024 warm-up replays followed by 4,096 measured replays, using a prebuilt binding
and request. It runs with zero and 1,000 other pending sessions and checks the original receipt identity
and replay disposition. `GC.GetAllocatedBytesForCurrentThread` must stay at or below 256 B/call, leaving
runtime/object-layout headroom while rejecting compilation or snapshot work on the replay path.
There is deliberately no wall-clock assertion; the one-off timings above remain illustrative only.

```sh
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release \
  --filter FullyQualifiedName~AdmissionReplayHasBoundedAllocationIndependentOfPendingWork -m:1
```

The outcome vocabulary is now operation-specific: IntegrationChangeOutcome, IntegrationAdmissionOutcome
and IntegrationCompletionOutcome. The shared generic receipt carrier retains one implementation of
acceptance/receipt behavior without making a Change caller handle completion-only cases. Bind throws
IntegrationBindingException with the complete reported DocumentValidationResult; later validation stages
are still conditional on earlier prerequisites. Retained history remains unbounded for the controller's
lifetime, with cleanup explicitly deferred rather than silently discarding replay evidence.
