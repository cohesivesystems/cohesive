using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Azure.Infra;

/// <summary>Redacted native Azure evidence; native states never establish application admission.</summary>
/// <param name="Evidence">Exact canonical association, observation time and evidence authority.</param>
/// <param name="ProvisioningState">Recognized provisioning token, or null when unavailable/unrecognized.</param>
/// <param name="SiteState">Recognized App Service state, or null for other services/unknown states.</param>
/// <param name="Diagnostics">Collector-owned diagnostics without provider bodies, headers or exceptions.</param>
/// <param name="AvailabilityState">Recognized Resource Health token, or null for other evidence/failed collection.</param>
public sealed record AzureInfrastructureCollectedResource(
    AzureInfrastructureEvidence Evidence, string? ProvisioningState, string? SiteState,
    ImmutableArray<DocumentValidationDiagnostic> Diagnostics, string? AvailabilityState = null);

/// <summary>One completed, attributable Azure capture; not an application readiness assertion.</summary>
/// <param name="Scope">Reviewed collection scope; caller authentication must enforce its tenant.</param>
/// <param name="StartedAtUtc">Capture start time.</param>
/// <param name="CompletedAtUtc">Capture completion time; observations retain native source times when available, otherwise request completion times.</param>
/// <param name="Resources">Per-binding results in physical-resource order, including partial failures.</param>
public sealed record AzureInfrastructureCollection(
    AzureInfrastructureObservationScope Scope, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    ImmutableArray<AzureInfrastructureCollectedResource> Resources);

/// <summary>Bounded, read-only ARM collection for explicitly associated Azure resources.</summary>
/// <remarks>
/// Uses the public Azure management endpoint only. The caller owns and authenticates the HttpClient for the
/// expected tenant and ARM audience, disables redirects, and ensures its transport honors cancellation.
/// No keys, list operations, automatic retries, caches or application health endpoints are used.
/// </remarks>
public sealed class AzureInfrastructureCollector
{
    const int MaximumResponseBytes = 262144;
    readonly HttpClient client;
    readonly TimeProvider clock;

    /// <summary>Creates a collector without taking ownership of the supplied client.</summary>
    /// <param name="client">Authenticated caller-owned client with redirects disabled and cancellation-aware transport.</param>
    /// <param name="clock">Clock used for request/capture timestamps, or the system UTC clock.</param>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    public AzureInfrastructureCollector(HttpClient client, TimeProvider? clock = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Collects exact management-plane evidence, retaining failures per resource.</summary>
    /// <param name="realization">Authoritative realization used to fence bindings before any I/O.</param>
    /// <param name="scope">Explicit expected environment, tenant, subscription, realization and handoff.</param>
    /// <param name="bindings">One reviewed binding per physical identity; equivalent ARM reads are shared within this call only.</param>
    /// <param name="maximumConcurrency">Maximum concurrent unique reads, from 1 through 32.</param>
    /// <param name="requestTimeout">Per-read deadline after scheduling, positive and at most ten minutes.</param>
    /// <param name="cancellationToken">Caller cancellation; cancellation throws rather than returning partial success.</param>
    /// <returns>A redacted capture. Even successful reads produce provisioning-only, Unknown runtime observations.</returns>
    /// <exception cref="ArgumentNullException">The realization or scope is null.</exception>
    /// <exception cref="ArgumentException">Scope/bindings are malformed, mismatched or duplicated.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Concurrency or timeout is outside the supported bounds.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation was requested.</exception>
    public Task<AzureInfrastructureCollection> CollectAsync(
        InfrastructureRealization realization, AzureInfrastructureObservationScope scope,
        ImmutableArray<AzureInfrastructureObservationBinding> bindings, int maximumConcurrency,
        TimeSpan requestTimeout, CancellationToken cancellationToken = default) =>
        CollectAsync(realization, scope, bindings, maximumConcurrency, requestTimeout,
            AzureInfrastructureEvidenceKind.Provisioning, cancellationToken);

