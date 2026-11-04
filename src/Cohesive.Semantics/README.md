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
cross-document type validation, and runtime evidence are not yet indexed. Unknown execution kinds remain inspectable,
but `domainValidationAvailable` is false. A successful integrity check does not establish domain validity.
Registered validators supplement mandatory shared integrity findings; they cannot suppress them. Identical findings
are deduplicated and ordered through the existing diagnostic authority; separately attributed evidence is retained.
Registered validators may themselves require
external shape or contract context; missing context remains diagnostic.

## Export in the authoring process

```csharp
var inputs = SemanticWorkspaceExport.DiscoverInputs(repositoryRoot,
    sourceDirectories: ["src/MyApplication"], additionalFiles: ["Directory.Build.props"]);
var path = SemanticWorkspaceExport.Write(
    root: repositoryRoot,
    outputPath: "tmp/workspace.json",
    inputPaths: inputs,
    producerVersion: exactCompilerVersion,
    lower: inputRevision => [BuildApplicationDefinition(inputRevision)]);
```

`BuildApplicationDefinition` represents the application's existing authoring/lowering entry point, returning
an `ExecutionDefinitionDocument`; it is not a framework method. Use the supplied input revision when the
application needs a deterministic execution revision identity. Declare all source, contract, configuration,
convention, dependency lock, and build inputs that affect lowering. The exporter hashes declared files before
and after invoking lowering and rejects observable input changes. `Write` publishes through an adjacent temporary file and atomic replacement. `Create` remains available
for callers that own publication. `DiscoverInputs` scans explicitly selected directories for C#, project, props,
targets and JSON files, skipping `bin`, `obj` and `.git`; add other inputs explicitly. This is a convention,
not dependency-graph inference.

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
and source references, independently paged with `offset`/`limit` and `sourceOffset`/`sourceLimit`. A larger object/array must be read through its children; an oversized scalar requires
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
knowledge retrieval, and then narrowly scoped source-edit recipes. The current `learn` guide is host-owned, versioned static guidance, not a general knowledge graph or a
compatibility guarantee for any project.

See [request cost measurements](../../docs/performance/development-mcp.md) for the warm-call probe,
allocation evidence, freshness tradeoff and remaining scaling limits.

## Direct consumers and portable source locations

`References(id, expectedSnapshot)` returns direct consumers projected by registered domain readers. Each row
retains `Source` and `Expected` exact references plus the exported `Available` candidate: null means missing,
an unequal candidate means stale revision/fingerprint, and `IsExactMatch` means both match. Looking up an absent
target is supported. Invalid document integrity prevents graph claims. `IncompleteDefinitions` lists unregistered
kinds and extension-bearing documents whose references have not been fully indexed. This is neither transitive
impact discovery nor type-compatibility validation. Custom hosts supply reference readers beside validators;
the protocol-independent workspace does not scan arbitrary JSON for objects resembling references.

`Inspect` also returns `SourceMappings`, independently paged with `sourceOffset`/`sourceLimit`. Root inspection
lists all producer mappings; narrower pointers select mapped references. An optional `Location` holds a normalized
repository-relative path and an inclusive one-based line span. The compiler virtual root `/_/` is explicitly
mapped by producer projects, never inferred by the workspace. Missing locations remain absent. Current C# builders
capture declaration lines, not complete expression ranges. Provenance, including locations, is outside semantic
fingerprints; strict readers predating the optional location field must be upgraded for exports containing it.

[Source-map cost measurements](../../docs/performance/source-locations.md) quantify the optional metadata's
construction and serialization overhead; the existing full-content freshness policy is unchanged.
