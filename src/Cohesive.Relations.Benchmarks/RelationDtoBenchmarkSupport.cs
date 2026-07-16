using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

static class RelationDtoBenchmarkSupport
{
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
        {
            var fields = ObjectFields(rows[i]);
            outputs[i] = new(
                fields[RelationDtoBenchmarkFixture.LoadIdFieldName].GetRequiredString(),
                fields[RelationDtoBenchmarkFixture.LoadStatusFieldName].GetRequiredString(),
                fields[RelationDtoBenchmarkFixture.LoadAmountFieldName].GetDecimal());
        }
        return outputs;
    }

    public static LoadSearchDto[] MapJoinedHandwritten(RelationQueryExecutionResult execution)
    {
        var rows = RelationRows(execution);
        var outputs = new LoadSearchDto[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var fields = ObjectFields(rows[i]);
            outputs[i] = new(
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
        return outputs;
    }

    public static TOutput[] MapObservations<TOutput>(
        ImmutableArray<Observation> observations,
        IObservationObjectMapper<TOutput> mapper)
    {
        var outputs = new TOutput[observations.Length];
        for (var i = 0; i < observations.Length; i++)
            outputs[i] = mapper.Map(observations[i]);
        return outputs;
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

    static ImmutableArray<RelationQueryOutputRow> RelationRows(RelationQueryExecutionResult execution) =>
        execution.Relation?.Rows
        ?? throw new InvalidOperationException("The benchmark expected a relation-terminal result.");

    static IReadOnlyDictionary<string, ObservationValue> ObjectFields(RelationQueryOutputRow row) =>
        row.Value.Fields
        ?? throw new InvalidOperationException("The benchmark expected an object-shaped output row.");
}
