# Execution document canonicalization

## Trigger and ownership

Ari's dev Jobs startup spent 121.8 of 142.4 seconds preparing its Process catalog in one
recycle sample. Nested stages attributed 65.1 seconds to definition construction and 48.9 seconds
to compilation. These overlap their parent stage; they must not be added to it. Local allocation
sampling identified mutable JSON tree materialization and canonical traversal as a substantial
shared cost. This is a Cohesive execution-document concern, not Ari topology or runtime policy.

For example, constructing a training Process already produces immutable JSON. Previously each
normalization and fingerprint operation expanded that JSON into `JsonObject`/`JsonArray` nodes,
then allocated ordering intermediates throughout the graph. Execution canonicalization now walks
the existing `JsonElement` tree, renting per-object ordering storage and returning it in `finally`.
Arrays keep sequence order. Exact decimal-rational formatting uses the existing shared formatter.

The execution document remains the authority. Integrity validation, duplicate-property rejection,
semantic validation, revision identity, extension failure codes, and fingerprint format are unchanged.
No admission result is cached or bypassed. The internal traversal adds no public API. The general
mutable-node writer remains appropriate for authored nodes and path-dependent set ordering; the
immutable execution path has fixed sequence/exact-number semantics. Differential tests retain the
previous physical implementation as an oracle, covering every JSON kind, generated nested payloads,
escaping, huge exponents, successful extensions and failed extension identity.

Temporary property storage is cleared before returning to the shared array pool; it does not retain
document references. Property-name strings, numeric-token formatting and contiguous output buffers
still allocate. Returned bytes and normalized document storage necessarily remain caller-owned.
This is neither a zero-allocation nor payload-bounded streaming claim. Nonempty extensions still use
the existing semantic projection to exclude diagnostic prose and locations from their fingerprints.

## Measurements

2026-09-27, Apple M5 Max/macOS arm64, .NET SDK 10.0.201/runtime 10.0.5, Release.
BenchmarkDotNet 0.15.8 ShortRun, three warmups and three measurements. Inputs are prepared once;
measurement returns canonical semantic bytes, excluding SHA-256 and document construction.
The reference performs the previous mutable-tree traversal; setup requires exact byte equality.
Other local validation was running, so short-run timing is indicative, not an application SLA.

| Fixture | Reference allocation/op | Direct allocation/op | Reference mean | Direct mean |
| --- | ---: | ---: | ---: | ---: |
| Flat object | 3,240 B | 904 B | 734 ns | 252 ns |
| 24 nested objects | 50,817 B | 20,904 B | 14.60 μs | 5.37 μs |
| 128 rows | 188,973 B | 56,720 B | 52.03 μs | 19.59 μs |
| 4,096 rows | 6,096,193 B | 1,929,940 B | 2.97 ms | 1.49 ms |

Raw benchmark results: [CSV](results/execution-canonicalization.csv).

A separate local Ari fixture at `6ec9bf4` compared core binaries built from unchanged Cohesive
`621eeaf` and this change, with all other assemblies fixed. Each scenario ran in a fresh process,
then repeated once; `GC.GetTotalAllocatedBytes` measured total allocations, not retained heap size.
The complete worker-catalog case invokes its existing admission test and includes assertions.

| Ari fixture | Source baseline cold / warm | Changed cold / warm |
| --- | ---: | ---: |
| Definition catalog | 1,258,865,776 / 313,376,152 B | 738,198,160 / 191,166,384 B |
| Complete worker catalog | 2,461,403,112 / 1,532,733,720 B | 1,317,380,008 / 749,043,344 B |

These are local source-assembly experiments, not a published-package adoption or Azure result.
Cold runs include static family initialization; warm runs still reconstruct roots. CLR type mapping,
duplicate-property validation and other compilation work remain allocation targets. The shared B1
plan was heavily utilized during the observed Azure recycles; this change does not prove that all
startup latency was caused by canonicalization. Scheduler qualification remains a separate gate.

## Reproduction and regression

```sh
dotnet test src/Cohesive.Tests -c Release --filter 'FullyQualifiedName~ExecutionDefinitionSerializationTests|FullyQualifiedName~CanonicalJsonWriterTests'
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*ExecutionCanonicalizationBenchmarks*' --job short
```

The deterministic allocation regression requires direct traversal to allocate less than half the
reference for a repeated structured payload after warmup. Timing thresholds are excluded from CI.
Existing known fingerprint, malformed input, extension and round-trip tests protect durable semantics.

