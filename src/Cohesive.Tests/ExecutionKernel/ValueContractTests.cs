using System.Text.Json;
using Cohesive.Model.Expressions;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ValueContractTests
{
    [Fact]
    public void EmptyShape_ProjectsToBehaviorCompletePortableObjectContract()
    {
        var shape = new Shape(new ShapeId("Order"), []);
        var contract = ValueContract.FromShape(shape);

        var objectType = Assert.IsType<ObjectTypeRef>(contract.Type);
        Assert.Empty(objectType.Fields);
        Assert.Equal(ExprResultCategory.Object, contract.GetResultCategory());

        var json = JsonSerializer.Serialize(contract);
        var roundTrip = JsonSerializer.Deserialize<ValueContract>(json);

        Assert.Equal(contract, roundTrip);
        Assert.Equal(contract.GetHashCode(), roundTrip!.GetHashCode());
        Assert.Equal(contract.GetResultCategory(), roundTrip.GetResultCategory());
    }

    [Fact]
    public void ExpressionAnalysis_ConsumesTheSharedValueContract()
    {
        var contract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool));
        var site = new Cohesive.Model.Expressions.ExprSite(
            new("tests/shared-value-contract"),
            Expr.Const(true),
            Cohesive.Model.Expressions.ExprScope.Empty,
            new(Cohesive.Model.Expressions.ExprResultCategory.Boolean, contract));

        var analysis = Cohesive.Model.Expressions.ExprAnalyzer.Analyze(site);

        Assert.True(analysis.Validation.IsValid);
        Assert.Same(contract, site.Expectation!.Value);
        Assert.Equal(contract, analysis.KnownResult);
    }
}
