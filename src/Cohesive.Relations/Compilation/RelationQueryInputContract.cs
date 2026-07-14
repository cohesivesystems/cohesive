using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// Semantic role of a source input in a compiled relation or query.
/// </summary>
public enum RelationQuerySourceInputRole
{
    /// <summary>The source supplies independently acquired values.</summary>
    Source = 0,

    /// <summary>The source supplies roots provided to rooted relation evaluation.</summary>
    RelationRoot = 1
}

/// <summary>
/// One use of a semantic input by a demanded output.
/// </summary>
public sealed record RelationQueryRequirementUse
{
    internal RelationQueryRequirementUse(
        RelationQueryInputId input,
        RelationQueryOutputReference output,
        RelationQueryRequirementEffect effect,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTrace> traces)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A requirement use requires an input identifier.", nameof(input));
        Input = input;
        Output = Guard.RequireNotNull(output);
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unsupported requirement effect.");
        if (!Enum.IsDefined(requirement))
            throw new ArgumentOutOfRangeException(nameof(requirement), requirement, "Unsupported input requirement.");
        Effect = effect;
        Requirement = requirement;
        Traces = RelationQueryRequirementOrdering.NormalizeTraces(traces);
        if (Traces.IsDefaultOrEmpty)
            throw new ArgumentException("A requirement use requires at least one provenance trace.", nameof(traces));
    }

    /// <summary>Stable identity of the semantic input being used.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Demanded output affected by the input.</summary>
    public RelationQueryOutputReference Output { get; }

    /// <summary>Semantic effect through which the input affects the output.</summary>
    public RelationQueryRequirementEffect Effect { get; }

    /// <summary>Whether acquisition of the input is required or optional.</summary>
    public QueryInputRequirement Requirement { get; }

    /// <summary>Distinct propagation traces explaining the use.</summary>
    public ImmutableArray<RelationQueryRequirementTrace> Traces { get; }
}

/// <summary>
/// Required field and all demanded-output uses of that field.
/// </summary>
public sealed record RelationQueryFieldInputContract
{
    internal RelationQueryFieldInputContract(
        RelationQueryFieldInput input,
        ImmutableArray<RelationQueryRequirementUse> uses)
    {
        Input = Guard.RequireNotNull(input);
        Uses = RelationQueryInputContractOrdering.NormalizeUses(uses, input.Id);
        if (Uses.IsDefaultOrEmpty)
            throw new ArgumentException("A field input contract requires at least one use.", nameof(uses));
    }

    /// <summary>Field input, including binding, shape, path, and resolved value contract.</summary>
    public RelationQueryFieldInput Input { get; }

    /// <summary>Demanded-output uses of the field.</summary>
    public ImmutableArray<RelationQueryRequirementUse> Uses { get; }
}

/// <summary>
/// Required source binding, source-set semantics, and selected fields.
/// </summary>
public sealed record RelationQuerySourceInputContract
{
    internal RelationQuerySourceInputContract(
        RelationQuerySourceSetInput input,
        ImmutableArray<RelationQueryFieldInputContract> fields,
        ImmutableArray<RelationQueryRequirementUse> uses)
    {
        Input = Guard.RequireNotNull(input);
        Fields = NormalizeFields(fields, input);
        Uses = RelationQueryInputContractOrdering.NormalizeUses(uses, input.Id);
        if (Uses.IsDefaultOrEmpty)
            throw new ArgumentException("A source input contract requires at least one source-set use.", nameof(uses));
    }

    /// <summary>Source-set requirement from which this contract is projected.</summary>
    public RelationQuerySourceSetInput Input { get; }

    /// <summary>Logical source node.</summary>
    public QueryNodeId Node => Input.Source;

    /// <summary>Binding introduced by the source.</summary>
    public ValueBindingId Binding => Input.Binding;

    /// <summary>Shape of values supplied by the source.</summary>
    public QualifiedShapeId Shape => Input.Shape;

    /// <summary>Whether the source supplies relation roots or independently acquired values.</summary>
    public RelationQuerySourceInputRole Role => Input.Role;

    /// <summary>Whether source-set acquisition is required or optional.</summary>
    public QueryInputRequirement Requirement => Input.Requirement;

    /// <summary>Selected source fields sorted by stable input identity.</summary>
    public ImmutableArray<RelationQueryFieldInputContract> Fields { get; }

    /// <summary>Demanded-output uses of source-set existence or enumeration.</summary>
    public ImmutableArray<RelationQueryRequirementUse> Uses { get; }

