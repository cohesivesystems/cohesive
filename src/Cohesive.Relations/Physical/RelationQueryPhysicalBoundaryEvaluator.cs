using System.Collections.Immutable;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Physical;

/// <summary>Evaluates source capability conditions against one exact bounded physical operation.</summary>
internal static class RelationQueryPhysicalBoundaryEvaluator
{
    public static ImmutableArray<RelationQueryTargetCapabilityEvidence> SelectCompatibleEvidence(
        RelationQuerySourceInstance source,
        RelationQuerySourcePlacementBinding binding,
        RelationQueryPhysicalPlanningPolicy policy,
        IEnumerable<RelationQueryPrimitiveCapabilityKind> primitives,
        long? batchSize = null)
    {
        var requested = primitives.Distinct().Order().ToArray();
        if (requested.Length == 0)
            return [];

        var analysis = RelationQueryTargetCapabilityProfileAnalysis.Analyze(source.TargetProfile);
        Dictionary<RelationQueryTargetCapabilityEvidenceId, RelationQueryTargetCapabilityEvidence> selected = [];
        foreach (var primitive in requested)
        {
            var evidence = analysis.Evidence.Values
                .Where(candidate => candidate.Capability is PrimitiveRelationQueryCapability capability
                    && capability.Kind == primitive
                    && IsCompatible(candidate, analysis.Boundaries, source, binding, policy, batchSize))
                .OrderBy(static candidate => candidate.Id.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            if (evidence is null)
                continue;

            selected[evidence.Id] = evidence;
            foreach (var boundaryId in evidence.OperatingBoundaries)
            {
                var boundary = analysis.Boundaries[boundaryId];
                if (IsDirectlySatisfied(boundary, source, binding, policy, batchSize))
                    continue;
                var validator = FindTargetEnforcement(analysis.Evidence.Values, boundaryId);
                if (validator is not null)
                    selected[validator.Id] = validator;
            }
        }
        return
        [
            .. selected.Values.OrderBy(static evidence => evidence.Id.Value, StringComparer.Ordinal)
        ];
    }

    public static bool IsCompatible(
        RelationQueryTargetCapabilityEvidence evidence,
        IReadOnlyDictionary<RelationQueryOperatingBoundaryId, RelationQueryOperatingBoundary> boundaries,
        RelationQuerySourceInstance source,
        RelationQuerySourcePlacementBinding binding,
        RelationQueryPhysicalPlanningPolicy policy,
        long? batchSize = null) =>
        evidence.OperatingBoundaries.All(boundaryId =>
            boundaries.TryGetValue(boundaryId, out var boundary)
            && IsSatisfied(boundary, source, binding, policy, batchSize));

    public static bool IsSatisfied(
        RelationQueryOperatingBoundary boundary,
        RelationQuerySourceInstance source,
        RelationQuerySourcePlacementBinding binding,
        RelationQueryPhysicalPlanningPolicy policy,
        long? batchSize = null)
    {
        return IsDirectlySatisfied(boundary, source, binding, policy, batchSize)
            || FindTargetEnforcement(source.TargetProfile.Capabilities, boundary.Id) is not null;
    }

    static bool IsDirectlySatisfied(
        RelationQueryOperatingBoundary boundary,
        RelationQuerySourceInstance source,
        RelationQuerySourcePlacementBinding binding,
        RelationQueryPhysicalPlanningPolicy policy,
        long? batchSize)
    {
        var maximumBatchSize = batchSize
            ?? Math.Min(policy.MaximumBatchSize, source.Limits.MaximumBatchSize);
        var maximumRows = Math.Min(
            policy.MaximumLocalRows,
            Math.Min(policy.MaximumBufferedRows, source.Limits.MaximumBufferedRows));
        var maximumFanOut = Math.Min(policy.MaximumFanOut, source.Limits.MaximumFanOut);

        return boundary.Kind switch
        {
            RelationQueryOperatingBoundaryKind.MaximumInputRows =>
                Fits(binding.Acquisition == RelationQuerySourceAcquisitionKind.BoundedLookup
                    ? maximumBatchSize
                    : maximumRows, boundary),
            RelationQueryOperatingBoundaryKind.MaximumOutputRows => Fits(maximumRows, boundary),
            RelationQueryOperatingBoundaryKind.MaximumFanOut => Fits(maximumFanOut, boundary),
            RelationQueryOperatingBoundaryKind.MaximumPageSize => Fits(maximumRows, boundary),
            RelationQueryOperatingBoundaryKind.MaximumBatchSize => Fits(maximumBatchSize, boundary),
            RelationQueryOperatingBoundaryKind.SingleSource => true,
            RelationQueryOperatingBoundaryKind.SinglePartition => binding.Partition is not null,
            _ => false
        };
    }

    static RelationQueryTargetCapabilityEvidence? FindTargetEnforcement(
        IEnumerable<RelationQueryTargetCapabilityEvidence> evidence,
        RelationQueryOperatingBoundaryId boundary) =>
        evidence
            .Where(candidate => candidate.Capability is OperatingBoundaryValidationRelationQueryCapability validation
            && validation.Boundary == boundary
            && candidate.OperatingBoundaries.IsDefaultOrEmpty)
            .OrderBy(static candidate => candidate.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();

    static bool Fits(long value, RelationQueryOperatingBoundary boundary) =>
        boundary.Limit is { } limit && value <= limit;
}
