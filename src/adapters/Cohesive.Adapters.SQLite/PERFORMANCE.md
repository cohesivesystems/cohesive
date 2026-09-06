# SQLite connection reuse evidence

COH-95 adds explicit provider pooling while retaining the original unpooled convention and profile checks on every
acquisition. The implementation uses the provider's existing pool, avoiding a second session or leasing abstraction.
Logical connection/transaction ownership remains per operation. Command templates separately cache SQL construction
and binding metadata; they do not cache mutable native commands or payloads.

## Reproduction

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*SqliteConnectionBenchmarks*' --job short --exporters json
```

The versioned `SqliteConnectionBenchmarks` fixture creates one file with a single row. Each operation opens a logical
connection, performs a SELECT in a deferred transaction or UPDATE RETURNING in an immediate transaction, commits,
and disposes. All paths use the same provider, file, WAL/FULL durability and timeout. The baseline uses direct pooled
provider connections and its prior three-PRAGMA setup. Verified paths use `SqliteDatabase` with pooling enabled or
disabled. Idle pooled handles keep the WAL open during repeated pooled operations, which is part of the lifetime
policy being measured. Setup and final pool cleanup are outside timing.

## Local short-run results

Measured September 5, 2026 (Pacific), macOS 26.6.2, Arm64, .NET 10.0.5, BenchmarkDotNet 0.15.8, three measured iterations:

| Acquisition | Read mean | Read allocation | Write mean | Write allocation |
| --- | ---: | ---: | ---: | ---: |
| Direct provider, pooled | 4.153 µs | 3.04 KB | 69.859 µs | 3.09 KB |
| Verified runtime, pooled | 7.357 µs | 6.54 KB | 78.880 µs | 6.59 KB |
| Verified runtime, unpooled | 245.831 µs | 9.87 KB | 435.406 µs | 9.91 KB |

Native handle reuse removes most of the acquisition cost in this workload. Verification still has a measurable
cost: roughly 3.2 µs and 3.5 KB on the tiny read compared with the direct-provider baseline. The design deliberately
retains those checks rather than assuming a prior borrower preserved the required profile.

These are short local measurements, not production throughput or tail-latency guarantees. Write standard deviation
was about 4.7 µs for the baseline and 30.9 µs for the unpooled runtime. The experiment does not separate native open,
WAL lifecycle and individual PRAGMA costs, nor measure multi-process contention or template binding in isolation.
Application workloads must also measure their own serialization, queries, batching, allocation and contention.

## Verification and deferred work

Real-file tests cover native-handle reuse, rollback of a provider-owned abandoned transaction, restoring foreign keys,
writable mode and synchronization, concurrent logical owners, FULL/NORMAL pool sharing, cancellation before acquisition
and explicit native-handle retirement. Template tests cover independent parameters, borrowed runtime bytes, isolated
constant bytes, duplicate/missing bindings, exact scalar-domain validation and bounded scalar subqueries.

Long-lived application sessions, automatic batching and global parameter/command caches are intentionally absent.
Each would introduce additional ownership or transactional behavior not required to address the measured regression.
