using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Stable diagnostic codes emitted while authoring Cosmos relation/query storage bindings.</summary>
public static class CosmosRelationQueryBindingAuthoringDiagnosticCodes
{
    /// <summary>The placed input is stale, malformed, or does not use the exact Cosmos target profile.</summary>
    public const string PlacementMismatch = "relationQuery.authoring.cosmos.placementMismatch";

    /// <summary>A required physical field or setting has no effective value.</summary>
    public const string BindingMissing = "relationQuery.authoring.cosmos.bindingMissing";

    /// <summary>A semantic selector does not identify a demanded field on the selected placed input.</summary>
    public const string FieldUnknown = "relationQuery.authoring.cosmos.fieldUnknown";

    /// <summary>A same-tier declaration repeats a scalar, field, or evidence setting.</summary>
    public const string BindingDuplicate = "relationQuery.authoring.cosmos.bindingDuplicate";

    /// <summary>A typed or structural selector cannot be interpreted as the requested path category.</summary>
    public const string SelectorInvalid = "relationQuery.authoring.cosmos.selectorInvalid";

    /// <summary>An option or override conflicts with another effective configuration fact.</summary>
    public const string ConfigurationConflict = "relationQuery.authoring.cosmos.configurationConflict";

    /// <summary>The normalized effective facts could not construct the immutable storage-binding artifact.</summary>
    public const string ArtifactInvalid = "relationQuery.authoring.cosmos.artifactInvalid";
}

/// <summary>Convention used to map demanded semantic fields without local field overrides.</summary>
public enum CosmosRelationQueryFieldMappingConvention
{
    /// <summary>Map each demanded field to the same relative physical document path as its semantic path.</summary>
    SemanticPath = 0,

    /// <summary>Require every demanded field to have an explicit local or scoped physical mapping.</summary>
    Explicit = 1
}

/// <summary>Scoped Cosmos binding-authoring values applied between adapter conventions and local declarations.</summary>
/// <remarks>
/// The constructor validates only the profile authority. Target-specific names, paths, ranges, enum values, and
/// cross-setting combinations are retained as configuration input and reported through structured diagnostics when
/// <see cref="CosmosRelationQueryStorageBindingBuilder.Build"/> resolves the profile against a placed input.
/// </remarks>
public sealed class CosmosRelationQueryBindingAuthoringOptions
{
    /// <summary>Creates a named, immutable scoped Cosmos authoring profile.</summary>
    /// <param name="authority">Stable profile identity and version attributed to every supplied option.</param>
    /// <param name="bindingId">Optional non-default scoped storage-binding identity, validated by the builder.</param>
    /// <param name="containerName">Optional non-empty scoped physical Cosmos container name, validated by the builder.</param>
    /// <param name="rootAlias">Optional simple Cosmos SQL root alias, validated by the builder.</param>
    /// <param name="identityPath">Optional identity path relative to <paramref name="documentRoot"/>.</param>
    /// <param name="documentRoot">Optional physical property root containing semantic values.</param>
    /// <param name="partitionPath">Optional partition-key path relative to the complete physical document.</param>
    /// <param name="fieldPaths">
    /// Optional semantic-to-physical field-path overrides relative to <paramref name="documentRoot"/>; paths are
    /// validated against the selected demanded fields by the builder.
    /// </param>
    /// <param name="fieldMappingConvention">
    /// Optional convention for otherwise-unmapped demanded fields; <see langword="null"/> keeps the adapter convention.
    /// </param>
    /// <param name="stableUniqueOrderingPaths">
    /// Optional property-only paths relative to <paramref name="documentRoot"/> that prove stable unique ordering.
    /// </param>
    /// <param name="exactOrderingPaths">
    /// Optional property-only paths relative to <paramref name="documentRoot"/> that prove exact Cosmos ordering.
    /// </param>
    /// <param name="maximumInputRows">
    /// Optional exact aggregate input-row bound from 1 through
    /// <see cref="CosmosRelationQueryTargetProfile.MaximumExactInteger"/>, validated by the builder.
    /// </param>
    /// <param name="missingValueEncoding">Optional physical missing-value encoding.</param>
    /// <param name="nullValueEncoding">Optional physical null encoding.</param>
    /// <param name="conventionSetVersion">Optional convention-set attribution override.</param>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="authority"/> is empty or white space.</exception>
    public CosmosRelationQueryBindingAuthoringOptions(
        string authority,
        CosmosRelationQueryBindingId? bindingId = null,
        string? containerName = null,
        string? rootAlias = null,
        FieldPath? identityPath = null,
        FieldPath? documentRoot = null,
        FieldPath? partitionPath = null,
        IReadOnlyDictionary<FieldPath, FieldPath>? fieldPaths = null,
        CosmosRelationQueryFieldMappingConvention? fieldMappingConvention = null,
        ImmutableArray<FieldPath> stableUniqueOrderingPaths = default,
        ImmutableArray<FieldPath> exactOrderingPaths = default,
        long? maximumInputRows = null,
        CosmosMissingValueEncoding? missingValueEncoding = null,
        CosmosNullValueEncoding? nullValueEncoding = null,
        string? conventionSetVersion = null)
    {
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        var mappings = fieldPaths ?? ImmutableDictionary<FieldPath, FieldPath>.Empty;
        BindingId = bindingId;
        ContainerName = containerName;
        RootAlias = rootAlias;
        IdentityPath = identityPath;
        DocumentRoot = documentRoot;
        PartitionPath = partitionPath;
        FieldPaths = mappings.ToImmutableDictionary();
        FieldMappingConvention = fieldMappingConvention;
        StableUniqueOrderingPaths = stableUniqueOrderingPaths.IsDefault ? [] : stableUniqueOrderingPaths;
        ExactOrderingPaths = exactOrderingPaths.IsDefault ? [] : exactOrderingPaths;
        MaximumInputRows = maximumInputRows;
        MissingValueEncoding = missingValueEncoding;
        NullValueEncoding = nullValueEncoding;
        ConventionSetVersion = conventionSetVersion;
    }

    /// <summary>Stable profile identity and version.</summary>
    public string Authority { get; }

    /// <summary>Optional scoped storage-binding identity.</summary>
    public CosmosRelationQueryBindingId? BindingId { get; }

    /// <summary>Optional scoped physical Cosmos container name.</summary>
    public string? ContainerName { get; }

    /// <summary>Optional scoped Cosmos SQL root alias.</summary>
    public string? RootAlias { get; }

    /// <summary>Optional scoped identity path relative to <see cref="DocumentRoot"/>.</summary>
    public FieldPath? IdentityPath { get; }

    /// <summary>Optional scoped physical property root containing semantic values.</summary>
    public FieldPath? DocumentRoot { get; }

    /// <summary>Optional scoped partition-key path relative to the complete physical document.</summary>
    public FieldPath? PartitionPath { get; }

    /// <summary>Scoped semantic-to-physical field mappings.</summary>
    public ImmutableDictionary<FieldPath, FieldPath> FieldPaths { get; }

    /// <summary>Optional scoped convention for otherwise-unmapped demanded fields.</summary>
    public CosmosRelationQueryFieldMappingConvention? FieldMappingConvention { get; }

    /// <summary>Scoped stable-unique ordering evidence.</summary>
    public ImmutableArray<FieldPath> StableUniqueOrderingPaths { get; }

    /// <summary>Scoped exact-ordering evidence.</summary>
    public ImmutableArray<FieldPath> ExactOrderingPaths { get; }

    /// <summary>Optional scoped exact aggregate input-row bound.</summary>
    public long? MaximumInputRows { get; }

    /// <summary>Optional scoped physical missing-value encoding.</summary>
    public CosmosMissingValueEncoding? MissingValueEncoding { get; }

    /// <summary>Optional scoped physical null encoding.</summary>
    public CosmosNullValueEncoding? NullValueEncoding { get; }

    /// <summary>Optional scoped convention-set attribution.</summary>
    public string? ConventionSetVersion { get; }
}

