# Prepared hosted observation projections

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
