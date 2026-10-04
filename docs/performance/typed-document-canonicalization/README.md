# Typed strict document canonicalization

`StrictDocumentJson` owns typed portable-document canonical bytes. Previously, it serialized a typed
value into a `JsonNode` and expanded every object/array during canonical traversal. Process snapshot
and repository fingerprinting repeat this operation on large documents. Ari's cold compilation
profile identified repeated typed canonicalization as substantial CPU and allocation work.

The typed API now serializes to an invocation-owned immutable `JsonDocument` and uses the existing
`CanonicalJsonWriter` exact decimal-rational sequence traversal. This extends the owning Cohesive
component; no Ari implementation, new canonical profile, or inferred model is introduced. The
serializer still supplies wire names, converters, and scalar encoding. Object names are ordered
ordinally and arrays preserve sequence order. The existing exact-number writer remains authoritative.

Caller-owned values and invocation results are not cached. The temporary document is disposed before
return; canonical output bytes belong to the caller. Object-property sorting uses the existing pooled
traversal and clears property references before returning the buffer, including on failure. Duplicate
ordinal properties are rejected during the sorted traversal. Non-strict case-insensitive serializer
options retain the previous JsonNode implementation to preserve its property collision behavior.
Converters that write a null root still fail, while scalar and array converter roots remain supported.

Differential tests compare bytes and SHA-256 fingerprints with the previous node path across all
ObservationValue kinds and 512 seeded generated values, including nested values, numeric boundaries,
escaping, and base64 binary encoding. Further checks protect duplicate/case-collision failures,
null roots, custom converter roots, binary rejection policy, formatting independence, and caller
mutation. An allocation regression compares the two paths on the same bounded collection document;
no elapsed-time threshold is asserted.

## Measurement

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*TypedDocumentCanonicalizationBenchmarks*' --job short
```

The retained [BenchmarkDotNet report](benchmarks.md) compares both algorithms within one run on an
Apple M5 Max, .NET 10.0.5, Release. Setup prepares the inputs and verifies canonical equivalence;
measurements cover warm serializer metadata and include returned canonical bytes. Flat, 24-level
nested, 128-row collection, and bounded 4,096-row large documents are represented. These are allocation
and mechanism measurements, not production latency claims. Short-run time confidence intervals are
wide. Managed allocations fell 57–68% across the shapes. The large document fell from 5,826.82 KB to
1,980.90 KB, with mean time 3.18 ms versus 1.96 ms.

Ari's nine canonical compilation cases passed through local-only NuGet packages, keeping every other
Cohesive dependency at alpha.122. The mapper-only package and this package contain identical source
except typed canonicalization. The first cold X12 004010 normal case fell from 2,710,862,608 to
2,118,381,144 allocated bytes (21.9%). Process execution fell from 1,389,711,680 to 795,627,560 bytes
(42.7%), and 2,564 ms to 1,526 ms. Total case time was 6,280 ms versus 5,514 ms; nine cases took
17 versus 14 seconds. This single local comparison includes JIT, cold catalogs, and other process work;
it does not establish deployment or production latency. Cold definition catalog allocations remained
approximately 553 MB. Further immutable fingerprint reuse or streaming hashing is separate work.

Full Cohesive.Tests validation passed 4,168 tests with 33 optional integration skips, and the expanded
canonical subset covers converter and binary failure paths. Canonical bytes, fingerprint profiles,
and external/persisted validation boundaries remain unchanged. Ari adoption follows review and a
consumable package release.

The allocation regression fails on the previous implementation (406,408 bytes on both compared
paths). Full Ari.Engine.Tests qualification passed 849 tests, 18 scheduler skips, in 72 seconds
against this local package, versus 96 seconds for the mapper-only local package. Both runs enabled
the same optional stage instrumentation. These are single local samples, not CI speedup guarantees.
