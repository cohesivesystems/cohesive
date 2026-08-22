using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Realization;

/// <summary>Stable identity of one canonical aggregate storage structure.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct StorageStructureId
{
    /// <summary>Creates a storage-structure identity.</summary>
    /// <param name="value">Stable identity independent of a physical storage target.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public StorageStructureId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of one owned collection within a canonical storage structure.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct StorageOwnedCollectionId
{
    /// <summary>Creates an owned-collection identity.</summary>
    /// <param name="value">Stable identity independent of an embedded or decomposed realization.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public StorageOwnedCollectionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Canonical ownership and ordering semantics for one collection-valued aggregate field.
/// </summary>
/// <remarks>
/// The component type is not an independently governed entity. Its lifetime and tenant scope are inherited from the
/// aggregate root. A physical adapter may nevertheless decompose component occurrences into separate records.
/// </remarks>
public sealed record StorageOwnedCollectionDefinition
{
    /// <summary>Creates an owned ordered collection definition.</summary>
    /// <param name="id">Stable collection identity.</param>
    /// <param name="collectionPath">Canonical path from the root shape to the collection field.</param>
    /// <param name="componentType">Stable named structural type of each collection item.</param>
    /// <param name="localIdentityPath">Component-relative stable local identity path.</param>
    /// <param name="ordinalPath">Component-relative ordering ordinal path.</param>
    /// <exception cref="ArgumentException">A path or identity is default.</exception>
    [JsonConstructor]
    public StorageOwnedCollectionDefinition(
        StorageOwnedCollectionId id,
        FieldPath collectionPath,
        TypeId componentType,
        FieldPath localIdentityPath,
        FieldPath ordinalPath)
    {
        StorageRealizationContract.RequireId(id.Value, nameof(id));
        StorageRealizationContract.RequirePath(collectionPath, nameof(collectionPath));
        StorageRealizationContract.RequireId(componentType.Value, nameof(componentType));
        StorageRealizationContract.RequirePath(localIdentityPath, nameof(localIdentityPath));
        StorageRealizationContract.RequirePath(ordinalPath, nameof(ordinalPath));

        Id = id;
        CollectionPath = collectionPath;
        ComponentType = componentType;
        LocalIdentityPath = localIdentityPath;
        OrdinalPath = ordinalPath;
    }

    /// <summary>Stable collection identity.</summary>
    public StorageOwnedCollectionId Id { get; }

    /// <summary>Canonical path from the root shape to the collection field.</summary>
    public FieldPath CollectionPath { get; }

    /// <summary>Stable named structural type of each collection item.</summary>
    public TypeId ComponentType { get; }

    /// <summary>Component-relative stable local identity path.</summary>
    public FieldPath LocalIdentityPath { get; }

    /// <summary>Component-relative ordering ordinal path.</summary>
    public FieldPath OrdinalPath { get; }
}

/// <summary>
/// Canonical semantic storage structure for one independently governed entity root and its owned collections.
/// </summary>
/// <remarks>
/// The retained shape graph is the field and type authority. This definition adds ownership, aggregate boundary,
/// stable component identity, ordering, and inherited partition meaning without copying the underlying fields.
/// </remarks>
public sealed record StorageStructureDefinition
{
    /// <summary>Creates a canonical storage structure.</summary>
    /// <param name="id">Stable structure identity.</param>
    /// <param name="semanticModel">Exact portable shape graph containing the root and component types.</param>
    /// <param name="rootShape">Independently governed aggregate-root shape.</param>
    /// <param name="rootIdentityPath">Root-relative stable observation identity path.</param>
    /// <param name="partitionPath">Root-relative logical partition path inherited by owned components.</param>
    /// <param name="ownedCollections">Owned collections, normalized by stable identity.</param>
    /// <param name="provenance">Required producer and source attribution.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="semanticModel"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity or path is default, or <paramref name="ownedCollections"/> contains a null or duplicate identity.
    /// </exception>
    [JsonConstructor]
    public StorageStructureDefinition(
        StorageStructureId id,
        ShapeGraphDocument semanticModel,
        QualifiedShapeId rootShape,
        FieldPath rootIdentityPath,
        FieldPath partitionPath,
        ImmutableArray<StorageOwnedCollectionDefinition> ownedCollections,
        ExecutionProvenance provenance)
    {
        StorageRealizationContract.RequireId(id.Value, nameof(id));
        SemanticModel = Guard.RequireNotNull(semanticModel);
        if (string.IsNullOrWhiteSpace(rootShape.GraphId.Value)
            || string.IsNullOrWhiteSpace(rootShape.ShapeId.Value))
        {
            throw new ArgumentException("A storage structure requires a graph-qualified root shape.", nameof(rootShape));
        }
        StorageRealizationContract.RequirePath(rootIdentityPath, nameof(rootIdentityPath));
        StorageRealizationContract.RequirePath(partitionPath, nameof(partitionPath));
        var normalizedInput = ownedCollections.IsDefault ? [] : ownedCollections;
        var normalized = new StorageOwnedCollectionDefinition[normalizedInput.Length];
        HashSet<StorageOwnedCollectionId> identities = [];
        for (var index = 0; index < normalizedInput.Length; index++)
        {
            var collection = normalizedInput[index]
                ?? throw new ArgumentException("Owned collections cannot contain null entries.", nameof(ownedCollections));
            if (!identities.Add(collection.Id))
            {
                throw new ArgumentException(
                    $"Owned collection identity '{collection.Id.Value}' is duplicated.",
                    nameof(ownedCollections));
            }
            normalized[index] = collection;
        }

        Id = id;
        RootShape = rootShape;
        RootIdentityPath = rootIdentityPath;
        PartitionPath = partitionPath;
        OwnedCollections = [.. normalized.OrderBy(static collection => collection.Id.Value, StringComparer.Ordinal)];
        Provenance = Guard.RequireNotNull(provenance);
    }

    /// <summary>Stable structure identity.</summary>
    public StorageStructureId Id { get; }

    /// <summary>Exact portable shape graph containing the root and component types.</summary>
    public ShapeGraphDocument SemanticModel { get; }

    /// <summary>Independently governed aggregate-root shape.</summary>
    public QualifiedShapeId RootShape { get; }

    /// <summary>Root-relative stable observation identity path.</summary>
    public FieldPath RootIdentityPath { get; }

    /// <summary>Root-relative logical partition path inherited by owned components.</summary>
    public FieldPath PartitionPath { get; }

    /// <summary>Owned collections in deterministic stable-identity order.</summary>
    public ImmutableArray<StorageOwnedCollectionDefinition> OwnedCollections { get; }

    /// <summary>Required producer and source attribution.</summary>
    public ExecutionProvenance Provenance { get; }
}

static class StorageRealizationContract
{
    public static void RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A storage realization identity cannot be empty.", parameterName);
    }

    public static void RequirePath(FieldPath path, string parameterName)
    {
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A storage realization path cannot be empty.", parameterName);
    }

    public static ImmutableArray<string> NormalizeEvidence(
        ImmutableArray<string> references,
        string parameterName)
    {
        var normalizedInput = references.IsDefault ? [] : references;
        if (normalizedInput.IsDefaultOrEmpty)
            throw new ArgumentException("Storage realization evidence cannot be empty.", parameterName);

        HashSet<string> observed = new(StringComparer.Ordinal);
        var normalized = new string[normalizedInput.Length];
        for (var index = 0; index < normalizedInput.Length; index++)
        {
            var reference = Guard.RequireNotNullOrWhiteSpace(normalizedInput[index], parameterName);
            if (!observed.Add(reference))
                throw new ArgumentException($"Storage realization evidence '{reference}' is duplicated.", parameterName);
            normalized[index] = reference;
        }
        return [.. normalized.Order(StringComparer.Ordinal)];
    }
}