Execution schema v4 subsequently removes repeated inline type trees at the source; see
[compact execution type references](compact-execution-types.md) for the wire contract and qualification.

[Duplicate-property checks](duplicate-property-checks.md) subsequently remove successful warm
scan allocations while preserving first-failure diagnostics and strict imported-document validation.

## Shared lexical sequence profile

`CanonicalJsonWriter.WriteCanonicalSequence(writer, element, profile)` supports
`PreserveLexical`: exact numeric token spelling, stable repeated-key order, sequence
arrays and ordinal UTF-16 key sorting. Escaping belongs to the caller's writer.
Strict profiles retain normalization and duplicate rejection. Both representations
use one structural walk; only storage access and scalar emission differ.

`ComputeCanonicalSequenceSha256` uses the existing pooled incremental sink.
Complete tokens flush periodically; a single large scalar can still grow the pooled
working buffer. No digest, payload, authorization state or admission result is cached.
`CanonicalJsonPropertyExclusions` prepares exact JSON Pointer object paths once,
so hashes can omit self-identifiers during traversal instead of building/editing a
node tree and serializing it again. Rules do not propagate into array elements.
Omitted values are not visited or validated; fingerprint projection never replaces
separate document admission.

Property names are copied/unescaped once into object-owned pooled UTF-8 storage.
Comparison uses a common byte prefix and the first differing scalar's UTF-16 units,
so supplementary characters still sort before U+E000/U+FFFF. Successful sequence
writing creates no property-name strings. Strict array-policy paths are formatted
without an intermediate name string; required path strings and failure diagnostics
remain. A one-byte gap gives empty keys distinct source offsets for stable ties.
Both property entries and name/scratch buffers are cleared on return, including
failure. Node entries retain their owned names and original storage width.

[Compact results](results/canonical-sequence-profiles.csv) include strict and lexical
before/after measurements plus direct-document versus node-projection workloads.
Apple M5 Max, macOS 27.0.1, SDK 10.0.201 / runtime 10.0.5 arm64; ShortRun uses one
launch, three warmups and three measured iterations. Hash inputs are already-parsed,
borrowed documents; projection cases include serialization of a typed synthetic
payload. They exclude source acquisition and product execution.

| Lexical hash workload | alpha.136 CryptoStream allocations | Optimized pooled allocations |
|---|---:|---:|
| 128 flat fields | 10,641 B | 360 B |
| 128 escaped names | 12,641 B | 360 B |
| 4,096 rows | 5,689 B | 361 B |
| One large escaped token | 1,574,156 B | 362 B |

The 128-field projection allocates 37,506 B through a node tree versus 800 B
through direct document serialization with exclusions (both use the current hash
sink). It measured 63.8 versus 45.1 μs. Eliminating names has a CPU tradeoff:
flat/row lexical hashing measured about 10–11% slower than the former CryptoStream
path; escaped-name hashing about 30% slower. Large-token hashing was faster.
Strict flat streaming allocations fall from 5,672 B to 520 B; its short-run time
moves from 11.45 to 12.58 μs. These are not end-to-end latency claims.

Warm-runtime median-of-seven allocation guards bound borrowed wide-name and row
hashes at 1.5 KiB (360 B measured), and fresh parsed scalar-heavy hashes at 2 KiB
(432 B measured). Tests protect numeric lexemes, duplicate ties including empty and
escaped aliases, both encoders, invalid UTF-8, malformed surrogate keys/values,
generated Unicode ordering, exact omissions, projection/admission distinction and
concurrent reuse of immutable exclusion configuration. Timing thresholds are excluded
from CI. Run the opt-in benchmarks with:

```sh
dotnet run --project eng/runtime-performance/canonical-hashes/CanonicalHashes.csproj -c Release -- --filter '*Lexical*Benchmarks*'
```

A source-backed Ari X12 004010 case compared against published alpha.136 in five
isolated alternating process runs allocated a median 466.0 -> 443.6 MB cold and
204.3 -> 186.2 MB repeated. Median elapsed times were 3.087 -> 3.091 seconds cold
and 1.393 -> 1.302 seconds repeated, with variation. Both pinned X12 fingerprints
were unchanged. This includes Ari's projection adoption; it does not attribute
the entire saving to raw-name comparison or establish a stable timing gain.
