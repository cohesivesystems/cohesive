using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Pulumi;
using Pulumi.AzureNative.CosmosDB;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Constructed Cosmos resources and derived data-plane inputs, retaining the original plan and policy.</summary>
public sealed class AzureCosmosResources
{
    internal AzureCosmosResources(InfrastructureTargetDeploymentPlan deployment, AzureCosmosPolicy policy,
        DatabaseAccount account, SqlResourceSqlDatabase database,
        ImmutableSortedDictionary<string, SqlResourceSqlContainer> containers,
        ImmutableArray<InfrastructureBindingDefinition> bindings)
    {
        Deployment = deployment; Policy = policy; Account = account; Database = database;
        Containers = containers; RepositoryBindings = bindings;
    }
    /// <summary>Original exact deployment; no second topology is reconstructed.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }
    /// <summary>Explicit policy, including attributable access decisions.</summary>
    public AzureCosmosPolicy Policy { get; }
    /// <summary>Account registered through the host's Azure Native provider.</summary>
    public DatabaseAccount Account { get; }
    /// <summary>Database with explicit shared throughput.</summary>
    public SqlResourceSqlDatabase Database { get; }
    /// <summary>Containers keyed by physical name in ordinal order.</summary>
    public ImmutableSortedDictionary<string, SqlResourceSqlContainer> Containers { get; }
    /// <summary>Participating canonical repository bindings in ordinal identity order.</summary>
    public ImmutableArray<InfrastructureBindingDefinition> RepositoryBindings { get; }
    /// <summary>Provider endpoint; carries Pulumi classification and contains no retrieved account keys.</summary>
    public Output<string> Endpoint => Account.DocumentEndpoint;
    /// <summary>Built-in SQL data contributor role under the actual account ID.</summary>
    public Output<string> DataContributorRoleId => Account.Id.Apply(id => $"{id}/sqlRoleDefinitions/{AzureCosmosConstruction.DataContributorRole}");

    /// <summary>Derives a SQL data-plane scope for this constructed facility, including explicit bootstrap consumers.</summary>
    /// <param name="scope">Explicit account, database or container scope.</param>
    /// <param name="containerName">Declared physical container only for Container scope.</param>
    /// <returns>Scope under the actual account ID using Cosmos dbs/colls paths, not management-plane child IDs.</returns>
    /// <exception cref="ArgumentException">The scope/name pair is invalid or refers to an undeclared container.</exception>
    /// <remarks>This supplies no principal or grant. The caller owns bootstrap authorization and evidence;
    /// canonical workload grants should use AccessGrant so the selected policy cannot be silently widened.</remarks>
    public Output<string> DataPlaneScope(AzureCosmosScope scope, string? containerName = null)
    {
        if (!AzureCosmosConstruction.ValidScope(scope, containerName, Containers.Keys))
            throw new ArgumentException("Select an explicit valid scope within the constructed facility.", nameof(scope));
        if (scope == AzureCosmosScope.Account) return Account.Id;
        var databaseScope = Output.Tuple(Account.Id, Database.Name).Apply(v => $"{v.Item1}/dbs/{v.Item2}");
        return scope == AzureCosmosScope.Database ? databaseScope :
            Output.Tuple(databaseScope, Containers[containerName!].Name).Apply(v => $"{v.Item1}/colls/{v.Item2}");
    }

    /// <summary>Creates SQL Data Contributor assignment inputs at the binding's previously validated explicit scope.</summary>
    /// <param name="binding">Participating canonical repository binding.</param>
    /// <param name="subscriptionId">The role provider's configured subscription, matching construction policy.</param>
    /// <param name="principalId">That workload's principal; secret Output classification is preserved.</param>
    /// <param name="assignmentId">Existing or explicitly chosen role GUID; never regenerated from ordering.</param>
    /// <returns>Role inputs. Caller preserves logical name, provider, parent and any role dependencies.</returns>
    /// <exception cref="ArgumentNullException">Principal input is null.</exception>
    /// <exception cref="ArgumentException">Binding is absent, subscription differs or assignment GUID is empty.</exception>
    public SqlResourceSqlRoleAssignmentArgs AccessGrant(InfrastructureBindingId binding, Guid subscriptionId,
        Input<string> principalId, Guid assignmentId)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        if (!RepositoryBindings.Any(b => b.Id == binding)) throw new ArgumentException("Select a participating repository binding.", nameof(binding));
        if (subscriptionId != Policy.SubscriptionId) throw new ArgumentException("Cross-subscription grants are unsupported.", nameof(subscriptionId));
        if (assignmentId == Guid.Empty) throw new ArgumentException("Supply an explicit role-assignment GUID.", nameof(assignmentId));
        var access = Policy.Access.Single(a => a.Binding == binding);
        return new()
        {
            AccountName = Account.Name, ResourceGroupName = Policy.ResourceGroupName,
            Scope = DataPlaneScope(access.Scope, access.ContainerName), RoleDefinitionId = DataContributorRoleId,
            PrincipalId = principalId, RoleAssignmentId = assignmentId.ToString("D")
        };
    }
}
