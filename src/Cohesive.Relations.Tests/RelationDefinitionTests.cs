using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Cohesive.Relations.Tests;

public sealed class RelationDefinitionTests
{
    [Fact]
    public void RelationDefinition_DuplicateSourceAliases_Throws()
    {
        var mapping = new MappingDefinition(
            id: new MappingId("proj_1"),
            name: new MappingName("OrdersProjection"),
            targetShapeId: new ShapeId("shape_out"),
            assignments:
            [
                new FieldAssignment(
                    targetField: "Output",
                    expr: Expr.Field("fld_in"))
            ]);

        Assert.Throws<ArgumentException>(() => new RelationDefinition(
            id: new RelationId("rel_orders"),
            name: new RelationName("OrdersProjection"),
            sources:
            [
                new RelationSource(new SourceAlias("o"), new ShapeId("shape_order"), SourceCardinality.Many),
                new RelationSource(new SourceAlias("o"), new ShapeId("shape_order_line"), SourceCardinality.Many)
            ],
            mappings: [mapping]));
    }

    [Fact]
    public void RelationDefinition_CapturesJoinAndFilterMetadata()
    {
        var filter = new CallExpr(
            function: "eq",
            arguments:
            [
                new FieldRefExpr(
                    path: FieldPath.FromField("Status"),
                    type: new ScalarTypeRef(ScalarTypeKind.String)
                    ),
                new LiteralExpr(new ScalarTypeRef(ScalarTypeKind.String), ObservationValue.FromString("Assigned"))
            ],
            returnType: new ScalarTypeRef(ScalarTypeKind.Bool)
        );

        var relation = new RelationDefinition(
            id: new RelationId("rel_orders_with_carrier"),
            name: new RelationName("OrdersWithCarrier"),
            sources:
            [
                new RelationSource(new SourceAlias("o"), new ShapeId("shape_order"), SourceCardinality.Many),
                new RelationSource(new SourceAlias("c"), new ShapeId("shape_carrier"), SourceCardinality.Many)
            ],
            joins:
            [
                new JoinDefinition(
                    left: new SourceAlias("o"),
                    right: new SourceAlias("c"),
                    kind: JoinKind.Inner,
                    on: Expr.Eq(Expr.Field("o.fld_order_carrier_id"), Expr.Field("c.fld_carrier_id"))
                    )
            ],
            filter: filter,
            mappings:
            [
                new MappingDefinition(
                    id: new MappingId("proj_orders"),
                    name: new MappingName("OrdersProjection"),
                    targetShapeId: new ShapeId("shape_order_projection"),
                    assignments:
                    [
                        new FieldAssignment(
                            targetField: "ProjectionStatus",
                            expr: Expr.Field("Status"))
                    ])
            ],
            metadata: new RelationMetadata(
                allowCodegen: true,
                deterministic: true,
                hints: ImmutableDictionary<string, string>.Empty
                )
        );

        Assert.Single(relation.Joins);
        Assert.NotNull(relation.Filter);
        Assert.Equal(new RelationId("rel_orders_with_carrier"), relation.Id);
    }
}
