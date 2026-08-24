using System.Collections.Immutable;
using Cohesive.Api.Execution;
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
        Func<Task<ProcessChildCancellationIntent>>? waitForChildCancellation = null,
        Func<ProcessSignalTargetResolution, Task<ProcessSignalTargetResult>>? resolveSignalTarget = null,
        Func<SignalEnvelope, Task>? deliverSignal = null,
        Func<Task<ProcessControlCommand>>? waitForControl = null,
        Func<DomainEventPublicationInvocation, Task<DurableTaskDomainEventPublication>>? publishDomainEvent = null,
        Func<Task<DurableTaskProcessControlRequest>>? waitForControlRequest = null,
        Func<string, DurableTaskProcessControlResponse, Task>? retainControlResponse = null)
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
        var planSendsSignals = plan.Definition.Nodes.Any(static node => node is SendSignalProcessNode);
        if (planSendsSignals)
        {
            ArgumentNullException.ThrowIfNull(resolveSignalTarget);
            ArgumentNullException.ThrowIfNull(deliverSignal);
        }
        var planDirectlyEmitsDomainEvents = plan.Definition.Nodes.Any(static node => node is EmitEventProcessNode);
        if (planDirectlyEmitsDomainEvents)
        {
            ArgumentNullException.ThrowIfNull(publishDomainEvent);
            if (start.ActivationContext.Delivery.Durability != InteractionDurabilityDemand.Durable
                || start.ActivationContext.Delivery.Visibility != InteractionVisibilityDemand.AfterOriginCommit)
            {
                throw new ArgumentException(
                    "Durable Task domain-event publication requires durable after-origin-commit delivery.",
                    nameof(start));
            }
        }
        if (waitForControl is not null && waitForControlRequest is not null)
        {
            throw new ArgumentException(
                "A Durable Task Process cannot have two lifecycle-control event authorities.",
                nameof(waitForControlRequest));
        }
        if (waitForControlRequest is not null && retainControlResponse is null)
        {
            throw new ArgumentNullException(
                nameof(retainControlResponse),
                "Durable lifecycle-control admission requires a response-retention callback.");
        }

        var resumed = start.Resume?.Result;
        var state = resumed?.State ?? ProcessReferenceInterpreter.Create(plan, start.Receipt);
        var control = resumed?.Control ?? start.Receipt.CreateInitialState();
        var controlExecutor = CreateControlExecutor(plan);
        var cancellationPolicy = plan.Definition.Nodes.Any(static node => node is CancellationFinalizerProcessNode)
            ? ProcessCancellationCompletionPolicy.AuthoredFinalization
            : ProcessCancellationCompletionPolicy.Immediate;
        ProcessControlDecision? latestControlDecision = resumed?.LatestControlDecision;
        var lastDisposition = resumed?.Disposition ?? ProcessActivationDisposition.Quiescent;
        var cause = resumed is null ? ProcessActivationCause.Start : ProcessActivationCause.Continue;
        var observedAtUtc = resumed is null ? start.Receipt.AcceptedAtUtc : RequireUtc(getCurrentUtc());
        ImmutableArray<ProcessActivationInput> inputs = [];
        List<InteractionEnvelope> emissions = resumed is null ? [] : [.. resumed.Emissions];
        List<ProcessInputReceipt> inputAdmissions = resumed is null ? [] : [.. resumed.InputAdmissions];
        List<DocumentValidationDiagnostic> diagnostics = resumed is null ? [] : [.. resumed.Diagnostics];
        List<ProcessExecutionEvidence> evidence = resumed is null ? [] : [.. resumed.Evidence];
        List<NormalizedExecutionTrace> traces = resumed is null ? [] : [.. resumed.Traces];
        List<DurableTaskDomainEventPublication> domainEventPublications = resumed is null
            ? []
            : [.. resumed.DomainEventPublications];
        Dictionary<EmissionId, DurableTaskDomainEventPublication> domainEventPublicationsByEmission =
            domainEventPublications.ToDictionary(static publication => publication.EmissionId);
        Dictionary<EmissionId, DurableTaskDurableOperationResult> durableOperations = resumed is null
            ? []
            : resumed.DurableOperations.ToDictionary(static operation => operation.State.OperationId);
        Dictionary<EmissionId, PendingDurableOperation> pendingOperations = [];
        Dictionary<(ProcessWaitRegistrationId Wait, ExecutionNodeId Clause), PendingProcessTimer> pendingTimers = [];
        Task<ProcessActivationInput>? pendingInteraction = null;
        Task<ProcessChildCancellationIntent>? pendingChildCancellation = null;
        Task<PendingProcessControl>? pendingControl = null;
        HashSet<string> dispatchedChildCancellations = new(StringComparer.Ordinal);
        ProcessCancellationIntent? cancellation = null;
        ImmutableArray<ProcessChildCancellationClosure> childCancellationClosures = [];
        var planAcceptsAwaitMatchInteractions = plan.Definition.Nodes
            .OfType<AwaitMatchProcessNode>()
            .Any(static node => node.Clauses.Any(static clause => clause is ProcessAwaitInteractionClause));
        if (planAcceptsAwaitMatchInteractions)
        {
            pendingInteraction = BeginInteractionWait();
        }
        if (waitForControl is not null || waitForControlRequest is not null)
        {
            pendingControl = BeginControlWait();
        }

        while (true)
        {
            await DrainCompletedControlCommandsAsync().ConfigureAwait(true);
            if (control.Mode is ProcessControlMode.Terminated or ProcessControlMode.CancellationFailed)
            {
                AbandonAttemptOwnedPhysicalWork();
                return CurrentResult(lastDisposition);
            }
            if (control.Mode == ProcessControlMode.Cancelled)
            {
                await SynchronizeChildLifecycleAsync().ConfigureAwait(true);
                SynchronizeTimers();
                await AwaitPropagatedChildClosuresAsync().ConfigureAwait(true);
                AbandonAttemptOwnedPhysicalWork();
                return CurrentResult(lastDisposition);
            }
            if (control.Mode == ProcessControlMode.Cancelling
                && state.CancellationFinalization?.Phase
                    == ProcessCancellationFinalizationPhase.WaitingForPropagatedChildren)
            {
                await SynchronizeChildLifecycleAsync().ConfigureAwait(true);
                childCancellationClosures = await AwaitPropagatedChildClosuresAsync().ConfigureAwait(true);
                cause = ProcessActivationCause.Control;
                observedAtUtc = RequireUtc(getCurrentUtc());
            }
            else if (control.Mode == ProcessControlMode.Cancelling
                     && state.CancellationFinalization?.Phase
                        == ProcessCancellationFinalizationPhase.FinalizerActive
                     && inputs.IsEmpty
                     && pendingOperations.Count != 0)
            {
                var finalizerStimulus = await WaitForNextStimulusAsync().ConfigureAwait(true);
                if (!finalizerStimulus.Available)
                {
                    return CurrentResult(lastDisposition);
                }
                Apply(finalizerStimulus);
                observedAtUtc = RequireUtc(getCurrentUtc());
            }
            QueueUnconsumedControlSignals();
            if (control.Mode == ProcessControlMode.Paused)
            {
                observe?.Invoke(CurrentResult(lastDisposition));
                await WaitForControlCommandAsync().ConfigureAwait(true);
                continue;
            }

            if (inputs.IsEmpty
                && cancellation is null
                && pendingInteraction?.IsCompleted == true)
            {
                inputs = [await pendingInteraction.ConfigureAwait(true)];
                pendingInteraction = null;
                if (cause != ProcessActivationCause.Start)
                {
                    cause = ProcessActivationCause.Interaction;
                    observedAtUtc = RequireUtc(getCurrentUtc());
                }
            }
            var activation = new ProcessActivation(
                DurableTaskSequentialProcessIdentities.Activation(state),
                cause,
                observedAtUtc,
                start.ActivationContext,
                inputs,
                cancellation,
                admissionOperatingPoints: default,
                childCancellationClosures: childCancellationClosures);
            cancellation = null;
            childCancellationClosures = [];
            var beforeActivation = state;
            latestControlDecision = controlExecutor.BeginActivation(
                control,
                new(Expectation(control), activation.Id, activation.ObservedAtUtc));
            if (latestControlDecision.Disposition != ProcessControlDecisionDisposition.ActivationStarted)
            {
                throw new InvalidOperationException(
                    "Canonical lifecycle control rejected a Durable Task activation boundary: "
                    + FormatControlDiagnostics(latestControlDecision));
            }
            control = latestControlDecision.State;
            if (waitForControl is not null || waitForControlRequest is not null)
            {
                observe?.Invoke(CurrentResult(lastDisposition));
            }
            ProcessActivationDecision decision;
            try
            {
                decision = await ActivateAsync(
                        plan,
                        state,
                        activation,
                        executeOperation,
                        resolveSignalTarget,
                        AwaitHostWorkWithControlAsync)
                    .ConfigureAwait(true);
            }
            catch (ProcessTerminatedDuringActivationException)
            {
                AbandonAttemptOwnedPhysicalWork();
                return CurrentResult(lastDisposition);
            }

            await DrainCompletedControlCommandsAsync().ConfigureAwait(true);
            if (control.Mode == ProcessControlMode.Terminated)
            {
                AbandonAttemptOwnedPhysicalWork();
                return CurrentResult(lastDisposition);
            }

            var trace = ProjectTrace(decision);
            state = decision.State;
            lastDisposition = decision.Disposition;
            emissions.AddRange(decision.Emissions);
            inputAdmissions.AddRange(decision.InputAdmissions);
            diagnostics.AddRange(decision.Diagnostics);
            evidence.Add(decision.Evidence);
            traces.Add(trace);
            await DispatchImmediateEmissionsAsync(decision.Emissions).ConfigureAwait(true);

            var safePointNode = ResolveSafePointNode(plan, decision);
            latestControlDecision = controlExecutor.ReachSafePoint(
                control,
                new(
                    DurableTaskSequentialProcessIdentities.SafePoint(
                        beforeActivation,
                        activation.Id,
                        safePointNode),
                    Expectation(control),
                    activation.Id,
                    safePointNode,
                    RequireUtc(getCurrentUtc())),
                cancellationPolicy);
            if (latestControlDecision.Disposition != ProcessControlDecisionDisposition.SafePointReached)
            {
                throw new InvalidOperationException(
                    "Canonical lifecycle control rejected a Durable Task safe point: "
                    + FormatControlDiagnostics(latestControlDecision));
            }
            control = latestControlDecision.State;
            CompleteCancellationControlIfTerminal();
            await RealizeControlIntentAsync(latestControlDecision.Intent).ConfigureAwait(true);
            var result = new DurableTaskSequentialProcessResult(
                decision.Disposition,
                state,
                control,
                latestControlDecision,
                [.. emissions],
                [.. inputAdmissions],
                [.. diagnostics],
                [.. evidence],
                [.. durableOperations.Values.OrderBy(
                    static operation => operation.State.OperationId.Value,
                    StringComparer.Ordinal)],
                [.. traces],
                [.. domainEventPublications]);
            await SynchronizeChildLifecycleAsync().ConfigureAwait(true);
            SynchronizeTimers();
            result = CurrentResult(decision.Disposition);
            observe?.Invoke(result);

            if (control.Mode is ProcessControlMode.Cancelled or ProcessControlMode.CancellationFailed)
            {
                await AwaitPropagatedChildClosuresAsync().ConfigureAwait(true);
                AbandonAttemptOwnedPhysicalWork();
                return CurrentResult(lastDisposition);
            }
            if (control.Mode == ProcessControlMode.Paused
                || state.Continuation.ProcessAttemptId != beforeActivation.Continuation.ProcessAttemptId)
            {
                continue;
            }

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
                        && state.OutstandingRequests.IsEmpty
                        && !HasExternalInteractionSource()
                        && pendingControl is null)
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
                        case AwaitMatchProcessNode:
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
                        case CancellationFinalizerProcessNode:
                            ScheduleDurableRequests(decision.Emissions);

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
                            if (pendingTimers.Count == 0
                                && pendingInteraction?.IsCompleted != true
                                && continueAsNew is not null)
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
            control,
            latestControlDecision,
            [.. emissions],
            [.. inputAdmissions],
            [.. diagnostics],
            [.. evidence],
            [.. durableOperations.Values.OrderBy(
                static operation => operation.State.OperationId.Value,
                StringComparer.Ordinal)],
            [.. traces],
            [.. domainEventPublications]);

        void CompleteCancellationControlIfTerminal()
        {
            if (control.Mode != ProcessControlMode.Cancelling
                || state.Terminal.Kind is not (
                    ExecutionTerminalOutcomeKind.Cancelled or ExecutionTerminalOutcomeKind.Failed))
            {
                return;
            }

            var intent = state.CancellationFinalization?.Intent
                ?? throw new InvalidOperationException(
                    "A terminal authored cancellation continuation retained no causal cancellation intent.");
            latestControlDecision = controlExecutor.CompleteCancellationFinalization(
                control,
                new(intent, state.Terminal.Kind, RequireUtc(getCurrentUtc())));
            if (latestControlDecision.Disposition
                != ProcessControlDecisionDisposition.CancellationFinalized)
            {
                throw new InvalidOperationException(
                    "Canonical lifecycle control rejected terminal authored cancellation evidence: "
                    + FormatControlDiagnostics(latestControlDecision));
            }
            control = latestControlDecision.State;
        }

        async Task DispatchImmediateEmissionsAsync(ImmutableArray<InteractionEnvelope> immediate)
        {
            foreach (var envelope in immediate)
            {
                switch (envelope)
                {
                    case DomainEventEnvelope domainEvent:
                        if (domainEventPublicationsByEmission.TryGetValue(
                                domainEvent.Context.EmissionId,
                                out var retained))
                        {
                            if (retained.DeduplicationKey != DomainEventPublicationDeduplicationKey.From(domainEvent)
                                || retained.ContentFingerprint
                                    != InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(domainEvent))
                            {
                                throw new InvalidOperationException(
                                    $"Domain event '{domainEvent.Context.EmissionId.Value}' conflicts with retained publication evidence.");
                            }
                            break;
                        }

                        var publisher = publishDomainEvent
                            ?? throw new InvalidOperationException(
                                "A canonical host operation emitted a domain event without a Durable Task publication projection.");
                        var invocation = DomainEventPublicationInvocation.From(domainEvent);
                        var publication = await publisher(invocation).ConfigureAwait(true)
                            ?? throw new InvalidOperationException(
                                "The Durable Task domain-event publication delegate returned null evidence.");
                        if (publication.EmissionId != domainEvent.Context.EmissionId
                            || publication.DeduplicationKey != invocation.DeduplicationKey
                            || publication.ContentFingerprint
                                != InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(domainEvent))
                        {
                            throw new InvalidOperationException(
                                $"Domain-event publication evidence does not match emission "
                                + $"'{domainEvent.Context.EmissionId.Value}'.");
                        }
                        domainEventPublications.Add(publication);
                        domainEventPublicationsByEmission.Add(publication.EmissionId, publication);
                        break;

                    case SignalEnvelope signal:
                        var dispatcher = deliverSignal
                            ?? throw new InvalidOperationException(
                                "A canonical host operation emitted a Signal without a Durable Task delivery projection.");
                        await dispatcher(signal).ConfigureAwait(true);
                        break;
                }
            }
        }

        void ScheduleDurableRequests(IEnumerable<InteractionEnvelope> scheduledEmissions)
        {
            foreach (var request in scheduledEmissions
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
        }

        async Task DrainCompletedControlCommandsAsync()
        {
            while (pendingControl?.IsCompleted == true)
            {
                var command = await pendingControl.ConfigureAwait(true);
                pendingControl = BeginControlWait();
                await ApplyControlCommandAsync(command).ConfigureAwait(true);
                if (control.IsTerminal)
                {
                    return;
                }
            }
        }

        async Task WaitForControlCommandAsync()
        {
            if (pendingControl is null)
            {
                throw new InvalidOperationException(
                    "A paused Durable Task Process has no canonical lifecycle-control source.");
            }

            var command = await pendingControl.ConfigureAwait(true);
            pendingControl = BeginControlWait();
            await ApplyControlCommandAsync(command).ConfigureAwait(true);
        }

        async Task ApplyControlCommandAsync(PendingProcessControl pending)
        {
            ArgumentNullException.ThrowIfNull(pending);
            var command = pending.Admission is null
                ? pending.Command
                : ExecutionProcessControlCommandAdmission.Rebind(
                    pending.Admission.Request,
                    pending.Admission.Invocation,
                    control);
            ArgumentNullException.ThrowIfNull(command);
            if (command is RestartProcessAttemptCommand
                && plan.Definition.RecoveryPolicy != ProcessRecoveryPolicy.RestartAttempt)
            {
                throw new InvalidOperationException(
                    "The Durable Task lifecycle target cannot admit RestartAttempt for a Process definition "
                    + "whose canonical recovery policy does not permit replacement attempts.");
            }
            if (command is RestartProcessAttemptCommand { Plan.Cleanup: not ProcessAttemptCleanupRequirement.RetainEvidence }
                || command is TerminateProcessCommand { Cleanup: not ProcessAttemptCleanupRequirement.RetainEvidence })
            {
                throw new InvalidOperationException(
                    "The Durable Task lifecycle target currently supports only RetainEvidence cleanup; "
                    + "attempt-resource or affinity cleanup must fail before canonical command admission.");
            }
            observedAtUtc = RequireUtc(getCurrentUtc());
            latestControlDecision = controlExecutor.Apply(
                control,
                command,
                observedAtUtc,
                cancellationPolicy);
            control = latestControlDecision.State;
            if (pending.ResponseIdentity is not null)
            {
                var status = ExecutionStatusProjector.Project(latestControlDecision.State);
                await retainControlResponse!(
                        pending.ResponseIdentity,
                        DurableTaskProcessControlResponse.FromDecision(latestControlDecision, status))
                    .ConfigureAwait(true);
            }
            await RealizeControlIntentAsync(latestControlDecision.Intent).ConfigureAwait(true);
            observe?.Invoke(CurrentResult(lastDisposition));
        }

        async Task AwaitHostWorkWithControlAsync(Task hostWork)
        {
            ArgumentNullException.ThrowIfNull(hostWork);
            while (!hostWork.IsCompleted)
            {
                if (pendingControl is null)
                {
                    await hostWork.ConfigureAwait(true);
                    return;
                }

                var completed = await Task.WhenAny(hostWork, pendingControl).ConfigureAwait(true);
                if (ReferenceEquals(completed, hostWork))
                {
                    await hostWork.ConfigureAwait(true);
                    return;
                }

                var command = await pendingControl.ConfigureAwait(true);
                pendingControl = BeginControlWait();
                await ApplyControlCommandAsync(command).ConfigureAwait(true);
                if (control.Mode == ProcessControlMode.Terminated)
                {
                    ObserveAbandoned(hostWork);
                    throw new ProcessTerminatedDuringActivationException();
                }
            }

            await hostWork.ConfigureAwait(true);
        }

        async Task RealizeControlIntentAsync(ProcessControlIntent? intent)
        {
            switch (intent)
            {
                case null:
                case ProcessReachSafePointIntent:
                case ProcessSignalAdmissionIntent:
                    return;

                case ProcessAttemptRestartIntent restart:
                    if (plan.Definition.RecoveryPolicy != ProcessRecoveryPolicy.RestartAttempt)
                    {
                        throw new InvalidOperationException(
                            "Canonical RestartAttempt control requires a Process definition with RestartAttempt recovery policy.");
                    }
                    if (restart.ProcessInstanceId != state.Continuation.ProcessInstanceId
                        || restart.AbandonedAttemptId != state.Continuation.ProcessAttemptId
                        || restart.ReplacementAttemptId != control.CurrentAttempt.AttemptId)
                    {
                        throw new InvalidOperationException(
                            "Canonical RestartAttempt intent does not match the active Durable Task continuation lineage.");
                    }

                    var childClosures = PrepareAttemptRestartChildClosures(restart);
                    AbandonAttemptOwnedPhysicalWork();
                    state = ProcessReferenceInterpreter.RestartAttempt(
                        plan,
                        state,
                        restart.ReplacementAttemptId);
                    inputs = [];
                    cancellation = null;
                    cause = ProcessActivationCause.Control;
                    observedAtUtc = RequireUtc(getCurrentUtc());
                    await CloseAbandonedAttemptChildrenAsync(childClosures).ConfigureAwait(true);
                    return;

                case ProcessCancellationIntent cancel:
                    if (cancel.AttemptId != state.Continuation.ProcessAttemptId)
                    {
                        throw new InvalidOperationException(
                            "Canonical cancellation intent does not match the active Durable Task continuation attempt.");
                    }

                    var cancellationActivation = new ProcessActivation(
                        DurableTaskSequentialProcessIdentities.CancellationActivation(
                            state,
                            cancel.CommandId),
                        ProcessActivationCause.Control,
                        RequireUtc(getCurrentUtc()),
                        start.ActivationContext,
                        inputs,
                        cancel);
                    inputs = [];
                    var beforeCancellation = state;
                    if (cancellationPolicy == ProcessCancellationCompletionPolicy.AuthoredFinalization)
                    {
                        latestControlDecision = controlExecutor.BeginActivation(
                            control,
                            new(
                                Expectation(control),
                                cancellationActivation.Id,
                                cancellationActivation.ObservedAtUtc));
                        if (latestControlDecision.Disposition
                            != ProcessControlDecisionDisposition.ActivationStarted)
                        {
                            throw new InvalidOperationException(
                                "Canonical lifecycle control rejected the authored cancellation activation: "
                                + FormatControlDiagnostics(latestControlDecision));
                        }
                        control = latestControlDecision.State;
                    }
                    var cancellationDecision = await ActivateAsync(
                            plan,
                            state,
                            cancellationActivation,
                            executeOperation,
                            resolveSignalTarget)
                        .ConfigureAwait(true);
                    if (cancellationDecision.Disposition == ProcessActivationDisposition.Rejected
                        || cancellationPolicy == ProcessCancellationCompletionPolicy.Immediate
                            && cancellationDecision.Disposition != ProcessActivationDisposition.Cancelled)
                    {
                        throw new InvalidOperationException(
                            "Canonical cooperative cancellation rejected its retained cancellation intent.");
                    }
                    var cancellationTrace = ProjectTrace(cancellationDecision);
                    state = cancellationDecision.State;
                    lastDisposition = cancellationDecision.Disposition;
                    emissions.AddRange(cancellationDecision.Emissions);
                    inputAdmissions.AddRange(cancellationDecision.InputAdmissions);
                    diagnostics.AddRange(cancellationDecision.Diagnostics);
                    evidence.Add(cancellationDecision.Evidence);
                    traces.Add(cancellationTrace);
                    await DispatchImmediateEmissionsAsync(cancellationDecision.Emissions).ConfigureAwait(true);
                    ScheduleDurableRequests(cancellationDecision.Emissions);
                    if (cancellationPolicy == ProcessCancellationCompletionPolicy.Immediate)
                    {
                        return;
                    }
                    var cancellationSafePointNode = ResolveSafePointNode(plan, cancellationDecision);
                    latestControlDecision = controlExecutor.ReachSafePoint(
                        control,
                        new(
                            DurableTaskSequentialProcessIdentities.SafePoint(
                                beforeCancellation,
                                cancellationActivation.Id,
                                cancellationSafePointNode),
                            Expectation(control),
                            cancellationActivation.Id,
                            cancellationSafePointNode,
                            RequireUtc(getCurrentUtc())),
                        cancellationPolicy);
                    if (latestControlDecision.Disposition
                        != ProcessControlDecisionDisposition.SafePointReached)
                    {
                        throw new InvalidOperationException(
                            "Canonical lifecycle control rejected the authored cancellation safe point: "
                            + FormatControlDiagnostics(latestControlDecision));
                    }
                    control = latestControlDecision.State;
                    CompleteCancellationControlIfTerminal();
                    cause = ProcessActivationCause.Control;
                    observedAtUtc = RequireUtc(getCurrentUtc());
                    return;

                case ProcessTerminationIntent termination:
                    if (termination.AttemptId != state.Continuation.ProcessAttemptId)
                    {
                        throw new InvalidOperationException(
                            "Canonical termination intent does not match the active Durable Task continuation attempt.");
                    }
                    AbandonAttemptOwnedPhysicalWork();
                    return;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(intent),
                        intent.GetType().Name,
                        "Unsupported canonical lifecycle-control intent.");
            }
        }

        void QueueUnconsumedControlSignals()
        {
            if (control.Mode != ProcessControlMode.Running)
            {
                return;
            }

            var alreadyPresented = state.InputReceipts
                .Select(static receipt => receipt.Emission)
                .ToHashSet();
            var alreadyQueued = inputs
                .Select(static input => input.Envelope.Context.EmissionId)
                .ToHashSet();
            foreach (var admission in control.SignalAdmissions)
            {
                if (alreadyPresented.Contains(admission.Signal.Context.EmissionId)
                    || !alreadyQueued.Add(admission.Signal.Context.EmissionId)
                    || admission.Signal.Target is not ProcessTokenInteractionTarget target
                    || target.Continuation != state.Continuation)
                {
                    continue;
                }
                inputs = inputs.Add(new(target, admission.Signal));
            }
            if (!inputs.IsEmpty && cause != ProcessActivationCause.Start)
            {
                cause = ProcessActivationCause.Interaction;
                observedAtUtc = RequireUtc(getCurrentUtc());
            }
        }

        void AbandonAttemptOwnedPhysicalWork()
        {
            foreach (var operation in pendingOperations.Values)
            {
                ObserveAbandoned(operation.Execution);
            }
            pendingOperations.Clear();
            foreach (var timer in pendingTimers.Values)
            {
                timer.Cancellation.Cancel();
                timer.Cancellation.Dispose();
                ObserveAbandoned(timer.Execution);
            }
            pendingTimers.Clear();
        }

        ImmutableArray<AttemptRestartChildClosure> PrepareAttemptRestartChildClosures(
            ProcessAttemptRestartIntent restart)
        {
            var intents = ProcessChildCancellationIntents.ProjectAttemptRestart(state, restart);
            if (intents.IsEmpty)
            {
                return [];
            }

            var closures = ImmutableArray.CreateBuilder<AttemptRestartChildClosure>(intents.Length);
            foreach (var intent in intents)
            {
                if (pendingOperations.Remove(intent.RequestEmission, out var pending))
                {
                    closures.Add(new(intent, pending));
                    continue;
                }
                if (durableOperations.TryGetValue(intent.RequestEmission, out var completed))
                {
                    durableOperations[intent.RequestEmission] = FenceAbandonedChildResult(completed);
                    continue;
                }
                throw new InvalidOperationException(
                    $"RestartAttempt cannot close child '{intent.ChildRegistrationId}' because its exact "
                    + $"Request '{intent.RequestEmission.Value}' has no retained Durable Task execution evidence.");
            }
            if (closures.Any(static closure => !closure.Operation.Execution.IsCompleted)
                && dispatchChildCancellation is null)
            {
                throw new InvalidOperationException(
                    "RestartAttempt requires propagated child closure, but this Durable Task host has no "
                    + "child-control dispatcher.");
            }
            return closures.Count == closures.Capacity ? closures.MoveToImmutable() : closures.ToImmutable();
        }

        async Task CloseAbandonedAttemptChildrenAsync(
            ImmutableArray<AttemptRestartChildClosure> closures)
        {
            foreach (var closure in closures)
            {
                if (!closure.Operation.Execution.IsCompleted)
                {
                    await DispatchChildCancellationAsync(closure.Intent).ConfigureAwait(true);
                }
            }

            foreach (var closure in closures)
            {
                var operation = FenceAbandonedChildResult(
                    await closure.Operation.Execution.ConfigureAwait(true));
                durableOperations[operation.State.OperationId] = operation;
            }
        }

        async Task<NextProcessStimulus> WaitForNextStimulusAsync()
        {
            while (true)
            {
                if (HasExternalInteractionSource() && pendingInteraction is null)
                {
                    pendingInteraction = BeginInteractionWait();
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
                        .OrderBy(static pair => pair.Key.Wait.Value, StringComparer.Ordinal)
                        .ThenBy(static pair => pair.Key.Clause.Value, StringComparer.Ordinal)
                        .Select(static pair => pair.Value.Execution)
                ];
                if (pendingControl is not null)
                {
                    candidates.Insert(0, pendingControl);
                }
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
                if (ReferenceEquals(completed, pendingControl))
                {
                    var command = await pendingControl!.ConfigureAwait(true);
                    pendingControl = BeginControlWait();
                    var priorMode = control.Mode;
                    var priorAttempt = control.CurrentAttempt.AttemptId;
                    var priorInputs = inputs;
                    await ApplyControlCommandAsync(command).ConfigureAwait(true);
                    if (control.Mode != priorMode
                        || control.CurrentAttempt.AttemptId != priorAttempt
                        || !inputs.SequenceEqual(priorInputs))
                    {
                        return NextProcessStimulus.ForControl();
                    }
                    continue;
                }
                var completedTimer = pendingTimers.Any(candidate =>
                    ReferenceEquals(candidate.Value.Execution, completed));
                if (ReferenceEquals(completed, pendingInteraction) || completedTimer)
                {
                    ProcessActivationInput? input = null;
                    if (pendingInteraction?.IsCompleted == true)
                    {
                        input = await pendingInteraction.ConfigureAwait(true);
                        pendingInteraction = null;
                    }

                    foreach (var timer in pendingTimers
                                 .Where(static candidate => candidate.Value.Execution.IsCompleted)
                                 .OrderBy(static candidate => candidate.Key.Wait.Value, StringComparer.Ordinal)
                                 .ThenBy(static candidate => candidate.Key.Clause.Value, StringComparer.Ordinal)
                                 .ToArray())
                    {
                        await timer.Value.Execution.ConfigureAwait(true);
                        pendingTimers.Remove(timer.Key);
                        timer.Value.Cancellation.Dispose();
                    }
                    return input is null
                        ? NextProcessStimulus.ForTimer()
                        : NextProcessStimulus.For(input);
                }
                if (ReferenceEquals(completed, pendingChildCancellation))
                {
                    var intent = await pendingChildCancellation!.ConfigureAwait(true);
                    pendingChildCancellation = null;
                    await ApplyControlCommandAsync(
                        PendingProcessControl.FromCommand(ToCancellationCommand(intent))).ConfigureAwait(true);
                    return NextProcessStimulus.ForControl();
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
                await DispatchChildCancellationAsync(intent).ConfigureAwait(true);
            }
        }

        async Task DispatchChildCancellationAsync(ProcessChildCancellationIntent intent)
        {
            if (!dispatchedChildCancellations.Add(intent.IntentId))
            {
                return;
            }
            if (dispatchChildCancellation is null)
            {
                throw new InvalidOperationException(
                    "The Process requested propagated child cancellation, but this Durable Task host has no child-control dispatcher.");
            }
            await dispatchChildCancellation(intent).ConfigureAwait(true);
        }

        void SynchronizeTimers()
        {
            Dictionary<(ProcessWaitRegistrationId Wait, ExecutionNodeId Clause), ProcessTimerState> active = [];
            foreach (var wait in state.Waits.Where(static wait =>
                         wait.Active && wait.Kind is ProcessWaitKind.Timer or ProcessWaitKind.AwaitMatch))
            {
                foreach (var timer in wait.Timers)
                {
                    var key = (wait.RegistrationId, timer.Clause);
                    if (!active.TryAdd(key, timer))
                    {
                        throw new InvalidOperationException(
                            $"Canonical wait '{wait.RegistrationId.Value}' repeats timer clause '{timer.Clause.Value}'.");
                    }
                }
            }
            foreach (var obsolete in pendingTimers.Keys
                         .Where(key => !active.ContainsKey(key))
                         .OrderBy(static key => key.Wait.Value, StringComparer.Ordinal)
                         .ThenBy(static key => key.Clause.Value, StringComparer.Ordinal)
                         .ToArray())
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
            foreach (var candidate in active
                         .OrderBy(static candidate => candidate.Key.Wait.Value, StringComparer.Ordinal)
                         .ThenBy(static candidate => candidate.Key.Clause.Value, StringComparer.Ordinal))
            {
                if (pendingTimers.ContainsKey(candidate.Key))
                {
                    continue;
                }
                var timer = candidate.Value;
                var delay = timer.DueAtUtc - currentUtc;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }
                var cancellationSource = new CancellationTokenSource();
                var execution = createTimer(delay, cancellationSource.Token)
                    ?? throw new InvalidOperationException("The Durable Task timer delegate returned null.");
                pendingTimers.Add(
                    candidate.Key,
                    new(cancellationSource, execution));
            }
        }

        bool HasExternalInteractionSource()
        {
            if (state.OutstandingRequests.Any(request => !pendingOperations.ContainsKey(request.Emission)))
            {
                return true;
            }

            return state.Waits.Any(wait =>
                wait.Active
                && wait.Kind == ProcessWaitKind.AwaitMatch
                && plan.GetNode(wait.Node) is AwaitMatchProcessNode awaitMatch
                && awaitMatch.Clauses.Any(static clause => clause is ProcessAwaitInteractionClause));
        }

        Task<ProcessActivationInput> BeginInteractionWait() =>
            waitForInteraction()
            ?? throw new InvalidOperationException("The Durable Task interaction-wait delegate returned null.");

        Task<PendingProcessControl> BeginControlWait()
        {
            if (waitForControlRequest is not null)
                return AwaitAdmissionAsync();
            var command = waitForControl?.Invoke()
                ?? throw new InvalidOperationException("The Durable Task control-wait delegate returned null.");
            return AwaitCommandAsync(command);

            static async Task<PendingProcessControl> AwaitCommandAsync(Task<ProcessControlCommand> pending) =>
                PendingProcessControl.FromCommand(await pending.ConfigureAwait(true));

            async Task<PendingProcessControl> AwaitAdmissionAsync()
            {
                var request = await (waitForControlRequest.Invoke()
                    ?? throw new InvalidOperationException(
                        "The Durable Task control-request wait delegate returned null.")).ConfigureAwait(true);
                return PendingProcessControl.FromAdmission(request);
            }
        }

        async Task<ImmutableArray<ProcessChildCancellationClosure>> AwaitPropagatedChildClosuresAsync()
        {
            var intents = ProcessChildCancellationIntents.Project(state);
            if (intents.IsEmpty)
            {
                return [];
            }

            var closures = ImmutableArray.CreateBuilder<ProcessChildCancellationClosure>(intents.Length);
            foreach (var intent in intents)
            {
                if (!pendingOperations.Remove(intent.RequestEmission, out var pending))
                {
                    throw new InvalidOperationException(
                        $"Cancellation-requested child '{intent.ChildRegistrationId}' has no retained "
                        + $"Durable Task execution for Request '{intent.RequestEmission.Value}'.");
                }
                var operation = await pending.Execution.ConfigureAwait(true);
                durableOperations[operation.State.OperationId] = operation;
                var acknowledgement = operation.State.Acknowledgement
                    ?? throw new InvalidOperationException(
                        $"Cancellation-requested child '{intent.ChildRegistrationId}' returned no terminal "
                        + "durable-operation acknowledgement.");
                var target = pending.Request.ChildTarget
                    ?? throw new InvalidOperationException(
                        "A propagated child-cancellation operation retained no exact child target.");
                if (target.Definition != intent.ChildDefinition
                    || target.Continuation != intent.ChildContinuation)
                {
                    throw new InvalidOperationException(
                        "A propagated child-cancellation result does not match its retained exact child target.");
                }
                closures.Add(new(
                    intent.IntentId,
                    intent.ChildContinuation,
                    TerminalFor(target.OutcomeMapping, acknowledgement.Outcome.Id),
                    RequireUtc(getCurrentUtc())));
            }
            return closures.MoveToImmutable();
        }

        CancelProcessCommand ToCancellationCommand(ProcessChildCancellationIntent intent)
        {
            if (intent.ChildDefinition != state.Definition
                || intent.ChildContinuation != state.Continuation)
            {
                throw new InvalidOperationException(
                    "A propagated child cancellation named another definition or continuation.");
            }
            return new(
                ProcessControlCommand.CurrentSchemaVersion,
                new(
                    new(intent.IntentId),
                    new(intent.IntentId),
                    state.Continuation.ProcessInstanceId,
                    start.Receipt.Request.Context.Authorization,
                    RequireUtc(getCurrentUtc()),
                    start.Receipt.Request.Context.Provenance),
                Expectation(control),
                new("parent.child-cancellation"));
        }

        void Apply(NextProcessStimulus stimulus)
        {
            if (stimulus.ControlApplied)
            {
                return;
            }
            if (stimulus.Input is not null)
            {
                inputs = [stimulus.Input];
                cancellation = null;
                cause = ProcessActivationCause.Interaction;
                return;
            }
            if (stimulus.TimerElapsed)
            {
                inputs = [];
                cancellation = null;
                cause = ProcessActivationCause.Timer;
                return;
            }
            inputs = [];
            cancellation = stimulus.Cancellation
                ?? throw new InvalidOperationException("An available Process stimulus carried no input or cancellation.");
            cause = ProcessActivationCause.Control;
        }
    }

    static ProcessControlExpectation Expectation(ProcessControlState state) => new(
        new(state.ProcessInstanceId, state.CurrentAttempt.AttemptId),
        state.Revision);

    static ExecutionTerminalOutcomeKind TerminalFor(
        ProcessChildOutcomeMapping mapping,
        RequestTerminalOutcomeId outcome) =>
        outcome == mapping.Completed ? ExecutionTerminalOutcomeKind.Completed
        : outcome == mapping.Failed ? ExecutionTerminalOutcomeKind.Failed
        : outcome == mapping.Cancelled ? ExecutionTerminalOutcomeKind.Cancelled
        : outcome == mapping.Terminated ? ExecutionTerminalOutcomeKind.Terminated
        : throw new InvalidOperationException(
            $"Child Request outcome '{outcome.Value}' is absent from its exact terminal mapping.");

    internal static DurableTaskDurableOperationResult FenceAbandonedChildResult(
        DurableTaskDurableOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Disposition == DurableTaskDurableOperationDisposition.ReplyReady
            ? new(
                DurableTaskDurableOperationDisposition.SupersededByAttemptRestart,
                result.State)
            : result;
    }

    static ProcessControlReferenceExecutor CreateControlExecutor(CompiledProcessPlan plan)
    {
        var contracts = plan.ValidationContext.InteractionContracts;
        if (contracts is null)
        {
            var validation = InteractionContractCatalog.TryCreate([], out contracts);
            if (!validation.IsValid || contracts is null)
            {
                throw new InvalidOperationException("The empty canonical interaction catalog could not be constructed.");
            }
        }
        return new(contracts);
    }

    static ExecutionNodeId ResolveSafePointNode(
        CompiledProcessPlan plan,
        ProcessActivationDecision decision) =>
        decision.Evidence.SafePointNode
        ?? (decision.Evidence.Trace.IsEmpty
            ? plan.Definition.Entry
            : decision.Evidence.Trace[^1].Node);

    static string FormatControlDiagnostics(ProcessControlDecision decision) =>
        decision.Diagnostics.IsEmpty
            ? decision.Disposition.ToString()
            : string.Join("; ", decision.Diagnostics.Select(static diagnostic => diagnostic.Message));

    static NormalizedExecutionTrace ProjectTrace(ProcessActivationDecision decision) =>
        RequireTrace(ProcessExecutionTraceProjector.Project(decision));

    internal static NormalizedExecutionTrace RequireTrace(ExecutionTraceProjectionResult projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Trace is { } trace)
        {
            return trace;
        }

        var diagnostics = string.Join(
            "; ",
            projection.Validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));
        throw new InvalidOperationException(
            "The Durable Task Process interpreter could not retain the canonical normalized activation trace: "
            + diagnostics);
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
        Func<DurableTaskProcessHostOperation, Task<ProcessOperationResult>> executeOperation,
        Func<ProcessSignalTargetResolution, Task<ProcessSignalTargetResult>>? resolveSignalTarget,
        Func<Task, Task>? awaitHostWork = null)
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
                var execution = executeOperation(pending.Operation)
                    ?? throw new InvalidOperationException("A Durable Task host-operation activity returned null.");
                if (awaitHostWork is not null)
                {
                    await awaitHostWork(execution).ConfigureAwait(true);
                }
                var result = await execution.ConfigureAwait(true);
                host.Materialize(pending.Operation, result);
            }
            catch (PendingSignalTargetResolutionException pending)
            {
                var resolver = resolveSignalTarget
                    ?? throw new InvalidOperationException(
                        "The Process reached a Signal node without a Durable Task target-resolution activity.");
                var execution = resolver(pending.Resolution)
                    ?? throw new InvalidOperationException(
                        "A Durable Task Signal-target activity returned null evidence.");
                if (awaitHostWork is not null)
                {
                    await awaitHostWork(execution).ConfigureAwait(true);
                }
                var result = await execution.ConfigureAwait(true);
                host.Materialize(pending.Resolution, result);
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

    sealed class PendingSignalTargetResolutionException(ProcessSignalTargetResolution resolution) : Exception
    {
        internal ProcessSignalTargetResolution Resolution { get; } = resolution;
    }

    sealed class ProcessTerminatedDuringActivationException : Exception
    {
    }

    sealed class SuspendingHost : IProcessReferenceHost
    {
        readonly Dictionary<OperationKey, MaterializedOperation> materialized = [];
        readonly Dictionary<OperationKey, MaterializedSignalTarget> signalTargets = [];

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            Resolve(DurableTaskProcessHostOperation.For(invocation));

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            Resolve(DurableTaskProcessHostOperation.For(evaluation));

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution)
        {
            var key = Key(resolution);
            if (!signalTargets.TryGetValue(key, out var retained))
            {
                throw new PendingSignalTargetResolutionException(resolution);
            }
            if (retained.Resolution != resolution)
            {
                throw new InvalidOperationException(
                    "One Process Signal-target occurrence produced inconsistent resolution evidence during replay.");
            }
            return retained.Result;
        }

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

        internal void Materialize(ProcessSignalTargetResolution resolution, ProcessSignalTargetResult result)
        {
            ArgumentNullException.ThrowIfNull(resolution);
            ArgumentNullException.ThrowIfNull(result);
            var key = Key(resolution);
            if (signalTargets.TryGetValue(key, out var retained))
            {
                if (retained.Resolution != resolution || retained.Result != result)
                {
                    throw new InvalidOperationException(
                        "One Process Signal-target occurrence was materialized with conflicting evidence.");
                }
                return;
            }
            signalTargets.Add(key, new(resolution, result));
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

        static OperationKey Key(ProcessSignalTargetResolution resolution) => new(
            resolution.Continuation,
            resolution.Activation,
            resolution.Token,
            resolution.Node,
            resolution.Occurrence);
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

    sealed record MaterializedSignalTarget(
        ProcessSignalTargetResolution Resolution,
        ProcessSignalTargetResult Result);

    sealed record PendingDurableOperation(
        RequestEnvelope Request,
        Task<DurableTaskDurableOperationResult> Execution);

    sealed record AttemptRestartChildClosure(
        ProcessChildCancellationIntent Intent,
        PendingDurableOperation Operation);

    sealed record PendingProcessTimer(
        CancellationTokenSource Cancellation,
        Task Execution);

    sealed record PendingProcessControl(
        ProcessControlCommand Command,
        DurableTaskProcessControlAdmission? Admission,
        string? ResponseIdentity)
    {
        internal static PendingProcessControl FromCommand(ProcessControlCommand command) =>
            new(command ?? throw new ArgumentNullException(nameof(command)), null, null);

        internal static PendingProcessControl FromAdmission(DurableTaskProcessControlRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return new(
                request.Admission.Request,
                request.Admission,
                request.ResponseIdentity);
        }
    }

    readonly record struct NextProcessStimulus(
        bool Available,
        ProcessActivationInput? Input,
        ProcessCancellationIntent? Cancellation,
        bool TimerElapsed,
        bool ControlApplied)
    {
        internal static NextProcessStimulus Unavailable => new(false, null, null, false, false);

        internal static NextProcessStimulus ForControl() => new(true, null, null, false, true);

        internal static NextProcessStimulus For(ProcessActivationInput input) =>
            new(true, input ?? throw new ArgumentNullException(nameof(input)), null, false, false);

        internal static NextProcessStimulus For(ProcessCancellationIntent cancellation) =>
            new(true, null, cancellation ?? throw new ArgumentNullException(nameof(cancellation)), false, false);

        internal static NextProcessStimulus ForTimer() => new(true, null, null, true, false);
    }
}
