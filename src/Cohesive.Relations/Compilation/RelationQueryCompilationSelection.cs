using System.Collections.Immutable;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// Deterministic target-independent scope selected for one demanded result branch.
/// </summary>
/// <remarks>
/// The selection is projected once from the compiled topology, demanded outputs, profile requirements, and physical
/// placement. Target adapters can consume it without independently rediscovering branch reachability or input scope.
/// </remarks>
public sealed class RelationQueryBranchSelection
{
    readonly IReadOnlyDictionary<RelationQueryInputId, ImmutableArray<RelationQueryRequirementEdge>> inputEdges;
    readonly HashSet<QueryNodeId> reachableNodeIds;
    readonly HashSet<RelationQueryOutputId> outputIds;
    readonly Dictionary<RelationQueryRealizationRequirementId, RelationQueryRealizationRequirement> requirementsById;

    internal RelationQueryBranchSelection(
        RelationQueryNativeResultBranch branch,
        ImmutableArray<QueryNodeId> reachableNodes,
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        ImmutableArray<RelationQuerySourceInputContract> sources,
        ImmutableArray<RelationQueryTraversalInputContract> traversals,
        ImmutableArray<RelationQueryFieldInputContract> fields,
        ImmutableArray<RelationQueryRequirementInput> inputs,
        ImmutableArray<RelationQueryTemporalCapabilityInputContract> temporalCapabilities,
        ImmutableArray<RelationQueryInputId> inputIds,
        ImmutableArray<RelationQuerySourcePlacementBinding> placementBindings,
        ImmutableArray<RelationQuerySourceInstance> sourceInstances,
        IReadOnlyDictionary<RelationQueryInputId, ImmutableArray<RelationQueryRequirementEdge>> inputEdges)
    {
        Branch = branch;
        ReachableNodes = reachableNodes;
        Outputs = branch.Outputs;
        Requirements = requirements;
        Sources = sources;
        Traversals = traversals;
        Fields = fields;
        Inputs = inputs;
        TemporalCapabilities = temporalCapabilities;
        InputIds = inputIds;
        PlacementBindings = placementBindings;
        SourceInstances = sourceInstances;
        this.inputEdges = inputEdges;
        reachableNodeIds = [.. reachableNodes];
        outputIds = [.. Outputs.Select(static output => output.Id)];
        requirementsById = requirements.ToDictionary(static requirement => requirement.Id);
    }

    /// <summary>Selected demanded terminal branch.</summary>
    public RelationQueryNativeResultBranch Branch { get; }

    /// <summary>Logical nodes reachable from <see cref="Branch"/>, sorted by stable node identity.</summary>
    public ImmutableArray<QueryNodeId> ReachableNodes { get; }

    /// <summary>Demanded outputs represented by <see cref="Branch"/>, sorted by stable output identity.</summary>
    public ImmutableArray<RelationQueryOutputReference> Outputs { get; }

    /// <summary>
    /// Profile requirements applicable to <see cref="Outputs"/>, including plan-wide requirements, in canonical
    /// requirement order.
    /// </summary>
    public ImmutableArray<RelationQueryRealizationRequirement> Requirements { get; }

    /// <summary>Source input contracts selected by this branch in canonical source-node order.</summary>
    public ImmutableArray<RelationQuerySourceInputContract> Sources { get; }

    /// <summary>Relationship traversal contracts selected by this branch in canonical traversal-node order.</summary>
    public ImmutableArray<RelationQueryTraversalInputContract> Traversals { get; }

    /// <summary>Field input contracts selected by this branch in canonical input-identity order.</summary>
    public ImmutableArray<RelationQueryFieldInputContract> Fields { get; }

    /// <summary>
    /// Requirement-graph inputs used by this branch in canonical input-identity order. Temporal target capabilities
    /// are exposed separately by <see cref="TemporalCapabilities"/>.
    /// </summary>
    public ImmutableArray<RelationQueryRequirementInput> Inputs { get; }

    /// <summary>Temporal target capabilities selected by reachable nodes in canonical input-identity order.</summary>
    public ImmutableArray<RelationQueryTemporalCapabilityInputContract> TemporalCapabilities { get; }

    /// <summary>
    /// Union of requirement-graph and temporal-capability input identities in canonical identity order.
    /// </summary>
    public ImmutableArray<RelationQueryInputId> InputIds { get; }

