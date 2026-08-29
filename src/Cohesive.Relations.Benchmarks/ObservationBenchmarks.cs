using System.Buffers;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Model;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
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
    IndexedObservationOccurrence indexedObservation = null!;
    ObservationMaterializer<ObservationBenchmarkState> materializer = null!;
    ObservationMaterializer<ObservationBenchmarkState> ordinalMaterializer = null!;
    ArrayBufferWriter<byte> canonicalJsonOutput = null!;

    /// <summary>Creates and validates a representative state value and warms both materializer paths.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var fixture = ObservationProjectionFixture.Create();
        observation = fixture.Observation;
        materializer = fixture.Materializer;
        var layout = ObservationLayout.Create(fixture.Shape);
        ordinalMaterializer = ObservationMaterializer
            .For<ObservationBenchmarkState>(fixture.Shape)
            .Compile(layout);
        indexedObservation = IndexedObservationOccurrence.FromObservation(
            fixture.Shape,
            new(
                new("observation-projection-benchmark/0"),
                new("observation-projection-benchmark"),
                fixture.Shape.QualifiedId,
                "observation-projection-benchmark/0"),
            observation,
            layout);
        var expectedCanonicalJson = observation.ToCanonicalJsonUtf8();
        canonicalJsonOutput = new(initialCapacity: expectedCanonicalJson.Length);
        observation.WriteCanonicalJson(canonicalJsonOutput);

        var handwritten = MaterializeHandwritten();
        var compiled = materializer.Materialize(observation);
        var indexed = materializer.Materialize(indexedObservation);
        var indexedByOrdinal = ordinalMaterializer.Materialize(indexedObservation);
        var cachedDefault = observation.Materialize<ObservationBenchmarkState>();
        if (!MatchesExpected(handwritten, fixture.Expected)
            || !MatchesExpected(compiled, fixture.Expected)
            || !MatchesExpected(indexed, fixture.Expected)
            || !MatchesExpected(indexedByOrdinal, fixture.Expected)
            || !MatchesExpected(cachedDefault, fixture.Expected)
            || !canonicalJsonOutput.WrittenSpan.SequenceEqual(expectedCanonicalJson))
        {
            throw new InvalidOperationException("Observation benchmark projection produced an unexpected value.");
        }
    }

    /// <summary>Materializes state with direct handwritten reads as the destination-allocation lower bound.</summary>
    /// <returns>The materialized CLR state.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Observation", "ClrMaterialization")]
    public ObservationBenchmarkState MaterializeHandwritten()
    {
        var fields = observation.Fields;
        var address = fields["Address"].Fields!;
        var observedTags = fields["Tags"].EnumerateArray();
        var tags = new string[observedTags.Length];
        for (var index = 0; index < tags.Length; index++)
        {
            tags[index] = observedTags[index].GetString()!;
        }

        return new(
            fields["Id"].GetString()!,
            fields["Version"].GetInt64(),
            fields["Name"].GetString()!,
            fields["Enabled"].GetBoolean(),
            fields["Balance"].GetDecimal(),
            new(address["City"].GetString()!, address["PostalCode"].GetString()!),
            tags);
    }

    /// <summary>Materializes state through a precompiled reusable materializer.</summary>
    /// <returns>The materialized CLR state.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "ClrMaterialization")]
    public ObservationBenchmarkState MaterializeWithCompiledPlan() =>
        materializer.Materialize(observation);

    /// <summary>Materializes state through the same string-bound plan over an indexed physical occurrence.</summary>
    /// <returns>The materialized CLR state.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "ClrMaterialization")]
    public ObservationBenchmarkState MaterializeIndexedWithStringPlan() =>
        materializer.Materialize(indexedObservation);

    /// <summary>Materializes state through a plan prebound to the indexed occurrence's shared layout.</summary>
    /// <returns>The materialized CLR state.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "ClrMaterialization")]
    public ObservationBenchmarkState MaterializeIndexedWithOrdinalPlan() =>
        ordinalMaterializer.Materialize(indexedObservation);

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

    /// <summary>Writes canonical portable UTF-8 into reusable caller-owned storage.</summary>
    /// <returns>The number of canonical UTF-8 bytes written.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "JsonSerialization")]
    public int WriteCanonicalJsonToCallerOwnedBuffer()
    {
        canonicalJsonOutput.Clear();
        observation.WriteCanonicalJson(canonicalJsonOutput);
        return canonicalJsonOutput.WrittenCount;
    }

    /// <summary>Computes the canonical observation fingerprint without materializing the JSON payload.</summary>
    /// <returns>The versioned canonical fingerprint.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "JsonFingerprint")]
    public ObservationFingerprint ComputeCanonicalFingerprint() => observation.ComputeFingerprint();

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

