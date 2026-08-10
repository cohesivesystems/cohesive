using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Execution;

/// <summary>Projects retained Process compilation, effect, trace, and status evidence into shared explain form.</summary>
public static class ProcessExecutionExplainProjector
{
    const string CompilationEvidenceKind = "process.compilation";
    const string AtomicScopeDemandKind = "process.guaranteeDemand.atomicScope";
    const string EffectKind = "process.effect";
    const string ResourceRequirementKind = "process.resourceRequirement";

    /// <summary>Reference-interpreter profile used by convention when no explicit profile is supplied.</summary>
    public static ExecutionInterpreterProfileReference ReferenceInterpreterProfile { get; } = new(
        id: "cohesive.processes.reference",
        version: "v1",
        schemaCompatibility: new([ExecutionDefinitionDocument.CurrentSchemaVersion]),
        definitionKinds: [ProcessDefinitionDocuments.Kind],
        provenance: new(
            new("Cohesive.Processes", "v1"),
            new("Cohesive.Processes/ProcessReferenceInterpreter"),
            DocumentOrigin.System));

    /// <summary>Projects exactly the Process lifecycle artifacts already supplied by the caller.</summary>
    /// <param name="compilation">Target-independent Process compilation result.</param>
    /// <param name="decision">Optional finite Process activation decision to normalize without re-execution.</param>
    /// <param name="runtimeStatus">Optional safe Process runtime-status observation.</param>
    /// <param name="interpreter">
    /// Explicit interpreter profile, or null to use <see cref="ReferenceInterpreterProfile"/> by convention.
    /// </param>
    /// <param name="additionalEvidence">
    /// Optional effect realization, Control, materialization, or adapter evidence from later lifecycle stages.
    /// </param>
    /// <param name="additionalDiagnostics">Optional diagnostics from later lifecycle stages.</param>
    /// <returns>An execution explain artifact, or structured projection-affinity diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Supplied evidence or diagnostics are malformed.</exception>
    /// <exception cref="InvalidOperationException">Explain or trace content cannot be materialized.</exception>
    /// <exception cref="System.Text.Json.JsonException">Explain or trace content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Explain or trace content contains an unsupported serialization type.</exception>
    public static ExecutionExplainProjectionResult Project(
        ProcessCompilationResult compilation,
        ProcessActivationDecision? decision = null,
        ExecutionStatus? runtimeStatus = null,
        ExecutionInterpreterProfileReference? interpreter = null,
        ImmutableArray<ExecutionExplainEvidence> additionalEvidence = default,
        ImmutableArray<DocumentValidationDiagnostic> additionalDiagnostics = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        NormalizedExecutionTrace? trace = null;
        if (decision is not null)
        {
            var traceProjection = ProcessExecutionTraceProjector.Project(decision);
            if (!traceProjection.IsSuccessful)
                return ExecutionExplainProjectionResult.Failure(traceProjection.Validation.Diagnostics);
            trace = traceProjection.Trace;
        }

        return ProjectArtifacts(
            compilation,
            trace,
            runtimeStatus,
            interpreter,
            additionalEvidence,
            additionalDiagnostics);
    }

