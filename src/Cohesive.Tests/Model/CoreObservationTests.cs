using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using CoreObservation = Cohesive.Model.Observation;

namespace Cohesive.Tests.Model;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CoreObservationPerformanceTestCollection
{
    public const string Name = "Core observation performance";
}

[Collection(CoreObservationPerformanceTestCollection.Name)]
public sealed class CoreObservationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(4096)]
    public void RequiredNullableFieldsRemainPresentInNestedAndCollectionValues(int count)
    {
        var objectType = new ObjectTypeRef([new("note", new ScalarTypeRef(ScalarTypeKind.String), nullability: FieldNullability.Nullable)]);
        Shape shape = new(new("nullable-observation"),
        [
            new(new("note"), new ScalarTypeRef(ScalarTypeKind.String), nullability: FieldNullability.Nullable),
            new(new("nested"), objectType),
            new(new("items"), objectType, cardinality: FieldCardinality.Many)
        ]);
        var nested = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["note"] = ObservationValue.Null });
        Dictionary<string, ObservationValue> fields = new()
        {
            ["note"] = ObservationValue.Null,
            ["nested"] = nested,
            ["items"] = ObservationValue.FromArray([.. Enumerable.Repeat(nested, count)])
        };
        Assert.True(ObservationValidator.TryValidateAgainstShape(fields, shape, out var error), error);
        fields["nested"] = ObservationValue.FromObject(new Dictionary<string, ObservationValue>());
        Assert.False(ObservationValidator.TryValidateAgainstShape(fields, shape, out _));
        fields["nested"] = nested;
        fields.Remove("note");
        Assert.False(ObservationValidator.TryValidateAgainstShape(fields, shape, out _));
    }

    [Fact]
    public void Create_CapturesQualifiedShapeAndValidatedFields()
    {
        var graph = CreateGraph();

        var observation = CoreObservation.Create(
            Shape(graph),
            Fields(
                ("name", ObservationValue.FromString("Ada")),
                ("tags", ObservationValue.FromArray([ObservationValue.FromString("priority")])),
                ("profile", ObservationValue.FromObject(Fields(
                    ("city", ObservationValue.FromString("London")))))));

        Assert.Equal(new QualifiedShapeId(graph.Id, new("customer")), observation.ShapeId);
        Assert.Equal("Ada", observation.GetField("name").GetString());
        Assert.Equal("London", observation.GetField(FieldPath.Parse("profile.city")).GetString());
        Assert.Throws<NotSupportedException>(() =>
            observation.TryGetField(FieldPath.Parse("tags.[]"), out _));
    }

    [Fact]
    public void Create_RejectsQualifiedShapeFromAnotherOrMissingGraph()
    {
        var graph = CreateGraph();
        var mismatched = new QualifiedShapeId(new("other-graph"), new("customer"));

        var mismatch = Assert.Throws<ArgumentException>(() => CoreObservation.Create(
            graph,
            mismatched,
            ObservationValue.FromObject(ValidFields())));
        var missing = Assert.Throws<ArgumentException>(() => new GraphShapeId(graph, new("missing")));

        Assert.Contains("not supplied graph", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("does not contain shape", missing.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidRoots))]
    public void Create_RejectsNonConcreteObjectRoots(ObservationValue value)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CoreObservation.Create(Shape(CreateGraph()), value));

        Assert.Contains("concrete, present, non-null object", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<ObservationValue> InvalidRoots => new()
    {
        ObservationValue.Undefined,
        ObservationValue.Null,
        ObservationValue.FromString("not-an-object"),
        ObservationValue.FromArray([])
    };

    [Fact]
    public void Create_RejectsUndefinedAndNonFiniteNestedValues()
    {
        var undefined = ValidFields();
        undefined["profile"] = ObservationValue.FromObject(Fields(
            ("city", ObservationValue.Undefined)));
        var nonFinite = ValidFields();
        nonFinite["profile"] = ObservationValue.FromObject(Fields(
            ("city", ObservationValue.FromString("London")),
            ("score", ObservationValue.FromDouble(double.NaN))));

        var undefinedFailure = Assert.Throws<ArgumentException>(() =>
            CoreObservation.Create(Shape(CreateGraph()), undefined));
        var nonFiniteFailure = Assert.Throws<ArgumentException>(() =>
            CoreObservation.Create(Shape(CreateGraph()), nonFinite));

        Assert.Contains("$.profile.city", undefinedFailure.Message, StringComparison.Ordinal);
        Assert.Contains("non-finite number", nonFiniteFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsUnknownMissingNullAndCardinalityViolations()
    {
        var shape = Shape(CreateGraph());

        var unknown = ValidFields();
        unknown["unrecognized"] = ObservationValue.FromBool(true);
        Assert.Contains(
            "unknown field 'unrecognized'",
            Assert.Throws<ArgumentException>(() => CoreObservation.Create(shape, unknown)).Message,
            StringComparison.Ordinal);

        var missing = ValidFields();
        missing.Remove("name");
        Assert.Contains(
            "missing required field 'name'",
            Assert.Throws<ArgumentException>(() => CoreObservation.Create(shape, missing)).Message,
            StringComparison.Ordinal);

        var nullValue = ValidFields();
        nullValue["name"] = ObservationValue.Null;
        Assert.Contains(
            "required and cannot be null",
            Assert.Throws<ArgumentException>(() => CoreObservation.Create(shape, nullValue)).Message,
            StringComparison.Ordinal);

        var wrongCardinality = ValidFields();
        wrongCardinality["tags"] = ObservationValue.FromString("not-an-array");
        Assert.Contains(
            "expects an array value",
            Assert.Throws<ArgumentException>(() => CoreObservation.Create(shape, wrongCardinality)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsNestedObjectAndEnumTypeViolations()
    {
        var shape = Shape(CreateGraph());

        var nested = ValidFields();
        nested["profile"] = ObservationValue.FromObject(Fields(
            ("country", ObservationValue.FromString("UK"))));
        Assert.Contains(
            "missing required property 'city'",
            Assert.Throws<ArgumentException>(() => CoreObservation.Create(shape, nested)).Message,
            StringComparison.Ordinal);

        var unknownNested = ValidFields();
        unknownNested["profile"] = ObservationValue.FromObject(Fields(
            ("city", ObservationValue.FromString("London")),
            ("planet", ObservationValue.FromString("Earth"))));
        Assert.Contains(
            "unknown property 'planet'",
            Assert.Throws<ArgumentException>(() => CoreObservation.Create(shape, unknownNested)).Message,
            StringComparison.Ordinal);

        var invalidEnum = ValidFields();
        invalidEnum["status"] = ObservationValue.FromString("suspended");
        Assert.Contains(
            "not a valid member",
            Assert.Throws<ArgumentException>(() => CoreObservation.Create(shape, invalidEnum)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ResolvesNamedStructuralTypesFromExactGraphEvidence()
    {
        TypeDefinition.Structural address = new(
            id: new("address"),
            fields: [new(name: new("city"), type: new ScalarTypeRef(ScalarTypeKind.String))]);
        Shape customer = new(
            id: new("customer"),
            fields: [new(name: new("address"), type: new NamedTypeRef(address.Id))]);
        ShapeGraph graph = new(
            id: new("customer-with-address-v1"),
            shapes: [customer],
            namedTypes: [address]);

        var observation = CoreObservation.Create(
            new GraphShapeId(graph, customer.Id),
            Fields(("address", ObservationValue.FromObject(Fields(
                ("city", ObservationValue.FromString("London")))))));

        Assert.Equal("London", observation.GetField(FieldPath.Parse("address.city")).GetString());
    }

    [Fact]
    public void Observation_IsIsolatedFromCallerOwnedMutableInputs()
    {
        var tags = new[] { ObservationValue.FromString("original") };
        var fields = ValidFields();
        fields["tags"] = ObservationValue.FromArray(tags);
        var observation = CoreObservation.Create(Shape(CreateGraph()), fields);

        tags[0] = ObservationValue.FromString("mutated-array");
        fields["name"] = ObservationValue.FromString("mutated-fields");

        Assert.Equal("Ada", observation.GetField("name").GetString());
        Assert.Equal("original", observation.GetField("tags").Array[0].GetString());
        Assert.Same(observation.Value.Fields, observation.Fields);
    }

    [Fact]
    public void EqualityHashSerializationAndFingerprint_AreStructuralAndOrderIndependent()
    {
        var graph = CreateGraph();
        var first = CoreObservation.Create(Shape(graph), Fields(
            ("name", ObservationValue.FromString("Ada")),
            ("status", ObservationValue.FromString("active"))));
        var second = CoreObservation.Create(Shape(graph), Fields(
            ("status", ObservationValue.FromString("active")),
            ("name", ObservationValue.FromString("Ada"))));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(first.ToCanonicalJson(), second.ToCanonicalJson());
        Assert.Equal(first.ComputeFingerprint(), second.ComputeFingerprint());
        Assert.Equal(CoreObservation.FingerprintAlgorithm, first.ComputeFingerprint().Algorithm);
        Assert.Equal(CoreObservation.FingerprintCanonicalization, first.ComputeFingerprint().Canonicalization);
        Assert.Equal(
            "{\"format\":\"cohesive-observation/v1\",\"graphId\":\"customer-graph-v1\",\"shapeId\":\"customer\",\"value\":{\"name\":\"Ada\",\"status\":\"active\"}}",
            first.ToCanonicalJson());
    }

    [Fact]
    public void WriteCanonicalJson_AppendsEquivalentUtf8WithoutAllocatingAfterWarmup()
    {
        const int Iterations = 1_000;
        var observation = CoreObservation.Create(Shape(CreateGraph()), ValidFields());
        var expected = observation.ToCanonicalJsonUtf8();
        var output = new ArrayBufferWriter<byte>(expected.Length + 16);
        ReadOnlySpan<byte> prefix = "prefix:"u8;
        prefix.CopyTo(output.GetSpan(prefix.Length));
        output.Advance(prefix.Length);

        observation.WriteCanonicalJson(output);

        Assert.True(expected.AsSpan().SequenceEqual(output.WrittenSpan[prefix.Length..]));
        Assert.Throws<ArgumentNullException>(() => observation.WriteCanonicalJson(null!));

        for (var iteration = 0; iteration < 100; iteration++)
        {
            output.Clear();
            observation.WriteCanonicalJson(output);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            output.Clear();
            observation.WriteCanonicalJson(output);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(expected.Length, output.WrittenCount);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ComputeFingerprint_HashesCanonicalStreamWithPayloadIndependentAllocations()
    {
        Shape definition = new(
            new("payload"),
            [new(new("value"), new ScalarTypeRef(ScalarTypeKind.String))]);
        ShapeGraph graph = new(new("payload-fingerprint-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var small = CoreObservation.Create(
            shape,
            Fields(("value", ObservationValue.FromString("small"))));
        var large = CoreObservation.Create(
            shape,
            Fields(("value", ObservationValue.FromString(new string('<', 100_000)))));
        var expectedDigest = Convert.ToHexStringLower(SHA256.HashData(small.ToCanonicalJsonUtf8()));
        Func<ObservationFingerprint> fingerprintSmall = small.ComputeFingerprint;
        Func<ObservationFingerprint> fingerprintLarge = large.ComputeFingerprint;

        var smallAllocated = MeasureAllocations(fingerprintSmall, iterations: 100, out var smallFingerprint);
        var largeAllocated = MeasureAllocations(fingerprintLarge, iterations: 100, out _);

        Assert.Equal(expectedDigest, smallFingerprint.Value);
        Assert.InRange(largeAllocated - smallAllocated, -512, 512);
    }

    [Fact]
    public void Equality_DistinguishesQualifiedShapeButNormalizesEqualNumbers()
    {
        Shape numeric = new(
            id: new("numeric"),
            fields: [new(name: new("value"), type: new JsonTypeRef(JsonTypeKind.Number))]);
        ShapeGraph firstGraph = new(id: new("numeric-v1"), shapes: [numeric]);
        ShapeGraph secondGraph = new(id: new("numeric-v2"), shapes: [numeric]);
        var integer = CoreObservation.Create(
            new GraphShapeId(firstGraph, numeric.Id),
            Fields(("value", ObservationValue.FromInt64(0))));
        var decimalValue = CoreObservation.Create(
            new GraphShapeId(firstGraph, numeric.Id),
            Fields(("value", ObservationValue.FromDecimal(0m))));
        var anotherShape = CoreObservation.Create(
            new GraphShapeId(secondGraph, numeric.Id),
            Fields(("value", ObservationValue.FromInt64(0))));

        Assert.Equal(integer, decimalValue);
        Assert.Equal(integer.ComputeFingerprint(), decimalValue.ComputeFingerprint());
        Assert.NotEqual(integer, anotherShape);
        Assert.NotEqual(integer.ComputeFingerprint(), anotherShape.ComputeFingerprint());
    }

    [Fact]
    public void Observation_HasNoOccurrenceOrPhysicalIdentitySurface()
    {
        var properties = typeof(CoreObservation).GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ShapeId", properties);
        Assert.Contains("Value", properties);
        Assert.Contains("Fields", properties);
        Assert.DoesNotContain("Id", properties);
        Assert.DoesNotContain("Version", properties);
        Assert.DoesNotContain("Lineage", properties);
        Assert.DoesNotContain("Layout", properties);
        Assert.DoesNotContain("ValuesByOrdinal", properties);
        Assert.DoesNotContain("HasValueBitMask", properties);
    }

    [Fact]
    public void TryGetField_RejectsDefaultPath()
    {
        var observation = CoreObservation.Create(Shape(CreateGraph()), ValidFields());

        var exception = Assert.Throws<ArgumentException>(() =>
            observation.TryGetField(default(FieldPath), out _));

        Assert.Contains("requires at least one segment", exception.Message, StringComparison.Ordinal);
    }

    static ShapeGraph CreateGraph(string graphId = "customer-graph-v1")
    {
        Shape customer = new(
            id: new("customer"),
            fields:
            [
                new(name: new("name"), type: new ScalarTypeRef(ScalarTypeKind.String)),
                new(
                    name: new("status"),
                    type: new EnumTypeRef("CustomerStatus", ["active", "inactive"]),
                    presence: FieldPresence.Optional),
                new(
                    name: new("tags"),
                    type: new ScalarTypeRef(ScalarTypeKind.String),
                    cardinality: FieldCardinality.Many,
                    presence: FieldPresence.Optional),
                new(
                    name: new("profile"),
                    type: new ObjectTypeRef(
                    [
                        new("city", new ScalarTypeRef(ScalarTypeKind.String)),
                        new(
                            name: "country",
                            type: new ScalarTypeRef(ScalarTypeKind.String),
                            presence: FieldPresence.Optional)
                    ]),
                    presence: FieldPresence.Optional)
            ]);

        return new(id: new(graphId), shapes: [customer]);
    }

    static GraphShapeId Shape(ShapeGraph graph) => new(graph, new("customer"));

    static Dictionary<string, ObservationValue> ValidFields() => Fields(
        ("name", ObservationValue.FromString("Ada")),
        ("status", ObservationValue.FromString("active")),
        ("tags", ObservationValue.FromArray([ObservationValue.FromString("priority")])),
        ("profile", ObservationValue.FromObject(Fields(
            ("city", ObservationValue.FromString("London"))))));

    static Dictionary<string, ObservationValue> Fields(
        params (string Name, ObservationValue Value)[] fields) =>
        fields.ToDictionary(
            static field => field.Name,
            static field => field.Value,
            StringComparer.Ordinal);

    static long MeasureAllocations<T>(Func<T> operation, int iterations, out T result)
    {
        result = default!;
        for (var iteration = 0; iteration < 20; iteration++)
            result = operation();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
            result = operation();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        GC.KeepAlive(result);
        return allocated;
    }
}
