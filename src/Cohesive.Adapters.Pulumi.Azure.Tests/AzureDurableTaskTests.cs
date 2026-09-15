using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Pulumi.Azure;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.Authorization;
using Pulumi.Testing;

namespace Cohesive.Adapters.Pulumi.Azure.Tests;

public sealed class AzureDurableTaskTests
{
    static readonly Guid Subscription = Guid.Parse("b6708815-d5b5-4070-af5b-675272a80b77");
    static readonly SourceReference Source = SourceReference.Create("test", "durable-task-policy");
    static readonly InfrastructureNodeId Worker = new("workloads/worker");
    static readonly InfrastructureNodeId Scheduler = new("resources/scheduler");
    static readonly InfrastructureBindingId Binding = new("bindings/worker/scheduler");
    static readonly InfrastructureBindingContractId Contract = new("contracts/durable-worker");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("pulumi/test/production");
    const string Physical = "azure/durable-task/schedulers/test-scheduler/task-hubs/test-hub";
    const string HubId = "/subscriptions/b6708815-d5b5-4070-af5b-675272a80b77/resourceGroups/test-rg/providers/Microsoft.DurableTask/schedulers/test-scheduler/taskHubs/test-hub";

    static AzureDurableTaskPolicy Policy() => new()
    {
        Resource = Scheduler, WorkerContract = Contract, LifecycleAuthority = Authority,
        SubscriptionId = Subscription, ResourceGroupName = "test-rg", Location = "westus",
        ProviderName = "existing-durable-provider", SchedulerName = "existing-scheduler", TaskHubName = "existing-hub",
        IpAllowlist = ["192.0.2.0/24"], SourceReferences = [Source],
        Tags = ImmutableSortedDictionary<string, string>.Empty.Add("environment", "test")
    };

