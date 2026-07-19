namespace Cohesive.Processes.Model;

/// <summary>
/// Base class for process nodes (aka: steps).
/// </summary>
public abstract class ProcessNode
{
    /// <summary>
    /// Creates a process node.
    /// </summary>
    protected ProcessNode(string name)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
    }

    /// <summary>
    /// Stable node name.
    /// </summary>
    public string Name { get; }
}

/// <summary>
/// Base node type containing an optional next-node edge.
/// </summary>
public abstract class ProcessNodeWithNext : ProcessNode
{
    /// <summary>
    /// Creates a process node with an optional next-node edge.
    /// </summary>
    protected ProcessNodeWithNext(string name, string? nextNode) : base(name)
    {
        NextNode = string.IsNullOrWhiteSpace(nextNode)
            ? null
            : nextNode;
    }

    /// <summary>
    /// Next node name.
    /// </summary>
    public string? NextNode { get; }
}

/// <summary>
/// Runs a transition for an entity reference.
/// </summary>
public sealed class RunEntityTransitionNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates a transition node.
    /// </summary>
    public RunEntityTransitionNode(
        string name,
        Func<ProcessExecutionContext, ProcessEntityRef> entityRefExpression,
        string transitionName,
        Func<ProcessExecutionContext, object?>? inputExpression = null,
        string? resultVariable = null,
        string? nextNode = null,
        ProcessEffectSchedulingMode effectScheduling = ProcessEffectSchedulingMode.AutoDispatch,
        string? onPreconditionFailureNode = null
        ) : base(name, nextNode)
    {
        EntityRefExpression = Guard.RequireNotNull(entityRefExpression);
        TransitionName = Guard.RequireNotNullOrWhiteSpace(transitionName);
        InputExpression = inputExpression;
        ResultVariable = resultVariable;
        EffectScheduling = effectScheduling;
        OnPreconditionFailureNode = onPreconditionFailureNode;
    }

    /// <summary>
    /// Resolves entity reference.
    /// </summary>
    public Func<ProcessExecutionContext, ProcessEntityRef> EntityRefExpression { get; }

    /// <summary>
    /// Transition name.
    /// </summary>
    public string TransitionName { get; }

    /// <summary>
    /// Optional transition input resolver.
    /// </summary>
    public Func<ProcessExecutionContext, object?>? InputExpression { get; }

    /// <summary>
    /// Optional captured variable name receiving the <see cref="TransitionResult"/>.
    /// Set in <see cref="ProcessExecutionContext"/>.
    /// </summary>
    public string? ResultVariable { get; }

    /// <summary>
    /// Effect scheduling mode for emitted requests.
    /// </summary>
    public ProcessEffectSchedulingMode EffectScheduling { get; }

    /// <summary>
    /// Optional node jumped to when the transition precondition fails.
    /// </summary>
    public string? OnPreconditionFailureNode { get; }
}

/// <summary>
/// Executes an effect request (IO).
/// </summary>
public sealed class ExecuteEffectRequestNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates an execute-request node.
    /// </summary>
    public ExecuteEffectRequestNode(
        string name,
        Func<ProcessExecutionContext, object?> requestExpression,
        string? resultVariable = null,
        Func<ProcessExecutionContext, ProcessEntityRef>? continuationEntityExpression = null,
        string? nextNode = null
        ) : base(name, nextNode)
    {
        RequestExpression = Guard.RequireNotNull(requestExpression);
        ResultVariable = resultVariable;
        ContinuationEntityExpression = continuationEntityExpression;
    }

    /// <summary>
    /// Request expression returning either <see cref="EffectRequest"/> or <see cref="ProcessRequestInvocation"/>.
    /// </summary>
    public Func<ProcessExecutionContext, object?> RequestExpression { get; }

    /// <summary>
    /// Optional captured variable name receiving an effect handler result.
    /// Set in <see cref="ProcessExecutionContext"/>.
    /// </summary>
    public string? ResultVariable { get; }

    /// <summary>
    /// Optional entity ref for continuation transition execution.
    /// </summary>
    public Func<ProcessExecutionContext, ProcessEntityRef>? ContinuationEntityExpression { get; }
}

/// <summary>
/// Executes a process-native entity read.
/// </summary>
public sealed class ExecuteEntityReadNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates an entity-read node.
    /// </summary>
    public ExecuteEntityReadNode(
        string name,
        Func<ProcessExecutionContext, object?> readExpression,
        string? resultVariable = null,
        string? nextNode = null
        ) : base(name, nextNode)
    {
        ReadExpression = Guard.RequireNotNull(readExpression);
        ResultVariable = resultVariable;
    }

    /// <summary>
    /// Read expression returning a <see cref="IProcessEntityReadInvocation"/>.
    /// </summary>
    public Func<ProcessExecutionContext, object?> ReadExpression { get; }

    /// <summary>
    /// Optional captured variable name receiving the read result.
    /// </summary>
    public string? ResultVariable { get; }
}

