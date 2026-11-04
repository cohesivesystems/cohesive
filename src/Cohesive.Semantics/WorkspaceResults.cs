using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Semantics;

/// <summary>Revision-bound result over the declared export scope, never a claim about the entire repository.</summary>
/// <param name="SnapshotId">Digest of the complete exported bundle, including provenance.</param>
/// <param name="InputRevision">Digest of declared input hashes and producer version.</param>
/// <param name="SemanticFingerprint">Aggregate of verified document identities and semantic fingerprints; null on integrity failure.</param>
/// <param name="ChangedInputs">Declared files changed or unavailable since export.</param>
/// <param name="Limitations">Explicit limits on the claims this result supports.</param>
/// <param name="Result">Operation-specific data.</param>
/// <typeparam name="T">Operation result contract.</typeparam>
public sealed record WorkspaceResult<T>(
    string SnapshotId,
    string InputRevision,
    string? SemanticFingerprint,
    ImmutableArray<string> ChangedInputs,
    ImmutableArray<string> Limitations,
    T Result);

/// <summary>Bounded, deterministic result page.</summary>
/// <param name="Items">Items ordered by stable identity or canonical path.</param>
/// <param name="Total">Total matches before pagination.</param>
/// <param name="NextOffset">Offset of the next page, or null when complete.</param>
/// <typeparam name="T">Page item contract.</typeparam>
public sealed record WorkspacePage<T>(ImmutableArray<T> Items, int Total, int? NextOffset);

/// <summary>Identity and attribution projected from an existing execution document.</summary>
/// <param name="Id">Stable definition identity.</param>
/// <param name="Kind">Existing execution kind.</param>
/// <param name="DisplayName">Producer-supplied display name, if any.</param>
/// <param name="Fingerprint">Declared document fingerprint; consult check for validation.</param>
/// <param name="Source">Producer-defined source reference; it is not necessarily a file path.</param>
public sealed record WorkspaceSubject(string Id, string Kind, string? DisplayName, string Fingerprint, string Source);

/// <summary>A bounded view into canonical document JSON, with source mappings retained by the owner.</summary>
/// <param name="Subject">Identity and ownership attribution.</param>
/// <param name="Pointer">RFC 6901 pointer in the complete execution document.</param>
/// <param name="Value">Selected JSON, or null when it exceeds the response bound.</param>
/// <param name="Children">Paged child pointers for progressive retrieval.</param>
/// <param name="SourceReferences">Paged source references mapped to the selected semantic location.</param>
public sealed record WorkspaceInspection(
    WorkspaceSubject Subject, string Pointer, JsonElement? Value,
    WorkspacePage<string> Children, WorkspacePage<string> SourceReferences);

/// <summary>Validation evidence for a single exported definition.</summary>
/// <param name="Id">Stable definition identity.</param>
/// <param name="DomainValidationAvailable">Whether a domain validator was registered for the kind.</param>
/// <param name="IsValid">Whether mandatory shared integrity and available domain validation findings are free of errors.</param>
/// <param name="Diagnostics">Paged existing structured diagnostics; no new diagnostic authority is introduced.</param>
public sealed record WorkspaceCheck(string Id, bool DomainValidationAvailable, bool IsValid, WorkspacePage<DocumentValidationDiagnostic> Diagnostics);

/// <summary>Document-level semantic comparison; does not infer compatibility or identity continuity.</summary>
/// <param name="Id">Stable definition identity.</param>
/// <param name="Before">Verified prior fingerprint, or null for an addition.</param>
/// <param name="After">Verified current fingerprint, or null for a removal.</param>
public sealed record WorkspaceDifference(string Id, string? Before, string? After);

/// <summary>Comparison evidence tied to both exact snapshots.</summary>
/// <param name="BaselineSnapshotId">Exact retained baseline snapshot.</param>
/// <param name="Changes">Paged document-level changes.</param>
public sealed record WorkspaceComparison(string BaselineSnapshotId, WorkspacePage<WorkspaceDifference> Changes);
