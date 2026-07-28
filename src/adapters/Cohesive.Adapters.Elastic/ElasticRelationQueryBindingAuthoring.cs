using System.Collections.Immutable;
using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;

namespace Cohesive.Adapters.Elastic;

/// <summary>Stable diagnostic codes emitted while authoring Elasticsearch relation/query storage bindings.</summary>
public static class ElasticRelationQueryBindingAuthoringDiagnosticCodes
{
    /// <summary>The placed input is stale, malformed, or does not use the exact Elasticsearch target profile.</summary>
    public const string PlacementMismatch = "relationQuery.authoring.elastic.placementMismatch";

    /// <summary>A required physical field or setting has no effective value.</summary>
    public const string BindingMissing = "relationQuery.authoring.elastic.bindingMissing";

    /// <summary>A semantic selector does not identify a demanded field on the selected placed input.</summary>
    public const string FieldUnknown = "relationQuery.authoring.elastic.fieldUnknown";

    /// <summary>An explicit local declaration repeats a field or evidence setting.</summary>
    public const string BindingDuplicate = "relationQuery.authoring.elastic.bindingDuplicate";

    /// <summary>A typed or structural selector cannot be interpreted as the requested path category.</summary>
    public const string SelectorInvalid = "relationQuery.authoring.elastic.selectorInvalid";

    /// <summary>A mapping, encoding, capability, or override conflicts with another effective physical fact.</summary>
    public const string ConfigurationConflict = "relationQuery.authoring.elastic.configurationConflict";

    /// <summary>The normalized effective facts could not construct the immutable storage-binding artifact.</summary>
    public const string ArtifactInvalid = "relationQuery.authoring.elastic.artifactInvalid";
}

/// <summary>Convention used for demanded fields without an explicit Elasticsearch physical mapping.</summary>
public enum ElasticRelationQueryFieldMappingConvention
{
    /// <summary>Map each otherwise-unmapped field to a source-only path equal to its semantic path.</summary>
    SemanticPath = 0,

    /// <summary>Require an explicit physical mapping for every demanded field.</summary>
    Explicit = 1
}

/// <summary>Scoped Elasticsearch binding-authoring values applied between adapter conventions and local declarations.</summary>
/// <remarks>
/// The constructor validates only the profile <see cref="Authority"/>. Other supplied values are retained verbatim so
/// <see cref="ElasticRelationQueryStorageBindingBuilder.Build"/> can report invalid identities, names, enum values,
/// ranges, and cross-setting constraints as structured authoring diagnostics.
/// </remarks>
public sealed class ElasticRelationQueryBindingAuthoringOptions
{
    /// <summary>Creates a named immutable scoped Elasticsearch authoring profile.</summary>
    /// <param name="authority">Stable profile identity and version attributed to every supplied option.</param>
    /// <param name="bindingId">Optional non-default scoped storage-binding identity, validated when the builder is built.</param>
    /// <param name="indexName">Optional concrete lowercase Elasticsearch index name, validated when the builder is built.</param>
    /// <param name="sourceMode">Optional defined physical <c>_source</c> mode, validated when the builder is built.</param>
    /// <param name="maximumResultWindow">Optional positive configured <c>index.max_result_window</c>, validated when the builder is built.</param>
    /// <param name="maximumPageSize">
    /// Optional positive semantic page-size boundary no greater than the effective result window, validated when the
    /// builder is built.
    /// </param>
    /// <param name="paginationConsistency">Optional defined cross-request pagination-consistency evidence, validated when the builder is built.</param>
    /// <param name="fieldMappingConvention">
    /// Defined convention for otherwise-unmapped demanded fields; <see langword="null"/> keeps the adapter convention.
    /// The value is validated when the builder is built.
    /// </param>
    /// <param name="conventionSetVersion">
    /// Optional nonempty convention-set attribution override, validated when the builder is built.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="authority"/> is empty or white space.</exception>
    public ElasticRelationQueryBindingAuthoringOptions(
        string authority,
        ElasticRelationQueryBindingId? bindingId = null,
        string? indexName = null,
        ElasticRelationQuerySourceMode? sourceMode = null,
        int? maximumResultWindow = null,
        int? maximumPageSize = null,
        ElasticRelationQueryPaginationConsistency? paginationConsistency = null,
        ElasticRelationQueryFieldMappingConvention? fieldMappingConvention = null,
        string? conventionSetVersion = null)
    {
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        BindingId = bindingId;
        IndexName = indexName;
        SourceMode = sourceMode;
        MaximumResultWindow = maximumResultWindow;
        MaximumPageSize = maximumPageSize;
        PaginationConsistency = paginationConsistency;
        FieldMappingConvention = fieldMappingConvention;
        ConventionSetVersion = conventionSetVersion;
    }

    /// <summary>Stable scoped-profile identity and version.</summary>
    public string Authority { get; }

    /// <summary>Optional scoped storage-binding identity.</summary>
    public ElasticRelationQueryBindingId? BindingId { get; }

    /// <summary>Optional scoped concrete Elasticsearch index name.</summary>
    public string? IndexName { get; }

    /// <summary>Optional scoped physical <c>_source</c> mode.</summary>
    public ElasticRelationQuerySourceMode? SourceMode { get; }

    /// <summary>Optional scoped <c>index.max_result_window</c>.</summary>
    public int? MaximumResultWindow { get; }

    /// <summary>Optional scoped semantic page-size boundary.</summary>
    public int? MaximumPageSize { get; }

    /// <summary>Optional scoped cross-request pagination-consistency evidence.</summary>
    public ElasticRelationQueryPaginationConsistency? PaginationConsistency { get; }

    /// <summary>Optional scoped field-mapping convention.</summary>
    public ElasticRelationQueryFieldMappingConvention? FieldMappingConvention { get; }

    /// <summary>Optional scoped convention-set attribution.</summary>
    public string? ConventionSetVersion { get; }
}

/// <summary>Adapter-owned entry point for authoring Elasticsearch bindings from exact placed inputs.</summary>
public static class ElasticRelationQueryBinding
{
    /// <summary>Stable authority used for explicit local declarations when no consumer authority is supplied.</summary>
    public const string LocalDeclarationAuthority = "cohesive.relations.authoring/local/v1";

    /// <summary>Starts Elasticsearch binding authoring for one exact plan-bound placed input.</summary>
    /// <param name="placedInput">Plan-bound source placement to bind to an Elasticsearch index.</param>
    /// <param name="options">Optional scoped authoring profile.</param>
    /// <param name="explicitAuthority">Stable authority attributed to explicit local declarations.</param>
    /// <returns>A mutable, session-local Elasticsearch storage-binding builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="placedInput"/> or <paramref name="explicitAuthority"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="explicitAuthority"/> is empty or white space.</exception>
    public static ElasticRelationQueryStorageBindingBuilder For(
        RelationQueryPlacedInput placedInput,
        ElasticRelationQueryBindingAuthoringOptions? options = null,
        string explicitAuthority = LocalDeclarationAuthority) =>
        new(placedInput, options, explicitAuthority);

    /// <summary>Starts typed Elasticsearch binding authoring for one exact CLR-backed placed input.</summary>
    /// <typeparam name="T">CLR type represented by the placed semantic shape.</typeparam>
    /// <param name="placedInput">Typed plan-bound source placement to bind to an Elasticsearch index.</param>
    /// <param name="options">Optional scoped authoring profile.</param>
    /// <param name="explicitAuthority">Stable authority attributed to explicit local declarations.</param>
    /// <returns>A typed, mutable, session-local Elasticsearch storage-binding builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="placedInput"/> or <paramref name="explicitAuthority"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="explicitAuthority"/> is empty or white space.</exception>
    public static ElasticRelationQueryStorageBindingBuilder<T> For<T>(
        RelationQueryPlacedInput<T> placedInput,
        ElasticRelationQueryBindingAuthoringOptions? options = null,
        string explicitAuthority = LocalDeclarationAuthority)
        where T : notnull =>
        new(placedInput, options, explicitAuthority);
}

/// <summary>Configures the physical Elasticsearch evidence for one exact demanded semantic field.</summary>
public sealed class ElasticRelationQueryFieldBindingBuilder
{
    readonly ElasticFieldDeclaration declaration;
    readonly string authority;

    internal ElasticRelationQueryFieldBindingBuilder(ElasticFieldDeclaration declaration, string authority)
    {
        this.declaration = declaration;
        this.authority = authority;
    }

    /// <summary>Declares root-document <c>_source</c> retrieval for this field.</summary>
    /// <param name="path">Physical <c>_source</c> property path.</param>
    /// <param name="encoding">Exact JSON encoding produced by retrieval.</param>
    /// <returns>This field builder.</returns>
    public ElasticRelationQueryFieldBindingBuilder Source(
        FieldPath path,
        ElasticRelationQueryFieldValueEncoding encoding)
    {
        if (declaration.TrySet("sourceField"))
        {
            declaration.SourceField = Explicit<FieldPath?>(path);
        }

        if (declaration.TrySet("retrievalKind"))
        {
            declaration.RetrievalKind = Explicit(ElasticRelationQueryFieldRetrievalKind.Source);
        }

        if (declaration.TrySet("retrievalEncoding"))
        {
            declaration.RetrievalEncoding = Explicit<ElasticRelationQueryFieldValueEncoding?>(encoding);
        }

        return this;
    }

    /// <summary>Declares an indexed query field and its Elasticsearch mapping family.</summary>
    /// <param name="path">Physical indexed property or multifield path.</param>
    /// <param name="mappingKind">Physical mapping family.</param>
    /// <returns>This field builder.</returns>
    public ElasticRelationQueryFieldBindingBuilder Query(
        FieldPath path,
        ElasticRelationQueryFieldMappingKind mappingKind)
    {
        if (declaration.TrySet("queryField"))
        {
            declaration.QueryField = Explicit<FieldPath?>(path);
        }

        if (declaration.TrySet("mappingKind"))
        {
            declaration.MappingKind = Explicit(mappingKind);
        }

        return this;
    }

    /// <summary>Declares a query-only indexed field.</summary>
    /// <param name="path">Physical indexed property or multifield path.</param>
    /// <param name="mappingKind">Physical mapping family.</param>
    /// <returns>This field builder.</returns>
    public ElasticRelationQueryFieldBindingBuilder QueryOnly(
        FieldPath path,
        ElasticRelationQueryFieldMappingKind mappingKind)
    {
        Query(path, mappingKind);
        if (declaration.TrySet("sourceField"))
        {
            declaration.SourceField = Explicit<FieldPath?>(null);
        }

        if (declaration.TrySet("retrievalKind"))
        {
            declaration.RetrievalKind = Explicit(ElasticRelationQueryFieldRetrievalKind.Unavailable);
        }

        if (declaration.TrySet("retrievalEncoding"))
        {
            declaration.RetrievalEncoding = Explicit<ElasticRelationQueryFieldValueEncoding?>(null);
        }

        return this;
    }

