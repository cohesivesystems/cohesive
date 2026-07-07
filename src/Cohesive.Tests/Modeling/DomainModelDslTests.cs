using System.Text.Json.Nodes;

namespace Cohesive.Tests.Modeling;

/// <summary>
/// Tests for embedded domain-model DSL features.
/// </summary>
public sealed class DomainModelDslTests
{
    [Fact]
    public void EntityBuilder_CanDeclareInvariantsAndTransitions()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Order", order => order
                .Field(name: "Id", type: DomainTypes.Guid(), f => f.WriteOnce())
                .Field(name: "Status", type: DomainTypes.Enum(name: "OrderStatus", members: ["Draft", "Confirmed"]))
                .Invariant(
                    name: "StatusMustBeKnown",
                    expression: Expr.Or(
                        left: Expr.Eq(Expr.Field("Status"), Expr.Const("Draft")),
                        right: Expr.Eq(Expr.Field("Status"), Expr.Const("Confirmed")))
                    )
                .Transition(name: "Confirm", t => t
                    .Parameter(name: "note", type: DomainTypes.String(), isRequired: false)
                    .Requires(name: "StatusMustBeDraft", expression: Expr.Eq(Expr.Field("Status"), Expr.Const("Draft")))
                    .Set("Status", Expr.Const("Confirmed"))
                    .Emit(name: "OrderConfirmed")
                )
            )
        );

        var entity = Assert.Single(model.Entities);
        var invariant = Assert.Single(entity.Invariants);
        var transition = Assert.Single(entity.Transitions);
        var parameter = Assert.Single(transition.Inputs);
        var precondition = Assert.Single(transition.Preconditions);
        var update = Assert.Single(transition.Updates);
        var effect = Assert.Single(transition.Effects);

        Assert.Equal("StatusMustBeKnown", invariant.Name);
        Assert.Equal("Confirm", transition.Name);
        Assert.Equal("note", parameter.Name);
        Assert.False(parameter.IsRequired);
        Assert.Equal("StatusMustBeDraft", precondition.Name);
        Assert.Equal("Status", update.Field);
        Assert.Equal("OrderConfirmed", effect.Name);
    }

    [Fact]
    public void EntityBuilder_FieldOverload_UsesFieldNameAsCanonicalIdentity()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Order", order => order
                .Field(name: "Status", type: DomainTypes.String())
                .Transition(name: "Confirm", t => t
                    .Set(field: "Status", valueExpression: Expr.Const("Confirmed")))));

        var entity = Assert.Single(model.Entities);
        var field = Assert.Single(entity.Fields);
        var transition = Assert.Single(entity.Transitions);
        var update = Assert.Single(transition.Updates);

        Assert.Equal("Status", field.Name.Value);
        Assert.Equal("Status", update.Field);
        Assert.Equal(["Status"], transition.ReadSet.ToArray());
        Assert.Equal(["Status"], transition.WriteSet.ToArray());
    }

    [Fact]
    public void EntityBuilder_DuplicateTransitionNames_Throws()
    {
        Assert.Throws<ArgumentException>(
            testCode: () => DomainModelDsl.Define(configure: domain => domain
                .Entity(name: "Order", configure: order => order
                    .Field(name: "Id", type: DomainTypes.Guid())
                    .Transition(name: "Submit")
                    .Transition(name: "Submit"))));
    }

    [Fact]
    public void TransitionBuilder_TypedRequest_UsesDirectContinuationReference()
    {
        var applyMileage = new TransitionBuilder()
            .Parameter(name: "totalMiles", type: DomainTypes.Decimal(), isRequired: true)
            .Build(name: "ApplyMileage");

        var addStop = new TransitionBuilder()
            .Request<CalculateMileageRequest, MileageCalculatedResult>(
                payload: Expr.Call(
                    function: "object",
                    Expr.Const("revision"),
                    Expr.Const(1)))
            .Then(applyMileage)
            .Build(name: "AddStop");

        var effect = Assert.Single(addStop.Effects);
        Assert.Equal(CalculateMileageRequest.RequestName, effect.Name);
        Assert.Equal("ApplyMileage", effect.Continuation?.TransitionName);
        Assert.Same(applyMileage, effect.Continuation?.Transition);
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
                .Field(name: "Id", type: DomainTypes.Guid(), f => f
                    .Annotation("field.lookup", new
                    {
                        table = "orders",
                        key = "id"
                    }))
                .Transition(name: "AssignCarrier", t => t
                    .Annotation("transition.audit", new
                    {
                        enabled = true,
                        channels = new[] { "event", "log" }
                    }))));

        var entity = Assert.Single(model.Entities);
        var field = Assert.Single(entity.Fields);
        var transition = Assert.Single(entity.Transitions);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"source":"dsl","tags":["typed","object"]}"""),
            model.Annotations[new AnnotationKey("model.meta")].Value));

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"replayable":true}"""),
            entity.Annotations[new AnnotationKey("entity.meta")].Value));

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"table":"orders","key":"id"}"""),
            field.Annotations[new AnnotationKey("field.lookup")].Value));

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"enabled":true,"channels":["event","log"]}"""),
            transition.Annotations[new AnnotationKey("transition.audit")].Value));
    }

    sealed record MileageCalculatedResult(decimal TotalMiles);

    sealed record CalculateMileageRequest(int Revision)
        : IEffectRequest<MileageCalculatedResult>
    {
        public static string RequestName => "CalculateMileage";
    }
}
