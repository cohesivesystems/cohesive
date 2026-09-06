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

## Prepared-command reuse

COH-97 adds explicit `SqliteCommandScope` ownership within a caller's active transaction. Templates remain immutable;
the scope lazily retains one native command and parameter set per template instance, disposing them together. A
SQLite authorizer test verifies one preparation for eight repeated writes, versus eight with fresh commands. Scope
tests also cover null rebinding, invalid and canceled input, constraint failures, active-reader disposal, transaction
affinity and independent concurrent owners.

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*SqliteCommandReuseBenchmarks*' --job short --exporters json
```

Measured September 5, 2026 (Pacific), macOS 26.6.2, Arm64, .NET 10.0.5, BenchmarkDotNet 0.15.8, three measured iterations:

| Strategy | Rows | Mean | Allocated |
| --- | ---: | ---: | ---: |
| Fresh command per row | 1 | 3.645 µs | 2.27 KB |
| Operation scope | 1 | 3.732 µs | 2.57 KB |
| Multi-row candidate | 1 | 3.662 µs | 2.39 KB |
| Fresh command per row | 100 | 118.554 µs | 110.55 KB |
| Operation scope | 100 | 44.965 µs | 47.43 KB |
| Multi-row candidate | 100 | 281.202 µs | 74.40 KB |
| Fresh command per row | 1,000 | 1,171.273 µs | 1,094.93 KB |
| Operation scope | 1,000 | 415.304 µs | 455.24 KB |
| Multi-row candidate | 1,000 | 20,643.627 µs | 729.01 KB |

Every operation inserts integer IDs, 512-byte payloads and constant text in one immediate transaction on an open
WAL file, then rolls back within the timed method. SQL construction and file initialization are outside timing;
command creation, parameter binding, native execution and rollback are inside. No native command survives between
operations. This isolates execution overhead and excludes commit/fsync, acquisition, application serialization,
indexes beyond the primary key, read-back validation and contention.

The scope reduces mean time by 62–65% and allocation by 57–58% at 100/1,000 rows. One row pays about 0.09 µs and
0.30 KB of scope overhead in this short run. The benchmark-only multi-row candidate binds three uniquely named
parameters per row and prepares one large statement per operation; it is slower here. This is evidence against
adding that particular realization now, not a claim that every multi-row strategy is slower. Bounded chunks or
different binding strategies would require their own representative measurements. The 1,000-row candidate's
standard deviation was 495 µs; short-run confidence intervals are wide. Application-level results may differ.
