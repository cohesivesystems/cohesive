# Strict nested ingestion adoption

This change integrates [layout-bound nested validation](observation-values-ordinal-validation.md) into `ObservationMaterializer<T>.MaterializeJson` for plans explicitly compiled with an `ObservationLayout`. Root parsing/admission still covers every field, including unmapped payloads, before projection converters execute.

## Semantic and physical authority

The original graph, type definitions, typed JSON reader and validator remain semantic authorities. A private ingestion plan traverses the existing prepared validation closure, prepares complete unambiguous nested layouts, and binds declaration indexes to their physical ordinals. Cycles are traversed finitely by reference identity. Scalars, dates, bytes, arrays, named types, unions, opaque/JSON values and depth checks continue through the existing typed reader and validator; no Int64-only parsing candidate is promoted.

Private nested layouts reuse `ObservationLayout`'s dispatch/order mechanism without constructing synthetic domain graphs or registering new shape identities. Their qualified identity is owning-root provenance only. `ObservationNestedFields` exposes an immutable dictionary view, **not** a shape-qualified reader; nested physical storage cannot escape as a claimed domain shape. The wrapper reuses dense or packed ordinal storage and canonical traversal unwraps it through the existing mechanism. Records through the existing eight-field inline boundary keep the original typed reader. Eligible wider, more-than-half-full records use dense vectors; wider at-most-half-full records use packed values. This threshold is inherited from the measured experiment, not a universal optimum.

Exact decoded property names use ordinal storage, with exact decoded nested duplicates keeping the last value. On a spelling/unknown-name miss, parsing restores that object's reader checkpoint and uses the original typed reader for that object. Misses count per private layout. An exact read resets the count, and four consecutive misses stop speculation for that layout for the rest of the input. One stray spelling early in a batch costs a single fallback rather than disabling the fast path for every later row, while a consistently differently-cased batch stops speculating after four rows. Earlier completed records and ancestors keep their immutable layouts. Validation uses ordinal bindings only where exact private layout identity proves them applicable; generic storage retains original lookup semantics. Ambiguous case-insensitive declarations are not bound. Union discriminator exceptions retain the original validator behavior. This replaces the experiment's whole-input retry with a local typed-reader retry; observable outcomes, spelling and diagnostics are preserved by differential checks.

Failures rerun the original unbound diagnostic interpreter. Strict root names, decoded duplicate/unknown-root rejection, complete-input framing, admission-before-trailing-content error precedence, missing-member policy, serializer customization and converter ordering are unchanged. Caller input is borrowed only for the call. Scratch arrays are cleared on return, completed values own their storage, mutable CLR projections remain independent, and the shared plan is immutable; fallback state belongs to the individual traversal.

The change introduces two internal concepts: the scoped ingestion plan and the nested dictionary view that prevents domain-identity leakage. The latter owns no parallel value algorithm; it reuses the existing storage implementations. Public signatures and dependencies of Core are unchanged. Only a benchmark project reference to the repository's existing freight model is added. Name-only materializers and ordinary JSON conversion do not select this ingestion plan.

## Verification and measurements

Five new test cases and existing strict-boundary tests check exact diagnostics/canonical bytes/hashes, reordered root layouts, inline/named records, temporal/binary hydration, escaped and duplicate names, case/unknown fallback, malformed-input recovery, converter ordering, sparse allocation budgets, bounded boxed enumeration, input ownership, mutable output independence and concurrent reuse. The canonical freight application's graph and DTOs round-trip in both exact-case and case-variant nested forms.

All **5,188 Core tests** and **1,149 Relations tests** pass; 60 environment-dependent Core tests are skipped. The **223 differential scenarios** now cover seven interpretations, including the integrated production reader and the prior experimental/reference routes. They include recursive declarations, unsupported types and union fallback. The 24 focused boundary tests pass. The frozen final benchmark Core is checked against the fully tested Core for identical method bodies, flags, local signatures and exception regions.

