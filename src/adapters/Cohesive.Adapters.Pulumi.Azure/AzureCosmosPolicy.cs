using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Attributable policy for one single-region SQL account/database selected by an exact Infra plan.</summary>
/// <remarks>Physical account/database identity belongs to the manifest. Container declarations are supplied by the
/// consumer's existing topology authority. This slice supports manual throughput, single-path Hash partitioning,
/// default indexing with optional ordered composite indexes, no TTL or unique keys, and no automatic failover.</remarks>
public sealed record AzureCosmosPolicy
{
    /// <summary>Canonical database resource.</summary>
    public required InfrastructureNodeId Resource { get; init; }
    /// <summary>Canonical contract for repository consumers.</summary>
    public required InfrastructureBindingContractId RepositoryContract { get; init; }
    /// <summary>Expected exclusive Pulumi lifecycle authority.</summary>
    public required InfrastructureLifecycleAuthorityId LifecycleAuthority { get; init; }
    /// <summary>Explicit provider subscription, matched against the host's declared subscription.</summary>
    public required Guid SubscriptionId { get; init; }
    /// <summary>Existing resource group; dependency ordering is supplied to Register separately.</summary>
    public required string ResourceGroupName { get; init; }
    /// <summary>Only configured account region.</summary>
    public required string Location { get; init; }
    /// <summary>Preserved Pulumi account logical name.</summary>
    public required string AccountName { get; init; }
    /// <summary>Preserved Pulumi database logical name.</summary>
    public required string DatabaseName { get; init; }
    /// <summary>Shared database throughput in RU/s, at least 400 and a multiple of 100.</summary>
    public required int DatabaseThroughput { get; init; }
    /// <summary>Explicit consistency: Session, Eventual, ConsistentPrefix or Strong. BoundedStaleness is unsupported.</summary>
    public required string Consistency { get; init; }
    /// <summary>Explicit free-tier decision; availability remains an Azure admission decision.</summary>
    public required bool EnableFreeTier { get; init; }
    /// <summary>Whether key authentication is disabled. The adapter never reads account keys.</summary>
    public required bool DisableLocalAuth { get; init; }
    /// <summary>Explicit public endpoint admission. Private endpoints and IP rules are outside this slice.</summary>
    public required bool PublicNetworkAccess { get; init; }
    /// <summary>Complete container policy; no container names are inferred or added.</summary>
    public required ImmutableArray<AzureCosmosContainerPolicy> Containers { get; init; }
    /// <summary>Exactly one explicit grant scope per participating repository binding; no implicit broadening.</summary>
    public required ImmutableArray<AzureCosmosBindingAccess> Access { get; init; }
    /// <summary>Non-secret Azure account tags.</summary>
    public ImmutableSortedDictionary<string, string> Tags { get; init; } = ImmutableSortedDictionary<string, string>.Empty;
    /// <summary>Non-empty evidence attributing provider, topology, admission and access decisions.</summary>
    public required ImmutableArray<SourceReference> SourceReferences { get; init; }
}

/// <summary>Physical container policy projected from the consumer's authoritative topology.</summary>
public sealed record AzureCosmosContainerPolicy
{
    /// <summary>Exact physical container name, also used for explicit container grant selection.</summary>
    public required string Name { get; init; }
    /// <summary>Existing or explicitly chosen Pulumi logical name.</summary>
    public required string LogicalName { get; init; }
    /// <summary>Single Hash partition path. This bounded slice accepts slash-separated identifier segments.</summary>
    public required string PartitionKeyPath { get; init; }
    /// <summary>Dedicated RU/s, or null to inherit database throughput. Never silently converted to autoscale.</summary>
    public int? Throughput { get; init; }
    /// <summary>Composite index definitions. Path ordering inside each index is semantically significant.</summary>
    public ImmutableArray<ImmutableArray<AzureCosmosCompositePath>> CompositeIndexes { get; init; } = [];
}

/// <summary>One ordered path in a Cosmos composite index.</summary>
/// <param name="Path">Explicit slash-separated identifier path; wildcards are unsupported.</param>
/// <param name="Order">Azure order spelling: ascending or descending.</param>
public sealed record AzureCosmosCompositePath(string Path, string Order);

/// <summary>Supported Cosmos SQL data-plane scope levels, distinct from Azure management-plane RBAC.</summary>
public enum AzureCosmosScope
{
    /// <summary>Missing or default scope; always rejected rather than interpreted as account-wide access.</summary>
    Unspecified,
    /// <summary>Explicit account-wide permission; never inferred from a database binding.</summary>
    Account,
    /// <summary>Only the selected database.</summary>
    Database,
    /// <summary>Only one declared container.</summary>
    Container
}

/// <summary>Explicit access decision for one canonical participating repository binding.</summary>
/// <param name="Binding">Exact canonical binding identity.</param>
/// <param name="Scope">Selected permission scope; account-wide migration policy must be explicit.</param>
/// <param name="ContainerName">Declared physical container name only for Container scope; otherwise null.</param>
public sealed record AzureCosmosBindingAccess(InfrastructureBindingId Binding, AzureCosmosScope Scope, string? ContainerName = null);
