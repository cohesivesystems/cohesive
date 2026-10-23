# Observation value baselines and first execution optimizations

October 18, 2026; macOS 27.0.1 arm64, SDK 10.0.402/runtime 10.0.13, Release.
The [semantic decision](../decisions/observation-value-optimization.md) records the rules preserved
by this increment. [Commands and workload boundaries](../../eng/runtime-performance/observation-values/README.md)
describe reproduction, ownership, required result allocation and retained-heap limitations.

## Scope

This establishes the initial baseline suite and implements the first measured part of stage 2:

- Remove JSON container capacity pre-scans. Small objects use the existing bounded field storage;
  larger objects promote to the existing owned dictionary. Arrays build with inline/pooled scratch,
  then copy once into exact immutable retained storage. All rentals clear references on every exit.
- Reduce aggregates directly over existing source arrays. Selector aggregates use pooled scratch,
  deliberately retaining complete selector evaluation before reduction and its first-failure behavior.
- Resolve expression fields through core's exact name lookup rather than enumerating the dictionary
  through another implementation. This removes per-field enumerator allocation and wide-field scans.

The two new construction helpers are internal and have distinct ownership responsibilities. They
introduce no new public value representation or semantic type hierarchy. Both token and element
decoders use the same small-object capacity authority. Numeric reduction is shared with the existing
aggregate operation; comparer, numeric and diagnostic semantics remain in their existing authorities.
No external dependency or new application-level cache is introduced.

Further response/checkpoint codec work, broader projection preparation and reusable join indexes
remain follow-on work. Arbitrary CLR response projections cannot be bypassed without preserving their
behavior, and unknown/opaque contracts prevent universally replacing lossless tags with plain JSON.
Existing prepared projection and immutable interpretation mechanisms remain the starting points.

## Matched representation measurements

The same benchmark binary and dependency closure run in separate control/candidate directories;
only Core and Relations DLLs differ. Control production sources are commit
`a5a6d5a208c70ff1a986e3100b2b08e2677a0e1c`. The
[manifest](evidence/observation-values-stage1/manifest.json) identifies both binary pairs, the identical
benchmark binary, candidate source hashes and consumer artifacts. Initial exploratory runs are excluded.
The final control and candidate runs did not overlap builds, tests, tracing or other benchmarks.

BenchmarkDotNet uses in-process emit, three warmups, five measured iterations and a 250 ms target.
Setup prepares immutable inputs and verifies expected values outside timing. These are local operation
measurements, not deployment latency guarantees. Allocation includes required retained results.

| Operation | Control mean | Candidate mean | Control B/op | Candidate B/op |
| --- | ---: | ---: | ---: | ---: |
| Parse sixteen flat fields | 846 ns | 611 ns | 1,680 | 1,680 |
| Parse 32 nested objects | 15.27 μs | 1.74 μs | 12,184 | 4,720 |
| Parse 128 records | 72.19 μs | 30.48 μs | 99,730 | 60,570 |
| Parse 4,096 records | 3.61 ms | 1.86 ms | 3,180,153 | 1,947,364 |
| Parse/map/write, 128 records | 80.48 μs | 35.31 μs | 114,418 | 60,786 |
| Parse/map/write, 4,096 records | 3.67 ms | 1.84 ms | 3,639,642 | 1,955,538 |
| Sum 4,096 scalars | 77.68 μs | 34.29 μs | 131,393 | 81 |
| Sum 4,096 selected fields | 219.68 μs | 131.39 μs | 459,073 | 152 |

The nested direct parser is about 8.8 times faster in this workload. Selector aggregation eliminates
the allocation proportional to source cardinality, but still requires O(n) pooled working storage
because selector failures precede reduction failures. Warm allocation ceilings are not guarantees
that a concurrent or cold pool rental allocates nothing.

The unchanged element projector remains useful: its bounded name/string reuse gives it lower
allocation on repeated-record input than the new direct reader. The large direct parser still
allocates about 1.95 MB versus the element route's 1.35 MB. This identifies shared token decoding
and traversal-local string reuse as a follow-on experiment, not evidence to replace every element
path blindly. The element projector's randomized bounded cache can also vary across fresh processes.

