# Cohesive development loop

Author typed source through the domain's authoring API. Lowering produces canonical domain documents;
these documents are derived artifacts and must not be edited. Inspect source references to locate the owner.

Use `workspace` to discover exported scope and retain its snapshotId. Use `resolve` to find identities and
`inspect` to retrieve document subtrees and sourceMappings with optional portable line locations. Missing
locations must not be guessed from descriptions. Use `references` to discover direct consumers before editing;
review incompleteDefinitions, missing targets (available absent/null) and reference evidence: isContentMatch verifies content, while isExactMatch also requires revision equality.
A null revision is unknown; identity-only edges never prove content equality.
Follow child pointers for large values. Use `impact` for reverse reachability (maxDepth 1–16, limit 1–100).
Results contain one deterministic shortest evidence path per consumer; cycles are suppressed. Review
depthLimitReached, resultLimitReached and incompleteDefinitions. This is potential dependency impact, not compatibility proof.
Edit owning source with the ordinary editor, rebuild and re-export, then obtain a new snapshot before calling
`check` and `compare`. Source-only changes can change inputRevision while preserving semanticFingerprint.

Declare all source, dependency, convention and configuration inputs through `SemanticWorkspaceExport`.
`CreateDocuments`/`WriteDocuments` support original domain JSON through schema-keyed readers.
`DiscoverInputs` supplies C# source-discovery conventions and `Write` atomically publishes the lowered export.
Input completeness remains a producer responsibility. Filesystem metadata is not proof of unchanged content.

`check` preserves shared integrity findings and reports whether domain validation is available. Missing
validators, external shape context, target capability evidence and runtime guarantees must not be inferred
from successful fingerprint checks. `compare` reports document-level semantic changes, not migration safety.

This guide belongs to the server build, not to an application snapshot. A custom host may supply domain-specific
guidance, validators and domain-owned reference readers through DevelopmentMcpServer.RunAsync. Consult the installed domain's authoring API
and executable examples for version-specific syntax; this server does not provide general documentation search.

The default host reads entity state, qualified shape/field/type members, relationship catalogs, native queries, execution documents and portable API contracts. Legacy endpoint-inspection documents retain their narrower fingerprint scope. Resolve a field, then use impact to find potential consumers; expression-level coverage is partial.

The portable API document retains HTTP bindings, result variants, serializer profile, qualified core types and execution/service references. Use `compatibility` with an endpoint ID, retained baseline snapshot and fresh expected snapshot. The default direction is existing client to proposed server; the reverse direction tests the proposed client against the existing server. Under `http-json-tolerant-readers/v2`, requests must remain acceptable to the server and responses readable by the client. Findings identify wire paths and return Compatible, Breaking or Unknown. Custom serializer behavior, authorization changes and unsupported type semantics can prevent a proof. This policy does not qualify arbitrary clients, deployed routes, or runtime behavior.

Policy v2 separates reader-required fields from writer presence. Optional constructor defaults can accept omitted fields; required-constructor handling and JsonRequired follow the exported serializer evidence. Older exports keep their original profile and coverage gaps. Re-export both source revisions with a qualified producer when needed; do not turn Unknown into a passing result.
