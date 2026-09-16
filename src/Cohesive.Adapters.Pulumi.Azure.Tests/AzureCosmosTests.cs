using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.CosmosDB;
using Pulumi.Testing;

namespace Cohesive.Adapters.Pulumi.Azure.Tests;

public sealed class AzureCosmosTests
{
    static readonly Guid Subscription = Guid.Parse("b6708815-d5b5-4070-af5b-675272a80b77");
    static readonly SourceReference Source = SourceReference.Create("test", "cosmos-policy");
    static readonly InfrastructureNodeId Worker = new("workloads/worker");
    static readonly InfrastructureNodeId Store = new("resources/store");
    static readonly InfrastructureBindingId Binding = new("bindings/worker/store");
    static readonly InfrastructureBindingContractId Contract = new("contracts/repository");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("pulumi/test/production");
    const string Physical = "azure/cosmos-db/accounts/test-account/databases/test-db";
    const string AccountId = "/subscriptions/b6708815-d5b5-4070-af5b-675272a80b77/resourceGroups/test-rg/providers/Microsoft.DocumentDB/databaseAccounts/test-account";

    static AzureCosmosPolicy Policy() => new()
    {
        Resource = Store, RepositoryContract = Contract, LifecycleAuthority = Authority, SubscriptionId = Subscription,
        ResourceGroupName = "test-rg", Location = "westus", AccountName = "existing-account", DatabaseName = "existing-database",
        DatabaseThroughput = 400, Consistency = "Session", EnableFreeTier = true, DisableLocalAuth = false, PublicNetworkAccess = true,
        Containers = [new() { Name = "inbox", LogicalName = "existing-inbox", PartitionKeyPath = "/partitionKey" },
            new() { Name = "examples", LogicalName = "existing-examples", PartitionKeyPath = "/partitionKey", Throughput = 800,
                CompositeIndexes = [[new("/observation/TimestampUtc", "ascending"), new("/observation/Id", "descending")]] }],
        Access = [new(Binding, AzureCosmosScope.Database)], SourceReferences = [Source],
        Tags = ImmutableSortedDictionary<string, string>.Empty.Add("environment", "test")
    };

