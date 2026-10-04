using System.Collections.Immutable;
using Cohesive.Infra.Realization;
using Cohesive.Model;


namespace Cohesive.Infra;

/// <summary>
/// Reusable authoring selection pairing a native implementation family with its leaf capability evidence.
/// Selecting it in a deployment contributes both the facility and its evidence to the existing canonical IR.
/// Native objects and construction callbacks are deliberately absent.
/// </summary>
public sealed class InfrastructureTargetImplementation
{
    /// <summary>Creates an implementation selection; capability assertions remain attributable target evidence.</summary>
    /// <param name="id">Stable target-local implementation identity.</param>
    /// <param name="nodeKind">Kind of declaration this implementation can realize.</param>
    /// <param name="evidence">Nonempty native or constrained leaf evidence; composed guarantees use profile rules.</param>
    /// <exception cref="ArgumentException">Identity/evidence is invalid, duplicated, or contains composed evidence.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The node kind is unsupported.</exception>
    public InfrastructureTargetImplementation(InfrastructureTargetFacilityId id, InfrastructureNodeKind nodeKind,
        ImmutableArray<InfrastructureCapabilityEvidence> evidence)
    {
        if (evidence.IsDefaultOrEmpty || evidence.Any(item => item is null || item.Realization == CapabilityRealizationKind.Composed))
            throw new ArgumentException("An implementation requires native or constrained leaf evidence.", nameof(evidence));
        Facility = new(id, nodeKind, [.. evidence.Select(item => item.Id)]);
        Evidence = [.. evidence.OrderBy(item => item.Id.Value, StringComparer.Ordinal)];
    }

    /// <summary>Canonical facility supplied when this implementation is selected.</summary>
    public InfrastructureTargetFacility Facility { get; }
    /// <summary>Immutable leaf evidence contributed to the target profile.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidence> Evidence { get; }
}