    /// <summary>Projects retained Process artifacts when a normalized trace already exists.</summary>
    /// <param name="compilation">Target-independent Process compilation result.</param>
    /// <param name="trace">Optional normalized trace projected from the authoritative runtime or durable store.</param>
    /// <param name="runtimeStatus">Optional safe Process runtime-status observation.</param>
    /// <param name="interpreter">
    /// Explicit interpreter profile, or null to use <see cref="ReferenceInterpreterProfile"/> by convention.
    /// </param>
    /// <param name="additionalEvidence">
    /// Optional effect realization, Control, materialization, or adapter evidence from later lifecycle stages.
    /// </param>
    /// <param name="additionalDiagnostics">Optional diagnostics from later lifecycle stages.</param>
    /// <returns>An execution explain artifact, or structured artifact-affinity diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Supplied evidence or diagnostics are malformed.</exception>
    /// <exception cref="InvalidOperationException">Explain content cannot be materialized.</exception>
    /// <exception cref="System.Text.Json.JsonException">Explain content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static ExecutionExplainProjectionResult ProjectArtifacts(
        ProcessCompilationResult compilation,
        NormalizedExecutionTrace? trace = null,
        ExecutionStatus? runtimeStatus = null,
        ExecutionInterpreterProfileReference? interpreter = null,
        ImmutableArray<ExecutionExplainEvidence> additionalEvidence = default,
        ImmutableArray<DocumentValidationDiagnostic> additionalDiagnostics = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return ProjectArtifactsCore(
            compilation.Document,
            compilation.Plan,
            compilation.IsSuccessful,
            compilation.Validation.Diagnostics,
            trace,
            runtimeStatus,
            interpreter,
            additionalEvidence,
            additionalDiagnostics);
    }

    /// <summary>Projects an already compiled exact Process plan and later lifecycle artifacts.</summary>
    /// <param name="plan">Successfully compiled canonical Process plan retained by the execution target.</param>
    /// <param name="trace">Optional normalized trace projected from the authoritative runtime or durable store.</param>
    /// <param name="runtimeStatus">Optional safe Process runtime-status observation.</param>
    /// <param name="interpreter">
    /// Explicit interpreter profile, or null to use <see cref="ReferenceInterpreterProfile"/> by convention.
    /// </param>
    /// <param name="additionalEvidence">
    /// Optional effect realization, Control, materialization, or adapter evidence from later lifecycle stages.
    /// </param>
    /// <param name="additionalDiagnostics">
    /// Optional retained compilation diagnostics and diagnostics from later lifecycle stages. A compiled plan proves
    /// successful static compilation but does not itself retain non-fatal diagnostics from its original result.
    /// </param>
    /// <returns>An execution explain artifact, or structured artifact-affinity diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Supplied evidence or diagnostics are malformed.</exception>
    /// <exception cref="InvalidOperationException">Explain content cannot be materialized.</exception>
    /// <exception cref="System.Text.Json.JsonException">Explain content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static ExecutionExplainProjectionResult ProjectArtifacts(
        CompiledProcessPlan plan,
        NormalizedExecutionTrace? trace = null,
        ExecutionStatus? runtimeStatus = null,
        ExecutionInterpreterProfileReference? interpreter = null,
        ImmutableArray<ExecutionExplainEvidence> additionalEvidence = default,
        ImmutableArray<DocumentValidationDiagnostic> additionalDiagnostics = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ProjectArtifactsCore(
            plan.Document,
            plan,
            compilationSuccessful: true,
            compilationDiagnostics: [],
            trace,
            runtimeStatus,
            interpreter,
            additionalEvidence,
            additionalDiagnostics);
    }

    static ExecutionExplainProjectionResult ProjectArtifactsCore(
        ExecutionDefinitionDocument document,
        CompiledProcessPlan? plan,
        bool compilationSuccessful,
        ImmutableArray<DocumentValidationDiagnostic> compilationDiagnostics,
        NormalizedExecutionTrace? trace,
        ExecutionStatus? runtimeStatus,
        ExecutionInterpreterProfileReference? interpreter,
        ImmutableArray<ExecutionExplainEvidence> additionalEvidence,
        ImmutableArray<DocumentValidationDiagnostic> additionalDiagnostics)
    {
        List<ExecutionExplainEvidence> evidence =
        [
            new(
                ExecutionExplainStageNames.StaticCompilation,
                CompilationEvidenceKind,
                document.Metadata.DefinitionId.Value,
                ExecutionExplainEvidenceAuthority.Derived,
                compilationSuccessful ? "Complete" : "Invalid",
                sourceReferences: [document.Metadata.Provenance.Source.Reference])
        ];
        if (plan is not null)
        {
            evidence.Add(new(
                ExecutionExplainStageNames.StaticCompilation,
                AtomicScopeDemandKind,
                document.Metadata.DefinitionId.Value,
                ExecutionExplainEvidenceAuthority.Declared,
                plan.Options.AtomicScope.ToString(),
                sourceReferences: [document.Metadata.Provenance.Source.Reference]));
            foreach (var effect in plan.EffectSummary.Effects)
            {
                evidence.Add(new(
                    ExecutionExplainStageNames.StaticCompilation,
                    EffectKind,
                    effect.Node.Value,
                    ExecutionExplainEvidenceAuthority.Derived,
                    effect.Kind.ToString(),
                    sourceReferences: [document.Metadata.Provenance.Source.Reference]));
            }
            HashSet<(ExecutionNodeId Node, string Definition, ProcessResourceAccessKind Access)> resourceClaims = [];
            foreach (var resource in plan.EffectSummary.Resources)
            {
                var definition = DefinitionIdentity(resource.Resource);
                if (!resourceClaims.Add((resource.Node, definition, resource.Access)))
                    continue;
                evidence.Add(new(
                    ExecutionExplainStageNames.StaticCompilation,
                    ResourceRequirementKind,
                    $"{resource.Node.Value}:{definition}",
                    ExecutionExplainEvidenceAuthority.Derived,
                    resource.Access.ToString(),
                    relatedSubjects: [resource.Node.Value, definition],
                    sourceReferences: [document.Metadata.Provenance.Source.Reference]));
            }
        }
        if (!additionalEvidence.IsDefaultOrEmpty)
            evidence.AddRange(additionalEvidence);

        List<DocumentValidationDiagnostic> diagnostics = [.. compilationDiagnostics];
        if (!additionalDiagnostics.IsDefaultOrEmpty)
            diagnostics.AddRange(additionalDiagnostics);

        return ExecutionExplainArtifactProjector.Project(
            document,
            interpreter ?? ReferenceInterpreterProfile,
            [.. evidence],
            trace,
            runtimeStatus,
            [.. diagnostics]);
    }

    static string DefinitionIdentity(ExecutionDefinitionReference definition) =>
        $"{definition.DefinitionId.Value}@{definition.RevisionId.Value}#{definition.Fingerprint.Value}";
}