    [Theory]
    [InlineData(AzureCosmosScope.Account, null, "")]
    [InlineData(AzureCosmosScope.Database, null, "/dbs/test-db")]
    [InlineData(AzureCosmosScope.Container, "inbox", "/dbs/test-db/colls/inbox")]
    public async Task Construction_preserves_topology_and_requires_exact_explicit_grant_scope(AzureCosmosScope scope, string? container, string suffix)
    {
        var plan = Plan();
        Assert.True(plan.IsComplete);
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions { IsPreview = false }, async () =>
        {
            var resources = AzureCosmosConstruction.Register(plan, Policy() with { Access = [new(Binding, scope, container)] }, Subscription);
            Assert.Same(plan, resources.Deployment);
            Assert.Equal(Binding, Assert.Single(resources.RepositoryBindings).Id);
            Assert.Equal(new[] { "examples", "inbox" }, resources.Containers.Keys);
            resources.Endpoint.Apply(value => { Assert.Equal("https://cosmos.invalid/", value); return value; });
            Assert.False(await Output.IsSecretAsync(resources.Endpoint));
            var args = resources.AccessGrant(Binding, Subscription, Output.CreateSecret("principal"), Guid.Parse("9da6bc9c-60e4-4e3c-99cf-2fe700670ded"));
            Assert.True(await Output.IsSecretAsync((Output<string>)args.PrincipalId!));
            _ = new SqlResourceSqlRoleAssignment("existing-grant", args);
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Guid.NewGuid(), "p", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(new("unknown"), Subscription, "p", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Subscription, "p", Guid.Empty));
            Assert.Throws<ArgumentException>(() => resources.DataPlaneScope(AzureCosmosScope.Container, "other"));
            Assert.Throws<ArgumentException>(() => resources.DataPlaneScope(AzureCosmosScope.Account, "inbox"));
        });
        var account = Assert.Single(mocks.Resources, r => r.Name == "existing-account");
        Assert.Equal("test-account", account.Inputs["accountName"]);
        Assert.Equal(true, account.Inputs["enableFreeTier"]);
        Assert.Equal(false, account.Inputs["disableLocalAuth"]);
        Assert.Equal("Enabled", account.Inputs["publicNetworkAccess"]);
        var database = Assert.Single(mocks.Resources, r => r.Name == "existing-database");
        Assert.Equal("test-db", database.Inputs["databaseName"]);
        Assert.Equal(400d, Map(database.Inputs["options"])["throughput"]);
        var inbox = Assert.Single(mocks.Resources, r => r.Name == "existing-inbox");
        Assert.False(inbox.Inputs.ContainsKey("options"));
        Assert.Equal("inbox", inbox.Inputs["containerName"]);
        var inboxResource = Map(inbox.Inputs["resource"]);
        Assert.False(inboxResource.ContainsKey("defaultTtl"));
        Assert.False(inboxResource.ContainsKey("uniqueKeyPolicy"));
        Assert.Equal("/partitionKey", Assert.Single(Assert.IsAssignableFrom<IEnumerable<object>>(Map(inboxResource["partitionKey"])["paths"])));
        var examples = Assert.Single(mocks.Resources, r => r.Name == "existing-examples");
        Assert.Equal(800d, Map(examples.Inputs["options"])["throughput"]);
        var indexes = Map(Map(examples.Inputs["resource"])["indexingPolicy"])["compositeIndexes"];
        var index = Assert.IsAssignableFrom<IEnumerable<object>>(Assert.Single(Assert.IsAssignableFrom<IEnumerable<object>>(indexes))).ToArray();
        Assert.Equal("/observation/TimestampUtc", Map(index[0])["path"]);
        Assert.Equal("descending", Map(index[1])["order"]);
        var grant = Assert.Single(mocks.Resources, r => r.Name == "existing-grant");
        Assert.Equal(AccountId + suffix, grant.Inputs["scope"]);
        Assert.Equal(AccountId + "/sqlRoleDefinitions/" + AzureCosmosConstruction.DataContributorRole, grant.Inputs["roleDefinitionId"]);
        Assert.Equal("9da6bc9c-60e4-4e3c-99cf-2fe700670ded", grant.Inputs["roleAssignmentId"]);
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.StartsWith("pulumi:providers:") == true);
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("lifecycle")]
    [InlineData("target")]
    [InlineData("incomplete")]
    [InlineData("physical-identity")]
    [InlineData("alias")]
    [InlineData("containers")]
    [InlineData("partition")]
    [InlineData("indexes")]
    [InlineData("throughput")]
    [InlineData("consistency")]
    [InlineData("provenance")]
    [InlineData("access")]
    [InlineData("binding")]
    [InlineData("tags")]
    [InlineData("location")]
    [InlineData("logical-name")]
    [InlineData("facility")]
    public async Task Invalid_input_registers_no_provider_resources(string code)
    {
        var policy = code switch
        {
            "subscription" => Policy() with { SubscriptionId = Guid.NewGuid() },
            "lifecycle" => Policy() with { LifecycleAuthority = new("other/owner") },
            "containers" => Policy() with { Containers = [Policy().Containers[0], Policy().Containers[0]] },
            "partition" => Policy() with { Containers = [Policy().Containers[0] with { PartitionKeyPath = "/*" }] },
            "indexes" => Policy() with { Containers = [Policy().Containers[0] with { CompositeIndexes = [[new("/a", "sideways")]] }] },
            "throughput" => Policy() with { Containers = [Policy().Containers[0] with { Throughput = 450 }] },
            "consistency" => Policy() with { Consistency = "BoundedStaleness" },
            "provenance" => Policy() with { SourceReferences = [] },
            "access" => Policy() with { Access = [] },
            "binding" => Policy() with { RepositoryContract = new("wrong/contract") },
            "location" => Policy() with { ResourceGroupName = "bad/group" },
            "logical-name" => Policy() with { DatabaseName = "" },
            "facility" => Policy() with { Resource = new("not/a/resource") },
            "tags" => Policy() with { Tags = ImmutableSortedDictionary<string, string>.Empty.Add("invalid/key", "value") },
            _ => Policy()
        };
        var plan = Plan(physical: code == "physical-identity" ? "wrong/physical" : Physical,
            target: code == "target" ? "wrong/target" : AzureCosmosConstruction.Target, incomplete: code == "incomplete", sharedAccount: code == "alias");
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions(), () =>
        {
            var error = Assert.Throws<AzureCosmosValidationException>(() => AzureCosmosConstruction.Register(plan, policy, Subscription));
            Assert.Contains(error.Diagnostics, d => d.Code == "azure.cosmos." + code);
            Assert.All(error.Diagnostics, d => Assert.NotNull(d.Evidence));
        });
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.StartsWith("pulumi:pulumi:") != true);
    }

    [Fact]
    public async Task Reordered_containers_preserve_inputs_parent_provider_and_group_dependency()
    {
        async Task<string[]> Construct(ImmutableArray<AzureCosmosContainerPolicy> containers)
        {
            var mocks = new Mocks();
            var checkedParents = 0;
            await Deployment.TestAsync(mocks, new TestOptions { IsPreview = false }, () =>
            {
                var group = new ComponentResource("test:index:Group", "group");
                var provider = new global::Pulumi.AzureNative.Provider("existing-provider", new() { SubscriptionId = Subscription.ToString("D") });
                ComponentResource? parent = null;
                parent = new ComponentResource("test:index:Parent", "existing-parent", new ComponentResourceOptions
                {
                    ResourceTransformations = { args =>
                    {
                        if (args.Resource is CustomResource)
                        {
                            Assert.Same(parent, args.Options.Parent);
                            Assert.Same(provider, ((CustomResourceOptions)args.Options).Provider);
                            checkedParents++;
                            if (args.Resource is DatabaseAccount)
                                ((Output<ImmutableArray<Resource>>)args.Options.DependsOn).Apply(ds => { Assert.Contains(group, ds); return ds; });
                        }
                        return null;
                    } }
                });
                AzureCosmosConstruction.Register(Plan(), Policy() with { Containers = containers }, Subscription, provider, parent, group);
            });
            Assert.Equal(4, checkedParents);
            return mocks.Resources.OrderBy(r => r.Name).Select(r => JsonSerializer.Serialize(new { r.Type, r.Name, r.Provider,
                Inputs = r.Inputs.OrderBy(i => i.Key).ToDictionary() })).ToArray();
        }
        Assert.Equal(await Construct(Policy().Containers), await Construct([.. Policy().Containers.Reverse()]));
    }

    [Fact]
    public async Task Nonparticipating_worker_requires_no_grant_and_cannot_receive_one()
    {
        var plan = Plan(nonparticipating: true);
        Assert.True(plan.IsComplete);
        Assert.Contains(AzureCosmosConstruction.Validate(plan, Policy(), Subscription), d => d.Code == "azure.cosmos.access");
        await Deployment.TestAsync(new Mocks(), new TestOptions(), () =>
        {
            var resources = AzureCosmosConstruction.Register(plan, Policy() with { Access = [] }, Subscription);
            Assert.Empty(resources.RepositoryBindings);
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Subscription, "principal", Guid.NewGuid()));
        });
    }

    [Fact]
    public void Policy_roundtrip_and_unsupported_scope_fail_closed()
    {
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(Policy(), options);
        var restored = JsonSerializer.Deserialize<AzureCosmosPolicy>(json, options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        var imported = JsonNode.Parse(json)!.AsObject();
        var accessKey = imported.First(p => p.Key.Equals("access", StringComparison.OrdinalIgnoreCase)).Key;
        var importedGrant = imported[accessKey]![0]!.AsObject();
        importedGrant.Remove(importedGrant.First(p => p.Key.Equals("scope", StringComparison.OrdinalIgnoreCase)).Key);
        var missingScope = JsonSerializer.Deserialize<AzureCosmosPolicy>(imported.ToJsonString(), options)!;
        Assert.Contains(AzureCosmosConstruction.Validate(Plan(), missingScope, Subscription), d => d.Code == "azure.cosmos.access");
        Assert.Empty(AzureCosmosConstruction.Validate(Plan(), restored, Subscription));
        foreach (var access in new[] { new AzureCosmosBindingAccess(Binding, (AzureCosmosScope)99),
            new(Binding, AzureCosmosScope.Container, "missing"), new(Binding, AzureCosmosScope.Database, "inbox") })
            Assert.Contains(AzureCosmosConstruction.Validate(Plan(), Policy() with { Access = [access] }, Subscription), d => d.Code == "azure.cosmos.access");
        Assert.Contains(AzureCosmosConstruction.Validate(Plan(external: true), Policy(), Subscription), d => d.Code == "azure.cosmos.lifecycle");
        Assert.Throws<OperationCanceledException>(() => AzureCosmosConstruction.Register(Plan(), Policy(), Subscription,
            cancellationToken: new CancellationToken(true)));
    }

    static IReadOnlyDictionary<string, object> Map(object value) => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(value);
    static InfrastructureTargetDeploymentPlan Plan(string physical = Physical,
        string target = AzureCosmosConstruction.Target, bool incomplete = false,
        bool nonparticipating = false, bool external = false, bool sharedAccount = false)
    {
        InfrastructureCapabilityId execution = new("test/execution");
        InfrastructureCapabilityId durable = new("test/repository");
        var semantic = Infrastructure.Define(new("test/cosmos"), new("1"), new("test/bindings/v1"), infra =>
        {
            var contract = infra.Contract(Contract, new("test/rule")).Requires(durable).SourcedFrom(Source.Value);
            var worker = infra.Workload(Worker).Requires(execution);
            if (incomplete) worker.Requires(new("test/unsupported"));
            var store = infra.Resource(Store).Requires(durable);
            if (external) store.External(); else store.Persistent();
            if (sharedAccount) infra.Resource(new("resources/other-database")).Persistent().Requires(durable);
            infra.Bind(Binding, worker).To(store).As(contract);
        });
        var facilities = InfrastructureTargetFacilities.Define(new("test/facilities/v1"), new("test/capabilities/v1"),
            new(target), new("test/production"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], facility =>
            {
                facility.Workload(new("test/worker")).Provides(new(new("test/execution/evidence"), execution,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
                facility.Resource(new(AzureCosmosConstruction.Facility)).Provides(new(new("test/cosmos/evidence"), durable,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment/v1"), semantic.Definition, facilities,
            deployment =>
            {
                if (nonparticipating)
                    deployment.NonParticipatingWorkload(Worker, "This environment hosts no worker.", [Source.Value]);
                else
                    deployment.Workload(Worker, new("test/worker"), new("test/workers/worker"), [Source]);
                deployment.Resource(Store, new(AzureCosmosConstruction.Facility), new(physical), Authority, [Source]);
                if (sharedAccount)
                    deployment.Resource(new("resources/other-database"), new(AzureCosmosConstruction.Facility),
                        new("azure/cosmos-db/accounts/test-account/databases/other-db"), Authority, [Source]);
            });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    sealed class Mocks : IMocks
    {
        public ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        public Task<object> CallAsync(MockCallArgs args) => throw new InvalidOperationException("No key or provider invokes expected.");
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args);
            var state = args.Inputs.ToDictionary();
            string id = args.Name + "-id";
            if (args.Type == "azure-native:cosmosdb:DatabaseAccount")
            {
                state["name"] = args.Inputs["accountName"];
                state["documentEndpoint"] = "https://cosmos.invalid/";
                id = AccountId;
            }
            if (args.Type == "azure-native:cosmosdb:SqlResourceSqlDatabase") state["name"] = args.Inputs["databaseName"];
            if (args.Type == "azure-native:cosmosdb:SqlResourceSqlContainer") state["name"] = args.Inputs["containerName"];
            return Task.FromResult<(string?, object)>((id, state));
        }
    }
}