    /// <summary>Declares that this field is interpreted in the root Elasticsearch document.</summary>
    /// <returns>This field builder.</returns>
    public ElasticRelationQueryFieldBindingBuilder RootDocument()
    {
        if (declaration.TrySet("documentScope"))
        {
            declaration.DocumentScope = Explicit(ElasticRelationQueryFieldDocumentScope.RootDocument);
        }

        return this;
    }

    /// <summary>Attests exact semantic facilities supplied by the indexed mapping.</summary>
    /// <param name="capabilities">Exact physical facilities being attested.</param>
    /// <param name="semanticProfile">Stable mapping, normalization, transform, and cluster-setting profile.</param>
    /// <returns>This field builder.</returns>
    public ElasticRelationQueryFieldBindingBuilder Attest(
        ElasticRelationQueryFieldSemanticCapabilities capabilities,
        string semanticProfile)
    {
        if (declaration.TrySet("semanticCapabilities"))
        {
            declaration.SemanticCapabilities = Explicit(capabilities);
        }

        if (declaration.TrySet("semanticProfile"))
        {
            declaration.SemanticProfile = Explicit<string?>(semanticProfile);
        }

        return this;
    }

    /// <summary>Declares the indexed reversed representation available to an exact prefix suffix strategy.</summary>
    /// <param name="path">Physical reversed keyword-field path.</param>
    /// <returns>This field builder.</returns>
    public ElasticRelationQueryFieldBindingBuilder ReversedSuffix(FieldPath path)
    {
        if (declaration.TrySet("reversedSuffixField"))
        {
            declaration.ReversedSuffixField = Explicit<FieldPath?>(path);
        }

        return this;
    }

    /// <summary>Declares physical missing-value handling and its optional sentinel.</summary>
    /// <param name="behavior">Physical missing-value behavior.</param>
    /// <param name="sentinel">Reserved indexed sentinel, when required by <paramref name="behavior"/>.</param>
    /// <returns>This field builder.</returns>
    public ElasticRelationQueryFieldBindingBuilder MissingValues(
        ElasticRelationQueryMissingValueBehavior behavior,
        ObservationValue? sentinel = null)
    {
        if (declaration.TrySet("missingValueBehavior"))
        {
            declaration.MissingValueBehavior = Explicit(behavior);
        }

        if (declaration.TrySet("missingValueSentinel"))
        {
            declaration.MissingValueSentinel = Explicit(sentinel);
        }

        return this;
    }

    /// <summary>Declares physical explicit-null handling and its optional sentinel.</summary>
    /// <param name="behavior">Physical explicit-null behavior.</param>
    /// <param name="sentinel">Reserved indexed sentinel, when required by <paramref name="behavior"/>.</param>
    /// <returns>This field builder.</returns>
    public ElasticRelationQueryFieldBindingBuilder NullValues(
        ElasticRelationQueryNullValueBehavior behavior,
        ObservationValue? sentinel = null)
    {
        if (declaration.TrySet("nullValueBehavior"))
        {
            declaration.NullValueBehavior = Explicit(behavior);
        }

        if (declaration.TrySet("nullValueSentinel"))
        {
            declaration.NullValueSentinel = Explicit(sentinel);
        }

        return this;
    }

    /// <summary>Declares an explicit structured nested-collection evidence contract.</summary>
    /// <param name="nestedPath">Physical path mapped as Elasticsearch <c>nested</c>.</param>
    /// <param name="configure">Configures correlation, absence, and direct child mappings.</param>
    /// <returns>This field builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="Exception">The <paramref name="configure"/> callback throws; the same exception is propagated.</exception>
    public ElasticRelationQueryFieldBindingBuilder Nested(
        FieldPath nestedPath,
        Action<ElasticRelationQueryNestedScopeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        QueryOnly(nestedPath, ElasticRelationQueryFieldMappingKind.Nested);
        if (declaration.TrySet("documentScope"))
        {
            declaration.DocumentScope = Explicit(ElasticRelationQueryFieldDocumentScope.NestedDocument);
        }

        var nested = new ElasticNestedDeclaration(nestedPath, declaration.ReportNestedDuplicate);
        configure(new(nested, authority));
        if (declaration.TrySet("nestedScope"))
        {
            declaration.Nested = nested;
        }

        return this;
    }

    ElasticAuthoringValue<T> Explicit<T>(T value) =>
        new(value, RelationQueryConfigurationValueOrigin.Explicit, authority);
}

/// <summary>Configures exact nested-collection correlation and direct child mappings.</summary>
public sealed class ElasticRelationQueryNestedScopeBuilder
{
    readonly ElasticNestedDeclaration declaration;
    readonly string authority;

    internal ElasticRelationQueryNestedScopeBuilder(ElasticNestedDeclaration declaration, string authority)
    {
        this.declaration = declaration;
        this.authority = authority;
    }

    /// <summary>
    /// Explicitly attests the absence and correlation representation required by canonical structured
    /// collection existential semantics.
    /// </summary>
    /// <returns>This nested-scope builder.</returns>
    public ElasticRelationQueryNestedScopeBuilder AttestCanonicalAnyRepresentation()
    {
        if (declaration.TrySet("correlationGuarantee"))
        {
            declaration.Correlation = Explicit(ElasticRelationQueryNestedCorrelationGuarantee.SameNestedDocument);
        }

        if (declaration.TrySet("nullElementBehavior"))
        {
            declaration.NullElements = Explicit(ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion);
        }

        if (declaration.TrySet("emptyCollectionBehavior"))
        {
            declaration.EmptyCollections = Explicit(ElasticRelationQueryEmptyCollectionBehavior.NoNestedDocuments);
        }

        if (declaration.TrySet("outerMissingValueBehavior"))
        {
            declaration.OuterMissing = Explicit(ElasticRelationQueryMissingValueBehavior.ProhibitedByIngestion);
        }

        if (declaration.TrySet("outerNullValueBehavior"))
        {
            declaration.OuterNull = Explicit(ElasticRelationQueryNullValueBehavior.ProhibitedByIngestion);
        }

        return this;
    }

    /// <summary>Adds one direct element-child mapping.</summary>
    /// <param name="elementPath">One direct semantic field path relative to a collection element.</param>
    /// <param name="queryField">Complete physical indexed child-field path.</param>
    /// <param name="mappingKind">Supported exact scalar mapping family.</param>
    /// <param name="semanticCapabilities">Exact child facilities being attested.</param>
    /// <param name="semanticProfile">Stable mapping and normalization profile.</param>
    /// <param name="missingValueBehavior">Physical treatment of a missing child field.</param>
    /// <param name="nullValueBehavior">Physical treatment of an explicit-null child field.</param>
    /// <returns>This nested-scope builder.</returns>
    public ElasticRelationQueryNestedScopeBuilder Child(
        FieldPath elementPath,
        FieldPath queryField,
        ElasticRelationQueryFieldMappingKind mappingKind,
        ElasticRelationQueryFieldSemanticCapabilities semanticCapabilities,
        string semanticProfile,
        ElasticRelationQueryNestedAbsenceBehavior missingValueBehavior =
            ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion,
        ElasticRelationQueryNestedAbsenceBehavior nullValueBehavior =
            ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion)
    {
        if (declaration.Children.Any(child => string.Equals(
                ElasticRelationQueryStorageBindingBuilder.SafePathKey(child.ElementPath),
                ElasticRelationQueryStorageBindingBuilder.SafePathKey(elementPath),
                StringComparison.Ordinal)))
        {
            declaration.ReportDuplicate(
                "child/" + ElasticRelationQueryStorageBindingBuilder.SafePathKey(elementPath));
            return this;
        }
        declaration.Children.Add(new(
            elementPath,
            queryField,
            mappingKind,
            semanticCapabilities,
            semanticProfile,
            missingValueBehavior,
            nullValueBehavior,
            RelationQueryConfigurationValueOrigin.Explicit,
            authority));
        return this;
    }

    ElasticAuthoringValue<T> Explicit<T>(T value) =>
        new(value, RelationQueryConfigurationValueOrigin.Explicit, authority);
}

