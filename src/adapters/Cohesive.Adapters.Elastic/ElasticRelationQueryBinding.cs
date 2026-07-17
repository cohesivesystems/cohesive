using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.Elastic;

/// <summary>Stable identity of a versioned Elasticsearch relation/query storage binding.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ElasticRelationQueryBindingId
{
    /// <summary>Creates an Elasticsearch storage-binding identity.</summary>
    /// <param name="value">Stable versioned identity value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public ElasticRelationQueryBindingId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Stable versioned identity value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of one normalized Elasticsearch storage binding.</summary>
public sealed record ElasticRelationQueryBindingFingerprint
{
    /// <summary>Creates a binding fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    public ElasticRelationQueryBindingFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>Origin of an Elasticsearch storage-binding decision.</summary>
public enum ElasticRelationQueryBindingOrigin
{
    /// <summary>The binding was declared explicitly by the consumer.</summary>
    Explicit = 0,

    /// <summary>The binding was derived by a named deterministic convention set.</summary>
    Convention = 1
}

/// <summary>How Elasticsearch reconstructs the physical <c>_source</c> document.</summary>
public enum ElasticRelationQuerySourceMode
{
    /// <summary>A stored <c>_source</c> document is available.</summary>
    Enabled = 0,

    /// <summary>Elasticsearch reconstructs <c>_source</c> from indexed values.</summary>
    Synthetic = 1,

    /// <summary>The index does not expose <c>_source</c>.</summary>
    Disabled = 2
}

/// <summary>Physical Elasticsearch mapping family used by one semantic field.</summary>
public enum ElasticRelationQueryFieldMappingKind
{
    /// <summary>The value is available only in a retrievable document representation and is not indexed.</summary>
    Unindexed = 0,

    /// <summary>An exact, non-analyzed keyword field.</summary>
    Keyword = 1,

    /// <summary>An Elasticsearch wildcard field optimized for wildcard and regular-expression matching.</summary>
    Wildcard = 2,

    /// <summary>An analyzed text field.</summary>
    Text = 3,

    /// <summary>A Boolean field.</summary>
    Boolean = 4,

    /// <summary>A 32-bit integer field.</summary>
    Integer = 5,

    /// <summary>A 64-bit integer field.</summary>
    Long = 6,

    /// <summary>A single-precision floating-point field.</summary>
    Float = 7,

    /// <summary>A double-precision floating-point field.</summary>
    Double = 8,

    /// <summary>A scaled floating-point field.</summary>
    ScaledFloat = 9,

    /// <summary>A millisecond-oriented date field.</summary>
    Date = 10,

    /// <summary>A nanosecond-oriented date field.</summary>
    DateNanos = 11,

    /// <summary>A structurally flattened object field.</summary>
    Object = 12,

    /// <summary>A nested object field preserving per-element correlation.</summary>
    Nested = 13
}

/// <summary>Physical channel from which one result field is reconstructed.</summary>
public enum ElasticRelationQueryFieldRetrievalKind
{
    /// <summary>The value is read from the filtered <c>_source</c> document.</summary>
    Source = 0,

    /// <summary>The value is read from Elasticsearch doc values.</summary>
    DocValues = 1,

    /// <summary>The value is read from a separately stored field.</summary>
    StoredField = 2,

    /// <summary>The field is query-only and cannot be reconstructed as a result value.</summary>
    Unavailable = 3
}

/// <summary>Exact physical JSON encoding produced by one Elasticsearch result-retrieval channel.</summary>
public enum ElasticRelationQueryFieldValueEncoding
{
    /// <summary>The retrieval channel produces a JSON Boolean.</summary>
    JsonBoolean = 0,

    /// <summary>The retrieval channel produces a JSON integer in the canonical signed 64-bit domain.</summary>
    JsonInt64 = 1,

    /// <summary>The retrieval channel produces a JSON string without target-side normalization.</summary>
    JsonString = 2,

    /// <summary>The retrieval channel produces the canonical temporal representation as a JSON string.</summary>
    CanonicalTemporalString = 3
}

/// <summary>Physical root-versus-nested document scope governing exact retrieval and querying.</summary>
public enum ElasticRelationQueryFieldDocumentScope
{
    /// <summary>The binding does not attest whether the field is rooted or nested.</summary>
    Unproven = 0,

    /// <summary>The field is queryable directly in the root Elasticsearch document.</summary>
    RootDocument = 1,

    /// <summary>The field resides in a nested document and requires nested-query correlation.</summary>
    NestedDocument = 2
}

/// <summary>Index-consistency evidence available to multi-request Elasticsearch pagination.</summary>
public enum ElasticRelationQueryPaginationConsistency
{
    /// <summary>No snapshot or immutability guarantee is attested.</summary>
    Unproven = 0,

    /// <summary>
    /// The binding attests one unchanged search-visible document set and ordering for the complete logical page
    /// sequence, including completed refreshes and no intervening writes, deletes, refresh visibility changes, or
    /// target reassignment.
    /// </summary>
    StableSearchView = 1
}

/// <summary>Physical handling of a missing semantic field.</summary>
public enum ElasticRelationQueryMissingValueBehavior
{
    /// <summary>The missing value has no indexed term.</summary>
    NotIndexed = 0,

    /// <summary>Ingestion writes an explicit reserved scalar term for the missing value.</summary>
    IndexedSentinel = 1
}

/// <summary>Physical handling of an explicit semantic null.</summary>
public enum ElasticRelationQueryNullValueBehavior
{
    /// <summary>The source contains JSON null and the field has no indexed term.</summary>
    JsonNullNotIndexed = 0,

    /// <summary>The mapping indexes an explicit reserved scalar through its <c>null_value</c> behavior.</summary>
    IndexedSentinel = 1
}

/// <summary>Exact semantic facilities asserted for one Elasticsearch field mapping.</summary>
[Flags]
public enum ElasticRelationQueryFieldSemanticCapabilities
{
    /// <summary>No exact query facility is asserted.</summary>
    None = 0,

    /// <summary>A term query preserves canonical scalar equality for the field.</summary>
    ExactTerm = 1 << 0,

    /// <summary>A range query preserves canonical scalar ordering comparisons for the field.</summary>
    ExactRange = 1 << 1,

    /// <summary>A field sort preserves canonical value ordering.</summary>
    ExactOrdering = 1 << 2,

    /// <summary>The field is a stable unique final ordering key.</summary>
    StableUniqueOrdering = 1 << 3,

    /// <summary>Metric or bucket aggregation over the field preserves the declared canonical semantics.</summary>
    ExactAggregation = 1 << 4,

    /// <summary>
    /// A leading-wildcard query is executable under the bound index and cluster settings and preserves ordinal
    /// canonical suffix semantics.
    /// </summary>
    WildcardSuffix = 1 << 5,

    /// <summary>
    /// A prefix query over <see cref="ElasticRelationQueryFieldBinding.ReversedSuffixField"/> is executable under
    /// the bound index and cluster settings and preserves ordinal suffix semantics.
    /// </summary>
    ReversedPrefixSuffix = 1 << 6
}

/// <summary>Physical Elasticsearch evidence for one exact compiled semantic field input.</summary>
public sealed record ElasticRelationQueryFieldBinding
{
    const ElasticRelationQueryFieldSemanticCapabilities AllSemanticCapabilities =
        ElasticRelationQueryFieldSemanticCapabilities.ExactTerm
        | ElasticRelationQueryFieldSemanticCapabilities.ExactRange
        | ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
        | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering
        | ElasticRelationQueryFieldSemanticCapabilities.ExactAggregation
        | ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix
        | ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix;

    /// <summary>Creates one compiled-input-to-Elasticsearch-field binding.</summary>
    /// <param name="input">Exact compiled field-input identity.</param>
    /// <param name="sourceField">Physical path in <c>_source</c>, or <see langword="null"/> when unavailable.</param>
    /// <param name="queryField">Physical indexed field or multifield path, or <see langword="null"/> when unindexed.</param>
    /// <param name="mappingKind">Physical Elasticsearch mapping family.</param>
    /// <param name="retrievalKind">Channel used to reconstruct the result value.</param>
    /// <param name="retrievalEncoding">
    /// Exact JSON encoding produced by the retrieval channel, or <see langword="null"/> when retrieval is unavailable.
    /// </param>
    /// <param name="documentScope">Physical root-versus-nested document scope shared by retrieval and querying.</param>
    /// <param name="semanticCapabilities">Exact semantic facilities attested by the binding.</param>
    /// <param name="reversedSuffixField">Optional keyword field containing the configured reversed representation.</param>
    /// <param name="semanticProfile">
    /// Stable mapping, normalizer, collation, precision, transform, and relevant cluster query-setting profile
    /// supporting the asserted capabilities.
    /// </param>
    /// <param name="missingValueBehavior">Physical handling of a missing field.</param>
    /// <param name="missingValueSentinel">Reserved indexed scalar used for missing, when one is declared.</param>
    /// <param name="nullValueBehavior">Physical handling of explicit null.</param>
    /// <param name="nullValueSentinel">Reserved indexed scalar used for null, when one is declared.</param>
    /// <exception cref="ArgumentException">
    /// An identity, path, profile, sentinel, mapping, retrieval, or capability combination is inconsistent.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value or capability flag is unsupported.</exception>
    public ElasticRelationQueryFieldBinding(
        RelationQueryInputId input,
        FieldPath? sourceField,
        FieldPath? queryField,
        ElasticRelationQueryFieldMappingKind mappingKind,
        ElasticRelationQueryFieldRetrievalKind retrievalKind,
        ElasticRelationQueryFieldValueEncoding? retrievalEncoding,
        ElasticRelationQueryFieldDocumentScope documentScope = ElasticRelationQueryFieldDocumentScope.Unproven,
        ElasticRelationQueryFieldSemanticCapabilities semanticCapabilities = ElasticRelationQueryFieldSemanticCapabilities.None,
        FieldPath? reversedSuffixField = null,
        string? semanticProfile = null,
        ElasticRelationQueryMissingValueBehavior missingValueBehavior = ElasticRelationQueryMissingValueBehavior.NotIndexed,
        ObservationValue? missingValueSentinel = null,
        ElasticRelationQueryNullValueBehavior nullValueBehavior = ElasticRelationQueryNullValueBehavior.JsonNullNotIndexed,
        ObservationValue? nullValueSentinel = null)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("An Elasticsearch field binding requires a compiled input identity.", nameof(input));
        if (!Enum.IsDefined(mappingKind))
            throw new ArgumentOutOfRangeException(nameof(mappingKind), mappingKind, "Unsupported Elasticsearch mapping kind.");
        if (!Enum.IsDefined(retrievalKind))
            throw new ArgumentOutOfRangeException(nameof(retrievalKind), retrievalKind, "Unsupported Elasticsearch retrieval kind.");
        if (retrievalEncoding is { } encoding && !Enum.IsDefined(encoding))
            throw new ArgumentOutOfRangeException(nameof(retrievalEncoding), encoding, "Unsupported Elasticsearch retrieval encoding.");
        if (!Enum.IsDefined(documentScope))
            throw new ArgumentOutOfRangeException(nameof(documentScope), documentScope, "Unsupported Elasticsearch document scope.");
        if (!Enum.IsDefined(missingValueBehavior))
            throw new ArgumentOutOfRangeException(nameof(missingValueBehavior), missingValueBehavior, "Unsupported Elasticsearch missing-value behavior.");
        if (!Enum.IsDefined(nullValueBehavior))
            throw new ArgumentOutOfRangeException(nameof(nullValueBehavior), nullValueBehavior, "Unsupported Elasticsearch null-value behavior.");
        if ((semanticCapabilities & ~AllSemanticCapabilities) != 0)
            throw new ArgumentOutOfRangeException(nameof(semanticCapabilities), semanticCapabilities, "Unsupported Elasticsearch field capability flag.");

        FieldPath? normalizedSourceField = sourceField is { } source
            ? ElasticRelationQueryStorageBinding.RequirePhysicalFieldPath(source, nameof(sourceField))
            : null;
        FieldPath? normalizedQueryField = queryField is { } query
            ? ElasticRelationQueryStorageBinding.RequirePhysicalFieldPath(query, nameof(queryField))
            : null;
        FieldPath? normalizedReversedSuffixField = reversedSuffixField is { } reversed
            ? ElasticRelationQueryStorageBinding.RequirePhysicalFieldPath(reversed, nameof(reversedSuffixField))
            : null;

        if (normalizedSourceField is null && normalizedQueryField is null)
            throw new ArgumentException("An Elasticsearch field binding requires a source or indexed field path.", nameof(sourceField));
        if (mappingKind == ElasticRelationQueryFieldMappingKind.Unindexed && normalizedQueryField is not null)
            throw new ArgumentException("An unindexed Elasticsearch field cannot declare an indexed query path.", nameof(queryField));
        if (mappingKind != ElasticRelationQueryFieldMappingKind.Unindexed && normalizedQueryField is null)
            throw new ArgumentException("An indexed Elasticsearch mapping requires its physical query field.", nameof(queryField));
        if (retrievalKind == ElasticRelationQueryFieldRetrievalKind.Source && normalizedSourceField is null)
            throw new ArgumentException("Source retrieval requires a physical _source field.", nameof(sourceField));
        if (retrievalKind is ElasticRelationQueryFieldRetrievalKind.DocValues or ElasticRelationQueryFieldRetrievalKind.StoredField
            && normalizedQueryField is null)
        {
            throw new ArgumentException("Doc-value and stored-field retrieval require an indexed field path.", nameof(queryField));
        }
        if ((retrievalKind == ElasticRelationQueryFieldRetrievalKind.Unavailable) != (retrievalEncoding is null))
        {
            throw new ArgumentException(
                "A retrievable Elasticsearch field and its exact physical value encoding must be declared together.",
                nameof(retrievalEncoding));
        }
        if (semanticCapabilities != ElasticRelationQueryFieldSemanticCapabilities.None
            && normalizedQueryField is null)
        {
            throw new ArgumentException("Exact query capabilities require an indexed query field.", nameof(semanticCapabilities));
        }
        if (semanticCapabilities != ElasticRelationQueryFieldSemanticCapabilities.None
            && documentScope == ElasticRelationQueryFieldDocumentScope.Unproven)
        {
            throw new ArgumentException(
                "Exact query capabilities require attributable root-versus-nested field-scope evidence.",
                nameof(documentScope));
        }
        if (semanticCapabilities != ElasticRelationQueryFieldSemanticCapabilities.None
            && string.IsNullOrWhiteSpace(semanticProfile))
        {
            throw new ArgumentException("Exact query capabilities require an attributable semantic profile.", nameof(semanticProfile));
        }
        if (semanticProfile is not null && string.IsNullOrWhiteSpace(semanticProfile))
            throw new ArgumentException("An Elasticsearch semantic profile cannot be empty.", nameof(semanticProfile));
        if (semanticCapabilities.HasFlag(ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering)
            && !semanticCapabilities.HasFlag(ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering))
        {
            throw new ArgumentException("A stable unique ordering field must also assert exact canonical ordering.", nameof(semanticCapabilities));
        }

        var suffixCapabilities = semanticCapabilities
                                 & (ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix
                                    | ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix);
        if (suffixCapabilities != ElasticRelationQueryFieldSemanticCapabilities.None
            && mappingKind is not (ElasticRelationQueryFieldMappingKind.Keyword or ElasticRelationQueryFieldMappingKind.Wildcard))
        {
            throw new ArgumentException("Exact suffix strategies require a keyword or wildcard mapping.", nameof(mappingKind));
        }
        if (semanticCapabilities.HasFlag(ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix)
            != (normalizedReversedSuffixField is not null))
        {
            throw new ArgumentException(
                "A reversed suffix field and its exact reversed-prefix capability must be declared together.",
                nameof(reversedSuffixField));
        }
        if (mappingKind is ElasticRelationQueryFieldMappingKind.Object or ElasticRelationQueryFieldMappingKind.Nested
            && semanticCapabilities != ElasticRelationQueryFieldSemanticCapabilities.None)
        {
            throw new ArgumentException("Object and nested mappings cannot assert scalar query capabilities.", nameof(semanticCapabilities));
        }

        if (IsMetadataId(normalizedSourceField))
            throw new ArgumentException("Elasticsearch _id metadata is not retrievable through _source.", nameof(sourceField));
        var metadataIdUnsupportedCapabilities = ElasticRelationQueryFieldSemanticCapabilities.ExactRange
                                                | ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                                                | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering
                                                | ElasticRelationQueryFieldSemanticCapabilities.ExactAggregation
                                                | ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix
                                                | ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix;
        if (IsMetadataId(normalizedQueryField)
            && (semanticCapabilities & metadataIdUnsupportedCapabilities) != 0)
        {
            throw new ArgumentException(
                "Elasticsearch _id metadata cannot be sorted, ranged, aggregated, or used for suffix matching.",
                nameof(semanticCapabilities));
        }

        ValidateSentinel(missingValueBehavior == ElasticRelationQueryMissingValueBehavior.IndexedSentinel,
            missingValueSentinel, nameof(missingValueSentinel));
        ValidateSentinel(nullValueBehavior == ElasticRelationQueryNullValueBehavior.IndexedSentinel,
            nullValueSentinel, nameof(nullValueSentinel));
        if (missingValueSentinel is { } missingSentinel
            && nullValueSentinel is { } nullSentinel
            && missingSentinel == nullSentinel)
        {
            throw new ArgumentException("Missing and null sentinels must remain distinct.", nameof(nullValueSentinel));
        }

        Input = input;
        SourceField = normalizedSourceField;
        QueryField = normalizedQueryField;
        MappingKind = mappingKind;
        RetrievalKind = retrievalKind;
        RetrievalEncoding = retrievalEncoding;
        DocumentScope = documentScope;
        SemanticCapabilities = semanticCapabilities;
        ReversedSuffixField = normalizedReversedSuffixField;
        SemanticProfile = semanticProfile;
        MissingValueBehavior = missingValueBehavior;
        MissingValueSentinel = missingValueSentinel;
        NullValueBehavior = nullValueBehavior;
        NullValueSentinel = nullValueSentinel;
    }

    /// <summary>Exact compiled field-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Physical path in <c>_source</c>, or <see langword="null"/>.</summary>
    public FieldPath? SourceField { get; }

    /// <summary>Physical indexed field or multifield path, or <see langword="null"/>.</summary>
    public FieldPath? QueryField { get; }

    /// <summary>Physical Elasticsearch mapping family.</summary>
    public ElasticRelationQueryFieldMappingKind MappingKind { get; }

    /// <summary>Physical result-value retrieval channel.</summary>
    public ElasticRelationQueryFieldRetrievalKind RetrievalKind { get; }

    /// <summary>Exact JSON encoding produced by the result-retrieval channel, or <see langword="null"/>.</summary>
    public ElasticRelationQueryFieldValueEncoding? RetrievalEncoding { get; }

    /// <summary>Physical root-versus-nested document scope shared by retrieval and querying.</summary>
    public ElasticRelationQueryFieldDocumentScope DocumentScope { get; }

    /// <summary>Exact semantic facilities attested by this field binding.</summary>
    public ElasticRelationQueryFieldSemanticCapabilities SemanticCapabilities { get; }

    /// <summary>Optional physical keyword field containing the configured reversed representation.</summary>
    public FieldPath? ReversedSuffixField { get; }

    /// <summary>Stable mapping, normalization, precision, transform, and query-setting profile identity.</summary>
    public string? SemanticProfile { get; }

    /// <summary>Physical missing-value handling.</summary>
    public ElasticRelationQueryMissingValueBehavior MissingValueBehavior { get; }

    /// <summary>Reserved indexed missing-value scalar, or <see langword="null"/>.</summary>
    public ObservationValue? MissingValueSentinel { get; }

    /// <summary>Physical explicit-null handling.</summary>
    public ElasticRelationQueryNullValueBehavior NullValueBehavior { get; }

    /// <summary>Reserved indexed null-value scalar, or <see langword="null"/>.</summary>
    public ObservationValue? NullValueSentinel { get; }

    static bool IsMetadataId(FieldPath? path) => path is { } value
        && value.Segments.Length == 1
        && value.Segments[0] is { Kind: SegmentKind.Field, Segment: "_id" };

    static void ValidateSentinel(bool required, ObservationValue? sentinel, string parameterName)
    {
        if (required != (sentinel is not null))
            throw new ArgumentException("An indexed sentinel behavior and its sentinel value must be declared together.", parameterName);
        if (sentinel is not { } value)
            return;
        if (value.Kind is ObservationValueKind.Undefined
            or ObservationValueKind.Null
            or ObservationValueKind.Bytes
            or ObservationValueKind.Array
            or ObservationValueKind.Object
            || value.Kind == ObservationValueKind.Double && !double.IsFinite(value.Double))
        {
            throw new ArgumentException("An Elasticsearch sentinel must be one finite, concrete scalar value.", parameterName);
        }
    }
}

/// <summary>
/// Immutable, versioned binding from one exact placed semantic source to one concrete Elasticsearch index mapping.
/// </summary>
public sealed class ElasticRelationQueryStorageBinding
{
    /// <summary>Portable binding schema understood by the canonical Elasticsearch v1 compiler.</summary>
    public const string CurrentSchemaVersion = "cohesive.relations.elastic-binding/v1";

    /// <summary>Default deterministic convention set for semantic-path Elasticsearch bindings.</summary>
    public const string SemanticPathConventionSet = "cohesive.relations.elastic/semantic-path-conventions/v1";

    /// <summary>Default Elasticsearch <c>index.max_result_window</c> represented by convention.</summary>
    public const int DefaultMaximumResultWindow = 10_000;

    /// <summary>Default semantic page-size boundary represented by the canonical v1 adapter.</summary>
    public const int DefaultMaximumPageSize = 1_000;

    /// <summary>Creates an explicit Elasticsearch storage binding.</summary>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="source">Physical source instance bound to the index.</param>
    /// <param name="placementBinding">Exact plan-scoped source-set placement interpreted by this binding.</param>
    /// <param name="target">Expected Elasticsearch interpretation-target identity.</param>
    /// <param name="targetProfile">Expected target capability-profile identity.</param>
    /// <param name="indexName">Concrete Elasticsearch index name.</param>
    /// <param name="fields">Exact compiled field-input mappings; may be empty for a fieldless query.</param>
    /// <param name="sourceMode">Physical <c>_source</c> behavior.</param>
    /// <param name="maximumResultWindow">Configured <c>index.max_result_window</c> used to validate offset paging.</param>
    /// <param name="maximumPageSize">Maximum semantic row-page size accepted by the adapter profile.</param>
    /// <param name="paginationConsistency">Consistency evidence available across multi-request pagination.</param>
    /// <param name="origin">Whether the binding was explicit or convention-derived.</param>
    /// <param name="conventionSetVersion">Attributable convention-set identity, when applicable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="indexName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity, index name, field collection, source mode, or convention attribution is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An enum value, result-window size, or page-size boundary is unsupported.
    /// </exception>
    public ElasticRelationQueryStorageBinding(
        ElasticRelationQueryBindingId id,
        RelationQuerySourceInstanceId source,
        RelationQuerySourcePlacementBindingId placementBinding,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        string indexName,
        ImmutableArray<ElasticRelationQueryFieldBinding> fields,
        ElasticRelationQuerySourceMode sourceMode = ElasticRelationQuerySourceMode.Enabled,
        int maximumResultWindow = DefaultMaximumResultWindow,
        int maximumPageSize = DefaultMaximumPageSize,
        ElasticRelationQueryPaginationConsistency paginationConsistency = ElasticRelationQueryPaginationConsistency.Unproven,
        ElasticRelationQueryBindingOrigin origin = ElasticRelationQueryBindingOrigin.Explicit,
        string? conventionSetVersion = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value) || string.IsNullOrWhiteSpace(source.Value)
            || string.IsNullOrWhiteSpace(placementBinding.Value) || string.IsNullOrWhiteSpace(target.Value)
            || string.IsNullOrWhiteSpace(targetProfile.Value))
        {
            throw new ArgumentException("An Elasticsearch storage binding requires non-default identities.", nameof(id));
        }
        if (!Enum.IsDefined(sourceMode))
            throw new ArgumentOutOfRangeException(nameof(sourceMode), sourceMode, "Unsupported Elasticsearch source mode.");
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported Elasticsearch binding origin.");
        if (!Enum.IsDefined(paginationConsistency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(paginationConsistency),
                paginationConsistency,
                "Unsupported Elasticsearch pagination consistency.");
        }
        if (maximumResultWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumResultWindow), maximumResultWindow, "The maximum result window must be positive.");
        if (maximumPageSize <= 0 || maximumPageSize > maximumResultWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPageSize),
                maximumPageSize,
                "The maximum page size must be positive and cannot exceed the result window.");
        }
        if (origin == ElasticRelationQueryBindingOrigin.Convention && string.IsNullOrWhiteSpace(conventionSetVersion))
        {
            throw new ArgumentException(
                "A convention-derived Elasticsearch binding requires its convention-set identity.",
                nameof(conventionSetVersion));
        }
        if (conventionSetVersion is not null && string.IsNullOrWhiteSpace(conventionSetVersion))
            throw new ArgumentException("An Elasticsearch convention-set identity cannot be empty.", nameof(conventionSetVersion));

        var normalizedFields = fields.IsDefault ? [] : fields;
        if (normalizedFields.Any(static field => field is null))
            throw new ArgumentException("An Elasticsearch storage binding cannot contain a null field binding.", nameof(fields));
        if (normalizedFields.GroupBy(static field => field.Input).Any(static group => group.Count() > 1))
            throw new ArgumentException("An Elasticsearch storage binding cannot repeat a compiled field input.", nameof(fields));
        if (sourceMode == ElasticRelationQuerySourceMode.Disabled
            && normalizedFields.Any(static field => field.RetrievalKind == ElasticRelationQueryFieldRetrievalKind.Source))
        {
            throw new ArgumentException("Source retrieval cannot be used when Elasticsearch _source is disabled.", nameof(sourceMode));
        }

        Id = id;
        Source = source;
        PlacementBinding = placementBinding;
        Target = target;
        TargetProfile = targetProfile;
        IndexName = RequireConcreteIndexName(indexName, nameof(indexName));
        Fields = [.. normalizedFields.OrderBy(static field => field.Input.Value, StringComparer.Ordinal)];
        SourceMode = sourceMode;
        MaximumResultWindow = maximumResultWindow;
        MaximumPageSize = maximumPageSize;
        PaginationConsistency = paginationConsistency;
        Origin = origin;
        ConventionSetVersion = conventionSetVersion;
        Fingerprint = ElasticRelationQueryBindingFingerprinter.Compute(this);
    }

    /// <summary>Rehydrates and verifies a persisted Elasticsearch storage binding.</summary>
    /// <param name="schemaVersion">Persisted binding schema version.</param>
    /// <param name="fingerprint">Persisted fingerprint expected to match normalized binding facts.</param>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="source">Physical source instance bound to the index.</param>
    /// <param name="placementBinding">Exact plan-scoped source-set placement interpreted by this binding.</param>
    /// <param name="target">Expected Elasticsearch interpretation-target identity.</param>
    /// <param name="targetProfile">Expected target capability-profile identity.</param>
    /// <param name="indexName">Concrete Elasticsearch index name.</param>
    /// <param name="fields">Exact compiled field-input mappings; may be empty for a fieldless query.</param>
    /// <param name="sourceMode">Physical <c>_source</c> behavior.</param>
    /// <param name="maximumResultWindow">Configured <c>index.max_result_window</c>.</param>
    /// <param name="maximumPageSize">Maximum semantic row-page size.</param>
    /// <param name="paginationConsistency">Consistency evidence available across multi-request pagination.</param>
    /// <param name="origin">Whether the binding was explicit or convention-derived.</param>
    /// <param name="conventionSetVersion">Attributable convention-set identity, when applicable.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="fingerprint"/>, or another required value is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema version or fingerprint is stale, or another normalized binding invariant is violated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum or numeric boundary is unsupported.</exception>
    [JsonConstructor]
    public ElasticRelationQueryStorageBinding(
        string schemaVersion,
        ElasticRelationQueryBindingFingerprint fingerprint,
        ElasticRelationQueryBindingId id,
        RelationQuerySourceInstanceId source,
        RelationQuerySourcePlacementBindingId placementBinding,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        string indexName,
        ImmutableArray<ElasticRelationQueryFieldBinding> fields,
        ElasticRelationQuerySourceMode sourceMode = ElasticRelationQuerySourceMode.Enabled,
        int maximumResultWindow = DefaultMaximumResultWindow,
        int maximumPageSize = DefaultMaximumPageSize,
        ElasticRelationQueryPaginationConsistency paginationConsistency = ElasticRelationQueryPaginationConsistency.Unproven,
        ElasticRelationQueryBindingOrigin origin = ElasticRelationQueryBindingOrigin.Explicit,
        string? conventionSetVersion = null)
        : this(
            id,
            source,
            placementBinding,
            target,
            targetProfile,
            indexName,
            fields,
            sourceMode,
            maximumResultWindow,
            maximumPageSize,
            paginationConsistency,
            origin,
            conventionSetVersion)
    {
        var persistedSchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(persistedSchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported Elasticsearch relation/query storage-binding schema version '{persistedSchemaVersion}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(fingerprint);
        if (!Equals(fingerprint, Fingerprint))
        {
            throw new ArgumentException(
                "The Elasticsearch relation/query storage-binding fingerprint does not match normalized content.",
                nameof(fingerprint));
        }
    }

    /// <summary>Binding schema version.</summary>
    public string SchemaVersion => CurrentSchemaVersion;

    /// <summary>Stable versioned binding identity.</summary>
    public ElasticRelationQueryBindingId Id { get; }

    /// <summary>Physical source instance bound to the index.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Exact plan-scoped source-set placement interpreted by this binding.</summary>
    public RelationQuerySourcePlacementBindingId PlacementBinding { get; }

    /// <summary>Expected Elasticsearch interpretation-target identity.</summary>
    public RelationQueryTargetId Target { get; }

    /// <summary>Expected Elasticsearch target capability-profile identity.</summary>
    public RelationQueryTargetProfileId TargetProfile { get; }

    /// <summary>Concrete physical Elasticsearch index name.</summary>
    public string IndexName { get; }

    /// <summary>Exact compiled field-input mappings in stable input-identity order.</summary>
    public ImmutableArray<ElasticRelationQueryFieldBinding> Fields { get; }

    /// <summary>Physical <c>_source</c> behavior.</summary>
    public ElasticRelationQuerySourceMode SourceMode { get; }

    /// <summary>Configured <c>index.max_result_window</c> used to validate offset pages.</summary>
    public int MaximumResultWindow { get; }

    /// <summary>Maximum semantic row-page size accepted by this binding.</summary>
    public int MaximumPageSize { get; }

    /// <summary>Consistency evidence available across multi-request pagination.</summary>
    public ElasticRelationQueryPaginationConsistency PaginationConsistency { get; }

    /// <summary>Whether this binding was explicit or convention-derived.</summary>
    public ElasticRelationQueryBindingOrigin Origin { get; }

    /// <summary>Attributable convention-set identity, or <see langword="null"/>.</summary>
    public string? ConventionSetVersion { get; }

    /// <summary>Deterministic identity of every normalized binding fact.</summary>
    public ElasticRelationQueryBindingFingerprint Fingerprint { get; }

    /// <summary>Resolves all physical evidence for one exact compiled field input.</summary>
    /// <param name="input">Compiled field-input identity.</param>
    /// <returns>The exact normalized field binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is not bound.</exception>
    public ElasticRelationQueryFieldBinding ResolveField(RelationQueryInputId input)
    {
        foreach (var field in Fields)
        {
            if (field.Input == input)
                return field;
        }
        throw new KeyNotFoundException($"Compiled input '{input.Value}' has no Elasticsearch field binding.");
    }

    internal static FieldPath RequirePhysicalFieldPath(FieldPath path, string parameterName)
    {
        if (path.Segments.IsDefaultOrEmpty
            || path.Segments.Any(static segment =>
                segment.Kind != SegmentKind.Field || string.IsNullOrEmpty(segment.Segment)))
        {
            throw new ArgumentException(
                "An Elasticsearch physical field path requires non-empty property segments and no element segments.",
                parameterName);
        }
        return path;
    }

    internal static string FieldPathKey(FieldPath path) => string.Join(
        '\u001f',
        path.Segments.Select(static segment => string.Concat(
            ((int)segment.Kind).ToString(CultureInfo.InvariantCulture),
            ":",
            (segment.Segment?.Length ?? -1).ToString(CultureInfo.InvariantCulture),
            ":",
            segment.Segment)));

    static string RequireConcreteIndexName(string value, string parameterName)
    {
        var index = Guard.RequireNotNullOrWhiteSpace(value);
        if (index is "." or ".."
            || index[0] is '_' or '-' or '+'
            || !string.Equals(index, index.ToLowerInvariant(), StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(index) > 255
            || index.Any(static character => char.IsWhiteSpace(character)
                || character is '\\' or '/' or '*' or '?' or '"' or '<' or '>' or '|' or ',' or '#' or ':'))
        {
            throw new ArgumentException(
                "The Elasticsearch binding requires one valid concrete lowercase index name without wildcard syntax.",
                parameterName);
        }
        return index;
    }
}

static class ElasticRelationQueryBindingFingerprinter
{
    const string Algorithm = "sha256";
    const string Canonicalization = "cohesive.relations.elastic-binding/v1-c14n/v1";

    public static ElasticRelationQueryBindingFingerprint Compute(ElasticRelationQueryStorageBinding binding)
    {
        StringBuilder canonical = new();
        Append(canonical, Canonicalization);
        Append(canonical, binding.SchemaVersion);
        Append(canonical, binding.Id.Value);
        Append(canonical, binding.Source.Value);
        Append(canonical, binding.PlacementBinding.Value);
        Append(canonical, binding.Target.Value);
        Append(canonical, binding.TargetProfile.Value);
        Append(canonical, binding.IndexName);
        Append(canonical, (int)binding.SourceMode);
        Append(canonical, binding.MaximumResultWindow);
        Append(canonical, binding.MaximumPageSize);
        Append(canonical, (int)binding.PaginationConsistency);
        Append(canonical, (int)binding.Origin);
        Append(canonical, binding.ConventionSetVersion);
        Append(canonical, binding.Fields.Length);
        foreach (var field in binding.Fields)
        {
            Append(canonical, field.Input.Value);
            Append(canonical, field.SourceField);
            Append(canonical, field.QueryField);
            Append(canonical, (int)field.MappingKind);
            Append(canonical, (int)field.RetrievalKind);
            Append(canonical, field.RetrievalEncoding is null ? -1 : (int)field.RetrievalEncoding.Value);
            Append(canonical, (int)field.DocumentScope);
            Append(canonical, (int)field.SemanticCapabilities);
            Append(canonical, field.ReversedSuffixField);
            Append(canonical, field.SemanticProfile);
            Append(canonical, (int)field.MissingValueBehavior);
            Append(canonical, field.MissingValueSentinel);
            Append(canonical, (int)field.NullValueBehavior);
            Append(canonical, field.NullValueSentinel);
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new(Algorithm, Canonicalization, Convert.ToHexStringLower(bytes));
    }

    static void Append(StringBuilder builder, string? value)
    {
        builder
            .Append(value?.Length ?? -1)
            .Append(':')
            .Append(value)
            .Append(';');
    }

    static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));

    static void Append(StringBuilder builder, long value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));

    static void Append(StringBuilder builder, FieldPath? path)
    {
        if (path is null)
        {
            Append(builder, (string?)null);
            return;
        }
        Append(builder, path.Value.Segments.Length);
        foreach (var segment in path.Value.Segments)
        {
            Append(builder, (int)segment.Kind);
            Append(builder, segment.Segment);
        }
    }

    static void Append(StringBuilder builder, ObservationValue? value)
    {
        if (value is null)
        {
            Append(builder, (string?)null);
            return;
        }

        Append(builder, (int)value.Value.Kind);
        switch (value.Value.Kind)
        {
            case ObservationValueKind.Int64:
                Append(builder, value.Value.Int64);
                break;
            case ObservationValueKind.Double:
                Append(builder, value.Value.Double.ToString("R", CultureInfo.InvariantCulture));
                break;
            case ObservationValueKind.Bool:
                Append(builder, value.Value.Bool ? 1 : 0);
                break;
            case ObservationValueKind.String:
            case ObservationValueKind.DateTimeOffset:
            case ObservationValueKind.DateOnly:
            case ObservationValueKind.TimeOnly:
            case ObservationValueKind.TimeSpan:
                Append(builder, value.Value.String);
                break;
            default:
                throw new InvalidOperationException(
                    $"Observation value kind '{value.Value.Kind}' is not a normalized Elasticsearch sentinel.");
        }
    }
}
