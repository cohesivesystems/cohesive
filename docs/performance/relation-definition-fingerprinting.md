# Immutable relation definition fingerprinting

Relation definitions previously created serializer options for each fingerprint, materialized a mutable
JSON tree, exported canonical bytes, and copied them into a schema-prefixed buffer before SHA-256.
For example, a 512-filter query allocated 3.18 MB per fingerprint after warmup. The revised path
allocates about 683 KB, with declaration setup outside the measurement boundary.

## Ownership and invariants

Cohesive owns canonical JSON algorithms; Relations owns its existing array-ordering registry and
`relation-query/v1-c14n/v4` profile. The shared canonical writer now accepts borrowed immutable
`JsonElement` input, retaining neither the input nor payload. It preserves ordinal property order,
sequence order, declared string/object-set ordering, first-authored duplicate diagnostics and portable
observation number semantics. Execution's exact decimal-rational profile remains distinct.

Relations lazily shares frozen serializer metadata. Public `CreateOptions` still returns independently
mutable options. The cache contains serializer metadata, not definitions, invocation context or results.
Each fingerprint serializes to an immutable element and streams the existing schema-version/NUL prefix
and canonical content through Cohesive's existing SHA-256 buffer writer. Export and hashing use the
same canonical algorithm. No Ari-local serializer, parallel model or hashing abstraction was needed.

## Measurements

| Fixture | Previous fresh metadata + mutable tree | Mutable tree with shared metadata | Revised |
| --- | ---: | ---: | ---: |
| Flat query | 1.20 MB | 54.01 KB | 20.37 KB |
| Nested predicate | 1.23 MB | 98.00 KB | 39.77 KB |
| 128 filters | 1.72 MB | 574.97 KB | 183.62 KB |
| 512 filters | 3.18 MB | 2,039.53 KB | 682.70 KB |

[Baseline report](relation-definition-fingerprinting/baseline.md) and
[revised report](relation-definition-fingerprinting/revised.md) record environment and dispersion.
The revised report includes the old tree path with metadata already shared, isolating a further
60–68% allocation reduction from immutable canonicalization and streaming. Inputs are prepared outside
the boundary, metadata is warmed, and a new digest is computed each invocation. These short local
allocation comparisons are not end-to-end latency claims.

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*RelationDefinitionFingerprintBenchmarks*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~CanonicalJsonElementWriterTests'
dotnet test src/Cohesive.Relations.Tests/Cohesive.Relations.Tests.csproj -c Release --filter 'FullyQualifiedName~RelationQueryIRTests'
```

Recorded short-job settings: `--inProcess --job Short --warmupCount 1 --iterationCount 3
--invocationCount 16 --unrollFactor 1`.

## Qualification

Generated differential tests compare the immutable and prior mutable writers across both numeric
profiles, Unicode, escaping, array paths and set failure diagnostics. Independent schema-prefixed
hashing, existing golden fingerprints, concurrent reads and allocation regressions protect Relations.
All 164 Ari canonical catalog bodies remained byte-identical.

A fresh-process local Ari harness measured cumulative allocations separately:

| Boundary | Before | After |
| --- | ---: | ---: |
| Catalog authoring | 410,590,016 B | 381,836,032 B |
| First admission | 142,764,472 B | 123,388,480 B |
| Warm admission | 1,035,992 B | 1,031,336 B |

These are source qualification observations, not retained heap measurements or published-package
results. Ari package pins remain unchanged and temporary test-output overlays were restored.
Validation passed 4,262 Core tests (33 existing skips), 1,082 Relations tests, 855 Ari engine tests
(18 existing skips), and solution package/API validation. No tests were excluded or timeouts increased.

Input normalization, nonscalar parent projection and CLR nullability preparation remain separate targets.