/// <summary>Repeated top-level field reads through semantic, indexed-name, and prebound-ordinal paths.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ObservationFieldAccessBenchmarks
{
    CoreObservation observation = null!;
    IndexedObservationOccurrence indexedObservation = null!;
    string[] fieldNames = null!;

    /// <summary>Number of present scalar fields read per benchmark operation.</summary>
    [Params(4, 16, 64)]
    public int FieldCount { get; set; }

    /// <summary>Builds one semantic observation and one equivalent indexed occurrence outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var fixture = ObservationFieldAccessFixture.Create(FieldCount);
        observation = fixture.Observation;
        indexedObservation = fixture.IndexedObservation;
        fieldNames = fixture.FieldNames;

        var expected = checked((long)(FieldCount - 1) * FieldCount / 2);
        if (ReadSemanticByName() != expected
            || ReadIndexedByName() != expected
            || ReadIndexedByOrdinal() != expected)
        {
            throw new InvalidOperationException("Observation field-access benchmark produced an unexpected sum.");
        }
    }

    /// <summary>Reads every field through the semantic object dictionary.</summary>
    /// <returns>Sum of all scalar values.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Observation", "FieldAccess")]
    public long ReadSemanticByName()
    {
        long sum = 0;
        foreach (var fieldName in fieldNames)
        {
            if (!observation.TryGetField(fieldName, out var value))
                throw new InvalidOperationException($"Semantic observation omitted field '{fieldName}'.");
            sum += value.Int64;
        }
        return sum;
    }

    /// <summary>Reads every field through the physical layout's name-to-ordinal index.</summary>
    /// <returns>Sum of all scalar values.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "FieldAccess")]
    public long ReadIndexedByName()
    {
        long sum = 0;
        foreach (var fieldName in fieldNames)
        {
            if (!indexedObservation.TryGetField(fieldName, out var value))
                throw new InvalidOperationException($"Indexed observation omitted field '{fieldName}'.");
            sum += value.Int64;
        }
        return sum;
    }

    /// <summary>Reads every field through prebound physical ordinals.</summary>
    /// <returns>Sum of all scalar values.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "FieldAccess")]
    public long ReadIndexedByOrdinal()
    {
        long sum = 0;
        for (var ordinal = 0; ordinal < FieldCount; ordinal++)
        {
            if (!indexedObservation.TryGetField(ordinal, out var value))
                throw new InvalidOperationException($"Indexed observation omitted ordinal '{ordinal}'.");
            sum += value.Int64;
        }
        return sum;
    }
}

