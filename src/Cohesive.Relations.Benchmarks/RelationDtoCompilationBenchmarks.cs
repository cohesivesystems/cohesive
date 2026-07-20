using AutoMapper;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Cold kernel compilation and warm compiler-cache lookup costs.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RelationDtoCompilationBenchmarks
{
    RelationDtoMapperCompiler cachedCompiler = null!;

    /// <summary>Warms one independent compiler with the simple and joined kernels.</summary>
    [GlobalSetup]
    public void Setup()
    {
        cachedCompiler = new();
        _ = RelationDtoBenchmarkSupport.CompileMapper<LoadSummaryDto>(
            RelationDtoBenchmarkFixture.SimplePlan,
            cachedCompiler);
        _ = RelationDtoBenchmarkSupport.CompileMapper<LoadSearchDto>(
            RelationDtoBenchmarkFixture.JoinedPlan,
            cachedCompiler);
    }

    /// <summary>Compiles a single-source kernel through a fresh compiler cache.</summary>
    /// <returns>Compilation result containing the new kernel.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Compilation", "Simple")]
    public RelationDtoMapperCompilationResult<LoadSummaryDto> ColdCompileSimple() =>
        new RelationDtoMapperCompiler().Compile<LoadSummaryDto>(RelationDtoBenchmarkFixture.SimplePlan);

    /// <summary>Retrieves the cached single-source kernel.</summary>
    /// <returns>Cached compilation result.</returns>
    [Benchmark]
    [BenchmarkCategory("Compilation", "Simple")]
    public RelationDtoMapperCompilationResult<LoadSummaryDto> CachedCompileSimple() =>
        cachedCompiler.Compile<LoadSummaryDto>(RelationDtoBenchmarkFixture.SimplePlan);

    /// <summary>
    /// Creates, validates, and eagerly compiles a fresh AutoMapper canonical-row member plan.
    /// </summary>
    /// <returns>The compiled AutoMapper configuration.</returns>
    [Benchmark]
    [BenchmarkCategory("Compilation", "Simple")]
    public MapperConfiguration ColdConfigureAndCompileAutoMapperSimple() =>
        RelationDtoBenchmarkSupport.ConfigureAutoMapperSimple();

    /// <summary>Compiles a joined kernel through a fresh compiler cache.</summary>
    /// <returns>Compilation result containing the new kernel.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Compilation", "Joined")]
    public RelationDtoMapperCompilationResult<LoadSearchDto> ColdCompileJoined() =>
        new RelationDtoMapperCompiler().Compile<LoadSearchDto>(RelationDtoBenchmarkFixture.JoinedPlan);

    /// <summary>Retrieves the cached joined kernel.</summary>
    /// <returns>Cached compilation result.</returns>
    [Benchmark]
    [BenchmarkCategory("Compilation", "Joined")]
    public RelationDtoMapperCompilationResult<LoadSearchDto> CachedCompileJoined() =>
        cachedCompiler.Compile<LoadSearchDto>(RelationDtoBenchmarkFixture.JoinedPlan);

    /// <summary>
    /// Creates, validates, and eagerly compiles a fresh AutoMapper canonical joined-row member plan.
    /// </summary>
    /// <returns>The compiled AutoMapper configuration.</returns>
    [Benchmark]
    [BenchmarkCategory("Compilation", "Joined")]
    public MapperConfiguration ColdConfigureAndCompileAutoMapperJoined() =>
        RelationDtoBenchmarkSupport.ConfigureAutoMapperJoined();
}
