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

/// <summary>Physical element scope asserted for one structured Cosmos collection.</summary>
public enum CosmosRelationQueryCollectionElementScope
{
    /// <summary>No exact collection-element scope is asserted.</summary>
    Unproven = 0,

    /// <summary>Each current item is one element produced by iterating the bound JSON array.</summary>
    JsonArrayElement = 1
}

/// <summary>Physical guarantee that preserves predicates over one structured collection element.</summary>
public enum CosmosRelationQueryCollectionCorrelationGuarantee
{
    /// <summary>No same-element correlation guarantee is asserted.</summary>
    Unproven = 0,

    /// <summary>Every child predicate in one existential scope is evaluated against the same JSON array element.</summary>
    SameArrayElement = 1
}

/// <summary>Physical treatment of unavailable structured-collection values.</summary>
public enum CosmosRelationQueryStructuredCollectionAbsenceBehavior
{
    /// <summary>The binding does not prove how the unavailable value is handled.</summary>
    Unproven = 0,

    /// <summary>Ingestion rejects the unavailable value, so every stored document satisfies the canonical contract.</summary>
    ProhibitedByIngestion = 1
}

/// <summary>Physical representation of an empty structured collection.</summary>
public enum CosmosRelationQueryEmptyCollectionBehavior
{
    /// <summary>The binding does not prove how an empty collection is represented.</summary>
    Unproven = 0,

    /// <summary>An empty JSON array produces no collection elements.</summary>
    NoElements = 1
}

/// <summary>Exact canonical scalar domain stored by one Cosmos collection-element child field.</summary>
public enum CosmosRelationQueryCollectionElementValueDomain
{
    /// <summary>A JSON Boolean preserving canonical <see cref="ScalarTypeKind.Bool"/> values.</summary>
    Bool = 0,

    /// <summary>An exact JSON integer preserving canonical <see cref="ScalarTypeKind.Int32"/> values.</summary>
    Int32 = 1,

    /// <summary>A JSON string preserving canonical ordinal <see cref="ScalarTypeKind.String"/> values.</summary>
    String = 2,

    /// <summary>A canonical GUID string preserving <see cref="ScalarTypeKind.Guid"/> identity.</summary>
    Guid = 3,

    /// <summary>A canonical date string preserving <see cref="ScalarTypeKind.Date"/> identity.</summary>
    Date = 4
}

/// <summary>Exact scalar comparison facilities attested for one Cosmos collection-element child field.</summary>
[Flags]
public enum CosmosRelationQueryCollectionElementSemanticCapabilities
{
    /// <summary>No exact scalar comparison facility is asserted.</summary>
    None = 0,

    /// <summary>Cosmos equality preserves canonical equality for the declared value domain.</summary>
    ExactEquality = 1 << 0,

    /// <summary>Cosmos inequality preserves canonical inequality for the declared value domain.</summary>
    ExactInequality = 1 << 1
}

/// <summary>Exact physical mapping of one canonical field relative to a structured JSON-array element.</summary>
public sealed record CosmosRelationQueryCollectionElementFieldBinding
{
    const CosmosRelationQueryCollectionElementSemanticCapabilities AllSemanticCapabilities =
        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality;

    /// <summary>Creates one direct collection-element child-field binding.</summary>
    /// <param name="elementPath">Canonical field path relative to one collection element.</param>
    /// <param name="documentPath">Physical JSON property path relative to one collection element.</param>
    /// <param name="valueDomain">Exact scalar value domain stored by the physical field.</param>
    /// <param name="semanticCapabilities">Exact equality and inequality facilities attested by the binding.</param>
    /// <param name="semanticProfile">Stable encoding and comparison profile supporting the asserted capabilities.</param>
    /// <param name="missingValueBehavior">Physical treatment of a missing child property.</param>
    /// <param name="nullValueBehavior">Physical treatment of an explicit-null child property.</param>
    /// <exception cref="ArgumentException">A path, profile, or capability combination is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value or capability flag is unsupported.</exception>
    public CosmosRelationQueryCollectionElementFieldBinding(
        FieldPath elementPath,
        FieldPath documentPath,
        CosmosRelationQueryCollectionElementValueDomain valueDomain,
        CosmosRelationQueryCollectionElementSemanticCapabilities semanticCapabilities,
        string? semanticProfile,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior missingValueBehavior,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior nullValueBehavior)
    {
        if (!Enum.IsDefined(valueDomain))
        {
            throw new ArgumentOutOfRangeException(nameof(valueDomain), valueDomain, "Unsupported collection-element value domain.");
        }

        if ((semanticCapabilities & ~AllSemanticCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(semanticCapabilities),
                semanticCapabilities,
                "Unsupported collection-element semantic capability flag.");
        }

        if (!Enum.IsDefined(missingValueBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(missingValueBehavior), missingValueBehavior, "Unsupported missing-child behavior.");
        }

        if (!Enum.IsDefined(nullValueBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(nullValueBehavior), nullValueBehavior, "Unsupported null-child behavior.");
        }

        if (semanticCapabilities != CosmosRelationQueryCollectionElementSemanticCapabilities.None
            && string.IsNullOrWhiteSpace(semanticProfile))
        {
            throw new ArgumentException(
                "Exact collection-element comparisons require an attributable semantic profile.",
                nameof(semanticProfile));
        }

        if (semanticProfile is not null && string.IsNullOrWhiteSpace(semanticProfile))
        {
            throw new ArgumentException("A collection-element semantic profile cannot be empty.", nameof(semanticProfile));
        }

        ElementPath = RequireDirectFieldPath(elementPath, nameof(elementPath), "canonical element");
        DocumentPath = RequireDirectFieldPath(documentPath, nameof(documentPath), "physical element");
        ValueDomain = valueDomain;
        SemanticCapabilities = semanticCapabilities;
        SemanticProfile = semanticProfile;
        MissingValueBehavior = missingValueBehavior;
        NullValueBehavior = nullValueBehavior;
    }

