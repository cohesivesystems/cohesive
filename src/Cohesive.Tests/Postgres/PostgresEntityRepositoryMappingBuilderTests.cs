using System.Text.Json.Serialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresEntityRepositoryMappingBuilderTests
{
    sealed record Order([property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("partition")] string Partition);
    static readonly EntityDefinition Entity = ObjectEntityDefinition.For<Order>(new("test/order"));
    static PostgresEntityRepositoryMapping Mapping() => PostgresEntityRepositoryMapping.For<Order>(Entity)
        .Table("public", "orders").Identity(order => order.Id, "order_id")
        .Partition(order => order.Partition, "partition_key").Build();
    public sealed record GuidEntity(Guid Id, string Partition);

    [Fact]
    public void Native_uuid_key_remains_rejected_by_the_existing_mapping_boundary()
    {
        var entity = new DomainModelBuilder().Entity<GuidEntity>("test/guid-entity");
        var error = Assert.Throws<ArgumentException>(() => PostgresEntityRepositoryMapping.For(entity)
            .Table("public", "guid_entities").Identity(value => value.Id, "id")
            .Partition(value => value.Partition, "partition").Build());
        Assert.Contains("required non-null text", error.Message);
    }

    [Fact]
    public void Fluent_mapping_preserves_names_encodings_and_rejects_invalid_declarations()
    {
        var mapping = Mapping();
        Assert.Equal("id", mapping.IdentityField);
        Assert.Equal("partition", mapping.PartitionField);
        Assert.Equal("order_id", mapping.Fields[0].Column.Value);
        Assert.All(mapping.Fields, field => Assert.Equal(PostgresRelationQueryScalarType.Text, field.ScalarType));
        var builder = PostgresEntityRepositoryMapping.For<Order>(Entity)
            .Table("public", "orders").Identity(order => order.Id, "id");
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<ArgumentException>(() => builder.Column(order => order.Id.Length, "length"));
        Assert.Throws<ArgumentException>(() => builder.Column(order => order.Id.ToUpper(), "upper"));
        var snapshot = builder.Partition(order => order.Partition, "partition").Build();
        builder.Column(order => order.Id, "duplicate");
        Assert.Equal(2, snapshot.Fields.Length);
        Assert.Throws<ArgumentException>(() => builder.Build());
        Assert.Throws<ArgumentException>(() => PostgresEntityRepositoryMapping.For<Order>(Entity)
            .Table("public", "orders").Identity(order => order.Id, "same")
            .Partition(order => order.Partition, "same").Build());
    }

}
