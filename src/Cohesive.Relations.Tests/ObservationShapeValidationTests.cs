namespace Cohesive.Relations.Tests;

public sealed class ObservationShapeValidationTests
{
    [Fact]
    public void Observation_SupportsArraySegmentBackedBuffer()
    {
        var layout = new ObservationLayout(
            schema: new ShapeId("shape.order"),
            fieldNames:
            [
                "OrderNumber",
                "Status",
                "StopCount"
            ]);

        var valueStorage = new ObservationValue[5];
        valueStorage[1] = ObservationValue.FromString("ORD-9");
        valueStorage[2] = ObservationValue.FromString("Assigned");
        valueStorage[3] = ObservationValue.FromInt64(2);

        var maskStorage = new ulong[3];
        maskStorage[1] = (1UL << 0) | (1UL << 2);

        var observation = new Observation(
            layout: layout,
            id: "order-9",
            valuesByOrdinal: new(valueStorage, offset: 1, count: 3),
            hasValueBitMask: new(maskStorage, offset: 1, count: 1)
            );

        Assert.Equal(3, observation.ValuesByOrdinal.Length);
        Assert.Equal(1, observation.HasValueBitMask.Length);
        Assert.Equal("ORD-9", observation.GetField("OrderNumber").GetString());
        Assert.False(observation.TryGetField("Status", out _));
        Assert.Equal(2, observation.GetField("StopCount").GetInt32());
    }

    [Fact]
    public void ObservationShapeValidator_ReturnsTrue_WhenObservationAdheres()
    {
        var shape = new Shape(
            id: new("shape.order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("orderNumber"),
                    type: new ScalarTypeRef(ScalarTypeKind.String)),
                new FieldDefinition(
                    name: new FieldName("stops"),
                    type: new ScalarTypeRef(ScalarTypeKind.String),
                    cardinality: FieldCardinality.Many,
                    presence: FieldPresence.Optional)
            ]);

        var observation = new Observation(
            shapeId: shape.Id,
            id: "order-1",
            fields: Fields(new
            {
                orderNumber = "ORD-1",
                stops = new[] { "PU", "DL" }
            }));

        var valid = ObservationShapeValidator.TryValidateAgainstShape(observation, shape, out var error);

        Assert.True(valid);
        Assert.Null(error);
        observation.EnsureAdheresToShape(shape);
    }

    [Fact]
    public void ObservationShapeValidator_ReturnsFalse_WhenUnknownFieldExists()
    {
        var shape = new Shape(
            id: new ShapeId("shape.order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("orderNumber"),
                    type: new ScalarTypeRef(ScalarTypeKind.String))
            ]);

        var observation = new Observation(
            shapeId: shape.Id,
            id: "order-2",
            fields: Fields(new
            {
                orderNumber = "ORD-2",
                unexpected = "boom"
            }));

        var valid = ObservationShapeValidator.TryValidateAgainstShape(observation, shape, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("unknown field 'unexpected'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureAdheresToShape_Throws_WhenRequiredFieldIsMissing()
    {
        var shape = new Shape(
            id: new ShapeId("shape.order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("orderNumber"),
                    type: new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Required)
            ]);

        var observation = new Observation(
            layout: new ObservationLayout(shape.Id, ["orderNumber"]),
            id: "order-3",
            valuesByOrdinal: [ObservationValue.Undefined],
            hasValueByOrdinal: [false]);

        var exception = Assert.Throws<InvalidOperationException>(() => observation.EnsureAdheresToShape(shape));

        Assert.Contains("missing required field 'orderNumber'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservationShapeValidator_ReturnsFalse_WhenNonNullableFieldIsNull()
    {
        var shape = new Shape(
            id: new ShapeId("shape.order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("customer"),
                    type: new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.NonNullable)
            ]);

        var observation = new Observation(
            shapeId: shape.Id,
            id: "order-6",
            fields: Fields(new
            {
                customer = (string?)null
            }));

        var valid = ObservationShapeValidator.TryValidateAgainstShape(observation, shape, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("non-nullable", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservationShapeValidator_ValidatesNamedStructuralTypes_WithShapeGraph()
    {
        var addressTypeId = new TypeId("type.address");
        var addressType = new TypeDefinition.Structural(
            id: addressTypeId,
            fields:
            [
                new StructuralField(
                    name: new FieldName("city"),
                    type: new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Required),
                new StructuralField(
                    name: new FieldName("postalCode"),
                    type: new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Optional)
            ]);

        var shape = new Shape(
            id: new ShapeId("shape.order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("address"),
                    type: new NamedTypeRef(addressTypeId))
            ]);

        var graph = new ShapeGraph(
            id: new GraphId("graph-1"),
            shapes: [shape],
            namedTypes: [addressType]);

        var observation = new Observation(
            shapeId: shape.Id,
            id: "order-4",
            fields: Fields(new
            {
                address = new { city = "Austin", postalCode = "78701" }
            }));

        var valid = ObservationShapeValidator.TryValidateAgainstShape(observation, shape, out var error, graph);

        Assert.True(valid);
        Assert.Null(error);
    }

    [Fact]
    public void ObservationShapeValidator_ReturnsFalse_WhenNamedTypeCannotBeResolved()
    {
        var addressTypeId = new TypeId("type.address");
        var shape = new Shape(
            id: new ShapeId("shape.order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new(name: new("address"),
                    type: new NamedTypeRef(addressTypeId)
                    )
            ]);

        var observation = new Observation(
            shapeId: shape.Id,
            id: "order-5",
            fields: Fields(new
            {
                address = new { city = "Austin" }
            }));

        var valid = ObservationShapeValidator.TryValidateAgainstShape(observation, shape, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("no shape graph was provided", error, StringComparison.Ordinal);
    }

    static IReadOnlyDictionary<string, ObservationValue> Fields(object expression)
        => ObservationValue.ToFieldDictionary(expression);
}
