using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Shared output columns and durable exporters for relation benchmarks.</summary>
public sealed class RelationBenchmarkConfig : ManualConfig
{
    /// <summary>Creates the benchmark configuration without imposing a run-length job.</summary>
    public RelationBenchmarkConfig()
    {
        AddColumn(StatisticColumn.OperationsPerSecond);
        AddExporter(JsonExporter.Full);
    }
}
