using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Semantics;

/// <summary>Inspection reference projected from an owning domain; absent evidence is never invented.</summary>
/// <param name="DefinitionId">Workspace identity, qualified by the domain when necessary.</param>
/// <param name="RevisionId">Authored revision, or null when the domain has no revisions.</param>
/// <param name="Fingerprint">Content evidence, or null for an identity-only reference.</param>
public sealed record WorkspaceDocumentReference(ExecutionDefinitionId DefinitionId,
    ExecutionRevisionId? RevisionId, ExecutionDefinitionFingerprint? Fingerprint)
{
    /// <summary>Projects an existing exact execution reference without changing its evidence.</summary>
    /// <param name="reference">Canonical execution reference.</param>
    /// <returns>The same identity, revision and fingerprint in the inspection vocabulary.</returns>
    public static WorkspaceDocumentReference FromExecution(ExecutionDefinitionReference reference)
        => new(reference.DefinitionId, reference.RevisionId, reference.Fingerprint);
}

/// <summary>Transient inspection view of an original domain document, never an authoring or executable IR.</summary>
/// <param name="Reference">Identity and integrity evidence supplied by the domain reader.</param>
/// <param name="Kind">Owning domain kind.</param>
/// <param name="DisplayName">Optional human-readable name.</param>
/// <param name="Json">Canonical original domain JSON; reader must return independently owned storage.</param>
/// <param name="Source">Original declaration attribution, when available.</param>
/// <param name="SourceMap">Original source mappings; null means unavailable.</param>
/// <param name="Validation">Mandatory domain integrity validation, including fingerprint verification.</param>
/// <param name="DomainValidationAvailable">Whether domain semantics were validated.</param>
/// <param name="Dependencies">Projected references; null means no reader is available.</param>
/// <param name="ReferencesComplete">Whether dependencies cover the reader's documented domain scope.</param>
public sealed record WorkspaceDocument(WorkspaceDocumentReference Reference, string Kind, string? DisplayName,
    JsonElement Json, ExecutionSourceProvenance? Source, ExecutionSourceMap? SourceMap,
    DocumentValidationResult Validation, bool DomainValidationAvailable,
    ImmutableArray<WorkspaceDocumentReference>? Dependencies, bool ReferencesComplete)
{
    internal Func<DocumentValidationResult>? ValidateDomain { get; init; }
    internal Func<ImmutableArray<WorkspaceDocumentReference>>? ReadDependencies { get; init; }
}
