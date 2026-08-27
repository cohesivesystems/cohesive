using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Model;
using CoreObservation = Cohesive.Model.Observation;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Representative value-tree shapes used to track observation creation and validation costs.</summary>
public enum ObservationBenchmarkScenario
{
    /// <summary>A flat object containing sixteen scalar fields.</summary>
    FlatScalar,

    /// <summary>An object containing a sixteen-field nested object.</summary>
    NestedObject,

    /// <summary>An object containing an array of sixty-four scalar values.</summary>
    ArrayHeavy
}

/// <summary>Creation and validation throughput for canonical identity-free observations.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ObservationCreationBenchmarks
{
    ObservationCreationFixture fixture = null!;

    /// <summary>Gets or sets the representative observation value-tree shape.</summary>
    [Params(
        ObservationBenchmarkScenario.FlatScalar,
        ObservationBenchmarkScenario.NestedObject,
        ObservationBenchmarkScenario.ArrayHeavy)]
    public ObservationBenchmarkScenario Scenario { get; set; }

    /// <summary>Builds immutable and caller-owned inputs outside the timed operations.</summary>
    [GlobalSetup]
    public void Setup() => fixture = ObservationCreationFixture.Create(Scenario);

    /// <summary>Creates an observation from an already-owned immutable object value.</summary>
    /// <returns>The validated observation.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Observation", "Creation")]
    public CoreObservation CreateFromImmutableValue() =>
        CoreObservation.Create(fixture.Shape, fixture.Value);

    /// <summary>Creates an observation while snapshotting a caller-owned mutable field dictionary.</summary>
    /// <returns>The validated observation.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "Creation")]
    public CoreObservation CreateFromMutableFields() =>
        CoreObservation.Create(fixture.Shape, fixture.MutableFields);

    /// <summary>Validates an already-owned immutable value without constructing an observation.</summary>
    /// <returns><see langword="true"/> when the representative value remains valid.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "Validation")]
    public bool ValidateAgainstShape() =>
        ObservationValidator.TryValidateAgainstShape(
            value: fixture.Value,
            shape: fixture.Definition,
            validationError: out _,
            graph: fixture.Shape.Graph);

    /// <summary>Measures deterministic diagnostic production independently from successful validation.</summary>
    /// <returns>The validation diagnostic for the invalid representative value.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "ValidationDiagnostic")]
    public string? ValidateInvalidValue()
    {
        _ = ObservationValidator.TryValidateAgainstShape(
            value: fixture.InvalidValue,
            shape: fixture.Definition,
            validationError: out var validationError,
            graph: fixture.Shape.Graph);
        return validationError;
    }
}

/// <summary>Warm CLR materialization and canonical JSON serialization throughput for observations.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ObservationProjectionBenchmarks
{
    CoreObservation observation = null!;
    ObservationMaterializer<ObservationBenchmarkState> materializer = null!;

    /// <summary>Creates and validates a representative state value and warms both materializer paths.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var fixture = ObservationProjectionFixture.Create();
        observation = fixture.Observation;
        materializer = fixture.Materializer;

        var compiled = materializer.Materialize(observation);
        var cachedDefault = observation.Materialize<ObservationBenchmarkState>();
        if (!MatchesExpected(compiled, fixture.Expected)
            || !MatchesExpected(cachedDefault, fixture.Expected))
        {
            throw new InvalidOperationException("Observation benchmark materialization produced an unexpected value.");
        }
    }

    /// <summary>Materializes state through a precompiled reusable materializer.</summary>
    /// <returns>The materialized CLR state.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Observation", "ClrMaterialization")]
    public ObservationBenchmarkState MaterializeWithCompiledPlan() =>
        materializer.Materialize(observation);

    /// <summary>Materializes state through the process-wide default materializer cache.</summary>
    /// <returns>The materialized CLR state.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "ClrMaterialization")]
    public ObservationBenchmarkState MaterializeWithDefaultCachedPlan() =>
        observation.Materialize<ObservationBenchmarkState>();

    /// <summary>Serializes state to the canonical portable UTF-8 representation.</summary>
    /// <returns>A newly allocated canonical UTF-8 payload.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Observation", "JsonSerialization")]
    public byte[] ToCanonicalJsonUtf8() => observation.ToCanonicalJsonUtf8();

    /// <summary>Serializes state to the canonical portable JSON string representation.</summary>
    /// <returns>A newly allocated canonical JSON string.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "JsonSerialization")]
    public string ToCanonicalJsonString() => observation.ToCanonicalJson();

    static bool MatchesExpected(
        ObservationBenchmarkState actual,
        ObservationBenchmarkState expected) =>
        actual.Id == expected.Id
        && actual.Version == expected.Version
        && actual.Name == expected.Name
        && actual.Enabled == expected.Enabled
        && actual.Balance == expected.Balance
        && actual.Address == expected.Address
        && actual.Tags.SequenceEqual(expected.Tags, StringComparer.Ordinal);
}

