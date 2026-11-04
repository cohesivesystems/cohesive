# Cohesive.Semantics

An initial, protocol-independent development workspace over derived `ExecutionDefinitionDocument` exports.
It supports discovery, identity resolution, source attribution, bounded JSON inspection, domain validation,
and comparison of semantic document fingerprints. The [MCP adapter](../adapters/Cohesive.Adapters.Mcp.Development/README.md)
projects these operations without reinterpreting their semantics.

## Authority and scope

Authoring source remains the editable authority. Existing lowering pipelines produce the canonical documents;
the workspace reads their existing identity, provenance, source maps, serialization, fingerprints and validators.
It does not introduce another entity, transition, process, relation, or diagnostic model.

The first export boundary covers execution documents. It does **not** claim to discover every construct in a
repository. Shape graphs, standalone relations, APIs, generated clients, storage mappings, capability matching,
cross-document linking, and runtime evidence are not yet indexed. Unknown execution kinds remain inspectable,
but `domainValidationAvailable` is false. A successful integrity check does not establish domain validity.
Registered validators supplement mandatory shared integrity findings; they cannot suppress them. Identical findings
are deduplicated and ordered through the existing diagnostic authority; separately attributed evidence is retained.
Registered validators may themselves require
external shape or contract context; missing context remains diagnostic.

## Export in the authoring process

```csharp
var json = SemanticWorkspaceExport.Create(
    root: repositoryRoot,
    inputPaths: declaredInputs,
    producerVersion: exactCompilerVersion,
    lower: inputRevision => [BuildApplicationDefinition(inputRevision)]);
```

`BuildApplicationDefinition` represents the application's existing authoring/lowering entry point, returning
an `ExecutionDefinitionDocument`; it is not a framework method. Use the supplied input revision when the
application needs a deterministic execution revision identity. Declare all source, contract, configuration,
convention, dependency lock, and build inputs that affect lowering. The exporter hashes declared files before
and after invoking lowering and rejects observable input changes. The caller writes the returned JSON atomically.

The exporter cannot establish that the declaration includes every dependency, detect a newly added undeclared
file, or prove that an already-loaded application binary matches the working tree. Build and export from an
isolated, fixed source revision for reproducibility; the local example is a convenience workflow, not build
attestation. A change-and-revert during lowering is likewise outside the input check's guarantee.

The inspection service never runs this callback. Export runs separately with the author's ordinary build
permissions. No callback survives in the exported documents. Bundle files are derived artifacts, not hand-edited source.

## Revisions and freshness

- `snapshotId`: SHA-256 of the exact bundle bytes. Metadata or formatting changes can change this identity.
- `inputRevision`: SHA-256 over the canonical bundle-schema version, producer version and declared file hashes.
- `semanticFingerprint`: SHA-256 over the canonical bundle-schema version and ordinally ordered definition
  identities and existing versioned fingerprints. It excludes provenance and revision labels. It is null if a
  document fails integrity validation.
- `changedInputs`: declared files currently changed, missing, unreadable, or replaced with symbolic links.
  An empty array means only that the declared inputs match the export, not that the whole repository is current.

Every operation rereads the bundle and checks declared inputs before and after inspection. Changes observed on
either side are reported; a bundle replaced during inspection rejects the result. These checks do not lock the
working tree or detect change-and-revert races. Subsequent calls supply the expected snapshot.
A replacement bundle rejects stale requests. A malformed replacement fails explicitly rather than serving an
older graph as current. Source edits without an export leave the snapshot unchanged and populate `changedInputs`.
Re-export after edits before treating validation as evidence about the current source.

The session retains four snapshots for comparison. Evicted baselines fail explicitly. Comparison reports added,
removed and content-changed definitions by stable identity; it does not infer renames, migration safety, semantic
equivalence of different implementations, or compatibility. Read the before/after definitions to review content.

## Bounds and failure behavior

Bundles and individual inputs are limited to 16 MiB, bundles to 256 definitions and 4096 declared inputs.
Relative file paths cannot escape the selected root or traverse symbolic links. Duplicate identities, duplicate
JSON properties, unsupported bundle versions and malformed input fingerprints are rejected.

List operations page 1–100 entries. `inspect` returns at most 16 KiB of selected JSON plus paged child pointers
and source references. A larger object/array must be read through its children; an oversized scalar requires
local artifact inspection and returns an actionable error. Source references are producer-defined attribution, not automatically trusted file
paths. `check` pages definitions and supplies at most 20 diagnostics per definition; use `diagnosticOffset` to
retrieve later diagnostics. Only the selected page invokes domain validators; shared document integrity is checked
when loading a snapshot and retained with that immutable snapshot. Counts describe the complete matching scope,
not just the current page. Individual
diagnostic strings retain their original content, so response byte size is not globally capped.

Public methods propagate file/permission failures, reject invalid pagination, and report unknown identities and
pointers explicitly. Validation findings retain the existing `DocumentValidationDiagnostic` contract. The session
serializes concurrent operations under a lock and rereads bounded files synchronously; incremental indexing and
asynchronous cancellation of in-progress inspection are intentionally deferred. No hot-path performance claim is made.

## Next extensions

Add domain-specific indexing and authored source locations only where the owning domain can provide reliable
evidence. Priorities are relation/shape export, cross-layer reference edges, a CLI over the same API, version-matched
knowledge retrieval, and then narrowly scoped source-edit recipes. The current `learn` example is a compiled
fixture for the adapter's framework build, not a general knowledge graph or a compatibility guarantee for any project.