/// <summary>Adapter-owned entry point for authoring Cosmos bindings from exact placed inputs.</summary>
public static class CosmosRelationQueryBinding
{
    /// <summary>Stable authority used for explicit local declarations when no consumer authority is supplied.</summary>
    public const string LocalDeclarationAuthority = "cohesive.relations.authoring/local/v1";

    /// <summary>Starts Cosmos binding authoring for one exact plan-bound placed input.</summary>
    /// <param name="placedInput">Plan-bound source placement to bind to a Cosmos container.</param>
    /// <param name="options">Optional scoped authoring profile.</param>
    /// <param name="explicitAuthority">Stable authority attributed to explicit local declarations.</param>
    /// <returns>A mutable, session-local Cosmos storage-binding builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="placedInput"/> or <paramref name="explicitAuthority"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="explicitAuthority"/> is empty or white space.</exception>
    public static CosmosRelationQueryStorageBindingBuilder For(
        RelationQueryPlacedInput placedInput,
        CosmosRelationQueryBindingAuthoringOptions? options = null,
        string explicitAuthority = LocalDeclarationAuthority) =>
        new(placedInput, options, explicitAuthority);

    /// <summary>Starts typed Cosmos binding authoring for one exact CLR-backed placed input.</summary>
    /// <typeparam name="T">CLR type represented by the placed semantic shape.</typeparam>
    /// <param name="placedInput">Typed plan-bound source placement to bind to a Cosmos container.</param>
    /// <param name="options">Optional scoped authoring profile.</param>
    /// <param name="explicitAuthority">Stable authority attributed to explicit local declarations.</param>
    /// <returns>A typed, mutable, session-local Cosmos storage-binding builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="placedInput"/> or <paramref name="explicitAuthority"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="explicitAuthority"/> is empty or white space.</exception>
    public static CosmosRelationQueryStorageBindingBuilder<T> For<T>(
        RelationQueryPlacedInput<T> placedInput,
        CosmosRelationQueryBindingAuthoringOptions? options = null,
        string explicitAuthority = LocalDeclarationAuthority)
        where T : notnull =>
        new(placedInput, options, explicitAuthority);
}

/// <summary>Typed fluent facade over one Cosmos storage-binding authoring session.</summary>
/// <typeparam name="T">CLR type represented by the selected placed semantic input.</typeparam>
public sealed class CosmosRelationQueryStorageBindingBuilder<T>
    where T : notnull
{
    readonly RelationQueryPlacedInput<T> placedInput;
    readonly CosmosRelationQueryStorageBindingBuilder inner;

    internal CosmosRelationQueryStorageBindingBuilder(
        RelationQueryPlacedInput<T> placedInput,
        CosmosRelationQueryBindingAuthoringOptions? options,
        string explicitAuthority)
    {
        this.placedInput = Guard.RequireNotNull(placedInput);
        inner = new(placedInput, options, explicitAuthority);
    }

    /// <summary>Declares the physical Cosmos container.</summary>
    /// <param name="name">Non-empty physical container name.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> Container(string name)
    {
        inner.Container(name);
        return this;
    }

    /// <summary>Overrides the deterministic convention-derived storage-binding identity.</summary>
    /// <param name="id">Stable explicit binding identity.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> WithId(CosmosRelationQueryBindingId id)
    {
        inner.WithId(id);
        return this;
    }

    /// <summary>Overrides the Cosmos SQL root alias.</summary>
    /// <param name="alias">Simple Cosmos SQL identifier.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is empty or white space.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> RootAlias(string alias)
    {
        inner.RootAlias(alias);
        return this;
    }

    /// <summary>Uses a typed demanded semantic field's effective physical mapping as document identity.</summary>
    /// <typeparam name="TValue">CLR value selected as identity.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic identity field.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> Identity<TValue>(
        System.Linq.Expressions.Expression<Func<T, TValue>> selector)
    {
        inner.Identity(placedInput, selector);
        return this;
    }

    /// <summary>Declares a physical identity path relative to the effective document root.</summary>
    /// <param name="path">Physical property-only identity path.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> IdentityDocumentPath(FieldPath path)
    {
        inner.IdentityDocumentPath(path);
        return this;
    }

    /// <summary>Declares the optional property root containing semantic document values.</summary>
    /// <param name="path">Physical property-only document-root path.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> AtDocumentRoot(FieldPath path)
    {
        inner.AtDocumentRoot(path);
        return this;
    }

    /// <summary>Explicitly selects the physical document root itself as the semantic value root.</summary>
    /// <returns>This typed builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder<T> AtDocumentRoot()
    {
        inner.AtDocumentRoot();
        return this;
    }

    /// <summary>Uses a typed demanded semantic field's effective physical mapping as the partition path.</summary>
    /// <typeparam name="TValue">CLR value selected as the partition key.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic partition field.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> Partition<TValue>(
        System.Linq.Expressions.Expression<Func<T, TValue>> selector)
    {
        inner.Partition(placedInput, selector);
        return this;
    }

    /// <summary>Declares a partition-key path relative to the complete physical document.</summary>
    /// <param name="path">Physical property-only partition path.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> PartitionDocumentPath(FieldPath path)
    {
        inner.PartitionDocumentPath(path);
        return this;
    }

    /// <summary>Enables semantic-path mapping for every demanded field not explicitly overridden.</summary>
    /// <returns>This typed builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder<T> FieldsBySemanticPath()
    {
        inner.FieldsBySemanticPath();
        return this;
    }

    /// <summary>Disables field conventions so every demanded field requires an explicit or scoped mapping.</summary>
    /// <returns>This typed builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder<T> FieldsExplicitly()
    {
        inner.FieldsExplicitly();
        return this;
    }

    /// <summary>Overrides one typed semantic field's physical Cosmos document path.</summary>
    /// <typeparam name="TValue">CLR value selected by the semantic property chain.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic field.</param>
    /// <param name="documentPath">Physical path relative to the effective document root.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> Field<TValue>(
        System.Linq.Expressions.Expression<Func<T, TValue>> selector,
        FieldPath documentPath)
    {
        inner.Field(placedInput, selector, documentPath);
        return this;
    }

    /// <summary>Overrides one structurally selected semantic field's physical Cosmos document path.</summary>
    /// <param name="semanticPath">Demanded semantic path on the selected placed input.</param>
    /// <param name="documentPath">Physical path relative to the effective document root.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentException">A path is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> Field(FieldPath semanticPath, FieldPath documentPath)
    {
        inner.Field(semanticPath, documentPath);
        return this;
    }

    /// <summary>Declares one typed field's effective physical mapping as a stable unique ordering key.</summary>
    /// <typeparam name="TValue">CLR value selected by the semantic property chain.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic field.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> StableUnique<TValue>(
        System.Linq.Expressions.Expression<Func<T, TValue>> selector)
    {
        inner.StableUnique(placedInput, selector);
        return this;
    }

    /// <summary>Declares one physical property path as a stable unique ordering key.</summary>
    /// <param name="path">Physical property-only path relative to the effective document root.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> StableUniqueDocumentPath(FieldPath path)
    {
        inner.StableUniqueDocumentPath(path);
        return this;
    }

    /// <summary>Declares one typed field's effective physical mapping as preserving exact Cosmos ordering.</summary>
    /// <typeparam name="TValue">CLR value selected by the semantic property chain.</typeparam>
    /// <param name="selector">Readable CLR property chain selecting the semantic field.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> ExactOrdering<TValue>(
        System.Linq.Expressions.Expression<Func<T, TValue>> selector)
    {
        inner.ExactOrdering(placedInput, selector);
        return this;
    }

    /// <summary>Declares one physical property path as preserving exact Cosmos ordering.</summary>
    /// <param name="path">Physical property-only path relative to the effective document root.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> ExactOrderingDocumentPath(FieldPath path)
    {
        inner.ExactOrderingDocumentPath(path);
        return this;
    }

    /// <summary>Declares an exact upper bound on rows participating in one Cosmos aggregation.</summary>
    /// <param name="value">Positive bound no greater than the exact Cosmos integer range.</param>
    /// <returns>This typed builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder<T> MaximumInputRows(long value)
    {
        inner.MaximumInputRows(value);
        return this;
    }

    /// <summary>Overrides the physical missing-value encoding.</summary>
    /// <param name="encoding">Physical missing-value representation.</param>
    /// <returns>This typed builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder<T> MissingValues(CosmosMissingValueEncoding encoding)
    {
        inner.MissingValues(encoding);
        return this;
    }

    /// <summary>Overrides the physical null-value encoding.</summary>
    /// <param name="encoding">Physical null-value representation.</param>
    /// <returns>This typed builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder<T> NullValues(CosmosNullValueEncoding encoding)
    {
        inner.NullValues(encoding);
        return this;
    }

    /// <summary>Overrides the convention-set identity retained by the binding.</summary>
    /// <param name="version">Stable convention identity and version.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="version"/> is empty or white space.</exception>
    public CosmosRelationQueryStorageBindingBuilder<T> ConventionSetVersion(string version)
    {
        inner.ConventionSetVersion(version);
        return this;
    }

    /// <summary>Builds a well-formed exact-affinity Cosmos binding or returns structured diagnostics.</summary>
    /// <returns>
    /// A plan- and placement-bound artifact or deterministic authoring diagnostics. Branch-specific semantic
    /// realizability remains a compiler obligation.
    /// </returns>
    public RelationQueryArtifactAuthoringResult<CosmosRelationQueryStorageBinding> Build() => inner.Build();
}

