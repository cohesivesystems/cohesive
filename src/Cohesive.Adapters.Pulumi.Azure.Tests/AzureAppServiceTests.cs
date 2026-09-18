using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Pulumi.Azure;
using Cohesive.Infra;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Pulumi;
using Pulumi.AzureNative.Web;
using Pulumi.Testing;

namespace Cohesive.Adapters.Pulumi.Azure.Tests;

public sealed class AzureAppServiceTests
{
    static readonly Guid Subscription = Guid.Parse("a31934a7-73b5-41bc-bf7c-783a51a35973");
    const string Tenant = "f65e5cc1-be78-4782-afdd-4d4596704825";
    const string Principal = "6a4517a1-3c0f-4c19-a398-08c46eefc9ef";
    static readonly InfrastructureNodeId Api = new("workloads/api"), Ui = new("workloads/ui"), Hosting = new("resources/plan");
    static readonly InfrastructureBindingId Edge = new("bindings/ui-api");
    static readonly InfrastructureBindingContractId HostedBy = new("contracts/hosted-by");
    static readonly InfrastructureBindingContractId Http = new("contracts/http");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("pulumi/test/production");
    static readonly SourceReference Source = SourceReference.Create("test", "app-service");
    static readonly InfrastructureSettingId Secret = new("CLIENT_SECRET"), Address = new("API_URL");
    static TestOptions Options(bool preview = false) => new() { ProjectName = "test", StackName = "production", IsPreview = preview };

