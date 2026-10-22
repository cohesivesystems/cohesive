using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Complete strict JSON-to-CLR ingestion, including owned parsing, shape admission and sixteen mapped reads.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationJsonMaterializationBenchmarks
{
    GraphShapeId shape;
    ObservationLayout layout = null!;
    RelationQueryObservationOccurrence occurrence = null!;
    ObservationMaterializer<ObservationFlatScalarState> names = null!;
    ObservationMaterializer<ObservationFlatScalarState> ordinals = null!;
    byte[] json = null!;

    /// <summary>Flat scalar fields or additional unmapped payloads whose complete validation remains required.</summary>
    [Params("flat", "nested", "collection", "large")]
    public string Workload { get; set; } = "flat";

    /// <summary>Prepares one exact shape/layout and compiled plans, checking all routes produce the same CLR value.</summary>
    /// <exception cref="InvalidOperationException">The equivalent ingestion routes produce different values.</exception>
    [GlobalSetup]
    public void Setup()
    {
        FieldDefinition[] fields = [.. Enumerable.Range(0, 16).Select(index =>
            new FieldDefinition(new($"Field{index:D2}"), new ScalarTypeRef(ScalarTypeKind.Int64)))];
        Shape definition = new(new("json-materialization"), Workload == "flat" ? [.. fields] :
            [.. fields, new(new("payload"), new JsonTypeRef())]);
        ShapeGraph graph = new(new("json-materialization-benchmark"), [definition]);
        shape = new(graph, definition.Id);
        layout = ObservationLayout.Create(shape);
        occurrence = new(new("record/0"), new("record"), shape.QualifiedId, "record/0");
        names = ObservationMaterializer.For<ObservationFlatScalarState>(shape).Compile();
        ordinals = ObservationMaterializer.For<ObservationFlatScalarState>(shape).Compile(layout);
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            for (var index = 15; index >= 0; index--) writer.WriteNumber($"Field{index:D2}", index);
            if (Workload != "flat")
            {
                writer.WritePropertyName("payload");
                if (Workload == "nested")
                {
                    for (var depth = 0; depth < 16; depth++) { writer.WriteStartObject(); writer.WritePropertyName("child"); }
                    writer.WriteStringValue("nested café");
                    for (var depth = 0; depth < 16; depth++) writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStartArray();
                    var count = Workload == "large" ? 4096 : 128;
                    for (var row = 0; row < count; row++)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("amount", row);
                        writer.WriteString("description", "load café");
                        writer.WriteBoolean("active", row % 2 == 0);
                        writer.WriteNull("optional");
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
            }
            writer.WriteEndObject();
        }
        json = output.WrittenSpan.ToArray();
        if (IndexedNamePlan() != StrictOrdinalPlan() || IndexedNamePlan() != IndexedOrdinalPlan())
            throw new InvalidOperationException("Strict JSON materialization routes differ.");
    }

    /// <summary>Existing strict indexed ingestion followed by name-bound materialization.</summary>
    /// <returns>The independently materialized scalar record.</returns>
    [Benchmark(Baseline = true)]
    public ObservationFlatScalarState IndexedNamePlan() => names.Materialize(IndexedObservationOccurrence.FromJson(shape, occurrence, layout, json));

    /// <summary>Existing strict indexed ingestion with a plan explicitly bound to the same layout.</summary>
    /// <returns>The independently materialized scalar record.</returns>
    [Benchmark]
    public ObservationFlatScalarState IndexedOrdinalPlan() => ordinals.Materialize(IndexedObservationOccurrence.FromJson(shape, occurrence, layout, json));

    /// <summary>Adopted strict ingestion on the ordinal-bound Core materializer.</summary>
    /// <returns>The independently materialized scalar record after complete shape admission.</returns>
    [Benchmark]
    public ObservationFlatScalarState StrictOrdinalPlan() => ordinals.MaterializeJson(json);
}
