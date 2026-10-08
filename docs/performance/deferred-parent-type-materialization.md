# Materialize parent type entries after canonical numbering

## Authority and change

Execution's document-local type pool uses the existing `TypeRef` serializer registry as wire authority.
Child-first discovery prepares canonical content keys for structural deduplication, then depth-by-depth
ordering assigns final indices. Parent entries must be projected again with those final child indices.
Previously, parent discovery parsed an owned canonical `JsonElement` solely to obtain its text key;
that provisional document was replaced during final numbering.

The provisional parent now produces its canonical text key directly through the same validation,
canonical sequence writer, exact decimal-rational rules and cleared pooled buffer as owned document
normalization. It does not parse another owned JSON document. Leaf entries retain their prepared
canonical element and text because they have no child references to renumber. Parent slots remain
unmaterialized inside the private pool until `Order` fills every final entry before table emission.
No new type model, reference writer, cache or caller-data retention is introduced.

For example, two parent fields whose annotations contain `1.0000` and `1e0` still deduplicate when
otherwise equivalent, including differently ordered object keys. Introducing a Bool leaf ahead of
String forces parent references to be renumbered; numeric annotation values remain data and are not
rewritten as references. Regression coverage verifies this exact scenario, final round-trip bytes and
shared decoded instances. External strict decoding, cycle detection, unknown-member rejection,
unused-entry admission and independent fingerprint validation remain intact.

Two broader reuse prototypes were measured and discarded: storing per-parent dependency lists
increased Ari allocations, while recording a single shared child produced no meaningful application
reduction. The retained change removes unnecessary representation ownership without new traversal
or dependency metadata. Final parent serialization remained necessary at this stage. Subsequent
[parent reference replay](parent-reference-replay.md) replaces that serializer pass with precise
converter-recorded number token replacement while retaining the canonical writer.

## Representative allocation evidence

The existing `ExecutionTypeInterningBenchmarks` fixture was run against the core at `5924d76` and
this change. Declaration construction is outside measurement; serializer metadata is warmed.
Each call creates a new document, including retained output and document-local preparation.

| Fixture | Previous | Revised |
| --- | ---: | ---: |
| 16-field object | 62.22 KB | 57.33 KB |
| Object behind 12 arrays | 100.89 KB | 93.28 KB |
| 128-field object | 434.19 KB | 396.69 KB |
| 512-field object | 1,720.26 KB | 1,570.39 KB |

[Baseline](deferred-parent-type-materialization/baseline.md) and
[revised](deferred-parent-type-materialization/revised.md) record environment and dispersion. Recorded
settings were `--inProcess --job Short --warmupCount 3 --iterationCount 8 --invocationCount 64
--unrollFactor 1`. Timing varied by fixture: the large fixture improved in these samples, while the
flat fixture was slower. This is an allocation improvement, not a general latency claim.

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ExecutionTypeInterningBenchmarks*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~ExecutionDefinitionNormalizationTests|FullyQualifiedName~ExecutionDefinitionTypeReferenceTests'
```

The 128-field regression includes new document preparation and retained output after warmup, with
input construction outside the boundary. Its 425,000 B ceiling fails against the preceding core at
444,584 B. Differential key tests compare owned normalization with direct canonical text across
Unicode, escaping, nested collections, exact extreme numbers and identical invalid-input diagnostics.

## Ari qualification

A paired fresh-process source harness measured authoring separately from cold and warm admission:

| Boundary | Previous | Revised |
| --- | ---: | ---: |
| Catalog authoring | 277,638,848 B | 258,635,096 B |
| First admission | 103,213,824 B | 98,432,520 B |
| Warm admission | 893,352 B | 862,464 B |

These are cumulative local allocations, not retained heap or published-package measurements.
All 164 canonical catalog bodies remained byte-identical. No Ari package pin or permanent source
bridge changed. Temporary test-output dependency overlays are restored after qualification.
Common integer normalization is addressed in [canonical integer normalization](canonical-integer-normalization.md).
Further candidates include repeated enum/property attribute inspection, remaining exact-number intermediate
representations, and the final serializer projection that still renumbers parents.

Qualification passed 4,276 Core tests (33 existing skips), 1,082 Relations tests and 855 Ari engine
tests (18 existing skips). All 35 focused normalization/type-table tests passed. The new allocation
regression fails against the preceding implementation. Ari dependency overlays were restored
byte-for-byte. No tests were excluded, timeouts increased or packages published.
