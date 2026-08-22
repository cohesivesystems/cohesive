using System.Collections.Immutable;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Realization;

namespace Cohesive.Tests.Storage;

public sealed class StorageAdapterRealizationCompilerTests
{
    static readonly RelationQuerySourceInstanceId Source = new("freight-source");
    static readonly RelationQueryInputId RootInput = new("source:orders");
    static readonly RelationQueryInputId TenantInput = new("field:orders:tenantId");
    static readonly RelationQueryInputId StopsInput = new("field:orders:stops");

    [Fact]
    public void SameCanonicalStructure_CompilesToEmbeddedCosmosAndDecomposedPostgresRealizations()
    {
        var structure = StorageRealizationTests.Structure();
        var placement = Placement(structure);
        var postgresBinding = PostgresBinding(placement);
        var cosmosBinding = CosmosBinding(placement);

        var postgres = new PostgresStorageRealizationCompiler().Compile(
            structure: structure,
            rootPlacement: placement,
            storageBinding: postgresBinding,
            realizationId: new("postgres/freight-order/v1"),
            provenance: StorageRealizationTests.Provenance("tests/postgres-realization"));
        var cosmos = new CosmosStorageRealizationCompiler().Compile(
            structure: structure,
            rootPlacement: placement,
            storageBinding: cosmosBinding,
            realizationId: new("cosmos/freight-order/v1"),
            provenance: StorageRealizationTests.Provenance("tests/cosmos-realization"));

        Assert.True(
            postgres.IsSuccessful,
            string.Join(Environment.NewLine, postgres.Diagnostics.Select(static item => item.Message)));
        Assert.True(
            cosmos.IsSuccessful,
            string.Join(Environment.NewLine, cosmos.Diagnostics.Select(static item => item.Message)));
        var postgresDocument = Assert.IsType<StorageRealizationDocument>(postgres.Document);
        var cosmosDocument = Assert.IsType<StorageRealizationDocument>(cosmos.Document);
        Assert.Equal(postgresDocument.StructureFingerprint, cosmosDocument.StructureFingerprint);
        var decomposed = Assert.IsType<StorageDecomposedOwnedCollectionRealization>(
            Assert.Single(postgresDocument.Realization.OwnedCollections));
        var embedded = Assert.IsType<StorageEmbeddedOwnedCollectionRealization>(
            Assert.Single(cosmosDocument.Realization.OwnedCollections));
        Assert.Equal(StorageAggregateAtomicityKind.TransactionAcrossRecords, decomposed.Atomicity);
        Assert.Equal(StorageOwnedCollectionChangeCaptureKind.ComponentParentIdentity, decomposed.ChangeCapture);
        Assert.Equal(StorageAggregateAtomicityKind.SingleDocument, embedded.Atomicity);
        Assert.Equal(StorageOwnedCollectionChangeCaptureKind.RootDocumentIdentity, embedded.ChangeCapture);
        Assert.Contains(
            decomposed.BindingEvidenceReferences,
            reference => reference.Contains(postgresBinding.Fingerprint.Value, StringComparison.Ordinal));
        Assert.Contains(
            embedded.BindingEvidenceReferences,
            reference => reference.Contains(cosmosBinding.Fingerprint.Value, StringComparison.Ordinal));
        var postgresExplain = Assert.Single(
            StorageRealizationExplainProjector.Project(postgresDocument).OwnedCollections);
        var cosmosExplain = Assert.Single(
            StorageRealizationExplainProjector.Project(cosmosDocument).OwnedCollections);
        Assert.Equal(FieldPath.FromField("stops"), postgresExplain.CollectionPath);
        Assert.Equal(postgresExplain.LocalIdentityPath, cosmosExplain.LocalIdentityPath);
        Assert.Equal(FieldPath.FromField("sequence"), postgresExplain.OrdinalPath);
        Assert.Equal(postgresExplain.OrdinalPath, cosmosExplain.OrdinalPath);
    }

