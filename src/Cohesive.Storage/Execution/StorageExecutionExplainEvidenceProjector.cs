using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Storage.Materialization;

namespace Cohesive.Storage;

/// <summary>Stable Storage-owned kinds used in common execution explain evidence.</summary>
public static class StorageExecutionExplainEvidenceKinds
{
    /// <summary>Pure Control evaluation decision.</summary>
    public const string ControlDecision = "control.decision";

    /// <summary>Measured Control pressure observation.</summary>
    public const string ControlObservation = "control.observation";

    /// <summary>Non-authoritative Control operating-point recommendation.</summary>
    public const string ControlRecommendation = "control.recommendation";

    /// <summary>Control safe-point actuation result.</summary>
    public const string ControlActuationResult = "control.actuationResult";

    /// <summary>Applied Control actuation receipt.</summary>
    public const string ControlActuation = "control.actuation";

    /// <summary>Effective Control operating point.</summary>
    public const string ControlOperatingPoint = "control.operatingPoint";

    /// <summary>Declared materialization capability requirement.</summary>
    public const string MaterializationRequirement = "materialization.capabilityRequirement";

    /// <summary>Materialization capability realization decision.</summary>
    public const string MaterializationDecision = "materialization.capabilityDecision";

    /// <summary>Durable materialization backend routing state.</summary>
    public const string MaterializationRouting = "materialization.routing";

    /// <summary>Durable materialization source-feed progress.</summary>
    public const string MaterializationSourceProgress = "materialization.sourceProgress";

    /// <summary>Measured materialization backlog.</summary>
    public const string MaterializationBacklog = "materialization.backlog";

    /// <summary>Measured materialization end-to-end lag.</summary>
    public const string MaterializationLag = "materialization.lag";

    /// <summary>Derived materialization generation health.</summary>
    public const string MaterializationGenerationHealth = "materialization.generationHealth";
}

/// <summary>Projects Storage-owned capability and Control authorities into payload-free execution explain claims.</summary>
public static class StorageExecutionExplainEvidenceProjector
{
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
                StorageExecutionExplainEvidenceKinds.ControlDecision,
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
                StorageExecutionExplainEvidenceKinds.ControlRecommendation,
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
            StorageExecutionExplainEvidenceKinds.ControlActuationResult,
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
                StorageExecutionExplainEvidenceKinds.ControlActuation,
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
                StorageExecutionExplainEvidenceKinds.MaterializationRequirement,
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
                StorageExecutionExplainEvidenceKinds.MaterializationDecision,
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

