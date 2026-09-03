using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;

namespace Cohesive.Simulation.Generation;

/// <summary>Stable property and discriminator names in the portable generation-definition wire contract.</summary>
public static class GenerationDefinitionWireNames
{
    /// <summary>Polymorphic discriminator property for one value-generator node.</summary>
    public const string GeneratorDiscriminator = "$generator";

    /// <summary>Discriminator for an exact constant generator.</summary>
    public const string Constant = "constant";

    /// <summary>Discriminator for an inclusive uniform Int32 generator.</summary>
    public const string Int32 = "int32";

    /// <summary>Discriminator for a Bernoulli Boolean generator.</summary>
    public const string Bernoulli = "bernoulli";

    /// <summary>Discriminator for a finite weighted categorical generator.</summary>
    public const string WeightedCategorical = "weightedCategorical";

    /// <summary>Discriminator for a portable expression over sampled value bindings.</summary>
    public const string Expression = "expression";
}

/// <summary>Base contract for one provider-neutral generator of a concrete field value.</summary>
/// <remarks>
/// Generator nodes describe value semantics only. Entropy, CLR materialization, and test-runner policy belong to
/// separate interpretations.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = GenerationDefinitionWireNames.GeneratorDiscriminator)]
[JsonDerivedType(typeof(ConstantGenerationNode), GenerationDefinitionWireNames.Constant)]
[JsonDerivedType(typeof(Int32GenerationNode), GenerationDefinitionWireNames.Int32)]
[JsonDerivedType(typeof(BernoulliGenerationNode), GenerationDefinitionWireNames.Bernoulli)]
[JsonDerivedType(typeof(WeightedCategoricalGenerationNode), GenerationDefinitionWireNames.WeightedCategorical)]
[JsonDerivedType(typeof(ExpressionGenerationNode), GenerationDefinitionWireNames.Expression)]
public abstract record ValueGeneratorNode
{
    /// <summary>Gets the portable semantic type produced by this node.</summary>
    public abstract TypeRef ValueType { get; }
}

/// <summary>Produces one exact portable value without consuming entropy.</summary>
public sealed record ConstantGenerationNode : ValueGeneratorNode
{
    /// <summary>Creates a constant generator.</summary>
    /// <param name="valueType">Portable semantic type of <paramref name="value"/>.</param>
    /// <param name="value">Exact portable value emitted by the node.</param>
    /// <exception cref="ArgumentNullException"><paramref name="valueType"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ConstantGenerationNode(TypeRef valueType, ObservationValue value)
    {
        ValueType = Guard.RequireNotNull(valueType);
        Value = value;
    }

    /// <inheritdoc />
    public override TypeRef ValueType { get; }

    /// <summary>Gets the exact portable value emitted by this node.</summary>
    public ObservationValue Value { get; }
}

/// <summary>Produces an integer uniformly from an inclusive Int32 range.</summary>
public sealed record Int32GenerationNode : ValueGeneratorNode
{
    static readonly ScalarTypeRef Int32Type = new(ScalarTypeKind.Int32);

    /// <summary>Creates an inclusive bounded Int32 generator.</summary>
    /// <param name="minimum">Inclusive minimum value.</param>
    /// <param name="maximum">Inclusive maximum value.</param>
    /// <remarks>Range ordering is validated by the generation compiler so invalid direct IR yields diagnostics.</remarks>
    [JsonConstructor]
    public Int32GenerationNode(int minimum, int maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <inheritdoc />
    public override TypeRef ValueType => Int32Type;

    /// <summary>Gets the inclusive minimum value.</summary>
    public int Minimum { get; }

    /// <summary>Gets the inclusive maximum value.</summary>
    public int Maximum { get; }
}

/// <summary>Produces a Boolean with a declared probability of <see langword="true"/>.</summary>
public sealed record BernoulliGenerationNode : ValueGeneratorNode
{
    static readonly ScalarTypeRef BoolType = new(ScalarTypeKind.Bool);

    /// <summary>Creates a Bernoulli generator.</summary>
    /// <param name="probability">Finite probability of <see langword="true"/> in the inclusive range 0 through 1.</param>
    /// <remarks>Probability validity is checked by the compiler so invalid direct IR yields diagnostics.</remarks>
    [JsonConstructor]
    public BernoulliGenerationNode(double probability) => Probability = probability;

    /// <inheritdoc />
    public override TypeRef ValueType => BoolType;

    /// <summary>Gets the probability of producing <see langword="true"/>.</summary>
    public double Probability { get; }
}

/// <summary>One portable value and its relative weight in a categorical generator.</summary>
public sealed record WeightedCategoricalOption
{
    /// <summary>Creates a weighted categorical option.</summary>
    /// <param name="value">Portable value selected by this option.</param>
    /// <param name="weight">Finite positive relative weight.</param>
    /// <remarks>Weight validity is checked by the compiler so invalid direct IR yields diagnostics.</remarks>
    [JsonConstructor]
    public WeightedCategoricalOption(ObservationValue value, double weight)
    {
        Value = value;
        Weight = weight;
    }

    /// <summary>Gets the portable option value.</summary>
    public ObservationValue Value { get; }

    /// <summary>Gets the relative option weight.</summary>
    public double Weight { get; }
}

/// <summary>Produces one value from a finite weighted categorical distribution.</summary>
public sealed record WeightedCategoricalGenerationNode : ValueGeneratorNode
{
    /// <summary>Creates a weighted categorical generator.</summary>
    /// <param name="valueType">Portable semantic type shared by every option.</param>
    /// <param name="options">Finite categorical options in authoring order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="valueType"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public WeightedCategoricalGenerationNode(
        TypeRef valueType,
        ImmutableArray<WeightedCategoricalOption> options)
    {
        ValueType = Guard.RequireNotNull(valueType);
        Options = options.IsDefault ? [] : options;
    }

