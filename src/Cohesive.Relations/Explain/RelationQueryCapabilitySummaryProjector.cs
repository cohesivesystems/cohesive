using System.Collections.Immutable;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Explain;

/// <summary>
/// Projects a compact, deterministic capability index from canonical target and realization artifacts.
/// </summary>
public static class RelationQueryCapabilitySummaryProjector
{
    /// <summary>Projects every capability and boundary declared by a target profile.</summary>
    /// <param name="targetProfile">Canonical target capability profile to summarize.</param>
    /// <returns>
    /// A profile-only summary whose evidence and boundary identities resolve against
    /// <paramref name="targetProfile"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="targetProfile"/> is <see langword="null"/>.</exception>
    public static RelationQueryCapabilitySummary Project(RelationQueryTargetCapabilityProfile targetProfile)
    {
        ArgumentNullException.ThrowIfNull(targetProfile);
        return ProjectCore(targetProfile, profileFeasibility: null, boundRealization: null);
    }

    /// <summary>Projects target declarations and demand-scoped profile realization references.</summary>
    /// <param name="profileFeasibility">Canonical profile-feasibility report to summarize.</param>
    /// <returns>
    /// A summary whose requirements, target evidence, and operating-boundary identities resolve against the
    /// embedded target profile and whose profile attribution identifies <paramref name="profileFeasibility"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="profileFeasibility"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A decision references a requirement absent from the report.
    /// </exception>
    public static RelationQueryCapabilitySummary Project(RelationQueryRealizationReport profileFeasibility)
    {
        ArgumentNullException.ThrowIfNull(profileFeasibility);
        return ProjectCore(profileFeasibility.TargetProfile, profileFeasibility, boundRealization: null);
    }

    /// <summary>Projects target, profile, and exact contextual realization references.</summary>
    /// <param name="boundRealization">Canonical bound-realization report to summarize.</param>
    /// <returns>
    /// A summary whose contextual-evidence identities resolve against <paramref name="boundRealization"/> and whose
    /// other identities resolve against its embedded profile-feasibility report and target profile.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="boundRealization"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A decision or contextual assessment references a requirement absent from the embedded profile report.
    /// </exception>
    public static RelationQueryCapabilitySummary Project(RelationQueryBoundRealizationReport boundRealization)
    {
        ArgumentNullException.ThrowIfNull(boundRealization);
        return ProjectCore(
            boundRealization.ProfileFeasibility.TargetProfile,
            boundRealization.ProfileFeasibility,
            boundRealization);
    }

    static RelationQueryCapabilitySummary ProjectCore(
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationReport? profileFeasibility,
        RelationQueryBoundRealizationReport? boundRealization)
    {
        Dictionary<RelationQueryCapability, MutableEntry> entries = [];
        HashSet<RelationQueryTargetCapabilityEvidenceId> resolvableEvidence =
        [
            .. targetProfile.Capabilities.Select(static evidence => evidence.Id)
        ];
        HashSet<RelationQueryOperatingBoundaryId> resolvableBoundaries =
        [
            .. targetProfile.OperatingBoundaries.Select(static boundary => boundary.Id)
        ];

        foreach (var evidence in targetProfile.Capabilities)
        {
            var entry = GetOrAdd(entries, evidence.Capability);
            entry.CapabilityEvidence.Add(evidence.Id);
            AddResolvable(entry.OperatingBoundaries, evidence.OperatingBoundaries, resolvableBoundaries);
        }

        if (profileFeasibility is not null)
            AddProfileFeasibility(entries, profileFeasibility, resolvableEvidence, resolvableBoundaries);
        if (boundRealization is not null)
            AddBoundRealization(entries, boundRealization, resolvableEvidence, resolvableBoundaries);

        ImmutableArray<RelationQueryCapabilitySummaryEntry>.Builder projected =
            ImmutableArray.CreateBuilder<RelationQueryCapabilitySummaryEntry>(entries.Count);
        foreach (var entry in entries.Values)
        {
            projected.Add(new(
                entry.Capability,
                [.. entry.Requirements],
                [.. entry.MissingForRequirements],
                [.. entry.CapabilityEvidence],
                [.. entry.OperatingBoundaries],
                [.. entry.ContextEvidence]));
        }

        return new(
            targetProfile.Target,
            targetProfile.Id,
            profileFeasibility?.Policy.Id,
            profileFeasibility?.Fingerprint,
            boundRealization?.Fingerprint,
            [.. resolvableBoundaries],
            projected.MoveToImmutable());
    }