    /// <summary>Physical placement bindings selected by this branch in canonical placement-identity order.</summary>
    public ImmutableArray<RelationQuerySourcePlacementBinding> PlacementBindings { get; }

    /// <summary>Physical source instances referenced by <see cref="PlacementBindings"/> in canonical identity order.</summary>
    public ImmutableArray<RelationQuerySourceInstance> SourceInstances { get; }

    /// <summary>Determines whether one logical node is reachable from the selected branch.</summary>
    /// <param name="node">Logical node identity to test.</param>
    /// <returns><see langword="true"/> when <paramref name="node"/> belongs to this branch's reachable topology.</returns>
    public bool ContainsNode(QueryNodeId node) => reachableNodeIds.Contains(node);

    /// <summary>Determines whether one profile requirement applies to the selected branch.</summary>
    /// <param name="requirement">Realization requirement identity to test.</param>
    /// <returns><see langword="true"/> when <paramref name="requirement"/> applies to this branch.</returns>
    public bool ContainsRequirement(RelationQueryRealizationRequirementId requirement) =>
        requirementsById.ContainsKey(requirement);

    /// <summary>
    /// Determines whether a compiled input and optional node attribution can affect this branch through one
    /// applicable realization requirement.
    /// </summary>
    /// <param name="input">Compiled requirement-graph or temporal-capability input identity.</param>
    /// <param name="node">Optional logical node attributed by target evidence.</param>
    /// <param name="requirement">Exact applicable profile requirement being assessed.</param>
    /// <returns>
    /// <see langword="true"/> when an output edge or the requirement's typed origin relates the input and optional
    /// node to this branch; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="requirement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, or <paramref name="requirement"/> is not the exact requirement selected for this
    /// branch.
    /// </exception>
    public bool IsInputRelevant(
        RelationQueryInputId input,
        QueryNodeId? node,
        RelationQueryRealizationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("Input relevance requires a non-default input identity.", nameof(input));
        if (node is { } nodeId && string.IsNullOrWhiteSpace(nodeId.Value))
            throw new ArgumentException("Input relevance cannot use a default node identity.", nameof(node));
        if (!requirementsById.TryGetValue(requirement.Id, out var selected) || !Equals(selected, requirement))
        {
            throw new ArgumentException(
                "Input relevance requires the exact profile requirement selected for this branch.",
                nameof(requirement));
        }

        if (inputEdges.TryGetValue(input, out var edges))
        {
            foreach (var edge in edges)
            {
                if (!outputIds.Contains(edge.Output.Id))
                    continue;
                if (node is null || edge.Traces.Any(trace => trace.Steps.Any(step => step.Node == node)))
                    return true;
            }
        }

        if (requirement.Origin?.Input != input)
            return false;
        if (node is null)
            return true;
        return requirement.Origin.Node == node
               || requirement.Uses.Any(use => outputIds.Contains(use.Output.Id)
                                              && use.Traces.Any(trace =>
                                                  trace.Steps.Any(step => step.Node == node)));
    }

    /// <summary>
    /// Selects the most directly attributable requirement for one branch failure.
    /// </summary>
    /// <param name="input">Failed compiled input, or <see langword="null"/> when unavailable.</param>
    /// <param name="node">Failed logical node, or <see langword="null"/> when unavailable.</param>
    /// <returns>
    /// The first canonical requirement whose origin matches <paramref name="input"/>, then one whose origin matches
    /// <paramref name="node"/>, then the first canonical branch requirement; or <see langword="null"/> when this
    /// branch has no applicable requirements.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="input"/> or <paramref name="node"/> is default.</exception>
    public RelationQueryRealizationRequirement? SelectRequirementForFailure(
        RelationQueryInputId? input = null,
        QueryNodeId? node = null)
    {
        if (input is { } inputId && string.IsNullOrWhiteSpace(inputId.Value))
            throw new ArgumentException("Failure attribution cannot use a default input identity.", nameof(input));
        if (node is { } nodeId && string.IsNullOrWhiteSpace(nodeId.Value))
            throw new ArgumentException("Failure attribution cannot use a default node identity.", nameof(node));

        if (input is not null)
        {
            foreach (var requirement in Requirements)
            {
                if (requirement.Origin?.Input == input)
                    return requirement;
            }
        }
        if (node is not null)
        {
            foreach (var requirement in Requirements)
            {
                if (requirement.Origin?.Node == node)
                    return requirement;
            }
        }
        return Requirements.IsDefaultOrEmpty ? null : Requirements[0];
    }
}

