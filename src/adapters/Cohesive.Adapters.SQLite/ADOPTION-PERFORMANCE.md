# COH-89 foundational runtime measurements

Measured 2026-09-05 with BenchmarkDotNet 0.15.8, .NET SDK 10.0.201 / runtime 10.0.5,
Arm64 RyuJIT, Apple M5 Max, macOS 26.6.2. Short job: one launch, one warmup iteration,
three measured iterations. These are local diagnostic measurements, not CI timing thresholds.
Other Cohesive tests/builds were stopped during the measured workload.

Source: [AdoptionValueBenchmarks.cs](../../Cohesive.Relations.Benchmarks/AdoptionValueBenchmarks.cs).
Reproduce from the repository root:

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*Adoption*Benchmarks*' --job short --warmupCount 1 --iterationCount 3 --launchCount 1 --artifacts /tmp/coh-89-bench
```

## Lossless detached value serialization

Each operation canonicalizes one stored value and returns a newly owned UTF-8 byte array. This
models per-receipt/per-fingerprint work, not per-field materialization. Global setup constructs
the immutable input and warms serializer metadata outside measurements. Both paths use the
existing `StrictDocumentJson` canonical writer. The new profile delegates to the existing
PortableValue tagged node codec; there is no new scalar encoding algorithm.

| Workload | Plain JSON mean ± SD | Tagged JSON mean ± SD | Plain allocated | Tagged allocated |
| --- | ---: | ---: | ---: | ---: |
| Flat object, 16 alternating string/int64 fields | 2.838 ± 0.147 µs | 7.062 ± 0.061 µs | 11.82 KiB | 28.45 KiB |
| Nested object, four children of 16 fields | 10.794 ± 0.086 µs | 29.566 ± 0.248 µs | 32.88 KiB | 105.42 KiB |
| Array of 4,096 int64 values | 332.847 ± 2.994 µs | 2,073.902 ± 20.876 µs | 1,206.71 KiB | 4,659.15 KiB |

The baseline covers values both writers can encode, but plain JSON cannot retain all their original
scalar kinds. It cannot encode native byte values under the default policy at all. Tagged JSON costs
2.49–6.23× warm time and 2.41–3.86× managed allocation here. The conceptual priority is exact durable
evidence over the smaller lossy format. This profile is explicitly selected for entity receipt
persistence/fingerprinting; ordinary JSON and native entity-row encoding are unchanged.

Reported allocation includes the retained byte array **and** temporary JSON nodes/canonicalization
buffers. It is not a retained-state measurement. The 4,096-item case demonstrates that payload byte
limits do not bound total managed overhead at a 1:1 ratio. SQLite checks the retained BLOB length
before allocating its read buffer and bounds aggregate canonical bytes per page; writes can allocate
a larger candidate before rejecting it. Streaming canonicalization for large retained evidence is
intentionally deferred. No end-to-end database throughput improvement is claimed.

## Native byte materialization

Each operation materializes an immutable record containing a mutable byte array. The output must
own its bytes. Compiled plans, reflection metadata, and input construction are warmed in global
setup; they are excluded from the table. The prior default path threw for native bytes, so the
baseline is the explicit JSON/base64 converter a caller could previously supply, not a working
old default implementation.

| Bytes | JSON workaround mean ± SD | Direct mean ± SD | Workaround allocated | Direct allocated |
| ---: | ---: | ---: | ---: | ---: |
| 32 | 110.49 ± 0.87 ns | 16.38 ± 0.07 ns | 760 B | 80 B |
| 65,536 | 59.205 ± 3.677 µs | 1.329 ± 0.024 µs | 677,717 B | 65,584 B |

The direct path retains the record and one owned byte array, with no JSON text or temporary object
graph on successful conversion. Small-sample timing uncertainty is especially large for the 64 KiB
workaround; allocation and output ownership are the stronger deterministic evidence.

`CoreObservationMaterializationTests` enforces `payload length + 128 bytes` as a generous allocation
ceiling after warmup for 0, 32, and 65,536 bytes, and verifies output mutation cannot change the
observation or later materializations. Nested records/collections have independent ownership tests.
`PortableValueTests` verifies every `ObservationValueKind` is represented and that the detached
codec emits exactly the same tagged node bytes as PortableValue, then round-trips it. Existing
SQLite receipt tests enforce oversized-read/write/page failure and atomic rollback.