/// <summary>Typed fluent facade over one Elasticsearch storage-binding authoring session.</summary>
/// <typeparam name="T">CLR type represented by the selected placed semantic input.</typeparam>
public sealed class ElasticRelationQueryStorageBindingBuilder<T>
    where T : notnull
{
    readonly RelationQueryPlacedInput<T> placedInput;
    readonly ElasticRelationQueryStorageBindingBuilder inner;

    internal ElasticRelationQueryStorageBindingBuilder(
        RelationQueryPlacedInput<T> placedInput,
        ElasticRelationQueryBindingAuthoringOptions? options,
        string explicitAuthority)
    {
        this.placedInput = Guard.RequireNotNull(placedInput);
        inner = new(placedInput, options, explicitAuthority);
    }

    /// <summary>Declares the concrete Elasticsearch index.</summary>
    /// <param name="name">Concrete lowercase index name.</param>
    /// <returns>This typed builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder<T> Index(string name)
    {
        inner.Index(name);
        return this;
    }

    /// <summary>Overrides the deterministic convention-derived storage-binding identity.</summary>
    /// <param name="id">Stable explicit binding identity.</param>
    /// <returns>This typed builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder<T> WithId(ElasticRelationQueryBindingId id)
    {
        inner.WithId(id);
        return this;
    }

    /// <summary>Overrides the physical <c>_source</c> mode.</summary>
    /// <param name="mode">Effective source mode.</param>
    /// <returns>This typed builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder<T> SourceMode(ElasticRelationQuerySourceMode mode)
    {
        inner.SourceMode(mode);
        return this;
    }

    /// <summary>Overrides the configured <c>index.max_result_window</c>.</summary>
    /// <param name="value">Positive configured result window.</param>
    /// <returns>This typed builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder<T> MaximumResultWindow(int value)
    {
        inner.MaximumResultWindow(value);
        return this;
    }

    /// <summary>Overrides the adapter's maximum semantic page size.</summary>
    /// <param name="value">Positive page-size boundary no greater than the result window.</param>
    /// <returns>This typed builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder<T> MaximumPageSize(int value)
    {
        inner.MaximumPageSize(value);
        return this;
    }

    /// <summary>Overrides cross-request pagination-consistency evidence.</summary>
    /// <param name="consistency">Effective consistency evidence.</param>
    /// <returns>This typed builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder<T> PaginationConsistency(
        ElasticRelationQueryPaginationConsistency consistency)
    {
        inner.PaginationConsistency(consistency);
        return this;
    }

    /// <summary>Enables source-only semantic-path convention mappings for otherwise-unmapped demanded fields.</summary>
    /// <returns>This typed builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder<T> FieldsBySemanticPath()
    {
        inner.FieldsBySemanticPath();
        return this;
    }

    /// <summary>Disables field conventions so every demanded field requires an explicit mapping.</summary>
    /// <returns>This typed builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder<T> FieldsExplicitly()
    {
        inner.FieldsExplicitly();
        return this;
    }

    /// <summary>Configures one typed demanded semantic field using the general physical field builder.</summary>
    /// <typeparam name="TValue">CLR value selected by the semantic property chain.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic field.</param>
    /// <param name="configure">Configures physical retrieval, mapping, capabilities, and absence behavior.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="selector"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exception">The <paramref name="configure"/> callback throws; the same exception is propagated.</exception>
    public ElasticRelationQueryStorageBindingBuilder<T> Field<TValue>(
        Expression<Func<T, TValue>> selector,
        Action<ElasticRelationQueryFieldBindingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(selector);
        inner.Field(placedInput, selector, configure);
        return this;
    }

    /// <summary>Configures one structurally selected demanded field using the general physical field builder.</summary>
    /// <param name="semanticPath">Demanded semantic field path.</param>
    /// <param name="configure">Configures physical retrieval, mapping, capabilities, and absence behavior.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="Exception">The <paramref name="configure"/> callback throws; the same exception is propagated.</exception>
    public ElasticRelationQueryStorageBindingBuilder<T> Field(
        FieldPath semanticPath,
        Action<ElasticRelationQueryFieldBindingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        inner.Field(semanticPath, configure);
        return this;
    }

    /// <summary>Declares one typed demanded field as a root <c>_source</c>-only value.</summary>
    /// <typeparam name="TValue">CLR value selected by the semantic property chain.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic field.</param>
    /// <param name="sourceField">Physical <c>_source</c> path.</param>
    /// <param name="encoding">Exact physical JSON encoding.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public ElasticRelationQueryStorageBindingBuilder<T> SourceOnly<TValue>(
        Expression<Func<T, TValue>> selector,
        FieldPath sourceField,
        ElasticRelationQueryFieldValueEncoding encoding)
    {
        inner.SourceOnly(placedInput, selector, sourceField, encoding);
        return this;
    }

    /// <summary>Declares one typed demanded field as an exact keyword or wildcard mapping.</summary>
    /// <typeparam name="TValue">CLR value selected by the semantic property chain.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic field.</param>
    /// <param name="queryField">Physical keyword or wildcard query field.</param>
    /// <param name="capabilities">Exact physical facilities being attested.</param>
    /// <param name="semanticProfile">Stable mapping and normalization profile.</param>
    /// <param name="sourceField">Optional physical <c>_source</c> path.</param>
    /// <param name="mappingKind">Keyword or wildcard mapping family.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public ElasticRelationQueryStorageBindingBuilder<T> Keyword<TValue>(
        Expression<Func<T, TValue>> selector,
        FieldPath queryField,
        ElasticRelationQueryFieldSemanticCapabilities capabilities,
        string semanticProfile,
        FieldPath? sourceField = null,
        ElasticRelationQueryFieldMappingKind mappingKind = ElasticRelationQueryFieldMappingKind.Keyword)
    {
        inner.Keyword(placedInput, selector, queryField, capabilities, semanticProfile, sourceField, mappingKind);
        return this;
    }

    /// <summary>Declares one typed scalar collection as an exact keyword membership field.</summary>
    /// <typeparam name="TValue">CLR collection type selected by the semantic property chain.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic collection.</param>
    /// <param name="queryField">Physical multivalued keyword field.</param>
    /// <param name="semanticProfile">Stable mapping and normalization profile.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public ElasticRelationQueryStorageBindingBuilder<T> CollectionKeyword<TValue>(
        Expression<Func<T, TValue>> selector,
        FieldPath queryField,
        string semanticProfile)
    {
        inner.CollectionKeyword(placedInput, selector, queryField, semanticProfile);
        return this;
    }

    /// <summary>Declares one typed structured collection as an Elasticsearch nested mapping.</summary>
    /// <typeparam name="TValue">CLR collection type selected by the semantic property chain.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic collection.</param>
    /// <param name="nestedPath">Physical Elasticsearch nested path.</param>
    /// <param name="configure">Configures exact nested correlation and child mappings.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="selector"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exception">The <paramref name="configure"/> callback throws; the same exception is propagated.</exception>
    public ElasticRelationQueryStorageBindingBuilder<T> Nested<TValue>(
        Expression<Func<T, TValue>> selector,
        FieldPath nestedPath,
        Action<ElasticRelationQueryNestedScopeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(selector);
        inner.Nested(placedInput, selector, nestedPath, configure);
        return this;
    }

    /// <summary>Overrides the convention-set identity retained by the binding.</summary>
    /// <param name="version">Stable convention identity and version.</param>
    /// <returns>This typed builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder<T> ConventionSetVersion(string version)
    {
        inner.ConventionSetVersion(version);
        return this;
    }

    /// <summary>Builds a well-formed exact-affinity Elasticsearch binding or returns structured diagnostics.</summary>
    /// <returns>
    /// A plan- and placement-bound artifact or deterministic authoring diagnostics. Branch-specific semantic
    /// realizability remains a compiler obligation.
    /// </returns>
    public RelationQueryArtifactAuthoringResult<ElasticRelationQueryStorageBinding> Build() => inner.Build();
}

/// <summary>
/// Mutable, plan-bound authoring session that lowers Elasticsearch-specific physical decisions to one immutable
/// <see cref="ElasticRelationQueryStorageBinding"/>.
/// </summary>
/// <remarks>The builder is not thread-safe. The produced artifact is immutable and independently persistable.</remarks>
public sealed class ElasticRelationQueryStorageBindingBuilder
{
    const string DerivedIdAuthority = "cohesive.relations.elastic/binding-id-convention/v3";
    internal const string TargetSetting = "target";
    internal const string TargetProfileSetting = "targetProfile";
    internal const string IndexSetting = "indexName";
    internal const string SourceModeSetting = "sourceMode";
    internal const string MaximumResultWindowSetting = "maximumResultWindow";
    internal const string MaximumPageSizeSetting = "maximumPageSize";
    internal const string PaginationConsistencySetting = "paginationConsistency";
    internal const string ConventionSetting = "conventionSetVersion";
    internal const string BindingIdSetting = "bindingId";
    internal const string FieldMappingConventionSetting = "fieldMappingConvention";

    readonly RelationQueryPlacedInput placedInput;
    readonly ElasticRelationQueryBindingAuthoringOptions? options;
    readonly string explicitAuthority;
    readonly List<RelationQueryArtifactAuthoringDiagnostic> diagnostics = [];
    readonly Dictionary<RelationQueryInputId, ElasticFieldDeclaration> explicitFields = [];
    readonly HashSet<string> explicitSettings = new(StringComparer.Ordinal);

    ElasticAuthoringValue<ElasticRelationQueryBindingId>? explicitId;
    ElasticAuthoringValue<string>? indexName;
    ElasticAuthoringValue<ElasticRelationQuerySourceMode>? sourceMode;
    ElasticAuthoringValue<int>? maximumResultWindow;
    ElasticAuthoringValue<int>? maximumPageSize;
    ElasticAuthoringValue<ElasticRelationQueryPaginationConsistency>? paginationConsistency;
    ElasticAuthoringValue<ElasticRelationQueryFieldMappingConvention>? fieldMappingConvention;
    ElasticAuthoringValue<string>? conventionSetVersion;

    internal ElasticRelationQueryStorageBindingBuilder(
        RelationQueryPlacedInput placedInput,
        ElasticRelationQueryBindingAuthoringOptions? options,
        string explicitAuthority)
    {
        this.placedInput = Guard.RequireNotNull(placedInput);
        this.options = options;
        this.explicitAuthority = Guard.RequireNotNullOrWhiteSpace(explicitAuthority);
    }

    /// <summary>Declares the concrete Elasticsearch index.</summary>
    /// <param name="name">Concrete lowercase index name.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder Index(string name)
    {
        if (TrySetExplicit(IndexSetting))
        {
            indexName = Explicit(name);
        }

        return this;
    }

    /// <summary>Overrides the deterministic convention-derived storage-binding identity.</summary>
    /// <param name="id">Stable explicit binding identity.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder WithId(ElasticRelationQueryBindingId id)
    {
        if (TrySetExplicit(BindingIdSetting))
        {
            explicitId = Explicit(id);
        }

        return this;
    }

    /// <summary>Overrides the physical <c>_source</c> mode.</summary>
    /// <param name="mode">Effective source mode.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder SourceMode(ElasticRelationQuerySourceMode mode)
    {
        if (TrySetExplicit(SourceModeSetting))
        {
            sourceMode = Explicit(mode);
        }

        return this;
    }

    /// <summary>Overrides the configured <c>index.max_result_window</c>.</summary>
    /// <param name="value">Positive configured result window.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder MaximumResultWindow(int value)
    {
        if (TrySetExplicit(MaximumResultWindowSetting))
        {
            maximumResultWindow = Explicit(value);
        }

        return this;
    }

    /// <summary>Overrides the adapter's maximum semantic page size.</summary>
    /// <param name="value">Positive page-size boundary no greater than the result window.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder MaximumPageSize(int value)
    {
        if (TrySetExplicit(MaximumPageSizeSetting))
        {
            maximumPageSize = Explicit(value);
        }

        return this;
    }

