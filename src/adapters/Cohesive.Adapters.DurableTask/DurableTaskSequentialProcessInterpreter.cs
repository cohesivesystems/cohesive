using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Adapters.DurableTask;

static class DurableTaskSequentialProcessInterpreter
{
    internal static async Task<DurableTaskSequentialProcessResult> RunAsync(
        CompiledProcessPlan plan,
        DurableTaskSequentialProcessStart start,
        IDurableRequestBindingResolver bindingResolver,
        Func<DurableTaskProcessHostOperation, Task<ProcessOperationResult>> executeOperation,
        Func<DurableOperationInvocation, Task<DurableTaskDurableOperationAttemptResult>> executeDurableOperation,
        Func<DurableOperationInvocation, Task<DurableTaskDurableOperationAttemptResult>> executeChildProcess,
        Func<DurableOperationState, Task<DurableTaskDurableOperationReconciliationResult>> reconcileDurableOperation,
        Func<Task<ProcessActivationInput>> waitForInteraction,
        Func<TimeSpan, CancellationToken, Task> createTimer,
        Func<DateTimeOffset> getCurrentUtc,
        Action<DurableTaskSequentialProcessResult>? observe = null,
        Func<DurableTaskSequentialProcessStart, Task>? continueAsNew = null,
        Func<ProcessChildCancellationIntent, Task>? dispatchChildCancellation = null,
        Func<Task<ProcessChildCancellationIntent>>? waitForChildCancellation = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(bindingResolver);
        ArgumentNullException.ThrowIfNull(executeOperation);
        ArgumentNullException.ThrowIfNull(executeDurableOperation);
        ArgumentNullException.ThrowIfNull(executeChildProcess);
        ArgumentNullException.ThrowIfNull(reconcileDurableOperation);
        ArgumentNullException.ThrowIfNull(waitForInteraction);
        ArgumentNullException.ThrowIfNull(createTimer);
        ArgumentNullException.ThrowIfNull(getCurrentUtc);
        if (plan.DefinitionReference != start.Receipt.Request.Definition)
        {
            throw new ArgumentException("The Process start pins a different exact compiled definition.", nameof(start));
        }

        var resumed = start.Resume?.Result;
        var state = resumed?.State ?? ProcessReferenceInterpreter.Create(plan, start.Receipt);
        var cause = resumed is null ? ProcessActivationCause.Start : ProcessActivationCause.Continue;
        var observedAtUtc = resumed is null ? start.Receipt.AcceptedAtUtc : RequireUtc(getCurrentUtc());
        ImmutableArray<ProcessActivationInput> inputs = [];
        List<InteractionEnvelope> emissions = resumed is null ? [] : [.. resumed.Emissions];
        List<ProcessInputReceipt> inputAdmissions = resumed is null ? [] : [.. resumed.InputAdmissions];
        List<DocumentValidationDiagnostic> diagnostics = resumed is null ? [] : [.. resumed.Diagnostics];
        List<ProcessExecutionEvidence> evidence = resumed is null ? [] : [.. resumed.Evidence];
        Dictionary<EmissionId, DurableTaskDurableOperationResult> durableOperations = resumed is null
            ? []
            : resumed.DurableOperations.ToDictionary(static operation => operation.State.OperationId);
        Dictionary<EmissionId, PendingDurableOperation> pendingOperations = [];
        Dictionary<ProcessWaitRegistrationId, PendingProcessTimer> pendingTimers = [];
        Task<ProcessActivationInput>? pendingInteraction = null;
        Task<ProcessChildCancellationIntent>? pendingChildCancellation = null;
        HashSet<string> dispatchedChildCancellations = new(StringComparer.Ordinal);
        ProcessCancellationIntent? cancellation = null;

        while (true)
        {
            var activation = new ProcessActivation(
                DurableTaskSequentialProcessIdentities.Activation(state),
                cause,
                observedAtUtc,
                start.ActivationContext,
                inputs,
                cancellation);
            cancellation = null;
            var decision = await ActivateAsync(plan, state, activation, executeOperation).ConfigureAwait(true);
            state = decision.State;
            emissions.AddRange(decision.Emissions);
            inputAdmissions.AddRange(decision.InputAdmissions);
            diagnostics.AddRange(decision.Diagnostics);
            evidence.Add(decision.Evidence);
            var result = new DurableTaskSequentialProcessResult(
                decision.Disposition,
                state,
                [.. emissions],
                [.. inputAdmissions],
                [.. diagnostics],
                [.. evidence],
                [.. durableOperations.Values.OrderBy(
                    static operation => operation.State.OperationId.Value,
                    StringComparer.Ordinal)]);
            await SynchronizeChildLifecycleAsync().ConfigureAwait(true);
            SynchronizeTimers();
            result = CurrentResult(decision.Disposition);
            observe?.Invoke(result);

            switch (decision.Disposition)
            {
                case ProcessActivationDisposition.Completed:
                case ProcessActivationDisposition.Failed:
                case ProcessActivationDisposition.Cancelled:
                    await AwaitPropagatedChildClosuresAsync().ConfigureAwait(true);
                    return CurrentResult(decision.Disposition);

                case ProcessActivationDisposition.Quiescent:
                case ProcessActivationDisposition.Rejected:
                    if (pendingOperations.Count == 0
                        && pendingTimers.Count == 0
                        && state.OutstandingRequests.IsEmpty)
                    {
                        return result;
                    }
                    var quiescentStimulus = await WaitForNextStimulusAsync().ConfigureAwait(true);
                    result = CurrentResult(decision.Disposition);
                    observe?.Invoke(result);
                    if (!quiescentStimulus.Available)
                    {
                        return result;
                    }
                    Apply(quiescentStimulus);
                    observedAtUtc = RequireUtc(getCurrentUtc());
                    break;

                case ProcessActivationDisposition.DurableCut:
                    var safePoint = decision.Evidence.SafePointNode
                        ?? throw new InvalidOperationException("A durable-cut decision did not identify its safe-point node.");
                    switch (plan.GetNode(safePoint))
                    {
                        case TimerProcessNode:
                            if (state.Tokens.Any(static token =>
                                    token.Disposition == ExecutionTokenDisposition.Ready))
                            {
                                inputs = [];
                                cause = ProcessActivationCause.Continue;
                                observedAtUtc = RequireUtc(getCurrentUtc());
                                break;
                            }

                            var timerStimulus = await WaitForNextStimulusAsync().ConfigureAwait(true);
                            result = CurrentResult(decision.Disposition);
                            observe?.Invoke(result);
                            if (!timerStimulus.Available)
                            {
                                return result;
                            }
                            Apply(timerStimulus);
                            observedAtUtc = RequireUtc(getCurrentUtc());
                            break;
                        case RequestProcessNode:
                        case InvokeProcessProcessNode:
                        case ForEachPartitionProcessNode:
                            foreach (var request in decision.Emissions
                                         .OfType<RequestEnvelope>()
                                         .OrderBy(static request => request.Context.EmissionId.Value, StringComparer.Ordinal))
                            {
                                var operationId = request.Context.EmissionId;
                                if (durableOperations.ContainsKey(operationId)
                                    || pendingOperations.ContainsKey(operationId))
                                {
                                    continue;
                                }
                                if (!bindingResolver.TryResolve(request, out var binding) || binding is null)
                                {
                                    continue;
                                }
                                var contracts = plan.ValidationContext.InteractionContracts
                                    ?? throw new InvalidOperationException(
                                        "Automatic durable Request execution requires the compiled interaction catalog.");
                                var executor = request.ChildTarget is null
                                    ? executeDurableOperation
                                    : executeChildProcess;
                                var operationTask = DurableTaskDurableOperationInterpreter.RunAsync(
                                    contracts,
                                    request,
                                    binding,
                                    executor,
                                    reconcileDurableOperation,
                                    createTimer,
                                    getCurrentUtc,
                                    (cut, operationState) => createTimer(
                                        TimeSpan.Zero,
                                        CancellationToken.None),
                                    operationState => ObserveTarget(state, operationState));
                                pendingOperations.Add(operationId, new(request, operationTask));
                            }

                            if (state.Tokens.Any(static token =>
                                    token.Disposition == ExecutionTokenDisposition.Ready))
                            {
                                inputs = [];
                                cause = ProcessActivationCause.Continue;
                                observedAtUtc = RequireUtc(getCurrentUtc());
                                break;
                            }

                            var requestStimulus = await WaitForNextStimulusAsync().ConfigureAwait(true);
                            result = CurrentResult(decision.Disposition);
                            observe?.Invoke(result);
                            if (!requestStimulus.Available)
                            {
                                return result;
                            }
                            Apply(requestStimulus);
                            observedAtUtc = RequireUtc(getCurrentUtc());
                            break;
                        case ForkProcessNode:
                        case RepeatAcrossActivationProcessNode:
                        case DurableCutProcessNode:
                            if (pendingOperations.Count != 0)
                            {
                                throw new InvalidOperationException(
                                    "Continue-as-new cannot discard incomplete durable Request tasks.");
                            }
                            if (pendingTimers.Count == 0 && continueAsNew is not null)
                            {
                                await continueAsNew(start.ContinueFrom(result)).ConfigureAwait(true);
                                return result;
                            }
                            await createTimer(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(true);
                            inputs = [];
                            cause = ProcessActivationCause.Continue;
                            observedAtUtc = RequireUtc(getCurrentUtc());
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Durable Task execution cannot resume unsupported safe point '{safePoint.Value}'.");
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(decision.Disposition),
                        decision.Disposition,
                        "Unsupported canonical Process activation disposition.");
            }
        }

        DurableTaskSequentialProcessResult CurrentResult(ProcessActivationDisposition disposition) => new(
            disposition,
            state,
            [.. emissions],
            [.. inputAdmissions],
            [.. diagnostics],
            [.. evidence],
            [.. durableOperations.Values.OrderBy(
                static operation => operation.State.OperationId.Value,
                StringComparer.Ordinal)]);

        async Task<NextProcessStimulus> WaitForNextStimulusAsync()
        {
            while (true)
            {
                var bound = pendingOperations.Keys.ToHashSet();
                var hasExternalRequest = state.OutstandingRequests.Any(request => !bound.Contains(request.Emission));
                if (hasExternalRequest && pendingInteraction is null)
                {
                    pendingInteraction = waitForInteraction();
                }
                if (pendingOperations.Count == 0
                    && waitForChildCancellation is not null
                    && pendingChildCancellation is null)
                {
                    pendingChildCancellation = waitForChildCancellation();
                }

                List<Task> candidates =
                [
                    .. pendingOperations
                        .OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal)
                        .Select(static pair => (Task)pair.Value.Execution),
                    .. pendingTimers
                        .OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal)
                        .Select(static pair => pair.Value.Execution)
                ];
                if (pendingInteraction is not null)
                {
                    candidates.Add(pendingInteraction);
                }
                if (pendingOperations.Count == 0 && pendingChildCancellation is not null)
                {
                    candidates.Add(pendingChildCancellation);
                }
                if (candidates.Count == 0)
                {
                    throw new InvalidOperationException(
                        "A waiting Process has neither a scheduled durable Request nor a control or interaction source.");
                }

                var completed = await Task.WhenAny(candidates).ConfigureAwait(true);
                if (ReferenceEquals(completed, pendingInteraction))
                {
                    var input = await pendingInteraction!.ConfigureAwait(true);
                    pendingInteraction = null;
                    return NextProcessStimulus.For(input);
                }
                if (ReferenceEquals(completed, pendingChildCancellation))
                {
                    var intent = await pendingChildCancellation!.ConfigureAwait(true);
                    pendingChildCancellation = null;
                    return NextProcessStimulus.For(ToCancellation(intent));
                }

                var completedTimer = pendingTimers
                    .OrderBy(static candidate => candidate.Key.Value, StringComparer.Ordinal)
                    .FirstOrDefault(candidate => ReferenceEquals(candidate.Value.Execution, completed));
                if (completedTimer.Value is not null)
                {
                    await completedTimer.Value.Execution.ConfigureAwait(true);
                    pendingTimers.Remove(completedTimer.Key);
                    completedTimer.Value.Cancellation.Dispose();
                    return NextProcessStimulus.ForTimer();
                }

                var pair = pendingOperations
                    .OrderBy(static candidate => candidate.Key.Value, StringComparer.Ordinal)
                    .First(candidate => ReferenceEquals(candidate.Value.Execution, completed));
                var operation = await pair.Value.Execution.ConfigureAwait(true);
                pendingOperations.Remove(pair.Key);
                durableOperations[operation.State.OperationId] = operation;
                if (operation.Disposition == DurableTaskDurableOperationDisposition.ReplyReady)
                {
                    return NextProcessStimulus.For(operation.Input
                        ?? throw new InvalidOperationException("A Reply-ready durable operation returned no input."));
                }
                if (operation.Disposition == DurableTaskDurableOperationDisposition.ResultDispositioned)
                {
                    continue;
                }
                return NextProcessStimulus.Unavailable;
            }
        }

