using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Model;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Tests.Modeling;

public sealed class DomainRelationshipCompilerTests
{
    static readonly GraphId DomainGraphId = new("transportation-domain/v1");

    [Fact]
    public void Compile_EntityReferences_PreservesCustomShapesAndMatchesStandaloneAuthoring()
    {
        var model = CreateTransportationModel();

        var result = DomainRelationshipCompiler.Compile(model, DomainGraphId);

        Assert.True(result.IsValid);
        var catalog = Assert.IsType<RelationshipCatalog>(result.Catalog);
        Assert.Equal(2, catalog.Count);

        var load = Entity(model, "Load");
        var customer = Entity(model, "Customer");
        var equipment = Entity(model, "Equipment");
        Assert.Equal(new ShapeId("custom.load/v3"), load.Shape.Id);
        Assert.Equal(new ShapeId("custom.customer/v2"), customer.Shape.Id);
        Assert.Equal(new ShapeId("custom.equipment/v4"), equipment.Shape.Id);

        var expectedCustomer = StandaloneRelationship(load, "CustomerId", customer);
        var expectedEquipment = StandaloneRelationship(load, "EquipmentIds", equipment);
        Assert.Equal(expectedCustomer, catalog.GetRelationship(expectedCustomer.Id));
        Assert.Equal(expectedEquipment, catalog.GetRelationship(expectedEquipment.Id));

        var equipmentField = load.Shape.GetField("EquipmentIds");
        var customerField = load.Shape.GetField("CustomerId");
        Assert.Equal(FieldCardinality.Many, equipmentField.Cardinality);
        Assert.Equal(FieldPresence.Optional, equipmentField.Presence);
        Assert.Equal(FieldPresence.Required, customerField.Presence);
        Assert.Equal(
            RelationshipTraversalCardinality.Many,
            catalog.GetRelationship(expectedEquipment.Id).GetForwardCardinality(equipmentField));
        Assert.Equal(
            RelationshipTraversalCardinality.AtMostOne,
            catalog.GetRelationship(expectedCustomer.Id).GetForwardCardinality(customerField));

        var graph = new ShapeGraph(DomainGraphId, [.. model.Entities.Select(static entity => entity.Shape)]);
        Assert.True(RelationshipCatalogValidator.Validate(catalog, graph).IsValid);
    }

