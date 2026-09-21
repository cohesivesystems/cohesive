using System.Text.Json;
using Cohesive.Adapters.Azure.Infra;
using Cohesive.Execution;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cohesive.Adapters.AspNet.Azure.Infra;

/// <summary>Adapts one explicitly reviewed ASP.NET admission check to the exact Azure runtime evidence contract.</summary>
/// <remarks>No aggregate report, exception, description or data value is published. Construction validates attribution;
/// check execution occurs only after endpoint authorization, never during endpoint mapping.</remarks>
public sealed class AzureRuntimeHealthCheckProducer
{
    readonly AzureInfrastructureObservationScope scope;
    readonly AzureInfrastructureRuntimeContract contract;
    readonly string checkName;
    readonly TimeProvider clock;
    readonly SemaphoreSlim admission = new(1, 1);
    static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>Creates one producer using independently loaded deployment associations and actual host identities.</summary>
    /// <param name="realization">Exact independently retained realization.</param>
    /// <param name="bindings">Native deployment associations.</param>
    /// <param name="runtimeBindings">Reviewed runtime declarations for that same deployment.</param>
    /// <param name="expectedScope">Independently selected scope.</param>
    /// <param name="actualProducer">Producer identity declared by application code, not copied from runtimeBindings.</param>
    /// <param name="actualCheckContract">Versioned complete workload-admission contract implemented by application code.</param>
    /// <param name="actualDeployment">Actual immutable host deployment identity, not copied from runtimeBindings.</param>
    /// <param name="checkName">Exact registered HealthCheck name implementing the declared contract.</param>
    /// <param name="clock">Clock used at the beginning of check execution; defaults to system UTC.</param>
    /// <exception cref="ArgumentException">Attribution, check name or deployment declarations are invalid or ambiguous.</exception>
    /// <exception cref="ArgumentNullException">A required declaration is null.</exception>
    public AzureRuntimeHealthCheckProducer(InfrastructureRealization realization, AzureInfrastructureReadinessBindings bindings,
        AzureInfrastructureRuntimeBindings runtimeBindings, AzureInfrastructureObservationScope expectedScope,
        SourceReference actualProducer, SourceReference actualCheckContract, SourceReference actualDeployment,
        string checkName, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeBindings);
        if (string.IsNullOrWhiteSpace(checkName)) throw new ArgumentException("An exact admission check name is required.", nameof(checkName));
        this.clock = clock ?? TimeProvider.System;
        runtimeBindings.Validate(realization, expectedScope, bindings, this.clock.GetUtcNow(), TimeSpan.FromMinutes(5), TimeSpan.Zero);
        var matches = runtimeBindings.Endpoints.Where(e => e.Contract.Producer == actualProducer).ToArray();
        if (matches.Length != 1 || matches[0].Contract.CheckContract != actualCheckContract || matches[0].Contract.Deployment != actualDeployment)
            throw new ArgumentException("Producer, implemented check contract and actual deployment must match one reviewed declaration.");
        scope = expectedScope;
        contract = matches[0].Contract;
        this.checkName = checkName;
    }

    /// <summary>Runs only the named admission check and emits payload-free canonical evidence.</summary>
    /// <param name="checks">Host health-check service; dependencies are resolved at invocation time.</param>
    /// <param name="cancellationToken">Cancellation propagates without producing evidence.</param>
    /// <returns>Exact response; missing/degraded checks cannot claim readiness. Timestamp precedes all observation work.</returns>
    /// <exception cref="ArgumentNullException">Health-check service is null.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation was requested.</exception>
    /// <exception cref="Exception">Health-check service failures outside an individual check propagate; no evidence is produced.</exception>
    public async Task<AzureInfrastructureRuntimeResponse> ObserveAsync(HealthCheckService checks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        cancellationToken.ThrowIfCancellationRequested();
        await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var at = clock.GetUtcNow();
            var report = await checks.CheckHealthAsync(registration => registration.Name == checkName, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var status = report.Entries.TryGetValue(checkName, out var entry) && report.Entries.Count == 1 ? entry.Status : (HealthStatus?)null;
            var health = status switch
            {
                HealthStatus.Healthy => ExecutionHealthStatus.Healthy,
                HealthStatus.Unhealthy => ExecutionHealthStatus.Unhealthy,
                HealthStatus.Degraded => ExecutionHealthStatus.Degraded,
                _ => ExecutionHealthStatus.Unknown
            };
            var readiness = status switch
            {
                HealthStatus.Healthy => ExecutionReadinessStatus.Ready,
                HealthStatus.Unhealthy or HealthStatus.Degraded => ExecutionReadinessStatus.NotReady,
                _ => ExecutionReadinessStatus.Unknown
            };
            return new(AzureInfrastructureRuntimeResponse.CurrentSchemaVersion, scope,
                new(contract, new(contract.Binding.PhysicalResource, health, readiness, at, [contract.Producer, contract.CheckContract, contract.Deployment])));
        }
        finally { admission.Release(); }
    }

    /// <summary>Maps a protected, uncached producer endpoint without resolving health-check dependencies at startup.</summary>
    /// <param name="endpoints">Application route builder with authentication and authorization configured.</param>
    /// <param name="pattern">Explicit route path; endpoint ownership must agree with the reviewed declaration.</param>
    /// <param name="authorizationPolicy">Named policy requiring the intended authenticated inspection principal.</param>
    /// <returns>Route convention builder retaining required authorization metadata.</returns>
    /// <exception cref="ArgumentNullException">Route builder is null.</exception>
    /// <exception cref="ArgumentException">Pattern or policy is empty.</exception>
    public IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints, string pattern, string authorizationPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(authorizationPolicy))
            throw new ArgumentException("An explicit route and authorization policy are required.");
        return endpoints.MapGet(pattern, async (HttpContext context, HealthCheckService checks) =>
        {
            var response = await ObserveAsync(checks, context.RequestAborted).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(response, JsonOptions);
        }).RequireAuthorization(authorizationPolicy);
    }

    static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = null };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
