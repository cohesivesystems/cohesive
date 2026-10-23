# Observation boundary conversion and checkpoint measurements

October 18, 2026; macOS 27.0.1 arm64, SDK 10.0.402/runtime 10.0.13, Release.
This follows the [first measured increment](observation-values-stage1.md) and preserves the
[semantic decision](../decisions/observation-value-optimization.md). Results below compare against
that increment, rather than adding its gains a second time.

## Changes and operating boundaries

- Tagged portable values decode directly from tokens, using the existing small-object storage and
  pooled array builder. Binary decoding transfers its newly owned buffer without a second copy.
  Arbitrary member order remains supported: payload-before-kind buffers only the deferred payload.
  The lossless wire tags, kind semantics and writer are unchanged.
- Endpoint registration prepares an explicit frozen response profile through the existing projection
  compiler. Exact conventional members avoid value → JSON → CLR, including copying large strings.
  Mutable results remain independent. User projection callbacks and final HTTP serialization remain
  active. Custom metadata/converters, case variants and unsupported contracts keep serializer paths.
- A bounded whole-input check preserves JSON depth and encodability constraints even for ignored
  members. It pays an extra structural traversal; it neither caches results nor establishes admission.
  A shared allocation-free UTF-16 validator also preserves malformed-string normalization across
  prepared readers, annotations and declared projections.
- Compact checkpoint text uses the existing canonical writer and pooled staging, removing one
  owned full-size canonical byte array. Internal checkpoint operations reuse frozen framework
  serializer profiles and their metadata. Caller-created options remain mutable and independent.
  The temporary serializer document, complete canonical-wire validation and exact-plan recovery
  admission remain. No store/network interface or fingerprint format changes.

The implementation adds one public profile overload and one canonical-text operation, extending
existing authorities. Internal construction helpers and field visitors express physical ownership
and traversal; no alternative semantic value hierarchy, external dependency or global content cache
is introduced. Broader recursive layouts, generated views, storage streams, string interning and a
new binary identity format remain separate measured work.

## Measurement method

The identical benchmark binary runs with isolated control/candidate production closures. Control
Core/Relations binaries are the first increment's candidate; control Storage predates the boundary
changes. The evidence manifest records exact binary and source hashes. Both closures check exact
lossless round trips and complete checkpoint bytes in setup. Timing excludes fixture preparation.

BenchmarkDotNet uses in-process emit, three warmups, five measured iterations and a 250 ms target.
Runs are sequential and do not overlap builds, tests or tracing. Allocation includes required owned
results; it is not retained-heap measurement. Local timing is evidence about these bounded fixtures,
not a network or deployment latency guarantee. Endpoint benchmarks isolate response interpretation;
they exclude handler execution, authorization, projection callbacks and final HTTP encoding.

Reader preparation is measured separately with warm serializer metadata. It includes expression
compilation, but does not represent a fresh-process cold start. Checkpoint warm operations include
all runtime encoding or recovery work; one-time serializer metadata preparation happens in setup.
The baseline rebuilt that metadata on every call. Cold initialization must not be inferred from the
warm improvements.

After measurement, a final error-path audit restored `JsonException` wrapping when a failed
portable value contains invalid diagnostic evidence. The final 75 affected boundary/checkpoint
checks passed, including the added regression. This error-only adjustment was not rebenchmarked;
the manifest distinguishes measured source/binaries from final source and verification binaries.

## Matched results

Fresh lossless tagged payload decoding:

| Payload | Control mean | Candidate mean | Control B/op | Candidate B/op |
| --- | ---: | ---: | ---: | ---: |
| Flat | 1.947 μs | 0.956 μs | 2,224 | 1,496 |
| Nested 16 | 7.123 μs | 2.712 μs | 7,345 | 4,185 |
| 128 records | 149.279 μs | 62.981 μs | 159,975 | 88,973 |
| 4,096 records | 6.429 ms | 3.205 ms | 5,388,235 | 2,843,758 |

Prepared response interpretation, including 64 KiB of source text:

| Items | Compatibility mean | Prepared mean | Compatibility B/op | Prepared B/op |
| --- | ---: | ---: | ---: | ---: |
| 1 | 39.519 μs | 1.006 μs | 133,396 | 96 |
| 128 | 58.387 μs | 7.649 μs | 166,302 | 4,160 |
| 4,096 | 573.679 μs | 213.781 μs | 1,002,188 | 131,140 |

Preparing a new reader with warm metadata costs approximately 0.82–0.84 ms and 57 KB, independently
of source cardinality. Prepare once per endpoint registration, rather than per response. These
results include the structural safety gate. Required mutable result storage remains proportional
to item count; the retained source string is shared rather than duplicated.

Complete checkpoint text encoding and full decoding with recovery admission:

| Operation | Payload | Control mean | Candidate mean | Control B/op | Candidate B/op |
| --- | --- | ---: | ---: | ---: | ---: |
| Encode | Flat | 3.242 ms | 17.719 μs | 2,628,515 | 9,369 |
| Decode/admit | Flat | 3.341 ms | 59.080 μs | 2,678,931 | 37,534 |
| Encode | Nested 16 | 3.429 ms | 33.478 μs | 2,632,816 | 11,805 |
| Decode/admit | Nested 16 | 3.666 ms | 105.966 μs | 2,693,366 | 47,313 |
| Encode | 128 records | 3.700 ms | 284.873 μs | 2,742,831 | 78,933 |
| Decode/admit | 128 records | 4.338 ms | 887.756 μs | 2,972,897 | 219,854 |
| Encode | 4,096 records | 14.055 ms | 8.716 ms | 8,344,087 | 2,651,134 |
| Decode/admit | 4,096 records | 36.870 ms | 27.470 ms | 15,327,774 | 9,141,339 |

