# Semantic workspace request costs

The workspace validates exact content before and after each operation. A request reads the complete bundle twice
and every declared input twice, even on a retained snapshot. Size/mtime caching would weaken this contract:
a same-length edit with a restored timestamp must still populate `changedInputs`. The checks do not detect
change-and-revert races or establish an atomic filesystem snapshot.

## Reproducible cost probe

Run from the repository root:

```sh
dotnet run -c Release --project eng/benchmarks/development-mcp
```

This dependency-free probe measures warm workspace and small-pointer inspection calls directly, excluding MCP
transport, cold snapshot parsing, domain validation and export. Each input is 4 KiB. Each case has one synthetic
execution document, five warmups and twenty individually timed calls. Files are in a local temporary directory;
OS file caches are warm. Allocation uses `GC.GetAllocatedBytesForCurrentThread` around the synchronous call.

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
BenchmarkDotNet study under the repository quality protocol; no new benchmark dependency is introduced here.

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
