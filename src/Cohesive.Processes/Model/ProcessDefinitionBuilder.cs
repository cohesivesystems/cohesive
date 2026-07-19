namespace Cohesive.Processes.Model;

/// <summary>
/// Builder for constructing <see cref="ProcessDefinition"/> instances.
/// </summary>
public sealed class ProcessDefinitionBuilder
{
    readonly string name;
    readonly List<ProcessNode> nodes = [];
    readonly HashSet<string> nodeNames = new(StringComparer.Ordinal);
    string? entryNode;

    /// <summary>
    /// Creates a process-definition builder.
    /// </summary>
    /// <param name="name">Process definition name.</param>
    public ProcessDefinitionBuilder(string name)
    {
        this.name = Guard.RequireNotNullOrWhiteSpace(name);
    }

    /// <summary>
    /// Adds a node to the process definition.
    /// </summary>
    /// <param name="node">Node to add.</param>
    /// <param name="isEntry">Whether this node should become the entry node.</param>
    public ProcessDefinitionBuilder AddNode(ProcessNode node, bool isEntry = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        ReserveNodeName(node.Name);
        nodes.Add(node);

        if (entryNode is null || isEntry)
            entryNode = node.Name;

        return this;
    }

    /// <summary>
    /// Sets the entry node explicitly.
    /// </summary>
    /// <param name="nodeName">Entry node name.</param>
    public ProcessDefinitionBuilder SetEntryNode(string nodeName)
    {
        entryNode = Guard.RequireNotNullOrWhiteSpace(nodeName);
        return this;
    }

    /// <summary>
    /// Adds a typed effect-request node.
    /// </summary>
    /// <typeparam name="TResult">Request result type.</typeparam>
    /// <param name="name">Node name.</param>
    /// <param name="requestExpression">Request payload expression.</param>
    /// <param name="resultVariable">Optional result variable name.</param>
    /// <param name="continuationEntityExpression">Optional continuation entity expression.</param>
    /// <param name="nextNode">Optional next node name.</param>
    public ProcessDefinitionBuilder AddEffectRequestNode<TResult>(
        string name,
        Func<ProcessExecutionContext, IEffectRequestPayload<TResult>> requestExpression,
        string? resultVariable = null,
        Func<ProcessExecutionContext, ProcessEntityRef>? continuationEntityExpression = null,
        string? nextNode = null
        )
    {
        ArgumentNullException.ThrowIfNull(requestExpression);
        return AddNode(new ExecuteEffectRequestNode(
            name: name,
            requestExpression: context => ToEffectRequest(requestExpression(context)),
            resultVariable: resultVariable,
            continuationEntityExpression: continuationEntityExpression,
            nextNode: nextNode
            ));
    }

    /// <summary>
    /// Adds an effect-request invocation node using a preconstructed request or invocation object.
    /// </summary>
    /// <param name="name">Node name.</param>
    /// <param name="requestExpression">Request or invocation expression.</param>
    /// <param name="resultVariable">Optional result variable name.</param>
    /// <param name="continuationEntityExpression">Optional continuation entity expression.</param>
    /// <param name="nextNode">Optional next node name.</param>
    public ProcessDefinitionBuilder AddEffectRequestNode(
        string name,
        Func<ProcessExecutionContext, object?> requestExpression,
        string? resultVariable = null,
        Func<ProcessExecutionContext, ProcessEntityRef>? continuationEntityExpression = null,
        string? nextNode = null
        )
    {
        ArgumentNullException.ThrowIfNull(requestExpression);
        return AddNode(new ExecuteEffectRequestNode(
            name: name,
            requestExpression: requestExpression,
            resultVariable: resultVariable,
            continuationEntityExpression: continuationEntityExpression,
            nextNode: nextNode));
    }

    /// <summary>
    /// Adds a process-native entity read node.
    /// </summary>
    public ProcessDefinitionBuilder AddEntityReadNode(
        string name,
        Func<ProcessExecutionContext, object?> readExpression,
        string? resultVariable = null,
        string? nextNode = null)
    {
        ArgumentNullException.ThrowIfNull(readExpression);
        return AddNode(new ExecuteEntityReadNode(
            name: name,
            readExpression: readExpression,
            resultVariable: resultVariable,
            nextNode: nextNode));
    }

