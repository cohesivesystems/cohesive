# Direct typed value projection

Core's `ObservationValue.FromObject` retains declared JSON converters and polymorphic property
contracts while projecting their output from a disposable immutable JSON document. It no longer
expands that serialized output into a mutable JsonNode tree first. Root and declared-property
conversion share one helper and the existing token-to-observation walk. Values own their strings
and collection snapshots before the document is disposed.

Declared projection rejects duplicate object properties through the declared .NET 10 serializer
profile, including duplicates with equal values, and now throws JsonException for malformed JSON.
The earlier builder-based check and its ArgumentException behavior have been removed. Public
`FromJsonElement` retains its existing last-property-wins behavior. Declared serialization options,
discriminators, numeric observation kinds and array order remain authoritative.

Relations' `HostedQueryValueAdapter.Decode` now delegates to Core's pooled UTF-8
`ObservationValue.Deserialize<T>` instead of retaining a temporary JsonDocument or element clone. It retains exact contract matching, fresh value validation, declared CLR
serialization and structured conversion diagnostics. Typed outputs, including JsonElement
properties, own any JSON storage they retain after the temporary document is disposed. No input,
result, admission or contextual evidence is cached.

These changes extend existing Core projection and Relations admission responsibilities. The
shape-qualified Observation materializer cannot replace declared JSON conversion for arbitrary
hosted contracts without a bound graph, and would need to retain converter/polymorphism semantics.
No Ari-local projection, DTO cache, alternate type model or fingerprint profile is introduced.

## Why this target

A local breakdown of the X12 004010 commit payloads found:

| After commit | Complete checkpoint | Activation receipts | Continuation | Operation replay receipts |
|---|---:|---:|---:|---:|
| First | 4,099,884 B | 11,188 B | 2,042,532 B | 2,038,930 B |
| Second | 8,842,026 B | 41,975 B | 4,410,506 B | 4,381,672 B |

These are exact UTF-8 value slices from synthetic fixture canonical commit JSON captured in a
separate temporary baseline probe. The [composition rows](evidence/hosted-value-projection/checkpoint-composition.csv)
exclude property-name/envelope bytes from each component; columns should not be summed to the
complete checkpoint. Activation receipts are under 0.5%. Large operation outputs occur in both
current token bindings and the replay ledger, whose distinct execution/recovery responsibilities
must be preserved. Their serialized size does not imply two independent live payload graphs.

An allocation trace of the #414 implementation samples about 6 MB of warm commit hashing. Larger
costs remain in typed conversion: roughly 41 MB inclusive of HostedQueryValueAdapter.Decode and
60 MB inclusive of ObservationValue.FromPropertyValue. These sampled inclusive stack boundaries
may overlap and must not be added. Removing JSON intermediates addresses the measured path while
retaining persisted replay payloads. Content-addressed payload references remain a separate design
requiring explicit wire, recovery and integrity semantics.

## Local measurements

October 11, 2026, Apple M5 Max/macOS arm64, .NET SDK 10.0.201/runtime 10.0.5, Release.
Ari source b5d64ae (#210) uses matched Cohesive source assemblies at 8152b3ec (#414).
Only Core and Relations change in the final variant. All other dependencies and the fixture are
fixed. Each variant runs in a fresh process, executes the complete normal X12 004010 public test,
then repeats it in the same process. GC.GetTotalAllocatedBytes(precise: true) includes catalog,
seed, preview, execution, assertions and cleanup. No concurrent measurement/build/test work ran.

| Variant | Cold allocated bytes | Repeated allocated bytes |
|---|---:|---:|
| Streaming-hash parent | 561,122,968 | 295,370,664 |
| Direct typed projection | 519,217,616 | 253,818,680 |

This saves 41.9 MB cold (7.5%) and 41.6 MB repeated (14.1%), using decimal MB.
[Case measurements](evidence/hosted-value-projection/ari-case.csv) retain single elapsed samples;
they establish no end-to-end latency improvement. These are source-assembly qualifications,
not published-package, retained-heap or UAT results. Pools and final typed/observation values
still consume memory.

Opt-in BenchmarkDotNet ShortRun results (allocated KB uses 1024 bytes):

| Conversion workload | Previous allocation | Current allocation | Previous mean | Current mean |
|---|---:|---:|---:|---:|
| Declared flat, 128 fields | 36.13 KB | 16.02 KB | 15.39 μs | 12.15 μs |
| Declared nested, 24 levels | 20.86 KB | 10.37 KB | 5.23 μs | 4.61 μs |
| Declared 1,024 rows | 1,771.34 KB | 864.92 KB | 597.42 μs | 441.37 μs |
| Hosted decode, 1 row | 1.63 KB | 1.40 KB | 801.8 ns | 803.7 ns |
| Hosted decode, 1,024 rows | 454.35 KB | 281.31 KB | 507.03 μs | 485.86 μs |

[Projection results](evidence/hosted-value-projection/projection-benchmarks.csv) and
[decoder results](evidence/hosted-value-projection/decode-benchmarks.csv) include uncertainty.
Three measured iterations establish representative allocation reductions; small decoder timing
changes do not establish a latency benefit. An initial separate duplicate scan slowed projection;
the final implementation rejects duplicates in the existing builder pass.

## Qualification and reproduction

The final full Cohesive suite passes 4,423 tests with 33 skips. One earlier full run narrowly
missed an unchanged normalization allocation threshold (7.6 bytes beyond its limit); the focused
normalization rerun and final full suite pass. The normalization implementation is unchanged.

Ari source-assembly qualification passes 852 engine tests with 18 skips. Twenty tests fail because
precompiled test repository wrappers lack the newer source `IEntityRepository.IdentityField`
member. The exact same 20-test failure set is reproduced against the unchanged streaming-hash
parent; there are no additional failures. These runs do not qualify a published-package adoption.


Differential tests compare the previous node projection for root/property converter values,
polymorphic properties and sequences, numeric boundaries, empty/null structures and Unicode.
Duplicates with both equal and different values fail as before. Guards protect removal of the
mutable tree and decoder clone, and test JSON lifetime after disposal. Admission and conversion
failure codes remain covered alongside the existing GraphDelta portable round trip.

```bash
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release
dotnet run --project eng/runtime-performance/value-conversion/ValueConversion.csproj -c Release -- --filter '*DeclaredProjection*' '*TypedValueDecode*' '*HostedAdapterDecode*'
```

The opt-in benchmarks compare the previous physical paths against production conversion. Setup
requires exact projected/typed results. Inputs are outside measurement; required fresh validation
is inside both decoder methods. The Ari case uses #210's existing opt-in protocol-preparation
harness with matched source-module overlays, restored afterward. Ordinary runs use pinned packages.

See [pooled decoding follow-up](pooled-observation-decoding.md) for current ceiling guards, parser-policy tradeoffs and revised benchmark boundaries. The tables above qualify the earlier #415 implementation.
