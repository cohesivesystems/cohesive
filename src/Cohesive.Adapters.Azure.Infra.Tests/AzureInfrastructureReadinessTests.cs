using System.Net;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Azure.Infra;
using Cohesive.Execution;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Azure.Infra.Tests;

public sealed class AzureInfrastructureReadinessTests
{
    static readonly InfrastructureNodeId Api = new("workloads/api"), Jobs = new("workloads/jobs"),
        State = new("resources/state"), Scheduler = new("resources/scheduler"), Artifacts = new("resources/artifacts");
    static readonly SourceReference Source = SourceReference.Create("native-output", "fixture/v1");
    static readonly InfrastructureTargetDeploymentPlan Plan = CreatePlan();
    static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    static readonly Guid Subscription = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly AzureInfrastructureObservationScope Scope = new("dev", Guid.Parse("22222222-2222-2222-2222-222222222222"), Subscription,
        Plan.Realization!.ToReference(), SourceReference.Create("cohesive-infra-handoff", "fixture-exact-handoff"));
    const string Prefix = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/fixture/providers/";
    static Dictionary<InfrastructureNodeId, string> NativeIds() => new()
    {
        [Api] = Prefix + "Microsoft.Web/sites/api",
        [Jobs] = Prefix + "Microsoft.Web/sites/jobs",
        [State] = Prefix + "Microsoft.DocumentDB/databaseAccounts/account/sqlDatabases/state",
        [Scheduler] = Prefix + "Microsoft.DurableTask/schedulers/scheduler/taskHubs/hub",
        [Artifacts] = Prefix + "Microsoft.Storage/storageAccounts/account/blobServices/default/containers/artifacts"
    };

    [Fact]
    public void Native_ids_are_bound_by_canonical_nodes_and_roundtrip_without_rebuilding_names()
    {
        var artifact = AzureInfrastructureReadiness.Bind(Plan, Scope, NativeIds(), Source);
        var restored = JsonSerializer.Deserialize<AzureInfrastructureReadinessBindings>(JsonSerializer.Serialize(artifact))!;
        Assert.Equal(Scope, restored.Scope);
        Assert.True(artifact.Bindings.SequenceEqual(restored.Bindings));
        Assert.Equal(NativeIds()[Api], restored.Bindings.Single(b =>
            b.PhysicalResource == Plan.Manifest.Workloads.Single(w => w.Workload == Api).PhysicalResource).ResourceId);
        var reversed = AzureInfrastructureReadiness.Bind(Plan, Scope, NativeIds().Reverse().ToDictionary(), Source);
        Assert.True(artifact.Bindings.SequenceEqual(reversed.Bindings));
    }

