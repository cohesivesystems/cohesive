# Reference capture host and delivery recovery

The internal example in `eng/examples/assurance/FileCaptureHost.cs` supplies the next #558 capture
host slice. It consumes native activities through the existing OpenTelemetry adapter and commits a
bounded journal before acknowledging source delivery. It is linked into the OpenTelemetry test
project rather than exposed as another public storage or hosting abstraction.

The executable recovery tests live in `CaptureHostTests.cs`. They cover checkpoint/position recovery,
producer provenance, acknowledgment loss, permanent quarantine, partial publication failure,
capacity backpressure, exclusive ownership and incompatible restoration.

## One commit before acknowledgment

The host requires a stable source identity and contiguous delivery positions beginning at one.
For each delivery, one immutable numbered commit file includes:

- source delivery position and supplied cumulative alternative reference;
- raw activity source, trace/span IDs, and ordered event names/tags;
- accepted monitor checkpoint and producer envelope references;
- a rejection diagnostic and unchanged checkpoint for quarantined input.

The input snapshot records the actual activity events and tags used at ingestion. It is not a full
OTLP span dump: span timestamps/status and unrelated span attributes are excluded. It preserves the
semantic envelope payloads and transport correlation without treating that correlation as semantic order.

```csharp
// Example-only internal type. The source owns stable positions and at-least-once redelivery.
using var host = new FileCaptureHost(directory, "orders-capture", initialMonitor);

var receipt = host.Process(
    position: delivery.Position,
    activity: delivery.Activity,
    cumulativeReference: delivery.CumulativeReference,
    acknowledge: source.Acknowledge);

// This may remain inconclusive: receipt persistence never establishes capture completeness.
var evaluation = host.Monitor.Evaluation;
var hasCaptureGaps = host.HasCaptureGaps;
```

The source mapping and cumulative reference must remain stable on redelivery. The source must retain
its activity/envelope until acknowledged; this example does not make the SDK exporter itself durable.
The host checks redelivered content against its retained input and retries acknowledgment without
re-evaluating the monitor. Reusing a position with changed input or a different cumulative reference
fails without acknowledgment. There is no inferred serialization order from delivery positions.

## Persistence and failure contract

The host holds an exclusive local lock file for its lifetime. Calls are serialized. One delivery's
complete commit envelope is written to `capture.pending`, flushed, and atomically renamed to
`capture-0000000001.json` (then 2, 3, ...). Rename refuses to overwrite an existing commit. Only then
does the host update in-memory state and invoke acknowledgment. Receipt, checkpoint and position
are in that same file: there is no independently updated cursor or manifest. Earlier committed files
are never rewritten. The source binding and exact initial checkpoint reference accompany every commit.

A write/publication failure makes the host unavailable until disposed and reopened: publication outcome
may be ambiguous. On reopening, it reads the bounded sequence of numbered files and independently
restores retained monitor checkpoints. A pending file—even a torn one—is ignored. A gap before a later
commit, malformed filename, incompatible binding, invalid checkpoint or aggregate capacity excess is
rejected. Checkpoint predecessor links and no-completeness restrictions are checked. No damaged
committed receipt is silently skipped. Arbitrary deletion of a trailing suffix is not detectable without
an independent watermark; protecting committed files remains a storage requirement.

The format is now `cohesive-assurance-file-capture-example/v2`. A directory containing the old v1
`capture.json` is explicitly rejected instead of treated as empty or overwritten. Existing examples
must use a new directory or an explicitly designed migration; automatic migration is outside this change.

An acknowledgment failure leaves a committed receipt. Redelivery can acknowledge it again without
adding observations. The acknowledgment callback must be synchronous and must not re-enter the host.
The host is intended for one source/atomic semantic scope, not concurrent distributed partitions.

This is **process-crash recovery under atomic local rename**, not a power-loss guarantee. File data is
flushed, but parent-directory fsync and network/filesystem-specific durability have not been qualified.
Tests inject exceptions at the persistence boundaries; they do not kill a process or simulate disk
hardware failures. The journal is not authenticated: schema/checkpoint validation is not protection
against an adversary rewriting the whole history consistently.

## Quarantine versus backpressure

Malformed envelopes, wrong activity source, semantic binding mismatch, and payload-size rejection are
permanent under the current input/configuration. The host retains the input and diagnostic in the
journal with the old monitor checkpoint, publishes that quarantine receipt, then acknowledges delivery.
A later valid delivery can proceed. `HasCaptureGaps` remains true, and quarantine never supplies a
completeness certificate or means the rejected observations were evaluated.

If quarantine cannot be persisted, the input is not acknowledged. Monitor budget exhaustion, semantic
ordering/identity failures, cancellation, and host storage/capacity failures do not automatically become
quarantine receipts. The source must retain them while the operator resolves capacity or correctness.
No observations are silently evicted. Correcting gaps or replaying quarantined inputs under revised
contracts requires a separately attributed workflow; it is not implemented here.

## Bounds and next steps

Defaults are 256 deliveries and 16 MiB summed across committed files. These are retention bounds;
pending/lock files are not included. Every receipt still retains its cumulative checkpoint, so storage
and restoration can grow quadratically with history even though each commit is written only once.
Periodic checkpoints, log compaction and checkpoint deltas are deliberately deferred. Raw input and
commit encoding occur before the encoded byte limit is checked; this is not a strict heap bound.
There is still no production throughput or power-loss qualification.
Journal limits are host policy, distinct from monitor row/envelope limits and adapter payload limits.

The example starts with no completeness certificates and never adds them. Retention of sampled evidence
cannot prove storage isolation, complete production coverage, or universal correctness.

Still outstanding in #558: a deployed durable source/exporter and remote OTLP decoder, storage-specific
power-loss qualification, retention/compaction, correction propagation, qualified completeness protocols,
rich operation/transaction histories, and executable faulty/repaired Simulation reconstruction. A replay
of a captured artifact remains trace re-evaluation until a candidate executes and emits new observations.

## Measured overhead

The [original end-to-end baseline](../performance/assurance/end-to-end.md)
and [immutable-commit comparison](../performance/assurance/journal-persistence.md) measure the native SDK,
monitor, journal and restore paths across payload sizes, claim shapes, history sizes and delivery batches.
It includes all raw local trials and identifies batching, encoding guards and journal write amplification
as optimization targets. These are serial service-cost measurements, not a production throughput SLO.
