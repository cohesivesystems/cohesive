using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Infra.Realization;
using Cohesive.Model.Serialization;
using Cohesive.Model;

namespace Cohesive.Adapters.Azure.Infra;

/// <summary>Trusted association of a runtime contract with an explicitly authorized HTTPS endpoint.</summary>
/// <param name="Contract">Independently reviewed complete runtime admission contract.</param>
/// <param name="Endpoint">Exact nonsecret HTTPS URL; no user info, query or fragment. Not inferred from native resource names.</param>
public sealed record AzureInfrastructureRuntimeEndpoint(AzureInfrastructureRuntimeContract Contract, Uri Endpoint);

/// <summary>Versioned producer response. Scope and evidence remain untrusted until admission.</summary>
/// <param name="SchemaVersion">Must equal CurrentSchemaVersion.</param>
/// <param name="Scope">Infrastructure deployment scope retained by the producer.</param>
/// <param name="Evidence">One runtime result with original source time and exact attribution.</param>
public sealed record AzureInfrastructureRuntimeResponse(string SchemaVersion, AzureInfrastructureObservationScope Scope,
    AzureInfrastructureRuntimeEvidence Evidence)
{
    /// <summary>Exact supported producer wire contract; default System.Text.Json property naming.</summary>
    public const string CurrentSchemaVersion = "cohesive.azure-runtime-evidence/1";
}

/// <summary>Runtime inspection retaining failures separately from admitted canonical observations.</summary>
/// <param name="Scope">Independently selected infrastructure scope.</param>
/// <param name="AssessedAtUtc">UTC assessment time after collection.</param>
/// <param name="Observations">Admitted observations; failed or missing producers stay absent.</param>
/// <param name="Diagnostics">Sanitized collection diagnostics, keyed to physical resources.</param>
/// <param name="Assessment">Existing evaluator's assessment over the full realization.</param>
public sealed record AzureInfrastructureRuntimeInspection(AzureInfrastructureObservationScope Scope, DateTimeOffset AssessedAtUtc,
    ImmutableArray<InfrastructureResourceObservation> Observations, ImmutableArray<DocumentValidationDiagnostic> Diagnostics,
    InfrastructureReadinessAssessment Assessment);

