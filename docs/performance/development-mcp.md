# Semantic workspace request costs

The workspace validates exact content before and after each operation. A request reads the complete bundle twice
and every declared input twice, even on a retained snapshot. Size/mtime caching would weaken this contract:
a same-length edit with a restored timestamp must still populate `changedInputs`. The checks do not detect
change-and-revert races or establish an atomic filesystem snapshot.

## Reproducible BenchmarkDotNet run

Run from the repository root:

```sh
dotnet run -c Release --project eng/benchmarks/development-mcp -- --filter '*' --warmupCount 3 --iterationCount 8 --launchCount 1
```

The benchmark project references the approved repository version of BenchmarkDotNet, 0.15.8. Its memory diagnoser
measures warm workspace and small-pointer inspection calls directly, excluding MCP transport, cold snapshot
parsing, domain validation and export. Each input is 4 KiB. Each case has one synthetic execution document and
uses the same fixture sizes as the initial probe below. Setup exports and loads the snapshot before measurement.
Files are in a local temporary directory and OS file caches are warm. BenchmarkDotNet selects invocation counts;
the recorded job uses one launch, three warmup iterations and eight measurement iterations per case.

## BenchmarkDotNet results

Recorded on 2026-11-03 on Apple M5 Max, macOS 27.0.1 Arm64, .NET SDK 10.0.402 / runtime 10.0.13.
All six cases completed. The [generated report](development-mcp-benchmarkdotnet.md) retains environment,
mean, 99.9% confidence-interval half-width (`Error`), standard deviation, collection counts and allocation data.

| Inputs | Bundle bytes | Operation | Mean | Error | Allocated per call |
| ---: | ---: | --- | ---: | ---: | ---: |
| 8 | 17,752 | workspace | 0.2813 ms | 0.00641 ms | 45.46 KiB |
| 8 | 17,752 | inspect | 0.2776 ms | 0.00406 ms | 46.16 KiB |
| 128 | 1,059,570 | workspace | 4.8070 ms | 0.23604 ms | 1,370.83 KiB |
| 128 | 1,059,570 | inspect | 4.8019 ms | 0.25424 ms | 1,371.48 KiB |
| 4,096 | 16,064,138 | workspace | 155.2547 ms | 3.21740 ms | 26,191.88 KiB |
| 4,096 | 16,064,138 | inspect | 156.1664 ms | 1.22347 ms | 26,192.50 KiB |

These are local warm-call measurements with one process launch per case, not production latency guarantees.
They quantify the remaining full-content freshness cost. They do not measure cold disks, concurrent callers,
transport overhead, large selected subtrees or expensive domain validators.

## Initial allocation comparison

The following historical measurements used the dependency-free probe committed in `54ac5416` before the
BenchmarkDotNet reference was approved. Reproduce that probe from that revision. It used five warmups and twenty
individually timed calls, measuring allocation with `GC.GetAllocatedBytesForCurrentThread`. These measurements
are kept separate from BenchmarkDotNet results; they must not be compared as equivalent timing samples.

Recorded on 2026-11-03, macOS 27.0.1 Arm64, .NET 10.0.13, Release. Baseline is the initial PR implementation's
request path. Updated code streams raw file hashes, reuses `StrictDocumentJson.ComputeCanonicalSha256` for
canonical digests, and normalizes inspection JSON once per retained snapshot.

| Inputs | Bundle bytes | Operation | Baseline bytes allocated | Updated bytes allocated | Baseline median / p95 ms | Updated median / p95 ms |
| ---: | ---: | --- | ---: | ---: | ---: | ---: |
| 8 | 17,752 | workspace | 198,816 | 46,032 | 0.299 / 0.309 | 0.342 / 0.412 |
| 8 | 17,752 | inspect | 295,056 | 46,744 | 0.336 / 0.344 | 0.323 / 0.389 |
| 128 | 1,059,570 | workspace | 4,557,986 | 1,398,812 | 5.194 / 5.971 | 7.334 / 8.703 |
| 128 | 1,059,570 | inspect | 9,816,800 | 1,399,502 | 7.849 / 8.260 | 7.758 / 8.509 |
| 4,096 | 16,064,138 | workspace | 109,803,606 | 26,689,498 | 204.202 / 255.773 | 167.475 / 237.992 |
| 4,096 | 16,064,138 | inspect | 188,460,064 | 26,690,272 | 181.991 / 228.899 | 164.088 / 181.787 |

These timing samples expose request cost, not a controlled latency comparison or speedup claim. In particular,
the medium workspace case was slower in this run. Timing-sensitive optimization decisions require a controlled
BenchmarkDotNet study under the repository quality protocol. The run above characterizes the updated
implementation; the historical baseline has not been rerun under BenchmarkDotNet, so no speedup is claimed.

## Decision and remaining costs

The demonstrated allocation reduction justifies fixed-buffer file hashing and snapshot-owned inspection JSON.
A regression test verifies that two freshness passes over an 8 MiB input do not allocate input-sized buffers.
Canonical digest parity, same-size/restored-timestamp edits, tampering, and before/after validation freshness
remain covered. The existing shared canonical hash implementation owns canonicalization; the local file routine
only bounds reads and feeds raw bytes to the platform SHA-256 implementation.

This is still O(total declared bytes + file count) I/O per operation. The first bundle read still allocates a
bundle-sized byte array; path resolution and filesystem checks allocate per file. Cold snapshot loading now also
normalizes and retains inspection JSON, amortizing that work across calls. The unnormalized bundle document
array is discarded after loading; at most four snapshots are retained. Large selected subtrees still need a
size check. The 4,096-file case is expensive enough that callers should keep export scopes focused.

Metadata caches, filesystem journals, asynchronous reads and incremental refresh are deferred. Any such change
must state its freshness guarantees explicitly and be measured; it must not silently turn metadata equality into
proof of content equality. Static `learn` guidance performs no workspace I/O.