    /// <summary>Canonical direct field path relative to one collection element.</summary>
    public FieldPath ElementPath { get; }

    /// <summary>Physical direct JSON property path relative to one collection element.</summary>
    public FieldPath DocumentPath { get; }

    /// <summary>Exact canonical scalar value domain stored by the physical field.</summary>
    public CosmosRelationQueryCollectionElementValueDomain ValueDomain { get; }

    /// <summary>Exact scalar comparison facilities attested by this binding.</summary>
    public CosmosRelationQueryCollectionElementSemanticCapabilities SemanticCapabilities { get; }

    /// <summary>Stable encoding and comparison profile supporting the asserted capabilities.</summary>
    public string? SemanticProfile { get; }

    /// <summary>Physical treatment of a missing child property.</summary>
    public CosmosRelationQueryStructuredCollectionAbsenceBehavior MissingValueBehavior { get; }

    /// <summary>Physical treatment of an explicit-null child property.</summary>
    public CosmosRelationQueryStructuredCollectionAbsenceBehavior NullValueBehavior { get; }

    static FieldPath RequireDirectFieldPath(FieldPath path, string parameterName, string description)
    {
        var normalized = CosmosRelationQueryStorageBinding.RequirePropertyPath(path, parameterName);
        if (normalized.Segments.Length != 1)
        {
            throw new ArgumentException(
                $"The Cosmos v2 structured-collection closure requires one direct {description} field segment.",
                parameterName);
        }

        return normalized;
    }
}

/// <summary>Explicit physical evidence tying one canonical structured collection input to a Cosmos JSON array.</summary>
public sealed record CosmosRelationQueryCollectionScopeEvidence
{
    /// <summary>Creates collection-scope evidence owned by one structured collection field binding.</summary>
    /// <param name="semanticProfile">Stable JSON-array storage and iteration profile supporting the scope evidence.</param>
    /// <param name="elementScope">Physical scope represented by a canonical current item.</param>
    /// <param name="correlationGuarantee">Same-element correlation guarantee supplied by array iteration.</param>
    /// <param name="collectionMissingValueBehavior">Physical treatment of a missing collection property.</param>
    /// <param name="collectionNullValueBehavior">Physical treatment of an explicit-null collection property.</param>
    /// <param name="nullElementBehavior">Physical treatment of an explicit-null collection element.</param>
    /// <param name="emptyCollectionBehavior">Physical treatment of an empty collection.</param>
    /// <param name="childFields">Direct child mappings keyed by canonical element-relative paths.</param>
    /// <exception cref="ArgumentNullException"><paramref name="semanticProfile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The profile or child mapping collection is invalid or ambiguous.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is unsupported.</exception>
    public CosmosRelationQueryCollectionScopeEvidence(
        string semanticProfile,
        CosmosRelationQueryCollectionElementScope elementScope,
        CosmosRelationQueryCollectionCorrelationGuarantee correlationGuarantee,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior collectionMissingValueBehavior,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior collectionNullValueBehavior,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior nullElementBehavior,
        CosmosRelationQueryEmptyCollectionBehavior emptyCollectionBehavior,
        ImmutableArray<CosmosRelationQueryCollectionElementFieldBinding> childFields)
    {
        if (!Enum.IsDefined(elementScope))
        {
            throw new ArgumentOutOfRangeException(nameof(elementScope), elementScope, "Unsupported collection-element scope.");
        }

        if (!Enum.IsDefined(correlationGuarantee))
        {
            throw new ArgumentOutOfRangeException(nameof(correlationGuarantee), correlationGuarantee, "Unsupported collection correlation guarantee.");
        }

        if (!Enum.IsDefined(collectionMissingValueBehavior))
        {
            throw new ArgumentOutOfRangeException(
                nameof(collectionMissingValueBehavior),
                collectionMissingValueBehavior,
                "Unsupported missing-collection behavior.");
        }

        if (!Enum.IsDefined(collectionNullValueBehavior))
        {
            throw new ArgumentOutOfRangeException(
                nameof(collectionNullValueBehavior),
                collectionNullValueBehavior,
                "Unsupported null-collection behavior.");
        }

        if (!Enum.IsDefined(nullElementBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(nullElementBehavior), nullElementBehavior, "Unsupported null-element behavior.");
        }

        if (!Enum.IsDefined(emptyCollectionBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(emptyCollectionBehavior), emptyCollectionBehavior, "Unsupported empty-collection behavior.");
        }

        SemanticProfile = Guard.RequireNotNullOrWhiteSpace(semanticProfile);
        var normalizedChildren = childFields.IsDefault ? [] : childFields;
        if (normalizedChildren.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Cosmos collection-scope evidence requires at least one non-null child-field binding.",
                nameof(childFields));
        }

        var seenPaths = new HashSet<FieldPath>(normalizedChildren.Length);
        var isCanonicalOrder = true;
        string? previousKey = null;
        foreach (var child in normalizedChildren)
        {
            if (child is null)
            {
                throw new ArgumentException(
                    "Cosmos collection-scope evidence requires at least one non-null child-field binding.",
                    nameof(childFields));
            }
            if (!seenPaths.Add(child.ElementPath))
            {
                throw new ArgumentException(
                    "Cosmos collection-scope evidence cannot repeat a canonical element-relative path.",
                    nameof(childFields));
            }

            var key = CosmosRelationQueryStorageBinding.FieldPathKey(child.ElementPath);
            isCanonicalOrder &= previousKey is null
                || string.CompareOrdinal(previousKey, key) < 0;
            previousKey = key;
        }

