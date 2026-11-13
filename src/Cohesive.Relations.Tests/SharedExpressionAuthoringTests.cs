using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Cohesive.Authoring;

namespace Cohesive.Relations.Tests;

public class SharedExpressionAuthoringTests
{
    public sealed record Inventory([property: JsonPropertyName("on_hand")] decimal OnHand, decimal Reserved);

    [Fact]
    public void QueryAndNeutralBindingsLowerToTheSameExpression()
    {
        var query = RelationQuery.Expression();
        var row = query.Source<Inventory>();
        var neutral = new ExpressionAuthoring();
        var input = neutral.Input<Inventory>(row.Binding.Id.Value);
        Expression<Func<Inventory, bool>> predicate = s => s.Reserved <= s.OnHand;
        var lowerer = new ExpressionLowerer(neutral.Clr.ResolveMemberPath);
        var fromQuery = lowerer.LowerValue(predicate, [row.Binding], "predicate").RequireValue();
        var fromInput = lowerer.LowerValue(predicate, [input], "predicate").RequireValue();
        Assert.Equal(row.Binding.Type, input.Type);
        Assert.Equal(fromQuery.Value, fromInput.Value);
        Assert.Equal(fromQuery.Source, fromInput.Source);
        Assert.Same(row.Binding, ((IExpressionInput<Inventory>)row.Binding).Binding);
    }
}
