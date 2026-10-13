# Repeated representation work

Qualification on macOS arm64, .NET SDK 10.0.201, Release, October 13, 2026. These are
allocated bytes, not retained memory or end-to-end latency. Each opt-in harness constructs
inputs outside measurement, warms 20 operations, then reports the median of seven batches
of 32 operations. Outputs are checked for equivalence before measurement. Small fractional
bytes reflect batch amortization. Preparation and first pool rentals are excluded.

## Ordered projections and binary fingerprints

A 128-entry set with nested string sets previously became a mutable JSON tree, lost its
identity property, produced a contiguous canonical byte array, then copied its version
prefix and payload before hashing. Core now combines set ordering, exact exclusions and
prefix bytes in its existing canonical traversal and incremental sink. Relations serializers
reuse frozen metadata. Portable numeric semantics, relaxed escaping, ordering and prefixes
remain the fingerprint authorities; their existing version identifiers are unchanged.

| Warm operation | Previous B/op | Current B/op |
| --- | ---: | ---: |
| Ordered projected hash, 128 entries | 216,368 | 67,648 |
| Length-prefixed binary tokens, 512 KiB | 1,049,087 | 224 |

The binary token encoder is shared by physical, compiled-plan and realization hashing;
integer endianness, UTF-8 lengths and booleans retain their existing byte format. Physical
hashing no longer grows a full binary output buffer. One unusually large token can still
grow the pooled sink. Public hash APIs now require writer options explicitly; `default`
retains the former standard encoder, while Relations explicitly selects relaxed escaping.

Weak preparation uses one success-publication mechanism, including staged evaluator
compilation/preparation. A throwing factory leaves its same slot retryable; it cannot
remove a concurrent successful result. Completed diagnostic results remain cacheable.
Caches follow owner identity and do not retain invocation parameters or result objects.
Weak keys describe these caches, not a promise that serializer metadata elsewhere is collectible.

Run `dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --hash-projection-allocations 32`.
Exact token-format, streaming allocation-ceiling, numeric-profile, exclusion and encoder
regressions supplement existing fingerprint equivalence tests. Cache tests exercise
concurrent failure recovery and evaluator preparation retry.

## Prepared typed results

Typed query rows, nested result carriers and transition outcomes prepare
`ObservationMaterializer.PrepareJsonValue<T>()` when their binding is built. The existing
Core converter engine interprets conventional CLR contracts directly. Plain JSON defaults
remain separate from shape materialization's web defaults. Customized JSON attributes,
required-member contracts, serializer callbacks and unsupported projections keep the
compatibility path. Missing mutable properties preserve initializers and skip setters.

A record row with two nested child records allocates 1,368 B through JSON and 128 B through
prepared interpretation. Those 128 B are independent result objects and their array;
no mutable output is cached. The test's one-child variant has a fixed 128 B median ceiling.
Cold reflection and expression compilation are outside the warm measurement. Preparation
is weakly keyed by CLR type and JSON contract, not tenant, authorization or parameter data.

Run `dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --prepared-result-allocations 32`.
Differential tests cover casing, missing fields, numeric strings, overflow, wrong container
kinds, null, mutable outputs, customized names, required members and base64 compatibility.
Existing query/nested-result/transition admission remains in place before interpretation.
