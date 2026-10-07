# Reuse normalized execution content while streaming its fingerprint

## Contract and ownership

Execution documents own normalized, duplicate-checked JSON. Construction already establishes
ordinal object order and exact decimal-rational number spelling. Fingerprinting previously traversed
that document again with the general canonicalizer, then allocated a contiguous envelope and a copy
before hashing it. Now internal document fingerprinting writes its existing normalized `JsonElement`
with the same writer settings, without sorting or normalizing it again. SHA-256 computation reuses
Cohesive's existing `Sha256BufferWriter`; no second hashing abstraction or payload cache is introduced.
Both byte export and digest computation share one envelope writer.

The canonical JSON remains the authority. The trusted branch is supplied only by document construction
and document-owned computation. Public component APIs still validate duplicates and independently
canonicalize their supplied JSON. Imported documents normalize at their constructor and compute their
own digest; declared metadata is not accepted as evidence. Extensions retain their existing semantic
projection, including failed state and diagnostic code while excluding diagnostic prose/locations.
The schema/profile, canonical bytes, digest, contextual admission and cache lifetime remain unchanged.

For example, an imported document with 4,096 rows previously allocated 3,122,000 B for its first digest
alone after construction. The new test requires that boundary below 16,384 B; a 128-row fixture previously
allocated 94,296 B and also fails the new bound. Input normalization/ownership and retained document
allocation precede that isolated boundary. `ImportedFirstFingerprintDoesNotAllocateAFullCanonicalEnvelope`
protects it, while generated differential tests compare the trusted writer with independent component
canonicalization and the existing node reference across JSON kinds, escaping and extreme numbers.
Known digest, failed-extension, forged-metadata, concurrent import and fresh-admission tests remain.

## Reproducible benchmark evidence

The same `ExecutionDefinitionFingerprintReuseBenchmarks` fixture was run against the unchanged
`61df09047e9f99d31f67ead0a04db1d48a437511` core and the revised core. Its previous raw `JsonElement`
authoring fixture was incompatible with the typed codec root contract; both runs now use the same
explicit typed `Definition(Content)` wrapper. Declaration setup is outside the benchmark boundary.
Authored first use includes document creation/normalization and its construction digest; imported
first use includes imported document normalization and first independent computation.

| Boundary / fixture | Baseline allocation | Revised allocation |
| --- | ---: | ---: |
| Authored / flat | 5.36 KB | 4.17 KB |
| Authored / nested | 22.30 KB | 15.89 KB |
| Authored / 128 rows | 330.37 KB | 200.48 KB |
| Authored / 4,096 rows | 11,221.61 KB | 6,947.04 KB |
| Imported / flat | 3.87 KB | 2.73 KB |
| Imported / nested | 15.12 KB | 8.83 KB |
| Imported / 128 rows | 282.21 KB | 152.32 KB |
| Imported / 4,096 rows | 9,219.31 KB | 4,946.20 KB |

[Baseline report](canonical-fingerprinting/baseline.md) and
[revised report](canonical-fingerprinting/revised.md) retain hardware/runtime and timing dispersion.
These short in-process samples establish allocation comparisons, not production latency. Reproduce
with a longer independent benchmark run:

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ExecutionDefinitionFingerprintReuseBenchmarks.ImportAndCompute*' '*ExecutionDefinitionFingerprintReuseBenchmarks.CreateAndReuse*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~ExecutionDefinitionSerializationTests|FullyQualifiedName~ExecutionDefinitionFingerprintReuseTests'
```

Recorded short-job settings were `--inProcess --job Short --warmupCount 1 --iterationCount 3
--invocationCount 16 --unrollFactor 1`.

## Application qualification and remaining work

A local fresh-process Ari harness compared the preceding core and this revision. Catalog authoring
measured 424,642,744 B / 410,571,064 B, first admission 148,191,696 B / 142,765,312 B, and warm
admission 1,084,912 B / 1,035,992 B. These are cumulative local qualification observations, not
retained heap or release-package claims. Authoring is measured separately from admission. The
checked-in Ari isolated admission test is the application-boundary regression; no Ari package pins
or permanent source bridge changed. All 164 canonical catalog bodies matched exactly. Ari's
855 engine tests passed (18 existing skips), and its temporary test-output overlay was restored.

Qualification passed all 4,249 Cohesive core tests (33 existing skips) and 34 focused fingerprint/serialization tests. No tests were excluded or timeouts increased.

Relation-specific fingerprints are addressed in [immutable relation fingerprinting](relation-definition-fingerprinting.md).
Complete input normalization, nonscalar parent projection and
CLR nullability metadata preparation remain separate targets. Nonempty extensions still use their
existing semantic projection. This change introduces no additional retained canonical payload.
