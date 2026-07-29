using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using Cohesive.Transitions.Compilation;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessExpressionLanguageClosureTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void Capabilities_ReuseTheExactTransitionV1PureClosure()
    {
        Assert.Same(
            TransitionExpressionLanguage.Capabilities,
            ProcessExpressionLanguage.Capabilities);
    }

    [Fact]
    public void Validate_FunctionInsideTheSharedPureClosure_IsAccepted()
    {
        var expression = new CallExpr(
            ExprFunctionNames.Concat,
            [Expr.Const("co"), Expr.Const("hesive")],
            StringContract.Type);

        var validation = ProcessDefinitionValidator.Validate(Definition(expression, StringContract));

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
    }

    [Fact]
    public void Validate_FunctionOutsideTheProcessV1Closure_IsRejectedAtItsOwningSite()
    {
        var rowsType = new ArrayTypeRef(StringContract.Type!);
        var rows = new LiteralExpr(
            rowsType,
            ObservationValue.FromArray(
                [ObservationValue.FromString("alpha"), ObservationValue.FromString("beta")]));
        var resultContract = new ValueContract(new JsonTypeRef(JsonTypeKind.Object));
        var expression = new CallExpr(
            ExprFunctionNames.GroupBy,
            [rows, Expr.Const("constant-group")],
            resultContract.Type);

        var validation = ProcessDefinitionValidator.Validate(Definition(expression, resultContract));

        var diagnostic = Assert.Single(
            validation.Diagnostics,
            static candidate => candidate.Code == ExprAnalysisDiagnosticCodes.CapabilityUnsupported);
        Assert.Equal("/nodes/0/result", diagnostic.Location);
        Assert.Contains(
            ExprCapabilities.ForFunction(ExprFunctionNames.GroupBy).Value,
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    static CanonicalProcessDefinition Definition(Expr result, ValueContract resultContract) => new(
        StringContract,
        resultContract,
        new("return"),
        [new ReturnProcessNode(new("return"), result)],
        ProcessRecoveryPolicy.ContinueAttempt);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
