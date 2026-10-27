using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ObservationNestedIngestionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PlannedReaderPreservesAllNestedOutcomesAndExactDiagnostics(bool named)
    {
        ObjectFieldTypeDef[] fields = [new("Id", new ScalarTypeRef(ScalarTypeKind.Int64)),
            new("when", new ScalarTypeRef(ScalarTypeKind.DateTime)),
            new("bytes", new ScalarTypeRef(ScalarTypeKind.Bytes), presence: FieldPresence.Optional, nullability: FieldNullability.Nullable),
            .. Enumerable.Range(0, 6).Select(i => new ObjectFieldTypeDef("pad" + i, new ScalarTypeRef(ScalarTypeKind.Int64), presence: FieldPresence.Optional))];
        TypeRef type = new ObjectTypeRef([.. fields]); ImmutableArray<TypeDefinition> types = [];
        if (named)
        {
            TypeDefinition.Structural record = new(new("record"), [.. fields.Select(f => new StructuralField(new(f.Name), f.Type, f.Cardinality, f.Presence, f.Nullability))]);
            type = new NamedTypeRef(record.Id); types = [record];
        }
        Shape definition = new(new("root"), [new(new("Id"), new ScalarTypeRef(ScalarTypeKind.Int64)), new(new("rows"), type, cardinality: FieldCardinality.Many)]);
        ShapeGraph graph = new(new("nested-ingestion"), [definition], types);
        GraphShapeId shape = new(graph, definition.Id); var layout = ObservationLayout.Create(shape, ["rows", "Id"]);
        var plan = ObservationJsonIngestionPlan.Create(layout); Assert.NotNull(plan);
        const string baseGood = "{\"Id\":1,\"when\":\"2026-10-22T12:00:00Z\",\"bytes\":\"AQID\"}";
        var good = baseGood[..^1] + "," + string.Join(',', Enumerable.Range(0, 6).Select(i => "\"pad" + i + "\":1")) + "}";
        foreach (var row in new[] { good, good.Replace("Id", "ID"), good.Replace("Id", "\\u0049d"), good.Replace("1,", "1,\"Id\":2,"),
            good.Replace("1,", "1,\"ID\":2,"), good.Replace("1,", "1,\"extra\":0,"), good.Replace("\"Id\":1,", ""),
            good.Replace("\"Id\":1", "\"Id\":null"), good.Replace("\"Id\":1", "\"Id\":true"), good.Replace("AQID", "bad base64"),
            good.Replace("2026-10-22T12:00:00Z", "bad date"), "{}", "null", "[]", "1", good[..^1] })
        {
            var json = "{\"Id\":7,\"rows\":[" + good + "," + row + "]}";
            Assert.Equal(Outcome(json, null), Outcome(json, plan));
            Assert.Equal(Outcome(json + " {}", null), Outcome(json + " {}", plan));
        }
        foreach (var json in new[] { "", "[]", "{}", "{\"Id\":7,\"rows\":[]}", "{\"Id\":7,\"rows\":[],\"Id\":8}", "{\"id\":7,\"rows\":[]}", "{\"Id\":7,\"rows\":[" })
            Assert.Equal(Outcome(json, null), Outcome(json, plan));
        var input = Encoding.UTF8.GetBytes("{\"Id\":7,\"rows\":[" + good + "]}");
        var value = Parse(input, plan); var rowFields = value.GetProperty("rows").Array[0].Fields!;
        Assert.IsType<ObservationNestedFields>(rowFields);
        using (var ordered = new OrderedObservationFields(rowFields, new NoRentPool()))
        {
            var cursor = ordered.GetEnumerator(); var keys = new List<string>();
            while (cursor.MoveNext()) keys.Add(cursor.Current.Key);
            cursor.Dispose(); Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        }
        Assert.False(rowFields is IObservationFieldReader); // No invented domain shape identity escapes.
        Assert.False(rowFields.TryGetValue(" ", out _));
        var enumBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) { using var cursor = rowFields.GetEnumerator(); while (cursor.MoveNext()) _ = cursor.Current; }
        Assert.InRange((GC.GetAllocatedBytesForCurrentThread() - enumBefore) / 100, 0, 40);
        var before = Canonical(value); Array.Fill(input, (byte)0); Assert.Equal(before, Canonical(value));

        string Outcome(string json, ObservationJsonIngestionPlan? candidate)
        {
            try { var value = Parse(Encoding.UTF8.GetBytes(json), candidate); return Convert.ToHexString(Canonical(value)) + ":" + value.GetHashCode(); }
            catch (Exception error) when (error is JsonException or ArgumentException) { return error.GetType().FullName + ":" + error.Message; }
        }
        ObservationValue Parse(byte[] json, ObservationJsonIngestionPlan? candidate)
        {
            var values = new ObservationValue[layout.Count]; var presence = new ulong[1];
            if (!ObservationJsonReader.TryReadShape(json, shape, layout, values, presence, out var error, candidate)) throw new ArgumentException(error, nameof(json));
            return ObservationValue.FromOrdinalFields(new(layout, ImmutableArray.CreateRange(values), values.Count(v => v.Kind != ObservationValueKind.Undefined)));
        }
    }

    [Fact]
    public void SparseRowsUseCompactCanonicalOrderingAndNameMissesStayLocalToTheirLayout()
    {
        ObjectFieldTypeDef[] fields = [new("Id", new ScalarTypeRef(ScalarTypeKind.Int64)),
            .. Enumerable.Range(0, 9).Select(i => new ObjectFieldTypeDef("pad" + i, new ScalarTypeRef(ScalarTypeKind.Int64), presence: FieldPresence.Optional))];
        Shape definition = new(new("root"), [new(new("Id"), new ScalarTypeRef(ScalarTypeKind.Int64)),
            new(new("rows"), new ObjectTypeRef([.. fields]), cardinality: FieldCardinality.Many)]);
        ShapeGraph graph = new(new("nested-sparse-ingestion"), [definition]);
        GraphShapeId shape = new(graph, definition.Id); var layout = ObservationLayout.Create(shape, ["rows", "Id"]);
        var plan = ObservationJsonIngestionPlan.Create(layout); Assert.NotNull(plan);
        const string sparse = "{\"pad7\":7,\"Id\":1,\"pad2\":2}";
        const string variant = "{\"ID\":1,\"pad2\":2}";

        // Sparse rows are packed and canonically ordered in place: no field buffer is rented.
        var rows = Parse("{\"Id\":7,\"rows\":[" + sparse + "]}").GetProperty("rows").Array;
        var nested = Assert.IsType<ObservationNestedFields>(rows[0].Fields);
        Assert.IsType<CompactOrdinalObservationFields>(nested.Storage);
        using (var ordered = new OrderedObservationFields(nested, new NoRentPool()))
        {
            var cursor = ordered.GetEnumerator(); var keys = new List<string>();
            while (cursor.MoveNext()) keys.Add(cursor.Current.Key);
            cursor.Dispose(); Assert.Equal(["Id", "pad2", "pad7"], keys);
        }
        Assert.Equal([new KeyValuePair<string, ObservationValue>("Id", ObservationValue.FromInt64(1)),
            new("pad2", ObservationValue.FromInt64(2)), new("pad7", ObservationValue.FromInt64(7))], nested.ToArray());

        // One stray spelling early in a batch falls back for that row only.
        var mixed = "{\"Id\":7,\"rows\":[" + variant + "," + string.Join(',', Enumerable.Repeat(sparse, 3)) + "]}";
        Assert.Equal(Outcome(mixed, null), Outcome(mixed, plan));
        var mixedRows = Parse(mixed).GetProperty("rows").Array;
        Assert.IsNotType<ObservationNestedFields>(mixedRows[0].Fields);
        Assert.All(mixedRows.Skip(1), row => Assert.IsType<ObservationNestedFields>(row.Fields));

        // A consistent miss streak stops speculation for that layout; an exact read resets the streak.
        var streak = "{\"Id\":7,\"rows\":[" + string.Join(',', Enumerable.Repeat(variant, 4)) + "," + sparse + "]}";
        Assert.Equal(Outcome(streak, null), Outcome(streak, plan));
        Assert.IsNotType<ObservationNestedFields>(Parse(streak).GetProperty("rows").Array[^1].Fields);
        var reset = "{\"Id\":7,\"rows\":[" + string.Join(',', Enumerable.Repeat(variant, 3)) + "," + sparse + "," +
            string.Join(',', Enumerable.Repeat(variant, 3)) + "," + sparse + "]}";
        Assert.Equal(Outcome(reset, null), Outcome(reset, plan));
        Assert.IsType<ObservationNestedFields>(Parse(reset).GetProperty("rows").Array[^1].Fields);

        string Outcome(string json, ObservationJsonIngestionPlan? candidate)
        {
            try { var value = ParseWith(json, candidate); return Convert.ToHexString(Canonical(value)) + ":" + value.GetHashCode(); }
            catch (Exception error) when (error is JsonException or ArgumentException) { return error.GetType().FullName + ":" + error.Message; }
        }
        ObservationValue Parse(string json) => ParseWith(json, plan);
        ObservationValue ParseWith(string json, ObservationJsonIngestionPlan? candidate)
        {
            var values = new ObservationValue[layout.Count]; var presence = new ulong[1];
            if (!ObservationJsonReader.TryReadShape(Encoding.UTF8.GetBytes(json), shape, layout, values, presence, out var error, candidate))
                throw new ArgumentException(error, nameof(json));
            return ObservationValue.FromOrdinalFields(new(layout, ImmutableArray.CreateRange(values), values.Count(v => v.Kind != ObservationValueKind.Undefined)));
        }
    }

    [Fact]
    public void StrictMaterializerAdmitsUnmappedSparseRowsBeforeConvertersWithBoundedAllocation()
    {
        ObjectTypeRef row = new([.. Enumerable.Range(0, 16).Select(i => new ObjectFieldTypeDef("field" + i, new ScalarTypeRef(ScalarTypeKind.Int64),
            presence: i == 0 ? FieldPresence.Required : FieldPresence.Optional))]);
        Shape definition = new(new("root"), [new(new("Id"), new ScalarTypeRef(ScalarTypeKind.Int64)), new(new("payload"), row, cardinality: FieldCardinality.Many)]);
        ShapeGraph graph = new(new("sparse-ingestion"), [definition]); GraphShapeId shape = new(graph, definition.Id);
        var plan = ObservationMaterializer.For<OnlyId>(shape).Compile(ObservationLayout.Create(shape));
        var json = Encoding.UTF8.GetBytes("{\"Id\":7,\"payload\":[" + string.Join(',', Enumerable.Repeat("{\"field0\":1,\"field1\":2,\"field2\":3,\"field3\":4}", 128)) + "]}");
        for (var i = 0; i < 100; i++) _ = plan.MaterializeJson(json);
        var before = GC.GetAllocatedBytesForCurrentThread();
        OnlyId? result = null; for (var i = 0; i < 100; i++) result = plan.MaterializeJson(json);
        Assert.InRange((GC.GetAllocatedBytesForCurrentThread() - before) / 100, 0, 128 * 512 + 4096);
        Assert.Equal(new OnlyId(7), result);
        var calls = 0;
        var guarded = ObservationMaterializer.For<OnlyId>(shape).Map("Id", r => r.Id, v => { calls++; return v.Int64; }).Compile(ObservationLayout.Create(shape));
        Assert.Throws<ArgumentException>(() => guarded.MaterializeJson(Encoding.UTF8.GetBytes("{\"Id\":7,\"payload\":[{\"field0\":false}]}")));
        Assert.Equal(0, calls); Assert.Equal(new OnlyId(7), guarded.MaterializeJson(json)); Assert.Equal(1, calls);
        Assert.ThrowsAny<JsonException>(() => plan.MaterializeJson(json.AsSpan(0, json.Length - 2)));
        Assert.Equal(new OnlyId(7), plan.MaterializeJson(json));
    }

    [Fact]
    public void NestedMutableProjectionsRemainIndependentAndPlansAreThreadSafe()
    {
        ObjectTypeRef child = new([new("Bytes", new ScalarTypeRef(ScalarTypeKind.Bytes)),
            .. Enumerable.Range(0, 8).Select(i => new ObjectFieldTypeDef("optional" + i, new ScalarTypeRef(ScalarTypeKind.Int64), presence: FieldPresence.Optional))]);
        Shape definition = new(new("root"), [new(new("Child"), child)]); ShapeGraph graph = new(new("nested-bytes"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var plan = ObservationMaterializer.For<Envelope>(shape).Compile(ObservationLayout.Create(shape));
        var json = "{\"Child\":{\"Bytes\":\"AQID\"}}"u8.ToArray();
        var first = plan.MaterializeJson(json); first.Child.Bytes[0] = 99;
        Parallel.For(0, 100, _ => Assert.Equal(new byte[] { 1, 2, 3 }, plan.MaterializeJson(json).Child.Bytes));
        Array.Fill(json, (byte)0); Assert.Equal(99, first.Child.Bytes[0]);
    }

    [Fact]
    public void CanonicalFreightApplicationGraphRoundTripsThroughStrictBoundary()
    {
        var semantics = Cohesive.MaterializationHarness.Model.FreightOrderMaterializationModel.Create();
        var graph = semantics.CompilationRequest.ShapeDocuments.Single(d => d.Graph.Id == Cohesive.MaterializationHarness.Model.FreightOrderMaterializationModel.GraphId).Graph;
        GraphShapeId shape = new(graph, Cohesive.MaterializationHarness.Model.FreightOrderMaterializationModel.OrderShapeId.ShapeId);
        Assert.Null(ObservationJsonIngestionPlan.Create(ObservationLayout.Create(shape))); // Four-field stops retain their original reader.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plan = ObservationMaterializer.For<Cohesive.MaterializationHarness.Model.FreightOrder>(shape).WithSerializerOptions(options).Compile(ObservationLayout.Create(shape));
        Cohesive.MaterializationHarness.Model.FreightOrder order = new()
        {
            Id = "order-1", TenantId = "tenant", OrderNumber = "FR-1", CustomerAccountId = "customer", EquipmentClass = "DryVan",
            CreatedAt = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
            Stops = [.. Enumerable.Range(0, 128).Select(i => new Cohesive.MaterializationHarness.Model.FreightOrderStop
                { Id = "stop-" + i, Sequence = i + 1, StopType = "Pickup", LocationId = "location" })]
        };
        var expected = JsonSerializer.Serialize(order, options);
        foreach (var input in new[] { expected, expected.Replace("\"sequence\"", "\"SEQUENCE\"", StringComparison.Ordinal) })
            Assert.Equal(expected, JsonSerializer.Serialize(plan.MaterializeJson(Encoding.UTF8.GetBytes(input)), options));
    }

    sealed class NoRentPool : ArrayPool<KeyValuePair<string, ObservationValue>>
    {
        public override KeyValuePair<string, ObservationValue>[] Rent(int minimumLength) => throw new InvalidOperationException("Dense nested ordering must not rent a field buffer.");
        public override void Return(KeyValuePair<string, ObservationValue>[] array, bool clearArray = false) => throw new InvalidOperationException("No buffer is owned.");
    }

    static byte[] Canonical(ObservationValue value)
    { var buffer = new ArrayBufferWriter<byte>(); CanonicalJsonWriter.WriteCanonicalObservationValue(buffer, value); return buffer.WrittenSpan.ToArray(); }
    public sealed record OnlyId(long Id);
    public sealed record Envelope(Binary Child);
    public sealed record Binary(byte[] Bytes);
}
