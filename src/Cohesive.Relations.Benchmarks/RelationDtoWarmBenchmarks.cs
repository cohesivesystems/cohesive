using System.Collections.Immutable;
using AutoMapper;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Model;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Physical;
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
    ImmutableArray<IndexedObservationOccurrence> simpleIndexedOccurrences;
    ImmutableArray<IndexedObservationOccurrence> joinedIndexedOccurrences;
    ObservationMaterializer<LoadSummaryDto> simpleCoreMaterializer = null!;
    ObservationMaterializer<LoadSearchDto> joinedCoreMaterializer = null!;
    CompiledRelationDtoMapper<LoadSummaryDto> simpleCompiledMapper = null!;
    CompiledRelationDtoMapper<LoadSearchDto> joinedCompiledMapper = null!;
    Func<ObservationValue, LoadSummaryDto> simpleKernel = null!;
    Func<ObservationValue, LoadSearchDto> joinedKernel = null!;
    IMapper autoMapper = null!;
    RelationQueryOutputRow[] simpleCanonicalRows = null!;
    RelationQueryOutputRow[] joinedCanonicalRows = null!;

    /// <summary>Number of relation output rows materialized per benchmark operation.</summary>
    [Params(1, 32, 1024)]
    public int RowCount { get; set; }

    /// <summary>Builds deterministic executions and precompiled existing observation mappers.</summary>
    [GlobalSetup]
    public void Setup()
    {
        simple = RelationDtoBenchmarkFixture.CreateSimpleScenario(RowCount);
        joined = RelationDtoBenchmarkFixture.CreateJoinedScenario(RowCount);
        simpleIndexedOccurrences = RelationDtoBenchmarkSupport.ToIndexedOccurrences(simple);
        joinedIndexedOccurrences = RelationDtoBenchmarkSupport.ToIndexedOccurrences(joined);
        simpleCoreMaterializer = ObservationMaterializer
            .For<LoadSummaryDto>(simpleIndexedOccurrences[0].ShapeId)
            .Compile();
        joinedCoreMaterializer = ObservationMaterializer
            .For<LoadSearchDto>(joinedIndexedOccurrences[0].ShapeId)
            .Compile();
        ValidateOutput(
            RelationDtoBenchmarkSupport.MaterializeIndexed(simpleIndexedOccurrences, simpleCoreMaterializer),
            RelationDtoBenchmarkSupport.MapObservations(simple.Observations, simpleCoreMaterializer),
            "shared core indexed simple");
        ValidateOutput(
            RelationDtoBenchmarkSupport.MaterializeIndexed(joinedIndexedOccurrences, joinedCoreMaterializer),
            RelationDtoBenchmarkSupport.MapObservations(joined.Observations, joinedCoreMaterializer),
            "shared core indexed joined");
        simpleCompiledMapper = RelationDtoBenchmarkSupport.CompileMapper<LoadSummaryDto>(simple.Plan);
        joinedCompiledMapper = RelationDtoBenchmarkSupport.CompileMapper<LoadSearchDto>(joined.Plan);
        simpleKernel = simpleCompiledMapper.MaterializationKernel;
        joinedKernel = joinedCompiledMapper.MaterializationKernel;
        simpleCanonicalRows = RelationDtoBenchmarkSupport.ToRelationRows(simple.Execution);
        joinedCanonicalRows = RelationDtoBenchmarkSupport.ToRelationRows(joined.Execution);

        var autoMapperConfiguration = RelationDtoBenchmarkSupport.ConfigureAutoMapper();
        autoMapper = autoMapperConfiguration.CreateMapper();
        ValidateOutput(
            autoMapper.Map<LoadSummaryDto[]>(simpleCanonicalRows),
            RelationDtoBenchmarkSupport.MapSimpleHandwritten(simple.Execution),
            "simple");
        ValidateOutput(
            autoMapper.Map<LoadSearchDto[]>(joinedCanonicalRows),
            RelationDtoBenchmarkSupport.MapJoinedHandwritten(joined.Execution),
            "joined");
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

    /// <summary>Shared core materializer reading validated semantic observations.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Simple")]
    public LoadSummaryDto[] SharedCoreSemanticSimple() =>
        RelationDtoBenchmarkSupport.MapObservations(simple.Observations, simpleCoreMaterializer);

    /// <summary>Shared core materializer reading the explicit indexed occurrence representation.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Simple")]
    public LoadSummaryDto[] SharedCoreIndexedSimple() =>
        RelationDtoBenchmarkSupport.MaterializeIndexed(simpleIndexedOccurrences, simpleCoreMaterializer);

    /// <summary>
    /// Preconfigured AutoMapper member plan from the same canonical output-row representation.
    /// </summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Simple")]
    public LoadSummaryDto[] AutoMapperCanonicalRowsSimple() =>
        autoMapper.Map<LoadSummaryDto[]>(simpleCanonicalRows);

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

    /// <summary>Shared core materializer reading validated joined semantic observations.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Joined")]
    public LoadSearchDto[] SharedCoreSemanticJoined() =>
        RelationDtoBenchmarkSupport.MapObservations(joined.Observations, joinedCoreMaterializer);

    /// <summary>Shared core materializer reading explicit joined indexed occurrences.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Joined")]
    public LoadSearchDto[] SharedCoreIndexedJoined() =>
        RelationDtoBenchmarkSupport.MaterializeIndexed(joinedIndexedOccurrences, joinedCoreMaterializer);

    /// <summary>
    /// Preconfigured AutoMapper member plan from the same canonical joined output-row representation.
    /// </summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("Warm", "Joined")]
    public LoadSearchDto[] AutoMapperCanonicalRowsJoined() =>
        autoMapper.Map<LoadSearchDto[]>(joinedCanonicalRows);

    static void ValidateOutput<TOutput>(
        IReadOnlyList<TOutput> actual,
        IReadOnlyList<TOutput> expected,
        string scenario)
    {
        if (actual.Count == expected.Count
            && actual.SequenceEqual(expected))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The {scenario} output does not match the canonical fixture.");
    }
}
