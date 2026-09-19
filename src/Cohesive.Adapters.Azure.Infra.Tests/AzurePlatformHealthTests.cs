using System.Net;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Azure.Infra;
using Cohesive.Execution;
using Cohesive.Infra.Realization;
using static Cohesive.Adapters.Azure.Infra.Tests.AzureInfrastructureObservationsTests;

namespace Cohesive.Adapters.Azure.Infra.Tests;

public sealed class AzurePlatformHealthTests
{
    [Theory]
    [InlineData("Available", ExecutionHealthStatus.Healthy)]
    [InlineData("Unavailable", ExecutionHealthStatus.Unhealthy)]
    [InlineData("Degraded", ExecutionHealthStatus.Degraded)]
    [InlineData("Unknown", ExecutionHealthStatus.Unknown)]
    public async Task Native_platform_health_preserves_source_time_and_never_establishes_admission(string state, ExecutionHealthStatus health)
    {
        using var client = Client((request, _) => {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://management.azure.com" + ApiBinding.ResourceId + Suffix + "?api-version=2025-05-01", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response(Fixture(ApiBinding).Replace("Available", state)));
        });
        var capture = await Collector(client).CollectPlatformHealthAsync(Realization, Scope, [ApiBinding], 1, Timeout);
        var resource = Assert.Single(capture.Resources);
        Assert.Empty(resource.Diagnostics);
        Assert.Equal(state, resource.AvailabilityState);
        Assert.Null(resource.ProvisioningState);
        Assert.Null(resource.SiteState);
        Assert.Equal(AzureInfrastructureEvidenceKind.PlatformHealth, resource.Evidence.Kind);
        Assert.Equal(Now.AddSeconds(-30), resource.Evidence.Observation.ObservedAtUtc);
        Assert.Equal(Now, capture.CompletedAtUtc);
        Assert.DoesNotContain("synthetic-provider-detail", JsonSerializer.Serialize(capture));
        // Even a trusted producer accidentally supplying Ready cannot turn platform health into admission.
        var evidence = resource.Evidence with { Observation = new(Api, health, ExecutionReadinessStatus.Ready,
            resource.Evidence.Observation.ObservedAtUtc, resource.Evidence.Observation.SourceReferences) };
        var normalized = AzureInfrastructureObservations.Normalize(Realization, Scope, Scope, [ApiBinding], [evidence], Now, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        var observation = Assert.Single(normalized);
        Assert.Equal(health, observation.Health);
        Assert.Equal(ExecutionReadinessStatus.Unknown, observation.Readiness);
        Assert.Equal("infra.azure.observation.platformHealthOnly", Assert.Single(observation.Diagnostics).Code);
        Assert.Contains(observation.SourceReferences, s => s.Value.Contains("azure-resource-health-get"));
        Assert.False(InfrastructureReadinessEvaluator.Assess(Realization, normalized).IsReady);
    }

    [Theory]
    [InlineData("2026-09-19T11:58:00Z", "stale")]
    [InlineData("2026-09-19T12:00:01Z", "future")]
    public async Task Source_freshness_is_not_reset_by_successful_collection(string timestamp, string reason)
    {
        using var client = Client((_, _) => Task.FromResult(Response(Fixture(ApiBinding).Replace("2026-09-19T11:59:30Z", timestamp))));
        var capture = await Collector(client).CollectPlatformHealthAsync(Realization, Scope, [ApiBinding], 1, Timeout);
        var evidence = Assert.Single(capture.Resources).Evidence;
        var result = Assert.Single(AzureInfrastructureObservations.Normalize(Realization, Scope, Scope, [ApiBinding], [evidence], Now, TimeSpan.FromMinutes(1), TimeSpan.Zero));
        Assert.Equal(evidence.Observation.ObservedAtUtc, result.ObservedAtUtc);
        Assert.Equal(ExecutionHealthStatus.Unknown, result.Health);
        Assert.Equal("infra.azure.observation." + reason, Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("2026-09-19T11:59:30Z", "not-a-time", "invalidSourceTime")]
    [InlineData("2026-09-19T11:59:30Z", "2026-09-19T11:59:30", "invalidSourceTime")]
    [InlineData("2026-09-19T11:59:30Z", "2026-09-19T11:59:30-07:00", "invalidSourceTime")]
    [InlineData("reportedTime", "otherTime", "invalidSourceTime")]
    [InlineData("Available", "secret-unknown-status", "unsupportedAvailabilityState")]
    [InlineData("availabilityState", "otherState", "unsupportedAvailabilityState")]
    [InlineData("AvailabilityStatuses", "WrongType", "identityMismatch")]
    [InlineData("availabilityStatuses/current", "availabilityStatuses/old", "identityMismatch")]
    [InlineData("\"availabilityState\": \"Available\",", "\"availabilityState\": \"Available\", \"availabilityState\": \"Unavailable\",", "invalidResponse")]
    public async Task Unusable_or_contradictory_native_evidence_is_redacted(string old, string replacement, string code)
    {
        using var client = Client((_, _) => Task.FromResult(Response(Fixture(ApiBinding).Replace(old, replacement))));
        var capture = await Collector(client).CollectPlatformHealthAsync(Realization, Scope, [ApiBinding], 1, Timeout);
        var resource = Assert.Single(capture.Resources);
        Assert.Equal(AzureInfrastructureEvidenceKind.CollectionFailed, resource.Evidence.Kind);
        Assert.Equal(ExecutionHealthStatus.Unknown, resource.Evidence.Observation.Health);
        Assert.Equal("infra.azure.collection." + code, Assert.Single(resource.Diagnostics).Code);
        Assert.Null(resource.AvailabilityState);
        Assert.DoesNotContain("secret-unknown-status", JsonSerializer.Serialize(capture));
    }

    [Theory]
    [InlineData("Microsoft.DocumentDB/databaseAccounts/account/sqlDatabases/state")]
    [InlineData("Microsoft.DurableTask/schedulers/scheduler")]
    [InlineData("Microsoft.DurableTask/schedulers/scheduler/taskHubs/hub")]
    [InlineData("Microsoft.Storage/storageAccounts/account/blobServices/default/containers/artifacts")]
    public async Task Unsupported_resources_do_not_fall_back_to_parent_health(string native)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No request allowed."));
        var binding = StateBinding with { ResourceId = Prefix + native };
        var capture = await Collector(client).CollectPlatformHealthAsync(Realization, Scope, [binding], 1, Timeout);
        Assert.Equal("infra.azure.collection.unsupportedResource", Assert.Single(Assert.Single(capture.Resources).Diagnostics).Code);
    }

    [Fact]
    public async Task Equivalent_reads_are_shared_without_cross_capture_or_evidence_kind_caching()
    {
        var calls = 0;
        using var client = Client((request, _) => {
            calls++;
            var fixture = request.RequestUri!.AbsolutePath.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
                ? Fixture(ApiBinding) : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "site.json")).Replace("{{RESOURCE_ID}}", ApiBinding.ResourceId);
            return Task.FromResult(Response(fixture));
        });
        var collector = Collector(client);
        var alias = StateBinding with { ResourceId = ApiBinding.ResourceId.ToUpperInvariant() };
        var capture = await collector.CollectPlatformHealthAsync(Realization, Scope, [ApiBinding, alias], 2, Timeout);
        Assert.Equal(1, calls);
        Assert.Equal(2, capture.Resources.Length);
        Assert.Equal(2, capture.Resources.Select(r => r.Evidence.Binding.PhysicalResource).Distinct().Count());
        await collector.CollectPlatformHealthAsync(Realization, Scope, [ApiBinding, alias], 2, Timeout);
        await collector.CollectAsync(Realization, Scope, [ApiBinding, alias], 2, Timeout);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Partial_access_failure_does_not_erase_attributable_cosmos_account_health()
    {
        var account = StateBinding with { ResourceId = Prefix + "Microsoft.DocumentDB/databaseAccounts/account" };
        var calls = 0;
        using var client = Client((request, _) => {
            Interlocked.Increment(ref calls);
            return Task.FromResult(request.RequestUri!.AbsolutePath == ApiBinding.ResourceId + Suffix
                ? Response("secret-body", HttpStatusCode.Forbidden) : Response(Fixture(account)));
        });
        var capture = await Collector(client).CollectPlatformHealthAsync(Realization, Scope, [ApiBinding, account], 2, Timeout);
        Assert.Equal(2, calls);
        var failed = capture.Resources.Single(r => r.Evidence.Binding == ApiBinding);
        Assert.Equal("infra.azure.collection.accessDenied", Assert.Single(failed.Diagnostics).Code);
        Assert.Equal(ExecutionHealthStatus.Unknown, failed.Evidence.Observation.Health);
        Assert.Equal(ExecutionHealthStatus.Healthy, capture.Resources.Single(r => r.Evidence.Binding == account).Evidence.Observation.Health);
        Assert.DoesNotContain("secret-body", JsonSerializer.Serialize(capture));
    }

    [Fact]
    public async Task Cancellation_and_concurrency_bounds_apply_to_platform_reads()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = Client(async (_, ct) => {
            Assert.Equal(1, Interlocked.Increment(ref calls));
            entered.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
            return Response("{}");
        });
        using var cancel = new CancellationTokenSource();
        var account = StateBinding with { ResourceId = Prefix + "Microsoft.DocumentDB/databaseAccounts/account" };
        var capture = Collector(client).CollectPlatformHealthAsync(Realization, Scope, [ApiBinding, account], 1, Timeout, cancel.Token);
        await entered.Task.WaitAsync(Timeout);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(1, calls);
    }

    const string Suffix = "/providers/Microsoft.ResourceHealth/availabilityStatuses/current";
    static readonly string Prefix = $"/subscriptions/{Subscription:D}/resourceGroups/rg/providers/";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    static string Fixture(AzureInfrastructureObservationBinding binding) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "availability.json")).Replace("{{RESOURCE_ID}}", binding.ResourceId);
    static HttpResponseMessage Response(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    static AzureInfrastructureCollector Collector(HttpClient client) => new(client, new Clock());
    sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
