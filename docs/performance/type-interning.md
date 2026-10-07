# Document-local scalar preparation and canonical leaf reuse

## Responsibility and invariant

`ExecutionDefinitionTypes.TypePool` owns document-local type interning and canonical numbering.
Its serializer registry remains the wire authority. Each document discovers types child-first,
then sorts each dependency depth using entries with already-final child numbers. Parents still
need that second projection; leaf entries contain no child indices and can reuse their original
canonical JSON and sort keys.

Scalar declarations commonly create separate, equal `ScalarTypeRef` instances for each field.
The sealed scalar record's value equality covers its complete kind/format contract. A document-local
memo now avoids serialization, canonicalization and string-key construction for those equal instances.
Identity lookup, cycle rejection and canonical-content deduplication remain in place for other types.
There is no cross-document retention or cache of invocation, tenant or admission data. External
strict decoding, unused-entry rejection and integrity validation are unchanged.

A 512-field declaration with separate equal string scalars now prepares one scalar entry; distinct
formats still get distinct entries. `EqualScalarInstancesReusePreparationWithoutConflatingFormats`
requires exact canonical bytes/fingerprints and bounds extra allocation for distinct identities to
100 KB above the same declaration using one shared scalar object. In the local qualification it
measured 93,760 B shared versus 141,640 B distinct. The same test against the baseline
assembly measured 94,784 B / 829,448 B and failed the new allocation bound as expected. The test includes serializer warmup, new document
preparation and retained output; input declaration construction is outside the boundary.

## Representative benchmark evidence

The checked-in `ExecutionTypeInterningBenchmarks` covers a 16-field object, the same object behind
12 nested arrays, and 128/512-field objects. Each field authors a distinct equal scalar. Serializer
metadata is warmed; every benchmark call creates a new document and document-local type pool.
The same compiled workload was run against the unchanged baseline core assembly from commit
`3fd436784934b50806e1d4be011b2ed001742c0f` and the revised core assembly.

| Fixture | Baseline allocation | Revised allocation |
| --- | ---: | ---: |
| Flat | 111.95 KB | 88.63 KB |
| Nested | 162.42 KB | 139.16 KB |
| 128 fields | 800.28 KB | 608.78 KB |
| 512 fields | 3,229.36 KB | 2,461.37 KB |

Reports retain runtime, hardware, job settings and timing dispersion:
[baseline](type-interning/baseline.md), [revised](type-interning/revised.md).
This short qualification supports the allocation comparison; its noisy timing samples are not
an end-to-end latency claim. For a longer independent timing run:

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ExecutionTypeInterning*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter FullyQualifiedName~ExecutionDefinitionTypeReferenceTests --logger 'console;verbosity=detailed'
```

The recorded short job used `--inProcess --job Short --warmupCount 1 --iterationCount 3
--invocationCount 16 --unrollFactor 1`.

## Ari source qualification

A separate local fresh-process harness loaded the old or revised core assembly, authored Ari's
164-document catalog, and measured authoring independently from first/repeated deployment admission.
Ari's checked-in isolated admission test is the reproducible application-boundary regression;
local harness values are qualification observations, not a released-package or retained-heap claim.
Exports of all 164 canonical bodies matched exactly. No Ari package pins or source copies changed.
A temporary test-output core overlay is restored after integration qualification.

Provisional parent ownership is addressed in [deferred parent materialization](deferred-parent-type-materialization.md).
Final parent-entry serialization, nonscalar content-key preparation, complete document normalization,
fingerprinting and CLR nullability metadata remain separate profiling targets. A broader reference-token
rewrite prototype preserved bytes but increased allocation and was discarded. This change retains
ordinary serializer projection for parents rather than adding another serialization mechanism.

Qualification passed all 4,247 Cohesive core tests (33 existing skips), the 24 focused codec tests,
and all 855 Ari engine tests (18 existing skips). The Ari output assembly was restored to its
published alpha.131 version after the run. No skips or timeouts were added.
