using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Azure.Infra;
using Cohesive.Adapters.Azure.Infra;
using Cohesive.Execution;
using Cohesive.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using static Cohesive.Adapters.Azure.Infra.Tests.AzureInfrastructureObservationsTests;

namespace Cohesive.Adapters.AspNet.Azure.Infra.Tests;

public sealed class AzureRuntimeHealthCheckProducerTests
{
    const string CheckName = "workload-admission";
    static readonly SourceReference Producer = SourceReference.Create("producer", "api");
    static readonly SourceReference Contract = SourceReference.Create("check", "workload-admission/v1");
    static readonly SourceReference Deployment = SourceReference.Create("application-deployment", "build-42");
    static readonly AzureInfrastructureRuntimeBindings Runtime = new(AzureInfrastructureRuntimeBindings.CurrentSchemaVersion, Scope,
        [new(new(ApiBinding, Producer, Contract, Deployment), new Uri("https://api.example/runtime"))]);
    static readonly AzureInfrastructureReadinessBindings Native = new(AzureInfrastructureReadinessBindings.CurrentSchemaVersion, Scope, [ApiBinding]);

    [Theory]
    [InlineData(HealthStatus.Healthy, ExecutionReadinessStatus.Ready)]
    [InlineData(HealthStatus.Unhealthy, ExecutionReadinessStatus.NotReady)]
    [InlineData(HealthStatus.Degraded, ExecutionReadinessStatus.NotReady)]
    public async Task Named_check_is_the_only_authority_and_payload_is_redacted(HealthStatus status, ExecutionReadinessStatus expected)
    {
        var calls = 0;
        var services = new ServiceCollection().AddLogging();
        services.AddHealthChecks().AddCheck(CheckName, () =>
        {
            calls++;
            return new HealthCheckResult(status, "private-description", new Exception("private-exception"),
                new Dictionary<string, object> { ["secret"] = "private-data" });
        }).AddCheck("unrelated", () => throw new Exception("must not run"));
        using var provider = services.BuildServiceProvider();
        var response = await Create().ObserveAsync(provider.GetRequiredService<HealthCheckService>());
        Assert.Equal(1, calls);
        Assert.Equal(expected, response.Evidence.Observation.Readiness);
        Assert.Equal(Now, response.Evidence.Observation.ObservedAtUtc);
        Assert.Equal(Deployment, response.Evidence.Contract.Deployment);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(response));
        var normalized = AzureInfrastructureRuntimeObservations.Normalize(Realization, Scope, Scope,
            [Runtime.Endpoints[0].Contract], [response.Evidence], Now, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        Assert.Equal(expected, Assert.Single(normalized).Readiness);
    }