    /// <summary>
    /// Adds a process-native entity create node.
    /// </summary>
    public ProcessDefinitionBuilder AddEntityCreateNode(
        string name,
        Func<ProcessExecutionContext, object?> createExpression,
        string? resultVariable = null,
        string? nextNode = null)
    {
        ArgumentNullException.ThrowIfNull(createExpression);
        return AddNode(new ExecuteEntityCreateNode(
            name: name,
            createExpression: createExpression,
            resultVariable: resultVariable,
            nextNode: nextNode));
    }

    /// <summary>
    /// Adds a canonical relation/query evaluation node.
    /// </summary>
    /// <param name="name">Stable process-node name.</param>
    /// <param name="evaluationExpression">Expression producing the exact evaluation descriptor.</param>
    /// <param name="resultExpression">
    /// Required immediate projection from the non-wire evaluation outcome to an application-owned checkpoint value.
    /// </param>
    /// <param name="resultVariable">Optional variable receiving the projected value.</param>
    /// <param name="nextNode">Optional next-node name.</param>
    /// <returns>This builder for continued process authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/>, <paramref name="evaluationExpression"/>, or <paramref name="resultExpression"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or white space.
    /// </exception>
    /// <exception cref="SemanticRuleViolationException">
    /// <paramref name="name"/> duplicates another node name in this builder.
    /// </exception>
    public ProcessDefinitionBuilder AddRelationQueryEvaluationNode(
        string name,
        Func<ProcessExecutionContext, RelationQueryEvaluation> evaluationExpression,
        Func<RelationQueryEvaluationOutcome, object?> resultExpression,
        string? resultVariable = null,
        string? nextNode = null)
    {
        ArgumentNullException.ThrowIfNull(evaluationExpression);
        ArgumentNullException.ThrowIfNull(resultExpression);
        return AddNode(new EvaluateRelationQueryNode(
            name: name,
            evaluationExpression: evaluationExpression,
            resultExpression: resultExpression,
            resultVariable: resultVariable,
            nextNode: nextNode));
    }

    /// <summary>
    /// Adds a pure computation node.
    /// </summary>
    public ProcessDefinitionBuilder AddComputeNode(
        string name,
        Func<ProcessExecutionContext, object?> valueExpression,
        string? resultVariable = null,
        string? nextNode = null)
    {
        ArgumentNullException.ThrowIfNull(valueExpression);
        return AddNode(new ComputeValueNode(
            name: name,
            valueExpression: valueExpression,
            resultVariable: resultVariable,
            nextNode: nextNode));
    }

    /// <summary>
    /// Adds a typed transition invocation node.
    /// </summary>
    public ProcessDefinitionBuilder AddEntityTransitionNode(
        string name,
        Func<ProcessExecutionContext, object?> transitionExpression,
        string? resultVariable = null,
        string? nextNode = null)
    {
        ArgumentNullException.ThrowIfNull(transitionExpression);
        return AddNode(new ExecuteEntityTransitionNode(
            name: name,
            transitionExpression: transitionExpression,
            resultVariable: resultVariable,
            nextNode: nextNode));
    }

    /// <summary>
    /// Adds an entity transition node.
    /// </summary>
    public ProcessDefinitionBuilder AddTransitionNode(
        string name,
        Func<ProcessExecutionContext, ProcessEntityRef> entityRefExpression,
        string transitionName,
        Func<ProcessExecutionContext, object?>? inputExpression = null,
        string? resultVariable = null,
        string? nextNode = null,
        ProcessEffectSchedulingMode effectScheduling = ProcessEffectSchedulingMode.AutoDispatch,
        string? onPreconditionFailureNode = null
        )
    {
        return AddNode(new RunEntityTransitionNode(
            name: name,
            entityRefExpression: entityRefExpression,
            transitionName: transitionName,
            inputExpression: inputExpression,
            resultVariable: resultVariable,
            nextNode: nextNode,
            effectScheduling: effectScheduling,
            onPreconditionFailureNode: onPreconditionFailureNode
        ));
    }