/// <summary>
/// Deterministic target-independent selection shared by contextual realization and target-native compilation.
/// </summary>
public sealed class RelationQueryCompilationSelection
{
    readonly Dictionary<RelationQueryNativeResultBranchId, RelationQueryBranchSelection> branchesById;

    RelationQueryCompilationSelection(
        ImmutableArray<RelationQueryBranchSelection> branches,
        ImmutableArray<QueryNodeId> reachableNodes,
        ImmutableArray<RelationQueryOutputReference> outputs,
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        ImmutableArray<RelationQuerySourceInputContract> sources,
        ImmutableArray<RelationQueryTraversalInputContract> traversals,
        ImmutableArray<RelationQueryFieldInputContract> fields,
        ImmutableArray<RelationQueryRequirementInput> inputs,
        ImmutableArray<RelationQueryTemporalCapabilityInputContract> temporalCapabilities,
        ImmutableArray<RelationQueryInputId> inputIds,
        ImmutableArray<RelationQuerySourcePlacementBinding> placementBindings,
        ImmutableArray<RelationQuerySourceInstance> sourceInstances)
    {
        Branches = branches;
        ReachableNodes = reachableNodes;
        Outputs = outputs;
        Requirements = requirements;
        Sources = sources;
        Traversals = traversals;
        Fields = fields;
        Inputs = inputs;
        TemporalCapabilities = temporalCapabilities;
        InputIds = inputIds;
        PlacementBindings = placementBindings;
        SourceInstances = sourceInstances;
        branchesById = branches.ToDictionary(static branch => branch.Branch.Id);
    }

    /// <summary>Selected branches in canonical branch-identity order.</summary>
    public ImmutableArray<RelationQueryBranchSelection> Branches { get; }

    /// <summary>Union of selected reachable nodes in canonical node-identity order.</summary>
    public ImmutableArray<QueryNodeId> ReachableNodes { get; }

    /// <summary>Union of selected demanded outputs in canonical output-identity order.</summary>
    public ImmutableArray<RelationQueryOutputReference> Outputs { get; }

    /// <summary>Union of selected profile requirements in canonical requirement-identity order.</summary>
    public ImmutableArray<RelationQueryRealizationRequirement> Requirements { get; }

    /// <summary>Union of selected source input contracts in canonical source-node order.</summary>
    public ImmutableArray<RelationQuerySourceInputContract> Sources { get; }

    /// <summary>Union of selected traversal input contracts in canonical traversal-node order.</summary>
    public ImmutableArray<RelationQueryTraversalInputContract> Traversals { get; }

    /// <summary>Union of selected field input contracts in canonical input-identity order.</summary>
    public ImmutableArray<RelationQueryFieldInputContract> Fields { get; }

    /// <summary>Union of selected requirement-graph inputs in canonical input-identity order.</summary>
    public ImmutableArray<RelationQueryRequirementInput> Inputs { get; }

    /// <summary>Union of selected temporal target capabilities in canonical input-identity order.</summary>
    public ImmutableArray<RelationQueryTemporalCapabilityInputContract> TemporalCapabilities { get; }

    /// <summary>Union of all selected input identities in canonical identity order.</summary>
    public ImmutableArray<RelationQueryInputId> InputIds { get; }

    /// <summary>Union of selected physical placement bindings in canonical placement-identity order.</summary>
    public ImmutableArray<RelationQuerySourcePlacementBinding> PlacementBindings { get; }

    /// <summary>Union of physical source instances used by selected placements in canonical identity order.</summary>
    public ImmutableArray<RelationQuerySourceInstance> SourceInstances { get; }

    /// <summary>Gets the deterministic selection for one selected branch.</summary>
    /// <param name="branch">Selected native result-branch identity.</param>
    /// <returns>The exact branch selection owned by this compilation selection.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="branch"/> is default or does not identify a selected branch.
    /// </exception>
    public RelationQueryBranchSelection GetBranch(RelationQueryNativeResultBranchId branch)
    {
        if (string.IsNullOrWhiteSpace(branch.Value))
            throw new ArgumentException("A branch selection requires a non-default branch identity.", nameof(branch));
        if (!branchesById.TryGetValue(branch, out var selected))
            throw new ArgumentException("The requested branch is not part of this compilation selection.", nameof(branch));
        return selected;
    }

