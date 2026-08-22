using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Realization;

namespace Cohesive.Tests.Storage;

public sealed class StorageRealizationTests
{
    [Fact]
    public void EmbeddedAndDecomposedRealizations_ShareOneCanonicalStructureFingerprint()
    {
        var structure = Structure();
        var structureFingerprint = StorageRealizationFingerprinter.ComputeStructure(structure);
        var embedded = Target(
            structureFingerprint,
            new StorageEmbeddedOwnedCollectionRealization(
                collection: new("order/stops"),
                bindingEvidenceReferences: ["cosmos/field-binding/sha256/b", "cosmos/container-binding/sha256/a"],
                acquisitionEvidenceReference: "cosmos/json-array-expansion/v1",
                atomicityEvidenceReference: "cosmos/single-document-transaction/v1",
                changeCaptureEvidenceReference: "cosmos/root-document-change/v1"),
            adapter: "cohesive.adapters.cosmos",
            profile: "cosmos/structured-document/v1");
        var decomposed = Target(
            structureFingerprint,
            new StorageDecomposedOwnedCollectionRealization(
                collection: new("order/stops"),
                bindingEvidenceReferences: ["postgres/component-binding/sha256/b", "postgres/root-binding/sha256/a"],
                acquisitionEvidenceReference: "postgres/root-page-correlated-components/v1",
                atomicityEvidenceReference: "postgres/aggregate-transaction/v1",
                changeCaptureEvidenceReference: "postgres/component-parent-impact/v1"),
            adapter: "cohesive.adapters.postgres",
            profile: "postgres/decomposed-aggregate/v1");

        var embeddedDocument = StorageRealizationDocument.FromDefinitions(
            structure: structure,
            realization: embedded);
        var decomposedDocument = StorageRealizationDocument.FromDefinitions(
            structure: structure,
            realization: decomposed);

        Assert.Equal(embeddedDocument.StructureFingerprint, decomposedDocument.StructureFingerprint);
        Assert.NotEqual(embeddedDocument.RealizationFingerprint, decomposedDocument.RealizationFingerprint);
        Assert.Equal(
            StorageOwnedCollectionAcquisitionKind.InDocumentExpansion,
            Assert.Single(embeddedDocument.Realization.OwnedCollections).Acquisition);
        Assert.Equal(
            StorageOwnedCollectionAcquisitionKind.RootCorrelatedComponentRecords,
            Assert.Single(decomposedDocument.Realization.OwnedCollections).Acquisition);
        Assert.Equal(
            StorageAggregateAtomicityKind.SingleDocument,
            Assert.Single(embeddedDocument.Realization.OwnedCollections).Atomicity);
        Assert.Equal(
            StorageAggregateAtomicityKind.TransactionAcrossRecords,
            Assert.Single(decomposedDocument.Realization.OwnedCollections).Atomicity);
    }

    [Fact]
    public void JsonRoundTrip_IsStrictCanonicalAndPreservesFingerprints()
    {
        var structure = Structure();
        var realization = Target(
            StorageRealizationFingerprinter.ComputeStructure(structure),
            new StorageEmbeddedOwnedCollectionRealization(
                collection: new("order/stops"),
                bindingEvidenceReferences: ["binding/z", "binding/a"],
                acquisitionEvidenceReference: "acquisition/v1",
                atomicityEvidenceReference: "atomicity/v1",
                changeCaptureEvidenceReference: "changes/v1"),
            adapter: "cohesive.adapters.cosmos",
            profile: "cosmos/profile/v1");
        var document = StorageRealizationDocument.FromDefinitions(
            structure: structure,
            realization: realization);

        var json = StorageRealizationJsonSerializer.Serialize(
            document: document,
            formatting: PortableDocumentJsonFormatting.Compact);
        var roundTrip = StorageRealizationJsonSerializer.Deserialize(json);
        var roundTripJson = StorageRealizationJsonSerializer.Serialize(
            document: roundTrip,
            formatting: PortableDocumentJsonFormatting.Compact);

        Assert.Equal(json, roundTripJson);
        Assert.Equal(document.StructureFingerprint, roundTrip.StructureFingerprint);
        Assert.Equal(document.RealizationFingerprint, roundTrip.RealizationFingerprint);
        Assert.Contains("\"$strategy\":\"embedded\"", json, StringComparison.Ordinal);
        Assert.True(
            json.IndexOf("binding/a", StringComparison.Ordinal)
            < json.IndexOf("binding/z", StringComparison.Ordinal));
    }

