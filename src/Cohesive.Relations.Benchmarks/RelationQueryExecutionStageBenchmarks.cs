using System.Buffers;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Model;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>
/// Allocation and throughput attribution across the canonical relation-execution stages that precede CLR or
/// JSON delivery.
/// </summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RelationQueryExecutionStageBenchmarks
{
    RelationDtoFixtureScenario<LoadSummaryDto> simple = null!;
    RelationDtoFixtureScenario<LoadSearchDto> joined = null!;
    ObservationMaterializer<LoadSummaryDto> simpleMaterializer = null!;
    ObservationMaterializer<LoadSearchDto> joinedMaterializer = null!;
    ArrayBufferWriter<byte> simpleJsonOutput = null!;
    ArrayBufferWriter<byte> joinedJsonOutput = null!;

    /// <summary>Number of relation roots represented by the benchmark evidence.</summary>
    [Params(1, 32, 1024)]
    public int RowCount { get; set; }

    /// <summary>Builds deterministic plans, evidence, executions, materializers, and reusable JSON buffers.</summary>
    [GlobalSetup]
    public void Setup()
    {
        simple = RelationDtoBenchmarkFixture.CreateSimpleScenario(RowCount);
        joined = RelationDtoBenchmarkFixture.CreateJoinedScenario(RowCount);
        simpleMaterializer = ObservationMaterializer
            .For<LoadSummaryDto>(simple.Observations[0].ShapeId)
            .Compile();
        joinedMaterializer = ObservationMaterializer
            .For<LoadSearchDto>(joined.Observations[0].ShapeId)
            .Compile();
        simpleJsonOutput = CreateWarmedJsonOutput(simple.Observations);
        joinedJsonOutput = CreateWarmedJsonOutput(joined.Observations);
    }

    /// <summary>Validates simple runtime evidence and computes requirement-gap policy decisions.</summary>
    /// <returns>Deterministic validation diagnostics, gaps, and policy decisions.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Simple")]
    public RelationRequirementGapAnalysisResult AnalyzeRequirementsSimple() =>
        RelationRequirementGapAnalyzer.Analyze(simple.Plan, simple.Evidence);

    /// <summary>Builds the simple executor's current dictionary-backed index over validated evidence.</summary>
    /// <returns>A fresh execution-oriented evidence index.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Simple")]
    public object IndexEvidenceSimple() =>
        new RelationQueryEvidenceIndex(simple.Plan, simple.Evidence);

    /// <summary>Runs canonical simple relation execution without a DTO, Observation, or JSON projection.</summary>
    /// <returns>The canonical relation execution result.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Simple")]
    public RelationQueryExecutionResult ExecuteSimple() =>
        RelationQueryInMemoryInterpreter.Default.Execute(new(simple.Plan, simple.Evidence));

    /// <summary>Projects a precomputed simple execution result into validated semantic observations.</summary>
    /// <returns>Validated semantic output observations.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Simple")]
    public ImmutableArray<Observation> ProjectObservationsSimple() =>
        RelationDtoBenchmarkSupport.ToObservations(simple.Plan, simple.Execution);

    /// <summary>Materializes precomputed simple observations into CLR values.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Simple")]
    public LoadSummaryDto[] MaterializeClrSimple() =>
        RelationDtoBenchmarkSupport.MapObservations(simple.Observations, simpleMaterializer);

    /// <summary>Writes precomputed simple observations as canonical JSON into reusable caller-owned storage.</summary>
    /// <returns>Total canonical UTF-8 bytes written across the observations.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Simple")]
    public int WriteCanonicalJsonSimple() =>
        WriteCanonicalJson(simple.Observations, simpleJsonOutput);

    /// <summary>Validates joined runtime evidence and computes requirement-gap policy decisions.</summary>
    /// <returns>Deterministic validation diagnostics, gaps, and policy decisions.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Joined")]
    public RelationRequirementGapAnalysisResult AnalyzeRequirementsJoined() =>
        RelationRequirementGapAnalyzer.Analyze(joined.Plan, joined.Evidence);

    /// <summary>Builds the joined executor's current dictionary-backed index over validated evidence.</summary>
    /// <returns>A fresh execution-oriented evidence index.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Joined")]
    public object IndexEvidenceJoined() =>
        new RelationQueryEvidenceIndex(joined.Plan, joined.Evidence);

    /// <summary>Runs canonical joined relation execution without a DTO, Observation, or JSON projection.</summary>
    /// <returns>The canonical relation execution result.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Joined")]
    public RelationQueryExecutionResult ExecuteJoined() =>
        RelationQueryInMemoryInterpreter.Default.Execute(new(joined.Plan, joined.Evidence));

    /// <summary>Projects a precomputed joined execution result into validated semantic observations.</summary>
    /// <returns>Validated semantic output observations.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Joined")]
    public ImmutableArray<Observation> ProjectObservationsJoined() =>
        RelationDtoBenchmarkSupport.ToObservations(joined.Plan, joined.Execution);

    /// <summary>Materializes precomputed joined observations into CLR values.</summary>
    /// <returns>Materialized DTOs.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Joined")]
    public LoadSearchDto[] MaterializeClrJoined() =>
        RelationDtoBenchmarkSupport.MapObservations(joined.Observations, joinedMaterializer);

    /// <summary>Writes precomputed joined observations as canonical JSON into reusable caller-owned storage.</summary>
    /// <returns>Total canonical UTF-8 bytes written across the observations.</returns>
    [Benchmark]
    [BenchmarkCategory("ExecutionStages", "Joined")]
    public int WriteCanonicalJsonJoined() =>
        WriteCanonicalJson(joined.Observations, joinedJsonOutput);

    static ArrayBufferWriter<byte> CreateWarmedJsonOutput(ImmutableArray<Observation> observations)
    {
        var output = new ArrayBufferWriter<byte>();
        _ = WriteCanonicalJson(observations, output);
        return output;
    }

    static int WriteCanonicalJson(
        ImmutableArray<Observation> observations,
        ArrayBufferWriter<byte> output)
    {
        var byteCount = 0;
        foreach (var observation in observations)
        {
            output.Clear();
            observation.WriteCanonicalJson(output);
            byteCount += output.WrittenCount;
        }

        return byteCount;
    }
}