/// <summary>
/// Executes a process-native entity create.
/// </summary>
public sealed class ExecuteEntityCreateNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates an entity-create node.
    /// </summary>
    public ExecuteEntityCreateNode(
        string name,
        Func<ProcessExecutionContext, object?> createExpression,
        string? resultVariable = null,
        string? nextNode = null
        ) : base(name, nextNode)
    {
        CreateExpression = Guard.RequireNotNull(createExpression);
        ResultVariable = resultVariable;
    }

    /// <summary>
    /// Create expression returning a <see cref="IProcessEntityCreateInvocation"/>.
    /// </summary>
    public Func<ProcessExecutionContext, object?> CreateExpression { get; }

    /// <summary>
    /// Optional captured variable name receiving the create result.
    /// </summary>
    public string? ResultVariable { get; }
}

/// <summary>
/// Evaluates one canonical relation or query through the configured host evaluator.
/// </summary>
public sealed class EvaluateRelationQueryNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates a canonical relation/query evaluation node.
    /// </summary>
    /// <param name="name">Stable process-node name.</param>
    /// <param name="evaluationExpression">Expression producing the exact evaluation descriptor.</param>
    /// <param name="resultExpression">
    /// Required immediate projection from the non-wire outcome to an application-owned checkpoint value.
    /// </param>
    /// <param name="resultVariable">Optional variable receiving the projected value.</param>
    /// <param name="nextNode">Optional next-node name.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/>, <paramref name="evaluationExpression"/>, or <paramref name="resultExpression"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or white space.
    /// </exception>
    public EvaluateRelationQueryNode(
        string name,
        Func<ProcessExecutionContext, RelationQueryEvaluation> evaluationExpression,
        Func<RelationQueryEvaluationOutcome, object?> resultExpression,
        string? resultVariable = null,
        string? nextNode = null
        ) : base(name, nextNode)
    {
        EvaluationExpression = Guard.RequireNotNull(evaluationExpression);
        ResultExpression = Guard.RequireNotNull(resultExpression);
        ResultVariable = resultVariable;
    }

    /// <summary>
    /// Expression returning the exact canonical evaluation descriptor.
    /// </summary>
    public Func<ProcessExecutionContext, RelationQueryEvaluation> EvaluationExpression { get; }

    /// <summary>
    /// Required projection applied before checkpoint capture so the non-wire compiler outcome cannot become a
    /// process variable directly.
    /// </summary>
    public Func<RelationQueryEvaluationOutcome, object?> ResultExpression { get; }

    /// <summary>
    /// Optional captured variable name receiving the projected value.
    /// </summary>
    public string? ResultVariable { get; }
}

/// <summary>
/// Evaluates a pure computation and binds its result.
/// </summary>
public sealed class ComputeValueNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates a compute node.
    /// </summary>
    public ComputeValueNode(
        string name,
        Func<ProcessExecutionContext, object?> valueExpression,
        string? resultVariable = null,
        string? nextNode = null
        ) : base(name, nextNode)
    {
        ValueExpression = Guard.RequireNotNull(valueExpression);
        ResultVariable = resultVariable;
    }

    /// <summary>
    /// Pure value expression evaluated against the process execution context.
    /// </summary>
    public Func<ProcessExecutionContext, object?> ValueExpression { get; }

    /// <summary>
    /// Optional captured variable name receiving the computed value.
    /// </summary>
    public string? ResultVariable { get; }
}

/// <summary>
/// Executes a typed transition invocation or transition batch.
/// </summary>
public sealed class ExecuteEntityTransitionNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates an authored transition node.
    /// </summary>
    public ExecuteEntityTransitionNode(
        string name,
        Func<ProcessExecutionContext, object?> transitionExpression,
        string? resultVariable = null,
        string? nextNode = null
        ) : base(name, nextNode)
    {
        TransitionExpression = Guard.RequireNotNull(transitionExpression);
        ResultVariable = resultVariable;
    }

    /// <summary>
    /// Transition expression returning a <see cref="ProcessEntityTransitionInvocation"/> or <see cref="ProcessEntityTransitionBatch"/>.
    /// </summary>
    public Func<ProcessExecutionContext, object?> TransitionExpression { get; }

    /// <summary>
    /// Optional captured variable name receiving the transition result.
    /// </summary>
    public string? ResultVariable { get; }
}

/// <summary>
/// Wait types supported by process execution.
/// </summary>
public enum ProcessWaitType
{
    /// <summary>Represents the timer option.</summary>
    Timer = 0,
    /// <summary>Represents the external event option.</summary>
    ExternalEvent = 1
}