        ElementScope = elementScope;
        CorrelationGuarantee = correlationGuarantee;
        CollectionMissingValueBehavior = collectionMissingValueBehavior;
        CollectionNullValueBehavior = collectionNullValueBehavior;
        NullElementBehavior = nullElementBehavior;
        EmptyCollectionBehavior = emptyCollectionBehavior;
        ChildFields = isCanonicalOrder
            ? normalizedChildren
            :
            [
                .. normalizedChildren.OrderBy(
                    static child => CosmosRelationQueryStorageBinding.FieldPathKey(child.ElementPath),
                    StringComparer.Ordinal)
            ];
    }

    /// <summary>Physical scope represented by a canonical current item.</summary>
    public CosmosRelationQueryCollectionElementScope ElementScope { get; }

    /// <summary>Same-element correlation guarantee supplied by JSON-array iteration.</summary>
    public CosmosRelationQueryCollectionCorrelationGuarantee CorrelationGuarantee { get; }

    /// <summary>Physical treatment of a missing collection property.</summary>
    public CosmosRelationQueryStructuredCollectionAbsenceBehavior CollectionMissingValueBehavior { get; }

    /// <summary>Physical treatment of an explicit-null collection property.</summary>
    public CosmosRelationQueryStructuredCollectionAbsenceBehavior CollectionNullValueBehavior { get; }

    /// <summary>Physical treatment of an explicit-null collection element.</summary>
    public CosmosRelationQueryStructuredCollectionAbsenceBehavior NullElementBehavior { get; }

    /// <summary>Physical treatment of an empty collection.</summary>
    public CosmosRelationQueryEmptyCollectionBehavior EmptyCollectionBehavior { get; }

    /// <summary>Stable JSON-array storage and iteration profile supporting the scope evidence.</summary>
    public string SemanticProfile { get; }

    /// <summary>Direct child mappings in deterministic canonical element-path order.</summary>
    public ImmutableArray<CosmosRelationQueryCollectionElementFieldBinding> ChildFields { get; }

    /// <summary>Resolves one direct child mapping by canonical element-relative path.</summary>
    /// <param name="elementPath">Canonical path relative to the current collection element.</param>
    /// <returns>The exact collection-element child-field binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="elementPath"/> is not bound.</exception>
    public CosmosRelationQueryCollectionElementFieldBinding ResolveChild(FieldPath elementPath)
    {
        foreach (var child in ChildFields)
        {
            if (child.ElementPath == elementPath)
            {
                return child;
            }
        }

        throw new KeyNotFoundException($"Collection element field '{elementPath}' has no Cosmos child binding.");
    }

    /// <summary>Compares normalized collection-scope evidence using value semantics for child mappings.</summary>
    /// <param name="other">Other evidence to compare.</param>
    /// <returns><see langword="true"/> when every normalized evidence fact is equal.</returns>
    public bool Equals(CosmosRelationQueryCollectionScopeEvidence? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
               && ElementScope == other.ElementScope
               && CorrelationGuarantee == other.CorrelationGuarantee
               && CollectionMissingValueBehavior == other.CollectionMissingValueBehavior
               && CollectionNullValueBehavior == other.CollectionNullValueBehavior
               && NullElementBehavior == other.NullElementBehavior
               && EmptyCollectionBehavior == other.EmptyCollectionBehavior
               && string.Equals(SemanticProfile, other.SemanticProfile, StringComparison.Ordinal)
               && ChildFields.SequenceEqual(other.ChildFields);
    }

    /// <summary>Computes a value-semantic hash code for the normalized evidence.</summary>
    /// <returns>A hash code aligned with <see cref="Equals(CosmosRelationQueryCollectionScopeEvidence?)"/>.</returns>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add((int)ElementScope);
        hash.Add((int)CorrelationGuarantee);
        hash.Add((int)CollectionMissingValueBehavior);
        hash.Add((int)CollectionNullValueBehavior);
        hash.Add((int)NullElementBehavior);
        hash.Add((int)EmptyCollectionBehavior);
        hash.Add(SemanticProfile, StringComparer.Ordinal);
        foreach (var child in ChildFields)
        {
            hash.Add(child);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Exact integer domain asserted for one physical Cosmos JSON-number field.</summary>
/// <remarks>
/// Cosmos SQL evaluates JSON numbers in a binary64 domain. This evidence is valid only when the physical source
/// guarantees every retained integer lies between <see cref="Minimum"/> and <see cref="Maximum"/>; both bounds must
/// remain inside <see cref="CosmosRelationQueryTargetProfile.MaximumExactInteger"/>. It does not change the canonical
/// semantic type.
/// </remarks>
public sealed record CosmosRelationQueryExactIntegerDomain
{
    /// <summary>Creates exact physical integer-domain evidence.</summary>
    /// <param name="minimum">Inclusive minimum retained by the physical source.</param>
    /// <param name="maximum">Inclusive maximum retained by the physical source.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A bound exceeds Cosmos's exact integer domain or <paramref name="minimum"/> exceeds
    /// <paramref name="maximum"/>.
    /// </exception>
    public CosmosRelationQueryExactIntegerDomain(long minimum, long maximum)
    {
        if (minimum < -CosmosRelationQueryTargetProfile.MaximumExactInteger
            || maximum > CosmosRelationQueryTargetProfile.MaximumExactInteger
            || minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximum),
                maximum,
                $"A Cosmos exact integer domain must be ordered and remain inside "
                + $"[-{CosmosRelationQueryTargetProfile.MaximumExactInteger}, "
                + $"{CosmosRelationQueryTargetProfile.MaximumExactInteger}].");
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Inclusive physical minimum.</summary>
    public long Minimum { get; }

    /// <summary>Inclusive physical maximum.</summary>
    public long Maximum { get; }

    /// <summary>Nonnegative exact integer domain used by Cosmos observation versions.</summary>
    public static CosmosRelationQueryExactIntegerDomain NonNegative { get; } = new(
        0,
        CosmosRelationQueryTargetProfile.MaximumExactInteger);
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
    /// <param name="collectionScope">
    /// Optional explicit structured-collection evidence owned by this outer collection field.
    /// </param>
    /// <param name="exactIntegerDomain">
    /// Optional exact physical JSON-number domain for a scalar integer field.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> is default, or <paramref name="documentPath"/> is empty or malformed.
    /// </exception>
    public CosmosRelationQueryFieldBinding(
        RelationQueryInputId input,
        FieldPath documentPath,
        CosmosRelationQueryCollectionScopeEvidence? collectionScope = null,
        CosmosRelationQueryExactIntegerDomain? exactIntegerDomain = null)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
        {
            throw new ArgumentException("A Cosmos field binding requires a compiled input identity.", nameof(input));
        }

        Input = input;
        if (collectionScope is not null && exactIntegerDomain is not null)
        {
            throw new ArgumentException(
                "A Cosmos field binding cannot combine scalar exact-integer and structured-collection evidence.",
                nameof(exactIntegerDomain));
        }
        DocumentPath = collectionScope is null
            ? CosmosRelationQueryStorageBinding.RequireDocumentSelectorPath(documentPath, nameof(documentPath))
            : CosmosRelationQueryStorageBinding.RequirePropertyPath(documentPath, nameof(documentPath));
        CollectionScope = collectionScope;
        ExactIntegerDomain = exactIntegerDomain;
    }

    /// <summary>Exact compiled field-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Structural path relative to the configured document root.</summary>
    public FieldPath DocumentPath { get; }

    /// <summary>Explicit structured-collection scope evidence, or <see langword="null"/>.</summary>
    public CosmosRelationQueryCollectionScopeEvidence? CollectionScope { get; }

    /// <summary>Exact scalar integer-domain evidence, or <see langword="null"/>.</summary>
    public CosmosRelationQueryExactIntegerDomain? ExactIntegerDomain { get; }
}

