using System.Collections.Immutable;
using System.Collections.ObjectModel;
using AutoMapper;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.TestFixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Observation = Cohesive.Relations.Model.Observation;

namespace Cohesive.Relations.Benchmarks;

static class RelationDtoBenchmarkSupport
{
    public static MapperConfiguration ConfigureAutoMapper() => ConfigureAutoMapper(static configuration =>
    {
        ConfigureAutoMapperSimple(configuration);
        ConfigureAutoMapperJoined(configuration);
    });

    public static MapperConfiguration ConfigureAutoMapperSimple() =>
        ConfigureAutoMapper(ConfigureAutoMapperSimple);

    public static MapperConfiguration ConfigureAutoMapperJoined() =>
        ConfigureAutoMapper(ConfigureAutoMapperJoined);

    public static CompiledRelationDtoMapper<TOutput> CompileMapper<TOutput>(
        CompiledRelationQueryPlan plan,
        RelationDtoMapperCompiler? compiler = null)
    {
        var result = (compiler ?? RelationDtoMapperCompiler.Default).Compile<TOutput>(plan);
        if (result.Mapper is not null)
            return result.Mapper;

        throw new InvalidOperationException(string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    public static LoadSummaryDto[] MapSimpleHandwritten(RelationQueryExecutionResult execution)
    {
        var rows = RelationRows(execution);
        var outputs = new LoadSummaryDto[rows.Length];
        for (var i = 0; i < rows.Length; i++)
            outputs[i] = MapSimpleRow(rows[i]);
        return outputs;
    }

    public static LoadSearchDto[] MapJoinedHandwritten(RelationQueryExecutionResult execution)
    {
        var rows = RelationRows(execution);
        var outputs = new LoadSearchDto[rows.Length];
        for (var i = 0; i < rows.Length; i++)
            outputs[i] = MapJoinedRow(rows[i]);
        return outputs;
    }

    public static RelationQueryOutputRow[] ToRelationRows(RelationQueryExecutionResult execution) =>
        [.. RelationRows(execution)];

    public static TOutput[] MapObservations<TOutput>(
        ImmutableArray<Observation> observations,
        IObservationObjectMapper<TOutput> mapper)
    {
        var outputs = new TOutput[observations.Length];
        for (var i = 0; i < observations.Length; i++)
            outputs[i] = mapper.Map(observations[i]);
        return outputs;
    }

    public static TOutput[] MaterializeIndexed<TOutput>(
        ImmutableArray<IndexedObservationOccurrence> observations,
        ObservationMaterializer<TOutput> materializer)
    {
        var outputs = new TOutput[observations.Length];
        for (var i = 0; i < observations.Length; i++)
            outputs[i] = observations[i].Materialize(materializer);
        return outputs;
    }

    public static ImmutableArray<IndexedObservationOccurrence> ToIndexedOccurrences<TOutput>(
        RelationDtoFixtureScenario<TOutput> scenario)
    {
        var relation = scenario.Execution.Relation
            ?? throw new InvalidOperationException("The benchmark expected a relation-terminal result.");
        var graph = scenario.Plan.Provenance.ShapeDocuments
            .Single(document => document.Graph.Id == relation.Shape.GraphId)
            .Graph;
        var shape = new GraphShapeId(graph, relation.Shape.ShapeId);
        var observations = ImmutableArray.CreateBuilder<IndexedObservationOccurrence>(scenario.Observations.Length);
        for (var index = 0; index < scenario.Observations.Length; index++)
        {
            var legacy = scenario.Observations[index];
            var semantic = Cohesive.Model.Observation.Create(
                shape,
                ObservationValue.FromObject(legacy.Fields));
            observations.Add(IndexedObservationOccurrence.FromObservation(
                shape,
                new(
                    new($"benchmark-output/{index}"),
                    new("benchmark-output"),
                    shape.QualifiedId,
                    legacy.Id),
                semantic,
                legacy.Layout,
                legacy.Lineage));
        }

        return observations.MoveToImmutable();
    }

    public static TOutput[] MapKernel<TOutput>(
        RelationQueryExecutionResult execution,
        Func<ObservationValue, TOutput> kernel)
    {
        var rows = RelationRows(execution);
        var outputs = new TOutput[rows.Length];
        for (var i = 0; i < rows.Length; i++)
            outputs[i] = kernel(rows[i].Value);
        return outputs;
    }

    public static ImmutableArray<Observation> ToObservations(RelationQueryExecutionResult execution)
    {
        var rows = RelationRows(execution);
        var observations = ImmutableArray.CreateBuilder<Observation>(rows.Length);
        foreach (var row in rows)
        {
            observations.Add(new(
                row.Shape.ShapeId,
                row.Identity?.GetRequiredString()
                    ?? row.Root?.ObservationIdentity
                    ?? row.Root?.Id.Value
                    ?? throw new InvalidOperationException("A benchmark output row requires an identity."),
                ObjectFields(row)));
        }
        return observations.MoveToImmutable();
    }

    public static RelationQueryExecutionResult ReplaceFirstField(
        RelationQueryExecutionResult execution,
        string field,
        ObservationValue replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var relation = execution.Relation
            ?? throw new ArgumentException("The execution must contain a relation terminal.", nameof(execution));
        if (relation.Rows.IsDefaultOrEmpty)
            throw new ArgumentException("The execution must contain at least one relation row.", nameof(execution));

        var source = relation.Rows[0];
        var fields = ObjectFields(source).ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        fields[field] = replacement;
        var rewritten = new RelationQueryOutputRow(
            source.Shape,
            ObservationValue.FromObject(new ReadOnlyDictionary<string, ObservationValue>(fields)),
            source.Identity,
            source.Root,
            source.InputOccurrences,
            source.UnresolvedGaps);
        var rows = relation.Rows.SetItem(0, rewritten);
        var terminal = new RelationQueryRelationResult(
            relation.Relation,
            relation.Shape,
            relation.Mode,
            relation.State,
            rows);
        return new(
            execution.Status,
            execution.Evidence,
            execution.RequirementGapAnalysis,
            terminal,
            queryResults: [],
            execution.Diagnostics);
    }

    static MapperConfiguration ConfigureAutoMapper(
        Action<IMapperConfigurationExpression> configure)
    {
        MapperConfiguration configuration = new(configure, NullLoggerFactory.Instance);
        configuration.AssertConfigurationIsValid();
        configuration.CompileMappings();
        return configuration;
    }

    static void ConfigureAutoMapperSimple(IMapperConfigurationExpression configuration) =>
        configuration
            .CreateMap<RelationQueryOutputRow, LoadSummaryDto>()
            .ForCtorParam(
                nameof(LoadSummaryDto.Id),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.LoadIdFieldName)))
            .ForCtorParam(
                nameof(LoadSummaryDto.Status),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.LoadStatusFieldName)))
            .ForCtorParam(
                nameof(LoadSummaryDto.Amount),
                options => options.MapFrom(static row => ReadDecimal(
                    row,
                    RelationDtoBenchmarkFixture.LoadAmountFieldName)));

    static void ConfigureAutoMapperJoined(IMapperConfigurationExpression configuration) =>
        configuration
            .CreateMap<RelationQueryOutputRow, LoadSearchDto>()
            .ForCtorParam(
                nameof(LoadSearchDto.Id),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.LoadIdFieldName)))
            .ForCtorParam(
                nameof(LoadSearchDto.CustomerId),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.LoadCustomerIdFieldName)))
            .ForCtorParam(
                nameof(LoadSearchDto.CustomerName),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.SearchCustomerNameFieldName)))
            .ForCtorParam(
                nameof(LoadSearchDto.CustomerType),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.SearchCustomerTypeFieldName)))
            .ForCtorParam(
                nameof(LoadSearchDto.EquipmentId),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.LoadEquipmentIdFieldName)))
            .ForCtorParam(
                nameof(LoadSearchDto.EquipmentNumber),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.SearchEquipmentNumberFieldName)))
            .ForCtorParam(
                nameof(LoadSearchDto.EquipmentType),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.SearchEquipmentTypeFieldName)))
            .ForCtorParam(
                nameof(LoadSearchDto.Status),
                options => options.MapFrom(static row => ReadString(
                    row,
                    RelationDtoBenchmarkFixture.LoadStatusFieldName)))
            .ForCtorParam(
                nameof(LoadSearchDto.Amount),
                options => options.MapFrom(static row => ReadDecimal(
                    row,
                    RelationDtoBenchmarkFixture.LoadAmountFieldName)));

    static LoadSummaryDto MapSimpleRow(RelationQueryOutputRow row)
    {
        var fields = ObjectFields(row);
        return new(
            fields[RelationDtoBenchmarkFixture.LoadIdFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.LoadStatusFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.LoadAmountFieldName].GetDecimal());
    }

    static LoadSearchDto MapJoinedRow(RelationQueryOutputRow row)
    {
        var fields = ObjectFields(row);
        return new(
            fields[RelationDtoBenchmarkFixture.LoadIdFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.LoadCustomerIdFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.SearchCustomerNameFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.SearchCustomerTypeFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.LoadEquipmentIdFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.SearchEquipmentNumberFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.SearchEquipmentTypeFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.LoadStatusFieldName].GetRequiredString(),
            fields[RelationDtoBenchmarkFixture.LoadAmountFieldName].GetDecimal());
    }

    static string ReadString(RelationQueryOutputRow row, string fieldName) =>
        ObjectFields(row)[fieldName].GetRequiredString();

    static decimal ReadDecimal(RelationQueryOutputRow row, string fieldName) =>
        ObjectFields(row)[fieldName].GetDecimal();

    static ImmutableArray<RelationQueryOutputRow> RelationRows(RelationQueryExecutionResult execution) =>
        execution.Relation?.Rows
        ?? throw new InvalidOperationException("The benchmark expected a relation-terminal result.");

    static IReadOnlyDictionary<string, ObservationValue> ObjectFields(RelationQueryOutputRow row) =>
        row.Value.Fields
        ?? throw new InvalidOperationException("The benchmark expected an object-shaped output row.");
}
