using System.Collections.Immutable;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;

namespace Cohesive.Tests.Postgres;

public sealed partial class PostgresRelationQuerySourceReaderTests
{
    [Fact]
    public async Task OwnedCollection_PagesRootsBeforeJoinAndReconstructsOrderedAggregateArrays()
    {
        var fixture = CreateOwnedCollectionFixture();
        var request = new RelationQuerySourceReadRequest(
            physicalPlan: PhysicalPlan,
            stage: new("read/source"),
            placementBinding: fixture.Placement.Id,
            source: SourceId,
            shape: Shape,
            identitySelector: "id",
            fields:
            [
                new(
                    input: fixture.StopsInput,
                    semanticPath: FieldPath.FromField("stops"),
                    sourceSelector: "stops",
                    purpose: RelationQuerySourceReadFieldPurpose.SemanticInput)
            ],
            constraint: new RelationQueryBoundedEnumeration(maximumRows: 2),
            maximumBufferedRows: 2);

        var result = await fixture.Reader.ReadAsync(request);

        Assert.True(
            result.State == RelationQuerySourceReadState.Partial,
            result.EvidenceReference);
        Assert.Equal(["order-a", "order-b"], result.Observations.Select(static row => row.Identity));
        var firstStops = Assert.Single(result.Observations[0].Fields).Value!.Value.Array;
        Assert.Equal(2, firstStops.Length);
        Assert.Equal("stop-a1", firstStops[0].Fields!["id"].String);
        Assert.Equal(0, firstStops[0].Fields!["sequence"].Int64);
        Assert.Equal("stop-a2", firstStops[1].Fields!["id"].String);
        Assert.Equal(1, firstStops[1].Fields!["sequence"].Int64);
        Assert.Empty(Assert.Single(result.Observations[1].Fields).Value!.Value.Array);

        var command = Assert.Single(fixture.Commands);
        var rootLimit = command.Text.IndexOf("LIMIT 3", StringComparison.Ordinal);
        var componentJoin = command.Text.IndexOf("LEFT JOIN", StringComparison.Ordinal);
        Assert.True(rootLimit >= 0 && componentJoin > rootLimit, command.Text);
        Assert.Contains(
            "(\"root_page\".\"_identity\" COLLATE \"C\") = (\"component\".\"order_id\" COLLATE \"C\")",
            command.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "(\"root_page\".\"_root_partition\" COLLATE \"C\") = (\"component\".\"tenant_id\" COLLATE \"C\")",
            command.Text,
            StringComparison.Ordinal);
        var tenant = Assert.Single(command.Parameters);
        Assert.Equal("tenant-a", tenant.Value);
    }

    [Fact]
    public async Task OwnedCollection_IdentityLookupFiltersRootsBeforeComponentJoin()
    {
        var fixture = CreateOwnedCollectionFixture(identityLookup: true);
        var request = new RelationQuerySourceReadRequest(
            physicalPlan: PhysicalPlan,
            stage: new("read/identity"),
            placementBinding: fixture.Placement.Id,
            source: SourceId,
            shape: Shape,
            identitySelector: "id",
            fields:
            [
                new(
                    input: fixture.StopsInput,
                    semanticPath: FieldPath.FromField("stops"),
                    sourceSelector: "stops",
                    purpose: RelationQuerySourceReadFieldPurpose.SemanticInput)
            ],
            constraint: new RelationQueryIdentityBatchLookup(["order-b"]),
            maximumBufferedRows: 2);

        var result = await fixture.Reader.ReadAsync(request);

        Assert.Equal(RelationQuerySourceReadState.Complete, result.State);
        Assert.Equal("order-b", Assert.Single(result.Observations).Identity);
        var command = Assert.Single(fixture.Commands);
        Assert.Contains(" = ANY(", command.Text, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, static parameter => parameter.IsArray);
    }