    static ImmutableArray<RelationQueryFieldInputContract> NormalizeFields(
        ImmutableArray<RelationQueryFieldInputContract> fields,
        RelationQuerySourceSetInput source)
    {
        var normalized = RelationQueryInputContractOrdering.NormalizeFields(fields, nameof(fields));
        if (normalized.Any(field => field.Input.Producer != source.Source
                                    || field.Input.Binding != source.Binding
                                    || field.Input.Field.Shape != source.Shape))
        {
            throw new ArgumentException("Source fields must belong to the containing source binding.", nameof(fields));
        }
        return normalized;
    }
}

/// <summary>
/// Required semantic relationship traversal and related fields.
/// </summary>
public sealed record RelationQueryTraversalInputContract
{
    internal RelationQueryTraversalInputContract(
        RelationQueryRelationshipInput input,
        ImmutableArray<RelationQueryFieldInputContract> fields,
        ImmutableArray<RelationQueryRequirementUse> uses)
    {
        Input = Guard.RequireNotNull(input);
        Fields = RelationQueryInputContractOrdering.NormalizeFields(fields, nameof(fields));
        if (Fields.Any(field => field.Input.Producer != input.Traversal
                                || field.Input.Binding != input.Result
                                || field.Input.Field.Shape != input.ResultShape))
        {
            throw new ArgumentException("Traversal fields must belong to the traversal result binding.", nameof(fields));
        }
        Uses = RelationQueryInputContractOrdering.NormalizeUses(uses, input.Id);
        if (Uses.IsDefaultOrEmpty)
            throw new ArgumentException("A traversal input contract requires at least one relationship use.", nameof(uses));
    }

    /// <summary>Relationship requirement from which this contract is projected.</summary>
    public RelationQueryRelationshipInput Input { get; }

    /// <summary>Exact canonical relationship definition consumed from the catalog snapshot.</summary>
    public RelationshipDefinition Definition => Input.Definition;

    /// <summary>Visible binding from which traversal starts.</summary>
    public ValueBindingId From => Input.From;

    /// <summary>Shape required at the traversal source endpoint.</summary>
    public QualifiedShapeId FromShape => Input.FromShape;

    /// <summary>Binding introduced for related values.</summary>
    public ValueBindingId Result => Input.Result;

    /// <summary>Shape produced at the traversal result endpoint.</summary>
    public QualifiedShapeId ResultShape => Input.ResultShape;

    /// <summary>Join semantics applied when related values are absent.</summary>
    public JoinKind JoinKind => Input.JoinKind;

    /// <summary>Whether related-value resolution is required or optional.</summary>
    public QueryInputRequirement Requirement => Input.Requirement;

    /// <summary>Maximum number of result observations yielded for each source observation.</summary>
    public RelationshipTraversalCardinality Cardinality => Input.Cardinality;

    /// <summary>Selected fields required from related observations.</summary>
    public ImmutableArray<RelationQueryFieldInputContract> Fields { get; }

    /// <summary>Demanded-output uses of the relationship itself.</summary>
    public ImmutableArray<RelationQueryRequirementUse> Uses { get; }
}

/// <summary>
/// Required observation identity and its demanded-output uses.
/// </summary>
public sealed record RelationQueryIdentityInputContract
{
    internal RelationQueryIdentityInputContract(
        RelationQueryObservationIdentityInput input,
        ImmutableArray<RelationQueryRequirementUse> uses)
    {
        Input = Guard.RequireNotNull(input);
        Uses = RelationQueryInputContractOrdering.NormalizeUses(uses, input.Id);
        if (Uses.IsDefaultOrEmpty)
            throw new ArgumentException("An identity input contract requires at least one use.", nameof(uses));
    }

    /// <summary>Required identity input.</summary>
    public RelationQueryObservationIdentityInput Input { get; }

    /// <summary>Demanded-output uses of the identity.</summary>
    public ImmutableArray<RelationQueryRequirementUse> Uses { get; }
}

/// <summary>
/// Required invocation parameter and its demanded-output uses.
/// </summary>
public sealed record RelationQueryParameterInputContract
{
    internal RelationQueryParameterInputContract(
        RelationQueryParameterInput input,
        ImmutableArray<RelationQueryRequirementUse> uses)
    {
        Input = Guard.RequireNotNull(input);
        ValueContract = Input.Definition.EffectiveValueContract;
        Uses = RelationQueryInputContractOrdering.NormalizeUses(uses, input.Id);
        if (Uses.IsDefaultOrEmpty)
            throw new ArgumentException("A parameter input contract requires at least one use.", nameof(uses));
    }