All phase comparisons use one executable/dependency closure in each matched pair, swapping only `Cohesive.dll` between previous and integrated implementations. Each exact workload/route runs in a fresh BenchmarkDotNet process, four warmups, six 200 ms iterations, three counterbalanced repeats. The final qualified configuration has 60 main timing processes and 12 additional joined-load DTO processes, using the identical final Core in two matched harness closures. Three preceding 60-process phases preserve the initial candidate, isolated cursor-enumerator change, and broadly enabled canonical-ordering candidate. The application evidence motivates restricting binding to records wider than eight fields. Final canonical ordering also recognizes the private nested view, avoiding dense-field buffer rental. No timing overlaps builds, tests or compression. Compilation/cache population is outside timing.

The joined-load fixtures wrap the repository’s existing nine-field `LoadSearchDto` contract in a batch envelope. The existing CLR graph producer supplies its complete schema, avoiding a handwritten duplicate. String and decimal values are projected completely at 128 and 4,096 rows. The freight fixtures use the canonical `FreightOrderMaterializationModel` graph and existing `FreightOrder`/`FreightOrderStop` contracts, complete CLR projection, date restoration, identifiers and repeated text. Sizes are 8, 128 and 4,096 stops; a case-variant fixture changes nested `sequence` spelling while retaining the existing projection options. Sparse controls have 32 declared fields/four present per row and an unmapped payload that still requires complete admission. Flat/nested/collection/large controls retain the previous opaque `JsonTypeRef` workload, which is not eligible for record layout binding. This verifies in-process application ingestion and projection, not external database or service throughput.

## Final application and control measurements

Median of three fresh-process means; times are μs, allocation is B/complete ingestion.

| Workload | Before μs | Integrated μs | Speedup | Before B | Integrated B | Allocation change |
|---|---:|---:|---:|---:|---:|---:|
| freight-8 | 5.308 | 5.381 | 0.99× | 8960 | 8960 | +0.0% |
| freight-128 | 93.412 | 85.397 | 1.09× | 76424 | 77056 | +0.8% |
| freight-case-128 | 87.004 | 96.327 | 0.90× | 143936 | 143824 | -0.1% |
| freight-large | 3046.691 | 2911.075 | 1.05× | 2307480 | 2267866 | -1.7% |
| sparse-128 | 95.431 | 36.979 | 2.58× | 102256 | 37040 | -63.8% |
| sparse-large | 3573.795 | 1477.901 | 2.42× | 3181900 | 1180039 | -62.9% |
| flat | 0.977 | 0.992 | 0.98× | 720 | 720 | +0.0% |
| nested | 1.883 | 1.868 | 1.01× | 2848 | 2848 | +0.0% |
| collection | 29.024 | 29.365 | 0.99× | 35424 | 35424 | +0.0% |
| large | 1131.035 | 1129.895 | 1.00× | 1019598 | 1019711 | +0.0% |
| joined-128 | 225.388 | 163.334 | 1.38× | 236136 | 141136 | -40.2% |
| joined-large | 11895.203 | 10267.625 | 1.16× | 7367388 | 4419160 | -40.0% |

## Storage classification follow-up

`ObservationFieldStorage` is now the single classification of physical object storage. It unwraps nested views and
says which storage is owned and immutable and which is already in ordinal key order. Retention, traversal,
validation binding and canonical ordering all use it. Previously, five separate checks had drifted: canonical
ordering unwrapped nested views but did not recognize packed sparse storage. Canonical JSON, hashing and tagged
`PortableValue` writing therefore rented and sorted a field buffer for every sparse nested row. They now enumerate
packed storage in place. In a 128-row sparse batch with 4 of 16 fields present, canonical writing went from about
230 to 213 µs, with no managed allocation in either version, because pooled buffers were already reused.

`ObservationNestedFields` requires `IOrdinalObservationStorage`, so wrapping unsupported storage fails at
construction. Its enumerator moves to the next present ordinal through the presence bitmap and reads each value once.

## First-integration allocation finding