    /// <summary>Collects Azure platform availability, never application admission.</summary>
    /// <param name="realization">Authoritative realization used to fence bindings before I/O.</param>
    /// <param name="scope">Exact expected environment, tenant, subscription, realization and handoff.</param>
    /// <param name="bindings">Reviewed physical associations. Only App Service sites and Cosmos accounts are supported.</param>
    /// <param name="maximumConcurrency">Maximum concurrent unique reads, from 1 through 32.</param>
    /// <param name="requestTimeout">Per-read deadline after scheduling, positive and at most ten minutes.</param>
    /// <param name="cancellationToken">Caller cancellation; no completed partial capture is returned.</param>
    /// <returns>Platform health with original reportedTime and Unknown readiness; failures remain resource-scoped.</returns>
    /// <exception cref="ArgumentNullException">The realization or scope is null.</exception>
    /// <exception cref="ArgumentException">Scope/bindings are malformed, mismatched or duplicated.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Concurrency or timeout is outside supported bounds.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation was requested.</exception>
    public Task<AzureInfrastructureCollection> CollectPlatformHealthAsync(
        InfrastructureRealization realization, AzureInfrastructureObservationScope scope,
        ImmutableArray<AzureInfrastructureObservationBinding> bindings, int maximumConcurrency,
        TimeSpan requestTimeout, CancellationToken cancellationToken = default) =>
        CollectAsync(realization, scope, bindings, maximumConcurrency, requestTimeout,
            AzureInfrastructureEvidenceKind.PlatformHealth, cancellationToken);

