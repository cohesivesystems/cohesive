# Share property-declared CLR nullability metadata

## Responsibility and example

`DefaultClrTypeRefMapper` maps CLR authoring inputs to portable type references. Its previous traversal
cache avoided preparing a repeated property twice in one mapping, but every new invocation rebuilt
`NullabilityInfoContext` and property metadata. For example, repeatedly mapping a four-property leaf
allocated 7.68 KB per warm invocation, even across otherwise identical mapper instances.

`ShapeTypeInspector` already owns reflection-property preparation, including top-level shape
optionality. Extend that existing responsibility with an internal full nullability-tree lookup shared
by both the inspector and mapper. Nested generic and element metadata remain intact; top-level
optionality alone cannot replace them. The resulting warm leaf mapping allocates 2.72 KB.

## Cache and semantic boundaries

The lookup is a `ConditionalWeakTable<PropertyInfo, Lazy<NullabilityInfo>>`. Weak keys avoid adding
permanent property/type retention. A selected lazy entry coordinates first use; its reflection context
is created locally and never shared. Completed metadata is an internal read-only input and never
returned through a public API: its mutable generic-argument array must not escape or be modified.
The entry contains property-declared reflection facts, with no inferred type contract, explicit mapper
configuration, tenant context or caller-supplied occurrence metadata. CLR property metadata remains
valid for that property's lifetime, so it needs no version invalidation.

The mapper still traverses each occurrence and applies its own explicit mappings and recursion path.
For example, `IReadOnlyList<KeyValuePair<string,string>>` and the same runtime CLR type with a nullable
value annotation continue to produce distinct required/nullable value fields when supplied as root
occurrences. Shared property metadata cannot override those caller-provided annotations. Separate
mapper instances do not share their inferred contracts.

The mapper retains its existing `ArgumentException` fallback for unavailable nullability; the shape
inspector retains its existing exception behavior. A lazy metadata preparation failure is associated
with the stable property identity rather than retried on each invocation. Existing strong readable-
property/type caches in `ShapeTypeInspector` remain; this change does not claim to make that entire
subsystem support collectible assemblies.

## Evidence

The existing `ClrTypeRefMappingBenchmarks` fixtures were run with the core at `5ec2e78` and with
this change. Workloads are bounded CLR graphs; runtime collection contents are not measured.
Declaration setup precedes measurement, metadata is warmed, and each invocation constructs a new
portable type graph. Allocation includes retained IR and invocation-owned traversal work.

| Fixture | Previous | Revised |
| --- | ---: | ---: |
| Flat leaf | 7.68 KB | 2.72 KB |
| Nested branches | 19.53 KB | 11.03 KB |
| Collection | 36.63 KB | 23.52 KB |
| Eight branches | 101.35 KB | 86.74 KB |

[Baseline](shared-property-nullability/baseline.md) and [revised](shared-property-nullability/revised.md)
reports record macOS Arm64/.NET 10.0.5 and short-run settings. These local samples establish allocation
comparisons, not production latency. First use still prepares reflection metadata; warm reuse does
not eliminate that cold cost. The fresh-process Ari qualification below includes initial preparation.

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ClrTypeRefMappingBenchmarks*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~DefaultClrTypeRefMapperTests|FullyQualifiedName~ShapeTypeInspectorTests'
```

Tests cover coordinated cold first use, nested generic metadata, required/nullable root occurrences,
recursion from different roots and concurrent explicit mapper isolation. A new warm allocation bound
includes a fresh mapper instance and retained leaf IR, requiring less than 4,096 B; the repeated large
graph bound is tightened to 95,000 B. Neither imposes timing assertions.

Fresh-process Ari source qualification measured separate cumulative allocation boundaries:

| Boundary | Previous | Revised |
| --- | ---: | ---: |
| Catalog authoring | 316,465,856 B | 277,613,632 B |
| First admission | 105,145,344 B | 103,208,840 B |
| Warm admission | 893,512 B | 893,352 B |

These measurements are cumulative allocated bytes, not retained heap or published-package claims.
All 164 canonical catalog bodies remained byte-identical. No Ari package pins or permanent source
bridge changed. Parent type projection, property-name/attribute preparation and exact-number temporary
representations remain separate performance targets.

Qualification passed 4,268 Core tests (33 existing skips), 1,082 Relations tests, and 855 Ari engine
tests (18 existing skips). All 17 focused mapper/inspector tests passed. Both allocation regressions
fail against the preceding core: fresh-leaf mapping allocated 6,400 B against a 4,096 B ceiling and
the large graph allocated 102,272 B against a 95,000 B ceiling. Temporary Ari test dependencies
were restored byte-for-byte. No tests were excluded, timeouts increased or packages published.
