using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Azure.Infra;

/// <summary>Exact collection scope; attribution does not authenticate a producer.</summary>
/// <param name="Environment">Explicit product environment.</param>
/// <param name="Tenant">Explicit Azure tenant; never selected from ambient credentials.</param>
/// <param name="Subscription">Explicit Azure subscription.</param>
/// <param name="Realization">Exact semantic realization reference.</param>
/// <param name="Handoff">Exact handoff provenance supplied by the deployment adapter.</param>
public sealed record AzureInfrastructureObservationScope(
    string Environment, Guid Tenant, Guid Subscription,
    InfrastructureRealizationReference Realization, SourceReference Handoff);

/// <summary>Attributable association from a canonical physical resource to a native Azure resource.</summary>
/// <param name="PhysicalResource">Exact physical identity in the realization.</param>
/// <param name="ResourceId">Full subscription-scoped ARM resource ID, not a hostname or symbolic ID.</param>
/// <param name="Source">Provenance of the association, normally a checked native deployment output.</param>
public sealed record AzureInfrastructureObservationBinding(
    InfrastructurePhysicalResourceId PhysicalResource, string ResourceId, SourceReference Source);

/// <summary>Distinct evidence authority; provisioning cannot establish runtime admission.</summary>
public enum AzureInfrastructureEvidenceKind
{
    /// <summary>Native provisioning evidence only; runtime status remains unknown.</summary>
    Provisioning,
    /// <summary>Operational evidence normalized by a trusted runtime-specific producer.</summary>
    Runtime,
    /// <summary>Collection failed; this does not imply the resource itself is unhealthy.</summary>
    CollectionFailed
}

/// <summary>Payload-free evidence from a trusted source, fenced before canonical assessment.</summary>
/// <param name="Binding">Exact association used by the source.</param>
/// <param name="Kind">Whether the source establishes provisioning, runtime behavior or collection failure.</param>
/// <param name="Observation">Canonical statuses, source timestamp and references. Must contain no raw provider diagnostics.</param>
public sealed record AzureInfrastructureEvidence(
    AzureInfrastructureObservationBinding Binding, AzureInfrastructureEvidenceKind Kind,
    InfrastructureResourceObservation Observation);

