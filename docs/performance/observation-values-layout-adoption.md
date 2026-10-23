# Adopting shared layouts for strict JSON materialization

## Boundary and use

`ObservationMaterializer<T>.MaterializeJson(ReadOnlySpan<byte>)` is the strict shape-aware ingestion boundary for an explicitly layout-bound compiled CLR projection:

```csharp
var layout = ObservationLayout.Create(shape);
var materializer = ObservationMaterializer.For<MyRecord>(shape).Compile(layout);

// Reuse the plan and exact layout across records and requests.
var record = materializer.MaterializeJson(utf8Json);
```

The governing graph/shape and field definitions remain authoritative. The bound layout supplies the exact graph and precompiled field ordinals. Parsing constructs a single owned value vector and uses the existing `ObservationJsonReader` and `ObservationValidator`. After complete admission, the existing ordinal field reader transfers those values to the already compiled projection delegate. There is no per-record property-name index, per-field name lookup, second shape validation, or intermediate portable `Observation` object.

A name-only plan throws `InvalidOperationException` on this entry point: choosing strict ingestion and its exact layout is explicit. Existing `Materialize` overloads, general JSON conversion, fallback readers and their missing/duplicate policies are unchanged. A plan already compiled with an explicit layout can use this entry point without another authoring or configuration abstraction.

## Semantic preservation

- Root names remain exact-case layout identities. Unknown, wrong-case and decoded duplicate names fail with `JsonException`; ordinary JSON's last-property-wins behavior is unchanged elsewhere.
- The complete record is admitted before any custom converter or CLR construction, including all unmapped fields and trailing-input checks. Shaped temporal/binary restoration and semantic validation remain in the existing reader/validator.
- Missing, explicit Null, required fields, constructor defaults and configured missing-member policy retain their existing meanings. Serializer options affect CLR projection and cannot relax shape admission.
- Value storage is freshly owned; caller input buffers are never retained. Default mutable CLR results are independent. Custom converters keep their existing contracts.
- Presence words use bounded stack space or a temporary pooled buffer returned in `finally`, including parse, validation and conversion failures. Values themselves are never pooled or exposed as caller-mutable storage.

The existing `IndexedObservationOccurrence.FromJson` boundary now shares complete-input framing with this entry point. The helper preserves its failure order (shape admission before trailing-content inspection), messages and parameter names. It still retains its own occurrence/presence-buffer representation; the new Core API does not introduce a dependency on Relations or allocate occurrence metadata solely to materialize a CLR record.

## Architecture and alternatives

Existing layout-aware materialization already compiles field reads once. This change makes strict JSON ingestion flow directly into that path. It adds one public operation, no new semantic/storage type, no global record-shape inference and no additional cache. Shared complete-input framing centralizes the existing root/trailing policy rather than making the two ingestion entry points independent semantic authorities.

Keeping the public indexed-ingestion API plus an ordinal plan is still appropriate when callers need occurrence identity, lineage, retained indexed rows or additional interpretations. The new operation is appropriate when callers need a CLR result directly. General JSON boundaries are not migrated to strict parsing. The benchmark-only internal composition from the [shared-layout investigation](observation-values-shared-layout.md) is no longer the only way to use the Core pipeline.

## Verification

The full Core suite passes 5,094 tests (59 skipped); the full Relations suite passes 1,144 tests. New tests cover reordered layout ordinals, escaped keys, explicit null/defaults, name-only plan rejection, duplicate/case/unknown/trailing failures, validation of unmapped required fields before converters, custom converter execution order, independent binary outputs, warm allocation bounds, and 65/4,097-field presence buffers after malformed input. Indexed-ingestion tests also cover zero fields, multiword presence and shape-failure precedence over trailing content.

The final build's Core method bodies match the fully tested build across all 11,814 methods; only import ordering/documentation changed afterward. Builds emit existing analyzer/nullability warnings. `git diff --check` passes.

## Measurement protocol

`ObservationJsonMaterializationBenchmarks` compares three complete strict JSON-to-CLR routes mapping the same sixteen Int64 fields:

1. Existing indexed ingestion followed by a name-bound plan.
2. Existing indexed ingestion followed by an ordinal-bound plan.
3. The new `MaterializeJson` operation on that ordinal-bound plan.

