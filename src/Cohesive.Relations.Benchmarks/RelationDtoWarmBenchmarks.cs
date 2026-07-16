using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Columns;
using Cohesive.Model;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm typed-materialization throughput for single-source and joined relation outputs.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RelationDtoWarmBenchmarks
{
    RelationDtoFixtureScenario<LoadSummaryDto> simple = null!;
    RelationDtoFixtureScenario<LoadSearchDto> joined = null!;
    ObservationObjectMapper<LoadSummaryDto> simpleObservationMapper = null!;
    ObservationObjectMapper<LoadSearchDto> joinedObservationMapper = null!;
    CompiledRelationDtoMapper<LoadSummaryDto> simpleCompiledMapper = null!;
    CompiledRelationDtoMapper<LoadSearchDto> joinedCompiledMapper = null!;
    Func<ObservationValue, LoadSummaryDto> simpleKernel = null!;
    Func<ObservationValue, LoadSearchDto> joinedKernel = null!;

    /// <summary>Number of relation output rows materialized per benchmark operation.</summary>
    [Params(1, 32, 1024)]
    public int RowCount { get; set; }

    /// <summary>Builds deterministic executions and precompiled existing observation mappers.</summary>
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
        simpleKernel = simpleCompiledMapper.MaterializationKernel;
        joinedKernel = joinedCompiledMapper.MaterializationKernel;
    }

    /// <summary>Hand-written single-source materialization baseline.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Warm", "Simple")]
    public LoadSummaryDto[] HandwrittenSimple() =>
        RelationDtoBenchmarkSupport.MapSimpleHandwritten(simple.Execution);

    /// <summary>Generated single-source construction kernel without canonical result bookkeeping.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Simple")]
    public LoadSummaryDto[] CompiledKernelOnlySimple() =>
        RelationDtoBenchmarkSupport.MapKernel(simple.Execution, simpleKernel);

    /// <summary>Compiled canonical relation mapper for the single-source output.</summary>
    /// <returns>Typed rows and their canonical provenance.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Simple")]
    public RelationDtoMappingResult<LoadSummaryDto> CompiledCanonicalSimple() =>
        simpleCompiledMapper.Map(simple.Execution);

    /// <summary>Existing observation-object mapper for the single-source output.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Simple")]
    public LoadSummaryDto[] ExistingObservationMapperSimple() =>
        RelationDtoBenchmarkSupport.MapObservations(simple.Observations, simpleObservationMapper);

    /// <summary>Hand-written joined materialization baseline.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Warm", "Joined")]
    public LoadSearchDto[] HandwrittenJoined() =>
        RelationDtoBenchmarkSupport.MapJoinedHandwritten(joined.Execution);

    /// <summary>Generated joined construction kernel without canonical result bookkeeping.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Joined")]
    public LoadSearchDto[] CompiledKernelOnlyJoined() =>
        RelationDtoBenchmarkSupport.MapKernel(joined.Execution, joinedKernel);

    /// <summary>Compiled canonical relation mapper for the joined output.</summary>
    /// <returns>Typed rows and their canonical provenance.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Joined")]
    public RelationDtoMappingResult<LoadSearchDto> CompiledCanonicalJoined() =>
        joinedCompiledMapper.Map(joined.Execution);

    /// <summary>Existing observation-object mapper for the joined output.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Joined")]
    public LoadSearchDto[] ExistingObservationMapperJoined() =>
        RelationDtoBenchmarkSupport.MapObservations(joined.Observations, joinedObservationMapper);
}
