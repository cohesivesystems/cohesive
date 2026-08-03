using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Execution;

/// <summary>Projects authoritative Transition execution evidence into the shared normalized trace contract.</summary>
public static class TransitionExecutionTraceProjector
{
    const string Stage = "transitionTraceProjection";

    /// <summary>Projects one deterministic Transition decision without copying invocation or observation values.</summary>
    /// <param name="plan">Exact compiled Transition plan that authorized the decision.</param>
    /// <param name="decision">Complete non-committing Transition decision.</param>
    /// <returns>A normalized trace, or structured diagnostics when evidence affinity is invalid.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="decision"/> is <see langword="null"/>.
    /// </exception>
    public static ExecutionTraceProjectionResult Project(
        CompiledTransitionPlan plan,
        TransitionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(decision);

        var expectedDefinition = Reference(plan.Document);
        List<DocumentValidationDiagnostic> diagnostics = [];
        if (decision.Evidence.Definition != expectedDefinition)
        {
            diagnostics.Add(Error(
                ExecutionTraceDiagnosticCodes.DefinitionMismatch,
                "Transition trace evidence does not name the exact compiled definition.",
                "/definition",
                expectedDefinition.DefinitionId.Value,
                plan.Document.Metadata.Provenance.Source.Reference));
        }

        ValidateTrace(decision.Evidence, plan.Document.Metadata.Provenance.Source.Reference, diagnostics);
        if (diagnostics.Count != 0)
            return ExecutionTraceProjectionResult.Failure(diagnostics);

        var trace = decision.Evidence.Trace;
        var events = ImmutableArray.CreateBuilder<NormalizedExecutionTraceEvent>(trace.Length);
        var sourceReferences = ImmutableArray.Create(plan.Document.Metadata.Provenance.Source.Reference);
        foreach (var item in trace)
        {
            events.Add(new(
                sequence: item.Sequence,
                kind: ConventionName(item.Kind),
                node: item.Node,
                branchOrClause: item.SelectedCase,
                relatedDefinition: item.Contract,
                relatedNode: item.Edge,
                semanticPath: item.Path ?? item.Access?.Path,
                changed: item.Changed,
                detail: item.Detail,
                sourceReferences: sourceReferences));
        }

        return ExecutionTraceProjectionResult.Success(new(
            schemaVersion: NormalizedExecutionTrace.CurrentSchemaVersion,
            kind: TransitionDefinitionDocuments.Kind,
            definition: expectedDefinition,
            continuation: null,
            activation: decision.Evidence.Activation,
            disposition: ConventionName(decision.Kind),
            safePointNode: null,
            durableCommitSequence: null,
            events: events.MoveToImmutable()));
    }

    static void ValidateTrace(
        TransitionExecutionEvidence evidence,
        string fallbackSource,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        for (var index = 0; index < evidence.Trace.Length; index++)
        {
            var item = evidence.Trace[index];
            if (item is not null
                && item.Sequence == index
                && !string.IsNullOrWhiteSpace(item.Node.Value)
                && Enum.IsDefined(item.Kind))
            {
                continue;
            }

            diagnostics.Add(Error(
                ExecutionTraceDiagnosticCodes.EventInvalid,
                $"Transition trace event {index} has invalid sequence, kind, or node evidence.",
                $"/trace/{index}",
                evidence.Definition.DefinitionId.Value,
                fallbackSource));
        }
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static string ConventionName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    static DocumentValidationDiagnostic Error(
        string code,
        string message,
        string location,
        string subject,
        string sourceReference) => new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(
                stage: Stage,
                subject: subject,
                sourceReferences: [sourceReference]));
}