/// <summary>
/// A wait node that blocks until a timer or external event completes.
/// </summary>
public sealed class WaitNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates a wait node.
    /// </summary>
    public WaitNode(
        string name,
        ProcessWaitType waitType,
        Func<ProcessExecutionContext, string> keyExpression,
        Func<ProcessExecutionContext, TimeSpan?>? timeoutExpression = null,
        string? captureVar = null,
        string? nextNode = null
        ) : base(name, nextNode)
    {
        WaitType = waitType;
        KeyExpression = Guard.RequireNotNull(keyExpression);
        TimeoutExpression = timeoutExpression;
        CaptureVar = captureVar;
    }

    /// <summary>
    /// Wait mode.
    /// </summary>
    public ProcessWaitType WaitType { get; }

    /// <summary>
    /// Stable wait key expression.
    /// </summary>
    public Func<ProcessExecutionContext, string> KeyExpression { get; }

    /// <summary>
    /// Optional timeout expression.
    /// </summary>
    public Func<ProcessExecutionContext, TimeSpan?>? TimeoutExpression { get; }

    /// <summary>
    /// Optional captured variable name receiving wait payload.
    /// </summary>
    public string? CaptureVar { get; }
}

/// <summary>
/// Branching node.
/// </summary>
public sealed class BranchingNode : ProcessNode
{
    /// <summary>
    /// Creates a branching node.
    /// </summary>
    public BranchingNode(string name, IReadOnlyList<BranchNodeBranch> branches, string? elseNode = null)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Count == 0)
            throw new SemanticRuleViolationException($"Branch node '{name}' must declare at least one case.");

        Branches = branches;
        ElseNode = elseNode;
    }

    /// <summary>
    /// Ordered branch cases; the first matching case is chosen.
    /// </summary>
    public IReadOnlyList<BranchNodeBranch> Branches { get; }

    /// <summary>
    /// Optional fallback node.
    /// </summary>
    public string? ElseNode { get; }
}

/// <summary>
/// Branch in a <see cref="BranchingNode"/>.
/// </summary>
/// <param name="Condition">Branch predicate.</param>
/// <param name="Node">The target node when the case matches.</param>
public sealed record BranchNodeBranch(
    Func<ProcessExecutionContext, bool> Condition,
    string Node
    );

/// <summary>
/// Transaction node with explicit conflict policy.
/// </summary>
public sealed class TransactionNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates a transaction node.
    /// </summary>
    public TransactionNode(
        string name,
        ProcessTransactionScope scope,
        OnConflictPolicy onConflictPolicy,
        string bodyNode,
        ProcessIsolationLevel? isolationLevel = null,
        string? nextNode = null
        ) : base(name, nextNode)
    {
        Scope = Guard.RequireNotNull(scope);
        OnConflictPolicy = Guard.RequireNotNull(onConflictPolicy);
        BodyNode = Guard.RequireNotNullOrWhiteSpace(bodyNode);
        IsolationLevel = isolationLevel;
    }

    /// <summary>
    /// Transaction scope.
    /// </summary>
    public ProcessTransactionScope Scope { get; }

    /// <summary>
    /// Optional isolation level hint.
    /// </summary>
    public ProcessIsolationLevel? IsolationLevel { get; }

    /// <summary>
    /// Conflict policy.
    /// </summary>
    public OnConflictPolicy OnConflictPolicy { get; }

    /// <summary>
    /// Body node entry.
    /// </summary>
    public string BodyNode { get; }
}

/// <summary>
/// Locality move node.
/// </summary>
public sealed class MoveNode : ProcessNodeWithNext
{
    /// <summary>
    /// Creates a move node.
    /// </summary>
    public MoveNode(
        string name,
        string targetPlace,
        string bodyNode,
        string? nextNode = null
        ) : base(name, nextNode)
    {
        TargetPlace = Guard.RequireNotNullOrWhiteSpace(targetPlace);
        BodyNode = Guard.RequireNotNullOrWhiteSpace(bodyNode);
    }

    /// <summary>
    /// Target execution place.
    /// </summary>
    public string TargetPlace { get; }

    /// <summary>
    /// Body node entry executed inside the target place.
    /// </summary>
    public string BodyNode { get; }
}

/// <summary>
/// Terminal node.
/// </summary>
public sealed class EndNode : ProcessNode
{
    /// <summary>
    /// Creates an end node.
    /// </summary>
    public EndNode(string name, Func<ProcessExecutionContext, object?>? resultExpression = null)
        : base(name)
    {
        ResultExpression = resultExpression;
    }

    /// <summary>
    /// Optional result expression.
    /// </summary>
    public Func<ProcessExecutionContext, object?>? ResultExpression { get; }
}
