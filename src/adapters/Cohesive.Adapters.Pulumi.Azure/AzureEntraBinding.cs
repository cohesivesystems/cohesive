using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi.AzureAD;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Associates native AzureAD 6.9.0 resources with exact canonical ownership and authorization decisions.</summary>
public static class AzureEntraBinding
{
    /// <summary>Existing Azure Pulumi program interpretation, including its native AzureAD provider.</summary>
    public const string Target = "pulumi-azure-native/3.16.0";
    /// <summary>External directory facility.</summary>
    public const string TenantFacility = "azure/entra";
    /// <summary>Managed application registration facility.</summary>
    public const string ApplicationFacility = "azure/entra-application";
    /// <summary>Managed application service-principal facility.</summary>
    public const string PrincipalFacility = "azure/entra-service-principal";

    /// <summary>Validates canonical ownership, dependencies and complete explicit permission decisions before registration.</summary>
    /// <param name="deployment">Exact compiled deployment.</param>
    /// <param name="policy">Non-secret ownership and authorization policy.</param>
    /// <param name="tenantId">Tenant resolved using the caller's AzureAD provider; must match policy.</param>
    /// <returns>Ordered attributable errors; an empty result does not establish consent or runtime access.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    public static ImmutableArray<DocumentValidationDiagnostic> Validate(InfrastructureTargetDeploymentPlan deployment,
        AzureEntraPolicy policy, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(deployment); ArgumentNullException.ThrowIfNull(policy);
        var errors = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        void Error(string code, string message) => errors.Add(Diagnostic(deployment, policy, code, message));
        AzureConstructionPolicy.ValidatePlan(deployment, Target, policy.SourceReferences, errors, Error);
        if (tenantId == Guid.Empty || policy.TenantId != tenantId) Error("tenant", "Match the non-empty provider and policy tenant IDs.");
        var authority = policy.LifecycleAuthority.Value?.Split('/') ?? [];
        if (authority.Length != 3 || authority[0] != "pulumi" || authority.Any(string.IsNullOrWhiteSpace))
            Error("authority", "Use an exact pulumi/project/stack lifecycle authority for native URN checks.");
        var tenant = deployment.Manifest.Resources.SingleOrDefault(r => r.Resource == policy.Tenant);
        var definition = deployment.FacilityPlan.Definition.Definition;
        if (tenant?.Facility.Value != TenantFacility ||
            definition.Resources.SingleOrDefault(r => r.Id == policy.Tenant)?.Lifecycle != InfrastructureResourceLifecycle.External ||
            deployment.Realization?.Lifecycle.Bindings.Any(b => b.Resource == policy.Tenant && b.Disposition == InfrastructureLifecycleDisposition.Managed) == true)
            Error("tenant-ownership", "Declare the tenant as an external directory dependency, never a managed application resource.");
        else if (tenant.PhysicalResource.Value != "azure/entra/tenants/current-provider" &&
            tenant.PhysicalResource.Value != $"azure/entra/tenants/{tenantId:D}")
            Error("tenant-identity", "Select the explicit tenant GUID or the attributed current-provider tenant locator.");
        Check(policy.Application, ApplicationFacility, "applications");
        RequireDependency(policy.Application, policy.Tenant);
        if (policy.ServicePrincipal is { } principal)
        {
            Check(principal, PrincipalFacility, "service-principals");
            RequireDependency(principal, policy.Application);
        }
        if (string.IsNullOrWhiteSpace(policy.DelegatedAccessContract.Value) || string.IsNullOrWhiteSpace(policy.ApplicationAccessContract.Value) ||
            policy.DelegatedAccessContract == policy.ApplicationAccessContract)
            Error("contracts", "Select distinct non-empty delegated-scope and application-role contracts.");
        var bindings = Participating(deployment, policy).ToDictionary(b => b.Id);
        foreach (var binding in definition.Bindings.Where(b => b.Target == policy.Application))
        {
            if (binding.Contract != policy.DelegatedAccessContract && binding.Contract != policy.ApplicationAccessContract)
                Error("binding", "Incoming access bindings must use a supported explicit contract.");
            if (!bindings.ContainsKey(binding.Id) && deployment.Realization?.FindNonParticipation(binding.Source) is null)
                Error("consumer", "Access sources must be participating workloads or managed application registrations.");
        }
        var selected = new HashSet<InfrastructureBindingId>();
        if (policy.Permissions.IsDefault) Error("permissions", "Supply explicit permission decisions, including an empty collection when appropriate.");
        else foreach (var permission in policy.Permissions)
        {
            if (permission is null) { Error("permissions", "Permission decisions cannot be null."); continue; }
            if (!selected.Add(permission.Binding) || !bindings.TryGetValue(permission.Binding, out var binding))
            { Error("permissions", "Select each participating incoming binding exactly once."); continue; }
            var valid = permission.Action switch
            {
                AzureEntraPermissionAction.RequestDelegatedScope => binding.Contract == policy.DelegatedAccessContract,
                AzureEntraPermissionAction.AssignApplicationRole => binding.Contract == policy.ApplicationAccessContract && policy.ServicePrincipal is not null,
                _ => false
            };
            if (!valid || permission.PermissionId == Guid.Empty)
                Error("permission-action", "Choose a supported contract/action and explicit permission GUID; role assignments require an owned resource principal.");
            if (permission.SourceReferences.IsDefaultOrEmpty || permission.SourceReferences.Any(s => string.IsNullOrWhiteSpace(s.Value)))
                Error("permission-evidence", "Attribute every permission decision to non-empty policy references.");
        }
        if (!selected.SetEquals(bindings.Keys)) Error("permissions", "Provide one permission decision for every participating incoming access binding.");
        return DocumentValidationDiagnostics.Normalize(errors.ToImmutable());

