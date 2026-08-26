using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation;

/// <summary>Entry points for typed authoring of canonical generation definitions.</summary>
public static class Simulation
{
    /// <summary>Defines deterministic generation for an ordinary CLR object type.</summary>
    /// <typeparam name="T">Mutable class, immutable record, or explicitly materializable CLR target type.</typeparam>
    /// <param name="configure">Authoring callback that explicitly binds generated semantic members.</param>
    /// <returns>
    /// A typed authoring projection containing provider-neutral generator IR and an exact output shape graph.
    /// </returns>
    /// <remarks>
    /// The callback is executed immediately and does not survive into canonical IR. CLR property names, including
    /// explicit <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/> values, supply deterministic
    /// semantic member identities by convention.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    public static PocoGenerationDefinition<T> Define<T>(Action<PocoGeneratorBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        PocoGeneratorBuilder<T> builder = new();
        configure(builder);
        return builder.Build();
    }
}

/// <summary>Typed authoring projection of one provider-neutral field generator.</summary>
/// <typeparam name="TValue">CLR value type accepted by a matching member binding.</typeparam>
public sealed class Generator<TValue>
{
    internal Generator(ValueGeneratorNode node) => Node = Guard.RequireNotNull(node);

    /// <summary>Gets the canonical provider-neutral generator node.</summary>
    public ValueGeneratorNode Node { get; }
}

/// <summary>One typed categorical value and its relative weight.</summary>
/// <typeparam name="TValue">CLR value type projected into portable generation IR.</typeparam>
public sealed record WeightedValue<TValue>
{
    /// <summary>Creates a typed weighted value.</summary>
    /// <param name="value">CLR value projected into one portable categorical option.</param>
    /// <param name="weight">Finite positive relative weight, validated during compilation.</param>
    public WeightedValue(TValue value, double weight)
    {
        Value = value;
        Weight = weight;
    }

    /// <summary>Gets the categorical CLR value.</summary>
    public TValue Value { get; }

    /// <summary>Gets the relative option weight.</summary>
    public double Weight { get; }
}

/// <summary>Typed factories that lower common generation declarations into canonical generator nodes.</summary>
public static class Gen
{
    static readonly DefaultClrTypeRefMapper TypeMapper = new();

    /// <summary>Creates a constant generator from one portable CLR value.</summary>
    /// <typeparam name="TValue">CLR value type.</typeparam>
    /// <param name="value">Value to project and emit exactly.</param>
    /// <returns>A typed authoring projection over a canonical constant node.</returns>
    /// <exception cref="NotSupportedException"><paramref name="value"/> has no portable observation projection.</exception>
    public static Generator<TValue> Constant<TValue>(TValue value) => new(
        new ConstantGenerationNode(
            valueType: TypeMapper.Map(typeof(TValue), nullability: null),
            value: ObservationValue.FromObject(value)));

    /// <summary>Creates a uniform inclusive Int32 generator.</summary>
    /// <param name="minimum">Inclusive minimum value.</param>
    /// <param name="maximum">Inclusive maximum value.</param>
    /// <returns>A typed authoring projection over a canonical bounded-integer node.</returns>
    public static Generator<int> Int32(int minimum, int maximum) =>
        new(new Int32GenerationNode(minimum, maximum));

    /// <summary>Creates a Bernoulli Boolean generator.</summary>
    /// <param name="probability">Finite probability of <see langword="true"/> from 0 through 1.</param>
    /// <returns>A typed authoring projection over a canonical Bernoulli node.</returns>
    public static Generator<bool> Bernoulli(double probability) =>
        new(new BernoulliGenerationNode(probability));

    /// <summary>Creates one typed weighted categorical option.</summary>
    /// <typeparam name="TValue">CLR option value type.</typeparam>
    /// <param name="value">CLR option value.</param>
    /// <param name="weight">Finite positive relative weight, validated during compilation.</param>
    /// <returns>A typed option accepted by <see cref="Categorical{TValue}(WeightedValue{TValue}[])"/>.</returns>
    public static WeightedValue<TValue> Weighted<TValue>(TValue value, double weight) => new(value, weight);

    /// <summary>Creates a finite weighted categorical generator.</summary>
    /// <typeparam name="TValue">CLR value type shared by every option.</typeparam>
    /// <param name="options">Weighted options. The array is snapshotted into canonical IR.</param>
    /// <returns>A typed authoring projection over a canonical categorical node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">An option value has no portable observation projection.</exception>
    public static Generator<TValue> Categorical<TValue>(params WeightedValue<TValue>[] options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var canonicalOptions = ImmutableArray.CreateBuilder<WeightedCategoricalOption>(options.Length);
        foreach (var option in options)
        {
            if (option is null)
            {
                canonicalOptions.Add(null!);
                continue;
            }

            canonicalOptions.Add(new(
                value: ObservationValue.FromObject(option.Value),
                weight: option.Weight));
        }

        return new(new WeightedCategoricalGenerationNode(
            valueType: TypeMapper.Map(typeof(TValue), nullability: null),
            options: canonicalOptions.MoveToImmutable()));
    }
}