    [Fact]
    public void PhysicalComponentMapping_ChangesBindingAndRealizationButNotCanonicalStructure()
    {
        var structure = StorageRealizationTests.Structure();
        var placement = Placement(structure);
        var firstBinding = PostgresBinding(placement, locationColumn: "location_id");
        var secondBinding = PostgresBinding(placement, locationColumn: "facility_id");
        var compiler = new PostgresStorageRealizationCompiler();

        var first = compiler.Compile(
            structure,
            placement,
            firstBinding,
            new("postgres/freight-order/first"),
            StorageRealizationTests.Provenance("tests/postgres-first"));
        var second = compiler.Compile(
            structure,
            placement,
            secondBinding,
            new("postgres/freight-order/second"),
            StorageRealizationTests.Provenance("tests/postgres-second"));

        Assert.NotEqual(firstBinding.Fingerprint, secondBinding.Fingerprint);
        Assert.Equal(first.Document!.StructureFingerprint, second.Document!.StructureFingerprint);
        Assert.NotEqual(first.Document.RealizationFingerprint, second.Document.RealizationFingerprint);
        Assert.Contains(
            Assert.Single(first.Document.Realization.OwnedCollections).BindingEvidenceReferences,
            reference => reference.Contains(firstBinding.Fingerprint.Value, StringComparison.Ordinal));
        Assert.Contains(
            Assert.Single(second.Document.Realization.OwnedCollections).BindingEvidenceReferences,
            reference => reference.Contains(secondBinding.Fingerprint.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void Compilers_RejectUnprovenOrdinalAndStructuredArrayGuarantees()
    {
        var structure = StorageRealizationTests.Structure();
        var placement = Placement(structure);
        var postgres = new PostgresStorageRealizationCompiler().Compile(
            structure,
            placement,
            PostgresBinding(placement, exactOrdinal: false),
            new("postgres/freight-order/unproven"),
            StorageRealizationTests.Provenance("tests/postgres-unproven"));
        var cosmos = new CosmosStorageRealizationCompiler().Compile(
            structure,
            placement,
            CosmosBinding(placement, orderedProfile: false),
            new("cosmos/freight-order/unproven"),
            StorageRealizationTests.Provenance("tests/cosmos-unproven"));

        Assert.False(postgres.IsSuccessful);
        Assert.Contains(postgres.Diagnostics, static diagnostic =>
            diagnostic.Code == PostgresStorageRealizationDiagnosticCodes.GuaranteeUnavailable);
        Assert.False(cosmos.IsSuccessful);
        Assert.Contains(cosmos.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosStorageRealizationDiagnosticCodes.GuaranteeUnavailable);
    }

    [Fact]
    public void PostgresCompiler_RejectsAComponentTableOutsideTheRootTenantSelector()
    {
        var structure = StorageRealizationTests.Structure();
        var placement = Placement(structure);

        var result = new PostgresStorageRealizationCompiler().Compile(
            structure,
            placement,
            PostgresBinding(placement, componentPartitionSelector: "organizationId"),
            new("postgres/freight-order/wrong-tenant"),
            StorageRealizationTests.Provenance("tests/postgres-wrong-tenant"));

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == PostgresStorageRealizationDiagnosticCodes.GuaranteeUnavailable
            && diagnostic.Message.Contains("tenant partition", StringComparison.Ordinal));
    }

    static RelationQuerySourcePlacementBinding Placement(StorageStructureDefinition structure) => new(
        id: new("placement:orders"),
        input: RootInput,
        node: new QueryNodeId("node:orders"),
        binding: new ValueBindingId("binding:orders"),
        shape: structure.RootShape,
        source: Source,
        kind: RelationQuerySourcePlacementBindingKind.SourceSet,
        acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
        origin: RelationQuerySourcePlacementOrigin.Explicit,
        identity: new(
            shape: structure.RootShape,
            sourceSelector: "id",
            semanticPath: structure.RootIdentityPath),
        fields:
        [
            new(TenantInput, structure.PartitionPath, "tenantId"),
            new(StopsInput, FieldPath.FromField("stops"), "stops")
        ],
        partition: new("tenantId"));

    static PostgresRelationQueryStorageBinding PostgresBinding(
        RelationQuerySourcePlacementBinding placement,
        string locationColumn = "location_id",
        bool exactOrdinal = true,
        string componentPartitionSelector = "tenantId")
    {
        var equalityText = new PostgresRelationQueryTextSemantics(
            collation: "C",
            equality: PostgresRelationQueryTextEqualitySemantics.Ordinal);
        var root = new PostgresRelationQueryTableBinding(
            source: Source,
            placementBinding: placement.Id,
            input: RootInput,
            shape: placement.Shape,
            schemaName: "public",
            tableName: "orders",
            identity: new(
                semanticPath: FieldPath.FromField("id"),
                columnName: "id",
                scalarType: PostgresRelationQueryScalarType.Text,
                textSemantics: equalityText),
            fields:
            [
                new(
                    input: TenantInput,
                    semanticPath: FieldPath.FromField("tenantId"),
                    columnName: "tenant_id",
                    scalarType: PostgresRelationQueryScalarType.Text,
                    missingValueEncoding: PostgresRelationQueryMissingValueEncoding.Prohibited,
                    nullValueEncoding: PostgresRelationQueryNullValueEncoding.Prohibited,
                    textSemantics: equalityText)
            ],
            partition: new(
                sourceSelector: "tenantId",
                semanticPath: FieldPath.FromField("tenantId"),
                columnName: "tenant_id",
                scalarType: PostgresRelationQueryScalarType.Text,
                textSemantics: equalityText));
        var stops = new PostgresRelationQueryOwnedCollectionBinding(
            collection: new("order/stops"),
            rootPlacementBinding: placement.Id,
            collectionInput: StopsInput,
            collectionPath: FieldPath.FromField("stops"),
            componentType: new("freight.stop"),
            schemaName: "public",
            tableName: "order_stops",
            parentRoot: new(
                semanticPath: FieldPath.FromField("id"),
                columnName: "order_id",
                scalarType: PostgresRelationQueryScalarType.Text,
                textSemantics: equalityText),
            partition: new(
                sourceSelector: componentPartitionSelector,
                semanticPath: FieldPath.FromField("tenantId"),
                columnName: "tenant_id",
                scalarType: PostgresRelationQueryScalarType.Text,
                textSemantics: equalityText),
            localIdentityPath: FieldPath.FromField("id"),
            ordinalPath: FieldPath.FromField("sequence"),
            fields:
            [
                new(
                    semanticPath: FieldPath.FromField("id"),
                    columnName: "id",
                    scalarType: PostgresRelationQueryScalarType.Text,
                    missingValueEncoding: PostgresRelationQueryMissingValueEncoding.Prohibited,
                    nullValueEncoding: PostgresRelationQueryNullValueEncoding.Prohibited,
                    textSemantics: equalityText),
                new(
                    semanticPath: FieldPath.FromField("sequence"),
                    columnName: "sequence",
                    scalarType: PostgresRelationQueryScalarType.Int32,
                    missingValueEncoding: PostgresRelationQueryMissingValueEncoding.Prohibited,
                    nullValueEncoding: PostgresRelationQueryNullValueEncoding.Prohibited,
                    ordering: exactOrdinal
                        ? PostgresRelationQueryOrderingCapability.Exact
                        : PostgresRelationQueryOrderingCapability.None),
                new(
                    semanticPath: FieldPath.FromField("locationId"),
                    columnName: locationColumn,
                    scalarType: PostgresRelationQueryScalarType.Text,
                    missingValueEncoding: PostgresRelationQueryMissingValueEncoding.Prohibited,
                    nullValueEncoding: PostgresRelationQueryNullValueEncoding.Prohibited,
                    textSemantics: equalityText)
            ],
            validatedParentForeignKeyName: "fk_order_stops_orders",
            validatedAggregateIdentityName: "uq_order_stops_tenant_order_id",
            atomicityEvidenceReference: "postgres/transaction/orders-order-stops/v1",
            changeCaptureEvidenceReference: "postgres/change/order-stops-parent-order-id/v1");
        return new(
            id: new("postgres/freight-order/v1"),
            database: new("freight-test"),
            target: PostgresRelationQueryTargetProfile.Target,
            targetProfile: PostgresRelationQueryTargetProfile.ProfileId,
            tables: [root],
            ownedCollections: [stops]);
    }

    static CosmosRelationQueryStorageBinding CosmosBinding(
        RelationQuerySourcePlacementBinding placement,
        bool orderedProfile = true)
    {
        const CosmosRelationQueryCollectionElementSemanticCapabilities comparisons =
            CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
            | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality;
        var scope = new CosmosRelationQueryCollectionScopeEvidence(
            semanticProfile: orderedProfile
                ? CosmosStorageRealizationCompiler.CanonicalOrderedOwnedCollectionProfile
                : "cosmos/json-array/canonical-any/v1",
            elementScope: CosmosRelationQueryCollectionElementScope.JsonArrayElement,
            correlationGuarantee: CosmosRelationQueryCollectionCorrelationGuarantee.SameArrayElement,
            collectionMissingValueBehavior:
                CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
            collectionNullValueBehavior:
                CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
            nullElementBehavior:
                CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
            emptyCollectionBehavior: CosmosRelationQueryEmptyCollectionBehavior.NoElements,
            childFields:
            [
                Child("id", CosmosRelationQueryCollectionElementValueDomain.String),
                Child("sequence", CosmosRelationQueryCollectionElementValueDomain.Int32),
                Child("locationId", CosmosRelationQueryCollectionElementValueDomain.String)
            ]);
        return new(
            id: new("cosmos/freight-order/v1"),
            source: Source,
            placementBinding: placement.Id,
            target: CosmosRelationQueryTargetProfile.Target,
            targetProfile: CosmosRelationQueryTargetProfile.ProfileId,
            accountEndpoint: new("https://localhost:8081/"),
            databaseName: "freight",
            containerName: "orders",
            rootAlias: "c",
            identityPath: FieldPath.FromField("id"),
            fields:
            [
                new(TenantInput, FieldPath.FromField("tenantId")),
                new(StopsInput, FieldPath.FromField("stops"), scope)
            ],
            partitionPath: FieldPath.FromField("tenantId"));

        CosmosRelationQueryCollectionElementFieldBinding Child(
            string name,
            CosmosRelationQueryCollectionElementValueDomain domain) => new(
            elementPath: FieldPath.FromField(name),
            documentPath: FieldPath.FromField(name),
            valueDomain: domain,
            semanticCapabilities: comparisons,
            semanticProfile: "cosmos/json-scalar/canonical-v1",
            missingValueBehavior:
                CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
            nullValueBehavior:
                CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion);
    }
}
