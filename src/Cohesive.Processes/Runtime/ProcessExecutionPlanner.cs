namespace Cohesive.Processes.Runtime;

/// <summary>
/// Plans durable process progression from checkpoint snapshots.
/// </summary>
public sealed class ProcessExecutionPlanner
{
    readonly ProcessRuntimeServices services;

    /// <summary>
    /// Creates a planner over shared runtime services.
    /// </summary>
    public ProcessExecutionPlanner(ProcessRuntimeServices services)
    {
        this.services = Guard.RequireNotNull(services);
    }

    /// <summary>
    /// Creates an initial checkpoint for a process execution.
    /// </summary>
    public ProcessCheckpoint CreateCheckpoint(OperationContext context, ProcessDefinition process, IReadOnlyDictionary<string, object?>? parameters = null, ProcessRunOptions? runOptions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(process);
        context.CancellationToken.ThrowIfCancellationRequested();

        runOptions ??= new();

        var processId = string.IsNullOrWhiteSpace(runOptions.ProcessId)
            ? Guid.NewGuid().ToString("N")
            : runOptions.ProcessId;

        var initialPlace = string.IsNullOrWhiteSpace(runOptions.InitialPlace)
            ? services.Options.DefaultPlaceName
            : runOptions.InitialPlace;

        _ = services.ResolvePlace(initialPlace!);

        return new(
            ProcessId: processId!,
            ProcessName: process.Name,
            CurrentNode: process.EntryNode,
            CurrentPlace: initialPlace!,
            Status: ProcessExecutionStatus.Running,
            Parameters: parameters is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(parameters, StringComparer.Ordinal),
            Variables: new Dictionary<string, object?>(StringComparer.Ordinal),
            ContinuationFrames: [],
            Transitions: [],
            ExecutedEffects: [],
            PendingEffects: [],
            DeadLetters: [],
            Result: null,
            StartedAtUtc: context.UtcNow,
            UpdatedAtUtc: context.UtcNow,
            CompletedAtUtc: null);
    }

    /// <summary>
    /// Plans the next durable execution step for the supplied checkpoint.
    /// </summary>
    public ProcessExecutionPlan PlanNextStep(OperationContext context, ProcessDefinition process, ProcessCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(checkpoint);
        context.ThrowIfCancellationRequested();
        ValidateCheckpoint(process, checkpoint);

        var state = RestoreRuntimeState(checkpoint);
        var currentNode = NormalizeExecutionCursor(state, checkpoint.CurrentNode);
        if (string.IsNullOrWhiteSpace(currentNode))
        {
            var completed = BuildCheckpoint(
                process: process,
                state: state,
                currentNode: null,
                status: ProcessExecutionStatus.Completed,
                updatedAtUtc: context.UtcNow,
                result: checkpoint.Result,
                completedAtUtc: checkpoint.CompletedAtUtc ?? context.UtcNow
                );
            return new(ProcessExecutionPlanKind.Complete, completed);
        }

        var node = process.GetNode(currentNode);
        switch (node)
        {
            case MoveNode move:
                _ = services.ResolvePlace(move.TargetPlace);
                state.ContinuationFrames.Add(new(
                    NextNode: move.NextNode,
                    ReturnPlace: state.Context.CurrentPlace
                    ));
                state.Context.CurrentPlace = move.TargetPlace;

                return new(
                    ProcessExecutionPlanKind.Advance,
                    BuildCheckpoint(
                        process: process,
                        state: state,
                        currentNode: move.BodyNode,
                        status: ProcessExecutionStatus.Running,
                        updatedAtUtc: context.UtcNow));

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

                return new(
                    ProcessExecutionPlanKind.Advance,
                    BuildCheckpoint(
                        process: process,
                        state: state,
                        currentNode: nextNode ?? choose.ElseNode,
                        status: ProcessExecutionStatus.Running,
                        updatedAtUtc: context.UtcNow
                        )
                    );
            }

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

                return new(
                    ProcessExecutionPlanKind.Complete,
                    BuildCheckpoint(
                        process: process,
                        state: state,
                        currentNode: null,
                        status: ProcessExecutionStatus.Completed,
                        updatedAtUtc: context.UtcNow,
                        result: result,
                        completedAtUtc: context.UtcNow
                        )
                    );
            }

