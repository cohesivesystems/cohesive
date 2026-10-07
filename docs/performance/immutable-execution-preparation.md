# Immutable execution preparation

## Authority and lifetime

`ExecutionDefinitionDocument.Definition` remains the canonical persisted authority. Process, Transition,
Relation/Query, and interaction-contract IRs declare `IImmutableExecutionDefinition`: their entire
reachable graph is immutable and safe for concurrent reads. Both `GetDefinition<T>()` and
`ExecutionDefinitionJsonSerializer.DeserializeDefinition<T>()` use the same document-owned preparation.
One successful strict projection is retained per requested CLR type, coordinated under a document-local
lock. Failed decoding is never published. There is no global document cache, fingerprint-keyed cache,
tenant state, runtime result, or service in the retained projection. Arbitrary caller-defined types
that do not declare this contract keep independent deserialization, including mutable arrays.

For example, obtaining a public Process's input and result contracts previously decoded its full
payload twice. Those reads now return the same immutable canonical projection. Repeated closure
and interaction linking can likewise reuse decoding from the exact same document object.
The original authored object is never substituted for decoding the persisted authority.

Reuse conveys no integrity, canonical-wire, semantic-validation, or deployment-admission evidence.
External/persisted documents still undergo their existing checks. Shape resolution, interaction
requirements, deployed publishers, operation capabilities, and invocation state remain fresh.
The explicit immutable contract is preferable to a manually maintained block-type catalog in core,
a global cache keyed by a caller-declared fingerprint, or a cache on each individual authoring handle.
The earlier prepared-closure projection proposal addressed call-scoped duplicate decoding; this
mechanism covers every document reader without introducing a second preparation authority.

## Payload-free hashing and lazy diagnostics

`GetHashCode()` hashes kind, metadata (which includes the declared fingerprint), and extensions.
It does not materialize definition JSON as a string. Distinct payloads with identical metadata may
collide: exact persisted-content equality and actual fingerprint validation remain authoritative.
The hash is an in-memory collection operation, not an integrity decision or durable fingerprint.

Portable execution validation keeps name/index segments in one traversal-scoped depth buffer.
A child overwrites only its own depth; ancestor segments remain intact. Locations never escape
validation. Emitting a diagnostic immediately snapshots an escaped JSON Pointer string before
traversal continues. Successful traversal does not concatenate paths or format array indices.
Diagnostic order, escaping, root location, and individual sibling locations are unchanged.

## Qualification

Release/.NET SDK 10.0.201 on macOS. A standalone harness authored an Ari catalog before measuring
first and repeated `TrainingDurableTaskProcessDeploymentCatalog.Create`; it constructed equivalent
publisher and capability resolvers before the allocation boundary. Ari source was `f2f2c2b` (the tree
merged in PR 207). The source overlay replaced four Cohesive assemblies; qualification restores every
published build output afterward. These are cumulative per-thread allocated bytes, not retained heap
or a CI/production latency claim. The prior separate prepared-projection PR was not applied.

| Admission | Published alpha.129 | Direct metadata codec only | Complete preparation changes |
| --- | ---: | ---: | ---: |
| Cold | 298,636,976 B | 213,848,512 B | 153,315,824 B |
| Warm | 11,845,200 B | 7,042,680 B | 1,097,592 B |

A second fresh process measured 153,302,920 B cold and 1,096,848 B warm. All 164 catalog definition
bodies matched published canonical bytes exactly. No wire schema or fingerprint oracle changed.

Regression checks cover concurrent one-time projection, zero-allocation warm immutable reads,
mutable caller isolation, retryable failures, zero-allocation warm hashing with a 100,000-character
payload, forged metadata that still fails integrity validation, bounded successful 128-field type
validation allocations, and escaped sibling diagnostics. Existing suites cover exact external
validation, source-map diagnostics, context-dependent semantics, fresh deployment capabilities,
cancellation, and weak catalog retention.

Final qualification passed 4,230 shared-core tests (33 existing skips), all 1,263 Ari .NET tests
(18 existing skips), and full-solution NuGet API/package validation. No tests were excluded or
timeouts raised. All 45 overlaid Ari output assemblies were restored to published dependencies.
