using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi.AzureNative.ApplicationInsights;
using Pulumi.AzureNative.OperationalInsights;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Associates native telemetry resources with canonical identities and consumers; registers no resources or instrumentation.</summary>
public static class AzureTelemetryBinding
{
    /// <summary>Exact supported interpretation target.</summary>
    public const string Target = "pulumi-azure-native/3.16.0";
    /// <summary>Canonical Application Insights facility.</summary>
    public const string ComponentFacility = "azure/application-insights";
    /// <summary>Canonical Log Analytics facility.</summary>
    public const string WorkspaceFacility = "azure/log-analytics";

    /// <summary>Checks exact plan, exclusive shared ownership, identities and supported consumer bindings.</summary>
    /// <param name="deployment">Canonical compiled deployment and topology authority.</param>
    /// <param name="policy">Explicit canonical association, containing no connection credentials.</param>
    /// <param name="subscriptionId">Host's declared Azure provider subscription.</param>
    /// <returns>Ordered attributable diagnostics; an empty result does not prove runtime telemetry delivery.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    public static ImmutableArray<DocumentValidationDiagnostic> Validate(InfrastructureTargetDeploymentPlan deployment,
        AzureTelemetryPolicy policy, Guid subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(policy);
        var errors = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        void Error(string code, string message) => errors.Add(new("azure.telemetry." + code,
            DiagnosticSeverity.Error, message, SchemaLocation: policy.Component.Value,
            Evidence: new(stage: "pulumi-azure-binding", subject: policy.Component.Value ?? "unset-component",
                sourceReferences: [deployment.Manifest.Fingerprint.Value,
                    .. policy.SourceReferences.IsDefault ? [] : policy.SourceReferences.Select(s => s.Value)])));
        AzureConstructionPolicy.ValidateDeployment(deployment, Target, policy.SubscriptionId, subscriptionId,
            policy.SourceReferences, errors, Error);
        CheckResource(policy.Component, ComponentFacility, "components");
        CheckResource(policy.Workspace, WorkspaceFacility, "workspaces");
        if (!deployment.FacilityPlan.Definition.Definition.ReadinessDependencies.Any(d =>
            d.Subject == policy.Component && d.Dependency == policy.Workspace))
            Error("workspace-dependency", "Declare the component's readiness dependency on its supporting workspace in the canonical definition.");
        if (policy.Component == policy.Workspace) Error("identity", "Component and workspace require distinct canonical identities.");
        if (string.IsNullOrWhiteSpace(policy.ExportContract.Value)) Error("export-contract", "Select the canonical telemetry-export contract explicitly.");
        foreach (var binding in AzureConstructionPolicy.Bindings(deployment, policy.Component))
        {
            if (binding.Target != policy.Component || binding.Contract != policy.ExportContract)
                Error("binding", $"Binding '{binding.Id.Value}' has an unsupported direction or contract.");
            else if (!deployment.Manifest.Workloads.Any(w => w.Workload == binding.Source) &&
                deployment.Realization?.FindNonParticipation(binding.Source) is null)
                Error("consumer", $"Binding '{binding.Id.Value}' must originate at a deployed or explicitly non-participating workload.");
        }
        if (AzureConstructionPolicy.Bindings(deployment, policy.Workspace).Any())
            Error("workspace-binding", "Workspace consumer bindings are not supported by this telemetry association.");
        return DocumentValidationDiagnostics.Normalize(errors.ToImmutable());

        void CheckResource(InfrastructureNodeId id, string facility, string kind)
        {
            var resource = AzureConstructionPolicy.SelectManagedResource(deployment, id, facility,
                policy.LifecycleAuthority, Target, Error);
            if (resource is null) return;
            if (!PhysicalName(resource.PhysicalResource.Value, facility, kind).Success)
                Error("physical-identity", $"Select a physical identity '{facility}/{kind}/<name>' with a non-empty Azure resource name.");
            if (deployment.Manifest.Resources.Any(r => r.Resource != id &&
                string.Equals(r.PhysicalResource.Value, resource.PhysicalResource.Value, StringComparison.OrdinalIgnoreCase)))
                Error("alias", "Multiple canonical resources claim this telemetry resource; select one exclusive owner.");
        }
    }

    /// <summary>Returns canonical physical names after semantic validation, before native registration.</summary>
    /// <param name="deployment">Exact compiled deployment.</param>
    /// <param name="policy">Canonical component/workspace association.</param>
    /// <param name="subscriptionId">Host's declared subscription.</param>
    /// <returns>Names for native ComponentArgs.ResourceName and WorkspaceArgs.WorkspaceName.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    /// <exception cref="AzureTelemetryValidationException">Semantic association is invalid.</exception>
    public static (string Component, string Workspace) Names(InfrastructureTargetDeploymentPlan deployment,
        AzureTelemetryPolicy policy, Guid subscriptionId)
    {
        var diagnostics = Validate(deployment, policy, subscriptionId);
        if (!diagnostics.IsEmpty) throw new AzureTelemetryValidationException(diagnostics);
        return (Name(policy.Component, ComponentFacility, "components"), Name(policy.Workspace, WorkspaceFacility, "workspaces"));
        string Name(InfrastructureNodeId id, string facility, string kind) => PhysicalName(
            deployment.Manifest.Resources.Single(r => r.Resource == id).PhysicalResource.Value, facility, kind).Groups[1].Value;
    }

    /// <summary>Attaches caller-created native resources without registering resources or configuring instrumentation.</summary>
    /// <param name="deployment">Exact canonical deployment checked by the host handoff.</param>
    /// <param name="policy">Canonical association and provenance.</param>
    /// <param name="subscriptionId">Host's declared provider subscription.</param>
    /// <param name="workspace">Native Log Analytics workspace; caller owns all arguments and options.</param>
    /// <param name="component">Native Application Insights component linked to the workspace's actual ID.</param>
    /// <param name="cancellationToken">Checked before association; native lifecycle cancellation remains Pulumi-owned.</param>
    /// <returns>Identity-checked outputs and canonical participating export bindings.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="AzureTelemetryValidationException">Semantic association is invalid; resources may already be registered.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before association.</exception>
    public static AzureTelemetryResources Attach(InfrastructureTargetDeploymentPlan deployment, AzureTelemetryPolicy policy,
        Guid subscriptionId, Workspace workspace, Component component, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(component);
        var names = Names(deployment, policy, subscriptionId);
        return new(deployment, policy, workspace, component, names,
            [.. AzureConstructionPolicy.Bindings(deployment, policy.Component)
                .Where(b => deployment.Manifest.Workloads.Any(w => w.Workload == b.Source))
                .OrderBy(b => b.Id.Value, StringComparer.Ordinal)]);
    }

    // Syntax only: provider-specific name limits remain the native SDK/provider's responsibility.
    static Match PhysicalName(string value, string facility, string kind) => Regex.Match(value,
        "\\A" + Regex.Escape(facility + "/" + kind + "/") + @"([^/\s]+)\z");
}

/// <summary>Structured rejection of a canonical telemetry association.</summary>
public sealed class AzureTelemetryValidationException : ArgumentException
{
    internal AzureTelemetryValidationException(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Diagnostics = diagnostics;
    /// <summary>Ordered attributable diagnostics without provider credentials.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}