/// <summary>Warm CLR materialization for a representative state object containing sixteen flat scalar fields.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ObservationFlatScalarMaterializationBenchmarks
{
    IndexedObservationOccurrence indexedObservation = null!;
    ObservationMaterializer<ObservationFlatScalarState> stringMaterializer = null!;
    ObservationMaterializer<ObservationFlatScalarState> ordinalMaterializer = null!;

    /// <summary>Builds the semantic shape, shared layout, indexed occurrence, and both compiled plans.</summary>
    [GlobalSetup]
    public void Setup()
    {
        const int FieldCount = 16;
        var definitions = ImmutableArray.CreateBuilder<FieldDefinition>(FieldCount);
        var values = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < FieldCount; ordinal++)
        {
            var fieldIdentity = $"Field{ordinal:D2}";
            definitions.Add(new(new(fieldIdentity), new ScalarTypeRef(ScalarTypeKind.Int64)));
            values.Add(fieldIdentity, ObservationValue.FromInt64(ordinal));
        }

        Shape definition = new(new("flat-scalar-state"), definitions.MoveToImmutable());
        ShapeGraph graph = new(new("observation-flat-scalar-materialization-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var observation = CoreObservation.Create(shape, ObservationValue.FromObject(values.ToImmutable()));
        var layout = ObservationLayout.Create(shape);
        indexedObservation = IndexedObservationOccurrence.FromObservation(
            shape,
            new(
                new("flat-scalar-materialization/0"),
                new("flat-scalar-materialization"),
                shape.QualifiedId,
                "flat-scalar-materialization/0"),
            observation,
            layout);
        stringMaterializer = ObservationMaterializer.For<ObservationFlatScalarState>(shape).Compile();
        ordinalMaterializer = ObservationMaterializer.For<ObservationFlatScalarState>(shape).Compile(layout);

        var expected = Handwritten();
        if (stringMaterializer.Materialize(indexedObservation) != expected
            || ordinalMaterializer.Materialize(indexedObservation) != expected)
        {
            throw new InvalidOperationException("Flat-scalar observation materialization produced an unexpected value.");
        }
    }

    /// <summary>Materializes the sixteen scalar fields through direct handwritten ordinal reads.</summary>
    /// <returns>The materialized state.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Observation", "FlatScalarClrMaterialization")]
    public ObservationFlatScalarState Handwritten() =>
        new(
            Read(0), Read(1), Read(2), Read(3),
            Read(4), Read(5), Read(6), Read(7),
            Read(8), Read(9), Read(10), Read(11),
            Read(12), Read(13), Read(14), Read(15));

    /// <summary>Materializes the sixteen scalar fields through repeated layout name lookup.</summary>
    /// <returns>The materialized state.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "FlatScalarClrMaterialization")]
    public ObservationFlatScalarState MaterializeIndexedWithStringPlan() =>
        stringMaterializer.Materialize(indexedObservation);

    /// <summary>Materializes the sixteen scalar fields through prebound layout ordinals.</summary>
    /// <returns>The materialized state.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "FlatScalarClrMaterialization")]
    public ObservationFlatScalarState MaterializeIndexedWithOrdinalPlan() =>
        ordinalMaterializer.Materialize(indexedObservation);

    long Read(int ordinal) =>
        indexedObservation.TryGetField(ordinal, out var value)
            ? value.GetInt64()
            : throw new InvalidOperationException($"Flat-scalar observation omitted ordinal '{ordinal}'.");
}

/// <summary>Fresh observation-to-CLR plan compilation after process-wide CLR metadata caches are warm.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ObservationMaterializerCompilationBenchmarks
{
    QualifiedShapeId shapeId;
    ObservationLayout layout = null!;

    /// <summary>Warms process-wide CLR target metadata before measuring independently created plans.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var fixture = ObservationProjectionFixture.Create();
        shapeId = fixture.Observation.ShapeId;
        layout = ObservationLayout.Create(fixture.Shape);
        _ = CompileFreshPlan();
        _ = CompileFreshOrdinalPlan();
    }

    /// <summary>Creates and compiles a new conventional materializer plan.</summary>
    /// <returns>A newly compiled immutable materializer.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "ClrMaterializerCompilation")]
    public ObservationMaterializer<ObservationBenchmarkState> CompileFreshPlan() =>
        ObservationMaterializer.For<ObservationBenchmarkState>(shapeId).Compile();

    /// <summary>Creates and compiles a new conventional materializer with a shared-layout ordinal path.</summary>
    /// <returns>A newly compiled immutable materializer.</returns>
    [Benchmark]
    [BenchmarkCategory("Observation", "ClrMaterializerCompilation")]
    public ObservationMaterializer<ObservationBenchmarkState> CompileFreshOrdinalPlan() =>
        ObservationMaterializer.For<ObservationBenchmarkState>(shapeId).Compile(layout);
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

/// <summary>Representative flat state with sixteen scalar fields.</summary>
/// <param name="Field00">Scalar field at ordinal 0.</param>
/// <param name="Field01">Scalar field at ordinal 1.</param>
/// <param name="Field02">Scalar field at ordinal 2.</param>
/// <param name="Field03">Scalar field at ordinal 3.</param>
/// <param name="Field04">Scalar field at ordinal 4.</param>
/// <param name="Field05">Scalar field at ordinal 5.</param>
/// <param name="Field06">Scalar field at ordinal 6.</param>
/// <param name="Field07">Scalar field at ordinal 7.</param>
/// <param name="Field08">Scalar field at ordinal 8.</param>
/// <param name="Field09">Scalar field at ordinal 9.</param>
/// <param name="Field10">Scalar field at ordinal 10.</param>
/// <param name="Field11">Scalar field at ordinal 11.</param>
/// <param name="Field12">Scalar field at ordinal 12.</param>
/// <param name="Field13">Scalar field at ordinal 13.</param>
/// <param name="Field14">Scalar field at ordinal 14.</param>
/// <param name="Field15">Scalar field at ordinal 15.</param>
public sealed record ObservationFlatScalarState(
    long Field00,
    long Field01,
    long Field02,
    long Field03,
    long Field04,
    long Field05,
    long Field06,
    long Field07,
    long Field08,
    long Field09,
    long Field10,
    long Field11,
    long Field12,
    long Field13,
    long Field14,
    long Field15);

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
    GraphShapeId Shape,
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
        return new(shape, observation, materializer, expected);
    }
}

sealed record ObservationFieldAccessFixture(
    CoreObservation Observation,
    IndexedObservationOccurrence IndexedObservation,
    string[] FieldNames)
{
    public static ObservationFieldAccessFixture Create(int fieldCount)
    {
        if (fieldCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(fieldCount));

        var definitions = ImmutableArray.CreateBuilder<FieldDefinition>(fieldCount);
        var values = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        string[] fieldNames = new string[fieldCount];
        for (var ordinal = 0; ordinal < fieldCount; ordinal++)
        {
            var fieldName = $"field_{ordinal:D2}";
            fieldNames[ordinal] = fieldName;
            definitions.Add(new(new(fieldName), new ScalarTypeRef(ScalarTypeKind.Int64)));
            values.Add(fieldName, ObservationValue.FromInt64(ordinal));
        }

        Shape definition = new(new("field-access-state"), definitions.MoveToImmutable());
        ShapeGraph graph = new(new($"observation-field-access-{fieldCount}"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var observation = CoreObservation.Create(shape, ObservationValue.FromObject(values.ToImmutable()));
        var layout = ObservationLayout.Create(shape, fieldNames);
        var indexed = IndexedObservationOccurrence.FromObservation(
            shape,
            new(
                new("field-access/0"),
                new("field-access"),
                shape.QualifiedId,
                "field-access/0"),
            observation,
            layout);
        return new(observation, indexed, fieldNames);
    }
}
