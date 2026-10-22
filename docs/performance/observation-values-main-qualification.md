# ObservationValue performance: integrated qualification against main

## Decision and scope

Retain the measured streaming parser, bounded traversal string reuse, streaming tagged-value decoder, reusable canonical writer/checkpoint profiles, aggregate scratch-storage changes, prepared response projection, and explicit strict JSON-to-CLR ingestion boundary. This report qualifies their combined effect against main `39c490ed` after rebasing; earlier reports remain historical evidence for selecting and rejecting individual designs.

Main's newer preparation/result reuse, structural evidence, annotation reuse and weak-cache publication behavior is retained. `Annotations`, `PreparedDeclaredObservationProjection`, and `WeakPreparationCache` have no branch diff. The one overlapping UTF-16 optimization extends main's `JsonText` authority with a span overload and measured ASCII gate, rather than retaining a parallel validator.

## Semantics and use

The graph, shape, existing JSON readers, serializer metadata and observation validator remain authoritative. Physical construction and interpretation change; no independent schema, scalar-kind catalog, global text interner or inferred record-shape cache is added. Pooled scratch is returned on failure and cleared when it holds references; retained immutable values own exact storage. General JSON keeps decoded last-property-wins semantics, while strict shape ingestion rejects unknown, wrong-case and duplicate root names. Tagged values and canonical checkpoint bytes retain their existing contracts. Aggregate selectors finish before reduction, preserving failure precedence.

Compile a shape-aware plan and reuse it:

```csharp
var layout = ObservationLayout.Create(shape);
var plan = ObservationMaterializer.For<MyRecord>(shape).Compile(layout);
var result = plan.MaterializeJson(utf8Json);
```

`MaterializeJson` admits the complete shape and complete input before any mapped converter runs, then invokes prebound ordinal reads. It does not allocate a name index or a Relations occurrence solely to produce the CLR result. Unmapped payloads are still parsed and validated. Existing indexed occurrences remain appropriate for lineage or additional row interpretations. See [the strict boundary contract](observation-values-layout-adoption.md).

Prepared response delegates are created at ASP.NET service registration and retained. Conventional exact-name CLR members avoid JSON round trips. Custom metadata, naming policies, reference handling, unsupported types and converter-controlled members use the existing serializer paths. Mutable results stay independent; main's explicitly immutable result reuse remains governed by its existing payload/profile rules. These APIs do not establish shape admission.

## Measurement

Control and branch use the identical benchmark harness, runtime and unchanged dependency closure. Only Core, Relations and Storage DLLs differ. Each case runs in a fresh process with three alternating repeats, four warmups and six measured iterations of 200 ms. Builds, tests and tracing finish before timing. Numbers below are medians of process means; negative time changes mean faster. Fixtures, per-process observations, raw reports and exact binary hashes are retained in [the evidence directory](evidence/observation-values-main-qualification/README.md).

