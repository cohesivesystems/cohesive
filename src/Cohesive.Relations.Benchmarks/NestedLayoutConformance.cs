using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

// Executable differential checks for an experiment that remains outside production assemblies.
internal static class NestedLayoutConformance
{
    internal static int Run()
    {
        var checks = 0;
        foreach (var named in new[] { false, true })
        {
            var fields = new[] {
                new ObjectFieldTypeDef("field0", new ScalarTypeRef(ScalarTypeKind.Int64)),
                new ObjectFieldTypeDef("field1", new ScalarTypeRef(ScalarTypeKind.Int64), presence: FieldPresence.Optional, nullability: FieldNullability.Nullable)
            };
            TypeRef type = new ObjectTypeRef([.. fields]);
            ImmutableArray<TypeDefinition> definitions = [];
            if (named)
            {
                TypeDefinition.Structural definition = new(new("row"), [.. fields.Select(field =>
                    new StructuralField(new(field.Name), field.Type, field.Cardinality, field.Presence, field.Nullability))]);
                type = new NamedTypeRef(definition.Id); definitions = [definition];
            }
            foreach (var array in new[] { false, true })
            {
                var shape = Shape(type, array ? FieldCardinality.Many : FieldCardinality.Single, definitions);
                foreach (var payload in new[] {
                    "{\"field0\":1,\"field1\":2}", "{\"field1\":2,\"field0\":1}", "{\"field0\":1}",
                    "{\"field0\":1,\"field0\":3}", "{\"field0\":1,\"\\u0066ield0\":3}",
                    "{\"\\u0066ield0\":1}", "{\"FIELD0\":1}", "{\"field0\":1,\"FIELD0\":2}",
                    "{\"FIELD0\":1,\"field0\":2}", "{\"field0\":1,\"field1\":null}",
                    "{\"field0\":1,\"unknown\":3}", "{}", "{\"field0\":null}", "{\"field0\":true}",
                    "{\"field0\":1.25}", "{\"field0\":9223372036854775808}", "{\"field0\":\"bad\"}", "null", "[]", "1"
                })
                {
                    var value = array ? "[" + payload + "," + payload + "]" : payload;
                    Compare(shape, "{\"payload\":" + value + "}"); checks++;
                }
                foreach (var json in new[] { "", "[]", "{}", "{\"PAYLOAD\":{}}", "{\"payload\":{},\"payload\":{}}", "{\"payload\":{},\"extra\":1}",
                    "{\"payload\":{\"field0\":1}} {}", "{\"payload\":{\"field0\":1}} /*x*/", "{\"payload\":", "{\"payload\":{\"field0\":1}" })
                { Compare(shape, json); checks++; }
            }
        }
        // Unsupported typed restoration, opaque Json values and unions must remain entirely reader-owned.
        foreach (TypeRef type in new TypeRef[] { new ScalarTypeRef(ScalarTypeKind.Date), new ScalarTypeRef(ScalarTypeKind.Bytes),
            new JsonTypeRef(JsonTypeKind.Object), new ObjectTypeRef([new("Id", new ScalarTypeRef(ScalarTypeKind.Int64)), new("id", new ScalarTypeRef(ScalarTypeKind.String))]) })
            foreach (var payload in new[] { "\"2026-10-20\"", "\"AQID\"", "{\"Id\":1,\"id\":\"text\"}", "{\"x\":1,\"x\":2}", "null" })
            { Compare(Shape(type), "{\"payload\":" + payload + "}"); checks++; }
        TypeDefinition.Union union = new(new("choice"), new("kind"),
            [new("row", new ObjectTypeRef([new("field0", new ScalarTypeRef(ScalarTypeKind.Int64))]), "row")]);
        foreach (var payload in new[] { "{\"kind\":\"row\",\"field0\":1}", "{\"field0\":1,\"kind\":\"row\"}",
            "{\"kind\":\"other\",\"field0\":1}", "{\"KIND\":\"row\",\"field0\":1}", "{\"field0\":1}" })
        { Compare(Shape(new NamedTypeRef(union.Id), types: [union]), "{\"payload\":" + payload + "}"); checks++; }
        TypeDefinition.Structural recursive = new(new("recursive"), [new(new("field0"), new ScalarTypeRef(ScalarTypeKind.Int64)),
            new(new("next"), new NamedTypeRef(new("recursive")), presence: FieldPresence.Optional)]);
        Compare(Shape(new NamedTypeRef(recursive.Id), types: [recursive]), "{\"payload\":{\"field0\":1,\"next\":{\"field0\":2}}}"); checks++;
        Compare(Shape(new ArrayTypeRef(new ObjectTypeRef([new("field0", new ScalarTypeRef(ScalarTypeKind.Int64))]))), "{\"payload\":[{\"field0\":1}]}"); checks++;
        foreach (var scenario in new[] { "record", "nested", "array", "named-array", "sparse-array", "reordered-array", "case-array" })
            foreach (var width in new[] { 4, 16, 32, 256 })
            {
                var benchmark = new ObservationNestedLayoutBenchmarks { Width = width, Count = 8, Scenario = scenario };
                benchmark.Setup(); checks++;
            }
        foreach (var width in new[] { 4, 16, 32, 256 })
            foreach (var percent in new[] { 6, 25, 50, 75, 100 })
                foreach (var scenario in new[] { "array", "mixed-array" })
                {
                    var density = new ObservationNestedLayoutBenchmarks { Width = width, Count = 8, Scenario = scenario, PresentPercent = percent };
                    density.Setup(); checks++;
                }
        var wideType = new ObjectTypeRef([.. Enumerable.Range(0, 16).Select(index => new ObjectFieldTypeDef(
            "field" + index, new ScalarTypeRef(ScalarTypeKind.Int64), presence: index == 0 ? FieldPresence.Required : FieldPresence.Optional,
            nullability: FieldNullability.Nullable))]);
        var recoveryShape = Shape(wideType, FieldCardinality.Many);
        foreach (var json in new[] { "{\"payload\":[{\"field0\":1}]}", "{\"payload\":[{\"field0\":null}]}",
            "{\"payload\":[{\"field0\":1,\"field0\":2}]}", "{\"payload\":[{\"field0\":1,\"FIELD0\":2}]}",
            "{\"payload\":[{\"field0\":1,\"unknown\":2}]}", "{\"payload\":[{\"field0\":1}",
            "{\"payload\":[{\"field0\":", "{\"payload\":[{\"field0\":1},{\"field0\":false}]}" })
        { Compare(recoveryShape, json); checks++; }
        var owned = new ObservationNestedLayoutBenchmarks { Width = 16, Count = 128, Scenario = "array" };
        owned.Setup();
        var first = owned.ParseDensityAware(); var second = owned.ParseDensityAware();
        if (ReferenceEquals(first.CompositeStorage, second.CompositeStorage)
            || ReferenceEquals(first.GetProperty("payload").Array[0].CompositeStorage, second.GetProperty("payload").Array[0].CompositeStorage)
            || ReferenceEquals(first.GetProperty("payload").Array[0].CompositeStorage, first.GetProperty("payload").Array[1].CompositeStorage))
            throw new InvalidOperationException("Mutable construction storage was reused across retained records.");
        for (var index = 0; index < 100; index++) _ = owned.ParseDensityAware();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) _ = owned.ParseDensityAware();
        var allocation = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        if (allocation > 128 * 640 + 4096) throw new InvalidOperationException("Shared storage exceeded its bounded allocation budget: " + allocation);
        var sparse = new ObservationNestedLayoutBenchmarks { Width = 16, Count = 128, Scenario = "array", PresentPercent = 25 };
        sparse.Setup();
        for (var index = 0; index < 100; index++) _ = sparse.ParseDensityAware();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) _ = sparse.ParseDensityAware();
        var sparseAllocation = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        if (sparseAllocation > 125952) throw new InvalidOperationException("Sparse selection exceeded its allocation budget: " + sparseAllocation);
        var mixed = new ObservationNestedLayoutBenchmarks { Width = 32, Count = 2, Scenario = "mixed-array" };
        mixed.Setup();
        var rows = mixed.ParseDensityAware().GetProperty("payload").Array;
        if (rows[0].Fields is IOrdinalObservationFieldReader || rows[1].Fields is not IOrdinalObservationFieldReader)
            throw new InvalidOperationException("Per-record density selection disagrees with actual presence.");
        var packedRows = mixed.ParseCompactOrdinal().GetProperty("payload").Array;
        if (packedRows[0].Fields is not CompactOrdinalObservationFields || packedRows[1].Fields is not OrdinalObservationFields)
            throw new InvalidOperationException("Packed policy did not retain sparse ordinals/dense vectors.");
        Console.WriteLine($"sparse_128x16_bytes={sparseAllocation},sparse_allocation_budget_bytes=125952");
        Console.WriteLine($"checks={checks},candidate_variants=6,ownership=passed,allocation_128x16_bytes={allocation},allocation_budget_bytes={128 * 640 + 4096}");
        return 0;
    }

    static GraphShapeId Shape(TypeRef type, FieldCardinality cardinality = FieldCardinality.Single,
        ImmutableArray<TypeDefinition> types = default)
    {
        Shape definition = new(new("envelope"), [new(new("payload"), type, cardinality)]);
        ShapeGraph graph = new(new("nested-differential"), [definition], types);
        return new(graph, definition.Id);
    }

    static void Compare(GraphShapeId shape, string json)
    {
        var input = Encoding.UTF8.GetBytes(json);
        var layout = ObservationLayout.Create(shape);
        var candidate = new NestedLayoutCandidate(shape);
        var a = Outcome(() => NestedLayoutCandidate.ParseExisting(shape, layout, input));
        var b = Outcome(() => candidate.Parse(input, out _));
        var c = Outcome(() => candidate.ParseSeparated(input, out _));
        var adaptive = new NestedLayoutCandidate(shape, NestedLayoutStorage.DensityAware);
        var d = Outcome(() => adaptive.ParseSeparated(input, out _));
        var compact = new NestedLayoutCandidate(shape, NestedLayoutStorage.CompactOrdinal);
        var e = Outcome(() => compact.ParseSeparated(input, out _));
        foreach (var storage in new[] { NestedLayoutStorage.FixedVector, NestedLayoutStorage.CompactOrdinal })
        {
            var bound = new NestedLayoutCandidate(shape, storage, NestedValidationMode.LayoutBound);
            var f = Outcome(() => bound.ParseSeparated(input, out _));
            if (a != f) throw new InvalidOperationException($"Bound {storage} mismatch for {json}: {a} vs {f}");
        }
        if (a != e) throw new InvalidOperationException($"Packed mismatch for {json}: {a} vs {e}");
        if (a != c || a != d) throw new InvalidOperationException($"Adaptive/separated mismatch for {json}: {a} vs {c} vs {d}");
        if (a != b) throw new InvalidOperationException($"Differential mismatch for {json}: {a} vs {b}");
        // Caller bytes never become retained keys or value storage, including escaped keys and fallback.
        try
        {
            var value = new NestedLayoutCandidate(shape, NestedLayoutStorage.CompactOrdinal, NestedValidationMode.LayoutBound).ParseSeparated(input, out _);
            var before = Canonical(value);
            if (!NestedLayoutCandidate.ParseExisting(shape, layout, input).Equals(value)) throw new InvalidOperationException("Value kinds differ.");
            Array.Fill(input, (byte)0);
            if (Canonical(value) != before) throw new InvalidOperationException("Input bytes escaped.");
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { }
    }

    static string Canonical(ObservationValue value)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) Cohesive.Model.Serialization.CanonicalJsonWriter.WriteCanonicalObservationValue(writer, value);
        return Convert.ToHexString(output.ToArray());
    }

    static string Outcome(Func<ObservationValue> read)
    {
        try
        {
            var value = read();
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) Cohesive.Model.Serialization.CanonicalJsonWriter.WriteCanonicalObservationValue(writer, value);
            return "ok:" + Convert.ToHexString(output.ToArray()) + ":hash:" + value.GetHashCode();
        }
        catch (ArgumentException error) { return error.GetType().FullName + ":" + error.Message; }
        catch (JsonException error) { return error.GetType().FullName!; }
    }
}
