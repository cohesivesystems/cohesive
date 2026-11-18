# Reference capture host and delivery recovery

The internal example in `eng/examples/assurance/FileCaptureHost.cs` consumes native activities through
the OpenTelemetry adapter and persists a bounded source journal before acknowledgment. The source is
linked into the integration tests and benchmark; it is not a new public hosting/storage abstraction.

## Delivery commit and periodic checkpoint

The source must provide a stable identity and contiguous positions beginning at one. Each immutable
`capture-NNNNNNNNNN.json` commit contains:

- source binding, exact initial-checkpoint reference, and source position;
- raw activity source, trace/span IDs, ordered event names/tags, and producer references;
- requested cumulative alternative reference and resulting monitor checkpoint reference;
- an accepted-observation delta, or a quarantine diagnostic with no accepted delta.

The delta is derived from the newly accepted suffix of the monitor's append-only observation history.
It is not a second interpretation of activity tags. Duplicate occurrences add no rows; an empty accepted
delivery still retains its cumulative reference. Delta replay uses core `AdvanceCaptured` and must
reproduce the exact recorded checkpoint reference. A mismatch fails restoration.

Every `checkpointInterval` deliveries (default **32**), the host attempts to publish
`capture-checkpoint.json`: one full monitor artifact plus **all compact receipts** through that position.
Each compact receipt retains raw input, provenance, quarantine disposition and the result reference,
so old delivery positions still support exact redelivery checks. Historical full monitor artifacts and
covered replay deltas are removed; their references and raw input remain. Recovery validates the current
checkpoint rather than independently re-evaluating every historical prefix. Separately published evidence
artifacts are not part of this journal and must retain their own lifecycle.

```csharp
using var host = new FileCaptureHost(
    directory, "orders-capture", initialMonitor,
    checkpointInterval: 32);

var receipt = host.Process(
    position: delivery.Position,
    activity: delivery.Activity,
    cumulativeReference: delivery.CumulativeReference,
    acknowledge: source.Acknowledge);

var evaluation = host.Monitor.Evaluation;
var hasCaptureGaps = host.HasCaptureGaps;
```

The source must retain its input until acknowledged; this does not make SDK export durable. Span
status/timestamps and unrelated span attributes are excluded from the stored activity snapshot. Delivery
positions never establish application transaction order. Reusing a position with changed input or a
different cumulative reference fails without acknowledgment, including after compaction.

## Atomicity and crash boundaries

The host owns an exclusive local lock for its lifetime and serializes calls. It writes and flushes one
pending file, then renames it to the numbered delivery filename **without overwrite**. The delivery is
committed at that publication point. Checkpoint creation is a subsequent optional step:

1. Write and flush a checkpoint containing the accepted prefix and every receipt through its position.
2. Atomically replace the prior checkpoint file.
3. Delete only numbered files covered by the published checkpoint.
4. Acknowledge the delivery after this call's persistence work completes.

There is no separately updated cursor or manifest. A pending file is never a commit. No covered file is
deleted before checkpoint publication. A persistence or cleanup failure makes the host unavailable
until reopened; the delivery may already be committed, but the failed call never acknowledges it.

| Failure point | Recovery authority |
|---|---|
| Before delivery publication | Prior checkpoint plus committed tail; source redelivers the uncommitted position |
| After delivery publication, before checkpoint publication | Prior checkpoint plus tail including the new delivery |
| After checkpoint publication, before or during deletion | New checkpoint; covered files are obsolete and cleanup resumes |
| After persistence, acknowledgment lost | Retained receipt; acknowledge redelivery without re-evaluating or writing |

On reopen, the host checks storage bounds, restores the latest checkpoint, validates its receipt sequence
and exact source/model binding, then replays only subsequent contiguous deltas. It compares every replayed
result with the recorded checkpoint reference. Only after the entire surviving tail validates does it
resume covered-file deletion. A corrupt published checkpoint fails closed; it does not fall back to
covered logs that may already be deleted. Gaps before later tail files, malformed names, invalid deltas
and incompatible bindings fail recovery. Arbitrary deletion of a trailing suffix cannot be detected
without an independent watermark; storage must protect committed files.

The acknowledgment callback is synchronous and must not re-enter the host. The profile supports one
source/atomic semantic scope, not concurrent distributed partitions. The format is
`cohesive-assurance-file-capture-example/v3`; v1 snapshots and uncheckpointed v2 commits are explicitly
rejected. Use a new directory or a separately designed migration. No automatic migration or silent reset
is performed.

## Quarantine, bounds and retention

Permanent envelope/source/binding/payload failures retain raw input and diagnostic with the unchanged
checkpoint reference and no delta. Publish that receipt before acknowledging. Compaction retains those
receipts and `HasCaptureGaps`; it never converts a rejection into accepted evidence or completeness.
If quarantine cannot be persisted, the source remains unacknowledged. Monitor exhaustion, semantic
ordering/identity failures and storage/capacity failures require diagnosis or backpressure, not automatic
quarantine. The host begins with an uncertified scope and adds no completeness certificates.

Defaults remain **256 lifetime deliveries** and **16 MiB of committed files**. Compaction removes redundant
state/deltas but retains every receipt, so it does not reset delivery capacity, erase old redelivery
identities, evict monitor observations or make the source session unbounded. Receipt/input storage and
the current monitor still grow with history. No retention horizon is silently introduced.

The byte limit covers the peak committed state while the new checkpoint and covered logs coexist,
net of the atomically replaced old checkpoint. Pending and lock files are excluded. If checkpoint
publication lacks that headroom, optional compaction is skipped: the delivery log is already durable
and can be acknowledged. Future deliveries still face ordinary byte-bound backpressure. Configure an
interval and capacity with sufficient headroom; cleanup is never performed early to manufacture space.
Failures before checkpoint publication can leave a longer replay tail until a later successful checkpoint.

`CommittedBytes` reports actual retained committed file sizes. `PublishedBytes` counts all logical bytes
published by this open host, including replacement checkpoints; it never subtracts compaction deletions.
Raw input/delta/checkpoint encoding happens before encoded-length checks, so these are not strict heap
bounds. No final checkpoint is implicitly created by `Dispose`.

## Qualification and measured overhead

The guarantee remains **process-crash recovery under atomic local rename**, not power-loss durability.
File data is flushed, but directory fsync and network/filesystem behavior are unqualified. A production
store must make checkpoint directory metadata durable before deleting covered logs; this local example
does not establish that guarantee. Tests inject exceptions at publication/deletion boundaries rather
than killing processes or simulating hardware failures. The journal is not authenticated against an
adversary rewriting its history consistently.

See the [original baseline](../performance/assurance/end-to-end.md),
[immutable-commit optimization](../performance/assurance/journal-persistence.md), and
[periodic-checkpoint/compaction comparison](../performance/assurance/journal-compaction.md).
The reports retain raw trials and distinguish local service cost from production throughput/SLO claims.

Still outstanding in #558: a deployed durable source/exporter, remote OTLP qualification, production
storage/durability realization, correction/completeness protocols, richer transaction histories and
executable faulty/repaired Simulation reconstruction. The core monitor still re-evaluates retained
history on each changed update; this storage optimization does not make evaluation incremental.
