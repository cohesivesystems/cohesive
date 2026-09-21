using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Azure.Infra;
using Cohesive.Execution;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using static Cohesive.Adapters.Azure.Infra.Tests.AzureInfrastructureObservationsTests;

namespace Cohesive.Adapters.Azure.Infra.Tests;

public sealed class AzureInfrastructureRuntimeCollectorTests
{
    static readonly AzureInfrastructureRuntimeEndpoint ApiEndpoint = Endpoint(ApiBinding, "api");
    static readonly AzureInfrastructureRuntimeEndpoint StateEndpoint = Endpoint(StateBinding, "state");
    static readonly TimeSpan Age = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Complete_wire_collection_assesses_existing_graph_and_preserves_source_time()
    {
        using var handler = new Handler(request => Response(request.RequestUri == ApiEndpoint.Endpoint ? ApiEndpoint : StateEndpoint));
        var result = await Inspect(handler, [ApiEndpoint, StateEndpoint]);
        Assert.Equal(2, handler.Count);
        Assert.True(result.Assessment.IsReady);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Observations, o => Assert.Equal(Now, o.ObservedAtUtc));
    }

    [Theory]
    [InlineData("http://producer.example/check")]
    [InlineData("https://user:password@producer.example/check")]
    [InlineData("https://producer.example/check?token=private")]
    [InlineData("https://producer.example/check#part")]
    public async Task Invalid_endpoints_fail_before_any_requests(string uri)
    {
        using var handler = new Handler(_ => Response(ApiEndpoint));
        await Assert.ThrowsAsync<ArgumentException>(() => Inspect(handler, [ApiEndpoint with { Endpoint = new(uri) }]));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Duplicate_endpoints_are_rejected_before_io()
    {
        using var handler = new Handler(_ => Response(ApiEndpoint));
        await Assert.ThrowsAsync<ArgumentException>(() => Inspect(handler, [ApiEndpoint, StateEndpoint with { Endpoint = ApiEndpoint.Endpoint }]));
        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [InlineData("wrongScope")]
    [InlineData("wrongDeployment")]
    [InlineData("wrongResource")]
    [InlineData("rawSource")]
    [InlineData("unknownField")]
    [InlineData("duplicateField")]
    [InlineData("legacyHealth")]
    [InlineData("oversize")]
    public async Task Invalid_producer_payloads_fail_closed_without_echoing_payload(string kind)
    {
        using var handler = new Handler(_ =>
        {
            var response = Envelope(ApiEndpoint);
            response = kind switch
            {
                "wrongScope" => response with { Scope = Scope with { Environment = "other" } },
                "wrongDeployment" => response with { Evidence = response.Evidence with { Contract = response.Evidence.Contract with { Deployment = Source } } },
                "wrongResource" => response with { Evidence = response.Evidence with { Observation = Observation(State) } },
                "rawSource" => response with { Evidence = response.Evidence with { Observation = new(Api, ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready, Now, [SourceReference.Create("private", "payload-marker")]) } },
                _ => response
            };
            var json = JsonSerializer.Serialize(response);
            json = kind switch
            {
                "unknownField" => json.Insert(1, "\"private-payload-marker\":true,"),
                "duplicateField" => json.Insert(1, "\"SchemaVersion\":\"private-payload-marker\","),
                "legacyHealth" => "{\"Status\":\"Healthy\",\"Version\":\"private-payload-marker\"}",
                "oversize" => new string('x', 262145),
                _ => json
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        });
        var result = await Inspect(handler, [ApiEndpoint]);
        Assert.False(result.Assessment.IsReady);
        Assert.Empty(result.Observations);
        Assert.Single(result.Diagnostics);
        Assert.DoesNotContain("payload-marker", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "accessDenied")]
    [InlineData(HttpStatusCode.Redirect, "httpFailure")]
    [InlineData(HttpStatusCode.NotFound, "notFound")]
    public async Task Partial_failure_does_not_hide_success_or_fill_missing_child(HttpStatusCode status, string code)
    {
        using var handler = new Handler(request => request.RequestUri == ApiEndpoint.Endpoint ? Response(ApiEndpoint) : new(status));
        var result = await Inspect(handler, [ApiEndpoint, StateEndpoint]);
        Assert.Single(result.Observations);
        Assert.Equal("infra.azure.runtime." + code, Assert.Single(result.Diagnostics).Code);
        Assert.False(result.Assessment.IsReady);
    }

    [Fact]
    public async Task Stale_source_is_unknown_even_after_successful_get()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
            Envelope(ApiEndpoint) with { Evidence = Envelope(ApiEndpoint).Evidence with { Observation = Observation(Api, Now.AddMinutes(-6)) } })) });
        var result = await Inspect(handler, [ApiEndpoint]);
        Assert.Equal(ExecutionReadinessStatus.Unknown, Assert.Single(result.Observations).Readiness);
    }

    [Fact]
    public async Task Cancellation_returns_no_partial_inspection()
    {
        using var handler = new Handler(_ => Response(ApiEndpoint));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Inspect(handler, [ApiEndpoint], cancellation.Token));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public void Declarations_roundtrip_but_must_match_independent_native_outputs()
    {
        var declarations = new AzureInfrastructureRuntimeBindings(AzureInfrastructureRuntimeBindings.CurrentSchemaVersion, Scope, [ApiEndpoint]);
        var parsed = AzureInfrastructureRuntimeBindings.Parse(JsonSerializer.SerializeToUtf8Bytes(declarations));
        var native = new AzureInfrastructureReadinessBindings(AzureInfrastructureReadinessBindings.CurrentSchemaVersion, Scope, [ApiBinding]);
        parsed.Validate(Realization, Scope, native, Now, Age, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => parsed.Validate(Realization, Scope, native with { Bindings = [StateBinding] }, Now, Age, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => AzureInfrastructureRuntimeBindings.Parse(Encoding.UTF8.GetBytes("{\"SchemaVersion\":\"a\",\"SchemaVersion\":\"b\"}")));
    }

    [Fact]
    public async Task Combined_inspection_selects_runtime_only_and_validates_before_arm_reads()
    {
        using var armHandler = new Handler(_ => new(HttpStatusCode.Forbidden));
        using var runtimeHandler = new Handler(request => Response(request.RequestUri == ApiEndpoint.Endpoint ? ApiEndpoint : StateEndpoint));
        using var arm = new HttpClient(armHandler);
        using var runtime = new HttpClient(runtimeHandler);
        var native = new AzureInfrastructureReadinessBindings(AzureInfrastructureReadinessBindings.CurrentSchemaVersion, Scope, [ApiBinding, StateBinding]);
        var declarations = new AzureInfrastructureRuntimeBindings(AzureInfrastructureRuntimeBindings.CurrentSchemaVersion, Scope, [ApiEndpoint, StateEndpoint]);
        async Task<AzureInfrastructureReadinessInspection> Run(AzureInfrastructureRuntimeBindings bindings) =>
            await AzureInfrastructureReadiness.InspectRuntimeAsync(Realization, Scope, native, bindings,
                new(arm, new Clock()), new(runtime, new Clock()), 2, TimeSpan.FromSeconds(5), Age, TimeSpan.Zero, new Clock());
        await Assert.ThrowsAsync<ArgumentException>(() => Run(declarations with { Scope = Scope with { Environment = "other" } }));
        Assert.Equal(0, armHandler.Count);
        Assert.Equal(0, runtimeHandler.Count);
        var result = await Run(declarations);
        Assert.NotNull(result.Runtime);
        Assert.True(result.Assessment.IsReady);
        Assert.Same(result.Runtime.Assessment, result.Assessment);
        Assert.All(result.PlatformHealth.Resources, r => Assert.NotEmpty(r.Diagnostics));
    }

    [Fact]
    public async Task Deadline_and_inflight_cancellation_are_distinct_and_redacted()
    {
        using var handler = new AsyncHandler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(HttpStatusCode.OK); });
        using var client = new HttpClient(handler);
        var collector = new AzureInfrastructureRuntimeCollector(client, new Clock());
        var result = await collector.InspectAsync(Realization, Scope, [ApiEndpoint], 1, TimeSpan.FromMilliseconds(20), Age, TimeSpan.Zero);
        Assert.Equal("infra.azure.runtime.timeout", Assert.Single(result.Diagnostics).Code);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.InspectAsync(Realization, Scope, [ApiEndpoint],
            1, TimeSpan.FromSeconds(5), Age, TimeSpan.Zero, cancellation.Token));
    }

    [Fact]
    public async Task Concurrency_is_bounded_and_every_declared_resource_is_read_once()
    {
        var active = 0;
        var peak = 0;
        var count = 0;
        using var handler = new AsyncHandler(async (request, token) =>
        {
            Interlocked.Increment(ref count);
            var current = Interlocked.Increment(ref active);
            peak = Math.Max(peak, current);
            try { await Task.Delay(10, token); return Response(request.RequestUri == ApiEndpoint.Endpoint ? ApiEndpoint : StateEndpoint); }
            finally { Interlocked.Decrement(ref active); }
        });
        using var client = new HttpClient(handler);
        var result = await new AzureInfrastructureRuntimeCollector(client, new Clock()).InspectAsync(Realization, Scope,
            [ApiEndpoint, StateEndpoint], 1, TimeSpan.FromSeconds(5), Age, TimeSpan.Zero);
        Assert.True(result.Assessment.IsReady);
        Assert.Equal(1, peak);
        Assert.Equal(2, count);
    }

    sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    static AzureInfrastructureRuntimeEndpoint Endpoint(AzureInfrastructureObservationBinding binding, string host) => new(
        new(binding, SourceReference.Create("producer", host), SourceReference.Create("check", "complete/v1"), SourceReference.Create("deployment", "build-1")),
        new Uri("https://" + host + ".example/runtime"));
    static InfrastructureResourceObservation Observation(InfrastructurePhysicalResourceId id, DateTimeOffset? at = null) =>
        new(id, ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready, at ?? Now, [Source]);
    static AzureInfrastructureRuntimeResponse Envelope(AzureInfrastructureRuntimeEndpoint endpoint) =>
        new(AzureInfrastructureRuntimeResponse.CurrentSchemaVersion, Scope, new(endpoint.Contract, Observation(endpoint.Contract.Binding.PhysicalResource)));
    static HttpResponseMessage Response(AzureInfrastructureRuntimeEndpoint endpoint) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(Envelope(endpoint))) };
    static async Task<AzureInfrastructureRuntimeInspection> Inspect(Handler handler, ImmutableArray<AzureInfrastructureRuntimeEndpoint> endpoints,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        return await new AzureInfrastructureRuntimeCollector(client, new Clock()).InspectAsync(Realization, Scope, endpoints,
            maximumConcurrency: 2, requestTimeout: TimeSpan.FromSeconds(5), maximumAge: Age, futureTolerance: TimeSpan.Zero, cancellationToken);
    }
    sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        int count;
        internal int Count => count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref count); return Task.FromResult(respond(request)); }
    }
}
