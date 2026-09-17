using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Pulumi;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.KeyVault;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>One constructed vault and its canonical consumer decisions; construction does not assert effective access.</summary>
public sealed class AzureKeyVaultResources
{
    internal AzureKeyVaultResources(InfrastructureTargetDeploymentPlan deployment, AzureKeyVaultPolicy policy,
        Vault vault, ImmutableArray<InfrastructureBindingDefinition> bindings)
    {
        Deployment = deployment; Policy = policy; Vault = vault; SecretBindings = bindings;
        VaultUri = vault.Properties.Apply(p => !string.IsNullOrWhiteSpace(p.VaultUri) ? p.VaultUri :
            throw new InvalidOperationException("The vault provider returned no vault URI."));
    }
    /// <summary>Original canonical deployment and provenance.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }
    /// <summary>Immutable construction policy, including attributed decisions to create no grant.</summary>
    public AzureKeyVaultPolicy Policy { get; }
    /// <summary>Registered vault; its actual ID is the only supported grant scope.</summary>
    public Vault Vault { get; }
    /// <summary>Participating canonical secret-consumer bindings in ordinal ID order, including consumers without managed grants.</summary>
    public ImmutableArray<InfrastructureBindingDefinition> SecretBindings { get; }
    /// <summary>Actual provider URI without secret contents; output dependencies and secret classification propagate.</summary>
    /// <remarks>A missing resolved URI faults the output with <see cref="InvalidOperationException"/>; unknown preview outputs remain unknown.</remarks>
    public Output<string> VaultUri { get; }

    /// <summary>Produces vault-scoped role inputs only for an explicitly authorized canonical consumer.</summary>
    /// <param name="binding">Participating binding whose policy action is AssignSecretsUser.</param>
    /// <param name="subscriptionId">Role provider subscription; must match the vault's policy.</param>
    /// <param name="tenantId">Role principal's tenant; must match the vault's policy.</param>
    /// <param name="principalId">Source workload's service principal ID; caller owns association and preserves dependency/secret classification.</param>
    /// <param name="assignmentId">Existing or explicitly chosen role GUID, never inferred.</param>
    /// <returns>Role inputs; caller preserves its role logical name, provider, parent and extra dependencies.</returns>
    /// <exception cref="ArgumentNullException">Principal input is null.</exception>
    /// <exception cref="ArgumentException">Binding lacks an explicit grant decision, host scope differs, or assignment GUID is empty.</exception>
    public RoleAssignmentArgs AccessGrant(InfrastructureBindingId binding, Guid subscriptionId, Guid tenantId,
        Input<string> principalId, Guid assignmentId)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        if (!SecretBindings.Any(b => b.Id == binding) ||
            !Policy.Access.Any(a => a.Binding == binding && a.Action == AzureKeyVaultAccessAction.AssignSecretsUser))
            throw new ArgumentException("Select a participating consumer explicitly authorized for AssignSecretsUser.", nameof(binding));
        if (subscriptionId != Policy.SubscriptionId || tenantId != Policy.TenantId)
            throw new ArgumentException("Grant subscription and tenant must match the vault policy.");
        if (assignmentId == Guid.Empty) throw new ArgumentException("Supply an explicit assignment GUID.", nameof(assignmentId));
        return new()
        {
            Scope = Vault.Id, PrincipalId = principalId, PrincipalType = "ServicePrincipal",
            RoleAssignmentName = assignmentId.ToString("D"),
            RoleDefinitionId = $"/subscriptions/{subscriptionId:D}/providers/Microsoft.Authorization/roleDefinitions/{AzureKeyVaultConstruction.SecretsUserRole}"
        };
    }
}