/// <summary>
/// Mutable, plan-bound authoring session that lowers Cosmos-specific physical decisions to one immutable
/// <see cref="CosmosRelationQueryStorageBinding"/>.
/// </summary>
/// <remarks>
/// The builder is not thread-safe. It validates intrinsic binding facts and exact placement affinity; plan-wide
/// capability sufficiency remains authoritative in realization and native compilation. The produced artifact is
/// immutable and independently persistable. Source and placement-binding identities are inherited from the placed
/// input rather than repeated as configuration decisions. Direct fluent calls reject null, empty, or default
/// programmer-contract arguments immediately; scoped profile values and cross-setting compatibility are validated
/// by <see cref="Build"/> and returned as structured diagnostics.
/// </remarks>
public sealed class CosmosRelationQueryStorageBindingBuilder
{
    const string DerivedIdAuthority = "cohesive.relations.cosmos/binding-id-convention/v3";
    const string TargetSetting = "target";
    const string TargetProfileSetting = "targetProfile";
    const string ContainerSetting = "containerName";
    const string RootAliasSetting = "rootAlias";
    const string IdentityPathSetting = "identityPath";
    const string DocumentRootSetting = "documentRoot";
    const string PartitionPathSetting = "partitionPath";
    const string StableOrderingPrefix = "stableUniqueOrderingPath/";
    const string ExactOrderingPrefix = "exactOrderingPath/";
    const string MaximumRowsSetting = "maximumInputRows";
    const string MissingEncodingSetting = "missingValueEncoding";
    const string NullEncodingSetting = "nullValueEncoding";
    const string ConventionSetting = "conventionSetVersion";
    const string BindingIdSetting = "bindingId";
    const string FieldsConventionSetting = "fieldMappingConvention";

    readonly RelationQueryPlacedInput placedInput;
    readonly CosmosRelationQueryBindingAuthoringOptions? options;
    readonly string explicitAuthority;
    readonly List<RelationQueryArtifactAuthoringDiagnostic> diagnostics = [];
    readonly Dictionary<RelationQueryInputId, FieldOverride> explicitFields = [];
    readonly Dictionary<RelationQueryInputId, RelationQueryConfigurationDecision> stableFields = [];
    readonly Dictionary<RelationQueryInputId, RelationQueryConfigurationDecision> exactFields = [];
    readonly Dictionary<FieldPath, RelationQueryConfigurationDecision> stablePaths = [];
    readonly Dictionary<FieldPath, RelationQueryConfigurationDecision> exactPaths = [];
    readonly HashSet<string> explicitScalarDeclarations = new(StringComparer.Ordinal);

    Effective<CosmosRelationQueryBindingId>? explicitId;
    Effective<string>? containerName;
    Effective<string>? rootAlias;
    Effective<FieldPath>? identityPath;
    Effective<RelationQueryInputId>? identityField;
    Effective<FieldPath?>? documentRoot;
    Effective<FieldPath?>? partitionPath;
    Effective<RelationQueryInputId>? partitionField;
    Effective<CosmosRelationQueryFieldMappingConvention>? fieldMappingConvention;
    Effective<long?>? maximumInputRows;
    Effective<CosmosMissingValueEncoding>? missingValueEncoding;
    Effective<CosmosNullValueEncoding>? nullValueEncoding;
    Effective<string>? conventionSetVersion;

    internal CosmosRelationQueryStorageBindingBuilder(
        RelationQueryPlacedInput placedInput,
        CosmosRelationQueryBindingAuthoringOptions? options,
        string explicitAuthority)
    {
        this.placedInput = Guard.RequireNotNull(placedInput);
        this.options = options;
        this.explicitAuthority = Guard.RequireNotNullOrWhiteSpace(explicitAuthority);
    }

    /// <summary>Declares the physical Cosmos container.</summary>
    /// <param name="name">Non-empty physical container name.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    public CosmosRelationQueryStorageBindingBuilder Container(string name)
    {
        var validated = Guard.RequireNotNullOrWhiteSpace(name);
        if (TryDeclareScalar(ContainerSetting))
        {
            containerName = Explicit(validated);
        }

        return this;
    }

