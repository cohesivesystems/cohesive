# End-to-end Assurance overhead: local baseline

Measured on November 16, 2026 (America/Los_Angeles). Runtime: .NET 10.0.13,
macOS 27.0.1, Arm64, 18 logical processors. Local temporary-directory filesystem;
no remote collector or storage service. Host implementation is the #577 reference example, based on
`73aa78d6`. The benchmark itself and all raw trials are retained with this report.

**The full file-journal path is not a low-overhead per-observation production realization.**
Batching materially reduces both monitor work and journal rewrites. The benchmark establishes local
service costs and optimization priorities, not a production ingress capacity or request-latency SLO.

## Main findings

- **Sampling must guard payload construction.** For 128 attempted observations, the guarded sampled-out
  path takes 0.05 ms total versus
  2.66 ms when the discarded payload is still constructed.
  Export-only observation production takes 2.65 ms total,
  with a median run's per-delivery p95 of 24.0 µs.
  These are SDK/encoding costs, not HTTP request overhead or remote export latency.
- **History growth costs real work.** Matched in-memory evaluation grows from
  69.4 ms at 128 rows to
  256.8 ms at 256 rows with one row per delivery.
  Within the 256-row trial, consumer time averages 0.34 ms in
  the first quarter and 1.60 ms in the last quarter.
- **Persistence dominates this reference host.** For 256 matched rows, the journal path takes
  13.61 s, allocates 1.56 GiB cumulatively,
  and rewrites 1.48 GiB of logical JSON. The final journal is only
  16.9 MiB. These are cumulative allocation and logical writes,
  not peak live memory or physical disk writes.
- **Existing batching is the largest immediate lever.** With 16 rows per delivery, the same 256-row
  journal case takes 160.9 ms and writes
  7.9 MiB. In-memory service cost becomes
  18.7 ms. This comparison excludes the time spent waiting
  to assemble a batch; production batching needs an explicit maximum detection/acknowledgment delay.
- **Recovery also needs a budget.** Reopening the 256-row single-row-delivery journal takes
  1619.1 ms and allocates
  353.9 MiB. Repeated cumulative checkpoints amplify restoration too.

## What to optimize next

1. Guard producer snapshot/encoding work with `IsAllDataRequested`, and establish a bounded batching
   policy with a maximum wait. The benchmark exercises existing functionality; it does not change
   production instrumentation or weaken completeness semantics.
2. Replace full-journal rewrite/per-receipt cumulative checkpoints with a qualified append-log or
   transactional store plus periodic checkpoints. Preserve the atomic receipt/position/evidence commit
   and quarantine-before-acknowledgment contract; rerun the same recovery cases and this matrix.
3. Add incremental monitor evaluation under the existing batch evaluator as a conformance oracle.
   Measure matched and pending obligations separately. Do not infer that faster I/O removes the
   history-growth cost visible in the in-memory cases.
4. Before setting deployment budgets, run a concurrent offered-load test against the actual SDK
   exporter/collector and durable source: bursts, queue growth, loss, backpressure and recovery under load.
   Measure application request latency independently. This in-process benchmark cannot substitute for that.

## Reproduction and interpretation

See [runner/methodology](../../../eng/assurance-e2e-bench/README.md). The matrix has 30 cases,
three measured trials per case, and a 16-row warmup before each case. The fixed-JIT run uses
`DOTNET_TieredCompilation=0` to avoid tier-transition contamination. A runtime-default pilot showed
substantial warmup/order effects (including a smaller monitor case slower than a larger one), so it
is retained as [pilot evidence](e2e-local-tiered.json), not silently mixed into the fixed-JIT comparison.
Disabling tiering differs from production PGO; these figures must not be treated as a target deployment forecast.
[All fixed-JIT trials](e2e-local-fixed-jit.json) include CPU, GC counts, exported payload bytes,
first/last-quarter timings, restoration allocation, and per-delivery p50/p95/p99.

Claim/lowering, SDK/provider setup, initial monitor and filesystem setup are excluded from service cost.
Producer and consumer stages are timed independently around real SDK handoff and adapter/host operations.
Restore is measured separately. Allocations are managed-byte deltas, not live-heap estimates. The implied
row rate in JSON divides attempted rows by summed serial service time; it is not sustained ingress throughput.
Payload construction and journal encoding check size after allocation. The benchmark raises host journal
capacity to 256 MiB solely so capacity rejection is not mistaken for throughput.

Every run checks export counts, retained rows, expected open-scope verdict, satisfied matched obligations,
acknowledgments, absence of capture gaps, and checkpoint identity after restoration. Percentiles are
short-run per-delivery order statistics; batch-16 cases have few samples. No production p99 claim is made.

The table reports medians across three runs; ranges show total service-time variation. No outlier was removed.

