# Share context-independent CLR inspection

`DefaultClrTypeRefMapper` remains the owner of deterministic CLR-to-portable inference. The existing
`ShapeTypeInspector` supplies filtered readable properties and shared property nullability. Its raw
property cache did not eliminate repeated serialized-name discovery, sorting, polymorphism attributes
or structured-quantity interface discovery for each occurrence. Extending preparation inside the
existing mapper avoids introducing an Ari-specific mapper or a second inference authority.

For example, eight branches containing four equal leaf CLR shapes previously rediscovered and sorted
the same four property names 32 times per mapping. The mapper now prepares that ordered metadata once
per live CLR type. It still constructs 32 independent leaf contracts, applies the current mapper's
explicit overrides and preserves each property's nullability. Duplicate serialized names remain an
opaque diagnostic; ordinal ordering permits an adjacent comparison without a temporary set.

Three private weak-key tables lazily coordinate the independent inspection operations. They cache
only property/name metadata, polymorphism presence and the quantity representation type (including
absence). Preparation occurs at the existing decision point, preserving inference precedence and
avoiding eager inspection of unused capabilities. Lazy publication coordinates concurrent first use;
immutable CLR metadata and its discovery failures remain associated with that type identity. Private
property arrays are populated once and never exposed or changed. No inferred graph, serializer,
converter, explicit mapping, recursion path or caller occurrence nullability is shared.

These tables add no permanent CLR type roots. Existing `ShapeTypeInspector` strong type/property
caches are unchanged, so this change does not establish collectibility for the entire subsystem.
Other converter and collection inspection remains outside this fix.

## Measurements

The existing `ClrTypeRefMappingBenchmarks` retains each new IR graph after metadata warmup, covering
flat, nested, collection and eight-branch shapes. Baseline is `e56b794`; both runs use the same compiled
workload with only the Core assembly changed. Short in-process samples are allocation evidence,
not an end-to-end latency claim. The large timing sample has substantial dispersion.

| Shape | Previous allocation | Revised allocation |
| --- | ---: | ---: |
| Flat | 2.43 KB | 1.05 KB |
| Nested | 10.73 KB | 3.88 KB |
| Collection | 23.40 KB | 8.37 KB |
| Eight branches | 86.47 KB | 29.96 KB |

[Baseline](shared-clr-inspection/baseline.md) and [revised](shared-clr-inspection/revised.md) preserve
runtime, sampling settings and timing dispersion. Run with:

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ClrTypeRefMappingBenchmarks*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~DefaultClrTypeRefMapperTests|FullyQualifiedName~ObjectEntityDefinitionTests'
```

Warm fresh-mapper leaf and repeated large-graph regression ceilings include retained IR and traversal
work (1,400 B and 35,000 B). Concurrent cold mapping tests preserve serialized names, nullability,
independent graph ownership and explicit override isolation. Existing ambiguity, recursive graph,
polymorphic diagnostic and caller occurrence tests remain in force. The preceding Core fails the
new ceilings at 2,416 B for the leaf and 88,448 B for the large graph.

A paired fresh-process Ari source harness measured cumulative allocation at three separate boundaries:

| Boundary | Previous | Revised |
| --- | ---: | ---: |
| Catalog authoring | 231,058,560 B | 198,737,112 B |
| First admission | 93,241,368 B | 92,356,056 B |
| Warm admission | 814,696 B | 814,696 B |

Authoring includes cold metadata preparation. These are allocated bytes, not retained heap or
published-package measurements. All 164 canonical catalog bodies remained byte-identical. Ari package
pins are unchanged; its test dependency overlay is temporary and restored after qualification.

Qualification passed 4,297 Core tests (33 existing skips), 1,082 Relations tests and 855 Ari engine
tests (18 existing skips), plus 30 focused final mapper/entity tests. Both Ari dependency assemblies
were verified restored byte-for-byte. No tests were excluded or timeouts increased. This change is
local and unpublished.