Full distributions, GC data and exact measurements:
[control pipeline](evidence/observation-values-stage1/control-pipeline.md),
[candidate pipeline](evidence/observation-values-stage1/candidate-pipeline.md),
[control aggregates](evidence/observation-values-stage1/control-aggregates.md),
[candidate aggregates](evidence/observation-values-stage1/candidate-aggregates.md).
Corresponding full JSON exports are retained in the same evidence directory.

## Real EDI data plane

Five alternating fresh-process pairs at each size, each with separate definition preparation,
first execution, twenty warmups and seven measured batches of four instances. Each warm operation
freshly parses EDI, admits supplied roots, evaluates the accepted native relation and emits the
complete mapped JSON. One exact compilation request is reused, so warm execution does not
recompile the definition per instance. Medians below are medians of each process's batch medians.

| Stops | Input bytes | Output bytes | Control warm B/op | Candidate warm B/op | Allocation change | Control ms/op | Candidate ms/op |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 395 | 2,622 | 2,467,742 | 2,450,206 | -0.7% | 5.751 | 5.476 |
| 16 | 1,263 | 8,376 | 2,938,356 | 2,878,290 | -2.0% | 6.948 | 6.723 |
| 64 | 4,264 | 28,128 | 4,563,108 | 4,349,620 | -4.7% | 10.783 | 10.402 |

The timing differences are directional observations, not a production latency claim. The input
expansion is synthetic and explicitly bounded; no claim is made about production size distributions.
All mapped output hashes match between every control/candidate batch and process.
[All counters](evidence/observation-values-stage1/edi-counters.csv) include cold preparation and
first execution. [Raw retained-heap samples](evidence/observation-values-stage1/edi-retention.json)
separately retain wire outputs and parsed values; they include cache/GC variation and are not an
exact object census. Wire size is measured; complete Process checkpoint size is not measured here.

## Original X12 control plane and tradeoffs

The existing Ari repository-transition harness remains intact. Five alternating process pairs run
the complete public case cold and then again with a fresh fixture sharing process preparation.
The harness/closure is identical except the same Core/Relations substitution used above.

| Case | Control cold B | Candidate cold B | Control repeated B | Candidate repeated B | Control repeated ms | Candidate repeated ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 004010 | 245,113,848 | 245,111,984 | 78,349,928 | 78,232,920 | 853.057 | 844.964 |
| 005030 | 255,584,480 | 254,049,872 | 79,440,608 | 82,975,928 | 1,013.396 | 762.081 |

**The 005030 repeated case allocates 4.5% more despite lower elapsed time.** These changes do not
establish a universal control-plane allocation improvement. The first pass prioritizes removing
repeated subtree traversal and per-element temporary allocations, with substantial measured data-plane
and nested-read benefits. The artifact workload remains a qualification signal, not a result to hide.
Its process-to-process variance and allocation tradeoff need attribution before claiming an authoring
allocation win. No causal CPU/allocation profile is inferred from these counters alone.

[All original-workload counters](evidence/observation-values-stage1/protocol-counters.csv).
Both graph fingerprints remain byte-for-byte identical across the two runtimes:
[fingerprints](evidence/observation-values-stage1/protocol-fingerprints.txt).

## Verification

Full suites: **4,953 Core tests passed, 54 skipped; 1,141 Relations tests passed**. New conformance
tests cover numeric/hash equivalence, temporal offset semantics, presence, lossy/lossless codecs,
subtree sharing, generated nested JSON, exact numeric kinds, segmented UTF-8, duplicate decoded
names, small/dictionary/pool boundaries, malformed-input reuse, selector failure order, overflow,
ordinal field matching and warm allocation ceilings. Existing shape-bound, union, canonical and
durable suites remain active. Benchmark setup verifies both decoder results and aggregate identities.

Capacity pre-scans are removed; union discriminator lookahead remains necessary for typed case
selection. Ordinary JSON, lossless tags, canonical encodings and fingerprint format versions are
unchanged. No contextual validation or external admission is skipped.
