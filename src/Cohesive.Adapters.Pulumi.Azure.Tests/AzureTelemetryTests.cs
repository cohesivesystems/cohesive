using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Pulumi.Azure;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.ApplicationInsights;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.Testing;

namespace Cohesive.Adapters.Pulumi.Azure.Tests;

public sealed class AzureTelemetryTests
{
    static readonly Guid Subscription = Guid.Parse("b6708815-d5b5-4070-af5b-675272a80b77");
    static readonly SourceReference Source = SourceReference.Create("test", "telemetry-policy");
    static readonly InfrastructureNodeId Worker = new("workloads/worker");
    static readonly InfrastructureNodeId Telemetry = new("resources/telemetry");
    static readonly InfrastructureNodeId Store = new("resources/telemetry-store");
    static readonly InfrastructureBindingId Binding = new("bindings/worker/telemetry");
    static readonly InfrastructureBindingContractId Contract = new("contracts/telemetry-export");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("pulumi/test/production");
    const string ComponentPhysical = "azure/application-insights/components/test-insights";
    const string WorkspacePhysical = "azure/log-analytics/workspaces/test-workspace";
    const string Prefix = "/subscriptions/b6708815-d5b5-4070-af5b-675272a80b77/resourceGroups/test-rg/providers/";
    const string ComponentId = Prefix + "Microsoft.Insights/components/test-insights";
    const string WorkspaceId = Prefix + "Microsoft.OperationalInsights/workspaces/test-workspace";
    const string Connection = "InstrumentationKey=test-only-marker;IngestionEndpoint=https://example.invalid/";
    static AzureTelemetryPolicy Policy() => new()
    {
        Component = Telemetry, Workspace = Store, ExportContract = Contract, LifecycleAuthority = Authority,
        SubscriptionId = Subscription, SourceReferences = [Source]
    };

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 90)]
    public async Task Native_configuration_options_linkage_and_secret_consumer_output_are_preserved(bool providerSecret, int retention)
    {
        var plan = Plan(); var policy = Policy(); var mocks = new Mocks(providerSecret: providerSecret);
        Assert.True(plan.IsComplete);
        var checkedDependencies = false;
        await Deployment.TestAsync(mocks, new TestOptions { IsPreview = false }, async () =>
        {
            var dependency = new ComponentResource("test:index:Dependency", "existing-dependency");
            ComponentResource? parent = null;
            parent = new ComponentResource("test:index:Parent", "existing-parent", new ComponentResourceOptions
            {
                ResourceTransformations = { args =>
                {
                    if (args.Resource is Workspace or Component)
                    {
                        Assert.Same(parent, args.Options.Parent);
                        Assert.True(args.Options.Protect);
                        ((Output<ImmutableArray<Resource>>)args.Options.DependsOn).Apply(items =>
                        { Assert.Contains(dependency, items); checkedDependencies = true; return items; });
                    }
                    return null;
                } }
            });
            var provider = new global::Pulumi.AzureNative.Provider("existing-provider", new() { SubscriptionId = Subscription.ToString("D") });
            var (workspace, component) = Native(plan, policy, retention,
                new() { Parent = parent, Provider = provider, DependsOn = { dependency }, Protect = true });
            var resources = AzureTelemetryBinding.Attach(plan, policy, Subscription, workspace, component);
            Assert.Same(plan, resources.Deployment); Assert.Same(policy, resources.Policy);
            Assert.Same(workspace, resources.Workspace); Assert.Same(component, resources.Component);
            Assert.Equal(Binding, Assert.Single(resources.ExportBindings).Id);
            Assert.True(await Output.IsSecretAsync(resources.ConnectionString(Binding)));
            resources.ConnectionString(Binding).Apply(value => { Assert.Equal(Connection, value); return value; });
            resources.WorkspaceId.Apply(value => { Assert.Equal(WorkspaceId, value); return value; });
            resources.ComponentId.Apply(value => { Assert.Equal(ComponentId, value); return value; });
            _ = new global::Pulumi.AzureNative.Web.WebApp("consumer", new()
            {
                Name = "consumer", ResourceGroupName = "test-rg", SiteConfig = new global::Pulumi.AzureNative.Web.Inputs.SiteConfigArgs
                { AppSettings = { new global::Pulumi.AzureNative.Web.Inputs.NameValuePairArgs
                    { Name = "APPLICATIONINSIGHTS_CONNECTION_STRING", Value = resources.ConnectionString(Binding) } } }
            });
            Assert.Throws<ArgumentException>(() => resources.ConnectionString(new("missing")));
        });
        Assert.True(checkedDependencies);
        var workspaceArgs = Assert.Single(mocks.Resources, r => r.Type == "azure-native:operationalinsights:Workspace");
        var componentArgs = Assert.Single(mocks.Resources, r => r.Type == "azure-native:applicationinsights:Component");
        Assert.Equal("existing-workspace", workspaceArgs.Name);
        Assert.Equal("existing-insights", componentArgs.Name);
        Assert.Contains("existing-provider", workspaceArgs.Provider);
        Assert.Equal(workspaceArgs.Provider, componentArgs.Provider);
        Assert.Equal(retention, Convert.ToInt32(workspaceArgs.Inputs["retentionInDays"]));
        Assert.Equal(retention, Convert.ToInt32(componentArgs.Inputs["retentionInDays"]));
        Assert.Equal(WorkspaceId, componentArgs.Inputs["workspaceResourceId"]);
        Assert.Equal("web", componentArgs.Inputs["applicationType"]);
        Assert.Equal("Disabled", componentArgs.Inputs["publicNetworkAccessForQuery"]);
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.Contains("Extension", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("lifecycle")]
    [InlineData("provenance")]
    [InlineData("export-contract")]
    [InlineData("binding")]
    [InlineData("direction")]
    [InlineData("consumer")]
    [InlineData("physical-identity")]
    [InlineData("workspace-physical")]
    [InlineData("target")]
    [InlineData("incomplete")]
    [InlineData("alias")]
    [InlineData("workspace-dependency")]
    [InlineData("workspace-binding")]
    [InlineData("workspace-owner")]
    [InlineData("facility")]
    public async Task Invalid_semantics_fail_before_native_registration(string failure)
    {
        var policy = failure switch
        {
            "subscription" => Policy() with { SubscriptionId = Guid.NewGuid() },
            "lifecycle" => Policy() with { LifecycleAuthority = new("other/owner") },
            "provenance" => Policy() with { SourceReferences = [] },
            "export-contract" => Policy() with { ExportContract = default },
            "binding" => Policy() with { ExportContract = new("unsupported/contract") },
            "facility" => Policy() with { Workspace = Telemetry },
            _ => Policy()
        };
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions(), () =>
        {
            var error = Assert.Throws<AzureTelemetryValidationException>(() => AzureTelemetryBinding.Names(Plan(failure), policy, Subscription));
            var code = failure switch { "workspace-owner" => "lifecycle", "workspace-physical" => "physical-identity", "direction" => "binding", _ => failure };
            Assert.Contains(error.Diagnostics, d => d.Code == "azure.telemetry." + code);
            Assert.All(error.Diagnostics, d => Assert.NotNull(d.Evidence));
        });
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.StartsWith("pulumi:pulumi:") != true);
    }

    [Theory]
    [InlineData("component-name")]
    [InlineData("workspace-name")]
    [InlineData("component-subscription")]
    [InlineData("workspace-subscription")]
    [InlineData("component-kind")]
    [InlineData("workspace-kind")]
    [InlineData("link")]
    [InlineData("connection")]
    public async Task Resolved_mismatches_fault_checked_outputs_without_leaking_credentials(string mismatch)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Deployment.TestAsync(new Mocks(mismatch),
            new TestOptions { IsPreview = false }, () =>
            {
                var (workspace, component) = Native(Plan(), Policy());
                var resources = AzureTelemetryBinding.Attach(Plan(), Policy(), Subscription, workspace, component);
                resources.ConnectionString(Binding).Apply(value => value);
            }));
        Assert.Contains(mismatch == "connection" ? "no connection string" : "canonical association", error.ToString());
        Assert.DoesNotContain(Connection, error.ToString());
    }

    [Fact]
    public async Task Nonparticipating_consumers_are_excluded_and_association_registers_nothing()
    {
        var plan = Plan(nonparticipating: true); var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions(), () =>
        {
            var (workspace, component) = Native(plan, Policy());
            var resources = AzureTelemetryBinding.Attach(plan, Policy(), Subscription, workspace, component);
            Assert.Empty(resources.ExportBindings);
            Assert.Throws<ArgumentException>(() => resources.ConnectionString(Binding));
        });
        Assert.Equal(2, mocks.Resources.Count(r => r.Type?.StartsWith("azure-native:") == true));
    }

    [Fact]
    public async Task Every_participating_exporter_gets_the_same_classified_output_in_canonical_order()
    {
        var plan = Plan(multipleConsumers: true);
        await Deployment.TestAsync(new Mocks(), new TestOptions { IsPreview = false }, () =>
        {
            var (workspace, component) = Native(plan, Policy());
            var resources = AzureTelemetryBinding.Attach(plan, Policy(), Subscription, workspace, component);
            Assert.Equal(new[] { "bindings/another/telemetry", Binding.Value }, resources.ExportBindings.Select(b => b.Id.Value));
            Assert.Same(resources.ConnectionString(resources.ExportBindings[0].Id), resources.ConnectionString(Binding));
        });
    }

    [Fact]
    public async Task Unknown_preview_connection_remains_unknown_and_secret()
    {
        var resolved = false;
        await Deployment.TestAsync(new Mocks("unknown"), new TestOptions { IsPreview = true }, async () =>
        {
            var (workspace, component) = Native(Plan(), Policy());
            var resources = AzureTelemetryBinding.Attach(Plan(), Policy(), Subscription, workspace, component);
            Assert.True(await Output.IsSecretAsync(resources.ConnectionString(Binding)));
            resources.ConnectionString(Binding).Apply(value => { resolved = true; return value; });
        });
        Assert.False(resolved);
    }

    [Fact]
    public void Policy_is_portable_and_cancellation_precedes_association()
    {
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(Policy(), options);
        var restored = JsonSerializer.Deserialize<AzureTelemetryPolicy>(json, options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Empty(AzureTelemetryBinding.Validate(Plan(), restored, Subscription));
        Assert.Throws<OperationCanceledException>(() => AzureTelemetryBinding.Attach(Plan(), Policy(), Subscription, null!, null!, new(true)));
    }

    static (Workspace, Component) Native(InfrastructureTargetDeploymentPlan plan, AzureTelemetryPolicy policy,
        int retention = 30, CustomResourceOptions? options = null)
    {
        var names = AzureTelemetryBinding.Names(plan, policy, Subscription);
        var workspace = new Workspace("existing-workspace", new()
        {
            WorkspaceName = names.Workspace, ResourceGroupName = "test-rg", Location = "westus", RetentionInDays = retention,
            Sku = new global::Pulumi.AzureNative.OperationalInsights.Inputs.WorkspaceSkuArgs { Name = "PerGB2018" },
            Tags = { ["policy"] = "native" }
        }, options);
        var component = new Component("existing-insights", new()
        {
            ResourceName = names.Component, ResourceGroupName = "test-rg", Location = "westus", ApplicationType = "web", Kind = "web",
            WorkspaceResourceId = workspace.Id, RetentionInDays = retention, PublicNetworkAccessForQuery = "Disabled",
            Tags = { ["policy"] = "native" }
        }, options);
        return (workspace, component);
    }

    static InfrastructureTargetDeploymentPlan Plan(string? failure = null, bool nonparticipating = false, bool multipleConsumers = false)
    {
        InfrastructureCapabilityId execution = new("test/execution");
        InfrastructureCapabilityId ingestion = new("test/ingestion");
        InfrastructureCapabilityId storage = new("test/storage");
        var semantic = Infrastructure.Define(new("test/telemetry"), new("1"), new("test/bindings/v1"), infra =>
        {
            var contract = infra.Contract(Contract, new("test/rule")).Requires(ingestion).SourcedFrom(Source.Value);
            var worker = infra.Workload(Worker).Requires(execution);
            if (failure == "incomplete") worker.Requires(new("test/unsupported"));
            var component = infra.Resource(Telemetry).Persistent().Requires(ingestion);
            var workspace = infra.Resource(Store).Persistent().Requires(storage);
            if (multipleConsumers)
            {
                var another = infra.Workload(new("workloads/another")).Requires(execution);
                infra.Bind(new("bindings/another/telemetry"), another).To(component).As(contract);
            }
            if (failure != "workspace-dependency") component.RequiresReady(workspace);
            if (failure == "alias") infra.Resource(new("resources/alias")).Persistent().Requires(ingestion);
            if (failure == "direction") infra.Bind(Binding, component).To(worker).As(contract);
            else if (failure == "consumer") infra.Bind(Binding, workspace).To(component).As(contract);
            else infra.Bind(Binding, worker).To(component).As(contract);
            if (failure == "workspace-binding") infra.Bind(new("bindings/workspace/unsupported"), worker).To(workspace).As(contract);
        });
        var facilities = InfrastructureTargetFacilities.Define(new("test/facilities/v1"), new("test/capabilities/v1"),
            new(failure == "target" ? "other-target" : AzureTelemetryBinding.Target), new("test/production"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion], facility =>
            {
                facility.Workload(new("test/worker")).Provides(new(new("test/execution/evidence"), execution,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
                facility.Resource(new(AzureTelemetryBinding.ComponentFacility)).Provides(new(new("test/ingestion/evidence"), ingestion,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
                facility.Resource(new(AzureTelemetryBinding.WorkspaceFacility)).Provides(new(new("test/storage/evidence"), storage,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment/v1"), semantic.Definition, facilities, deployment =>
        {
            if (nonparticipating) deployment.NonParticipatingWorkload(Worker, "No exporter in this environment.", [Source.Value]);
            else deployment.Workload(Worker, new("test/worker"), new("test/workers/worker"), [Source]);
            if (multipleConsumers) deployment.Workload(new("workloads/another"), new("test/worker"), new("test/workers/another"), [Source]);
            deployment.Resource(Telemetry, new(AzureTelemetryBinding.ComponentFacility),
                new(failure == "physical-identity" ? "wrong" : ComponentPhysical), Authority, [Source]);
            deployment.Resource(Store, new(AzureTelemetryBinding.WorkspaceFacility),
                new(failure == "workspace-physical" ? "wrong" : WorkspacePhysical),
                failure == "workspace-owner" ? new("other/owner") : Authority, [Source]);
            if (failure == "alias") deployment.Resource(new("resources/alias"), new(AzureTelemetryBinding.ComponentFacility),
                new(ComponentPhysical.ToUpperInvariant()), Authority, [Source]);
        });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    sealed class Mocks(string? mismatch = null, bool providerSecret = false) : IMocks
    {
        public ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        public Task<object> CallAsync(MockCallArgs args) => throw new InvalidOperationException("Association must not invoke providers.");
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args);
            var state = args.Inputs.ToDictionary();
            var kind = args.Type switch { "azure-native:applicationinsights:Component" => "component",
                "azure-native:operationalinsights:Workspace" => "workspace", _ => "other" };
            var id = kind == "component" ? ComponentId : kind == "workspace" ? WorkspaceId : args.Name + "-id";
            if (kind != "other")
            {
                state["name"] = mismatch == kind + "-name" ? "wrong" : args.Inputs[kind == "component" ? "resourceName" : "workspaceName"];
                if (mismatch == kind + "-subscription") id = id.Replace(Subscription.ToString("D"), Guid.NewGuid().ToString("D"));
                if (mismatch == kind + "-kind") id = id.Replace("/providers/", "/wrong/");
            }
            if (kind == "component")
            {
                state["connectionString"] = providerSecret ? Output.CreateSecret(Connection) : mismatch == "connection" ? "" : Connection;
                if (mismatch == "unknown") state.Remove("connectionString");
                if (mismatch == "link") state["workspaceResourceId"] = WorkspaceId + "wrong";
            }
            return Task.FromResult<(string?, object)>((id, state));
        }
    }
}
