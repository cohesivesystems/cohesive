using System.Collections.Immutable;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Storage.Processes;

namespace Cohesive.Storage.Materialization;

/// <summary>Explains an index-sync Process and its current Storage and Control authorities.</summary>
public static class MaterializationIndexSyncExecutionExplainProjector
{
    /// <summary>Control has measured congestion for an index-sync workload.</summary>
    public const string ThrottledDiagnosticCode = "materialization.indexSync.throttled";

    /// <summary>Projects one coherent operational explanation without copying source or business payloads.</summary>
    /// <param name="compilation">Exact target-independent compilation of the index-sync Process.</param>
    /// <param name="checkpoint">Complete canonical durable Process checkpoint.</param>
    /// <param name="routing">Current exact placement-scoped routing snapshot.</param>
    /// <param name="progress">Current bounded per-source progress snapshots.</param>
    /// <param name="generations">Current exact backend coordinates and target generation snapshots.</param>
    /// <param name="control">Current compiled Control realizations and durable states.</param>
    /// <param name="observation">Supplemental provider-neutral runtime observations.</param>
    /// <param name="provenance">Attributable runtime producer and source evidence.</param>
    /// <param name="interpreter">Optional exact Process interpreter profile.</param>
    /// <returns>An execution explain artifact, or structured artifact-affinity diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Supplied operational evidence is malformed or has conflicting affinity.</exception>
    /// <exception cref="InvalidOperationException">Status or explain content cannot be materialized.</exception>
    /// <exception cref="System.Text.Json.JsonException">Explain content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static ExecutionExplainProjectionResult Project(
        ProcessCompilationResult compilation,
        ProcessDurableCheckpoint checkpoint,
        MaterializationBackendRoutingSnapshot routing,
        ImmutableArray<MaterializationIndexSyncProgressStatus> progress,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        ImmutableArray<MaterializationIndexSyncControlSnapshot> control,
        MaterializationIndexSyncRuntimeObservation observation,
        ExecutionProvenance provenance,
        ExecutionInterpreterProfileReference? interpreter = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(provenance);

        var extension = MaterializationIndexSyncStatusProjector.CreateExtension(
            routing: routing,
            progress: progress,
            generations: generations,
            control: control,
            observation: observation,
            provenance: provenance);
        var evidence = StorageExecutionExplainEvidenceProjector.ProjectMaterializationStatus(
            routing: routing,
            progress: progress,
            generations: generations,
            control: control,
            observation: observation,
            provenance: provenance);
        List<DocumentValidationDiagnostic> diagnostics = [.. observation.Failures];
        foreach (var snapshot in control.Where(static value =>
                     value.State.LastClassification == ControlPressureClassification.Congested))
        {
            var state = snapshot.State;
            var resolutionOptions = state.PendingRecommendation is null
                ? ImmutableArray.Create(
                    "Continue at the applied operating point and re-evaluate after fresh observations.")
                : ImmutableArray.Create(
                    "Apply the retained Control recommendation at the next declared safe point.",
                    "Continue at the applied operating point and re-evaluate after fresh observations.");
            diagnostics.Add(new(
                Code: ThrottledDiagnosticCode,
                Severity: DiagnosticSeverity.Warning,
                Message: "Control measured congestion for this index-sync workload and constrained its operating point.",
                SchemaLocation: $"control/{state.LoopId.Value}",
                Evidence: new(
                    stage: ExecutionExplainStageNames.Control,
                    subject: state.LoopId.Value,
                    relatedLocations:
                    [
                        $"backend:{snapshot.Key.TargetId.Value}",
                        $"generation:{snapshot.Key.GenerationId.Value}",
                        $"workload:{snapshot.Key.Workload}"
                    ],
                    sourceReferences: StorageExecutionExplainEvidenceProjector.SourceReferences(
                        provenance,
                        state.LastObservation?.Source),
                    resolutionOptions: resolutionOptions,
                    expected: ControlPressureClassification.Healthy.ToString(),
                    observed: ControlPressureClassification.Congested.ToString())));
        }

        return ProcessDurableExecutionExplainProjector.Project(
            compilation: compilation,
            checkpoint: checkpoint,
            interpreter: interpreter,
            runtimeExtensions: [extension],
            additionalEvidence: evidence,
            additionalDiagnostics: [.. diagnostics]);
    }

}
