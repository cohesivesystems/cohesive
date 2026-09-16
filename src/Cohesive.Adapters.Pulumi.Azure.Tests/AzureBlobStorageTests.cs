using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.Storage;
using Pulumi.Testing;

namespace Cohesive.Adapters.Pulumi.Azure.Tests;

public sealed class AzureBlobStorageTests
{
    static readonly Guid Subscription = Guid.Parse("b6708815-d5b5-4070-af5b-675272a80b77");
    static readonly SourceReference Source = SourceReference.Create("test", "blob-policy");
    static readonly InfrastructureNodeId Worker = new("workloads/worker");
    static readonly InfrastructureNodeId Artifacts = new("resources/artifacts");
    static readonly InfrastructureNodeId Data = new("resources/data");
    static readonly InfrastructureNodeId Logs = new("resources/logs");
    static readonly InfrastructureBindingId ArtifactsBinding = new("bindings/worker/artifacts");
    static readonly InfrastructureBindingId DataBinding = new("bindings/worker/data");
    static readonly InfrastructureBindingContractId Contract = new("contracts/blob");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("pulumi/test/production");
    const string Prefix = "azure/storage/accounts/teststorage/blob-services/default/containers/";
    const string AccountId = "/subscriptions/b6708815-d5b5-4070-af5b-675272a80b77/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/teststorage";
    const string Endpoint = "https://teststorage.blob.core.windows.net/";

    static AzureBlobStoragePolicy Policy(AzureBlobScope scope = AzureBlobScope.Account) => new()
    {
        AccountOwner = Artifacts, LifecycleAuthority = Authority, BlobContract = Contract, SubscriptionId = Subscription,
        ResourceGroupName = "test-rg", Location = "westus", AccountName = "existing-storage",
        Containers = [new(Artifacts, "existing-artifacts"), new(Data, "existing-data"), new(Logs, "existing-logs")],
        Access = [new(ArtifactsBinding, scope), new(DataBinding, scope)], SourceReferences = [Source],
        Tags = ImmutableSortedDictionary<string, string>.Empty.Add("environment", "test")
    };

    [Fact]
    public async Task One_account_owns_complete_topology_and_coalesces_only_compatible_explicit_grants()
    {
        var plan = Plan();
        Assert.True(plan.IsComplete);
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions { IsPreview = false }, async () =>
        {
            var resources = AzureBlobStorageConstruction.Register(plan, Policy(), Subscription);
            Assert.Same(plan, resources.Deployment);
            Assert.Equal(3, resources.Containers.Count);
            Assert.Equal(2, resources.BlobBindings.Length);
            resources.ContainerEndpoint(Data).Apply(value => { Assert.Equal(Endpoint + "data", value); return value; });
            Assert.False(await Output.IsSecretAsync(resources.BlobEndpoint));
            var args = resources.AccessGrant([ArtifactsBinding, DataBinding], Subscription, Output.CreateSecret("principal"),
                Guid.Parse("9da6bc9c-60e4-4e3c-99cf-2fe700670ded"));
            Assert.True(await Output.IsSecretAsync((Output<string>)args.PrincipalId!));
            _ = new RoleAssignment("existing-grant", args);
            Assert.Throws<ArgumentException>(() => resources.AccessGrant([ArtifactsBinding], Guid.NewGuid(), "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant([ArtifactsBinding], Subscription, "principal", Guid.Empty));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant([], Subscription, "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant([ArtifactsBinding, ArtifactsBinding], Subscription, "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant([new("missing")], Subscription, "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.ContainerEndpoint(new("missing")));
        });
        var account = Assert.Single(mocks.Resources, r => r.Type == "azure-native:storage:StorageAccount");
        Assert.Equal("existing-storage", account.Name);
        Assert.Equal("teststorage", account.Inputs["accountName"]);
        Assert.Equal("StorageV2", account.Inputs["kind"]);
        Assert.Equal("Standard_LRS", Map(account.Inputs["sku"])["name"]);
        Assert.Equal(true, account.Inputs["enableHttpsTrafficOnly"]);
        Assert.Equal(false, account.Inputs["allowBlobPublicAccess"]);
        Assert.Equal("TLS1_2", account.Inputs["minimumTlsVersion"]);
        var containers = mocks.Resources.Where(r => r.Type == "azure-native:storage:BlobContainer").ToArray();
        Assert.Equal(3, containers.Length);
        Assert.All(containers, c => Assert.Equal("None", c.Inputs["publicAccess"]));
        Assert.All(containers, c => Assert.Equal("teststorage", c.Inputs["accountName"]));
        var grant = Assert.Single(mocks.Resources, r => r.Name == "existing-grant");
        Assert.Equal(AccountId, grant.Inputs["scope"]);
        Assert.Equal($"/subscriptions/{Subscription:D}/providers/Microsoft.Authorization/roleDefinitions/{AzureBlobStorageConstruction.DataContributorRole}", grant.Inputs["roleDefinitionId"]);
        Assert.Equal("9da6bc9c-60e4-4e3c-99cf-2fe700670ded", grant.Inputs["roleAssignmentName"]);
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.StartsWith("pulumi:providers:") == true);
    }