    /// <summary>Projects routing, source progress, backlog, lag, generation health, and Control state.</summary>
    /// <param name="routing">Current exact placement-scoped routing snapshot.</param>
    /// <param name="progress">Current bounded per-source progress snapshots.</param>
    /// <param name="generations">Current target-owned generation snapshots.</param>
    /// <param name="control">Current compiled Control realizations and durable states.</param>
    /// <param name="observation">Supplemental provider-neutral runtime observations.</param>
    /// <param name="provenance">Attributable runtime producer and source evidence.</param>
    /// <returns>Payload-free normalized evidence in deterministic subject order.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A supplied collection is default or contains null.</exception>
    public static ImmutableArray<ExecutionExplainEvidence> ProjectMaterializationStatus(
        MaterializationBackendRoutingSnapshot routing,
        ImmutableArray<MaterializationIndexSyncProgressStatus> progress,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        ImmutableArray<MaterializationIndexSyncControlSnapshot> control,
        MaterializationIndexSyncRuntimeObservation observation,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(provenance);
        MaterializationIndexSyncStatusProjector.ValidateInputs(
            routing,
            progress,
            generations,
            control,
            observation.ChangeLag);

        List<ExecutionExplainEvidence> evidence =
        [
            new(
                stage: ExecutionExplainStageNames.Materialization,
                kind: StorageExecutionExplainEvidenceKinds.MaterializationRouting,
                subject: routing.PlacementSlice.Id.Value,
                authority: ExecutionExplainEvidenceAuthority.Applied,
                status: routing.Revision.Value,
                relatedSubjects:
                [
                    $"activeRead:{routing.ActiveRead?.Generation.ToString() ?? "unavailable"}",
                    $"activeWrite:{routing.ActiveWrite?.ToString() ?? "unavailable"}",
                    $"candidate:{routing.Candidate?.ToString() ?? "unavailable"}",
                    $"pool:{routing.PoolId.Value}"
                ],
                sourceReferences: [provenance.Source.Reference]),
            new(
                stage: ExecutionExplainStageNames.Materialization,
                kind: StorageExecutionExplainEvidenceKinds.MaterializationBacklog,
                subject: routing.PlacementSlice.Id.Value,
                authority: ExecutionExplainEvidenceAuthority.Measured,
                status: MaterializationIndexSyncStatusProjector.GetBacklogCount(generations, observation)
                    .ToString(CultureInfo.InvariantCulture),
                sourceReferences: [provenance.Source.Reference]),
            new(
                stage: ExecutionExplainStageNames.Materialization,
                kind: StorageExecutionExplainEvidenceKinds.MaterializationLag,
                subject: routing.PlacementSlice.Id.Value,
                authority: ExecutionExplainEvidenceAuthority.Measured,
                status: observation.LagMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable",
                relatedSubjects: observation.LagMilliseconds is null ? [] : ["unit:milliseconds"],
                sourceReferences: [provenance.Source.Reference])
        ];

        foreach (var item in progress.OrderBy(static value => value.Generation.TargetId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Generation.GenerationId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Snapshot.Key.Scope.Input.Value, StringComparer.Ordinal))
        {
            var scope = item.Snapshot.Key.Scope;
            evidence.Add(new(
                stage: ExecutionExplainStageNames.Materialization,
                kind: StorageExecutionExplainEvidenceKinds.MaterializationSourceProgress,
                subject: $"{item.Generation}:{scope.Input.Value}:{scope.Partition.Value}:{scope.OrderingScope.Value}",
                authority: ExecutionExplainEvidenceAuthority.Applied,
                status: item.Snapshot.Revision.Value,
                relatedSubjects:
                [
                    $"batch:{item.Snapshot.LatestBatchCheckpoint?.Kind.ToString() ?? "unavailable"}",
                    $"change:{item.Snapshot.LatestChangeCheckpoint?.Kind.ToString() ?? "unavailable"}",
                    $"source:{scope.Source.Value}",
                    $"target:{item.Generation}"
                ],
                sourceReferences: [provenance.Source.Reference]));
        }

        foreach (var item in generations.OrderBy(static value => value.Generation.TargetId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Generation.GenerationId.Value, StringComparer.Ordinal))
        {
            evidence.Add(new(
                stage: ExecutionExplainStageNames.Materialization,
                kind: StorageExecutionExplainEvidenceKinds.MaterializationGenerationHealth,
                subject: item.Generation.ToString(),
                authority: ExecutionExplainEvidenceAuthority.Measured,
                status: MaterializationIndexSyncStatusProjector.GetGenerationHealth(item.Snapshot).ToString(),
                relatedSubjects:
                [
                    $"pendingRetryable:{item.Snapshot.PendingRetryableMutationCount.ToString(CultureInfo.InvariantCulture)}",
                    $"state:{item.Snapshot.State}"
                ],
                sourceReferences: [provenance.Source.Reference]));
        }

        foreach (var snapshot in control.OrderBy(static value => value.Key.TargetId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Key.GenerationId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Key.Workload)
                     .ThenBy(static value => value.Key.LoopId.Value, StringComparer.Ordinal))
        {
            var state = snapshot.State;
            var sourceReferences = SourceReferences(provenance, state.LastObservation?.Source);
            if (state.LastObservation is { } measured)
            {
                evidence.Add(new(
                    stage: ExecutionExplainStageNames.Control,
                    kind: StorageExecutionExplainEvidenceKinds.ControlObservation,
                    subject: measured.Id.Value,
                    authority: ExecutionExplainEvidenceAuthority.Measured,
                    status: state.LastClassification?.ToString() ?? "Unknown",
                    relatedSubjects:
                    [
                        $"backend:{snapshot.Key.TargetId.Value}",
                        $"generation:{snapshot.Key.GenerationId.Value}",
                        $"loop:{state.LoopId.Value}",
                        $"workload:{snapshot.Key.Workload}"
                    ],
                    sourceReferences: sourceReferences));
            }

            if (state.PendingRecommendation is { } recommendation)
            {
                evidence.Add(new(
                    stage: ExecutionExplainStageNames.Control,
                    kind: StorageExecutionExplainEvidenceKinds.ControlRecommendation,
                    subject: recommendation.Id.Value,
                    authority: ExecutionExplainEvidenceAuthority.Recommended,
                    status: recommendation.Direction.ToString(),
                    relatedSubjects:
                    [
                        $"loop:{state.LoopId.Value}",
                        $"operatingPoint:{FormatOperatingPoint(recommendation.ProposedOperatingPoint)}",
                        $"observation:{recommendation.ObservationId.Value}"
                    ],
                    sourceReferences: sourceReferences));
            }

            evidence.Add(new(
                stage: ExecutionExplainStageNames.Control,
                kind: StorageExecutionExplainEvidenceKinds.ControlOperatingPoint,
                subject: state.LoopId.Value,
                authority: ExecutionExplainEvidenceAuthority.Applied,
                status: FormatOperatingPoint(state.OperatingPoint),
                relatedSubjects:
                [
                    $"actuation:{state.LastAppliedActuationId?.Value ?? "unavailable"}",
                    $"generation:{snapshot.Key.GenerationId.Value}",
                    $"revision:{state.Revision.Ordinal.ToString(CultureInfo.InvariantCulture)}"
                ],
                sourceReferences: sourceReferences));
        }

        return [.. evidence];
    }

    static string FormatOperatingPoint(ControlOperatingPoint point) => string.Join(
        ",",
        point.Values.Select(static value =>
            $"{value.Actuator}={value.Quantity.Value.ToString(CultureInfo.InvariantCulture)}:{value.Quantity.Unit}"));

    internal static ImmutableArray<string> SourceReferences(
        ExecutionProvenance provenance,
        string? additionalReference)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (additionalReference is null
            || string.Equals(additionalReference, provenance.Source.Reference, StringComparison.Ordinal))
        {
            return [provenance.Source.Reference];
        }

        return string.CompareOrdinal(provenance.Source.Reference, additionalReference) < 0
            ? [provenance.Source.Reference, additionalReference]
            : [additionalReference, provenance.Source.Reference];
    }
}
