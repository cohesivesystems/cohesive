using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Performs an explicit trusted read of retained canonical Process input and terminal values.
/// </summary>
/// <remarks>
/// Values may contain sensitive application payloads and are deliberately excluded from
/// <see cref="IProcessExecutionRepository"/> monitoring records. Application-facing callers must establish the
/// caller's authority before supplying <see cref="InteractionAuthorityScope"/>. Implementations must preserve the
/// canonical <see cref="PortableValue"/> contracts and availability states rather than projecting provider history.
/// </remarks>
public interface IProcessExecutionValueRepository
{
    /// <summary>Reads retained canonical values by trusted authority scope and logical Process identity.</summary>
    /// <param name="context">Operation context that supplies cancellation for the read.</param>
    /// <param name="authorityScope">Exact trusted authority and optional tenant isolating the execution.</param>
    /// <param name="processInstanceId">Canonical logical Process instance identity.</param>
    /// <returns>An explicit availability result and canonical values when the execution is retained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="authorityScope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="processInstanceId"/> is the default identity.</exception>
    /// <exception cref="InvalidOperationException">Retained canonical evidence is malformed or contradictory.</exception>
    /// <exception cref="NotSupportedException">The selected repository cannot retrieve canonical values.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    ValueTask<ProcessExecutionValueReadResult> GetValuesAsync(
        OperationContext context,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId);
}

/// <summary>Disposition of one explicit canonical Process-value read.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ProcessExecutionValueReadState
{
    /// <summary>No read disposition was supplied.</summary>
    Unspecified = 0,

    /// <summary>No matching canonical execution is retained by the repository.</summary>
    NotFound = 1,

    /// <summary>The retained execution has not produced its terminal canonical result artifact.</summary>
    InProgress = 2,

    /// <summary>The retained execution has canonical start and terminal values available.</summary>
    Available = 3,

    /// <summary>The execution is terminal but its canonical terminal result artifact is unavailable.</summary>
    TerminalArtifactUnavailable = 4
}

/// <summary>Canonical values retained for one logical Process execution.</summary>
public sealed record ProcessExecutionValues
{
    /// <summary>Creates one exact retained-value artifact.</summary>
    /// <param name="definition">Exact pinned Process definition.</param>
    /// <param name="processInstanceId">Canonical logical Process identity.</param>
    /// <param name="input">Optional canonical start input.</param>
    /// <param name="terminalOutcome">Canonical terminal outcome when its result artifact is available.</param>
    /// <param name="terminalContinuation">Exact terminal attempt when retained; null denotes unavailable attempt evidence.</param>
    /// <param name="evidence">Protected canonical activation evidence. Default denotes unavailable evidence;
    /// a materialized empty array denotes no retained activations. This is not normalized telemetry.</param>
    /// <param name="operationFailures">Optional exact failed-operation evidence from terminal state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="processInstanceId"/> is default or <paramref name="terminalOutcome"/> is nonterminal.
    /// </exception>
    [JsonConstructor]
    public ProcessExecutionValues(
        ExecutionDefinitionReference definition,
        ProcessInstanceId processInstanceId,
        PortableValue? input = null,
        ExecutionTerminalOutcome? terminalOutcome = null,
        ProcessContinuationIdentity? terminalContinuation = null,
        ImmutableArray<ProcessExecutionEvidence> evidence = default,
        ImmutableArray<ProcessOperationFailure> operationFailures = default)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
        {
            throw new ArgumentException(
                "Retained Process values require an initialized logical instance identity.",
                nameof(processInstanceId));
        }
        if (terminalOutcome is { Kind: ExecutionTerminalOutcomeKind.None })
        {
            throw new ArgumentException(
                "A retained terminal outcome must have a terminal kind.",
                nameof(terminalOutcome));
        }

