using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

[MemoryDiagnoser]
public class PropertyNameProjectionBenchmarks
{
    JsonDocument document = null!;
    [Params(1, 1024)] public int Rows { get; set; }
    [Params(8, 256)] public int NameLength { get; set; }
    [Params(false, true)] public bool UniqueNames { get; set; }
    [GlobalSetup]
    public void Setup() => document = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Range(0, Rows)
        .Select(i => new Dictionary<string, int> { [UniqueNames ? i.ToString().PadLeft(NameLength, 'n') : new string('n', NameLength)] = i })));
    [Benchmark] public ObservationValue Project() => ObservationValue.FromJsonElement(document.RootElement);
    [GlobalCleanup] public void Cleanup() => document.Dispose();
}