/// <summary>Fluent typed producer of one canonical record-generation definition.</summary>
/// <typeparam name="T">CLR type whose readable properties are bound explicitly.</typeparam>
/// <remarks>The builder is mutable and intended for one single-threaded authoring callback.</remarks>
public sealed class PocoGeneratorBuilder<T>
{
    readonly List<PocoMemberBinding> bindings = [];

    /// <summary>Creates an empty typed generator builder.</summary>
    public PocoGeneratorBuilder()
    {
    }

    /// <summary>Binds one direct CLR property to a typed canonical generator.</summary>
    /// <typeparam name="TValue">Selected property and generator value type.</typeparam>
    /// <param name="member">Direct property selector, such as <c>value =&gt; value.Name</c>.</param>
    /// <param name="generator">Typed generator for the selected property.</param>
    /// <returns>This builder for continued fluent authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> or <paramref name="generator"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="member"/> is not a direct readable property selector.</exception>
    public PocoGeneratorBuilder<T> Member<TValue>(
        Expression<Func<T, TValue>> member,
        Generator<TValue> generator)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(generator);
        var property = ResolveProperty(member);
        var identity = DefaultClrTypeRefMapper.GetSerializedMemberName(property);
        bindings.Add(new(property, new(identity), generator.Node));
        return this;
    }

    internal PocoGenerationDefinition<T> Build()
    {
        var shapeId = ClrShapeIdentityConvention.GetShapeId(typeof(T));
        var members = bindings
            .Select(static binding => new RecordGenerationMember(binding.Identity, binding.Generator))
            .ToImmutableArray();
        var root = new RecordGenerationNode(shapeId, members);

        Dictionary<string, FieldDefinition> fieldsByIdentity = new(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            fieldsByIdentity.TryAdd(
                binding.Identity.Value,
                new FieldDefinition(
                    name: binding.Identity,
                    type: binding.Generator.ValueType,
                    cardinality: FieldCardinality.Single,
                    presence: FieldPresence.Required,
                    nullability: FieldNullability.NonNullable));
        }

        var shape = new Shape(
            shapeId,
            [.. fieldsByIdentity.Values.OrderBy(static field => field.Name.Value, StringComparer.Ordinal)]);
        var shapeFingerprint = GenerationCanonicalizer.ComputeShapeFingerprint(shapeId, members);
        var clrTypeId = ClrShapeIdentityConvention.GetTypeId(typeof(T)).Value;
        var graph = new ShapeGraph(
            new GraphId($"simulation:shape:{clrTypeId}:{shapeFingerprint}"),
            [shape]);
        var definitionId = $"simulation:definition:{clrTypeId}";
        var provisional = new GenerationDefinition(
            id: definitionId,
            revision: "content-addressed",
            shapeGraph: graph,
            root: root);
        var revision = GenerationCanonicalizer.ComputeDefinitionFingerprint(provisional);
        var definition = new GenerationDefinition(
            id: definitionId,
            revision: revision,
            shapeGraph: graph,
            root: root);
        return new(definition, [.. bindings], explicitMaterializer: null);
    }

    static PropertyInfo ResolveProperty<TValue>(Expression<Func<T, TValue>> selector)
    {
        Expression body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
            body = conversion.Operand;

        if (body is not MemberExpression
            {
                Member: PropertyInfo property,
                Expression: ParameterExpression parameter
            }
            || parameter != selector.Parameters[0]
            || property.GetMethod is null
            || property.GetMethod.IsStatic
            || property.GetIndexParameters().Length != 0)
        {
            throw new ArgumentException(
                "A generated member selector must be a direct readable instance property access.",
                nameof(selector));
        }

        return property;
    }
}

/// <summary>Typed authoring projection that composes canonical generation with core CLR materialization.</summary>
/// <typeparam name="T">CLR target type.</typeparam>
public sealed class PocoGenerationDefinition<T>
{
    readonly ImmutableArray<PocoMemberBinding> bindings;
    readonly ObservationMaterializer<T>? explicitMaterializer;

    internal PocoGenerationDefinition(
        GenerationDefinition definition,
        ImmutableArray<PocoMemberBinding> bindings,
        ObservationMaterializer<T>? explicitMaterializer)
    {
        Definition = definition;
        this.bindings = bindings;
        this.explicitMaterializer = explicitMaterializer;
    }

