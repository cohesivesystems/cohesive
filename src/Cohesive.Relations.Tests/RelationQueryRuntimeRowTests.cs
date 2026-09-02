using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Cohesive.Model.Expressions;
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

    [Fact]
    public void CreateExecutionExpressionContext_RetainsRowAndUsesRuntimeAvailabilityOwner()
    {
        var occurrence = Occurrence("occurrence/execution", "value", "value-execution");
        var row = RelationQueryRuntimeRow.FromBinding(
            occurrence.Binding,
            RelationQueryRuntimeBinding.FromObservation(occurrence, ObservationValue.FromString("one")));
        IReadOnlyDictionary<string, ObservationValue> parameters =
            new ReadOnlyDictionary<string, ObservationValue>(
                new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["parameter"] = ObservationValue.FromString("value")
                });
        var availability = new RecordingRuntimeAvailability(row);

        var context = row.CreateExecutionExpressionContext(
            occurrence.Binding,
            parameters,
            availability);

        Assert.Same(row.ExpressionBindings, context.Bindings);
        Assert.Same(parameters, context.Parameters);
        Assert.True(context.IsFieldAvailable(occurrence.Binding, FieldPath.FromField("field")));
        Assert.False(context.IsParameterAvailable("parameter"));
        Assert.True(context.IsCapabilityAvailable(ExprCapabilities.Field));
        Assert.Same(row, availability.LastRow);
    }

    [Fact]
    public void OutputRowFromPrevalidatedExecution_RetainsCanonicalStores()
    {
        var first = Occurrence("occurrence/a", "first", "first-a");
        var second = Occurrence("occurrence/b", "second", "second-b");
        var occurrences = ImmutableArray.Create(first, second);
        var gaps = ImmutableArray.Create(
            new RelationRequirementGapId("gap/a"),
            new RelationRequirementGapId("gap/b"));

        var output = RelationQueryOutputRow.FromPrevalidatedExecution(
            LoadCustomerRelationFixture.LoadShapeId,
            ObservationValue.EmptyObject,
            identity: null,
            root: first,
            occurrences,
            gaps);

        Assert.True(occurrences == output.InputOccurrences);
        Assert.True(gaps == output.UnresolvedGaps);
        Assert.Same(first, output.Root);
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

    sealed class RecordingRuntimeAvailability(RelationQueryRuntimeRow expectedRow)
        : IRelationQueryExpressionRuntimeAvailability
    {
        public RelationQueryRuntimeRow? LastRow { get; private set; }

        public bool IsFieldAvailable(
            RelationQueryRuntimeRow row,
            ValueBindingId binding,
            FieldPath path)
        {
            Assert.Same(expectedRow, row);
            LastRow = row;
            return true;
        }

        public bool IsParameterAvailable(string parameter) => false;

        public bool IsCapabilityAvailable(ExprCapabilityId capability) => true;
    }
}
