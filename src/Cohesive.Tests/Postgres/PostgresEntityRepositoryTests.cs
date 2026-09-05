using Cohesive.Adapters.Sql;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Model;
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
        var entity = OrderEntity();
        var mapping = OrderMapping();
        using var dataSource = DataSource();
        var repository = new PostgresEntityRepository(entity, Runtime(dataSource), mapping);
        var sql = PostgresEntityRepositorySql.Create(mapping);

        Assert.Equal(entity, repository.EntityDefinition);
        Assert.True(repository.BatchCapabilities.SupportsNativeBatching);
        Assert.True(repository.BatchCapabilities.SupportsSamePartitionAtomicity);
        Assert.True(repository.BatchCapabilities.SupportsAllOrNothingAtomicity);
        Assert.Equal(64, repository.BatchCapabilities.MaxItemsPerBatch);
        Assert.Contains("INSERT INTO \"freight_harness\".\"orders\"", sql.Upsert.Text, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (\"tenant_id\", \"order_id\")", sql.Upsert.Text, StringComparison.Ordinal);
        Assert.Contains("\"observation_version\"", sql.Upsert.Text, StringComparison.Ordinal);
        Assert.Contains("(\"xmin\" = $", sql.Replace.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql.ReadByIdentity.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql.ReadByIdentityAndPartition.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql.Upsert.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql.Replace.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingRejectsMissingAndIncompatibleSemanticFields()
    {
        var entity = OrderEntity();
        using var dataSource = DataSource();
        var runtime = Runtime(dataSource);
        var missing = new PostgresEntityRepositoryMapping(
            new SqlQualifiedTable("freight_harness", "orders"),
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
        var entity = OrderEntity();
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

    [Fact]
    public void CoreObservationRejectsSchemaInvalidStateBeforeRepositoryAccess()
    {
        var entity = OrderEntity();
        var valid = Write(entity, id: "order-invalid", tenant: "tenant-a").Entity;
        var fields = valid.Observation.Fields.ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.Ordinal);
        fields.Add("unexpected", ObservationValue.FromString("invalid"));

        var error = Assert.Throws<ArgumentException>(() => Cohesive.Model.Observation.Create(
            entity.StateShape,
            fields));

        Assert.Contains("unknown field 'unexpected'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsSchemaInvalidCompleteObservationBeforeSelectedFieldProjection()
    {
        var entity = OrderEntity();
        using var dataSource = DataSource();
        var repository = new PostgresEntityRepository(entity, Runtime(dataSource), OrderMapping());
        var invalid = ForeignSnapshot(entity, "order-invalid");

        var error = Assert.Throws<SemanticRuleViolationException>(() => repository.CreateValidatedReadSnapshot(
            complete: invalid,
            partition: "tenant-a",
            concurrencyToken: new("7"),
            selectedFields: new HashSet<string>(["id"], StringComparer.Ordinal)));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadValidatesCompleteObservationAndRetainsSelectionAsStorageMetadata()
    {
        var entity = OrderEntity();
        using var dataSource = DataSource();
        var repository = new PostgresEntityRepository(entity, Runtime(dataSource), OrderMapping());
        var complete = Write(entity, id: "order-1", tenant: "tenant-a").Entity;
        IReadOnlySet<string> selectedFields = new HashSet<string>(["id", "orderNumber"], StringComparer.Ordinal);

        var snapshot = repository.CreateValidatedReadSnapshot(
            complete: complete,
            partition: "tenant-a",
            concurrencyToken: new("7"),
            selectedFields: selectedFields);

        Assert.Equal(selectedFields, snapshot.LoadedFields);
        Assert.Equal("order-1", snapshot.Entity.Observation.GetField("id").GetRequiredString());
        Assert.Equal("ORD-order-1", snapshot.Entity.Observation.GetField("orderNumber").GetRequiredString());
        Assert.True(snapshot.Entity.Observation.TryGetField("tenantId", out _));
    }

    static EntityObservationSnapshot ForeignSnapshot(EntityDefinition entity, string id)
    {
        var valid = Write(entity, id: id, tenant: "tenant-a").Entity;
        ShapeGraph graph = new(new("tests/foreign-order-state/v1"), [entity.Shape]);
        var observation = Cohesive.Model.Observation.Create(
            new(graph, entity.Shape.Id),
            valid.Observation.Fields);
        return new(valid.EntityId, valid.Version, observation);
    }

    static EntityWriteRequest Write(EntityDefinition entity, string id, string tenant) => new(
        entity.CreateState(
            id,
            new RepositoryOrder
            {
                Id = id,
                TenantId = tenant,
                OrderNumber = $"ORD-{id}",
                CustomerAccountId = "customer-1",
                EquipmentClass = "DryVan",
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            },
            version: 1).Snapshot);

    static EntityDefinition OrderEntity()
    {
        QualifiedShapeId shapeId = new(
            new("cohesive.tests.postgres-entity-repository"),
            new("order"));
        var shape = RelationQuery.Expression().Clr.Shape<RepositoryOrder>(shapeId);
        var canonical = shape.Document.Graph.TryGetShape(shape.Id)
            ?? throw new InvalidOperationException("The repository test Order shape is absent.");
        return new(
            new("postgres-repository-test-order"),
            new Shape(
                canonical.Id,
                canonical.Fields,
                canonical.Constraints,
                canonical.Annotations,
                ShapeRoles.Entity));
    }

    static PostgresEntityRepositoryMapping OrderMapping(int maximumBatchItems = 64) => new(
        new SqlQualifiedTable("freight_harness", "orders"),
        [
            new("id", "order_id", PostgresRelationQueryScalarType.Text),
            new("tenantId", "tenant_id", PostgresRelationQueryScalarType.Text),
            new("orderNumber", "order_number", PostgresRelationQueryScalarType.Text),
            new("customerAccountId", "customer_account_id", PostgresRelationQueryScalarType.Text),
            new("equipmentClass", "equipment_class", PostgresRelationQueryScalarType.Text),
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

    sealed record RepositoryOrder
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("tenantId")]
        public required string TenantId { get; init; }

        [JsonPropertyName("orderNumber")]
        public required string OrderNumber { get; init; }

        [JsonPropertyName("customerAccountId")]
        public required string CustomerAccountId { get; init; }

        [JsonPropertyName("equipmentClass")]
        public required string EquipmentClass { get; init; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; init; }
    }
}