    [Fact]
    public void Undeclared_nodes_and_cross_subscription_native_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => AzureInfrastructureReadiness.Bind(Plan, Scope, new Dictionary<InfrastructureNodeId, string> { [new("unknown")] = Prefix + "Microsoft.Web/sites/api" }, Source));
        var ids = NativeIds();
        ids[Api] = ids[Api].Replace(Subscription.ToString(), Guid.NewGuid().ToString());
        Assert.Throws<ArgumentException>(() => AzureInfrastructureReadiness.Bind(Plan, Scope, ids, Source));
    }

    [Fact]
    public void Shared_physical_associations_require_exact_agreement_and_provenance()
    {
        var plan = CreatePlan(sharedSite: true);
        var scope = Scope with { Realization = plan.Realization!.ToReference() };
        var ids = NativeIds();
        Assert.Throws<ArgumentException>(() => AzureInfrastructureReadiness.Bind(plan, scope, ids, Source));
        ids[Jobs] = ids[Api];
        var artifact = AzureInfrastructureReadiness.Bind(plan, scope, ids, Source);
        Assert.Equal(4, artifact.Bindings.Length);
        Assert.All(artifact.Bindings, b => Assert.Equal(Source, b.Source));
        Assert.Throws<ArgumentException>(() => AzureInfrastructureReadiness.Bind(plan, scope, ids, default));
    }

    [Fact]
    public async Task Platform_available_and_provisioned_resources_cannot_hide_missing_dependency_coverage()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(request => {
            Interlocked.Increment(ref calls);
            var path = request.RequestUri!.AbsolutePath;
            var platform = path.Contains("Microsoft.ResourceHealth");
            var type = platform ? "Microsoft.ResourceHealth/availabilityStatuses"
                : path.Contains("Microsoft.Web") ? "Microsoft.Web/sites"
                : path.Contains("Microsoft.DocumentDB") ? "Microsoft.DocumentDB/databaseAccounts/sqlDatabases"
                : "Microsoft.DurableTask/schedulers/taskHubs";
            var properties = platform ? new { availabilityState = "Available", reportedTime = "2026-09-19T11:59:30Z" } as object
                : new { provisioningState = "Succeeded", state = "Running" };
            var body = JsonSerializer.Serialize(new { id = path, type, properties });
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }));
        var report = await Inspect(client, AzureInfrastructureReadiness.Bind(Plan, Scope, NativeIds(), Source));
        Assert.Equal(6, calls); // Four supported management reads plus two site health reads; no Blob or parent substitutions.
        Assert.False(report.Assessment.IsReady);
        Assert.False(report.Assessment.FindDecision(Api)!.IsReady);
        Assert.False(report.Assessment.FindDecision(Jobs)!.IsReady);
        var artifacts = Plan.Manifest.FindResource(Artifacts).PhysicalResource;
        Assert.Contains(artifacts, report.Assessment.FindDecision(Api)!.UnknownDependencies);
        Assert.Contains(report.PlatformHealth.Resources, r => r.Evidence.Binding.PhysicalResource == artifacts && r.Diagnostics.Any(d => d.Code == "infra.azure.collection.unsupportedResource"));
        Assert.All(report.Assessment.Observations, o => Assert.Equal(ExecutionReadinessStatus.Unknown, o.Readiness));
        Assert.Equal(TimeSpan.FromMinutes(1), report.MaximumAge);
        Assert.Equal(Scope.Handoff, report.Bindings.Scope.Handoff);
        Assert.Contains(report.Assessment.Observations, o => o.ObservedAtUtc == Now.AddSeconds(-30));
        var restored = JsonSerializer.Deserialize<AzureInfrastructureReadinessInspection>(JsonSerializer.Serialize(report))!;
        Assert.Equal(report.Assessment.Fingerprint, restored.Assessment.Fingerprint);
    }

    [Fact]
    public async Task Persisted_version_scope_and_age_policy_are_revalidated_before_io()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not issue I/O.")));
        var artifact = AzureInfrastructureReadiness.Bind(Plan, Scope, NativeIds(), Source);
        await Assert.ThrowsAsync<ArgumentException>(() => Inspect(client, artifact with { SchemaVersion = "future" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Inspect(client, artifact with { Scope = Scope with { Environment = "prod" } }));
        await Assert.ThrowsAsync<ArgumentException>(() => Inspect(client, artifact with { Scope = Scope with { Handoff = SourceReference.Create("handoff", "other") } }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AzureInfrastructureReadiness.InspectPlatformHealthAsync(Plan.Realization!, Scope, artifact,
            new(client, new Clock()), 2, TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.Zero, new Clock()));
    }

    [Fact]
    public async Task Collection_failure_is_a_redacted_non_ready_report_not_a_healthy_or_crashed_inspection()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("secret-provider-body") }));
        var report = await Inspect(client, AzureInfrastructureReadiness.Bind(Plan, Scope, NativeIds(), Source));
        Assert.False(report.Assessment.IsReady);
        Assert.All(report.Assessment.Observations, o => Assert.Equal(ExecutionHealthStatus.Unknown, o.Health));
        Assert.DoesNotContain("secret-provider-body", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Caller_cancellation_cannot_return_a_completed_partial_inspection()
    {
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(_ => {
            cancel.Cancel();
            return new(HttpStatusCode.Forbidden);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AzureInfrastructureReadiness.InspectPlatformHealthAsync(Plan.Realization!, Scope,
            AzureInfrastructureReadiness.Bind(Plan, Scope, NativeIds(), Source), new(client, new Clock()), 1, TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(1), TimeSpan.Zero, new Clock(), cancel.Token));
    }

    static Task<AzureInfrastructureReadinessInspection> Inspect(HttpClient client, AzureInfrastructureReadinessBindings artifact) =>
        AzureInfrastructureReadiness.InspectPlatformHealthAsync(Plan.Realization!, Scope, artifact, new(client, new Clock()),
            2, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), TimeSpan.Zero, new Clock());
    static InfrastructureTargetDeploymentPlan CreatePlan(bool sharedSite = false)
    {
        var semantic = Infrastructure.Define(new("inspection"), new("v1"), new("inspection/bindings/v1"), b => {
            var state = b.Resource(State).Persistent();
            var artifacts = b.Resource(Artifacts).Persistent();
            var scheduler = b.Resource(Scheduler).Persistent();
            var api = b.Workload(Api).RequiresReady(state).RequiresReady(artifacts);
            b.Workload(Jobs).RequiresReady(api).RequiresReady(state).RequiresReady(artifacts).RequiresReady(scheduler);
        });
        InfrastructureTargetFacilityId site = new("site"), storage = new("storage");
        var facilities = InfrastructureTargetFacilities.Define(new("facilities/v1"), new("capabilities/v1"),
            new("azure"), new("dev"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], b => {
                b.Workload(site).Provides(new(new("site/evidence"), new("site/capability"), CapabilityRealizationKind.Native, sourceReferences: [Source]));
                b.Resource(storage).Provides(new(new("storage/evidence"), new("storage/capability"), CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("deployment/v1"), semantic.Definition, facilities, b => {
            b.Workload(Api, site, new("sites/api"), [Source]);
            b.Workload(Jobs, site, new(sharedSite ? "sites/api" : "sites/jobs"), [Source]);
            b.Resource(State, storage, new("data/state"), new("test/dev"), [Source]);
            b.Resource(Scheduler, storage, new("data/scheduler"), new("test/dev"), [Source]);
            b.Resource(Artifacts, storage, new("data/artifacts"), new("test/dev"), [Source]);
        });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }
    sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
