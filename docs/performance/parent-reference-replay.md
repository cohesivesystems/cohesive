# Replay parent reference tokens after canonical numbering

## Authority and boundary

The existing `TypeRef` serializer registry remains the authority for execution's compact document-local
type table. Discovery interns child types first using provisional indices. Canonical ordering then
assigns final child indices by depth and canonical entry text, so parents must change their references.
Previously the pool traversed each unique parent CLR graph through the serializer a second time.

The existing converter now records the exact byte position, digit length and provisional index of each
reference token it emits during entry discovery. A unique parent retains its original serialized UTF-8
payload and reference locations until ordering. Replaying that payload changes only recorded tokens
to final child indices. This handles differing digit widths without shifting later source locations:
locations always address the original payload and replacement bytes go to a separate cleared pooled
buffer. Both committed and pending writer bytes count toward positions, including across flushes.

For example, an array initially referring to type `0` can become a reference to type `127` after
sorting. Its sibling reference locations still address the original bytes. An annotation containing
`{"elementType":0,"type":128,"values":[0,9,10,99,100]}` remains ordinary data and is never renumbered.
Tests cover these cases with 12 and 128 distinct nested enum/array pairs, escaped Unicode annotations,
structural deduplication, dictionary insertion order, round-trip bytes and fingerprints. Both semantic
fixtures also pass against the preceding implementation.

The replay is a physical codec optimization, not a second type model or canonical format. It does not
inspect JSON property names to guess which numbers are references. Existing normalization supplies
ordinal property ordering, duplicate rejection, exact numeric normalization and owned final elements.
Structural deduplication still uses canonical provisional content, and each depth is fully prepared
before final indices are used by the next depth. Leaves reuse their final canonical entries.

Initial serialization now produces UTF-8 bytes parsed by a disposable borrowing `JsonDocument`,
avoiding an owned intermediate `JsonElement`. Duplicate parents do not retain payloads. Unique parent
payloads and token lists are released after replay. They are document-invocation scoped and never
retained by idle codec leases. The borrowed pooled memory is parsed and disposed before pool return;
normalization returns an independent owned element. Failures abandon the invocation's pool while codec
disposal clears its pool reference. External strict decoding, cycle/invalid-index checks, unknown-member
rejection, unused-entry validation and independent fingerprint admission remain unchanged.

The tradeoff is temporary storage of each unique parent's serialized bytes and one small record per
reference. This is linear in discovered entry payloads and uses no cross-document cache. Measurements
show a net allocation reduction despite this storage; retained-heap reduction has not been measured.
Earlier dependency-list reuse prototypes did not help; this replay removes the serializer pass itself
and preserves the existing canonical writer rather than reconstructing parent semantics.

## Measurements

`ExecutionTypeInterningBenchmarks` constructs declarations outside measurement and warms serializer
metadata. Each operation includes document-local preparation and retained canonical output. Baseline
is Core `4841ea40`; both runs use the same compiled workload with only the Core assembly changed.

| Fixture | Previous allocation | Revised allocation |
| --- | ---: | ---: |
| 16-field object | 54.83 KB | 46.20 KB |
| Object behind 12 arrays | 84.07 KB | 75.26 KB |
| 128-field object | 376.24 KB | 310.16 KB |
| 512-field object | 1,539.04 KB | 1,257.00 KB |

[Baseline](parent-reference-replay/baseline.md) and [revised](parent-reference-replay/revised.md)
preserve runtime and timing dispersion. Short in-process samples show 10–18% less allocation;
timing is noisy and these results do not establish end-to-end test-suite latency.

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ExecutionTypeInterningBenchmarks*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~ExecutionDefinitionTypeReferenceTests'
```

The 128-field preparation regression now requires at most 340,000 B after warmup, including retained
output. The preceding Core fails at 384,312 B. No timing threshold is used.

A paired fresh-process Ari source harness measured separate cumulative allocation boundaries:

| Boundary | Previous | Revised |
| --- | ---: | ---: |
| Catalog authoring | 198,279,392 B | 183,928,416 B |
| First admission | 92,307,560 B | 84,883,088 B |
| Warm admission | 814,696 B | 763,512 B |

These are allocated bytes, not retained heap or published-package measurements. Authoring includes
cold metadata preparation. All 164 catalog bodies remained byte-identical. No Ari package pins or
permanent source bridges changed; qualification uses a temporary dependency overlay restored afterward.

Qualification passed 4,299 Core tests (33 existing skips), 1,082 Relations tests and 855 Ari engine
tests (18 existing skips). The revised digit-width fixtures and tightened allocation check pass; the
preceding assembly passes the semantic fixtures but fails the allocation ceiling. Both Ari dependency
assemblies were verified restored byte-for-byte. No tests were excluded or timeouts increased.