    [Fact]
    public async Task OwnedCollection_RelationshipLookupFiltersRootsBeforeComponentJoin()
    {
        var fixture = CreateOwnedCollectionFixture(relationshipLookup: true);
        var request = new RelationQuerySourceReadRequest(
            physicalPlan: PhysicalPlan,
            stage: new("read/predicate"),
            placementBinding: fixture.Placement.Id,
            source: SourceId,
            shape: Shape,
            identitySelector: "id",
            fields:
            [
                new(
                    input: fixture.CustomerInput,
                    semanticPath: FieldPath.FromField("customerAccountId"),
                    sourceSelector: "customer_id",
                    purpose: RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation),
                new(
                    input: fixture.StopsInput,
                    semanticPath: FieldPath.FromField("stops"),
                    sourceSelector: "stops",
                    purpose: RelationQuerySourceReadFieldPurpose.SemanticInput)
            ],
            constraint: new RelationQueryRelationshipKeyBatchLookup(
                relationshipReference: FieldPath.FromField("customerAccountId"),
                sourceSelector: "customer_id",
                keys: ["customer-a"]),
            maximumBufferedRows: 2);

        var result = await fixture.Reader.ReadAsync(request);

        Assert.Equal(RelationQuerySourceReadState.Complete, result.State);
        Assert.Equal("order-a", Assert.Single(result.Observations).Identity);
        var command = Assert.Single(fixture.Commands);
        Assert.Contains("\"source\".\"customer_id\"", command.Text, StringComparison.Ordinal);
        Assert.Contains(" = ANY(", command.Text, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, static parameter => parameter.IsArray);
    }

