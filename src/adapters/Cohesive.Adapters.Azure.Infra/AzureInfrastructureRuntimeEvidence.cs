using System.Collections.Immutable;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Azure.Infra;

/// <summary>Independently trusted runtime admission contract for one exact physical resource.</summary>
/// <param name="Binding">Checked native association, never inferred from an endpoint.</param>
/// <param name="Producer">Exact nonsecret producer identity; not an authentication credential.</param>
/// <param name="CheckContract">Versioned check contract defining the complete readiness claim for this resource; not an aggregate HTTP status.</param>
/// <param name="Deployment">Exact application deployment identity, independently associated with the selected infrastructure handoff.</param>
public sealed record AzureInfrastructureRuntimeContract(
    AzureInfrastructureObservationBinding Binding, SourceReference Producer,
    SourceReference CheckContract, SourceReference Deployment);

/// <summary>Payload-free result from an authenticated, trusted runtime producer; construction does not admit evidence.</summary>
/// <param name="Contract">Producer-reported attribution, compared against independently trusted expectations.</param>
/// <param name="Observation">Canonical result and source timestamp for exactly the contract's resource, without raw diagnostics.</param>
public sealed record AzureInfrastructureRuntimeEvidence(
    AzureInfrastructureRuntimeContract Contract, InfrastructureResourceObservation Observation);

/// <summary>Pure runtime attribution admission over the existing Azure scope, freshness and observation boundary.</summary>
public static class AzureInfrastructureRuntimeObservations
{
    /// <summary>Admits runtime results without deriving readiness from HTTP, platform health or parent resources.</summary>
    /// <param name="realization">Authoritative complete realization used by the existing evaluator.</param>
    /// <param name="expectedScope">Independently trusted infrastructure deployment scope.</param>
    /// <param name="observedScope">Producer-retained scope; must match exactly.</param>
    /// <param name="contracts">One trusted complete admission contract per physical resource. Omitted resources remain uncovered.</param>
    /// <param name="evidence">At most one result per contract. Missing results remain absent, never synthesized as ready.</param>
    /// <param name="assessedAtUtc">Explicit UTC assessment clock.</param>
    /// <param name="maximumAge">Positive maximum source age, inclusive.</param>
    /// <param name="futureTolerance">Nonnegative permitted clock lead, inclusive.</param>
    /// <returns>Deterministically ordered canonical observations retaining runtime attribution and original source time.</returns>
    /// <exception cref="ArgumentNullException">A required scope or realization is null.</exception>
    /// <exception cref="ArgumentException">Collections, attribution, native identity, scope or diagnostic payloads are invalid, duplicated or conflicting.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The time policy is invalid.</exception>
    public static ImmutableArray<InfrastructureResourceObservation> Normalize(
        InfrastructureRealization realization,
        AzureInfrastructureObservationScope expectedScope,
        AzureInfrastructureObservationScope observedScope,
        ImmutableArray<AzureInfrastructureRuntimeContract> contracts,
        ImmutableArray<AzureInfrastructureRuntimeEvidence> evidence,
        DateTimeOffset assessedAtUtc, TimeSpan maximumAge, TimeSpan futureTolerance)
    {
        if (contracts.IsDefault || evidence.IsDefault)
            throw new ArgumentException("Runtime contract and evidence collections must be initialized.");
        var expected = new Dictionary<InfrastructurePhysicalResourceId, AzureInfrastructureRuntimeContract>();
        var bindings = ImmutableArray.CreateBuilder<AzureInfrastructureObservationBinding>(contracts.Length);
        foreach (var contract in contracts)
        {
            if (contract?.Binding is null || string.IsNullOrWhiteSpace(contract.Producer.Value)
                || string.IsNullOrWhiteSpace(contract.CheckContract.Value) || string.IsNullOrWhiteSpace(contract.Deployment.Value)
                || !expected.TryAdd(contract.Binding.PhysicalResource, contract))
                throw new ArgumentException("Runtime contracts require unique native bindings and explicit producer, check and deployment identities.");
            bindings.Add(contract.Binding);
        }
        var admitted = ImmutableArray.CreateBuilder<AzureInfrastructureEvidence>(evidence.Length);
        foreach (var item in evidence)
        {
            if (item?.Contract?.Binding is null || item.Observation is null
                || !expected.TryGetValue(item.Observation.PhysicalResource, out var contract) || item.Contract != contract)
                throw new ArgumentException("Runtime evidence does not match an independently trusted admission contract.");
            var observation = item.Observation;
            // Add only checked attribution. The common boundary still owns identity, duplicates, diagnostics and freshness.
            var sources = observation.SourceReferences.ToHashSet();
            sources.Add(contract.Producer);
            sources.Add(contract.CheckContract);
            sources.Add(contract.Deployment);
            admitted.Add(new(contract.Binding, AzureInfrastructureEvidenceKind.Runtime,
                new(observation.PhysicalResource, observation.Health, observation.Readiness, observation.ObservedAtUtc,
                    SourceReference.NormalizeSet([.. sources], requireNonEmpty: true), observation.Diagnostics)));
        }
        return AzureInfrastructureObservations.Normalize(realization, expectedScope, observedScope,
            bindings.MoveToImmutable(), admitted.MoveToImmutable(), assessedAtUtc, maximumAge, futureTolerance);
    }
}
