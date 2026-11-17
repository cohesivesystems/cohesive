# Journal persistence: immutable commit comparison

Measured November 16, 2026 (America/Los_Angeles), using the same 30-case/three-trial matrix,
.NET 10.0.13, macOS 27.0.1 Arm64, 18 logical processors, and `DOTNET_TieredCompilation=0`
as the [stored baseline](end-to-end.md). All [90 new measured trials](e2e-local-append-v2.json)
are retained. This is a local serial-service comparison, not a production load/SLO qualification.

## Change and preserved boundary

The host now publishes one immutable `capture-NNNNNNNNNN.json` per delivery. Each version-2
commit contains the source binding, exact initial-checkpoint reference, input, receipt, cumulative
monitor checkpoint and position. Flush the pending file, rename without overwrite, then acknowledge.
Earlier commits are untouched. No separate cursor/manifest transaction is needed. Quarantine uses
the same publication boundary with an unchanged monitor checkpoint.

The original v1 single-file journal is explicitly rejected on reopen; no accepted history is silently
reset or migrated. Existing artifacts require a separate migration decision or a new directory.
The retained-byte bound now sums committed files. Pending files are not commits, and gaps before
later commits or invalid/corrupt committed files fail recovery. Source redelivery after publication but
before acknowledgment reuses the committed receipt without writing or evaluating it again.

Tests cover both sides of publication, acknowledgment failure, failed quarantine persistence, prior
file content/timestamp preservation, publication collisions, torn pending input, missing committed
positions, aggregate byte limits, incompatible legacy files, and exact restored checkpoints. All 36
OpenTelemetry integration tests pass; 14 are host/recovery cases. The benchmark also checks every
trial's rows, verdicts, export/acknowledgment counts, no gaps, and restored checkpoint identity.

## Measured result

For 256 matched rows, one row per delivery:

- Median total service time: **13.61 s → 1.83 s**
  (7.4× faster).
- Managed allocation: **1600.7 MiB → 98.5 MiB**.
- Logical writes: **1517.8 MiB → 17.0 MiB**
  (89.5× less).
- Median run's per-delivery consumer p95: **130.0 ms → 10.6 ms**.
- Retained storage: **16.9 MiB → 17.0 MiB**.
  This optimization removes rewriting, not cumulative-checkpoint retention.

All rows below process 256 observations; values are before → after medians of three trials.

| Claim shape | Rows/delivery | Padding chars | Service ms | Allocated MiB | Logical writes MiB |
|---|---:|---:|---:|---:|---:|
| matched | 1 | 32 | 13610.1 → 1832.0 | 1600.7 → 98.5 | 1517.8 → 17.0 |
| pending | 1 | 32 | 18950.1 → 2444.0 | 2304.5 → 126.4 | 2201.7 → 24.9 |
| state | 1 | 32 | 16820.5 → 1859.0 | 2024.3 → 133.2 | 1911.4 → 21.6 |
| matched | 16 | 32 | 160.9 → 109.9 | 14.2 → 7.5 | 7.9 → 1.2 |
| pending | 16 | 32 | 191.8 → 121.0 | 18.7 → 9.4 | 11.1 → 1.8 |
| state | 16 | 32 | 184.4 → 122.5 | 18.6 → 10.2 | 10.0 → 1.6 |
| matched | 16 | 2048 | 313.9 → 145.2 | 60.0 → 28.7 | 37.2 → 5.9 |

The matched single-row service range after the change was
1.82–2.33 s.
The unchanged matched in-memory control was 256.8 →
241.1 ms, showing ordinary run variation. There is no
claim of statistical significance from three runs; the logical write reduction follows directly
from writing one commit instead of repeatedly encoding all previous receipts.

## Recovery tradeoff and remaining cost

Restore still opens and validates every retained cumulative checkpoint. It did **not** improve;
latency was higher and more variable in this run. Per-file reads add overhead, and this run cannot
separate that from host/runtime noise. The regression is reported rather than hidden by write gains.
These rows use 256 observations with one row per delivery:

| Shape | Before restore ms | After median [min–max] ms | Restore allocation MiB before → after |
|---|---:|---:|---:|
| matched | 1619 | 1758 [1751–3312] | 353.9 → 359.0 |
| pending | 2195 | 3109 [2504–4102] | 509.0 → 514.1 |
| state | 2117 | 2234 [2218–2320] | 482.6 → 487.6 |

Per-delivery checkpoint construction and the reference monitor's batch re-evaluation still grow with
history. Periodic checkpoints plus replayable deltas/compaction are the next persistence candidate if
restore or retained storage is the priority; incremental evaluation is the next CPU candidate. Both
need their own semantic and crash-recovery qualification. The current bounded journal retains all
receipts and acknowledges only after their atomic publication, so this change does not weaken recovery
to obtain better timings.

The power-loss/directory-fsync and remote-filesystem limits are unchanged. Arbitrary deletion of a
committed trailing suffix is not detectable without an external watermark; local storage must protect
committed files. There is still one cooperating writer and a bounded file count.

## Reproduce

Use the [same runner and methodology](../../../eng/assurance-e2e-bench/README.md):

```sh
DOTNET_TieredCompilation=0 dotnet run --project eng/assurance-e2e-bench -c Release -- /tmp/append-v2.json
python3 eng/assurance-e2e-bench/summarize.py /tmp/append-v2.json
```

The sole measurement adaptation is logical-write accounting: v1 sums snapshot-file sizes on each
rewrite; v2 sums growth in committed bytes, validated against actual file sizes in the tests. Namespace
metadata writes and physical filesystem write amplification are not measured. Steady-state measured
work still includes snapshot creation, SDK handoff, decoding, evaluation, commit encoding, flush,
publication and acknowledgment; restore is separately timed. Scenario definitions, trial counts,
JIT configuration, row/payload limits and correctness checks are unchanged.
