using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Execution;

/// <summary>A failed host-operation occurrence paired with its canonical token diagnostic.</summary>
/// <remarks>Derived protected evidence, not a new failure authority. Unlike an aggregate child failure,
/// this identifies the exact attempt, activation, token, node and occurrence that failed.</remarks>
public sealed record ProcessOperationFailure
{
    /// <summary>Creates an attributed failure from retained operation and token evidence.</summary>
    /// <param name="operation">The exact failed OperationCompleted trace event.</param>
    /// <param name="diagnostic">The canonical failed token's error diagnostic.</param>
    /// <exception cref="ArgumentException">The event or diagnostic does not describe an operation failure.</exception>
    [JsonConstructor]
    public ProcessOperationFailure(ProcessTraceEvent operation, DocumentValidationDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (operation.Kind != ProcessTraceEventKind.OperationCompleted || operation.Detail != "failed"
            || operation.OperationOccurrence is null or < 0 || operation.ReceiptReference is not null
            || diagnostic.Severity != DiagnosticSeverity.Error)
            throw new ArgumentException("An operation failure requires an exact failed occurrence and error diagnostic.");
        Operation = operation;
        Diagnostic = diagnostic;
    }

    /// <summary>Existing canonical trace identifying the failed occurrence.</summary>
    public ProcessTraceEvent Operation { get; }
    /// <summary>Existing canonical failure diagnostic, preserving code, message and location.</summary>
    public DocumentValidationDiagnostic Diagnostic { get; }

    /// <summary>Projects failed terminal tokens onto their unique retained host-operation occurrences.</summary>
    /// <param name="state">Validated terminal continuation state.</param>
    /// <param name="evidence">Validated retained activation evidence, possibly spanning attempts.</param>
    /// <returns>Exact attributable operation failures. Non-operation failures are not fabricated.</returns>
    /// <exception cref="ArgumentException">A failed occurrence is ambiguous.</exception>
    public static ImmutableArray<ProcessOperationFailure> Project(ProcessContinuationState state,
        ImmutableArray<ProcessExecutionEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Terminal.Kind != ExecutionTerminalOutcomeKind.Failed || evidence.IsDefaultOrEmpty)
            return [];
        var result = ImmutableArray.CreateBuilder<ProcessOperationFailure>();
        foreach (var token in state.Tokens.Where(token => token.Disposition == ExecutionTokenDisposition.Failed && token.Failure is not null))
        {
            var matches = evidence.SelectMany(item => item.Trace).Where(trace =>
                trace.Continuation == state.Continuation && trace.Token == token.Id && trace.Node == token.Node
                && trace.Kind == ProcessTraceEventKind.OperationCompleted && trace.Detail == "failed"
                && trace.OperationOccurrence == token.Step - 1).Take(2).ToArray();
            if (matches.Length > 1)
                throw new ArgumentException("A failed token has ambiguous retained operation occurrences.", nameof(evidence));
            if (matches.Length == 1)
                result.Add(new(matches[0], token.Failure!));
        }
        return result.ToImmutable();
    }
}
