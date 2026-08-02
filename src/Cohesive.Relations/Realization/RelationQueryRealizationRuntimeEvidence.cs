using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;

namespace Cohesive.Relations.Realization;

/// <summary>Projects runtime evidence that is authoritatively established by an exact successful realization.</summary>
public static class RelationQueryRealizationRuntimeEvidence
{
    const string EvidenceReferencePrefix = "relation-query-realization-target";

    /// <summary>Projects available expression-capability evidence from one exact successful target realization.</summary>
    /// <param name="plan">Exact compiled semantic plan whose capability inputs are projected.</param>
    /// <param name="realization">Exact successful target realization for <paramref name="plan"/>.</param>
    /// <returns>Available capability evidence in canonical compiled-input order.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The realization is unsuccessful or belongs to another compiled semantic plan.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A shape snapshot cannot be represented by the compiled-plan canonicalization profile.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A shape snapshot cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    public static ImmutableArray<RelationQueryCapabilityEvidence> ProjectCapabilities(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(realization);
        if (!realization.IsRealizable
            || !realization.Plan.GetMismatchedComponents(plan).IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Runtime capability evidence requires one exact successful semantic-plan realization.",
                nameof(realization));
        }

        var evidenceReference = string.Concat(
            EvidenceReferencePrefix,
            "/",
            Uri.EscapeDataString(realization.TargetProfile.Target.Value),
            "/profile/",
            Uri.EscapeDataString(realization.TargetProfile.Id.Value));
        return
        [
            .. plan.InputContract.Capabilities
                .Select(capability => new RelationQueryCapabilityEvidence(
                    input: capability.Input.Id,
                    state: RelationQueryCapabilityEvidenceState.Available,
                    evidenceReference: evidenceReference))
        ];
    }
}
