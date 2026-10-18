# Prepared hosted observation projections

Latest qualification: [prepared graph representations through typed decoding](#prepared-graph-representations-through-typed-decoding).

A hosted Query used to admit its portable value, write the entire observation tree into pooled JSON,
and deserialize the whole CLR envelope on every call. Preview comparison, persistence planning and
preview output rebuilt graph documents even when they referenced identical immutable observation storage.
Removing only the temporary JSON document did not solve this amplification in the earlier Ari experiment.

The existing observation materializer now also prepares the framework's exact hosted CLR contract.
Conventional records, arrays and mutable property bags read their fields directly. The adapter owns the
frozen serializer policy; Core's materializer does not depend on execution serialization policy. Selected
JSON constructors are honored, computed getter-only properties are ignored on read, and strict unknown
members are checked through the existing allocation-free field traversal. Custom type/member converters,
callbacks, required-member contracts and unsupported shapes retain their owning serializer path.
Constructor member converters retain canonical field order. Exact enum literals are prepared through the
existing wire-member catalog and the actual serializer; other forms retain that serializer's reader and
rejection rules, avoiding one JSON allocation per enum field on the common path. Cross-kind numeric reads use stack-only JSON
numeric tokens where a CLR cast would change rounding, retaining decimal scale and signed-zero semantics.
The existing admission walk detects inputs requiring whole-payload JSON depth, byte-policy or malformed
UTF-16 normalization; those use the original serializer path, including ignored fields. Observation-backed
bindings retain their existing native-value semantics. This adds no second traversal or success-path allocation. This is
an extension of the existing materializer, not another CLR decoder or an Ari-local conversion mechanism.
Member codecs interpret their own payload. Codecs requiring enclosing JSON context must be declared on
that owning envelope, which retains the whole-value serializer path.

`IImmutableObservationProjection` explicitly promises deep immutability and deterministic, context-free
conversion. `ShapeGraphDocument` opts in. When an object is decoded, its exact immutable field-storage
identity owns its projections. Each frozen serializer-options instance has a weakly held profile scope;
projection type and binary encoding distinguish entries inside it. Default options use the framework default
profile. Empty objects and mutable options are not shared. A successful projection is published once with
`PreparationSlot`; exceptions leave the same slot retryable. Neither discarded payloads nor discarded
profiles are retained. No content hash, request key or tenant result is used as an alternative authority.

The owning portable value remains authoritative. Immutable reuse conveys no contextual validation,
authorization, source-token or deployment-admission evidence. Hosted admission still runs before every
projection. Mutable outer envelopes, arrays and caller projections remain independent; only explicitly
immutable children are shared. Imported/persisted observations have independent storage and are admitted
normally. Existing execution-definition projections still use their owning execution document and their
separate compact-definition codec; this does not replace that boundary.

Framework marked graphs receive the existing reflection audit for settable properties, arrays and mutable
collections. Consumer implementations and converter purity remain explicit contracts, not runtime-proven
facts. Differential tests compare prepared admission to the exact owning serializer, including failures.
Other tests cover constructor/converter selection, case/unknown-member rejection, mutation isolation,
profile/encoding identity, concurrent first use, zero warm read allocations, retryable failures and weak
payload/profile lifetimes. Opt-in `PreparedHostedProjectionBenchmarks` measures the real adapter, including
fresh admission, for a shared 128-field immutable schema and 1/1,024 mutable rows.

## Ari qualification

Ari #225 and #226 are merged; the paired control is the frozen published-alpha.141 harness containing
#226's validation reuse. The source-qualified candidate changes Core/Relations and opts the existing
protocol document/carrier into the explicit immutable contract. No package was published from this branch.
Both X12 graph digests must remain identical. Counter runs are isolated from builds and tests. Measurements
are cumulative managed allocations, not retained heap; caching may retain one typed interpretation as long
as its owning observation remains live. No application or CI latency claim is made.

The consumer dependency order is: merge this capability, publish it, then upgrade Ari and adopt the protocol
markers. Source-assembly substitution is qualification only, using the existing durable-bookkeeping target;
it is not a production dependency or an Ari-local fallback. The companion Ari branch records the exact
commands and paired allocation evidence.

### Measured results (October 16, 2026)

The original X12 repeated cases allocate **50.26 → 45.96 MB (8.6%)** for 004010 and
**57.34 → 52.28 MB (8.8%)** for 005030. Cold cases improve by 1.6% / 1.8%.
Five alternating fresh-process pairs use precise process allocation counters, Release, Apple M5 Max arm64,
macOS 27.0.1, .NET SDK 10.0.201/runtime 10.0.5. These are cumulative allocations, not retained heap or
an application latency claim. Candidate code is Core `8147168a15c6eaf5fb68a8b12eee11a720197000`
and Ari `16df2b59103cb754e60fe10744d5af645339e774`; the control contains merged Ari #226 on public alpha.141.

The real hosted-adapter counter harness (fresh admission included, median seven after four warm calls)
falls from 288,056 to 57,672 bytes for 1,024 mutable rows, and 1,472 to 384 bytes for one row. The micro
control uses the same entry point with frozen public-alpha.141 runtime DLLs; it is a runtime comparison,
not a separate package project rebuild. This 80% row saving is not an 80% X12 saving. The new opt-in
BenchmarkDotNet class also completed all four Dry smoke cases; Dry timing is not performance evidence.

Qualification: **4,683 Core tests passed**, 41 environment skips; **1,352 Ari solution tests passed**,
18 emulator skips. Both X12 graph digests remain unchanged. The checked-in
[Ari report and raw counters](https://github.com/cohesivesystems/ari/blob/07ab81b1e609d17ab0ecd5a55bfa4c0ff7f0908a/docs/performance/x12-prepared-projections.md)
are the evidence authority, including assembly hashes, trace quality and disjoint remaining allocation budgets.
Initial observation projection, scalar annotation creation/rehydration and protocol JSON remain substantial;
this capability does not eliminate those boundaries.

## Compiled graph materialization

The next bottleneck was the existing materializer's fallback for marked immutable roots, immutable arrays,
and polymorphic members. For example, preview comparison and persistence planning both decode a compiled
X12 `ShapeGraphDocument`. Its constructor-backed graph can now read fields directly, including immutable
arrays and supported string-discriminator unions. Dispatch comes from the owning `JsonTypeInfo`, not a
second TypeRef/TypeDefinition case list. Unknown/missing discriminators use the serializer's reader;
custom converters, reference handlers and reserved-name conflicts retain the serializer path. Known union
members currently remove the discriminator through a native observation update before reading the concrete
member. That small copy remains a possible follow-up; there is no JSON tree involved in that update.

The existing annotation-map converter's default profile can also read strings directly into native
`AnnotationValue`s. Structured values retain owned serializer snapshots and custom annotation/key/element
converters retain their full contract. This applies to observations; it does not discard lexical snapshots
when admitting arbitrary parsed annotation JSON. Prepared materialization publishes through the existing
observation/profile-owned immutable projection slot. It adds no result cache, admission evidence or Ari API.
The existing JSON acquisition walk separately reuses repeated string values in its bounded name table;
see [scope and rejection guarantees](observation-name-reuse.md#compiled-graph-follow-up).

### Allocation qualification

October 17, 2026, Apple M5 Max arm64, macOS 27.0.1, SDK 10.0.402/runtime 10.0.13, Release.
Control is Ari `299c73e` (draft Ari #230) with merged Cohesive #445 at
`eb8c9b4d53b99d23894d2853fd6a690de8c40a6d`. Candidate uses the same frozen Ari harness and all the
same runtime assemblies except `Cohesive.dll`. These are source-qualified builds, not published packages.
The [manifest](evidence/compiled-graph-projection/runtime-manifest.json) records assembly hashes and both
unchanged graph fingerprints. Five alternating fresh-process pairs run the original X12 repository-transition
case twice per process; medians below use precise process allocation counters. Repeated cases have fresh
repositories/fixtures and share application preparation. Builds, tests and benchmarks did not overlap these
counters. MB means decimal megabytes. These are cumulative allocations, not retained heap or a CI speed claim.

| Original X12 case | Control MB | Candidate MB | Change |
| --- | ---: | ---: | ---: |
| 004010 repeated | 32.15 | 28.35 | -3.80 MB / -11.8% |
| 005030 repeated | 36.10 | 31.30 | -4.80 MB / -13.3% |
| 004010 cold | 178.13 | 180.44 | +2.31 MB / +1.3% |
| 005030 cold | 181.40 | 182.72 | +1.32 MB / +0.7% |

[All paired samples](evidence/compiled-graph-projection/x12-counters.csv) include elapsed observations.
Preparing additional compiled readers has a first-use allocation cost. The elapsed samples did not establish
an end-to-end speed improvement; separate steady-state benchmark evidence is required. Neither this
measurement nor weak ownership tests prove a retained-heap improvement.

`ValueConversion.dll --graph-counters` measures projection and fresh admitted decoding separately.
Each sample is a seven-call median after eight warm-ups; the table below takes the median across five
alternating fresh processes per variant. Each decode creates a new root storage owner, preventing an
immutable result-cache hit. The workload is one shape with 1/128/1,024 fields, scalar union members,
repeated string annotations and structured annotations. It is synthetic, not Ari's catalog.

| Fields | Operation | Control bytes | Candidate bytes |
| ---: | --- | ---: | ---: |
| 1 | Project | 6,320 | 6,320 |
| 128 | Project | 248,936 | 202,584 |
| 1,024 | Project | 1,962,088 | 1,589,696 |
| 1 | Fresh decode | 8,584 | 5,984 |
| 128 | Fresh decode | 291,104 | 225,440 |
| 1,024 | Fresh decode | 2,284,392 | 1,773,728 |

[Raw micro counters](evidence/compiled-graph-projection/micro-counters.csv). Raw-string hash collisions can
change savings across processes; they always fall back to ordinary decoding. The projection still serializes
and parses once. This change reduces retained string duplication and member-level decoding round trips;
it does not replace compiled graph authoring with a native observation compiler.

Qualification: 4,773 Core tests passed, 41 existing environment skips; 1,372 Ari solution tests passed,
18 existing emulator skips. The focused regressions cover serializer-equivalent graph output and annotation
snapshots, custom annotation readers, polymorphic nulls and rejection, retryable unknown immutable roots,
traversal string ownership and malformed escapes. A 128-field first-decode ceiling is 280,000 bytes
(225 KB measured); a repeated-string projection has a 48 KiB ceiling. Running the guards against the old
Core runtime fails the graph ceiling at 290,560 bytes and the string-identity invariant. Existing concurrent
first-read, weak-lifetime, immutable graph audit and fresh-admission regressions continue to pass.

### Throughput tradeoff

Matched BenchmarkDotNet 0.15.8 runs use the same benchmark binary and one frozen Core DLL per variant,
in-process emit, three warmups, five measured iterations and a 250 ms target. The in-process toolchain
prevents a benchmark build from replacing the frozen control with candidate project references.
[Control report](evidence/compiled-graph-projection/control-benchmark.md) and
[candidate report](evidence/compiled-graph-projection/candidate-benchmark.md) retain the environment,
uncertainty and GC measurements. These are separate steady-state synthetic runs, not timings of Ari.

| Fields | Operation | Control mean | Candidate mean |
| ---: | --- | ---: | ---: |
| 1 | Project | 4.049 μs | 4.058 μs |
| 128 | Project | 229.395 μs | 247.212 μs |
| 1,024 | Project | 1,971.696 μs | 2,038.178 μs |
| 1 | Fresh decode | 8.147 μs | 5.787 μs |
| 128 | Fresh decode | 367.064 μs | 245.579 μs |
| 1,024 | Fresh decode | 2,962.300 μs | 2,004.891 μs |

Typed decoding is about 32–33% faster for the larger inputs. Projection trades about 3–8% of throughput
for fewer equal string allocations. This is an explicit allocation/CPU tradeoff; the original X12 elapsed
samples were somewhat slower and do not establish an overall speedup. Further removal of encoding and
member copies needs to be evaluated against both CPU and allocations, not just one counter.

Reproduce allocation counters with:

```bash
dotnet run -c Release --project eng/runtime-performance/value-conversion/ValueConversion.csproj -- --graph-counters
```

Run normal opt-in
throughput benchmarks with `--filter '*CompiledGraphProjectionBenchmarks*' --job short`; frozen-runtime
comparisons use `--inProcess --warmupCount 3 --iterationCount 5 --iterationTime 250 --launchCount 1`.
The existing `eng/runtime-performance/durable-bookkeeping/compare-ari.py` collects paired Ari counters.
Freeze the same Ari harness and its dependency closure, replace only Core for the candidate, and provide
its two DLL paths to that script. The script's `--runs 5` output is the checked-in X12 evidence.

## Prepared graph representations through typed decoding

The existing `ObservationValue` projection and immutable interpretation cache now share a prepared
representation across compiler output, preview and typed persistence planning. The declared serializer
metadata remains the field/discriminator authority. This extends Core's existing immutable projection
contract and preparation helpers; it introduces no Ari cache, model or fingerprint authority.

For example, projecting a compiled `ShapeGraphDocument`, decoding it through the hosted adapter and
projecting that result again retains the original observation fields when the declared encoding is
unchanged. A metadata-only envelope change shares the graph interpretation but still reads the new
metadata. First decoding continues to run constructors. Changed and independently parsed observations
have independent admission; contract, depth, normalization and contextual checks remain fresh.

Supported declared object contracts project into Core's compact field vectors using frozen JSON metadata.
Actual wrapper converters supply their existing string getters. Opaque converters and contextual member
policies retain serialization. Mutable envelopes are not cached. Eligibility requires the existing explicit
deep-immutability contract; framework graphs participate in its reflection audit.

A successful typed read supplies an encoding **candidate**, not evidence of canonical output. Reprojection
checks property membership, discriminator, arrays and exact scalar representation before reusing fields.
Constructor normalization, custom read profiles, decimal scale, signed zero and unknown fields can force
fresh output. Small annotation tokens compare using existing field names; wider objects keep the linear
strict token projector. Duplicate names remain rejected without another serialization pass. The JSON
scalar reader remains authoritative. Native UTF-16 checks preserve replacement behavior without the
boxed-char allocation found in the generic short-span range helper on this Arm64 runtime.

Preparation is keyed by source object identity plus declared type, never IDs, equality overrides or
fingerprints. The framework's fixed strict read profile retains its source until preparation so collection
between Process steps does not discard it. Arbitrary read profiles supply weak candidates. Publication
clears candidates; completed preparation owns its resulting storage. Nested misses prepare outside another
owner's lock before publishing, so invalid cyclic values cannot introduce lock-order deadlocks. Concurrent
nested misses can duplicate preparation; completed values and top-level first preparation are shared.
A live source can retain its prepared field tree; lower cumulative allocation is not a retained-heap claim.
Failed preparation remains retryable. Dead serializer profiles are pruned on subsequent reads. Profile
references live inside the payload's weakly owned entry rather than a second weak table keyed by globally
live options: this avoids retaining a discarded payload through the new decode/reprojection cycle.

### Qualification

October 17, 2026 local time; macOS 27.0.1, Apple M5 Max arm64, SDK 10.0.402/runtime 10.0.13, Release.
Control is public alpha.145. Candidate replaces **only `Cohesive.dll`** in the identical frozen Ari runtime
closure. This is source qualification, not publication or an Ari package upgrade. The
[manifest](evidence/prepared-graph-representation/runtime-manifest.json) identifies source-file hashes,
runtime and benchmark binaries, shared closure digest, and unchanged graph hashes. No builds, tests,
benchmarks or traces overlapped the uninstrumented counters.

Five alternating fresh-process pairs per source, original repository-transition case, fresh fixtures and
repositories in both invocations. Repeated means the second invocation sharing application preparation,
not a stabilized steady-state service. MB is decimal and allocations are cumulative, **not retained heap**.

| X12 case | Control repeated MB | Candidate repeated MB | Allocation change | Control CPU ms | Candidate CPU ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| 004010 | 28.76 | 25.75 | -3.01 MB / -10.5% | 315.2 | 283.6 |
| 005030 | 31.32 | 28.52 | -2.80 MB / -8.9% | 361.6 | 349.9 |

Cold allocation medians are 157.74 → 154.70 MB and 159.88 → 157.36 MB. Cold CPU is 1,775.1 →
1,779.1 ms and 1,831.6 → 1,805.5 ms: no broad cold CPU gain is established. Repeated elapsed medians
are 306.7 → 283.6 ms and 343.7 → 338.3 ms. CPU includes background GC/JIT and native threads;
the smaller 005030 timing difference warrants caution. This does not establish CI or deployment latency.
[All 40 counters](evidence/prepared-graph-representation/x12-counters.csv).

Identical BenchmarkDotNet binaries use in-process emit, three warmups, five measured iterations and a
250 ms target. [Control](evidence/prepared-graph-representation/control-benchmark.md) and
[candidate](evidence/prepared-graph-representation/candidate-benchmark.md) include uncertainty/GC data.
At 128 fields, authoring a fresh graph and projecting it takes 262.19 → 155.26 μs; first admission of fresh
graph storage takes 254.93 → 136.10 μs. A fresh envelope over already-prepared graph storage takes
255.36 → 24.07 μs and 212.18 KiB → 4,077 B. Repeated projection of the same source is allocation-free.
Fresh graph benchmark setup asserts distinct decoded graph instances: assigning an equal child with
`WithField` can preserve its old storage and must not masquerade as a first-decode measurement.

The median-of-seven first-reprojection guard uses a 128-field decoded graph: **28,240 B**, ceiling
40,960 B. A separate guard allows 256 B total for 128 warm projections of a 1,024-field graph. Exact-byte
comparisons cover every declared TypeRef, named-type and constraint case. Regressions cover Unicode,
malformed escapes in annotation keys/values, duplicate spellings, constructor normalization, custom read
profiles, unknown fields, depth, cycles, concurrent first use, collection between steps, discarded graph
cycles and expired profiles. Full validation: **4,807 Core tests**, **233 SQLite tests**, **963 Ari engine
tests**; 41/18 pre-existing Core/Ari integration skips. The final targeted byte/regression run also passes.

Three candidate traces per source retain disjoint
[allocation](evidence/prepared-graph-representation/allocation-budget.csv) and
[managed CPU](evidence/prepared-graph-representation/cpu-budget.csv) budgets. No lost events or missing
managed CPU stacks; one 005030 repeated allocation sample is unresolved and remains in Other.
[Trace quality](evidence/prepared-graph-representation/trace-quality.csv). Remaining repeated allocations
include observation projection (5.85/7.13 MB), hosted admission/decoding (3.75/4.14 MB) and other EDI
compilation (5.33/5.62 MB). These are sampled categories, not extra costs to add to the precise totals.
First graph construction, scalar-wrapper/annotation decoding and mandatory validation remain; this
change removes repeated representation work, not all first-use work or all JSON conversion.

Reproduce by freezing the public-alpha.145 harness closure twice and replacing only candidate Core:

```sh
python3 eng/runtime-performance/durable-bookkeeping/compare-ari.py \
  --baseline CONTROL/ProtocolPreparation.dll --candidate CANDIDATE/ProtocolPreparation.dll \
  --output counters.csv --runs 5
```

The comparator now retains optional `cpu_ms` from the existing Ari harness. Benchmark commands remain
those above; freeze the same benchmark binary in both directories and use each variant's Core DLL.
Raw traces stay outside Git and are identified by hashes in the manifest.

Publication qualification also covers hash-independent annotation byte ordering: Linux CI exposed
that the new annotation dictionary projection had retained source hash enumeration order. It now
uses the existing token-reader ordering helper. A constant-hash comparer regression fails before
this correction and passes after it. The source measurements above precede this small ordering
correction; they remain identified by the checked-in runtime manifest.

## Scalar wrapper decoding

The next retained change targets first decoding of string-backed values such as `ShapeId`.
Previously the prepared reader serialized each observation string to pooled UTF-8 and invoked the
JSON reader merely to call the wrapper constructor. The selected `SingleValueWrapperJsonConverter`
now supplies that native read itself. This extends the existing converter preparation used by hosted
CLR reads, plain JSON preparation and default shape materialization; it adds no decoding API, result
cache or parallel wrapper catalog. Constructors run on every read. Customized underlying string
readers, legacy objects, nulls, other value kinds and malformed UTF-16 retain the actual serializer
path. The native guard is shared with declared projection and native string annotations.

A proposed observation-backed structured annotation representation was also implemented and tested,
then discarded. Its extra full-case allocation saving was small, while retaining an observation
subtree alongside a requested JSON view introduced another lifetime and encoding-order obligation.
Annotations keep their existing backing. The remaining decode category also includes retained model,
collection and validation work; it must not be treated as entirely removable JSON overhead.

### Qualification

Public **alpha.146** is the baseline. Candidate replaces only `Cohesive.dll` in an otherwise identical
frozen Ari runtime; this is not a published package upgrade. All 27 baseline packaged runtime DLLs
match official archives and their merged publisher commit, and the other 26 candidate packaged DLLs
are identical. The three Ari-owned protocol incubators remain source projects.

Matched BenchmarkDotNet workloads, Apple M5 Max arm64, macOS 27.0.1, SDK 10.0.402 / runtime 10.0.13,
Release, separate processes per variant, in-process emit, one launch, three warmups, five 250 ms
iterations. Means are elapsed time per operation, not per-method CPU measurements.

| Workload | Baseline time | Candidate time | Baseline allocation | Candidate allocation |
| --- | ---: | ---: | ---: | ---: |
| Decode 128 string wrappers | 15.54 μs | 1.66 μs | 28,013 B | 1,384 B |
| First decode, 128-field graph | 121.08 μs | 103.68 μs | 184.50 KiB | 158.08 KiB |
| First decode, 1,024-field graph | 1,150.59 μs | 968.21 μs | 1,409.62 KiB | 1,201.18 KiB |

The wrapper workload includes fresh typed results and hosted contract validation. Fresh graph storage
is asserted by its existing setup; a shared decoded graph cannot make this a warm result-cache hit.
[All sizes, errors, deviations and settings](evidence/scalar-wrapper-decoding/benchmarks.csv).
Fixed allocation ceilings cover 128 wrapped fields through the hosted-profile reader, public prepared
JSON reader and default shape materializer, using medians of seven samples after preparation.

Five alternating fresh-process pairs per source run the original X12 case twice, with fresh repositories
and fixtures on each invocation. No local builds, tests, benchmarks or traces overlap measurements.
Second invocation is not established steady state. Whole-process CPU includes GC, JIT and native work.

| Case, second invocation | Baseline MB | Candidate MB | Baseline CPU ms | Candidate CPU ms |
| --- | ---: | ---: | ---: | ---: |
| 004010 | 25.714 | 25.690 | 304.4 | 276.9 |
| 005030 | 28.552 | 28.257 | 307.2 | 324.2 |

These whole-case differences are small relative to process-to-process allocation variation, including
roughly 1.6–2 MB excursions in some samples. Median paired allocation differences are 0.219 / 0.149 MB;
they are not guaranteed per-case savings. Cold allocation medians are 154.995 → 154.685 MB and
157.265 → 156.915 MB. There is **no established end-to-end CPU or latency improvement**: the two
sources' CPU medians move in opposite directions. Exploratory longer runs continued to change with
runtime warmup; disabling tiered compilation was a separate diagnostic, not a deployment-performance
claim. Keep the small shared converter fix on its component evidence, rather than claim a large X12 win.
[All 40 counters](evidence/scalar-wrapper-decoding/x12-counters.csv),
[exact source/runtime identity](evidence/scalar-wrapper-decoding/runtime-manifest.json), and
[unchanged graph hashes](evidence/scalar-wrapper-decoding/graph-hashes.csv).

Three new traces per variant and source have no lost events or missing allocation/managed CPU stacks.
Candidate second-use sampled allocation remains about **5.5–6.1 MB in EDI construction**, **5.4–6.5 MB
in observation projection**, **3.4–3.9 MB in hosted decoding/admission**, and **6.5–7.0 MB in Other**.
These are disjoint category estimates, not predicted savings; inclusive call-site totals must not be
added to them. The next larger target is annotation/map and graph construction, rather than more
wrapper-level caching. [Allocation budgets](evidence/scalar-wrapper-decoding/allocation-budget.csv),
[managed CPU shares](evidence/scalar-wrapper-decoding/cpu-budget.csv),
[quality](evidence/scalar-wrapper-decoding/trace-quality.csv).

Validation: **4,818 Core tests, 233 SQLite tests, 963 Ari engine tests and three comparison-driver tests
pass**, with the existing integration/emulator skips. Coverage includes constructor normalization,
nulls, malformed Unicode, strict/legacy objects, custom wrapper/string readers, mutable result
independence and fixed allocation bounds. Both X12 graph hashes remain unchanged.

Reproduce after building isolated runtime/benchmark variants:

```sh
python3 eng/runtime-performance/durable-bookkeeping/compare-ari.py --baseline /absolute/baseline/ProtocolPreparation.dll --candidate /absolute/candidate/ProtocolPreparation.dll --output /absolute/results/counters.csv --runs 5
dotnet /absolute/variant/ValueConversion.dll --filter '*ScalarWrapperDecodeBenchmarks*' '*CompiledGraphProjectionBenchmarks.DecodeNewGraphStorage*' --inProcess --warmupCount 3 --iterationCount 5 --iterationTime 250 --launchCount 1 --artifacts /absolute/results/benchmark
```

For longer diagnostic runs, the comparison driver accepts `--iterations 2..64` with Ari's updated
harness. Default two-phase comparisons remain compatible with the original harness; stale harnesses
cannot silently supply fewer phases than requested. A fixed invocation count alone does not establish
steady state.

## Immutable annotation-map overlays

`AnnotationMap.Merge` now shares the first compatible immutable map instead of rebuilding it before
applying overlays. It still uses default key/value equality, preserves later unequal values and leaves
inputs unchanged. Custom input comparers never become the result's policy; a single default map can
retain its root unchanged. This extends the existing collection operation and introduces no cache,
new annotation representation or retention authority.

A synthetic 128-entry map plus one new annotation falls from **8,296 B / 5.968 µs** to
**608 B / 0.152 µs**. Small balanced maps benefit less; the benchmark covers 8, 128 and 1,024 retained
entries with one/eight additions. These are isolated merge results, not X12 or service CPU claims.
The [compact benchmark](evidence/annotation-map-merge/benchmarks.csv) retains error/stddev and settings;
[identity/environment](evidence/annotation-map-merge/manifest.json). BenchmarkDotNet InProcessEmit,
three warmups, five 250 ms iterations, one launch, Apple M5 Max arm64, .NET 10.0.13, Release.

All **4,931 Core tests pass**, with 41 existing integration skips. Tests cover custom key and value
comparers independently, structural equality, input independence and concurrent overlays. A median-of-seven
4 KiB allocation ceiling on a 1,024-entry root plus one addition rejects rebuilding the whole root.