The initial candidate boxed the inner storage enumerator, including its cached key/value pair. The final nested view boxes only its cursor state, and canonical ordering recognizes its dense storage without a field buffer. The final production policy also excludes narrow records; the table below reflects all these changes together, not an isolated enumeration speedup. Canonical traversal continues to use the existing unboxed enumerators. This adds no new value representation or serialization semantics.

| Workload | Initial integrated B | Final integrated B |
|---|---:|---:|
| freight-8 | 8256 | 8960 |
| freight-128 | 85144 | 77056 |
| freight-large | 2580728 | 2267866 |

## Adopted boundary and tradeoffs

Enable the new path only for **unambiguous nested records wider than the existing eight-field inline boundary**, inside an explicitly layout-bound materializer's strict JSON entry point. Smaller records, ambiguous declarations and ineligible values retain the original typed reader. Private layouts and bound validation do not replace domain identities or semantic definitions.

The real joined-load application contract has nine fields, including strings and decimal values. Its 128-row batch improves from 225.388 to 163.334 μs (1.38×) with allocation reduced from 236,136 to 141,136 B (40.2%). Its process means are 190.628–355.139 μs before and 160.613–166.006 μs after. At 4,096 rows the median improves 1.16× and allocation falls 40.0%; CPU ranges overlap substantially (9.043–15.418 ms before, 7.794–10.936 ms after), so the larger batch's allocation result is stronger evidence than its timing median.

The wide sparse admission control improves 2.58× at 128 rows and 2.42× at 4,096 rows, reducing allocation by 63.8% and 62.9%. These cases include complete admission of an unmapped payload, not just mapped field reads. They establish the intended sparse-ingestion gain rather than stand in for complete application projection.

The initial broadly enabled implementation did not justify changing narrow freight records: allocation increased on repeated four-field stops, while CPU results were process-sensitive. Shrinking the nested-view enumerator and recognizing its dense storage in canonical ordering reduced avoidable work, but did not establish a universal small-record win. The final plan therefore retains the original freight reader. This is a measured physical selection constraint, not a new semantic restriction. The historical prototype tables and raw phases must not be presented as final adoption results.

Small freight records and opaque-JSON controls are regression controls. The eight-stop freight case and flat/opaque controls have about 0–1.5% median timing differences, with unchanged small-fixture allocation. Repeated freight measurements remain noisy even though the selected reader is unchanged: the 128-stop before range is 81.958–146.652 μs, and the large before range is 2.830–5.028 ms. Case-variant freight has a +10.7% median difference, but wide overlapping ranges (85.637–110.733 μs before, 87.512–116.329 μs after). **No fallback latency equivalence or speedup is claimed.** Its original casing, acceptance, values and diagnostics are preserved; further JIT/tail-latency qualification remains useful. Larger allocation counters also vary between processes, so small differences are not attributed to the binding policy.

Preparation and private metadata are outside warm timing and remain per compiled layout-bound materializer. Their cold cost, schema lifetime and density crossover are intentionally not claimed optimal here. The nested view's ordinary enumerator is bounded to a small cursor box; canonical dense ordering uses no field-buffer rental. Canonical compact ordering can still use the existing pooled ordering buffer. Further physical consolidation or required-bitmap/present-only validation is deferred instead of adding another value format or semantic rule catalog.

Verification completes on the final gated implementation: 5,188 Core tests pass, 60 environment-dependent tests skip; 1,149 Relations tests pass; 24 focused strict-boundary tests pass; all 223 differential scenarios pass across seven variants. The tested Core, final main timing Core and joined-load timing Core are **byte-identical**, in addition to method-body/flags/local-signature/exception-region equality. The last benchmark-only extension adds the real joined DTO batch; it changes no product code. Every projected field is checked before each timing process.

The [evidence directory](evidence/observation-values-nested-adoption/README.md) retains 72 final timing processes, 180 preceding exploratory processes, all source snapshots, matched dependency hashes, complete raw reports and verification logs. Selection is adopted in `MaterializeJson`; ordinary JSON conversion and name-only materialization are unchanged. All changes remain in the isolated branch, and the main checkout is unchanged.
