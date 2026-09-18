using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Pulumi;
using Pulumi.AzureNative.ApplicationInsights;
using Pulumi.AzureNative.OperationalInsights;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Native telemetry association with validated identities and secret-classified consumer projections.</summary>
public sealed class AzureTelemetryResources
{
    internal AzureTelemetryResources(InfrastructureTargetDeploymentPlan deployment, AzureTelemetryPolicy policy,
        Workspace workspace, Component component, (string Component, string Workspace) names,
        ImmutableArray<InfrastructureBindingDefinition> bindings)
    {
        Deployment = deployment; Policy = policy; Workspace = workspace; Component = component; ExportBindings = bindings;
        var workspaceIdentity = Output.Tuple(workspace.Name, workspace.Id).Apply(value =>
            ValidIdentity(value.Item1, value.Item2, names.Workspace, "Microsoft.OperationalInsights", "workspaces") ? value.Item2 :
                throw new InvalidOperationException("The attached workspace's resolved identity does not match its canonical association."));
        var componentIdentity = Output.Tuple(component.Name, component.Id, component.WorkspaceResourceId, workspaceIdentity).Apply(value =>
        {
            if (!ValidIdentity(value.Item1, value.Item2, names.Component, "Microsoft.Insights", "components") ||
                !string.Equals(value.Item3, value.Item4, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The attached component's resolved identity or workspace linkage does not match its canonical association.");
            return value.Item2;
        });
        ComponentId = componentIdentity;
        WorkspaceId = Output.Tuple(workspaceIdentity, componentIdentity).Apply(value => value.Item1);
        connectionString = Output.CreateSecret(Output.Tuple(componentIdentity, component.ConnectionString).Apply(value =>
            !string.IsNullOrWhiteSpace(value.Item2) ? value.Item2 :
                throw new InvalidOperationException("The telemetry provider returned no connection string.")));

        bool ValidIdentity(string name, string id, string expected, string provider, string kind)
        {
            var parts = id.Split('/');
            return string.Equals(name, expected, StringComparison.OrdinalIgnoreCase) && parts.Length == 9 && parts[0].Length == 0 &&
                string.Equals(parts[1], "subscriptions", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(parts[2], out var subscription) && subscription == policy.SubscriptionId &&
                string.Equals(parts[3], "resourceGroups", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parts[4]) &&
                string.Equals(parts[5], "providers", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[6], provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[7], kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[8], expected, StringComparison.OrdinalIgnoreCase);
        }
    }
    readonly Output<string> connectionString;
    /// <summary>Original canonical deployment and provenance.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }
    /// <summary>Immutable canonical association and attributed native policy references.</summary>
    public AzureTelemetryPolicy Policy { get; }
    /// <summary>Original native workspace; raw SDK outputs bypass association validation.</summary>
    public Workspace Workspace { get; }
    /// <summary>Original native component; raw SDK outputs bypass association validation.</summary>
    public Component Component { get; }
    /// <summary>Actual workspace ID after both identities and their link resolve successfully; unknown previews remain unknown.</summary>
    public Output<string> WorkspaceId { get; }
    /// <summary>Actual component ID after both identities and their link resolve successfully; mismatches fault with InvalidOperationException.</summary>
    public Output<string> ComponentId { get; }
    /// <summary>Participating canonical telemetry-export bindings in ordinal ID order.</summary>
    public ImmutableArray<InfrastructureBindingDefinition> ExportBindings { get; }

    /// <summary>Returns the actual provider connection string only for a participating canonical export binding.</summary>
    /// <param name="binding">Canonical binding whose source workload participates in this deployment.</param>
    /// <returns>Always secret-classified output retaining native dependencies; does not install instrumentation or prove delivery.</returns>
    /// <exception cref="ArgumentException">Binding is absent, unsupported or explicitly non-participating.</exception>
    /// <remarks>Resolved identity/linkage mismatches or an empty connection string fault with InvalidOperationException without exposing values.</remarks>
    public Output<string> ConnectionString(InfrastructureBindingId binding) => ExportBindings.Any(b => b.Id == binding)
        ? connectionString : throw new ArgumentException("Select a participating canonical telemetry-export binding.", nameof(binding));
}
