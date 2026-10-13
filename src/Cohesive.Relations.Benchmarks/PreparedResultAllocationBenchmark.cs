using System.Text.Json;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

internal static class PreparedResultAllocationBenchmark
{
    internal static int Run(string[] args)
    {
        var count = AllocationMeasurement.Iterations(args);
        AllocationMeasurement.Header();
        var row = ObservationValue.FromObject(new Row(7, "é<>&", [new(3), new(9)]));
        var convert = ObservationMaterializer.PrepareJsonValue<Row>();
        if (JsonSerializer.Serialize(row.Deserialize<Row>()) != JsonSerializer.Serialize(convert(row))) throw new InvalidOperationException("Conversion differs.");
        AllocationMeasurement.Measure("typed-nested-row", "json", () => row.Deserialize<Row>(), count);
        AllocationMeasurement.Measure("typed-nested-row", "prepared", () => convert(row), count);
        return 0;
    }
    public sealed record Child(int Value);
    public sealed record Row(int Number, string Name, Child[] Items);
}