    [Fact]
    public async Task Shared_native_plan_is_registered_once_with_original_parents_options_settings_and_live_identity()
    {
        var deployment = DeploymentPlan(); var policy = Policy(); var mocks = new Mocks();
        var parents = new List<Resource>();
        await Deployment.TestAsync(mocks, Options(), async () =>
        {
            ComponentResource? parent = null;
            parent = new ComponentResource("original:component:Host", "existing-parent", new()
            {
                ResourceTransformations = { args =>
                {
                    if (args.Resource is WebApp or AppServicePlan) { Assert.Same(parent, args.Options.Parent); parents.Add(args.Resource); }
                    return null;
                } }
            });
            var plan = new AppServicePlan("existing-plan", new() { Name = "shared-plan", ResourceGroupName = "rg-test", Sku = new global::Pulumi.AzureNative.Web.Inputs.SkuDescriptionArgs { Name = "B1", Tier = "Basic" } }, new() { Parent = parent });
            var api = Site("api", plan, true, new() { Parent = parent });
            var attachedApi = AzureAppServiceBinding.Attach(deployment, policy, Api, Subscription, plan, api);
            var settings = AzureAppServiceBinding.Settings(deployment, policy, Ui, new Dictionary<InfrastructureSettingId, Input<string>>
            {
                [Secret] = "fixture-secret", [Address] = attachedApi.Endpoint(Edge), [new("ALREADY_SECRET")] = Output.CreateSecret("classified")
            });
            var settingsArray = (Output<ImmutableArray<global::Pulumi.AzureNative.Web.Inputs.NameValuePairArgs>>)settings;
            // The projection retains existing classification and explicitly classifies the declared credential.
            Assert.True(await Output.IsSecretAsync(settingsArray.Apply(items => (Output<string>)items[0].Value!)));
            Assert.True(await Output.IsSecretAsync(settingsArray.Apply(items => (Output<string>)items[2].Value!)));
            var ui = new WebApp("existing-ui", new()
            {
                Name = "ui", ResourceGroupName = "rg-test", ServerFarmId = plan.Id, Enabled = true, HttpsOnly = true,
                SiteConfig = new global::Pulumi.AzureNative.Web.Inputs.SiteConfigArgs { AppSettings = settings, HealthCheckPath = "/health", LinuxFxVersion = "DOTNETCORE|10.0", AppCommandLine = "native-command" }
            }, new() { Parent = parent });
            var attachedUi = AzureAppServiceBinding.Attach(deployment, policy, Ui, Subscription, plan, ui);
            Assert.Same(plan, attachedApi.Plan); Assert.Same(plan, attachedUi.Plan);
            attachedApi.Endpoint(Edge).Apply(value => { Assert.Equal("https://api.azurewebsites.net", value); return value; });
            attachedApi.ReadManagedIdentity().Apply(identity => { Assert.Equal(Principal, identity.PrincipalId); Assert.Equal(Tenant, identity.TenantId); return identity; });
            attachedUi.SiteId.Apply(value => { Assert.EndsWith("/sites/ui", value); return value; });
        });
        Assert.Equal(3, parents.Count);
        Assert.Single(mocks.Resources, r => r.Type == "azure-native:web:AppServicePlan");
        Assert.Equal(2, mocks.Resources.Count(r => r.Type == "azure-native:web:WebApp"));
        Assert.Equal(1, mocks.Calls);
        var uiInputs = mocks.Resources.Single(r => r.Name == "existing-ui").Inputs;
        var json = JsonSerializer.SerializeToElement(uiInputs);
        Assert.Equal("/health", json.GetProperty("siteConfig").GetProperty("healthCheckPath").GetString());
        Assert.Equal("native-command", json.GetProperty("siteConfig").GetProperty("appCommandLine").GetString());
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("authority")]
    [InlineData("resource-group")]
    [InlineData("provenance")]
    [InlineData("placements")]
    [InlineData("duplicate")]
    [InlineData("activation")]
    [InlineData("settings-budget")]
    [InlineData("secret-settings")]
    [InlineData("binding-settings")]
    [InlineData("plan-binding")]
    [InlineData("readiness-only")]
    [InlineData("reversed-hosting")]
    [InlineData("wrong-hosting-contract")]
    [InlineData("ambiguous-hosting")]
    [InlineData("hosting-contract")]
    [InlineData("hosting-endpoint-overlap")]
    [InlineData("wrong-selected-plan")]
    [InlineData("plan-identity")]
    [InlineData("site-identity")]
    [InlineData("owner")]
    [InlineData("external-plan")]
    [InlineData("alias")]
    [InlineData("target")]
    public void Semantic_failures_reject_before_registration(string failure)
    {
        var policy = Policy(); var first = policy.Placements[0];
        policy = failure switch
        {
            "hosting-contract" => policy with { HostingContract = default },
            "hosting-endpoint-overlap" => policy with { HostingContract = Http },
            "wrong-selected-plan" => policy with { Placements = [first with { Plan = new("resources/other-plan") }, policy.Placements[1]] },
            "subscription" => policy with { SubscriptionId = Guid.Empty },
            "authority" => policy with { LifecycleAuthority = new("wrong") },
            "resource-group" => policy with { ResourceGroupName = "invalid/rg" },
            "provenance" => policy with { SourceReferences = [] },
            "placements" => policy with { Placements = [] },
            "duplicate" => policy with { Placements = [first, first] },
            "activation" => policy with { Placements = [first with { Activation = AzureAppServiceActivation.Unspecified }, policy.Placements[1]] },
            "settings-budget" => policy with { MaximumSettingNameLength = 0 },
            "secret-settings" => policy with { Placements = [first with { SecretSettings = [default] }, policy.Placements[1]] },
            "binding-settings" => policy with { Placements = [first with { BindingSettings = [new(new("X"), new("unknown"))] }, policy.Placements[1]] },
            _ => policy
        };
        var deployment = DeploymentPlan(failure);
        var diagnostics = AzureAppServiceBinding.Validate(deployment, policy, Subscription);
        Assert.NotEmpty(diagnostics);
        if (failure is "plan-binding" or "readiness-only" or "reversed-hosting" or "wrong-hosting-contract" or "ambiguous-hosting" or "wrong-selected-plan")
            Assert.Contains(diagnostics, d => d.Code == "azure.app-service.plan-binding" && d.SchemaLocation == Api.Value);
        if (failure is "hosting-contract" or "hosting-endpoint-overlap")
            Assert.Contains(diagnostics, d => d.Code == "azure.app-service.hosting-contract");
        Assert.All(diagnostics, d => Assert.NotNull(d.Evidence));
        if (failure == "activation") Assert.Contains(diagnostics, d => d.SchemaLocation == Api.Value);
        Assert.Throws<AzureAppServiceValidationException>(() => AzureAppServiceBinding.Names(deployment, policy, Api, Subscription));
    }

