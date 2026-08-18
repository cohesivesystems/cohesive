using Cohesive.Adapters.Postgres;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Npgsql;

namespace Cohesive.Tests.Postgres;

/// <summary>Contract tests for canonical entity-to-PostgreSQL repository mappings.</summary>
public sealed class PostgresEntityRepositoryTests
{
    [Fact]
    public void MappingRetainsCanonicalAuthorityAndProducesInjectionSafeCommands()
    {
        var entity = FreightOrderMaterializationModel.Create().Storage.Order;
        var mapping = OrderMapping();
        using var dataSource = DataSource();
        var repository = new PostgresEntityRepository(entity, Runtime(dataSource), mapping);
        var sql = PostgresEntityRepositorySql.Create(mapping);

        Assert.Equal(entity, repository.EntityDefinition);
        Assert.True(repository.BatchCapabilities.SupportsNativeBatching);
        Assert.True(repository.BatchCapabilities.SupportsSamePartitionAtomicity);
        Assert.True(repository.BatchCapabilities.SupportsAllOrNothingAtomicity);
        Assert.Equal(64, repository.BatchCapabilities.MaxItemsPerBatch);
        Assert.Contains("INSERT INTO \"freight_harness\".\"orders\"", sql.Upsert, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (\"tenant_id\", \"order_id\")", sql.Upsert, StringComparison.Ordinal);
        Assert.Contains("\"observation_version\"", sql.Upsert, StringComparison.Ordinal);
        Assert.Contains("xmin::text = @expected_concurrency", sql.Replace, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingRejectsMissingAndIncompatibleSemanticFields()
    {
        var entity = FreightOrderMaterializationModel.Create().Storage.Order;
        using var dataSource = DataSource();
        var runtime = Runtime(dataSource);
        var missing = new PostgresEntityRepositoryMapping(
            new PostgresSqlQualifiedTable("freight_harness", "orders"),
            [
                new("id", "order_id", PostgresRelationQueryScalarType.Text),
                new("tenantId", "tenant_id", PostgresRelationQueryScalarType.Text)
            ],
            identityField: "id",
            partitionField: "tenantId");
        var incompatible = new PostgresEntityRepositoryMapping(
            OrderMapping().Table,
            [
                .. OrderMapping().Fields.Select(field => field.FieldName == "createdAt"
                    ? new PostgresEntityRepositoryFieldBinding(
                        field.FieldName,
                        field.Column.Value,
                        PostgresRelationQueryScalarType.Text)
                    : field)
            ],
            identityField: "id",
            partitionField: "tenantId");

        var missingError = Assert.Throws<ArgumentException>(() => new PostgresEntityRepository(entity, runtime, missing));
        var incompatibleError = Assert.Throws<ArgumentException>(() => new PostgresEntityRepository(entity, runtime, incompatible));

        Assert.Contains("missing entity field", missingError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("createdAt", incompatibleError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchRejectsCapacityAndCrossPartitionClaimsBeforeDatabaseAccess()
    {
        var entity = FreightOrderMaterializationModel.Create().Storage.Order;
        using var dataSource = DataSource();
        var capacityRepository = new PostgresEntityRepository(
            entity,
            Runtime(dataSource),
            OrderMapping(maximumBatchItems: 1));
        var writes = new[]
        {
            Write(entity, id: "order-1", tenant: "tenant-a"),
            Write(entity, id: "order-2", tenant: "tenant-b")
        };

        var capacityError = await Assert.ThrowsAsync<NotSupportedException>(() => capacityRepository.UpsertBatch(
            OperationContext.Create(),
            new(writes, EntityBatchAtomicity.AllOrNothing)));

        var partitionRepository = new PostgresEntityRepository(entity, Runtime(dataSource), OrderMapping());
        var partitionError = await Assert.ThrowsAsync<NotSupportedException>(() => partitionRepository.UpsertBatch(
            OperationContext.Create(),
            new(writes, EntityBatchAtomicity.SamePartition)));

        Assert.Contains("at most 1", capacityError.Message, StringComparison.Ordinal);
        Assert.Contains("multiple partitions", partitionError.Message, StringComparison.Ordinal);
    }

    static EntityWriteRequest Write(EntityDefinition entity, string id, string tenant) => new(
        entity.CreateState(
            id,
            new FreightOrder
            {
                Id = id,
                TenantId = tenant,
                OrderNumber = $"ORD-{id}",
                CustomerAccountId = "customer-1",
                EquipmentClass = "DryVan",
                PickupStopId = "pickup-1",
                DeliveryStopId = "delivery-1",
                OriginLocationId = "origin-1",
                DestinationLocationId = "destination-1",
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            },
            version: 1).Observation);

    static PostgresEntityRepositoryMapping OrderMapping(int maximumBatchItems = 64) => new(
        new PostgresSqlQualifiedTable("freight_harness", "orders"),
        [
            new("id", "order_id", PostgresRelationQueryScalarType.Text),
            new("tenantId", "tenant_id", PostgresRelationQueryScalarType.Text),
            new("orderNumber", "order_number", PostgresRelationQueryScalarType.Text),
            new("customerAccountId", "customer_account_id", PostgresRelationQueryScalarType.Text),
            new("equipmentClass", "equipment_class", PostgresRelationQueryScalarType.Text),
            new("pickupStopId", "pickup_stop_id", PostgresRelationQueryScalarType.Text),
            new("deliveryStopId", "delivery_stop_id", PostgresRelationQueryScalarType.Text),
            new("originLocationId", "origin_location_id", PostgresRelationQueryScalarType.Text),
            new("destinationLocationId", "destination_location_id", PostgresRelationQueryScalarType.Text),
            new("createdAt", "created_at", PostgresRelationQueryScalarType.TimestampWithTimeZone)
        ],
        identityField: "id",
        partitionField: "tenantId",
        maximumBatchItems: maximumBatchItems);

    static NpgsqlDataSource DataSource() => NpgsqlDataSource.Create(
        "Host=localhost;Database=unused;Username=unused;Password=unused;Pooling=false");

    static PostgresNpgsqlRuntimeBinding Runtime(NpgsqlDataSource dataSource) => new(
        new("tests/postgres-entity-repository"),
        dataSource,
        "cohesive.tests");
}