    [Fact]
    public void Explain_ProjectsSemanticPathsStrategyGuaranteesAndAdapterEvidence()
    {
        var structure = Structure();
        var realization = Target(
            StorageRealizationFingerprinter.ComputeStructure(structure),
            new StorageDecomposedOwnedCollectionRealization(
                collection: new("order/stops"),
                bindingEvidenceReferences: ["postgres/storage-binding/sha256/abc"],
                acquisitionEvidenceReference: "postgres/root-page-correlated-components/v1",
                atomicityEvidenceReference: "postgres/transaction/v1",
                changeCaptureEvidenceReference: "postgres/parent-key/v1"),
            adapter: "cohesive.adapters.postgres",
            profile: "postgres/profile/v1");
        var document = StorageRealizationDocument.FromDefinitions(
            structure: structure,
            realization: realization);

        var explain = StorageRealizationExplainProjector.Project(document);
        var collection = Assert.Single(explain.OwnedCollections);

        Assert.Equal(FieldPath.FromField("stops"), collection.CollectionPath);
        Assert.Equal(FieldPath.FromField("id"), collection.LocalIdentityPath);
        Assert.Equal(FieldPath.FromField("sequence"), collection.OrdinalPath);
        Assert.Equal(
            StorageOwnedCollectionChangeCaptureKind.ComponentParentIdentity,
            collection.ChangeCapture);
        Assert.Equal(["postgres/storage-binding/sha256/abc"], collection.BindingEvidenceReferences.ToArray());
        Assert.Equal(document.StructureFingerprint, explain.StructureFingerprint);
        Assert.Equal(document.RealizationFingerprint, explain.RealizationFingerprint);
    }

    [Fact]
    public void Validation_RejectsInvalidSemanticPathsAndMissingTargetCoverage()
    {
        var valid = Structure();
        var invalid = new StorageStructureDefinition(
            id: valid.Id,
            semanticModel: valid.SemanticModel,
            rootShape: valid.RootShape,
            rootIdentityPath: FieldPath.FromField("missingIdentity"),
            partitionPath: valid.PartitionPath,
            ownedCollections:
            [
                new(
                    id: new("order/stops"),
                    collectionPath: FieldPath.FromField("stops"),
                    componentType: new("freight.stop"),
                    localIdentityPath: FieldPath.FromField("missingStopId"),
                    ordinalPath: FieldPath.FromField("locationId"))
            ],
            provenance: valid.Provenance);

        var semanticValidation = StorageRealizationValidator.ValidateStructure(invalid);
        Assert.Contains(semanticValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == StorageRealizationDiagnosticCodes.RootFieldInvalid);
        Assert.Contains(semanticValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == StorageRealizationDiagnosticCodes.ComponentIdentityInvalid);
        Assert.Contains(semanticValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == StorageRealizationDiagnosticCodes.ComponentOrdinalInvalid);

        var fingerprint = StorageRealizationFingerprinter.ComputeStructure(valid);
        var uncovered = new StorageTargetRealization(
            id: new("postgres/freight/v1"),
            structureFingerprint: fingerprint,
            target: new("cohesive.adapters.postgres", "postgres/profile/v1"),
            ownedCollections: [],
            provenance: Provenance("tests/postgres"));
        var coverageValidation = StorageRealizationValidator.Validate(valid, uncovered);
        Assert.Contains(coverageValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == StorageRealizationDiagnosticCodes.CollectionRealizationMissing);
    }

    [Fact]
    public void Definitions_RejectDuplicateOwnedCollectionIdentities()
    {
        var valid = Structure();
        var collection = Assert.Single(valid.OwnedCollections);

        var exception = Assert.Throws<ArgumentException>(() => new StorageStructureDefinition(
            id: valid.Id,
            semanticModel: valid.SemanticModel,
            rootShape: valid.RootShape,
            rootIdentityPath: valid.RootIdentityPath,
            partitionPath: valid.PartitionPath,
            ownedCollections: [collection, collection],
            provenance: valid.Provenance));

        Assert.Equal("ownedCollections", exception.ParamName);
    }

    [Fact]
    public void JsonLoading_RejectsGuaranteeThatContradictsConcreteStrategy()
    {
        var structure = Structure();
        var realization = Target(
            StorageRealizationFingerprinter.ComputeStructure(structure),
            new StorageEmbeddedOwnedCollectionRealization(
                collection: new("order/stops"),
                bindingEvidenceReferences: ["cosmos/binding/v1"],
                acquisitionEvidenceReference: "cosmos/acquisition/v1",
                atomicityEvidenceReference: "cosmos/atomicity/v1",
                changeCaptureEvidenceReference: "cosmos/changes/v1"),
            adapter: "cohesive.adapters.cosmos",
            profile: "cosmos/profile/v1");
        var json = StorageRealizationJsonSerializer.Serialize(
            document: StorageRealizationDocument.FromDefinitions(
                structure: structure,
                realization: realization),
            formatting: PortableDocumentJsonFormatting.Compact);
        var weakened = json.Replace(
            "\"atomicity\":\"SingleDocument\"",
            "\"atomicity\":\"TransactionAcrossRecords\"",
            StringComparison.Ordinal);

        var validation = StorageRealizationJsonSerializer.TryDeserialize(
            json: weakened,
            document: out _);

        Assert.NotEqual(json, weakened);
        Assert.False(validation.IsValid);
    }

