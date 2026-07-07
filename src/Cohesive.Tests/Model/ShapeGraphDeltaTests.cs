using System.Collections.Immutable;

namespace Cohesive.Tests.Model;

public sealed class ShapeGraphDeltaTests
{
    static readonly ShapeId Edi204ShapeId = new("shape.edi204.file");
    static readonly TypeId At5TypeId = new("type.edi204.at5");
    static readonly TypeId At5LoopTypeId = new("type.edi204.at5Loop");
    static readonly TypeId N1LoopTypeId = new("type.edi204.n1Loop");
    static readonly TypeId N1TypeId = new("type.edi204.n1");

    [Fact]
    public void Overlay_RestrictsNestedQualifierValues_AndAddsInterpretationMetadata()
    {
        var graph = Create4010Graph();
        
        var qualifierPath = GraphFieldPath.ForShape(
            Edi204ShapeId,
            FieldPath.Parse("N1Loops[].N1.EntityIdentifierCode")
            );

        ImmutableArray<string> restrictedQualifiers = ["CN", "SH"];
        AnnotationKey interpretationKey = new("edi.qualifier.SH.interpretation");
        
        var overlay = new OverlayDelta(
            id: "overlay.blue-yonder.general-mills",
            appliesTo: new(
                shapeId: Edi204ShapeId,
                standard: "x12",
                transactionSet: "204",
                release: "004010",
                tradingPartnerId: "blue-yonder",
                customerId: "general-mills"
                ),
            operations:
            [
                new RestrictAllowedValuesOperation(Target: qualifierPath, Values: restrictedQualifiers),
                new SetFieldPresenceOperation(
                    Target: GraphFieldPath.ForShape(Edi204ShapeId, FieldPath.Parse("AT5Segments")),
                    Presence: FieldPresence.Required
                    ),
                new SetFieldAnnotationOperation(
                    Target: qualifierPath,
                    Key: interpretationKey,
                    Value: AnnotationValue.FromString("shipper")
                    )
            ]);

        var resolved = ShapeGraphDeltaApplicator.Overlay(graph, overlay, resultGraphId: new("graph_edi204_4010_blue_yonder_general_mills"));
        
        var qualifier = resolved.GetStructuralType(N1TypeId).GetField("EntityIdentifierCode");
        var allowed = qualifier.Constraints.OfType<AllowedValuesConstraint>().Single();

        Assert.True(allowed.Values.SequenceEqual(restrictedQualifiers));
        Assert.True(qualifier.Annotations.ContainsKey(interpretationKey));
        Assert.Equal(FieldPresence.Required, resolved.GetShape(Edi204ShapeId).GetField("AT5Segments").Presence);
    }

    [Fact]
    public void Diff_AndEvolve_CanRepresentAt5SegmentBecomingLoopBetweenEdiVersions()
    {
        var source = Create4010Graph();
        var target = Create5030Graph();

        var graphDelta = ShapeGraphDiffer.Diff(source, target, GraphDeltaKind.Version, deltaId: "x12.204.004010_to_005030");

        Assert.Contains(graphDelta.Operations, x => x is RemoveShapeFieldOperation remove && remove.FieldName.Value == "AT5Segments");
        Assert.Contains(graphDelta.Operations, x => x is AddShapeFieldOperation add && add.Field.Name.Value == "AT5Loops");
        Assert.Contains(graphDelta.Operations, x => x is AddNamedTypeOperation add && add.Type.Id == At5LoopTypeId);
        Assert.Contains(graphDelta.Operations, x => x is SetFieldPresenceOperation set
                                                    && set.Target.TypeId == At5TypeId
                                                    && set.Target.Path.Matches("SpecialHandlingCode")
                                                    && set.Presence == FieldPresence.Required
                                                    );

        var versionDelta = VersionDelta.FromGraphDelta(
            id: "x12.204.004010_to_005030",
            rootShapeId: Edi204ShapeId,
            fromVersion: "004010",
            toVersion: "005030",
            delta: graphDelta,
            compatibility: ShapeCompatibility.RequiresMigration
            );

        var evolved = ShapeGraphDeltaApplicator.Evolve(source, versionDelta, target.Id);

        AssertEquivalentGraph(target, evolved);
    }

    [Fact]
    public void Diff_CanBeLiftedIntoOverlayDelta_ForPartnerSpecsThatRepeatBaseShapes()
    {
        var baseGraph = Create4010Graph();
        var partnerGraph = ShapeGraphDeltaApplicator.Overlay(
            baseGraph,
            delta: new(
                id: "partner.spec.delta",
                appliesTo: new(Edi204ShapeId, standard: "x12", transactionSet: "204", release: "004010", tradingPartnerId: "partner-a"),
                operations:
                [
                    new RestrictAllowedValuesOperation(
                        GraphFieldPath.ForShape(Edi204ShapeId, FieldPath.Parse("N1Loops[].N1.EntityIdentifierCode")),
                        ["BT", "CN", "SH"]),
                    new SetFieldAnnotationOperation(
                        GraphFieldPath.ForType(N1TypeId, FieldPath.Parse("EntityIdentifierCode")),
                        new AnnotationKey("edi.qualifier.BT.interpretation"),
                        AnnotationValue.FromString("bill-to"))
                ]),
            new GraphId("graph_partner_a")
            );

        var graphDelta = ShapeGraphDiffer.Diff(baseGraph, partnerGraph, GraphDeltaKind.Overlay, "partner-a.overlay");
        var overlay = OverlayDelta.FromGraphDelta(
            id: "partner-a.overlay",
            appliesTo: new(Edi204ShapeId, standard: "x12", transactionSet: "204", release: "004010", tradingPartnerId: "partner-a"),
            delta: graphDelta
            );

        var resolved = ShapeGraphDeltaApplicator.Overlay(baseGraph, overlay, partnerGraph.Id);

        AssertEquivalentGraph(partnerGraph, resolved);
    }