/// <summary>
/// One exact top-level string equality that defines membership in the physical Cosmos source represented by a
/// canonical source instance.
/// </summary>
/// <remarks>
/// This is adapter binding evidence, not a canonical business predicate. Native compilation conjoins every equality
/// with the canonical query filter so shared-container envelope records outside the bound source cannot participate.
/// </remarks>
public sealed record CosmosRelationQuerySourceScopeEquality
{
    /// <summary>Creates one exact top-level physical source-membership equality.</summary>
    /// <param name="documentPath">One direct top-level Cosmos document property.</param>
    /// <param name="value">Non-empty ordinal string value required for source membership.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="documentPath"/> is not one direct property or <paramref name="value"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public CosmosRelationQuerySourceScopeEquality(FieldPath documentPath, string value)
    {
        var normalizedPath = CosmosRelationQueryStorageBinding.RequirePropertyPath(documentPath, nameof(documentPath));
        if (normalizedPath.Segments.Length != 1)
        {
            throw new ArgumentException(
                "A Cosmos source-scope equality requires one direct top-level document property.",
                nameof(documentPath));
        }

        DocumentPath = normalizedPath;
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Direct top-level physical document property tested for membership.</summary>
    public FieldPath DocumentPath { get; }

    /// <summary>Exact ordinal string value required for membership.</summary>
    public string Value { get; }
}

/// <summary>
/// Immutable, versioned binding from one exact placed semantic source to one Cosmos account, database, container,
/// and document shape.
/// </summary>
/// <remarks>
/// Adapter authoring supplies exact compiled-plan and placement fingerprints. Direct construction may omit both
/// affinity facts as an explicit unverified escape hatch; native compilation then validates structural identities but
/// cannot detect reuse of stale artifacts that deliberately retain the same identities.
/// </remarks>
public sealed class CosmosRelationQueryStorageBinding
{
    /// <summary>Current portable Cosmos relation/query storage-binding schema.</summary>
    public const string CurrentSchemaVersion = "cohesive.relations.cosmos-binding/v6";

    /// <summary>Default deterministic convention set for semantic-path document bindings.</summary>
    public const string SemanticPathConventionSet = "cohesive.relations.cosmos/semantic-path-conventions/v1";

