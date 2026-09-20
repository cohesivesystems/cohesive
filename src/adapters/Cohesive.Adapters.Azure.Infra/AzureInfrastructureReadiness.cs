using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Azure.Infra;

/// <summary>Versioned deployment artifact associating canonical identities with actual native IDs; not health evidence.</summary>
/// <param name="SchemaVersion">Exact supported artifact version.</param>
/// <param name="Scope">Deployment environment, explicit Azure scope and exact realization/handoff attribution.</param>
/// <param name="Bindings">Native IDs supplied by checked resource outputs, never inferred from symbolic names.</param>
public sealed record AzureInfrastructureReadinessBindings(string SchemaVersion, AzureInfrastructureObservationScope Scope,
    ImmutableArray<AzureInfrastructureObservationBinding> Bindings)
{
    /// <summary>Current native binding artifact contract.</summary>
    public const string CurrentSchemaVersion = "cohesive.azure-readiness-bindings/1";

    /// <summary>Revalidates a persisted artifact against independently trusted scope before credentials or I/O.</summary>
    /// <param name="realization">Independently selected canonical realization.</param>
    /// <param name="expectedScope">Trusted expected scope, never inferred from this artifact.</param>
    /// <param name="assessedAtUtc">Explicit UTC validation instant.</param>
    /// <param name="maximumAge">Positive source-age limit.</param>
    /// <param name="futureTolerance">Nonnegative source-clock tolerance.</param>
    /// <exception cref="ArgumentException">Version, scope, bindings or time policy are invalid.</exception>
    /// <exception cref="ArgumentNullException">A required reference is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Freshness policy is invalid.</exception>
    public void Validate(InfrastructureRealization realization, AzureInfrastructureObservationScope expectedScope,
        DateTimeOffset assessedAtUtc, TimeSpan maximumAge, TimeSpan futureTolerance)
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("Unsupported Azure readiness binding artifact version.");
        AzureInfrastructureObservations.Normalize(realization, expectedScope, Scope, Bindings, [],
            assessedAtUtc, maximumAge, futureTolerance);
    }

}

/// <summary>Completed read-only inspection; missing coverage is a valid non-ready result.</summary>
/// <param name="Bindings">Exact admitted binding artifact.</param>
/// <param name="AssessedAtUtc">Explicit assessment clock after collection.</param>
/// <param name="MaximumAge">Freshness policy applied to source timestamps.</param>
/// <param name="FutureTolerance">Permitted source-clock lead.</param>
/// <param name="Provisioning">Companion native management evidence; never used to manufacture readiness.</param>
/// <param name="PlatformHealth">Platform evidence selected for assessment, including coverage/collection failures.</param>
/// <param name="Assessment">Canonical assessment over the entire supplied realization.</param>
public sealed record AzureInfrastructureReadinessInspection(AzureInfrastructureReadinessBindings Bindings, DateTimeOffset AssessedAtUtc,
    TimeSpan MaximumAge, TimeSpan FutureTolerance, AzureInfrastructureCollection Provisioning,
    AzureInfrastructureCollection PlatformHealth, InfrastructureReadinessAssessment Assessment);

