using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Row construction before and after retaining ordinal observation storage; decoding is outside the measured boundary.</summary>
[MemoryDiagnoser]
public class ObservationOrdinalStorageBenchmarks
{
    GraphShapeId shape;
    ObservationLayout layout = null!;
    ImmutableArray<ObservationValue> values;

    /// <summary>Representative flat, nested, collection-heavy, and bounded-wide rows.</summary>
    [Params("flat", "nested", "array", "wide")]
    public string Scenario { get; set; } = "flat";

    /// <summary>Resolves the shared layout and decoded immutable field values, warming validation outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        if (Scenario == "wide")
        {
            const int FieldCount = 4096;
            var fields = ImmutableArray.CreateBuilder<FieldDefinition>(FieldCount);
            var row = ImmutableArray.CreateBuilder<ObservationValue>(FieldCount);
            for (var index = 0; index < FieldCount; index++)
            {
                fields.Add(new(new("field" + index), new ScalarTypeRef(ScalarTypeKind.Int64)));
                row.Add(ObservationValue.FromInt64(index));
            }
            Shape definition = new(new("wide"), fields.MoveToImmutable());
            ShapeGraph graph = new(new("wide-benchmark"), [definition]);
            shape = new(graph, definition.Id);
            layout = ObservationLayout.Create(shape);
            values = row.MoveToImmutable();
        }
        else
        {
            var fixture = ObservationCreationFixture.Create(Scenario switch
            {
                "flat" => ObservationBenchmarkScenario.FlatScalar,
                "nested" => ObservationBenchmarkScenario.NestedObject,
                "array" => ObservationBenchmarkScenario.ArrayHeavy,
                _ => throw new InvalidOperationException(Scenario)
            });
            shape = fixture.Shape;
            layout = ObservationLayout.Create(shape);
            values = [.. layout.FieldIdentities.Select(name => fixture.MutableFields[name])];
        }
        var expected = DictionaryRow().ToCanonicalJsonUtf8();
        if (!expected.AsSpan().SequenceEqual(OrdinalRow().ToCanonicalJsonUtf8()))
            throw new InvalidOperationException("Ordinal storage changed canonical bytes.");
        _ = RetainImmutableRow();
    }

    /// <summary>Builds and snapshots the name-keyed row used by the original repository reader.</summary>
    /// <returns>A validated observation owning its field dictionary.</returns>
    [Benchmark(Baseline = true)]
    public Observation DictionaryRow()
    {
        var fields = new Dictionary<string, ObservationValue>(values.Length, StringComparer.Ordinal);
        for (var index = 0; index < values.Length; index++) fields.Add(layout.FieldIdentities[index], values[index]);
        return Observation.Create(shape, fields);
    }

    /// <summary>Builds one owned ordinal vector, transferring its immutable storage into the observation.</summary>
    /// <returns>A validated observation retaining the row vector and shared layout.</returns>
    [Benchmark]
    public Observation OrdinalRow()
    {
        var row = ImmutableArray.CreateBuilder<ObservationValue>(values.Length);
        for (var index = 0; index < values.Length; index++) row.Add(values[index]);
        return Observation.Create(shape, layout, row.MoveToImmutable());
    }

    /// <summary>Measures construction when immutable row storage is already available.</summary>
    /// <returns>A validated observation retaining existing immutable values.</returns>
    [Benchmark]
    public Observation RetainImmutableRow() => Observation.Create(shape, layout, values);
}