    internal static RelationQueryCompilationSelection Create(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport profileFeasibility,
        RelationQuerySourcePlacement placement,
        ImmutableArray<RelationQueryNativeResultBranch> branches)
    {
        var nodesById = plan.ExecutionSlice.LogicalPlan.Nodes.ToDictionary(static node => node.Node);
        var inputEdges = plan.InputContract.Requirements.Edges
            .GroupBy(static edge => edge.Input.Id)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
        var selectedBranches = ImmutableArray.CreateBuilder<RelationQueryBranchSelection>(branches.Length);

        HashSet<QueryNodeId> selectedNodeIds = [];
        HashSet<RelationQueryOutputId> selectedOutputIds = [];
        HashSet<RelationQueryRealizationRequirementId> selectedRequirementIds = [];
        HashSet<RelationQueryInputId> selectedInputIds = [];
        HashSet<RelationQueryInputId> selectedTemporalInputIds = [];
        HashSet<RelationQueryInputId> selectedSourceInputIds = [];
        HashSet<RelationQueryInputId> selectedTraversalInputIds = [];
        HashSet<RelationQueryInputId> selectedFieldInputIds = [];
        HashSet<RelationQuerySourcePlacementBindingId> selectedPlacementIds = [];
        HashSet<RelationQuerySourceInstanceId> selectedSourceInstanceIds = [];

        foreach (var branch in branches)
        {
            var selection = CreateBranch(
                plan,
                profileFeasibility,
                placement,
                branch,
                nodesById,
                inputEdges);
            selectedBranches.Add(selection);
            AddIds(selection.ReachableNodes, selectedNodeIds);
            foreach (var output in selection.Outputs)
                selectedOutputIds.Add(output.Id);
            foreach (var requirement in selection.Requirements)
                selectedRequirementIds.Add(requirement.Id);
            foreach (var input in selection.Inputs)
                selectedInputIds.Add(input.Id);
            foreach (var temporal in selection.TemporalCapabilities)
                selectedTemporalInputIds.Add(temporal.Id);
            foreach (var source in selection.Sources)
                selectedSourceInputIds.Add(source.Input.Id);
            foreach (var traversal in selection.Traversals)
                selectedTraversalInputIds.Add(traversal.Input.Id);
            foreach (var field in selection.Fields)
                selectedFieldInputIds.Add(field.Input.Id);
            foreach (var placementBinding in selection.PlacementBindings)
                selectedPlacementIds.Add(placementBinding.Id);
            foreach (var sourceInstance in selection.SourceInstances)
                selectedSourceInstanceIds.Add(sourceInstance.Id);
        }

        return new(
            selectedBranches.MoveToImmutable(),
            SelectNodes(plan, selectedNodeIds),
            SelectOutputs(plan, selectedOutputIds),
            SelectRequirements(profileFeasibility, selectedRequirementIds),
            SelectSources(plan, selectedSourceInputIds),
            SelectTraversals(plan, selectedTraversalInputIds),
            SelectFields(plan, selectedFieldInputIds),
            SelectInputs(plan, selectedInputIds),
            SelectTemporalCapabilities(plan, selectedTemporalInputIds),
            SelectInputIds(selectedInputIds, selectedTemporalInputIds),
            SelectPlacementBindings(placement, selectedPlacementIds),
            SelectSourceInstances(placement, selectedSourceInstanceIds));
    }

