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
