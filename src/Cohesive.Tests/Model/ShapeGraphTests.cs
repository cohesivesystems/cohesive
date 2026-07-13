using System.Collections.Immutable;

namespace Cohesive.Tests.Model;

public sealed class ShapeGraphTests
{
    [Fact]
    public void FieldDefinition_ExplicitEquality_UsesValueSemantics_ForAnnotationsConstraintsAndTypeMembers()
    {
        var left = new FieldDefinition(
            name: new FieldName("Status"),
            type: DomainTypes.Enum("OrderStatus", "Accepted", "Rejected"),
            constraints:
            [
                new AllowedValuesConstraint(["Accepted", "Rejected"])
            ],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(
                new AnnotationKey("meta"),
                AnnotationValue.FromObject(new
                {
                    priority = 1,
                    domain = "edi"
                })));

        var right = new FieldDefinition(
            name: new FieldName("Status"),
            type: DomainTypes.Enum("OrderStatus", "Accepted", "Rejected"),
            constraints:
            [
                new AllowedValuesConstraint(["Rejected", "Accepted"])
            ],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(
                new AnnotationKey("meta"),
                AnnotationValue.FromObject(new
                {
                    domain = "edi",
                    priority = 1
                })));

        Assert.True(left.Equals(right));
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void FieldDefinition_ExplicitEquality_DetectsConstraintDifferences()
    {
        var left = new FieldDefinition(
            name: new FieldName("Status"),
            type: DomainTypes.String(),
            constraints:
            [
                new AllowedValuesConstraint(["Accepted", "Rejected"])
            ]);

        var right = left with
        {
            Constraints =
            [
                new AllowedValuesConstraint(["Accepted"])
            ]
        };

        Assert.False(left.Equals(right));
        Assert.NotEqual(left, right);
    }

    [Fact]
    public void TypeReferences_ExplicitEquality_UsesValueSemantics_ForObjectFieldsAndAnnotations()
    {
        var left = DomainTypes.Object(
            new ObjectFieldTypeDef(
                name: "Status",
                type: DomainTypes.Enum("OrderStatus", "Accepted", "Rejected"),
                annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(
                    new AnnotationKey("meta"),
                    AnnotationValue.FromObject(new
                    {
                        priority = 1,
                        domain = "edi"
                    }))));

        var right = DomainTypes.Object(
            new ObjectFieldTypeDef(
                name: "Status",
                type: DomainTypes.Enum("OrderStatus", "Accepted", "Rejected"),
                annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(
                    new AnnotationKey("meta"),
                    AnnotationValue.FromObject(new
                    {
                        domain = "edi",
                        priority = 1
                    }))));

        Assert.True(left.Equals(right));
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void TypeDefinitions_ExplicitEquality_UsesValueSemantics_ForArraysAndAnnotations()
    {
        var structuralLeft = new TypeDefinition.Structural(
            id: new("type.stop"),
            fields:
            [
                new(
                    name: new("StopReasonCode"),
                    type: DomainTypes.String(),
                    constraints:
                    [
                        new AllowedValuesConstraint(["CL", "PU"])
                    ])
            ],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(
                new AnnotationKey("meta"),
                AnnotationValue.FromObject(new { release = "004010", source = "base" })));

        var structuralRight = new TypeDefinition.Structural(
            id: new("type.stop"),
            fields:
            [
                new(
                    name: new("StopReasonCode"),
                    type: DomainTypes.String(),
                    constraints:
                    [
                        new AllowedValuesConstraint(["PU", "CL"])
                    ])
            ],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(
                new AnnotationKey("meta"),
                AnnotationValue.FromObject(new { source = "base", release = "004010" })));

        var enumLeft = new TypeDefinition.Enum(
            id: new("type.stopReason"),
            underlying: PrimitiveType.String,
            values:
            [
                new("PU", "Pickup"),
                new("CL", "Close")
            ]);

        var enumRight = new TypeDefinition.Enum(
            id: new("type.stopReason"),
            underlying: PrimitiveType.String,
            values:
            [
                new("PU", "Pickup"),
                new("CL", "Close")
            ]);

	        var unionLeft = new TypeDefinition.Union(
	            id: new("type.stopEvent"),
	            discriminator: new("kind"),
	            cases:
	            [
	                new("planned", new NamedTypeRef(new("type.stop")))
	            ]);

	        var unionRight = new TypeDefinition.Union(
	            id: new("type.stopEvent"),
	            discriminator: new("kind"),
	            cases:
	            [
	                new("planned", new NamedTypeRef(new("type.stop")))
	            ]);

        Assert.Equal(structuralLeft, structuralRight);
        Assert.Equal(structuralLeft.GetHashCode(), structuralRight.GetHashCode());
        Assert.Equal(enumLeft, enumRight);
        Assert.Equal(enumLeft.GetHashCode(), enumRight.GetHashCode());
        Assert.Equal(unionLeft, unionRight);
        Assert.Equal(unionLeft.GetHashCode(), unionRight.GetHashCode());
    }

    [Fact]
    public void StructuralType_TryGetField_UsesCanonicalFieldIdentity()
    {
        var type = new TypeDefinition.Structural(
            id: new("type.stop"),
            fields:
            [
                new(
                    name: new("StopReasonCode"),
                    type: DomainTypes.String())
            ]);

        Assert.True(type.TryGetField("StopReasonCode", out var field));
        Assert.Equal(new FieldName("StopReasonCode"), field.Name);
        Assert.False(type.TryGetField("Missing", out _));
        Assert.Same(field, type.GetField("StopReasonCode"));
        Assert.Throws<KeyNotFoundException>(() => type.GetField("Missing"));
    }

    [Fact]
    public void ShapeGraph_GetType_ReturnsNamedTypesOrThrows()
    {
        var structural = new TypeDefinition.Structural(
            id: new("type.stop"),
            fields:
            [
                new(
                    name: new("StopReasonCode"),
                    type: DomainTypes.String())
            ]);

        var graph = new ShapeGraph(
            id: new("graph.stop"),
            shapes:
            [
                new(
                    id: new("shape.stop"),
                    role: ShapeRoles.Entity,
                    fields:
                    [
                        new(
                            name: new("Stop"),
                            type: new NamedTypeRef(structural.Id))
                    ])
            ],
            namedTypes: [structural]);

        Assert.Same(structural, graph.GetType(structural.Id));
        Assert.Same(structural, graph.GetStructuralType(structural.Id));
        Assert.Throws<KeyNotFoundException>(() => graph.GetType(new("type.missing")));
    }

    [Fact]
    public void Shape_TryGetField_UsesCanonicalFieldName()
    {
        var shape = new Shape(
            id: new("shape.stop"),
            role: ShapeRoles.Entity,
            fields:
            [
                new(
                    name: new("StopReasonCode"),
                    type: DomainTypes.String())
            ]);

        Assert.True(shape.TryGetField("StopReasonCode", out var field));
        Assert.Equal(new FieldName("StopReasonCode"), field.Name);
        Assert.False(shape.TryGetField("Missing", out _));
        Assert.Same(field, shape.GetField("StopReasonCode"));
        Assert.Throws<KeyNotFoundException>(() => shape.GetField("Missing"));
    }

    [Fact]
    public void Shape_ExplicitEquality_UsesValueSemantics_ForAnnotationsAndCollections()
    {
        var left = new Shape(
            id: new ShapeId("shape_eq_order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("OrderId"),
                    type: DomainTypes.String())
            ],
            constraints:
            [
                new RequiredConstraint(message: "Order must have an id.")
            ],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(
                new AnnotationKey("meta"),
                AnnotationValue.FromObject(new
                {
                    region = "NA",
                    priority = 1
                }))
            );

        var right = new Shape(
            id: new ShapeId("shape_eq_order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("OrderId"),
                    type: DomainTypes.String())
            ],
            constraints:
            [
                new RequiredConstraint(message: "Order must have an id.")
            ],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(
                new AnnotationKey("meta"),
                AnnotationValue.FromObject(new
                {
                    priority = 1,
                    region = "NA"
                }))
            );

        Assert.True(left.Equals(right));
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Shape_ProjectsKindToRoleAnnotation_AndIgnoresRoleInEquality()
    {
        var entity = new Shape(
            id: new ShapeId("shape_eq_order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("OrderId"),
                    type: DomainTypes.String())
            ]);

        var projection = new Shape(
            id: new ShapeId("shape_eq_order"),
            role: ShapeRoles.Projection,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("OrderId"),
                    type: DomainTypes.String())
            ]);

        Assert.Equal("entity", entity.Role);
        Assert.Equal("projection", projection.Role);
        Assert.True(entity.HasRole(ShapeRoles.Entity));
        Assert.True(projection.HasRole(ShapeRoles.Projection));
        Assert.Equal(entity, projection);
        Assert.Equal(entity.GetHashCode(), projection.GetHashCode());
    }

    [Fact]
    public void Shape_ExplicitEquality_DetectsAnnotationDifferences()
    {
        var baseShape = new Shape(
            id: new ShapeId("shape_eq_order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("OrderId"),
                    type: DomainTypes.String())
            ],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(
                new AnnotationKey("meta"),
                AnnotationValue.FromObject(new
                {
                    priority = 1
                }))
            );

        var different = baseShape with
        {
            Annotations = baseShape.Annotations.SetItem(
                new AnnotationKey("meta"),
                AnnotationValue.FromObject(new
                {
                    priority = 2
                }))
        };

        Assert.False(baseShape.Equals(different));
        Assert.NotEqual(baseShape, different);
    }

    [Fact]
    public void ShapeGraph_ReportsDuplicateShapeIds_AndMissingNamedTypeReferences()
    {
        var first = new Shape(
            id: new ShapeId("shape_order"),
            role: ShapeRoles.Entity,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("Status"),
                    type: new NamedTypeRef(new TypeId("type_order_status")))
            ]);

        var duplicate = new Shape(
            id: new ShapeId("shape_order"),
            role: ShapeRoles.Projection,
            fields:
            [
                new FieldDefinition(
                    name: new FieldName("ProjectionId"),
                    type: DomainTypes.String())
            ]);

        var graph = new ShapeGraph(
            id: new GraphId("graph_orders"),
            shapes: [first, duplicate],
            namedTypes: []);

        Assert.True(graph.HasErrors);
        Assert.Contains(graph.Diagnostics, x => x.Id == new DiagnosticId("shape.duplicateId"));
        Assert.Contains(graph.Diagnostics, x => x.Id == new DiagnosticId("type.ref.missing"));
    }

    [Fact]
    public void ShapeGraph_ReportsMissingNamedTypeReferences_FromNestedNamedTypesAndUnionCases()
    {
        var wrapper = new TypeDefinition.Structural(
            id: new TypeId("type_wrapper"),
            fields:
            [
                new StructuralField(
                    name: new FieldName("Payload"),
                    type: new NamedTypeRef(new TypeId("type_missing_payload")))
            ]);

        var union = new TypeDefinition.Union(
            id: new TypeId("type_union"),
            discriminator: new UnionDiscriminator("Kind"),
            cases:
            [
                new UnionCase("MissingCase", new NamedTypeRef(new TypeId("type_missing_case")), "missing")
            ]);

        var graph = new ShapeGraph(
            id: new GraphId("graph_nested_type_refs"),
            shapes: [],
            namedTypes: [wrapper, union]);

        Assert.True(graph.HasErrors);
        Assert.Contains(graph.Diagnostics, x => x.TypeId == wrapper.Id && x.FieldIdentity == "Payload");
        Assert.Contains(graph.Diagnostics, x => x.TypeId == union.Id && x.FieldIdentity == "MissingCase");
    }

}