    /// <summary>Overrides the deterministic convention-derived storage-binding identity.</summary>
    /// <param name="id">Stable explicit binding identity.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    public CosmosRelationQueryStorageBindingBuilder WithId(CosmosRelationQueryBindingId id)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("An explicit Cosmos binding identity cannot be default.", nameof(id));
        }

        if (TryDeclareScalar(BindingIdSetting))
        {
            explicitId = Explicit(id);
        }

        return this;
    }

    /// <summary>Overrides the Cosmos SQL root alias.</summary>
    /// <param name="alias">Simple Cosmos SQL identifier.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is empty or white space.</exception>
    public CosmosRelationQueryStorageBindingBuilder RootAlias(string alias)
    {
        var validated = Guard.RequireNotNullOrWhiteSpace(alias);
        if (TryDeclareScalar(RootAliasSetting))
        {
            rootAlias = Explicit(validated);
        }

        return this;
    }

    /// <summary>Declares a physical identity path relative to the effective document root.</summary>
    /// <param name="path">Physical property-only identity path.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder IdentityDocumentPath(FieldPath path)
    {
        RequireNonEmpty(path, nameof(path));
        if (TryDeclareScalar(IdentityPathSetting))
        {
            identityPath = Explicit(path);
            identityField = null;
        }
        return this;
    }

    /// <summary>Uses the effective physical mapping of one demanded semantic field as document identity.</summary>
    /// <param name="field">Exact demanded field owned by the selected placed input.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public CosmosRelationQueryStorageBindingBuilder Identity(RelationQueryFieldInputContract field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (TryOwnField(field, IdentityPathSetting) && TryDeclareScalar(IdentityPathSetting))
        {
            identityField = Explicit(field.Input.Id);
            identityPath = null;
        }
        return this;
    }

    /// <summary>Uses a typed demanded semantic field's effective physical mapping as document identity.</summary>
    /// <typeparam name="T">CLR type represented by the placed input.</typeparam>
    /// <typeparam name="TValue">CLR value selected as identity.</typeparam>
    /// <param name="input">Typed view of the same placed input supplied to <see cref="CosmosRelationQueryBinding.For"/>.</param>
    /// <param name="selector">Readable CLR property chain selecting the semantic identity field.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    internal CosmosRelationQueryStorageBindingBuilder Identity<T, TValue>(
        RelationQueryPlacedInput<T> input,
        System.Linq.Expressions.Expression<Func<T, TValue>> selector)
        where T : notnull
    {
        if (TryResolveTypedField(input, selector, IdentityPathSetting, out var field))
        {
            Identity(field);
        }

        return this;
    }

    /// <summary>Declares the optional property root containing semantic document values.</summary>
    /// <param name="path">Physical property-only document-root path.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder AtDocumentRoot(FieldPath path)
    {
        RequireNonEmpty(path, nameof(path));
        if (TryDeclareScalar(DocumentRootSetting))
        {
            documentRoot = Explicit<FieldPath?>(path);
        }

        return this;
    }

    /// <summary>Explicitly selects the physical document root itself as the semantic value root.</summary>
    /// <returns>This builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder AtDocumentRoot()
    {
        if (TryDeclareScalar(DocumentRootSetting))
        {
            documentRoot = Explicit<FieldPath?>(null);
        }

        return this;
    }

    /// <summary>Declares an optional partition-key path relative to the complete physical document.</summary>
    /// <param name="path">Physical property-only partition path.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder PartitionDocumentPath(FieldPath path)
    {
        RequireNonEmpty(path, nameof(path));
        if (TryDeclareScalar(PartitionPathSetting))
        {
            partitionPath = Explicit<FieldPath?>(path);
            partitionField = null;
        }
        return this;
    }

    /// <summary>Uses one demanded semantic field's effective physical mapping as the partition path.</summary>
    /// <param name="field">Exact demanded field owned by the selected placed input.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public CosmosRelationQueryStorageBindingBuilder Partition(RelationQueryFieldInputContract field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (TryOwnField(field, PartitionPathSetting) && TryDeclareScalar(PartitionPathSetting))
        {
            partitionField = Explicit(field.Input.Id);
            partitionPath = null;
        }
        return this;
    }

    /// <summary>Uses a typed demanded semantic field's effective physical mapping as the partition path.</summary>
    /// <typeparam name="T">CLR type represented by the placed input.</typeparam>
    /// <typeparam name="TValue">CLR value selected as the partition key.</typeparam>
    /// <param name="input">Typed view of the same placed input supplied to <see cref="CosmosRelationQueryBinding.For"/>.</param>
    /// <param name="selector">Readable CLR property chain selecting the semantic partition field.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    internal CosmosRelationQueryStorageBindingBuilder Partition<T, TValue>(
        RelationQueryPlacedInput<T> input,
        System.Linq.Expressions.Expression<Func<T, TValue>> selector)
        where T : notnull
    {
        if (TryResolveTypedField(input, selector, PartitionPathSetting, out var field))
        {
            Partition(field);
        }

        return this;
    }

    /// <summary>Enables semantic-path mapping for every demanded field not explicitly overridden.</summary>
    /// <returns>This builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder FieldsBySemanticPath()
    {
        if (TryDeclareScalar(FieldsConventionSetting))
        {
            fieldMappingConvention = Explicit(CosmosRelationQueryFieldMappingConvention.SemanticPath);
        }

        return this;
    }

    /// <summary>Disables field conventions so every demanded field requires an explicit or scoped mapping.</summary>
    /// <returns>This builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder FieldsExplicitly()
    {
        if (TryDeclareScalar(FieldsConventionSetting))
        {
            fieldMappingConvention = Explicit(CosmosRelationQueryFieldMappingConvention.Explicit);
        }

        return this;
    }

    /// <summary>Overrides one exact demanded field's physical Cosmos document path.</summary>
    /// <param name="field">Exact demanded field owned by the selected placed input.</param>
    /// <param name="documentPath">Physical path relative to the effective document root.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder Field(
        RelationQueryFieldInputContract field,
        FieldPath documentPath)
    {
        ArgumentNullException.ThrowIfNull(field);
        RequireNonEmpty(documentPath, nameof(documentPath));
        if (!TryOwnField(field, FieldSetting(field.Input.Id)))
        {
            return this;
        }

        if (!explicitFields.TryAdd(
                field.Input.Id,
                new(field, documentPath, Decision(FieldSetting(field.Input.Id), RelationQueryConfigurationValueOrigin.Explicit, explicitAuthority))))
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                $"Compiled field input '{field.Input.Id.Value}' has more than one explicit Cosmos mapping.",
                field.Input.Id,
                field.Input.Field.Path,
                FieldSetting(field.Input.Id));
        }
        return this;
    }

    /// <summary>Overrides one structurally selected semantic field's physical Cosmos document path.</summary>
    /// <param name="semanticPath">Demanded semantic path on the selected placed input.</param>
    /// <param name="documentPath">Physical path relative to the effective document root.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A path is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder Field(FieldPath semanticPath, FieldPath documentPath)
    {
        RequireNonEmpty(semanticPath, nameof(semanticPath));
        RequireNonEmpty(documentPath, nameof(documentPath));
        if (TryGetField(semanticPath, FieldSetting(semanticPath), out var field))
        {
            Field(field, documentPath);
        }

        return this;
    }

    /// <summary>Overrides one typed semantic field's physical Cosmos document path.</summary>
    /// <typeparam name="T">CLR type represented by the placed input.</typeparam>
    /// <typeparam name="TValue">CLR value selected by the semantic property chain.</typeparam>
    /// <param name="input">Typed view of the same placed input supplied to <see cref="CosmosRelationQueryBinding.For"/>.</param>
    /// <param name="selector">Readable CLR property chain selecting the semantic field.</param>
    /// <param name="documentPath">Physical path relative to the effective document root.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> is empty.</exception>
    internal CosmosRelationQueryStorageBindingBuilder Field<T, TValue>(
        RelationQueryPlacedInput<T> input,
        System.Linq.Expressions.Expression<Func<T, TValue>> selector,
        FieldPath documentPath)
        where T : notnull
    {
        RequireNonEmpty(documentPath, nameof(documentPath));
        if (TryResolveTypedField(input, selector, "field", out var field))
        {
            Field(field, documentPath);
        }

        return this;
    }

    /// <summary>Declares one demanded field's effective physical mapping as a stable unique ordering key.</summary>
    /// <param name="field">Exact demanded field owned by the selected placed input.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public CosmosRelationQueryStorageBindingBuilder StableUnique(RelationQueryFieldInputContract field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (TryOwnField(field, StableOrderingPrefix + field.Input.Id.Value))
        {
            AddFieldEvidence(stableFields, field, StableOrderingPrefix);
        }

        return this;
    }

    /// <summary>Declares one typed field's effective physical mapping as a stable unique ordering key.</summary>
    /// <typeparam name="T">CLR type represented by the placed input.</typeparam>
    /// <typeparam name="TValue">CLR value selected by the semantic property chain.</typeparam>
    /// <param name="input">Typed view of the same placed input supplied to <see cref="CosmosRelationQueryBinding.For"/>.</param>
    /// <param name="selector">Readable CLR property chain selecting the semantic field.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    internal CosmosRelationQueryStorageBindingBuilder StableUnique<T, TValue>(
        RelationQueryPlacedInput<T> input,
        System.Linq.Expressions.Expression<Func<T, TValue>> selector)
        where T : notnull
    {
        if (TryResolveTypedField(input, selector, StableOrderingPrefix + "typed", out var field))
        {
            StableUnique(field);
        }

        return this;
    }

    /// <summary>Declares one physical property path as a stable unique ordering key.</summary>
    /// <param name="path">Physical property-only path relative to the effective document root.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder StableUniqueDocumentPath(FieldPath path)
    {
        RequireNonEmpty(path, nameof(path));
        AddPathEvidence(stablePaths, path, StableOrderingPrefix);
        return this;
    }

    /// <summary>Declares one demanded field's effective physical mapping as preserving exact Cosmos ordering.</summary>
    /// <param name="field">Exact demanded field owned by the selected placed input.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public CosmosRelationQueryStorageBindingBuilder ExactOrdering(RelationQueryFieldInputContract field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (TryOwnField(field, ExactOrderingPrefix + field.Input.Id.Value))
        {
            AddFieldEvidence(exactFields, field, ExactOrderingPrefix);
        }

        return this;
    }

    /// <summary>Declares one typed field's effective physical mapping as preserving exact Cosmos ordering.</summary>
    /// <typeparam name="T">CLR type represented by the placed input.</typeparam>
    /// <typeparam name="TValue">CLR value selected by the semantic property chain.</typeparam>
    /// <param name="input">Typed view of the same placed input supplied to <see cref="CosmosRelationQueryBinding.For"/>.</param>
    /// <param name="selector">Readable CLR property chain selecting the semantic field.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    internal CosmosRelationQueryStorageBindingBuilder ExactOrdering<T, TValue>(
        RelationQueryPlacedInput<T> input,
        System.Linq.Expressions.Expression<Func<T, TValue>> selector)
        where T : notnull
    {
        if (TryResolveTypedField(input, selector, ExactOrderingPrefix + "typed", out var field))
        {
            ExactOrdering(field);
        }

        return this;
    }

    /// <summary>Declares one physical property path as preserving exact Cosmos ordering.</summary>
    /// <param name="path">Physical property-only path relative to the effective document root.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public CosmosRelationQueryStorageBindingBuilder ExactOrderingDocumentPath(FieldPath path)
    {
        RequireNonEmpty(path, nameof(path));
        AddPathEvidence(exactPaths, path, ExactOrderingPrefix);
        return this;
    }

    /// <summary>Declares an exact upper bound on rows participating in one Cosmos aggregation.</summary>
    /// <param name="value">Positive bound no greater than the exact Cosmos integer range.</param>
    /// <returns>This builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder MaximumInputRows(long value)
    {
        if (TryDeclareScalar(MaximumRowsSetting))
        {
            maximumInputRows = Explicit<long?>(value);
        }

        return this;
    }

    /// <summary>Overrides the physical missing-value encoding.</summary>
    /// <param name="encoding">Physical missing-value representation.</param>
    /// <returns>This builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder MissingValues(CosmosMissingValueEncoding encoding)
    {
        if (TryDeclareScalar(MissingEncodingSetting))
        {
            missingValueEncoding = Explicit(encoding);
        }

        return this;
    }

    /// <summary>Overrides the physical null-value encoding.</summary>
    /// <param name="encoding">Physical null-value representation.</param>
    /// <returns>This builder.</returns>
    public CosmosRelationQueryStorageBindingBuilder NullValues(CosmosNullValueEncoding encoding)
    {
        if (TryDeclareScalar(NullEncodingSetting))
        {
            nullValueEncoding = Explicit(encoding);
        }

        return this;
    }

    /// <summary>Overrides the convention-set identity retained by the binding.</summary>
    /// <param name="version">Stable convention identity and version.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="version"/> is empty or white space.</exception>
    public CosmosRelationQueryStorageBindingBuilder ConventionSetVersion(string version)
    {
        var validated = Guard.RequireNotNullOrWhiteSpace(version);
        if (TryDeclareScalar(ConventionSetting))
        {
            conventionSetVersion = Explicit(validated);
        }

        return this;
    }

    /// <summary>Builds a well-formed exact-affinity Cosmos binding or returns structured diagnostics.</summary>
    /// <returns>
    /// A plan- and placement-bound artifact or deterministic authoring diagnostics. Branch-specific semantic
    /// realizability remains a compiler obligation.
    /// </returns>
    public RelationQueryArtifactAuthoringResult<CosmosRelationQueryStorageBinding> Build()
    {
        ValidatePlacedInput();
        var effective = ResolveEffectiveConfiguration();
        if (HasErrors)
        {
            return Failure();
        }

        try
        {
            var decisions = effective.Decisions;
            var compiledPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(placedInput.Plan));
            var placementFingerprint = placedInput.Placement.Fingerprint;
            var id = effective.Id?.Value
                     ?? DeriveId(effective, decisions, compiledPlanFingerprint, placementFingerprint);
            decisions.Add(Decision(
                BindingIdSetting,
                effective.Id is null
                    ? RelationQueryConfigurationValueOrigin.AdapterConvention
                    : effective.Id.Value.Origin,
                effective.Id is null ? DerivedIdAuthority : effective.Id.Value.Authority));
            var hasConsumerConfiguration = decisions.Any(static decision => decision.Origin is
                RelationQueryConfigurationValueOrigin.Explicit
                or RelationQueryConfigurationValueOrigin.ScopedProfile);
            var artifact = new CosmosRelationQueryStorageBinding(
                id,
                placedInput.Source.Id,
                placedInput.Binding.Id,
                CosmosRelationQueryTargetProfile.Target,
                CosmosRelationQueryTargetProfile.ProfileId,
                effective.ContainerName.Value,
                effective.RootAlias.Value,
                effective.IdentityPath.Value,
                [.. effective.Fields.Select(static field => new CosmosRelationQueryFieldBinding(field.Field.Input.Id, field.Path))],
                effective.DocumentRoot.Value,
                effective.PartitionPath.Value,
                effective.StableUniqueOrderingPaths,
                effective.ExactOrderingPaths,
                effective.MaximumInputRows.Value,
                effective.MissingValueEncoding.Value,
                effective.NullValueEncoding.Value,
                hasConsumerConfiguration
                    ? CosmosRelationQueryBindingOrigin.Explicit
                    : CosmosRelationQueryBindingOrigin.Convention,
                effective.ConventionSetVersion.Value,
                [.. decisions],
                compiledPlanFingerprint,
                placementFingerprint);
            return new(artifact, [.. diagnostics]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.ArtifactInvalid,
                $"Cosmos storage-binding construction failed: {exception.Message}");
            return Failure();
        }
    }

    EffectiveConfiguration ResolveEffectiveConfiguration()
    {
        var convention = CosmosRelationQueryStorageBinding.SemanticPathConventionSet;
        var optionAuthority = options?.Authority;
        var effectiveFieldConvention = fieldMappingConvention
                                       ?? (options?.FieldMappingConvention is { } configuredFieldConvention
                                           ? Scoped(configuredFieldConvention, optionAuthority!)
                                           : Adapter(
                                               CosmosRelationQueryFieldMappingConvention.SemanticPath,
                                               convention));
        if (!Enum.IsDefined(effectiveFieldConvention.Value))
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                $"Unsupported Cosmos field-mapping convention '{effectiveFieldConvention.Value}'.",
                setting: FieldsConventionSetting);
        }
        var effectiveAlias = rootAlias
                             ?? (options?.RootAlias is { } alias
                                 ? Scoped(alias, optionAuthority!)
                                 : Adapter("c", convention));
        var effectiveRoot = documentRoot
                            ?? (options?.DocumentRoot is { } root
                                ? Scoped<FieldPath?>(root, optionAuthority!)
                                : Adapter<FieldPath?>(null, convention));
        var effectiveMaximumRows = maximumInputRows
                                   ?? (options?.MaximumInputRows is { } rows
                                       ? Scoped<long?>(rows, optionAuthority!)
                                       : Adapter<long?>(null, convention));
        var effectiveMissing = missingValueEncoding
                               ?? (options?.MissingValueEncoding is { } missing
                                   ? Scoped(missing, optionAuthority!)
                                   : Adapter(CosmosMissingValueEncoding.OmittedProperty, convention));
        var effectiveNull = nullValueEncoding
                            ?? (options?.NullValueEncoding is { } nullEncoding
                                ? Scoped(nullEncoding, optionAuthority!)
                                : Adapter(CosmosNullValueEncoding.JsonNull, convention));
        var effectiveConvention = conventionSetVersion
                                  ?? (options?.ConventionSetVersion is { } configuredConvention
                                      ? Scoped(configuredConvention, optionAuthority!)
                                      : Adapter(convention, convention));
        var effectiveContainer = containerName
                                 ?? (options?.ContainerName is { } configuredContainer
                                     ? Scoped(configuredContainer, optionAuthority!)
                                     : null);
        if (effectiveContainer is null)
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
                "Cosmos binding authoring requires an explicit physical container name.",
                setting: ContainerSetting);
            effectiveContainer = Explicit(string.Empty);
        }
        else if (string.IsNullOrWhiteSpace(effectiveContainer.Value.Value))
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                "The Cosmos container name cannot be empty or white space.",
                setting: ContainerSetting);
        }

        var fields = ResolveFields(effectiveFieldConvention, convention);
        var fieldsById = fields.ToDictionary(static field => field.Field.Input.Id);
        var effectiveIdentity = ResolveIdentity(fieldsById, convention);
        var effectivePartition = ResolvePartition(fieldsById, effectiveRoot, convention);
        var stable = ResolveEvidence(options?.StableUniqueOrderingPaths ?? [], stablePaths, stableFields, fieldsById, StableOrderingPrefix);
        var exact = ResolveEvidence(options?.ExactOrderingPaths ?? [], exactPaths, exactFields, fieldsById, ExactOrderingPrefix);

        List<RelationQueryConfigurationDecision> decisions =
        [
            Decision(
                TargetSetting,
                RelationQueryConfigurationValueOrigin.AdapterConvention,
                CosmosRelationQueryTargetProfile.ProfileId.Value),
            Decision(
                TargetProfileSetting,
                RelationQueryConfigurationValueOrigin.AdapterConvention,
                CosmosRelationQueryTargetProfile.ProfileId.Value),
            Configuration(ContainerSetting, effectiveContainer.Value),
            Configuration(RootAliasSetting, effectiveAlias),
            Configuration(IdentityPathSetting, effectiveIdentity),
            Configuration(DocumentRootSetting, effectiveRoot),
            Configuration(PartitionPathSetting, effectivePartition),
            Configuration(MaximumRowsSetting, effectiveMaximumRows),
            Configuration(MissingEncodingSetting, effectiveMissing),
            Configuration(NullEncodingSetting, effectiveNull),
            Configuration(ConventionSetting, effectiveConvention)
        ];
        decisions.AddRange(fields.Select(static field => field.Decision));
        decisions.AddRange(stable.Decisions);
        decisions.AddRange(exact.Decisions);

        Effective<CosmosRelationQueryBindingId>? selectedId = explicitId;
        if (selectedId is null && options?.BindingId is { } optionId)
        {
            selectedId = Scoped(optionId, optionAuthority!);
        }

        if (selectedId is { } configuredId && string.IsNullOrWhiteSpace(configuredId.Value.Value))
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                "The Cosmos binding identity cannot be default.",
                setting: BindingIdSetting);
        }

        ValidateScalarConfiguration(
            effectiveAlias,
            effectiveIdentity,
            effectiveRoot,
            effectivePartition,
            effectiveMaximumRows,
            effectiveMissing,
            effectiveNull,
            effectiveConvention);

        return new(
            selectedId,
            effectiveContainer.Value,
            effectiveAlias,
            effectiveIdentity,
            effectiveRoot,
            effectivePartition,
            fields,
            stable.Paths,
            exact.Paths,
            effectiveMaximumRows,
            effectiveMissing,
            effectiveNull,
            effectiveConvention,
            decisions);
    }

    ImmutableArray<EffectiveField> ResolveFields(
        Effective<CosmosRelationQueryFieldMappingConvention> mappingConvention,
        string convention)
    {
        Dictionary<RelationQueryInputId, EffectiveField> fields = [];
        if (mappingConvention.Value == CosmosRelationQueryFieldMappingConvention.SemanticPath)
        {
            foreach (var field in placedInput.Fields)
            {
                fields[field.Input.Id] = new(
                    field,
                    field.Input.Field.Path,
                    Decision(
                        FieldSetting(field.Input.Id),
                        RelationQueryConfigurationValueOrigin.AdapterConvention,
                        convention));
            }
        }

        if (options is not null)
        {
            foreach (var mapping in options.FieldPaths.OrderBy(static pair => SafePathKey(pair.Key), StringComparer.Ordinal))
            {
                if (!TryGetField(mapping.Key, FieldSetting(mapping.Key), out var field))
                {
                    continue;
                }

                fields[field.Input.Id] = new(
                    field,
                    mapping.Value,
                    Decision(FieldSetting(field.Input.Id), RelationQueryConfigurationValueOrigin.ScopedProfile, options.Authority));
            }
        }

        foreach (var (input, field) in explicitFields)
        {
            fields[input] = new(field.Field, field.Path, field.Decision);
        }

        foreach (var expected in placedInput.Fields)
        {
            if (!fields.ContainsKey(expected.Input.Id))
            {
                Error(
                    CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
                    $"Demanded field '{expected.Input.Field.Path}' has no Cosmos document mapping.",
                    expected.Input.Id,
                    expected.Input.Field.Path,
                    FieldSetting(expected.Input.Id));
            }
        }

        foreach (var field in fields.Values)
        {
            ValidateDocumentSelector(field.Path, field.Field.Input.Id, field.Field.Input.Field.Path, field.Decision.Setting);
        }
        return [.. fields.Values.OrderBy(static field => field.Field.Input.Id.Value, StringComparer.Ordinal)];
    }

    Effective<FieldPath> ResolveIdentity(
        IReadOnlyDictionary<RelationQueryInputId, EffectiveField> fields,
        string convention)
    {
        if (identityField is { } selected)
        {
            return ResolveMappedField(selected, fields, IdentityPathSetting);
        }

        if (identityPath is { } explicitPath)
        {
            return explicitPath;
        }

        if (options?.IdentityPath is { } optionPath)
        {
            return Scoped(optionPath, options.Authority);
        }

        var selector = placedInput.Binding.Identity?.SourceSelector;
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var matches = fields.Values
                .Where(field => string.Equals(field.Field.Input.Field.Path.ToString(), selector, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 1)
            {
                return Adapter(matches[0].Path, convention);
            }

            try
            {
                return Adapter(FieldPath.Parse(selector), convention);
            }
            catch (ArgumentException)
            {
                Error(
                    CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                    "The placed identity selector cannot be interpreted as a Cosmos property path.",
                    placedInput.Binding.Input,
                    setting: IdentityPathSetting);
                return Adapter(FieldPath.FromField("invalid"), convention);
            }
        }

        Error(
            CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
            "Cosmos binding authoring requires an identity path or an unambiguous placed identity field.",
            placedInput.Binding.Input,
            setting: IdentityPathSetting);
        return Adapter(FieldPath.FromField("invalid"), convention);
    }

    Effective<FieldPath?> ResolvePartition(
        IReadOnlyDictionary<RelationQueryInputId, EffectiveField> fields,
        Effective<FieldPath?> documentRoot,
        string convention)
    {
        if (partitionField is { } selected)
        {
            var mapped = ResolveMappedField(selected, fields, PartitionPathSetting);
            return new(AtDocumentRoot(documentRoot.Value, mapped.Value), mapped.Origin, mapped.Authority);
        }
        if (partitionPath is { } explicitPath)
        {
            return explicitPath;
        }

        if (options?.PartitionPath is { } optionPath)
        {
            return Scoped<FieldPath?>(optionPath, options.Authority);
        }

        var selector = placedInput.Binding.Partition?.SourceSelector;
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Adapter<FieldPath?>(null, convention);
        }

        var matches = fields.Values
            .Where(field => string.Equals(field.Field.Input.Field.Path.ToString(), selector, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1)
        {
            return Adapter<FieldPath?>(AtDocumentRoot(documentRoot.Value, matches[0].Path), convention);
        }

        try
        {
            return Adapter<FieldPath?>(FieldPath.Parse(selector), convention);
        }
        catch (ArgumentException)
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                "The placed partition selector cannot be interpreted as a Cosmos property path.",
                placedInput.Binding.Input,
                setting: PartitionPathSetting);
            return Adapter<FieldPath?>(null, convention);
        }
    }

    static FieldPath AtDocumentRoot(FieldPath? documentRoot, FieldPath relativePath) =>
        documentRoot is { } root
            ? new([.. root.Segments, .. relativePath.Segments])
            : relativePath;

    ResolvedEvidence ResolveEvidence(
        ImmutableArray<FieldPath> optionPaths,
        IReadOnlyDictionary<FieldPath, RelationQueryConfigurationDecision> explicitDocumentPaths,
        IReadOnlyDictionary<RelationQueryInputId, RelationQueryConfigurationDecision> explicitFieldInputs,
        IReadOnlyDictionary<RelationQueryInputId, EffectiveField> effectiveFields,
        string prefix)
    {
        Dictionary<FieldPath, RelationQueryConfigurationDecision> resolved = [];
        if (options is not null)
        {
            HashSet<FieldPath> scopedPaths = [];
            foreach (var path in optionPaths)
            {
                if (path.Segments.IsDefaultOrEmpty)
                {
                    Error(
                        CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                        "A scoped Cosmos ordering-evidence path cannot be empty.",
                        placedInput.Binding.Input,
                        setting: prefix + "invalid");
                    continue;
                }
                if (!scopedPaths.Add(path))
                {
                    Error(
                        CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                        $"Physical path '{path}' repeats scoped Cosmos ordering evidence.",
                        placedInput.Binding.Input,
                        setting: prefix + PathKey(path));
                    continue;
                }
                resolved[path] = Decision(
                    prefix + PathKey(path),
                    RelationQueryConfigurationValueOrigin.ScopedProfile,
                    options.Authority);
            }
        }
        foreach (var (path, decision) in explicitDocumentPaths)
        {
            resolved[path] = decision;
        }

        foreach (var (input, decision) in explicitFieldInputs)
        {
            if (!effectiveFields.TryGetValue(input, out var field))
            {
                continue;
            }

            if (field.Path.Segments.IsDefaultOrEmpty)
            {
                Error(
                    CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                    "A field selected as Cosmos ordering evidence has an empty effective physical path.",
                    input,
                    field.Field.Input.Field.Path,
                    prefix + input.Value);
                continue;
            }
            if (resolved.TryGetValue(field.Path, out var existing)
                && existing.Origin == RelationQueryConfigurationValueOrigin.Explicit)
            {
                Error(
                    CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                    $"Physical path '{field.Path}' repeats explicit Cosmos ordering evidence.",
                    input,
                    field.Field.Input.Field.Path,
                    prefix + PathKey(field.Path));
            }
            resolved[field.Path] = Decision(prefix + PathKey(field.Path), decision.Origin, decision.Authority);
        }
        foreach (var (path, decision) in resolved)
        {
            ValidatePropertyPath(path, placedInput.Binding.Input, semanticPath: null, decision.Setting);
        }

        return new(
            [.. resolved.Keys.OrderBy(PathKey, StringComparer.Ordinal)],
            [.. resolved.Values.OrderBy(static decision => decision.Setting, StringComparer.Ordinal)]);
    }

    void ValidatePlacedInput()
    {
        var bindingMatches = placedInput.Placement.Bindings.Count(binding => binding.Id == placedInput.Binding.Id) == 1;
        var sourceMatches = placedInput.Placement.SourceInstances.Count(source => source.Id == placedInput.Source.Id) == 1;
        if (!bindingMatches
            || !sourceMatches
            || placedInput.Binding.Source != placedInput.Source.Id
            || placedInput.Binding.Kind != RelationQuerySourcePlacementBindingKind.SourceSet
            || placedInput.Source.TargetProfile.Target != CosmosRelationQueryTargetProfile.Target
            || placedInput.Source.TargetProfile.Id != CosmosRelationQueryTargetProfile.ProfileId
            || !CosmosRelationQueryCompiler.ProfilesEquivalent(
                placedInput.Source.TargetProfile,
                CosmosRelationQueryTargetProfile.Default)
            || !ReferencesPlan(placedInput.Placement.Plan, placedInput.Plan))
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
                "Cosmos binding authoring requires one exact source-set placement using the canonical Cosmos target profile and compiled plan.",
                placedInput.Binding.Input);
        }

        var expected = placedInput.Fields.Select(static field => field.Input.Id).ToHashSet();
        var placed = placedInput.Binding.Fields.Select(static field => field.Input).ToHashSet();
        if (!expected.SetEquals(placed))
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
                "The placed source fields do not exactly match the plan-bound demanded field contracts.",
                placedInput.Binding.Input);
        }
    }

    void ValidateScalarConfiguration(
        Effective<string> alias,
        Effective<FieldPath> identity,
        Effective<FieldPath?> root,
        Effective<FieldPath?> partition,
        Effective<long?> rows,
        Effective<CosmosMissingValueEncoding> missing,
        Effective<CosmosNullValueEncoding> nullEncoding,
        Effective<string> convention)
    {
        try
        {
            CosmosSqlNames.RequireIdentifier(alias.Value, RootAliasSetting);
        }
        catch (ArgumentException exception)
        {
            Error(CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid, exception.Message, setting: RootAliasSetting);
        }
        ValidatePropertyPath(identity.Value, placedInput.Binding.Input, semanticPath: null, IdentityPathSetting);
        if (root.Value is { } rootPath)
        {
            ValidatePropertyPath(rootPath, placedInput.Binding.Input, semanticPath: null, DocumentRootSetting);
        }

        if (partition.Value is { } partitionPathValue)
        {
            ValidatePropertyPath(partitionPathValue, placedInput.Binding.Input, semanticPath: null, PartitionPathSetting);
        }

        if (rows.Value is <= 0 or > CosmosRelationQueryTargetProfile.MaximumExactInteger)
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict,
                $"The maximum input-row bound must be between 1 and {CosmosRelationQueryTargetProfile.MaximumExactInteger}.",
                setting: MaximumRowsSetting);
        }
        if (!Enum.IsDefined(missing.Value))
        {
            Error(CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict, "Unsupported Cosmos missing-value encoding.", setting: MissingEncodingSetting);
        }

        if (!Enum.IsDefined(nullEncoding.Value))
        {
            Error(CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict, "Unsupported Cosmos null-value encoding.", setting: NullEncodingSetting);
        }

        if (string.IsNullOrWhiteSpace(convention.Value))
        {
            Error(CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict, "The convention-set identity cannot be empty.", setting: ConventionSetting);
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
            CosmosRelationQueryBindingAuthoringDiagnosticCodes.FieldUnknown,
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
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                "A Cosmos semantic field path cannot be empty.",
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
            CosmosRelationQueryBindingAuthoringDiagnosticCodes.FieldUnknown,
            $"Semantic path '{semanticPath}' is not a demanded field on the selected placed input.",
            placedInput.Binding.Input,
            semanticPath,
            setting);
        field = null!;
        return false;
    }

    bool TryResolveTypedField<T, TValue>(
        RelationQueryPlacedInput<T> input,
        System.Linq.Expressions.Expression<Func<T, TValue>> selector,
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
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
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
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                $"The typed semantic selector cannot resolve an exact demanded field: {exception.Message}",
                input.Binding.Input,
                setting: setting);
            field = null!;
            return false;
        }
    }

    void AddFieldEvidence(
        IDictionary<RelationQueryInputId, RelationQueryConfigurationDecision> target,
        RelationQueryFieldInputContract field,
        string prefix)
    {
        var setting = prefix + field.Input.Id.Value;
        if (!target.TryAdd(
                field.Input.Id,
                Decision(setting, RelationQueryConfigurationValueOrigin.Explicit, explicitAuthority)))
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                $"Field '{field.Input.Field.Path}' repeats explicit Cosmos ordering evidence.",
                field.Input.Id,
                field.Input.Field.Path,
                setting);
        }
    }

    void AddPathEvidence(
        IDictionary<FieldPath, RelationQueryConfigurationDecision> target,
        FieldPath path,
        string prefix)
    {
        var setting = prefix + PathKey(path);
        if (!target.TryAdd(path, Decision(setting, RelationQueryConfigurationValueOrigin.Explicit, explicitAuthority)))
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                $"Physical path '{path}' repeats explicit Cosmos ordering evidence.",
                setting: setting);
        }
    }

    bool TryDeclareScalar(string setting)
    {
        if (explicitScalarDeclarations.Add(setting))
        {
            return true;
        }

        Error(
            CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
            $"Cosmos configuration setting '{setting}' has more than one explicit declaration; the first declaration is retained.",
            placedInput.Binding.Input,
            setting: setting);
        return false;
    }

    Effective<FieldPath> ResolveMappedField(
        Effective<RelationQueryInputId> selected,
        IReadOnlyDictionary<RelationQueryInputId, EffectiveField> fields,
        string setting)
    {
        if (fields.TryGetValue(selected.Value, out var field))
        {
            return new(field.Path, selected.Origin, selected.Authority);
        }

        Error(
            CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
            $"Selected field input '{selected.Value.Value}' has no effective Cosmos mapping.",
            selected.Value,
            setting: setting);
        return new(FieldPath.FromField("invalid"), selected.Origin, selected.Authority);
    }

    void ValidateDocumentSelector(
        FieldPath path,
        RelationQueryInputId input,
        FieldPath semanticPath,
        string setting)
    {
        try
        {
            CosmosRelationQueryStorageBinding.RequireDocumentSelectorPath(path, setting);
        }
        catch (ArgumentException exception)
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                exception.Message,
                input,
                semanticPath,
                setting);
        }
    }

    void ValidatePropertyPath(
        FieldPath path,
        RelationQueryInputId? input,
        FieldPath? semanticPath,
        string setting)
    {
        try
        {
            CosmosRelationQueryStorageBinding.RequirePropertyPath(path, setting);
        }
        catch (ArgumentException exception)
        {
            Error(
                CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                exception.Message,
                input,
                semanticPath,
                setting);
        }
    }

    CosmosRelationQueryBindingId DeriveId(
        EffectiveConfiguration effective,
        IEnumerable<RelationQueryConfigurationDecision> decisions,
        RelationQueryPlanComponentFingerprint planFingerprint,
        RelationQuerySourcePlacementFingerprint placementFingerprint)
    {
        StringBuilder canonical = new();
        Append(canonical, DerivedIdAuthority);
        Append(canonical, CosmosRelationQueryStorageBinding.CurrentSchemaVersion);
        Append(canonical, planFingerprint.Algorithm);
        Append(canonical, planFingerprint.Canonicalization);
        Append(canonical, planFingerprint.Value);
        Append(canonical, placementFingerprint.Algorithm);
        Append(canonical, placementFingerprint.Canonicalization);
        Append(canonical, placementFingerprint.Value);
        Append(canonical, placedInput.Source.Id.Value);
        Append(canonical, placedInput.Binding.Id.Value);
        Append(canonical, CosmosRelationQueryTargetProfile.Target.Value);
        Append(canonical, CosmosRelationQueryTargetProfile.ProfileId.Value);
        Append(canonical, effective.ContainerName.Value);
        Append(canonical, effective.RootAlias.Value);
        Append(canonical, effective.IdentityPath.Value);
        Append(canonical, effective.DocumentRoot.Value);
        Append(canonical, effective.PartitionPath.Value);
        foreach (var field in effective.Fields)
        {
            Append(canonical, field.Field.Input.Id.Value);
            Append(canonical, field.Path);
        }
        foreach (var path in effective.StableUniqueOrderingPaths)
        {
            Append(canonical, path);
        }

        foreach (var path in effective.ExactOrderingPaths)
        {
            Append(canonical, path);
        }

        Append(canonical, effective.MaximumInputRows.Value?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, ((int)effective.MissingValueEncoding.Value).ToString(CultureInfo.InvariantCulture));
        Append(canonical, ((int)effective.NullValueEncoding.Value).ToString(CultureInfo.InvariantCulture));
        Append(canonical, effective.ConventionSetVersion.Value);
        foreach (var decision in decisions.OrderBy(static decision => decision.Setting, StringComparer.Ordinal))
        {
            Append(canonical, decision.Setting);
            Append(canonical, ((int)decision.Origin).ToString(CultureInfo.InvariantCulture));
            Append(canonical, decision.Authority);
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new($"cosmos-binding/{Convert.ToHexStringLower(hash)}");
    }

    RelationQueryArtifactAuthoringResult<CosmosRelationQueryStorageBinding> Failure() =>
        new(null, [.. diagnostics]);

    bool HasErrors => diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    void Error(
        string code,
        string message,
        RelationQueryInputId? input = null,
        FieldPath? semanticPath = null,
        string? setting = null) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Error, message, input, semanticPath, setting));

    static string FieldSetting(RelationQueryInputId input) => "field/" + input.Value;

    static string FieldSetting(FieldPath path) => "field/semantic/" + SafePathKey(path);

    static string PathKey(FieldPath path) => CosmosRelationQueryStorageBinding.FieldPathKey(path);

    static string SafePathKey(FieldPath path) =>
        path.Segments.IsDefaultOrEmpty ? "invalid" : PathKey(path);

    static RelationQueryConfigurationDecision Configuration<T>(string setting, Effective<T> effective) =>
        Decision(setting, effective.Origin, effective.Authority);

    static RelationQueryConfigurationDecision Decision(
        string setting,
        RelationQueryConfigurationValueOrigin origin,
        string authority) => new(setting, origin, authority);

    static bool ReferencesPlan(
        RelationQueryCompiledPlanReference reference,
        CompiledRelationQueryPlan plan) =>
        Equals(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(reference),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(plan)));

    Effective<T> Explicit<T>(T value) =>
        new(value, RelationQueryConfigurationValueOrigin.Explicit, explicitAuthority);

    static Effective<T> Scoped<T>(T value, string authority) =>
        new(value, RelationQueryConfigurationValueOrigin.ScopedProfile, authority);

    static Effective<T> Adapter<T>(T value, string authority) =>
        new(value, RelationQueryConfigurationValueOrigin.AdapterConvention, authority);

    static void RequireNonEmpty(FieldPath path, string parameterName)
    {
        if (path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A Cosmos authoring path cannot be empty.", parameterName);
        }
    }

    static void Append(StringBuilder builder, string? value) => builder
        .Append(value?.Length ?? -1)
        .Append(':')
        .Append(value)
        .Append(';');

    static void Append(StringBuilder builder, FieldPath? path)
    {
        if (path is null)
        {
            Append(builder, value: null);
            return;
        }
        Append(builder, path.Value.Segments.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var segment in path.Value.Segments)
        {
            Append(builder, ((int)segment.Kind).ToString(CultureInfo.InvariantCulture));
            Append(builder, segment.Segment);
        }
    }

    readonly record struct Effective<T>(T Value, RelationQueryConfigurationValueOrigin Origin, string Authority);

    sealed record FieldOverride(
        RelationQueryFieldInputContract Field,
        FieldPath Path,
        RelationQueryConfigurationDecision Decision);

    sealed record EffectiveField(
        RelationQueryFieldInputContract Field,
        FieldPath Path,
        RelationQueryConfigurationDecision Decision);

    sealed record ResolvedEvidence(
        ImmutableArray<FieldPath> Paths,
        ImmutableArray<RelationQueryConfigurationDecision> Decisions);

    sealed record EffectiveConfiguration(
        Effective<CosmosRelationQueryBindingId>? Id,
        Effective<string> ContainerName,
        Effective<string> RootAlias,
        Effective<FieldPath> IdentityPath,
        Effective<FieldPath?> DocumentRoot,
        Effective<FieldPath?> PartitionPath,
        ImmutableArray<EffectiveField> Fields,
        ImmutableArray<FieldPath> StableUniqueOrderingPaths,
        ImmutableArray<FieldPath> ExactOrderingPaths,
        Effective<long?> MaximumInputRows,
        Effective<CosmosMissingValueEncoding> MissingValueEncoding,
        Effective<CosmosNullValueEncoding> NullValueEncoding,
        Effective<string> ConventionSetVersion,
        List<RelationQueryConfigurationDecision> Decisions);
}
