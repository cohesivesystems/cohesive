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
/// <param name="SourceMappings">Producer mappings with optional portable locations; root inspection lists all mappings.</param>
/// <param name="ValueKind">JSON kind of the selected value, including when its content is truncated.</param>
public sealed record WorkspaceInspection(
    WorkspaceSubject Subject, string Pointer, JsonElement? Value,
    WorkspacePage<string> Children, WorkspacePage<string> SourceReferences, WorkspacePage<ExecutionSourceProvenance> SourceMappings,
    JsonValueKind ValueKind)
{
    /// <summary>True only when content exceeds the inspection bound; a selected JSON null is complete.</summary>
    public bool IsTruncated => !Value.HasValue && ValueKind != JsonValueKind.Null;
}

/// <summary>Validation evidence for a single exported definition.</summary>
/// <param name="Id">Stable definition identity.</param>
/// <param name="DomainValidationAvailable">Whether a domain validator was registered for the kind.</param>
/// <param name="IsValid">Whether mandatory shared integrity and available domain validation findings are free of errors.</param>
/// <param name="Diagnostics">Paged existing structured diagnostics; no new diagnostic authority is introduced.</param>
public sealed record WorkspaceCheck(string Id, bool DomainValidationAvailable, bool IsValid, WorkspacePage<DocumentValidationDiagnostic> Diagnostics);

/// <summary>Document-level semantic comparison; does not infer compatibility or identity continuity.</summary>
/// <param name="Id">Stable definition identity.</param>
/// <param name="Before">Verified prior fingerprint descriptor, or null for an addition.</param>
/// <param name="After">Verified current fingerprint descriptor, or null for a removal.</param>
public sealed record WorkspaceDifference(string Id, ExecutionDefinitionFingerprint? Before, ExecutionDefinitionFingerprint? After);

/// <summary>Comparison evidence tied to both exact snapshots.</summary>
/// <param name="BaselineSnapshotId">Exact retained baseline snapshot.</param>
/// <param name="Changes">Paged document-level changes.</param>
public sealed record WorkspaceComparison(string BaselineSnapshotId, WorkspacePage<WorkspaceDifference> Changes);

/// <summary>An attributed dependency and the currently exported candidate with that identity.</summary>
/// <param name="Source">Consumer identity and available revision/content evidence.</param>
/// <param name="Expected">Reference evidence retained by that consumer.</param>
/// <param name="Available">Exported target reference, or null if the target is missing.</param>
public sealed record WorkspaceReference(WorkspaceDocumentReference Source, WorkspaceDocumentReference Expected,
    WorkspaceDocumentReference? Available)
{
    /// <summary>True only when the full fingerprint descriptor matches; absent revision evidence remains unknown.</summary>
    public bool IsContentMatch => Expected.Fingerprint is not null && Expected.Fingerprint == Available?.Fingerprint;

    /// <summary>True only when all supplied revision evidence and content evidence agree.</summary>
    public bool IsExactMatch => Expected.Fingerprint is not null && Expected == Available;
}

/// <summary>Direct reference evidence and explicit bounds on indexing coverage.</summary>
/// <param name="References">Paged consumers of the requested identity, including missing or stale targets.</param>
/// <param name="IncompleteDefinitions">Identities with no registered reader, partial domain coverage or uninspected extensions.</param>
public sealed record WorkspaceReferences(WorkspacePage<WorkspaceReference> References, ImmutableArray<string> IncompleteDefinitions);

/// <summary>A potential affected consumer with one deterministic shortest dependency path from the requested target.</summary>
/// <param name="Consumer">Reached consumer.</param><param name="Path">Edges ordered from the requested target outward.</param>
public sealed record WorkspaceImpactItem(WorkspaceDocumentReference Consumer, ImmutableArray<WorkspaceReference> Path);

/// <summary>Bounded reverse reachability, not behavioral change or compatibility proof.</summary>
/// <param name="Items">Consumers in deterministic breadth-first discovery order; excludes the starting target.</param>
/// <param name="DepthLimitReached">True when undiscovered consumers exist beyond the selected depth.</param>
/// <param name="ResultLimitReached">True when reachable consumers were omitted from the result.</param>
/// <param name="IncompleteDefinitions">Definitions whose dependency coverage is partial or unknown.</param>
public sealed record WorkspaceImpact(ImmutableArray<WorkspaceImpactItem> Items, bool DepthLimitReached,
    bool ResultLimitReached, ImmutableArray<string> IncompleteDefinitions);
