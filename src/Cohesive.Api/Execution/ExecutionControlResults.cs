using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Api;

/// <summary>Stable non-sensitive problem codes emitted by execution API boundary adapters.</summary>
public static class ExecutionApiProblemCodes
{
    /// <summary>The trusted caller context does not satisfy the operation's authorization requirement.</summary>
    public const string Forbidden = "execution.api.forbidden";

    /// <summary>No execution resource is visible at the authorized target identity.</summary>
    public const string NotFound = "execution.api.notFound";

    /// <summary>The supplied runtime request type does not match the declared endpoint contract.</summary>
    public const string RequestTypeMismatch = "execution.api.requestTypeMismatch";
}

/// <summary>A non-sensitive API boundary failure that intentionally omits target and runtime state.</summary>
public sealed record ExecutionApiProblem
{
    /// <summary>Creates a safe boundary problem.</summary>
    /// <param name="code">Stable machine-readable problem code.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="code"/> is empty or white space.</exception>
    public ExecutionApiProblem(string code) => Code = Guard.RequireNotNullOrWhiteSpace(code);

    /// <summary>Stable machine-readable problem code.</summary>
    public string Code { get; }
}

/// <summary>Safe receipt summary for one accepted or replayed Process lifecycle command.</summary>
/// <remarks>
/// The summary deliberately excludes the retained command, Signal, reasons, authorization evidence, provenance,
/// affinity values, and realization intent.
/// </remarks>
public sealed record ExecutionControlReceiptSummary
{
    /// <summary>Creates a safe command-receipt summary.</summary>
    /// <param name="commandId">Stable command occurrence identity.</param>
    /// <param name="disposition">Original durable receipt disposition.</param>
    /// <param name="beforeRevision">Semantic control revision before the decision.</param>
    /// <param name="afterRevision">Semantic control revision after the decision.</param>
    /// <param name="recordedAtUtc">Explicit UTC receipt time.</param>
    /// <exception cref="ArgumentException">An identity, revision, or timestamp is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    public ExecutionControlReceiptSummary(
        ProcessControlCommandId commandId,
        ProcessControlReceiptDisposition disposition,
        ProcessControlRevision beforeRevision,
        ProcessControlRevision afterRevision,
        DateTimeOffset recordedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(commandId.Value))
            throw new ArgumentException("A control receipt summary requires a command identity.", nameof(commandId));
        if (!Enum.IsDefined(disposition) || disposition == ProcessControlReceiptDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "A control receipt disposition must be explicit.");
        }
        if (string.IsNullOrWhiteSpace(beforeRevision.Value) || string.IsNullOrWhiteSpace(afterRevision.Value))
            throw new ArgumentException("A control receipt summary requires both revisions.", nameof(beforeRevision));
        var beforeOrdinal = long.Parse(beforeRevision.Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var afterOrdinal = long.Parse(afterRevision.Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var advancesRevision = disposition is ProcessControlReceiptDisposition.Applied
            or ProcessControlReceiptDisposition.DeferredToSafePoint;
        if (advancesRevision
            ? beforeOrdinal == long.MaxValue || afterOrdinal != beforeOrdinal + 1
            : afterOrdinal != beforeOrdinal)
        {
            throw new ArgumentException(
                "A control receipt summary's disposition contradicts its before and after revisions.",
                nameof(afterRevision));
        }
        if (recordedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("A control receipt time must be expressed in UTC.", nameof(recordedAtUtc));

        CommandId = commandId;
        Disposition = disposition;
        BeforeRevision = beforeRevision;
        AfterRevision = afterRevision;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Stable command occurrence identity.</summary>
    public ProcessControlCommandId CommandId { get; }

    /// <summary>Original durable receipt disposition.</summary>
    public ProcessControlReceiptDisposition Disposition { get; }

    /// <summary>Semantic control revision before the original decision.</summary>
    public ProcessControlRevision BeforeRevision { get; }

    /// <summary>Semantic control revision after the original decision.</summary>
    public ProcessControlRevision AfterRevision { get; }

    /// <summary>Explicit UTC receipt time.</summary>
    public DateTimeOffset RecordedAtUtc { get; }

    internal static ExecutionControlReceiptSummary From(ProcessControlCommandReceipt receipt) =>
        new(
            receipt.Command.Context.CommandId,
            receipt.Disposition,
            receipt.BeforeRevision,
            receipt.AfterRevision,
            receipt.RecordedAtUtc);
}

/// <summary>Non-sensitive transport-neutral projection of one canonical Process-control decision.</summary>
/// <remarks>
/// Raw <see cref="ProcessControlDecision"/> and <see cref="ProcessControlState"/> values are never response
/// bodies: they retain payload-bearing commands and audit evidence. This projection preserves the exact canonical
/// disposition and safe fence/status metadata while structurally omitting sensitive evidence.
/// </remarks>
public sealed record ExecutionControlResult
{
    /// <summary>Creates a validated safe Process-control result.</summary>
    /// <param name="disposition">Exact canonical decision disposition.</param>
    /// <param name="status">Safe common execution status after the decision.</param>
    /// <param name="receipt">Safe original receipt summary for accepted or replayed commands.</param>
    /// <param name="diagnosticCodes">Stable rejection codes without messages or evidence.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="status"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A diagnostic code is empty or white space, or disposition, receipt, diagnostics, and status fence do not
    /// form a coherent safe projection.
    /// </exception>
    [JsonConstructor]
    public ExecutionControlResult(
        ProcessControlDecisionDisposition disposition,
        ExecutionStatus status,
        ExecutionControlReceiptSummary? receipt = null,
        ImmutableArray<string> diagnosticCodes = default)
    {
        if (!Enum.IsDefined(disposition) || disposition == ProcessControlDecisionDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "A Process-control result disposition must be explicit.");
        }

        if (disposition is ProcessControlDecisionDisposition.Unauthorized
            or ProcessControlDecisionDisposition.TargetMismatch)
        {
            throw new ArgumentException(
                "Authorization and target-concealment failures must use an opaque execution API problem.",
                nameof(disposition));
        }

        if (diagnosticCodes.IsDefault)
            diagnosticCodes = [];
        if (diagnosticCodes.Any(static code => string.IsNullOrWhiteSpace(code)))
            throw new ArgumentException("Diagnostic codes cannot be null, empty, or white space.", nameof(diagnosticCodes));

        status = Guard.RequireNotNull(status);
        ValidateResultShape(disposition, status, receipt, diagnosticCodes);

        Disposition = disposition;
        Status = status;
        Receipt = receipt;
        DiagnosticCodes = [.. diagnosticCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>Exact canonical Process-control decision disposition.</summary>
    public ProcessControlDecisionDisposition Disposition { get; }

    /// <summary>Safe common execution status after the decision.</summary>
    public ExecutionStatus Status { get; }

    /// <summary>Safe original receipt summary for accepted or replayed commands.</summary>
    public ExecutionControlReceiptSummary? Receipt { get; }

    /// <summary>Stable rejection codes without messages or diagnostic evidence.</summary>
    public ImmutableArray<string> DiagnosticCodes { get; }

    static void ValidateResultShape(
        ProcessControlDecisionDisposition disposition,
        ExecutionStatus status,
        ExecutionControlReceiptSummary? receipt,
        ImmutableArray<string> diagnosticCodes)
    {
        if (IsRejection(disposition))
        {
            if (diagnosticCodes.IsDefaultOrEmpty || receipt is not null)
            {
                throw new ArgumentException(
                    "A rejected Process-control result requires diagnostic codes and cannot carry a receipt.",
                    nameof(disposition));
            }

            return;
        }

        if (!diagnosticCodes.IsDefaultOrEmpty)
            throw new ArgumentException("Only rejected Process-control results carry diagnostic codes.", nameof(diagnosticCodes));

        if (disposition is ProcessControlDecisionDisposition.Inspected
            or ProcessControlDecisionDisposition.ActivationStarted
            or ProcessControlDecisionDisposition.AffinityBound)
        {
            if (receipt is not null)
                throw new ArgumentException("Observation-only Process-control results cannot carry a receipt.", nameof(receipt));
            return;
        }

        if (disposition == ProcessControlDecisionDisposition.Replayed)
        {
            if (receipt is not null && !ReceiptPrecedesOrEqualsStatus(receipt, status))
                throw new ArgumentException("A replay receipt cannot follow the current status fence.", nameof(receipt));
            return;
        }

        if (disposition == ProcessControlDecisionDisposition.SafePointReached)
        {
            if (receipt is not null
                && (receipt.Disposition != ProcessControlReceiptDisposition.DeferredToSafePoint
                    || !ReceiptStrictlyPrecedesStatus(receipt, status)))
            {
                throw new ArgumentException(
                    "A safe-point result may expose only the original deferred command receipt.",
                    nameof(receipt));
            }

            return;
        }

        var expectedReceiptDisposition = disposition switch
        {
            ProcessControlDecisionDisposition.Applied => ProcessControlReceiptDisposition.Applied,
            ProcessControlDecisionDisposition.DeferredToSafePoint => ProcessControlReceiptDisposition.DeferredToSafePoint,
            ProcessControlDecisionDisposition.AlreadySatisfied => ProcessControlReceiptDisposition.AlreadySatisfied,
            ProcessControlDecisionDisposition.AlreadyRequested => ProcessControlReceiptDisposition.AlreadyRequested,
            ProcessControlDecisionDisposition.SignalAccepted => ProcessControlReceiptDisposition.SignalAccepted,
            ProcessControlDecisionDisposition.SignalBuffered => ProcessControlReceiptDisposition.SignalBuffered,
            ProcessControlDecisionDisposition.SignalDuplicate => ProcessControlReceiptDisposition.SignalDuplicate,
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported Process-control result disposition.")
        };
        if (receipt is null
            || receipt.Disposition != expectedReceiptDisposition
            || receipt.AfterRevision != status.ControlRevision
            || receipt.RecordedAtUtc > status.UpdatedAtUtc)
        {
            throw new ArgumentException(
                "A first-time Process-control result requires its exact disposition and current status-fence receipt.",
                nameof(receipt));
        }
    }

    static bool ReceiptPrecedesOrEqualsStatus(ExecutionControlReceiptSummary receipt, ExecutionStatus status) =>
        RevisionOrdinal(receipt.AfterRevision) <= RevisionOrdinal(status.ControlRevision)
        && receipt.RecordedAtUtc <= status.UpdatedAtUtc;

    static bool ReceiptStrictlyPrecedesStatus(ExecutionControlReceiptSummary receipt, ExecutionStatus status) =>
        RevisionOrdinal(receipt.AfterRevision) < RevisionOrdinal(status.ControlRevision)
        && receipt.RecordedAtUtc <= status.UpdatedAtUtc;

    static long RevisionOrdinal(ProcessControlRevision revision) =>
        long.Parse(revision.Value, NumberStyles.None, CultureInfo.InvariantCulture);

    static bool IsRejection(ProcessControlDecisionDisposition disposition) =>
        disposition is ProcessControlDecisionDisposition.StaleAttempt
            or ProcessControlDecisionDisposition.StaleRevision
            or ProcessControlDecisionDisposition.IdentityConflict
            or ProcessControlDecisionDisposition.IdempotencyConflict
            or ProcessControlDecisionDisposition.SignalConflict
            or ProcessControlDecisionDisposition.AffinityConflict
            or ProcessControlDecisionDisposition.InvalidState
            or ProcessControlDecisionDisposition.InvalidCommand;

    /// <summary>Semantic API result category derived from the canonical disposition.</summary>
    [JsonIgnore]
    public ApiResultKind ResultKind => Disposition switch
    {
        ProcessControlDecisionDisposition.StaleAttempt or ProcessControlDecisionDisposition.StaleRevision =>
            ApiResultKind.PreconditionFailed,
        ProcessControlDecisionDisposition.IdentityConflict
            or ProcessControlDecisionDisposition.IdempotencyConflict
            or ProcessControlDecisionDisposition.SignalConflict
            or ProcessControlDecisionDisposition.AffinityConflict
            or ProcessControlDecisionDisposition.InvalidState => ApiResultKind.Conflict,
        ProcessControlDecisionDisposition.InvalidCommand => ApiResultKind.ValidationFailed,
        _ => ApiResultKind.Success
    };

    /// <summary>Projects one canonical decision without exposing retained payload-bearing state.</summary>
    /// <param name="decision">Canonical reducer decision to project.</param>
    /// <param name="runtime">
    /// Optional runtime-supplied token, wait, progress, demand, capacity, health, and extension observations.
    /// </param>
    /// <param name="terminalOutcome">Optional terminal outcome with explicit disclosure semantics.</param>
    /// <returns>A structurally safe result retaining exact disposition, receipt fence, and common status.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The decision is an authorization or target-concealment failure that must be projected as an opaque problem.
    /// </exception>
    public static ExecutionControlResult FromDecision(
        ProcessControlDecision decision,
        ExecutionRuntimeStatusDetails? runtime = null,
        ExecutionTerminalOutcome? terminalOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Disposition is ProcessControlDecisionDisposition.Unauthorized
            or ProcessControlDecisionDisposition.TargetMismatch)
        {
            throw new InvalidOperationException(
                "Authorization and target-concealment decisions cannot be projected with Process state.");
        }

        return new(
            decision.Disposition,
            ExecutionStatusProjector.Project(decision.State, runtime, terminalOutcome),
            decision.Receipt is null ? null : ExecutionControlReceiptSummary.From(decision.Receipt),
            [.. decision.Diagnostics.Select(static diagnostic => diagnostic.Code)]);
    }
}

/// <summary>Runtime result selected by an in-memory execution API adapter from one declared result variant.</summary>
public sealed record ExecutionApiDispatchResult
{
    /// <summary>Creates a validated runtime result.</summary>
    /// <param name="endpoint">Typed semantic endpoint handle.</param>
    /// <param name="result">Exact declared semantic result variant.</param>
    /// <param name="body">Body value matching <paramref name="result"/>, or null for a void result.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="endpoint"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The result is not declared by the endpoint or the body does not match its declared type.
    /// </exception>
    public ExecutionApiDispatchResult(
        ApiEndpoint endpoint,
        ApiResultDefinition result,
        object? body)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(result);
        if (!endpoint.Operation.Results.Any(candidate => ReferenceEquals(candidate, result)))
            throw new ArgumentException("The result variant is not declared by the endpoint.", nameof(result));
        if (result.BodyType == typeof(void))
        {
            if (body is not null)
                throw new ArgumentException("A void result cannot carry a response body.", nameof(body));
        }
        else if (body is null || !result.BodyType.IsInstanceOfType(body))
        {
            throw new ArgumentException(
                $"Result '{result.Id}' requires body type '{result.BodyType.FullName}'.",
                nameof(body));
        }

        Endpoint = endpoint;
        Result = result;
        Body = body;
    }

    /// <summary>Typed semantic endpoint handle.</summary>
    public ApiEndpoint Endpoint { get; }

    /// <summary>Exact declared semantic result variant.</summary>
    public ApiResultDefinition Result { get; }

    /// <summary>Validated runtime response body.</summary>
    public object? Body { get; }
}