        async Task SynchronizeChildLifecycleAsync()
        {
            foreach (var child in state.Children
                         .Where(static child => child.Disposition == ProcessChildDisposition.Detached)
                         .OrderBy(static child => child.RegistrationId, StringComparer.Ordinal))
            {
                if (child.RequestEmission is not { } emission
                    || !pendingOperations.Remove(emission, out var detached))
                {
                    continue;
                }
                ObserveAbandoned(detached.Execution);
            }

            foreach (var intent in ProcessChildCancellationIntents.Project(state))
            {
                if (!dispatchedChildCancellations.Add(intent.IntentId))
                {
                    continue;
                }
                if (dispatchChildCancellation is null)
                {
                    throw new InvalidOperationException(
                        "The Process requested propagated child cancellation, but this Durable Task host has no child-control dispatcher.");
                }
                await dispatchChildCancellation(intent).ConfigureAwait(true);
            }
        }

        void SynchronizeTimers()
        {
            var active = state.Waits
                .Where(static wait => wait.Active && wait.Kind == ProcessWaitKind.Timer)
                .ToDictionary(static wait => wait.RegistrationId);
            foreach (var obsolete in pendingTimers.Keys.Where(key => !active.ContainsKey(key)).ToArray())
            {
                var pending = pendingTimers[obsolete];
                pendingTimers.Remove(obsolete);
                pending.Cancellation.Cancel();
                pending.Cancellation.Dispose();
                ObserveAbandoned(pending.Execution);
            }

            if (active.Count == 0)
            {
                return;
            }

            var currentUtc = RequireUtc(getCurrentUtc());
            foreach (var wait in active.Values.OrderBy(static wait => wait.RegistrationId.Value, StringComparer.Ordinal))
            {
                if (pendingTimers.ContainsKey(wait.RegistrationId))
                {
                    continue;
                }
                var timer = wait.Timers.Single();
                var delay = timer.DueAtUtc - currentUtc;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }
                var cancellationSource = new CancellationTokenSource();
                var execution = createTimer(delay, cancellationSource.Token)
                    ?? throw new InvalidOperationException("The Durable Task timer delegate returned null.");
                pendingTimers.Add(
                    wait.RegistrationId,
                    new(cancellationSource, execution));
            }
        }

        async Task AwaitPropagatedChildClosuresAsync()
        {
            var propagated = state.Children
                .Where(static child => child.Disposition == ProcessChildDisposition.CancellationRequested)
                .Select(static child => child.RequestEmission)
                .Where(static emission => emission is not null)
                .Select(static emission => emission!.Value)
                .ToHashSet();
            while (pendingOperations.Any(pair => propagated.Contains(pair.Key)))
            {
                var pair = pendingOperations
                    .Where(pair => propagated.Contains(pair.Key))
                    .OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal)
                    .First();
                var operation = await pair.Value.Execution.ConfigureAwait(true);
                pendingOperations.Remove(pair.Key);
                durableOperations[operation.State.OperationId] = operation;
                if (operation.Disposition == DurableTaskDurableOperationDisposition.ReplyReady)
                {
                    throw new InvalidOperationException(
                        "A cancellation-requested child result advanced a parent target that was already closed.");
                }
            }
        }

        ProcessCancellationIntent ToCancellation(ProcessChildCancellationIntent intent)
        {
            if (intent.ChildDefinition != state.Definition
                || intent.ChildContinuation != state.Continuation)
            {
                throw new InvalidOperationException(
                    "A propagated child cancellation named another definition or continuation.");
            }
            return new(
                state.Continuation.ProcessAttemptId,
                new ProcessControlReason("parent.child-cancellation"));
        }

        void Apply(NextProcessStimulus stimulus)
        {
            if (stimulus.TimerElapsed)
            {
                inputs = [];
                cancellation = null;
                cause = ProcessActivationCause.Timer;
                return;
            }
            if (stimulus.Input is not null)
            {
                inputs = [stimulus.Input];
                cancellation = null;
                cause = ProcessActivationCause.Interaction;
                return;
            }
            inputs = [];
            cancellation = stimulus.Cancellation
                ?? throw new InvalidOperationException("An available Process stimulus carried no input or cancellation.");
            cause = ProcessActivationCause.Control;
        }
    }

    static DurableOperationTargetObservation ObserveTarget(
        ProcessContinuationState state,
        DurableOperationState operation)
    {
        if (operation.Request.ResponseTarget is not ProcessTokenInteractionTarget target)
        {
            throw new InvalidOperationException(
                "The Durable Task Process interpreter admits durable results only to Process-token targets.");
        }
        if (target.Continuation != state.Continuation)
        {
            return new(target, DurableOperationResultArrival.Stale, DurableOperationAdmissionDisposition.Rejected);
        }

        var token = state.Tokens.FirstOrDefault(candidate => candidate.Id == target.Token);
        if (token is null)
        {
            return new(target, DurableOperationResultArrival.Stale, DurableOperationAdmissionDisposition.Rejected);
        }
        var wait = target.WaitRegistrationId is { } registration
            ? state.Waits.FirstOrDefault(candidate => candidate.RegistrationId == registration)
            : state.Waits.FirstOrDefault(candidate =>
                candidate.Token == target.Token
                && candidate.Kind == ProcessWaitKind.Request
                && candidate.ObligationEmission == operation.OperationId);
        if (wait is null
            || wait.Token != target.Token
            || wait.Kind != ProcessWaitKind.Request
            || wait.ObligationEmission != operation.OperationId)
        {
            return new(
                target,
                IsTerminal(token.Disposition)
                    ? DurableOperationResultArrival.Late
                    : DurableOperationResultArrival.Stale,
                DurableOperationAdmissionDisposition.Rejected);
        }
        if (!wait.Active || IsTerminal(token.Disposition))
        {
            return new(
                target,
                DurableOperationResultArrival.Late,
                FindPriorDisposition(state, wait) ?? DurableOperationAdmissionDisposition.Rejected);
        }
        var outstanding = state.OutstandingRequests.Any(candidate =>
            candidate.Token == target.Token
            && candidate.Emission == operation.OperationId);
        return outstanding && token.Disposition == ExecutionTokenDisposition.Waiting
            ? new(target, DurableOperationResultArrival.Eligible)
            : new(target, DurableOperationResultArrival.Stale, DurableOperationAdmissionDisposition.Rejected);
    }

    static DurableOperationAdmissionDisposition? FindPriorDisposition(
        ProcessContinuationState state,
        ProcessWaitState wait)
    {
        if (wait.WinnerInput is not { } winner)
        {
            return null;
        }
        var receipt = state.InputReceipts.FirstOrDefault(candidate => candidate.Emission == winner);
        return receipt?.Disposition switch
        {
            ProcessInputAdmissionDisposition.Consumed => DurableOperationAdmissionDisposition.Accepted,
            ProcessInputAdmissionDisposition.Observed => DurableOperationAdmissionDisposition.Observed,
            ProcessInputAdmissionDisposition.Rejected => DurableOperationAdmissionDisposition.Rejected,
            _ => null
        };
    }

    static bool IsTerminal(ExecutionTokenDisposition disposition) => disposition is
        ExecutionTokenDisposition.Completed
        or ExecutionTokenDisposition.Failed
        or ExecutionTokenDisposition.Cancelled;

    static void ObserveAbandoned(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    static async Task<ProcessActivationDecision> ActivateAsync(
        CompiledProcessPlan plan,
        ProcessContinuationState state,
        ProcessActivation activation,
        Func<DurableTaskProcessHostOperation, Task<ProcessOperationResult>> executeOperation)
    {
        var host = new SuspendingHost();
        while (true)
        {
            try
            {
                return ProcessReferenceInterpreter.Activate(plan, state, activation, host);
            }
            catch (PendingHostOperationException pending)
            {
                var result = await executeOperation(pending.Operation).ConfigureAwait(true)
                    ?? throw new InvalidOperationException("A Durable Task host-operation activity returned null.");
                host.Materialize(pending.Operation, result);
            }
        }
    }

    static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Durable Task orchestration time must use the UTC offset.");
        }
        return value;
    }

    sealed class PendingHostOperationException(DurableTaskProcessHostOperation operation) : Exception
    {
        internal DurableTaskProcessHostOperation Operation { get; } = operation;
    }

    sealed class SuspendingHost : IProcessReferenceHost
    {
        readonly Dictionary<OperationKey, MaterializedOperation> materialized = [];

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            Resolve(DurableTaskProcessHostOperation.For(invocation));

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            Resolve(DurableTaskProcessHostOperation.For(evaluation));

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new NotSupportedException(
                $"Signal target resolution at '{resolution.Node.Value}' is outside the bounded executable slice.");

        internal void Materialize(DurableTaskProcessHostOperation operation, ProcessOperationResult result)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(result);
            if (!result.IsValidOutcome())
            {
                throw new InvalidOperationException("A Durable Task host-operation activity returned an invalid outcome.");
            }

            var key = Key(operation);
            if (materialized.TryGetValue(key, out var retained))
            {
                if (retained.Operation != operation || retained.Result != result)
                {
                    throw new InvalidOperationException(
                        "One Process host-operation occurrence was materialized with conflicting evidence.");
                }
                return;
            }
            materialized.Add(key, new(operation, result));
        }

        ProcessOperationResult Resolve(DurableTaskProcessHostOperation operation)
        {
            var key = Key(operation);
            if (!materialized.TryGetValue(key, out var retained))
            {
                throw new PendingHostOperationException(operation);
            }
            if (retained.Operation != operation)
            {
                throw new InvalidOperationException(
                    "One Process host-operation occurrence produced inconsistent invocation evidence during replay.");
            }
            return retained.Result;
        }

        static OperationKey Key(DurableTaskProcessHostOperation operation) => operation.Kind switch
        {
            DurableTaskProcessHostOperationKind.Transition => Key(operation.Transition!),
            DurableTaskProcessHostOperationKind.RelationQuery => Key(operation.RelationQuery!),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, "Unsupported host operation.")
        };

        static OperationKey Key(ProcessTransitionInvocation invocation) => new(
            invocation.Continuation,
            invocation.Activation,
            invocation.Token,
            invocation.Node,
            invocation.Occurrence);

        static OperationKey Key(ProcessRelationEvaluation evaluation) => new(
            evaluation.Continuation,
            evaluation.Activation,
            evaluation.Token,
            evaluation.Node,
            evaluation.Occurrence);
    }

    readonly record struct OperationKey(
        ProcessContinuationIdentity Continuation,
        ActivationId Activation,
        TokenId Token,
        ExecutionNodeId Node,
        long Occurrence);

    sealed record MaterializedOperation(
        DurableTaskProcessHostOperation Operation,
        ProcessOperationResult Result);

    sealed record PendingDurableOperation(
        RequestEnvelope Request,
        Task<DurableTaskDurableOperationResult> Execution);

    sealed record PendingProcessTimer(
        CancellationTokenSource Cancellation,
        Task Execution);

    readonly record struct NextProcessStimulus(
        bool Available,
        ProcessActivationInput? Input,
        ProcessCancellationIntent? Cancellation,
        bool TimerElapsed)
    {
        internal static NextProcessStimulus Unavailable => new(false, null, null, false);

        internal static NextProcessStimulus For(ProcessActivationInput input) =>
            new(true, input ?? throw new ArgumentNullException(nameof(input)), null, false);

        internal static NextProcessStimulus For(ProcessCancellationIntent cancellation) =>
            new(true, null, cancellation ?? throw new ArgumentNullException(nameof(cancellation)), false);

        internal static NextProcessStimulus ForTimer() => new(true, null, null, true);
    }
}
