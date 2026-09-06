using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Transitions.IR;

/// <summary>
/// Stable diagnostic codes emitted by the canonical Transition document facade.
/// </summary>
public static class TransitionDefinitionDocumentDiagnosticCodes
{
    /// <summary>The shared execution document does not contain a Transition definition.</summary>
    public const string KindMismatch = "transitions.document.kindMismatch";

    /// <summary>The canonical definition payload cannot be projected as typed Transition IR.</summary>
    public const string DefinitionProjectionInvalid = "transitions.document.definitionProjectionInvalid";

    /// <summary>The projected Transition has a different canonical wire representation than the persisted payload.</summary>
    public const string DefinitionWireNonCanonical = "transitions.document.definitionWireNonCanonical";
}

/// <summary>
/// Creates and validates canonical Transition definitions in the shared execution-definition envelope.
/// </summary>
/// <remarks>
/// This facade does not introduce another document, schema version, fingerprint, or metadata model.
/// <see cref="ExecutionDefinitionDocument"/> remains the persisted authority; this type adds exact
/// Transition-kind dispatch, strict typed projection, unique canonical-wire validation, and block-specific
/// semantic validation.
/// </remarks>
public static class TransitionDefinitionDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<TransitionDefinition> Projection = new(
        kind: new(TransitionWireNames.DefinitionKind),
        kindMismatchCode: TransitionDefinitionDocumentDiagnosticCodes.KindMismatch,
        projectionInvalidCode: TransitionDefinitionDocumentDiagnosticCodes.DefinitionProjectionInvalid,
        wireNonCanonicalCode: TransitionDefinitionDocumentDiagnosticCodes.DefinitionWireNonCanonical,
        wireNonCanonicalMessage:
            "The persisted definition is not the unique canonical typed Transition v1 wire representation.",
        projectionFailurePath: static (_, exception) => (exception as JsonException)?.Path);

    /// <summary>Shared execution-definition kind for canonical Transition IR.</summary>
    public static ExecutionDefinitionKind Kind => Projection.Kind;

    /// <summary>Creates a fingerprinted shared execution document containing typed Transition IR.</summary>
    /// <param name="definitionId">Stable identity shared by every semantic revision of the Transition.</param>
    /// <param name="revisionId">Stable identity of this accepted Transition revision.</param>
    /// <param name="definition">Canonical typed Transition definition payload.</param>
    /// <param name="provenance">Required producer and root-source attribution.</param>
    /// <param name="extensions">Optional exact-versioned semantic extensions.</param>
    /// <param name="displayName">Optional human-facing name excluded from semantic fingerprinting.</param>
    /// <param name="description">Optional human-facing description excluded from semantic fingerprinting.</param>
    /// <param name="sourceMap">Optional normalized per-construct source attribution.</param>
    /// <param name="diagnostics">Optional retained authoring or validation diagnostics.</param>
    /// <returns>
    /// A current-version <see cref="ExecutionDefinitionDocument"/> whose kind is
    /// <see cref="Kind"/> and whose canonical payload is <paramref name="definition"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, the definition does not serialize as a JSON object or contains duplicate
    /// properties, or an extension or retained metadata value violates its structural contract.
    /// </exception>
    /// <exception cref="JsonException">
    /// The typed definition cannot be encoded using the strict execution-definition wire contract.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The typed definition has no canonical JSON representation.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The typed definition contains an unsupported runtime type.
    /// </exception>
    public static ExecutionDefinitionDocument Create(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        TransitionDefinition definition,
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

    /// <summary>
    /// Validates shared envelope integrity, exact Transition kind, canonical typed projection, and Transition IR.
    /// </summary>
    /// <param name="document">Shared execution-definition document to validate.</param>
    /// <returns>Deterministically ordered shared and Transition-specific diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">Semantic content cannot be encoded using the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(ExecutionDefinitionDocument document) =>
        ValidateCore(document, graph: null);

    /// <summary>
    /// Validates shared envelope integrity and Transition semantics using a graph that resolves referenced types
    /// and shapes.
    /// </summary>
    /// <param name="document">Shared execution-definition document to validate.</param>
    /// <param name="graph">Shared shape graph used to resolve named types and qualified shapes.</param>
    /// <returns>Deterministically ordered shared and Transition-specific diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">Semantic content cannot be encoded using the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(
        ExecutionDefinitionDocument document,
        ShapeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return ValidateCore(document, graph);
    }

    /// <summary>
    /// Attempts to read a canonical Transition document and project a validated typed definition.
    /// </summary>
    /// <param name="json">Persisted shared execution-definition JSON.</param>
    /// <param name="document">
    /// Receives the parsed shared document when structural deserialization succeeds, including when integrity,
    /// kind, projection, or Transition validation subsequently fails; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="definition">
    /// Receives the typed Transition definition only when all shared and block-specific validation succeeds;
    /// otherwise <see langword="null"/>.
    /// </param>
    /// <returns>Deterministically ordered read, integrity, kind, projection, canonical-wire, and Transition diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out ExecutionDefinitionDocument? document,
        out TransitionDefinition? definition)
    {
        var sharedValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(json, out document);
        return CompleteDeserialization(
            sharedValidation,
            document,
            graph: null,
            out definition);
    }

    /// <summary>
    /// Attempts to read a canonical Transition document using a graph that resolves referenced types and shapes.
    /// </summary>
    /// <param name="json">Persisted shared execution-definition JSON.</param>
    /// <param name="graph">Shared shape graph used to resolve named types and qualified shapes.</param>
    /// <param name="document">
    /// Receives the parsed shared document when structural deserialization succeeds, including when integrity,
    /// kind, projection, or Transition validation subsequently fails; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="definition">
    /// Receives the typed Transition definition only when all shared and block-specific validation succeeds;
    /// otherwise <see langword="null"/>.
    /// </param>
    /// <returns>Deterministically ordered read, integrity, kind, projection, canonical-wire, and Transition diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        ShapeGraph graph,
        out ExecutionDefinitionDocument? document,
        out TransitionDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var sharedValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(json, graph, out document);
        return CompleteDeserialization(sharedValidation, document, graph, out definition);
    }

    static DocumentValidationResult ValidateCore(
        ExecutionDefinitionDocument document,
        ShapeGraph? graph)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Projection.ValidateAndProject(
            ExecutionDefinitionDocumentValidator.Validate(document, graph),
            document,
            definition => TransitionDefinitionValidator.Validate(definition, graph),
            out _);
    }

    internal static DocumentValidationResult ValidateAuthored(
        ExecutionDefinitionDocument document,
        TransitionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(definition);
        return Projection.ValidateAuthored(
            document,
            definition,
            candidate => TransitionDefinitionValidator.Validate(candidate));
    }

    static DocumentValidationResult CompleteDeserialization(
        DocumentValidationResult sharedValidation,
        ExecutionDefinitionDocument? document,
        ShapeGraph? graph,
        out TransitionDefinition? definition)
    {
        return Projection.ValidateAndProject(
            sharedValidation,
            document,
            candidate => TransitionDefinitionValidator.Validate(candidate, graph),
            out definition);
    }

}