    [Fact]
    public async Task Container_grants_use_actual_management_resource_ids_and_cannot_be_widened_or_coalesced()
    {
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions(), () =>
        {
            var resources = AzureBlobStorageConstruction.Register(Plan(), Policy(AzureBlobScope.Container), Subscription);
            _ = new RoleAssignment("container-grant", resources.AccessGrant([DataBinding], Subscription, "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant([ArtifactsBinding, DataBinding], Subscription, "principal", Guid.NewGuid()));
        });
        Assert.Equal(AccountId + "/blobServices/default/containers/data",
            Assert.Single(mocks.Resources, r => r.Name == "container-grant").Inputs["scope"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_workloads_or_scope_decisions_cannot_be_coalesced(bool otherWorker)
    {
        var policy = otherWorker ? Policy() : Policy() with
        {
            Access = [new(ArtifactsBinding, AzureBlobScope.Account), new(DataBinding, AzureBlobScope.Container)]
        };
        await Deployment.TestAsync(new Mocks(), new TestOptions(), () =>
        {
            var resources = AzureBlobStorageConstruction.Register(Plan(otherWorker: otherWorker), policy, Subscription);
            Assert.Throws<ArgumentException>(() => resources.AccessGrant([ArtifactsBinding, DataBinding], Subscription, "principal", Guid.NewGuid()));
        });
    }

    [Fact]
    public async Task Provider_endpoint_secret_classification_is_preserved_without_reading_credentials()
    {
        await Deployment.TestAsync(new Mocks(secretEndpoint: true), new TestOptions(), async () =>
        {
            var resources = AzureBlobStorageConstruction.Register(Plan(), Policy(), Subscription);
            Assert.True(await Output.IsSecretAsync(resources.BlobEndpoint));
            Assert.True(await Output.IsSecretAsync(resources.ContainerEndpoint(Data)));
        });
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("lifecycle")]
    [InlineData("target")]
    [InlineData("incomplete")]
    [InlineData("physical-identity")]
    [InlineData("account")]
    [InlineData("containers")]
    [InlineData("provenance")]
    [InlineData("access")]
    [InlineData("binding")]
    [InlineData("tags")]
    [InlineData("location")]
    [InlineData("logical-name")]
    [InlineData("facility")]
    public async Task Invalid_policy_or_plan_registers_nothing(string code)
    {
        var policy = code switch
        {
            "subscription" => Policy() with { SubscriptionId = Guid.NewGuid() },
            "lifecycle" => Policy() with { LifecycleAuthority = new("other/owner") },
            "account" => Policy() with { Containers = [Policy().Containers[0], Policy().Containers[1]] },
            "containers" => Policy() with { Containers = [.. Policy().Containers, Policy().Containers[0]] },
            "provenance" => Policy() with { SourceReferences = [] },
            "access" => Policy() with { Access = [new(ArtifactsBinding, AzureBlobScope.Unspecified)] },
            "binding" => Policy() with { BlobContract = new("contracts/queue") },
            "tags" => Policy() with { Tags = ImmutableSortedDictionary<string, string>.Empty.Add("bad/tag", "value") },
            "location" => Policy() with { Location = "" },
            "logical-name" => Policy() with { AccountName = "" },
            "facility" => Policy() with { AccountOwner = new("absent") },
            _ => Policy()
        };
        var plan = Plan(physical: code == "physical-identity" ? Prefix + "bad--container" : Prefix + "data",
            target: code == "target" ? "unsupported/target" : AzureBlobStorageConstruction.Target,
            incomplete: code == "incomplete");
        Assert.Contains(AzureBlobStorageConstruction.Validate(plan, policy, Subscription), d => d.Code == "azure.blob-storage." + code);
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions(), () =>
        {
            var error = Assert.Throws<AzureBlobStorageValidationException>(() => AzureBlobStorageConstruction.Register(plan, policy, Subscription));
            Assert.NotEmpty(error.Diagnostics);
            Assert.All(error.Diagnostics.Where(d => d.Code.StartsWith("azure.blob-storage.")), d => Assert.NotNull(d.Evidence));
        });
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.StartsWith("azure-native:") == true);
    }

    [Theory]
    [InlineData("conflicting-owner")]
    [InlineData("external")]
    [InlineData("alias")]
    [InlineData("other-account")]
    [InlineData("queue")]
    [InlineData("table")]
    public void Conflicting_ownership_aliases_and_nonblob_account_members_fail_closed(string scenario)
    {
        var physical = scenario switch
        {
            "alias" => Prefix + "artifacts",
            "other-account" => Prefix.Replace("teststorage", "otherstorage") + "data",
            "queue" => "azure/storage/accounts/teststorage/queue-services/default/queues/data",
            "table" => "azure/storage/accounts/teststorage/table-services/default/tables/data",
            _ => Prefix + "data"
        };
        var errors = AzureBlobStorageConstruction.Validate(Plan(physical, external: scenario == "external",
            conflict: scenario == "conflicting-owner"), Policy(), Subscription);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Code == "azure.blob-storage." +
            (scenario is "conflicting-owner" or "external" ? "lifecycle" : scenario is "queue" or "table" ? "facility" : "account"));
    }

    [Theory]
    [InlineData("contracts/queue")]
    [InlineData("contracts/table")]
    public void Complete_plan_does_not_allow_unknown_service_contracts_to_be_treated_as_blob_access(string contract)
    {
        var plan = Plan(otherContract: contract);
        Assert.True(plan.IsComplete);
        Assert.Contains(AzureBlobStorageConstruction.Validate(plan, Policy(), Subscription), d => d.Code == "azure.blob-storage.binding");
    }

    [Fact]
    public async Task Reordering_policy_preserves_inputs_parent_provider_and_dependency_edges()
    {
        async Task<string[]> Construct(ImmutableArray<AzureBlobContainerPolicy> containers)
        {
            var mocks = new Mocks(); var checkedParents = 0;
            await Deployment.TestAsync(mocks, new TestOptions(), () =>
            {
                var provider = new global::Pulumi.AzureNative.Provider("host-provider", new() { SubscriptionId = Subscription.ToString() });
                var group = new global::Pulumi.AzureNative.Resources.ResourceGroup("host-group", new() { ResourceGroupName = "test-rg" });
                ComponentResource? parent = null;
                parent = new ComponentResource("test:Parent", "host-parent", new ComponentResourceOptions
                {
                    ResourceTransformations = { args =>
                    {
                        if (args.Resource is CustomResource)
                        {
                            Assert.Same(provider, ((CustomResourceOptions)args.Options).Provider);
                            Assert.Same(parent, args.Options.Parent);
                            checkedParents++;
                            if (args.Resource is StorageAccount)
                                ((Output<ImmutableArray<Resource>>)args.Options.DependsOn).Apply(ds => { Assert.Contains(group, ds); return ds; });
                        }
                        return null;
                    } }
                });
                var facility = AzureBlobStorageConstruction.Register(Plan(), Policy() with { Containers = containers }, Subscription, provider, parent, group);
                Assert.Equal(3, facility.Containers.Count);
            });
            Assert.Equal(4, checkedParents);
            return mocks.Resources.OrderBy(r => r.Name).Select(r => JsonSerializer.Serialize(new { r.Type, r.Name, r.Provider,
                Inputs = r.Inputs.OrderBy(i => i.Key).ToDictionary() })).ToArray();
        }
        Assert.Equal(await Construct(Policy().Containers), await Construct([.. Policy().Containers.Reverse()]));
    }

    [Fact]
    public async Task Explicit_nonparticipation_and_unbound_containers_do_not_infer_grants()
    {
        var plan = Plan(nonparticipating: true);
        Assert.True(plan.IsComplete);
        Assert.Contains(AzureBlobStorageConstruction.Validate(plan, Policy(), Subscription), d => d.Code == "azure.blob-storage.access");
        await Deployment.TestAsync(new Mocks(), new TestOptions(), () =>
        {
            var resources = AzureBlobStorageConstruction.Register(plan, Policy() with { Access = [] }, Subscription);
            Assert.Empty(resources.BlobBindings);
            Assert.Equal(3, resources.Containers.Count);
            Assert.Throws<ArgumentException>(() => resources.AccessGrant([ArtifactsBinding], Subscription, "principal", Guid.NewGuid()));
        });
    }

    [Fact]
    public void Json_roundtrip_missing_scope_and_cancellation_are_explicit()
    {
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(Policy(), options);
        var restored = JsonSerializer.Deserialize<AzureBlobStoragePolicy>(json, options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Empty(AzureBlobStorageConstruction.Validate(Plan(), restored, Subscription));
        var node = JsonNode.Parse(json)!.AsObject();
        var access = node.First(p => p.Key.Equals("access", StringComparison.OrdinalIgnoreCase)).Value!.AsArray();
        var grant = access[0]!.AsObject();
        grant.Remove(grant.First(p => p.Key.Equals("scope", StringComparison.OrdinalIgnoreCase)).Key);
        Assert.Contains(AzureBlobStorageConstruction.Validate(Plan(), JsonSerializer.Deserialize<AzureBlobStoragePolicy>(node.ToJsonString(), options)!, Subscription), d => d.Code == "azure.blob-storage.access");
        Assert.Throws<OperationCanceledException>(() => AzureBlobStorageConstruction.Register(Plan(), Policy(), Subscription,
            cancellationToken: new CancellationToken(true)));
    }

    static IReadOnlyDictionary<string, object> Map(object value) => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(value);

    static InfrastructureTargetDeploymentPlan Plan(string physical = Prefix + "data", string target = AzureBlobStorageConstruction.Target,
        bool incomplete = false, bool nonparticipating = false, bool external = false, bool conflict = false, bool otherWorker = false, string? otherContract = null)
    {
        InfrastructureCapabilityId execution = new("test/execution"), durable = new("test/blobs");
        var semantic = Infrastructure.Define(new("test/blob-storage"), new("1"), new("test/bindings/v1"), infra =>
        {
            var contract = infra.Contract(Contract, new("test/rule")).Requires(durable).SourcedFrom(Source.Value);
            var dataContract = otherContract is null ? contract : infra.Contract(new(otherContract), new("test/other-rule")).Requires(durable).SourcedFrom(Source.Value);
            var worker = infra.Workload(Worker).Requires(execution);
            if (incomplete) worker.Requires(new("test/unsupported"));
            var artifacts = infra.Resource(Artifacts).Persistent().Requires(durable);
            var data = infra.Resource(Data).Requires(durable);
            if (external) data.External(); else data.Persistent();
            infra.Resource(Logs).Persistent().Requires(durable);
            infra.Bind(ArtifactsBinding, worker).To(artifacts).As(contract);
            if (otherWorker)
                infra.Bind(DataBinding, infra.Workload(new("workloads/other")).Requires(execution)).To(data).As(dataContract);
            else infra.Bind(DataBinding, worker).To(data).As(dataContract);
        });
        var facilities = InfrastructureTargetFacilities.Define(new("test/facilities/v1"), new("test/capabilities/v1"),
            new(target), new("test/production"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], facility =>
            {
                facility.Workload(new("test/worker")).Provides(new(new("test/execution/evidence"), execution, CapabilityRealizationKind.Native, sourceReferences: [Source]));
                facility.Resource(new(AzureBlobStorageConstruction.Facility)).Provides(new(new("test/blob/evidence"), durable, CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment/v1"), semantic.Definition, facilities, deployment =>
        {
            if (nonparticipating) deployment.NonParticipatingWorkload(Worker, "No worker in this environment.", [Source.Value]);
            else deployment.Workload(Worker, new("test/worker"), new("test/workers/worker"), [Source]);
            if (otherWorker) deployment.Workload(new("workloads/other"), new("test/worker"), new("test/workers/other"), [Source]);
            deployment.Resource(Artifacts, new(AzureBlobStorageConstruction.Facility), new(Prefix + "artifacts"), Authority, [Source]);
            deployment.Resource(Data, new(AzureBlobStorageConstruction.Facility), new(physical), conflict ? new("pulumi/other/owner") : Authority, [Source]);
            deployment.Resource(Logs, new(AzureBlobStorageConstruction.Facility), new(Prefix + "logs"), Authority, [Source]);
        });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    sealed class Mocks(bool secretEndpoint = false) : IMocks
    {
        public ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        public Task<object> CallAsync(MockCallArgs args) => throw new InvalidOperationException("No key or provider invokes expected.");
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args);
            var state = args.Inputs.ToDictionary(); string id = args.Name + "-id";
            if (args.Type == "azure-native:storage:StorageAccount")
            {
                state["name"] = args.Inputs["accountName"];
                var endpoints = new Dictionary<string, object> { ["blob"] = Endpoint };
                state["primaryEndpoints"] = secretEndpoint ? Output.CreateSecret(endpoints) : endpoints;
                id = AccountId;
            }
            if (args.Type == "azure-native:storage:BlobContainer")
            {
                state["name"] = args.Inputs["containerName"];
                id = AccountId + "/blobServices/default/containers/" + args.Inputs["containerName"];
            }
            return Task.FromResult<(string?, object)>((id, state));
        }
    }
}