/// <summary>Bounded HTTPS collection of exact runtime envelopes. Never parses aggregate application health JSON.</summary>
/// <remarks>The caller owns the client, authenticates authorized producers, disables redirects, and ensures cancellation-aware transport.
/// Use a dedicated client: never forward ARM credentials to runtime endpoints. Endpoint declarations must be independently trusted.
/// This collector authenticates neither user-supplied declarations nor deployment claims by itself.</remarks>
public sealed class AzureInfrastructureRuntimeCollector
{
    readonly HttpClient client;
    readonly TimeProvider clock;
    static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Creates a collector without taking ownership of the client.</summary>
    /// <param name="client">Dedicated caller-owned HTTPS client with redirects disabled; no ARM credentials.</param>
    /// <param name="clock">UTC capture/assessment clock, or system clock.</param>
    /// <exception cref="ArgumentNullException">Client is null.</exception>
    public AzureInfrastructureRuntimeCollector(HttpClient client, TimeProvider? clock = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Validates all declarations before I/O, collects once per endpoint, and assesses the complete realization.</summary>
    /// <param name="realization">Independently trusted realization.</param>
    /// <param name="scope">Explicit trusted deployment scope.</param>
    /// <param name="endpoints">One complete contract and unique HTTPS endpoint per physical resource.</param>
    /// <param name="maximumConcurrency">Maximum simultaneous requests, 1–32.</param>
    /// <param name="requestTimeout">Positive deadline per scheduled request, at most ten minutes.</param>
    /// <param name="maximumAge">Positive maximum source age at final assessment.</param>
    /// <param name="futureTolerance">Nonnegative source clock tolerance.</param>
    /// <param name="cancellationToken">Cancellation aborts without a completed partial report.</param>
    /// <returns>Canonical runtime assessment and redacted partial failures. No retry, cache or platform fallback.</returns>
    /// <exception cref="ArgumentException">A declaration, scope or binding is invalid or duplicated.</exception>
    /// <exception cref="ArgumentNullException">Realization or scope is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Limits or time policy are invalid.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation was requested.</exception>
    public async Task<AzureInfrastructureRuntimeInspection> InspectAsync(InfrastructureRealization realization,
        AzureInfrastructureObservationScope scope, ImmutableArray<AzureInfrastructureRuntimeEndpoint> endpoints,
        int maximumConcurrency, TimeSpan requestTimeout, TimeSpan maximumAge, TimeSpan futureTolerance,
        CancellationToken cancellationToken = default)
    {
        if (maximumConcurrency is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        Validate(realization, scope, endpoints, clock.GetUtcNow(), maximumAge, futureTolerance);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new (AzureInfrastructureRuntimeEvidence? Evidence, string? Error)[endpoints.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, endpoints.Length),
            new ParallelOptions { MaxDegreeOfParallelism = maximumConcurrency, CancellationToken = cancellationToken },
            async (i, token) => results[i] = await ReadAsync(scope, endpoints[i], requestTimeout, token));
        cancellationToken.ThrowIfCancellationRequested();
        var at = clock.GetUtcNow();
        var evidence = ImmutableArray.CreateBuilder<AzureInfrastructureRuntimeEvidence>();
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i].Evidence is { } item) evidence.Add(item);
            else diagnostics.Add(new(Code: "infra.azure.runtime." + results[i].Error, Severity: DiagnosticSeverity.Warning,
                Message: "Runtime evidence could not be admitted; inspect producer contract, attribution and access.",
                Location: "/runtime", SchemaLocation: endpoints[i].Contract.Binding.PhysicalResource.Value));
        }
        var observations = AzureInfrastructureRuntimeObservations.Normalize(realization, scope, scope,
            [.. endpoints.Select(e => e.Contract)], evidence.ToImmutable(), at, maximumAge, futureTolerance);
        var assessment = InfrastructureReadinessEvaluator.Assess(realization, observations);
        cancellationToken.ThrowIfCancellationRequested();
        return new(scope, at, observations,
            diagnostics.OrderBy(d => d.SchemaLocation, StringComparer.Ordinal).ToImmutableArray(), assessment);
    }

    /// <summary>Validates independently trusted endpoint declarations without credentials or network access.</summary>
    /// <param name="realization">Expected full realization.</param>
    /// <param name="scope">Trusted exact deployment scope.</param>
    /// <param name="endpoints">Unique physical-resource contracts and unique nonsecret HTTPS endpoints.</param>
    /// <param name="at">Explicit UTC validation time.</param>
    /// <param name="maximumAge">Positive maximum source age.</param>
    /// <param name="futureTolerance">Nonnegative clock tolerance.</param>
    /// <exception cref="ArgumentException">Declarations, scope or identity are invalid.</exception>
    /// <exception cref="ArgumentNullException">Realization or scope is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Time policy is invalid.</exception>
    public static void Validate(InfrastructureRealization realization, AzureInfrastructureObservationScope scope,
        ImmutableArray<AzureInfrastructureRuntimeEndpoint> endpoints, DateTimeOffset at, TimeSpan maximumAge, TimeSpan futureTolerance)
    {
        if (endpoints.IsDefault) throw new ArgumentException("Runtime endpoints must be initialized.");
        var seen = new HashSet<Uri>();
        foreach (var endpoint in endpoints)
            if (endpoint?.Contract is null || endpoint.Endpoint is not { IsAbsoluteUri: true } uri
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !seen.Add(uri))
                throw new ArgumentException("Runtime endpoints require unique explicit nonsecret HTTPS URLs.");
        AzureInfrastructureRuntimeObservations.Normalize(realization, scope, scope,
            [.. endpoints.Select(e => e.Contract)], [], at, maximumAge, futureTolerance);
    }

    async Task<(AzureInfrastructureRuntimeEvidence? Evidence, string? Error)> ReadAsync(AzureInfrastructureObservationScope scope,
        AzureInfrastructureRuntimeEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var result = await AzureInfrastructureHttpReader.ReadAsync(client, endpoint.Endpoint, deadline.Token);
            using var document = result.Document;
            if (result.Error is not null) return (null, result.Error);
            if (StrictDocumentJson.TryFindDuplicateProperty(document!.RootElement, "", out _)) return (null, "invalidResponse");
            var response = document.Deserialize<AzureInfrastructureRuntimeResponse>(JsonOptions);
            if (response is null || response.SchemaVersion != AzureInfrastructureRuntimeResponse.CurrentSchemaVersion) return (null, "unsupportedContract");
            var evidence = response.Evidence;
            if (response.Scope != scope || evidence?.Contract != endpoint.Contract || evidence.Observation is not { } observation
                || observation.PhysicalResource != endpoint.Contract.Binding.PhysicalResource || !observation.Diagnostics.IsEmpty)
                return (null, "attributionMismatch");
            // HTTP producers cannot smuggle arbitrary payload text through provenance references.
            var allowedSources = new[] { endpoint.Contract.Binding.Source, endpoint.Contract.Producer,
                endpoint.Contract.CheckContract, endpoint.Contract.Deployment, scope.Handoff };
            if (observation.SourceReferences.Any(source => !allowedSources.Contains(source))) return (null, "attributionMismatch");
            // Graph traversal and freshness admission happen once, after all reads, at the final assessment time.
            return (evidence, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (null, "timeout"); }
        catch (HttpRequestException) { return (null, "transportFailure"); }
        catch (IOException) { return (null, "transportFailure"); }
        catch (JsonException) { return (null, "invalidResponse"); }
        catch (ArgumentException) { return (null, "attributionMismatch"); }
    }
}
