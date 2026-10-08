# Prepare serialized enum catalogs once per live CLR type

## Authority and reuse

`SerializedEnumMemberCatalog` owns the existing mapping between CLR enum names and declared JSON
wire names, consumed by CLR type inference and strict document serialization. Repeated inference
previously inspected attributes and rebuilt two dictionaries, then copied member names twice for every
occurrence. A profile after canonical integer normalization attributed roughly 13 MB across catalog
authoring and first admission to this discovery path.

The catalog now uses weak CLR type keys and lazily coordinated discovery. Completed catalogs retain
private write-once lookup dictionaries and one immutable member array. Each inferred `EnumTypeRef`
still belongs to its mapping invocation, but safely shares that immutable array. No inferred object
graph, mapper configuration, tenant context, serializer instance or converter instance is cached.
Discovery is deferred until needed; first use still pays reflection and metadata storage costs. Cache
retention follows the live CLR type identity rather than an additional permanent type root.

Strict wire inference and explicit CLR-name fallback remain separate policies. Ordinary and standard
string enums share the same completed catalog across both. For custom converters, strict discovery
retains its unsupported-converter diagnostic; fallback lazily prepares CLR member names without
re-inspecting the converter attribute. Ambiguous standard wire names fail under both policies.
Stable metadata failure results are shared, preserving their reason and converter identity. Existing
flags translation, numeric alias handling and serialized-member order remain unchanged.

For example, discovering a custom-converted enum with fallback enabled and then mapping that enum
strictly still produces `UnsupportedEnumConverter`, regardless of call order. Two standard members
both annotated `same` remain ambiguous under both modes. Tests exercise both scenarios, generic
string converters, cold concurrent publication and shared immutable member-array identity.

## Measurements

The checked-in `EnumCatalogMappingBenchmarks` uses a three-member string enum, a repeated nested
shape, collections and eight branches. Inputs and mapper construction precede measurement; metadata
is warmed, but each call constructs and retains a new portable type graph. Baseline core is `a4e5507`.

| Fixture | Previous | Revised |
| --- | ---: | ---: |
| Enum root | 2.88 KB | 192 B |
| Nested graph | 52.94 KB | 10,929 B |
| Collection graph | 107.79 KB | 23,838 B |
| Eight branches | 424.14 KB | 88,043 B |

[Baseline](shared-enum-catalog/baseline.md) and [revised](shared-enum-catalog/revised.md) retain
hardware/runtime and timing dispersion. Recorded settings were `--inProcess --job Short
--warmupCount 3 --iterationCount 8 --invocationCount 64 --unrollFactor 1` on Arm64 macOS/.NET 10.0.5.
Warm allocation falls approximately 78–94%; these microbenchmarks are not a production latency claim.

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*EnumCatalogMappingBenchmarks*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~DefaultClrTypeRefMapperTests|FullyQualifiedName~StrictDocumentJsonTests'
```

A fresh-mapper regression includes retained enum IR after warmup and requires no more than 256 B,
plus shared immutable backing-array identity. It protects both metadata discovery and member-copy
removal without a timing threshold. It fails against the preceding core at 2,640 B against
the 256 B ceiling, and all 45 focused mapper/strict-serialization tests pass with the fix.

A paired fresh-process Ari source harness measured separate cumulative allocation boundaries:

| Boundary | Previous | Revised |
| --- | ---: | ---: |
| Catalog authoring | 245,689,256 B | 231,051,536 B |
| First admission | 93,595,056 B | 93,070,448 B |
| Warm admission | 814,696 B | 814,696 B |

These are allocated bytes, not retained heap or published-package measurements. The authoring sample
includes cold metadata preparation. All 164 canonical catalog bodies remained byte-identical. No Ari
package pin or permanent source bridge changed; temporary test dependencies are restored afterward.

Qualification passed 4,296 Core tests (33 existing skips), 1,082 Relations tests and 855 Ari engine
tests (18 existing skips). No tests were excluded, timeouts increased or packages published.
Subsequent [shared CLR inspection](shared-clr-inspection.md) removes repeated property/name ordering,
polymorphism and quantity metadata discovery. Final parent serializer projection remains a target.