| Mode / shape | Rows | Batch | Padding | Service ms median [min–max] | Allocated MiB | Producer p95 µs | Consumer p95 ms | Logical writes MiB | Restore ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| plain-sdk / matched | 128 | 1 | 32 | 0.06 [0.05–0.06] | 0.06 | 0.90 | 0.00 | 0.00 | 0.00 |
| sampled-out / matched | 128 | 1 | 32 | 0.05 [0.05–0.05] | 0.06 | 0.80 | 0.00 | 0.00 | 0.00 |
| sampled-out-eager / matched | 128 | 1 | 32 | 2.66 [2.60–2.69] | 0.81 | 23.70 | 0.00 | 0.00 | 0.00 |
| export-only / matched | 128 | 1 | 32 | 2.65 [2.62–2.71] | 0.81 | 24.00 | 0.00 | 0.00 | 0.00 |
| monitor / matched | 128 | 1 | 32 | 69.44 [68.77–70.54] | 10.77 | 31.70 | 0.88 | 0.00 | 0.00 |
| journal / matched | 128 | 1 | 32 | 2448.30 [2435.44–2462.98] | 240.19 | 114.10 | 40.58 | 216.69 | 431.47 |
| monitor / pending | 128 | 1 | 32 | 81.23 [80.89–81.83] | 10.15 | 28.10 | 1.07 | 0.00 | 0.00 |
| monitor / state | 128 | 1 | 32 | 85.82 [85.80–86.02] | 13.73 | 32.30 | 1.16 | 0.00 | 0.00 |
| monitor / matched | 128 | 16 | 32 | 5.93 [5.88–6.17] | 1.10 | 117.60 | 1.01 | 0.00 | 0.00 |
| monitor / pending | 128 | 16 | 32 | 6.78 [6.62–7.09] | 1.06 | 108.80 | 1.19 | 0.00 | 0.00 |
| monitor / state | 128 | 16 | 32 | 9.02 [7.43–9.16] | 1.45 | 161.20 | 1.49 | 0.00 | 0.00 |
| monitor / matched | 256 | 1 | 32 | 256.77 [246.75–265.82] | 36.64 | 51.40 | 1.72 | 0.00 | 0.00 |
| monitor / pending | 256 | 1 | 32 | 297.21 [296.71–298.11] | 34.21 | 32.60 | 2.08 | 0.00 | 0.00 |
| monitor / state | 256 | 1 | 32 | 315.53 [314.71–315.71] | 48.17 | 33.50 | 2.20 | 0.00 | 0.00 |
| monitor / matched | 256 | 16 | 32 | 18.73 [18.72–18.93] | 3.15 | 112.00 | 1.87 | 0.00 | 0.00 |
| monitor / pending | 256 | 16 | 32 | 21.82 [21.50–22.11] | 2.99 | 115.00 | 2.19 | 0.00 | 0.00 |
| monitor / state | 256 | 16 | 32 | 24.49 [23.72–24.51] | 4.20 | 143.10 | 2.46 | 0.00 | 0.00 |
| journal / pending | 128 | 1 | 32 | 2963.27 [2955.95–2964.67] | 330.89 | 107.40 | 49.95 | 302.45 | 538.47 |
| journal / state | 128 | 1 | 32 | 2807.39 [2801.87–2821.24] | 296.18 | 119.30 | 46.89 | 265.16 | 548.41 |
| journal / matched | 128 | 16 | 32 | 52.58 [51.72–52.59] | 3.44 | 147.50 | 8.43 | 1.40 | 29.89 |
| journal / pending | 128 | 16 | 32 | 58.62 [55.81–59.74] | 4.25 | 164.70 | 10.69 | 1.86 | 36.54 |
| journal / state | 128 | 16 | 32 | 57.07 [55.91–61.27] | 4.44 | 180.50 | 9.73 | 1.71 | 37.23 |
| journal / matched | 256 | 1 | 32 | 13610.08 [13572.24–14162.51] | 1600.72 | 122.70 | 129.95 | 1517.83 | 1619.12 |
| journal / pending | 256 | 1 | 32 | 18950.11 [17834.10–19140.84] | 2304.45 | 155.00 | 186.69 | 2201.71 | 2194.76 |
| journal / state | 256 | 1 | 32 | 16820.49 [16631.33–17373.96] | 2024.34 | 136.50 | 164.74 | 1911.44 | 2116.90 |
| journal / matched | 256 | 16 | 32 | 160.87 [160.58–171.20] | 14.24 | 192.20 | 17.82 | 7.94 | 107.63 |
| journal / pending | 256 | 16 | 32 | 191.76 [190.28–197.38] | 18.73 | 200.50 | 22.49 | 11.12 | 134.42 |
| journal / state | 256 | 16 | 32 | 184.43 [183.66–187.59] | 18.56 | 214.20 | 21.56 | 9.96 | 137.65 |
| monitor / matched | 256 | 16 | 2048 | 24.67 [24.27–24.75] | 6.11 | 147.40 | 2.40 | 0.00 | 0.00 |
| journal / matched | 256 | 16 | 2048 | 313.94 [313.90–316.28] | 60.01 | 226.50 | 39.52 | 37.23 | 167.66 |
