using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Canonical association of a native Application Insights component and its supporting workspace.</summary>
/// <remarks>Both physical identities are declared in the deployment. Retention, networking and resource options remain native SDK configuration.</remarks>
public sealed record AzureTelemetryPolicy
{
    /// <summary>Canonical telemetry ingestion resource deployed as Application Insights.</summary>
    public required InfrastructureNodeId Component { get; init; }
    /// <summary>Canonical supporting telemetry store deployed as Log Analytics.</summary>
    public required InfrastructureNodeId Workspace { get; init; }
    /// <summary>Canonical contract identifying telemetry-exporting consumers.</summary>
    public required InfrastructureBindingContractId ExportContract { get; init; }
    /// <summary>Expected shared exclusive Pulumi state authority for both resources.</summary>
    public required InfrastructureLifecycleAuthorityId LifecycleAuthority { get; init; }
    /// <summary>Explicit Azure subscription checked against the host and resolved resources.</summary>
    public required Guid SubscriptionId { get; init; }
    /// <summary>Non-empty references attributing the association and native configuration policy.</summary>
    public required ImmutableArray<SourceReference> SourceReferences { get; init; }
}