    /// <summary>Required parameter input.</summary>
    public RelationQueryParameterInput Input { get; }

    /// <summary>Canonical parameter declaration.</summary>
    public QueryParameterDefinition Definition => Input.Definition;

    /// <summary>Effective expression value contract after parameter defaults are applied.</summary>
    public ExprValueContract ValueContract { get; }

    /// <summary>Demanded-output uses of the parameter.</summary>
    public ImmutableArray<RelationQueryRequirementUse> Uses { get; }
}

/// <summary>
/// Required expression capability and its demanded-output uses.
/// </summary>
public sealed record RelationQueryCapabilityInputContract
{
    internal RelationQueryCapabilityInputContract(
        RelationQueryCapabilityInput input,
        ImmutableArray<RelationQueryRequirementUse> uses)
    {
        Input = Guard.RequireNotNull(input);
        Uses = RelationQueryInputContractOrdering.NormalizeUses(uses, input.Id);
        if (Uses.IsDefaultOrEmpty)
            throw new ArgumentException("A capability input contract requires at least one use.", nameof(uses));
    }

    /// <summary>Required capability input.</summary>
    public RelationQueryCapabilityInput Input { get; }

    /// <summary>Demanded-output uses of the capability.</summary>
    public ImmutableArray<RelationQueryRequirementUse> Uses { get; }
}