/// <summary>A representative CLR projection of canonical application state.</summary>
/// <param name="Id">Stable state identity.</param>
/// <param name="Version">State version.</param>
/// <param name="Name">Display name.</param>
/// <param name="Enabled">Whether the state is enabled.</param>
/// <param name="Balance">Current balance.</param>
/// <param name="Address">Nested address state.</param>
/// <param name="Tags">State classification tags.</param>
public sealed record ObservationBenchmarkState(
    string Id,
    long Version,
    string Name,
    bool Enabled,
    decimal Balance,
    ObservationBenchmarkAddress Address,
    string[] Tags);

/// <summary>A representative nested CLR value projected from observation state.</summary>
/// <param name="City">Address city.</param>
/// <param name="PostalCode">Address postal code.</param>
public sealed record ObservationBenchmarkAddress(string City, string PostalCode);

sealed record ObservationCreationFixture(
    GraphShapeId Shape,
    Shape Definition,
    ObservationValue Value,
    ObservationValue InvalidValue,
    IReadOnlyDictionary<string, ObservationValue> MutableFields)
{
    public static ObservationCreationFixture Create(ObservationBenchmarkScenario scenario)
    {
        var (definition, fields) = scenario switch
        {
            ObservationBenchmarkScenario.FlatScalar => CreateFlatScalar(),
            ObservationBenchmarkScenario.NestedObject => CreateNestedObject(),
            ObservationBenchmarkScenario.ArrayHeavy => CreateArrayHeavy(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
        ShapeGraph graph = new(new($"observation-benchmark-{scenario}"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var value = ObservationValue.FromObject(fields);
        var mutableFields = fields.ToDictionary(
            static field => field.Key,
            static field => field.Value,
            StringComparer.Ordinal);
        var invalidFields = fields.SetItem("unknown_field", ObservationValue.FromBool(true));
        var invalidValue = ObservationValue.FromObject(invalidFields);

        if (!ObservationValidator.TryValidateAgainstShape(
                value,
                definition,
                out var validationError,
                graph))
        {
            throw new InvalidOperationException(
                $"Observation benchmark fixture '{scenario}' is invalid: {validationError}");
        }

        if (ObservationValidator.TryValidateAgainstShape(
                invalidValue,
                definition,
                out _,
                graph))
        {
            throw new InvalidOperationException(
                $"Observation benchmark fixture '{scenario}' must retain an invalid diagnostic value.");
        }

        return new(shape, definition, value, invalidValue, mutableFields);
    }

    static (Shape Definition, ImmutableDictionary<string, ObservationValue> Fields) CreateFlatScalar()
    {
        const int FieldCount = 16;
        var definitions = ImmutableArray.CreateBuilder<FieldDefinition>(FieldCount);
        var values = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < FieldCount; ordinal++)
        {
            var fieldName = $"field_{ordinal:D2}";
            definitions.Add(new(new(fieldName), new ScalarTypeRef(ScalarTypeKind.String)));
            values.Add(fieldName, ObservationValue.FromString($"value-{ordinal:D2}"));
        }

        return (
            new(new("flat-scalar-state"), definitions.MoveToImmutable()),
            values.ToImmutable());
    }

    static (Shape Definition, ImmutableDictionary<string, ObservationValue> Fields) CreateNestedObject()
    {
        const int NestedFieldCount = 16;
        var definitions = ImmutableArray.CreateBuilder<ObjectFieldTypeDef>(NestedFieldCount);
        var nestedValues = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < NestedFieldCount; ordinal++)
        {
            var fieldName = $"nested_{ordinal:D2}";
            definitions.Add(new(fieldName, new ScalarTypeRef(ScalarTypeKind.String)));
            nestedValues.Add(fieldName, ObservationValue.FromString($"value-{ordinal:D2}"));
        }

        Shape definition = new(
            new("nested-object-state"),
            [
                new(new("id"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("payload"), new ObjectTypeRef(definitions.MoveToImmutable()))
            ]);
        var values = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        values.Add("id", ObservationValue.FromString("state-42"));
        values.Add("payload", ObservationValue.FromObject(nestedValues.ToImmutable()));
        return (definition, values.ToImmutable());
    }

    static (Shape Definition, ImmutableDictionary<string, ObservationValue> Fields) CreateArrayHeavy()
    {
        Shape definition = new(
            new("array-heavy-state"),
            [
                new(new("id"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(
                    new("items"),
                    new ScalarTypeRef(ScalarTypeKind.String),
                    cardinality: FieldCardinality.Many)
            ]);
        var items = new ObservationValue[64];
        for (var index = 0; index < items.Length; index++)
            items[index] = ObservationValue.FromString($"item-{index:D2}");

        var values = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        values.Add("id", ObservationValue.FromString("state-42"));
        values.Add("items", ObservationValue.FromArray(items));
        return (definition, values.ToImmutable());
    }
}

sealed record ObservationProjectionFixture(
    CoreObservation Observation,
    ObservationMaterializer<ObservationBenchmarkState> Materializer,
    ObservationBenchmarkState Expected)
{
    public static ObservationProjectionFixture Create()
    {
        Shape definition = new(
            new("application-state"),
            [
                new(new("Id"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("Version"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                new(new("Name"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("Enabled"), new ScalarTypeRef(ScalarTypeKind.Bool)),
                new(new("Balance"), new ScalarTypeRef(ScalarTypeKind.Decimal)),
                new(
                    new("Address"),
                    new ObjectTypeRef(
                    [
                        new("City", new ScalarTypeRef(ScalarTypeKind.String)),
                        new("PostalCode", new ScalarTypeRef(ScalarTypeKind.String))
                    ])),
                new(
                    new("Tags"),
                    new ScalarTypeRef(ScalarTypeKind.String),
                    cardinality: FieldCardinality.Many)
            ]);
        ShapeGraph graph = new(new("observation-projection-benchmark-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var addressValues = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        addressValues.Add("City", ObservationValue.FromString("Seattle"));
        addressValues.Add("PostalCode", ObservationValue.FromString("98101"));
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("Id", ObservationValue.FromString("state-42"));
        fields.Add("Version", ObservationValue.FromInt64(17));
        fields.Add("Name", ObservationValue.FromString("Primary account"));
        fields.Add("Enabled", ObservationValue.FromBool(true));
        fields.Add("Balance", ObservationValue.FromDecimal(1250.75m));
        fields.Add("Address", ObservationValue.FromObject(addressValues.ToImmutable()));
        fields.Add("Tags", ObservationValue.FromArray(
        [
            ObservationValue.FromString("priority"),
            ObservationValue.FromString("west")
        ]));
        var observation = CoreObservation.Create(shape, ObservationValue.FromObject(fields.ToImmutable()));
        var materializer = ObservationMaterializer.For<ObservationBenchmarkState>(shape).Compile();
        var expected = new ObservationBenchmarkState(
            Id: "state-42",
            Version: 17,
            Name: "Primary account",
            Enabled: true,
            Balance: 1250.75m,
            Address: new("Seattle", "98101"),
            Tags: ["priority", "west"]);
        return new(observation, materializer, expected);
    }
}
