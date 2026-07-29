namespace Cohesive.Execution;

/// <summary>Pure lifecycle legality and state-transition authority for Process control.</summary>
/// <remarks>
/// This reducer intentionally excludes command identity, authorization, portable-value validation, chronology,
/// and physical attempt-lineage construction. Those concerns surround the reducer, while every interpretation
/// shares these mode, phase, and attempt-affinity semantics.
/// </remarks>
internal static class ProcessControlLifecycleSemantics
{
    internal readonly record struct Position(
        ProcessControlMode Mode,
        ProcessControlExecutionPhase Phase,
        ProcessAttemptId AttemptId);

    internal static bool TryClassifyCommand(
        Position position,
        ProcessControlCommand command,
        bool duplicateSignal,
        out ProcessControlReceiptDisposition disposition,
        out Position next)
    {
        disposition = ClassifyDisposition(position, command, duplicateSignal);
        if (disposition == ProcessControlReceiptDisposition.Unspecified
            || command.Expectation?.Continuation.ProcessAttemptId != position.AttemptId)
        {
            next = position;
            return false;
        }

        next = ApplyEffect(position, command, disposition);
        return true;
    }

    internal static bool TryApplyReceipt(
        Position position,
        ProcessControlCommand command,
        ProcessControlReceiptDisposition disposition,
        out Position next)
    {
        var expected = ClassifyDisposition(
            position,
            command,
            duplicateSignal: disposition == ProcessControlReceiptDisposition.SignalDuplicate);
        if (command.Expectation?.Continuation.ProcessAttemptId != position.AttemptId
            || disposition != expected)
        {
            next = position;
            return false;
        }

        next = ApplyEffect(position, command, disposition);
        return true;
    }

    static ProcessControlReceiptDisposition ClassifyDisposition(
        Position position,
        ProcessControlCommand command,
        bool duplicateSignal) =>
        command switch
        {
            PauseProcessCommand when position.Mode == ProcessControlMode.PauseRequested =>
                ProcessControlReceiptDisposition.AlreadyRequested,
            PauseProcessCommand when position.Mode == ProcessControlMode.Paused =>
                ProcessControlReceiptDisposition.AlreadySatisfied,
            PauseProcessCommand when position.Mode == ProcessControlMode.Running
                && position.Phase == ProcessControlExecutionPhase.InActivation =>
                ProcessControlReceiptDisposition.DeferredToSafePoint,
            PauseProcessCommand when position.Mode == ProcessControlMode.Running
                && IsSafeBoundary(position.Phase) =>
                ProcessControlReceiptDisposition.Applied,

            ContinueProcessCommand when position.Mode == ProcessControlMode.Running =>
                ProcessControlReceiptDisposition.AlreadySatisfied,
            ContinueProcessCommand when position.Mode == ProcessControlMode.Paused
                && IsSafeBoundary(position.Phase) =>
                ProcessControlReceiptDisposition.Applied,

            RestartProcessAttemptCommand when position.Mode == ProcessControlMode.Running
                && position.Phase == ProcessControlExecutionPhase.InActivation =>
                ProcessControlReceiptDisposition.DeferredToSafePoint,
            RestartProcessAttemptCommand when position.Mode is ProcessControlMode.Running or ProcessControlMode.Paused
                && IsSafeBoundary(position.Phase) =>
                ProcessControlReceiptDisposition.Applied,

            CancelProcessCommand when position.Mode == ProcessControlMode.CancellationRequested =>
                ProcessControlReceiptDisposition.AlreadyRequested,
            CancelProcessCommand when position.Mode == ProcessControlMode.Cancelled =>
                ProcessControlReceiptDisposition.AlreadySatisfied,
            CancelProcessCommand when position.Mode == ProcessControlMode.Running
                && position.Phase == ProcessControlExecutionPhase.InActivation =>
                ProcessControlReceiptDisposition.DeferredToSafePoint,
            CancelProcessCommand when position.Mode is ProcessControlMode.Running or ProcessControlMode.Paused
                && IsSafeBoundary(position.Phase) =>
                ProcessControlReceiptDisposition.Applied,

            TerminateProcessCommand when position.Mode == ProcessControlMode.Terminated =>
                ProcessControlReceiptDisposition.AlreadySatisfied,
            TerminateProcessCommand when position.Mode is not (ProcessControlMode.Cancelled or ProcessControlMode.Terminated) =>
                ProcessControlReceiptDisposition.Applied,

            SignalProcessCommand when duplicateSignal
                && position.Mode is ProcessControlMode.Running
                    or ProcessControlMode.Paused
                    or ProcessControlMode.PauseRequested =>
                ProcessControlReceiptDisposition.SignalDuplicate,
            SignalProcessCommand when !duplicateSignal && position.Mode == ProcessControlMode.Running =>
                ProcessControlReceiptDisposition.SignalAccepted,
            SignalProcessCommand when !duplicateSignal
                && position.Mode is ProcessControlMode.Paused or ProcessControlMode.PauseRequested =>
                ProcessControlReceiptDisposition.SignalBuffered,
            _ => ProcessControlReceiptDisposition.Unspecified
        };

