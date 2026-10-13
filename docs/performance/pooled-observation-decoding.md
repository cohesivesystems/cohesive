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
