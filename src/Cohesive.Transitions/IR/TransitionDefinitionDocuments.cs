using System.Collections.Immutable;
using System.Text;
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
    /// <summary>Shared execution-definition kind for canonical Transition IR.</summary>
    public static ExecutionDefinitionKind Kind { get; } = new(TransitionWireNames.DefinitionKind);

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
        var sharedValidation = ExecutionDefinitionDocumentValidator.Validate(document, graph);
        var transitionValidation = ValidateTransitionContent(document, graph, out _);
        return CombineDeterministically(sharedValidation, transitionValidation);
    }

    static DocumentValidationResult CompleteDeserialization(
        DocumentValidationResult sharedValidation,
        ExecutionDefinitionDocument? document,
        ShapeGraph? graph,
        out TransitionDefinition? definition)
    {
        definition = null;
        if (document is null)
        {
            return sharedValidation;
        }

        var transitionValidation = ValidateTransitionContent(
            document,
            graph,
            out var candidateDefinition);
        var combined = CombineDeterministically(sharedValidation, transitionValidation);
        if (combined.IsValid)
        {
            definition = candidateDefinition;
        }

        return combined;
    }

    static DocumentValidationResult ValidateTransitionContent(
        ExecutionDefinitionDocument document,
        ShapeGraph? graph,
        out TransitionDefinition? definition)
    {
        definition = null;
        if (document.Kind != Kind)
        {
            return Error(
                TransitionDefinitionDocumentDiagnosticCodes.KindMismatch,
                $"Expected execution-definition kind '{Kind.Value}', but found '{document.Kind.Value}'.",
                "/kind");
        }

        TransitionDefinition candidate;
        try
        {
            candidate = document.GetDefinition<TransitionDefinition>();
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or FormatException
                                          or OverflowException)
        {
            return Error(
                TransitionDefinitionDocumentDiagnosticCodes.DefinitionProjectionInvalid,
                exception.Message,
                exception is JsonException jsonException
                    ? PrefixDefinitionLocation(JsonPathToPointer(jsonException.Path))
                    : "/definition");
        }

        var validation = CombineDeterministically(
            ValidateCanonicalWire(document, candidate),
            PrefixDefinitionLocations(TransitionDefinitionValidator.Validate(candidate, graph)));
        if (validation.IsValid)
        {
            definition = candidate;
        }

        return validation;
    }

    static DocumentValidationResult ValidateCanonicalWire(
        ExecutionDefinitionDocument document,
        TransitionDefinition definition)
    {
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        var projected = JsonSerializer.SerializeToElement(definition, options);
        var persistedBytes = ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document);
        var projectedBytes = ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(
            document.Metadata.SchemaVersion,
            document.Kind,
            projected,
            document.Extensions);
        return persistedBytes.AsSpan().SequenceEqual(projectedBytes)
            ? DocumentValidationResult.Valid
            : Error(
                TransitionDefinitionDocumentDiagnosticCodes.DefinitionWireNonCanonical,
                "The persisted definition is not the unique canonical typed Transition v1 wire representation.",
                "/definition");
    }

    static DocumentValidationResult PrefixDefinitionLocations(DocumentValidationResult validation)
    {
        if (validation.Diagnostics.IsDefaultOrEmpty)
        {
            return validation;
        }

        return DocumentValidationResult.FromDiagnostics(validation.Diagnostics.Select(static diagnostic =>
            diagnostic with { Location = PrefixDefinitionLocation(diagnostic.Location) }));
    }

    static string PrefixDefinitionLocation(string? location)
    {
        if (string.IsNullOrEmpty(location) || location == "$")
        {
            return "/definition";
        }

        if (string.Equals(location, "/definition", StringComparison.Ordinal)
            || location.StartsWith("/definition/", StringComparison.Ordinal))
        {
            return location;
        }
        return location[0] == '/'
            ? "/definition" + location
            : "/definition";
    }

    static string JsonPathToPointer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return "$";
        }

        StringBuilder pointer = new();
        var index = path[0] == '$' ? 1 : 0;
        while (index < path.Length)
        {
            switch (path[index])
            {
                case '.':
                    index++;
                    AppendSegment(ReadUntil(path, ref index, '.', '['), pointer);
                    break;
                case '[':
                    index++;
                    if (index < path.Length && path[index] is '\'' or '"')
                    {
                        var quote = path[index++];
                        StringBuilder segment = new();
                        while (index < path.Length && path[index] != quote)
                        {
                            if (path[index] == '\\' && index + 1 < path.Length)
                            {
                                index++;
                            }

                            segment.Append(path[index++]);
                        }

                        if (index < path.Length && path[index] == quote)
                        {
                            index++;
                        }
                        AppendSegment(segment.ToString(), pointer);
                    }
                    else
                    {
                        AppendSegment(ReadUntil(path, ref index, ']'), pointer);
                    }

                    if (index < path.Length && path[index] == ']')
                    {
                        index++;
                    }
                    break;
                default:
                    AppendSegment(ReadUntil(path, ref index, '.', '['), pointer);
                    break;
            }
        }

        return pointer.Length == 0 ? "$" : pointer.ToString();
    }

    static string ReadUntil(string value, ref int index, params char[] terminators)
    {
        var start = index;
        while (index < value.Length && Array.IndexOf(terminators, value[index]) < 0)
        {
            index++;
        }
        return value[start..index];
    }

    static void AppendSegment(string segment, StringBuilder pointer)
    {
        if (segment.Length == 0)
        {
            return;
        }

        pointer.Append('/');
        foreach (var character in segment)
        {
            switch (character)
            {
                case '~':
                    pointer.Append("~0");
                    break;
                case '/':
                    pointer.Append("~1");
                    break;
                default:
                    pointer.Append(character);
                    break;
            }
        }
    }

    static DocumentValidationResult CombineDeterministically(
        params DocumentValidationResult[] results)
    {
        List<DocumentValidationDiagnostic> diagnostics = [];
        foreach (var result in results)
        {
            diagnostics.AddRange(result.Diagnostics);
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static DocumentValidationResult Error(string code, string message, string location) =>
        DocumentValidationResult.FromDiagnostics([
            new(
                Code: code,
                Severity: DiagnosticSeverity.Error,
                Message: message,
                Location: location)
        ]);
}
