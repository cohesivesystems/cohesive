using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Physical;
using CoreObservation = Cohesive.Model.Observation;

namespace Cohesive.Relations.Tests;

public sealed class IndexedObservationOccurrenceTests
{
    [Fact]
    public void FromObservation_PreservesQualifiedShapeFieldsOccurrenceAndLineage()
    {
        var shape = CustomerShape("customer-graph/v1");
        var semantic = Observe(
            shape,
            ("name", ObservationValue.FromString("Ada")),
            ("count", ObservationValue.FromInt64(3)),
            ("note", ObservationValue.Null));
        var occurrence = Occurrence(shape, "occurrence/customer-1", "customer-1");
        var layout = new ObservationLayout(shape.ShapeId, ["count", "alias", "note", "name"]);
        List<FieldLineage> lineageFields = [new("name", [])];
        var lineage = new ObservationLineage(lineageFields);

        var indexed = IndexedObservationOccurrence.FromObservation(
            shape,
            occurrence,
            semantic,
            layout,
            lineage);
        lineageFields.Clear();

        Assert.Same(occurrence, indexed.Occurrence);
        Assert.Equal(shape.QualifiedId, indexed.ShapeId);
        Assert.Equal("Ada", indexed.GetRequiredField("name").GetString());
        Assert.Equal(3, indexed.GetRequiredField(0).GetInt32());
        Assert.Equal(ObservationValueKind.Null, indexed.GetRequiredField("note").Kind);
        Assert.False(indexed.TryGetField("alias", out _));
        Assert.Single(indexed.Lineage.Fields);
        Assert.Equal(semantic, indexed.ToObservation(shape.Graph));
    }

    [Fact]
    public void Create_ValidatesAndSnapshotsPhysicalInputs()
    {
        var shape = CustomerShape("customer-graph/v1");
        var occurrence = Occurrence(shape, "occurrence/customer-1", "customer-1");
        var fieldNames = new[] { "name", "count", "note" };
        var layout = new ObservationLayout(shape.ShapeId, fieldNames);
        var values = new[]
        {
            ObservationValue.FromString("Ada"),
            ObservationValue.FromInt64(3),
            default
        };
        var presence = new ulong[1];
        ObservationBuffer.SetHasValue(presence, 0);
        ObservationBuffer.SetHasValue(presence, 1);

        var indexed = IndexedObservationOccurrence.Create(
            shape,
            occurrence,
            layout,
            values,
            presence);
        fieldNames[0] = "changed";
        values[0] = ObservationValue.FromString("Grace");
        presence[0] = 0;

        Assert.Equal("name", indexed.Layout.FieldNames[0]);
        Assert.Equal("Ada", indexed.GetRequiredField("name").GetString());
        Assert.Equal(3, indexed.GetRequiredField("count").GetInt32());
        Assert.Equal(
            Observe(
                shape,
                ("name", ObservationValue.FromString("Ada")),
                ("count", ObservationValue.FromInt64(3))),
            indexed.ToObservation(shape.Graph));
    }

