# Periodic checkpoints and compaction

Measured November 16, 2026 (America/Los_Angeles), comparing the v2 immutable-commit host at
`655dff15` with v3 periodic checkpoints and compacted accepted-observation deltas. Both use the same
original 30-case matrix; v3 adds two near-full replay-tail cases. Runs use three measured trials per case,
.NET 10.0.13, macOS 27.0.1 Arm64, 18 logical processors,
and `DOTNET_TieredCompilation=0`. This remains a local serial-service test, not production load qualification.

## Preserved contract and deliberate change

Each delivery still publishes its source position, receipt, raw input and provenance before acknowledgment.
The stored accepted delta is derived from the monitor's newly accepted suffix, including zero new rows
for duplicate/empty accepted deliveries. Replay uses the core capture entry point and must reproduce the
receipt's exact checkpoint reference. Quarantine carries no delta and retains the prior checkpoint reference.

Every 32 deliveries, one full monitor checkpoint plus **all compact receipts** is atomically published.
Only then are covered delta files deleted. There is no separate cursor update. Compacted receipts retain
raw input, producer references, cumulative/result references and rejection diagnostics, so old redelivery
identity checks and capture gaps survive. Historical full checkpoint payloads are replaced by references;
recovery verifies the current checkpoint and replays the remaining tail. This does not remove independently
retained Assurance evidence artifacts or evict the monitor's observation history.

The source/receipt cap remains lifetime-bounded, not an automatically sliding retention window. Headroom
is required for the new checkpoint and covered files to coexist within the committed-byte cap; without it,
optional compaction is skipped and the already-durable delivery may still be acknowledged. Pending files
are excluded from that cap. Commit/checkpoint/deletion failure makes the host unavailable until reopened.
Cleanup resumes only after the published checkpoint and the complete surviving tail validate.

The format is v3; older v1/v2 journals fail explicitly rather than being silently reset. See the
[capture-host contract](../../assurance/capture-host.md) for file layout, migration, lifetime limits and
failure cases. Production directory-fsync/power-loss durability is still unqualified: deletion requires
truly durable checkpoint publication on a production store, beyond this local process-crash profile.

## Results

For 256 matched observations with one row per delivery, medians across three trials:

| Metric | V2: checkpoint per receipt | V3: interval 32 + compaction |
|---|---:|---:|
| Total service time | 1.83 s | 1.53 s |
| Retained storage | 16.96 MiB | 0.74 MiB |
| Restore time | 1758.2 ms | 22.9 ms |
| Restore allocation | 359.0 MiB | 7.2 MiB |
| Service allocation | 98.5 MiB | 45.3 MiB |
| Logical bytes published | 16.96 MiB | 4.39 MiB |
| Per-delivery consumer p95 | 10.63 ms | 6.83 ms |
| Per-delivery consumer p99 | 14.68 ms | 19.99 ms |

Storage is 22.8× smaller and restore is
76.9× faster in this run. The unchanged matched
in-memory control measured 241.1 →
248.1 ms. No outliers were removed.

**Periodic work increases some acknowledgment-latency spikes.** Compaction executes synchronously before
acknowledgment on a checkpoint delivery. Average service cost/storage/recovery improve, but the measured
per-delivery p99 is higher. These short-run order statistics are not production p99 SLOs. Interval 32 is
a declared reference policy, not an optimized default for every latency budget.

All following cases contain 256 observations; values are v2 → v3 medians:

| Shape | Rows/delivery | Padding chars | Service ms | Retained MiB | Restore ms |
|---|---:|---:|---:|---:|---:|
| matched | 1 | 32 | 1832.0 → 1531.0 | 16.96 → 0.74 | 1758.2 → 22.9 |
| pending | 1 | 32 | 2444.0 → 1594.7 | 24.94 → 0.80 | 3109.3 → 26.7 |
| state | 1 | 32 | 1859.0 → 1610.1 | 21.57 → 0.80 | 2233.6 → 25.8 |
| matched | 16 | 32 | 109.9 → 95.5 | 1.23 → 0.27 | 119.7 → 27.1 |
| pending | 16 | 32 | 121.0 → 94.4 | 1.76 → 0.27 | 146.6 → 29.6 |
| state | 16 | 32 | 122.5 → 95.8 | 1.56 → 0.31 | 159.6 → 31.7 |
| matched | 16 | 2048 | 145.2 → 99.6 | 5.91 → 1.26 | 181.8 → 37.7 |