    [Fact]
    public void Diff_IgnoresShapeRoleAnnotation()
    {
        var source = new ShapeGraph(
            id: new("graph.source"),
            shapes:
            [
                new(
                    id: Edi204ShapeId,
                    role: ShapeRoles.Transport,
                    fields: [])
            ]);

        var target = new ShapeGraph(
            id: new("graph.target"),
            shapes:
            [
                new(
                    id: Edi204ShapeId,
                    role: ShapeRoles.Dto,
                    fields: [])
            ]);

        var delta = ShapeGraphDiffer.Diff(source, target);

        Assert.Empty(delta.Operations);
    }

    static ShapeGraph Create4010Graph()
    {
        var root = new Shape(
            id: Edi204ShapeId,
            role: ShapeRoles.Transport,
            fields:
            [
                new(
                    name: new("AT5Segments"),
                    type: new NamedTypeRef(At5TypeId),
                    cardinality: FieldCardinality.Many,
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable),
                new(
                    name: new("N1Loops"),
                    type: new NamedTypeRef(N1LoopTypeId),
                    cardinality: FieldCardinality.Many,
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable)
            ]);

        return new(
            id: new("graph_edi204_4010"),
            shapes: [root],
            namedTypes:
            [
                CreateAT5Type(requiredSpecialHandlingCode: false),
                new TypeDefinition.Structural(
                    id: N1LoopTypeId,
                    fields:
                    [
                        new(
                            name: new("N1"),
                            type: new NamedTypeRef(N1TypeId),
                            presence: FieldPresence.Required)
                    ]),
                new TypeDefinition.Structural(
                    id: N1TypeId,
                    fields:
                    [
                        new(
                            name: new("EntityIdentifierCode"),
                            type: DomainTypes.String(),
                            presence: FieldPresence.Optional,
                            constraints:
                            [
                                new AllowedValuesConstraint(["BT", "CN", "SH", "ST"])
                        ])
                    ])
            ],
            annotations: AnnotationMap.Create("x12.release", "004010"));
    }

    static ShapeGraph Create5030Graph()
    {
        var root = new Shape(
            id: Edi204ShapeId,
            role: ShapeRoles.Transport,
            fields:
            [
                new(
                    name: new("AT5Loops"),
                    type: new NamedTypeRef(At5LoopTypeId),
                    cardinality: FieldCardinality.Many,
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable,
                    constraints:
                    [
                        new OccurrenceConstraint(minimum: 0, maximum: 6)
                    ]),
                new(
                    name: new("N1Loops"),
                    type: new NamedTypeRef(N1LoopTypeId),
                    cardinality: FieldCardinality.Many,
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable)
            ]);

        return new(
            id: new("graph_edi204_5030"),
            shapes: [root],
            namedTypes:
            [
                CreateAT5Type(requiredSpecialHandlingCode: true),
                new TypeDefinition.Structural(
                    id: At5LoopTypeId,
                    fields:
                    [
                        new(
                            name: new("AT5"),
                            type: new NamedTypeRef(At5TypeId),
                            presence: FieldPresence.Required)
                    ]),
                new TypeDefinition.Structural(
                    id: N1LoopTypeId,
                    fields:
                    [
                        new(
                            name: new("N1"),
                            type: new NamedTypeRef(N1TypeId),
                            presence: FieldPresence.Required)
                    ]),
                new TypeDefinition.Structural(
                    id: N1TypeId,
                    fields:
                    [
                        new(
                            name: new("EntityIdentifierCode"),
                            type: DomainTypes.String(),
                            presence: FieldPresence.Optional,
                            constraints:
                            [
                                new AllowedValuesConstraint(["BT", "CN", "SH", "ST"])
                        ])
                    ])
            ],
            annotations: AnnotationMap.Create("x12.release", "005030"));
    }

	    static TypeDefinition.Structural CreateAT5Type(bool requiredSpecialHandlingCode) =>
	        new(
	            id: At5TypeId,
	            fields:
	            [
                new(name: new("SpecialHandlingCode"),
                    type: DomainTypes.String(),
                    presence: requiredSpecialHandlingCode ? FieldPresence.Required : FieldPresence.Optional
                    ),
                new(name: new("SpecialServicesCode"),
                    type: DomainTypes.String(),
                    presence: FieldPresence.Optional
                    )
            ]);

    static void AssertEquivalentGraph(ShapeGraph expected, ShapeGraph actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        AssertAnnotationsEqual(expected.Annotations, actual.Annotations);
        Assert.Equal(expected.Shapes.Length, actual.Shapes.Length);
        foreach (var expectedShape in expected.Shapes)
        {
            var actualShape = actual.TryGetShape(expectedShape.Id);
            Assert.NotNull(actualShape);
            Assert.Equal(expectedShape, actualShape);
        }

        Assert.Equal(expected.NamedTypes.Length, actual.NamedTypes.Length);
        foreach (var expectedType in expected.NamedTypes)
        {
            var actualType = actual.GetType(expectedType.Id);
            Assert.Equal(expectedType, actualType);
        }
    }

    static void AssertAnnotationsEqual(
        ImmutableDictionary<AnnotationKey, AnnotationValue> expected,
        ImmutableDictionary<AnnotationKey, AnnotationValue> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, value) in expected)
        {
            Assert.True(actual.TryGetValue(key, out var actualValue), $"Missing annotation '{key.Value}'.");
            Assert.Equal(value, actualValue);
        }
    }
}
