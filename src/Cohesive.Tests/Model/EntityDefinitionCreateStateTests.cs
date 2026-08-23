using Cohesive.Relations.Model;

namespace Cohesive.Tests.Model;

public sealed class EntityDefinitionCreateStateTests
{
    [Fact]
    public void CreateState_FromObservation_PreservesObservationInstance()
    {
        var entity = new CustomerEntity();
        var observation = new Observation(
            shapeId: entity.Definition.Shape.Id,
            id: "customer-1",
            fields: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(CustomerEntity.Id)] = ObservationValue.FromString("customer-1"),
                [nameof(CustomerEntity.Name)] = ObservationValue.FromString("Acme Freight")
            },
            version: 7);

        var state = entity.Definition.CreateState(observation);

        Assert.Same(observation, state.Observation);
        Assert.Equal("customer-1", state.EntityId.Value);
        Assert.Equal(7, state.Version);
    }

    [Fact]
    public void ValidateObservation_AcceptsACompleteValidObservationWithoutConstructingState()
    {
        var entity = new CustomerEntity();
        var observation = new Observation(
            shapeId: entity.Definition.Shape.Id,
            id: "customer-1",
            fields: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(CustomerEntity.Id)] = ObservationValue.FromString("customer-1"),
                [nameof(CustomerEntity.Name)] = ObservationValue.FromString("Acme Freight")
            },
            version: 7);

        entity.Definition.ValidateObservation(observation);
    }

    [Fact]
    public void CreateState_FromObservation_RejectsValuedUnknownFieldWithoutMaterializingState()
    {
        var entity = new CustomerEntity();
        var layout = new ObservationLayout(
            schema: entity.Definition.Shape.Id,
            fieldNames:
            [
                nameof(CustomerEntity.Id),
                nameof(CustomerEntity.Name),
                "Unexpected"
            ]);
        var values = new[]
        {
            ObservationValue.FromString("customer-1"),
            ObservationValue.FromString("Acme Freight"),
            ObservationValue.FromString("value")
        };
        var hasValues = new[] { true, true, true };
        var observation = new Observation(layout, id: "customer-1", values, hasValues);

        var validationError = Assert.Throws<SemanticRuleViolationException>(() =>
            entity.Definition.ValidateObservation(observation));
        var stateError = Assert.Throws<SemanticRuleViolationException>(() =>
            entity.Definition.CreateState(observation));

        Assert.Equal(validationError.Message, stateError.Message);
        Assert.Contains("unknown field 'Unexpected'", validationError.Message, StringComparison.Ordinal);
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
}