Batch-16 cases have only 16 deliveries, below the checkpoint interval. They restore their complete delta
tail; their gains come from storing/replaying accepted deltas rather than cumulative checkpoint artifacts,
not from a checkpoint publication that never occurred. Single-row 128/256-row cases publish 4/8 checkpoints
and finish exactly on a checkpoint. No checkpoint is forced during disposal or outside measured service time.


### Nearly full replay tails

The additional phase cases restore the checkpoint at position 224 and replay the remaining tail. These measure ordinary near-boundary recovery, not a hard latency bound after failed or skipped checkpoints.

| Shape | Rows / tail deliveries | Service ms | Retained MiB | Restore median [min–max] ms |
|---|---:|---:|---:|---:|
| matched | 254 / 30 | 1499.4 | 0.77 | 77.0 [75.5–85.2] |
| pending | 255 / 31 | 1544.3 | 0.83 | 91.2 [90.6–94.2] |

## Recovery qualification

All **49 integration tests** pass, including 27 host/recovery cases. Coverage includes:

- crash points before/after checkpoint publication and before/after deletion, plus replacement of an
  existing checkpoint; a committed but unacknowledged delivery survives every case;
- old-position redelivery and changed-input rejection after compaction;
- quarantined and empty deliveries, including exact preservation of a corrected initial monitor;
- corrupted checkpoint rejection without fallback to obsolete logs, and altered-delta/result mismatch;
- absent checkpoint headroom retaining the durable tail without deletion or false acknowledgment;
- logical publication-byte accounting against actual delta and replacement-checkpoint file sizes;
- earlier delivery publication, acknowledgment-loss, bounds, ordering and ownership regressions.

These are injected process-boundary exceptions, not process-kill or hardware fault tests. A corrupt published
checkpoint fails closed; obsolete logs may already be deleted. Storage protection against arbitrary suffix
or checkpoint deletion, authenticated history, directory durability and distributed ownership remain outside
this local reference profile.

## Reproduction, accounting and variability

Run the [existing harness](../../../eng/assurance-e2e-bench/README.md):

```sh
DOTNET_TieredCompilation=0 dotnet run --project eng/assurance-e2e-bench -c Release -- /tmp/compacted-v3.json
python3 eng/assurance-e2e-bench/summarize.py /tmp/compacted-v3.json
```

All [96 primary trials](e2e-local-compacted-v3.json) are retained. A preceding run launched alongside tests
showed slower unchanged controls; all [90 preceding trials](e2e-local-compacted-v3-pilot.json) are retained
separately, as is the [90-trial repeat without concurrent tests](e2e-local-compacted-v3-aligned.json).
The primary run then added two phase-sensitivity cases and launched no concurrent test runner;
the machine was not otherwise isolated from background activity. Three trials establish a local baseline, not statistical confidence or a
production capacity promise.

V3 logical writes use the monotone `PublishedBytes` counter: delta files **plus** replacement checkpoints,
without subtracting deleted files. `CommittedBytes` measures current retained files. Tests cross-check both
against actual published file sizes. This avoids falsely crediting compaction with negative writes. Physical
disk amplification, directory metadata I/O and waiting to fill a batch are not included in logical byte counts.
All trial verdicts, rows, exports, acknowledgments and restored checkpoint identities are verified.

## Remaining work

The core monitor still re-evaluates growing history, and result-reference computation serializes current
monitor state. Raw receipts and the current checkpoint also grow with session history. Incremental evaluation,
production storage realization, interval tuning against offered-load acknowledgment budgets, durable OTLP
capture and executable Simulation reconstruction remain separate work. Compaction does not invent
completeness or make the bounded source session unbounded.