    /// <summary>Gets the canonical provider-neutral generation definition.</summary>
    public GenerationDefinition Definition { get; }

    /// <summary>Gets the exact graph-scoped output shape.</summary>
    public GraphShapeId OutputShape => new(Definition.ShapeGraph, Definition.Root.ShapeId);

    /// <summary>Returns a definition using an explicitly compiled core observation materializer.</summary>
    /// <param name="materializer">Materializer for custom constructors, mappings, converters, or value objects.</param>
    /// <returns>A new typed definition retaining the same canonical generator IR.</returns>
    /// <remarks>
    /// The materializer remains a local CLR interpretation and does not enter canonical generator IR. Its qualified
    /// shape identity is verified during compilation.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="materializer"/> is <see langword="null"/>.</exception>
    public PocoGenerationDefinition<T> WithMaterializer(ObservationMaterializer<T> materializer) =>
        new(Definition, bindings, Guard.RequireNotNull(materializer));

    /// <summary>Attempts typed compilation and retains all structured diagnostics.</summary>
    /// <returns>A result containing a compiled typed generator only when every invariant is satisfied.</returns>
    public PocoGenerationCompilationResult<T> CompileResult()
    {
        var canonical = GenerationCompiler.Compile(Definition);
        List<DocumentValidationDiagnostic> diagnostics = [.. canonical.Validation.Diagnostics];
        ValidateBindings(diagnostics);

        ObservationMaterializer<T>? materializer = explicitMaterializer;
        if (materializer is not null && materializer.ShapeId != OutputShape.QualifiedId)
        {
            diagnostics.Add(Diagnostic(
                code: "simulation.generation.materializerShapeMismatch",
                message: $"Materializer shape '{materializer.ShapeId}' does not match generated shape '{OutputShape.QualifiedId}'.",
                location: "/materializer/shapeId"));
            materializer = null;
        }

        if (materializer is null && !HasErrors(diagnostics))
        {
            try
            {
                materializer = ObservationMaterializer.For<T>(OutputShape)
                    .MapAll(DefaultClrTypeRefMapper.GetSerializedMemberName)
                    .WithMissingFieldBehavior(ObservationMissingFieldBehavior.Throw)
                    .Compile();
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or NotSupportedException)
            {
                diagnostics.Add(Diagnostic(
                    code: "simulation.generation.materializerUnsupported",
                    message: $"CLR type '{typeof(T).FullName}' cannot be materialized: {exception.Message}",
                    location: "/materializer"));
            }
        }

        var validation = new DocumentValidationResult(
            DocumentValidationDiagnostics.Normalize([.. diagnostics]));
        var compiled = canonical.Plan is not null && validation.IsValid && materializer is not null
            ? new CompiledPocoGenerator<T>(canonical.Plan, materializer)
            : null;
        return new(Definition, compiled, validation);
    }

    /// <summary>Compiles this typed definition or throws with structured diagnostics.</summary>
    /// <returns>An immutable reusable deterministic POCO generator.</returns>
    /// <exception cref="GenerationCompilationException">Generation or CLR materialization validation fails.</exception>
    public CompiledPocoGenerator<T> Compile()
    {
        var result = CompileResult();
        return result.Generator ?? throw new GenerationCompilationException(result.Validation);
    }

    void ValidateBindings(ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var readable = ShapeTypeInspector.GetReadableProperties<T>();
        foreach (var property in readable)
        {
            var count = bindings.Count(binding => ShapeTypeInspector.IsSameProperty(binding.Property, property));
            if (count == 1)
                continue;

            var identity = DefaultClrTypeRefMapper.GetSerializedMemberName(property);
            diagnostics.Add(Diagnostic(
                code: count == 0
                    ? "simulation.generation.clrBindingMissing"
                    : "simulation.generation.clrBindingDuplicate",
                message: count == 0
                    ? $"Readable CLR property '{property.Name}' has no generated member binding."
                    : $"Readable CLR property '{property.Name}' is bound more than once.",
                location: $"/clrBindings/{identity}"));
        }
    }

    static bool HasErrors(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    static DocumentValidationDiagnostic Diagnostic(string code, string message, string location) => new(
        Code: code,
        Severity: DiagnosticSeverity.Error,
        Message: message,
        Location: location,
        Evidence: new(stage: "poco-generation-compilation"));
}

/// <summary>Result of attempting typed POCO-generation compilation.</summary>
/// <typeparam name="T">CLR target type.</typeparam>
public sealed class PocoGenerationCompilationResult<T>
{
    internal PocoGenerationCompilationResult(
        GenerationDefinition definition,
        CompiledPocoGenerator<T>? generator,
        DocumentValidationResult validation)
    {
        Definition = definition;
        Generator = generator;
        Validation = validation;
    }

