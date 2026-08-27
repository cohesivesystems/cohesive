using System.Text.Json.Serialization;
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
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="processInstanceId"/> is default or <paramref name="terminalOutcome"/> is nonterminal.
    /// </exception>
    [JsonConstructor]
    public ProcessExecutionValues(
        ExecutionDefinitionReference definition,
        ProcessInstanceId processInstanceId,
        PortableValue? input = null,
        ExecutionTerminalOutcome? terminalOutcome = null)
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

        ProcessInstanceId = processInstanceId;
        Input = input;
        TerminalOutcome = terminalOutcome;
    }

    /// <summary>Exact pinned Process definition.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Canonical logical Process identity.</summary>
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Optional canonical start input, retaining its exact portable contract and state.</summary>
    public PortableValue? Input { get; }

    /// <summary>Canonical terminal outcome when a terminal result artifact is available.</summary>
    public ExecutionTerminalOutcome? TerminalOutcome { get; }
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
