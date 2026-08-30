using System.Collections.ObjectModel;
using Cohesive.Relations.Diagnostics;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryRuntimeRowTests
{
    [Fact]
    public void Mutations_PreserveCanonicalProvenanceWithoutDroppingPriorOccurrences()
    {
        var first = Occurrence("occurrence/c", "first", "first-c");
        var second = Occurrence("occurrence/a", "second", "second-a");
        var third = Occurrence("occurrence/b", "third", "third-b");
        var row = RelationQueryRuntimeRow
            .FromBinding(
                first.Binding,
                RelationQueryRuntimeBinding.FromObservation(first, ObservationValue.FromString("first")),
                first)
            .WithBinding(
                second.Binding,
                RelationQueryRuntimeBinding.FromObservation(second, ObservationValue.FromString("second")))
            .WithOnlyBinding(
                third.Binding,
                RelationQueryRuntimeBinding.FromObservation(third, ObservationValue.FromString("third")))
            .WithAdditionalProvenance([third, second]);

        Assert.Equal(
            [second.Id, third.Id, first.Id],
            row.Provenance.Select(static occurrence => occurrence.Id));
        Assert.Equal(first, row.Root);
        Assert.Single(row.Bindings);
        Assert.True(row.Bindings.ContainsKey(third.Binding));
    }

    [Fact]
    public void Merge_RejectsConflictingOccurrencesWithTheSameIdentity()
    {
        var first = Occurrence("occurrence/shared", "first", "first-value");
        var conflicting = Occurrence("occurrence/shared", "second", "second-value");
        var firstRow = RelationQueryRuntimeRow.FromBinding(
            first.Binding,
            RelationQueryRuntimeBinding.FromObservation(first, ObservationValue.FromString("first")));
        var secondRow = RelationQueryRuntimeRow.FromBinding(
            conflicting.Binding,
            RelationQueryRuntimeBinding.FromObservation(conflicting, ObservationValue.FromString("second")));

        var exception = Assert.Throws<ArgumentException>(() => firstRow.Merge(secondRow));

        Assert.Contains(first.Id.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateExpressionContext_RetainsValidatedExecutionOwnedStores()
    {
        var occurrence = Occurrence("occurrence/one", "value", "value-one");
        var row = RelationQueryRuntimeRow.FromBinding(
            occurrence.Binding,
            RelationQueryRuntimeBinding.FromObservation(occurrence, ObservationValue.FromString("one")));
        IReadOnlyDictionary<string, ObservationValue> parameters =
            new ReadOnlyDictionary<string, ObservationValue>(
                new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["parameter"] = ObservationValue.FromString("value")
                });
        IReadOnlyList<ObservationValue> sourceRows = [ObservationValue.FromString("source")];

        var context = row.CreateExpressionContext(parameters: parameters, sourceRows: sourceRows);

        Assert.Same(row, row.ExpressionBindings);
        Assert.Same(row.ExpressionBindings, context.Bindings);
        Assert.Same(parameters, context.Parameters);
        Assert.Same(sourceRows, context.SourceRows);
    }

    static RelationQueryObservationOccurrence Occurrence(
        string occurrence,
        string binding,
        string observationIdentity) =>
        new(
            new(occurrence),
            new(binding),
            LoadCustomerRelationFixture.LoadShapeId,
            observationIdentity);
}
