using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted by the typed Channel document projection.</summary>
public static class ChannelDefinitionDocumentDiagnosticCodes
{
    /// <summary>The shared execution document contains another definition kind.</summary>
    public const string KindMismatch = "channels.document.kind.mismatch";

    /// <summary>The canonical Channel payload cannot be projected into its closed typed contract.</summary>
    public const string DefinitionProjectionInvalid = "channels.document.definition.projectionInvalid";

    /// <summary>The persisted Channel payload differs from the unique canonical typed wire.</summary>
    public const string DefinitionWireNonCanonical = "channels.document.definition.wireNonCanonical";
}

/// <summary>Typed facade over the shared persisted execution-definition authority for Channels.</summary>
public static class ChannelDefinitionDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<ChannelDefinition> Projection = new(
        kind: new ExecutionDefinitionKind(ChannelWireNames.DefinitionKind),
        kindMismatchCode: ChannelDefinitionDocumentDiagnosticCodes.KindMismatch,
        projectionInvalidCode: ChannelDefinitionDocumentDiagnosticCodes.DefinitionProjectionInvalid,
        wireNonCanonicalCode: ChannelDefinitionDocumentDiagnosticCodes.DefinitionWireNonCanonical,
        wireNonCanonicalMessage: "The persisted definition is not the unique canonical typed Channel wire representation.");

    /// <summary>Shared execution-definition kind for canonical Channels.</summary>
    public static ExecutionDefinitionKind Kind => Projection.Kind;

    /// <summary>Creates a fingerprinted shared execution document containing one Channel definition.</summary>
    /// <param name="definitionId">Stable identity shared by all revisions of the Channel.</param>
    /// <param name="revisionId">Stable identity of this accepted semantic revision.</param>
    /// <param name="definition">Canonical provider-neutral Channel definition.</param>
    /// <param name="provenance">Required producer and root-source attribution.</param>
    /// <param name="extensions">Optional exact-versioned semantic extensions.</param>
    /// <param name="displayName">Optional human-facing name excluded from fingerprinting.</param>
    /// <param name="description">Optional human-facing description excluded from fingerprinting.</param>
    /// <param name="sourceMap">Optional normalized per-construct source map.</param>
    /// <param name="diagnostics">Optional retained authoring or validation diagnostics.</param>
    /// <returns>A current-version shared execution-definition document.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity, extension, or metadata value is invalid.</exception>
    /// <exception cref="JsonException">The definition cannot be encoded by the strict wire contract.</exception>
    /// <exception cref="InvalidOperationException">The definition has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">The definition contains an unsupported runtime type.</exception>
    public static ExecutionDefinitionDocument Create(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        ChannelDefinition definition,
        ExecutionProvenance provenance,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default,
        string? displayName = null,
        string? description = null,
        ExecutionSourceMap? sourceMap = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default) =>
        ExecutionDefinitionDocument.Create(
            Kind,
            definitionId,
            revisionId,
            definition,
            provenance,
            extensions,
            displayName,
            description,
            sourceMap,
            diagnostics);

    /// <summary>Validates shared document integrity, typed projection, canonical wire form, and Channel semantics.</summary>
    /// <param name="document">Shared execution-definition document to validate.</param>
    /// <returns>Deterministically ordered shared and Channel diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document violates the strict canonical JSON contract.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(ExecutionDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Projection.ValidateAndProject(
            ExecutionDefinitionDocumentValidator.Validate(document),
            document,
            ChannelDefinitionValidator.Validate,
            out _);
    }

    /// <summary>Attempts to read and validate a canonical Channel document.</summary>
    /// <param name="json">Persisted shared execution-definition JSON.</param>
    /// <param name="document">Parsed shared document when structural deserialization succeeds.</param>
    /// <param name="definition">Validated typed Channel definition, or <see langword="null"/> on failure.</param>
    /// <returns>Deterministically ordered read, integrity, projection, and semantic diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out ExecutionDefinitionDocument? document,
        out ChannelDefinition? definition)
    {
        var shared = ExecutionDefinitionJsonSerializer.TryDeserialize(json, out document);
        return Projection.ValidateAndProject(
            shared,
            document,
            ChannelDefinitionValidator.Validate,
            out definition);
    }
}
