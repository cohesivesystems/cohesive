namespace Cohesive.Processes.Model;

/// <summary>
/// Durable process definition composed of named nodes and an entry point.
/// </summary>
public sealed class ProcessDefinition
{
    readonly IReadOnlyDictionary<string, ProcessNode> nodesByName;

    /// <summary>
    /// Creates a process definition.
    /// </summary>
    /// <exception cref="SemanticRuleViolationException"></exception>
    public ProcessDefinition(string name, IReadOnlyList<ProcessNode> nodes, string entryNode)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(nodes);
        EntryNode = Guard.RequireNotNullOrWhiteSpace(entryNode);
        if (nodes.Count == 0)
            throw new SemanticRuleViolationException($"Process '{Name}' must declare at least one node.");

        Dictionary<string, ProcessNode> dictionary = new(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (!dictionary.TryAdd(node.Name, node))
                throw new SemanticRuleViolationException($"Process '{Name}' contains duplicate node '{node.Name}'.");
        }

        nodesByName = dictionary;
        if (!nodesByName.ContainsKey(EntryNode))
            throw new SemanticRuleViolationException($"Process '{Name}' entry node '{EntryNode}' is not declared.");

        ValidateReferences();
    }

    /// <summary>
    /// Process name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Entry node name.
    /// </summary>
    public string EntryNode { get; }

    /// <summary>
    /// Node dictionary keyed by node name.
    /// </summary>
    public IReadOnlyDictionary<string, ProcessNode> Nodes => nodesByName;

    internal ProcessNode GetNode(string nodeName)
    {
        if (!nodesByName.TryGetValue(nodeName, out var node))
            throw new SemanticRuleViolationException($"Process '{Name}' does not declare node '{nodeName}'.");

        return node;
    }

    void ValidateReferences()
    {
        foreach (var node in nodesByName.Values)
        {
            if (node is ProcessNodeWithNext withNext)
                EnsureNodeExists(withNext.NextNode, node.Name, "next");

            switch (node)
            {
                case RunEntityTransitionNode runTransition:
                    EnsureNodeExists(runTransition.OnPreconditionFailureNode, node.Name, "on precondition failure");
                    break;
                case BranchingNode branching:
                    foreach (var branch in branching.Branches)
                        EnsureNodeExists(branch.Node, node.Name, "case target");
                    EnsureNodeExists(branching.ElseNode, node.Name, "else target");
                    break;
                case TransactionNode transaction:
                    EnsureNodeExists(transaction.BodyNode, node.Name, "transaction body");
                    break;
                case MoveNode move:
                    EnsureNodeExists(move.BodyNode, node.Name, "move body");
                    break;
            }
        }
    }

    void EnsureNodeExists(string? nodeName, string sourceNode, string relation)
    {
        if (string.IsNullOrWhiteSpace(nodeName))
            return;

        if (!nodesByName.ContainsKey(nodeName))
            throw new SemanticRuleViolationException($"Process '{Name}' node '{sourceNode}' references unknown {relation} node '{nodeName}'.");
    }
}