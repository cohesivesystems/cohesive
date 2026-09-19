using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Azure.Infra;
using Cohesive.Execution;
using Cohesive.Infra.Realization;
using static Cohesive.Adapters.Azure.Infra.Tests.AzureInfrastructureObservationsTests;

namespace Cohesive.Adapters.Azure.Infra.Tests;

public sealed class AzureInfrastructureCollectorTests
{
    [Theory]
    [InlineData("site", "Microsoft.Web/sites/api", "2025-03-01", null, "Running")]
    [InlineData("cosmos", "Microsoft.DocumentDB/databaseAccounts/account", "2025-10-15", "Succeeded", null)]
    [InlineData("database", "Microsoft.DocumentDB/databaseAccounts/account/sqlDatabases/state", "2025-10-15", null, null)]
    [InlineData("scheduler", "Microsoft.DurableTask/schedulers/scheduler", "2025-11-01", "Succeeded", null)]
    [InlineData("taskhub", "Microsoft.DurableTask/schedulers/scheduler/taskHubs/hub", "2025-11-01", "Succeeded", null)]
    public async Task Pinned_get_contracts_preserve_native_state_without_claiming_runtime_readiness(
        string fixture, string native, string version, string? provisioning, string? site)
    {
        var binding = ApiBinding with { ResourceId = Prefix + native };
        TrackingContent? content = null;
        using var client = Client((request, _) => {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("management.azure.com", request.RequestUri!.Host);
            Assert.Equal("?api-version=" + version, request.RequestUri.Query);
            Assert.Equal(binding.ResourceId, request.RequestUri.AbsolutePath);
            content = new TrackingContent(Fixture(fixture, binding.ResourceId));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var capture = await Collector(client).CollectAsync(Realization, Scope, [binding], 2, TimeSpan.FromSeconds(5));
        var resource = Assert.Single(capture.Resources);
        Assert.Equal(provisioning, resource.ProvisioningState);
        Assert.Equal(site, resource.SiteState);
        Assert.Equal(AzureInfrastructureEvidenceKind.Provisioning, resource.Evidence.Kind);
        Assert.Equal(ExecutionReadinessStatus.Unknown, resource.Evidence.Observation.Readiness);
        Assert.Empty(resource.Diagnostics);
        Assert.Equal(Now, resource.Evidence.Observation.ObservedAtUtc);
        Assert.Equal(Scope, capture.Scope);
        Assert.True(content!.Disposed);
        var normalized = AzureInfrastructureObservations.Normalize(Realization, Scope, Scope, [binding], [resource.Evidence], Now, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        Assert.Equal(ExecutionReadinessStatus.Unknown, Assert.Single(normalized).Readiness);
    }

    [Theory]
    [InlineData(401, "accessDenied")]
    [InlineData(403, "accessDenied")]
    [InlineData(404, "notFound")]
    [InlineData(429, "httpFailure")]
    [InlineData(500, "httpFailure")]
    [InlineData(302, "httpFailure")]
    public async Task Http_failures_are_redacted_partial_results_without_retries(int status, string expected)
    {
        var calls = 0;
        using var client = Client((r, _) => {
            Interlocked.Increment(ref calls);
            return Task.FromResult(r.RequestUri!.AbsolutePath == ApiBinding.ResourceId
                ? Response("secret-provider-payload", (HttpStatusCode)status)
                : Response(Fixture("database", StateBinding.ResourceId)));
        });
        var capture = await Collector(client).CollectAsync(Realization, Scope, [ApiBinding, StateBinding], 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        var failed = capture.Resources.Single(r => r.Evidence.Binding == ApiBinding);
        Assert.Equal("infra.azure.collection." + expected, Assert.Single(failed.Diagnostics).Code);
        Assert.Equal(AzureInfrastructureEvidenceKind.CollectionFailed, failed.Evidence.Kind);
        Assert.Equal(AzureInfrastructureEvidenceKind.Provisioning, capture.Resources.Single(r => r.Evidence.Binding == StateBinding).Evidence.Kind);
        Assert.DoesNotContain("secret-provider-payload", JsonSerializer.Serialize(capture));
    }

    [Fact]
    public async Task Equivalent_native_reads_are_shared_per_capture_but_not_across_captures()
    {
        var calls = 0;
        using var client = Client((_, _) => {
            Interlocked.Increment(ref calls); return Task.FromResult(Response(Fixture("site", ApiBinding.ResourceId)));
        });
        var bindings = ImmutableArray.Create(ApiBinding, StateBinding with { ResourceId = ApiBinding.ResourceId.ToUpperInvariant() });
        var collector = Collector(client);
        var first = await collector.CollectAsync(Realization, Scope, bindings, 2, TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls); Assert.Equal(2, first.Resources.Length);
        Assert.Equal(2, first.Resources.Select(r => r.Evidence.Observation.PhysicalResource).Distinct().Count());
        await collector.CollectAsync(Realization, Scope, bindings, 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Bound_is_enforced_and_caller_cancellation_returns_no_capture()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var calls = 0;
        using var client = Client(async (_, ct) => {
            Assert.Equal(1, Interlocked.Increment(ref active));
            Interlocked.Increment(ref calls); entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Response("{}"); }
            finally { Interlocked.Decrement(ref active); }
        });
        using var cancel = new CancellationTokenSource();
        var task = Collector(client).CollectAsync(Realization, Scope, [ApiBinding, StateBinding], 1, TimeSpan.FromSeconds(5), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, calls); Assert.Equal(0, active);
    }

    [Fact]
    public async Task Request_timeout_is_a_classified_failure()
    {
        using var client = Client(async (_, ct) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Response("{}");
        });
        var result = await Collector(client).CollectAsync(Realization, Scope, [ApiBinding], 1, TimeSpan.FromMilliseconds(20));
        Assert.Equal("infra.azure.collection.timeout", Assert.Single(Assert.Single(result.Resources).Diagnostics).Code);
    }

    [Fact]
    public async Task Unsupported_resource_does_not_make_a_request()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Must not query unsupported resources."));
        var binding = ApiBinding with { ResourceId = Prefix + "Microsoft.Storage/storageAccounts/account/blobServices/default/containers/artifacts" };
        var result = await Collector(client).CollectAsync(Realization, Scope, [binding], 1, TimeSpan.FromSeconds(5));
        Assert.Equal("infra.azure.collection.unsupportedResource", Assert.Single(Assert.Single(result.Resources).Diagnostics).Code);
    }

    [Theory]
    [InlineData("bad-json", "invalidResponse")]
    [InlineData("[]", "invalidResponse")]
    [InlineData("{\"id\":\"wrong\",\"type\":\"Microsoft.Web/sites\",\"properties\":{}}", "identityMismatch")]
    [InlineData("{\"id\":\"{{RESOURCE_ID}}\",\"type\":\"Microsoft.Web/sites\",\"properties\":{},\"id\":\"{{RESOURCE_ID}}\"}", "invalidResponse")]
    [InlineData("{\"id\":\"{{RESOURCE_ID}}\",\"type\":\"Microsoft.Web/sites\",\"properties\":{\"state\":\"Running\",\"state\":\"Stopped\"}}", "invalidResponse")]
    public async Task Invalid_or_mismatched_response_does_not_admit_evidence(string json, string expected)
    {
        using var client = Client((_, _) => Task.FromResult(Response(json.Replace("{{RESOURCE_ID}}", ApiBinding.ResourceId))));
        var result = await Collector(client).CollectAsync(Realization, Scope, [ApiBinding], 1, TimeSpan.FromSeconds(5));
        Assert.Equal("infra.azure.collection." + expected, Assert.Single(Assert.Single(result.Resources).Diagnostics).Code);
    }

    [Fact]
    public async Task Unknown_native_tokens_and_unrelated_payload_are_not_retained()
    {
        var json = JsonSerializer.Serialize(new { id = ApiBinding.ResourceId, type = "Microsoft.Web/sites", properties = new { state = "secret-state", provisioningState = "secret-provisioning", password = "secret-password" } });
        using var client = Client((_, _) => Task.FromResult(Response(json)));
        var result = await Collector(client).CollectAsync(Realization, Scope, [ApiBinding], 1, TimeSpan.FromSeconds(5));
        var resource = Assert.Single(result.Resources);
        Assert.Null(resource.SiteState); Assert.Null(resource.ProvisioningState);
        Assert.DoesNotContain("secret-", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Oversized_and_transport_failures_are_bounded_redacted_and_disposed()
    {
        var content = new TrackingContent(new string('x', 262145));
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var result = await Collector(client).CollectAsync(Realization, Scope, [ApiBinding], 1, TimeSpan.FromSeconds(5));
        Assert.Equal("infra.azure.collection.responseTooLarge", Assert.Single(Assert.Single(result.Resources).Diagnostics).Code);
        Assert.True(content.Disposed);
        using var failed = Client((_, _) => throw new HttpRequestException("secret-exception"));
        var failure = await Collector(failed).CollectAsync(Realization, Scope, [ApiBinding], 1, TimeSpan.FromSeconds(5));
        Assert.Equal("infra.azure.collection.transportFailure", Assert.Single(Assert.Single(failure.Resources).Diagnostics).Code);
        Assert.DoesNotContain("secret-exception", JsonSerializer.Serialize(failure));
    }

    [Fact]
    public async Task Unknown_length_body_is_still_size_bounded()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StreamContent(new NonSeekableMemoryStream(new byte[262145])) }));
        var result = await Collector(client).CollectAsync(Realization, Scope, [ApiBinding], 1, TimeSpan.FromSeconds(5));
        Assert.Equal("infra.azure.collection.responseTooLarge", Assert.Single(Assert.Single(result.Resources).Diagnostics).Code);
    }

