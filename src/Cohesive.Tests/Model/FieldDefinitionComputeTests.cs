namespace Cohesive.Tests.Model;

public sealed class FieldDefinitionComputeTests
{
    [Fact]
    public void FieldDefinition_ComputedMutability_RequiresComputeDefinition()
    {
        var ex = Assert.Throws<ArgumentException>(() => new FieldDefinition(
            name: new FieldName("Total"),
            type: DomainTypes.Int32(),
            role: FieldRole.Computed,
            mutability: FieldMutability.Computed));

        Assert.Equal("compute", ex.ParamName);
    }

    [Fact]
    public void FieldDefinition_NonComputedMutability_RejectsComputeDefinition()
    {
        var ex = Assert.Throws<ArgumentException>(() => new FieldDefinition(
            name: new FieldName("Total"),
            type: DomainTypes.Int32(),
            role: FieldRole.Data,
            mutability: FieldMutability.Mutable,
            compute: new ComputeDefinition(Expr.Const(1))));

        Assert.Equal("compute", ex.ParamName);
    }

    [Fact]
    public void FieldDefinition_ComputedRole_RequiresComputedMutability()
    {
        var ex = Assert.Throws<ArgumentException>(() => new FieldDefinition(
            name: new FieldName("Total"),
            type: DomainTypes.Int32(),
            role: FieldRole.Computed,
            mutability: FieldMutability.Mutable,
            compute: new ComputeDefinition(Expr.Const(1))));

        Assert.Equal("mutability", ex.ParamName);
    }

    [Fact]
    public void FieldDefinition_CapturesWriteOnceMutability()
    {
        var field = new FieldDefinition(
            name: new FieldName("Reference"),
            type: DomainTypes.String(),
            role: FieldRole.Data,
            mutability: FieldMutability.WriteOnce);

        Assert.Equal(FieldMutability.WriteOnce, field.Mutability);
    }

    [Fact]
    public void CallExpr_ExplicitEquality_UsesValueSemantics_ForArguments()
    {
        var left = new CallExpr(
            function: "concat",
            arguments:
            [
                Expr.Field("FirstName"),
                Expr.Field("LastName")
            ],
            returnType: DomainTypes.String());

        var right = new CallExpr(
            function: "concat",
            arguments:
            [
                Expr.Field("FirstName"),
                Expr.Field("LastName")
            ],
            returnType: DomainTypes.String());

        var different = new CallExpr(
            function: "concat",
            arguments:
            [
                Expr.Field("FirstName")
            ],
            returnType: DomainTypes.String());

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, different);
    }

    [Fact]
    public void AggregateExpr_ExplicitEquality_UsesValueSemantics_ForGroupBy()
    {
        var left = new AggregateExpr(
            @operator: AggregateOperator.Count,
            source: Expr.Field("Stops"),
            returnType: DomainTypes.Int32(),
            groupBy:
            [
                Expr.Field("CarrierId"),
                Expr.Field("StopReasonCode")
            ]);

        var right = new AggregateExpr(
            @operator: AggregateOperator.Count,
            source: Expr.Field("Stops"),
            returnType: DomainTypes.Int32(),
            groupBy:
            [
                Expr.Field("CarrierId"),
                Expr.Field("StopReasonCode")
            ]);

        var different = new AggregateExpr(
            @operator: AggregateOperator.Count,
            source: Expr.Field("Stops"),
            returnType: DomainTypes.Int32(),
            groupBy:
            [
                Expr.Field("CarrierId")
            ]);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, different);
    }

    [Fact]
    public void FieldDefinition_ExplicitEquality_UsesExprEquality_ForCompute()
    {
        var left = new FieldDefinition(
            name: new FieldName("DisplayName"),
            type: DomainTypes.String(),
            role: FieldRole.Computed,
            mutability: FieldMutability.Computed,
            compute: new ComputeDefinition(
                new CallExpr(
                    function: "concat",
                    arguments:
                    [
                        Expr.Field("FirstName"),
                        Expr.Field("LastName")
                    ],
                    returnType: DomainTypes.String())));

        var right = new FieldDefinition(
            name: new FieldName("DisplayName"),
            type: DomainTypes.String(),
            role: FieldRole.Computed,
            mutability: FieldMutability.Computed,
            compute: new ComputeDefinition(
                new CallExpr(
                    function: "concat",
                    arguments:
                    [
                        Expr.Field("FirstName"),
                        Expr.Field("LastName")
                    ],
                    returnType: DomainTypes.String())));

        var different = left with
        {
            Compute = new ComputeDefinition(
                new CallExpr(
                    function: "concat",
                    arguments:
                    [
                        Expr.Field("FirstName")
                    ],
                    returnType: DomainTypes.String()))
        };

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, different);
    }
}
