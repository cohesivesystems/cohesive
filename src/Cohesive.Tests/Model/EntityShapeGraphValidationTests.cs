using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Model;

public sealed class EntityShapeGraphValidationTests
{
    static readonly GraphId Graph = new("tests/entity-state/order/v1");
    static readonly ShapeId OrderShape = new("order");
    static readonly TypeId StopType = new("order-stop");

    [Fact]
    public void GraphBackedEntity_ValidatesArraysOfNamedComponentsWithoutInlineProjection()
    {
        var document = ValidDocument();
        var entity = Entity(document);

        var validation = entity.ValidateShapeGraph();
        var state = entity.CreateState(
            entityId: "order-1",
            stateObject: new
            {
                id = "order-1",
                stops = new[]
                {
                    new { id = "stop-1", sequence = 1L },
                    new { id = "stop-2", sequence = 2L }
                }
            });

        Assert.True(validation.IsValid);
        Assert.Equal(new NamedTypeRef(StopType), entity.Shape.GetField("stops").Type);
        Assert.Equal(ObservationValueKind.Array, state.Fields["stops"].Kind);
        Assert.Throws<SemanticRuleViolationException>(() => entity.CreateState(
            entityId: "order-invalid",
            stateObject: new
            {
                id = "order-invalid",
                stops = new[] { new { id = "stop-1", sequence = "first" } }
            }));
        Assert.Throws<SemanticRuleViolationException>(() => entity.CreateState(
            entityId: "order-missing",
            stateObject: new
            {
                id = "order-missing",
                stops = new[] { new { id = "stop-1" } }
            }));
    }

    [Fact]
    public void InlineObjectShape_RemainsAValidStateAuthority()
    {
        EntityDefinition entity = new(
            name: new("inline-order"),
            fields:
            [
                new(
                    name: new("payload"),
                    type: new ObjectTypeRef(
                    [
                        new(
                            name: "code",
                            type: new ScalarTypeRef(ScalarTypeKind.String))
                    ]))
            ]);

        var state = entity.CreateState(
            entityId: "inline-1",
            stateObject: new { payload = new { code = "A" } });

        Assert.True(entity.ValidateShapeGraph().IsValid);
        Assert.Null(entity.ShapeGraph);
        Assert.Equal(ObservationValueKind.Object, state.Fields["payload"].Kind);
    }

    [Fact]
    public void MissingNamedType_FailsWithStructuredDiagnosticsBeforeStateValidation()
    {
        var root = RootShape();
        ShapeGraphDocument document = ShapeGraphDocument.FromGraph(new(
            id: Graph,
            shapes: [root]));
        var entity = Entity(document);

        var validation = entity.ValidateShapeGraph();
        var exception = Assert.Throws<EntityShapeGraphValidationException>(() => entity.CreateState(
            entityId: "order-1",
            stateObject: new { id = "order-1", stops = Array.Empty<object>() }));

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == EntityShapeGraphDiagnosticCodes.NamedTypeMissing);
        Assert.Equal(
            validation.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Location)),
            exception.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Location)));
    }

    [Fact]
    public void CyclicNamedTypes_FailWithStableCycleEvidence()
    {
        TypeId address = new("address");
        TypeId parent = new("parent");
        var root = new Shape(
            id: OrderShape,
            role: ShapeRoles.Entity,
            fields:
            [
                new(name: new("address"), type: new NamedTypeRef(address))
            ]);
        ShapeGraphDocument document = ShapeGraphDocument.FromGraph(new(
            id: Graph,
            shapes: [root],
            namedTypes:
            [
                new TypeDefinition.Structural(
                    id: address,
                    fields: [new(name: new("parent"), type: new NamedTypeRef(parent))]),
                new TypeDefinition.Structural(
                    id: parent,
                    fields: [new(name: new("address"), type: new NamedTypeRef(address))])
            ]));
        var entity = Entity(document);

        var first = entity.ValidateShapeGraph();
        var second = entity.ValidateShapeGraph();

        var cycle = Assert.Single(
            first.Diagnostics,
            static diagnostic => diagnostic.Code == EntityShapeGraphDiagnosticCodes.NamedTypeCycle);
        Assert.Equal(first, second);
        Assert.Contains("address -> parent -> address", cycle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionMismatchAndDuplicateIdentities_AreStructuredAndDeterministic()
    {
        var root = RootShape();
        ShapeGraphDocument document = ShapeGraphDocument.FromGraph(new(
            id: Graph,
            shapes: [root, root],
            namedTypes: [StopDefinition()]));
        EntityDefinition entity = new(
            name: new("order"),
            fields: root.Fields,
            shape: root,
            shapeGraph: new(
                shape: new(new("tests/entity-state/order/v2"), OrderShape),
                document: document));

        var validation = entity.ValidateShapeGraph();

        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == EntityShapeGraphDiagnosticCodes.RevisionIncompatible);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == EntityShapeGraphDiagnosticCodes.DuplicateIdentity);
        Assert.Equal(
            validation.Diagnostics.Order(DocumentValidationDiagnosticComparer.Ordinal),
            validation.Diagnostics);
    }

    [Fact]
    public void BuilderAndDirectIr_ProduceEquivalentGraphBackedEntityDocuments()
    {
        var document = ValidDocument();
        var direct = Entity(document);
        var authored = DomainModelDsl.Define(domain => domain.Entity(
                name: "order",
                order => order.ShapeGraph(
                    shape: new(Graph, OrderShape),
                    document: document)))
            .Entities[0];
        var options = JsonOptions();

        var directJson = JsonSerializer.Serialize(direct, options);
        var authoredJson = JsonSerializer.Serialize(authored, options);
        var restored = JsonSerializer.Deserialize<EntityDefinition>(directJson, options)
            ?? throw new InvalidOperationException("Failed to restore graph-backed entity definition.");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(directJson), JsonNode.Parse(authoredJson)));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(directJson),
            JsonNode.Parse(JsonSerializer.Serialize(restored, options))));
        Assert.True(restored.ValidateShapeGraph().IsValid);
        Assert.Equal(document.Metadata.SourceUri, restored.ShapeGraph?.Document.Metadata.SourceUri);
    }

    static EntityDefinition Entity(ShapeGraphDocument document) => new(
        name: new("order"),
        shapeGraph: new(
            shape: new(Graph, OrderShape),
            document: document));

    static ShapeGraphDocument ValidDocument() => ShapeGraphDocument.FromGraph(
        graph: new(
            id: Graph,
            shapes: [RootShape()],
            namedTypes: [StopDefinition()]),
        metadata: new(
            origin: DocumentOrigin.Generated,
            sourceUri: "tests://entity-state/order/v1"));

    static Shape RootShape() => new(
        id: OrderShape,
        role: ShapeRoles.Entity,
        fields:
        [
            new(name: new("id"), type: new ScalarTypeRef(ScalarTypeKind.String)),
            new(
                name: new("stops"),
                type: new NamedTypeRef(StopType),
                cardinality: FieldCardinality.Many)
        ]);

    static TypeDefinition.Structural StopDefinition() => new(
        id: StopType,
        fields:
        [
            new(name: new("id"), type: new ScalarTypeRef(ScalarTypeKind.String)),
            new(name: new("sequence"), type: new ScalarTypeRef(ScalarTypeKind.Int64))
        ]);

    static JsonSerializerOptions JsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
