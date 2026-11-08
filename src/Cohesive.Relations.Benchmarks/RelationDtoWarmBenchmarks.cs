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
    Func<IndexedObservationOccurrence, LoadSummaryDto> simpleOrdinal = null!;
    Func<IndexedObservationOccurrence, LoadSearchDto> joinedOrdinal = null!;
    GraphShapeId simpleShape;
    GraphShapeId joinedShape;
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
            .Compile(simpleIndexedOccurrences[0].Layout);
        joinedCoreMaterializer = ObservationMaterializer
            .For<LoadSearchDto>(joinedIndexedOccurrences[0].ShapeId)
            .Compile(joinedIndexedOccurrences[0].Layout);
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
        simpleShape = ResolveShape(simple);
        joinedShape = ResolveShape(joined);
        simpleOrdinal = RelationDtoMapperBuilder.Compile<LoadSummaryDto>(simple.Plan,
            RelationDtoMapperProfile.Conventional, RelationDtoMapperCompilationOptions.Conventional,
            simpleIndexedOccurrences[0].Layout).Mapper!.OrdinalMaterializationKernel!;
        joinedOrdinal = RelationDtoMapperBuilder.Compile<LoadSearchDto>(joined.Plan,
            RelationDtoMapperProfile.Conventional, RelationDtoMapperCompilationOptions.Conventional,
            joinedIndexedOccurrences[0].Layout).Mapper!.OrdinalMaterializationKernel!;
        ValidateOutput(MapOrdinal(simpleIndexedOccurrences, simpleOrdinal),
            RelationDtoBenchmarkSupport.MapSimpleHandwritten(simple.Execution), "ordinal simple");
        ValidateOutput(MapOrdinal(joinedIndexedOccurrences, joinedOrdinal),
            RelationDtoBenchmarkSupport.MapJoinedHandwritten(joined.Execution), "ordinal joined");
        simpleCanonicalRows = RelationDtoBenchmarkSupport.ToRelationRows(simple.Execution);
        joinedCanonicalRows = RelationDtoBenchmarkSupport.ToRelationRows(joined.Execution);

        ValidateOutput(DirectPipelineOrdinalSimple(), DirectPipelineCanonicalSimple(), "direct pipeline simple");
        ValidateOutput(DirectPipelineOrdinalJoined(), DirectPipelineCanonicalJoined(), "direct pipeline joined");
        var autoMapperConfiguration = RelationDtoBenchmarkSupport.ConfigureAutoMapper();
        ValidateOutput(MapOrdinal(Admit(simpleShape, simpleCanonicalRows, simpleIndexedOccurrences), simpleOrdinal),
            RelationDtoBenchmarkSupport.MapSimpleHandwritten(simple.Execution), "admitted ordinal simple");
        ValidateOutput(MapOrdinal(Admit(joinedShape, joinedCanonicalRows, joinedIndexedOccurrences), joinedOrdinal),
            RelationDtoBenchmarkSupport.MapJoinedHandwritten(joined.Execution), "admitted ordinal joined");
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

    /// <summary>Direct prebound reads from validated ordinal storage; includes exact layout checks.</summary>
    /// <returns>Fresh simple DTO destinations.</returns>
    [Benchmark, BenchmarkCategory("Warm", "Simple")]
    public LoadSummaryDto[] CompiledOrdinalSimple() => MapOrdinal(simpleIndexedOccurrences, simpleOrdinal);

    /// <summary>Direct prebound joined reads from validated ordinal storage.</summary>
    /// <returns>Fresh joined DTO destinations.</returns>
    [Benchmark, BenchmarkCategory("Warm", "Joined")]
    public LoadSearchDto[] CompiledOrdinalJoined() => MapOrdinal(joinedIndexedOccurrences, joinedOrdinal);

    /// <summary>Validated ordinal ingestion from already-built canonical rows.</summary>
    /// <returns>New owned ordinal occurrences; includes copying and validation.</returns>
    [Benchmark, BenchmarkCategory("Admission", "Simple")]
    public ImmutableArray<IndexedObservationOccurrence> OrdinalAdmissionSimple() =>
        Admit(simpleShape, simpleCanonicalRows, simpleIndexedOccurrences);

    /// <summary>Validated joined ordinal ingestion from canonical rows.</summary>
    /// <returns>New owned ordinal occurrences; includes copying and validation.</returns>
    [Benchmark, BenchmarkCategory("Admission", "Joined")]
    public ImmutableArray<IndexedObservationOccurrence> OrdinalAdmissionJoined() =>
        Admit(joinedShape, joinedCanonicalRows, joinedIndexedOccurrences);

    /// <summary>One-shot canonical rows to validated ordinal storage to simple DTOs.</summary>
    /// <returns>Fresh DTOs after allocating, populating and validating ordinal storage.</returns>
    [Benchmark, BenchmarkCategory("Admission", "Simple")]
    public LoadSummaryDto[] OrdinalAdmissionAndMapSimple() =>
        MapOrdinal(OrdinalAdmissionSimple(), simpleOrdinal);

    /// <summary>One-shot canonical rows to validated ordinal storage to joined DTOs.</summary>
    /// <returns>Fresh DTOs after allocating, populating and validating ordinal storage.</returns>
    [Benchmark, BenchmarkCategory("Admission", "Joined")]
    public LoadSearchDto[] OrdinalAdmissionAndMapJoined() =>
        MapOrdinal(OrdinalAdmissionJoined(), joinedOrdinal);

    static GraphShapeId ResolveShape<T>(RelationDtoFixtureScenario<T> scenario)
    {
        var shape = scenario.Execution.Relation!.Shape;
        return new(scenario.Plan.Provenance.ShapeDocuments.Single(document => document.Graph.Id == shape.GraphId).Graph,
            shape.ShapeId);
    }

    static ImmutableArray<IndexedObservationOccurrence> Admit(GraphShapeId shape,
        RelationQueryOutputRow[] rows, ImmutableArray<IndexedObservationOccurrence> templates)
    {
        var result = ImmutableArray.CreateBuilder<IndexedObservationOccurrence>(rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var template = templates[index];
            var builder = IndexedObservationOccurrence.CreateBuilder(shape, template.Occurrence, template.Layout);
            foreach (var field in rows[index].Value.Fields!) builder.SetField(field.Key, field.Value);
            result.Add(builder.Build());
        }
        return result.MoveToImmutable();
    }

    static T[] MapOrdinal<T>(ImmutableArray<IndexedObservationOccurrence> rows,
        Func<IndexedObservationOccurrence, T> kernel)
    {
        var result = new T[rows.Length];
        for (var index = 0; index < result.Length; index++) result[index] = kernel(rows[index]);
        return result;
    }

    static readonly RelationQueryInMemoryInterpreter ordinalInterpreter = new() { UseOrdinalProjection = true };

    /// <summary>Executes the canonical query, validates its output and maps DTOs.</summary>
    /// <returns>Fresh simple DTOs including execution and admission costs.</returns>
    [Benchmark, BenchmarkCategory("Pipeline", "Simple")]
    public LoadSummaryDto[] DirectPipelineCanonicalSimple() => ExecuteCanonical(simple);

    /// <summary>Executes a query directly into ordinal output, validates and maps it.</summary>
    /// <returns>Fresh simple DTOs including execution and admission costs.</returns>
    [Benchmark, BenchmarkCategory("Pipeline", "Simple")]
    public LoadSummaryDto[] DirectPipelineOrdinalSimple() => ExecuteOrdinal(simple);

    /// <summary>Executes the canonical joined query, validates output and maps DTOs.</summary>
    /// <returns>Fresh joined DTOs including execution and admission costs.</returns>
    [Benchmark, BenchmarkCategory("Pipeline", "Joined")]
    public LoadSearchDto[] DirectPipelineCanonicalJoined() => ExecuteCanonical(joined);

    /// <summary>Executes a join directly into ordinal projection output, validates and maps it.</summary>
    /// <returns>Fresh joined DTOs including execution and admission costs.</returns>
    [Benchmark, BenchmarkCategory("Pipeline", "Joined")]
    public LoadSearchDto[] DirectPipelineOrdinalJoined() => ExecuteOrdinal(joined);

    T[] ExecuteCanonical<T>(RelationDtoFixtureScenario<T> scenario)
    {
        var execution = RelationQueryInMemoryInterpreter.Default.Execute(new(scenario.Plan, scenario.Evidence));
        var shape = ResolveShape(scenario);
        var mapper = RelationDtoBenchmarkSupport.CompileMapper<T>(scenario.Plan);
        var rows = execution.Relation!.Rows;
        var output = new T[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var admitted = Observation.Create(shape, rows[index].Value.Fields!);
            output[index] = mapper.MaterializationKernel(ObservationValue.FromObject(admitted.Fields));
        }
        return output;
    }

    T[] ExecuteOrdinal<T>(RelationDtoFixtureScenario<T> scenario)
    {
        var execution = ordinalInterpreter.Execute(new(scenario.Plan, scenario.Evidence));
        var shape = ResolveShape(scenario);
        var rows = execution.Relation!.Rows;
        var reader = (IOrdinalObservationFieldReader)rows[0].Value.Fields!;
        var mapper = PreparedOrdinal<T>(scenario.Plan, reader.Layout);
        var output = new T[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var admitted = Observation.Create(shape, rows[index].Value.Fields!);
            output[index] = mapper((IOrdinalObservationFieldReader)admitted.Fields);
        }
        return output;
    }

    readonly Dictionary<(Type, ObservationLayout), object> preparedOrdinals = [];

    Func<IOrdinalObservationFieldReader, T> PreparedOrdinal<T>(Cohesive.Relations.Compilation.CompiledRelationQueryPlan plan, ObservationLayout layout)
    {
        var key = (typeof(T), layout);
        if (!preparedOrdinals.TryGetValue(key, out var prepared))
        {
            prepared = RelationDtoMapperBuilder.Compile<T>(plan, RelationDtoMapperProfile.Conventional,
                RelationDtoMapperCompilationOptions.Conventional, layout).Mapper!.OrdinalMaterializationKernel!;
            preparedOrdinals.Add(key, prepared);
        }
        return (Func<IOrdinalObservationFieldReader, T>)prepared;
    }

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
