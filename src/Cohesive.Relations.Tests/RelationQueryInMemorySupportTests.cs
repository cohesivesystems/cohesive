using Cohesive.Model.Expressions;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryInMemorySupportTests
{
    [Fact]
    public void ExpressionCapabilities_DeclareExactImplementedCanonicalFunctionSurface()
    {
        string[] expectedDeferredFunctions =
        [
            ExprFunctionNames.EntityId,
            ExprFunctionNames.GroupBy,
            ExprFunctionNames.GroupByRows,
            ExprFunctionNames.Join,
            ExprFunctionNames.Key,
            ExprFunctionNames.RelatedField,
            ExprFunctionNames.SourceRows
        ];

        var profile = RelationQueryInMemoryInterpreter.ExpressionCapabilities;
        var deferredFunctions = ExprSemanticsCatalog.Default.Functions
            .Where(function => !profile.Supports(function.OperationCapability))
            .Select(static function => function.Id.Value)
            .ToArray();

        Assert.Equal(expectedDeferredFunctions, deferredFunctions);
        Assert.True(profile.Supports(ExprCapabilities.ForFunction(ExprFunctionNames.Concat)));
        Assert.False(profile.Supports(ExprCapabilities.ForFunction(ExprFunctionNames.GroupByRows)));
        Assert.All(
            ExprSemanticsCatalog.Default.UnaryOperators,
            definition => Assert.True(profile.Supports(definition.OperationCapability)));
        Assert.All(
            ExprSemanticsCatalog.Default.BinaryOperators,
            definition => Assert.True(profile.Supports(definition.OperationCapability)));
        Assert.All(
            ExprSemanticsCatalog.Default.AggregateOperators,
            definition => Assert.True(profile.Supports(definition.OperationCapability)));
    }
}
