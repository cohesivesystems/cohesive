# Pooled observation decoding

Core's public `ObservationValue.Deserialize<T>` writes its existing plain-value JSON contract
through `PooledByteBufferWriter` and deserializes the UTF-8 span before returning pooled storage.
It no longer creates an owned growing byte buffer and UTF-16 `GetRawText` string. Relations'
`HostedQueryValueAdapter.Decode` delegates to that boundary rather than building an intermediate
JsonDocument. Fresh portable-value admission, exact contract matching, CLR deserializer options,
binary encoding policy and conversion diagnostics remain in their existing owners. Results own
any retained JSON storage; inputs, results and contextual/admission evidence are not cached.

Converted dictionary keys use the shared declared JSON projection instead of materializing a
JsonNode. Keys must still project to a nonblank string; numeric, null and object results remain
unsupported. Core's existing pooled writer and projection are reused, without an Ari-local bridge
or a second typed conversion authority.

## Duplicate rejection

The .NET 10 declared serializer profile sets `AllowDuplicateProperties = false`. A direct local
probe confirmed this also rejects duplicate properties written by a custom converter through
`SerializeToDocument`, including equal-valued duplicates. This moves rejection to parsing and
removes the builder's Add/count special case and duplicate-mode propagation through the walk.
Malformed converter JSON now throws JsonException instead of the earlier ArgumentException.
Public `FromJsonElement` retains its last-property-wins behavior for independently parsed tokens.
Root and property duplicate regressions explicitly cover the intentional exception change.

Native rejection performs additional preparation: the flat 128-field guard measures 20,384 bytes,
compared with approximately 16 KB under the previous builder check. This tradeoff is qualified
with before/after benchmarks and the complete Ari case rather than hidden behind a difference
from the much larger node-tree path.

## Deterministic allocation ceilings

The fixtures warm four calls, then measure seven synchronous current-thread allocation samples
and assert the median against fixed limits. Inputs, expected values and assertions are outside
the measured calls. Successful typed/observation result allocation is included.

| Fixture | Fixed ceiling |
|---|---:|
| Declared projection, 1,024 rows | 960 bytes per row |
| Declared projection, 128 flat fields | 176 bytes per field |
| Public typed decode, 1,024 rows | 296 bytes per row |
| Hosted typed decode including fresh admission, 1,024 rows | 304 bytes per row |

These are representative fixture budgets, not universal per-row costs. The row projection fixture
starts with immutable JSON tokens, matching the benchmark. Its short warmed unit-test median is
about 886 KB; the 983,040-byte ceiling leaves roughly 11% headroom. It does not assume the longer
BenchmarkDotNet run's lower allocation level. The flat guard retains its raw-string converter
input and includes that cost. Old JSON paths remain
only as semantic differential or opt-in performance references; ordinary allocation guards do
not compare their allocations or depend on their improvement. Tests retain converters,
polymorphism, Unicode/numeric boundaries, JSON lifetime, options, bytes policy and failure cleanup.

## Workload and reproduction

