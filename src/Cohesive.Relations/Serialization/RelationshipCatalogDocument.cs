using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Descriptive and provenance metadata carried by a persisted relationship catalog document.
/// </summary>
public sealed record RelationshipCatalogDocumentMetadata
{
    /// <summary>Empty relationship catalog document metadata.</summary>
    public static RelationshipCatalogDocumentMetadata Empty { get; } = new();

    /// <summary>Creates relationship catalog document metadata.</summary>
    /// <param name="origin">Coarse origin of the persisted catalog.</param>
    /// <param name="name">Optional human-facing catalog name.</param>
    /// <param name="description">Optional human-facing catalog description.</param>
    /// <param name="sourceUri">Optional URI identifying the source representation.</param>
    /// <param name="producer">Optional producer identity.</param>
    /// <param name="producerVersion">Optional producer version.</param>
    /// <param name="createdAtUtc">Optional creation timestamp.</param>
    /// <param name="updatedAtUtc">Optional update timestamp.</param>
    /// <param name="annotations">Portable metadata annotations.</param>
    [JsonConstructor]
    public RelationshipCatalogDocumentMetadata(
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

    /// <summary>Coarse origin of the persisted catalog.</summary>
    public DocumentOrigin Origin { get; init; }

    /// <summary>Optional human-facing catalog name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional human-facing catalog description.</summary>
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

    /// <summary>Portable metadata annotations excluded from semantic fingerprinting.</summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }
}

/// <summary>
/// Cryptographic fingerprint of canonical relationship catalog semantics.
/// </summary>
public sealed record RelationshipCatalogFingerprint
{
    /// <summary>Creates a relationship catalog fingerprint.</summary>
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
    [JsonConstructor]
    public RelationshipCatalogFingerprint(string algorithm, string canonicalization, string value)
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
/// Portable, versioned document envelope for a canonical semantic relationship catalog.
/// </summary>
public sealed record RelationshipCatalogDocument
{
    /// <summary>Current relationship catalog document schema version.</summary>
    public const string CurrentSchemaVersion = "relationship-catalog/v1";

    /// <summary>Creates a portable relationship catalog document.</summary>
    /// <param name="schemaVersion">Portable document schema version.</param>
    /// <param name="catalog">Canonical semantic relationship catalog.</param>
    /// <param name="catalogFingerprint">Fingerprint of semantic catalog content.</param>
    /// <param name="metadata">Document provenance and descriptive metadata.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="catalog"/>, or
    /// <paramref name="catalogFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public RelationshipCatalogDocument(
        string schemaVersion,
        RelationshipCatalog catalog,
        RelationshipCatalogFingerprint catalogFingerprint,
        RelationshipCatalogDocumentMetadata? metadata = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        Catalog = Guard.RequireNotNull(catalog);
        CatalogFingerprint = Guard.RequireNotNull(catalogFingerprint);
        Metadata = metadata ?? RelationshipCatalogDocumentMetadata.Empty;
    }

    /// <summary>Portable document schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>Canonical semantic relationship catalog.</summary>
    public RelationshipCatalog Catalog { get; init; }

    /// <summary>Fingerprint of semantic catalog content.</summary>
    public RelationshipCatalogFingerprint CatalogFingerprint { get; init; }

    /// <summary>Document provenance and descriptive metadata.</summary>
    public RelationshipCatalogDocumentMetadata Metadata { get; init; }

    /// <summary>Creates a current-version document and computes its semantic catalog fingerprint.</summary>
    /// <param name="catalog">Canonical semantic relationship catalog.</param>
    /// <param name="metadata">Optional document provenance and descriptive metadata.</param>
    /// <returns>A current-version persisted relationship catalog document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="catalog"/> fails catalog-local semantic validation.</exception>
    /// <exception cref="InvalidOperationException">
    /// The catalog contains a value that has no canonical relationship catalog JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The catalog contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static RelationshipCatalogDocument FromCatalog(
        RelationshipCatalog catalog,
        RelationshipCatalogDocumentMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var validation = RelationshipCatalogValidator.Validate(catalog);
        if (!validation.IsValid)
        {
            var message = string.Join(
                Environment.NewLine,
                validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
            throw new ArgumentException(message, nameof(catalog));
        }

        return new(
            schemaVersion: CurrentSchemaVersion,
            catalog,
            catalogFingerprint: RelationshipCatalogFingerprinter.Compute(catalog),
            metadata);
    }
}