    [Theory]
    [InlineData("plan-id")]
    [InlineData("plan-name")]
    [InlineData("site-id")]
    [InlineData("site-name")]
    [InlineData("plan-link")]
    [InlineData("activation")]
    [InlineData("resource-group")]
    [InlineData("foreign-stack")]
    [InlineData("https")]
    [InlineData("hostname")]
    [InlineData("observation-id")]
    [InlineData("principal")]
    [InlineData("tenant")]
    [InlineData("identity-type")]
    [InlineData("invoke")]
    public async Task Resolved_mismatches_fail_without_exposing_values(string failure)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await Deployment.TestAsync(new Mocks(failure),
            failure == "foreign-stack" ? new TestOptions { ProjectName = "test", StackName = "foreign", IsPreview = false } : Options(), () =>
            {
                var plan = new AppServicePlan("existing-plan", new() { Name = "shared-plan", ResourceGroupName = "rg-test" });
                var site = Site("api", plan, true);
                var attached = AzureAppServiceBinding.Attach(DeploymentPlan(), Policy(), Api, Subscription, plan, site);
                _ = attached.Endpoint(Edge); _ = attached.ReadManagedIdentity();
            }));
        Assert.Contains("InvalidOperationException", error.ToString());
        Assert.DoesNotContain("fixture-secret", error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disabled_endpoint_sources_or_targets_are_rejected_but_retained_configuration_is_supported(bool disableTarget)
    {
        var original = Policy();
        var policy = original with { Placements = [.. original.Placements.Select(p => p with
            { Activation = (p.Workload == Api) == disableTarget ? AzureAppServiceActivation.Disabled : AzureAppServiceActivation.Enabled })] };
        await Deployment.TestAsync(new Mocks(), Options(), () =>
        {
            var plan = new AppServicePlan("existing-plan", new() { Name = "shared-plan", ResourceGroupName = "rg-test" });
            var api = Site("api", plan, !disableTarget);
            var attached = AzureAppServiceBinding.Attach(DeploymentPlan(), policy, Api, Subscription, plan, api);
            Assert.Throws<ArgumentException>(() => attached.Endpoint(Edge));
            Assert.Throws<ArgumentException>(() => attached.Endpoint(new("unknown")));
            _ = AzureAppServiceBinding.Settings(DeploymentPlan(), policy, Ui, new Dictionary<InfrastructureSettingId, Input<string>>
                { [Secret] = "fixture-secret", [Address] = "retained configuration" });
        });
    }

    [Fact]
    public void Settings_budget_missing_attribution_and_cancellation_fail_before_registration()
    {
        var plan = DeploymentPlan(); var policy = Policy();
        Assert.Throws<ArgumentException>(() => AzureAppServiceBinding.Settings(plan, policy, Ui,
            new Dictionary<InfrastructureSettingId, Input<string>> { [new(new string('x', 101))] = "fixture-secret" }));
        Assert.Throws<ArgumentException>(() => AzureAppServiceBinding.Settings(plan, policy, Ui,
            new Dictionary<InfrastructureSettingId, Input<string>> { [Secret] = "fixture-secret" }));
        Assert.Throws<OperationCanceledException>(() => AzureAppServiceBinding.Attach(plan, policy, Api, Subscription, null!, null!, new(true)));
        var roundtrip = JsonSerializer.Deserialize<AzureAppServicePolicy>(JsonSerializer.Serialize(policy))!;
        Assert.Empty(AzureAppServiceBinding.Validate(plan, roundtrip, Subscription));
        Assert.True(policy.Placements[1].BindingSettings.SequenceEqual(roundtrip.Placements[1].BindingSettings));
    }

    [Fact]
    public async Task Unknown_fresh_preview_identity_does_not_invoke_the_provider()
    {
        var mocks = new Mocks("unknown-site"); var observed = false;
        await Deployment.TestAsync(mocks, Options(preview: true), () =>
        {
            var plan = new AppServicePlan("existing-plan", new() { Name = "shared-plan", ResourceGroupName = "rg-test" });
            var site = Site("api", plan, true);
            var attached = AzureAppServiceBinding.Attach(DeploymentPlan(), Policy(), Api, Subscription, plan, site);
            attached.ReadManagedIdentity().Apply(value => { observed = true; return value; });
        });
        Assert.False(observed); Assert.Equal(0, mocks.Calls);
    }

    [Fact]
    public async Task Non_participating_workload_cannot_acquire_settings_or_endpoint_projection()
    {
        var deployment = DeploymentPlan("excluded"); var policy = Policy() with { Placements = [Policy().Placements[0]] };
        Assert.Empty(AzureAppServiceBinding.Validate(deployment, policy, Subscription));
        Assert.Throws<ArgumentException>(() => AzureAppServiceBinding.Settings(deployment, policy, Ui,
            new Dictionary<InfrastructureSettingId, Input<string>>()));
        await Deployment.TestAsync(new Mocks(), Options(), () =>
        {
            var plan = new AppServicePlan("existing-plan", new() { Name = "shared-plan", ResourceGroupName = "rg-test" });
            var attached = AzureAppServiceBinding.Attach(deployment, policy, Api, Subscription, plan, Site("api", plan, true));
            Assert.Throws<ArgumentException>(() => attached.Endpoint(Edge));
        });
    }

    [Fact]
    public async Task Dedicated_plan_is_selected_by_canonical_dependency_without_changing_native_sizing()
    {
        var deployment = DeploymentPlan("dedicated"); var policy = Policy();
        policy = policy with { Placements = [policy.Placements[0], policy.Placements[1] with { Plan = new("resources/dedicated-plan") }] };
        Assert.Empty(AzureAppServiceBinding.Validate(deployment, policy, Subscription));
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, Options(), () =>
        {
            var shared = new AppServicePlan("existing-plan", new() { Name = "shared-plan", ResourceGroupName = "rg-test" });
            var dedicated = new AppServicePlan("existing-dedicated", new() { Name = "dedicated-plan", ResourceGroupName = "rg-test",
                Sku = new global::Pulumi.AzureNative.Web.Inputs.SkuDescriptionArgs { Name = "P1v3", Capacity = 2 } });
            _ = AzureAppServiceBinding.Attach(deployment, policy, Api, Subscription, shared, Site("api", shared, true));
            _ = AzureAppServiceBinding.Attach(deployment, policy, Ui, Subscription, dedicated, Site("ui", dedicated, true));
        });
        Assert.Equal(2, mocks.Resources.Count(r => r.Type == "azure-native:web:AppServicePlan"));
        var sizing = JsonSerializer.SerializeToElement(mocks.Resources.Single(r => r.Name == "existing-dedicated").Inputs).GetProperty("sku");
        Assert.Equal("P1v3", sizing.GetProperty("name").GetString()); Assert.Equal(2, sizing.GetProperty("capacity").GetInt32());
    }

    [Fact]
    public void Hosting_is_a_canonical_binding_without_a_runtime_readiness_obligation()
    {
        var deployment = DeploymentPlan();
        Assert.True(deployment.IsComplete);
        Assert.Empty(deployment.FacilityPlan.Definition.Definition.ReadinessDependencies);
        Assert.Equal(2, deployment.FacilityPlan.Definition.Definition.Bindings.Count(b => b.Contract == HostedBy));
        Assert.Empty(AzureAppServiceBinding.Validate(deployment, Policy(), Subscription));
    }

    static WebApp Site(string name, AppServicePlan plan, bool enabled, CustomResourceOptions? options = null) => new("existing-" + name,
        new() { Name = name, ResourceGroupName = "rg-test", ServerFarmId = plan.Id, Enabled = enabled, HttpsOnly = true }, options);

    static AzureAppServicePolicy Policy() => new()
    {
        LifecycleAuthority = Authority, SubscriptionId = Subscription, ResourceGroupName = "rg-test", MaximumSettingNameLength = 100,
        HostingContract = HostedBy, EndpointContracts = [Http], SourceReferences = [Source],
        Placements = [new() { Workload = Api, Plan = Hosting, Activation = AzureAppServiceActivation.Enabled, SourceReferences = [Source] },
            new() { Workload = Ui, Plan = Hosting, Activation = AzureAppServiceActivation.Enabled, SourceReferences = [Source],
                SecretSettings = [Secret], BindingSettings = [new(Address, Edge)] }]
    };

    static InfrastructureTargetDeploymentPlan DeploymentPlan(string? failure = null)
    {
        InfrastructureCapabilityId hosting = new("hosting"), executable = new("executable");
        var semantic = Infrastructure.Define(new("test"), new("v1"), new("binding/v1"), infra =>
        {
            var resource = infra.Resource(Hosting).Requires(hosting);
            if (failure == "external-plan") resource.External(); else resource.Persistent();
            var api = infra.Workload(Api).Requires(executable); var ui = infra.Workload(Ui).Requires(executable);
            var hostedBy = failure is "readiness-only" or "plan-binding" ? null : infra.Contract(failure == "wrong-hosting-contract" ? new("contracts/other") : HostedBy,
                new("hosting-rule")).Requires(hosting).SourcedFrom(Source.Value);
            if (failure == "readiness-only") { api.RequiresReady(resource); ui.RequiresReady(resource); }
            else if (failure == "reversed-hosting")
            {
                infra.Bind(resource).To(api).As(hostedBy!);
                infra.Bind(resource).To(ui).As(hostedBy!);
            }
            else if (failure != "plan-binding")
            {
                infra.Bind(api).To(resource).As(hostedBy!);
                if (failure == "dedicated") infra.Bind(ui).To(infra.Resource(new("resources/dedicated-plan")).Persistent().Requires(hosting)).As(hostedBy!);
                else infra.Bind(ui).To(resource).As(hostedBy!);
                if (failure == "ambiguous-hosting")
                    infra.Bind(api).To(infra.Resource(new("resources/dedicated-plan")).Persistent().Requires(hosting)).As(hostedBy!);
            }
            var contract = infra.Contract(Http, new("http-rule")).Requires(executable).SourcedFrom(Source.Value);
            infra.Bind(Edge, ui).To(api).As(contract);
            if (failure == "alias") infra.Resource(new("resources/alias")).Persistent().Requires(hosting);
        });
        var facilities = InfrastructureTargetFacilities.Define(new("facilities/v1"), new("profile/v1"),
            new(failure == "target" ? "wrong" : AzureAppServiceBinding.Target), new("variant/v1"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], target =>
            {
                target.Workload(new(AzureAppServiceBinding.SiteFacility)).Provides(new(new("executable-proof"), executable, CapabilityRealizationKind.Native, sourceReferences: [Source]));
                target.Resource(new(AzureAppServiceBinding.PlanFacility)).Provides(new(new("hosting-proof"), hosting, CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("deployment/v1"), semantic.Definition, facilities, deployment =>
        {
            deployment.Workload(Api, new(AzureAppServiceBinding.SiteFacility), new(failure == "site-identity" ? "wrong" : "azure/app-service/sites/api"), [Source]);
            if (failure == "excluded") deployment.NonParticipatingWorkload(Ui, "No UI in this environment", [Source.Value]);
            else deployment.Workload(Ui, new(AzureAppServiceBinding.SiteFacility), new("azure/app-service/sites/ui"), [Source]);
            if (failure is "dedicated" or "ambiguous-hosting") deployment.Resource(new("resources/dedicated-plan"), new(AzureAppServiceBinding.PlanFacility),
                new("azure/app-service/plans/dedicated-plan"), Authority, [Source]);
            deployment.Resource(Hosting, new(AzureAppServiceBinding.PlanFacility), new(failure == "plan-identity" ? "wrong" : "azure/app-service/plans/shared-plan"),
                failure == "owner" ? new("pulumi/foreign/stack") : Authority, [Source]);
            if (failure == "alias") deployment.Resource(new("resources/alias"), new(AzureAppServiceBinding.PlanFacility), new("azure/app-service/plans/shared-plan"), Authority, [Source]);
        });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    sealed class Mocks(string? failure = null) : IMocks
    {
        public ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        public int Calls;
        static string Id(string kind, string name) => $"/subscriptions/{Subscription:D}/resourceGroups/rg-test/providers/Microsoft.Web/{kind}/{name}";
        public Task<object> CallAsync(MockCallArgs args)
        {
            Interlocked.Increment(ref Calls);
            if (failure == "invoke") throw new InvalidOperationException("Provider observation failed after native registration.");
            var name = args.Args["name"].ToString()!;
            return Task.FromResult<object>(new Dictionary<string, object>
            {
                ["id"] = failure == "observation-id" ? Id("sites", "wrong") : Id("sites", name), ["name"] = name,
                ["identity"] = new Dictionary<string, object> { ["principalId"] = failure == "principal" ? "invalid" : Principal,
                    ["tenantId"] = failure == "tenant" ? "invalid" : Tenant, ["type"] = failure == "identity-type" ? "UserAssigned" : "SystemAssigned" }
            });
        }
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args); var state = args.Inputs.ToDictionary(); var id = args.Name + "-id";
            if (args.Type is "azure-native:web:WebApp" or "azure-native:web:AppServicePlan")
            {
                var site = args.Type == "azure-native:web:WebApp"; var name = args.Inputs["name"].ToString()!;
                id = Id(site ? "sites" : "serverfarms", name);
                if (failure == (site ? "site-id" : "plan-id")) id += "-wrong";
                if (failure == (site ? "site-name" : "plan-name")) state["name"] = "wrong";
                if (failure == "resource-group") id = id.Replace("rg-test", "foreign");
                if (site)
                {
                    state["defaultHostName"] = failure == "hostname" ? "invalid/path" : name + ".azurewebsites.net";
                    if (failure == "plan-link") state["serverFarmId"] = Id("serverfarms", "wrong");
                    if (failure == "activation") state["enabled"] = false;
                    if (failure == "https") state["httpsOnly"] = false;
                }
            }
            return Task.FromResult<(string?, object)>((failure == "unknown-site" && args.Type == "azure-native:web:WebApp" ? null : id, state));
        }
    }
}
