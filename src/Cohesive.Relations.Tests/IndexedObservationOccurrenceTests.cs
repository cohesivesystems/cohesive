using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Physical;
using CoreObservation = Cohesive.Model.Observation;

namespace Cohesive.Relations.Tests;

public sealed class IndexedObservationOccurrenceTests
{
    [Fact]
    public void CanonicalLayout_IsSharedForTheExactGraphShape()
    {
        var shape = CustomerShape("customer-graph/v1");

        var first = ObservationLayout.Create(shape);
        var second = ObservationLayout.Create(shape);
        var explicitlyOrdered = ObservationLayout.Create(shape, first.FieldIdentities);

        Assert.Same(first, second);
        Assert.NotSame(first, explicitlyOrdered);
    }

    [Fact]
    public void FromObservation_PreservesQualifiedShapeFieldsOccurrenceAndLineage()
    {
        var shape = CustomerShape("customer-graph/v1");
        var semantic = Observe(
            shape,
            ("name", ObservationValue.FromString("Ada")),
            ("count", ObservationValue.FromInt64(3)),
            ("note", ObservationValue.Null));
        var occurrence = Occurrence(shape, "occurrence/customer-1", "customer-1");
        var layout = ObservationLayout.Create(shape, ["count", "alias", "note", "name"]);
        List<FieldLineage> lineageFields = [new("name", [])];
        var lineage = new ObservationLineage(lineageFields);

        var indexed = IndexedObservationOccurrence.FromObservation(
            shape,
            occurrence,
            semantic,
            layout,
            lineage);
        lineageFields.Clear();

        Assert.Same(occurrence, indexed.Occurrence);
        Assert.Same(layout, indexed.Layout);
        Assert.Equal(shape.QualifiedId, indexed.ShapeId);
        Assert.Equal("Ada", indexed.GetRequiredField("name").GetString());
        Assert.Equal(3, indexed.GetRequiredField(0).GetInt32());
        Assert.Equal(ObservationValueKind.Null, indexed.GetRequiredField("note").Kind);
        Assert.False(indexed.TryGetField("alias", out _));
        Assert.Single(indexed.Lineage.Fields);
        Assert.Equal(semantic, indexed.ToObservation(shape.Graph));
    }

    [Fact]
    public void Create_ValidatesAndSnapshotsPhysicalInputs()
    {
        var shape = CustomerShape("customer-graph/v1");
        var occurrence = Occurrence(shape, "occurrence/customer-1", "customer-1");
        var fieldNames = new[] { "name", "count", "note" };
        var layout = ObservationLayout.Create(shape, fieldNames);
        var values = new[]
        {
            ObservationValue.FromString("Ada"),
            ObservationValue.FromInt64(3),
            default
        };
        var presence = new ulong[1];
        ObservationBuffer.SetHasValue(presence, 0);
        ObservationBuffer.SetHasValue(presence, 1);

        var indexed = IndexedObservationOccurrence.Create(
            shape,
            occurrence,
            layout,
            values,
            presence);
        fieldNames[0] = "changed";
        values[0] = ObservationValue.FromString("Grace");
        presence[0] = 0;

        Assert.Equal("name", indexed.Layout.FieldIdentities[0]);
        Assert.Same(layout, indexed.Layout);
        Assert.Equal("Ada", indexed.GetRequiredField("name").GetString());
        Assert.Equal(3, indexed.GetRequiredField("count").GetInt32());
        Assert.Equal(
            Observe(
                shape,
                ("name", ObservationValue.FromString("Ada")),
                ("count", ObservationValue.FromInt64(3))),
            indexed.ToObservation(shape.Graph));
    }

