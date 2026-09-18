using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Canonical workspace and its existing supporting resources; native SDK arguments own provider detail.</summary>
public sealed record AzureMachineLearningWorkspacePolicy
{
    /// <summary>Managed canonical ML workspace.</summary>
    public required InfrastructureNodeId Workspace { get; init; }
    /// <summary>Canonical Blob container whose owning account backs this workspace.</summary>
    public required InfrastructureNodeId Storage { get; init; }
    /// <summary>Canonical managed Key Vault.</summary>
    public required InfrastructureNodeId Vault { get; init; }
    /// <summary>Canonical managed Application Insights component.</summary>
    public required InfrastructureNodeId Telemetry { get; init; }
    /// <summary>Exclusive pulumi/project/stack authority shared by this workspace and its dependencies.</summary>
    public required InfrastructureLifecycleAuthorityId LifecycleAuthority { get; init; }
    /// <summary>Explicit provider subscription.</summary>
    public required Guid SubscriptionId { get; init; }
    /// <summary>Resource group containing the workspace and supporting resources.</summary>
    public required string ResourceGroupName { get; init; }
    /// <summary>Attribution for the association and host's native configuration.</summary>
    public required ImmutableArray<SourceReference> SourceReferences { get; init; }
}

/// <summary>Explicit registry lifecycle selection; no mode implies resource construction.</summary>
public enum AzureMachineLearningRegistryMode
{
    /// <summary>No decision; rejected.</summary>
    Unspecified,
    /// <summary>No registry selected or constructed.</summary>
    Disabled,
    /// <summary>Registry is owned by the current Pulumi program.</summary>
    Managed,
    /// <summary>Registry availability is read from a separately owned stack.</summary>
    Referenced
}

/// <summary>Registry lifecycle and shared-output contract, independent of workspace ownership.</summary>
public sealed record AzureMachineLearningRegistryPolicy
{
    /// <summary>Explicit disabled, managed or referenced decision.</summary>
    public required AzureMachineLearningRegistryMode Mode { get; init; }
    /// <summary>Canonical registry for managed/referenced modes; absent when disabled.</summary>
    public InfrastructureNodeId? Registry { get; init; }
    /// <summary>Expected registry owner, formatted pulumi/project/stack; no owner when disabled.</summary>
    public InfrastructureLifecycleAuthorityId? LifecycleAuthority { get; init; }
    /// <summary>Explicit subscription used by the registry's provider.</summary>
    public required Guid SubscriptionId { get; init; }
    /// <summary>Registry's resource group, required only for managed/referenced modes.</summary>
    public string? ResourceGroupName { get; init; }
    /// <summary>Exact organization/project/stack source; referenced mode only.</summary>
    public string? ReferenceStack { get; init; }
    /// <summary>Native shared-stack output key containing an explicit Boolean; referenced mode only.</summary>
    public string? EnabledOutput { get; init; }
    /// <summary>Native shared-stack output key containing a name or empty string; referenced mode only.</summary>
    public string? NameOutput { get; init; }
    /// <summary>Sources attributing lifecycle and output selection.</summary>
    public required ImmutableArray<SourceReference> SourceReferences { get; init; }
}