| Case | Repeats | Main ns | Branch ns | Time change | B main / branch |
|---|---:|---:|---:|---:|---:|
| ParseDirect-flat | 3 | 814.27 | 552.68 | -32.1% | 1680 / 1680 |
| ParseDirect-nested | 3 | 13620.99 | 1815.29 | -86.7% | 12184 / 4720 |
| ParseDirect-collection | 3 | 74380.15 | 34711.26 | -53.3% | 99728 / 34984 |
| ParseDirect-large | 3 | 3316212.25 | 1396735.87 | -57.9% | 3.17922e+06 / 1.01925e+06 |
| ParseMapWrite-flat | 3 | 1102.68 | 695.35 | -36.9% | 2096 / 1936 |
| ParseMapWrite-large | 3 | 3739583.99 | 1548505.21 | -58.6% | 3.63867e+06 / 1.01943e+06 |
| aggregate-False | 3 | 44138.04 | 30694.27 | -30.5% | 131355 / 80 |
| aggregate-True | 3 | 218813.65 | 140240.45 | -35.9% | 459033 / 80 |
| portable-flat | 3 | 2023.31 | 927.33 | -54.2% | 2224 / 1312 |
| portable-nested | 3 | 7575.30 | 2680.72 | -64.6% | 7345 / 3360 |
| portable-collection | 3 | 157908.09 | 65228.81 | -58.7% | 159915 / 39341 |
| portable-large | 3 | 7080422.95 | 2236203.15 | -68.4% | 5.08134e+06 / 1.1505e+06 |
| Encode-flat | 3 | 6675082.90 | 17037.89 | -99.7% | 2.64594e+06 / 9367 |
| Encode-large | 3 | 39820444.46 | 11439873.83 | -71.3% | 6.06657e+06 / 2.22173e+06 |
| DecodeAndAdmit-flat | 3 | 7104139.71 | 57502.12 | -99.2% | 2.70446e+06 / 37238 |
| DecodeAndAdmit-large | 3 | 68878482.67 | 38182232.67 | -44.6% | 1.21147e+07 / 3.64948e+06 |
| strings-unique | 3 | 63871.27 | 31724.64 | -50.3% | 221240 / 110648 |
| strings-escaped | 3 | 149577.37 | 76540.98 | -48.8% | 229432 / 69024 |
| response-1-compatibility | 3 | 23395.63 | 23671.87 | +1.2% | 132433 / 132433 |
| response-4096-compatibility | 3 | 591638.76 | 580224.33 | -1.9% | 984441 / 984441 |

| Added route | Main / comparison ns | Branch ns | Time change | B comparison / branch |
|---|---:|---:|---:|---:|
| Prepared response, 1 child | 23395.63 | 1012.45 | -95.7% | 132433 / 96 |
| Prepared response, 4,096 children | 591638.76 | 207228.01 | -65.0% | 984441 / 131136 |
| Strict flat vs branch indexed names | 1051.42 | 994.32 | -5.4% | 752 / 720 |

Compatibility response time changes of +1.2% and −1.9% with identical allocation do not establish a material change. Every adopted comparison improves in all three pairs. Absolute checkpoint timing varies appreciably across fresh processes (including tiering effects); the reported medians and retained individual observations should be read together.

Checkpoint encoding and admission include complete durable envelopes. Their flat input is 3,538 bytes and large input is 1,117,436 bytes; all four fixture sizes and SHA-256 digests match main byte-for-byte. Their gains include removing repeated construction of serializer metadata for each call, not just optimizing the value payload.

The response benchmark retains 64 KiB ASCII text and returns independently owned result arrays. Prepared projection can retain immutable text without serializing and decoding it. Its large case includes 4,096 child records. Registration/preparation is excluded, so these results do not measure cold startup. Compatibility-mode measurements remain separate from the newly adopted route.

The strict flat comparison uses complete shape admission plus the same sixteen mapped Int64 fields in both routes. It isolates the incremental effect of direct ingestion into prebound fields from the broader general-parser changes. Large-payload historical qualification did not establish a reliable incremental speedup for this boundary; parsing and validation dominate it.

## Verification and limits

Post-rebase full suites: **5,155 Core tests passed, 60 skipped; 1,149 Relations tests passed**. They include strict root/trailing validation, decoded duplicates, temporal/binary kinds, malformed input and pool recovery, ownership, all aggregate functions and selector failure order, serializer fallback/converter behavior, canonical bytes, and main's newer cache contracts. The final benchmark Core's method bodies match the tested Core; the detailed comparison is retained with the evidence. `git diff --check` passes. Builds retain existing analyzer/nullability warnings.

The explicit boundary adds one public operation and shares framing with the existing indexed ingestion API; no new model/storage abstraction is needed. Wider inline contiguous field storage and indiscriminate dictionary-promotion changes remain rejected by the earlier construction/lookup experiments. The accepted eight-field storage boundary remains in force. This work does not add automatic cross-record layout inference, lazy validation of unmapped payloads, general naming-policy compilation, or new production workload claims. Those require separate semantic and performance evidence.
