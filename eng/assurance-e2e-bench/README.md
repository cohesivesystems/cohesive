# Assurance end-to-end service-cost benchmark

Run from the repository root in Release:

```sh
DOTNET_TieredCompilation=0 dotnet run --project eng/assurance-e2e-bench -c Release -- /tmp/assurance-e2e.json
# Optional second argument selects one mode for a shorter run:
dotnet run --project eng/assurance-e2e-bench -c Release -- /tmp/assurance-monitor.json monitor
python3 eng/assurance-e2e-bench/summarize.py /tmp/assurance-e2e.json
```

The SDK version is the same 1.18.0 dependency already used by the OpenTelemetry test project.
The internal file host source is linked directly: this measures the real example's serialization,
flush, rename, receipt and checkpoint path rather than a simulated persistence callback.

## Workload matrix

- Baselines: plain SDK span; sampled-out with payload construction guarded by `IsAllDataRequested`;
  sampled-out with eager payload construction; exported semantic observations without a consumer.
- Consumers: in-memory `TraceMonitor` and `FileCaptureHost`, both through native SDK export and
  `AssuranceActivityAdapter.Apply` (the host invokes it internally).
- Shapes: inventory state invariant (`Reserved <= OnHand`), adjacent correlated paid/shipped pairs,
  and payment triggers with no shipment witnesses, which remain pending without completeness. All scopes stay open.
- Histories: 128 and 256 retained rows; deliveries of 1 or 16 rows. No session resets during a measured trial.
- Payloads: 32-character padding normally; a 2,048-character payload comparison at 256 rows/batch 16.

Claim creation, provider setup, initial monitor creation and directory/lock setup are outside the service
measurement. Each scenario executes a 16-row warmup, then three fresh-history trials. Tiered compilation
uses the runtime default unless overridden; warmup does not guarantee every tier has stabilized.
The documented full run disables tiered compilation for stable structural comparisons after a pilot
showed material tier-transition distortion. This differs from a normal tiered production runtime;
measure both configurations on the target deployment. All raw trials are retained, including outliers. Results should be repeated on the intended production
hardware/filesystem with competing workloads controlled before making capacity commitments.

The producer stage includes portable-value snapshot construction, envelope encoding, activity events,
and `SimpleActivityExportProcessor` handoff to an in-process exporter. The consumer runs separately
after the producer timer stops. This avoids charging evaluator and disk work to application instrumentation
while retaining both stage costs. One in-flight activity is handed off; there is no network transport,
collector, concurrent worker queue or request-serving application in this benchmark.

## Measurements and correctness

The output includes producer and consumer p50/p95/p99 per delivery, first/last-quarter consumer means,
summed service time and its implied attempted-row rate, process CPU, managed allocation, GC collection
counts, exported payload bytes, final journal size, cumulative logical journal bytes written and separate
restore time/allocation. Journal bytes are serialized file sizes, not physical device writes. Sampled-out
and plain-span rates describe attempted instrumentation, not observed/evaluated rows.

Allocation is the process-wide managed-byte delta around timed producer and consumer operations. It
includes small measurement-list overhead; it excludes setup, input-size accounting and restore. GC counts
and CPU include the whole delivery loop, including bookkeeping. These are process measurements, not CPU
attribution or peak live-memory measurements. Restore includes initial monitor recreation and full journal
opening/verification; it is excluded from service throughput and measured once per trial.

SDK export counts are checked. Consumers must retain exactly the expected rows with an inconclusive open
scope verdict. Matched responses must satisfy all observed obligations. Journal trials must acknowledge
every delivery without a gap, and restoration must reproduce the exact monitor checkpoint. Any mismatch
terminates the benchmark. Existing recovery tests cover failure semantics; this benchmark does not inject
failures into timed runs.

Percentiles are per-delivery order statistics from short serial trials, not production request latency
or load-test queueing percentiles. A batch of 16 has fewer samples and more work per sample than a batch
of one. Three-run summaries use the median of each run's metric, with total-time ranges to expose noise.
The implied service rate is not demonstrated sustainable ingress throughput. No numeric pass/fail SLO is
invented without deployment requirements.

The journal benchmark can allocate several GiB cumulatively while rewriting bounded histories. It creates
and deletes temporary directories; defaults in the benchmark allow up to 256 MiB per journal so the 256-row
matrix can complete without treating capacity rejection as successful throughput. That override does not
change the host's production/example defaults and is recorded here explicitly.

Not yet qualified: remote OTLP, concurrent partitions, burst queues/backpressure performance, failure/recovery
under live offered load, power-loss durability, very large catalogs/histories, or application request overhead.