        void RequireDependency(InfrastructureNodeId subject, InfrastructureNodeId dependency)
        {
            if (!definition.ReadinessDependencies.Any(d => d.Subject == subject && d.Dependency == dependency))
                Error("dependency", "Declare application-to-tenant and principal-to-application dependencies canonically.");
        }
        void Check(InfrastructureNodeId id, string facility, string kind)
        {
            var resource = AzureConstructionPolicy.SelectManagedResource(deployment, id, facility, policy.LifecycleAuthority, Target, Error);
            if (resource is null) return;
            if (LogicalName(resource.PhysicalResource.Value, kind) is null)
                Error("physical-identity", "Use azure/entra/applications/<Pulumi-name> or azure/entra/service-principals/<Pulumi-name> locators.");
            if (deployment.Manifest.Resources.Any(r => r.Resource != id && r.PhysicalResource == resource.PhysicalResource))
                Error("alias", "Multiple canonical resources claim this registration locator.");
        }
    }

    /// <summary>Returns native logical names from validated canonical registration locators.</summary>
    /// <param name="deployment">Exact compiled deployment.</param>
    /// <param name="policy">Canonical association.</param>
    /// <param name="tenantId">Resolved provider tenant.</param>
    /// <returns>Application and optional service-principal logical names; display names remain native configuration.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    /// <exception cref="AzureEntraValidationException">Semantic validation failed.</exception>
    public static (string Application, string? ServicePrincipal) Names(InfrastructureTargetDeploymentPlan deployment,
        AzureEntraPolicy policy, Guid tenantId)
    {
        var diagnostics = Validate(deployment, policy, tenantId);
        if (!diagnostics.IsEmpty) throw new AzureEntraValidationException(diagnostics);
        return (Name(policy.Application, "applications"), policy.ServicePrincipal is { } principal ? Name(principal, "service-principals") : null);
        string Name(InfrastructureNodeId id, string kind) => LogicalName(deployment.Manifest.FindResource(id).PhysicalResource.Value, kind)!;
    }

    /// <summary>Attaches native resources without constructing a directory, application, principal, grant or credential.</summary>
    /// <param name="deployment">Exact canonical deployment.</param>
    /// <param name="policy">Canonical ownership and permission decisions.</param>
    /// <param name="tenantId">Tenant obtained from the same provider used for native construction.</param>
    /// <param name="application">Caller-owned native application.</param>
    /// <param name="servicePrincipal">Owned principal if and only if policy declares one.</param>
    /// <param name="cancellationToken">Checked before association; native lifecycle cancellation remains Pulumi-owned.</param>
    /// <returns>Checked native outputs and explicit permission projections.</returns>
    /// <exception cref="ArgumentNullException">Required input is null.</exception>
    /// <exception cref="ArgumentException">Declared and attached principal presence differs.</exception>
    /// <exception cref="AzureEntraValidationException">Canonical association is invalid; native registration may already have occurred.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static AzureEntraResources Attach(InfrastructureTargetDeploymentPlan deployment, AzureEntraPolicy policy,
        Guid tenantId, Application application, ServicePrincipal? servicePrincipal = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(application);
        var names = Names(deployment, policy, tenantId);
        if ((servicePrincipal is null) != (policy.ServicePrincipal is null)) throw new ArgumentException("Attach exactly the principal declared by policy.", nameof(servicePrincipal));
        return new(deployment, policy, application, servicePrincipal, names, [.. Participating(deployment, policy).OrderBy(b => b.Id.Value, StringComparer.Ordinal)]);
    }

    internal static DocumentValidationDiagnostic Diagnostic(InfrastructureTargetDeploymentPlan plan, AzureEntraPolicy policy,
        string code, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) => new("azure.entra." + code, severity, message,
        SchemaLocation: policy.Application.Value, Evidence: new(stage: "pulumi-azure-binding", subject: policy.Application.Value ?? "unset-application",
            sourceReferences: [plan.Manifest.Fingerprint.Value, .. policy.SourceReferences.IsDefault ? [] : policy.SourceReferences.Select(s => s.Value)]));

    static IEnumerable<InfrastructureBindingDefinition> Participating(InfrastructureTargetDeploymentPlan deployment, AzureEntraPolicy policy) =>
        deployment.FacilityPlan.Definition.Definition.Bindings.Where(b => b.Target == policy.Application &&
            (deployment.Manifest.Workloads.Any(w => w.Workload == b.Source) ||
             deployment.Manifest.Resources.Any(r => r.Resource == b.Source && r.Facility.Value == ApplicationFacility && r.ManagingInterpreter is null &&
                 deployment.Realization?.Lifecycle.Bindings.Any(l => l.Resource == r.Resource && l.Disposition == InfrastructureLifecycleDisposition.Managed) == true)));

    static string? LogicalName(string physical, string kind)
    {
        var prefix = "azure/entra/" + kind + "/";
        if (!physical.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var name = physical[prefix.Length..];
        return name.Length > 0 && !name.Any(c => char.IsWhiteSpace(c) || c is '/' or ':') ? name : null;
    }
}

/// <summary>Structured rejection of a canonical Entra association.</summary>
public sealed class AzureEntraValidationException : ArgumentException
{
    internal AzureEntraValidationException(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Diagnostics = diagnostics;
    /// <summary>Ordered attributable diagnostics without provider credentials.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}
