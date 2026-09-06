using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Relations.Execution;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Full reference execution cost with a warmed plan, realization and fixed candidate evidence.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
public class RepresentativeSelectionBenchmarks
{
    RelationQueryExecutionRequest request = null!;

    /// <summary>Number of candidate rows, partitioned into ten candidates per key.</summary>
    [Params(100, 1000, 10000)]
    public int RowCount { get; set; }

    /// <summary>Creates immutable input evidence, warms realization, and checks winner cardinality.</summary>
    /// <exception cref="InvalidOperationException">The fixture does not produce one winner per partition.</exception>
    [GlobalSetup]
    public void Setup()
    {
        const int RowsPerPartition = 10;
        var plan = RepresentativeSelectionFixture.Compile(RepresentativeSelectionFixture.Document());
        var rows = new RepresentativeSelectionFixture.Candidate[RowCount];
        for (var index = 0; index < rows.Length; index++)
            rows[index] = new(index, ObservationValue.FromString((index / RowsPerPartition).ToString(
                System.Globalization.CultureInfo.InvariantCulture)), index % RowsPerPartition);
        request = new(plan, RepresentativeSelectionFixture.Evidence(plan, rows));
        var result = Execute();
        if (result.Status != RelationQueryExecutionStatus.Succeeded
            || result.QueryResults.Single().Rows.Length != RowCount / RowsPerPartition)
            throw new InvalidOperationException("Representative-selection benchmark fixture failed.");
    }

    /// <summary>Validates evidence, selects representatives, orders winners and projects output rows.</summary>
    /// <returns>The canonical result including selected input provenance.</returns>
    [Benchmark]
    public RelationQueryExecutionResult Execute() => RelationQueryInMemoryInterpreter.Default.Execute(request);
}
