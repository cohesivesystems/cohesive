# Own normalized JSON without temporary payload copies

## Evidence and decision

After immutable relation fingerprinting, a fresh-process allocation trace attributed approximately
127 MB of authoring and 39 MB of cold admission allocation to execution normalization stacks.
The trace was collected after commit `4b1c5d0`, with .NET 10.0.5 on Arm64 macOS, using runtime
GC allocation ticks and explicit authoring/cold/warm stage markers.
Parent type ordering accounted for approximately 30 MB and 24 MB respectively, overlapping those
normalization stacks. Sampled stack totals are estimates and cannot be added across nested operations.
The canonical writer itself showed substantial byte-array allocation from growing an ordinary
`ArrayBufferWriter<byte>`, in addition to property-name strings and exact-number normalization.

Normalization is a reusable Cohesive execution-document responsibility. Existing core
`PooledByteBufferWriter` already provides an exclusively owned temporary destination and clears
buffers on return; it fits without an Ari bridge or another buffering abstraction. The revised method
uses that writer, then `JsonElement.ParseValue` to create the owned immutable result before returning
pooled storage. Previously it parsed a temporary `JsonDocument` and cloned its root, after allocating
ordinary growing output buffers. Retained canonical document storage remains necessary.

Duplicate-property scanning, ordinal object ordering, exact decimal-rational normalization, sequence
ordering, depth limits and integrity checks remain unchanged. No trusted-import bypass, document cache
or altered fingerprint profile is introduced. The input is borrowed; the returned element owns its
bytes and survives input disposal and concurrent reuse of pooled working storage.

## Representative measurements

[Comparison report](owned-execution-normalization/comparison.md) records the environment and short-job
settings. Both paths include the same successful duplicate validation and canonical writer. Inputs
and reflection-resolved delegates are prepared outside measurement, pooled storage is warmed, and
each operation returns a newly owned element.

| Shape | Previous | Revised |
| --- | ---: | ---: |
| Flat | 1.74 KB | 1.40 KB |
| 24 nested objects | 7.80 KB | 3.22 KB |
| 128 rows | 113.41 KB | 100.59 KB |
| 4,096 rows | 3,737.75 KB | 3,196.70 KB |

These short local runs establish allocation comparisons, not production latency. Small-fixture timing
was slightly higher in these noisy samples; large-fixture timing improved. Reproduce with a longer run:

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ExecutionDefinitionNormalizationBenchmarks*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~ExecutionDefinitionNormalizationTests'
```

Allocation regressions require at least a 5% reduction versus the previous implementation at equal
validation boundaries for 128 and 4,096 rows. A separate ownership regression disposes the input and
normalizes other documents concurrently before reading the retained result. Existing serialization,
number, duplicate, fingerprint, admission and forged-metadata tests retain semantic coverage.

The Ari source qualification compares catalog authoring separately from first and warm admission.
A paired run measured:

| Boundary | Before | After |
| --- | ---: | ---: |
| Catalog authoring | 383,466,000 B | 316,455,128 B |
| First admission | 125,458,360 B | 104,806,608 B |
| Warm admission | 1,030,168 B | 893,512 B |

These are cumulative allocated bytes, not retained heap or release-package measurements. All 164
canonical catalog bodies remained byte-identical. No package pins changed; temporary test output
overlays are restored after qualification. Remaining candidates include CLR nullability metadata,
parent type projection, exact-number temporary strings and repeated property-name extraction.

Qualification passed 4,265 Core tests (33 existing skips), 1,082 Relations tests, and 855 Ari engine
tests (18 existing skips). Three focused allocation/ownership cases passed. No tests were excluded,
timeouts increased or packages published.
