using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Explicit environment policy for the Consumption Durable Task construction slice.</summary>
/// <remarks>
/// This policy supplies provider configuration, not topology. Scheduler and hub names come from the exact
/// deployment manifest. Network rules must be explicitly supplied and attributed. Empty rules deny public access.
/// Pulumi logical names must match existing state during migration. No component parent is added implicitly.
/// </remarks>
public sealed record AzureDurableTaskPolicy
{
    /// <summary>Canonical resource to construct.</summary>
    public required InfrastructureNodeId Resource { get; init; }
    /// <summary>Canonical binding contract identifying workers granted task-hub data access.</summary>
    public required InfrastructureBindingContractId WorkerContract { get; init; }
    /// <summary>Exact expected Pulumi state authority.</summary>
    public required InfrastructureLifecycleAuthorityId LifecycleAuthority { get; init; }
    /// <summary>Subscription assigned explicitly to the generated Azure provider.</summary>
    public required Guid SubscriptionId { get; init; }
    /// <summary>Existing Azure resource group containing the scheduler.</summary>
    public required string ResourceGroupName { get; init; }
    /// <summary>Explicit Azure region; no ambient configuration is consulted.</summary>
    public required string Location { get; init; }
    /// <summary>Existing or new Pulumi provider logical name.</summary>
    public required string ProviderName { get; init; }
    /// <summary>Existing or new Pulumi scheduler logical name.</summary>
    public required string SchedulerName { get; init; }
    /// <summary>Existing or new Pulumi task-hub logical name.</summary>
    public required string TaskHubName { get; init; }
    /// <summary>Explicit IPv4 addresses or CIDR network rules. A default array is rejected.</summary>
    public required ImmutableArray<string> IpAllowlist { get; init; }
    /// <summary>Existing scheduler tags; empty explicitly declares no tags. Keys and values are non-secret policy.</summary>
    public ImmutableSortedDictionary<string, string> Tags { get; init; } = ImmutableSortedDictionary<string, string>.Empty;
    /// <summary>Non-empty references attributing scope, naming, and network policy.</summary>
    public required ImmutableArray<SourceReference> SourceReferences { get; init; }
}
