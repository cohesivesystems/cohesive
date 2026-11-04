# Cohesive development loop

Author typed source through the domain's authoring API. Lowering produces canonical execution documents;
these documents are derived artifacts and must not be edited. Inspect source references to locate the owner.

Use `workspace` to discover exported scope and retain its snapshotId. Use `resolve` to find identities and
`inspect` to retrieve document subtrees with their source references. Follow child pointers for large values.
Edit owning source with the ordinary editor, rebuild and re-export, then obtain a new snapshot before calling
`check` and `compare`. Source-only changes can change inputRevision while preserving semanticFingerprint.

Declare all source, dependency, convention and configuration inputs through `SemanticWorkspaceExport`.
`DiscoverInputs` supplies C# source-discovery conventions and `Write` atomically publishes the lowered export.
Input completeness remains a producer responsibility. Filesystem metadata is not proof of unchanged content.

`check` preserves shared integrity findings and reports whether domain validation is available. Missing
validators, external shape context, target capability evidence and runtime guarantees must not be inferred
from successful fingerprint checks. `compare` reports document-level semantic changes, not migration safety.

This guide belongs to the server build, not to an application snapshot. A custom host may supply domain-specific
guidance and validators through DevelopmentMcpServer.RunAsync. Consult the installed domain's authoring API
and executable examples for version-specific syntax; this server does not provide general documentation search.
