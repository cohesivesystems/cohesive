namespace Cohesive.Tests.Model;

public sealed class EntityDefinitionCreateStateTests
{
    [Theory]
    [InlineData(FieldPresence.Required, FieldNullability.Nullable)]
    [InlineData(FieldPresence.Required, FieldNullability.NonNullable)]
    [InlineData(FieldPresence.Optional, FieldNullability.Nullable)]
    [InlineData(FieldPresence.Optional, FieldNullability.NonNullable)]
    public void PresenceAndNullabilityRemainIndependentAcrossStateConstructionAndValidation(
        FieldPresence presence, FieldNullability nullability)
    {
        var definition = new EntityDefinition(new("nullable-state"),
            [new(new("value"), new ScalarTypeRef(ScalarTypeKind.String), presence: presence, nullability: nullability)]);
        Dictionary<string, ObservationValue> suppliedNull = new() { ["value"] = ObservationValue.Null };
        Dictionary<string, ObservationValue> absent = [];
        if (nullability == FieldNullability.Nullable)
        {
            var state = definition.CreateState("one", suppliedNull);
            Assert.Equal(ObservationValue.Null, state.Observation.GetField("value"));
            definition.ValidateObservation(state.Observation);
            definition.ValidateState(state);
            Assert.Equal(state.Snapshot, definition.CreateState(state.Snapshot).Snapshot);
            Assert.True(ValueContract.FromField(definition.Fields[0]).IsSatisfiedByConstant(ObservationValue.Null));
        }
        else
            Assert.Throws<SemanticRuleViolationException>(() => definition.CreateState("one", suppliedNull));

        if (presence == FieldPresence.Required)
            Assert.Throws<SemanticRuleViolationException>(() => definition.CreateState("one", absent));
        else
            Assert.False(definition.CreateState("one", absent).Observation.TryGetField("value", out _));
    }

    [Fact]
    public void InlineStateShapeIdentity_IsDeterministicAndChangesWithShapeSemantics()
    {
        var first = new EntityDefinition(new("Customer"), EntityShape("Customer", ScalarTypeKind.String));
        var equivalent = new EntityDefinition(new("Customer"), EntityShape("Customer", ScalarTypeKind.String));
        var changed = new EntityDefinition(new("Customer"), EntityShape("Customer", ScalarTypeKind.Int64));

        Assert.Equal(first.StateShape.QualifiedId, equivalent.StateShape.QualifiedId);
        Assert.NotEqual(first.StateShape.QualifiedId, changed.StateShape.QualifiedId);
        Assert.Equal(first.Shape.Id, first.StateShape.ShapeId);
    }

    [Fact]
    public void GraphBackedStateShape_RetainsDeclaredGraphRevision()
    {
        var shape = EntityShape("Customer", ScalarTypeKind.String);
        ShapeGraph graph = new(new("customer-domain/v7"), [shape]);
        var definition = new EntityDefinition(
            new("Customer"),
            new EntityShapeGraphBinding(
                new(graph.Id, shape.Id),
                Cohesive.Model.Serialization.ShapeGraphDocument.FromGraph(graph)));

        Assert.Equal(new QualifiedShapeId(graph.Id, shape.Id), definition.StateShape.QualifiedId);
        Assert.Same(graph, definition.StateShape.Graph);
    }

    [Fact]
    public void CreateState_FromObservation_PreservesObservationInstance()
    {
        var entity = new CustomerEntity();
        var observation = Cohesive.Model.Observation.Create(
            entity.Definition.StateShape,
            new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(CustomerEntity.Id)] = ObservationValue.FromString("customer-1"),
                [nameof(CustomerEntity.Name)] = ObservationValue.FromString("Acme Freight")
            });
        EntityObservationSnapshot snapshot = new(new("customer-1"), version: 7, observation);

        var state = entity.Definition.CreateState(snapshot);

        Assert.Same(observation, state.Observation);
        Assert.Equal("customer-1", state.EntityId.Value);
        Assert.Equal(7, state.Version);
    }

    [Fact]
    public void ValidateObservation_AcceptsACompleteValidObservationWithoutConstructingState()
    {
        var entity = new CustomerEntity();
        var observation = Cohesive.Model.Observation.Create(
            entity.Definition.StateShape,
            new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(CustomerEntity.Id)] = ObservationValue.FromString("customer-1"),
                [nameof(CustomerEntity.Name)] = ObservationValue.FromString("Acme Freight")
            });

        entity.Definition.ValidateObservation(observation);
    }

    [Fact]
    public void CoreObservation_RejectsValuedUnknownFieldBeforeEntityStateConstruction()
    {
        var entity = new CustomerEntity();
        var error = Assert.Throws<ArgumentException>(() => Cohesive.Model.Observation.Create(
            entity.Definition.StateShape,
            new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(CustomerEntity.Id)] = ObservationValue.FromString("customer-1"),
                [nameof(CustomerEntity.Name)] = ObservationValue.FromString("Acme Freight"),
                ["Unexpected"] = ObservationValue.FromString("value")
            }));

        Assert.Contains("unknown field 'Unexpected'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    sealed class CustomerEntity : Entity<CustomerEntity>
    {
        public CustomerEntity()
        {
            Id = WriteOnceField<string>(nameof(Id));
            Name = MutableField<string>(nameof(Name));
        }

        public Field<string> Id { get; }

        public Field<string> Name { get; }
    }

    static Shape EntityShape(string id, ScalarTypeKind valueKind) => new(
        new(id),
        [new(new("Value"), new ScalarTypeRef(valueKind))],
        role: ShapeRoles.Entity);
}
