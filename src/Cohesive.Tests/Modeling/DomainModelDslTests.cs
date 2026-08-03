using System.Text.Json.Nodes;

namespace Cohesive.Tests.Modeling;

/// <summary>
/// Tests for embedded domain-model entity-shape authoring.
/// </summary>
public sealed class DomainModelDslTests
{
    [Fact]
    public void EntityBuilder_CanDeclareFieldsAndInvariants()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Order", order => order
                .Field(name: "Id", type: DomainTypes.Guid(), field => field.WriteOnce())
                .Field(name: "Status", type: DomainTypes.Enum(
                    name: "OrderStatus",
                    members: ["Draft", "Confirmed"]))
                .Invariant(
                    name: "StatusMustBeKnown",
                    expression: Expr.Or(
                        left: Expr.Eq(Expr.Field("Status"), Expr.Const("Draft")),
                        right: Expr.Eq(Expr.Field("Status"), Expr.Const("Confirmed"))))));

        var entity = Assert.Single(model.Entities);
        var invariant = Assert.Single(entity.Invariants);

        Assert.Equal("StatusMustBeKnown", invariant.Name);
        Assert.Equal(
            ["Id", "Status"],
            entity.Fields.Select(static field => field.Name.Value).ToArray());
    }

    [Fact]
    public void EntityBuilder_FieldOverload_UsesFieldNameAsCanonicalIdentity()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Order", order => order
                .Field(name: "Status", type: DomainTypes.String())));

        var field = Assert.Single(Assert.Single(model.Entities).Fields);

        Assert.Equal("Status", field.Name.Value);
    }

    [Fact]
    public void Builders_AcceptClrObjectAnnotations()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Annotation("model.meta", new
            {
                source = "dsl",
                tags = new[] { "typed", "object" }
            })
            .Entity(name: "Order", order => order
                .Annotation("entity.meta", new
                {
                    replayable = true
                })
                .Field(name: "Id", type: DomainTypes.Guid(), field => field
                    .Annotation("field.lookup", new
                    {
                        table = "orders",
                        key = "id"
                    }))));

        var entity = Assert.Single(model.Entities);
        var field = Assert.Single(entity.Fields);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"source":"dsl","tags":["typed","object"]}"""),
            model.Annotations[new AnnotationKey("model.meta")].Value));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"replayable":true}"""),
            entity.Annotations[new AnnotationKey("entity.meta")].Value));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"table":"orders","key":"id"}"""),
            field.Annotations[new AnnotationKey("field.lookup")].Value));
    }
}
