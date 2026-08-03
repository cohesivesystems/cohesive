using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Storage.Processes;

/// <summary>Explains a canonical durable Process checkpoint through the common execution artifact.</summary>
public static class ProcessDurableExecutionExplainProjector
{
    /// <summary>An active AwaitMatch registration needs one compatible input or an eligible timer.</summary>
    public const string InputRequiredDiagnosticCode = "process.wait.inputRequired";

    /// <summary>Stable evidence kind for an exact durable wait registration.</summary>
    public const string WaitRegistrationEvidenceKind = "process.wait.registration";

    /// <summary>Stable evidence kind for an interaction contract eligible to resolve an active wait.</summary>
    public const string ExpectedInputEvidenceKind = "process.wait.expectedInput";

    /// <summary>Stable evidence kind for a timer eligible to resolve an active wait.</summary>
    public const string TimerEvidenceKind = "process.wait.timer";

    /// <summary>Projects durable trace, status, lineage, and active-wait evidence without exposing Process payloads.</summary>
    /// <param name="compilation">Exact target-independent compilation of the checkpoint's Process definition.</param>
    /// <param name="checkpoint">Complete canonical durable Process checkpoint.</param>
    /// <param name="interpreter">Optional exact interpreter profile used to realize the Process.</param>
    /// <param name="runtimeExtensions">Optional typed runtime status extensions owned by other blocks.</param>
    /// <param name="additionalEvidence">Optional later-stage realization or operational evidence.</param>
    /// <param name="additionalDiagnostics">Optional later-stage structured diagnostics.</param>
    /// <returns>An execution explain artifact, or structured artifact-affinity diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilation"/> or <paramref name="checkpoint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Supplied extensions, evidence, or diagnostics are malformed.</exception>
    /// <exception cref="InvalidOperationException">Explain content cannot be materialized.</exception>
    /// <exception cref="System.Text.Json.JsonException">Explain content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static ExecutionExplainProjectionResult Project(
        ProcessCompilationResult compilation,
        ProcessDurableCheckpoint checkpoint,
        ExecutionInterpreterProfileReference? interpreter = null,
        ImmutableArray<ExecutionRuntimeStatusExtension> runtimeExtensions = default,
        ImmutableArray<ExecutionExplainEvidence> additionalEvidence = default,
        ImmutableArray<DocumentValidationDiagnostic> additionalDiagnostics = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(checkpoint);

        var status = ProcessDurableExecutionStatusProjector.Project(checkpoint, runtimeExtensions);
        var traceResults = ProcessDurableExecutionTraceProjector.Project(checkpoint);
        List<DocumentValidationDiagnostic> diagnostics = [];
        NormalizedExecutionTrace? latestTrace = null;
        foreach (var result in traceResults)
        {
            if (!result.IsSuccessful)
            {
                diagnostics.AddRange(result.Validation.Diagnostics);
                continue;
            }

            if (result.Trace?.Continuation == checkpoint.ContinuationIdentity)
                latestTrace = result.Trace;
        }

        List<ExecutionExplainEvidence> evidence = [];
        var plan = compilation.Plan;
        if (plan is not null && plan.DefinitionReference == checkpoint.Definition)
            ProjectWaits(plan, checkpoint, evidence, diagnostics);
        if (!additionalEvidence.IsDefaultOrEmpty)
            evidence.AddRange(additionalEvidence);
        if (!additionalDiagnostics.IsDefaultOrEmpty)
            diagnostics.AddRange(additionalDiagnostics);

