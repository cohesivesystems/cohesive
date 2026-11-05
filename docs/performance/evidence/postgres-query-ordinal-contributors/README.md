# PostgreSQL attributed query-row ordinal ingestion

Execution source: `de5fe899`. This experiment adds query-row join-contributor reconstruction;
it is separate from the earlier entity snapshot benchmark.

The native compiler retains hidden physical source identities and outer presence through row-preserving
joins and projections. The reader binds the exact semantic plan revision, streams through the shared
provider executor, validates scalar and complete-shape contracts, and retains owned values, exact source
contributors and DTOs. Dictionary and ordinal variants differ only in field storage. No provenance is
omitted from either side. See `manifest.json` for the operating boundary and source hashes.

## Local results

Three alternating repeats per case, fresh process each, 4 warmups and six 200 ms iterations, 1024 left
source rows. PostgreSQL 14.23 in Docker with loopback TCP; no concurrent local builds/tests/profiling.
Setup, compilation and serialization excluded; pool checkout and real SQL execution included.

| Join | Dictionary ms | Ordinal ms | Dictionary bytes | Ordinal bytes |
| --- | ---: | ---: | ---: | ---: |
| Inner | 13.683 | 13.463 | 3,138,939 | 2,713,163 |
| Left | 13.785 | 13.987 | 4,133,666 | 3,530,380 |

Allocation falls 13.6% / 14.6%. Latency changes are small and mixed (+/- about 1.6%); no general latency
improvement is established. Contributor reconstruction costs remain on both paths. Repeated timings and
all raw BenchmarkDotNet JSON are retained, rather than selecting the best run. `measure.py` records the
exact local invocation and temporary output paths; set the database environment variable and adapt the
binary/output locations to reproduce elsewhere.

## Verification

75 targeted live PostgreSQL tests pass, including inner/left joins, fan-out, same-shape self-joins,
null/missing/outer absence, hidden identity consistency, exact-plan revision mismatch, artifact roundtrip,
row/hidden-byte bounds, cancellation, and refusal to silently discard lineage. Values and contributor sets
match the reference interpreter. Aggregation and DISTINCT exact-contributor compilation fail explicitly.
All 1200 relations tests pass. All 12 final benchmark Dry smoke cases pass. The strict Release benchmark
build has zero warnings/errors. Generated capability and frontend contract currentness checks pass.

The complete core suite has 5306 passes, 68 environment-dependent skips and one failure:
`LogicalReplication_RejectsTamperingAndConfirmedAheadBeforeStreaming` expects an `ArgumentException`
which is not thrown. It also fails in isolation. Its replication test/path is unchanged by this work;
this suite is not reported as green. Logs are archived in `verification-logs.tar.gz`.

The disposable local database was removed after validation.

## Linux CI results

[Run 41947214718](https://github.com/cohesivesystems/cohesive/actions/runs/41947214718) completed all
12 Short-job cases on source `de5fe899`, Ubuntu 26.04.1 / AMD EPYC 7763 / .NET 10.0.14,
against the workflow's disposable PostgreSQL 14 service. All cases have valid statistics.

| Left-source rows | Join | Dictionary us | Ordinal us | Dictionary bytes | Ordinal bytes |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Inner | 450.834 | 451.199 | 10,782 | 10,269 |
| 1 | Left | 493.401 | 491.852 | 12,098 | 11,585 |
| 32 | Inner | 584.905 | 587.057 | 104,825 | 91,315 |
| 32 | Left | 672.571 | 630.035 | 135,123 | 116,432 |
| 1024 | Inner | 39,272.306 | 35,765.848 | 3,139,274 | 2,715,219 |
| 1024 | Left | 37,175.545 | 36,777.249 | 4,133,739 | 3,530,571 |

At 1024 rows, allocation falls 13.5% / 14.6%; observed latency falls 8.9% / 1.1%. The Short-job sample
and flat/mixed local results do not establish a universal latency improvement. The consistent finding is
lower allocation with equivalent state and contributors. These are synthetic join pipelines with network
and provenance costs; prior entity/SQLite results have different boundaries and are not interchangeable.
Raw JSON/CSV, environment and logs are in `ci/raw-artifacts.tar.gz`; normalized CSV and summaries are also
checked in. The workflow completed successfully and removed its service.
