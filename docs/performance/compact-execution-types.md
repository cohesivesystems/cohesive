# Compact execution type references

## Authority and encoding

Execution documents own their portable type graph. Execution schema `cohesive-execution/v4`
encodes every `TypeRef` occurrence in the definition as an integer into the definition's
`$types` array. Each distinct structural type is stored once. Child type uses are also indices.
The entries retain the existing `TypeRef` discriminators and strict member contracts; this
is a physical encoding of the existing model, not a second semantic catalog.

For example, a Process returning a 128-field result from 25 branches previously embedded
25 complete type descriptions. Its definition now contains one object entry, its shared
field-type entries, and 25 integer uses. Authors continue supplying the existing immutable
TypeRef objects. They neither assign IDs nor maintain a declaration catalog.

The codec discovers types, deduplicates their canonical wire content, and numbers them
bottom-up by dependency depth and ordinal canonical entry bytes. Numbering therefore does
not depend on object identity or dictionary insertion order. Array/field order remains semantic.
Fingerprints cover the entire table and all uses. Exact canonical re-projection continues to
reject surplus entries, duplicate declarations, and alternate numbering at admission.
Strict projection validates every entry, including unused entries, rejects invalid indices,
unknown members/discriminators and cycles, and resolves uses to shared immutable TypeRef
instances. NamedTypeRef remains a semantic reference resolved through ShapeGraph; it was
evaluated but cannot replace self-contained structural execution contracts without introducing
an external catalog dependency. No old execution wire format is accepted by the current schema.

Frozen serializer metadata is reused through at most eight idle, exclusively leased codecs.
Each operation owns its type pool; codec return clears the pool reference in a finally/Dispose
boundary. Concurrent operations never share input, decoded types, authorization or validation
results. The pool is lazy and retains CLR serializer metadata only. Excess concurrent codecs
are discarded. There is no document cache or tenant-dependent global state.

TypeScript Process presentation retains the compact source evidence and its document-local
`typeDefinitions`; contract evidence can carry an integer type use. It does not expand the table
back into repeated JSON or claim that a local index is a globally meaningful type identity.

## Qualification

Ari's `RepeatedAdmissionSharesPreparationButRetainsFreshDeploymentValidation`, Release,
.NET SDK 10.0.201/runtime 10.0.5 on macOS, isolated first use of a newly authored catalog:

| Boundary | alpha.127 | Compact types prototype |
| --- | ---: | ---: |
| First admission, cumulative allocated bytes | 549,600,016 | 331,131,760 |
| Warm repeated admission, cumulative allocated bytes | 42,938,960 | 12,166,352 |

The prototype measured 648 ms first admission and 22 ms warm admission in one local sample.
These are allocation qualification results, not CI or production latency claims. Catalog authoring
before the admission boundary is excluded, and these numbers are not retained heap size.
The prototype uses local Cohesive assemblies; final package adoption must repeat qualification.
The earlier prepared-projection fix is not included in this prototype's source baseline.

## Direct metadata projection

The compact codec handles `$type` and `$types` through exclusively leased, frozen serializer
metadata. Type entries deserialize from their existing `JsonElement`; the definition body does
likewise. The type table is validated completely before body projection, then its tokens are
consumed without materializing another table. Unknown members remain disallowed. Root metadata
is permitted only on the definition root; ordinary nested objects gain no metadata exceptions.
Polymorphic roots dispatch from the existing declared serializer registry because the built-in
polymorphic reader reserves `$` properties. No parallel discriminator catalog is introduced.

Writing discovers references through a non-retained stream, orders entries as before, and emits
the final body and table directly. Type-entry discriminators are ordinary projected metadata.
The former `WriteObject` path—buffer, parse, clone, then project—is removed. Canonical normalization,
exact re-projection, integrity checks, and immutable document ownership remain unchanged.

Local Release qualification on macOS/.NET SDK 10.0.201 compared published alpha.129 with a temporary
core-assembly overlay of this change, using Ari `f2f2c2b`. No package pins or source bridges changed.
The standalone harness authored the catalog before measurement, constructed the same resolver and
capabilities, and measured first and repeated `TrainingDurableTaskProcessDeploymentCatalog.Create`.
EventPipe allocation ticks corroborated the call-stack attribution; per-thread counters supplied
the totals. Harness setup differs slightly from the original xUnit measurement, so these are paired
harness comparisons, not a comparison of unrelated test runs.

| Boundary | Published codec | Direct metadata codec |
| --- | ---: | ---: |
| Cold admission | 298,636,976 B | 213,848,512 B |
| Warm admission | 11,845,200 B | 7,042,680 B |
| Warm projection: 100,000-character text, shared 128-field object | 541,448 B | 270,032 B |

Allocations are cumulative, not retained heap. The projection regression allows 400,000 B,
including the retained string and typed fields, and rejects the former filtered-tree implementation.
These measurements do not establish CI or production latency. Repeated typed projection,
whole-payload strings from document hashing, and successful diagnostic-path construction remain
separate opportunities; this change does not claim to remove those costs.

The final shared-core suite passed 4,224 tests (33 existing external-backend/scheduler skips).
Ari's full .NET solution passed 1,263 tests (18 existing scheduler skips) with the temporary core
overlay; every output assembly was restored afterward. Independently exporting all 164 Ari catalog
definition bodies through the old and new codecs produced exactly equal canonical JSON bytes.
Focused regressions cover a known scalar wire oracle, shared decoded identity, insertion-order
independence, malformed/unused entries, reserved-property and nested-member rejection, concurrent
codec isolation, compact growth, and bounded successful projection allocation.

The subsequent [immutable execution preparation](immutable-execution-preparation.md) addresses repeated
typed decoding, payload strings during hashing, and eager successful-validation paths before publishing
the combined optimization. The earlier direct-codec-only numbers above remain a separate qualification.
