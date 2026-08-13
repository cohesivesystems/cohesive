using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Serialization;

/// <summary>Stable diagnostics emitted by the canonical hosted-Query document facade.</summary>
public static class HostedQueryDefinitionDocumentDiagnosticCodes
{
    /// <summary>The shared execution document does not contain a hosted Query.</summary>
    public const string KindMismatch = "hostedQueries.document.kindMismatch";

    /// <summary>The canonical payload cannot be projected as a typed hosted Query.</summary>
    public const string DefinitionProjectionInvalid = "hostedQueries.document.definitionProjectionInvalid";

    /// <summary>The projected hosted Query differs from the unique canonical typed wire representation.</summary>
    public const string DefinitionWireNonCanonical = "hostedQueries.document.definitionWireNonCanonical";
}

/// <summary>Creates and validates hosted Queries in the shared execution-definition envelope.</summary>
/// <remarks>
/// <see cref="ExecutionDefinitionDocument"/> remains the sole persisted definition, schema, fingerprint, and
/// provenance authority. This facade supplies hosted-Query kind dispatch and typed semantic validation only.
/// </remarks>
public static class HostedQueryDefinitionDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<HostedQueryDefinition> Projection = new(
        kind: new(HostedQueryWireNames.DefinitionKind),
        kindMismatchCode: HostedQueryDefinitionDocumentDiagnosticCodes.KindMismatch,
        projectionInvalidCode: HostedQueryDefinitionDocumentDiagnosticCodes.DefinitionProjectionInvalid,
        wireNonCanonicalCode: HostedQueryDefinitionDocumentDiagnosticCodes.DefinitionWireNonCanonical,
        wireNonCanonicalMessage:
            "The persisted definition is not the unique canonical typed hosted-Query wire representation.");

    /// <summary>Shared execution-definition kind for canonical hosted Queries.</summary>
    public static ExecutionDefinitionKind Kind => Projection.Kind;

    /// <summary>Creates a fingerprinted shared execution document containing one hosted Query.</summary>
    /// <param name="definitionId">Stable identity shared by all revisions of the hosted Query.</param>
    /// <param name="revisionId">Stable identity of this accepted semantic revision.</param>
    /// <param name="definition">Canonical typed hosted-Query definition.</param>
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
        HostedQueryDefinition definition,
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

    /// <summary>Validates shared integrity, typed projection, canonical wire form, and hosted-Query semantics.</summary>
    /// <param name="document">Shared execution-definition document to validate.</param>
    /// <returns>Deterministically ordered shared and hosted-Query diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document violates the strict canonical JSON contract.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(ExecutionDefinitionDocument document) =>
        ValidateCore(document, graph: null);

    /// <summary>Validates a hosted-Query document using a graph that resolves named portable types and shapes.</summary>
    /// <param name="document">Shared execution-definition document to validate.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified value contracts.</param>
    /// <returns>Deterministically ordered shared and hosted-Query diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">The document violates the strict canonical JSON contract.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(ExecutionDefinitionDocument document, ShapeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return ValidateCore(document, graph);
    }

    /// <summary>Attempts to read and validate a canonical hosted-Query document.</summary>
    /// <param name="json">Persisted shared execution-definition JSON.</param>
    /// <param name="document">Parsed shared document when structural deserialization succeeds.</param>
    /// <param name="definition">Validated typed hosted Query, or <see langword="null"/> on failure.</param>
    /// <returns>Deterministically ordered read, integrity, projection, and semantic diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out ExecutionDefinitionDocument? document,
        out HostedQueryDefinition? definition)
    {
        var shared = ExecutionDefinitionJsonSerializer.TryDeserialize(json, out document);
        return Complete(shared, document, graph: null, out definition);
    }

    /// <summary>Attempts to read a hosted-Query document using a graph that resolves portable types and shapes.</summary>
    /// <param name="json">Persisted shared execution-definition JSON.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified value contracts.</param>
    /// <param name="document">Parsed shared document when structural deserialization succeeds.</param>
    /// <param name="definition">Validated typed hosted Query, or <see langword="null"/> on failure.</param>
    /// <returns>Deterministically ordered read, integrity, projection, and semantic diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        ShapeGraph graph,
        out ExecutionDefinitionDocument? document,
        out HostedQueryDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var shared = ExecutionDefinitionJsonSerializer.TryDeserialize(json, graph, out document);
        return Complete(shared, document, graph, out definition);
    }

    static DocumentValidationResult ValidateCore(ExecutionDefinitionDocument document, ShapeGraph? graph)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Projection.ValidateAndProject(
            ExecutionDefinitionDocumentValidator.Validate(document, graph),
            document,
            definition => graph is null
                ? HostedQueryDefinitionValidator.Validate(definition)
                : HostedQueryDefinitionValidator.Validate(definition, graph),
            out _);
    }

    static DocumentValidationResult Complete(
        DocumentValidationResult shared,
        ExecutionDefinitionDocument? document,
        ShapeGraph? graph,
        out HostedQueryDefinition? definition) =>
        Projection.ValidateAndProject(
            shared,
            document,
            candidate => graph is null
                ? HostedQueryDefinitionValidator.Validate(candidate)
                : HostedQueryDefinitionValidator.Validate(candidate, graph),
            out definition);
}
