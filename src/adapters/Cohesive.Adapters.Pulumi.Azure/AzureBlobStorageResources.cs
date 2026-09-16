using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Pulumi;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.Storage;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>One shared Storage account, exact canonical containers and derived noncredential outputs.</summary>
public sealed class AzureBlobStorageResources
{
    internal AzureBlobStorageResources(InfrastructureTargetDeploymentPlan deployment, AzureBlobStoragePolicy policy,
        StorageAccount account, ImmutableDictionary<InfrastructureNodeId, BlobContainer> containers,
        ImmutableArray<InfrastructureBindingDefinition> bindings)
    {
        Deployment = deployment; Policy = policy; Account = account; Containers = containers; BlobBindings = bindings;
    }
    /// <summary>Original exact deployment; no independent topology is reconstructed.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }
    /// <summary>Inspectable ownership, topology and access decisions.</summary>
    public AzureBlobStoragePolicy Policy { get; }
    /// <summary>Single account constructed for this complete group.</summary>
    public StorageAccount Account { get; }
    /// <summary>Containers keyed by their canonical resource identity.</summary>
    public ImmutableDictionary<InfrastructureNodeId, BlobContainer> Containers { get; }
    /// <summary>Participating canonical bindings in ordinal identity order.</summary>
    public ImmutableArray<InfrastructureBindingDefinition> BlobBindings { get; }
    /// <summary>Actual provider blob endpoint, without keys or SAS. Preserves provider Output secret classification.</summary>
    public Output<string> BlobEndpoint => Account.PrimaryEndpoints.Apply(endpoints => endpoints.Blob
        ?? throw new InvalidOperationException("The constructed Storage account has no blob endpoint."));

    /// <summary>Derives an HTTPS container root from the provider endpoint and actual container name.</summary>
    /// <param name="resource">Declared canonical container.</param>
    /// <returns>Credential-free root with Pulumi dependencies and secret classification preserved.</returns>
    /// <exception cref="ArgumentException">Resource is not part of this constructed group.</exception>
    /// <exception cref="InvalidOperationException">Provider returned no blob endpoint.</exception>
    public Output<string> ContainerEndpoint(InfrastructureNodeId resource)
    {
        if (!Containers.TryGetValue(resource, out var container)) throw new ArgumentException("Select a declared canonical container.", nameof(resource));
        return Output.Tuple(BlobEndpoint, container.Name).Apply(v => $"{v.Item1.TrimEnd('/')}/{v.Item2}");
    }

    /// <summary>Produces one Blob Data Contributor grant for one binding or compatible bindings of the same workload.</summary>
    /// <param name="bindings">Non-empty unique canonical binding IDs. Coalescing requires the same workload and effective scope;
    /// account-wide access for each binding must already be explicit in policy. Container grants must target the same container.</param>
    /// <param name="subscriptionId">Actual role provider subscription, matched to construction policy.</param>
    /// <param name="principalId">Selected workload's managed principal; Output secrecy is preserved.</param>
    /// <param name="assignmentId">Existing or explicitly chosen stable GUID; never inferred from enumeration order.</param>
    /// <returns>Role inputs; caller retains the role's logical name, provider, parent and dependencies.</returns>
    /// <exception cref="ArgumentException">Bindings are absent, duplicated, incompatible or have different scopes; subscription differs or GUID is empty.</exception>
    /// <exception cref="ArgumentNullException">Principal is null.</exception>
    public RoleAssignmentArgs AccessGrant(ImmutableArray<InfrastructureBindingId> bindings, Guid subscriptionId,
        Input<string> principalId, Guid assignmentId)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        if (bindings.IsDefaultOrEmpty || bindings.Distinct().Count() != bindings.Length ||
            bindings.Any(id => !BlobBindings.Any(b => b.Id == id)))
            throw new ArgumentException("Select unique participating canonical blob bindings.", nameof(bindings));
        if (subscriptionId != Policy.SubscriptionId) throw new ArgumentException("Cross-subscription grants are unsupported.", nameof(subscriptionId));
        if (assignmentId == Guid.Empty) throw new ArgumentException("Supply an explicit assignment GUID.", nameof(assignmentId));
        var selected = bindings.Select(id => BlobBindings.Single(b => b.Id == id)).ToArray();
        var access = Policy.Access.Single(a => a.Binding == selected[0].Id);
        if (selected.Any(b => b.Source != selected[0].Source || Policy.Access.Single(a => a.Binding == b.Id).Scope != access.Scope ||
            access.Scope == AzureBlobScope.Container && b.Target != selected[0].Target))
            throw new ArgumentException("Only bindings of one workload at the same explicit effective scope may share a grant.", nameof(bindings));
        return new()
        {
            Scope = access.Scope == AzureBlobScope.Account ? Account.Id : Containers[selected[0].Target].Id,
            RoleDefinitionId = $"/subscriptions/{subscriptionId:D}/providers/Microsoft.Authorization/roleDefinitions/{AzureBlobStorageConstruction.DataContributorRole}",
            PrincipalId = principalId, PrincipalType = "ServicePrincipal", RoleAssignmentName = assignmentId.ToString("D")
        };
    }
}
