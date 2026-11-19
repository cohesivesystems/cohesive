# Bounded reference monitoring

`TraceMonitor` adds incremental ingestion, checkpoint recovery and explicit correction epochs to the
four-form batch evaluator. It accepts one complete mapping of a declared ordered atomic scope.
Claim, observation contract, target and evaluation limits are pinned at session creation.

The intended live host is the optional #558 production-capture adapter/worker. It owns source
positions, atomic checkpoint persistence, acknowledgment, backpressure and correction propagation.
Simulation provides conformance and reconstruction scenarios; it does not own the live consumer
lifecycle. The optional [OpenTelemetry bridge](opentelemetry-observations.md) now supplies explicit
semantic event ingestion; a durable production worker is not yet implemented.

```csharp
var monitor = TraceMonitor.Start(claim, initialBatch, limits);

// The adapter provides semantic order and completeness; neither comes from arrival time.
monitor = monitor.Advance(new TraceObservationAlternative(
    cumulativeAlternativeReference, newObservations, advancedFrontiers));

// Commit this checkpoint with the source delivery position before acknowledging consumption.
var checkpoint = monitor.Serialize();
var resumed = TraceMonitor.Restore(checkpoint);
var evaluation = resumed.Evaluation;
```

The initial batch and each update must remain within the declared observation/frontier record bound.
`Evaluation` describes only the accepted prefix. It is a normal batch result suitable for the existing
trace evidence projection; it does not prove storage isolation, producer completeness or universal
correctness. A completed evaluation may still have an inconclusive verdict or pending obligations.

## Update and failure contract

- New occurrences append in declared semantic step/time order. The monitor never sorts observations
  to invent a serialization order. Adapters needing reorder buffers must establish order upstream.
- Redelivery uses partition/occurrence identity and compares full semantic content. Matching deliveries
  consume no additional retained capacity. Conflicting duplicates reject the whole update.
- Completeness updates replace individual selector/partition frontiers. Other partitions retain their
  prior frontier; the batch evaluator requires every relevant source before establishing absence.
- Frontiers cannot regress, reopen a closed scope or change its closed boundary within an epoch.
  An unseen occurrence at or behind either previously certified inclusive frontier is a breach.
- An occurrence and its advancing certificate can arrive together in one atomic update. The certificate
  covers the newly supplied row. An inclusive deadline witness therefore remains eligible.
- No wall-clock passage, empty poll, missing partition or checkpoint establishes closure. No obligations
  are evicted or expired because a resource bound was reached.
- Incoming updates and retained history are bounded by `MaxObservations`; evaluation uses the same
  predicate and event-candidate budgets as the batch interpreter. Capacity or evaluation exhaustion,
  invalid observations, unsupported evaluation and cancellation do not accept any part of the update.

`TraceObservationException.Diagnostic` identifies update and restore rejection. Restore uses
`assurance.monitor.checkpoint` for malformed/incompatible or unverifiable artifacts,
`assurance.monitor.profile` for mapping-profile violations, and `assurance.monitor.correction`
for invalid correction lineage. Cancellation uses
`OperationCanceledException`. The monitor is immutable, so a rejected operation leaves the accepted
checkpoint unchanged. Do not acknowledge rejected deliveries or treat the prior checkpoint as
covering them. Apply backpressure and retry, or explicitly start a session with revised limits.
A source that drops records must report incomplete capture rather than issue false completeness.

The update's exact alternative reference identifies the resulting cumulative alternative. A producer
must not reuse historical references for changed content. Reuse of the current reference is checked:
identical redelivery returns the current snapshot after record validation, without rebuilding or
re-evaluating the batch. Changed content is rejected. This monitor
does not retain an unbounded catalog of earlier reference identities.

The retained history collapses newly received duplicate deliveries; batch duplicate-delivery counts
are not a durable transport metric. Equivalence is to the same retained semantic observations and
latest frontier set, not to a concatenation of every transport delivery.

## Checkpoint and correction

`Reference()` computes the immutable checkpoint fingerprint once on first use and caches it with
thread-safe publication. Cache state is excluded from serialization. Observation/frontier shape and
identity validation are shared with the batch evaluator; changed cumulative batches still pass through
that same validator defensively. Monitor continuity rules remain separate.