All routes validate the same whole shape. Nested, 128-row collection and 4,096-row large cases include an additional unmapped Json-typed payload, ensuring that its construction and validation cannot be skipped merely because the CLR result omits it. Plans and schema/layout setup are excluded from warm timing; output DTO construction is included. Each route/workload runs in a fresh process, with three repeats and alternating route order.

A separate before/after guard reuses the previous frozen benchmark assembly, runtime closure and three exact public indexed-ingestion cases (4/16/32 fields plus sixteen ordinal reads). Only Core and Relations DLLs differ between those two closures. This detects regressions from shared framing independently of the new API's savings. The new end-to-end comparisons all use the same current assembly closure.

BenchmarkDotNet uses `--inProcess`, four warmup iterations and six measured iterations of 200 ms. Each CLI filter must discover exactly one benchmark. Timings do not overlap builds, tests, traces or other timed runs. Reports and manifests are in [the evidence directory](evidence/observation-values-layout-adoption/README.md). These results qualify the current working-tree implementation against its frozen pre-adoption baseline, not against subsequently merged changes on main.

## Results and decision

Median of the three process means; negative changes are faster. Allocation is per complete parsed/materialized record.

| Workload | Pairs | Indexed names ns | Indexed ordinals ns | MaterializeJson ns | Change vs names | B: names / ordinals / new |
|---|---:|---:|---:|---:|---:|---|
| flat | 3 | 1023.36 | 984.74 | 970.35 | -5.2% | 752 / 752 / 720 |
| nested | 3 | 1901.83 | 1868.09 | 1873.83 | -1.5% | 2880 / 2880 / 2848 |
| collection | 3 | 31806.68 | 29512.61 | 29494.03 | -7.3% | 35456 / 35456 / 35424 |
| large | 3 | 1129995.66 | 1176185.63 | 1179789.14 | +4.4% | 1.01974e+06 / 1.01974e+06 / 1.01973e+06 |

| Guard width | Pairs | Before ns | After ns | Change | B before / after |
|---:|---:|---:|---:|---:|---|
| 4 | 3 | 207.76 | 210.57 | +1.4% | 224 / 224 |
| 16 | 3 | 722.79 | 721.89 | -0.1% | 608 / 608 |
| 32 | 3 | 1403.61 | 1405.30 | +0.1% | 1120 / 1120 |

The flat-record result is a modest improvement over an already shape-aware parser: approximately 5.2% lower latency and 32 B less allocation per record. Merely choosing an existing ordinal plan realizes most of the CPU benefit; the new boundary also avoids the Relations occurrence object and shape/layout dispatch needed by that route. The 32 B saving is deterministic for flat/nested/collection fixtures.

The initial large-payload median is 4.4% slower than the name-plan route, and the existing ordinal route is similarly slower. A counterbalanced follow-up with six warmups, ten measured iterations of 300 ms and four fresh-process pairs gives 1.2371 ms (names) versus 1.2435 ms (new), +0.5%. Paired differences are +3.2%, -3.6%, +1.0%, -0.7%. This does not establish a repeatable large-input speedup or regression. Payload construction/validation dominates those cases. Process-randomized traversal string-cache collisions can also vary large-input allocation; the small differences in that row must not be interpreted as an allocation improvement. Both regimes are preserved in the raw evidence.

The existing indexed-ingestion guard changes by +1.4%, -0.1% and +0.1% at 4/16/32 fields, with identical allocation. This is not evidence of a material guard improvement or regression.

**Retain the explicit strict entry point and shared framing.** It makes the previously measured shared-layout path directly usable in Core, preserves full admission, and gives a small measured incremental gain for the representative scalar mapping without inventing occurrence metadata. It does not justify migrating permissive JSON boundaries or claiming a universal pipeline speedup. Whole-tree parsing/validation of unmapped payloads remains intentionally intact.

These measurements are working-tree qualification against the recorded pre-adoption baseline. The subsequent [integrated qualification against main](observation-values-main-qualification.md) records the rebase, current full test counts and fresh matched comparisons; historical measurements are not substituted for that comparison.