        if (terminalContinuation is not null && (terminalOutcome is null
            || terminalContinuation.ProcessInstanceId != processInstanceId))
            throw new ArgumentException("Terminal continuation must identify this instance and a retained terminal outcome.", nameof(terminalContinuation));
        if (!evidence.IsDefaultOrEmpty)
        {
            if (terminalContinuation is null)
                throw new ArgumentException("Retained activation evidence requires exact terminal continuation evidence.", nameof(evidence));
            foreach (var activation in evidence)
                if (activation is null || activation.Definition != definition || activation.Trace.IsDefault
                    || activation.Trace.Any(item => item is null || item.Definition != definition
                        || item.Activation != activation.Activation || item.Continuation is null
                        || item.Continuation.ProcessInstanceId != processInstanceId))
                    throw new ArgumentException("Retained activation evidence contradicts the exact Process definition or instance.", nameof(evidence));
        }
        if (!operationFailures.IsDefaultOrEmpty)
        {
            if (terminalOutcome?.Kind != ExecutionTerminalOutcomeKind.Failed || evidence.IsDefaultOrEmpty
                || operationFailures.Any(failure => failure is null
                    || failure.Operation.Continuation != terminalContinuation
                    || evidence.SelectMany(item => item.Trace).Count(trace =>
                        trace.Definition == failure.Operation.Definition && trace.Continuation == failure.Operation.Continuation
                        && trace.Activation == failure.Operation.Activation && trace.Token == failure.Operation.Token
                        && trace.Node == failure.Operation.Node && trace.OperationOccurrence == failure.Operation.OperationOccurrence
                        && trace.Sequence == failure.Operation.Sequence && trace.Kind == failure.Operation.Kind
                        && trace.Detail == failure.Operation.Detail && trace.ReceiptReference is null) != 1)
                || operationFailures.Select(failure => (failure.Operation.Continuation, failure.Operation.Activation,
                    failure.Operation.Token, failure.Operation.Node, failure.Operation.OperationOccurrence)).Distinct().Count() != operationFailures.Length)
                throw new ArgumentException("Operation failures require unique exact terminal-attempt trace evidence.", nameof(operationFailures));
        }
        OperationFailures = operationFailures;
        ProcessInstanceId = processInstanceId;
        Input = input;
        TerminalOutcome = terminalOutcome;
        TerminalContinuation = terminalContinuation;
        Evidence = evidence;
    }

    /// <summary>Protected exact-operation failures projected from terminal token state; default means unavailable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<ProcessOperationFailure> OperationFailures { get; }

    /// <summary>Exact pinned Process definition.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Canonical logical Process identity.</summary>
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Optional canonical start input, retaining its exact portable contract and state.</summary>
    public PortableValue? Input { get; }

    /// <summary>Canonical terminal outcome when a terminal result artifact is available.</summary>
    public ExecutionTerminalOutcome? TerminalOutcome { get; }

    /// <summary>Exact attempt producing the terminal result, or null when that evidence is unavailable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProcessContinuationIdentity? TerminalContinuation { get; }

    /// <summary>Protected canonical activation evidence; default means unavailable, not an empty history.</summary>
    /// <remarks>May contain receipt locators and protected identities. Do not export as normalized telemetry.
    /// Evidence can span prior attempts; terminal response selection must use TerminalContinuation.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<ProcessExecutionEvidence> Evidence { get; }
}

/// <summary>Explicit outcome of reading retained canonical Process values.</summary>
public sealed record ProcessExecutionValueReadResult
{
    /// <summary>Creates one exact value-read result.</summary>
    /// <param name="state">Read disposition.</param>
    /// <param name="values">Canonical values exactly when the execution is retained.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unspecified or unsupported.</exception>
    /// <exception cref="ArgumentException">Value or terminal-outcome presence contradicts <paramref name="state"/>.</exception>
    [JsonConstructor]
    public ProcessExecutionValueReadResult(
        ProcessExecutionValueReadState state,
        ProcessExecutionValues? values = null)
    {
        if (!Enum.IsDefined(state) || state == ProcessExecutionValueReadState.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "A Process-value read requires an explicit state.");
        }
        if ((state == ProcessExecutionValueReadState.NotFound) != (values is null))
        {
            throw new ArgumentException(
                "Canonical Process values exist exactly when the execution is retained.",
                nameof(values));
        }
        if (values is not null
            && ((state == ProcessExecutionValueReadState.Available) != (values.TerminalOutcome is not null)))
        {
            throw new ArgumentException(
                "A terminal outcome exists exactly when canonical terminal values are available.",
                nameof(values));
        }

        State = state;
        Values = values;
    }

    /// <summary>Explicit availability disposition.</summary>
    public ProcessExecutionValueReadState State { get; }

    /// <summary>Canonical retained values, or <see langword="null"/> when no execution was found.</summary>
    public ProcessExecutionValues? Values { get; }

    /// <summary>Creates a result for an execution that is not retained.</summary>
    public static ProcessExecutionValueReadResult NotFound() =>
        new(ProcessExecutionValueReadState.NotFound);

    /// <summary>Creates a result for a retained execution that is still active.</summary>
    public static ProcessExecutionValueReadResult InProgress(ProcessExecutionValues values) =>
        new(ProcessExecutionValueReadState.InProgress, WithoutTerminal(values));

    /// <summary>Creates a result containing canonical start and terminal values.</summary>
    public static ProcessExecutionValueReadResult Available(ProcessExecutionValues values) =>
        new(
            ProcessExecutionValueReadState.Available,
            values?.TerminalOutcome is null
                ? throw new ArgumentException("Available Process values require a terminal outcome.", nameof(values))
                : values);

    /// <summary>Creates a result for a terminal execution whose canonical result artifact is unavailable.</summary>
    public static ProcessExecutionValueReadResult TerminalArtifactUnavailable(ProcessExecutionValues values) =>
        new(ProcessExecutionValueReadState.TerminalArtifactUnavailable, WithoutTerminal(values));

    static ProcessExecutionValues WithoutTerminal(ProcessExecutionValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.TerminalOutcome is null
            ? values
            : throw new ArgumentException("This Process-value read state cannot carry a terminal outcome.", nameof(values));
    }
}
