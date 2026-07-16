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

namespace Cohesive.Adapters.Cosmos;

/// <summary>Stable identity of a versioned Cosmos relation/query storage binding.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct CosmosRelationQueryBindingId
{
    /// <summary>Creates a Cosmos storage-binding identity.</summary>
    /// <param name="value">Stable versioned identity value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public CosmosRelationQueryBindingId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Stable versioned identity value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of one normalized Cosmos storage binding.</summary>
public sealed record CosmosRelationQueryBindingFingerprint
{
    /// <summary>Creates a binding fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    public CosmosRelationQueryBindingFingerprint(string algorithm, string canonicalization, string value)
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

/// <summary>Origin of a Cosmos storage-binding decision.</summary>
public enum CosmosRelationQueryBindingOrigin
{
    /// <summary>The binding was declared explicitly by the consumer.</summary>
    Explicit = 0,

    /// <summary>The binding was derived by a named deterministic convention set.</summary>
    Convention = 1
}

/// <summary>Physical encoding used when a semantic field is missing from a Cosmos document.</summary>
public enum CosmosMissingValueEncoding
{
    /// <summary>The JSON property is omitted and Cosmos SQL observes it as undefined.</summary>
    OmittedProperty = 0
}

/// <summary>Physical encoding used for semantic null in a Cosmos document.</summary>
public enum CosmosNullValueEncoding
{
    /// <summary>The property contains a JSON null value.</summary>
    JsonNull = 0
}

/// <summary>Physical Cosmos document selector for one exact compiled field input.</summary>
public sealed record CosmosRelationQueryFieldBinding
{
    /// <summary>Creates one compiled-input-to-document-selector binding.</summary>
    /// <param name="input">Exact compiled field-input identity.</param>
    /// <param name="documentPath">
    /// Structural path relative to the configured document root. Element segments identify traversal through an
    /// expanded array and are interpreted only in an expansion scope; direct SQL property access remains property-only.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> is default, or <paramref name="documentPath"/> is empty or malformed.
    /// </exception>
    public CosmosRelationQueryFieldBinding(RelationQueryInputId input, FieldPath documentPath)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A Cosmos field binding requires a compiled input identity.", nameof(input));
        Input = input;
        DocumentPath = CosmosRelationQueryStorageBinding.RequireDocumentSelectorPath(documentPath, nameof(documentPath));
    }

    /// <summary>Exact compiled field-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Structural path relative to the configured document root.</summary>
    public FieldPath DocumentPath { get; }
}

/// <summary>
/// Immutable, versioned binding from one exact placed semantic source to one Cosmos container and document shape.
/// </summary>
public sealed class CosmosRelationQueryStorageBinding
{
    /// <summary>Portable binding schema understood by the v1 Cosmos compiler.</summary>
    public const string CurrentSchemaVersion = "cohesive.relations.cosmos-binding/v1";

    /// <summary>Default deterministic convention set for semantic-path document bindings.</summary>
    public const string SemanticPathConventionSet = "cohesive.relations.cosmos/semantic-path-conventions/v1";

    /// <summary>Creates an explicit Cosmos storage binding.</summary>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="source">Physical source instance bound to the Cosmos container.</param>
    /// <param name="placementBinding">Exact plan-scoped placement binding interpreted by this binding.</param>
    /// <param name="target">Expected Cosmos target identity.</param>
    /// <param name="targetProfile">Expected target capability-profile identity.</param>
    /// <param name="containerName">Physical Cosmos container name retained for execution integration.</param>
    /// <param name="rootAlias">Simple SQL alias emitted after <c>FROM</c>.</param>
    /// <param name="identityPath">Stable identity property path relative to the document root.</param>
    /// <param name="fields">Exact compiled field-input selectors.</param>
    /// <param name="documentRoot">Optional property path below the physical document containing semantic values.</param>
    /// <param name="partitionPath">Optional physical partition-key path relative to the physical document.</param>
    /// <param name="stableUniqueOrderingPaths">
    /// Paths known to be unique and stable ordering keys; these are used to prove deterministic ordering and
    /// offset-page stability.
    /// </param>
    /// <param name="exactOrderingPaths">
    /// Paths whose physical Cosmos ordering is explicitly proven equivalent to canonical ordering semantics.
    /// </param>
    /// <param name="maximumInputRows">
    /// Optional asserted upper bound on rows participating in one query, used to prove exact numeric aggregates.
    /// </param>
    /// <param name="missingValueEncoding">Physical missing-value encoding.</param>
    /// <param name="nullValueEncoding">Physical null encoding.</param>
    /// <param name="origin">Whether the binding was explicit or convention-derived.</param>
    /// <param name="conventionSetVersion">
    /// Convention identity when <paramref name="origin"/> is <see cref="CosmosRelationQueryBindingOrigin.Convention"/>;
    /// otherwise an optional attribution string.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="containerName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity or string is empty; a path is invalid; <paramref name="fields"/> is empty, default, contains a
    /// <see langword="null"/> entry, or repeats an input; or origin and convention attribution conflict.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumInputRows"/> is outside the exact Cosmos numeric range, or
    /// <paramref name="missingValueEncoding"/>, <paramref name="nullValueEncoding"/>, or <paramref name="origin"/> is unsupported.
    /// </exception>
    public CosmosRelationQueryStorageBinding(
        CosmosRelationQueryBindingId id,
        RelationQuerySourceInstanceId source,
        RelationQuerySourcePlacementBindingId placementBinding,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        string containerName,
        string rootAlias,
        FieldPath identityPath,
        ImmutableArray<CosmosRelationQueryFieldBinding> fields,
        FieldPath? documentRoot = null,
        FieldPath? partitionPath = null,
        ImmutableArray<FieldPath> stableUniqueOrderingPaths = default,
        ImmutableArray<FieldPath> exactOrderingPaths = default,
        long? maximumInputRows = null,
        CosmosMissingValueEncoding missingValueEncoding = CosmosMissingValueEncoding.OmittedProperty,
        CosmosNullValueEncoding nullValueEncoding = CosmosNullValueEncoding.JsonNull,
        CosmosRelationQueryBindingOrigin origin = CosmosRelationQueryBindingOrigin.Explicit,
        string? conventionSetVersion = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value) || string.IsNullOrWhiteSpace(source.Value)
            || string.IsNullOrWhiteSpace(placementBinding.Value) || string.IsNullOrWhiteSpace(target.Value)
            || string.IsNullOrWhiteSpace(targetProfile.Value))
        {
            throw new ArgumentException("A Cosmos storage binding requires non-default identities.", nameof(id));
        }
        if (!Enum.IsDefined(missingValueEncoding))
            throw new ArgumentOutOfRangeException(nameof(missingValueEncoding), missingValueEncoding, "Unsupported Cosmos missing-value encoding.");
        if (!Enum.IsDefined(nullValueEncoding))
            throw new ArgumentOutOfRangeException(nameof(nullValueEncoding), nullValueEncoding, "Unsupported Cosmos null encoding.");
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported Cosmos binding origin.");
        if (origin == CosmosRelationQueryBindingOrigin.Convention && string.IsNullOrWhiteSpace(conventionSetVersion))
            throw new ArgumentException("A convention-derived Cosmos binding requires its convention-set identity.", nameof(conventionSetVersion));
        if (conventionSetVersion is not null && string.IsNullOrWhiteSpace(conventionSetVersion))
            throw new ArgumentException("A Cosmos convention-set identity cannot be empty.", nameof(conventionSetVersion));
        if (maximumInputRows is <= 0 or > CosmosRelationQueryTargetProfile.MaximumExactInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInputRows),
                maximumInputRows,
                $"A Cosmos input-row bound must be between 1 and {CosmosRelationQueryTargetProfile.MaximumExactInteger}.");
        }

        var normalizedFields = fields.IsDefault ? [] : fields;
        if (normalizedFields.IsDefaultOrEmpty || normalizedFields.Any(static field => field is null))
            throw new ArgumentException("A Cosmos storage binding requires one or more field bindings.", nameof(fields));
        if (normalizedFields.GroupBy(static field => field.Input).Any(static group => group.Count() > 1))
            throw new ArgumentException("A Cosmos storage binding cannot repeat a compiled field input.", nameof(fields));

        var normalizedStablePaths = stableUniqueOrderingPaths.IsDefault ? [] : stableUniqueOrderingPaths;
        foreach (var path in normalizedStablePaths)
            RequirePropertyPath(path, nameof(stableUniqueOrderingPaths));
        normalizedStablePaths =
        [
            .. normalizedStablePaths.Distinct().OrderBy(FieldPathKey, StringComparer.Ordinal)
        ];
        var normalizedExactOrderingPaths = exactOrderingPaths.IsDefault ? [] : exactOrderingPaths;
        foreach (var path in normalizedExactOrderingPaths)
            RequirePropertyPath(path, nameof(exactOrderingPaths));
        normalizedExactOrderingPaths =
        [
            .. normalizedExactOrderingPaths.Distinct().OrderBy(FieldPathKey, StringComparer.Ordinal)
        ];

        Id = id;
        Source = source;
        PlacementBinding = placementBinding;
        Target = target;
        TargetProfile = targetProfile;
        ContainerName = Guard.RequireNotNullOrWhiteSpace(containerName);
        RootAlias = CosmosSqlNames.RequireIdentifier(rootAlias, nameof(rootAlias));
        IdentityPath = RequirePropertyPath(identityPath, nameof(identityPath));
        Fields = [.. normalizedFields.OrderBy(static field => field.Input.Value, StringComparer.Ordinal)];
        DocumentRoot = documentRoot is { } root ? RequirePropertyPath(root, nameof(documentRoot)) : null;
        PartitionPath = partitionPath is { } partition ? RequirePropertyPath(partition, nameof(partitionPath)) : null;
        StableUniqueOrderingPaths = normalizedStablePaths;
        ExactOrderingPaths = normalizedExactOrderingPaths;
        MaximumInputRows = maximumInputRows;
        MissingValueEncoding = missingValueEncoding;
        NullValueEncoding = nullValueEncoding;
        Origin = origin;
        ConventionSetVersion = conventionSetVersion;
        Fingerprint = CosmosRelationQueryBindingFingerprinter.Compute(this);
    }

    /// <summary>
    /// Rehydrates a persisted Cosmos storage binding and verifies its schema version and fingerprint against the
    /// normalized binding facts.
    /// </summary>
    /// <param name="schemaVersion">Persisted binding schema version.</param>
    /// <param name="fingerprint">Persisted fingerprint that must match the normalized binding facts.</param>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="source">Physical source instance bound to the Cosmos container.</param>
    /// <param name="placementBinding">Exact plan-scoped placement binding interpreted by this binding.</param>
    /// <param name="target">Expected Cosmos target identity.</param>
    /// <param name="targetProfile">Expected target capability-profile identity.</param>
    /// <param name="containerName">Physical Cosmos container name retained for execution integration.</param>
    /// <param name="rootAlias">Simple SQL alias emitted after <c>FROM</c>.</param>
    /// <param name="identityPath">Stable identity property path relative to the document root.</param>
    /// <param name="fields">Exact compiled field-input selectors.</param>
    /// <param name="documentRoot">Optional property path below the physical document containing semantic values.</param>
    /// <param name="partitionPath">Optional physical partition-key path relative to the physical document.</param>
    /// <param name="stableUniqueOrderingPaths">Paths known to be unique and stable ordering keys.</param>
    /// <param name="exactOrderingPaths">Paths whose physical ordering is proven equivalent to canonical ordering.</param>
    /// <param name="maximumInputRows">Optional asserted upper bound on rows participating in one query.</param>
    /// <param name="missingValueEncoding">Physical missing-value encoding.</param>
    /// <param name="nullValueEncoding">Physical null encoding.</param>
    /// <param name="origin">Whether the binding was explicit or convention-derived.</param>
    /// <param name="conventionSetVersion">Attributable convention-set identity, when applicable.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/> or <paramref name="fingerprint"/> is <see langword="null"/>, or another
    /// required value is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is unsupported, <paramref name="fingerprint"/> does not match the normalized
    /// content, or another binding invariant is violated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric or enum binding fact is outside its supported range.</exception>
    [JsonConstructor]
    public CosmosRelationQueryStorageBinding(
        string schemaVersion,
        CosmosRelationQueryBindingFingerprint fingerprint,
        CosmosRelationQueryBindingId id,
        RelationQuerySourceInstanceId source,
        RelationQuerySourcePlacementBindingId placementBinding,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        string containerName,
        string rootAlias,
        FieldPath identityPath,
        ImmutableArray<CosmosRelationQueryFieldBinding> fields,
        FieldPath? documentRoot = null,
        FieldPath? partitionPath = null,
        ImmutableArray<FieldPath> stableUniqueOrderingPaths = default,
        ImmutableArray<FieldPath> exactOrderingPaths = default,
        long? maximumInputRows = null,
        CosmosMissingValueEncoding missingValueEncoding = CosmosMissingValueEncoding.OmittedProperty,
        CosmosNullValueEncoding nullValueEncoding = CosmosNullValueEncoding.JsonNull,
        CosmosRelationQueryBindingOrigin origin = CosmosRelationQueryBindingOrigin.Explicit,
        string? conventionSetVersion = null)
        : this(
            id,
            source,
            placementBinding,
            target,
            targetProfile,
            containerName,
            rootAlias,
            identityPath,
            fields,
            documentRoot,
            partitionPath,
            stableUniqueOrderingPaths,
            exactOrderingPaths,
            maximumInputRows,
            missingValueEncoding,
            nullValueEncoding,
            origin,
            conventionSetVersion)
    {
        var persistedSchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(persistedSchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported Cosmos relation/query storage-binding schema version '{persistedSchemaVersion}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(fingerprint);
        if (!Equals(fingerprint, Fingerprint))
        {
            throw new ArgumentException(
                "The Cosmos relation/query storage-binding fingerprint does not match normalized content.",
                nameof(fingerprint));
        }
    }

    /// <summary>Binding schema version.</summary>
    public string SchemaVersion => CurrentSchemaVersion;

    /// <summary>Stable versioned binding identity.</summary>
    public CosmosRelationQueryBindingId Id { get; }

    /// <summary>Physical source instance bound to the Cosmos container.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Exact plan-scoped placement binding interpreted by this binding.</summary>
    public RelationQuerySourcePlacementBindingId PlacementBinding { get; }

    /// <summary>Expected Cosmos interpretation-target identity.</summary>
    public RelationQueryTargetId Target { get; }

    /// <summary>Expected target capability-profile identity.</summary>
    public RelationQueryTargetProfileId TargetProfile { get; }

    /// <summary>Physical Cosmos container name.</summary>
    public string ContainerName { get; }

    /// <summary>Simple Cosmos SQL document alias.</summary>
    public string RootAlias { get; }

    /// <summary>Optional property path below the physical document containing semantic values.</summary>
    public FieldPath? DocumentRoot { get; }

    /// <summary>Stable identity path relative to <see cref="DocumentRoot"/>.</summary>
    public FieldPath IdentityPath { get; }

    /// <summary>Exact compiled field-input selectors in stable input-identity order.</summary>
    public ImmutableArray<CosmosRelationQueryFieldBinding> Fields { get; }

    /// <summary>Optional physical partition-key path relative to the physical document.</summary>
    public FieldPath? PartitionPath { get; }

    /// <summary>Paths proven to be stable unique ordering keys.</summary>
    public ImmutableArray<FieldPath> StableUniqueOrderingPaths { get; }

    /// <summary>Paths explicitly proven to preserve canonical ordering under Cosmos SQL.</summary>
    public ImmutableArray<FieldPath> ExactOrderingPaths { get; }

    /// <summary>Asserted maximum participating rows, or <see langword="null"/> when no cardinality proof exists.</summary>
    public long? MaximumInputRows { get; }

    /// <summary>Physical missing-value encoding.</summary>
    public CosmosMissingValueEncoding MissingValueEncoding { get; }

    /// <summary>Physical null encoding.</summary>
    public CosmosNullValueEncoding NullValueEncoding { get; }

    /// <summary>Whether this binding was explicit or convention-derived.</summary>
    public CosmosRelationQueryBindingOrigin Origin { get; }

    /// <summary>Attributable convention-set identity, or <see langword="null"/>.</summary>
    public string? ConventionSetVersion { get; }

    /// <summary>Deterministic identity of all normalized binding facts.</summary>
    public CosmosRelationQueryBindingFingerprint Fingerprint { get; }

    /// <summary>Resolves the physical document selector for an exact compiled field input.</summary>
    /// <param name="input">Compiled field-input identity.</param>
    /// <returns>
    /// The bound structural selector relative to <see cref="DocumentRoot"/>, including any element segments used
    /// within collection-expansion scopes.
    /// </returns>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is not bound.</exception>
    public FieldPath ResolveField(RelationQueryInputId input)
    {
        foreach (var field in Fields)
        {
            if (field.Input == input)
                return field.DocumentPath;
        }
        throw new KeyNotFoundException($"Compiled input '{input.Value}' has no Cosmos field binding.");
    }

    /// <summary>
    /// Creates a deterministic convention binding that maps every placed semantic field path to the same Cosmos
    /// document path. Explicit construction should be used when the physical shape differs.
    /// </summary>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="placement">Placed source-set binding whose fields are mapped.</param>
    /// <param name="target">Expected Cosmos target identity.</param>
    /// <param name="targetProfile">Expected target capability-profile identity.</param>
    /// <param name="containerName">Physical Cosmos container name.</param>
    /// <param name="identityPath">Stable identity property path relative to the document root.</param>
    /// <param name="rootAlias">Simple Cosmos SQL document alias.</param>
    /// <param name="documentRoot">Optional semantic-value document root.</param>
    /// <param name="partitionPath">Optional physical partition-key path.</param>
    /// <param name="stableUniqueOrderingPaths">Paths proven to be stable unique ordering keys.</param>
    /// <param name="exactOrderingPaths">Paths whose Cosmos ordering is proven equivalent to canonical ordering.</param>
    /// <param name="maximumInputRows">Optional asserted maximum participating rows.</param>
    /// <returns>A convention-attributed immutable storage binding.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="placement"/> or <paramref name="containerName"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The placement is not a source-set binding, contains no fields, or another supplied binding fact is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumInputRows"/> is outside the exact Cosmos numeric range.
    /// </exception>
    public static CosmosRelationQueryStorageBinding FromSemanticPathConvention(
        CosmosRelationQueryBindingId id,
        RelationQuerySourcePlacementBinding placement,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        string containerName,
        FieldPath identityPath,
        string rootAlias = "c",
        FieldPath? documentRoot = null,
        FieldPath? partitionPath = null,
        ImmutableArray<FieldPath> stableUniqueOrderingPaths = default,
        ImmutableArray<FieldPath> exactOrderingPaths = default,
        long? maximumInputRows = null)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.Kind != RelationQuerySourcePlacementBindingKind.SourceSet)
            throw new ArgumentException("The Cosmos semantic-path convention requires a source-set placement.", nameof(placement));
        return new(
            id,
            placement.Source,
            placement.Id,
            target,
            targetProfile,
            containerName,
            rootAlias,
            identityPath,
            [.. placement.Fields.Select(static field => new CosmosRelationQueryFieldBinding(field.Input, field.SemanticPath))],
            documentRoot,
            partitionPath,
            stableUniqueOrderingPaths,
            exactOrderingPaths,
            maximumInputRows,
            origin: CosmosRelationQueryBindingOrigin.Convention,
            conventionSetVersion: SemanticPathConventionSet);
    }

    internal static FieldPath RequirePropertyPath(FieldPath path, string parameterName)
    {
        if (path.Segments.IsDefaultOrEmpty
            || path.Segments.Any(static segment =>
                segment.Kind != SegmentKind.Field || string.IsNullOrEmpty(segment.Segment)))
        {
            throw new ArgumentException(
                "A Cosmos document path must contain one or more non-empty field segments and no element segments.",
                parameterName);
        }
        return path;
    }

    internal static FieldPath RequireDocumentSelectorPath(FieldPath path, string parameterName)
    {
        if (path.Segments.IsDefaultOrEmpty
            || path.Segments.Any(static segment => segment.Kind switch
            {
                SegmentKind.Field => string.IsNullOrEmpty(segment.Segment),
                SegmentKind.Element => segment.Segment is not null,
                _ => true
            }))
        {
            throw new ArgumentException(
                "A Cosmos document selector must contain one or more valid field or element segments.",
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
}

static class CosmosRelationQueryBindingFingerprinter
{
    const string Algorithm = "sha256";
    const string Canonicalization = "cohesive.relations.cosmos-binding/v1-c14n/v1";

    public static CosmosRelationQueryBindingFingerprint Compute(CosmosRelationQueryStorageBinding binding)
    {
        StringBuilder canonical = new();
        Append(canonical, Canonicalization);
        Append(canonical, binding.SchemaVersion);
        Append(canonical, binding.Id.Value);
        Append(canonical, binding.Source.Value);
        Append(canonical, binding.PlacementBinding.Value);
        Append(canonical, binding.Target.Value);
        Append(canonical, binding.TargetProfile.Value);
        Append(canonical, binding.ContainerName);
        Append(canonical, binding.RootAlias);
        Append(canonical, binding.DocumentRoot);
        Append(canonical, binding.IdentityPath);
        Append(canonical, binding.PartitionPath);
        Append(canonical, (int)binding.MissingValueEncoding);
        Append(canonical, (int)binding.NullValueEncoding);
        Append(canonical, (int)binding.Origin);
        Append(canonical, binding.ConventionSetVersion);
        Append(canonical, binding.Fields.Length);
        foreach (var field in binding.Fields)
        {
            Append(canonical, field.Input.Value);
            Append(canonical, field.DocumentPath);
        }
        Append(canonical, binding.StableUniqueOrderingPaths.Length);
        foreach (var path in binding.StableUniqueOrderingPaths)
            Append(canonical, path);
        Append(canonical, binding.ExactOrderingPaths.Length);
        foreach (var path in binding.ExactOrderingPaths)
            Append(canonical, path);
        Append(canonical, binding.MaximumInputRows?.ToString(CultureInfo.InvariantCulture));
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

    static void Append(StringBuilder builder, FieldPath? path)
    {
        if (path is null)
        {
            Append(builder, value: null);
            return;
        }
        Append(builder, path.Value.Segments.Length);
        foreach (var segment in path.Value.Segments)
        {
            Append(builder, (int)segment.Kind);
            Append(builder, segment.Segment);
        }
    }
}
