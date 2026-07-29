using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted by the canonical interaction-contract document facade.</summary>
public static class InteractionContractDocumentDiagnosticCodes
{
    /// <summary>The shared execution document does not contain an interaction contract.</summary>
    public const string KindMismatch = "interactions.document.kindMismatch";

    /// <summary>The canonical payload cannot be projected as a typed interaction contract.</summary>
    public const string DefinitionProjectionInvalid = "interactions.document.definitionProjectionInvalid";

    /// <summary>The projected contract differs from the unique canonical typed wire representation.</summary>
    public const string DefinitionWireNonCanonical = "interactions.document.definitionWireNonCanonical";
}

/// <summary>Creates and validates interaction contracts in the shared execution-definition envelope.</summary>
/// <remarks>
/// <see cref="ExecutionDefinitionDocument"/> remains the sole persisted definition, schema, fingerprint, and
/// provenance authority. This facade supplies interaction-kind dispatch and typed semantic validation only.
/// </remarks>
public static class InteractionContractDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<InteractionContractDefinition> Projection = new(
        kind: new(InteractionWireNames.DefinitionKind),
        kindMismatchCode: InteractionContractDocumentDiagnosticCodes.KindMismatch,
        projectionInvalidCode: InteractionContractDocumentDiagnosticCodes.DefinitionProjectionInvalid,
        wireNonCanonicalCode: InteractionContractDocumentDiagnosticCodes.DefinitionWireNonCanonical,
        wireNonCanonicalMessage:
            "The persisted definition is not the unique canonical typed interaction wire representation.");

    /// <summary>Shared execution-definition kind for canonical interaction contracts.</summary>
    public static ExecutionDefinitionKind Kind => Projection.Kind;

    /// <summary>Creates a fingerprinted shared execution document containing one interaction contract.</summary>
    /// <param name="definitionId">Stable identity shared by all revisions of the contract.</param>
    /// <param name="revisionId">Stable identity of this accepted semantic revision.</param>
    /// <param name="definition">Canonical typed interaction contract.</param>
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
        InteractionContractDefinition definition,
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

    /// <summary>Validates shared document integrity, typed projection, canonical wire form, and contract semantics.</summary>
    /// <param name="document">Shared execution-definition document to validate.</param>
    /// <returns>Deterministically ordered shared and interaction diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document violates the strict canonical JSON contract.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(ExecutionDefinitionDocument document) =>
        ValidateCore(document, graph: null);

    /// <summary>Validates an interaction document using a graph that resolves named portable types.</summary>
    /// <param name="document">Shared execution-definition document to validate.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified value contracts.</param>
    /// <returns>Deterministically ordered shared and interaction diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">The document violates the strict canonical JSON contract.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(
        ExecutionDefinitionDocument document,
        ShapeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return ValidateCore(document, graph);
    }

    /// <summary>Attempts to read and validate a canonical interaction-contract document.</summary>
    /// <param name="json">Persisted shared execution-definition JSON.</param>
    /// <param name="document">Parsed shared document when structural deserialization succeeds.</param>
    /// <param name="definition">Validated typed interaction contract, or <see langword="null"/> on failure.</param>
    /// <returns>Deterministically ordered read, integrity, projection, and semantic diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out ExecutionDefinitionDocument? document,
        out InteractionContractDefinition? definition)
    {
        var shared = ExecutionDefinitionJsonSerializer.TryDeserialize(json, out document);
        return Complete(shared, document, graph: null, out definition);
    }

    /// <summary>Attempts to read an interaction document using a graph that resolves named portable types.</summary>
    /// <param name="json">Persisted shared execution-definition JSON.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified value contracts.</param>
    /// <param name="document">Parsed shared document when structural deserialization succeeds.</param>
    /// <param name="definition">Validated typed interaction contract, or <see langword="null"/> on failure.</param>
    /// <returns>Deterministically ordered read, integrity, projection, and semantic diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        ShapeGraph graph,
        out ExecutionDefinitionDocument? document,
        out InteractionContractDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var shared = ExecutionDefinitionJsonSerializer.TryDeserialize(json, graph, out document);
        return Complete(shared, document, graph, out definition);
    }

    static DocumentValidationResult ValidateCore(
        ExecutionDefinitionDocument document,
        ShapeGraph? graph)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Projection.ValidateAndProject(
            ExecutionDefinitionDocumentValidator.Validate(document, graph),
            document,
            definition => graph is null
                ? InteractionContractValidator.Validate(definition)
                : InteractionContractValidator.Validate(definition, graph),
            out _);
    }

    static DocumentValidationResult Complete(
        DocumentValidationResult shared,
        ExecutionDefinitionDocument? document,
        ShapeGraph? graph,
        out InteractionContractDefinition? definition)
    {
        return Projection.ValidateAndProject(
            shared,
            document,
            candidate => graph is null
                ? InteractionContractValidator.Validate(candidate)
                : InteractionContractValidator.Validate(candidate, graph),
            out definition);
    }
}
