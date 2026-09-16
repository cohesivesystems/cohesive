using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.CosmosDB;
using Pulumi.AzureNative.CosmosDB.Inputs;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Validates and constructs one exact single-region Cosmos SQL facility in the host's Pulumi program.</summary>
public static class AzureCosmosConstruction
{
    static readonly ImmutableDictionary<string, DefaultConsistencyLevel> Consistencies = new[]
    {
        DefaultConsistencyLevel.Session, DefaultConsistencyLevel.Eventual,
        DefaultConsistencyLevel.ConsistentPrefix, DefaultConsistencyLevel.Strong
    }.ToImmutableDictionary(level => level.ToString(), StringComparer.Ordinal);

    /// <summary>Exact Azure Native target supported by this package.</summary>
    public const string Target = AzureDurableTaskConstruction.Target;
    /// <summary>Selected database facility.</summary>
    public const string Facility = "azure/cosmos-db";
    /// <summary>Cosmos SQL built-in Data Contributor role suffix, not an Azure management-plane role.</summary>
    public const string DataContributorRole = "00000000-0000-0000-0000-000000000002";

    /// <summary>Checks the complete realization, exclusive ownership, topology and explicit binding scopes without I/O.</summary>
    /// <param name="deployment">Exact compiled plan and semantic authority.</param>
    /// <param name="policy">Non-secret, attributable provider policy.</param>
    /// <param name="subscriptionId">Host's explicitly configured Azure Native provider subscription.</param>
    /// <returns>Stable structured errors, or an empty array.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    public static ImmutableArray<DocumentValidationDiagnostic> Validate(InfrastructureTargetDeploymentPlan deployment,
        AzureCosmosPolicy policy, Guid subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(policy);
        var errors = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        void Error(string code, string message) => errors.Add(new("azure.cosmos." + code, DiagnosticSeverity.Error,
            message, SchemaLocation: policy.Resource.Value,
            Evidence: new(stage: "pulumi-azure-construction", subject: policy.Resource.Value ?? "unset-resource",
                sourceReferences: [deployment.Manifest.Fingerprint.Value,
                    .. policy.SourceReferences.IsDefault ? [] : policy.SourceReferences.Select(s => s.Value)])));
        AzureConstructionPolicy.ValidateDeployment(deployment, Target, policy.SubscriptionId, subscriptionId,
            policy.SourceReferences, errors, Error);
        if (!AzureConstructionPolicy.ValidResourceGroup(policy.ResourceGroupName) || string.IsNullOrWhiteSpace(policy.Location))
            Error("location", "Supply a valid explicit resource group and region.");
        if (string.IsNullOrWhiteSpace(policy.AccountName) || string.IsNullOrWhiteSpace(policy.DatabaseName))
            Error("logical-name", "Supply existing or explicitly chosen account/database logical names.");
        if (!AzureConstructionPolicy.ValidTags(policy.Tags)) Error("tags", "Supply valid non-secret Azure account tags.");
        if (!ValidThroughput(policy.DatabaseThroughput)) Error("throughput", "Manual database RU/s must be at least 400 and a multiple of 100.");
        if (policy.Consistency is null || !Consistencies.ContainsKey(policy.Consistency))
            Error("consistency", "Supported single-region consistency levels are Session, Eventual, ConsistentPrefix and Strong.");

        var resource = AzureConstructionPolicy.SelectManagedResource(deployment, policy.Resource, Facility,
            policy.LifecycleAuthority, Target, Error);
        if (resource is not null)
        {
            var physical = ParsePhysical(resource.PhysicalResource.Value);
            if (!physical.Success) Error("physical-identity", "Expected azure/cosmos-db/accounts/<account>/databases/<database> with supported names.");
            if (physical.Success && deployment.Manifest.Resources.Any(r => r.Resource != resource.Resource &&
                (r.PhysicalResource == resource.PhysicalResource ||
                 string.Equals(ParsePhysical(r.PhysicalResource.Value).Groups[1].Value, physical.Groups[1].Value, StringComparison.OrdinalIgnoreCase))))
                Error("alias", "Another canonical resource shares this account; declare one account construction owner before using this slice.");
        }

