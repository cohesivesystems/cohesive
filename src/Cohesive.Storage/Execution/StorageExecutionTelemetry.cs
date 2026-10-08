using System.Collections.Immutable;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;

namespace Cohesive.Storage;

/// <summary>Projects and records Storage runtime evidence through the common execution observability contract.</summary>
public static class StorageExecutionTelemetry
{
    /// <summary>Records bounded checkpoint metrics from one complete durable Process checkpoint.</summary>
    /// <param name="checkpoint">Canonical durable checkpoint to observe.</param>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    public static void RecordCheckpoint(ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!ExecutionTelemetry.IsEnabled)
        {
            return;
        }

        var status = ProcessDurableExecutionStatusProjector.Project(checkpoint);
        var outcome = GetOutcome(checkpoint.Continuation.Terminal.Kind);

        long retryCount = 0;
        long backlogCount = 0;
        foreach (var operation in checkpoint.DurableOperations)
        {
            retryCount = SaturatingAdd(retryCount, Math.Max(0, operation.Attempts.Length - 1L));
            if (operation.Status != DurableOperationStatus.Dispositioned)
            {
                backlogCount = SaturatingAdd(backlogCount, 1);
            }
        }

        var activity = ExecutionTelemetry.StartActivity(ExecutionTelemetryActivityKind.Checkpoint);
        ExecutionTelemetry.RecordStatus(status);
        ExecutionTelemetry.RecordCheckpoint(
            signalCount: checkpoint.Inbox.Length,
            pendingSignalCount: checkpoint.Inbox.Count(static input => input.Receipt is null),
            retryCount: retryCount,
            backlogCount: backlogCount,
            outcome: outcome);
        ExecutionTelemetry.CompleteActivity(activity, outcome);
    }

    /// <summary>Records the measured and optional recommended authorities represented by one Control decision.</summary>
    /// <param name="decision">Existing pure Control decision authority.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    public static void RecordControlDecision(ControlDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var outcome = decision.Disposition switch
        {
            ControlDecisionDisposition.Held => ExecutionTelemetryOutcome.Observed,
            ControlDecisionDisposition.Recommended => ExecutionTelemetryOutcome.Pending,
            ControlDecisionDisposition.Replayed => ExecutionTelemetryOutcome.Replayed,
            ControlDecisionDisposition.Rejected => ExecutionTelemetryOutcome.Rejected,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision.Disposition,
                "Unsupported Control decision disposition.")
        };
        var activity = ExecutionTelemetry.StartActivity(ExecutionTelemetryActivityKind.ControlDecision);
        ExecutionTelemetry.RecordControl(ExecutionExplainEvidenceAuthority.Measured, outcome);
        if (decision.Recommendation is not null)
        {
            ExecutionTelemetry.RecordControl(ExecutionExplainEvidenceAuthority.Recommended, outcome);
        }

        ExecutionTelemetry.CompleteActivity(activity, outcome);
    }

    /// <summary>Records applied Control authority separately from unapplied retained recommendations.</summary>
    /// <param name="result">Existing safe-point Control actuation result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static void RecordControlActuation(ControlActuationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RecordControlActuation(
            disposition: result.Disposition,
            hasActuation: result.Actuation is not null,
            hasPendingRecommendation: result.State.PendingRecommendation is not null);
    }

    /// <summary>Records applied operator Control authority separately from unapplied retained limit updates.</summary>
    /// <param name="result">Existing operator limit-update actuation result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static void RecordControlActuation(ControlLimitUpdateActuationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RecordControlActuation(
            disposition: result.Disposition,
            hasActuation: result.Actuation is not null,
            hasPendingRecommendation: result.State.PendingLimitUpdate is not null);
    }

    static void RecordControlActuation(
        ControlActuationDisposition disposition,
        bool hasActuation,
        bool hasPendingRecommendation)
    {
        var outcome = disposition switch
        {
            ControlActuationDisposition.Applied => ExecutionTelemetryOutcome.Succeeded,
            ControlActuationDisposition.Replayed => ExecutionTelemetryOutcome.Replayed,
            ControlActuationDisposition.Deferred => ExecutionTelemetryOutcome.Deferred,
            ControlActuationDisposition.Rejected => ExecutionTelemetryOutcome.Rejected,
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported Control actuation disposition.")
        };
        var activity = ExecutionTelemetry.StartActivity(ExecutionTelemetryActivityKind.ControlActuation);
        if (hasActuation)
        {
            ExecutionTelemetry.RecordControl(ExecutionExplainEvidenceAuthority.Applied, outcome);
        }
        else if (hasPendingRecommendation)
        {
            ExecutionTelemetry.RecordControl(ExecutionExplainEvidenceAuthority.Recommended, outcome);
        }
        ExecutionTelemetry.CompleteActivity(activity, outcome);
    }

    /// <summary>Projects Control-loop health from the latest accepted pressure classification.</summary>
    /// <param name="state">Canonical durable Control state.</param>
    /// <param name="provenance">Producer and source attribution for the projection.</param>
    /// <returns>An immutable health observation at the state's latest durable update.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    public static ExecutionHealthObservation ProjectControlHealth(
        ControlLoopState state,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(provenance);
        var health = state.LastClassification switch
        {
            null => ExecutionHealthStatus.Unknown,
            ControlPressureClassification.Healthy => ExecutionHealthStatus.Healthy,
            ControlPressureClassification.Hysteresis or ControlPressureClassification.Congested =>
                ExecutionHealthStatus.Degraded,
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state.LastClassification,
                "Unsupported Control pressure classification.")
        };
        var evidence = state.LastObservation is { Source: var source }
            && !string.Equals(source, provenance.Source.Reference, StringComparison.Ordinal)
                ? ImmutableArray.Create(provenance.Source.Reference, source)
                : ImmutableArray.Create(provenance.Source.Reference);
        return new(
            health: health,
            readiness: health == ExecutionHealthStatus.Unknown
                ? ExecutionReadinessStatus.Unknown
                : ExecutionReadinessStatus.Ready,
            observedAtUtc: state.UpdatedAtUtc,
            provenance: provenance,
            evidenceReferences: evidence);
    }

    /// <summary>Projects aggregate materialization health from routing, generation, and runtime evidence.</summary>
    /// <param name="routing">Canonical placement routing snapshot.</param>
    /// <param name="generations">Current target-owned generation snapshots.</param>
    /// <param name="observation">Supplemental provider-neutral runtime observations.</param>
    /// <param name="observedAtUtc">UTC time at which the supplied authorities were observed coherently.</param>
    /// <param name="provenance">Producer and source attribution for the projection.</param>
    /// <returns>
    /// An immutable health and readiness observation retaining attributable backend-generation and source evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC.</exception>
    public static ExecutionHealthObservation ProjectMaterializationHealth(
        MaterializationBackendRoutingSnapshot routing,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        MaterializationIndexSyncRuntimeObservation observation,
        DateTimeOffset observedAtUtc,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(provenance);
        var health = GetMaterializationHealth(generations, observation);
        var readiness = health == ExecutionHealthStatus.Unknown
            ? ExecutionReadinessStatus.Unknown
            : routing.ActiveRead is not null
                && routing.ActiveWrite is not null
                && health != ExecutionHealthStatus.Unhealthy
                    ? ExecutionReadinessStatus.Ready
                    : ExecutionReadinessStatus.NotReady;
        return new(
            health: health,
            readiness: readiness,
            observedAtUtc: observedAtUtc,
            provenance: provenance,
            evidenceReferences: MaterializationEvidence(provenance, generations, observation),
            diagnostics: observation.Failures);
    }

    /// <summary>Records bounded materialization and Control observations from existing index-sync status evidence.</summary>
    /// <param name="generations">Current target-owned generation snapshots.</param>
    /// <param name="progress">Current durable source-feed progress snapshots.</param>
    /// <param name="control">Current durable index-sync Control snapshots.</param>
    /// <param name="observation">Supplemental provider-neutral runtime observations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> is <see langword="null"/>.</exception>
    public static void RecordMaterialization(
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        ImmutableArray<MaterializationIndexSyncProgressStatus> progress,
        ImmutableArray<MaterializationIndexSyncControlSnapshot> control,
        MaterializationIndexSyncRuntimeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!ExecutionTelemetry.IsEnabled)
        {
            return;
        }

        var backlog = MaterializationIndexSyncStatusProjector.GetBacklogCount(generations, observation);

        var health = GetMaterializationHealth(generations, observation);
        var activity = ExecutionTelemetry.StartActivity(ExecutionTelemetryActivityKind.Materialization);
        ExecutionTelemetry.RecordMaterialization(
            backlogCount: backlog,
            lagMilliseconds: observation.LagMilliseconds,
            shardCount: progress.Length,
            generationCount: generations.Length,
            health: health);

        long measured = 0;
        long recommended = 0;
        long applied = 0;
        foreach (var snapshot in control)
        {
            if (snapshot.State.LastObservation is not null)
            {
                measured++;
            }

            if (snapshot.State.PendingRecommendation is not null)
            {
                recommended++;
            }

            if (snapshot.State.LastActuation is not null)
            {
                applied++;
            }
        }
        if (measured > 0)
        {
            ExecutionTelemetry.RecordControl(ExecutionExplainEvidenceAuthority.Measured, ExecutionTelemetryOutcome.Observed, measured);
        }

        if (recommended > 0)
        {
            ExecutionTelemetry.RecordControl(ExecutionExplainEvidenceAuthority.Recommended, ExecutionTelemetryOutcome.Pending, recommended);
        }

        if (applied > 0)
        {
            ExecutionTelemetry.RecordControl(ExecutionExplainEvidenceAuthority.Applied, ExecutionTelemetryOutcome.Succeeded, applied);
        }

        ExecutionTelemetry.CompleteActivity(
            activity,
            health == ExecutionHealthStatus.Unhealthy
                ? ExecutionTelemetryOutcome.Failed
                : ExecutionTelemetryOutcome.Observed);
    }

    static ExecutionTelemetryOutcome GetOutcome(ExecutionTerminalOutcomeKind outcome) => outcome switch
    {
        ExecutionTerminalOutcomeKind.None => ExecutionTelemetryOutcome.Observed,
        ExecutionTerminalOutcomeKind.Completed => ExecutionTelemetryOutcome.Succeeded,
        ExecutionTerminalOutcomeKind.Failed or ExecutionTerminalOutcomeKind.Terminated =>
            ExecutionTelemetryOutcome.Failed,
        ExecutionTerminalOutcomeKind.Cancelled => ExecutionTelemetryOutcome.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported terminal outcome.")
    };

    static ExecutionHealthStatus GetMaterializationHealth(
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        MaterializationIndexSyncRuntimeObservation observation)
    {
        if (!observation.Failures.IsEmpty
            || generations.Any(static generation =>
                MaterializationIndexSyncStatusProjector.GetGenerationHealth(generation.Snapshot)
                    == MaterializationIndexSyncGenerationHealth.Failed))
        {
            return ExecutionHealthStatus.Unhealthy;
        }
        if (generations.Any(static generation =>
                MaterializationIndexSyncStatusProjector.GetGenerationHealth(generation.Snapshot)
                    == MaterializationIndexSyncGenerationHealth.Degraded))
        {
            return ExecutionHealthStatus.Degraded;
        }

        return generations.IsDefaultOrEmpty
            ? ExecutionHealthStatus.Unknown
            : ExecutionHealthStatus.Healthy;
    }

    static ImmutableArray<string> MaterializationEvidence(
        ExecutionProvenance provenance,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        MaterializationIndexSyncRuntimeObservation observation)
    {
        var references = ImmutableArray.CreateBuilder<string>(
            generations.Length + observation.ChangeLag.Length + 1);
        references.Add(provenance.Source.Reference);
        foreach (var generation in generations)
        {
            var coordinate = generation.Generation.ToString();
            if (!references.Contains(coordinate, StringComparer.Ordinal))
            {
                references.Add(coordinate);
            }
        }
        foreach (var lag in observation.ChangeLag)
        {
            if (lag.Observation.EvidenceReference is { } evidence
                && !references.Contains(evidence, StringComparer.Ordinal))
            {
                references.Add(evidence);
            }
        }
        return references.ToImmutable();
    }

    static long SaturatingAdd(long left, long right) => long.MaxValue - left < right
        ? long.MaxValue
        : left + right;
}