    [Fact]
    public void CreateBuilder_TransfersOwnedOrdinalStorageAndConsumesSuccessfulBuilder()
    {
        var shape = CustomerShape("customer-graph/v1");
        var occurrence = Occurrence(shape, "occurrence/customer-1", "customer-1");
        var layout = ObservationLayout.Create(shape, ["count", "note", "name"]);
        var builder = IndexedObservationOccurrence.CreateBuilder(shape, occurrence, layout);

        builder.SetField(0, ObservationValue.FromInt64(3));
        builder.SetField("name", ObservationValue.FromString("Grace"));
        builder.SetField("name", ObservationValue.FromString("Ada"));
        var indexed = builder.Build();

        Assert.Same(layout, builder.Layout);
        Assert.Same(layout, indexed.Layout);
        Assert.Equal("Ada", indexed.GetRequiredField("name").GetString());
        Assert.Equal(3, indexed.GetRequiredField(0).GetInt32());
        Assert.False(indexed.TryGetField("note", out _));
        Assert.Throws<InvalidOperationException>(() =>
            builder.SetField("note", ObservationValue.Null));
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void CreateBuilder_FailedValidationCanBeCompletedAndRetried()
    {
        var shape = CustomerShape("customer-graph/v1");
        var builder = IndexedObservationOccurrence.CreateBuilder(
            shape,
            Occurrence(shape, "occurrence/customer-1", "customer-1"),
            ObservationLayout.Create(shape));
        builder.SetField("name", ObservationValue.FromString("Ada"));

        var failure = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("required field 'count'", failure.Message, StringComparison.Ordinal);
        builder.SetField("count", ObservationValue.FromInt64(3));
        var indexed = builder.Build();
        Assert.Equal(3, indexed.GetRequiredField("count").GetInt32());
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void CreateBuilder_HandlesInlineAndExternalPresenceBoundary(int fieldCount)
    {
        var definitions = Enumerable.Range(0, fieldCount)
            .Select(static ordinal => new FieldDefinition(
                new($"field_{ordinal:D2}"),
                new ScalarTypeRef(ScalarTypeKind.Int64)))
            .ToImmutableArray();
        Shape definition = new(new("large-ordinal-state"), definitions);
        ShapeGraph graph = new(new("large-ordinal-state-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var builder = IndexedObservationOccurrence.CreateBuilder(
            shape,
            Occurrence(shape, "occurrence/large-1", "large-1"),
            ObservationLayout.Create(shape));
        for (var ordinal = 0; ordinal < fieldCount; ordinal++)
            builder.SetField(ordinal, ObservationValue.FromInt64(ordinal));

        var indexed = builder.Build();

        Assert.Equal(0, indexed.GetRequiredField(0).GetInt32());
        Assert.Equal(fieldCount - 1, indexed.GetRequiredField(fieldCount - 1).GetInt32());
    }

    [Fact]
    public void Create_OrdinalBuffers_DoesNotReconstructDictionaryState()
    {
        const int FieldCount = 16;
        const int Iterations = 1_000;
        var definitions = Enumerable.Range(0, FieldCount)
            .Select(static ordinal => new FieldDefinition(
                new($"field_{ordinal:D2}"),
                new ScalarTypeRef(ScalarTypeKind.Int64)))
            .ToImmutableArray();
        Shape definition = new(new("ordinal-state"), definitions);
        ShapeGraph graph = new(new("ordinal-state-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var layout = ObservationLayout.Create(shape);
        var values = Enumerable.Range(0, FieldCount)
            .Select(static value => ObservationValue.FromInt64(value))
            .ToArray();
        ulong[] presence = [ushort.MaxValue];
        var occurrence = Occurrence(shape, "occurrence/ordinal-1", "ordinal-1");

        for (var iteration = 0; iteration < 100; iteration++)
            _ = IndexedObservationOccurrence.Create(shape, occurrence, layout, values, presence);

        IndexedObservationOccurrence? last = null;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < Iterations; iteration++)
            last = IndexedObservationOccurrence.Create(shape, occurrence, layout, values, presence);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var allocatedPerOccurrence = allocated / Iterations;

        Assert.NotNull(last);
        Assert.InRange(allocatedPerOccurrence, 600, 620);
    }

    [Fact]
    public void FromJson_ReadsDirectlyIntoLayoutAndRestoresShapedPrimitives()
    {
        Shape definition = new(
            new("event"),
            [
                new(new("name"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("day"), new ScalarTypeRef(ScalarTypeKind.Date)),
                new(new("at"), new ScalarTypeRef(ScalarTypeKind.Instant)),
                new(new("payload"), new ScalarTypeRef(ScalarTypeKind.Bytes)),
                new(
                    new("scores"),
                    new ScalarTypeRef(ScalarTypeKind.Decimal),
                    cardinality: FieldCardinality.Many),
                new(
                    new("details"),
                    new ObjectTypeRef(
                    [
                        new("effective", new ScalarTypeRef(ScalarTypeKind.Date))
                    ]))
            ]);
        ShapeGraph graph = new(new("event-graph/v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var layout = ObservationLayout.Create(
            shape,
            ["payload", "details", "scores", "at", "day", "name"]);
        var occurrence = Occurrence(shape, "occurrence/event-1", "event-1");
        var json = """
            {"details":{"effective":"2026-08-30"},"name":"release","payload":"AQID","day":"2026-08-29","scores":[1,2.5],"at":"2026-08-29T18:30:00Z"}
            """u8;

        var indexed = IndexedObservationOccurrence.FromJson(shape, occurrence, layout, json);

        Assert.Same(layout, indexed.Layout);
        Assert.Equal("release", indexed.GetRequiredField("name").GetString());
        Assert.Equal(new DateOnly(2026, 8, 29), indexed.GetRequiredField("day").GetDateOnly());
        Assert.True(indexed.GetRequiredField("at").TryGetInstant(out _));
        Assert.Equal(new byte[] { 1, 2, 3 }, indexed.GetRequiredField("payload").GetBytes().ToArray());
        Assert.Equal(2.5m, indexed.GetRequiredField("scores").EnumerateArray()[1].GetDecimal());
        Assert.Equal(
            new DateOnly(2026, 8, 30),
            indexed.GetRequiredField("details").GetProperty("effective").GetDateOnly());
    }

    [Fact]
    public void FromJson_UsesUnionDiscriminatorSemanticsBeforeReadingTypedCase()
    {
        TypeId payloadTypeId = new("event-payload");
        ObjectTypeRef datedPayload = new(
        [
            new("kind", new ScalarTypeRef(ScalarTypeKind.String)),
            new("when", new ScalarTypeRef(ScalarTypeKind.Date))
        ]);
        TypeDefinition.Union payloadType = new(
            payloadTypeId,
            new("kind"),
            [new("dated", datedPayload)]);
        Shape definition = new(
            new("event"),
            [new(new("payload"), new NamedTypeRef(payloadTypeId))]);
        ShapeGraph graph = new(
            new("union-event-graph/v1"),
            [definition],
            namedTypes: [payloadType]);
        GraphShapeId shape = new(graph, definition.Id);

        var indexed = IndexedObservationOccurrence.FromJson(
            shape,
            Occurrence(shape, "occurrence/event-1", "event-1"),
            "{\"payload\":{\"when\":\"2026-08-29\",\"KIND\":\"dated\"}}"u8);

        Assert.Equal(
            new DateOnly(2026, 8, 29),
            indexed.GetRequiredField("payload").GetProperty("when").GetDateOnly());
    }

    [Fact]
    public void FromJson_RejectsInvalidShapeDuplicateUnknownAndTrailingContent()
    {
        var shape = CustomerShape("customer-graph/v1");
        var occurrence = Occurrence(shape, "occurrence/customer-1", "customer-1");
        var layout = ObservationLayout.Create(shape);

        var missing = Assert.Throws<ArgumentException>(() =>
            IndexedObservationOccurrence.FromJson(shape, occurrence, layout, "{}"u8));
        var duplicate = Assert.Throws<JsonException>(() =>
            IndexedObservationOccurrence.FromJson(
                shape,
                occurrence,
                layout,
                "{\"name\":\"Ada\",\"name\":\"Grace\",\"count\":3}"u8));
        var unknown = Assert.Throws<JsonException>(() =>
            IndexedObservationOccurrence.FromJson(
                shape,
                occurrence,
                layout,
                "{\"name\":\"Ada\",\"count\":3,\"other\":true}"u8));
        var trailing = Assert.ThrowsAny<JsonException>(() =>
            IndexedObservationOccurrence.FromJson(
                shape,
                occurrence,
                layout,
                "{\"name\":\"Ada\",\"count\":3}{}"u8));

        Assert.Contains("required field 'name'", missing.Message, StringComparison.Ordinal);
        Assert.Contains("more than once", duplicate.Message, StringComparison.Ordinal);
        Assert.Contains("other", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("trailing content", trailing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromJson_FlatOrdinalHydrationAvoidsRootDictionaryAllocations()
    {
        const int FieldCount = 16;
        const int Iterations = 1_000;
        var definitions = Enumerable.Range(0, FieldCount)
            .Select(static ordinal => new FieldDefinition(
                new($"field_{ordinal:D2}"),
                new ScalarTypeRef(ScalarTypeKind.Int64)))
            .ToImmutableArray();
        Shape definition = new(new("json-state"), definitions);
        ShapeGraph graph = new(new("json-state-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var layout = ObservationLayout.Create(shape);
        var occurrence = Occurrence(shape, "occurrence/json-state-1", "json-state-1");
        var json = Encoding.UTF8.GetBytes(
            "{" + string.Join(",", Enumerable.Range(0, FieldCount)
                .Select(static ordinal => $"\"field_{ordinal:D2}\":{ordinal}")) + "}");

        for (var iteration = 0; iteration < 100; iteration++)
            _ = IndexedObservationOccurrence.FromJson(shape, occurrence, layout, json);

        IndexedObservationOccurrence? last = null;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < Iterations; iteration++)
            last = IndexedObservationOccurrence.FromJson(shape, occurrence, layout, json);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var allocatedPerOccurrence = allocated / Iterations;

        Assert.NotNull(last);
        Assert.Equal(15, last.GetRequiredField("field_15").GetInt32());
        Assert.InRange(allocatedPerOccurrence, 600, 620);
    }

    [Fact]
    public void WriteCanonicalJson_WritesOrdinalStorageWithoutSteadyStateAllocation()
    {
        const int Iterations = 1_000;
        var shape = CustomerShape("customer-graph/v1");
        var semantic = Observe(
            shape,
            ("name", ObservationValue.FromString("Ada")),
            ("count", ObservationValue.FromInt64(3)),
            ("note", ObservationValue.Null));
        var indexed = IndexedObservationOccurrence.FromObservation(
            shape,
            Occurrence(shape, "occurrence/customer-1", "customer-1"),
            semantic,
            ObservationLayout.Create(shape, ["note", "alias", "count", "name"]));
        var expected = semantic.ToCanonicalJsonUtf8();
        var output = new ArrayBufferWriter<byte>(expected.Length + 16);

        indexed.WriteCanonicalJson(output);

        Assert.True(expected.AsSpan().SequenceEqual(output.WrittenSpan));
        Assert.Throws<ArgumentNullException>(() => indexed.WriteCanonicalJson(null!));

        for (var iteration = 0; iteration < 100; iteration++)
        {
            output.Clear();
            indexed.WriteCanonicalJson(output);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            output.Clear();
            indexed.WriteCanonicalJson(output);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(expected.Length, output.WrittenCount);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Create_RejectsForeignLayoutFieldsInvalidValuesAndOutOfRangePresence()
    {
        var shape = CustomerShape("customer-graph/v1");
        var occurrence = Occurrence(shape, "occurrence/customer-1", "customer-1");

        var foreignLayoutFailure = Assert.Throws<ArgumentException>(() =>
            IndexedObservationOccurrence.Create(
                shape,
                occurrence,
                ObservationLayout.Create(shape, ["name", "foreign"]),
                new[] { ObservationValue.FromString("Ada"), default },
                new ulong[] { 1UL }));
        var invalidValueFailure = Assert.Throws<ArgumentException>(() =>
            IndexedObservationOccurrence.Create(
                shape,
                occurrence,
                ObservationLayout.Create(shape, ["name"]),
                new[] { ObservationValue.FromInt64(7) },
                new ulong[] { 1UL }));
        var presenceFailure = Assert.Throws<ArgumentException>(() =>
            IndexedObservationOccurrence.Create(
                shape,
                occurrence,
                ObservationLayout.Create(shape, ["name"]),
                new[] { ObservationValue.FromString("Ada") },
                new ulong[] { 3UL }));
        var lineageFailure = Assert.Throws<ArgumentException>(() =>
            IndexedObservationOccurrence.FromObservation(
                shape,
                occurrence,
                Observe(
                    shape,
                    ("name", ObservationValue.FromString("Ada")),
                    ("count", ObservationValue.FromInt64(3))),
                ObservationLayout.Create(shape, ["name", "count", "note"]),
                new([new FieldLineage("note", [])])));

        Assert.Contains("unknown field 'foreign'", foreignLayoutFailure.Message, StringComparison.Ordinal);
        Assert.Contains("does not adhere", invalidValueFailure.Message, StringComparison.Ordinal);
        Assert.Contains("outside the layout", presenceFailure.Message, StringComparison.Ordinal);
        Assert.Contains("absent from the physical occurrence", lineageFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_ExecutesSharedCorePlanDirectlyAgainstIndexedAccess()
    {
        var shape = CustomerShape("customer-graph/v1");
        var layout = ObservationLayout.Create(shape);
        var indexed = IndexedObservationOccurrence.FromObservation(
            shape,
            Occurrence(shape, "occurrence/customer-1", "customer-1"),
            Observe(
                shape,
                ("name", ObservationValue.FromString("Ada")),
                ("count", ObservationValue.FromInt64(3))),
            layout);
        var materializer = ObservationMaterializer
            .For<Customer>(shape)
            .Map("name", customer => customer.Name)
            .Map("count", customer => customer.Count)
            .Compile(layout);

        var customer = indexed.Materialize(materializer);
        var firstDefault = ObservationMaterializer.GetDefault<Customer>(indexed);
        var secondDefault = ObservationMaterializer.GetDefault<Customer>(indexed);

        Assert.Equal(new Customer("Ada", 3), customer);
        Assert.Same(firstDefault, secondDefault);
    }

    [Fact]
    public void QualifiedShapeIdentity_DistinguishesSameLocalShapeAcrossGraphs()
    {
        var firstShape = CustomerShape("customer-graph/v1");
        var secondShape = CustomerShape("customer-graph/v2");
        var second = IndexedObservationOccurrence.FromObservation(
            secondShape,
            Occurrence(secondShape, "occurrence/customer-1", "customer-1"),
            Observe(
                secondShape,
                ("name", ObservationValue.FromString("Ada")),
                ("count", ObservationValue.FromInt64(3))));
        var firstMaterializer = ObservationMaterializer.For<Customer>(firstShape).Compile();

        var exception = Assert.Throws<InvalidOperationException>(() => second.Materialize(firstMaterializer));
        var projectionException = Assert.Throws<ArgumentException>(() => second.ToObservation(firstShape.Graph));

        Assert.Contains("does not match observation shape", exception.Message, StringComparison.Ordinal);
        Assert.Contains("belongs to graph", projectionException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromObservation_RoundTripsAnEmptyShapeAndBuffer()
    {
        Shape definition = new(new("marker"), []);
        ShapeGraph graph = new(new("marker-graph/v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var semantic = CoreObservation.Create(shape, new Dictionary<string, ObservationValue>());
        var indexed = IndexedObservationOccurrence.FromObservation(
            shape,
            Occurrence(shape, "occurrence/marker-1", "marker-1"),
            semantic);

        Assert.Equal(0, indexed.Layout.Count);
        Assert.Equal(semantic, indexed.ToObservation(graph));
    }

    static GraphShapeId CustomerShape(string graphId)
    {
        Shape shape = new(
            new("customer"),
            [
                new(new("name"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("count"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                new(
                    new("note"),
                    new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable),
                new(
                    new("alias"),
                    new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Optional)
            ]);
        ShapeGraph graph = new(new(graphId), [shape]);
        return new(graph, shape.Id);
    }

    static RelationQueryObservationOccurrence Occurrence(
        GraphShapeId shape,
        string occurrenceId,
        string observationIdentity) =>
        new(new(occurrenceId), new("customer"), shape.QualifiedId, observationIdentity);

    static CoreObservation Observe(
        GraphShapeId shape,
        params (string Name, ObservationValue Value)[] fields) =>
        CoreObservation.Create(
            shape,
            fields.ToDictionary(
                static field => field.Name,
                static field => field.Value,
                StringComparer.Ordinal));

    sealed record Customer(string Name, int Count);
}

static class IndexedObservationOccurrenceTestExtensions
{
    public static ObservationValue GetRequiredField(
        this IndexedObservationOccurrence occurrence,
        string fieldIdentity) =>
        occurrence.TryGetField(fieldIdentity, out var field)
            ? field
            : throw new Xunit.Sdk.XunitException($"Expected field '{fieldIdentity}' to be present.");

    public static ObservationValue GetRequiredField(
        this IndexedObservationOccurrence occurrence,
        int ordinal) =>
        occurrence.TryGetField(ordinal, out var field)
            ? field
            : throw new Xunit.Sdk.XunitException($"Expected ordinal '{ordinal}' to be present.");
}