    /// <summary>
    /// Adds a wait node.
    /// </summary>
    public ProcessDefinitionBuilder AddWaitNode(
        string name,
        ProcessWaitType waitType,
        Func<ProcessExecutionContext, string> keyExpression,
        Func<ProcessExecutionContext, TimeSpan?>? timeoutExpression = null,
        string? captureVar = null,
        string? nextNode = null
        )
    {
        return AddNode(new WaitNode(
            name: name,
            waitType: waitType,
            keyExpression: keyExpression,
            timeoutExpression: timeoutExpression,
            captureVar: captureVar,
            nextNode: nextNode
            ));
    }

    /// <summary>
    /// Adds a branching node.
    /// </summary>
    public ProcessDefinitionBuilder AddBranchingNode(string name, IReadOnlyList<BranchNodeBranch> branches, string? elseNode = null)
    {
        return AddNode(new BranchingNode(
            name: name,
            branches: branches,
            elseNode: elseNode
            ));
    }

    /// <summary>
    /// Adds a transaction node.
    /// </summary>
    public ProcessDefinitionBuilder AddTransactionNode(string name, ProcessTransactionScope scope, OnConflictPolicy onConflictPolicy, string bodyNode, ProcessIsolationLevel? isolationLevel = null, string? nextNode = null)
    {
        return AddNode(new TransactionNode(
            name: name,
            scope: scope,
            onConflictPolicy: onConflictPolicy,
            bodyNode: bodyNode,
            isolationLevel: isolationLevel,
            nextNode: nextNode
            ));
    }

    /// <summary>
    /// Adds a move node.
    /// </summary>
    public ProcessDefinitionBuilder AddMoveNode(string name, string targetPlace, string bodyNode, string? nextNode = null)
    {
        return AddNode(new MoveNode(
            name: name,
            targetPlace: targetPlace,
            bodyNode: bodyNode,
            nextNode: nextNode
            ));
    }

    /// <summary>
    /// Adds a terminal node with no result expression.
    /// </summary>
    /// <param name="name">Node name.</param>
    public ProcessDefinitionBuilder AddEndNode(string name) => AddNode(new EndNode(name: name));

    /// <summary>
    /// Adds a terminal node with a typed result expression.
    /// </summary>
    /// <typeparam name="TResult">Process result type.</typeparam>
    /// <param name="name">Node name.</param>
    /// <param name="resultExpression">Result expression.</param>
    public ProcessDefinitionBuilder AddEndNode<TResult>(string name, Func<ProcessExecutionContext, TResult> resultExpression)
    {
        ArgumentNullException.ThrowIfNull(resultExpression);
        return AddNode(new EndNode(
            name: name,
            resultExpression: context => resultExpression(context)
            ));
    }

    /// <summary>
    /// Builds the process definition.
    /// </summary>
    public ProcessDefinition Build()
    {
        if (nodes.Count == 0)
            throw new SemanticRuleViolationException($"Process '{name}' must declare at least one node.");

        return new(
            name: name,
            nodes: nodes,
            entryNode: entryNode ?? nodes[0].Name);
    }

    void ReserveNodeName(string nodeName)
    {
        if (!nodeNames.Add(nodeName))
            throw new SemanticRuleViolationException($"Process '{name}' already declares node '{nodeName}'.");
    }

    static EffectRequest ToEffectRequest<TResult>(IEffectRequestPayload<TResult> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is not IEffectRequest effectRequest)
        {
            throw new SemanticRuleViolationException(
                $"Effect request type '{request.GetType().FullName}' must implement '{typeof(IEffectRequest).FullName}'.");
        }

        var requestName = effectRequest
            .GetType()
            .GetProperty(name: nameof(IEffectRequest.RequestName))
            ?.GetValue(null) as string;

        if (string.IsNullOrWhiteSpace(requestName))
        {
            throw new SemanticRuleViolationException(
                $"Effect request type '{request.GetType().FullName}' must expose a static '{nameof(IEffectRequest.RequestName)}' property.");
        }

        return EffectRequest.Named(requestName, request);
    }
}
