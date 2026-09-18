using Cohesive.Infra.Realization;
using Pulumi;
using ML = Pulumi.AzureNative.MachineLearningServices;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Original native workspace plus dependency- and identity-checked outputs.</summary>
public sealed class AzureMachineLearningWorkspaceResources
{
    internal AzureMachineLearningWorkspaceResources(InfrastructureTargetDeploymentPlan deployment, AzureMachineLearningWorkspacePolicy policy, ML.Workspace workspace, Output<string> id)
    { Deployment = deployment; Policy = policy; Workspace = workspace; WorkspaceId = id; WorkspaceName = Output.Tuple(id, workspace.Name).Apply(v => v.Item2); }
    /// <summary>Exact canonical topology and provenance.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }
    /// <summary>Canonical ownership and supporting-resource association.</summary>
    public AzureMachineLearningWorkspacePolicy Policy { get; }
    /// <summary>Caller-owned native workspace; raw SDK outputs bypass these checks.</summary>
    public ML.Workspace Workspace { get; }
    /// <summary>Actual ID after native identity, ownership, dependency and auth checks; unknown remains unknown.</summary>
    public Output<string> WorkspaceId { get; }
    /// <summary>Actual name after the same checks, preserving dependencies and secret classification.</summary>
    public Output<string> WorkspaceName { get; }
}

/// <summary>Explicit registry selection; reference availability never implies local construction or live access.</summary>
public sealed class AzureMachineLearningRegistryResources
{
    internal AzureMachineLearningRegistryResources(AzureMachineLearningRegistryPolicy policy, ML.Registry? registry, StackReference? reference,
        Output<string>? id, Output<bool> enabled, Output<string> name)
    { Policy = policy; Registry = registry; Reference = reference; RegistryId = id; Enabled = enabled; Name = name; }
    /// <summary>Original lifecycle and output contract.</summary>
    public AzureMachineLearningRegistryPolicy Policy { get; }
    /// <summary>Caller-created registry for managed selection only; raw outputs bypass validation.</summary>
    public ML.Registry? Registry { get; }
    /// <summary>Original separately owned native stack reference; referenced selection only.</summary>
    public StackReference? Reference { get; }
    /// <summary>Checked actual ID only for managed selection; no ARM ID is fabricated for disabled or referenced modes.</summary>
    public Output<string>? RegistryId { get; }
    /// <summary>Validated configured availability, not runtime health or authorization.</summary>
    public Output<bool> Enabled { get; }
    /// <summary>Checked registry name or empty string when disabled; retains unknown/secret native semantics.</summary>
    public Output<string> Name { get; }
}
