using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Model;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Structured missing-input and runtime-conversion diagnostic paths.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RelationDtoDiagnosticBenchmarks
{
    RelationDtoFixtureScenario<LoadSearchDto> missingCustomer = null!;
    RelationQueryExecutionResult malformedAmount = null!;
    CompiledRelationDtoMapper<LoadSearchDto> joinedMapper = null!;
    CompiledRelationDtoMapper<LoadSummaryDto> simpleMapper = null!;

    /// <summary>Builds one missing-input execution and one type-incompatible output row.</summary>
    [GlobalSetup]
    public void Setup()
    {
        missingCustomer = RelationDtoBenchmarkFixture.CreateJoinedScenario(
            rowCount: 1,
            RelationDtoFixtureVariant.MissingCustomer);
        var simple = RelationDtoBenchmarkFixture.CreateSimpleScenario(rowCount: 1);
        malformedAmount = RelationDtoBenchmarkSupport.ReplaceFirstField(
            simple.Execution,
            RelationDtoBenchmarkFixture.LoadAmountFieldName,
            ObservationValue.FromString("not-a-decimal"));
        joinedMapper = RelationDtoBenchmarkSupport.CompileMapper<LoadSearchDto>(missingCustomer.Plan);
        simpleMapper = RelationDtoBenchmarkSupport.CompileMapper<LoadSummaryDto>(simple.Plan);
    }

    /// <summary>Collects diagnostics for a required Customer that is absent from canonical evidence.</summary>
    /// <returns>Incomplete result retaining gaps, source rows, and mapper diagnostics.</returns>
    [Benchmark]
    [BenchmarkCategory("Diagnostics", "MissingInput")]
    public RelationDtoMappingResult<LoadSearchDto> MissingJoinedInput() =>
        joinedMapper.Map(
            missingCustomer.Execution,
            RelationDtoMappingFailurePolicy.CollectDiagnostics);

    /// <summary>Collects diagnostics for a string value supplied to a decimal DTO member.</summary>
    /// <returns>Incomplete result retaining the failed source row and conversion diagnostic.</returns>
    [Benchmark]
    [BenchmarkCategory("Diagnostics", "Conversion")]
    public RelationDtoMappingResult<LoadSummaryDto> ConversionFailure() =>
        simpleMapper.Map(
            malformedAmount,
            RelationDtoMappingFailurePolicy.CollectDiagnostics);
}