/// <summary>Native binding projection and bounded platform inspection over the canonical infrastructure realization.</summary>
public static class AzureInfrastructureReadiness
{
    /// <summary>Projects actual native resource IDs onto the compiled declaration at the native output seam.</summary>
    /// <param name="plan">Exact compiled deployment owning canonical placements.</param>
    /// <param name="scope">Explicit provider scope and exact handoff attribution.</param>
    /// <param name="nativeResourceIds">Canonical node to actual noncredential ARM ID; no names or URLs.</param>
    /// <param name="source">Caller-supplied nonsecret native-output provenance retained on every association.</param>
    /// <returns>A validated, deterministic binding artifact. Unbound nodes stay absent for assessment.</returns>
    /// <exception cref="ArgumentException">A node is absent, scope/ID is invalid, or physical associations conflict.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static AzureInfrastructureReadinessBindings Bind(InfrastructureTargetDeploymentPlan plan,
        AzureInfrastructureObservationScope scope, IReadOnlyDictionary<InfrastructureNodeId, string> nativeResourceIds, SourceReference source)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(nativeResourceIds);
        if (string.IsNullOrWhiteSpace(source.Value)) throw new ArgumentException("Native output provenance is required.", nameof(source));
        if (!plan.IsComplete || plan.Realization is null) throw new ArgumentException("A complete realization is required.", nameof(plan));
        var realization = plan.Realization;
        var physical = new Dictionary<InfrastructurePhysicalResourceId, AzureInfrastructureObservationBinding>();
        foreach (var (node, id) in nativeResourceIds)
        {
            var placement = plan.Manifest.Workloads.SingleOrDefault(w => w.Workload == node)?.PhysicalResource
                ?? plan.Manifest.Resources.SingleOrDefault(r => r.Resource == node)?.PhysicalResource
                ?? throw new ArgumentException("Native output refers to an undeclared canonical node.", nameof(nativeResourceIds));
            var binding = new AzureInfrastructureObservationBinding(placement, id,
                source);
            if (physical.TryGetValue(placement, out var existing) && existing.ResourceId != id)
                throw new ArgumentException("One physical resource has contradictory native IDs.", nameof(nativeResourceIds));
            physical[placement] = binding;
        }
        var bindings = physical.Values.OrderBy(b => b.PhysicalResource.Value, StringComparer.Ordinal).ToImmutableArray();
        AzureInfrastructureObservations.Normalize(realization, scope, scope, bindings, [], DateTimeOffset.UnixEpoch,
            TimeSpan.FromTicks(1), TimeSpan.Zero);
        return new(AzureInfrastructureReadinessBindings.CurrentSchemaVersion, scope, bindings);
    }

    /// <summary>Collects a bounded, read-only report without replacing the canonical dependency graph.</summary>
    /// <param name="realization">Exact expected realization, independently selected by the caller.</param>
    /// <param name="expectedScope">Trusted expected scope, independently selected rather than copied from an untrusted artifact.</param>
    /// <param name="bindings">Deserialized native-output artifact, revalidated before I/O.</param>
    /// <param name="collector">Caller-owned, explicitly authenticated shared Azure collector.</param>
    /// <param name="maximumConcurrency">Maximum concurrent native reads (1–32).</param>
    /// <param name="requestTimeout">Positive per-read deadline, at most ten minutes.</param>
    /// <param name="maximumAge">Positive maximum source age.</param>
    /// <param name="futureTolerance">Nonnegative source-clock tolerance.</param>
    /// <param name="clock">Explicit UTC clock; caller controls assessment reproducibility.</param>
    /// <param name="cancellationToken">Cancellation propagates without returning a completed partial inspection.</param>
    /// <returns>Both captures and the full canonical assessment; non-ready is not a command failure.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The artifact version, scope, binding or policy is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Collection limits or freshness policy are invalid.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation was requested.</exception>
    public static async Task<AzureInfrastructureReadinessInspection> InspectPlatformHealthAsync(InfrastructureRealization realization,
        AzureInfrastructureObservationScope expectedScope, AzureInfrastructureReadinessBindings bindings,
        AzureInfrastructureCollector collector, int maximumConcurrency, TimeSpan requestTimeout,
        TimeSpan maximumAge, TimeSpan futureTolerance, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(clock);
        bindings.Validate(realization, expectedScope, clock.GetUtcNow(), maximumAge, futureTolerance);
        var provisioning = await collector.CollectAsync(realization, expectedScope, bindings.Bindings,
            maximumConcurrency, requestTimeout, cancellationToken).ConfigureAwait(false);
        var platform = await collector.CollectPlatformHealthAsync(realization, expectedScope, bindings.Bindings,
            maximumConcurrency, requestTimeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var assessedAt = clock.GetUtcNow();
        // Explicit initial policy: platform evidence is the sole assessment input. Provisioning stays a companion artifact.
        // Missing/unsupported dependencies remain in the realization and cannot be pruned by collector coverage.
        var observations = AzureInfrastructureObservations.Normalize(realization, expectedScope, platform.Scope, bindings.Bindings,
            [.. platform.Resources.Select(r => r.Evidence)], assessedAt, maximumAge, futureTolerance);
        var assessment = InfrastructureReadinessEvaluator.Assess(realization, observations);
        cancellationToken.ThrowIfCancellationRequested();
        return new(bindings, assessedAt, maximumAge, futureTolerance, provisioning, platform, assessment);
    }
}