    [Fact]
    public void Compile_UnknownOrdinalTarget_EmitsDiagnosticAndWithholdsPartialCatalog()
    {
        var customer = EntityDefinition("Customer", "custom.customer/v1", [DataField("Name")]);
        var load = EntityDefinition(
            "Load",
            "custom.load/v1",
            [
                ReferenceField("CustomerId", "Customer"),
                ReferenceField("UnresolvedCustomerId", "customer")
            ]);
        var model = new DomainModelDefinition([load, customer]);

        var result = DomainRelationshipCompiler.Compile(model, DomainGraphId);

        Assert.False(result.IsValid);
        Assert.Null(result.Catalog);
        var diagnostic = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal("transitions.relationship.entityReference.targetMissing", diagnostic.Code);
        Assert.Contains("unknown entity 'customer'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("/entities/0/shape/fields/1/type/entity", diagnostic.Location);
    }

    [Fact]
    public void Compile_RequiresExplicitStableGraphId()
    {
        var model = CreateTransportationModel();

        var exception = Assert.Throws<ArgumentException>(() =>
            DomainRelationshipCompiler.Compile(model, default));

        Assert.Equal("graphId", exception.ParamName);
    }

    [Fact]
    public void Compile_InvalidShapeAndReferenceIdentities_ReturnsDiagnosticsWithoutThrowing()
    {
        var customer = new EntityDefinition(
            new EntityTypeName("Customer"),
            new Shape(
                default,
                [DataField("Name")],
                role: ShapeRoles.Entity));
        var load = EntityDefinition(
            "Load",
            "custom.load/v1",
            [
                ReferenceField("CustomerId", "Customer"),
                new FieldDefinition(
                    new FieldName("MissingTargetId"),
                    new EntityReferenceTypeRef(new EntityTypeName("Placeholder"))
                    {
                        Entity = default
                    },
                    role: FieldRole.Reference)
            ]);

        var result = DomainRelationshipCompiler.Compile(
            new DomainModelDefinition([load, customer]),
            DomainGraphId);

        Assert.False(result.IsValid);
        Assert.Null(result.Catalog);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "transitions.relationship.entity.shapeIdMissing");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "transitions.relationship.entityReference.targetNameMissing");
        Assert.Throws<ArgumentException>(() => new EntityReferenceTypeRef(default));
    }

    [Fact]
    public void EntityDefinition_AddsEntityTypeMetadataAndRejectsContradictions()
    {
        var shape = new Shape(
            new ShapeId("custom.load/v5"),
            [DataField("Number")],
            role: ShapeRoles.Entity);

        var entity = new EntityDefinition(new("Load"), shape);
        var defaultShapeEntity = new EntityDefinition(
            new("Customer"),
            [DataField("Name")]);

        Assert.Equal("Load", EntityTypeAnnotation(entity.Shape));
        Assert.Equal(shape.Id, entity.Shape.Id);
        Assert.Equal("Customer", EntityTypeAnnotation(defaultShapeEntity.Shape));

        var contradictoryShape = shape with
        {
            Annotations = shape.Annotations.SetItem(
                new(ShapeAnnotationKeys.EntityType),
                AnnotationValue.FromString("Order"))
        };
        var exception = Assert.Throws<ArgumentException>(() =>
            new EntityDefinition(new("Load"), contradictoryShape));
        Assert.Equal("shape", exception.ParamName);
    }

    static DomainModelDefinition CreateTransportationModel()
    {
        var load = EntityDefinition(
            "Load",
            "custom.load/v3",
            [
                DataField("Number"),
                ReferenceField("CustomerId", "Customer"),
                ReferenceField(
                    "EquipmentIds",
                    "Equipment",
                    cardinality: FieldCardinality.Many,
                    presence: FieldPresence.Optional)
            ]);
        var customer = EntityDefinition("Customer", "custom.customer/v2", [DataField("Name")]);
        var equipment = EntityDefinition("Equipment", "custom.equipment/v4", [DataField("Number")]);
        return new([load, customer, equipment]);
    }

    static EntityDefinition EntityDefinition(
        string entityName,
        string shapeId,
        ImmutableArray<FieldDefinition> fields) => new(
        new EntityTypeName(entityName),
        new Shape(
            new ShapeId(shapeId),
            fields,
            role: ShapeRoles.Entity));

    static FieldDefinition DataField(string name) => new(
        new FieldName(name),
        new ScalarTypeRef(ScalarTypeKind.String));

    static FieldDefinition ReferenceField(
        string name,
        string targetEntity,
        FieldCardinality cardinality = FieldCardinality.Single,
        FieldPresence presence = FieldPresence.Required) => new(
        new FieldName(name),
        new EntityReferenceTypeRef(new(targetEntity)),
        cardinality,
        presence,
        presence == FieldPresence.Required ? FieldNullability.NonNullable : FieldNullability.Nullable,
        FieldRole.Reference);

    static RelationshipDefinition StandaloneRelationship(
        EntityDefinition source,
        string sourceField,
        EntityDefinition target)
    {
        var sourceShape = new QualifiedShapeId(DomainGraphId, source.Shape.Id);
        var targetShape = new QualifiedShapeId(DomainGraphId, target.Shape.Id);
        return Relationship
            .From(sourceShape)
            .Reference(FieldPath.FromField(sourceField))
            .To(targetShape);
    }

    static EntityDefinition Entity(DomainModelDefinition model, string name) =>
        Assert.Single(model.Entities, entity => string.Equals(entity.Name.Value, name, StringComparison.Ordinal));

    static string EntityTypeAnnotation(Shape shape) =>
        shape.Annotations[new(ShapeAnnotationKeys.EntityType)].Value!.GetValue<string>();
}