    /// <inheritdoc />
    public override TypeRef ValueType { get; }

    /// <summary>Gets categorical options in authoring order.</summary>
    public ImmutableArray<WeightedCategoricalOption> Options { get; }
}

/// <summary>Produces a value by evaluating a portable core expression over sampled record bindings.</summary>
/// <remarks>
/// The generation compiler declares the exact expression capabilities supported by this IR version. The initial
/// profile supports whole-binding and field-path projection. Expression callbacks and CLR metadata are not retained.
/// </remarks>
public sealed record ExpressionGenerationNode : ValueGeneratorNode
{
    /// <summary>Creates an expression-backed generator.</summary>
    /// <param name="valueType">Declared portable result type, verified against expression analysis.</param>
    /// <param name="expression">Canonical portable expression evaluated for each generated record.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="valueType"/> or <paramref name="expression"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ExpressionGenerationNode(TypeRef valueType, Expr expression)
    {
        ValueType = Guard.RequireNotNull(valueType);
        Expression = Guard.RequireNotNull(expression);
    }

    /// <inheritdoc />
    public override TypeRef ValueType { get; }

    /// <summary>Gets the canonical portable expression.</summary>
    public Expr Expression { get; }
}

/// <summary>Associates one stable semantic binding identity with a source sampled once per generated record.</summary>
public sealed record RecordGenerationBinding
{
    /// <summary>Creates a sampled record binding.</summary>
    /// <param name="identity">Stable identity used by bound expressions and entropy addressing.</param>
    /// <param name="generator">Provider-neutral source generator evaluated once per output record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RecordGenerationBinding(ValueBindingId identity, ValueGeneratorNode generator)
    {
        Identity = identity;
        Generator = Guard.RequireNotNull(generator);
    }

    /// <summary>Gets the stable semantic binding identity.</summary>
    public ValueBindingId Identity { get; }

    /// <summary>Gets the canonical source generator.</summary>
    public ValueGeneratorNode Generator { get; }
}

/// <summary>Associates one stable semantic field identity with its canonical value generator.</summary>
public sealed record RecordGenerationMember
{
    /// <summary>Creates a generated record member.</summary>
    /// <param name="identity">Stable semantic identity shared with the output shape field.</param>
    /// <param name="generator">Provider-neutral field-value generator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RecordGenerationMember(FieldName identity, ValueGeneratorNode generator)
    {
        Identity = identity;
        Generator = Guard.RequireNotNull(generator);
    }

    /// <summary>Gets the stable semantic member identity.</summary>
    public FieldName Identity { get; }

    /// <summary>Gets the canonical value generator.</summary>
    public ValueGeneratorNode Generator { get; }
}

/// <summary>Canonical composition of generated fields into one object-shaped observation value.</summary>
public sealed record RecordGenerationNode
{
    /// <summary>Creates a record generator.</summary>
    /// <param name="shapeId">Stable identity of the output shape.</param>
    /// <param name="bindings">Record sources sampled once per output record. Declaration order is non-semantic.</param>
    /// <param name="members">Generated members. Declaration order is non-semantic.</param>
    [JsonConstructor]
    public RecordGenerationNode(
        ShapeId shapeId,
        ImmutableArray<RecordGenerationBinding> bindings,
        ImmutableArray<RecordGenerationMember> members)
    {
        ShapeId = shapeId;
        Bindings = bindings.IsDefault ? [] : bindings;
        Members = members.IsDefault ? [] : members;
    }

    /// <summary>Creates a record generator without sampled record bindings.</summary>
    /// <param name="shapeId">Stable identity of the output shape.</param>
    /// <param name="members">Generated members. Declaration order is non-semantic.</param>
    public RecordGenerationNode(ShapeId shapeId, ImmutableArray<RecordGenerationMember> members)
        : this(shapeId, bindings: [], members)
    {
    }

    /// <summary>Gets the stable output-shape identity.</summary>
    public ShapeId ShapeId { get; }

    /// <summary>Gets sampled record bindings in authoring order.</summary>
    public ImmutableArray<RecordGenerationBinding> Bindings { get; }

    /// <summary>Gets generated members in authoring order.</summary>
    public ImmutableArray<RecordGenerationMember> Members { get; }
}

/// <summary>Portable semantic authority for one deterministic record-generation definition.</summary>
public sealed record GenerationDefinition
{
    /// <summary>Creates a generation definition.</summary>
    /// <param name="id">Stable logical definition identity.</param>
    /// <param name="revision">Exact authored definition revision.</param>
    /// <param name="shapeGraph">Exact semantic graph governing generated observations.</param>
    /// <param name="root">Canonical record generator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shapeGraph"/> or <paramref name="root"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="revision"/> is empty.</exception>
    [JsonConstructor]
    public GenerationDefinition(
        string id,
        string revision,
        ShapeGraph shapeGraph,
        RecordGenerationNode root)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        ShapeGraph = Guard.RequireNotNull(shapeGraph);
        Root = Guard.RequireNotNull(root);
    }

    /// <summary>Gets the stable logical definition identity.</summary>
    public string Id { get; }

    /// <summary>Gets the exact authored revision.</summary>
    public string Revision { get; }

    /// <summary>Gets the exact semantic graph governing generated observations.</summary>
    public ShapeGraph ShapeGraph { get; }

    /// <summary>Gets the canonical record generator.</summary>
    public RecordGenerationNode Root { get; }
}
