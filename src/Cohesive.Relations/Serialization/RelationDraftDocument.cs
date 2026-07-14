using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Opaque reference from a portable draft document to an artifact owned by its producer.
/// </summary>
/// <remarks>
/// The producer controls the reference value and its resolution. Cohesive.Relations does not
/// interpret the referenced artifact or include it in semantic draft identity.
/// </remarks>
public sealed record RelationDraftProducerArtifactReference
{
    /// <summary>Creates an external producer artifact reference.</summary>
    /// <param name="kind">Producer-defined, stable artifact kind.</param>
    /// <param name="value">Opaque producer-owned artifact identity.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="kind"/> or <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="kind"/> or <paramref name="value"/> is empty or consists only of white-space.
    /// </exception>
    [JsonConstructor]
    public RelationDraftProducerArtifactReference(string kind, string value)
    {
        Kind = Guard.RequireNotNullOrWhiteSpace(kind);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Producer-defined, stable artifact kind.</summary>
    public string Kind { get; init; }

    /// <summary>Opaque producer-owned artifact identity.</summary>
    public string Value { get; init; }
}

/// <summary>
/// Descriptive and producer metadata carried by a persisted relation draft document.
/// </summary>
/// <remarks>
/// Metadata describes the document artifact and is excluded from the semantic draft fingerprint.
/// Producer-specific evidence, scores, workflow state, and telemetry should remain in the producer's
/// own model and may be linked through <see cref="ProducerArtifacts"/> or <see cref="SourceUri"/>.
/// Deterministic convention explanations may be retained through <see cref="ConventionDecisions"/>.
/// </remarks>
public sealed record RelationDraftDocumentMetadata
{
    /// <summary>Empty relation draft document metadata.</summary>
    public static RelationDraftDocumentMetadata Empty { get; } = new();

    /// <summary>Creates relation draft document metadata.</summary>
    /// <param name="origin">Coarse origin of the persisted draft.</param>
    /// <param name="name">Optional human-facing document name.</param>
    /// <param name="description">Optional human-facing document description.</param>
    /// <param name="sourceUri">Optional URI identifying the source or producer artifact.</param>
    /// <param name="producer">Optional producer identity.</param>
    /// <param name="producerVersion">Optional producer version.</param>
    /// <param name="createdAtUtc">Optional creation timestamp.</param>
    /// <param name="updatedAtUtc">Optional last-update timestamp.</param>
    /// <param name="annotations">Portable document metadata annotations.</param>
    /// <param name="producerArtifacts">Opaque references to artifacts retained by the producer.</param>
    /// <param name="conventionDecisions">
    /// Attributable decisions emitted by a deterministic convention producer.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="producerArtifacts"/> or <paramref name="conventionDecisions"/> contains a
    /// <see langword="null"/> entry.
    /// </exception>
    [JsonConstructor]
    public RelationDraftDocumentMetadata(
        DocumentOrigin origin = DocumentOrigin.Unknown,
        string? name = null,
        string? description = null,
        string? sourceUri = null,
        string? producer = null,
        string? producerVersion = null,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? updatedAtUtc = null,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null,
        ImmutableArray<RelationDraftProducerArtifactReference> producerArtifacts = default,
        ImmutableArray<RelationDraftConventionDecision> conventionDecisions = default)
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
        if (!producerArtifacts.IsDefault && producerArtifacts.Any(static reference => reference is null))
            throw new ArgumentException("Producer artifact references cannot contain null entries.", nameof(producerArtifacts));
        if (!conventionDecisions.IsDefault && conventionDecisions.Any(static decision => decision is null))
            throw new ArgumentException("Convention decisions cannot contain null entries.", nameof(conventionDecisions));
        ProducerArtifacts = producerArtifacts.IsDefault
            ? []
            :
            [
                .. producerArtifacts
                    .OrderBy(static reference => reference?.Kind ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static reference => reference?.Value ?? string.Empty, StringComparer.Ordinal)
            ];
        ConventionDecisions = conventionDecisions.IsDefault
            ? []
            :
            [
                .. conventionDecisions
                    .OrderBy(static decision => decision?.SlotId.Value ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static decision => decision?.RuleId ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static decision => decision?.CandidateId?.Value ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static decision => decision?.Source?.ToString() ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static decision => decision?.Target.ToString() ?? string.Empty, StringComparer.Ordinal)
            ];
    }

