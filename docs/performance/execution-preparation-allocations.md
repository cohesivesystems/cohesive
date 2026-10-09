# Execution preparation allocation improvements

This is the current overview of the accumulated performance work. The linked stage reports preserve
paired before/after workloads and their qualification boundaries; descriptions of remaining work,
intermediate allocation budgets and ownership behavior in those reports refer to that stage, not the
final implementation. Earlier measurements should not be added together or presented as one paired
end-to-end comparison.

## Final behavior

The existing CLR mapper, TypeRef serializer registry and canonical JSON writer remain the semantic
authorities. No Ari-specific type system or parallel canonical format was introduced.

- CLR reflection, enum wire members and property-declared nullability are prepared once under weak
  metadata ownership. Default root contracts without caller occurrence metadata or explicit overrides
  share immutable type graphs by weak CLR type key. First traversal reuses completed cycle-free children;
  recursive projections retain ancestor-specific diagnostics.
- Compact type tables deduplicate scalar contracts before serialization and other contracts by exact
  canonical UTF-8 equality. Duplicate candidates do not create text keys or owned canonical leaf JSON.
  Parents retain original serializer bytes and converter-recorded reference token locations; final
  numbering replays only those tokens. Final ordering retains ordinal canonical text semantics.
- Execution normalization uses pooled temporary storage and owned returned JSON. Canonical writing
  reuses small-object property names within one write and handles exact integers without number-text
  allocation. Execution/relation fingerprints stream through the same canonical authority, with
  immutable document/definition reuse at the appropriate lifetime.

Strict external decoding, unused-table rejection, cycle checks, semantic fingerprints, nullability,
explicit mappings and invocation isolation remain intact. No test skips or timeout increases were added.
Instance validation plans are a separate next investigation and are not included here.

## Concrete example and current measurements

A 64-parent declaration in which every parent has the same 128-field contract previously allocated
3,931,064 bytes for document creation after serializer warmup. Exact canonical-byte lookup reduces
that fixture to 2,004,024 bytes, retains two table entries and decodes all parent uses to one shared
immutable type. The regression budget is 2,200,000 bytes and fails against the previous assembly.

The most recent paired Ari source-assembly run measures catalog root authoring at 115,032,272 to
111,651,184 bytes, first deployment catalog access at 76,571,112 to 76,032,744 bytes, and warm access
at 701,192 to 685,544 bytes. This compares the final byte-key change with the preceding local revision,
not the entire series against main. All 164 canonical document bodies match byte-for-byte. Root
authoring excludes lazy document projection; first access includes it. These are cumulative allocations,
not retained heap, release-package measurements or end-to-end latency guarantees.

## Review map

1. [Default root reuse](default-type-contract-reuse.md) and [nested reuse](nested-type-contract-reuse.md):
   graph ownership, weak retention, occurrence nullability and recursive boundaries.
2. [Canonical byte keys](canonical-byte-type-keys.md) and [parent reference replay](parent-reference-replay.md):
   compact wire identity, annotation fidelity, child numbering and temporary ownership.
3. [Canonical property names](canonical-property-names.md) and [integer normalization](canonical-integer-normalization.md):
   shared writer semantics, ordinal ordering, duplicate names and numeric equivalence.
4. [Execution fingerprints](canonical-fingerprinting.md), [relation fingerprints](relation-definition-fingerprinting.md)
   and [owned normalization](owned-execution-normalization.md): canonical-byte equivalence and lifetime boundaries.

Each report links representative BenchmarkDotNet evidence. Short jobs support allocation comparisons;
their timings should not be interpreted as application latency gains. The discarded pooled-candidate
serialization experiment is not included: its first-admission allocation increase did not justify adoption.

Packages are not published and Ari pins are not upgraded by this change. Adoption follows package
publication and removal of temporary qualification overlays; overlays are restored after every run.