    async Task<AzureInfrastructureCollection> CollectAsync(
        InfrastructureRealization realization, AzureInfrastructureObservationScope scope,
        ImmutableArray<AzureInfrastructureObservationBinding> bindings, int maximumConcurrency,
        TimeSpan requestTimeout, AzureInfrastructureEvidenceKind kind, CancellationToken cancellationToken)
    {
        if (maximumConcurrency is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        var started = clock.GetUtcNow();
        // Reuse the admission boundary's graph/scope/ARM validation once before any network operation.
        AzureInfrastructureObservations.Normalize(realization, scope, scope, bindings, [], started, TimeSpan.FromTicks(1), TimeSpan.Zero);
        var groups = bindings.GroupBy(b => b.ResourceId, StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new NativeResult[groups.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, groups.Length),
            new ParallelOptions { MaxDegreeOfParallelism = maximumConcurrency, CancellationToken = cancellationToken },
            async (index, token) => results[index] = await ReadAsync(groups[index].Key, kind, requestTimeout, token));
        cancellationToken.ThrowIfCancellationRequested();
        var resources = ImmutableArray.CreateBuilder<AzureInfrastructureCollectedResource>(bindings.Length);
        for (var index = 0; index < groups.Length; index++)
        {
            var result = results[index];
            foreach (var binding in groups[index])
            {
                var observation = new InfrastructureResourceObservation(binding.PhysicalResource,
                    result.Health, ExecutionReadinessStatus.Unknown, result.At,
                    [SourceReference.Create(kind == AzureInfrastructureEvidenceKind.PlatformHealth ? "azure-resource-health-get" : "azure-arm-get", result.ApiVersion ?? "unsupported")]);
                var evidence = new AzureInfrastructureEvidence(binding,
                    result.Error is null ? kind : AzureInfrastructureEvidenceKind.CollectionFailed,
                    observation);
                resources.Add(new(evidence, result.ProvisioningState, result.SiteState,
                    result.Error is null ? [] : [new DocumentValidationDiagnostic(
                        Code: "infra.azure.collection." + result.Error, Severity: DiagnosticSeverity.Warning,
                        Message: "Azure evidence could not be admitted; inspect identity, access and collector coverage.",
                        Location: "/resources", SchemaLocation: binding.PhysicalResource.Value)], result.AvailabilityState));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(scope, started, clock.GetUtcNow(),
            resources.OrderBy(r => r.Evidence.Binding.PhysicalResource.Value, StringComparer.Ordinal).ToImmutableArray());
    }

    async Task<NativeResult> ReadAsync(string resourceId, AzureInfrastructureEvidenceKind kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var segments = resourceId.Split('/');
        var resourceType = segments[6] + "/" + string.Join('/', segments.Where((_, i) => i >= 7 && i % 2 == 1));
        var platform = kind == AzureInfrastructureEvidenceKind.PlatformHealth;
        var version = platform
            ? resourceType.ToLowerInvariant() is "microsoft.web/sites" or "microsoft.documentdb/databaseaccounts" ? "2025-05-01" : null
            : resourceType.ToLowerInvariant() switch
        {
            "microsoft.web/sites" => "2025-03-01",
            "microsoft.documentdb/databaseaccounts" => "2025-10-15",
            "microsoft.documentdb/databaseaccounts/sqldatabases" => "2025-10-15",
            "microsoft.durabletask/schedulers" => "2025-11-01",
            "microsoft.durabletask/schedulers/taskhubs" => "2025-11-01",
            _ => null
        };
        NativeResult Failure(string code) => new(clock.GetUtcNow(), version, null, null, code);
        if (version is null) return Failure("unsupportedResource");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var responseId = platform ? resourceId + "/providers/Microsoft.ResourceHealth/availabilityStatuses/current" : resourceId;
            var responseType = platform ? "Microsoft.ResourceHealth/availabilityStatuses" : resourceType;
            var path = string.Join('/', responseId.Split('/').Select(Uri.EscapeDataString));
            var uri = new Uri("https://management.azure.com" + path + "?api-version=" + version);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            // A caller misconfigured redirect handling must never admit the redirected response.
            if (response.RequestMessage?.RequestUri is { } effective && effective != uri) return Failure("unexpectedEndpoint");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Failure("accessDenied");
            if (response.StatusCode == HttpStatusCode.NotFound) return Failure("notFound");
            if (response.StatusCode != HttpStatusCode.OK) return Failure("httpFailure");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) return Failure("responseTooLarge");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, deadline.Token);
                if (count == 0) break;
                if (buffer.Length + count > MaximumResponseBytes) return Failure("responseTooLarge");
                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicates(root)) return Failure("invalidResponse");
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), responseId, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), responseType, StringComparison.OrdinalIgnoreCase)) return Failure("identityMismatch");
            if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object || HasDuplicates(properties))
                return Failure("invalidResponse");
            if (platform)
            {
                var availability = Token(properties, "availabilityState", ["Available", "Unavailable", "Degraded", "Unknown"]);
                if (availability is null) return Failure("unsupportedAvailabilityState");
                // Request completion cannot make an old provider check fresh. Missing/ambiguous source time is not evidence.
                if (!properties.TryGetProperty("reportedTime", out var reported) || reported.ValueKind != JsonValueKind.String
                    || !reported.TryGetDateTimeOffset(out var at) || at.Offset != TimeSpan.Zero
                    || !(reported.GetString()!.EndsWith('Z') || reported.GetString()!.EndsWith("+00:00", StringComparison.Ordinal)))
                    return Failure("invalidSourceTime");
                var health = availability switch
                {
                    "Available" => ExecutionHealthStatus.Healthy,
                    "Unavailable" => ExecutionHealthStatus.Unhealthy,
                    "Degraded" => ExecutionHealthStatus.Degraded,
                    _ => ExecutionHealthStatus.Unknown
                };
                return new(at, version, null, null, null, availability, health);
            }
            // Allowlisted state tokens only; arbitrary provider property text never enters the artifact.
            var provisioning = Token(properties, "provisioningState", ["Succeeded", "Failed", "Canceled", "Creating", "Updating", "Deleting", "Accepted"]);
            var site = resourceType.Equals("Microsoft.Web/sites", StringComparison.OrdinalIgnoreCase)
                ? Token(properties, "state", ["Running", "Stopped"]) : null;
            return new(clock.GetUtcNow(), version, provisioning, site, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("timeout"); }
        catch (HttpRequestException) { return Failure("transportFailure"); }
        catch (IOException) { return Failure("transportFailure"); }
        catch (JsonException) { return Failure("invalidResponse"); }
    }

    static bool HasDuplicates(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(p => !names.Add(p.Name));
    }

    static string? Token(JsonElement properties, string name, string[] allowed) =>
        properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? allowed.FirstOrDefault(candidate => string.Equals(candidate, value.GetString(), StringComparison.OrdinalIgnoreCase))
            : null;

    sealed record NativeResult(DateTimeOffset At, string? ApiVersion, string? ProvisioningState, string? SiteState, string? Error,
        string? AvailabilityState = null, ExecutionHealthStatus Health = ExecutionHealthStatus.Unknown);
}
