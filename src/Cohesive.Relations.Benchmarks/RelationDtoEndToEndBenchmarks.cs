using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Canonical interpretation plus typed materialization for simple and joined relations.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RelationDtoEndToEndBenchmarks
{
    RelationDtoFixtureScenario<LoadSummaryDto> simple = null!;
    RelationDtoFixtureScenario<LoadSearchDto> joined = null!;
    ObservationObjectMapper<LoadSummaryDto> simpleObservationMapper = null!;
    ObservationObjectMapper<LoadSearchDto> joinedObservationMapper = null!;
    CompiledRelationDtoMapper<LoadSummaryDto> simpleCompiledMapper = null!;
    CompiledRelationDtoMapper<LoadSearchDto> joinedCompiledMapper = null!;

    /// <summary>Number of relation roots interpreted and materialized per operation.</summary>
    [Params(1, 32, 1024)]
    public int RowCount { get; set; }

    /// <summary>Builds deterministic evidence and precompiled observation mappers.</summary>
    [GlobalSetup]
    public void Setup()
    {
        simple = RelationDtoBenchmarkFixture.CreateSimpleScenario(RowCount);
        joined = RelationDtoBenchmarkFixture.CreateJoinedScenario(RowCount);
        simpleObservationMapper = ObservationObjectMapper
            .For<LoadSummaryDto>(simple.Observations[0].Layout)
            .Build();
        joinedObservationMapper = ObservationObjectMapper
            .For<LoadSearchDto>(joined.Observations[0].Layout)
            .Build();
        simpleCompiledMapper = RelationDtoBenchmarkSupport.CompileMapper<LoadSummaryDto>(simple.Plan);
        joinedCompiledMapper = RelationDtoBenchmarkSupport.CompileMapper<LoadSearchDto>(joined.Plan);
    }

    /// <summary>Canonical single-source interpretation followed by hand-written materialization.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("EndToEnd", "Simple")]
    public LoadSummaryDto[] HandwrittenSimple()
    {
        var execution = RelationQueryInMemoryInterpreter.Default.Execute(
            new(simple.Plan, simple.Evidence));
        return RelationDtoBenchmarkSupport.MapSimpleHandwritten(execution);
    }

    /// <summary>Canonical single-source interpretation followed by the existing observation mapper.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("EndToEnd", "Simple")]
    public LoadSummaryDto[] ExistingObservationMapperSimple()
    {
        var execution = RelationQueryInMemoryInterpreter.Default.Execute(
            new(simple.Plan, simple.Evidence));
        return RelationDtoBenchmarkSupport.MapObservations(
            RelationDtoBenchmarkSupport.ToObservations(execution),
            simpleObservationMapper);
    }

    /// <summary>Canonical single-source interpretation followed by the compiled relation mapper.</summary>
    /// <returns>Typed rows and their canonical provenance.</returns>
    [Benchmark]
    [BenchmarkCategory("EndToEnd", "Simple")]
    public RelationDtoMappingResult<LoadSummaryDto> CompiledCanonicalSimple()
    {
        var execution = RelationQueryInMemoryInterpreter.Default.Execute(
            new(simple.Plan, simple.Evidence));
        return simpleCompiledMapper.Map(execution);
    }

    /// <summary>Canonical joined interpretation followed by hand-written materialization.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("EndToEnd", "Joined")]
    public LoadSearchDto[] HandwrittenJoined()
    {
        var execution = RelationQueryInMemoryInterpreter.Default.Execute(
            new(joined.Plan, joined.Evidence));
        return RelationDtoBenchmarkSupport.MapJoinedHandwritten(execution);
    }

    /// <summary>Canonical joined interpretation followed by the existing observation mapper.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("EndToEnd", "Joined")]
    public LoadSearchDto[] ExistingObservationMapperJoined()
    {
        var execution = RelationQueryInMemoryInterpreter.Default.Execute(
            new(joined.Plan, joined.Evidence));
        return RelationDtoBenchmarkSupport.MapObservations(
            RelationDtoBenchmarkSupport.ToObservations(execution),
            joinedObservationMapper);
    }

    /// <summary>Canonical joined interpretation followed by the compiled relation mapper.</summary>
    /// <returns>Typed rows and their canonical provenance.</returns>
    [Benchmark]
    [BenchmarkCategory("EndToEnd", "Joined")]
    public RelationDtoMappingResult<LoadSearchDto> CompiledCanonicalJoined()
    {
        var execution = RelationQueryInMemoryInterpreter.Default.Execute(
            new(joined.Plan, joined.Evidence));
        return joinedCompiledMapper.Map(execution);
    }
}
