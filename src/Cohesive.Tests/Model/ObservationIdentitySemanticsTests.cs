using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Model;
using CoreObservation = Cohesive.Model.Observation;
using PhysicalObservation = Cohesive.Relations.Model.Observation;

namespace Cohesive.Tests.Model;

public sealed class ObservationIdentitySemanticsTests
{
    [Fact]
    public void EntityObservationSnapshot_ComposesRequiredIdentityVersionAndIdentityFreeValue()
    {
        var observation = Observe("entity-state-v1", "Ada");

        EntityObservationSnapshot snapshot = new(new("customer-1"), version: 7, observation);

        Assert.Equal(new EntityId("customer-1"), snapshot.EntityId);
        Assert.Equal(7, snapshot.Version);
        Assert.Same(observation, snapshot.Observation);
        Assert.DoesNotContain(
            typeof(CoreObservation).GetProperties(),
            static property => property.Name is "Id" or "Version" or "Lineage");
    }

    [Fact]
    public void EntityObservationSnapshot_RejectsDefaultIdentityNegativeVersionAndNullValue()
    {
        var observation = Observe("entity-state-v1", "Ada");

        var identityFailure = Assert.Throws<ArgumentException>(() =>
            new EntityObservationSnapshot(default, version: 0, observation));
        var versionFailure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EntityObservationSnapshot(new("customer-1"), version: -1, observation));
        var observationFailure = Assert.Throws<ArgumentNullException>(() =>
            new EntityObservationSnapshot(new("customer-1"), version: 0, observation: null!));

        Assert.Contains("requires an entity identity", identityFailure.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be negative", versionFailure.Message, StringComparison.Ordinal);
        Assert.Equal("observation", observationFailure.ParamName);
    }

    [Fact]
    public void RelationOccurrencesIdentifyEvaluationParticipationRatherThanEntitySnapshots()
    {
        var observation = Observe("relation-input-v1", "Ada");
        RelationQueryObservationOccurrence customerSource = new(
            new("evaluation/customer-source/0"),
            new("customer-source"),
            observation.ShapeId,
            observationIdentity: "customer-1");
        RelationQueryObservationOccurrence preferredCustomer = new(
            new("evaluation/preferred-customer/0"),
            new("preferred-customer"),
            observation.ShapeId,
            observationIdentity: "customer-1");

        Assert.NotEqual(customerSource.Id, preferredCustomer.Id);
        Assert.NotEqual(customerSource.Binding, preferredCustomer.Binding);
        Assert.Equal(customerSource.ObservationIdentity, preferredCustomer.ObservationIdentity);
        Assert.Null(typeof(RelationQueryObservationOccurrence).GetProperty("Version"));
        Assert.Null(typeof(RelationQueryObservationOccurrence).GetProperty("Observation"));
    }

    [Fact]
    public void DerivedLineage_RemainsOnTheLegacyRelationsOccurrenceCompatibilityPath()
    {
        ObservationLineage lineage = new(
        [
            new FieldLineage(
                targetField: "display_name",
                contributions:
                [
                    new LineageContribution(
                        nodeId: "projection/display-name",
                        sourcePaths: [FieldPath.Parse("customer.name")],
                        expression: Expr.Const("Ada"),
                        reason: "Projected from the contributing customer occurrence")
                ])
        ]);
        PhysicalObservation derivedPhysicalOccurrence = new(
            shapeId: new("customer-display"),
            id: "evaluation/customer-display/0",
            fields: Fields(("display_name", ObservationValue.FromString("Ada"))),
            version: 0,
            lineage);
        var entitySnapshot = new EntityObservationSnapshot(
            new("customer-1"),
            version: 3,
            Observe("entity-state-v1", "Ada"));

        Assert.Same(lineage, derivedPhysicalOccurrence.Lineage);
        Assert.Null(typeof(EntityObservationSnapshot).GetProperty("Lineage"));
        Assert.Null(typeof(CoreObservation).GetProperty("Lineage"));
    }

    static CoreObservation Observe(string graphId, string name)
    {
        Shape shape = new(
            id: new("customer"),
            fields: [new(new("name"), new ScalarTypeRef(ScalarTypeKind.String))]);
        ShapeGraph graph = new(new(graphId), [shape]);
        return CoreObservation.Create(
            new GraphShapeId(graph, shape.Id),
            Fields(("name", ObservationValue.FromString(name))));
    }

    static Dictionary<string, ObservationValue> Fields(
        params (string Name, ObservationValue Value)[] fields) =>
        fields.ToDictionary(
            static field => field.Name,
            static field => field.Value,
            StringComparer.Ordinal);
}