    /// <summary>Creates an explicit Cosmos storage binding.</summary>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="source">Physical source instance bound to the Cosmos container.</param>
    /// <param name="placementBinding">Exact plan-scoped placement binding interpreted by this binding.</param>
    /// <param name="target">Expected Cosmos target identity.</param>
    /// <param name="targetProfile">Expected target capability-profile identity.</param>
    /// <param name="accountEndpoint">
    /// Absolute Cosmos account endpoint retained as normalized physical affinity with one trailing separator.
    /// </param>
    /// <param name="databaseName">Physical Cosmos database name retained for execution integration.</param>
    /// <param name="containerName">Physical Cosmos container name retained for execution integration.</param>
    /// <param name="rootAlias">Simple SQL alias emitted after <c>FROM</c>.</param>
    /// <param name="identityPath">Stable identity property path relative to the document root.</param>
    /// <param name="fields">
    /// Exact compiled field-input selectors. May be empty when an operation such as unfiltered row count consumes no
    /// semantic field input.
    /// </param>
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
    /// <param name="configurationDecisions">
    /// Optional normalized provenance for effective convention, scoped-profile, default, and explicit configuration.
    /// The binding properties remain the source of truth for effective values.
    /// </param>
    /// <param name="compiledPlanFingerprint">
    /// Exact compiled-plan fingerprint, or <see langword="null"/> together with <paramref name="placementFingerprint"/>
    /// for an explicitly unverified low-level binding.
    /// </param>
    /// <param name="placementFingerprint">
    /// Exact source-placement fingerprint, or <see langword="null"/> together with
    /// <paramref name="compiledPlanFingerprint"/> for an explicitly unverified low-level binding.
    /// </param>
    /// <param name="sourceScopeEqualities">
    /// Exact top-level physical source-membership equalities conjoined with every compiled canonical filter.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="accountEndpoint"/>, <paramref name="databaseName"/>, or <paramref name="containerName"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="accountEndpoint"/> is relative, is not HTTP or HTTPS, has no host, or contains credentials, a
    /// query, or a fragment; an identity or string is empty; a path is invalid; <paramref name="fields"/> contains a
    /// <see langword="null"/> entry, or repeats an input; configuration decisions contain a null entry or repeat a
    /// setting; a configuration setting does not belong to the Cosmos binding grammar; origin and configuration
    /// provenance conflict; plan and placement affinity are only partially supplied; or origin and convention
    /// attribution conflict.
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
        Uri accountEndpoint,
        string databaseName,
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
        string? conventionSetVersion = null,
        ImmutableArray<EffectiveConfigurationDecision> configurationDecisions = default,
        RelationQueryPlanComponentFingerprint? compiledPlanFingerprint = null,
        RelationQuerySourcePlacementFingerprint? placementFingerprint = null,
        ImmutableArray<CosmosRelationQuerySourceScopeEquality> sourceScopeEqualities = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value) || string.IsNullOrWhiteSpace(source.Value)
            || string.IsNullOrWhiteSpace(placementBinding.Value) || string.IsNullOrWhiteSpace(target.Value)
            || string.IsNullOrWhiteSpace(targetProfile.Value))
        {
            throw new ArgumentException("A Cosmos storage binding requires non-default identities.", nameof(id));
        }
        if (!Enum.IsDefined(missingValueEncoding))
        {
            throw new ArgumentOutOfRangeException(nameof(missingValueEncoding), missingValueEncoding, "Unsupported Cosmos missing-value encoding.");
        }

        if (!Enum.IsDefined(nullValueEncoding))
        {
            throw new ArgumentOutOfRangeException(nameof(nullValueEncoding), nullValueEncoding, "Unsupported Cosmos null encoding.");
        }

        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported Cosmos binding origin.");
        }

        if (origin == CosmosRelationQueryBindingOrigin.Convention && string.IsNullOrWhiteSpace(conventionSetVersion))
        {
            throw new ArgumentException("A convention-derived Cosmos binding requires its convention-set identity.", nameof(conventionSetVersion));
        }

        if (conventionSetVersion is not null && string.IsNullOrWhiteSpace(conventionSetVersion))
        {
            throw new ArgumentException("A Cosmos convention-set identity cannot be empty.", nameof(conventionSetVersion));
        }

        if (maximumInputRows is <= 0 or > CosmosRelationQueryTargetProfile.MaximumExactInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInputRows),
                maximumInputRows,
                $"A Cosmos input-row bound must be between 1 and {CosmosRelationQueryTargetProfile.MaximumExactInteger}.");
        }

        var normalizedFields = fields.IsDefault ? [] : fields;
        if (normalizedFields.Any(static field => field is null))
        {
            throw new ArgumentException("Cosmos storage-binding fields cannot contain null entries.", nameof(fields));
        }

        if (normalizedFields.GroupBy(static field => field.Input).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A Cosmos storage binding cannot repeat a compiled field input.", nameof(fields));
        }

        var normalizedDecisions = configurationDecisions.IsDefault ? [] : configurationDecisions;
        if (normalizedDecisions.Any(static decision => decision is null))
        {
            throw new ArgumentException("Cosmos configuration decisions cannot contain null entries.", nameof(configurationDecisions));
        }

        if (normalizedDecisions.GroupBy(static decision => decision.Setting, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Cosmos configuration decisions cannot repeat a setting.",
                nameof(configurationDecisions));
        }

        var normalizedSourceScope = sourceScopeEqualities.IsDefault ? [] : sourceScopeEqualities;
        if (normalizedSourceScope.Any(static equality => equality is null))
        {
            throw new ArgumentException("Cosmos source-scope equalities cannot contain null entries.", nameof(sourceScopeEqualities));
        }

        if (normalizedSourceScope.GroupBy(static equality => equality.DocumentPath)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "A Cosmos storage binding cannot repeat a physical source-scope path.",
                nameof(sourceScopeEqualities));
        }

        if ((compiledPlanFingerprint is null) != (placementFingerprint is null))
        {
            throw new ArgumentException(
                "Cosmos compiled-plan and source-placement affinity must be supplied together or both omitted.",
                nameof(compiledPlanFingerprint));
        }

        if (origin == CosmosRelationQueryBindingOrigin.Convention
            && normalizedDecisions.Any(static decision => decision.Origin is
                EffectiveConfigurationOrigin.Explicit
                or EffectiveConfigurationOrigin.ScopedProfile))
        {
            throw new ArgumentException(
                "A convention-origin Cosmos binding cannot retain explicit or scoped-profile configuration decisions.",
                nameof(configurationDecisions));
        }

        var normalizedStablePaths = stableUniqueOrderingPaths.IsDefault ? [] : stableUniqueOrderingPaths;
        foreach (var path in normalizedStablePaths)
        {
            RequirePropertyPath(path, nameof(stableUniqueOrderingPaths));
        }

        normalizedStablePaths =
        [
            .. normalizedStablePaths.Distinct().OrderBy(FieldPathKey, StringComparer.Ordinal)
        ];
        var normalizedExactOrderingPaths = exactOrderingPaths.IsDefault ? [] : exactOrderingPaths;
        foreach (var path in normalizedExactOrderingPaths)
        {
            RequirePropertyPath(path, nameof(exactOrderingPaths));
        }

        normalizedExactOrderingPaths =
        [
            .. normalizedExactOrderingPaths.Distinct().OrderBy(FieldPathKey, StringComparer.Ordinal)
        ];

        Id = id;
        Source = source;
        PlacementBinding = placementBinding;
        Target = target;
        TargetProfile = targetProfile;
        AccountEndpoint = CosmosPhysicalAffinity.NormalizeAccountEndpoint(accountEndpoint);
        DatabaseName = Guard.RequireNotNullOrWhiteSpace(databaseName);
        ContainerName = Guard.RequireNotNullOrWhiteSpace(containerName);
        RootAlias = CosmosSqlNames.RequireIdentifier(rootAlias, nameof(rootAlias));
        IdentityPath = RequirePropertyPath(identityPath, nameof(identityPath));
        Fields = [.. normalizedFields.OrderBy(static field => field.Input.Value, StringComparer.Ordinal)];
        DocumentRoot = documentRoot is { } root ? RequirePropertyPath(root, nameof(documentRoot)) : null;
        PartitionPath = partitionPath is { } partition ? RequirePropertyPath(partition, nameof(partitionPath)) : null;
        StableUniqueOrderingPaths = normalizedStablePaths;
        ExactOrderingPaths = normalizedExactOrderingPaths;
        SourceScopeEqualities =
        [
            .. normalizedSourceScope.OrderBy(
                static equality => FieldPathKey(equality.DocumentPath),
                StringComparer.Ordinal)
        ];
        MaximumInputRows = maximumInputRows;
        MissingValueEncoding = missingValueEncoding;
        NullValueEncoding = nullValueEncoding;
        Origin = origin;
        ConventionSetVersion = conventionSetVersion;
        ConfigurationDecisions =
        [
            .. normalizedDecisions.OrderBy(static decision => decision.Setting, StringComparer.Ordinal)
        ];
        ValidateConfigurationDecisionSettings(this, ConfigurationDecisions, nameof(configurationDecisions));
        CompiledPlanFingerprint = compiledPlanFingerprint;
        PlacementFingerprint = placementFingerprint;
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
    /// <param name="accountEndpoint">
    /// Absolute Cosmos account endpoint retained as normalized physical affinity with one trailing separator.
    /// </param>
    /// <param name="databaseName">Physical Cosmos database name retained for execution integration.</param>
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
    /// <param name="configurationDecisions">
    /// Optional normalized provenance for the effective binding configuration.
    /// </param>
    /// <param name="compiledPlanFingerprint">
    /// Exact persisted compiled-plan fingerprint, or <see langword="null"/> together with
    /// <paramref name="placementFingerprint"/> for an unverified low-level binding.
    /// </param>
    /// <param name="placementFingerprint">
    /// Exact persisted source-placement fingerprint, or <see langword="null"/> together with
    /// <paramref name="compiledPlanFingerprint"/> for an unverified low-level binding.
    /// </param>
    /// <param name="sourceScopeEqualities">
    /// Exact top-level physical source-membership equalities conjoined with every compiled canonical filter.
    /// </param>
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
        Uri accountEndpoint,
        string databaseName,
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
        string? conventionSetVersion = null,
        ImmutableArray<EffectiveConfigurationDecision> configurationDecisions = default,
        RelationQueryPlanComponentFingerprint? compiledPlanFingerprint = null,
        RelationQuerySourcePlacementFingerprint? placementFingerprint = null,
        ImmutableArray<CosmosRelationQuerySourceScopeEquality> sourceScopeEqualities = default)
        : this(
            id,
            source,
            placementBinding,
            target,
            targetProfile,
            accountEndpoint,
            databaseName,
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
            conventionSetVersion,
            configurationDecisions,
            compiledPlanFingerprint,
            placementFingerprint,
            sourceScopeEqualities)
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

    /// <summary>
    /// Physical source instance inherited as exact plan-bound affinity rather than adapter configuration.
    /// </summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>
    /// Exact plan-scoped placement binding inherited as affinity rather than adapter configuration.
    /// </summary>
    public RelationQuerySourcePlacementBindingId PlacementBinding { get; }

    /// <summary>Expected Cosmos interpretation-target identity.</summary>
    public RelationQueryTargetId Target { get; }

    /// <summary>Expected target capability-profile identity.</summary>
    public RelationQueryTargetProfileId TargetProfile { get; }

    /// <summary>Normalized absolute Cosmos account endpoint with one trailing separator.</summary>
    public Uri AccountEndpoint { get; }

    /// <summary>Physical Cosmos database name.</summary>
    public string DatabaseName { get; }

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

    /// <summary>
    /// Exact top-level physical source-membership equalities in stable path order. These predicates are outside
    /// <see cref="DocumentRoot"/> because they classify complete container documents.
    /// </summary>
    public ImmutableArray<CosmosRelationQuerySourceScopeEquality> SourceScopeEqualities { get; }

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

    /// <summary>
    /// Effective configuration-decision provenance in stable setting order. Effective values remain represented by
    /// the binding's dedicated properties.
    /// </summary>
    public ImmutableArray<EffectiveConfigurationDecision> ConfigurationDecisions { get; }

    /// <summary>
    /// Exact compiled-plan fingerprint verified by adapter authoring, or <see langword="null"/> for an explicitly
    /// unverified low-level binding.
    /// </summary>
    public RelationQueryPlanComponentFingerprint? CompiledPlanFingerprint { get; }

    /// <summary>
    /// Exact source-placement fingerprint verified by adapter authoring, or <see langword="null"/> for an explicitly
    /// unverified low-level binding.
    /// </summary>
    public RelationQuerySourcePlacementFingerprint? PlacementFingerprint { get; }

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
        return ResolveFieldBinding(input).DocumentPath;
    }

    /// <summary>Resolves the complete physical field binding for an exact compiled field input.</summary>
    /// <param name="input">Compiled field-input identity.</param>
    /// <returns>The exact field binding, including any structured-collection evidence.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is not bound.</exception>
    public CosmosRelationQueryFieldBinding ResolveFieldBinding(RelationQueryInputId input)
    {
        foreach (var field in Fields)
        {
            if (field.Input == input)
            {
                return field;
            }
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
    /// <param name="accountEndpoint">Absolute Cosmos account endpoint.</param>
    /// <param name="databaseName">Physical Cosmos database name.</param>
    /// <param name="containerName">Physical Cosmos container name.</param>
    /// <param name="identityPath">Stable identity property path relative to the document root.</param>
    /// <param name="rootAlias">Simple Cosmos SQL document alias.</param>
    /// <param name="documentRoot">Optional semantic-value document root.</param>
    /// <param name="partitionPath">Optional physical partition-key path.</param>
    /// <param name="stableUniqueOrderingPaths">Paths proven to be stable unique ordering keys.</param>
    /// <param name="exactOrderingPaths">Paths whose Cosmos ordering is proven equivalent to canonical ordering.</param>
    /// <param name="maximumInputRows">Optional asserted maximum participating rows.</param>
    /// <returns>
    /// A convention-attributed immutable storage binding without compiled-plan or placement fingerprint affinity.
    /// The result is an explicitly unverified low-level binding.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="placement"/>, <paramref name="accountEndpoint"/>, <paramref name="databaseName"/>, or
    /// <paramref name="containerName"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The placement is not a source-set binding or another supplied binding fact is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumInputRows"/> is outside the exact Cosmos numeric range.
    /// </exception>
    public static CosmosRelationQueryStorageBinding FromSemanticPathConvention(
        CosmosRelationQueryBindingId id,
        RelationQuerySourcePlacementBinding placement,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        Uri accountEndpoint,
        string databaseName,
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
        {
            throw new ArgumentException("The Cosmos semantic-path convention requires a source-set placement.", nameof(placement));
        }

        return new(
            id,
            placement.Source,
            placement.Id,
            target,
            targetProfile,
            accountEndpoint,
            databaseName,
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

    static void ValidateConfigurationDecisionSettings(
        CosmosRelationQueryStorageBinding binding,
        ImmutableArray<EffectiveConfigurationDecision> decisions,
        string parameterName)
    {
        HashSet<string> allowed = new(StringComparer.Ordinal)
        {
            "target",
            "targetProfile",
            "accountEndpoint",
            "databaseName",
            "containerName",
            "rootAlias",
            "identityPath",
            "documentRoot",
            "partitionPath",
            "maximumInputRows",
            "missingValueEncoding",
            "nullValueEncoding",
            "conventionSetVersion",
            "bindingId"
        };
        foreach (var field in binding.Fields)
        {
            var fieldPrefix = "field/" + field.Input.Value;
            allowed.Add(fieldPrefix);
            if (field.CollectionScope is not { } collection)
                continue;

            allowed.Add(fieldPrefix + "/collectionScope");
            var collectionPrefix = fieldPrefix + "/collection/";
            allowed.Add(collectionPrefix + "semanticProfile");
            allowed.Add(collectionPrefix + "elementScope");
            allowed.Add(collectionPrefix + "correlationGuarantee");
            allowed.Add(collectionPrefix + "collectionMissingValueBehavior");
            allowed.Add(collectionPrefix + "collectionNullValueBehavior");
            allowed.Add(collectionPrefix + "nullElementBehavior");
            allowed.Add(collectionPrefix + "emptyCollectionBehavior");
            foreach (var child in collection.ChildFields)
            {
                var childPrefix = collectionPrefix + "child/" + FieldPathKey(child.ElementPath) + "/";
                allowed.Add(childPrefix + "elementPath");
                allowed.Add(childPrefix + "documentPath");
                allowed.Add(childPrefix + "valueDomain");
                allowed.Add(childPrefix + "semanticCapabilities");
                allowed.Add(childPrefix + "semanticProfile");
                allowed.Add(childPrefix + "missingValueBehavior");
                allowed.Add(childPrefix + "nullValueBehavior");
            }
        }

        foreach (var path in binding.StableUniqueOrderingPaths)
        {
            allowed.Add("stableUniqueOrderingPath/" + FieldPathKey(path));
        }

        foreach (var path in binding.ExactOrderingPaths)
        {
            allowed.Add("exactOrderingPath/" + FieldPathKey(path));
        }

        foreach (var equality in binding.SourceScopeEqualities)
        {
            allowed.Add("sourceScopeEquality/" + FieldPathKey(equality.DocumentPath));
        }

        var foreign = decisions.FirstOrDefault(decision => !allowed.Contains(decision.Setting));
        if (foreign is not null)
        {
            throw new ArgumentException(
                $"Configuration setting '{foreign.Setting}' does not belong to this Cosmos storage binding.",
                parameterName);
        }
    }
}

internal static class CosmosRelationQueryCollectionScopeContracts
{
    internal static bool TryGetValueDomain(
        TypeRef? type,
        out CosmosRelationQueryCollectionElementValueDomain domain)
    {
        switch (type)
        {
            case ScalarTypeRef { Kind: ScalarTypeKind.Bool }:
                domain = CosmosRelationQueryCollectionElementValueDomain.Bool;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Int32 }:
                domain = CosmosRelationQueryCollectionElementValueDomain.Int32;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.String }:
                domain = CosmosRelationQueryCollectionElementValueDomain.String;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Guid }:
                domain = CosmosRelationQueryCollectionElementValueDomain.Guid;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Date }:
                domain = CosmosRelationQueryCollectionElementValueDomain.Date;
                return true;
            default:
                domain = default;
                return false;
        }
    }

    internal static CosmosRelationQueryCollectionScopeGap? GetGap(
        CosmosRelationQueryCollectionScopeEvidence scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ElementScope != CosmosRelationQueryCollectionElementScope.JsonArrayElement)
        {
            return new(
                "The Cosmos collection binding does not attest JSON-array element scope.",
                "Attest JsonArrayElement scope for the structured collection.");
        }
        if (scope.CorrelationGuarantee
            != CosmosRelationQueryCollectionCorrelationGuarantee.SameArrayElement)
        {
            return new(
                "The Cosmos collection binding does not attest same-array-element correlation.",
                "Attest SameArrayElement correlation for the structured collection.");
        }
        if (scope.CollectionMissingValueBehavior
                != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion
            || scope.CollectionNullValueBehavior
                != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion)
        {
            return new(
                "The Cosmos collection binding must attest that ingestion prohibits missing and null collections; treating them as empty would weaken canonical any semantics.",
                "Prohibit missing and explicit-null collection values during ingestion.");
        }
        if (scope.NullElementBehavior
            != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion)
        {
            return new(
                "The Cosmos collection binding must attest that ingestion prohibits explicit-null collection elements.",
                "Prohibit explicit-null elements during ingestion.");
        }
        if (scope.EmptyCollectionBehavior != CosmosRelationQueryEmptyCollectionBehavior.NoElements)
        {
            return new(
                "The Cosmos collection binding does not prove that an empty JSON array contributes no existential subquery rows.",
                "Attest NoElements behavior for an empty JSON array.");
        }
        return null;
    }
}

