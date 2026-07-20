using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Diagnostic-heavy joined DTO materialization at representative batch sizes.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RelationDtoDiagnosticScaleBenchmarks
{
    RelationDtoFixtureScenario<LoadSearchDto> missingCustomers = null!;
    CompiledRelationDtoMapper<LoadSearchDto> mapper = null!;

    /// <summary>Number of incomplete joined rows diagnosed per benchmark operation.</summary>
    [Params(32, 1024)]
    public int RowCount { get; set; }

    /// <summary>Builds an incomplete joined execution and validates its diagnostic cardinality.</summary>
    [GlobalSetup]
    public void Setup()
    {
        missingCustomers = RelationDtoBenchmarkFixture.CreateJoinedScenario(
            RowCount,
            RelationDtoFixtureVariant.MissingCustomer);
        mapper = RelationDtoBenchmarkSupport.CompileMapper<LoadSearchDto>(missingCustomers.Plan);
        var probe = mapper.Map(
            missingCustomers.Execution,
            RelationDtoMappingFailurePolicy.CollectDiagnostics);
        if (probe.Status != RelationDtoMappingStatus.Incomplete
            || probe.Rows.Length != 0
            || probe.FailedRows.Length != RowCount
            || probe.Diagnostics.Length < RowCount)
        {
            throw new InvalidOperationException(
                "The diagnostic-scale fixture must produce one failed mapping outcome for every input row.");
        }
    }

    /// <summary>Collects structured diagnostics for every missing joined Customer input.</summary>
    /// <returns>Incomplete typed-mapping result retaining every failed canonical source row.</returns>
    [Benchmark]
    [BenchmarkCategory("Diagnostics", "MissingInputScale")]
    public RelationDtoMappingResult<LoadSearchDto> MissingJoinedInputs() =>
        mapper.Map(
            missingCustomers.Execution,
            RelationDtoMappingFailurePolicy.CollectDiagnostics);
}
