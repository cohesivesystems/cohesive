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
    /// <summary>Shared execution-definition kind for canonical interaction contracts.</summary>
    public static ExecutionDefinitionKind Kind { get; } = new(InteractionWireNames.DefinitionKind);

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
        return WithSourceReferences(
            document,
            Combine(
                ExecutionDefinitionDocumentValidator.Validate(document, graph),
                ValidateContent(document, graph, out _)));
    }

    static DocumentValidationResult Complete(
        DocumentValidationResult shared,
        ExecutionDefinitionDocument? document,
        ShapeGraph? graph,
        out InteractionContractDefinition? definition)
    {
        definition = null;
        if (document is null)
            return shared;

        var content = ValidateContent(document, graph, out var candidate);
        var combined = WithSourceReferences(document, Combine(shared, content));
        if (combined.IsValid)
            definition = candidate;
        return combined;
    }

    static DocumentValidationResult ValidateContent(
        ExecutionDefinitionDocument document,
        ShapeGraph? graph,
        out InteractionContractDefinition? definition)
    {
        definition = null;
        if (document.Kind != Kind)
        {
            return Error(
                InteractionContractDocumentDiagnosticCodes.KindMismatch,
                $"Expected execution-definition kind '{Kind.Value}', but found '{document.Kind.Value}'.",
                "/kind");
        }

        InteractionContractDefinition candidate;
        try
        {
            candidate = document.GetDefinition<InteractionContractDefinition>();
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or FormatException
                                          or OverflowException)
        {
            return Error(
                InteractionContractDocumentDiagnosticCodes.DefinitionProjectionInvalid,
                exception.Message,
                "/definition");
        }

        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        var projected = JsonSerializer.SerializeToElement(candidate, options);
        var persistedBytes = ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document);
        var projectedBytes = ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(
            document.Metadata.SchemaVersion,
            document.Kind,
            projected,
            document.Extensions);
        var wire = persistedBytes.AsSpan().SequenceEqual(projectedBytes)
            ? DocumentValidationResult.Valid
            : Error(
                InteractionContractDocumentDiagnosticCodes.DefinitionWireNonCanonical,
                "The persisted definition is not the unique canonical typed interaction wire representation.",
                "/definition");
        var semantic = graph is null
            ? InteractionContractValidator.Validate(candidate)
            : InteractionContractValidator.Validate(candidate, graph);
        var validation = Combine(wire, PrefixDefinition(semantic));
        if (validation.IsValid)
            definition = candidate;
        return validation;
    }

    static DocumentValidationResult PrefixDefinition(DocumentValidationResult validation) =>
        validation.Diagnostics.IsDefaultOrEmpty
            ? validation
            : DocumentValidationResult.FromDiagnostics(validation.Diagnostics.Select(static diagnostic =>
                diagnostic with
                {
                    Location = string.IsNullOrEmpty(diagnostic.Location) || diagnostic.Location == "$"
                        ? "/definition"
                        : diagnostic.Location![0] == '/'
                            ? "/definition" + diagnostic.Location
                            : "/definition"
                }));

    static DocumentValidationResult WithSourceReferences(
        ExecutionDefinitionDocument document,
        DocumentValidationResult validation)
    {
        if (validation.Diagnostics.IsDefaultOrEmpty)
            return validation;

        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(validation.Diagnostics.Length);
        foreach (var diagnostic in validation.Diagnostics)
        {
            diagnostics.Add(document.Metadata.SourceMap.WithResolvedSourceReferences(
                diagnostic,
                document.Metadata.Provenance.Source.Reference,
                "canonicalValidation"));
        }
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics.MoveToImmutable());
    }

    static DocumentValidationResult Combine(params DocumentValidationResult[] results)
    {
        List<DocumentValidationDiagnostic> diagnostics = [];
        foreach (var result in results)
            diagnostics.AddRange(result.Diagnostics);
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static DocumentValidationResult Error(string code, string message, string location) =>
        DocumentValidationResult.FromDiagnostics([
            new(code, DiagnosticSeverity.Error, message, location)
        ]);
}
