using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Pulumi;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Outputs;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Checked native hosting outputs; raw SDK objects remain available as explicit escape hatches.</summary>
public sealed class AzureAppServiceResources
{
    internal AzureAppServiceResources(InfrastructureTargetDeploymentPlan deployment, AzureAppServicePolicy policy,
        AzureAppServicePlacement placement, (string Site, string Plan) names, AppServicePlan plan, WebApp site)
    {
        Deployment = deployment; Policy = policy; Placement = placement; Plan = plan; Site = site;
        var planId = Output.Tuple(plan.Name, plan.Id, plan.Urn).Apply(value =>
            ValidIdentity(value.Item1, value.Item2, names.Plan, "serverfarms") && ValidUrn(value.Item3, "azure-native:web:AppServicePlan") ? value.Item2 :
                throw new InvalidOperationException("The native hosting plan does not match its canonical identity and owner."));
        SiteId = Output.Tuple(site.Name, site.Id, site.ServerFarmId, site.Urn, site.Enabled, planId).Apply(value =>
            ValidIdentity(value.Item1, value.Item2, names.Site, "sites") && ValidUrn(value.Item4, "azure-native:web:WebApp") &&
            string.Equals(value.Item3, value.Item6, StringComparison.OrdinalIgnoreCase) &&
            value.Item5 == (placement.Activation == AzureAppServiceActivation.Enabled) ? value.Item2 :
                throw new InvalidOperationException("The native site's identity, hosting plan, owner or activation differs from its canonical association."));
        PlanId = planId;

        bool ValidIdentity(string name, string id, string expected, string kind) => AzureConstructionPolicy.ValidResourceIdentity(
            name, id, expected, policy.SubscriptionId, "Microsoft.Web", kind, policy.ResourceGroupName);
        bool ValidUrn(string urn, string type) => AzureConstructionPolicy.ValidPulumiUrn(urn, policy.LifecycleAuthority, type);
    }
    /// <summary>Exact canonical topology and provenance.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }
    /// <summary>Complete hosting decisions used by endpoint admission.</summary>
    public AzureAppServicePolicy Policy { get; }
    /// <summary>This site's canonical placement and activation.</summary>
    public AzureAppServicePlacement Placement { get; }
    /// <summary>Original native plan, preserving parent/provider/options and shared instance identity.</summary>
    public AppServicePlan Plan { get; }
    /// <summary>Original native site; raw outputs bypass checked association.</summary>
    public WebApp Site { get; }
    /// <summary>Actual plan ID after identity and owner checks; unknown previews remain unknown.</summary>
    public Output<string> PlanId { get; }
    /// <summary>Actual site ID after identity, plan, owner and activation checks; resolved mismatches fault with InvalidOperationException.</summary>
    public Output<string> SiteId { get; }

    /// <summary>Projects an HTTPS endpoint only for an explicitly allowed canonical binding between enabled placed sites.</summary>
    /// <param name="binding">Incoming endpoint binding whose target is this workload.</param>
    /// <returns>Native provider hostname as HTTPS, preserving output dependencies and classification.</returns>
    /// <exception cref="ArgumentException">Binding is absent, not authorized, non-participating or touches a disabled site.</exception>
    /// <remarks>Unknown outputs remain unknown. A resolved non-HTTPS site or invalid hostname faults with InvalidOperationException. This does not prove reachability or authentication.</remarks>
    public Output<string> Endpoint(InfrastructureBindingId binding)
    {
        var edge = Deployment.FacilityPlan.Definition.Definition.Bindings.SingleOrDefault(b => b.Id == binding);
        if (edge is null || edge.Target != Placement.Workload || !Policy.EndpointContracts.Contains(edge.Contract) ||
            Placement.Activation != AzureAppServiceActivation.Enabled || !Policy.Placements.Any(p =>
                p.Workload == edge.Source && p.Activation == AzureAppServiceActivation.Enabled))
            throw new ArgumentException("Select an authorized incoming endpoint binding between enabled participating sites.", nameof(binding));
        return Output.Tuple(SiteId, Site.DefaultHostName, Site.HttpsOnly).Apply(value =>
        {
            if (value.Item3 != true || Uri.CheckHostName(value.Item2) != UriHostNameType.Dns)
                throw new InvalidOperationException("A declared HTTPS endpoint requires a valid native host name and HTTPS-only site.");
            return "https://" + value.Item2;
        });
    }

    /// <summary>Explicitly reads the site's live system-assigned identity after its checked resource ID is known.</summary>
    /// <param name="options">Native invoke options; use the same provider as the attached site.</param>
    /// <returns>Native identity evidence, including principal and tenant, from one site observation.</returns>
    /// <remarks>
    /// Unknown fresh-preview IDs do not invoke Azure. The caller opts into this read and owns provider configuration.
    /// The native SDK owns invoke cancellation/retry; failures propagate through the output. Disabled sites may retain
    /// identity for existing lifecycle operations; this method grants nothing and does not authorize active bindings.
    /// Resolved identity/name/tenant failures fault with InvalidOperationException without exposing resolved values.
    /// </remarks>
    public Output<ManagedServiceIdentityResponse> ReadManagedIdentity(InvokeOptions? options = null) => SiteId.Apply(async id =>
    {
        var result = await GetWebApp.InvokeAsync(new GetWebAppArgs
        {
            Name = id.Split('/')[8], ResourceGroupName = Policy.ResourceGroupName
        }, options);
        var identity = result.Identity;
        if (!string.Equals(result.Id, id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.Name, id.Split('/')[8], StringComparison.OrdinalIgnoreCase) || identity is null ||
            identity.Type is null || !identity.Type.Split(',').Select(s => s.Trim()).Contains("SystemAssigned", StringComparer.Ordinal) ||
            !Guid.TryParse(identity.PrincipalId, out var principal) || principal == Guid.Empty ||
            !Guid.TryParse(identity.TenantId, out var tenant) || tenant == Guid.Empty)
            throw new InvalidOperationException("The site observation has no matching system-assigned principal and tenant evidence.");
        return identity;
    });
}