        return ProcessExecutionExplainProjector.ProjectArtifacts(
            compilation: compilation,
            trace: latestTrace,
            runtimeStatus: status,
            interpreter: interpreter,
            additionalEvidence: [.. evidence],
            additionalDiagnostics: [.. diagnostics]);
    }

    static void ProjectWaits(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        List<ExecutionExplainEvidence> evidence,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        var continuation = checkpoint.ContinuationIdentity;
        foreach (var wait in checkpoint.Continuation.Waits.Where(static value => value.Active))
        {
            var node = plan.GetNode(wait.Node);
            var nodeIndex = plan.Definition.Nodes.IndexOf(node);
            var location = $"/definition/nodes/{nodeIndex.ToString(CultureInfo.InvariantCulture)}";
            var sourceReferences = plan.Document.Metadata.SourceMap.ResolveReferences(
                location,
                plan.Document.Metadata.Provenance.Source.Reference);
            var lineage = ImmutableArray.Create(
                $"attempt:{continuation.ProcessAttemptId.Value}",
                $"instance:{continuation.ProcessInstanceId.Value}",
                $"kind:{wait.Kind}",
                $"node:{wait.Node.Value}",
                $"occurrence:{wait.Occurrence.ToString(CultureInfo.InvariantCulture)}",
                $"token:{wait.Token.Value}");
            evidence.Add(new(
                stage: ExecutionExplainStageNames.RuntimeStatus,
                kind: WaitRegistrationEvidenceKind,
                subject: wait.RegistrationId.Value,
                authority: ExecutionExplainEvidenceAuthority.Applied,
                status: "Active",
                relatedSubjects: lineage,
                sourceReferences: sourceReferences));

            if (node is not AwaitMatchProcessNode awaitMatch)
                continue;

            var interactionClauses = 0;
            foreach (var clause in awaitMatch.Clauses)
            {
                switch (clause)
                {
                    case ProcessAwaitInteractionClause interaction:
                        interactionClauses++;
                        evidence.Add(new(
                            stage: ExecutionExplainStageNames.RuntimeStatus,
                            kind: ExpectedInputEvidenceKind,
                            subject: interaction.Id.Value,
                            authority: ExecutionExplainEvidenceAuthority.Declared,
                            status: InteractionKind(interaction.Contract),
                            relatedSubjects:
                            [
                                $"clause:{interaction.Id.Value}",
                                $"definition:{DefinitionIdentity(interaction.Contract.Definition)}",
                                $"wait:{wait.RegistrationId.Value}"
                            ],
                            sourceReferences: sourceReferences));
                        break;
                    case ProcessAwaitTimerClause timer:
                        var state = wait.Timers.Single(value => value.Clause == timer.Id);
                        evidence.Add(new(
                            stage: ExecutionExplainStageNames.RuntimeStatus,
                            kind: TimerEvidenceKind,
                            subject: timer.Id.Value,
                            authority: ExecutionExplainEvidenceAuthority.Applied,
                            status: state.DueAtUtc.ToString("O", CultureInfo.InvariantCulture),
                            relatedSubjects: [$"wait:{wait.RegistrationId.Value}"],
                            sourceReferences: sourceReferences));
                        break;
                }
            }

            if (interactionClauses == 0)
                continue;

            diagnostics.Add(new(
                Code: InputRequiredDiagnosticCode,
                Severity: DiagnosticSeverity.Warning,
                Message: "The Process is durably waiting for one compatible authored input or an eligible timer.",
                Location: location,
                Evidence: new(
                    stage: ExecutionExplainStageNames.RuntimeStatus,
                    subject: wait.RegistrationId.Value,
                    relatedLocations:
                    [
                        $"attempt:{continuation.ProcessAttemptId.Value}",
                        $"instance:{continuation.ProcessInstanceId.Value}",
                        $"node:{wait.Node.Value}",
                        $"token:{wait.Token.Value}"
                    ],
                    sourceReferences: sourceReferences,
                    resolutionOptions:
                    [
                        "Submit one compatible authored input for this exact wait registration.",
                        "Allow one declared timer clause to become eligible."
                    ],
                    expected: string.Join(
                        ",",
                        awaitMatch.Clauses.OfType<ProcessAwaitInteractionClause>()
                            .Select(static clause => DefinitionIdentity(clause.Contract.Definition))
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)),
                    observed: "No compatible input has won arbitration.")));
        }
    }

    static string InteractionKind(InteractionContractReference contract) => contract switch
    {
        DomainEventContractReference => "DomainEvent",
        RequestContractReference => "Request",
        SignalContractReference => "Signal",
        ReplyContractReference => "Reply",
        _ => throw new ArgumentOutOfRangeException(nameof(contract), contract, "Unsupported interaction contract.")
    };

    static string DefinitionIdentity(ExecutionDefinitionReference definition) =>
        $"{definition.DefinitionId.Value}@{definition.RevisionId.Value}#{definition.Fingerprint.Value}";
}
