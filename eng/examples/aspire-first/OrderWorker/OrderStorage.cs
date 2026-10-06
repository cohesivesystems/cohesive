using Cohesive.Adapters.Postgres;
using System.Text.Json.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using Npgsql;

namespace AspireFirst.Orders;

/// <summary>POCO authoring source for the canonical order state.</summary>
/// <param name="Id">Order identity, serialized under its stable canonical field name.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Partition">Storage partition, not an authorization boundary.</param>
public sealed record Order(
    [property: JsonPropertyName(OrderStorage.IdField)] string Id,
    [property: JsonPropertyName(OrderStorage.PartitionField)] string Partition,
    [property: JsonPropertyName("status")] string Status = "Draft");

/// <summary>Canonical order state and its explicit PostgreSQL realization for the local example.</summary>
public static class OrderStorage
{
    /// <summary>Native Aspire database/connection reference name.</summary>
    public const string DatabaseName = "orders";
    /// <summary>Canonical order identity field.</summary>
    public const string IdField = "id";
    /// <summary>Canonical storage partition field; this sample does not implement tenant authorization.</summary>
    public const string PartitionField = "partition";
    /// <summary>Single local demonstration partition.</summary>
    public const string LocalPartition = "local";

    /// <summary>Semantic field authority; PostgreSQL bindings add only physical details.</summary>
    public static EntityDefinition Entity { get; } = FulfillmentDomain.Orders.Definition;

    /// <summary>Complete physical field mapping; schema lifecycle is explicit in schema.sql.</summary>
    public static PostgresEntityRepositoryMapping Mapping { get; } = PostgresEntityRepositoryMapping.For(FulfillmentDomain.Orders)
        .Table("public", "cohesive_orders")
        .Identity(order => order.Id, "order_id")
        .Partition(order => order.Partition, "partition_key")
        .Column(order => order.Status, "status")
        .Build();

    /// <summary>Binds the existing Aspire-supplied connection to the canonical repository.</summary>
    /// <param name="database">Caller-owned data source; must outlive the repository.</param>
    /// <returns>The shared repository through the provider-neutral storage contract.</returns>
    /// <exception cref="ArgumentNullException">Database is null.</exception>
    /// <exception cref="ArgumentException">Runtime affinity or canonical mapping is invalid.</exception>
    public static IEntityRepository Bind(NpgsqlDataSource database) => new PostgresEntityRepository(Entity,
        new PostgresNpgsqlRuntimeBinding(new($"aspire/resource/{DatabaseName}"), database, "aspire-first/apphost"), Mapping);

    /// <summary>Creates validated order state for an ID-only order registration.</summary>
    /// <param name="id">Order identity, normalized to standard GUID text.</param>
    /// <returns>A complete canonical write; no optimistic-concurrency precondition is asserted.</returns>
    public static EntityWriteRequest Register(Guid id)
    {
        var identity = id.ToString("D");
        return new(Entity.CreateState(identity, new Order(identity, LocalPartition), version: 1).Snapshot);
    }
}
