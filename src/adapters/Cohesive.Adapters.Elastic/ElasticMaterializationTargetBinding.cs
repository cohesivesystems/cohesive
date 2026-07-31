using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Elastic;

/// <summary>Stable identity of one persisted Elasticsearch materialization-target binding.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ElasticMaterializationTargetBindingId
{
    /// <summary>Creates a target-binding identity.</summary>
    /// <param name="value">Stable versioned binding identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ElasticMaterializationTargetBindingId(string value) =>
        Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Gets the stable versioned binding identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable binding identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of one normalized Elasticsearch materialization-target binding.</summary>
public sealed record ElasticMaterializationTargetBindingFingerprint
{
    /// <summary>Creates a binding fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty, or <paramref name="value"/> is not lowercase hexadecimal.</exception>
    [JsonConstructor]
    public ElasticMaterializationTargetBindingFingerprint(
        string algorithm,
        string canonicalization,
        string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = ElasticMaterializationBindingContract.RequireLowerHex(value, nameof(value));
    }

    /// <summary>Gets the hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Fingerprint of the externally managed index template expected by one generation binding.</summary>
public sealed record ElasticMaterializationIndexTemplateFingerprint
{
    /// <summary>Creates an index-template fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Template canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty, or <paramref name="value"/> is not lowercase hexadecimal.</exception>
    [JsonConstructor]
    public ElasticMaterializationIndexTemplateFingerprint(
        string algorithm,
        string canonicalization,
        string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = ElasticMaterializationBindingContract.RequireLowerHex(value, nameof(value));
    }

    /// <summary>Gets the hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the template canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Attributable deployment evidence for the externally managed index template declared for generations.</summary>
/// <remarks>
/// The current Relations binding describes exact query and retrieval semantics but intentionally does not duplicate
/// every analyzer, index setting, ingest pipeline, or mapping-template declaration. This evidence keeps the expected
/// physical authority explicit and fingerprinted in binding and validation provenance. The adapter does not currently
/// read live template metadata or independently attest that the deployed template still matches this fingerprint;
/// deployment validation remains responsible for that drift check.
/// </remarks>
public sealed record ElasticMaterializationIndexTemplateEvidence
{
    /// <summary>Creates index-template evidence.</summary>
    /// <param name="name">Concrete composable index-template identity.</param>
    /// <param name="fingerprint">Expected normalized template-content fingerprint.</param>
    /// <param name="authority">Stable non-secret identity of the template's configuration authority.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/>, <paramref name="fingerprint"/>, or <paramref name="authority"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not a bounded lowercase template identity, or <paramref name="authority"/> is not
    /// a bounded ASCII provenance identity.
    /// </exception>
    [JsonConstructor]
    public ElasticMaterializationIndexTemplateEvidence(
        string name,
        ElasticMaterializationIndexTemplateFingerprint fingerprint,
        string authority)
    {
        Name = ElasticMaterializationBindingContract.RequireTemplateName(name, nameof(name));
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        Authority = ElasticMaterializationBindingContract.RequireAuthority(authority, nameof(authority));
    }

    /// <summary>Gets the concrete composable index-template identity.</summary>
    public string Name { get; }

    /// <summary>Gets the expected normalized template-content fingerprint.</summary>
    public ElasticMaterializationIndexTemplateFingerprint Fingerprint { get; }

    /// <summary>Gets the stable non-secret configuration authority.</summary>
    public string Authority { get; }
}

/// <summary>
/// Persisted evidence for the external single-writer authority that fences one Elasticsearch materialization scope.
/// </summary>
/// <remarks>
/// Elasticsearch bulk operations cannot atomically fence mutations across every document in a generation. The
/// adapter therefore requires an external coordination authority to admit at most one active generation writer for
/// <see cref="Scope"/>. This evidence identifies that deployment contract; it is not itself a runtime lease or fence.
/// </remarks>
public sealed record ElasticMaterializationSingleWriterEvidence
{
    /// <summary>Creates explicit single-writer coordination evidence.</summary>
    /// <param name="authority">Stable non-secret identity of the system enforcing exclusive writer admission.</param>
    /// <param name="scope">Stable non-secret identity of the exclusivity scope protected by the authority.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A parameter is empty, exceeds 256 characters, or is not a bounded ASCII provenance identity.
    /// </exception>
    [JsonConstructor]
    public ElasticMaterializationSingleWriterEvidence(string authority, string scope)
    {
        Authority = ElasticMaterializationBindingContract.RequireAuthority(authority, nameof(authority));
        Scope = ElasticMaterializationBindingContract.RequireAuthority(scope, nameof(scope));
    }

    /// <summary>Gets the system responsible for admitting only one writer in <see cref="Scope"/>.</summary>
    public string Authority { get; }

    /// <summary>Gets the exact exclusivity scope covered by the external coordination authority.</summary>
    public string Scope { get; }
}

/// <summary>
/// Persisted physical binding for one Elasticsearch generation target and its stable canonical read alias.
/// </summary>
/// <remarks>
/// Generation index identities are derived deterministically from this binding and the caller-assigned canonical
/// generation identity. The bound Relations storage artifact addresses <see cref="ReadAlias"/>, so alias promotion
/// changes physical placement without recompiling canonical row or aggregation semantics. The runtime client is
/// deliberately absent and is supplied through <see cref="ElasticElasticsearchRuntimeBinding"/>. Every physical
/// field path in the Relations artifact must remain inside the <see cref="ValueField"/> envelope; adapter metadata
/// and Elasticsearch <c>_id</c> metadata are not part of the materialized canonical value.
/// </remarks>
public sealed class ElasticMaterializationTargetBinding
{
    /// <summary>Current persisted Elasticsearch materialization-target binding schema.</summary>
    public const string CurrentSchemaVersion = "cohesive.storage.elastic-materialization-target/v1";

    /// <summary>Fixed root field containing adapter-owned version, idempotency, and tombstone metadata.</summary>
    public const string MetadataField = "_cohesive";

    /// <summary>Fixed root field containing the portable materialized value.</summary>
    public const string ValueField = "value";

    /// <summary>
    /// Maximum UTF-16 character count for identities indexed as Elasticsearch keywords; well-formed Unicode at this
    /// bound occupies at most 32,764 UTF-8 bytes.
    /// </summary>
    public const int MaximumIndexedIdentityCharacters = 8_191;

    internal const string FingerprintAlgorithm = "sha256";
    internal const string FingerprintCanonicalization =
        "cohesive.storage.elastic-materialization-target/v1-c14n/v1";
    const int GenerationHashCharacters = 64;

    /// <summary>Creates an explicit persisted target binding.</summary>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="cluster">Persisted Elasticsearch cluster identity.</param>
    /// <param name="targetId">Canonical materialization target identity.</param>
    /// <param name="materializationId">Logical materialization stored by this target.</param>
    /// <param name="readAlias">Stable alias used by canonical Relations reads.</param>
    /// <param name="generationIndexPrefix">Lowercase physical prefix ending in <c>-</c> for generation indexes.</param>
    /// <param name="controlIndexName">Concrete hidden or ordinary index retaining target coordination state.</param>
    /// <param name="indexTemplate">Exact externally managed template evidence for generation indexes.</param>
    /// <param name="singleWriter">External single-writer coordination evidence for generation mutations.</param>
    /// <param name="searchBinding">
    /// Exact canonical Relations binding whose index is <paramref name="readAlias"/> and whose physical paths are
    /// rooted in <see cref="ValueField"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="indexTemplate"/>, <paramref name="singleWriter"/>, or <paramref name="searchBinding"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity or physical name is invalid; names overlap; the generation prefix cannot leave room for its fixed
    /// digest; or <paramref name="searchBinding"/> does not address the exact alias and canonical Elasticsearch
    /// target, escapes the materialized value envelope, or targets Elasticsearch <c>_id</c> metadata.
    /// </exception>
    public ElasticMaterializationTargetBinding(
        ElasticMaterializationTargetBindingId id,
        ElasticClusterId cluster,
        MaterializationTargetId targetId,
        MaterializationId materializationId,
        string readAlias,
        string generationIndexPrefix,
        string controlIndexName,
        ElasticMaterializationIndexTemplateEvidence indexTemplate,
        ElasticMaterializationSingleWriterEvidence singleWriter,
        ElasticRelationQueryStorageBinding searchBinding)
    {
        RequireDefined(id.Value, nameof(id));
        RequireDefined(cluster.Value, nameof(cluster));
        RequireDefined(targetId.Value, nameof(targetId));
        RequireDefined(materializationId.Value, nameof(materializationId));
        Id = id;
        Cluster = cluster;
        TargetId = targetId;
        MaterializationId = materializationId;
        ReadAlias = ElasticRelationQueryStorageBinding.RequireConcreteIndexName(readAlias, nameof(readAlias));
        GenerationIndexPrefix = ElasticMaterializationBindingContract.RequireGenerationPrefix(
            generationIndexPrefix,
            GenerationHashCharacters,
            nameof(generationIndexPrefix));
        ControlIndexName = ElasticRelationQueryStorageBinding.RequireConcreteIndexName(
            controlIndexName,
            nameof(controlIndexName));
        IndexTemplate = indexTemplate ?? throw new ArgumentNullException(nameof(indexTemplate));
        SingleWriter = singleWriter ?? throw new ArgumentNullException(nameof(singleWriter));
        SearchBinding = searchBinding ?? throw new ArgumentNullException(nameof(searchBinding));

        if (string.Equals(ReadAlias, ControlIndexName, StringComparison.Ordinal)
            || ReadAlias.StartsWith(GenerationIndexPrefix, StringComparison.Ordinal)
            || ControlIndexName.StartsWith(GenerationIndexPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The read alias, generation-index namespace, and control index must be distinct.",
                nameof(readAlias));
        }

        if (!string.Equals(searchBinding.IndexName, ReadAlias, StringComparison.Ordinal)
            || searchBinding.Target != ElasticRelationQueryTargetProfile.Target
            || searchBinding.TargetProfile != ElasticRelationQueryTargetProfile.ProfileId)
        {
            throw new ArgumentException(
                "The target read binding must address the exact stable alias and canonical Elasticsearch target profile.",
                nameof(searchBinding));
        }
        if (searchBinding.PaginationConsistency == ElasticRelationQueryPaginationConsistency.StableSearchView)
        {
            throw new ArgumentException(
                "A swappable materialization read alias cannot attest a stable multi-request search view without an external read/promotion lease or pinned search context.",
                nameof(searchBinding));
        }

        RequireMaterializedValuePaths(searchBinding);

        Fingerprint = ElasticMaterializationTargetBindingFingerprinter.Compute(this);
    }

    /// <summary>Rehydrates and verifies a persisted target binding.</summary>
    /// <param name="schemaVersion">Persisted binding schema version.</param>
    /// <param name="fingerprint">Persisted fingerprint expected to match normalized content.</param>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="cluster">Persisted Elasticsearch cluster identity.</param>
    /// <param name="targetId">Canonical materialization target identity.</param>
    /// <param name="materializationId">Logical materialization stored by this target.</param>
    /// <param name="readAlias">Stable alias used by canonical Relations reads.</param>
    /// <param name="generationIndexPrefix">Lowercase physical prefix ending in <c>-</c> for generation indexes.</param>
    /// <param name="controlIndexName">Concrete index retaining target coordination state.</param>
    /// <param name="indexTemplate">Exact externally managed template evidence.</param>
    /// <param name="singleWriter">External single-writer coordination evidence for generation mutations.</param>
    /// <param name="searchBinding">
    /// Exact canonical Relations binding whose index is <paramref name="readAlias"/> and whose physical paths are
    /// rooted in <see cref="ValueField"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="fingerprint"/>, <paramref name="indexTemplate"/>,
    /// <paramref name="singleWriter"/>, or <paramref name="searchBinding"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema or fingerprint is stale, or another normalized target-binding invariant, including the materialized
    /// value-envelope boundary, is violated.
    /// </exception>
    [JsonConstructor]
    public ElasticMaterializationTargetBinding(
        string schemaVersion,
        ElasticMaterializationTargetBindingFingerprint fingerprint,
        ElasticMaterializationTargetBindingId id,
        ElasticClusterId cluster,
        MaterializationTargetId targetId,
        MaterializationId materializationId,
        string readAlias,
        string generationIndexPrefix,
        string controlIndexName,
        ElasticMaterializationIndexTemplateEvidence indexTemplate,
        ElasticMaterializationSingleWriterEvidence singleWriter,
        ElasticRelationQueryStorageBinding searchBinding)
        : this(
            id,
            cluster,
            targetId,
            materializationId,
            readAlias,
            generationIndexPrefix,
            controlIndexName,
            indexTemplate,
            singleWriter,
            searchBinding)
    {
        var persistedSchema = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(persistedSchema, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported Elasticsearch materialization-target binding schema '{persistedSchema}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(fingerprint);
        if (!Equals(fingerprint, Fingerprint))
        {
            throw new ArgumentException(
                "The persisted Elasticsearch materialization-target binding fingerprint does not match normalized content.",
                nameof(fingerprint));
        }
    }

    /// <summary>Gets the current persisted binding schema.</summary>
    public string SchemaVersion => CurrentSchemaVersion;

    /// <summary>Gets the stable versioned binding identity.</summary>
    public ElasticMaterializationTargetBindingId Id { get; }

    /// <summary>Gets the persisted Elasticsearch cluster identity.</summary>
    public ElasticClusterId Cluster { get; }

    /// <summary>Gets the canonical materialization target identity.</summary>
    public MaterializationTargetId TargetId { get; }

    /// <summary>Gets the logical materialization stored by this target.</summary>
    public MaterializationId MaterializationId { get; }

    /// <summary>Gets the stable alias used by canonical Relations reads.</summary>
    public string ReadAlias { get; }

    /// <summary>Gets the lowercase physical prefix used for isolated generation indexes.</summary>
    public string GenerationIndexPrefix { get; }

    /// <summary>Gets the concrete index retaining target coordination state.</summary>
    public string ControlIndexName { get; }

    /// <summary>Gets the exact externally managed generation-template evidence.</summary>
    public ElasticMaterializationIndexTemplateEvidence IndexTemplate { get; }

    /// <summary>Gets the external single-writer coordination evidence constraining generation mutations.</summary>
    public ElasticMaterializationSingleWriterEvidence SingleWriter { get; }

    /// <summary>
    /// Gets the exact canonical Relations binding addressed to <see cref="ReadAlias"/> and rooted in
    /// <see cref="ValueField"/>.
    /// </summary>
    public ElasticRelationQueryStorageBinding SearchBinding { get; }

    /// <summary>Gets the deterministic fingerprint of all normalized binding facts.</summary>
    public ElasticMaterializationTargetBindingFingerprint Fingerprint { get; }

    /// <summary>Derives the immutable physical index identity for one canonical generation.</summary>
    /// <param name="generationId">Caller-assigned generation identity that is never reused for different intent.</param>
    /// <returns>A valid lowercase index identity consisting of the configured prefix and a full SHA-256 digest.</returns>
    /// <exception cref="ArgumentException"><paramref name="generationId"/> is default.</exception>
    public string GetGenerationIndexName(MaterializationGenerationId generationId)
    {
        RequireDefined(generationId.Value, nameof(generationId));
        StringBuilder canonical = new(256 + generationId.Value.Length);
        ElasticMaterializationTargetBindingFingerprinter.Append(canonical, CurrentSchemaVersion);
        ElasticMaterializationTargetBindingFingerprinter.Append(canonical, Fingerprint.Value);
        ElasticMaterializationTargetBindingFingerprinter.Append(canonical, generationId.Value);
        return GenerationIndexPrefix
               + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    static void RequireDefined(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "An Elasticsearch materialization-target binding requires non-default identities.",
                parameterName);
        }
    }

    static void RequireMaterializedValuePaths(ElasticRelationQueryStorageBinding searchBinding)
    {
        const string parameterName = nameof(searchBinding);
        foreach (var field in searchBinding.Fields)
        {
            RequireMaterializedValuePath(field.SourceField, "source", field.Input.Value, parameterName);
            RequireMaterializedValuePath(field.QueryField, "query", field.Input.Value, parameterName);
            RequireMaterializedValuePath(
                field.ReversedSuffixField,
                "reversed-suffix query",
                field.Input.Value,
                parameterName);
            if (field.NestedScope is not { } nested)
            {
                continue;
            }

            RequireMaterializedValuePath(nested.NestedPath, "nested-scope query", field.Input.Value, parameterName);
            foreach (var child in nested.ChildFields)
            {
                RequireMaterializedValuePath(
                    child.QueryField,
                    "nested-child query",
                    field.Input.Value,
                    parameterName);
            }
        }
    }

    static void RequireMaterializedValuePath(
        FieldPath? path,
        string role,
        string input,
        string parameterName)
    {
        if (path is not { } physicalPath)
        {
            return;
        }

        var segments = physicalPath.Segments;
        var isValueEnvelope = segments[0] is
        {
            Kind: SegmentKind.Field,
            Segment: ValueField
        };
        var targetsMetadataId = segments.Any(static segment =>
            segment is { Kind: SegmentKind.Field, Segment: "_id" });
        if (isValueEnvelope && !targetsMetadataId)
        {
            return;
        }

        throw new ArgumentException(
            $"Canonical Relations {role} path '{physicalPath}' for input '{input}' must remain inside the "
            + $"'{ValueField}' envelope and must not target Elasticsearch _id metadata.",
            parameterName);
    }
}

static class ElasticMaterializationTargetBindingFingerprinter
{
    internal static ElasticMaterializationTargetBindingFingerprint Compute(
        ElasticMaterializationTargetBinding binding)
    {
        StringBuilder canonical = new(1024);
        Append(canonical, ElasticMaterializationTargetBinding.CurrentSchemaVersion);
        Append(canonical, binding.Id.Value);
        Append(canonical, binding.Cluster.Value);
        Append(canonical, binding.TargetId.Value);
        Append(canonical, binding.MaterializationId.Value);
        Append(canonical, binding.ReadAlias);
        Append(canonical, binding.GenerationIndexPrefix);
        Append(canonical, binding.ControlIndexName);
        Append(canonical, binding.IndexTemplate.Name);
        Append(canonical, binding.IndexTemplate.Fingerprint.Algorithm);
        Append(canonical, binding.IndexTemplate.Fingerprint.Canonicalization);
        Append(canonical, binding.IndexTemplate.Fingerprint.Value);
        Append(canonical, binding.IndexTemplate.Authority);
        Append(canonical, binding.SingleWriter.Authority);
        Append(canonical, binding.SingleWriter.Scope);
        Append(canonical, binding.SearchBinding.SchemaVersion);
        Append(canonical, binding.SearchBinding.Fingerprint.Algorithm);
        Append(canonical, binding.SearchBinding.Fingerprint.Canonicalization);
        Append(canonical, binding.SearchBinding.Fingerprint.Value);
        return new(
            ElasticMaterializationTargetBinding.FingerprintAlgorithm,
            ElasticMaterializationTargetBinding.FingerprintCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));
    }

    internal static void Append(StringBuilder canonical, string value)
    {
        canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(value);
        canonical.Append(';');
    }
}

static class ElasticMaterializationBindingContract
{
    internal static string RequireLowerHex(string value, string parameterName)
    {
        var normalized = Guard.RequireNotNullOrWhiteSpace(value);
        if (normalized.Length % 2 != 0
            || normalized.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A fingerprint value must be lowercase hexadecimal bytes.", parameterName);
        }

        return normalized;
    }

    internal static string RequireAuthority(string authority, string parameterName)
    {
        var value = Guard.RequireNotNullOrWhiteSpace(authority);
        if (value.Length > 256 || value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('/' or '.' or '_' or '-' or ':' or '@')))
        {
            throw new ArgumentException(
                "An Elasticsearch authority must be a bounded ASCII provenance identity, not an endpoint or credential.",
                parameterName);
        }

        return value;
    }

    internal static string RequireTemplateName(string name, string parameterName)
    {
        var value = Guard.RequireNotNullOrWhiteSpace(name);
        if (value.Length > 255
            || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
            || value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException(
                "An Elasticsearch index-template identity must be bounded lowercase ASCII text using letters, digits, '.', '_', or '-'.",
                parameterName);
        }

        return value;
    }

    internal static string RequireGenerationPrefix(
        string prefix,
        int digestCharacters,
        string parameterName)
    {
        var value = Guard.RequireNotNullOrWhiteSpace(prefix);
        if (!value.EndsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("A generation-index prefix must end with '-'.", parameterName);
        }

        _ = ElasticRelationQueryStorageBinding.RequireConcreteIndexName(value + "0", parameterName);
        if (Encoding.UTF8.GetByteCount(value) + digestCharacters > 255)
        {
            throw new ArgumentException(
                "A generation-index prefix must leave room for the complete immutable generation digest.",
                parameterName);
        }

        return value;
    }
}