    static void AddProfileFeasibility(
        Dictionary<RelationQueryCapability, MutableEntry> entries,
        RelationQueryRealizationReport report,
        HashSet<RelationQueryTargetCapabilityEvidenceId> resolvableEvidence,
        HashSet<RelationQueryOperatingBoundaryId> resolvableBoundaries)
    {
        Dictionary<RelationQueryRealizationRequirementId, RelationQueryCapability> capabilitiesByRequirement =
            new(report.Requirements.Length);
        foreach (var requirement in report.Requirements)
        {
            capabilitiesByRequirement.Add(requirement.Id, requirement.Capability);
            GetOrAdd(entries, requirement.Capability).Requirements.Add(requirement.Id);
        }

        foreach (var decision in report.Decisions)
        {
            if (!capabilitiesByRequirement.TryGetValue(decision.Requirement, out var capability))
            {
                throw new InvalidOperationException(
                    $"Realization decision references unknown requirement '{decision.Requirement.Value}'.");
            }

            var entry = GetOrAdd(entries, capability);
            AddResolvable(entry.CapabilityEvidence, decision.GetCapabilityEvidence(), resolvableEvidence);
            AddResolvable(
                entry.OperatingBoundaries,
                decision.GetBoundaryValidations().Select(static validation => validation.Boundary),
                resolvableBoundaries);

            if (decision is not UnavailableRelationQueryRealizationDecision unavailable)
                continue;
            foreach (var missing in unavailable.MissingCapabilities)
                GetOrAdd(entries, missing).MissingForRequirements.Add(decision.Requirement);
        }
    }

    static void AddBoundRealization(
        Dictionary<RelationQueryCapability, MutableEntry> entries,
        RelationQueryBoundRealizationReport report,
        HashSet<RelationQueryTargetCapabilityEvidenceId> resolvableEvidence,
        HashSet<RelationQueryOperatingBoundaryId> resolvableBoundaries)
    {
        var capabilitiesByRequirement = report.ProfileFeasibility.Requirements.ToDictionary(
            static requirement => requirement.Id,
            static requirement => requirement.Capability);
        foreach (var assessment in report.Evidence.Assessments)
        {
            if (!capabilitiesByRequirement.TryGetValue(assessment.Requirement, out var capability))
            {
                throw new InvalidOperationException(
                    $"Contextual assessment references unknown requirement '{assessment.Requirement.Value}'.");
            }

            var entry = GetOrAdd(entries, capability);
            entry.ContextEvidence.Add(assessment.Id);
            AddResolvable(entry.CapabilityEvidence, assessment.CapabilityEvidence, resolvableEvidence);
            AddResolvable(entry.OperatingBoundaries, assessment.OperatingBoundaries, resolvableBoundaries);
            if (assessment.FailedOperatingBoundary is { } failed && resolvableBoundaries.Contains(failed))
                entry.OperatingBoundaries.Add(failed);
        }
    }

    static MutableEntry GetOrAdd(
        Dictionary<RelationQueryCapability, MutableEntry> entries,
        RelationQueryCapability capability)
    {
        if (entries.TryGetValue(capability, out var entry))
            return entry;

        entry = new(capability);
        entries.Add(capability, entry);
        return entry;
    }

    static void AddResolvable<T>(HashSet<T> destination, IEnumerable<T> candidates, HashSet<T> resolvable)
        where T : struct
    {
        foreach (var candidate in candidates)
        {
            if (resolvable.Contains(candidate))
                destination.Add(candidate);
        }
    }

    sealed class MutableEntry(RelationQueryCapability capability)
    {
        public RelationQueryCapability Capability { get; } = capability;

        public HashSet<RelationQueryRealizationRequirementId> Requirements { get; } = [];

        public HashSet<RelationQueryRealizationRequirementId> MissingForRequirements { get; } = [];

        public HashSet<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; } = [];

        public HashSet<RelationQueryOperatingBoundaryId> OperatingBoundaries { get; } = [];

        public HashSet<RelationQueryContextEvidenceId> ContextEvidence { get; } = [];
    }
}