    /// <summary>Gets the exact canonical generation definition.</summary>
    public GenerationDefinition Definition { get; }

    /// <summary>Gets the compiled typed generator only when validation succeeds.</summary>
    public CompiledPocoGenerator<T>? Generator { get; }

    /// <summary>Gets deterministic structured generation and materialization diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Gets whether compilation produced a complete typed generator.</summary>
    public bool IsSuccessful => Generator is not null && Validation.IsValid;
}

/// <summary>Failure raised when a convenience compilation call encounters structured diagnostics.</summary>
public sealed class GenerationCompilationException : InvalidOperationException
{
    /// <summary>Creates a generation compilation exception.</summary>
    /// <param name="validation">Structured validation evidence explaining the failure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    public GenerationCompilationException(DocumentValidationResult validation)
        : base(CreateMessage(validation)) => Validation = validation;

    /// <summary>Gets structured validation evidence explaining the failure.</summary>
    public DocumentValidationResult Validation { get; }

    static string CreateMessage(DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var errors = validation.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}");
        return $"Generation definition could not be compiled: {string.Join(" | ", errors)}";
    }
}

/// <summary>Immutable reusable deterministic generator for one CLR target type.</summary>
/// <typeparam name="T">CLR target type.</typeparam>
public sealed class CompiledPocoGenerator<T>
{
    internal CompiledPocoGenerator(
        CompiledGenerationPlan plan,
        ObservationMaterializer<T> materializer)
    {
        Plan = plan;
        Materializer = materializer;
    }

    /// <summary>Gets the validated provider-neutral generation plan.</summary>
    public CompiledGenerationPlan Plan { get; }

    /// <summary>Gets the shared compiled core observation materializer.</summary>
    public ObservationMaterializer<T> Materializer { get; }

    /// <summary>Generates one deterministic CLR value at sequence index zero.</summary>
    /// <param name="seed">Caller-supplied root seed.</param>
    /// <returns>The CLR value, its authoritative generated observation, and replay evidence.</returns>
    public Generated<T> Generate(long seed) => Generate(seed, sequenceIndex: 0);

    /// <summary>Generates one deterministic CLR value at an explicit sequence address.</summary>
    /// <param name="seed">Caller-supplied root seed.</param>
    /// <param name="sequenceIndex">Stable zero-based sequence item index.</param>
    /// <returns>The CLR value, its authoritative generated observation, and replay evidence.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is negative.</exception>
    public Generated<T> Generate(long seed, long sequenceIndex)
    {
        var generated = ReferenceGenerationInterpreter.Generate(Plan, seed, sequenceIndex);
        return new(
            value: Materializer.Materialize(generated.Observation),
            observation: generated.Observation,
            replay: generated.Replay);
    }

    /// <summary>Generates an eagerly materialized bounded sequence addressed by item index.</summary>
    /// <param name="seed">Caller-supplied root seed shared by the sequence.</param>
    /// <param name="count">Number of items to generate.</param>
    /// <returns>Generated CLR values in ascending zero-based sequence-index order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public ImmutableArray<Generated<T>> GenerateSequence(long seed, int count)
    {
        var observations = ReferenceGenerationInterpreter.GenerateSequence(Plan, seed, count);
        if (observations.IsDefaultOrEmpty)
            return [];

        var generated = ImmutableArray.CreateBuilder<Generated<T>>(observations.Length);
        foreach (var item in observations)
        {
            generated.Add(new(
                value: Materializer.Materialize(item.Observation),
                observation: item.Observation,
                replay: item.Replay));
        }

        return generated.MoveToImmutable();
    }
}

/// <summary>One generated CLR value, its core observation authority, and separate replay evidence.</summary>
/// <typeparam name="T">CLR value type.</typeparam>
public sealed record Generated<T>
{
    /// <summary>Creates a generated typed result.</summary>
    /// <param name="value">Materialized CLR interpretation.</param>
    /// <param name="observation">Authoritative generated identity-free core observation.</param>
    /// <param name="replay">Replay evidence for this exact generated item.</param>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> or <paramref name="replay"/> is null.</exception>
    public Generated(T value, Observation observation, GenerationReplayEvidence replay)
    {
        Value = value;
        Observation = Guard.RequireNotNull(observation);
        Replay = Guard.RequireNotNull(replay);
    }

    /// <summary>Gets the materialized CLR interpretation.</summary>
    public T Value { get; }

    /// <summary>Gets the authoritative generated identity-free core observation.</summary>
    public Observation Observation { get; }

    /// <summary>Gets replay evidence kept outside observation semantics.</summary>
    public GenerationReplayEvidence Replay { get; }
}

sealed record PocoMemberBinding(
    PropertyInfo Property,
    FieldName Identity,
    ValueGeneratorNode Generator);