        var containers = policy.Containers.IsDefault ? [] : policy.Containers;
        if (containers.IsEmpty || containers.Any(c => c is null)) Error("containers", "Supply a non-empty complete container topology with no null entries.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var logicalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in containers.Where(c => c is not null))
        {
            if (!ValidName(container.Name) || !names.Add(container.Name) || string.IsNullOrWhiteSpace(container.LogicalName) || !logicalNames.Add(container.LogicalName))
                Error("containers", "Container physical and logical names must be valid and unique.");
            if (!ValidPath(container.PartitionKeyPath)) Error("partition", "Supply one Hash partition path with slash-separated identifier segments.");
            if (container.Throughput is { } ru && !ValidThroughput(ru)) Error("throughput", "Dedicated RU/s must be at least 400 and a multiple of 100; null inherits the database.");
            if (container.CompositeIndexes.IsDefault) Error("indexes", "Supply explicit composite indexes or an empty array.");
            else foreach (var index in container.CompositeIndexes)
            {
                if (index.IsDefaultOrEmpty || index.Length is < 2 or > 8 ||
                    index.Any(p => p is null || !ValidPath(p.Path) || p.Order is not ("ascending" or "descending")) ||
                    index.Where(p => p is not null).Select(p => p.Path).Distinct(StringComparer.Ordinal).Count() != index.Length)
                    Error("indexes", "Each composite index requires 2–8 distinct valid paths with explicit ascending/descending order.");
            }
        }
        if (string.IsNullOrWhiteSpace(policy.RepositoryContract.Value)) Error("binding", "Select an explicit repository contract.");
        var bindings = Bindings(deployment, policy).ToArray();
        foreach (var binding in bindings)
            if (binding.Target != policy.Resource || binding.Contract != policy.RepositoryContract ||
                !deployment.Manifest.Workloads.Any(w => w.Workload == binding.Source) && deployment.Realization?.FindNonParticipation(binding.Source) is null)
                Error("binding", "Only incoming repository bindings from deployed or explicitly non-participating workloads are supported.");
        var active = bindings.Where(b => deployment.Manifest.Workloads.Any(w => w.Workload == b.Source)).ToArray();
        var access = policy.Access.IsDefault ? [] : policy.Access;
        if (policy.Access.IsDefault || access.Any(a => a is null) ||
            active.Any(b => access.Count(a => a?.Binding == b.Id) != 1) ||
            access.Any(a => a is not null && !active.Any(b => b.Id == a.Binding)))
            Error("access", "Declare exactly one explicit access scope for every participating binding, and none for absent workers.");
        foreach (var grant in access.Where(a => a is not null))
            if (!ValidScope(grant.Scope, grant.ContainerName, names)) Error("access", "Select account, database, or one declared container explicitly; only container scope accepts a container name.");
        return DocumentValidationDiagnostics.Normalize(errors.ToImmutable());
    }

