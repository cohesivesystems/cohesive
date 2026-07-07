namespace Cohesive.Tests.Model;

public sealed class SemanticTransitionDefinitionTests
{
    [Fact]
    public void TransitionDefinition_UsesTypedFieldPathMutations()
    {
        var transition = new TransitionDefinition(
            name: "AssignCarrier",
            inputs:
            [
                new TransitionParameterDefinition(
                    name: "carrierId",
                    type: DomainTypes.String())
            ],
            preconditions:
            [
                new TransitionPreconditionDefinition(
                    name: "StatusMustBeDraft",
                    expression: Expr.Eq(
                        left: Expr.Field("fld_order_status"),
                        right: Expr.Const("Draft")))
            ],
            updates:
            [
                new FieldUpdateDefinition(
                    field: "fld_order_carrier_id",
                    valueExpression: Expr.Param("carrierId"))
            ],
            effects:
            [
                new EffectDefinition(
                    name: "OrderAssigned",
                    payload: Expr.Field("fld_order_carrier_id"))
            ],
            writeEntities:
            [
                new EntityTypeName("Order")
            ]);

        Assert.Equal("AssignCarrier", transition.Name);
        Assert.Equal([new EntityTypeName("Order")], transition.ReadEntities.ToArray());
        Assert.Equal([new EntityTypeName("Order")], transition.WriteEntities.ToArray());
        Assert.Single(transition.Updates);
        Assert.Single(transition.Effects);
        Assert.Single(transition.Inputs);
    }

    [Fact]
    public void TransitionDefinition_NormalizesEntityReadWriteSets()
    {
        var transition = new TransitionDefinition(
            name: "Sync",
            readEntities:
            [
                new EntityTypeName("Customer"),
                new EntityTypeName("Customer")
            ],
            writeEntities:
            [
                new EntityTypeName("Carrier")
            ]);

        Assert.Equal(
            [new EntityTypeName("Carrier")],
            transition.WriteEntities.ToArray());
        Assert.Equal(
            [new EntityTypeName("Carrier"), new EntityTypeName("Customer")],
            transition.ReadEntities.ToArray());
    }
}