    /// <summary>Overrides cross-request pagination-consistency evidence.</summary>
    /// <param name="consistency">Effective consistency evidence.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder PaginationConsistency(
        ElasticRelationQueryPaginationConsistency consistency)
    {
        if (TrySetExplicit(PaginationConsistencySetting))
        {
            paginationConsistency = Explicit(consistency);
        }

        return this;
    }

    /// <summary>Enables source-only semantic-path convention mappings for otherwise-unmapped demanded fields.</summary>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder FieldsBySemanticPath()
    {
        if (TrySetExplicit(FieldMappingConventionSetting))
        {
            fieldMappingConvention = Explicit(ElasticRelationQueryFieldMappingConvention.SemanticPath);
        }

        return this;
    }

    /// <summary>Disables field conventions so every demanded field requires an explicit mapping.</summary>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder FieldsExplicitly()
    {
        if (TrySetExplicit(FieldMappingConventionSetting))
        {
            fieldMappingConvention = Explicit(ElasticRelationQueryFieldMappingConvention.Explicit);
        }

        return this;
    }

    /// <summary>Configures one exact demanded field using the general physical field builder.</summary>
    /// <param name="field">Exact demanded field owned by the selected placed input.</param>
    /// <param name="configure">Configures physical retrieval, mapping, capabilities, and absence behavior.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="field"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exception">The <paramref name="configure"/> callback throws; the same exception is propagated.</exception>
    public ElasticRelationQueryStorageBindingBuilder Field(
        RelationQueryFieldInputContract field,
        Action<ElasticRelationQueryFieldBindingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(field);
        if (!TryOwnField(field, FieldSetting(field.Input.Id)))
        {
            return this;
        }

        if (explicitFields.ContainsKey(field.Input.Id))
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                $"Compiled field input '{field.Input.Id.Value}' has more than one explicit Elasticsearch mapping.",
                field.Input.Id,
                field.Input.Field.Path,
                FieldSetting(field.Input.Id));
            return this;
        }
        var declaration = new ElasticFieldDeclaration(
            field,
            setting => Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                $"Field '{field.Input.Field.Path}' repeats the explicit Elasticsearch setting '{setting}'.",
                field.Input.Id,
                field.Input.Field.Path,
                FieldSetting(field.Input.Id) + "/" + setting));
        explicitFields.Add(field.Input.Id, declaration);
        configure(new(declaration, explicitAuthority));
        return this;
    }

    /// <summary>Configures one structurally selected demanded field using the general physical field builder.</summary>
    /// <param name="semanticPath">Demanded semantic field path.</param>
    /// <param name="configure">Configures physical retrieval, mapping, capabilities, and absence behavior.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="Exception">The <paramref name="configure"/> callback throws; the same exception is propagated.</exception>
    public ElasticRelationQueryStorageBindingBuilder Field(
        FieldPath semanticPath,
        Action<ElasticRelationQueryFieldBindingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (TryGetField(semanticPath, FieldSetting(semanticPath), out var field))
        {
            Field(field, configure);
        }

        return this;
    }

    /// <summary>Declares one exact demanded field as a root <c>_source</c>-only value.</summary>
    /// <param name="field">Exact demanded field owned by the selected placed input.</param>
    /// <param name="sourceField">Physical <c>_source</c> path.</param>
    /// <param name="encoding">Exact physical JSON encoding.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public ElasticRelationQueryStorageBindingBuilder SourceOnly(
        RelationQueryFieldInputContract field,
        FieldPath sourceField,
        ElasticRelationQueryFieldValueEncoding encoding) =>
        Field(field, builder => builder.Source(sourceField, encoding).RootDocument());

    /// <summary>Declares one structurally selected demanded field as a root <c>_source</c>-only value.</summary>
    /// <param name="semanticPath">Demanded semantic field path.</param>
    /// <param name="sourceField">Physical <c>_source</c> path.</param>
    /// <param name="encoding">Exact physical JSON encoding.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder SourceOnly(
        FieldPath semanticPath,
        FieldPath sourceField,
        ElasticRelationQueryFieldValueEncoding encoding)
    {
        if (TryGetField(semanticPath, FieldSetting(semanticPath), out var field))
        {
            SourceOnly(field, sourceField, encoding);
        }

        return this;
    }

    /// <summary>Declares one exact demanded field as an exact keyword or wildcard mapping.</summary>
    /// <param name="field">Exact demanded field owned by the selected placed input.</param>
    /// <param name="queryField">Physical keyword or wildcard query field.</param>
    /// <param name="capabilities">Exact physical facilities being attested.</param>
    /// <param name="semanticProfile">Stable mapping and normalization profile.</param>
    /// <param name="sourceField">Optional physical <c>_source</c> path.</param>
    /// <param name="mappingKind">Keyword or wildcard mapping family.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public ElasticRelationQueryStorageBindingBuilder Keyword(
        RelationQueryFieldInputContract field,
        FieldPath queryField,
        ElasticRelationQueryFieldSemanticCapabilities capabilities,
        string semanticProfile,
        FieldPath? sourceField = null,
        ElasticRelationQueryFieldMappingKind mappingKind = ElasticRelationQueryFieldMappingKind.Keyword)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (mappingKind is not (
                ElasticRelationQueryFieldMappingKind.Keyword
                or ElasticRelationQueryFieldMappingKind.Wildcard))
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                "The keyword helper requires a keyword or wildcard mapping family.",
                field.Input.Id,
                field.Input.Field.Path,
                FieldSetting(field.Input.Id) + "/mappingKind");
            return this;
        }

        return Field(field, builder =>
        {
            if (sourceField is not { } source)
            {
                builder.QueryOnly(queryField, mappingKind);
            }
            else
            {
                builder.Source(source, ElasticRelationQueryFieldValueEncoding.JsonString);
                builder.Query(queryField, mappingKind);
            }

            builder.RootDocument().Attest(capabilities, semanticProfile);
        });
    }

    /// <summary>Declares one structurally selected demanded field as an exact keyword or wildcard mapping.</summary>
    /// <param name="semanticPath">Demanded semantic field path.</param>
    /// <param name="queryField">Physical keyword or wildcard query field.</param>
    /// <param name="capabilities">Exact physical facilities being attested.</param>
    /// <param name="semanticProfile">Stable mapping and normalization profile.</param>
    /// <param name="sourceField">Optional physical <c>_source</c> path.</param>
    /// <param name="mappingKind">Keyword or wildcard mapping family.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder Keyword(
        FieldPath semanticPath,
        FieldPath queryField,
        ElasticRelationQueryFieldSemanticCapabilities capabilities,
        string semanticProfile,
        FieldPath? sourceField = null,
        ElasticRelationQueryFieldMappingKind mappingKind = ElasticRelationQueryFieldMappingKind.Keyword)
    {
        if (TryGetField(semanticPath, FieldSetting(semanticPath), out var field))
        {
            Keyword(field, queryField, capabilities, semanticProfile, sourceField, mappingKind);
        }

        return this;
    }

    /// <summary>Declares one exact demanded scalar collection as an exact keyword membership field.</summary>
    /// <param name="field">Exact demanded collection field owned by the selected placed input.</param>
    /// <param name="queryField">Physical multivalued keyword field.</param>
    /// <param name="semanticProfile">Stable mapping and normalization profile.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public ElasticRelationQueryStorageBindingBuilder CollectionKeyword(
        RelationQueryFieldInputContract field,
        FieldPath queryField,
        string semanticProfile) =>
        Keyword(
            field,
            queryField,
            ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership,
            semanticProfile);

    /// <summary>Declares one structurally selected scalar collection as an exact keyword membership field.</summary>
    /// <param name="semanticPath">Demanded semantic collection field path.</param>
    /// <param name="queryField">Physical multivalued keyword field.</param>
    /// <param name="semanticProfile">Stable mapping and normalization profile.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder CollectionKeyword(
        FieldPath semanticPath,
        FieldPath queryField,
        string semanticProfile)
    {
        if (TryGetField(semanticPath, FieldSetting(semanticPath), out var field))
        {
            CollectionKeyword(field, queryField, semanticProfile);
        }

        return this;
    }

    /// <summary>Declares one exact demanded structured collection as an Elasticsearch nested mapping.</summary>
    /// <param name="field">Exact demanded collection field owned by the selected placed input.</param>
    /// <param name="nestedPath">Physical Elasticsearch nested path.</param>
    /// <param name="configure">Configures exact nested correlation and child mappings.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="field"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exception">The <paramref name="configure"/> callback throws; the same exception is propagated.</exception>
    public ElasticRelationQueryStorageBindingBuilder Nested(
        RelationQueryFieldInputContract field,
        FieldPath nestedPath,
        Action<ElasticRelationQueryNestedScopeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(field);
        return Field(field, builder => builder.Nested(nestedPath, configure));
    }

    /// <summary>Declares one structurally selected structured collection as an Elasticsearch nested mapping.</summary>
    /// <param name="semanticPath">Demanded semantic collection field path.</param>
    /// <param name="nestedPath">Physical Elasticsearch nested path.</param>
    /// <param name="configure">Configures exact nested correlation and child mappings.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="Exception">The <paramref name="configure"/> callback throws; the same exception is propagated.</exception>
    public ElasticRelationQueryStorageBindingBuilder Nested(
        FieldPath semanticPath,
        FieldPath nestedPath,
        Action<ElasticRelationQueryNestedScopeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (TryGetField(semanticPath, FieldSetting(semanticPath), out var field))
        {
            Nested(field, nestedPath, configure);
        }

        return this;
    }

    /// <summary>Overrides the convention-set identity retained by the binding.</summary>
    /// <param name="version">Stable convention identity and version.</param>
    /// <returns>This builder.</returns>
    public ElasticRelationQueryStorageBindingBuilder ConventionSetVersion(string version)
    {
        if (TrySetExplicit(ConventionSetting))
        {
            conventionSetVersion = Explicit(version);
        }

        return this;
    }

    /// <summary>Builds a well-formed exact-affinity Elasticsearch binding or returns structured diagnostics.</summary>
    /// <returns>
    /// A plan- and placement-bound artifact or deterministic authoring diagnostics. Branch-specific semantic
    /// realizability remains a compiler obligation.
    /// </returns>
    public RelationQueryArtifactAuthoringResult<ElasticRelationQueryStorageBinding> Build()
    {
        ValidatePlacedInput();
        var effective = ResolveEffectiveConfiguration();
        if (HasErrors)
        {
            return Failure();
        }

        try
        {
            var decisions = effective.Decisions.ToList();
            var compiledPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(placedInput.Plan));
            var placementFingerprint = placedInput.Placement.Fingerprint;
            var id = effective.Id?.Value
                     ?? DeriveId(effective, compiledPlanFingerprint, placementFingerprint);
            decisions.Add(Decision(
                BindingIdSetting,
                effective.Id?.Origin ?? RelationQueryConfigurationValueOrigin.AdapterConvention,
                effective.Id?.Authority ?? DerivedIdAuthority));
            var local = decisions.Any(static decision => decision.Origin is
                RelationQueryConfigurationValueOrigin.Explicit
                or RelationQueryConfigurationValueOrigin.ScopedProfile);
            var artifact = new ElasticRelationQueryStorageBinding(
                id,
                placedInput.Source.Id,
                placedInput.Binding.Id,
                ElasticRelationQueryTargetProfile.Target,
                ElasticRelationQueryTargetProfile.ProfileId,
                effective.IndexName.Value,
                [.. effective.Fields.Select(static field => field.Binding)],
                effective.SourceMode.Value,
                effective.MaximumResultWindow.Value,
                effective.MaximumPageSize.Value,
                effective.PaginationConsistency.Value,
                local ? ElasticRelationQueryBindingOrigin.Explicit : ElasticRelationQueryBindingOrigin.Convention,
                effective.ConventionSetVersion.Value,
                [.. decisions],
                compiledPlanFingerprint,
                placementFingerprint);
            return new(artifact, [.. diagnostics]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.ArtifactInvalid,
                $"Elasticsearch storage-binding construction failed: {exception.Message}");
            return Failure();
        }
    }

    internal ElasticRelationQueryStorageBindingBuilder Field<T, TValue>(
        RelationQueryPlacedInput<T> input,
        Expression<Func<T, TValue>> selector,
        Action<ElasticRelationQueryFieldBindingBuilder> configure)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (TryResolveTypedField(input, selector, "field", out var field))
        {
            Field(field, configure);
        }

        return this;
    }

    internal ElasticRelationQueryStorageBindingBuilder SourceOnly<T, TValue>(
        RelationQueryPlacedInput<T> input,
        Expression<Func<T, TValue>> selector,
        FieldPath sourceField,
        ElasticRelationQueryFieldValueEncoding encoding)
        where T : notnull
    {
        if (TryResolveTypedField(input, selector, "sourceOnly", out var field))
        {
            SourceOnly(field, sourceField, encoding);
        }

        return this;
    }

    internal ElasticRelationQueryStorageBindingBuilder Keyword<T, TValue>(
        RelationQueryPlacedInput<T> input,
        Expression<Func<T, TValue>> selector,
        FieldPath queryField,
        ElasticRelationQueryFieldSemanticCapabilities capabilities,
        string semanticProfile,
        FieldPath? sourceField,
        ElasticRelationQueryFieldMappingKind mappingKind)
        where T : notnull
    {
        if (TryResolveTypedField(input, selector, "keyword", out var field))
        {
            Keyword(field, queryField, capabilities, semanticProfile, sourceField, mappingKind);
        }

        return this;
    }

    internal ElasticRelationQueryStorageBindingBuilder CollectionKeyword<T, TValue>(
        RelationQueryPlacedInput<T> input,
        Expression<Func<T, TValue>> selector,
        FieldPath queryField,
        string semanticProfile)
        where T : notnull
    {
        if (TryResolveTypedField(input, selector, "collectionKeyword", out var field))
        {
            CollectionKeyword(field, queryField, semanticProfile);
        }

        return this;
    }

    internal ElasticRelationQueryStorageBindingBuilder Nested<T, TValue>(
        RelationQueryPlacedInput<T> input,
        Expression<Func<T, TValue>> selector,
        FieldPath nestedPath,
        Action<ElasticRelationQueryNestedScopeBuilder> configure)
        where T : notnull
    {
        if (TryResolveTypedField(input, selector, "nested", out var field))
        {
            Nested(field, nestedPath, configure);
        }

        return this;
    }

    ElasticEffectiveConfiguration ResolveEffectiveConfiguration()
    {
        var convention = ElasticRelationQueryStorageBinding.SemanticPathConventionSet;
        var optionAuthority = options?.Authority;
        var effectiveIndex = indexName
                             ?? (options?.IndexName is { } optionIndex
                                 ? Scoped(optionIndex, optionAuthority!)
                                 : MissingIndex());
        var effectiveSourceMode = sourceMode
                                  ?? (options?.SourceMode is { } optionSourceMode
                                      ? Scoped(optionSourceMode, optionAuthority!)
                                      : Adapter(ElasticRelationQuerySourceMode.Enabled, convention));
        var effectiveWindow = maximumResultWindow
                              ?? (options?.MaximumResultWindow is { } optionWindow
                                  ? Scoped(optionWindow, optionAuthority!)
                                  : Adapter(ElasticRelationQueryStorageBinding.DefaultMaximumResultWindow, convention));
        var effectivePageSize = maximumPageSize
                                ?? (options?.MaximumPageSize is { } optionPageSize
                                    ? Scoped(optionPageSize, optionAuthority!)
                                    : Adapter(ElasticRelationQueryStorageBinding.DefaultMaximumPageSize, convention));
        var effectiveConsistency = paginationConsistency
                                   ?? (options?.PaginationConsistency is { } optionConsistency
                                       ? Scoped(optionConsistency, optionAuthority!)
                                       : Adapter(ElasticRelationQueryPaginationConsistency.Unproven, convention));
        var effectiveFieldMapping = fieldMappingConvention
                                    ?? (options?.FieldMappingConvention is { } optionFieldMapping
                                        ? Scoped(optionFieldMapping, optionAuthority!)
                                        : Adapter(ElasticRelationQueryFieldMappingConvention.SemanticPath, convention));
        var effectiveConvention = conventionSetVersion
                                  ?? (options?.ConventionSetVersion is { } optionConvention
                                      ? Scoped(optionConvention, optionAuthority!)
                                      : Adapter(convention, convention));
        var selectedId = explicitId;
        if (selectedId is null && options?.BindingId is { } optionId)
        {
            selectedId = Scoped(optionId, optionAuthority!);
        }

        if (selectedId is { } configuredId && string.IsNullOrWhiteSpace(configuredId.Value.Value))
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                "The Elasticsearch binding identity cannot be default.",
                setting: BindingIdSetting);
        }

        ValidateScalarConfiguration(
            effectiveIndex,
            effectiveSourceMode,
            effectiveWindow,
            effectivePageSize,
            effectiveConsistency,
            effectiveFieldMapping,
            effectiveConvention);
        var fields = ResolveFields(effectiveFieldMapping, convention);
        if (effectiveSourceMode.Value == ElasticRelationQuerySourceMode.Disabled
            && fields.Any(static field => field.Binding.RetrievalKind == ElasticRelationQueryFieldRetrievalKind.Source))
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                "Source retrieval cannot be configured when Elasticsearch _source is disabled.",
                setting: SourceModeSetting);
        }

        List<RelationQueryConfigurationDecision> decisions =
        [
            Decision(
                TargetSetting,
                RelationQueryConfigurationValueOrigin.AdapterConvention,
                ElasticRelationQueryTargetProfile.ProfileId.Value),
            Decision(
                TargetProfileSetting,
                RelationQueryConfigurationValueOrigin.AdapterConvention,
                ElasticRelationQueryTargetProfile.ProfileId.Value),
            Configuration(IndexSetting, effectiveIndex),
            Configuration(SourceModeSetting, effectiveSourceMode),
            Configuration(MaximumResultWindowSetting, effectiveWindow),
            Configuration(MaximumPageSizeSetting, effectivePageSize),
            Configuration(PaginationConsistencySetting, effectiveConsistency),
            Configuration(ConventionSetting, effectiveConvention)
        ];
        decisions.AddRange(fields.SelectMany(static field => field.Decisions));
        return new(
            selectedId,
            effectiveIndex,
            effectiveSourceMode,
            effectiveWindow,
            effectivePageSize,
            effectiveConsistency,
            fields,
            effectiveConvention,
            decisions);
    }

    ElasticAuthoringValue<string> MissingIndex()
    {
        Error(
            ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
            "Elasticsearch binding authoring requires a concrete physical index name.",
            setting: IndexSetting);
        return Adapter(string.Empty, ElasticRelationQueryStorageBinding.SemanticPathConventionSet);
    }

    ImmutableArray<ElasticEffectiveField> ResolveFields(
        ElasticAuthoringValue<ElasticRelationQueryFieldMappingConvention> mappingConvention,
        string convention)
    {
        List<ElasticEffectiveField> fields = [];
        foreach (var field in placedInput.Fields)
        {
            if (explicitFields.TryGetValue(field.Input.Id, out var declaration))
            {
                var resolved = ResolveField(declaration, convention);
                if (resolved is not null)
                {
                    fields.Add(resolved);
                }

                continue;
            }
            if (mappingConvention.Value == ElasticRelationQueryFieldMappingConvention.SemanticPath)
            {
                var resolved = ResolveConventionField(field, convention);
                if (resolved is not null)
                {
                    fields.Add(resolved);
                }

                continue;
            }
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
                $"Demanded field '{field.Input.Field.Path}' has no Elasticsearch physical mapping.",
                field.Input.Id,
                field.Input.Field.Path,
                FieldSetting(field.Input.Id));
        }
        return [.. fields.OrderBy(static field => field.Binding.Input.Value, StringComparer.Ordinal)];
    }

    ElasticEffectiveField? ResolveConventionField(
        RelationQueryFieldInputContract field,
        string convention)
    {
        var path = field.Input.Field.Path;
        ElasticRelationQueryFieldValueEncoding? encoding =
            TryInferRetrievalEncoding(field.Input.ValueContract, out var inferred) ? inferred : null;
        var binding = TryCreateFieldBinding(
            field,
            sourceField: path,
            queryField: null,
            ElasticRelationQueryFieldMappingKind.Unindexed,
            encoding is null
                ? ElasticRelationQueryFieldRetrievalKind.Unavailable
                : ElasticRelationQueryFieldRetrievalKind.Source,
            encoding,
            ElasticRelationQueryFieldDocumentScope.RootDocument,
            ElasticRelationQueryFieldSemanticCapabilities.None,
            reversedSuffixField: null,
            semanticProfile: null,
            ElasticRelationQueryMissingValueBehavior.NotIndexed,
            missingValueSentinel: null,
            ElasticRelationQueryNullValueBehavior.JsonNullNotIndexed,
            nullValueSentinel: null,
            nestedScope: null);
        if (binding is null)
        {
            return null;
        }

        var prefix = FieldSetting(field.Input.Id) + "/";
        var decisions = FieldDecisions(
            prefix,
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            convention,
            nested: null);
        return new(binding, decisions);
    }

    ElasticEffectiveField? ResolveField(ElasticFieldDeclaration declaration, string convention)
    {
        var field = declaration.Field;
        var source = declaration.SourceField ?? Adapter<FieldPath?>(null, convention);
        var query = declaration.QueryField ?? Adapter<FieldPath?>(null, convention);
        var mapping = declaration.MappingKind
                      ?? Adapter(
                          query.Value is null
                              ? ElasticRelationQueryFieldMappingKind.Unindexed
                              : ElasticRelationQueryFieldMappingKind.Keyword,
                          convention);
        var retrieval = declaration.RetrievalKind
                        ?? Adapter(
                            source.Value is null
                                ? ElasticRelationQueryFieldRetrievalKind.Unavailable
                                : ElasticRelationQueryFieldRetrievalKind.Source,
                            convention);
        ElasticAuthoringValue<ElasticRelationQueryFieldValueEncoding?> encoding;
        if (declaration.RetrievalEncoding is { } configuredEncoding)
        {
            encoding = configuredEncoding;
        }
        else if (retrieval.Value == ElasticRelationQueryFieldRetrievalKind.Unavailable)
        {
            encoding = Adapter<ElasticRelationQueryFieldValueEncoding?>(null, convention);
        }
        else if (TryInferRetrievalEncoding(field.Input.ValueContract, out var inferredEncoding))
        {
            encoding = Adapter<ElasticRelationQueryFieldValueEncoding?>(inferredEncoding, convention);
        }
        else
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
                $"Demanded field '{field.Input.Field.Path}' requires an explicit exact retrieval encoding.",
                field.Input.Id,
                field.Input.Field.Path,
                FieldSetting(field.Input.Id) + "/retrievalEncoding");
            encoding = Adapter<ElasticRelationQueryFieldValueEncoding?>(null, convention);
        }
        var scope = declaration.DocumentScope
                    ?? Adapter(
                        declaration.Nested is null
                            ? ElasticRelationQueryFieldDocumentScope.RootDocument
                            : ElasticRelationQueryFieldDocumentScope.NestedDocument,
                        convention);
        var capabilities = declaration.SemanticCapabilities
                           ?? Adapter(ElasticRelationQueryFieldSemanticCapabilities.None, convention);
        var reversed = declaration.ReversedSuffixField ?? Adapter<FieldPath?>(null, convention);
        var profile = declaration.SemanticProfile ?? Adapter<string?>(null, convention);
        var missing = declaration.MissingValueBehavior
                      ?? declaration.Nested?.OuterMissing
                      ?? Adapter(ElasticRelationQueryMissingValueBehavior.NotIndexed, convention);
        var missingSentinel = declaration.MissingValueSentinel ?? Adapter<ObservationValue?>(null, convention);
        var nullBehavior = declaration.NullValueBehavior
                           ?? declaration.Nested?.OuterNull
                           ?? Adapter(ElasticRelationQueryNullValueBehavior.JsonNullNotIndexed, convention);
        var nullSentinel = declaration.NullValueSentinel ?? Adapter<ObservationValue?>(null, convention);
        ElasticRelationQueryNestedScopeEvidence? nestedScope = null;
        List<RelationQueryConfigurationDecision> nestedDecisions = [];
        if (declaration.Nested is { } nested)
        {
            if (declaration.MissingValueBehavior is not null && nested.OuterMissing is not null)
            {
                Error(
                    ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                    "The field and its nested scope both explicitly declare missing-value behavior.",
                    field.Input.Id,
                    field.Input.Field.Path,
                    FieldSetting(field.Input.Id) + "/missingValueBehavior");
            }
            if (declaration.NullValueBehavior is not null && nested.OuterNull is not null)
            {
                Error(
                    ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                    "The field and its nested scope both explicitly declare null-value behavior.",
                    field.Input.Id,
                    field.Input.Field.Path,
                    FieldSetting(field.Input.Id) + "/nullValueBehavior");
            }
            nestedScope = ResolveNested(field, nested, convention, nestedDecisions);
            if (nestedScope is null)
            {
                return null;
            }
        }

        var binding = TryCreateFieldBinding(
            field,
            source.Value,
            query.Value,
            mapping.Value,
            retrieval.Value,
            encoding.Value,
            scope.Value,
            capabilities.Value,
            reversed.Value,
            profile.Value,
            missing.Value,
            missingSentinel.Value,
            nullBehavior.Value,
            nullSentinel.Value,
            nestedScope);
        if (binding is null)
        {
            return null;
        }

        var prefix = FieldSetting(field.Input.Id) + "/";
        List<RelationQueryConfigurationDecision> decisions =
        [
            Configuration(prefix + "sourceField", source),
            Configuration(prefix + "queryField", query),
            Configuration(prefix + "mappingKind", mapping),
            Configuration(prefix + "retrievalKind", retrieval),
            Configuration(prefix + "retrievalEncoding", encoding),
            Configuration(prefix + "documentScope", scope),
            Configuration(prefix + "semanticCapabilities", capabilities),
            Configuration(prefix + "reversedSuffixField", reversed),
            Configuration(prefix + "semanticProfile", profile),
            Configuration(prefix + "missingValueBehavior", missing),
            Configuration(prefix + "missingValueSentinel", missingSentinel),
            Configuration(prefix + "nullValueBehavior", nullBehavior),
            Configuration(prefix + "nullValueSentinel", nullSentinel),
            new(prefix + "nestedScope", declaration.Nested is null
                ? RelationQueryConfigurationValueOrigin.AdapterConvention
                : RelationQueryConfigurationValueOrigin.Explicit,
                declaration.Nested is null ? convention : explicitAuthority)
        ];
        decisions.AddRange(nestedDecisions);
        return new(binding, [.. decisions]);
    }

    ElasticRelationQueryNestedScopeEvidence? ResolveNested(
        RelationQueryFieldInputContract field,
        ElasticNestedDeclaration declaration,
        string convention,
        ICollection<RelationQueryConfigurationDecision> decisions)
    {
        var correlation = declaration.Correlation
                          ?? Adapter(ElasticRelationQueryNestedCorrelationGuarantee.Unproven, convention);
        var nullElements = declaration.NullElements
                           ?? Adapter(ElasticRelationQueryNestedAbsenceBehavior.Unproven, convention);
        var empty = declaration.EmptyCollections
                    ?? Adapter(ElasticRelationQueryEmptyCollectionBehavior.Unproven, convention);
        var prefix = FieldSetting(field.Input.Id) + "/nested/";
        decisions.Add(Decision(
            prefix + "nestedPath",
            RelationQueryConfigurationValueOrigin.Explicit,
            explicitAuthority));
        decisions.Add(Configuration(prefix + "correlationGuarantee", correlation));
        decisions.Add(Configuration(prefix + "nullElementBehavior", nullElements));
        decisions.Add(Configuration(prefix + "emptyCollectionBehavior", empty));

        var nestedPathValid = ValidatePhysicalPath(
            declaration.NestedPath,
            field,
            prefix + "nestedPath");

        var duplicate = declaration.Children
            .GroupBy(static child => SafePathKey(child.ElementPath), StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                $"Nested element path '{duplicate.Key}' has more than one explicit Elasticsearch child mapping.",
                field.Input.Id,
                field.Input.Field.Path,
                prefix + "child/" + duplicate.Key);
            return null;
        }
        List<ElasticNestedChildDeclaration> validChildren = [];
        foreach (var child in declaration.Children)
        {
            var childPrefix = prefix + "child/" + SafePathKey(child.ElementPath) + "/";
            var elementPathValid = ValidatePhysicalPath(
                child.ElementPath,
                field,
                childPrefix + "elementPath",
                requireDirectField: true);
            var queryFieldValid = ValidatePhysicalPath(
                child.QueryField,
                field,
                childPrefix + "queryField");
            if (elementPathValid && queryFieldValid)
            {
                validChildren.Add(child);
            }
        }
        if (!nestedPathValid || validChildren.Count != declaration.Children.Count)
        {
            return null;
        }

        var children = ImmutableArray.CreateBuilder<ElasticRelationQueryNestedChildFieldBinding>();
        foreach (var child in validChildren.OrderBy(static child => SafePathKey(child.ElementPath), StringComparer.Ordinal))
        {
            try
            {
                children.Add(new(
                    child.ElementPath,
                    child.QueryField,
                    child.MappingKind,
                    child.SemanticCapabilities,
                    child.SemanticProfile,
                    child.MissingValueBehavior,
                    child.NullValueBehavior));
                var childPrefix = prefix + "child/" + SafePathKey(child.ElementPath) + "/";
                decisions.Add(Decision(childPrefix + "elementPath", child.Origin, child.Authority));
                decisions.Add(Decision(childPrefix + "queryField", child.Origin, child.Authority));
                decisions.Add(Decision(childPrefix + "mappingKind", child.Origin, child.Authority));
                decisions.Add(Decision(childPrefix + "semanticCapabilities", child.Origin, child.Authority));
                decisions.Add(Decision(childPrefix + "semanticProfile", child.Origin, child.Authority));
                decisions.Add(Decision(childPrefix + "missingValueBehavior", child.Origin, child.Authority));
                decisions.Add(Decision(childPrefix + "nullValueBehavior", child.Origin, child.Authority));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                Error(
                    ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                    $"Nested child mapping is inconsistent: {exception.Message}",
                    field.Input.Id,
                    field.Input.Field.Path,
                    prefix + "child/" + SafePathKey(child.ElementPath));
            }
        }
        if (HasErrors)
        {
            return null;
        }

        try
        {
            return new(
                declaration.NestedPath,
                correlation.Value,
                nullElements.Value,
                empty.Value,
                children.ToImmutable());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                $"Nested scope evidence is inconsistent: {exception.Message}",
                field.Input.Id,
                field.Input.Field.Path,
                prefix.TrimEnd('/'));
            return null;
        }
    }

    ElasticRelationQueryFieldBinding? TryCreateFieldBinding(
        RelationQueryFieldInputContract field,
        FieldPath? sourceField,
        FieldPath? queryField,
        ElasticRelationQueryFieldMappingKind mappingKind,
        ElasticRelationQueryFieldRetrievalKind retrievalKind,
        ElasticRelationQueryFieldValueEncoding? retrievalEncoding,
        ElasticRelationQueryFieldDocumentScope documentScope,
        ElasticRelationQueryFieldSemanticCapabilities semanticCapabilities,
        FieldPath? reversedSuffixField,
        string? semanticProfile,
        ElasticRelationQueryMissingValueBehavior missingValueBehavior,
        ObservationValue? missingValueSentinel,
        ElasticRelationQueryNullValueBehavior nullValueBehavior,
        ObservationValue? nullValueSentinel,
        ElasticRelationQueryNestedScopeEvidence? nestedScope)
    {
        try
        {
            return new(
                field.Input.Id,
                sourceField,
                queryField,
                mappingKind,
                retrievalKind,
                retrievalEncoding,
                documentScope,
                semanticCapabilities,
                reversedSuffixField,
                semanticProfile,
                missingValueBehavior,
                missingValueSentinel,
                nullValueBehavior,
                nullValueSentinel,
                nestedScope);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                $"Field mapping is inconsistent: {exception.Message}",
                field.Input.Id,
                field.Input.Field.Path,
                FieldSetting(field.Input.Id));
            return null;
        }
    }

    void ValidatePlacedInput()
    {
        var bindingMatches = placedInput.Placement.Bindings.Count(binding => binding.Id == placedInput.Binding.Id) == 1;
        var sourceMatches = placedInput.Placement.SourceInstances.Count(source => source.Id == placedInput.Source.Id) == 1;
        if (!bindingMatches
            || !sourceMatches
            || placedInput.Binding.Source != placedInput.Source.Id
            || placedInput.Binding.Kind != RelationQuerySourcePlacementBindingKind.SourceSet
            || placedInput.Source.TargetProfile.Target != ElasticRelationQueryTargetProfile.Target
            || placedInput.Source.TargetProfile.Id != ElasticRelationQueryTargetProfile.ProfileId
            || !placedInput.Source.TargetProfile.HasSameSemantics(ElasticRelationQueryTargetProfile.Default)
            || !ReferencesPlan(placedInput.Placement.Plan, placedInput.Plan))
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
                "Elasticsearch binding authoring requires one exact source-set placement using the canonical Elasticsearch target profile and compiled plan.",
                placedInput.Binding.Input);
        }
        var expected = placedInput.Fields.Select(static field => field.Input.Id).ToHashSet();
        var placed = placedInput.Binding.Fields.Select(static field => field.Input).ToHashSet();
        if (!expected.SetEquals(placed))
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
                "The placed source fields do not exactly match the plan-bound demanded field contracts.",
                placedInput.Binding.Input);
        }
    }

    void ValidateScalarConfiguration(
        ElasticAuthoringValue<string> index,
        ElasticAuthoringValue<ElasticRelationQuerySourceMode> source,
        ElasticAuthoringValue<int> window,
        ElasticAuthoringValue<int> pageSize,
        ElasticAuthoringValue<ElasticRelationQueryPaginationConsistency> consistency,
        ElasticAuthoringValue<ElasticRelationQueryFieldMappingConvention> fieldMapping,
        ElasticAuthoringValue<string> convention)
    {
        if (string.IsNullOrWhiteSpace(index.Value))
        {
            Error(ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing, "The Elasticsearch index name cannot be empty.", setting: IndexSetting);
        }
        else
        {
            try
            {
                ElasticRelationQueryStorageBinding.RequireConcreteIndexName(index.Value, IndexSetting);
            }
            catch (ArgumentException exception)
            {
                Error(
                    ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                    exception.Message,
                    setting: IndexSetting);
            }
        }
        if (!Enum.IsDefined(source.Value))
        {
            Error(ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict, "Unsupported Elasticsearch _source mode.", setting: SourceModeSetting);
        }

        if (window.Value <= 0)
        {
            Error(ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict, "The maximum result window must be positive.", setting: MaximumResultWindowSetting);
        }

        if (pageSize.Value <= 0 || pageSize.Value > window.Value)
        {
            Error(ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict, "The maximum page size must be positive and cannot exceed the result window.", setting: MaximumPageSizeSetting);
        }

        if (!Enum.IsDefined(consistency.Value))
        {
            Error(ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict, "Unsupported Elasticsearch pagination consistency.", setting: PaginationConsistencySetting);
        }

        if (!Enum.IsDefined(fieldMapping.Value))
        {
            Error(ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict, "Unsupported Elasticsearch field-mapping convention.", setting: FieldMappingConventionSetting);
        }

        if (string.IsNullOrWhiteSpace(convention.Value))
        {
            Error(ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict, "The convention-set identity cannot be empty.", setting: ConventionSetting);
        }
    }

    bool TryOwnField(RelationQueryFieldInputContract field, string setting)
    {
        var owned = placedInput.Fields.SingleOrDefault(candidate => candidate.Input.Id == field.Input.Id);
        if (owned is not null
            && owned.Input.Field.Path == field.Input.Field.Path
            && owned.Input.Binding == field.Input.Binding
            && owned.Input.Field.Shape == field.Input.Field.Shape)
        {
            return true;
        }

        Error(
            ElasticRelationQueryBindingAuthoringDiagnosticCodes.FieldUnknown,
            $"Compiled field input '{field.Input.Id.Value}' does not belong to the selected placed semantic shape.",
            field.Input.Id,
            field.Input.Field.Path,
            setting);
        return false;
    }

    bool TryGetField(FieldPath semanticPath, string setting, out RelationQueryFieldInputContract field)
    {
        if (semanticPath.Segments.IsDefaultOrEmpty)
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                "A semantic field selector path cannot be empty.",
                placedInput.Binding.Input,
                setting: setting);
            field = null!;
            return false;
        }
        if (placedInput.TryGetField(semanticPath, out var found))
        {
            field = found;
            return true;
        }
        Error(
            ElasticRelationQueryBindingAuthoringDiagnosticCodes.FieldUnknown,
            $"Semantic path '{semanticPath}' is not a demanded field on the selected placed input.",
            placedInput.Binding.Input,
            semanticPath,
            setting);
        field = null!;
        return false;
    }

    bool TryResolveTypedField<T, TValue>(
        RelationQueryPlacedInput<T> input,
        Expression<Func<T, TValue>> selector,
        string setting,
        out RelationQueryFieldInputContract field)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(selector);
        if (input.Binding.Id != placedInput.Binding.Id
            || !ReferenceEquals(input.Plan, placedInput.Plan)
            || input.Shape != placedInput.Shape)
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
                "The typed selector belongs to a different placed input or compiled plan.",
                input.Binding.Input,
                setting: setting);
            field = null!;
            return false;
        }
        try
        {
            field = input.GetField(selector);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                $"The typed semantic selector cannot resolve an exact demanded field: {exception.Message}",
                input.Binding.Input,
                setting: setting);
            field = null!;
            return false;
        }
    }

    ElasticRelationQueryBindingId DeriveId(
        ElasticEffectiveConfiguration effective,
        RelationQueryPlanComponentFingerprint planFingerprint,
        RelationQuerySourcePlacementFingerprint placementFingerprint)
    {
        StringBuilder canonical = new();
        Append(canonical, DerivedIdAuthority);
        Append(canonical, ElasticRelationQueryStorageBinding.CurrentSchemaVersion);
        Append(canonical, planFingerprint.Algorithm);
        Append(canonical, planFingerprint.Canonicalization);
        Append(canonical, planFingerprint.Value);
        Append(canonical, placementFingerprint.Algorithm);
        Append(canonical, placementFingerprint.Canonicalization);
        Append(canonical, placementFingerprint.Value);
        Append(canonical, placedInput.Source.Id.Value);
        Append(canonical, placedInput.Binding.Id.Value);
        Append(canonical, ElasticRelationQueryTargetProfile.Target.Value);
        Append(canonical, ElasticRelationQueryTargetProfile.ProfileId.Value);
        Append(canonical, effective.IndexName.Value);
        Append(canonical, ((int)effective.SourceMode.Value).ToString(CultureInfo.InvariantCulture));
        Append(canonical, effective.MaximumResultWindow.Value.ToString(CultureInfo.InvariantCulture));
        Append(canonical, effective.MaximumPageSize.Value.ToString(CultureInfo.InvariantCulture));
        Append(canonical, ((int)effective.PaginationConsistency.Value).ToString(CultureInfo.InvariantCulture));
        Append(canonical, effective.ConventionSetVersion.Value);
        Append(canonical, effective.Fields.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var field in effective.Fields)
        {
            Append(canonical, field.Binding.Input.Value);
            Append(canonical, field.Binding.SourceField);
            Append(canonical, field.Binding.QueryField);
            Append(canonical, ((int)field.Binding.MappingKind).ToString(CultureInfo.InvariantCulture));
            Append(canonical, ((int)field.Binding.RetrievalKind).ToString(CultureInfo.InvariantCulture));
            Append(
                canonical,
                field.Binding.RetrievalEncoding is null
                    ? null
                    : ((int)field.Binding.RetrievalEncoding.Value).ToString(CultureInfo.InvariantCulture));
            Append(canonical, ((int)field.Binding.DocumentScope).ToString(CultureInfo.InvariantCulture));
            Append(canonical, ((int)field.Binding.SemanticCapabilities).ToString(CultureInfo.InvariantCulture));
            Append(canonical, field.Binding.ReversedSuffixField);
            Append(canonical, field.Binding.SemanticProfile);
            Append(canonical, ((int)field.Binding.MissingValueBehavior).ToString(CultureInfo.InvariantCulture));
            Append(canonical, field.Binding.MissingValueSentinel);
            Append(canonical, ((int)field.Binding.NullValueBehavior).ToString(CultureInfo.InvariantCulture));
            Append(canonical, field.Binding.NullValueSentinel);
            Append(canonical, field.Binding.NestedScope is null ? "0" : "1");
            if (field.Binding.NestedScope is { } nested)
            {
                Append(canonical, nested.NestedPath);
                Append(canonical, ((int)nested.CorrelationGuarantee).ToString(CultureInfo.InvariantCulture));
                Append(canonical, ((int)nested.NullElementBehavior).ToString(CultureInfo.InvariantCulture));
                Append(canonical, ((int)nested.EmptyCollectionBehavior).ToString(CultureInfo.InvariantCulture));
                Append(canonical, nested.ChildFields.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var child in nested.ChildFields)
                {
                    Append(canonical, child.ElementPath);
                    Append(canonical, child.QueryField);
                    Append(canonical, ((int)child.MappingKind).ToString(CultureInfo.InvariantCulture));
                    Append(canonical, ((int)child.SemanticCapabilities).ToString(CultureInfo.InvariantCulture));
                    Append(canonical, child.SemanticProfile);
                    Append(canonical, ((int)child.MissingValueBehavior).ToString(CultureInfo.InvariantCulture));
                    Append(canonical, ((int)child.NullValueBehavior).ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        foreach (var decision in effective.Decisions.OrderBy(static decision => decision.Setting, StringComparer.Ordinal))
        {
            Append(canonical, decision.Setting);
            Append(canonical, ((int)decision.Origin).ToString(CultureInfo.InvariantCulture));
            Append(canonical, decision.Authority);
        }
        return new($"elastic-binding/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))}");
    }

    static bool TryInferRetrievalEncoding(
        ValueContract? contract,
        out ElasticRelationQueryFieldValueEncoding encoding)
    {
        encoding = contract?.GetEffectiveType() switch
        {
            ScalarTypeRef { Kind: ScalarTypeKind.Bool } => ElasticRelationQueryFieldValueEncoding.JsonBoolean,
            ScalarTypeRef { Kind: ScalarTypeKind.Int32 or ScalarTypeKind.Int64 } => ElasticRelationQueryFieldValueEncoding.JsonInt64,
            ScalarTypeRef { Kind: ScalarTypeKind.String or ScalarTypeKind.Guid } => ElasticRelationQueryFieldValueEncoding.JsonString,
            ScalarTypeRef { Kind: ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant } => ElasticRelationQueryFieldValueEncoding.CanonicalTemporalString,
            _ => default
        };
        return contract?.GetEffectiveType() is ScalarTypeRef
        {
            Kind: ScalarTypeKind.Bool
                or ScalarTypeKind.Int32
                or ScalarTypeKind.Int64
                or ScalarTypeKind.String
                or ScalarTypeKind.Guid
                or ScalarTypeKind.Date
                or ScalarTypeKind.DateTime
                or ScalarTypeKind.Instant
        };
    }

    ImmutableArray<RelationQueryConfigurationDecision> FieldDecisions(
        string prefix,
        RelationQueryConfigurationValueOrigin origin,
        string authority,
        ElasticNestedDeclaration? nested) =>
    [
        Decision(prefix + "sourceField", origin, authority),
        Decision(prefix + "queryField", origin, authority),
        Decision(prefix + "mappingKind", origin, authority),
        Decision(prefix + "retrievalKind", origin, authority),
        Decision(prefix + "retrievalEncoding", origin, authority),
        Decision(prefix + "documentScope", origin, authority),
        Decision(prefix + "semanticCapabilities", origin, authority),
        Decision(prefix + "reversedSuffixField", origin, authority),
        Decision(prefix + "semanticProfile", origin, authority),
        Decision(prefix + "missingValueBehavior", origin, authority),
        Decision(prefix + "missingValueSentinel", origin, authority),
        Decision(prefix + "nullValueBehavior", origin, authority),
        Decision(prefix + "nullValueSentinel", origin, authority),
        Decision(prefix + "nestedScope", origin, authority)
    ];

    RelationQueryArtifactAuthoringResult<ElasticRelationQueryStorageBinding> Failure() =>
        new(null, [.. diagnostics]);

    bool HasErrors => diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    bool TrySetExplicit(string setting)
    {
        if (explicitSettings.Add(setting))
        {
            return true;
        }

        Error(
            ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
            $"The explicit Elasticsearch setting '{setting}' is declared more than once.",
            setting: setting);
        return false;
    }

    void Error(
        string code,
        string message,
        RelationQueryInputId? input = null,
        FieldPath? semanticPath = null,
        string? setting = null) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Error, message, input, semanticPath, setting));

    ElasticAuthoringValue<T> Explicit<T>(T value) =>
        new(value, RelationQueryConfigurationValueOrigin.Explicit, explicitAuthority);

    static ElasticAuthoringValue<T> Scoped<T>(T value, string authority) =>
        new(value, RelationQueryConfigurationValueOrigin.ScopedProfile, authority);

    static ElasticAuthoringValue<T> Adapter<T>(T value, string authority) =>
        new(value, RelationQueryConfigurationValueOrigin.AdapterConvention, authority);

    static RelationQueryConfigurationDecision Configuration<T>(string setting, ElasticAuthoringValue<T> effective) =>
        Decision(setting, effective.Origin, effective.Authority);

    static RelationQueryConfigurationDecision Decision(
        string setting,
        RelationQueryConfigurationValueOrigin origin,
        string authority) => new(setting, origin, authority);

    internal static string FieldSetting(RelationQueryInputId input) => "field/" + input.Value;

    static string FieldSetting(FieldPath path) => "field/semantic/" + SafePathKey(path);

    static string PathKey(FieldPath path) => ElasticRelationQueryStorageBinding.FieldPathKey(path);

    internal static string SafePathKey(FieldPath path) =>
        path.Segments.IsDefaultOrEmpty ? "invalid" : PathKey(path);

    bool ValidatePhysicalPath(
        FieldPath path,
        RelationQueryFieldInputContract field,
        string setting,
        bool requireDirectField = false)
    {
        try
        {
            ElasticRelationQueryStorageBinding.RequirePhysicalFieldPath(path, setting);
            if (requireDirectField && path.Segments.Length != 1)
            {
                throw new ArgumentException(
                    "A nested element child path must contain exactly one direct field segment.",
                    setting);
            }
            return true;
        }
        catch (ArgumentException exception)
        {
            Error(
                ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                exception.Message,
                field.Input.Id,
                field.Input.Field.Path,
                setting);
            return false;
        }
    }

    static bool ReferencesPlan(RelationQueryCompiledPlanReference reference, CompiledRelationQueryPlan plan) =>
        Equals(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(reference),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(plan)));

    static void Append(StringBuilder builder, string? value) => builder
        .Append(value?.Length ?? -1)
        .Append(':')
        .Append(value)
        .Append(';');

    static void Append(StringBuilder builder, FieldPath? path)
    {
        if (path is null)
        {
            Append(builder, (string?)null);
            return;
        }
        Append(builder, path.Value.Segments.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var segment in path.Value.Segments)
        {
            Append(builder, ((int)segment.Kind).ToString(CultureInfo.InvariantCulture));
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

        Append(builder, ((int)value.Value.Kind).ToString(CultureInfo.InvariantCulture));
        switch (value.Value.Kind)
        {
            case ObservationValueKind.Int64:
                Append(builder, value.Value.Int64.ToString(CultureInfo.InvariantCulture));
                break;
            case ObservationValueKind.Double:
                Append(builder, value.Value.Double.ToString("R", CultureInfo.InvariantCulture));
                break;
            case ObservationValueKind.Bool:
                Append(builder, value.Value.Bool ? "1" : "0");
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

readonly record struct ElasticAuthoringValue<T>(
    T Value,
    RelationQueryConfigurationValueOrigin Origin,
    string Authority);

sealed class ElasticFieldDeclaration(
    RelationQueryFieldInputContract field,
    Action<string> reportDuplicate)
{
    readonly HashSet<string> explicitSettings = new(StringComparer.Ordinal);

    public RelationQueryFieldInputContract Field { get; } = field;
    public ElasticAuthoringValue<FieldPath?>? SourceField { get; set; }
    public ElasticAuthoringValue<FieldPath?>? QueryField { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryFieldMappingKind>? MappingKind { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryFieldRetrievalKind>? RetrievalKind { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryFieldValueEncoding?>? RetrievalEncoding { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryFieldDocumentScope>? DocumentScope { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryFieldSemanticCapabilities>? SemanticCapabilities { get; set; }
    public ElasticAuthoringValue<FieldPath?>? ReversedSuffixField { get; set; }
    public ElasticAuthoringValue<string?>? SemanticProfile { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryMissingValueBehavior>? MissingValueBehavior { get; set; }
    public ElasticAuthoringValue<ObservationValue?>? MissingValueSentinel { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryNullValueBehavior>? NullValueBehavior { get; set; }
    public ElasticAuthoringValue<ObservationValue?>? NullValueSentinel { get; set; }
    public ElasticNestedDeclaration? Nested { get; set; }

    public bool TrySet(string setting)
    {
        if (explicitSettings.Add(setting))
        {
            return true;
        }

        reportDuplicate(setting);
        return false;
    }

    public void ReportNestedDuplicate(string setting) => reportDuplicate("nested/" + setting);
}

sealed class ElasticNestedDeclaration(
    FieldPath nestedPath,
    Action<string> reportDuplicate)
{
    readonly HashSet<string> explicitSettings = new(StringComparer.Ordinal);

    public FieldPath NestedPath { get; } = nestedPath;
    public ElasticAuthoringValue<ElasticRelationQueryNestedCorrelationGuarantee>? Correlation { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryNestedAbsenceBehavior>? NullElements { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryEmptyCollectionBehavior>? EmptyCollections { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryMissingValueBehavior>? OuterMissing { get; set; }
    public ElasticAuthoringValue<ElasticRelationQueryNullValueBehavior>? OuterNull { get; set; }
    public List<ElasticNestedChildDeclaration> Children { get; } = [];

    public bool TrySet(string setting)
    {
        if (explicitSettings.Add(setting))
        {
            return true;
        }

        reportDuplicate(setting);
        return false;
    }

    public void ReportDuplicate(string setting) => reportDuplicate(setting);
}

sealed record ElasticNestedChildDeclaration(
    FieldPath ElementPath,
    FieldPath QueryField,
    ElasticRelationQueryFieldMappingKind MappingKind,
    ElasticRelationQueryFieldSemanticCapabilities SemanticCapabilities,
    string SemanticProfile,
    ElasticRelationQueryNestedAbsenceBehavior MissingValueBehavior,
    ElasticRelationQueryNestedAbsenceBehavior NullValueBehavior,
    RelationQueryConfigurationValueOrigin Origin,
    string Authority);

sealed record ElasticEffectiveField(
    ElasticRelationQueryFieldBinding Binding,
    ImmutableArray<RelationQueryConfigurationDecision> Decisions);

sealed record ElasticEffectiveConfiguration(
    ElasticAuthoringValue<ElasticRelationQueryBindingId>? Id,
    ElasticAuthoringValue<string> IndexName,
    ElasticAuthoringValue<ElasticRelationQuerySourceMode> SourceMode,
    ElasticAuthoringValue<int> MaximumResultWindow,
    ElasticAuthoringValue<int> MaximumPageSize,
    ElasticAuthoringValue<ElasticRelationQueryPaginationConsistency> PaginationConsistency,
    ImmutableArray<ElasticEffectiveField> Fields,
    ElasticAuthoringValue<string> ConventionSetVersion,
    IReadOnlyList<RelationQueryConfigurationDecision> Decisions);