/// <summary>
/// Immutable acquisition contract projected exclusively from a canonical requirement graph.
/// </summary>
public sealed class RelationQueryInputContract
{
    internal RelationQueryInputContract(RelationQueryRequirementGraph requirements)
    {
        Requirements = Guard.RequireNotNull(requirements);
        var uses = requirements.Edges
            .GroupBy(static edge => edge.Input.Id)
            .ToDictionary(
                static group => group.Key,
                static group => RelationQueryInputContractOrdering.NormalizeUses(
                    [
                        .. group.Select(static edge => new RelationQueryRequirementUse(
                            edge.Input.Id,
                            edge.Output,
                            edge.Effect,
                            edge.Requirement,
                            edge.Traces))
                    ],
                    group.Key));
        var fields = requirements.Inputs.OfType<RelationQueryFieldInput>().ToImmutableArray();
        HashSet<RelationQueryInputId> claimedFields = [];

        Sources =
        [
            .. requirements.Inputs.OfType<RelationQuerySourceSetInput>()
                .Select(source => new RelationQuerySourceInputContract(
                    source,
                    CreateFields(fields, claimedFields, field =>
                        field.Producer == source.Source
                        && field.Binding == source.Binding
                        && field.Field.Shape == source.Shape,
                        uses),
                    RequiredUses(uses, source.Id)))
                .OrderBy(static source => source.Node.Value, StringComparer.Ordinal)
        ];
        Traversals =
        [
            .. requirements.Inputs.OfType<RelationQueryRelationshipInput>()
                .Select(traversal => new RelationQueryTraversalInputContract(
                    traversal,
                    CreateFields(fields, claimedFields, field =>
                        field.Producer == traversal.Traversal
                        && field.Binding == traversal.Result
                        && field.Field.Shape == traversal.ResultShape,
                        uses),
                    RequiredUses(uses, traversal.Id)))
                .OrderBy(static traversal => traversal.Input.Traversal.Value, StringComparer.Ordinal)
        ];

        var unclaimed = fields.Where(field => !claimedFields.Contains(field.Id)).ToArray();
        if (unclaimed.Length != 0)
        {
            throw new ArgumentException(
                $"Requirement graph contains {unclaimed.Length} field input(s) not owned by a source or traversal.",
                nameof(requirements));
        }

        Identities =
        [
            .. requirements.Inputs.OfType<RelationQueryObservationIdentityInput>()
                .Select(input => new RelationQueryIdentityInputContract(input, RequiredUses(uses, input.Id)))
                .OrderBy(static identity => identity.Input.Id.Value, StringComparer.Ordinal)
        ];
        Parameters =
        [
            .. requirements.Inputs.OfType<RelationQueryParameterInput>()
                .Select(input => new RelationQueryParameterInputContract(input, RequiredUses(uses, input.Id)))
                .OrderBy(static parameter => parameter.Input.Parameter.Value, StringComparer.Ordinal)
        ];
        Capabilities =
        [
            .. requirements.Inputs.OfType<RelationQueryCapabilityInput>()
                .Select(input => new RelationQueryCapabilityInputContract(input, RequiredUses(uses, input.Id)))
                .OrderBy(static capability => (int)capability.Input.Capability.Kind)
                .ThenBy(static capability => capability.Input.Capability.Capability.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Canonical requirement graph from which this acquisition contract was projected.</summary>
    public RelationQueryRequirementGraph Requirements { get; }

    /// <summary>Required source bindings sorted by logical node identity.</summary>
    public ImmutableArray<RelationQuerySourceInputContract> Sources { get; }

    /// <summary>Required relationship traversals sorted by logical node identity.</summary>
    public ImmutableArray<RelationQueryTraversalInputContract> Traversals { get; }

    /// <summary>Required observation identities sorted by stable input identity.</summary>
    public ImmutableArray<RelationQueryIdentityInputContract> Identities { get; }

    /// <summary>Required invocation parameters sorted by parameter identity.</summary>
    public ImmutableArray<RelationQueryParameterInputContract> Parameters { get; }

    /// <summary>Required expression capabilities sorted by kind and capability identity.</summary>
    public ImmutableArray<RelationQueryCapabilityInputContract> Capabilities { get; }

    static ImmutableArray<RelationQueryFieldInputContract> CreateFields(
        ImmutableArray<RelationQueryFieldInput> fields,
        ISet<RelationQueryInputId> claimed,
        Func<RelationQueryFieldInput, bool> predicate,
        IReadOnlyDictionary<RelationQueryInputId, ImmutableArray<RelationQueryRequirementUse>> uses)
    {
        var selected = fields.Where(predicate).ToImmutableArray();
        foreach (var field in selected)
            claimed.Add(field.Id);
        return
        [
            .. selected.Select(field => new RelationQueryFieldInputContract(field, RequiredUses(uses, field.Id)))
                .OrderBy(static field => field.Input.Id.Value, StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<RelationQueryRequirementUse> RequiredUses(
        IReadOnlyDictionary<RelationQueryInputId, ImmutableArray<RelationQueryRequirementUse>> uses,
        RelationQueryInputId input) =>
        uses.TryGetValue(input, out var found)
            ? found
            : throw new ArgumentException($"Requirement input '{input.Value}' has no graph edge.", nameof(uses));
}

internal static class RelationQueryInputContractOrdering
{
    public static ImmutableArray<RelationQueryFieldInputContract> NormalizeFields(
        ImmutableArray<RelationQueryFieldInputContract> fields,
        string parameterName)
    {
        var normalized = fields.IsDefault ? [] : fields;
        if (normalized.Any(static field => field is null))
            throw new ArgumentException("Field input contracts cannot contain null entries.", parameterName);
        if (normalized.GroupBy(static field => field.Input.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Field input contracts cannot repeat an input identifier.", parameterName);
        return [.. normalized.OrderBy(static field => field.Input.Id.Value, StringComparer.Ordinal)];
    }

    public static ImmutableArray<RelationQueryRequirementUse> NormalizeUses(
        ImmutableArray<RelationQueryRequirementUse> uses,
        RelationQueryInputId expectedInput)
    {
        var normalized = uses.IsDefault ? [] : uses;
        if (normalized.Any(static use => use is null))
            throw new ArgumentException("Requirement uses cannot contain null entries.", nameof(uses));
        if (normalized.Any(use => use.Input != expectedInput))
            throw new ArgumentException("Every requirement use must reference the containing input.", nameof(uses));
        foreach (var group in normalized.GroupBy(static use => use.Output.Id))
        {
            var output = group.First().Output;
            if (group.Skip(1).Any(use => !Equals(use.Output, output)))
                throw new ArgumentException($"Output id '{group.Key.Value}' has conflicting requirement uses.", nameof(uses));
        }

        return
        [
            .. normalized
                .GroupBy(static use => (use.Output.Id, use.Effect))
                .Select(group => new RelationQueryRequirementUse(
                    expectedInput,
                    group.First().Output,
                    group.Key.Effect,
                    group.Any(static use => use.Requirement == QueryInputRequirement.Required)
                        ? QueryInputRequirement.Required
                        : QueryInputRequirement.Optional,
                    [.. group.SelectMany(static use => use.Traces)]))
                .OrderBy(static use => use.Output.Id.Value, StringComparer.Ordinal)
                .ThenBy(static use => (int)use.Effect)
                .ThenBy(static use => (int)use.Requirement)
        ];
    }
}
