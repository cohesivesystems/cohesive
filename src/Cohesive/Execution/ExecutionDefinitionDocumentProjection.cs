using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Projects one typed execution-definition family from the shared persisted document authority.
/// </summary>
/// <typeparam name="TDefinition">Closed typed definition payload produced by the semantic block.</typeparam>
/// <remarks>
/// This helper owns the shared kind gate, strict typed projection, canonical-wire comparison, definition-path
/// attribution, source-map resolution, and deterministic diagnostic combination. Block facades remain responsible
/// for shared envelope validation and for supplying their own semantic validator.
/// </remarks>
public sealed class ExecutionDefinitionDocumentProjection<TDefinition>
    where TDefinition : class
{
    readonly string kindMismatchCode;
    readonly string projectionInvalidCode;
    readonly string wireNonCanonicalCode;
    readonly string wireNonCanonicalMessage;
    readonly Func<JsonElement, Exception, string?>? projectionFailurePath;

    /// <summary>Creates the typed projection policy for one execution-definition family.</summary>
    /// <param name="kind">Exact shared execution-definition kind accepted by this projection.</param>
    /// <param name="kindMismatchCode">Stable diagnostic code emitted when the document has another kind.</param>
    /// <param name="projectionInvalidCode">Stable diagnostic code emitted when typed projection fails.</param>
    /// <param name="wireNonCanonicalCode">
    /// Stable diagnostic code emitted when the typed projection does not reproduce the persisted semantic wire.
    /// </param>
    /// <param name="wireNonCanonicalMessage">Human-readable explanation of a noncanonical typed wire payload.</param>
    /// <param name="projectionFailurePath">
    /// Optional selector that can inspect the raw definition and serializer failure to identify a JSON path. When
    /// omitted, projection failures are attributed to the definition root.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="kind"/> is default or a diagnostic code or message is empty or white-space.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// A diagnostic code or message is <see langword="null"/>.
    /// </exception>
    public ExecutionDefinitionDocumentProjection(
        ExecutionDefinitionKind kind,
        string kindMismatchCode,
        string projectionInvalidCode,
        string wireNonCanonicalCode,
        string wireNonCanonicalMessage,
        Func<JsonElement, Exception, string?>? projectionFailurePath = null)
    {
        if (string.IsNullOrWhiteSpace(kind.Value))
        {
            throw new ArgumentException(
                "A typed execution-definition projection requires a non-default definition kind.",
                nameof(kind));
        }

        Kind = kind;
        this.kindMismatchCode = Guard.RequireNotNullOrWhiteSpace(kindMismatchCode);
        this.projectionInvalidCode = Guard.RequireNotNullOrWhiteSpace(projectionInvalidCode);
        this.wireNonCanonicalCode = Guard.RequireNotNullOrWhiteSpace(wireNonCanonicalCode);
        this.wireNonCanonicalMessage = Guard.RequireNotNullOrWhiteSpace(wireNonCanonicalMessage);
        this.projectionFailurePath = projectionFailurePath;
    }

    /// <summary>Exact shared execution-definition kind accepted by this projection.</summary>
    public ExecutionDefinitionKind Kind { get; }

    /// <summary>
    /// Combines shared envelope validation with typed projection, canonical-wire validation, and block semantics.
    /// </summary>
    /// <param name="envelopeValidation">
    /// Existing shared deserialization or document-integrity validation result.
    /// </param>
    /// <param name="document">
    /// Parsed shared document, or <see langword="null"/> when shared deserialization could not materialize one.
    /// </param>
    /// <param name="validateDefinition">Block-owned semantic validator for a successfully projected definition.</param>
    /// <param name="definition">
    /// Receives the typed definition only when shared, projection, wire, and semantic validation all succeed.
    /// </param>
    /// <returns>Deterministically ordered and source-attributed diagnostics from every validation layer.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="envelopeValidation"/> or <paramref name="validateDefinition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">Canonical typed content cannot be encoded by the strict wire contract.</exception>
    /// <exception cref="InvalidOperationException">Typed content has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">Typed content contains an unsupported runtime type.</exception>
    public DocumentValidationResult ValidateAndProject(
        DocumentValidationResult envelopeValidation,
        ExecutionDefinitionDocument? document,
        Func<TDefinition, DocumentValidationResult> validateDefinition,
        out TDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(envelopeValidation);
        ArgumentNullException.ThrowIfNull(validateDefinition);
        definition = null;
        if (document is null)
            return envelopeValidation;

        var contentValidation = ValidateContent(document, validateDefinition, out var candidate);
        var combined = WithSourceReferences(document, Combine(envelopeValidation, contentValidation));
        if (combined.IsValid)
            definition = candidate;
        return combined;
    }

    internal DocumentValidationResult ValidateAuthored(
        ExecutionDefinitionDocument document,
        TDefinition definition,
        Func<TDefinition, DocumentValidationResult> validateDefinition)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(validateDefinition);
        if (document.Kind != Kind)
        {
            return WithSourceReferences(
                document,
                Error(
                    kindMismatchCode,
                    $"Expected execution-definition kind '{Kind.Value}', but found '{document.Kind.Value}'.",
                    "/kind"));
        }

        return WithSourceReferences(
            document,
            PrefixDefinitionLocations(validateDefinition(definition)));
    }

    DocumentValidationResult ValidateContent(
        ExecutionDefinitionDocument document,
        Func<TDefinition, DocumentValidationResult> validateDefinition,
        out TDefinition? definition)
    {
        definition = null;
        if (document.Kind != Kind)
        {
            return Error(
                kindMismatchCode,
                $"Expected execution-definition kind '{Kind.Value}', but found '{document.Kind.Value}'.",
                "/kind");
        }

        TDefinition candidate;
        try
        {
            candidate = document.GetDefinition<TDefinition>();
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or FormatException
                                          or OverflowException)
        {
            var sourcePath = projectionFailurePath?.Invoke(document.Definition, exception);
            return Error(
                projectionInvalidCode,
                exception.Message,
                PrefixDefinitionLocation(JsonPathToPointer(sourcePath)));
        }

        var validation = Combine(
            ValidateCanonicalWire(document, candidate),
            PrefixDefinitionLocations(validateDefinition(candidate)));
        if (validation.IsValid)
            definition = candidate;
        return validation;
    }

    DocumentValidationResult ValidateCanonicalWire(
        ExecutionDefinitionDocument document,
        TDefinition definition)
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
            : Error(wireNonCanonicalCode, wireNonCanonicalMessage, "/definition");
    }

    static DocumentValidationResult PrefixDefinitionLocations(DocumentValidationResult validation)
    {
        if (validation.Diagnostics.IsDefaultOrEmpty)
            return validation;

        return DocumentValidationResult.FromDiagnostics(validation.Diagnostics.Select(static diagnostic =>
            diagnostic with
            {
                Location = PrefixDefinitionLocation(diagnostic.Location),
                Evidence = PrefixDefinitionEvidence(diagnostic.Evidence)
            }));
    }

    static DocumentDiagnosticEvidence? PrefixDefinitionEvidence(DocumentDiagnosticEvidence? evidence)
    {
        if (evidence is null || evidence.RelatedLocations.IsDefaultOrEmpty)
            return evidence;

        return new(
            evidence.Stage,
            evidence.Subject,
            [.. evidence.RelatedLocations.Select(PrefixDefinitionLocation)],
            evidence.SourceReferences,
            evidence.ResolutionOptions,
            evidence.Expected,
            evidence.Observed);
    }

    static string PrefixDefinitionLocation(string? location)
    {
        if (string.IsNullOrEmpty(location) || location == "$")
            return "/definition";
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
            return "$";

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
                                index++;
                            segment.Append(path[index++]);
                        }

                        if (index < path.Length && path[index] == quote)
                            index++;
                        AppendSegment(segment.ToString(), pointer);
                    }
                    else
                    {
                        AppendSegment(ReadUntil(path, ref index, ']'), pointer);
                    }

                    if (index < path.Length && path[index] == ']')
                        index++;
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
            index++;
        return value[start..index];
    }

    static void AppendSegment(string segment, StringBuilder pointer)
    {
        if (segment.Length == 0)
            return;

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

    static DocumentValidationResult Combine(
        DocumentValidationResult first,
        DocumentValidationResult second)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(
            first.Diagnostics.Length + second.Diagnostics.Length);
        diagnostics.AddRange(first.Diagnostics);
        diagnostics.AddRange(second.Diagnostics);
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics.MoveToImmutable());
    }

    static DocumentValidationResult Error(string code, string message, string location) =>
        DocumentValidationResult.FromDiagnostics([
            new(code, DiagnosticSeverity.Error, message, location)
        ]);
}
