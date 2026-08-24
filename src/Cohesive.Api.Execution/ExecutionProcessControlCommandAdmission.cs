using Cohesive.Execution;

namespace Cohesive.Api.Execution;

/// <summary>Shared trusted-context admission for canonical Process-control commands.</summary>
/// <remarks>
/// API request contracts carry portable command evidence, but an authenticated adapter owns authority, issuance,
/// observation, and provenance. Exact replay restores the retained occurrence evidence before canonical equality
/// and idempotency evaluation; it never trusts a later caller to restate that evidence.
/// </remarks>
public static class ExecutionProcessControlCommandAdmission
{
    /// <summary>Rebinds one first-occurrence caller command to trusted current invocation evidence.</summary>
    /// <param name="command">Caller command whose authority evidence is untrusted.</param>
    /// <param name="invocation">Trusted current API invocation evidence.</param>
    /// <returns>A canonical first-occurrence command ready for reduction.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A first-time Signal does not have a trusted interaction-envelope context.
    /// </exception>
    public static ProcessControlCommand Rebind(
        ProcessControlCommand command,
        ExecutionApiInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(invocation);
        return Rebind(command, invocation, prior: null);
    }

    /// <summary>Rebinds one caller command to trusted current or retained occurrence evidence.</summary>
    /// <param name="command">Caller command whose authority evidence is untrusted.</param>
    /// <param name="invocation">Trusted current API invocation evidence.</param>
    /// <param name="state">Authoritative retained Process-control state used to find exact replay evidence.</param>
    /// <returns>A canonical command ready for <see cref="ProcessControlReferenceExecutor"/> reduction.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A first-time Signal does not have a trusted interaction-envelope context.
    /// </exception>
    public static ProcessControlCommand Rebind(
        ProcessControlCommand command,
        ExecutionApiInvocationContext invocation,
        ProcessControlState state)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(state);
        return Rebind(command, invocation, FindPriorCommand(state, command.Context));
    }

    internal static ProcessControlCommand Rebind(
        ProcessControlCommand command,
        ExecutionApiInvocationContext invocation,
        ProcessControlCommand? prior)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(invocation);
        var context = Rebind(command.Context, invocation, prior?.Context);
        return command switch
        {
            InspectProcessCommand inspect => new InspectProcessCommand(
                inspect.SchemaVersion,
                context,
                inspect.Expectation),
            SignalProcessCommand signal => new SignalProcessCommand(
                signal.SchemaVersion,
                context,
                signal.Expectation!,
                Rebind(
                    signal.Signal,
                    (prior as SignalProcessCommand)?.Signal.Context
                        ?? invocation.SignalContext
                        ?? throw new InvalidOperationException(
                            "A first-time Signal invocation requires trusted envelope context."))),
            PauseProcessCommand pause => new PauseProcessCommand(
                pause.SchemaVersion,
                context,
                pause.Expectation!),
            ContinueProcessCommand continueProcess => new ContinueProcessCommand(
                continueProcess.SchemaVersion,
                context,
                continueProcess.Expectation!),
            RestartProcessAttemptCommand restart => new RestartProcessAttemptCommand(
                restart.SchemaVersion,
                context,
                restart.Expectation!,
                restart.Plan),
            CancelProcessCommand cancel => new CancelProcessCommand(
                cancel.SchemaVersion,
                context,
                cancel.Expectation!,
                cancel.Reason),
            TerminateProcessCommand terminate => new TerminateProcessCommand(
                terminate.SchemaVersion,
                context,
                terminate.Expectation!,
                terminate.Reason,
                terminate.Cleanup),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.GetType(), "Unsupported control command.")
        };
    }

    static ProcessControlCommand? FindPriorCommand(
        ProcessControlState state,
        ProcessControlCommandContext context)
    {
        if (state.FindReceipt(context.CommandId) is { } sameCommand)
            return sameCommand.Command;

        for (var index = 0; index < state.Receipts.Length; index++)
        {
            var candidate = state.Receipts[index].Command;
            if (candidate.Context.IdempotencyKey == context.IdempotencyKey)
                return candidate;
        }

        return null;
    }

    static ProcessControlCommandContext Rebind(
        ProcessControlCommandContext context,
        ExecutionApiInvocationContext invocation,
        ProcessControlCommandContext? prior) =>
        new(
            context.CommandId,
            context.IdempotencyKey,
            context.ProcessInstanceId,
            prior?.Authorization ?? invocation.Authorization,
            prior?.IssuedAtUtc ?? invocation.IssuedAtUtc,
            prior?.Provenance ?? invocation.Provenance);

    static SignalEnvelope Rebind(SignalEnvelope signal, InteractionEnvelopeContext trustedContext) =>
        new(
            signal.SchemaVersion,
            trustedContext,
            signal.Contract,
            signal.Payload,
            signal.Target);
}
