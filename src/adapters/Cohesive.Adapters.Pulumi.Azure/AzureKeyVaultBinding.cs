using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.KeyVault;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Binds native Pulumi Key Vault resources to canonical Infra declarations without wrapping provider configuration.</summary>
public static class AzureKeyVaultBinding
{
    /// <summary>Exact supported interpretation target.</summary>
    public const string Target = "pulumi-azure-native/3.16.0";
    /// <summary>Canonical target facility.</summary>
    public const string Facility = "azure/key-vault";
    /// <summary>Built-in read-only secret data role; never assigned without an explicit binding decision.</summary>
    public const string SecretsUserRole = "4633458b-17de-408a-b874-0445c86b69e6";

    /// <summary>Checks the exact plan, ownership, declared host scope and every participating access decision.</summary>
    /// <param name="deployment">Canonical compiled deployment and topology authority.</param>
    /// <param name="policy">Explicit non-secret canonical association and access policy.</param>
    /// <param name="subscriptionId">Host's declared Azure provider subscription.</param>
    /// <param name="tenantId">Host's declared Azure provider tenant.</param>
    /// <returns>Deterministically ordered errors; empty means binding policy is valid, not that cloud access is proven.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    public static ImmutableArray<DocumentValidationDiagnostic> Validate(InfrastructureTargetDeploymentPlan deployment,
        AzureKeyVaultPolicy policy, Guid subscriptionId, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(policy);
        var errors = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        void Error(string code, string message) => errors.Add(new("azure.key-vault." + code,
            DiagnosticSeverity.Error, message, SchemaLocation: policy.Resource.Value,
            Evidence: new(stage: "pulumi-azure-binding", subject: policy.Resource.Value ?? "unset-resource",
                sourceReferences: [deployment.Manifest.Fingerprint.Value,
                    .. policy.SourceReferences.IsDefault ? [] : policy.SourceReferences.Select(s => s.Value)])));
        AzureConstructionPolicy.ValidateDeployment(deployment, Target, policy.SubscriptionId, subscriptionId,
            policy.SourceReferences, errors, Error);
        if (tenantId == Guid.Empty || policy.TenantId != tenantId)
            Error("tenant", "Match the explicit non-empty host and policy tenants.");
        var resource = AzureConstructionPolicy.SelectManagedResource(deployment, policy.Resource, Facility,
            policy.LifecycleAuthority, Target, Error);
        if (resource is not null)
        {
            if (!ParsePhysical(resource.PhysicalResource.Value).Success || resource.PhysicalResource.Value.Contains("--", StringComparison.Ordinal))
                Error("physical-identity", "Expected 'azure/key-vault/vaults/<name>' with a 3–24 character Azure vault name, beginning with a letter, ending with an alphanumeric, and no consecutive hyphens.");
            if (deployment.Manifest.Resources.Any(r => r.Resource != resource.Resource &&
                string.Equals(r.PhysicalResource.Value, resource.PhysicalResource.Value, StringComparison.OrdinalIgnoreCase)))
                Error("alias", "Multiple canonical resources claim this vault; select one exclusive owner.");
        }
        if (string.IsNullOrWhiteSpace(policy.SecretReadContract.Value))
            Error("secret-contract", "Select the canonical secret-read binding contract explicitly.");
        var participating = new HashSet<InfrastructureBindingId>();
        foreach (var binding in AzureConstructionPolicy.Bindings(deployment, policy.Resource))
        {
            if (binding.Target != policy.Resource || binding.Contract != policy.SecretReadContract)
                Error("binding", $"Binding '{binding.Id.Value}' has an unsupported direction or contract.");
            else if (deployment.Manifest.Workloads.Any(w => w.Workload == binding.Source))
                participating.Add(binding.Id);
            else if (deployment.Realization?.FindNonParticipation(binding.Source) is null)
                Error("consumer", $"Binding '{binding.Id.Value}' must originate at a deployed or explicitly non-participating workload.");
        }
        var selected = new HashSet<InfrastructureBindingId>();
        if (policy.Access.IsDefault) Error("access", "Supply explicit access decisions, even when the collection is empty.");
        else foreach (var access in policy.Access)
        {
            if (access is null) { Error("access", "Access decisions cannot be null."); continue; }
            if (!selected.Add(access.Binding) || !participating.Contains(access.Binding))
                Error("access", "Access decisions must uniquely select participating canonical secret-read bindings.");
            if (access.Action is not (AzureKeyVaultAccessAction.AssignSecretsUser or AzureKeyVaultAccessAction.NoManagedGrant))
                Error("access", "Choose AssignSecretsUser or NoManagedGrant explicitly for every consumer.");
            if (string.IsNullOrWhiteSpace(access.Reason) || access.SourceReferences.IsDefaultOrEmpty ||
                access.SourceReferences.Any(s => string.IsNullOrWhiteSpace(s.Value)))
                Error("access-evidence", "Every access decision requires a non-secret rationale and references to evidence or an unresolved-access follow-up.");
        }
        if (!participating.SetEquals(selected))
            Error("access", "Provide exactly one explicit access decision for each participating secret consumer; no grants are inferred.");
        return DocumentValidationDiagnostics.Normalize(errors.ToImmutable());
    }

    /// <summary>Returns the canonical physical name after validating the declaration and access decisions.</summary>
    /// <param name="deployment">Exact compiled deployment.</param>
    /// <param name="policy">Explicit canonical association and access policy.</param>
    /// <param name="subscriptionId">Declared host subscription.</param>
    /// <param name="tenantId">Declared host tenant.</param>
    /// <returns>The manifest's vault name for native <see cref="VaultArgs.VaultName"/> configuration.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    /// <exception cref="AzureKeyVaultValidationException">Semantic association validation failed.</exception>
    public static string VaultName(InfrastructureTargetDeploymentPlan deployment, AzureKeyVaultPolicy policy,
        Guid subscriptionId, Guid tenantId)
    {
        var diagnostics = Validate(deployment, policy, subscriptionId, tenantId);
        if (!diagnostics.IsEmpty) throw new AzureKeyVaultValidationException(diagnostics);
        return ParsePhysical(deployment.Manifest.Resources.Single(r => r.Resource == policy.Resource).PhysicalResource.Value).Groups[1].Value;
    }

    /// <summary>Associates a caller-created native vault with its canonical declaration. Registers no resources.</summary>
    /// <param name="deployment">Exact compiled deployment, checked by the host handoff.</param>
    /// <param name="policy">Canonical association and explicit consumer access decisions.</param>
    /// <param name="subscriptionId">Host's declared provider subscription.</param>
    /// <param name="tenantId">Host's declared provider tenant.</param>
    /// <param name="vault">Native resource constructed with the canonical name; caller owns all SDK arguments and options.</param>
    /// <param name="cancellationToken">Checked before association; native registration and its cancellation remain caller/Pulumi-owned.</param>
    /// <returns>Canonical consumers and identity-checked output/grant projections.</returns>
    /// <exception cref="ArgumentNullException">Deployment, policy or vault is null.</exception>
    /// <exception cref="AzureKeyVaultValidationException">Semantic association is invalid. The caller's native resource may already be registered.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before association.</exception>
    public static AzureKeyVaultResources Attach(InfrastructureTargetDeploymentPlan deployment, AzureKeyVaultPolicy policy,
        Guid subscriptionId, Guid tenantId, Vault vault, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(vault);
        var name = VaultName(deployment, policy, subscriptionId, tenantId);
        return new(deployment, policy, vault, name,
            [.. AzureConstructionPolicy.Bindings(deployment, policy.Resource)
                .Where(b => deployment.Manifest.Workloads.Any(w => w.Workload == b.Source))
                .OrderBy(b => b.Id.Value, StringComparer.Ordinal)]);
    }

    static Match ParsePhysical(string value) => Regex.Match(value,
        @"\Aazure/key-vault/vaults/([a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9])\z");
}

/// <summary>Structured rejection of a canonical vault association.</summary>
public sealed class AzureKeyVaultValidationException : ArgumentException
{
    internal AzureKeyVaultValidationException(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Diagnostics = diagnostics;
    /// <summary>Ordered attributable diagnostics. Secret values are never read or included.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}
