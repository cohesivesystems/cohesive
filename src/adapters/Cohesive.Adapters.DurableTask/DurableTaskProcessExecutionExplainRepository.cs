using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Composes current Durable Task Process observations into canonical execution explanations.</summary>
/// <remarks>
/// Scheduler metadata remains physical acquisition evidence. The exact deployed canonical plan, realization ledger,
/// normalized trace, and protocol-neutral status remain the authorities projected into the shared artifact.
/// </remarks>
public sealed class DurableTaskProcessExecutionExplainRepository : IProcessExecutionExplainRepository
{
    /// <summary>Diagnostic emitted when retained normalized traces start after the activation-evidence inventory.</summary>
    public const string TraceCoverageIncompleteDiagnosticCode = "process.explain.traceCoverageIncomplete";

    /// <summary>Diagnostic emitted when a terminal execution has no canonical result artifact to supply traces.</summary>
    public const string TraceArtifactUnavailableDiagnosticCode = "process.explain.traceArtifactUnavailable";

    /// <summary>Stable explain-evidence kind for one exact Process realization-ledger disposition.</summary>
    public const string RealizationEvidenceKind = "process.interpreter.realization";

    static readonly ExecutionInterpreterProfileReference InterpreterProfile = new(
        DurableTaskProcessTargetProfile.Target.Value,
        DurableTaskProcessTargetProfile.PlanningProfileId.Value,
        new([ExecutionDefinitionDocument.CurrentSchemaVersion]),
        [ProcessDefinitionDocuments.Kind],
        new(
            new("Cohesive.Adapters.DurableTask", "v1"),
            new("Cohesive.Adapters.DurableTask/DurableTaskProcessTargetProfile"),
            DocumentOrigin.System));

    readonly DurableTaskProcessExecutionRepository executions;
    readonly DurableTaskSequentialProcessPlanCatalog plans;

