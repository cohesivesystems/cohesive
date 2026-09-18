using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Explicit native site activation; disabled sites retain configuration but expose no active binding endpoints.</summary>
public enum AzureAppServiceActivation
{
    /// <summary>No activation decision; rejected.</summary>
    Unspecified = 0,
    /// <summary>The native site is enabled and may supply declared endpoints.</summary>
    Enabled = 1,
    /// <summary>The native site is retained disabled; no active endpoints are projected.</summary>
    Disabled = 2
}

/// <summary>One workload's native hosting decision; contains no SDK options or setting values.</summary>
public sealed record AzureAppServicePlacement
{
    /// <summary>Participating canonical workload.</summary>
    public required InfrastructureNodeId Workload { get; init; }
    /// <summary>Canonical managed App Service plan, shared by reference where appropriate.</summary>
    public required InfrastructureNodeId Plan { get; init; }
    /// <summary>Explicit activation matching the native Enabled property.</summary>
    public required AzureAppServiceActivation Activation { get; init; }
    /// <summary>Setting identities that must always be classified secret; values remain native Pulumi inputs.</summary>
    public ImmutableArray<InfrastructureSettingId> SecretSettings { get; init; } = [];
    /// <summary>Attribution from native setting identities to incident canonical bindings; product/platform settings need no invented binding.</summary>
    public ImmutableArray<KeyValuePair<InfrastructureSettingId, InfrastructureBindingId>> BindingSettings { get; init; } = [];
    /// <summary>Sources explaining placement, activation and setting attribution.</summary>
    public required ImmutableArray<SourceReference> SourceReferences { get; init; }
}

/// <summary>Complete App Service placement policy for one exact Azure program and resource-group scope.</summary>
public sealed record AzureAppServicePolicy
{
    /// <summary>Expected exclusive Pulumi project/stack owner, formatted pulumi/project/stack.</summary>
    public required InfrastructureLifecycleAuthorityId LifecycleAuthority { get; init; }
    /// <summary>Explicit subscription used by the native provider.</summary>
    public required Guid SubscriptionId { get; init; }
    /// <summary>Exact resource group containing these native plans and sites.</summary>
    public required string ResourceGroupName { get; init; }
    /// <summary>One decision for every participating App Service workload; excludes non-participating workloads.</summary>
    public required ImmutableArray<AzureAppServicePlacement> Placements { get; init; }
    /// <summary>Canonical directed workload-to-plan contract; hosting does not imply runtime health readiness.</summary>
    public required InfrastructureBindingContractId HostingContract { get; init; }
    /// <summary>Canonical contracts explicitly permitted to consume HTTPS site endpoints.</summary>
    public required ImmutableArray<InfrastructureBindingContractId> EndpointContracts { get; init; }
    /// <summary>Host-selected positive setting-name character budget, checked before native registration; not a universal Azure limit.</summary>
    public required int MaximumSettingNameLength { get; init; }
    /// <summary>Sources attributing provider scope and policy.</summary>
    public required ImmutableArray<SourceReference> SourceReferences { get; init; }
}