    [Fact]
    public void Create_RejectsForeignLayoutFieldsInvalidValuesAndOutOfRangePresence()
    {
        var shape = CustomerShape("customer-graph/v1");
        var occurrence = Occurrence(shape, "occurrence/customer-1", "customer-1");

        var foreignLayoutFailure = Assert.Throws<ArgumentException>(() =>
            IndexedObservationOccurrence.Create(
                shape,
                occurrence,
                new(shape.ShapeId, ["name", "foreign"]),
                new[] { ObservationValue.FromString("Ada"), default },
                new ulong[] { 1UL }));
        var invalidValueFailure = Assert.Throws<ArgumentException>(() =>
            IndexedObservationOccurrence.Create(
                shape,
                occurrence,
                new(shape.ShapeId, ["name"]),
                new[] { ObservationValue.FromInt64(7) },
                new ulong[] { 1UL }));
        var presenceFailure = Assert.Throws<ArgumentException>(() =>
            IndexedObservationOccurrence.Create(
                shape,
                occurrence,
                new(shape.ShapeId, ["name"]),
                new[] { ObservationValue.FromString("Ada") },
                new ulong[] { 3UL }));
        var lineageFailure = Assert.Throws<ArgumentException>(() =>
            IndexedObservationOccurrence.FromObservation(
                shape,
                occurrence,
                Observe(
                    shape,
                    ("name", ObservationValue.FromString("Ada")),
                    ("count", ObservationValue.FromInt64(3))),
                new(shape.ShapeId, ["name", "count", "note"]),
                new([new FieldLineage("note", [])])));

        Assert.Contains("unknown field 'foreign'", foreignLayoutFailure.Message, StringComparison.Ordinal);
        Assert.Contains("does not adhere", invalidValueFailure.Message, StringComparison.Ordinal);
        Assert.Contains("outside the observation layout", presenceFailure.Message, StringComparison.Ordinal);
        Assert.Contains("absent from the physical occurrence", lineageFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_ExecutesSharedCorePlanDirectlyAgainstIndexedAccess()
    {
        var shape = CustomerShape("customer-graph/v1");
        var indexed = IndexedObservationOccurrence.FromObservation(
            Occurrence(shape, "occurrence/customer-1", "customer-1"),
            Observe(
                shape,
                ("name", ObservationValue.FromString("Ada")),
                ("count", ObservationValue.FromInt64(3))));
        var materializer = ObservationMaterializer
            .For<Customer>(shape)
            .Map("name", customer => customer.Name)
            .Map("count", customer => customer.Count)
            .Compile();

        var customer = indexed.Materialize(materializer);
        var firstDefault = ObservationMaterializer.GetDefault<Customer>(indexed);
        var secondDefault = ObservationMaterializer.GetDefault<Customer>(indexed);

        Assert.Equal(new Customer("Ada", 3), customer);
        Assert.Same(firstDefault, secondDefault);
    }

    [Fact]
    public void QualifiedShapeIdentity_DistinguishesSameLocalShapeAcrossGraphs()
    {
        var firstShape = CustomerShape("customer-graph/v1");
        var secondShape = CustomerShape("customer-graph/v2");
        var second = IndexedObservationOccurrence.FromObservation(
            Occurrence(secondShape, "occurrence/customer-1", "customer-1"),
            Observe(
                secondShape,
                ("name", ObservationValue.FromString("Ada")),
                ("count", ObservationValue.FromInt64(3))));
        var firstMaterializer = ObservationMaterializer.For<Customer>(firstShape).Compile();

        var exception = Assert.Throws<InvalidOperationException>(() => second.Materialize(firstMaterializer));
        var projectionException = Assert.Throws<ArgumentException>(() => second.ToObservation(firstShape.Graph));

        Assert.Contains("does not match observation shape", exception.Message, StringComparison.Ordinal);
        Assert.Contains("belongs to graph", projectionException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromObservation_RoundTripsAnEmptyShapeAndBuffer()
    {
        Shape definition = new(new("marker"), []);
        ShapeGraph graph = new(new("marker-graph/v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var semantic = CoreObservation.Create(shape, new Dictionary<string, ObservationValue>());
        var indexed = IndexedObservationOccurrence.FromObservation(
            Occurrence(shape, "occurrence/marker-1", "marker-1"),
            semantic);

        Assert.Equal(0, indexed.Layout.Count);
        Assert.Empty(indexed.ValuesByOrdinal.ToArray());
        Assert.Empty(indexed.HasValueBitMask.ToArray());
        Assert.Equal(semantic, indexed.ToObservation(graph));
    }

    static GraphShapeId CustomerShape(string graphId)
    {
        Shape shape = new(
            new("customer"),
            [
                new(new("name"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("count"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                new(
                    new("note"),
                    new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable),
                new(
                    new("alias"),
                    new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Optional)
            ]);
        ShapeGraph graph = new(new(graphId), [shape]);
        return new(graph, shape.Id);
    }

    static RelationQueryObservationOccurrence Occurrence(
        GraphShapeId shape,
        string occurrenceId,
        string observationIdentity) =>
        new(new(occurrenceId), new("customer"), shape.QualifiedId, observationIdentity);

    static CoreObservation Observe(
        GraphShapeId shape,
        params (string Name, ObservationValue Value)[] fields) =>
        CoreObservation.Create(
            shape,
            fields.ToDictionary(
                static field => field.Name,
                static field => field.Value,
                StringComparer.Ordinal));

    sealed record Customer(string Name, int Count);
}

static class IndexedObservationOccurrenceTestExtensions
{
    public static ObservationValue GetRequiredField(
        this IndexedObservationOccurrence occurrence,
        string fieldIdentity) =>
        occurrence.TryGetField(fieldIdentity, out var field)
            ? field
            : throw new Xunit.Sdk.XunitException($"Expected field '{fieldIdentity}' to be present.");

    public static ObservationValue GetRequiredField(
        this IndexedObservationOccurrence occurrence,
        int ordinal) =>
        occurrence.TryGetField(ordinal, out var field)
            ? field
            : throw new Xunit.Sdk.XunitException($"Expected ordinal '{ordinal}' to be present.");
}
