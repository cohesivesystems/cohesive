# Default CLR type contract reuse

Catalog authoring repeatedly requested the same CLR contracts through fresh default mappers.
Reflection metadata was already shared, but each request rebuilt the immutable type graph.
For example, Ari's training catalog contains many occurrences of the same structural contracts;
those separate instances also reached compact type encoding before content deduplication.

## Ownership and semantics

Reuse belongs in Cohesive's `DefaultClrTypeRefMapper`, the existing authority for inferred CLR
contracts. No Ari-specific cache or second structural schema/comparer is introduced.
Default roots with no occurrence nullability metadata share one immutable projection per CLR
Type through a weak-key table. Lazy initialization coordinates concurrent first reads. Failed
preparation removes the entry so later calls can retry. Retention follows CLR type lifetime;
this is a process-wide cache of inferred contracts, not invocation results or consumer definitions.

Nested mapping never consults the root cache: recursive diagnostics depend on the current
ancestor path. Each root is inferred with a fresh path. Explicit mappings and caller-supplied
nullability metadata bypass shared root preparation. All returned framework TypeRef variants
are audited for deep immutable public graphs. Record copies cannot alter the shared source.

This removes repeated root inference; it does not intern equivalent nested nodes inside the
first projection, eliminate arbitrary caller-created duplicate graphs, or change JSON authority.

## Qualification

Local macOS Arm64, .NET SDK 10.0.201/runtime 10.0.5. Paired source-assembly profiler runs use
identical Ari inputs, replacing only Cohesive Core. Numbers are cumulative allocated bytes,
not retained heap or elapsed-time claims.

| Boundary | Before bytes | After bytes |
|---|---:|---:|
| CreateDefault root authoring | 167,767,568 | 122,158,056 |
| First deployment catalog access, including deferred document projection | 77,770,864 | 77,826,320 |
| Second deployment catalog access | 695,896 | 701,192 |

Authoring falls by 45,609,512 bytes (27.2%); admission allocations are essentially unchanged.
All 164 canonical document bodies are byte-for-byte identical. The authoring counter excludes
lazy document projection; the first deployment access includes it. These nested/lifetime scopes
must not be summed into an exclusive authoring figure.

Representative BenchmarkDotNet warm Map allocations fall from 1.05 KB flat, 3.89 KB nested,
8.37 KB collection and 29.96 KB large to zero measured bytes. These are warmed cache reads,
not first-use costs. Short iteration timings are too small for reliable latency conclusions.
Before/after reports are adjacent to this note. A deterministic 128-byte warm allocation budget
fails against the previous assembly (30,584 bytes on the large fixture). Tests cover concurrent
first use, explicit overrides, occurrence nullability, recursion paths and immutable type graphs.

Validation: 4,301 Core tests pass with 33 existing skips; the final focused mapper and
immutability suite passes all 22 tests, including the newly added graph audit. Relations passes
1,082 tests; Ari engine passes 855 with 18 existing skips. Ari package assemblies were restored
byte-for-byte after qualification. This change is local and unpublished.