    static RelationQueryBranchSelection CreateBranch(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport profileFeasibility,
        RelationQuerySourcePlacement placement,
        RelationQueryNativeResultBranch branch,
        IReadOnlyDictionary<QueryNodeId, RelationQueryLogicalPlanNode> nodesById,
        IReadOnlyDictionary<RelationQueryInputId, ImmutableArray<RelationQueryRequirementEdge>> inputEdges)
    {
        HashSet<QueryNodeId> reachable = [];
        Stack<QueryNodeId> pending = new(capacity: 1);
        pending.Push(branch.Node);
        while (pending.TryPop(out var node))
        {
            if (!reachable.Add(node))
                continue;
            if (!nodesById.TryGetValue(node, out var logicalNode))
            {
                throw new InvalidOperationException(
                    $"Selected branch '{branch.Id.Value}' references node '{node.Value}' absent from its logical plan.");
            }
            foreach (var input in logicalNode.EffectiveInputs)
                pending.Push(input);
        }

        HashSet<RelationQueryOutputId> outputs = [];
        foreach (var output in branch.Outputs)
            outputs.Add(output.Id);

        HashSet<RelationQueryInputId> inputIds = [];
        foreach (var edge in plan.InputContract.Requirements.Edges)
        {
            if (outputs.Contains(edge.Output.Id))
                inputIds.Add(edge.Input.Id);
        }

        HashSet<RelationQueryInputId> temporalInputIds = [];
        foreach (var temporal in plan.InputContract.TemporalCapabilities)
        {
            if (reachable.Contains(temporal.Node))
                temporalInputIds.Add(temporal.Id);
        }

        var sources = SelectBranchSources(plan, reachable);
        var traversals = SelectBranchTraversals(plan, reachable);
        foreach (var source in sources)
            inputIds.Add(source.Input.Id);
        foreach (var traversal in traversals)
            inputIds.Add(traversal.Input.Id);
        var fields = SelectBranchFields(sources, traversals, inputIds);
        HashSet<RelationQueryInputId> placementOwnerInputs = [];
        foreach (var source in sources)
            placementOwnerInputs.Add(source.Input.Id);
        foreach (var traversal in traversals)
            placementOwnerInputs.Add(traversal.Input.Id);
        var placementBindings = SelectBranchPlacements(placement, placementOwnerInputs);
        HashSet<RelationQuerySourceInstanceId> sourceInstances = [];
        foreach (var placementBinding in placementBindings)
            sourceInstances.Add(placementBinding.Source);

        return new(
            branch,
            SelectNodes(plan, reachable),
            SelectBranchRequirements(profileFeasibility, outputs),
            sources,
            traversals,
            fields,
            SelectInputs(plan, inputIds),
            SelectTemporalCapabilities(plan, temporalInputIds),
            SelectInputIds(inputIds, temporalInputIds),
            placementBindings,
            SelectSourceInstances(placement, sourceInstances),
            inputEdges);
    }

