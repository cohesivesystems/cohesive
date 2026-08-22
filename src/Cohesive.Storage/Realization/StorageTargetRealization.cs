using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Storage.Realization;

/// <summary>Stable identity of one target-specific interpretation of a canonical storage structure.</summary>
[JsonConverter(typeof(Cohesive.Model.Serialization.SingleValueWrapperJsonConverter))]
public readonly record struct StorageTargetRealizationId
{
    /// <summary>Creates a target-realization identity.</summary>
    /// <param name="value">Stable versioned realization identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public StorageTargetRealizationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable realization identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Acquisition strategy used to reconstruct one canonical owned collection.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageOwnedCollectionAcquisitionKind
{
    /// <summary>The collection is expanded from the aggregate document that contains it.</summary>
    InDocumentExpansion = 0,

    /// <summary>Component records are correlated to an already bounded page of aggregate roots.</summary>
    RootCorrelatedComponentRecords = 1
}

/// <summary>Atomicity boundary preserving writes to one aggregate and its owned components.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageAggregateAtomicityKind
{
    /// <summary>The root and components commit as one physical document.</summary>
    SingleDocument = 0,

    /// <summary>The root and component records commit in one target transaction.</summary>
    TransactionAcrossRecords = 1
}

/// <summary>Physical change evidence used to resolve an affected canonical aggregate root.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageOwnedCollectionChangeCaptureKind
{
    /// <summary>A changed aggregate document directly supplies the canonical root identity.</summary>
    RootDocumentIdentity = 0,

    /// <summary>A changed component record supplies a parent reference resolving the canonical root identity.</summary>
    ComponentParentIdentity = 1
}

/// <summary>Adapter and exact capability-profile identity selected for a storage realization.</summary>
public sealed record StorageRealizationTarget
{
    /// <summary>Creates a realization target.</summary>
    /// <param name="adapter">Stable adapter identity.</param>
    /// <param name="capabilityProfile">Exact target capability-profile identity.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public StorageRealizationTarget(string adapter, string capabilityProfile)
    {
        Adapter = Guard.RequireNotNullOrWhiteSpace(adapter);
        CapabilityProfile = Guard.RequireNotNullOrWhiteSpace(capabilityProfile);
    }

    /// <summary>Stable adapter identity.</summary>
    public string Adapter { get; }

    /// <summary>Exact target capability-profile identity.</summary>
    public string CapabilityProfile { get; }
}

/// <summary>
/// Target-specific interpretation of one canonical owned collection without duplicating adapter mapping catalogs.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$strategy")]
[JsonDerivedType(typeof(StorageEmbeddedOwnedCollectionRealization), "embedded")]
[JsonDerivedType(typeof(StorageDecomposedOwnedCollectionRealization), "decomposed")]
public abstract record StorageOwnedCollectionRealization
{
    /// <summary>Creates common owned-collection realization evidence.</summary>
    /// <param name="collection">Canonical owned-collection identity being interpreted.</param>
    /// <param name="bindingEvidenceReferences">
    /// Fingerprints or stable references to adapter-owned physical mapping artifacts.
    /// </param>
    /// <param name="acquisitionEvidenceReference">Evidence for the selected acquisition strategy.</param>
    /// <param name="atomicityEvidenceReference">Evidence for the declared aggregate atomicity boundary.</param>
    /// <param name="changeCaptureEvidenceReference">Evidence for physical-change-to-root impact resolution.</param>
    /// <exception cref="ArgumentNullException">An evidence reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or evidence reference is empty or duplicated.</exception>
    protected StorageOwnedCollectionRealization(
        StorageOwnedCollectionId collection,
        ImmutableArray<string> bindingEvidenceReferences,
        string acquisitionEvidenceReference,
        string atomicityEvidenceReference,
        string changeCaptureEvidenceReference)
    {
        StorageRealizationContract.RequireId(collection.Value, nameof(collection));
        Collection = collection;
        BindingEvidenceReferences = StorageRealizationContract.NormalizeEvidence(
            bindingEvidenceReferences,
            nameof(bindingEvidenceReferences));
        AcquisitionEvidenceReference = Guard.RequireNotNullOrWhiteSpace(acquisitionEvidenceReference);
        AtomicityEvidenceReference = Guard.RequireNotNullOrWhiteSpace(atomicityEvidenceReference);
        ChangeCaptureEvidenceReference = Guard.RequireNotNullOrWhiteSpace(changeCaptureEvidenceReference);
    }

    /// <summary>Canonical owned-collection identity being interpreted.</summary>
    public StorageOwnedCollectionId Collection { get; }

    /// <summary>Adapter-owned physical mapping fingerprints or stable references.</summary>
    public ImmutableArray<string> BindingEvidenceReferences { get; }

    /// <summary>Evidence for the selected acquisition strategy.</summary>
    public string AcquisitionEvidenceReference { get; }

    /// <summary>Evidence for the declared aggregate atomicity boundary.</summary>
    public string AtomicityEvidenceReference { get; }

    /// <summary>Evidence for physical-change-to-root impact resolution.</summary>
    public string ChangeCaptureEvidenceReference { get; }

    /// <summary>Acquisition strategy fixed by the concrete realization alternative.</summary>
    public abstract StorageOwnedCollectionAcquisitionKind Acquisition { get; }

    /// <summary>Aggregate atomicity boundary fixed by the concrete realization alternative.</summary>
    public abstract StorageAggregateAtomicityKind Atomicity { get; }

    /// <summary>Change-to-root strategy fixed by the concrete realization alternative.</summary>
    public abstract StorageOwnedCollectionChangeCaptureKind ChangeCapture { get; }
}

/// <summary>Realizes an owned collection inside its aggregate document.</summary>
public sealed record StorageEmbeddedOwnedCollectionRealization : StorageOwnedCollectionRealization
{
    /// <summary>Creates an embedded owned-collection realization.</summary>
    /// <param name="collection">Canonical owned-collection identity being interpreted.</param>
    /// <param name="bindingEvidenceReferences">Adapter-owned document and field-binding evidence.</param>
    /// <param name="acquisitionEvidenceReference">Evidence for in-document array expansion.</param>
    /// <param name="atomicityEvidenceReference">Evidence for single-document aggregate atomicity.</param>
    /// <param name="changeCaptureEvidenceReference">Evidence that document changes resolve directly to the root.</param>
    /// <exception cref="ArgumentNullException">An evidence reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or evidence reference is empty or duplicated.</exception>
    [JsonConstructor]
    public StorageEmbeddedOwnedCollectionRealization(
        StorageOwnedCollectionId collection,
        ImmutableArray<string> bindingEvidenceReferences,
        string acquisitionEvidenceReference,
        string atomicityEvidenceReference,
        string changeCaptureEvidenceReference)
        : base(
            collection,
            bindingEvidenceReferences,
            acquisitionEvidenceReference,
            atomicityEvidenceReference,
            changeCaptureEvidenceReference)
    {
    }

    /// <inheritdoc />
    public override StorageOwnedCollectionAcquisitionKind Acquisition =>
        StorageOwnedCollectionAcquisitionKind.InDocumentExpansion;

    /// <inheritdoc />
    public override StorageAggregateAtomicityKind Atomicity => StorageAggregateAtomicityKind.SingleDocument;

    /// <inheritdoc />
    public override StorageOwnedCollectionChangeCaptureKind ChangeCapture =>
        StorageOwnedCollectionChangeCaptureKind.RootDocumentIdentity;
}

/// <summary>Realizes an owned collection as root-correlated component records.</summary>
public sealed record StorageDecomposedOwnedCollectionRealization : StorageOwnedCollectionRealization
{
    /// <summary>Creates a decomposed owned-collection realization.</summary>
    /// <param name="collection">Canonical owned-collection identity being interpreted.</param>
    /// <param name="bindingEvidenceReferences">Adapter-owned root and component-record mapping evidence.</param>
    /// <param name="acquisitionEvidenceReference">Evidence that roots are bounded before components are correlated.</param>
    /// <param name="atomicityEvidenceReference">Evidence for transaction atomicity across root and component records.</param>
    /// <param name="changeCaptureEvidenceReference">Evidence that component changes resolve through their parent identity.</param>
    /// <exception cref="ArgumentNullException">An evidence reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or evidence reference is empty or duplicated.</exception>
    [JsonConstructor]
    public StorageDecomposedOwnedCollectionRealization(
        StorageOwnedCollectionId collection,
        ImmutableArray<string> bindingEvidenceReferences,
        string acquisitionEvidenceReference,
        string atomicityEvidenceReference,
        string changeCaptureEvidenceReference)
        : base(
            collection,
            bindingEvidenceReferences,
            acquisitionEvidenceReference,
            atomicityEvidenceReference,
            changeCaptureEvidenceReference)
    {
    }

    /// <inheritdoc />
    public override StorageOwnedCollectionAcquisitionKind Acquisition =>
        StorageOwnedCollectionAcquisitionKind.RootCorrelatedComponentRecords;

    /// <inheritdoc />
    public override StorageAggregateAtomicityKind Atomicity =>
        StorageAggregateAtomicityKind.TransactionAcrossRecords;

    /// <inheritdoc />
    public override StorageOwnedCollectionChangeCaptureKind ChangeCapture =>
        StorageOwnedCollectionChangeCaptureKind.ComponentParentIdentity;
}

/// <summary>One exact target interpretation of a canonical storage-structure fingerprint.</summary>
public sealed record StorageTargetRealization
{
    /// <summary>Creates a target-specific storage realization.</summary>
    /// <param name="id">Stable versioned realization identity.</param>
    /// <param name="structureFingerprint">Exact canonical structure fingerprint being interpreted.</param>
    /// <param name="target">Adapter and capability-profile identity.</param>
    /// <param name="ownedCollections">One realization for every canonical owned collection.</param>
    /// <param name="provenance">Required producer and source attribution.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is empty or a collection realization is null or duplicated.</exception>
    [JsonConstructor]
    public StorageTargetRealization(
        StorageTargetRealizationId id,
        ExecutionDefinitionFingerprint structureFingerprint,
        StorageRealizationTarget target,
        ImmutableArray<StorageOwnedCollectionRealization> ownedCollections,
        ExecutionProvenance provenance)
    {
        StorageRealizationContract.RequireId(id.Value, nameof(id));
        StructureFingerprint = Guard.RequireNotNull(structureFingerprint);
        Target = Guard.RequireNotNull(target);
        var normalizedInput = ownedCollections.IsDefault ? [] : ownedCollections;
        var normalized = new StorageOwnedCollectionRealization[normalizedInput.Length];
        HashSet<StorageOwnedCollectionId> identities = [];
        for (var index = 0; index < normalizedInput.Length; index++)
        {
            var realization = normalizedInput[index]
                ?? throw new ArgumentException("Owned-collection realizations cannot contain null entries.", nameof(ownedCollections));
            if (!identities.Add(realization.Collection))
            {
                throw new ArgumentException(
                    $"Owned collection '{realization.Collection.Value}' is realized more than once.",
                    nameof(ownedCollections));
            }
            normalized[index] = realization;
        }

        Id = id;
        OwnedCollections = [.. normalized.OrderBy(static item => item.Collection.Value, StringComparer.Ordinal)];
        Provenance = Guard.RequireNotNull(provenance);
    }

    /// <summary>Stable versioned realization identity.</summary>
    public StorageTargetRealizationId Id { get; }

    /// <summary>Exact canonical structure fingerprint being interpreted.</summary>
    public ExecutionDefinitionFingerprint StructureFingerprint { get; }

    /// <summary>Adapter and capability-profile identity.</summary>
    public StorageRealizationTarget Target { get; }

    /// <summary>Owned-collection realizations in canonical collection-identity order.</summary>
    public ImmutableArray<StorageOwnedCollectionRealization> OwnedCollections { get; }

    /// <summary>Required producer and source attribution.</summary>
    public ExecutionProvenance Provenance { get; }
}

/// <summary>Portable envelope fencing canonical structure semantics and one target realization.</summary>
public sealed record StorageRealizationDocument
{
    /// <summary>Current portable Storage Realization document schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-storage-realization/v1";

    /// <summary>Creates a persisted Storage Realization document.</summary>
    /// <param name="schemaVersion">Exact portable document schema version.</param>
    /// <param name="structure">Canonical semantic storage structure.</param>
    /// <param name="structureFingerprint">Fingerprint of the complete canonical structure.</param>
    /// <param name="realization">One target-specific interpretation of the structure.</param>
    /// <param name="realizationFingerprint">Fingerprint of the complete target realization.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="schemaVersion"/> is empty or unsupported.</exception>
    [JsonConstructor]
    public StorageRealizationDocument(
        string schemaVersion,
        StorageStructureDefinition structure,
        ExecutionDefinitionFingerprint structureFingerprint,
        StorageTargetRealization realization,
        ExecutionDefinitionFingerprint realizationFingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Storage Realization schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }
        Structure = Guard.RequireNotNull(structure);
        StructureFingerprint = Guard.RequireNotNull(structureFingerprint);
        Realization = Guard.RequireNotNull(realization);
        RealizationFingerprint = Guard.RequireNotNull(realizationFingerprint);
    }

    /// <summary>Exact portable document schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Canonical semantic storage structure.</summary>
    public StorageStructureDefinition Structure { get; }

    /// <summary>Fingerprint of the complete canonical structure.</summary>
    public ExecutionDefinitionFingerprint StructureFingerprint { get; }

    /// <summary>One target-specific interpretation of the structure.</summary>
    public StorageTargetRealization Realization { get; }

    /// <summary>Fingerprint of the complete target realization.</summary>
    public ExecutionDefinitionFingerprint RealizationFingerprint { get; }

    /// <summary>Validates and fences a canonical structure and target realization.</summary>
    /// <param name="structure">Canonical storage structure.</param>
    /// <param name="realization">Target realization linked to <paramref name="structure"/>.</param>
    /// <returns>A validated current-version document with deterministic fingerprints.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Semantic structure or target realization validation fails.</exception>
    /// <exception cref="InvalidOperationException">Content has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">Content contains an unsupported serialization type.</exception>
    public static StorageRealizationDocument FromDefinitions(
        StorageStructureDefinition structure,
        StorageTargetRealization realization)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(realization);
        var structureFingerprint = StorageRealizationFingerprinter.ComputeStructure(structure);
        var candidate = new StorageRealizationDocument(
            CurrentSchemaVersion,
            structure,
            structureFingerprint,
            realization,
            StorageRealizationFingerprinter.ComputeTarget(realization));
        var validation = StorageRealizationValidator.Validate(candidate);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join(" ", validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(realization));
        }
        return candidate;
    }
}