/// <summary>Pure Azure evidence admission boundary. Performs no I/O or authentication.</summary>
public static class AzureInfrastructureObservations
{
    /// <summary>Admits exact, fresh operational evidence for the existing readiness evaluator.</summary>
    /// <param name="realization">Authoritative realization; no replacement dependency graph is constructed.</param>
    /// <param name="expectedScope">Trusted expected deployment scope.</param>
    /// <param name="observedScope">Scope retained by the evidence producer; must match exactly.</param>
    /// <param name="bindings">Expected native associations; immutable and unique per physical resource.</param>
    /// <param name="evidence">At most one evidence item per bound physical resource.</param>
    /// <param name="assessedAtUtc">Explicit UTC assessment time.</param>
    /// <param name="maximumAge">Positive maximum age; exact boundary is admitted.</param>
    /// <param name="futureTolerance">Nonnegative permitted source-clock lead.</param>
    /// <returns>Canonical observations in physical-ID order, preserving original source times. Missing bindings remain absent for the evaluator.</returns>
    /// <exception cref="ArgumentNullException">A required reference is null.</exception>
    /// <exception cref="ArgumentException">Scope, binding, observation identity, diagnostic payload or collection is invalid or contradictory.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Time policy or evidence kind is invalid.</exception>
    public static ImmutableArray<InfrastructureResourceObservation> Normalize(
        InfrastructureRealization realization,
        AzureInfrastructureObservationScope expectedScope,
        AzureInfrastructureObservationScope observedScope,
        ImmutableArray<AzureInfrastructureObservationBinding> bindings,
        ImmutableArray<AzureInfrastructureEvidence> evidence,
        DateTimeOffset assessedAtUtc, TimeSpan maximumAge, TimeSpan futureTolerance)
    {
        ArgumentNullException.ThrowIfNull(realization);
        ArgumentNullException.ThrowIfNull(expectedScope);
        ArgumentNullException.ThrowIfNull(observedScope);
        if (string.IsNullOrWhiteSpace(expectedScope.Environment)
            || expectedScope.Tenant == Guid.Empty || expectedScope.Subscription == Guid.Empty
            || expectedScope.Realization is null || string.IsNullOrWhiteSpace(expectedScope.Handoff.Value)
            || expectedScope.Realization != realization.ToReference() || observedScope != expectedScope)
            throw new ArgumentException("Azure observation scope must match the exact expected environment, tenant, subscription, realization and handoff.");
        if (assessedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Assessment time must be UTC.", nameof(assessedAtUtc));
        if (maximumAge <= TimeSpan.Zero || futureTolerance < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Age must be positive and clock tolerance nonnegative.");
        if (bindings.IsDefault || evidence.IsDefault)
            throw new ArgumentException("Observation collections must be initialized.");

        var physical = realization.WorkloadPlacements.Select(p => p.PhysicalResource)
            .Concat(realization.Lifecycle.Bindings.Select(b => b.PhysicalResource)).ToHashSet();
        var expected = new Dictionary<InfrastructurePhysicalResourceId, AzureInfrastructureObservationBinding>();
        foreach (var binding in bindings)
        {
            if (binding is null || !physical.Contains(binding.PhysicalResource)
                || string.IsNullOrWhiteSpace(binding.Source.Value))
                throw new ArgumentException("A binding must identify a realized physical resource and its provenance.");
            ValidateResourceId(binding.ResourceId, expectedScope.Subscription);
            if (!expected.TryAdd(binding.PhysicalResource, binding))
                throw new ArgumentException("Duplicate physical-resource binding.");
        }

        var seen = new HashSet<InfrastructurePhysicalResourceId>();
        var observations = ImmutableArray.CreateBuilder<InfrastructureResourceObservation>(evidence.Length);
        foreach (var item in evidence)
        {
            if (item?.Binding is null || item.Observation is null)
                throw new ArgumentException("Evidence requires a binding and observation.");
            var observation = item.Observation;
            if (!expected.TryGetValue(observation.PhysicalResource, out var binding) || binding != item.Binding
                || !seen.Add(observation.PhysicalResource))
                throw new ArgumentException("Evidence is duplicated or does not match the exact expected native association.");
            if (!Enum.IsDefined(item.Kind))
                throw new ArgumentOutOfRangeException(nameof(evidence), "Unsupported evidence authority.");
            // Public diagnostics are constructed here, never copied from provider exception/response payloads.
            if (!observation.Diagnostics.IsEmpty)
                throw new ArgumentException("Evidence must not carry provider diagnostics; use a classified collection failure.");

            string? reason = null;
            if (observation.ObservedAtUtc - assessedAtUtc > futureTolerance) reason = "future";
            else if (assessedAtUtc - observation.ObservedAtUtc > maximumAge) reason = "stale";
            else if (item.Kind == AzureInfrastructureEvidenceKind.Provisioning) reason = "provisioningOnly";
            else if (item.Kind == AzureInfrastructureEvidenceKind.CollectionFailed) reason = "collectionFailed";

            var sources = new HashSet<SourceReference>
            {
                binding.Source, expectedScope.Handoff,
                SourceReference.Create("azure-resource", binding.ResourceId),
                SourceReference.Create("azure-observation-scope", $"{expectedScope.Environment}/{expectedScope.Tenant:D}/{expectedScope.Subscription:D}"),
                SourceReference.Create("infrastructure-realization", realization.Fingerprint.Value)
            };
            sources.UnionWith(observation.SourceReferences);
            observations.Add(new(observation.PhysicalResource,
                reason is null ? observation.Health : ExecutionHealthStatus.Unknown,
                reason is null ? observation.Readiness : ExecutionReadinessStatus.Unknown,
                observation.ObservedAtUtc, SourceReference.NormalizeSet([.. sources], requireNonEmpty: true),
                reason is null ? [] : [new DocumentValidationDiagnostic(
                    Code: $"infra.azure.observation.{reason}", Severity: DiagnosticSeverity.Warning,
                    Message: "Azure evidence does not establish current operational readiness.",
                    Location: "/observations", SchemaLocation: observation.PhysicalResource.Value)]));
        }
        return observations.OrderBy(o => o.PhysicalResource.Value, StringComparer.Ordinal).ToImmutableArray();
    }

    static void ValidateResourceId(string id, Guid subscription)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains('%') || id.Contains('?') || id.Contains('#') || id.Contains('\\'))
            throw new ArgumentException("Expected an unescaped subscription-scoped ARM resource ID.");
        var segments = id.Split('/');
        if (segments.Length < 9 || segments[0] != "" || !segments[1].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(segments[2], "D", out var actual) || actual != subscription
            || !segments[3].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase)
            || !segments[5].Equals("providers", StringComparison.OrdinalIgnoreCase)
            || (segments.Length - 7) % 2 != 0
            || segments.Skip(1).Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".."))
            throw new ArgumentException("ARM identity must retain explicit subscription, resource group, provider and type/name pairs.");
    }
}