October 11, 2026, Apple M5 Max/macOS arm64, .NET SDK 10.0.201/runtime 10.0.5, Release.
The parent is Cohesive 0815da16 (#416); Ari is b5d64ae (#210). Matched source dependency assemblies
are fixed except Core and Relations. Each fresh process executes the complete normal X12 004010
public compile-shape test, then repeats it in-process. `GC.GetTotalAllocatedBytes(precise: true)`
includes catalog, seeds, preview, execution, assertions and cleanup. No concurrent CPU work runs
during case measurements or opt-in benchmarks. Source-module qualification is not published
package adoption, retained-heap or UAT qualification.

```bash
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release
dotnet run --project eng/runtime-performance/value-conversion/ValueConversion.csproj -c Release -- --filter '*DeclaredProjection*' '*TypedValueDecode*' '*HostedAdapterDecode*'
```

The dedicated value-conversion project separates hashing from projection/decode. Core benchmarks
compare text, scoped document and public decoding with the same CLR results; the method named
PooledUtf8 calls Core.Deserialize, which still used text on the parent. Hosted benchmarks call the
real adapter with fresh admission. No copied validation oracle can drift from production. Report
construction remains outside measurement; final result and required admission remain inside it.

## Measured results

Matched complete-case allocations fall from 488,237,376 to 481,937,032 bytes cold, and from
222,743,896 to 216,940,528 bytes repeated: 6.3 MB (1.3%) and 5.8 MB (2.6%) respectively.
[Case rows](evidence/pooled-observation-decoding/ari-case.csv) preserve single elapsed samples;
no end-to-end latency claim is established.

BenchmarkDotNet 0.15.8 ShortRun, three measured iterations; allocated KB uses 1024 bytes:

| Workload | Parent allocation | Final allocation | Parent mean | Final mean |
|---|---:|---:|---:|---:|
| Public typed decode, 1 row | 1.63 KB | 1.17 KB | 577.2 ns | 540.8 ns |
| Public typed decode, 1,024 rows | 703.17 KB | 281.04 KB | 405.39 μs | 355.82 μs |
| Real hosted adapter, 1 row | 1.40 KB | 1.44 KB | 804.5 ns | 641.4 ns |
| Real hosted adapter, 1,024 rows | 281.31 KB | 281.35 KB | 477.995 μs | 390.567 μs |
| Declared projection, flat 128 fields | 16.02 KB | 19.73 KB | 12.439 μs | 14.062 μs |
| Declared projection, nested 24 levels | 10.37 KB | 9.16 KB | 4.653 μs | 4.973 μs |
| Declared projection, 1,024 rows | 864.92 KB | 736.99 KB | 443.320 μs | 471.737 μs |

Public decoding removes about 60% of allocated bytes for 1,024 rows. Hosted decode allocates
roughly the same as the already-scoped document path but avoids its intermediate parse, improving
mean time about 18% in this fixture. Native duplicate rejection has a measured projection tradeoff:
flat allocation rises 23%, while nested and row-heavy allocation fall 12% and 15%; projection means
rise 6–13%. We select one parser-owned malformed-JSON mechanism and reduced allocation for the
row-heavy product workload, explicitly retaining this small-workload cost. Broader throughput
qualification and published-package UAT remain separate.

[Parent projection](evidence/pooled-observation-decoding/parent-declaredprojection.csv),
[final projection](evidence/pooled-observation-decoding/final-declaredprojection.csv),
[parent Core decode](evidence/pooled-observation-decoding/parent-typedvaluedecode.csv),
[final Core decode](evidence/pooled-observation-decoding/final-typedvaluedecode.csv),
[parent adapter](evidence/pooled-observation-decoding/parent-hostedadapterdecode.csv) and
[final adapter](evidence/pooled-observation-decoding/final-hostedadapterdecode.csv) retain uncertainty.

## Guard qualification

All four fixed ceilings pass on final source and fail against the original #414 (8152b3ec)
Core/Relations assemblies with the same final test DLL: row projection 1,937,560 bytes,
flat projection 37,880 bytes, public decode 719,952 bytes and hosted decode 465,152 bytes.
Temporary overlays were restored after the negative run. These tests protect the original
allocation wins without calculating a delta against a copied implementation.

The short warmed test and long-running BenchmarkDotNet allocation levels differ. The final row
ceiling uses the measured test boundary with 11% headroom, rather than assuming that the benchmark's
lower allocation level applies to the unit-test run.

Full qualification: Core/integration 4,434 passed with 33 skips; Relations 1,111 passed.
Ari source-module qualification passes 852 engine tests with 18 skips. Twenty precompiled test
repository wrappers lack the newer IEntityRepository.IdentityField member; the exact failure set
matches the unchanged baseline. There are no new failures. Both opt-in conversion and the now
independent canonical-hash projects build successfully. Temporary source overlays are restored.

## Bounded observation and annotation ownership

JSON element projection keeps sorted immutable trees for objects with at most eight properties.
Wider objects use the existing owned dictionary and scoped ordinal ordering, preserving case-sensitive
keys, last-property-wins acquisition, ordinal enumeration, and independence from the source document.
No additional cache or storage abstraction is introduced.

`AnnotationMapJsonConverter` is an explicit opt-in for already-buffered messages. Core's internal
CLR-contract options use it; general strict document options do not. Maps with at most 64 KiB of
encoded JSON share one owned snapshot, including parser metadata. Retaining an individual annotation
can therefore retain its whole small map. Larger maps retain independent value snapshots. All maps
are parsed as complete values, so streaming callers must budget temporary memory for the whole map.
Custom key/value/JSON converters and reference-handling policies retain ordinary serializer behavior.
Empty maps return the shared immutable empty map with zero warm allocation.

The opt-in BenchmarkDotNet ShortRun on .NET 10, macOS Arm64 measures retained results with inputs
prepared outside the measurement. A 128-field object falls from 15,520 to 12,088 allocated bytes;
a 4,096-field object falls from 491,680 to 416,864. Tiny collection-heavy inputs retain the tree
representation. The 16-field case allocates less (2,080 to 1,680 bytes) but is slower in this short
run (684 to 1,103 ns); this is an allocation/CPU tradeoff, not a universal throughput improvement.
A 32-entry annotation map falls from 19,328 to 12,776 bytes. Timing uncertainty and read/write
results are retained in the compact [object report](evidence/pooled-observation-decoding/json-element-ownership.csv)
and [annotation report](evidence/pooled-observation-decoding/annotation-map-ownership.csv).

Regression tests use median-of-seven fixed allocation ceilings after warmup and cover nested values,
disposed sources, duplicate and escaped keys, nulls, custom converters, and reference metadata.
Timing runs remain opt-in:

```bash
dotnet run -c Release --project eng/runtime-performance/value-conversion -- --filter '*JsonElementProjectionBenchmarks*' '*AnnotationMapBenchmarks*'
```

The original Ari X12 004010 repository-transition case was measured in five alternating isolated
process pairs against published alpha.137 and a private package of this change, using the same Ari
source. Median total allocations were 443,503,920 → 434,124,544 bytes cold and
186,147,088 → 176,855,960 bytes repeated (5.0% lower repeated allocation). Median elapsed time
was 3,233 → 3,210 ms cold and 1,304 → 1,274 ms repeated; these variable short samples establish
allocation reduction rather than a durable latency claim. The
[case samples](evidence/pooled-observation-decoding/ari-json-ownership.csv) retain all measurements.
This is private-package qualification, not published-package adoption. Existing canonical spec hashes
remain the acceptance criterion before adoption.

Full qualification passed 5,614 Cohesive tests (33 existing skips) and 1,298 Ari tests
(18 existing skips); both canonical X12 spec hashes matched alpha.137. Ari's temporary override
was removed after measurement. A sampled repeated-case trace still attributes about 18.5 MB to
JSON object acquisition (including retained strings and tiny tree nodes), 10.1 MB to annotation-map
acquisition, and 13.6 MB to byte-array growth under canonical property writing. These are disjoint
nearest-application-frame samples, not exact totals or proof that retained values can be eliminated.
The next investigation should distinguish repeated typed projection from necessary retained results
and examine canonical output buffer growth.

## Pooled temporary canonical output

The three canonical byte-returning paths now use Core's existing `PooledByteBufferWriter` for
invocation-scoped temporary output. They still copy once into the required caller-owned result;
no returned byte array borrows pooled memory. Writer disposal precedes buffer disposal, and rented
storage is cleared on success and failure. Streaming hashes already use their own pooled sink.

An isolated benchmark runs the same exact-number traversal and encoder against either a growing
array or the pooled writer. Allocations fall from 552 to 272 bytes for one row, 41,273 to 10,456
for 128 rows, and 1,446,367 to 335,773 for 4,096 rows. ShortRun timing is comparable (the largest
case 1.263 to 1.210 ms), with uncertainty retained in the
[compact report](evidence/pooled-observation-decoding/canonical-output-buffer.csv).
Results include the returned payload. Tests cover both node and element output ownership and use
fixed warm ceilings of payload length plus 1 KiB; the streaming hash ceiling is independently 1 KiB.
Existing differential, failure and concurrent canonical tests remain in force.

The public `GetCanonicalSequenceText` entry point applies the same profile, encoder and exclusions
directly to pooled output before allocating its owned UTF-16 string. Ari's protocol conformance
serializer can use it instead of a MemoryStream. This is a reusable Core output capability;
Ari retains protocol serialization policy. No temporary adapter or alternate canonical walk is needed.
Malformed value and property-name escapes still fail, and temporary storage is disposed on failure.

```bash
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*CanonicalOutputBufferBenchmarks*'
```

The isolated lexical text benchmark includes the owned UTF-16 result and explicit default encoder.
MemoryStream output versus pooled text allocates 1,016 → 392 bytes for one row,
59,354 → 25,577 for 128 rows, and 1,877,607 → 827,229 for 4,096 rows. The one-row timing is
similar but slightly slower in the short sample (371 → 384 ns); the largest case measures
1.327 → 1.282 ms. The [text report](evidence/pooled-observation-decoding/canonical-text-buffer.csv)
retains uncertainty. Run with `--filter '*CanonicalTextBufferBenchmarks*'` in the same project.
Final Core qualification passes 5,621 tests with 33 existing skips. The local Ari text adoption
passes 1,298 tests with 18 existing skips, with both canonical X12 hashes unchanged. Regenerating
the Ari acceptance report changes only its Cohesive.Protocols source-provenance pin.

Five isolated alternating X12 process pairs compare the #420 ownership package with this Core
change plus the Ari text adoption. Median allocations fall from 434,235,280 to 403,181,816 bytes
cold and 176,796,800 to 156,308,984 repeated (20.5 MB, 11.6% repeated reduction). Median elapsed
time is 3,068 → 3,094 ms cold and 1,252 → 1,315 ms repeated: the repeated sample is slightly
slower, so this establishes an allocation win, not an end-to-end latency win. All
[original-case samples](evidence/pooled-observation-decoding/ari-canonical-text.csv) are retained.
These results include a private Core package and a locally tested two-line Ari adoption. The
adoption is dependency-ordered after Core merge/publication; private feed/version overrides are
removed after qualification. Byte-output pooling alone saves about 0.2 MB repeated; most of the
case gain requires moving Ari's protocol canonical text output off MemoryStream.

The final sampled repeated-case trace no longer puts canonical property-writing byte-array growth
among its largest buckets. Disjoint nearest application frames instead attribute about 18.5 MB to
observation object acquisition, 9.3 MB to other JSON-element observation projection, and 8.6 MB to
annotation-map acquisition. Most object acquisition is downstream of hosted typed-result encoding.
These samples include necessary owned values; the next investigation should examine repeated
conversion at that boundary before introducing any reuse or changing serialization semantics.

## Compact small-object observations

JSON objects with one through eight properties use a privately owned sorted field vector instead
of a sorted tree. Empty objects share empty storage; wider objects retain the existing owned
dictionary. The vector has bounded linear key lookup, and its mutable array never escapes its
owner. Names remain value-owned, rather than creating a global field-name cache or inventing a
shape identity for arbitrary JSON. Deep source independence, ordinal enumeration, case-sensitive
keys and last-property-wins token acquisition are unchanged.

The existing shared field traversal recognizes this immutable storage and visits it without boxing.
Canonical ordering borrows its already-sorted fields directly, including in streaming frames.
Portable descendant validation uses that same traversal and ordering helper, removing its separate
storage switches and successful diagnostic sorting through LINQ. Contextual validation remains fresh.

Using the same opt-in projection benchmark, four-field acquisition allocates 376 bytes versus
640 for the sorted-tree reference (the prior public hybrid path was 576). Eight fields allocate
696 versus 1,120. The 128-object nested workload allocates 60,136 versus 97,360 bytes, and 4,096
nested rows allocate 2,883,723 versus 4,358,329. Rows measure 2.20 versus 3.18 ms in this ShortRun;
this is a projection microbenchmark, not an end-to-end latency claim. Wider storage is unchanged,
including its previously reported 16-field CPU tradeoff. The
[compact report](evidence/pooled-observation-decoding/compact-small-fields.csv) retains uncertainty.

Fixed median-of-seven ceilings cover four fields, eight fields, and 128 nested four-field objects.
The canonical hash guard also samples seven warm operations instead of relying on one pool rental:
occasional shared-pool replenishment is distinct from per-operation temporary allocation growth.

Five isolated alternating original X12 pairs compare the prior canonical-text package and the
compact-field package with the same Ari source. Median allocations fall from 403,275,040 to
395,798,064 bytes cold and 156,348,560 to 148,697,728 repeated (7.7 MB, 4.9% repeated reduction).
Median time falls from 3,053 to 2,873 ms cold and 1,232 to 1,073 ms repeated; these are local
short-run results, not CI timing thresholds. The
[case samples](evidence/pooled-observation-decoding/ari-compact-fields.csv) retain every measurement.
Full implementation qualification passes 5,624 Cohesive tests (33 existing skips) and 1,298 Ari
tests (18 existing skips), with both X12 canonical hashes unchanged. Three subsequently added
regressions also pass in the 81-test focused validator/projection run. They cover deterministic
failure ordering on small/wide storage and zero warm allocation for nested portable validation.
The Ari private version and adoption are removed after qualification, pending Core publication.

The final sampled repeated trace replaces the earlier 18.5 MB object/tree bucket with about
7.9 MB under compact field acquisition. Its largest remaining nearest-frame buckets are annotation
map acquisition (9.8 MB), other element projection (8.5 MB), opaque protocol-value decoding
(7.3 MB), protocol document deserialization (6.0 MB) and fingerprint text construction. These
are approximate disjoint samples and include required owned results. Repeated opaque protocol
parsing and fingerprint text materialization are the next lifetime/work-amplification candidates.
