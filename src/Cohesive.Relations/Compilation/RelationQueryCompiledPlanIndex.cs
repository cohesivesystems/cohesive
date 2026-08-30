using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Model.Expressions;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// Read-only lookup projections derived from one immutable compiled plan and shared by its interpreters.
/// </summary>
/// <remarks>
/// The compiled plan remains the semantic authority. This index contains no runtime evidence, gap state,
/// policy decisions, or mutable execution results. Its weak cache therefore avoids extending the plan's lifetime.
/// </remarks>
sealed class RelationQueryCompiledPlanIndex
{
    static readonly ConditionalWeakTable<CompiledRelationQueryPlan, Lazy<RelationQueryCompiledPlanIndex>> Indexes = [];

    RelationQueryCompiledPlanIndex(CompiledRelationQueryPlan plan)
    {
        var requirementInputs = plan.RequirementGraph.Inputs;
        Inputs = requirementInputs.ToDictionary(static input => input.Id);
        Dependencies = plan.DependencyManifest.Entries.ToDictionary(static entry => entry.Input.Id);
        SourceContracts = plan.InputContract.Sources.ToDictionary(static source => source.Input.Id);
        TraversalContracts = plan.InputContract.Traversals.ToDictionary(static traversal => traversal.Input.Id);
        FieldContracts = plan.InputContract.Sources.SelectMany(static source => source.Fields)
            .Concat(plan.InputContract.Traversals.SelectMany(static traversal => traversal.Fields))
            .ToDictionary(static field => field.Input.Id);
        IdentityContracts = plan.InputContract.Identities.ToDictionary(static identity => identity.Input.Id);
        ParameterContracts = plan.InputContract.Parameters.ToDictionary(static parameter => parameter.Input.Id);
        CapabilityContracts = plan.InputContract.Capabilities.ToDictionary(static capability => capability.Input.Id);
        ExpansionContracts = plan.InputContract.Expansions.ToDictionary(static expansion => expansion.Expansion);
        SourceInputs = requirementInputs
            .OfType<RelationQuerySourceSetInput>()
            .ToDictionary(static input => input.Source);
        FieldInputs = requirementInputs
            .OfType<RelationQueryFieldInput>()
            .GroupBy(static input => (input.Binding, input.Field.Shape, input.Field.Path))
            .ToDictionary(static group => group.Key, static group => group.First());
        ParameterInputs = requirementInputs
            .OfType<RelationQueryParameterInput>()
            .ToDictionary(static input => input.Parameter);
        CapabilityInputs = requirementInputs
            .OfType<RelationQueryCapabilityInput>()
            .ToDictionary(static input => input.Capability.Capability);
        BindingFields = requirementInputs
            .OfType<RelationQueryFieldInput>()
            .GroupBy(static input => (input.Binding, input.Field.Shape))
            .ToDictionary(static group => group.Key, static group => CreateBindingFields(group));
        Nodes = plan.ExecutionSlice.Nodes.ToDictionary(static node => node.Id);
    }

    /// <summary>Gets the shared index for one exact compiled-plan instance.</summary>
    /// <param name="plan">Immutable compiled plan whose projections are indexed.</param>
    /// <returns>The plan-owned lookup projections.</returns>
    public static RelationQueryCompiledPlanIndex For(CompiledRelationQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Indexes.GetValue(
            plan,
            static candidate => new(
                () => new(candidate),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public IReadOnlyDictionary<RelationQueryInputId, RelationQueryRequirementInput> Inputs { get; }

    public IReadOnlyDictionary<RelationQueryInputId, RelationQueryDependencyEntry> Dependencies { get; }

    public IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourceInputContract> SourceContracts { get; }

    public IReadOnlyDictionary<RelationQueryInputId, RelationQueryTraversalInputContract> TraversalContracts { get; }

    public IReadOnlyDictionary<RelationQueryInputId, RelationQueryFieldInputContract> FieldContracts { get; }

    public IReadOnlyDictionary<RelationQueryInputId, RelationQueryIdentityInputContract> IdentityContracts { get; }

    public IReadOnlyDictionary<RelationQueryInputId, RelationQueryParameterInputContract> ParameterContracts { get; }

    public IReadOnlyDictionary<RelationQueryInputId, RelationQueryCapabilityInputContract> CapabilityContracts { get; }

    public IReadOnlyDictionary<QueryNodeId, RelationQueryCollectionExpansionInputContract> ExpansionContracts { get; }

    public IReadOnlyDictionary<QueryNodeId, RelationQuerySourceSetInput> SourceInputs { get; }

    public IReadOnlyDictionary<
        (ValueBindingId Binding, QualifiedShapeId Shape, FieldPath Path),
        RelationQueryFieldInput> FieldInputs { get; }

    public IReadOnlyDictionary<QueryParameterId, RelationQueryParameterInput> ParameterInputs { get; }

    public IReadOnlyDictionary<ExprCapabilityId, RelationQueryCapabilityInput> CapabilityInputs { get; }

    public IReadOnlyDictionary<
        (ValueBindingId Binding, QualifiedShapeId Shape),
        (ImmutableArray<RelationQueryFieldInput> Inputs, ImmutableArray<string> DirectFieldNames)> BindingFields { get; }

    public IReadOnlyDictionary<QueryNodeId, RelationQueryExecutionNode> Nodes { get; }

    static (ImmutableArray<RelationQueryFieldInput> Inputs, ImmutableArray<string> DirectFieldNames)
        CreateBindingFields(IEnumerable<RelationQueryFieldInput> fields)
    {
        var inputs = fields
            .OrderBy(static input => input.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var names = ImmutableArray.CreateBuilder<string>(inputs.Length);
        foreach (var input in inputs)
        {
            if (!input.Field.Path.TryGetDirectFieldName(out var name))
                return (inputs, default);
            names.Add(name);
        }
        return (inputs, names.MoveToImmutable());
    }
}