Checkpoints retain the exact evaluated batch, limits, prior checkpoint fingerprint and any correction
reference. Restoration independently re-evaluates the trace through the verified `TraceEvaluation`
converter. Forged outcomes and unknown checkpoint schemas are rejected. The host must inspect the
restored claim, target, scope and epoch before connecting a live source.

The monitor performs no storage I/O. The host owns atomic checkpoint/source-position persistence,
acknowledgment and concurrency control. Immutable snapshots can be read concurrently; concurrent
advances create branches, so a live consumer must serialize advancement or compare checkpoint
identities before committing. Referenced predecessor artifacts must be retained separately.

When a producer retracts completeness or supplies contradictory late evidence, use a new epoch:

```csharp
var corrected = monitor.Correct(
    replacementAlternative,
    newEpoch: correctionEpoch,
    evidence: correctionAndMappingEvidence);

// Prior evidence remains immutable; the new checkpoint explicitly identifies its predecessor.
Assert.Equal(monitor.Reference(), corrected.Predecessor);
```

The replacement is the complete corrected prefix, not a patch. It can remove observations or withdraw
frontiers. Claim, target and observation contract remain unchanged. Evidence must support the correction
and the new complete single mapping; the monitor does not independently establish that evidence's truth.

A late-data breach means earlier absence conclusions may no longer be trusted. The host must invalidate
or mark the earlier completeness dependencies unavailable for any downstream assessments. Correction
lineage makes the change attributable; this reference implementation does not automatically update
external assessments or prove that a predecessor reference is durably retained. Release/claim/mapping
changes require a new session; carrying obligations across those boundaries is outside this profile.

## Scope and cost

State invariant and transition contract appends reuse previously evaluated obligations in semantic order.
Each retained predicate still counts toward the cumulative evaluation budget. The evaluator retains
predicate diagnostics and computes closure, vacuity and the overall verdict against the updated frontiers.
For bounded responses, a previously matched trigger keeps its first witness. An unmatched trigger
resumes after its previously checked candidates and uses current completeness to decide absence.
Skipped comparisons count against both cumulative budgets in trigger order. Prefixes with predicate
diagnostics or duplicate input rows use full matching to preserve batch diagnostics and identity.
Historical requirements use full matching. Start, correction and checkpoint verification
run full batch evaluation. Append validation and result construction still traverse history, so repeated
single-row appends can require quadratic total work. Checkpoints retain the full bounded history.

In the local Release monitor benchmark (tiered compilation disabled, five warmups, 30 measured
appends from the same snapshot), appending one state to 100 retained rows went from 0.135 to
0.029 ms and 119,240 to 59,672 allocated bytes per operation. At 2,000 rows it went from
1.159 to 0.499 ms and 2,328,272 to 1,120,755 bytes. These measurements exclude journal I/O.

The conformance tests cover all four forms, partitioned deadline absence, inclusive deadlines,
checkpoint continuation, late evidence, corrections, duplicate content, closure, resource rejection,
cancellation and malformed checkpoints. The older A0 corpus is still not present in this checkout;
these tests do not claim to recover or replace that full normative corpus.

Remaining #557 work includes optimized per-obligation state, a qualified retention/eviction strategy,
multiple evolving joint mappings, cross-revision obligations and automated downstream correction
propagation. Production capture/reconstruction is tracked separately in #558.

## Bounded-response append measurements

Local Release runs of `eng/assurance-monitor-review-bench` with tiered compilation disabled compare
#586 with incremental matching. Each prefix contains the indicated number of triggers followed by
eight eligible witnesses. In the matched workload the first witness matches every trigger. In the
pending workload all eight miss. One additional matching witness is appended to the same snapshot
after five warmups. Values are means over 30 appends and include cumulative monitor validation.

| Triggers | Prefix | Before ms | After ms | Before bytes/op | After bytes/op |
|---:|---|---:|---:|---:|---:|
| 100 | matched | 0.155 | 0.175 | 130072 | 65840 |
| 100 | pending | 0.253 | 0.055 | 406377 | 131272 |
| 2000 | matched | 1.729 | 0.781 | 2506278 | 1187712 |
| 2000 | pending | 6.296 | 1.227 | 8011665 | 2507460 |

All four workloads allocated less. The small matched workload ran slower in this timing sample.
The monitor still visits retained triggers and constructs a witness index on each optimized append.
