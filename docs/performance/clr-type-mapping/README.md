# CLR type mapping reflection preparation

`DefaultClrTypeRefMapper` owns projection from CLR reflection metadata into portable semantic contracts.
Repeated child types previously created a new `NullabilityInfoContext` for every property occurrence,
rebuilding nullable attribute metadata repeatedly. Ari's cold process catalog profile attributed roughly
198 MB of sampled allocations to mapping, including 130 MB beneath nullability preparation.

The mapper now prepares property nullability once within each `Map` traversal, with one lazily created
reflection context and a traversal-owned property dictionary. Both are released after mapping. Calls
remain independent and concurrent calls share no mutable reflection context. Contract inference still
runs at each occurrence: explicit mappings, generic nullability, serialized identities, and recursion
paths retain their existing authority. There is no global inferred-contract cache or retention policy.
Existing `ShapeTypeInspector` caches were evaluated; their metadata retains top-level optionality rather
than the nested generic nullability tree needed here. The capability is an extension of the existing
Cohesive mapper, not an Ari-local abstraction.

Run the representative warm benchmarks with:

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ClrTypeRefMappingBenchmarks*' --job short
```

Use `--job dry` separately to inspect first invocation, including cold reflection/JIT preparation.
Dry timing is descriptive and does not establish a steady-state speedup. The warm reports were collected
on the same machine with identical benchmark source, first with the changed mapper and then with the
original mapper at commit `39b446a`. Short-run timing has wide confidence intervals, particularly for
large shapes; allocated bytes are the primary evidence. Shapes are bounded CLR type graphs, not runtime
collection contents. The large shape has eight branches of four shared leaves.

Warm allocation per mapping fell from 361.97 KB to 98.11 KB for large (73%), 92.88 KB to 35.28 KB for
collection (62%), and 41.87 KB to 18.47 KB for nested (56%). Flat remained approximately 6.9 KB.
Tests protect repeated shape allocation, nested generic nullability, serialized identities, recursion
from different roots, explicit mapping isolation, and concurrent invocations. The allocation ceiling
includes retained IR and temporary preparation after warming readable-property discovery; it is
intentionally generous and imposes no elapsed-time assertion.

These microbenchmarks do not establish Ari suite or production latency. Ari adoption requires a
consumable Cohesive package and a separate representative end-to-end measurement.

Ari's nine canonical compilation cases passed against both local core packages, with all other
Cohesive dependencies pinned to alpha.122. Identical local core source except this mapper change
was packaged as `0.1.0-dev.mapperbaseline.20261003` and `0.1.0-dev.mapper.20261003`. In the first cold
X12 004010 normal case, catalog allocations fell from 644,928,552 to 552,825,296 bytes (14.3%).
Total case allocations fell from 2,808,940,480 to 2,710,862,608 bytes (3.5%). Catalog wall time was
1,522 ms versus 1,460 ms; total case time was 6,335 ms versus 6,280 ms. Both nine-case runs took
17 seconds. This does not establish a meaningful end-to-end latency improvement; remaining JSON
materialization and fingerprinting work dominates. Tests passed with the production validation
boundaries intact. Full Cohesive project: 4,159 passed, 33 optional integration skips; final mapper
subset: 11 passed. The allocation regression fails on the original implementation.

Full Ari.Engine.Tests qualification with the fixed local package passed 849 tests, with 18 scheduler
skips, in 96 seconds. A preceding ordinary-package run took 90 seconds. Single runs with differing
parallel scheduling and instrumentation do not establish a suite latency improvement.
