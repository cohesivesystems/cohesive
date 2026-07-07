using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Executes activity-backed process nodes and the impure runtime behaviors they require.
/// </summary>
public sealed class ProcessNodeExecutor
{
    readonly record struct EffectDispatchOutcome(bool WasHandled, object? Result, TransitionResult? ContinuationTransition);

    readonly ProcessRuntimeServices services;
    readonly ProcessExecutionPlanner planner;
    readonly ILogger<ProcessNodeExecutor> logger;

    /// <summary>
    /// Creates a node executor over shared runtime services.
    /// </summary>
    public ProcessNodeExecutor(ProcessRuntimeServices services, ProcessExecutionPlanner? planner = null)
    {
        this.services = Guard.RequireNotNull(services);
        this.planner = planner ?? new ProcessExecutionPlanner(services);
        logger = services.LoggerFactory.CreateLogger<ProcessNodeExecutor>();
    }

    /// <summary>
    /// Executes a single activity-backed node from the supplied checkpoint.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    public async Task<ProcessCheckpoint> ExecuteNodeAsync(OperationContext context, ProcessDefinition process, ProcessCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(checkpoint);
        context.ThrowIfCancellationRequested();

        using var scope = services.PushOperationContext(context);

        ProcessExecutionPlanner.ValidateCheckpoint(process, checkpoint);

        var state = planner.RestoreRuntimeState(checkpoint);
        var currentNode = ProcessExecutionPlanner.NormalizeExecutionCursor(state, checkpoint.CurrentNode);
        if (string.IsNullOrWhiteSpace(currentNode))
        {
            var completedCheckpoint = planner.BuildCheckpoint(
                process: process,
                state: state,
                currentNode: null,
                status: ProcessExecutionStatus.Completed,
                updatedAtUtc: context.UtcNow,
                result: checkpoint.Result,
                completedAtUtc: checkpoint.CompletedAtUtc ?? context.UtcNow
                );

            logger.LogInformation(
                "Checkpoint execution for process '{ProcessName}' ({ProcessId}) was already complete.",
                process.Name,
                checkpoint.ProcessId
                );

            return completedCheckpoint;
        }

        logger.LogInformation(
            "Executing checkpoint node '{NodeName}' for process '{ProcessName}' ({ProcessId}) in place '{Place}'.",
            currentNode,
            process.Name,
            checkpoint.ProcessId,
            checkpoint.CurrentPlace
            );

        var node = process.GetNode(currentNode);
        if (node is not (RunEntityTransitionNode or ExecuteEntityTransitionNode or ExecuteEffectRequestNode or ExecuteEntityReadNode or ExecuteEntityCreateNode or ExecuteEntityQueryNode or ComputeValueNode or TransactionNode))
            throw new NotSupportedException($"Node '{currentNode}' must be executed by the durable orchestrator, not by '{nameof(ExecuteNodeAsync)}'.");

