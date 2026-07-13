using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Metadata carried by a persisted relation/query IR document.
/// </summary>
public sealed record RelationQueryDocumentMetadata
{
    /// <summary>Empty document metadata.</summary>
    public static RelationQueryDocumentMetadata Empty { get; } = new();

    /// <summary>Creates relation/query document metadata.</summary>
    /// <param name="origin">Coarse origin of the persisted definition.</param>
    /// <param name="name">Optional human-facing document name.</param>
    /// <param name="description">Optional human-facing description.</param>
    /// <param name="sourceUri">Optional URI identifying the source representation.</param>
    /// <param name="producer">Optional producer identity.</param>
    /// <param name="producerVersion">Optional producer version.</param>
    /// <param name="createdAtUtc">Optional creation timestamp.</param>
    /// <param name="updatedAtUtc">Optional update timestamp.</param>
    /// <param name="annotations">Portable metadata annotations.</param>
    public RelationQueryDocumentMetadata(
        DocumentOrigin origin = DocumentOrigin.Unknown,
        string? name = null,
        string? description = null,
        string? sourceUri = null,
        string? producer = null,
        string? producerVersion = null,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? updatedAtUtc = null,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null)
    {
        Origin = origin;
        Name = name.TrimmedEmptyOrWhiteSpaceAs();
        Description = description.TrimmedEmptyOrWhiteSpaceAs();
        SourceUri = sourceUri.TrimmedEmptyOrWhiteSpaceAs();
        Producer = producer.TrimmedEmptyOrWhiteSpaceAs();
        ProducerVersion = producerVersion.TrimmedEmptyOrWhiteSpaceAs();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Annotations = AnnotationMap.Normalize(annotations);
    }

    /// <summary>Coarse origin of the persisted definition.</summary>
    public DocumentOrigin Origin { get; init; }

    /// <summary>Optional human-facing document name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional human-facing document description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional URI identifying the source representation.</summary>
    public string? SourceUri { get; init; }

    /// <summary>Optional producer identity, such as a DSL compiler, importer, or Ari.</summary>
    public string? Producer { get; init; }

    /// <summary>Optional producer version.</summary>
    public string? ProducerVersion { get; init; }

    /// <summary>Optional creation timestamp.</summary>
    public DateTimeOffset? CreatedAtUtc { get; init; }

    /// <summary>Optional update timestamp.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>Portable metadata annotations.</summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }
}

/// <summary>
/// Cryptographic fingerprint of canonical semantic definition content.
/// </summary>
public sealed record RelationQueryDefinitionFingerprint
{
    /// <summary>Creates a definition fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/>
    /// is empty or consists only of white-space characters.
    /// </exception>
    public RelationQueryDefinitionFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; init; }

    /// <summary>Canonicalization profile applied before hashing.</summary>
    public string Canonicalization { get; init; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; init; }
}

/// <summary>
/// Portable, versioned document envelope for canonical relation/query IR.
/// </summary>
public sealed record RelationQueryDocument
{
    /// <summary>Current relation/query document schema version.</summary>
    public const string CurrentSchemaVersion = "relation-query/v1";

    /// <summary>Creates a portable relation/query document.</summary>
    /// <param name="schemaVersion">Portable document schema version.</param>
    /// <param name="definition">Canonical relation or query definition.</param>
    /// <param name="definitionFingerprint">Fingerprint of semantic definition content.</param>
    /// <param name="metadata">Document provenance and descriptive metadata.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="definition"/>, or
    /// <paramref name="definitionFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public RelationQueryDocument(
        string schemaVersion,
        RelationQueryDefinition definition,
        RelationQueryDefinitionFingerprint definitionFingerprint,
        RelationQueryDocumentMetadata? metadata = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        Definition = Guard.RequireNotNull(definition);
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        Metadata = metadata ?? RelationQueryDocumentMetadata.Empty;
    }

    /// <summary>Portable document schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>Canonical relation or query definition.</summary>
    public RelationQueryDefinition Definition { get; init; }

    /// <summary>Fingerprint of semantic definition content.</summary>
    public RelationQueryDefinitionFingerprint DefinitionFingerprint { get; init; }

    /// <summary>Document provenance and descriptive metadata.</summary>
    public RelationQueryDocumentMetadata Metadata { get; init; }

    /// <summary>Creates a current-version document and computes its semantic fingerprint.</summary>
    /// <param name="definition">Canonical relation or query definition.</param>
    /// <param name="metadata">Optional document provenance and descriptive metadata.</param>
    /// <returns>A current-version persisted document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> fails semantic validation.</exception>
    /// <exception cref="InvalidOperationException">
    /// The definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The definition contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static RelationQueryDocument FromDefinition(
        RelationQueryDefinition definition,
        RelationQueryDocumentMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var validation = RelationQueryDefinitionValidator.Validate(definition);
        if (!validation.IsValid)
        {
            var message = string.Join(
                Environment.NewLine,
                validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
            throw new ArgumentException(message, nameof(definition));
        }

        return new(
            schemaVersion: CurrentSchemaVersion,
            definition,
            definitionFingerprint: RelationQueryDefinitionFingerprinter.Compute(definition),
            metadata);
    }
}
