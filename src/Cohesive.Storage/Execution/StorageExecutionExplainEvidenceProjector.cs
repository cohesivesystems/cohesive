using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Storage.Materialization;

namespace Cohesive.Storage;

/// <summary>Projects Storage-owned capability and Control authorities into payload-free execution explain claims.</summary>
public static class StorageExecutionExplainEvidenceProjector
{
    const string ControlDecisionKind = "control.decision";
    const string ControlRecommendationKind = "control.recommendation";
    const string ControlActuationResultKind = "control.actuationResult";
    const string ControlActuationKind = "control.actuation";
    const string MaterializationRequirementKind = "materialization.capabilityRequirement";
    const string MaterializationDecisionKind = "materialization.capabilityDecision";

    /// <summary>Projects one pure Control decision without its observation values, target, or timestamps.</summary>
    /// <param name="decision">Existing Control decision authority.</param>
    /// <param name="provenance">Attribution for the Control interpreter or adapter that produced the decision.</param>
    /// <returns>One decision claim and, when present, one separate non-authoritative recommendation claim.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="decision"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    public static ImmutableArray<ExecutionExplainEvidence> ProjectControlDecision(
        ControlDecision decision,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(provenance);
        var state = decision.State;
        var count = decision.Recommendation is null ? 1 : 2;
        var evidence = ImmutableArray.CreateBuilder<ExecutionExplainEvidence>(count);
        evidence.Add(new(
            ExecutionExplainStageNames.Control,
            ControlDecisionKind,
            state.LoopId.Value,
            ExecutionExplainEvidenceAuthority.Interpreted,
            decision.Disposition.ToString(),
            relatedSubjects:
            [
                $"epoch:{state.Epoch.Value}",
                $"revision:{state.Revision.Ordinal.ToString(CultureInfo.InvariantCulture)}",
                $"definition:{state.DefinitionFingerprint.Value}"
            ],
            sourceReferences: [provenance.Source.Reference]));
        if (decision.Recommendation is { } recommendation)
        {
            evidence.Add(new(
                ExecutionExplainStageNames.Control,
                ControlRecommendationKind,
                recommendation.Id.Value,
                ExecutionExplainEvidenceAuthority.Recommended,
                recommendation.Direction.ToString(),
                relatedSubjects:
                [
                    $"loop:{recommendation.LoopId.Value}",
                    $"epoch:{recommendation.Epoch.Value}",
                    $"observation:{recommendation.ObservationId.Value}",
                    $"definition:{recommendation.DefinitionFingerprint.Value}"
                ],
                sourceReferences: [provenance.Source.Reference]));
        }
        return evidence.MoveToImmutable();
    }

    /// <summary>Projects one Control actuation attempt without observation values, target, or timestamps.</summary>
    /// <param name="result">Existing safe-point actuation result.</param>
    /// <param name="provenance">Attribution for the runtime authority that attempted actuation.</param>
    /// <returns>One result claim and, when present, one separate applied-actuation receipt claim.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="result"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    public static ImmutableArray<ExecutionExplainEvidence> ProjectControlActuation(
        ControlActuationResult result,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(provenance);
        var state = result.State;
        var count = result.Actuation is null ? 1 : 2;
        var evidence = ImmutableArray.CreateBuilder<ExecutionExplainEvidence>(count);
        evidence.Add(new(
            ExecutionExplainStageNames.Control,
            ControlActuationResultKind,
            state.LoopId.Value,
            result.Disposition is ControlActuationDisposition.Applied or ControlActuationDisposition.Replayed
                ? ExecutionExplainEvidenceAuthority.Applied
                : ExecutionExplainEvidenceAuthority.Interpreted,
            result.Disposition.ToString(),
            relatedSubjects:
            [
                $"epoch:{state.Epoch.Value}",
                $"revision:{state.Revision.Ordinal.ToString(CultureInfo.InvariantCulture)}",
                $"definition:{state.DefinitionFingerprint.Value}"
            ],
            sourceReferences: [provenance.Source.Reference]));
        if (result.Actuation is { } actuation)
        {
            evidence.Add(new(
                ExecutionExplainStageNames.Control,
                ControlActuationKind,
                actuation.Id.Value,
                ExecutionExplainEvidenceAuthority.Applied,
                result.Disposition.ToString(),
                relatedSubjects:
                [
                    $"recommendation:{actuation.Recommendation.Id.Value}",
                    $"applicationPoint:{actuation.ApplicationPoint.Id.Value}",
                    $"applicationSource:{actuation.ApplicationPoint.SourceReference}",
                    $"revision:{actuation.Revision.Ordinal.ToString(CultureInfo.InvariantCulture)}"
                ],
                sourceReferences: string.Equals(
                    provenance.Source.Reference,
                    actuation.ApplicationPoint.SourceReference,
                    StringComparison.Ordinal)
                    ? [provenance.Source.Reference]
                    : [provenance.Source.Reference, actuation.ApplicationPoint.SourceReference]));
        }
        return evidence.MoveToImmutable();
    }

    /// <summary>Projects materialization requirements and their exact capability decisions without copying profiles.</summary>
    /// <param name="match">Existing deterministic materialization capability match.</param>
    /// <returns>Declared requirement claims paired with adapter-supplied or unavailable realization decisions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="match"/> is <see langword="null"/>.</exception>
    public static ImmutableArray<ExecutionExplainEvidence> ProjectMaterializationCapabilities(
        MaterializationCapabilityMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var evidence = ImmutableArray.CreateBuilder<ExecutionExplainEvidence>(match.Decisions.Length * 2);
        foreach (var decision in match.Decisions)
        {
            var requirement = decision.Requirement;
            evidence.Add(new(
                ExecutionExplainStageNames.Materialization,
                MaterializationRequirementKind,
                requirement.Id.Value,
                ExecutionExplainEvidenceAuthority.Declared,
                requirement.Capability.ToString(),
                relatedSubjects:
                [
                    .. requirement.Guarantees.Select(static guarantee => $"guarantee:{guarantee}"),
                    $"modes:{requirement.Modes}"
                ]));
            evidence.Add(new(
                ExecutionExplainStageNames.Materialization,
                MaterializationDecisionKind,
                requirement.Id.Value,
                decision.Evidence is null
                    ? ExecutionExplainEvidenceAuthority.Interpreted
                    : ExecutionExplainEvidenceAuthority.AdapterSupplied,
                decision.Realization.ToString(),
                decision.Realization,
                relatedSubjects: decision.Evidence is null
                    ? []
                    :
                    [
                        $"evidence:{decision.Evidence.Id.Value}",
                        $"capability:{decision.Evidence.Capability}"
                    ],
                sourceReferences: decision.Evidence?.SourceReferences ?? []));
        }
        return evidence.MoveToImmutable();
    }
}
