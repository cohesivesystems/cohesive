# Immutable execution-definition fingerprint reuse

Catalog admission and block compilation both call shared execution-document integrity validation.
The canonical writer and SHA-256 computation were repeated for the same immutable document, including
immediately after the authored factory had computed its fingerprint. Ari's graph-derived closure adoption
exposed this multiplication by admitting a Process index before compiling the same definitions.

## Ownership and guarantees

`ExecutionDefinitionDocument` owns its successful computed digest for its own lifetime. Its definition
is normalized into an owned immutable `JsonElement`; extensions obey immutable portable-value contracts.
The retained result contains only a small versioned fingerprint, not canonical bytes, typed projections,
source documents, contextual admission results, or runtime invocation data. There is no global cache,
lookup by identity/revision, or new public model. Lifetime and invalidation follow the document object;
a new external document computes independently, even with identical identity, revision, and metadata.

The authored factory seeds the field only with the digest independently computed from the semantic
payload. The public/import constructor never trusts declared fingerprint metadata as a computed value.
Imported first use is coordinated through `LazyInitializer`: successful computation is shared among
concurrent callers, exceptions remain retryable. `WithRetainedDiagnostics` can reuse a successful digest
because it changes only non-semantic observations. Equality, hashing, serialization, canonical bytes,
and fingerprint profiles remain unchanged.

The validator still checks declared algorithm/profile/value and compares the computed digest on every
admission. Extension portability is checked against the supplied graph each time. No Process semantics,
shape evidence, authorization, tenant context, or physical capability admission is cached. Component-based
`ExecutionDefinitionFingerprinter.Compute(...)` remains uncached independent recomputation.

This extends the existing canonical document/fingerprinter responsibility in Cohesive. Alternatives were
an Ari-specific cache, a global cache by identifier, or bypassing repeated integrity validation through a
trust flag. Those either put reusable work in the wrong owner, confused distinct documents, or weakened
admission. Document-owned successful computation preserves the existing boundary with two private fields.

## Qualification

`ExecutionDefinitionFingerprintReuseTests` proves construction digest reuse, independent import computation,
concurrent successful first use, rejection of forged metadata after reuse, distinct content with the same
identity/revision, fresh graph-dependent extension validation, unchanged equality/hash/serialization,
canonical equivalence, and bounded warm allocation (1,024 calls on a 4,096-row payload).

46 focused fingerprint/serialization/catalog/compatibility tests passed. The complete Cohesive test
project passed 4,185 tests with 33 existing environment-dependent skips; its broader execution subset
also passed 1,309 tests with 10 existing skips. The complete Ari engine project, locally selecting
the new core and the closure compiler from PR #396, passed 852 tests with 18 existing skips. This local
assembly qualification is not exact published-package conformance or a deployment claim.

## Benchmarks

Run the existing benchmark project in Release:

```sh
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- \
  --filter '*ExecutionDefinitionFingerprintReuseBenchmarks*' --job short --inProcess \
  --warmupCount 1 --iterationCount 3 --launchCount 1
```

The baseline resolves the previous internal normalized computation as a delegate once during setup;
measured calls perform no reflection. Flat, 24-level nested, 128-row collection, and bounded 4,096-row
inputs have separate authored-first-use, imported-first-use, and warm cases. Both import cases include
normalization and owned JSON construction. Warm preparation is excluded from measured calls.

BenchmarkDotNet 0.15.8 ran on macOS 27.0.1, Apple M5 Max, .NET SDK 10.0.201/runtime 10.0.5, Arm64.
All 24 cases completed. The short in-process run overlapped local validation work; timings are indicative
and have wide confidence intervals. The warm cached read is below a reliable timing measurement boundary;
do not interpret its sub-nanosecond reported mean as actual operation latency. Allocation results and
the deterministic allocation test establish elimination of payload-sized warm work.

| Shape | Warm recomputation bytes | Warm reuse bytes | Authored first-use bytes: before → after |
| --- | ---: | ---: | ---: |
| Flat | 1,608 | 0 | 6,136 → 4,528 |
| Nested | 6,992 | 0 | 30,984 → 23,992 |
| Collection | 133,408 | 0 | 538,696 → 405,289 |
| Large | 4,376,268 | 0 | 17,541,898 → 13,165,492 |

Imported first use still performs integrity computation and adds about 88 bytes for coordinated initialization
on the smaller inputs. Large first-use GC accounting varies; no imported first-use allocation reduction is
claimed. Retention is one digest and, for imported first use, a private lock, bounded by each document's lifetime.
Full [BenchmarkDotNet report](evidence/execution-definition-fingerprint-reuse-benchmark.md) and
[CSV](evidence/execution-definition-fingerprint-reuse-benchmark.csv) retain the environment and all cases.

## Ari admission evidence

The isolated `RepeatedAdmissionSharesPreparationButRetainsFreshDeploymentValidation` test measured the
same boundary: source definition construction before measurement, first `Create` including preparation,
realization and fresh admission, then a second warm `Create`. Release .NET 10.0.201/macOS:

| Source and selected implementation | First allocated bytes | First ms | Warm allocated bytes | Warm ms |
| --- | ---: | ---: | ---: | ---: |
| Existing manual ordering, old core | 583,683,336 | 1,829.74 | 43,197,584 | 252.41 |
| Derived closure, old core | 670,257,384 | 2,152.06 | 43,201,840 | 201.41 |
| Derived closure, document digest reuse | 549,441,672 | 1,349.27 | 42,933,456 | 127.03 |

The combined local change removes the cold-allocation regression and allocates approximately 34 MB less
than the manual-source sample. The fingerprint change alone reduces the derived-closure sample by about
121 MB (18%). Single elapsed samples and a unit-suite run under concurrent benchmarking do not establish
an end-to-end speedup. Publication, exact package adoption, and package qualification remain separate steps.

Documents can also retain explicitly immutable typed projections through the separate
[execution preparation contract](immutable-execution-preparation.md). That cache does not change
the fingerprint-only cache described here or supply contextual admission evidence.