    static Position ApplyEffect(
        Position position,
        ProcessControlCommand command,
        ProcessControlReceiptDisposition disposition) =>
        (command, disposition) switch
        {
            (PauseProcessCommand, ProcessControlReceiptDisposition.Applied) =>
                position with { Mode = ProcessControlMode.Paused },
            (PauseProcessCommand, ProcessControlReceiptDisposition.DeferredToSafePoint) =>
                position with { Mode = ProcessControlMode.PauseRequested },
            (ContinueProcessCommand, ProcessControlReceiptDisposition.Applied) =>
                position with { Mode = ProcessControlMode.Running },
            (RestartProcessAttemptCommand restart, ProcessControlReceiptDisposition.Applied) =>
                position with
                {
                    Phase = ProcessControlExecutionPhase.Ready,
                    AttemptId = restart.Plan.NewAttemptId
                },
            (RestartProcessAttemptCommand, ProcessControlReceiptDisposition.DeferredToSafePoint) =>
                position with { Mode = ProcessControlMode.RestartRequested },
            (CancelProcessCommand, ProcessControlReceiptDisposition.Applied) =>
                position with
                {
                    Mode = ProcessControlMode.Cancelled,
                    Phase = ProcessControlExecutionPhase.Stopped
                },
            (CancelProcessCommand, ProcessControlReceiptDisposition.DeferredToSafePoint) =>
                position with { Mode = ProcessControlMode.CancellationRequested },
            (TerminateProcessCommand, ProcessControlReceiptDisposition.Applied) =>
                position with
                {
                    Mode = ProcessControlMode.Terminated,
                    Phase = ProcessControlExecutionPhase.Stopped
                },
            _ => position
        };

    internal static bool TryBeginActivation(Position position, ProcessAttemptId attemptId, out Position next)
    {
        next = position;
        if (position.Mode != ProcessControlMode.Running
            || !IsSafeBoundary(position.Phase)
            || attemptId != position.AttemptId)
        {
            return false;
        }

        next = position with { Phase = ProcessControlExecutionPhase.InActivation };
        return true;
    }

    internal static bool TryReachSafePoint(
        Position position,
        ProcessAttemptId attemptId,
        ProcessAttemptId? restartAttemptId,
        out Position next)
    {
        next = position;
        if (position.Mode is not (ProcessControlMode.Running
                or ProcessControlMode.PauseRequested
                or ProcessControlMode.RestartRequested
                or ProcessControlMode.CancellationRequested)
            || position.Phase != ProcessControlExecutionPhase.InActivation
            || attemptId != position.AttemptId)
        {
            return false;
        }

        next = position.Mode switch
        {
            ProcessControlMode.Running => position with { Phase = ProcessControlExecutionPhase.AtSafePoint },
            ProcessControlMode.PauseRequested => position with
            {
                Mode = ProcessControlMode.Paused,
                Phase = ProcessControlExecutionPhase.AtSafePoint
            },
            ProcessControlMode.RestartRequested when restartAttemptId is { } replacement
                && replacement != position.AttemptId => position with
                {
                    Mode = ProcessControlMode.Running,
                    Phase = ProcessControlExecutionPhase.Ready,
                    AttemptId = replacement
                },
            ProcessControlMode.CancellationRequested => position with
            {
                Mode = ProcessControlMode.Cancelled,
                Phase = ProcessControlExecutionPhase.Stopped
            },
            _ => position
        };
        return position.Mode != ProcessControlMode.RestartRequested || next != position;
    }

    internal static bool TryBindAttemptAffinity(Position position, ProcessAttemptId attemptId, out Position next)
    {
        next = position;
        return attemptId == position.AttemptId
            && position.Mode is ProcessControlMode.Running
                or ProcessControlMode.Paused
                or ProcessControlMode.PauseRequested;
    }

    static bool IsSafeBoundary(ProcessControlExecutionPhase phase) =>
        phase is ProcessControlExecutionPhase.Ready or ProcessControlExecutionPhase.AtSafePoint;
}