    /// <summary>Creates an explain repository over one current task-hub repository and exact deployed plan catalog.</summary>
    /// <param name="executions">Current standalone-client execution and trace repository.</param>
    /// <param name="plans">Exact immutable realization plans deployed to the interpreting worker.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public DurableTaskProcessExecutionExplainRepository(
        DurableTaskProcessExecutionRepository executions,
        DurableTaskSequentialProcessPlanCatalog plans)
    {
        this.executions = executions ?? throw new ArgumentNullException(nameof(executions));
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
    }

    /// <inheritdoc />
    public async ValueTask<ExecutionExplainArtifact?> GetExplainAsync(
        OperationContext context,
        string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.ThrowIfCancellationRequested();

        var traceRead = await executions.GetTracesAsync(context, processId).ConfigureAwait(false);
        var execution = await executions.GetAsync(context, processId).ConfigureAwait(false);
        if (execution is null)
        {
            return null;
        }

        if (execution.IsTerminal
            && traceRead.State is ProcessExecutionTraceReadState.NotFound
                or ProcessExecutionTraceReadState.InProgress)
        {
            traceRead = await executions.GetTracesAsync(context, processId).ConfigureAwait(false);
        }

        ValidateObservationAffinity(processId, execution, traceRead);
        var definition = execution.Definition
            ?? throw InvalidEvidence(processId, "does not retain its exact canonical definition reference");
        var plan = plans.GetExact(definition);
        var trace = SelectCurrentAttemptTrace(processId, execution.RuntimeStatus, traceRead.Artifact);
        var diagnostics = ProjectTraceDiagnostics(execution, traceRead);
        var projection = ProcessExecutionExplainProjector.ProjectArtifacts(
            plan.CanonicalPlan,
            trace,
            execution.RuntimeStatus,
            InterpreterProfile,
            ProjectRealizationEvidence(plan),
            diagnostics);
        if (!projection.IsSuccessful)
        {
            throw InvalidEvidence(
                processId,
                "cannot be projected into a canonical explanation: "
                + string.Join(
                    "; ",
                    projection.Validation.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.Code}: {diagnostic.Message}")));
        }

        return projection.Artifact;
    }

    /// <summary>Returns a canonical explanation by trusted authority scope and logical Process identity.</summary>
    /// <param name="context">Operation context that supplies cancellation for the read.</param>
    /// <param name="authorityScope">Exact trusted authority and optional tenant isolating the physical execution.</param>
    /// <param name="processInstanceId">Canonical logical Process instance identity.</param>
    /// <returns>The canonical explanation artifact, or <see langword="null"/> when no execution is retained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="authorityScope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="processInstanceId"/> is the default identity.</exception>
    /// <exception cref="KeyNotFoundException">No exact deployed definition plan can explain the execution.</exception>
    /// <exception cref="InvalidOperationException">Retained evidence is malformed or contradictory.</exception>
    /// <exception cref="NotSupportedException">The repository is a migration-only historical Core reader.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Canonical explanation content cannot be serialized for deterministic identity.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    public ValueTask<ExecutionExplainArtifact?> GetExplainAsync(
        OperationContext context,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorityScope);
        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
        {
            throw new ArgumentException("A logical Process explain read requires an initialized instance identity.", nameof(processInstanceId));
        }

        return GetExplainAsync(
            context,
            DurableTaskProcessExecutionIdentity.GetPhysicalInstanceId(authorityScope, processInstanceId));
    }

    static void ValidateObservationAffinity(
        string processId,
        ProcessExecutionRecord execution,
        ProcessExecutionTraceReadResult traceRead)
    {
        if (!string.Equals(execution.ProcessId, processId, StringComparison.Ordinal))
        {
            throw InvalidEvidence(processId, $"returned execution key '{execution.ProcessId}'");
        }

        if (execution.RuntimeStatus is { } status
            && execution.Definition is { } definition
            && status.Definition != definition)
        {
            throw InvalidEvidence(processId, "has conflicting execution-record and runtime-status definitions");
        }

        if (traceRead.Artifact is not { } traceArtifact)
        {
            if (execution.IsTerminal
                && traceRead.State is ProcessExecutionTraceReadState.NotFound
                    or ProcessExecutionTraceReadState.InProgress)
            {
                throw InvalidEvidence(processId, $"is terminal but its trace read remains '{traceRead.State}'");
            }
            if (!execution.IsTerminal
                && traceRead.State == ProcessExecutionTraceReadState.TerminalArtifactUnavailable)
            {
                throw InvalidEvidence(processId, "is nonterminal but its trace read reports a terminal artifact gap");
            }
            return;
        }

        if (!execution.IsTerminal)
        {
            throw InvalidEvidence(processId, "has a terminal trace result while its execution record is nonterminal");
        }

        if (execution.Definition != traceArtifact.Definition)
        {
            throw InvalidEvidence(processId, "has conflicting execution-record and trace definitions");
        }

        if (execution.RuntimeStatus is not { } runtimeStatus)
        {
            throw InvalidEvidence(processId, "has terminal traces without canonical runtime status");
        }

        if (runtimeStatus.ProcessInstanceId != traceArtifact.ProcessInstanceId)
        {
            throw InvalidEvidence(processId, "has conflicting runtime-status and trace logical instances");
        }
    }

    static NormalizedExecutionTrace? SelectCurrentAttemptTrace(
        string processId,
        ExecutionStatus? runtimeStatus,
        ProcessExecutionTraceArtifact? traceArtifact)
    {
        if (traceArtifact is null || traceArtifact.Traces.IsDefaultOrEmpty)
        {
            return null;
        }

        if (runtimeStatus is null)
        {
            throw InvalidEvidence(processId, "has retained traces without canonical runtime status");
        }

        NormalizedExecutionTrace? selected = null;
        foreach (var trace in traceArtifact.Traces)
        {
            if (trace.Continuation?.ProcessAttemptId == runtimeStatus.CurrentAttemptId)
            {
                selected = trace;
            }
        }
        return selected ?? throw InvalidEvidence(
            processId,
            $"retains traces but none for current attempt '{runtimeStatus.CurrentAttemptId.Value}'");
    }

    static ImmutableArray<ExecutionExplainEvidence> ProjectRealizationEvidence(
        DurableTaskProcessRealizationPlan plan)
    {
        var target = plan.Realization.TargetProfile;
        return
        [
            .. plan.Requirements.Select(pair => new ExecutionExplainEvidence(
                ExecutionExplainStageNames.Realization,
                RealizationEvidenceKind,
                pair.Requirement.Key.ToString(),
                ExecutionExplainEvidenceAuthority.AdapterSupplied,
                target.Target.Value,
                pair.Decision.Realization,
                relatedSubjects:
                [
                    .. pair.Requirement.Nodes.Select(static node => $"node:{node.Value}"),
                    .. pair.Requirement.LinkedDefinitions.Select(static definition =>
                        $"definition:{DefinitionIdentity(definition)}"),
                    .. pair.Decision.OperatingBoundaries.Select(static boundary => $"boundary:{boundary.Value}")
                ],
                sourceReferences: RealizationSourceReferences(target, pair.Decision)))
        ];
    }

    static ImmutableArray<string> RealizationSourceReferences(
        ProcessInterpreterCapabilityProfile target,
        ProcessInterpreterRealizationDecision decision)
    {
        var references = ImmutableArray.CreateBuilder<string>(
            1 + (decision.Evidence is null ? 0 : 1) + decision.AuxiliaryEvidence.Length);
        references.Add(target.Id.Value);
        if (decision.Evidence is { } evidence)
        {
            references.Add(evidence.Value);
        }

        references.AddRange(decision.AuxiliaryEvidence.Select(static item => item.Value));
        return references.MoveToImmutable();
    }

    static ImmutableArray<DocumentValidationDiagnostic> ProjectTraceDiagnostics(
        ProcessExecutionRecord execution,
        ProcessExecutionTraceReadResult traceRead)
    {
        if (traceRead.Artifact is { MissingTracePrefixCount: > 0 } artifact)
        {
            return
            [
                new(
                    TraceCoverageIncompleteDiagnosticCode,
                    DiagnosticSeverity.Warning,
                    "The retained normalized trace suffix does not cover the complete activation-evidence inventory.",
                    Evidence: new(
                        stage: ExecutionExplainStageNames.ExecutionTrace,
                        subject: artifact.ProcessInstanceId.Value,
                        relatedLocations:
                        [
                            $"definition:{DefinitionIdentity(artifact.Definition)}",
                            $"instance:{artifact.ProcessInstanceId.Value}"
                        ],
                        resolutionOptions:
                        [
                            "Treat the artifact as partial and inspect only retained activation traces.",
                            "Run a new execution after normalized trace retention was enabled."
                        ],
                        expected: "missingTracePrefixCount=0",
                        observed: $"missingTracePrefixCount={artifact.MissingTracePrefixCount}"))
            ];
        }
        if (traceRead.State == ProcessExecutionTraceReadState.TerminalArtifactUnavailable)
        {
            return
            [
                new(
                    TraceArtifactUnavailableDiagnosticCode,
                    DiagnosticSeverity.Warning,
                    "The terminal execution has no canonical result artifact from which normalized traces can be read.",
                    Evidence: new(
                        stage: ExecutionExplainStageNames.ExecutionTrace,
                        subject: execution.RuntimeStatus?.ProcessInstanceId.Value
                            ?? execution.Definition?.DefinitionId.Value
                            ?? "retained-process-execution",
                        relatedLocations: execution.Definition is { } definition
                            ? [$"definition:{DefinitionIdentity(definition)}"]
                            : [],
                        resolutionOptions:
                        [
                            "Use the available definition, realization, and runtime-status explanation as a partial artifact.",
                            "Run a new execution with canonical terminal-result retention enabled."
                        ],
                        expected: "canonical terminal result",
                        observed: "terminal artifact unavailable"))
            ];
        }
        return [];
    }

    static InvalidOperationException InvalidEvidence(string processId, string reason) => new(
        $"Durable Task Process execution '{processId}' {reason}.");

    static string DefinitionIdentity(ExecutionDefinitionReference definition) =>
        $"{definition.DefinitionId.Value}@{definition.RevisionId.Value}#{definition.Fingerprint.Value}";
}
