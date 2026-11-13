using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Cohesive.Authoring;
using Cohesive.Model;

namespace Cohesive.Assurance.Tests;

public class ExpressionAuthoringTests
{
    public sealed record Inventory([property: JsonPropertyName("on_hand")] decimal OnHand, decimal Reserved);

    [Fact]
    public void ExplicitIdentitiesAreIndependentOfDeclarationOrder()
    {
        var first = new ExpressionAuthoring();
        var stock = first.Input<Inventory>("stock");
        first.Input<Inventory>("other");
        var second = new ExpressionAuthoring();
        second.Input<Inventory>("other");
        var reordered = second.Input<Inventory>("stock");
        Expression<Func<Inventory, bool>> predicate = s => s.Reserved <= s.OnHand;
        var left = new ExpressionLowerer(first.Clr.ResolveMemberPath).LowerValue(predicate, [stock], "claim").RequireValue();
        var right = new ExpressionLowerer(second.Clr.ResolveMemberPath).LowerValue(predicate, [reordered], "claim").RequireValue();
        Assert.Equal(stock.Type, reordered.Type);
        Assert.Equal(left.Value, right.Value);
    }

    [Fact]
    public void IdentifiersAreUniqueAndAutomaticIdsSkipExplicitOnes()
    {
        var author = new ExpressionAuthoring();
        author.Input<Inventory>("input/0");
        Assert.Equal("input/1", author.Input<Inventory>().Id.Value);
        Assert.Throws<ArgumentException>(() => author.Input<Inventory>("input/0"));
        Assert.Throws<ArgumentException>(() => author.Input<Inventory>(" "));
    }

    [Fact]
    public void MixedSessionsAreRejectedBeforeLowering()
    {
        var first = new ExpressionAuthoring();
        var second = new ExpressionAuthoring();
        Expression<Func<Inventory, Inventory, bool>> predicate = (a, b) => a.Reserved <= b.OnHand;
        var result = new ExpressionLowerer(first.Clr.ResolveMemberPath)
            .LowerValue(predicate, [first.Input<Inventory>(), second.Input<Inventory>()], "claim");
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Code == ExpressionDiagnosticCodes.BindingInvalid);
    }

    [Fact]
    public void ImportedShapeAndSerializedFieldNamesRemainAuthoritative()
    {
        var original = new ClrAuthoringContext().Shape<Inventory>();
        var author = new ExpressionAuthoring();
        var imported = author.Clr.Shape<Inventory>(original.Document, original.Id);
        var input = author.Input(imported, "stock");
        Expression<Func<Inventory, bool>> predicate = s => s.OnHand >= 0m;
        var lowered = new ExpressionLowerer(author.Clr.ResolveMemberPath)
            .LowerValue(predicate, [input], "claim").RequireValue();
        Assert.Equal(original.Id, input.Shape);
        Assert.Equal(ClrIdentityOrigin.Imported, imported.IdentityOrigin);
        var binary = Assert.IsType<BinaryExpr>(lowered.Value);
        Assert.Equal(Expr.Field(input.Id, "on_hand"), binary.Left);
    }

    [Fact]
    public void AssuranceAndAuthoringHaveNoRelationsAssemblyDependency()
    {
        Assert.Same(typeof(Expr).Assembly, typeof(ExpressionAuthoring).Assembly);
        foreach (var assembly in new[] { typeof(Claim).Assembly, typeof(ExpressionAuthoring).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name == "Cohesive.Relations");
    }
}
