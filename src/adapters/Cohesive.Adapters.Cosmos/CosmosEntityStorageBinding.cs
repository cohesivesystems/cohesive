using Cohesive.Model;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Relations.Acquisition;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>One canonical observation-container declaration for repository access and bounded query acquisition.</summary>
/// <remarks>Uses the adapter's standard observation envelope and a fixed string partition. Native container
/// partition metadata must be verified by the host. No client or IO is needed for construction.</remarks>
public sealed class CosmosEntityStorageBinding : IEntityStorageBinding
{
    readonly EntityDefinition entity;
    readonly string identityField;
    readonly EntityPartitionKeyPolicy partition;

    /// <summary>Declares fixed-partition entity storage, deriving repository and query policies together.</summary>
    /// <param name="entity">Exact canonical entity.</param><param name="identityField">Canonical field containing its observation identity.</param>
    /// <param name="account">Logical account supplying the native connection.</param><param name="database">Native database name.</param>
    /// <param name="container">Native container name.</param><param name="logicalPartition">Shared logical scope for composed queries.</param>
    /// <param name="partitionKey">Trusted fixed string partition used by reads and writes.</param>
    /// <param name="partitionField">Canonical observation field which writes must match.</param>
    /// <param name="maximumEnumerationRows">Maximum observations in a bounded enumeration.</param>
    /// <param name="maximumKeysPerQuery">Maximum keys in one native lookup.</param>
    /// <param name="maximumQueryChunks">Maximum native queries per lookup.</param>
    /// <param name="maximumSdkPageSize">Requested SDK page size.</param>
    /// <exception cref="ArgumentNullException">Entity or logical partition is null.</exception>
    /// <exception cref="ArgumentException">A name, canonical field or partition is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An acquisition limit is invalid.</exception>
    public CosmosEntityStorageBinding(EntityDefinition entity, string identityField,
        string account, string database, string container, RelationQueryLogicalPartitionIdentity logicalPartition,
        string partitionKey, string partitionField, int maximumEnumerationRows = 10_000,
        int maximumKeysPerQuery = 100, int maximumQueryChunks = 16, int maximumSdkPageSize = 256)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityField);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionField);
        if (!entity.Fields.Any(field => field.Name.Value == identityField)
            || !entity.Fields.Any(field => field.Name.Value == partitionField))
            throw new ArgumentException("Identity and partition must name canonical entity fields.");
        this.entity = entity;
        this.identityField = identityField;
        partition = EntityPartitionKeyPolicy.Fixed(partitionKey, partitionField);
        Query = new(entity.StateShape.QualifiedId, account, database, container,
            new("partitionKey", logicalPartition, fixedPartitionKey: new PartitionKey(partitionKey),
                maximumEnumerationRows: maximumEnumerationRows, maximumKeysPerQuery: maximumKeysPerQuery,
                maximumQueryChunks: maximumQueryChunks, maximumSdkPageSize: maximumSdkPageSize),
            FieldPath.FromField(identityField));
    }

    /// <summary>Offline acquisition metadata used for physical planning and infrastructure evidence.</summary>
    public CosmosEntityQueryBinding Query { get; }

    /// <inheritdoc />
    public EntityRelationQuerySourceBinding Source => Query.Binding;

    /// <inheritdoc />
    public async ValueTask<EntityStorageRegistration> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var client = services.GetRequiredKeyedService<CosmosClient>(Query.Account);
        var container = client.GetContainer(Query.Database, Query.Container);
        var metadata = await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (metadata.Resource.PartitionKeyPath != "/partitionKey")
            throw new InvalidOperationException("The observation envelope requires container partition path /partitionKey.");
        return Bind(container);
    }

    /// <summary>Binds repository and query acquisition to the same caller-owned native container.</summary>
    /// <param name="container">Container whose database, name and partition metadata were verified by the host.</param>
    /// <returns>One repository/source registration; neither it nor this declaration owns the client.</returns>
    /// <exception cref="ArgumentNullException">Container is null.</exception>
    /// <exception cref="ArgumentException">Container address differs from this declaration.</exception>
    public EntityStorageRegistration Bind(Container container) => new(
        new CosmosEntityOutboxRepository(entity, container, partitionKeyPolicy: partition, identityField: identityField),
        Query.Bind(container));
}
