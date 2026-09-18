using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Checks native App Service hosting against canonical workload placement, plan ownership and bindings.</summary>
public static class AzureAppServiceBinding
{
    /// <summary>Supported exact Azure program target.</summary>
    public const string Target = "pulumi-azure-native/3.16.0";
    /// <summary>Workload hosting facility.</summary>
    public const string SiteFacility = "azure/app-service";
    /// <summary>Managed hosting-plan facility.</summary>
    public const string PlanFacility = "azure/app-service-plan";

    /// <summary>Validates complete hosting decisions before native construction; registers nothing.</summary>
    /// <param name="deployment">Exact compiled deployment.</param>
    /// <param name="policy">Non-secret hosting decisions.</param>
    /// <param name="subscriptionId">Native provider's explicit subscription.</param>
    /// <returns>Normalized attributable errors; no diagnostics means semantic association is valid, not runtime readiness.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    public static ImmutableArray<DocumentValidationDiagnostic> Validate(InfrastructureTargetDeploymentPlan deployment,
        AzureAppServicePolicy policy, Guid subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(deployment); ArgumentNullException.ThrowIfNull(policy);
        var errors = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        InfrastructureNodeId? selectedWorkload = null;
        ImmutableArray<SourceReference> placementSources = [];
        void Error(string code, string message) => errors.Add(new("azure.app-service." + code, DiagnosticSeverity.Error, message,
            SchemaLocation: selectedWorkload?.Value ?? policy.LifecycleAuthority.Value,
            Evidence: new(stage: "pulumi-azure-binding", subject: selectedWorkload?.Value ?? "app-service",
                sourceReferences: [.. (policy.SourceReferences.IsDefault ? [] : policy.SourceReferences).Concat(placementSources.IsDefault ? [] : placementSources)
                    .Select(s => s.Value).Append(deployment.Manifest.Fingerprint.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal)])));
        AzureConstructionPolicy.ValidateDeployment(deployment, Target, policy.SubscriptionId, subscriptionId, policy.SourceReferences, errors, Error);
        var authority = policy.LifecycleAuthority.Value?.Split('/') ?? [];
        if (authority.Length != 3 || authority[0] != "pulumi" || authority.Any(string.IsNullOrWhiteSpace))
            Error("authority", "Select one exact pulumi/project/stack owner.");
        if (!AzureConstructionPolicy.ValidResourceGroup(policy.ResourceGroupName)) Error("resource-group", "Supply a valid explicit resource group.");
        if (policy.MaximumSettingNameLength <= 0) Error("settings-budget", "Supply a positive setting-name budget.");
        var definition = deployment.FacilityPlan.Definition.Definition;
        var contracts = policy.EndpointContracts.IsDefault ? [] : policy.EndpointContracts;
        if (policy.EndpointContracts.IsDefault || contracts.Any(c => string.IsNullOrWhiteSpace(c.Value)) || contracts.Distinct().Count() != contracts.Length)
            Error("endpoint-contracts", "Supply explicit distinct endpoint contracts, including an empty set when none are authorized.");
        var workloads = deployment.Manifest.Workloads.Where(w => w.Facility.Value == SiteFacility).ToDictionary(w => w.Workload);
        var seen = new HashSet<InfrastructureNodeId>();
        var selectedPlans = new HashSet<InfrastructureNodeId>();
        if (policy.Placements.IsDefault) Error("placements", "Supply a decision for every participating App Service workload.");
        else foreach (var placement in policy.Placements)
        {
            selectedWorkload = placement?.Workload;
            placementSources = placement?.SourceReferences ?? [];
            if (placement is null || !seen.Add(placement.Workload) || !workloads.TryGetValue(placement.Workload, out var workload))
            { Error("placements", "Select each participating App Service workload exactly once."); continue; }
            if (placement.Activation is not (AzureAppServiceActivation.Enabled or AzureAppServiceActivation.Disabled))
                Error("activation", "Explicitly enable or retain disabled each native site.");
            if (placement.SourceReferences.IsDefaultOrEmpty || placement.SourceReferences.Any(s => string.IsNullOrWhiteSpace(s.Value)))
                Error("placement-provenance", "Attribute each placement and activation decision.");
            if (PhysicalName(workload.PhysicalResource.Value, "azure/app-service/sites/") is null)
                Error("site-identity", "Select azure/app-service/sites/<site-name>.");
            if (deployment.Manifest.Workloads.Any(w => w.Workload != workload.Workload &&
                string.Equals(w.PhysicalResource.Value, workload.PhysicalResource.Value, StringComparison.OrdinalIgnoreCase)))
                Error("alias", "Multiple workloads claim the same native site.");
            if (!definition.ReadinessDependencies.Any(d => d.Subject == placement.Workload && d.Dependency == placement.Plan))
                Error("plan-dependency", "Declare the workload's dependency on its hosting plan canonically.");
            if (selectedPlans.Add(placement.Plan))
            {
                var plan = AzureConstructionPolicy.SelectManagedResource(deployment, placement.Plan, PlanFacility,
                    policy.LifecycleAuthority, Target, Error);
                if (plan is not null)
                {
                    if (PhysicalName(plan.PhysicalResource.Value, "azure/app-service/plans/") is null)
                        Error("plan-identity", "Select azure/app-service/plans/<plan-name>.");
                    if (deployment.Manifest.Resources.Any(r => r.Resource != plan.Resource &&
                        string.Equals(r.PhysicalResource.Value, plan.PhysicalResource.Value, StringComparison.OrdinalIgnoreCase)))
                        Error("alias", "Multiple resources claim the same native hosting plan.");
                }
            }
            if (placement.SecretSettings.IsDefault || placement.SecretSettings.Any(s => !ValidSettingName(s.Value, policy.MaximumSettingNameLength)) ||
                placement.SecretSettings.Select(s => s.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != placement.SecretSettings.Length)
                Error("secret-settings", "Select distinct valid secret setting identities.");
            if (placement.BindingSettings.IsDefault || placement.BindingSettings.Select(s => s.Key.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != placement.BindingSettings.Length) Error("binding-settings", "Supply explicit setting attribution, including an empty mapping when appropriate.");
            else foreach (var setting in placement.BindingSettings)
            {
                if (!ValidSettingName(setting.Key.Value, policy.MaximumSettingNameLength) || !definition.Bindings.Any(b => b.Id == setting.Value &&
                    (b.Source == placement.Workload || b.Target == placement.Workload) &&
                    deployment.Realization?.FindNonParticipation(b.Source) is null && deployment.Realization?.FindNonParticipation(b.Target) is null))
                    Error("binding-settings", "Attribute setting identities only to incident participating canonical bindings.");
            }
        }
        selectedWorkload = null; placementSources = [];
        if (!seen.SetEquals(workloads.Keys)) Error("placements", "Cover every participating App Service workload without inventing placements.");
        return DocumentValidationDiagnostics.Normalize(errors.ToImmutable());
    }

    /// <summary>Returns canonical native names after pure validation.</summary>
    /// <param name="deployment">Exact deployment.</param>
    /// <param name="policy">Complete hosting policy.</param>
    /// <param name="workload">Participating workload to select.</param>
    /// <param name="subscriptionId">Explicit host subscription.</param>
    /// <returns>Site and plan physical names; Pulumi logical names and parents stay caller-owned.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    /// <exception cref="AzureAppServiceValidationException">Canonical association is invalid.</exception>
    /// <exception cref="ArgumentException">Workload has no placement.</exception>
    public static (string Site, string Plan) Names(InfrastructureTargetDeploymentPlan deployment, AzureAppServicePolicy policy,
        InfrastructureNodeId workload, Guid subscriptionId)
    {
        RequireValid(deployment, policy, subscriptionId);
        var placement = Placement(policy, workload);
        return (PhysicalName(deployment.Manifest.Workloads.Single(w => w.Workload == workload).PhysicalResource.Value, "azure/app-service/sites/")!,
            PhysicalName(deployment.Manifest.FindResource(placement.Plan).PhysicalResource.Value, "azure/app-service/plans/")!);
    }

    /// <summary>Projects native setting values once, with canonical key/binding attribution and explicit secret classification.</summary>
    /// <param name="deployment">Exact deployment.</param>
    /// <param name="policy">Hosting policy and setting-name budget.</param>
    /// <param name="workload">Site receiving the settings, including deliberately retained disabled sites.</param>
    /// <param name="values">Runtime native values keyed by existing Infra setting identities; never persisted in policy.</param>
    /// <returns>Deterministically ordered native inputs, preserving unknowns, dependencies and existing secrets.</returns>
    /// <exception cref="ArgumentNullException">An input collection or value is null.</exception>
    /// <exception cref="ArgumentException">Setting names exceed budget, collide, or omit declared secret/binding keys.</exception>
    /// <exception cref="AzureAppServiceValidationException">Canonical hosting policy is invalid.</exception>
    /// <remarks>Call before constructing the native site. This checks names, not unresolved value sizes or live provider acceptance.</remarks>
    public static InputList<NameValuePairArgs> Settings(InfrastructureTargetDeploymentPlan deployment, AzureAppServicePolicy policy,
        InfrastructureNodeId workload, IReadOnlyDictionary<InfrastructureSettingId, Input<string>> values)
    {
        ArgumentNullException.ThrowIfNull(values); ArgumentNullException.ThrowIfNull(policy); RequireValid(deployment, policy, policy.SubscriptionId);
        var placement = Placement(policy, workload);
        if (values.Keys.Any(k => !ValidSettingName(k.Value, policy.MaximumSettingNameLength)) ||
            values.Keys.Select(k => k.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count ||
            placement.SecretSettings.Any(k => !values.ContainsKey(k)) || placement.BindingSettings.Any(k => !values.ContainsKey(k.Key)))
            throw new ArgumentException("Provide distinct valid setting names within budget and every attributed/secret key.", nameof(values));
        var result = new InputList<NameValuePairArgs>();
        foreach (var (key, value) in values.OrderBy(v => v.Key.Value, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(value);
            result.Add(new NameValuePairArgs { Name = key.Value,
                Value = placement.SecretSettings.Contains(key) ? Output.CreateSecret((Output<string>)value) : value });
        }
        return result;
    }

    /// <summary>Attaches existing native plan/site resources; constructs no plans, sites, grants or parents.</summary>
    /// <param name="deployment">Exact deployment.</param>
    /// <param name="policy">Complete hosting decisions.</param>
    /// <param name="workload">Canonical workload represented by the site.</param>
    /// <param name="subscriptionId">Explicit host subscription.</param>
    /// <param name="plan">Native plan, reused by reference for shared placements.</param>
    /// <param name="site">Native site; caller retains all SDK arguments/options.</param>
    /// <param name="cancellationToken">Checked before attachment; Pulumi owns lifecycle cancellation afterward.</param>
    /// <returns>Checked identity, endpoint and explicit live identity observation boundary.</returns>
    /// <exception cref="ArgumentNullException">A required input is null.</exception>
    /// <exception cref="ArgumentException">Workload has no placement.</exception>
    /// <exception cref="AzureAppServiceValidationException">Canonical association is invalid; native registration may already have happened.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static AzureAppServiceResources Attach(InfrastructureTargetDeploymentPlan deployment, AzureAppServicePolicy policy,
        InfrastructureNodeId workload, Guid subscriptionId, AppServicePlan plan, WebApp site, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(site);
        var names = Names(deployment, policy, workload, subscriptionId);
        return new(deployment, policy, Placement(policy, workload), names, plan, site);
    }

    internal static AzureAppServicePlacement Placement(AzureAppServicePolicy policy, InfrastructureNodeId workload) =>
        policy.Placements.SingleOrDefault(p => p.Workload == workload) ?? throw new ArgumentException("Select a participating App Service workload.", nameof(workload));
    static void RequireValid(InfrastructureTargetDeploymentPlan deployment, AzureAppServicePolicy policy, Guid subscription)
    {
        var diagnostics = Validate(deployment, policy, subscription);
        if (!diagnostics.IsEmpty) throw new AzureAppServiceValidationException(diagnostics);
    }
    static bool ValidSettingName(string? name, int limit) => !string.IsNullOrWhiteSpace(name) && name.Length <= limit && name.IndexOfAny(['\0', '=']) < 0;
    static string? PhysicalName(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length > prefix.Length && !value[prefix.Length..].Any(c => char.IsWhiteSpace(c) || c is '/' or ':') ? value[prefix.Length..] : null;
}

/// <summary>Attributable semantic rejection of native App Service association.</summary>
public sealed class AzureAppServiceValidationException : ArgumentException
{
    internal AzureAppServiceValidationException(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Diagnostics = diagnostics;
    /// <summary>Normalized diagnostics containing no native setting values.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}