    /// <summary>Coarse origin of the persisted draft.</summary>
    public DocumentOrigin Origin { get; init; }

    /// <summary>Optional human-facing document name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional human-facing document description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional URI identifying the source or producer artifact.</summary>
    public string? SourceUri { get; init; }

    /// <summary>Optional producer identity, such as a convention matcher, importer, or Ari.</summary>
    public string? Producer { get; init; }

    /// <summary>Optional producer version.</summary>
    public string? ProducerVersion { get; init; }

    /// <summary>Optional creation timestamp.</summary>
    public DateTimeOffset? CreatedAtUtc { get; init; }

    /// <summary>Optional last-update timestamp.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>Portable metadata annotations excluded from semantic fingerprinting.</summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

    /// <summary>Opaque producer artifact references excluded from semantic draft identity.</summary>
    public ImmutableArray<RelationDraftProducerArtifactReference> ProducerArtifacts { get; init; }

    /// <summary>Attributable convention decisions excluded from semantic draft identity.</summary>
    public ImmutableArray<RelationDraftConventionDecision> ConventionDecisions { get; init; }
}

/// <summary>
/// Cryptographic fingerprint of canonical relation draft semantic content.
/// </summary>
public sealed record RelationDraftFingerprint
{
    /// <summary>Creates a relation draft fingerprint.</summary>
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
    public RelationDraftFingerprint(string algorithm, string canonicalization, string value)
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
/// Portable, versioned document envelope for a non-executable relation draft.
/// </summary>
public sealed record RelationDraftDocument
{
    /// <summary>Current relation draft document schema version.</summary>
    public const string CurrentSchemaVersion = "relation-draft/v1";

    /// <summary>Creates a portable relation draft document.</summary>
    /// <param name="schemaVersion">Portable document schema version.</param>
    /// <param name="draft">Portable, non-executable relation draft.</param>
    /// <param name="draftFingerprint">Fingerprint of the draft's semantic content.</param>
    /// <param name="metadata">Document provenance and descriptive metadata.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="draft"/>, or
    /// <paramref name="draftFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public RelationDraftDocument(
        string schemaVersion,
        RelationDraft draft,
        RelationDraftFingerprint draftFingerprint,
        RelationDraftDocumentMetadata? metadata = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        Draft = Guard.RequireNotNull(draft);
        DraftFingerprint = Guard.RequireNotNull(draftFingerprint);
        Metadata = metadata ?? RelationDraftDocumentMetadata.Empty;
    }

    /// <summary>Portable document schema version.</summary>
    public string SchemaVersion { get; init; }

    /// <summary>Portable, non-executable relation draft.</summary>
    public RelationDraft Draft { get; init; }

    /// <summary>Fingerprint of the draft's semantic content.</summary>
    public RelationDraftFingerprint DraftFingerprint { get; init; }

    /// <summary>Document provenance and descriptive metadata.</summary>
    public RelationDraftDocumentMetadata Metadata { get; init; }

    /// <summary>Creates a current-version document and computes its semantic draft fingerprint.</summary>
    /// <param name="draft">Portable relation draft to persist.</param>
    /// <param name="metadata">Optional document provenance and descriptive metadata.</param>
    /// <returns>A current-version persisted relation draft document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="draft"/> fails draft-local semantic validation.</exception>
    /// <exception cref="InvalidOperationException">
    /// The draft contains a value that has no canonical relation draft JSON encoding.
    /// </exception>
    /// <exception cref="JsonException">
    /// The draft contains a value that cannot be written using the strict canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The draft contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static RelationDraftDocument FromDraft(
        RelationDraft draft,
        RelationDraftDocumentMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var validation = RelationDraftValidator.Validate(draft);
        if (!validation.IsValid)
        {
            var message = string.Join(
                Environment.NewLine,
                validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
            throw new ArgumentException(message, nameof(draft));
        }

        return new(
            schemaVersion: CurrentSchemaVersion,
            draft,
            draftFingerprint: RelationDraftFingerprinter.Compute(draft),
            metadata);
    }
}