        try
        {
            var outcome = await ExecuteSingleNodeAsync(
                context: context,
                process: process,
                state: state,
                currentNode: currentNode,
                waitHandlingMode: ProcessWaitHandlingMode.Yield
                ).ConfigureAwait(false);

            if (outcome.IsWaiting)
                throw new InvalidOperationException($"Activity-backed node '{currentNode}' yielded an unexpected durable wait boundary.");

            var updatedCheckpoint = planner.BuildCheckpoint(
                process: process,
                state: state,
                currentNode: outcome.IsEnded ? null : outcome.NextNode,
                status: outcome.IsEnded ? ProcessExecutionStatus.Completed : ProcessExecutionStatus.Running,
                updatedAtUtc: context.UtcNow,
                result: outcome.IsEnded ? outcome.Result : checkpoint.Result,
                completedAtUtc: outcome.IsEnded ? context.UtcNow : checkpoint.CompletedAtUtc
                );

            logger.LogInformation(
                "Checkpoint node '{NodeName}' for process '{ProcessName}' ({ProcessId}) completed with status '{Status}' and next node '{NextNode}'.",
                currentNode,
                process.Name,
                checkpoint.ProcessId,
                updatedCheckpoint.Status,
                updatedCheckpoint.CurrentNode ?? "<none>"
                );

            return updatedCheckpoint;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Checkpoint node '{NodeName}' for process '{ProcessName}' ({ProcessId}) failed in place '{Place}'.",
                currentNode,
                process.Name,
                checkpoint.ProcessId,
                checkpoint.CurrentPlace
                );
            throw;
        }
    }

    internal async Task<ProcessRunResult> ExecuteToCompletionAsync(
        OperationContext context,
        ProcessDefinition process,
        IReadOnlyDictionary<string, object?>? parameters = null,
        ProcessRunOptions? runOptions = null
        )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(process);
        context.CancellationToken.ThrowIfCancellationRequested();

        using var scope = services.PushOperationContext(context);

        runOptions ??= new();

        var processId = string.IsNullOrWhiteSpace(runOptions.ProcessId)
            ? Guid.NewGuid().ToString("N")
            : runOptions.ProcessId;

        var initialPlace = string.IsNullOrWhiteSpace(runOptions.InitialPlace)
            ? services.Options.DefaultPlaceName
            : runOptions.InitialPlace;

        _ = services.ResolvePlace(initialPlace!);

        var executionContext = new ProcessExecutionContext(
            processId: processId!,
            processName: process.Name,
            parameters: parameters is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(parameters, StringComparer.Ordinal),
            currentPlace: initialPlace!
            );
        var state = new ProcessRuntimeState(executionContext)
        {
            StartedAtUtc = context.UtcNow
        };

        logger.LogInformation(
            "Starting process execution '{ProcessName}' ({ProcessId}) in place '{Place}'.",
            executionContext.ProcessName,
            executionContext.ProcessId,
            executionContext.CurrentPlace
            );

        try
        {
            await PersistCheckpointAsync(
                context,
                process: process,
                state: state,
                currentNode: process.EntryNode,
                isCompleted: false
                ).ConfigureAwait(false);

            var outcome = await ExecuteNodesAsync(
                context,
                process,
                state,
                startNode: process.EntryNode,
                waitHandlingMode: ProcessWaitHandlingMode.Block
                ).ConfigureAwait(false);

            if (outcome.IsWaiting)
                throw new InvalidOperationException($"Blocking process execution yielded unexpected wait boundary in process '{process.Name}'.");

            if (state.TransactionDepth == 0)
                await DrainAutoEffectsAsync(context, state).ConfigureAwait(false);

            await PersistCheckpointAsync(
                context,
                process: process,
                state: state,
                currentNode: null,
                isCompleted: true
                ).ConfigureAwait(false);

            logger.LogInformation(
                "Completed process execution '{ProcessName}' ({ProcessId}) in place '{Place}' with {TransitionCount} transitions, {EffectCount} executed effects, and {DeadLetterCount} dead letters.",
                executionContext.ProcessName,
                executionContext.ProcessId,
                executionContext.CurrentPlace,
                state.Transitions.Count,
                state.ExecutedEffects.Count,
                state.DeadLetters.Count
                );

            return new(
                ProcessId: executionContext.ProcessId,
                ProcessName: executionContext.ProcessName,
                Result: outcome.Result,
                FinalPlace: executionContext.CurrentPlace,
                Variables: executionContext.CloneVariables(),
                Transitions: [.. state.Transitions],
                ExecutedEffects: [.. state.ExecutedEffects],
                PendingEffects: [.. state.DeferredEffects, .. state.AutoEffects],
                DeadLetters: [.. state.DeadLetters]
                );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Process execution '{ProcessName}' ({ProcessId}) failed in place '{Place}'.",
                executionContext.ProcessName,
                executionContext.ProcessId,
                executionContext.CurrentPlace
                );
            throw;
        }
    }

    async Task<ProcessNodeExecutionOutcome> ExecuteNodesAsync(
        OperationContext context,
        ProcessDefinition process,
        ProcessRuntimeState state,
        string? startNode,
        ProcessWaitHandlingMode waitHandlingMode
        )
    {
        var currentNode = startNode;
        while (true)
        {
            while (!string.IsNullOrWhiteSpace(currentNode))
            {
                var outcome = await ExecuteSingleNodeAsync(
                    context: context,
                    process: process,
                    state: state,
                    currentNode: currentNode,
                    waitHandlingMode: waitHandlingMode
                    ).ConfigureAwait(false);

                if (outcome.IsEnded || outcome.IsWaiting)
                    return outcome;

                currentNode = outcome.NextNode;

                await PersistCheckpointAsync(
                    context,
                    process: process,
                    state: state,
                    currentNode: currentNode,
                    isCompleted: false
                    ).ConfigureAwait(false);
            }

            if (state.ContinuationFrames.Count == 0)
                return new(NextNode: null, IsEnded: false, Result: null, Wait: null);

            var continuation = state.ContinuationFrames[^1];
            state.ContinuationFrames.RemoveAt(state.ContinuationFrames.Count - 1);
            state.Context.CurrentPlace = continuation.ReturnPlace;
            currentNode = continuation.NextNode;

            await PersistCheckpointAsync(
                context,
                process: process,
                state: state,
                currentNode: currentNode,
                isCompleted: false
                ).ConfigureAwait(false);
        }
    }

    async Task<ProcessNodeExecutionOutcome> ExecuteSingleNodeAsync(
        OperationContext context,
        ProcessDefinition process,
        ProcessRuntimeState state,
        string currentNode,
        ProcessWaitHandlingMode waitHandlingMode
        )
    {
        context.ThrowIfCancellationRequested();

        var node = process.GetNode(currentNode);
        logger.LogDebug(
            "Executing process node '{NodeName}' ({NodeType}) for process '{ProcessName}' ({ProcessId}) in place '{Place}'.",
            node.Name,
            node.GetType().Name,
            state.Context.ProcessName,
            state.Context.ProcessId,
            state.Context.CurrentPlace
            );

        try
        {
            switch (node)
            {
                case RunEntityTransitionNode runTransition:
                {
                    try
                    {
                        var entityRef = runTransition.EntityRefExpression(state.Context);
                        var transitionInput = runTransition.InputExpression?.Invoke(state.Context);
                        var transitionResult = await ExecuteTransitionAsync(
                            context: context,
                            state: state,
                            entity: entityRef,
                            transitionName: runTransition.TransitionName,
                            input: transitionInput,
                            effectScheduling: runTransition.EffectScheduling
                            ).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(runTransition.ResultVariable))
                            state.Context.SetVariable(name: runTransition.ResultVariable, transitionResult);

                        return new(runTransition.NextNode, IsEnded: false, Result: null, Wait: null);
                    }
                    catch (TransitionPreconditionException preconditionFailure) when (!string.IsNullOrWhiteSpace(runTransition.OnPreconditionFailureNode))
                    {
                        state.Context.SetVariable(name: "__lastPreconditionFailure", preconditionFailure);
                        return new(runTransition.OnPreconditionFailureNode, IsEnded: false, Result: null, Wait: null);
                    }
                }

                case ExecuteEffectRequestNode executeRequest:
                {
                    var requestValue = executeRequest.RequestExpression(state.Context);
                    var invocation = ResolveRequestInvocation(
                        rawValue: requestValue,
                        defaultContinuationEntity: executeRequest.ContinuationEntityExpression?.Invoke(state.Context));

                    var execution = await ExecuteEffectRequestAsync(
                        context: context,
                        state: state,
                        request: invocation.Request,
                        continuationEntity: invocation.ContinuationEntity,
                        isExplicitExecution: true
                        ).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(executeRequest.ResultVariable))
                        state.Context.SetVariable(executeRequest.ResultVariable, execution.Result);

                    return new(executeRequest.NextNode, IsEnded: false, Result: null, Wait: null);
                }

                case ExecuteEntityReadNode executeRead:
                {
                    services.EnsureCapability(
                        capability: ProcessCapability.StateRead,
                        context: state.Context,
                        operation: $"execute entity read node '{executeRead.Name}'"
                        );

                    var rawRead = executeRead.ReadExpression(state.Context);
                    if (rawRead is not IProcessEntityReadInvocation readInvocation)
                    {
                        throw new SemanticRuleViolationException($"Entity read node '{executeRead.Name}' expects a '{nameof(IProcessEntityReadInvocation)}' but received '{rawRead?.GetType().FullName ?? "null"}'.");
                    }

                    var result = await readInvocation
                        .ExecuteAsync(context, services.EntityRepository)
                        .ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(executeRead.ResultVariable))
                        state.Context.SetVariable(executeRead.ResultVariable, result);

                    return new(executeRead.NextNode, IsEnded: false, Result: null, Wait: null);
                }

                case ExecuteEntityCreateNode executeCreate:
                {
                    services.EnsureCapability(
                        capability: ProcessCapability.StateMutation,
                        context: state.Context,
                        operation: $"execute entity create node '{executeCreate.Name}'"
                        );

                    var rawCreate = executeCreate.CreateExpression(state.Context);
                    if (rawCreate is not IProcessEntityCreateInvocation createInvocation)
                    {
                        throw new SemanticRuleViolationException($"Entity create node '{executeCreate.Name}' expects a '{nameof(IProcessEntityCreateInvocation)}' but received '{rawCreate?.GetType().FullName ?? "null"}'.");
                    }

                    var result = await createInvocation
                        .ExecuteAsync(context, services.EntityRepository, state.Context.ProcessId)
                        .ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(executeCreate.ResultVariable))
                        state.Context.SetVariable(executeCreate.ResultVariable, result);

                    return new(executeCreate.NextNode, IsEnded: false, Result: null, Wait: null);
                }

                case ExecuteEntityQueryNode executeQuery:
                {
                    services.EnsureCapability(
                        capability: ProcessCapability.StateRead,
                        context: state.Context,
                        operation: $"execute entity query node '{executeQuery.Name}'"
                        );

                    var rawQuery = executeQuery.QueryExpression(state.Context);
                    if (rawQuery is not IExecutableQuery queryInvocation)
                        throw new SemanticRuleViolationException($"Entity query node '{executeQuery.Name}' expects a '{nameof(IExecutableQuery)}' but received '{rawQuery?.GetType().FullName ?? "null"}'.");

                    var result = await queryInvocation.ExecuteAsync(context, services.RequireEntityReadRepositoryRegistry($"execute entity query node '{executeQuery.Name}'")).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(executeQuery.ResultVariable))
                        state.Context.SetVariable(executeQuery.ResultVariable, result);

                    return new(executeQuery.NextNode, IsEnded: false, Result: null, Wait: null);
                }

                case ComputeValueNode compute:
                {
                    services.EnsureCapability(
                        capability: ProcessCapability.PureEvaluation,
                        context: state.Context,
                        operation: $"execute compute node '{compute.Name}'"
                        );

                    var result = compute.ValueExpression(state.Context);
                    if (!string.IsNullOrWhiteSpace(compute.ResultVariable))
                        state.Context.SetVariable(compute.ResultVariable, result);

                    return new(compute.NextNode, IsEnded: false, Result: null, Wait: null);
                }

                case ExecuteEntityTransitionNode executeTransition:
                {
                    var transitionValue = executeTransition.TransitionExpression(state.Context);
                    var result = await ExecuteAuthoredTransitionAsync(
                        context: context,
                        state: state,
                        rawValue: transitionValue)
                        .ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(executeTransition.ResultVariable))
                        state.Context.SetVariable(executeTransition.ResultVariable, result);

                    return new(executeTransition.NextNode, IsEnded: false, Result: null, Wait: null);
                }

                case WaitNode wait:
                {
                    var waitKey = wait.KeyExpression(state.Context);
                    var timeout = wait.TimeoutExpression?.Invoke(state.Context);
                    if (waitHandlingMode is ProcessWaitHandlingMode.Yield)
                    {
                        if (state.TransactionDepth > 0)
                        {
                            throw new SemanticRuleViolationException($"Durable wait node '{wait.Name}' is not supported inside a transaction scope.");
                        }

                        await PersistCheckpointAsync(
                            context,
                            process: process,
                            state: state,
                            currentNode: wait.Name,
                            status: ProcessExecutionStatus.Waiting
                            ).ConfigureAwait(false);

                        logger.LogInformation(
                            "Process '{ProcessName}' ({ProcessId}) yielded wait node '{NodeName}' ({WaitType}) with key '{WaitKey}' and timeout '{Timeout}'.",
                            state.Context.ProcessName,
                            state.Context.ProcessId,
                            wait.Name,
                            wait.WaitType,
                            waitKey,
                            timeout
                            );

                        return new(
                            NextNode: wait.Name,
                            IsEnded: false,
                            Result: null,
                            Wait: new(
                                WaitType: wait.WaitType,
                                NodeName: wait.Name,
                                Key: waitKey,
                                Timeout: timeout,
                                CaptureVar: wait.CaptureVar,
                                NextNode: wait.NextNode
                            )
                        );
                    }

                    await PersistCheckpointAsync(
                        context,
                        process: process,
                        state: state,
                        currentNode: wait.Name,
                        status: ProcessExecutionStatus.Waiting
                        ).ConfigureAwait(false);

                    logger.LogInformation(
                        "Process '{ProcessName}' ({ProcessId}) waiting on node '{NodeName}' ({WaitType}) with key '{WaitKey}' and timeout '{Timeout}'.",
                        state.Context.ProcessName,
                        state.Context.ProcessId,
                        wait.Name,
                        wait.WaitType,
                        waitKey,
                        timeout
                        );

                    var payload = await services
                        .RequireWaitAdapter($"execute wait node '{wait.Name}'")
                        .WaitAsync(context, wait.WaitType, waitKey, timeout)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(wait.CaptureVar))
                        state.Context.SetVariable(wait.CaptureVar!, payload);

                    logger.LogInformation(
                        "Process '{ProcessName}' ({ProcessId}) resumed from wait node '{NodeName}' ({WaitType}) with key '{WaitKey}'.",
                        state.Context.ProcessName,
                        state.Context.ProcessId,
                        wait.Name,
                        wait.WaitType,
                        waitKey
                        );

                    return new(wait.NextNode, IsEnded: false, Result: null, Wait: null);
                }

                case BranchingNode choose:
                {
                    services.EnsureCapability(
                        capability: ProcessCapability.PureEvaluation,
                        context: state.Context,
                        operation: $"evaluate choose node '{choose.Name}'"
                        );

                    string? nextNode = null;
                    foreach (var c in choose.Branches)
                    {
                        if (c.Condition(state.Context))
                        {
                            nextNode = c.Node;
                            break;
                        }
                    }

                    return new(nextNode ?? choose.ElseNode, IsEnded: false, Result: null, Wait: null);
                }

                case TransactionNode transaction:
                {
                    if (transaction.Scope.Kind is not ProcessTransactionScopeKind.None)
                    {
                        services.EnsureCapability(
                            capability: ProcessCapability.Transactions,
                            context: state.Context,
                            operation: $"execute transaction node '{transaction.Name}'"
                            );
                    }

                    var outcome = await ExecuteTransactionNodeAsync(
                        context: context,
                        process: process,
                        state: state,
                        transaction: transaction,
                        waitHandlingMode: waitHandlingMode
                        ).ConfigureAwait(false);

                    if (outcome.IsEnded || outcome.IsWaiting)
                        return outcome;

                    return outcome with { NextNode = transaction.NextNode };
                }

                case MoveNode move:
                    _ = services.ResolvePlace(move.TargetPlace);
                    state.ContinuationFrames.Add(new(
                        NextNode: move.NextNode,
                        ReturnPlace: state.Context.CurrentPlace));
                    state.Context.CurrentPlace = move.TargetPlace;
                    return new(move.BodyNode, IsEnded: false, Result: null, Wait: null);

                case EndNode end:
                {
                    services.EnsureCapability(
                        capability: ProcessCapability.PureEvaluation,
                        context: state.Context,
                        operation: $"evaluate end node '{end.Name}'"
                        );

                    var result = end.ResultExpression?.Invoke(state.Context);

                    while (state.ContinuationFrames.Count > 0)
                    {
                        var frame = state.ContinuationFrames[^1];
                        state.ContinuationFrames.RemoveAt(state.ContinuationFrames.Count - 1);
                        state.Context.CurrentPlace = frame.ReturnPlace;
                    }

                    return new(NextNode: null, IsEnded: true, Result: result, Wait: null);
                }

                default:
                    throw new SemanticRuleViolationException($"Process node '{node.Name}' has unsupported node type '{node.GetType().Name}'.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Process node '{NodeName}' ({NodeType}) for process '{ProcessName}' ({ProcessId}) failed in place '{Place}'.",
                node.Name,
                node.GetType().Name,
                state.Context.ProcessName,
                state.Context.ProcessId,
                state.Context.CurrentPlace
                );
            throw;
        }
    }

    async Task<TransitionResult> ExecuteTransitionAsync(
        OperationContext context,
        ProcessRuntimeState state,
        ProcessEntityRef entity,
        string transitionName,
        object? input,
        ProcessEffectSchedulingMode effectScheduling
        )
    {
        services.EnsureCapability(
            capability: ProcessCapability.StateMutation,
            context: state.Context,
            operation: $"run transition '{transitionName}'"
            );

        var snapshot = await services.EntityRepository.Get(context, entity).ConfigureAwait(false);
        var normalizedInput = NormalizeTransitionInput(input);

        var transitionResult = await services.TransitionHost.DecideAsync(
            context: context,
            entity: entity,
            state: snapshot.State,
            version: snapshot.Version,
            transitionName: transitionName,
            input: normalizedInput
            ).ConfigureAwait(false);

        if (transitionResult.Effects.Count > 0)
        {
            services.EnsureCapability(
                capability: ProcessCapability.Outbox,
                context: state.Context,
                operation: $"persist outbox effects for transition '{transitionName}'"
                );
        }

        await services.EntityRepository.Update(
            context: context,
            entity: entity,
            transition: transitionResult,
            processId: state.Context.ProcessId,
            options: ProcessEntityWriteOptions.Full(snapshot.ConcurrencyToken)
            ).ConfigureAwait(false);

        state.Transitions.Add(transitionResult);

        foreach (var effect in transitionResult.Effects)
        {
            var pending = new ProcessPendingEffect(Request: effect, ContinuationEntity: entity);
            if (effectScheduling is ProcessEffectSchedulingMode.AutoDispatch)
                state.AutoEffects.Add(pending);
            else
                state.DeferredEffects.Add(pending);
        }

        if (effectScheduling is ProcessEffectSchedulingMode.AutoDispatch && state.TransactionDepth == 0)
        {
            await DrainAutoEffectsAsync(context, state).ConfigureAwait(false);
        }

        return transitionResult;
    }

    async Task<object?> ExecuteAuthoredTransitionAsync(
        OperationContext context,
        ProcessRuntimeState state,
        object? rawValue)
    {
        switch (rawValue)
        {
            case ProcessEntityTransitionInvocation invocation:
                return await ExecuteTransitionAsync(
                    context: context,
                    state: state,
                    entity: invocation.Entity,
                    transitionName: invocation.TransitionName,
                    input: invocation.Input,
                    effectScheduling: invocation.EffectScheduling
                    ).ConfigureAwait(false);

            case ProcessEntityTransitionBatch batch:
            {
                List<TransitionResult> results = [];
                foreach (var invocation in batch.Transitions)
                {
                    results.Add(await ExecuteTransitionAsync(
                        context: context,
                        state: state,
                        entity: invocation.Entity,
                        transitionName: invocation.TransitionName,
                        input: invocation.Input,
                        effectScheduling: invocation.EffectScheduling
                        ).ConfigureAwait(false));
                }

                return results;
            }

            default:
                throw new SemanticRuleViolationException(
                    $"Authored transition node expects a '{nameof(ProcessEntityTransitionInvocation)}' or '{nameof(ProcessEntityTransitionBatch)}' but received '{rawValue?.GetType().FullName ?? "null"}'.");
        }
    }

    async Task DrainAutoEffectsAsync(OperationContext context, ProcessRuntimeState state)
    {
        if (state.IsDrainingAutoEffects)
            return;

        state.IsDrainingAutoEffects = true;
        try
        {
            while (state.AutoEffects.Count > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var pending = state.AutoEffects[0];
                state.AutoEffects.RemoveAt(0);

                var outcome = await ExecuteEffectRequestAsync(
                    context: context,
                    state: state,
                    request: pending.Request,
                    continuationEntity: pending.ContinuationEntity,
                    isExplicitExecution: false).ConfigureAwait(false);

                if (!outcome.WasHandled)
                    state.DeferredEffects.Add(pending);
            }
        }
        finally
        {
            state.IsDrainingAutoEffects = false;
        }
    }

    async Task<EffectDispatchOutcome> ExecuteEffectRequestAsync(
        OperationContext context,
        ProcessRuntimeState state,
        EffectRequest request,
        ProcessEntityRef? continuationEntity,
        bool isExplicitExecution
        )
    {
        ArgumentNullException.ThrowIfNull(request);

        services.EnsureCapability(
            capability: ProcessCapability.ExternalIO,
            context: state.Context,
            operation: $"execute effect request '{request.Name}'"
            );

        if (!services.TryResolveHandler(request.Name, out var handler))
        {
            if (isExplicitExecution)
            {
                logger.LogError(
                    "No effect handler is registered for explicit request '{RequestName}' in process '{ProcessName}' ({ProcessId}).",
                    request.Name,
                    state.Context.ProcessName,
                    state.Context.ProcessId
                    );
                throw new SemanticRuleViolationException($"No effect handler is registered for request '{request.Name}'.");
            }

            logger.LogWarning(
                "Auto-dispatch effect request '{RequestName}' for process '{ProcessName}' ({ProcessId}) was deferred because no handler is registered.",
                request.Name,
                state.Context.ProcessName,
                state.Context.ProcessId
                );

            return new(false, null, null);
        }

        var attempt = 0;
        while (true)
        {
            context.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                logger.LogDebug(
                    "Dispatching effect request '{RequestName}' for process '{ProcessName}' ({ProcessId}) on attempt {Attempt}.",
                    request.Name,
                    state.Context.ProcessName,
                    state.Context.ProcessId,
                    attempt
                    );

                var handlerResult = await handler.HandleAsync(context, request).ConfigureAwait(false);

                var continuation = await ApplyContinuationIfAnyAsync(
                    context: context,
                    state: state,
                    request: request,
                    continuationEntity: continuationEntity,
                    handlerResult: handlerResult
                    ).ConfigureAwait(false);

                state.ExecutedEffects.Add(new(
                    Request: request,
                    Result: handlerResult,
                    ContinuationTransition: continuation
                ));

                logger.LogDebug(
                    "Effect request '{RequestName}' for process '{ProcessName}' ({ProcessId}) succeeded on attempt {Attempt}.",
                    request.Name,
                    state.Context.ProcessName,
                    state.Context.ProcessId,
                    attempt
                    );

                return new(true, handlerResult, continuation);
            }
            catch (Exception ex) when (IsTransientEffectFailure(ex) && attempt < services.Options.MaxEffectAttempts)
            {
                var delay = ComputeBackoff(services.Options.EffectRetryInitialDelay, attempt);
                logger.LogWarning(
                    ex,
                    "Transient failure executing effect request '{RequestName}' for process '{ProcessName}' ({ProcessId}) on attempt {Attempt}/{MaxAttempts}; retrying after {Delay}.",
                    request.Name,
                    state.Context.ProcessName,
                    state.Context.ProcessId,
                    attempt,
                    services.Options.MaxEffectAttempts,
                    delay
                    );
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, context.TimeProvider, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var deadLetter = new ProcessDeadLetter(
                    Request: request,
                    ContinuationEntity: continuationEntity,
                    Attempts: attempt,
                    IsTransient: IsTransientEffectFailure(ex),
                    ErrorType: ex.GetType().FullName ?? ex.GetType().Name,
                    ErrorMessage: ex.Message);

                state.DeadLetters.Add(deadLetter);
                await services.DeadLetterSink.EnqueueAsync(context, deadLetter).ConfigureAwait(false);

                logger.LogError(
                    ex,
                    "Effect request '{RequestName}' for process '{ProcessName}' ({ProcessId}) failed after {AttemptCount} attempt(s) and was dead-lettered.",
                    request.Name,
                    state.Context.ProcessName,
                    state.Context.ProcessId,
                    attempt
                    );

                if (isExplicitExecution)
                    throw;

                return new(true, null, null);
            }
        }
    }

    async Task<TransitionResult?> ApplyContinuationIfAnyAsync(
        OperationContext context,
        ProcessRuntimeState state,
        EffectRequest request,
        ProcessEntityRef? continuationEntity,
        object? handlerResult
        )
    {
        var continuation = request.Continuation;
        if (continuation is null)
            return null;

        if (continuation.HasDirectReference)
        {
            continuation.EnsureSnapshotMatches(request.Snapshot);
            var transition = await continuation
                .RunAsync(handlerResult, context.CancellationToken)
                .ConfigureAwait(false);

            state.Transitions.Add(transition);
            if (continuationEntity is not null)
            {
                var snapshot = await services.EntityRepository
                    .Get(context, continuationEntity)
                    .ConfigureAwait(false);

                await services.EntityRepository.Update(
                    context: context,
                    entity: continuationEntity,
                    transition: transition,
                    processId: state.Context.ProcessId,
                    options: ProcessEntityWriteOptions.Full(snapshot.ConcurrencyToken)).ConfigureAwait(false);
            }

            foreach (var effect in transition.Effects)
                state.AutoEffects.Add(new(effect, continuationEntity));

            if (state.TransactionDepth == 0)
                await DrainAutoEffectsAsync(context, state).ConfigureAwait(false);

            return transition;
        }

        if (continuationEntity is null)
            throw new SemanticRuleViolationException($"Continuation '{continuation.TransitionName}' for effect '{request.Name}' requires an entity reference.");

        if (!await IsContinuationSnapshotCurrentAsync(
                context: context,
                request: request,
                continuationEntity: continuationEntity).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Ignoring stale continuation transition '{TransitionName}' for effect '{RequestName}' in process '{ProcessName}' ({ProcessId}).",
                continuation.TransitionName,
                request.Name,
                state.Context.ProcessName,
                state.Context.ProcessId
                );
            return null;
        }

        return await ExecuteTransitionAsync(
            context: context,
            state: state,
            entity: continuationEntity,
            transitionName: continuation.TransitionName,
            input: handlerResult,
            effectScheduling: ProcessEffectSchedulingMode.AutoDispatch
            ).ConfigureAwait(false);
    }

    async Task<bool> IsContinuationSnapshotCurrentAsync(
        OperationContext context,
        EffectRequest request,
        ProcessEntityRef continuationEntity
        )
    {
        if (request.Snapshot is null)
            return true;

        var snapshot = await services.EntityRepository.Get(context, continuationEntity).ConfigureAwait(false);
        var currentToken = SnapshotTokenProjector.Compute(
            stateByFieldName: snapshot.State.Fields,
            fieldNames: request.Snapshot.FieldNames);

        if (string.Equals(currentToken, request.Snapshot.Token, StringComparison.Ordinal))
            return true;

        if (services.Options.StaleContinuationPolicy is StaleContinuationPolicy.Ignore)
            return false;

        throw new SemanticRuleViolationException($"Continuation transition '{request.Continuation?.TransitionName}' rejected stale effect result due to snapshot token mismatch.");
    }

    async Task<ProcessNodeExecutionOutcome> ExecuteTransactionNodeAsync(
        OperationContext context,
        ProcessDefinition process,
        ProcessRuntimeState state,
        TransactionNode transaction,
        ProcessWaitHandlingMode waitHandlingMode
        )
    {
        var attempt = 0;
        while (true)
        {
            context.ThrowIfCancellationRequested();
            attempt++;

            var snapshot = CaptureRuntimeSnapshot(state);
            state.TransactionDepth++;
            try
            {
                var outcome = await services.RequireTransactionGateway($"execute transaction node '{transaction.Name}'").ExecuteInTransactionAsync(
                    context: context,
                    scope: transaction.Scope,
                    action: transactionContext => ExecuteNodesAsync(
                        context: transactionContext,
                        process: process,
                        state: state,
                        startNode: transaction.BodyNode,
                        waitHandlingMode: waitHandlingMode
                        ),
                    isolationLevel: transaction.IsolationLevel
                    ).ConfigureAwait(false);

                state.TransactionDepth--;
                if (state.TransactionDepth == 0)
                    await DrainAutoEffectsAsync(context, state).ConfigureAwait(false);

                return outcome;
            }
            catch (ProcessConcurrencyConflictException conflict)
            {
                state.TransactionDepth--;
                RestoreRuntimeSnapshot(state, snapshot);

                var decision = await ResolveConflictDecisionAsync(
                    context: context,
                    policy: transaction.OnConflictPolicy,
                    scope: transaction.Scope,
                    attempt: attempt,
                    conflict: conflict).ConfigureAwait(false);

                switch (decision)
                {
                    case ConflictResolutionDecision.Retry:
                        logger.LogWarning(
                            conflict,
                            "Transaction node '{NodeName}' for process '{ProcessName}' ({ProcessId}) hit a concurrency conflict on attempt {Attempt} and will retry.",
                            transaction.Name,
                            state.Context.ProcessName,
                            state.Context.ProcessId,
                            attempt
                            );
                        continue;

                    case ConflictResolutionDecision.ConvertToSaga:
                        logger.LogWarning(
                            conflict,
                            "Transaction node '{NodeName}' for process '{ProcessName}' ({ProcessId}) escalated to saga after attempt {Attempt}.",
                            transaction.Name,
                            state.Context.ProcessName,
                            state.Context.ProcessId,
                            attempt
                            );
                        throw new ProcessSagaEscalationException(
                            $"Transaction node '{transaction.Name}' escalated to saga after conflict at attempt {attempt}. " +
                            $"Scope: '{transaction.Scope.Kind}'.");

                    case ConflictResolutionDecision.Fail:
                    default:
                        logger.LogError(
                            conflict,
                            "Transaction node '{NodeName}' for process '{ProcessName}' ({ProcessId}) failed after concurrency conflict on attempt {Attempt}.",
                            transaction.Name,
                            state.Context.ProcessName,
                            state.Context.ProcessId,
                            attempt
                            );
                        throw;
                }
            }
            catch
            {
                state.TransactionDepth--;
                RestoreRuntimeSnapshot(state, snapshot);
                throw;
            }
        }
    }

    static async Task<ConflictResolutionDecision> ResolveConflictDecisionAsync(
        OperationContext context,
        OnConflictPolicy policy,
        ProcessTransactionScope scope,
        int attempt,
        ProcessConcurrencyConflictException conflict
        )
    {
        ArgumentNullException.ThrowIfNull(policy);

        switch (policy)
        {
            case RetryWithBackoffPolicy retry:
            {
                if (attempt >= retry.MaxAttempts)
                    return ConflictResolutionDecision.Fail;

                var backoff = ComputeBackoff(retry.InitialDelay, attempt);
                if (backoff > TimeSpan.Zero)
                    await Task.Delay(backoff, context.TimeProvider, context.CancellationToken).ConfigureAwait(false);

                return ConflictResolutionDecision.Retry;
            }

            case ConvertToSagaOnConflictPolicy:
                return ConflictResolutionDecision.ConvertToSaga;

            case CustomOnConflictPolicy custom:
                return await custom.ResolveAsync(
                    context,
                    new ProcessConflictContext(scope, attempt, conflict)).ConfigureAwait(false);

            case FailOnConflictPolicy:
            default:
                return ConflictResolutionDecision.Fail;
        }
    }

    static ProcessRuntimeSnapshot CaptureRuntimeSnapshot(ProcessRuntimeState state)
    {
        return new(
            CurrentPlace: state.Context.CurrentPlace,
            Variables: state.Context.CloneVariables(),
            ContinuationFrames: [.. state.ContinuationFrames],
            TransitionCount: state.Transitions.Count,
            ExecutedEffectCount: state.ExecutedEffects.Count,
            DeferredEffectCount: state.DeferredEffects.Count,
            AutoEffectCount: state.AutoEffects.Count,
            DeadLetterCount: state.DeadLetters.Count,
            TransactionDepth: state.TransactionDepth);
    }

    static void RestoreRuntimeSnapshot(ProcessRuntimeState state, ProcessRuntimeSnapshot snapshot)
    {
        state.Context.CurrentPlace = snapshot.CurrentPlace;
        state.Context.RestoreVariables(snapshot.Variables);
        state.ContinuationFrames.Clear();
        state.ContinuationFrames.AddRange(snapshot.ContinuationFrames);
        Truncate(state.Transitions, snapshot.TransitionCount);
        Truncate(state.ExecutedEffects, snapshot.ExecutedEffectCount);
        Truncate(state.DeferredEffects, snapshot.DeferredEffectCount);
        Truncate(state.AutoEffects, snapshot.AutoEffectCount);
        Truncate(state.DeadLetters, snapshot.DeadLetterCount);
        state.TransactionDepth = snapshot.TransactionDepth;
    }

    static void Truncate<T>(List<T> list, int count)
    {
        if (list.Count <= count)
            return;

        list.RemoveRange(index: count, count: list.Count - count);
    }

    async Task PersistCheckpointAsync(
        OperationContext context,
        ProcessCheckpoint checkpoint
        )
    {
        var checkpointRepository = services.TryGetCheckpointRepository();
        if (checkpointRepository is null)
            return;

        await checkpointRepository.SaveCheckpointAsync(
            context: context,
            checkpoint: checkpoint
            ).ConfigureAwait(false);
    }

    async Task PersistCheckpointAsync(
        OperationContext context,
        ProcessDefinition process,
        ProcessRuntimeState state,
        string? currentNode,
        bool isCompleted
        )
    {
        await PersistCheckpointAsync(
            context: context,
            checkpoint: planner.BuildCheckpoint(
                process: process,
                state: state,
                currentNode: currentNode,
                status: isCompleted ? ProcessExecutionStatus.Completed : ProcessExecutionStatus.Running,
                updatedAtUtc: context.UtcNow,
                completedAtUtc: isCompleted ? context.UtcNow : null)
            ).ConfigureAwait(false);
    }

    async Task PersistCheckpointAsync(
        OperationContext context,
        ProcessDefinition process,
        ProcessRuntimeState state,
        string? currentNode,
        ProcessExecutionStatus status,
        object? result = null,
        DateTimeOffset? completedAtUtc = null
        )
    {
        await PersistCheckpointAsync(
            context: context,
            checkpoint: planner.BuildCheckpoint(
                process: process,
                state: state,
                currentNode: currentNode,
                status: status,
                updatedAtUtc: context.UtcNow,
                result: result,
                completedAtUtc: completedAtUtc
            )
        ).ConfigureAwait(false);
    }

    static bool IsTransientEffectFailure(Exception exception) =>
        exception is ProcessTransientEffectException or TimeoutException;

    static TimeSpan ComputeBackoff(TimeSpan initialDelay, int attempt)
    {
        if (initialDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var boundedExponent = Math.Clamp(attempt - 1, min: 0, max: 16);
        var multiplier = 1L << boundedExponent;
        var ticks = initialDelay.Ticks * multiplier;
        if (ticks <= 0 || ticks > TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;

        return TimeSpan.FromTicks(ticks);
    }

    static ProcessRequestInvocation ResolveRequestInvocation(object? rawValue, ProcessEntityRef? defaultContinuationEntity)
    {
        switch (rawValue)
        {
            case ProcessRequestInvocation invocation:
                if (invocation.ContinuationEntity is not null || defaultContinuationEntity is null)
                    return invocation;

                return invocation with
                {
                    ContinuationEntity = defaultContinuationEntity
                };

            case EffectRequest request:
                return new(Request: request, ContinuationEntity: defaultContinuationEntity);

            default:
                throw new SemanticRuleViolationException($"ExecuteRequest expects an EffectRequest or ProcessRequestInvocation but received '{rawValue?.GetType().FullName ?? "null"}'.");
        }
    }

    static IReadOnlyDictionary<string, ObservationValue> NormalizeTransitionInput(object? input)
    {
        if (input is null)
            return new Dictionary<string, ObservationValue>(StringComparer.Ordinal);

        if (input is IReadOnlyDictionary<string, ObservationValue> observedMap)
            return new Dictionary<string, ObservationValue>(observedMap, StringComparer.Ordinal);

        if (input is IDictionary<string, ObservationValue> observedDictionary)
        {
            return observedDictionary.ToDictionary(
                keySelector: x => x.Key,
                elementSelector: x => x.Value,
                comparer: StringComparer.Ordinal
                );
        }

        if (input is ObservationValue observed && observed.Kind == ObservationValueKind.Object && observed.Fields is not null)
            return new Dictionary<string, ObservationValue>(observed.Fields, StringComparer.Ordinal);

        if (input is IReadOnlyDictionary<string, object?> objectMap)
            return ConvertObjectDictionaryToObservations(objectMap);

        if (input is IDictionary<string, object?> objectDictionary)
            return ConvertObjectDictionaryToObservations(objectDictionary);

        var properties = input
            .GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.GetMethod is not null && !x.GetMethod.IsStatic && x.GetIndexParameters().Length == 0)
            .OrderBy(x => x.Name, StringComparer.Ordinal);

        Dictionary<string, ObservationValue> values = new(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            var value = property.GetValue(input);
            values[property.Name] = ToObservationValue(value);
        }

        return values;
    }

    static Dictionary<string, ObservationValue> ConvertObjectDictionaryToObservations(IEnumerable<KeyValuePair<string, object?>> values)
    {
        Dictionary<string, ObservationValue> result = new(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            result[key] = ToObservationValue(value);

        return result;
    }

    static ObservationValue ToObservationValue(object? value)
    {
        return value switch
        {
            ObservationValue observed => observed,
            _ => ObservationValue.FromObject(value)
        };
    }
}