    [Fact]
    public async Task Cancellation_during_body_read_throws_and_disposes_content()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new BlockingStream(entered);
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        using var cancel = new CancellationTokenSource();
        var task = Collector(client).CollectAsync(Realization, Scope, [ApiBinding], 1, TimeSpan.FromSeconds(5), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Invalid_scope_or_bindings_fail_before_network()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No network for invalid scope."));
        var collector = Collector(client);
        await Assert.ThrowsAsync<ArgumentException>(() => collector.CollectAsync(Realization, Scope, [ApiBinding, ApiBinding], 1, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ArgumentException>(() => collector.CollectAsync(Realization, Scope with { Subscription = Guid.NewGuid() }, [ApiBinding], 1, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => collector.CollectAsync(Realization, Scope, [ApiBinding], 0, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Redirected_response_is_rejected()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            RequestMessage = new(HttpMethod.Get, "https://other.invalid"), Content = new StringContent(Fixture("site", ApiBinding.ResourceId)) }));
        var result = await Collector(client).CollectAsync(Realization, Scope, [ApiBinding], 1, TimeSpan.FromSeconds(5));
        Assert.Equal("infra.azure.collection.unexpectedEndpoint", Assert.Single(Assert.Single(result.Resources).Diagnostics).Code);
    }

    static string Prefix => $"/subscriptions/{Subscription:D}/resourceGroups/rg/providers/";
    static string Fixture(string name, string id) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")).Replace("{{RESOURCE_ID}}", id);
    static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    static AzureInfrastructureCollector Collector(HttpClient client) => new(client, new FixedClock());
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    class NonSeekableMemoryStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    sealed class BlockingStream(TaskCompletionSource entered) : NonSeekableMemoryStream([])
    {
        internal bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    sealed class TrackingContent(string value) : StringContent(value)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
