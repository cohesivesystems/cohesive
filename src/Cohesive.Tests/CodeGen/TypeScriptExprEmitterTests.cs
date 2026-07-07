using Cohesive.Adapters.TypeScript;

namespace Cohesive.Tests.CodeGen;

public sealed class TypeScriptExprEmitterTests
{
    [Fact]
    public void Emit_ConditionalPredicate_UsesPortableTypeScriptOperators()
    {
        var expr = Expr.And(
            Expr.Ne(Expr.Param("resourceId"), Expr.Const("")),
            Expr.Or(
                Expr.Eq(Expr.Param("activeViewId"), Expr.Const("sample.edi-spec.details.structure")),
                Expr.Eq(Expr.Param("layout"), Expr.Const("split"))));

        var text = TypeScriptExprEmitter.EmitExpression(expr);

        Assert.Equal(
            "((context.resourceId !== '') && ((context.activeViewId === 'sample.edi-spec.details.structure') || (context.layout === 'split')))",
            text);
    }

    [Fact]
    public void Emit_Functions_EmitsContainsAndCount()
    {
        var expr = Expr.And(
            Expr.Call(ExprFunctionNames.Contains, Expr.Param("statuses"), Expr.Const("Running")),
            Expr.Gt(Expr.Call(ExprFunctionNames.Count, Expr.Field("items")), Expr.Const(0)));

        var text = new TypeScriptExprEmitter(new()
        {
            ParameterBindings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["statuses"] = "state.statuses"
            },
            FieldRootIdentifier = "data"
        }).Emit(expr);

        Assert.Equal("((state.statuses).includes('Running') && ((data.items).length > 0))", text);
    }

    [Fact]
    public void EmitStringUnionTypeAlias_EmitsChoiceUnion()
    {
        var text = TypeScriptChoiceTypeEmitter.EmitStringUnionTypeAlias(
            name: "DocumentEditorLayout",
            choices: ["single", "split"]);

        Assert.Equal("export type DocumentEditorLayout = 'single' | 'split';", text);
    }
}
