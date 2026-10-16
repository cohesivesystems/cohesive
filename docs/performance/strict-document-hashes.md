# Strict document hashing without canonical output buffers

The current Process runtime omits eager whole-commit hashing and makes recurring continuation hashes Diagnostic-only; see
[durable bookkeeping](durable-bookkeeping.md). The canonical hash implementation qualified below remains available.


`StrictDocumentJson.ComputeCanonicalSha256` computes SHA-256 over exactly the bytes returned by
`GetCanonicalBytes`, without retaining that canonical output for case-sensitive contracts. It
serializes through the same declared type/options into invocation-owned immutable JSON, uses the
existing canonical sequence/exact-decimal walk, and feeds the existing pooled SHA-256 buffer writer.
The caller owns the 32-byte digest and its profile prefix. No value, digest or admission evidence
is cached. Case-insensitive options retain the existing node collision semantics and contiguous
fallback. Canonical ordering, duplicate rejection, numeric normalization and escaping have one
implementation shared with byte-producing callers.

Cohesive.Storage consumes this core operation for commit, checkpoint and content fingerprints.
For example, Ari's X12 004010 fixture makes two commits with one and two activation receipts;
their canonical commit JSON is approximately 4.1 and 8.8 MB. Previously each fingerprint grew
an output buffer and copied the entire canonical JSON before hashing. The new path retains the
same `sha256-v1` evidence without those full output allocations. Replay, fencing, source reads,
external admission and checkpoint compatibility checks remain unchanged.

This extends existing Core serialization and Storage responsibilities. A Storage-local hash sink
or another canonical walk would duplicate existing mechanisms. Incremental receipt-ledger hashing
is deliberately separate: avoiding the growing-history walk requires an explicit profile design.

A subsequent [checkpoint breakdown](hosted-value-projection.md) shows that the large envelopes
are dominated by operation values in continuation bindings and replay receipts. Activation trace
receipts are under 0.5% of this case. Growing-history hashing remains a general scaling concern,
but typed value conversion is the larger next allocation target in this workload.

## Local allocation evidence

October 11, 2026, Apple M5 Max/macOS arm64, .NET SDK 10.0.201/runtime 10.0.5, Release.
Ari fixture source is b5d64ae (#210), including #209's hash/provenance reuse. All variants use
Cohesive source dependencies at 9656b670 (#413). Only Core and Storage change in the final variant;
Cosmos, Processes, Transitions and Relations are held fixed. Matching source dependencies are
necessary because published alpha.131 adapters implement an older Storage interface.

Each variant runs in a fresh process, calls the complete public X12 004010 normal execution
fixture, then repeats it in the same process. GC.GetTotalAllocatedBytes(precise: true) measures the
whole call, including catalog, seed, preview, execution, assertions and cleanup. No concurrent
builds/tests/benchmarks ran during these samples. Assembly overlays were restored afterward.

| Variant | Cold allocated bytes | Repeated allocated bytes |
|---|---:|---:|
| Unchanged source baseline | 667,748,792 | 402,089,064 |
| Commit/checkpoint streaming only | 622,169,264 | 356,542,024 |
| All Storage content streaming | 561,271,936 | 295,424,472 |

The full fix saves 106.5 MB cold (15.9%) and 106.7 MB repeated (26.5%), using decimal MB.
Commit/checkpoint streaming contributes about 45.5 MB; other Storage content fingerprints contribute
another 61 MB. [Case measurements](evidence/strict-document-hashes/ari-case.csv) include single timing
samples for transparency, without establishing an end-to-end latency improvement. These are source
qualifications, not published-package or UAT results. The 667.7/402.1 MB source baseline differs from
#210's 724.6/411.9 MB baseline because this comparison also uses matching source Storage dependencies.

A separate temporary, subsequently removed probe measured allocations inside each baseline commit
fingerprint method using GC.GetAllocatedBytesForCurrentThread, ending before logging. Across the two
commits that is 98.9 MB cold and 45.4 MB repeated, over 12.9 MB canonical output per case. This is a
narrow attribution boundary; do not add it to the enclosing case totals. The
[probe rows](evidence/strict-document-hashes/commit-attribution.csv) retain counts and payload sizes.

The opt-in BenchmarkDotNet 0.15.8 ShortRun matrix uses three warmups and three measured iterations.
Payload construction is outside the benchmark; typed serialization, canonicalization, SHA-256 and
returned digest are inside it. Both methods operate on the same immutable JSON-backed contract.

| Payload | Contiguous allocation/op | Streaming allocation/op |
|---|---:|---:|
| 128 flat fields | 12,040 B | 5,672 B |
| 32 nested objects | 6,264 B | 888 B |
| 4,096 nested rows | 1,422,829 B | 724 B |
| One 262,144-character Unicode token | 1,049,640 B | 586 B |

[Benchmark report](evidence/strict-document-hashes/benchmarks.csv) retains means and uncertainty.
Short-run timings are approximately unchanged. Allocation counters exclude retained pool storage;
these results do not imply constant total memory. Typed JSON still needs payload-sized storage,
large individual tokens can grow the pooled sink, and every hash still walks its entire input.

## Qualification and reproduction

Differential tests hash the previous canonical bytes across every Observation kind, generated
nested values, exact-number boundaries, escaping, array sequence order and formatter options.
Regressions also cover duplicate/case-collision errors, null/depth/converter failures, mutable
inputs, concurrent independent sinks, and removal of full canonical output allocations. Storage
checks compare complete commits/checkpoints against canonical SHA-256 references and retain durable
replay, continuation reuse, tampering and recovery coverage.

The complete Cohesive suite passes 4,410 tests with 33 existing skips. Ari's source-overlay run
passes 852 tests with 18 existing skips, including all nine protocol-compilation cases, but has
20 interface-load failures in repositories compiled against alpha.131. A focused comparison of
those test classes on unchanged source produces exactly the same 20 failures (53 pass, one skip).
Those wrappers need rebuilding/adoption against current Storage's required IdentityField member;
this is an existing source-versus-published interface difference, not a hash regression. Packages
and Ari pins are unchanged in this PR.

```bash
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release
dotnet run --project eng/runtime-performance/canonical-hashes/CanonicalHashes.csproj -c Release -- --filter '*'
```

The Ari end-to-end case uses the existing opt-in harness in #210:
`eng/runtime-performance/protocol-preparation/ProtocolPreparation.csproj`. Build matching source
dependencies, overlay them into its output directory, execute its DLL in a fresh process per variant,
and restore all overlays. Ordinary runs without overlays use the pinned published packages.
