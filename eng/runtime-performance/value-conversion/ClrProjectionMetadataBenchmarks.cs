using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

[MemoryDiagnoser]
public class ClrProjectionMetadataBenchmarks
{
    [Params(1, 128, 1024)] public int Rows { get; set; }
    Row[] rows = null!;
    [GlobalSetup] public void Setup()
    {
        rows = Enumerable.Range(0, Rows).Select(i => new Row { Id = i.ToString() }).ToArray();
        _ = Project();
    }
    [Benchmark] public ObservationValue Project() => ObservationValue.FromObject(rows);
    public sealed class Row
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "first";
        public string Label { get; set; } = "label";
        public string? Optional { get; set; }
        [JsonIgnore] public string Hidden { get; set; } = "hidden";
    }
}