internal sealed record CosmosRelationQueryCollectionScopeGap(
    string Message,
    string Resolution);

static class CosmosRelationQueryBindingFingerprinter
{
    const string Algorithm = "sha256";
    const string Canonicalization = "cohesive.relations.cosmos-binding/v7-c14n/v1";

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
        Append(canonical, binding.AccountEndpoint.AbsoluteUri);
        Append(canonical, binding.DatabaseName);
        Append(canonical, binding.ContainerName);
        Append(canonical, binding.RootAlias);
        Append(canonical, binding.DocumentRoot);
        Append(canonical, binding.IdentityPath);
        Append(canonical, binding.PartitionPath);
        Append(canonical, (int)binding.MissingValueEncoding);
        Append(canonical, (int)binding.NullValueEncoding);
        Append(canonical, (int)binding.Origin);
        Append(canonical, binding.ConventionSetVersion);
        Append(canonical, binding.CompiledPlanFingerprint is null ? 0 : 1);
        if (binding.CompiledPlanFingerprint is { } compiledPlan)
        {
            var placement = binding.PlacementFingerprint!;
            Append(canonical, compiledPlan.Algorithm);
            Append(canonical, compiledPlan.Canonicalization);
            Append(canonical, compiledPlan.Value);
            Append(canonical, placement.Algorithm);
            Append(canonical, placement.Canonicalization);
            Append(canonical, placement.Value);
        }
        Append(canonical, binding.Fields.Length);
        foreach (var field in binding.Fields)
        {
            Append(canonical, field.Input.Value);
            Append(canonical, field.DocumentPath);
            Append(canonical, field.ExactIntegerDomain is null ? 0 : 1);
            if (field.ExactIntegerDomain is { } integerDomain)
            {
                Append(canonical, integerDomain.Minimum);
                Append(canonical, integerDomain.Maximum);
            }
            Append(canonical, field.CollectionScope is null ? 0 : 1);
            if (field.CollectionScope is { } collection)
            {
                Append(canonical, (int)collection.ElementScope);
                Append(canonical, (int)collection.CorrelationGuarantee);
                Append(canonical, (int)collection.CollectionMissingValueBehavior);
                Append(canonical, (int)collection.CollectionNullValueBehavior);
                Append(canonical, (int)collection.NullElementBehavior);
                Append(canonical, (int)collection.EmptyCollectionBehavior);
                Append(canonical, collection.SemanticProfile);
                Append(canonical, collection.ChildFields.Length);
                foreach (var child in collection.ChildFields)
                {
                    Append(canonical, child.ElementPath);
                    Append(canonical, child.DocumentPath);
                    Append(canonical, (int)child.ValueDomain);
                    Append(canonical, (int)child.SemanticCapabilities);
                    Append(canonical, child.SemanticProfile);
                    Append(canonical, (int)child.MissingValueBehavior);
                    Append(canonical, (int)child.NullValueBehavior);
                }
            }
        }
        Append(canonical, binding.StableUniqueOrderingPaths.Length);
        foreach (var path in binding.StableUniqueOrderingPaths)
        {
            Append(canonical, path);
        }

        Append(canonical, binding.ExactOrderingPaths.Length);
        foreach (var path in binding.ExactOrderingPaths)
        {
            Append(canonical, path);
        }

        Append(canonical, binding.SourceScopeEqualities.Length);
        foreach (var equality in binding.SourceScopeEqualities)
        {
            Append(canonical, equality.DocumentPath);
            Append(canonical, equality.Value);
        }

        Append(canonical, binding.MaximumInputRows?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, binding.ConfigurationDecisions.Length);
        foreach (var decision in binding.ConfigurationDecisions)
        {
            Append(canonical, decision.Setting);
            Append(canonical, (int)decision.Origin);
            Append(canonical, decision.Authority);
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