    /// <summary>Constructs the validated account/database/containers, preserving the caller's provider and parent.</summary>
    /// <param name="deployment">Exact plan; check any Aspire handoff before calling.</param>
    /// <param name="policy">Explicit construction and access policy.</param>
    /// <param name="subscriptionId">Host provider's configured subscription; the caller must match its actual provider configuration.</param>
    /// <param name="provider">Existing Azure Native provider, or null to preserve the host's default provider. No new provider is introduced.</param>
    /// <param name="parent">Existing common resource parent, or null for root resources.</param>
    /// <param name="resourceGroupDependency">Resource group created by the same program, or null if already existing.</param>
    /// <param name="cancellationToken">Checked before registration; Pulumi owns execution cancellation after registration starts.</param>
    /// <returns>Registered resources and canonical data-plane access inputs. No account keys are retrieved.</returns>
    /// <exception cref="AzureCosmosValidationException">Policy/plan validation failed before resource registration.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before construction.</exception>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    public static AzureCosmosResources Register(InfrastructureTargetDeploymentPlan deployment, AzureCosmosPolicy policy,
        Guid subscriptionId, global::Pulumi.AzureNative.Provider? provider = null, Resource? parent = null,
        Resource? resourceGroupDependency = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(deployment, policy, subscriptionId);
        if (!diagnostics.IsEmpty) throw new AzureCosmosValidationException(diagnostics);
        var physical = ParsePhysical(deployment.Manifest.Resources.Single(r => r.Resource == policy.Resource).PhysicalResource.Value);
        cancellationToken.ThrowIfCancellationRequested();
        var account = new DatabaseAccount(policy.AccountName, new()
        {
            AccountName = physical.Groups[1].Value, ResourceGroupName = policy.ResourceGroupName, Location = policy.Location,
            Kind = "GlobalDocumentDB", DatabaseAccountOfferType = DatabaseAccountOfferType.Standard,
            ConsistencyPolicy = new ConsistencyPolicyArgs { DefaultConsistencyLevel = Consistencies[policy.Consistency] },
            DisableLocalAuth = policy.DisableLocalAuth, EnableFreeTier = policy.EnableFreeTier, EnableAutomaticFailover = false,
            PublicNetworkAccess = policy.PublicNetworkAccess ? "Enabled" : "Disabled",
            Locations = [new LocationArgs { LocationName = policy.Location, FailoverPriority = 0, IsZoneRedundant = false }],
            Tags = policy.Tags.ToDictionary(t => t.Key, t => t.Value)
        }, new() { Provider = provider, Parent = parent, DependsOn = resourceGroupDependency is null ? [] : [resourceGroupDependency] });
        var database = new SqlResourceSqlDatabase(policy.DatabaseName, new()
        {
            AccountName = account.Name, DatabaseName = physical.Groups[2].Value, ResourceGroupName = policy.ResourceGroupName,
            Location = policy.Location, Resource = new SqlDatabaseResourceArgs { Id = physical.Groups[2].Value },
            Options = new CreateUpdateOptionsArgs { Throughput = policy.DatabaseThroughput }
        }, new() { Provider = provider, Parent = parent });
        var containers = ImmutableSortedDictionary.CreateBuilder<string, SqlResourceSqlContainer>(StringComparer.Ordinal);
        foreach (var container in policy.Containers.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var resource = new SqlContainerResourceArgs
            {
                Id = container.Name, PartitionKey = new ContainerPartitionKeyArgs { Kind = "Hash", Paths = [container.PartitionKeyPath] }
            };
            if (!container.CompositeIndexes.IsEmpty)
                resource.IndexingPolicy = new IndexingPolicyArgs
                {
                    CompositeIndexes = [.. container.CompositeIndexes.Select(index => index.Select(p =>
                        new CompositePathArgs { Path = p.Path, Order = p.Order }).ToImmutableArray())]
                };
            var args = new SqlResourceSqlContainerArgs
            {
                AccountName = account.Name, DatabaseName = database.Name, ContainerName = container.Name,
                ResourceGroupName = policy.ResourceGroupName, Location = policy.Location, Resource = resource
            };
            if (container.Throughput is { } ru) args.Options = new CreateUpdateOptionsArgs { Throughput = ru };
            containers.Add(container.Name, new(container.LogicalName, args, new() { Provider = provider, Parent = parent }));
        }
        return new(deployment, policy, account, database, containers.ToImmutable(),
            [.. Bindings(deployment, policy).Where(b => deployment.Manifest.Workloads.Any(w => w.Workload == b.Source))
                .OrderBy(b => b.Id.Value, StringComparer.Ordinal)]);
    }

    internal static bool ValidScope(AzureCosmosScope scope, string? container, IEnumerable<string> names) => scope switch
    {
        AzureCosmosScope.Account or AzureCosmosScope.Database => container is null,
        AzureCosmosScope.Container => container is not null && names.Contains(container, StringComparer.Ordinal),
        _ => false
    };
    static bool ValidThroughput(int value) => value >= 400 && value % 100 == 0;
    static bool ValidName(string? value) => value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9_-]{1,255}\z");
    static bool ValidPath(string? value) => value is not null && Regex.IsMatch(value, @"\A(?:/[A-Za-z_][A-Za-z0-9_]*)+\z");
    static Match ParsePhysical(string value) => Regex.Match(value,
        @"\Aazure/cosmos-db/accounts/([a-z0-9][a-z0-9-]{1,42}[a-z0-9])/databases/([A-Za-z0-9_-]{1,255})\z");
    static IEnumerable<InfrastructureBindingDefinition> Bindings(InfrastructureTargetDeploymentPlan deployment, AzureCosmosPolicy policy) =>
        AzureConstructionPolicy.Bindings(deployment, policy.Resource);
}

/// <summary>Ordered, attributable Cosmos construction errors detected before registering any resource.</summary>
public sealed class AzureCosmosValidationException : ArgumentException
{
    internal AzureCosmosValidationException(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Diagnostics = diagnostics;
    /// <summary>Exact validation diagnostics with manifest and policy provenance.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}