    static OwnedCollectionFixture CreateOwnedCollectionFixture(
        bool identityLookup = false,
        bool relationshipLookup = false)
    {
        var sourceInput = new RelationQueryInputId("input:orders");
        var stopsInput = new RelationQueryInputId("field:stops");
        var customerInput = new RelationQueryInputId("field:customerAccountId");
        var relationshipInput = new RelationQueryInputId("relationship:orders-customer");
        var equalityText = new PostgresRelationQueryTextSemantics(
            collation: "C",
            equality: PostgresRelationQueryTextEqualitySemantics.Ordinal);
        var placementBinding = new RelationQuerySourcePlacementBinding(
            id: new("placement:orders"),
            input: sourceInput,
            node: new QueryNodeId("node:orders"),
            binding: new ValueBindingId("binding:orders"),
            shape: Shape,
            source: SourceId,
            kind: identityLookup || relationshipLookup
                ? RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                : RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: identityLookup || relationshipLookup
                ? RelationQuerySourceAcquisitionKind.BoundedLookup
                : RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new(
                shape: Shape,
                sourceSelector: "id",
                semanticPath: FieldPath.FromField("id")),
            fields: relationshipLookup
                ?
                [
                    new(stopsInput, FieldPath.FromField("stops"), "stops"),
                    new(customerInput, FieldPath.FromField("customerAccountId"), "customer_id")
                ]
                : [new(stopsInput, FieldPath.FromField("stops"), "stops")],
            relationshipKeys: relationshipLookup
                ? [new(
                    relationshipInput,
                    FieldPath.FromField("customerAccountId"),
                    "customer_id")]
                : [],
            partition: new("tenantId"));
        var source = new RelationQuerySourceInstance(
            id: SourceId,
            executionDomain: new("postgres/tests-domain"),
            targetProfile: PostgresRelationQuerySourceTargetProfile.Default,
            limits: new(
                maximumBatchSize: 10,
                maximumBufferedRows: 10,
                maximumFanOut: 10,
                maximumConcurrency: 2));
        var plan = new RelationQueryCompiledPlanReference(
            compilerProfile: "tests/static-compiler/v1",
            definitionSchemaVersion: "tests/definition/v1",
            definitionFingerprint: new("sha256", "tests/definition/v1", "owned-definition"),
            shapeSnapshotsFingerprint: new("sha256", "tests/shapes/v1", "owned-shapes"),
            relationshipCatalogFingerprint: null,
            demandFingerprint: new("sha256", "tests/demand/v1", "owned-demand"),
            inputs: relationshipLookup
                ? [sourceInput, stopsInput, customerInput, relationshipInput]
                : [sourceInput, stopsInput]);
        var placement = new RelationQuerySourcePlacement(
            schemaVersion: RelationQuerySourcePlacement.CurrentSchemaVersion,
            plan: plan,
            conventionSetVersion: "tests/postgres-placement-conventions/v1",
            sourceInstances: [source],
            bindings: [placementBinding]);
        var root = new PostgresRelationQueryTableBinding(
            source: SourceId,
            placementBinding: placementBinding.Id,
            input: sourceInput,
            shape: Shape,
            schemaName: "public",
            tableName: "orders",
            identity: new(
                semanticPath: FieldPath.FromField("id"),
                columnName: "id",
                scalarType: PostgresRelationQueryScalarType.Text,
                textSemantics: equalityText),
            fields: relationshipLookup
                ?
                [
                    new(
                        customerInput,
                        FieldPath.FromField("customerAccountId"),
                        "customer_id",
                        PostgresRelationQueryScalarType.Text,
                        PostgresRelationQueryMissingValueEncoding.Prohibited,
                        PostgresRelationQueryNullValueEncoding.Prohibited,
                        textSemantics: equalityText)
                ]
                : [],
            relationshipReferences: relationshipLookup
                ?
                [
                    new(
                        relationshipInput,
                        FieldPath.FromField("customerAccountId"),
                        "customer_id",
                        PostgresRelationQueryScalarType.Text,
                        SourceReferenceUniqueness.NotGuaranteed,
                        PostgresRelationQueryMissingValueEncoding.Prohibited,
                        PostgresRelationQueryNullValueEncoding.Prohibited,
                        textSemantics: equalityText)
                ]
                : [],
            partition: new(
                sourceSelector: "tenantId",
                semanticPath: FieldPath.FromField("tenantId"),
                columnName: "tenant_id",
                scalarType: PostgresRelationQueryScalarType.Text,
                textSemantics: equalityText));
        var stops = new PostgresRelationQueryOwnedCollectionBinding(
            collection: new("order/stops"),
            rootPlacementBinding: placementBinding.Id,
            collectionInput: stopsInput,
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
                sourceSelector: "tenantId",
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
                    semanticPath: FieldPath.FromField("locationId"),
                    columnName: "location_id",
                    scalarType: PostgresRelationQueryScalarType.Text,
                    missingValueEncoding: PostgresRelationQueryMissingValueEncoding.Prohibited,
                    nullValueEncoding: PostgresRelationQueryNullValueEncoding.Prohibited),
                new(
                    semanticPath: FieldPath.FromField("sequence"),
                    columnName: "sequence",
                    scalarType: PostgresRelationQueryScalarType.Int32,
                    missingValueEncoding: PostgresRelationQueryMissingValueEncoding.Prohibited,
                    nullValueEncoding: PostgresRelationQueryNullValueEncoding.Prohibited,
                    ordering: PostgresRelationQueryOrderingCapability.Exact)
            ],
            validatedParentForeignKeyName: "fk_order_stops_orders",
            validatedAggregateIdentityName: "uq_order_stops_tenant_order_id",
            atomicityEvidenceReference: "postgres/transaction/orders-order-stops/v1",
            changeCaptureEvidenceReference: "postgres/change/order-stops-parent-order-id/v1");
        var storage = new PostgresRelationQueryStorageBinding(
            id: new("tests/postgres/owned-source-binding/v1"),
            database: new("tests-database"),
            target: PostgresRelationQueryTargetProfile.Target,
            targetProfile: PostgresRelationQueryTargetProfile.ProfileId,
            tables: [root],
            compiledPlanFingerprint: RelationQueryCompiledPlanReferenceFingerprinter.Compute(plan),
            placementFingerprint: placement.Fingerprint,
            ownedCollections: [stops]);
        List<PostgresNpgsqlCommand> commands = [];
        var reader = new PostgresRelationQuerySourceReader(
            physicalPlan: PhysicalPlan,
            placement: placement,
            source: source,
            storage: storage,
            executeCommand: (command, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                commands.Add(command);
                return ValueTask.FromResult(new PostgresNpgsqlCommandResult(
                    relationshipLookup
                        ? [["order-a", "customer-a", "stop-a1", "location-1", 0]]
                        : identityLookup
                        ? [["order-b", null, null, null]]
                        :
                        [
                            ["order-a", "stop-a1", "location-1", 0],
                            ["order-a", "stop-a2", "location-2", 1],
                            ["order-b", null, null, null],
                            ["order-c", "stop-c1", "location-3", 0]
                        ]));
            },
            policy: TenantPolicy("tenant-a"));
        return new(reader, placementBinding, stopsInput, customerInput, commands);
    }

    sealed record OwnedCollectionFixture(
        PostgresRelationQuerySourceReader Reader,
        RelationQuerySourcePlacementBinding Placement,
        RelationQueryInputId StopsInput,
        RelationQueryInputId CustomerInput,
        List<PostgresNpgsqlCommand> Commands);
}