    [Fact]
    public async Task Missing_named_check_cannot_inherit_healthy_empty_report()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHealthChecks();
        using var provider = services.BuildServiceProvider();
        var result = await Create().ObserveAsync(provider.GetRequiredService<HealthCheckService>());
        Assert.Equal(ExecutionReadinessStatus.Unknown, result.Evidence.Observation.Readiness);
    }

    [Fact]
    public void Actual_identity_mismatch_is_rejected_before_health_resolution()
    {
        Assert.Throws<ArgumentException>(() => Create(deployment: Source));
        Assert.Throws<ArgumentException>(() => Create(producer: Source));
        Assert.Throws<ArgumentException>(() => Create(checkContract: Source));
        Assert.Throws<ArgumentException>(() => Create(runtime: Runtime with { Scope = Scope with { Environment = "prod" } }));
    }

    [Fact]
    public async Task Mapping_requires_authorization_and_does_not_construct_check_dependencies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddHealthChecks().AddCheck<ThrowingCheck>(CheckName);
        await using var app = builder.Build();
        var producer = Create();
        producer.Map(app, "/runtime", "inspection-reader");
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints));
        Assert.Equal("inspection-reader", Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        Assert.Throws<ArgumentException>(() => producer.Map(app, "/other", ""));
    }

    [Fact]
    public async Task Cancellation_does_not_publish_a_completed_observation()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHealthChecks();
        using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().ObserveAsync(provider.GetRequiredService<HealthCheckService>(), cancellation.Token));
    }

    [Fact]
    public async Task Concurrent_requests_do_not_multiply_check_execution_and_preserve_source_time()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var services = new ServiceCollection().AddLogging();
        services.AddHealthChecks().Add(new HealthCheckRegistration(CheckName,
            _ => new AsyncCheck(async cancellation =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellation);
                return HealthCheckResult.Healthy();
            }), null, null));
        using var provider = services.BuildServiceProvider();
        var checks = provider.GetRequiredService<HealthCheckService>();
        var clock = new MutableClock();
        var producer = new AzureRuntimeHealthCheckProducer(Realization, Native, Runtime, Scope,
            Producer, Contract, Deployment, CheckName, clock);
        var first = producer.ObserveAsync(checks);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var waiting = producer.ObserveAsync(checks, cancellation.Token);
        Assert.Equal(1, calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        clock.Current = Now.AddMinutes(10);
        release.SetResult();
        var response = await first;
        Assert.Equal(Now, response.Evidence.Observation.ObservedAtUtc);
        Assert.Equal(1, calls);
        var normalized = AzureInfrastructureRuntimeObservations.Normalize(Realization, Scope, Scope,
            [Runtime.Endpoints[0].Contract], [response.Evidence], clock.Current, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        Assert.DoesNotContain(normalized, observation => observation.Readiness == ExecutionReadinessStatus.Ready);
    }

    [Fact]
    public void Producer_artifact_rejects_incomplete_duplicate_and_oversized_inputs()
    {
        var path = Path.GetTempFileName();
        try
        {
            var artifact = new AzureRuntimeProducerArtifact(AzureRuntimeProducerArtifact.CurrentSchemaVersion, Realization, Native, Runtime);
            File.WriteAllText(path, JsonSerializer.Serialize(artifact));
            Assert.NotNull(AzureRuntimeProducerArtifact.Read(path).CreateProducer(Scope.Environment, Scope.Tenant,
                Scope.Subscription, Scope.Handoff, Producer, Contract, Deployment, CheckName));
            File.WriteAllText(path, "{\"SchemaVersion\":\"first\",\"SchemaVersion\":\"second\"}");
            Assert.Throws<ArgumentException>(() => AzureRuntimeProducerArtifact.Read(path));
            File.WriteAllText(path, "{}");
            Assert.Throws<ArgumentException>(() => AzureRuntimeProducerArtifact.Read(path));
            File.WriteAllBytes(path, new byte[4 * 1024 * 1024 + 1]);
            Assert.Throws<ArgumentException>(() => AzureRuntimeProducerArtifact.Read(path));
        }
        finally { File.Delete(path); }
    }

    sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    sealed class AsyncCheck(Func<CancellationToken, Task<HealthCheckResult>> execute) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) => execute(cancellationToken);
    }

    [Fact]
    public async Task Http_pipeline_denies_untrusted_readers_before_execution_and_preserves_wire_contract()
    {
        var calls = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorizationBuilder().AddPolicy("reader", policy => policy.RequireAuthenticatedUser().RequireClaim("reader", "allowed"));
        builder.Services.AddHealthChecks().AddCheck(CheckName, () => { calls++; return HealthCheckResult.Healthy(); });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        Create().Map(app, "/runtime", "reader");
        await app.StartAsync();
        try
        {
            var url = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            using var anonymous = await client.GetAsync("/runtime");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            client.DefaultRequestHeaders.Add("Fixture-Reader", "other");
            using var forbidden = await client.GetAsync("/runtime");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal(0, calls);
            client.DefaultRequestHeaders.Remove("Fixture-Reader");
            client.DefaultRequestHeaders.Add("Fixture-Reader", "allowed");
            using var response = await client.GetAsync("/runtime");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var payload = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"SchemaVersion\"", payload);
            Assert.DoesNotContain("\"schemaVersion\"", payload);
            Assert.Equal(Deployment, JsonSerializer.Deserialize<AzureInfrastructureRuntimeResponse>(payload)!.Evidence.Contract.Deployment);
            Assert.Equal(1, calls);
        }
        finally { await app.StopAsync(); }
    }

    sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var reader = Request.Headers["Fixture-Reader"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(reader) ? AuthenticateResult.NoResult() :
                AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("reader", reader)], Scheme.Name)), Scheme.Name)));
        }
    }

    static AzureRuntimeHealthCheckProducer Create(SourceReference? producer = null, SourceReference? checkContract = null,
        SourceReference? deployment = null, AzureInfrastructureRuntimeBindings? runtime = null) =>
        new(Realization, Native, runtime ?? Runtime, Scope, producer ?? Producer, checkContract ?? Contract,
            deployment ?? Deployment, CheckName, new Clock());
    sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    sealed class ThrowingCheck : IHealthCheck
    {
        public ThrowingCheck() => throw new InvalidOperationException("Must not resolve during endpoint mapping.");
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