            case WaitNode wait:
            {
                if (state.TransactionDepth > 0)
                    throw new SemanticRuleViolationException($"Durable wait node '{wait.Name}' is not supported inside a transaction scope.");

                var waitKey = wait.KeyExpression(state.Context);
                var timeout = wait.TimeoutExpression?.Invoke(state.Context);
                var waitingCheckpoint = BuildCheckpoint(
                    process: process,
                    state: state,
                    currentNode: wait.Name,
                    status: ProcessExecutionStatus.Waiting,
                    updatedAtUtc: context.UtcNow
                    );

                return new(
                    ProcessExecutionPlanKind.Wait,
                    waitingCheckpoint,
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

            case RunEntityTransitionNode:
            case ExecuteEntityTransitionNode:
            case ExecuteEffectRequestNode:
            case ExecuteEntityReadNode:
            case ExecuteEntityCreateNode:
            case ExecuteEntityQueryNode:
            case ComputeValueNode:
            case TransactionNode:
                return new(
                    ProcessExecutionPlanKind.ExecuteNode,
                    BuildCheckpoint(
                        process: process,
                        state: state,
                        currentNode: currentNode,
                        status: ProcessExecutionStatus.Running,
                        updatedAtUtc: context.UtcNow
                        ),
                    NodeName: currentNode
                );

            default:
                throw new SemanticRuleViolationException($"Process node '{node.Name}' has unsupported node type '{node.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Applies a wait payload to a checkpointed wait node.
    /// </summary>
    public ProcessCheckpoint ResumeWait(OperationContext context, ProcessDefinition process, ProcessCheckpoint checkpoint, object? resumePayload)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(checkpoint);
        context.ThrowIfCancellationRequested();
        ValidateCheckpoint(process, checkpoint);

        if (string.IsNullOrWhiteSpace(checkpoint.CurrentNode))
            throw new SemanticRuleViolationException($"Process '{checkpoint.ProcessId}' cannot resume because no current node is checkpointed.");

        if (process.GetNode(checkpoint.CurrentNode) is not WaitNode wait)
            throw new SemanticRuleViolationException($"Process '{checkpoint.ProcessId}' cannot resume from node '{checkpoint.CurrentNode}' because it is not a wait node.");

        var state = RestoreRuntimeState(checkpoint);
        if (!string.IsNullOrWhiteSpace(wait.CaptureVar))
            state.Context.SetVariable(wait.CaptureVar, resumePayload);

        return BuildCheckpoint(
            process: process,
            state: state,
            currentNode: wait.NextNode,
            status: ProcessExecutionStatus.Running,
            updatedAtUtc: context.UtcNow,
            result: checkpoint.Result
            );
    }

    /// <summary>
    /// Builds a process run result from a completed checkpoint snapshot.
    /// </summary>
    public ProcessRunResult BuildRunResult(ProcessCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        return new(
            ProcessId: checkpoint.ProcessId,
            ProcessName: checkpoint.ProcessName,
            Result: checkpoint.Result,
            FinalPlace: checkpoint.CurrentPlace,
            Variables: new Dictionary<string, object?>(checkpoint.Variables, StringComparer.Ordinal),
            Transitions: checkpoint.Transitions,
            ExecutedEffects: checkpoint.ExecutedEffects,
            PendingEffects: checkpoint.PendingEffects,
            DeadLetters: checkpoint.DeadLetters
            );
    }

    internal ProcessCheckpoint BuildCheckpoint(
        ProcessDefinition process,
        ProcessRuntimeState state,
        string? currentNode,
        ProcessExecutionStatus status,
        DateTimeOffset updatedAtUtc,
        object? result = null,
        DateTimeOffset? completedAtUtc = null
        )
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(state);

        if (status is ProcessExecutionStatus.Completed
            or ProcessExecutionStatus.Failed
            or ProcessExecutionStatus.Cancelled
            or ProcessExecutionStatus.Terminated)
            completedAtUtc ??= updatedAtUtc;
        else
            completedAtUtc = null;

        state.CompletedAtUtc = completedAtUtc;

        return new(
            ProcessId: state.Context.ProcessId,
            ProcessName: process.Name,
            CurrentNode: currentNode,
            CurrentPlace: state.Context.CurrentPlace,
            Status: status,
            Parameters: new Dictionary<string, object?>(state.Context.Parameters, StringComparer.Ordinal),
            Variables: state.Context.CloneVariables(),
            ContinuationFrames: [.. state.ContinuationFrames],
            Transitions: [.. state.Transitions],
            ExecutedEffects: [.. state.ExecutedEffects],
            PendingEffects: [.. state.DeferredEffects, .. state.AutoEffects],
            DeadLetters: [.. state.DeadLetters],
            Result: result,
            StartedAtUtc: state.StartedAtUtc,
            UpdatedAtUtc: updatedAtUtc,
            CompletedAtUtc: completedAtUtc);
    }

    internal ProcessRuntimeState RestoreRuntimeState(ProcessCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        _ = services.ResolvePlace(checkpoint.CurrentPlace);
        return ProcessRuntimeState.Restore(checkpoint);
    }

    internal static string? NormalizeExecutionCursor(ProcessRuntimeState state, string? currentNode)
    {
        while (string.IsNullOrWhiteSpace(currentNode) && state.ContinuationFrames.Count > 0)
        {
            var continuation = state.ContinuationFrames[^1];
            state.ContinuationFrames.RemoveAt(state.ContinuationFrames.Count - 1);
            state.Context.CurrentPlace = continuation.ReturnPlace;
            currentNode = continuation.NextNode;
        }

        return currentNode;
    }

    internal static void ValidateCheckpoint(ProcessDefinition process, ProcessCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (!string.Equals(checkpoint.ProcessName, process.Name, StringComparison.Ordinal))
        {
            throw new SemanticRuleViolationException(
                $"Process checkpoint '{checkpoint.ProcessId}' belongs to process '{checkpoint.ProcessName}', not '{process.Name}'.");
        }
    }
}