    static ImmutableArray<RelationQueryRealizationRequirement> SelectBranchRequirements(
        RelationQueryRealizationReport profileFeasibility,
        IReadOnlySet<RelationQueryOutputId> outputIds)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQueryRealizationRequirement>();
        foreach (var requirement in profileFeasibility.Requirements)
        {
            if (requirement.Uses.IsDefaultOrEmpty
                || requirement.Uses.Any(use => outputIds.Contains(use.Output.Id)))
            {
                selected.Add(requirement);
            }
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQuerySourceInputContract> SelectBranchSources(
        CompiledRelationQueryPlan plan,
        IReadOnlySet<QueryNodeId> reachable)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQuerySourceInputContract>();
        foreach (var source in plan.InputContract.Sources)
        {
            if (reachable.Contains(source.Node))
                selected.Add(source);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQueryTraversalInputContract> SelectBranchTraversals(
        CompiledRelationQueryPlan plan,
        IReadOnlySet<QueryNodeId> reachable)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQueryTraversalInputContract>();
        foreach (var traversal in plan.InputContract.Traversals)
        {
            if (reachable.Contains(traversal.Input.Traversal))
                selected.Add(traversal);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQueryFieldInputContract> SelectBranchFields(
        ImmutableArray<RelationQuerySourceInputContract> sources,
        ImmutableArray<RelationQueryTraversalInputContract> traversals,
        IReadOnlySet<RelationQueryInputId> inputIds)
    {
        List<RelationQueryFieldInputContract> selected = [];
        foreach (var source in sources)
        {
            foreach (var field in source.Fields)
            {
                if (inputIds.Contains(field.Input.Id))
                    selected.Add(field);
            }
        }
        foreach (var traversal in traversals)
        {
            foreach (var field in traversal.Fields)
            {
                if (inputIds.Contains(field.Input.Id))
                    selected.Add(field);
            }
        }
        selected.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Input.Id.Value, right.Input.Id.Value));
        return [.. selected];
    }

    static ImmutableArray<RelationQuerySourcePlacementBinding> SelectBranchPlacements(
        RelationQuerySourcePlacement placement,
        IReadOnlySet<RelationQueryInputId> ownerInputs)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQuerySourcePlacementBinding>();
        foreach (var binding in placement.Bindings)
        {
            if (ownerInputs.Contains(binding.Input))
                selected.Add(binding);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<QueryNodeId> SelectNodes(
        CompiledRelationQueryPlan plan,
        IReadOnlySet<QueryNodeId> ids)
    {
        List<QueryNodeId> selected = [];
        foreach (var node in plan.ExecutionSlice.LogicalPlan.Nodes)
        {
            if (ids.Contains(node.Node))
                selected.Add(node.Node);
        }
        selected.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
        return [.. selected];
    }

    static ImmutableArray<RelationQueryOutputReference> SelectOutputs(
        CompiledRelationQueryPlan plan,
        IReadOnlySet<RelationQueryOutputId> ids)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQueryOutputReference>();
        foreach (var output in plan.InputContract.Requirements.Outputs)
        {
            if (ids.Contains(output.Id))
                selected.Add(output);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQueryRealizationRequirement> SelectRequirements(
        RelationQueryRealizationReport profileFeasibility,
        IReadOnlySet<RelationQueryRealizationRequirementId> ids)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQueryRealizationRequirement>();
        foreach (var requirement in profileFeasibility.Requirements)
        {
            if (ids.Contains(requirement.Id))
                selected.Add(requirement);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQuerySourceInputContract> SelectSources(
        CompiledRelationQueryPlan plan,
        IReadOnlySet<RelationQueryInputId> ids)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQuerySourceInputContract>();
        foreach (var source in plan.InputContract.Sources)
        {
            if (ids.Contains(source.Input.Id))
                selected.Add(source);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQueryTraversalInputContract> SelectTraversals(
        CompiledRelationQueryPlan plan,
        IReadOnlySet<RelationQueryInputId> ids)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQueryTraversalInputContract>();
        foreach (var traversal in plan.InputContract.Traversals)
        {
            if (ids.Contains(traversal.Input.Id))
                selected.Add(traversal);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQueryFieldInputContract> SelectFields(
        CompiledRelationQueryPlan plan,
        IReadOnlySet<RelationQueryInputId> ids)
    {
        List<RelationQueryFieldInputContract> selected = [];
        foreach (var source in plan.InputContract.Sources)
        {
            foreach (var field in source.Fields)
            {
                if (ids.Contains(field.Input.Id))
                    selected.Add(field);
            }
        }
        foreach (var traversal in plan.InputContract.Traversals)
        {
            foreach (var field in traversal.Fields)
            {
                if (ids.Contains(field.Input.Id))
                    selected.Add(field);
            }
        }
        selected.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Input.Id.Value, right.Input.Id.Value));
        return [.. selected];
    }

    static ImmutableArray<RelationQueryRequirementInput> SelectInputs(
        CompiledRelationQueryPlan plan,
        IReadOnlySet<RelationQueryInputId> ids)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQueryRequirementInput>();
        foreach (var input in plan.InputContract.Requirements.Inputs)
        {
            if (ids.Contains(input.Id))
                selected.Add(input);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQueryTemporalCapabilityInputContract> SelectTemporalCapabilities(
        CompiledRelationQueryPlan plan,
        IReadOnlySet<RelationQueryInputId> ids)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQueryTemporalCapabilityInputContract>();
        foreach (var temporal in plan.InputContract.TemporalCapabilities)
        {
            if (ids.Contains(temporal.Id))
                selected.Add(temporal);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQueryInputId> SelectInputIds(
        IReadOnlySet<RelationQueryInputId> inputIds,
        IReadOnlySet<RelationQueryInputId> temporalInputIds)
    {
        HashSet<RelationQueryInputId> selected = [.. inputIds];
        selected.UnionWith(temporalInputIds);
        return [.. selected.OrderBy(static input => input.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQuerySourcePlacementBinding> SelectPlacementBindings(
        RelationQuerySourcePlacement placement,
        IReadOnlySet<RelationQuerySourcePlacementBindingId> ids)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQuerySourcePlacementBinding>();
        foreach (var binding in placement.Bindings)
        {
            if (ids.Contains(binding.Id))
                selected.Add(binding);
        }
        return selected.ToImmutable();
    }

    static ImmutableArray<RelationQuerySourceInstance> SelectSourceInstances(
        RelationQuerySourcePlacement placement,
        IReadOnlySet<RelationQuerySourceInstanceId> ids)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQuerySourceInstance>();
        foreach (var source in placement.SourceInstances)
        {
            if (ids.Contains(source.Id))
                selected.Add(source);
        }
        return selected.ToImmutable();
    }

    static void AddIds<T>(ImmutableArray<T> values, ISet<T> destination)
        where T : struct
    {
        foreach (var value in values)
            destination.Add(value);
    }
}
