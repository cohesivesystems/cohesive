# Reuse decoded property names within a canonical write

## Authority and ownership

`CanonicalJsonWriter` remains the authority for ordinal object ordering, duplicate rejection, array
policy, structural paths and numeric profiles. Its immutable-element path previously decoded every
property name to a new string while collecting each object for sorting. Repeated rows and compact
type fields therefore repeatedly decoded the same names.

For example, 4,096 rows containing `alpha`, `beta` and `gamma` previously allocated three name strings
per row. A write now borrows 32 string slots and uses object property count and source position to
select one candidate. `JsonProperty.NameEquals` must match that candidate exactly before reuse,
including escaped names and Unicode. A collision decodes and replaces the candidate. The slot
calculation is advisory, never an identity, semantic schema or ordering authority. Wide objects with
more than 32 properties bypass the cache so unique names do not churn it or require multiple probes.

The operation-local cache flows through recursive writes and is returned with every slot cleared on
success or failure. Idle pooled storage retains no names or documents. There is no shared symbol
catalog, consumer metadata cache or global string interning. Sorting and diagnostics use the same
decoded strings as before; duplicate names such as `x` and `\u0078` still fail with the same message.
The mutable-node writer, array-set keys, string values, numeric profiles and validation boundaries
are unchanged. Broader duplicate-validation traversals still decode their own names.

A prototype with a linear search through 16 names reduced allocations but slowed representative
type preparation. It was discarded. The retained implementation performs at most one candidate
comparison per property and uses existing array-pool ownership patterns rather than adding a
cross-document cache. Pool cold population still allocates storage; tiny objects without repeating
names need not improve. Cache collisions may reduce reuse but cannot change canonical output.

## Qualification and measurements

`CanonicalPropertyNameBenchmarks` covers a tiny flat object, 4,096 repeated three-field objects,
4,096 escaped-name objects and one 4,096-property object with unique names. Input preparation and
metadata warmup precede measurement; output buffer growth and owned output are included. Baseline
is Core `094f0c58`. Both runs use identical compiled workloads with only the Core assembly changed.

| Canonical fixture | Previous allocated bytes | Revised allocated bytes |
| --- | ---: | ---: |
| Tiny flat object | 542 | 642 |
| Repeated rows | 726,018 | 398,350 |
| Escaped-name rows | 787,222 | 394,124 |
| Wide unique-name object | 369,481 | 369,497 |

The tiny fixture shows about 100 B of overhead in these samples; wide unique-name allocation is
effectively unchanged. Repeated layouts reduce total allocated bytes by 45–50%. Short timing samples
are dispersed and do not support a general latency claim. Full reports retain those limitations:
[baseline](canonical-property-names/baseline-canonical.md) and
[revised](canonical-property-names/revised-canonical.md).

Existing `ExecutionTypeInterningBenchmarks` includes retained document output and document-local
preparation after warming serializer metadata:

| Type fixture | Previous allocation | Revised allocation |
| --- | ---: | ---: |
| 16-field object | 46.20 KB | 35.58 KB |
| Object behind 12 arrays | 75.27 KB | 63.49 KB |
| 128-field object | 310.24 KB | 217.94 KB |
| 512-field object | 1,257.05 KB | 886.06 KB |

[Baseline](canonical-property-names/baseline-types.md) and
[revised](canonical-property-names/revised-types.md) record 16–30% lower allocation. These are
microbenchmarks, not an end-to-end test-suite timing claim.

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*CanonicalPropertyNameBenchmarks*' '*ExecutionTypeInterningBenchmarks*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~CanonicalJsonElementWriterTests'
```

A regression isolates writing from output growth by reusing a preallocated byte buffer. After warmup,
4,096 rows must allocate at most 16,384 B for the writer and temporary work. The preceding Core fails
at 393,384 B. Escaped duplicate-name failure, collision-heavy Unicode layouts, concurrent independent
writes and recovery after failure are tested. Existing differential tests preserve exact bytes and
array callback paths against the node writer for both numeric profiles and all JSON value kinds.

A paired fresh-process Ari source harness measured separate cumulative boundaries:

| Boundary | Previous | Revised |
| --- | ---: | ---: |
| Catalog authoring | 183,896,288 B | 167,736,368 B |
| First admission | 84,868,800 B | 77,676,720 B |
| Warm admission | 763,512 B | 695,896 B |

Authoring includes cold cache/storage preparation. These are allocated bytes, not retained heap or
published-package measurements. All 164 canonical catalog bodies remained byte-identical. Ari package
pins remain unchanged; test dependency overlays are temporary and restored after qualification.

Qualification passed 4,301 Core tests (33 existing skips), 1,082 Relations tests and 855 Ari engine
tests (18 existing skips). All 15 focused immutable-writer tests pass. Both Ari dependency assemblies
were verified restored byte-for-byte. No tests were excluded, timeouts increased or packages published.