    [Fact]
    public async Task Constructs_pinned_resources_and_hub_scoped_grant_with_preserved_names_and_provenance()
    {
        var plan = Plan();
        Assert.True(plan.IsComplete);
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions { IsPreview = false }, async () =>
        {
            var resources = AzureDurableTaskConstruction.Register(plan, Policy(), Subscription);
            Assert.Same(plan, resources.Deployment);
            Assert.Equal(Binding, Assert.Single(resources.WorkerBindings).Id);
            resources.ConnectionString.Apply(value =>
            {
                Assert.Equal("Endpoint=https://scheduler.example;TaskHub=test-hub;Authentication=ManagedIdentity", value);
                return value;
            });
            Assert.False(await Output.IsSecretAsync(resources.ConnectionString));
            _ = new RoleAssignment("existing-worker-grant", resources.AccessGrant(Binding, Subscription,
                Output.CreateSecret("principal-id"), Guid.Parse("9da6bc9c-60e4-4e3c-99cf-2fe700670ded")));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Guid.NewGuid(), "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(new("unknown"), Subscription, "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Subscription, "principal", Guid.Empty));
        });
        var provider = Assert.Single(mocks.Resources, r => r.Name == "existing-durable-provider");
        Assert.Equal(Subscription.ToString("D"), provider.Inputs["subscriptionId"]);
        var scheduler = Assert.Single(mocks.Resources, r => r.Name == "existing-scheduler");
        Assert.Equal("azure-native_durabletask_v20251101:durabletask:Scheduler", scheduler.Type);
        Assert.Equal("test-scheduler", scheduler.Inputs["schedulerName"]);
        Assert.Equal("test", Assert.IsAssignableFrom<ImmutableDictionary<string, object>>(scheduler.Inputs["tags"])["environment"]);
        Assert.Contains("existing-durable-provider", scheduler.Provider);
        var hub = Assert.Single(mocks.Resources, r => r.Name == "existing-hub");
        Assert.Equal("test-hub", hub.Inputs["taskHubName"]);
        Assert.Equal(scheduler.Provider, hub.Provider);
        var grant = Assert.Single(mocks.Resources, r => r.Name == "existing-worker-grant");
        Assert.Equal(HubId, grant.Inputs["scope"]);
        Assert.Equal($"/subscriptions/{Subscription:D}/providers/Microsoft.Authorization/roleDefinitions/{AzureDurableTaskConstruction.DataContributorRole}",
            grant.Inputs["roleDefinitionId"]);
        Assert.Equal("9da6bc9c-60e4-4e3c-99cf-2fe700670ded", grant.Inputs["roleAssignmentName"]);
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("lifecycle")]
    [InlineData("network")]
    [InlineData("provenance")]
    [InlineData("binding")]
    [InlineData("physical-identity")]
    [InlineData("target")]
    [InlineData("incomplete")]
    [InlineData("alias")]
    [InlineData("tags")]
    public async Task Invalid_plans_register_nothing(string failure)
    {
        var policy = failure switch
        {
            "subscription" => Policy() with { SubscriptionId = Guid.NewGuid() },
            "lifecycle" => Policy() with { LifecycleAuthority = new("pulumi/other/stack") },
            "network" => Policy() with { IpAllowlist = ["::/0"] },
            "provenance" => Policy() with { SourceReferences = [] },
            "binding" => Policy() with { WorkerContract = new("unsupported/contract") },
            "tags" => Policy() with { Tags = ImmutableSortedDictionary<string, string>.Empty.Add("invalid/key", "value") },
            _ => Policy()
        };
        var plan = Plan(physical: failure == "physical-identity" ? "azure/durable-task/wrong" : Physical,
            target: failure == "target" ? "other-target" : AzureDurableTaskConstruction.Target,
            incomplete: failure == "incomplete", sharedScheduler: failure == "alias");
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions(), () =>
        {
            var error = Assert.Throws<AzureDurableTaskValidationException>(() =>
                AzureDurableTaskConstruction.Register(plan, policy, Subscription));
            Assert.Contains(error.Diagnostics, d => d.Code == "azure.durable-task." + failure);
            Assert.All(error.Diagnostics, d => Assert.NotNull(d.Evidence));
        });
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.StartsWith("pulumi:pulumi:") != true);
    }

    [Fact]
    public async Task Reordering_network_rules_preserves_construction_and_parent_options()
    {
        async Task<string[]> Construct(string[] rules)
        {
            var mocks = new Mocks();
            var checkedParents = 0;
            await Deployment.TestAsync(mocks, new TestOptions { IsPreview = false }, () =>
            {
                var resourceGroup = new ComponentResource("test:index:ResourceGroup", "existing-resource-group");
                ComponentResource? parent = null;
                parent = new ComponentResource("test:index:Existing", "existing-parent", new ComponentResourceOptions
                {
                    ResourceTransformations =
                    {
                        args =>
                        {
                            if (args.Resource is CustomResource)
                            {
                                Assert.Same(parent, args.Options.Parent);
                                checkedParents++;
                                if (args.Resource.GetType().Name == "Scheduler")
                                    ((Output<ImmutableArray<Resource>>)args.Options.DependsOn).Apply(dependencies =>
                                    {
                                        Assert.Contains(resourceGroup, dependencies);
                                        return dependencies;
                                    });
                            }
                            return null;
                        }
                    }
                });
                AzureDurableTaskConstruction.Register(Plan(), Policy() with { IpAllowlist = [.. rules] }, Subscription, parent, resourceGroup);
            });
            Assert.Equal(3, checkedParents);
            return mocks.Resources.OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => JsonSerializer.Serialize(new { r.Type, r.Name, r.Provider,
                    Inputs = r.Inputs.OrderBy(i => i.Key, StringComparer.Ordinal).ToDictionary() })).ToArray();
        }
        Assert.Equal(await Construct(["192.0.2.0/24", "198.51.100.0/24"]),
            await Construct(["198.51.100.0/24", "192.0.2.0/24", "192.0.2.0/24"]));
    }

    [Fact]
    public async Task Nonparticipating_workers_receive_no_access_grant()
    {
        var plan = Plan(nonparticipating: true);
        Assert.True(plan.IsComplete);
        await Deployment.TestAsync(new Mocks(), new TestOptions(), () =>
        {
            var resources = AzureDurableTaskConstruction.Register(plan, Policy(), Subscription);
            Assert.Empty(resources.WorkerBindings);
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Subscription, "principal", Guid.NewGuid()));
        });
    }

    [Fact]
    public void External_lifecycle_cannot_be_accidentally_managed()
    {
        Assert.Contains(AzureDurableTaskConstruction.Validate(Plan(external: true), Policy(), Subscription),
            d => d.Code == "azure.durable-task.lifecycle");
    }

    [Fact]
    public void Cancellation_happens_before_registration_without_a_Pulumi_runtime()
    {
        Assert.Throws<OperationCanceledException>(() => AzureDurableTaskConstruction.Register(
            Plan(), Policy(), Subscription, cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void Policy_roundtrip_preserves_exact_scope_and_explicit_network_configuration()
    {
        var options = StrictDocumentJson.CreateOptions();
        var policy = Policy();
        var json = JsonSerializer.Serialize(policy, options);
        var restored = JsonSerializer.Deserialize<AzureDurableTaskPolicy>(json, options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Empty(AzureDurableTaskConstruction.Validate(Plan(), restored, Subscription));
        Assert.Empty(AzureDurableTaskConstruction.Validate(Plan(), policy with { IpAllowlist = [] }, Subscription));
        Assert.Contains(AzureDurableTaskConstruction.Validate(Plan(), policy with { IpAllowlist = default }, Subscription),
            d => d.Code == "azure.durable-task.network");
    }

    static InfrastructureTargetDeploymentPlan Plan(string physical = Physical,
        string target = AzureDurableTaskConstruction.Target, bool incomplete = false,
        bool nonparticipating = false, bool external = false, bool sharedScheduler = false)
    {
        InfrastructureCapabilityId execution = new("test/execution");
        InfrastructureCapabilityId durable = new("test/task-hub");
        var semantic = Infrastructure.Define(new("test/durable"), new("1"), new("test/bindings/v1"), infra =>
        {
            var contract = infra.Contract(Contract, new("test/rule")).Requires(durable).SourcedFrom(Source.Value);
            var worker = infra.Workload(Worker).Requires(execution);
            if (incomplete) worker.Requires(new("test/unsupported"));
            var scheduler = infra.Resource(Scheduler).Requires(durable);
            if (external) scheduler.External(); else scheduler.Persistent();
            if (sharedScheduler) infra.Resource(new("resources/other-hub")).Persistent().Requires(durable);
            infra.Bind(Binding, worker).To(scheduler).As(contract);
        });
        var facilities = InfrastructureTargetFacilities.Define(new("test/facilities/v1"), new("test/capabilities/v1"),
            new(target), new("test/production"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], facility =>
            {
                facility.Workload(new("test/worker")).Provides(new(new("test/execution/evidence"), execution,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
                facility.Resource(new(AzureDurableTaskConstruction.Facility)).Provides(new(new("test/durable/evidence"), durable,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment/v1"), semantic.Definition, facilities,
            deployment =>
            {
                if (nonparticipating)
                    deployment.NonParticipatingWorkload(Worker, "This environment hosts no worker.", [Source.Value]);
                else
                    deployment.Workload(Worker, new("test/worker"), new("test/workers/worker"), [Source]);
                deployment.Resource(Scheduler, new(AzureDurableTaskConstruction.Facility), new(physical), Authority, [Source]);
                if (sharedScheduler)
                    deployment.Resource(new("resources/other-hub"), new(AzureDurableTaskConstruction.Facility),
                        new("azure/durable-task/schedulers/test-scheduler/task-hubs/other-hub"), Authority, [Source]);
            });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    sealed class Mocks : IMocks
    {
        public ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        public Task<object> CallAsync(MockCallArgs args) => throw new InvalidOperationException("No provider invokes are expected.");
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args);
            var state = args.Inputs.ToDictionary();
            if (args.Type?.EndsWith(":Scheduler") == true)
            {
                state["name"] = args.Inputs["schedulerName"];
                state["properties"] = new Dictionary<string, object> { ["endpoint"] = "https://scheduler.example" };
            }
            if (args.Type?.EndsWith(":TaskHub") == true) state["name"] = args.Inputs["taskHubName"];
            return Task.FromResult<(string?, object)>((args.Type?.EndsWith(":TaskHub") == true ? HubId : args.Name + "-id", state));
        }
    }
}