    [Fact]
    public void Validation_RejectsForeignStructureAndStaleDocumentFingerprints()
    {
        var structure = Structure();
        var correctStructureFingerprint = StorageRealizationFingerprinter.ComputeStructure(structure);
        var foreignFingerprint = new ExecutionDefinitionFingerprint(
            algorithm: "sha256",
            canonicalization: StorageRealizationFingerprinter.StructureCanonicalization,
            value: new string('f', 64));
        var foreign = Target(
            foreignFingerprint,
            new StorageEmbeddedOwnedCollectionRealization(
                collection: new("order/stops"),
                bindingEvidenceReferences: ["cosmos/binding/v1"],
                acquisitionEvidenceReference: "cosmos/acquisition/v1",
                atomicityEvidenceReference: "cosmos/atomicity/v1",
                changeCaptureEvidenceReference: "cosmos/changes/v1"),
            adapter: "cohesive.adapters.cosmos",
            profile: "cosmos/profile/v1");

        var linkage = StorageRealizationValidator.Validate(structure, foreign);
        Assert.Contains(linkage.Diagnostics, static diagnostic =>
            diagnostic.Code == StorageRealizationDiagnosticCodes.StructureLinkMismatch);

        var linked = Target(
            correctStructureFingerprint,
            Assert.IsType<StorageEmbeddedOwnedCollectionRealization>(Assert.Single(foreign.OwnedCollections)),
            adapter: "cohesive.adapters.cosmos",
            profile: "cosmos/profile/v1");
        var staleDocument = new StorageRealizationDocument(
            schemaVersion: StorageRealizationDocument.CurrentSchemaVersion,
            structure: structure,
            structureFingerprint: foreignFingerprint,
            realization: linked,
            realizationFingerprint: new(
                algorithm: "sha256",
                canonicalization: StorageRealizationFingerprinter.TargetCanonicalization,
                value: new string('e', 64)));

        var stale = StorageRealizationValidator.Validate(staleDocument);
        Assert.Equal(
            2,
            stale.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == StorageRealizationDiagnosticCodes.FingerprintMismatch));
    }

    internal static StorageStructureDefinition Structure()
    {
        var graphId = new GraphId("freight/storage/v1");
        var stopType = new TypeId("freight.stop");
        var rootShape = new QualifiedShapeId(graphId, new("freight.order"));
        var graph = new ShapeGraph(
            id: graphId,
            shapes:
            [
                new(
                    id: rootShape.ShapeId,
                    fields:
                    [
                        new(new("id"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new(new("tenantId"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new(
                            new("stops"),
                            new NamedTypeRef(stopType),
                            cardinality: FieldCardinality.Many)
                    ],
                    role: ShapeRoles.Entity)
            ],
            namedTypes:
            [
                new TypeDefinition.Structural(
                    id: stopType,
                    fields:
                    [
                        new(new("id"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new(new("sequence"), new ScalarTypeRef(ScalarTypeKind.Int32)),
                        new(new("locationId"), new ScalarTypeRef(ScalarTypeKind.String))
                    ])
            ]);
        return new(
            id: new("freight/order"),
            semanticModel: ShapeGraphDocument.FromGraph(graph),
            rootShape: rootShape,
            rootIdentityPath: FieldPath.FromField("id"),
            partitionPath: FieldPath.FromField("tenantId"),
            ownedCollections:
            [
                new(
                    id: new("order/stops"),
                    collectionPath: FieldPath.FromField("stops"),
                    componentType: stopType,
                    localIdentityPath: FieldPath.FromField("id"),
                    ordinalPath: FieldPath.FromField("sequence"))
            ],
            provenance: Provenance("tests/freight-order"));
    }

    static StorageTargetRealization Target(
        ExecutionDefinitionFingerprint structureFingerprint,
        StorageOwnedCollectionRealization collection,
        string adapter,
        string profile) => new(
        id: new($"{adapter}/freight-order/v1"),
        structureFingerprint: structureFingerprint,
        target: new(adapter, profile),
        ownedCollections: [collection],
        provenance: Provenance($"tests/{adapter}"));

    internal static ExecutionProvenance Provenance(string reference) => new(
        producer: new("cohesive-tests", "1"),
        source: new(reference),
        origin: DocumentOrigin.Generated);
}
