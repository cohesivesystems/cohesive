using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Storage.Realization;

/// <summary>Inspectable semantic and target evidence for one realized owned collection.</summary>
public sealed record StorageOwnedCollectionRealizationExplain
{
    internal StorageOwnedCollectionRealizationExplain(
        StorageOwnedCollectionDefinition semantic,
        StorageOwnedCollectionRealization realization)
    {
        Semantic = Guard.RequireNotNull(semantic);
        CollectionPath = semantic.CollectionPath;
        ComponentType = semantic.ComponentType;
        LocalIdentityPath = semantic.LocalIdentityPath;
        OrdinalPath = semantic.OrdinalPath;
        Acquisition = realization.Acquisition;
        Atomicity = realization.Atomicity;
        ChangeCapture = realization.ChangeCapture;
        BindingEvidenceReferences = realization.BindingEvidenceReferences;
        AcquisitionEvidenceReference = realization.AcquisitionEvidenceReference;
        AtomicityEvidenceReference = realization.AtomicityEvidenceReference;
        ChangeCaptureEvidenceReference = realization.ChangeCaptureEvidenceReference;
    }

    /// <summary>Canonical owned-collection definition.</summary>
    public StorageOwnedCollectionDefinition Semantic { get; }

    /// <summary>Canonical root-relative collection path.</summary>
    public FieldPath CollectionPath { get; }

    /// <summary>Canonical named structural component type.</summary>
    public TypeId ComponentType { get; }

    /// <summary>Canonical component-local stable identity path.</summary>
    public FieldPath LocalIdentityPath { get; }

    /// <summary>Canonical component-relative ordering ordinal path.</summary>
    public FieldPath OrdinalPath { get; }

    /// <summary>Selected collection acquisition strategy.</summary>
    public StorageOwnedCollectionAcquisitionKind Acquisition { get; }

    /// <summary>Declared aggregate atomicity boundary.</summary>
    public StorageAggregateAtomicityKind Atomicity { get; }

    /// <summary>Selected physical-change-to-root strategy.</summary>
    public StorageOwnedCollectionChangeCaptureKind ChangeCapture { get; }

    /// <summary>Adapter-owned physical mapping evidence.</summary>
    public ImmutableArray<string> BindingEvidenceReferences { get; }

    /// <summary>Evidence supporting <see cref="Acquisition"/>.</summary>
    public string AcquisitionEvidenceReference { get; }

    /// <summary>Evidence supporting <see cref="Atomicity"/>.</summary>
    public string AtomicityEvidenceReference { get; }

    /// <summary>Evidence supporting <see cref="ChangeCapture"/>.</summary>
    public string ChangeCaptureEvidenceReference { get; }
}

/// <summary>Complete deterministic explain projection for one Storage Realization document.</summary>
public sealed record StorageRealizationExplainArtifact
{
    internal StorageRealizationExplainArtifact(
        StorageStructureId structure,
        QualifiedShapeId rootShape,
        ExecutionDefinitionFingerprint structureFingerprint,
        StorageTargetRealizationId realization,
        StorageRealizationTarget target,
        ExecutionDefinitionFingerprint realizationFingerprint,
        ImmutableArray<StorageOwnedCollectionRealizationExplain> ownedCollections)
    {
        Structure = structure;
        RootShape = rootShape;
        StructureFingerprint = Guard.RequireNotNull(structureFingerprint);
        Realization = realization;
        Target = Guard.RequireNotNull(target);
        RealizationFingerprint = Guard.RequireNotNull(realizationFingerprint);
        OwnedCollections = ownedCollections;
    }

    /// <summary>Canonical storage-structure identity.</summary>
    public StorageStructureId Structure { get; }

    /// <summary>Independently governed aggregate-root shape.</summary>
    public QualifiedShapeId RootShape { get; }

    /// <summary>Exact canonical semantic-structure fingerprint.</summary>
    public ExecutionDefinitionFingerprint StructureFingerprint { get; }

    /// <summary>Target realization identity.</summary>
    public StorageTargetRealizationId Realization { get; }

    /// <summary>Adapter and exact capability-profile identity.</summary>
    public StorageRealizationTarget Target { get; }

    /// <summary>Exact target-realization fingerprint.</summary>
    public ExecutionDefinitionFingerprint RealizationFingerprint { get; }

    /// <summary>Owned-collection explanations in canonical identity order.</summary>
    public ImmutableArray<StorageOwnedCollectionRealizationExplain> OwnedCollections { get; }
}

/// <summary>Projects target strategy, guarantees, impact mapping, and binding provenance for review and tooling.</summary>
public static class StorageRealizationExplainProjector
{
    /// <summary>Projects one validated Storage Realization document.</summary>
    /// <param name="document">Document to explain.</param>
    /// <returns>A deterministic explanation retaining canonical semantics and exact target evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The document is invalid, stale, or semantically mismatched.</exception>
    public static StorageRealizationExplainArtifact Project(StorageRealizationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validation = StorageRealizationValidator.Validate(document);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join(" ", validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(document));
        }

        var semantic = document.Structure.OwnedCollections.ToDictionary(static collection => collection.Id);
        var collections = document.Realization.OwnedCollections.Select(realization => new
            StorageOwnedCollectionRealizationExplain(
                semantic: semantic[realization.Collection],
                realization: realization)).ToImmutableArray();
        return new(
            structure: document.Structure.Id,
            rootShape: document.Structure.RootShape,
            structureFingerprint: document.StructureFingerprint,
            realization: document.Realization.Id,
            target: document.Realization.Target,
            realizationFingerprint: document.RealizationFingerprint,
            ownedCollections: collections);
    }
}