The small-checkpoint gains primarily expose previously repeated serializer metadata preparation;
they are not a claim that token decoding alone became 183 times faster. Large checkpoint encoding
allocates 68.2% less, while full large recovery allocates 40.4% less and remains substantially more
expensive than payload decoding. Canonical-wire validation, envelope reconstruction and exact-plan
admission are still part of that workload.

Full distributions, GC data and unrounded values are in the evidence reports:
[control payloads](evidence/observation-values-stage2/control-payload.md),
[candidate payloads](evidence/observation-values-stage2/candidate-payload.md),
[control checkpoints](evidence/observation-values-stage2/control-checkpoint.md),
[candidate checkpoints](evidence/observation-values-stage2/candidate-checkpoint.md), and
[prepared responses](evidence/observation-values-stage2/prepared-response.md).
Full JSON and CSV exports accompany them.

Complete canonical checkpoint wire sizes are unchanged:

| Payload | UTF-8 bytes |
| --- | ---: |
| Flat | 3,538 |
| Nested 16 | 4,754 |
| 128 records | 38,140 |
| 4,096 records | 1,117,436 |

The evidence retains SHA-256 of every complete checkpoint for both closures; equality is exact,
not merely equivalent parsed JSON. These are complete initial checkpoints for a simple return
Process, not a bound on checkpoints with histories, emissions or additional durable state.

## Verification and limits

Conformance covers every observation kind, segmented input, generated nested trees, arbitrary member
order, strict duplicate/unknown-member rejection, binary policy, serializer customization, name
collisions, mutable independence, malformed UTF-16, depth limits and invalid ignored values.
Exact canonical-text/byte differential fixtures cover all kinds and nested structures. Allocation
guards enforce single-copy binary decoding, independent response graphs with shared text, zero
temporary string-validation allocation and absence of an owned full-payload canonical byte copy.
Caller option isolation and concurrent checkpoint serialization are also verified.

Final suites: **4,991 Core tests passed, 54 skipped; 1,141 Relations tests passed**. The final Core
run disabled test-collection parallelism. An earlier parallel run passed 4,990 tests but hit an
existing warmed-pool allocation-ratio assertion in `ExecutionDefinitionNormalizationTests`.
That test and all 27 other focused checks passed in isolation; the full serialized run then passed.
Pool contention is a plausible cause, not an established allocation profile. Warm allocation
ceilings do not imply that a concurrent pool miss allocates nothing.

The existing X12 control-plane and real EDI mapping harnesses remain qualification workloads. Their
earlier tradeoffs remain documented in the first report; boundary microbenchmarks do not establish
a universal authoring or mapping allocation win.

## Consumer qualification

Three alternating fresh-process pairs preserve the original harnesses. EDI uses separate preparation
and first execution, twenty warmups and seven measured batches of four. The table reports medians
of each process's batch medians. All output hashes match across batches and processes.

| Stops | Control warm B/op | Candidate warm B/op | Control ms/op | Candidate ms/op |
| --- | ---: | ---: | ---: | ---: |
| 2 | 2,449,794 | 2,450,206 | 4.666 | 4.389 |
| 16 | 2,878,290 | 2,879,128 | 6.465 | 5.374 |
| 64 | 4,349,628 | 4,349,620 | 10.259 | 10.534 |

These allocations are effectively unchanged relative to the first increment; elapsed differences
are directional observations. Raw [EDI counters](evidence/observation-values-stage2/edi-counters.csv)
and [retained-heap samples](evidence/observation-values-stage2/edi-retention.json) retain cold and
warm phases. Retained samples include runtime/cache variation and are not an exact object census.

The original public repository-transition workload also runs cold and then with a fresh fixture
sharing process preparation:

| Case | Control cold B | Candidate cold B | Control repeated B | Candidate repeated B | Control repeated ms | Candidate repeated ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 004010 | 245,181,904 | 245,461,968 | 78,220,896 | 78,213,720 | 813.502 | 872.504 |
| 005030 | 255,442,912 | 255,568,880 | 79,337,944 | 79,379,592 | 961.419 | 968.559 |

Warm allocations are again effectively unchanged, while 004010 elapsed time is 7.3% higher in this
local sample. There is no universal authoring-speed claim. The first report's 005030 allocation
signal is not attributed or declared resolved by these noisy process measurements.
[All counters](evidence/observation-values-stage2/protocol-counters.csv) and
[identical graph fingerprints](evidence/observation-values-stage2/protocol-fingerprints.txt) are retained.

Only Core and Relations are substituted in these older prebuilt consumer closures. An initial
attempt to substitute current Storage failed before candidate measurement because Ari expects an
older `ProcessDurableRuntimeOptions` constructor. That attempt is excluded. No compatibility shim
or sibling-repository edit was introduced. Current Storage is qualified by the rebuilt matched
whole-checkpoint benchmark and the checkpoint conformance suite, not by this older consumer ABI.
