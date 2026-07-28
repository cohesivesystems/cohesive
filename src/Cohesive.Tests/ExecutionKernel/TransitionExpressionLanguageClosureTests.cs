using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class TransitionExpressionLanguageClosureTests
{
    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static readonly ValueContract DecimalContract = new(new ScalarTypeRef(ScalarTypeKind.Decimal));
    static readonly ValueContract EmptyObjectContract = new(new ObjectTypeRef([]));
    static readonly ValueContract Int64Contract = new(new ScalarTypeRef(ScalarTypeKind.Int64));
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract StringArrayContract = new(new ArrayTypeRef(
        new ScalarTypeRef(ScalarTypeKind.String)));

    [Fact]
    public void Compile_GroupedAggregate_IsRejectedBeforeReferenceExecution()
    {
        var decimalType = new ScalarTypeRef(ScalarTypeKind.Decimal);
        var expression = new AggregateExpr(
            AggregateOperator.Sum,
            Expr.Field("values"),
            decimalType,
            [Expr.Const("constant-group")]);
        ShapeId aggregateShape = new("aggregate");
        ShapeGraph graph = new(
            new("grouped-aggregate"),
            [
                new(
                    aggregateShape,
                    [
                        new(new FieldName("values"), new ArrayTypeRef(decimalType)),
                        new(
                            new FieldName("total"),
                            decimalType,
                            role: FieldRole.Computed,
                            mutability: FieldMutability.Computed,
                            compute: new(expression))
                    ])
            ]);
        var observation = ValueContract.FromShape(
            graph.GetShape(aggregateShape),
            graph.Qualify(aggregateShape));
        var definition = Definition(Expr.Const("ok"), new(new ScalarTypeRef(ScalarTypeKind.String)), observation: observation);

        var result = TransitionStaticCompiler.Compile(Document(definition), graph);

        Assert.False(result.IsSuccessful);
        var diagnostic = Assert.Single(
            result.Validation.Diagnostics,
            static value => value.Code == TransitionCompilationDiagnosticCodes.GroupedAggregateUnsupported);
        Assert.Equal("/shape/fields/total/compute/expression", diagnostic.Location);
        Assert.Null(result.Plan);
    }

    [Theory]
    [InlineData(ExprFunctionNames.GroupBy, JsonTypeKind.Object)]
    [InlineData(ExprFunctionNames.GroupByRows, JsonTypeKind.Array)]
    [InlineData(ExprFunctionNames.Join, JsonTypeKind.Array)]
    public void Compile_PureFunctionOutsideClosure_IsRejected(
        string function,
        JsonTypeKind outcomeKind)
    {
        var input = ObjectContract(new ObjectFieldTypeDef("rows", StringArrayContract.Type!));
        var rows = Expr.Param("rows");
        var expression = function switch
        {
            ExprFunctionNames.GroupBy or ExprFunctionNames.GroupByRows =>
                new CallExpr(function, [rows, Expr.CurrentItem()], new JsonTypeRef(outcomeKind)),
            ExprFunctionNames.Join =>
                new CallExpr(
                    function,
                    [Expr.Const("left"), Expr.CurrentItem(), rows],
                    new JsonTypeRef(outcomeKind)),
            _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unexpected test function.")
        };

        var result = CompileOutcome(expression, new(new JsonTypeRef(outcomeKind)), input);

        Assert.False(result.IsSuccessful);
        var diagnostic = Assert.Single(
            result.Validation.Diagnostics,
            static value => value.Code == ExprAnalysisDiagnosticCodes.CapabilityUnsupported);
        Assert.Contains(ExprCapabilities.ForFunction(function).Value, diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Capabilities_AdvertiseTheExactTransitionV1Closure()
    {
        ExprCapabilityId[] expected =
        [
            ExprCapabilities.Binding,
            ExprCapabilities.Field,
            ExprCapabilities.NestedFieldPath,
            ExprCapabilities.Parameter,
            ExprCapabilities.Constant,
            ExprCapabilities.TypedField,
            ExprCapabilities.TypedLiteral,
            ExprCapabilities.Conditional,
            ExprCapabilities.CurrentItem,
            ExprCapabilities.ForUnary(UnaryOperator.Not),
            ExprCapabilities.ForBinary(BinaryOperator.Eq),
            ExprCapabilities.ForBinary(BinaryOperator.Ne),
            ExprCapabilities.ForBinary(BinaryOperator.Gt),
            ExprCapabilities.ForBinary(BinaryOperator.Ge),
            ExprCapabilities.ForBinary(BinaryOperator.Lt),
            ExprCapabilities.ForBinary(BinaryOperator.Le),
            ExprCapabilities.ForBinary(BinaryOperator.And),
            ExprCapabilities.ForBinary(BinaryOperator.Or),
            ExprCapabilities.ForBinary(BinaryOperator.Add),
            ExprCapabilities.ForBinary(BinaryOperator.Sub),
            ExprCapabilities.ForBinary(BinaryOperator.Mul),
            ExprCapabilities.ForBinary(BinaryOperator.Div),
            ExprCapabilities.ForAggregate(AggregateOperator.Count),
            ExprCapabilities.ForAggregate(AggregateOperator.Sum),
            ExprCapabilities.ForAggregate(AggregateOperator.Min),
            ExprCapabilities.ForAggregate(AggregateOperator.Max),
            ExprCapabilities.ForAggregate(AggregateOperator.Any),
            ExprCapabilities.ForAggregate(AggregateOperator.All),
            ExprCapabilities.ForAggregate(AggregateOperator.Average),
            ExprCapabilities.ForFunction(ExprFunctionNames.Contains),
            ExprCapabilities.ForFunction(ExprFunctionNames.Count),
            ExprCapabilities.ForFunction(ExprFunctionNames.EndsWith),
            ExprCapabilities.ForFunction(ExprFunctionNames.StartsWith),
            ExprCapabilities.ForFunction(ExprFunctionNames.TextContains),
            ExprCapabilities.ForFunction(ExprFunctionNames.Object),
            ExprCapabilities.ForFunction(ExprFunctionNames.Select),
            ExprCapabilities.ForFunction(ExprFunctionNames.Append),
            ExprCapabilities.ForFunction(ExprFunctionNames.AppendRange),
            ExprCapabilities.ForFunction(ExprFunctionNames.InsertAt),
            ExprCapabilities.ForFunction(ExprFunctionNames.InsertRangeAt),
            ExprCapabilities.ForFunction(ExprFunctionNames.Concat),
            ExprCapabilities.ForFunction(ExprFunctionNames.Sum),
            ExprCapabilities.ForFunction(ExprFunctionNames.Min),
            ExprCapabilities.ForFunction(ExprFunctionNames.Max),
            ExprCapabilities.ForFunction(ExprFunctionNames.Avg),
            ExprCapabilities.ForFunction(ExprFunctionNames.Any),
            ExprCapabilities.ForFunction(ExprFunctionNames.All)
        ];

        Assert.Equal(
            expected.OrderBy(static capability => capability.Value, StringComparer.Ordinal),
            TransitionExpressionLanguage.Capabilities.SupportedCapabilities);
    }

    [Fact]
    public void Capabilities_EveryAdvertisedFunctionHasExecutableReferenceSemantics()
    {
        var strings = new LiteralExpr(
            StringArrayContract.Type!,
            ObservationValue.FromArray(
                [ObservationValue.FromString("alpha"), ObservationValue.FromString("beta")]));
        var decimalArrayType = new ArrayTypeRef(DecimalContract.Type!);
        var decimals = new LiteralExpr(
            decimalArrayType,
            ObservationValue.FromArray(
                [ObservationValue.FromDecimal(2m), ObservationValue.FromDecimal(4m)]));
        var booleanArrayType = new ArrayTypeRef(BooleanContract.Type!);
        var booleans = new LiteralExpr(
            booleanArrayType,
            ObservationValue.FromArray(
                [ObservationValue.FromBool(true), ObservationValue.FromBool(false)]));
        var objectContract = Permissive(ObjectContract(new ObjectFieldTypeDef("value", StringContract.Type!)));
        FunctionCase[] cases =
        [
            Function(ExprFunctionNames.Contains, BooleanContract, ObservationValue.FromBool(true), strings, Expr.Const("beta")),
            Function(ExprFunctionNames.Count, Int64Contract, ObservationValue.FromInt64(2), strings),
            Function(ExprFunctionNames.EndsWith, BooleanContract, ObservationValue.FromBool(true), Expr.Const("alpha"), Expr.Const("pha")),
            Function(ExprFunctionNames.StartsWith, BooleanContract, ObservationValue.FromBool(true), Expr.Const("alpha"), Expr.Const("alp")),
            Function(ExprFunctionNames.TextContains, BooleanContract, ObservationValue.FromBool(true), Expr.Const("alpha"), Expr.Const("ph")),
            Function(
                ExprFunctionNames.Object,
                objectContract,
                ObservationValue.FromObject(new Dictionary<string, ObservationValue>
                {
                    ["value"] = ObservationValue.FromString("alpha")
                }),
                Expr.Const("value"),
                Expr.Const("alpha")),
            Function(
                ExprFunctionNames.Select,
                StringArrayContract,
                ObservationValue.FromArray(
                    [ObservationValue.FromString("alpha"), ObservationValue.FromString("beta")]),
                strings,
                Expr.CurrentItem()),
            Function(
                ExprFunctionNames.Append,
                StringArrayContract,
                ObservationValue.FromArray(
                    [ObservationValue.FromString("alpha"), ObservationValue.FromString("beta"), ObservationValue.FromString("gamma")]),
                strings,
                Expr.Const("gamma")),
            Function(
                ExprFunctionNames.AppendRange,
                StringArrayContract,
                ObservationValue.FromArray(
                    [ObservationValue.FromString("alpha"), ObservationValue.FromString("beta"), ObservationValue.FromString("gamma")]),
                strings,
                new LiteralExpr(
                    StringArrayContract.Type!,
                    ObservationValue.FromArray([ObservationValue.FromString("gamma")]))),
            Function(
                ExprFunctionNames.InsertAt,
                StringArrayContract,
                ObservationValue.FromArray(
                    [ObservationValue.FromString("alpha"), ObservationValue.FromString("inserted"), ObservationValue.FromString("beta")]),
                strings,
                Expr.Const(1),
                Expr.Const("inserted")),
            Function(
                ExprFunctionNames.InsertRangeAt,
                StringArrayContract,
                ObservationValue.FromArray(
                    [ObservationValue.FromString("alpha"), ObservationValue.FromString("inserted"), ObservationValue.FromString("beta")]),
                strings,
                Expr.Const(1),
                new LiteralExpr(
                    StringArrayContract.Type!,
                    ObservationValue.FromArray([ObservationValue.FromString("inserted")]))),
            Function(ExprFunctionNames.Concat, StringContract, ObservationValue.FromString("alphabeta"), Expr.Const("alpha"), Expr.Const("beta")),
            Function(ExprFunctionNames.Sum, DecimalContract, ObservationValue.FromDecimal(6m), decimals),
            Function(ExprFunctionNames.Min, DecimalContract, ObservationValue.FromDecimal(2m), decimals),
            Function(ExprFunctionNames.Max, DecimalContract, ObservationValue.FromDecimal(4m), decimals),
            Function(ExprFunctionNames.Avg, DecimalContract, ObservationValue.FromDecimal(3m), decimals),
            Function(ExprFunctionNames.Any, BooleanContract, ObservationValue.FromBool(true), booleans),
            Function(ExprFunctionNames.All, BooleanContract, ObservationValue.FromBool(false), booleans)
        ];

        Assert.Equal(
            TransitionExpressionLanguage.Capabilities.SupportedCapabilities
                .Where(static capability => capability.Value.StartsWith("expr.function.", StringComparison.Ordinal))
                .OrderBy(static capability => capability.Value, StringComparer.Ordinal),
            cases.Select(static value => ExprCapabilities.ForFunction(value.Name))
                .OrderBy(static capability => capability.Value, StringComparer.Ordinal));

        foreach (var @case in cases)
            AssertExpressionExecutes(@case.Name, @case.Expression, @case.Contract, @case.Expected);
    }

    [Fact]
    public void Capabilities_EveryAdvertisedOperatorAndAggregateHasExecutableReferenceSemantics()
    {
        foreach (var @operator in Enum.GetValues<UnaryOperator>())
        {
            AssertExpressionExecutes(
                @operator.ToString(),
                new UnaryExpr(@operator, Expr.Const(false)),
                BooleanContract,
                ObservationValue.FromBool(true));
        }

        foreach (var @operator in Enum.GetValues<BinaryOperator>())
        {
            var logical = @operator is BinaryOperator.And or BinaryOperator.Or;
            var comparison = @operator is BinaryOperator.Eq or BinaryOperator.Ne
                or BinaryOperator.Gt or BinaryOperator.Ge or BinaryOperator.Lt or BinaryOperator.Le;
            var expression = logical
                ? new BinaryExpr(@operator, Expr.Const(true), Expr.Const(false))
                : new BinaryExpr(@operator, Expr.Const(6m), Expr.Const(2m));
            var contract = logical || comparison ? BooleanContract : DecimalContract;
            var expected = @operator switch
            {
                BinaryOperator.Eq => ObservationValue.FromBool(false),
                BinaryOperator.Ne => ObservationValue.FromBool(true),
                BinaryOperator.Gt => ObservationValue.FromBool(true),
                BinaryOperator.Ge => ObservationValue.FromBool(true),
                BinaryOperator.Lt => ObservationValue.FromBool(false),
                BinaryOperator.Le => ObservationValue.FromBool(false),
                BinaryOperator.And => ObservationValue.FromBool(false),
                BinaryOperator.Or => ObservationValue.FromBool(true),
                BinaryOperator.Add => ObservationValue.FromDecimal(8m),
                BinaryOperator.Sub => ObservationValue.FromDecimal(4m),
                BinaryOperator.Mul => ObservationValue.FromDecimal(12m),
                BinaryOperator.Div => ObservationValue.FromDecimal(3m),
                _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unexpected operator.")
            };
            AssertExpressionExecutes(@operator.ToString(), expression, contract, expected);
        }

        var numbers = new LiteralExpr(
            new ArrayTypeRef(DecimalContract.Type!),
            ObservationValue.FromArray(
                [ObservationValue.FromDecimal(2m), ObservationValue.FromDecimal(4m)]));
        var booleans = new LiteralExpr(
            new ArrayTypeRef(BooleanContract.Type!),
            ObservationValue.FromArray(
                [ObservationValue.FromBool(true), ObservationValue.FromBool(false)]));
        foreach (var aggregate in Enum.GetValues<AggregateOperator>())
        {
            var boolean = aggregate is AggregateOperator.Any or AggregateOperator.All;
            var count = aggregate == AggregateOperator.Count;
            var contract = Permissive(boolean ? BooleanContract : count ? Int64Contract : DecimalContract);
            var expected = aggregate switch
            {
                AggregateOperator.Count => ObservationValue.FromInt64(2),
                AggregateOperator.Sum => ObservationValue.FromDecimal(6m),
                AggregateOperator.Min => ObservationValue.FromDecimal(2m),
                AggregateOperator.Max => ObservationValue.FromDecimal(4m),
                AggregateOperator.Any => ObservationValue.FromBool(true),
                AggregateOperator.All => ObservationValue.FromBool(false),
                AggregateOperator.Average => ObservationValue.FromDecimal(3m),
                _ => throw new ArgumentOutOfRangeException(nameof(aggregate), aggregate, "Unexpected aggregate.")
            };
            AssertExpressionExecutes(
                aggregate.ToString(),
                new AggregateExpr(aggregate, boolean ? booleans : numbers, contract.Type!),
                contract,
                expected);
        }
    }

    [Theory]
    [InlineData(PortableValueState.Missing, TransitionExecutionDiagnosticCodes.ObservationUnavailable)]
    [InlineData(PortableValueState.Unknown, TransitionExecutionDiagnosticCodes.ObservationUnknown)]
    [InlineData(PortableValueState.Failed, "test.input.failed")]
    public void Decide_NonTerminalPortableInput_DoesNotBecomeASuccessfulOutcome(
        PortableValueState state,
        string expectedDiagnosticCode)
    {
        var valueContract = new ValueContract(
            new ScalarTypeRef(ScalarTypeKind.String),
            presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable);
        var compilation = CompileOutcome(
            Expr.BoundValue(TransitionBindingIds.Input),
            valueContract,
            valueContract);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));

        var input = state switch
        {
            PortableValueState.Missing => PortableValue.Missing(valueContract),
            PortableValueState.Unknown => PortableValue.Unknown(valueContract),
            PortableValueState.Failed => PortableValue.Failed(
                valueContract,
                new(expectedDiagnosticCode, DiagnosticSeverity.Error, "The test input failed.")),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unexpected test state.")
        };
        var observation = PortableValue.Concrete(EmptyObjectContract, ObservationValue.EmptyObject);

        var decision = TransitionReferenceInterpreter.DecideFullState(
            compilation.Plan!,
            new("activation/non-terminal-input"),
            input,
            observation);

        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, decision.Kind);
        Assert.Null(decision.Outcome);
        Assert.Empty(decision.Patch);
        Assert.Empty(decision.Emissions);
        Assert.Empty(decision.MachineMovements);
        Assert.False(decision.GuaranteeDemands.CommitRequired);
        Assert.Equal(expectedDiagnosticCode, Assert.Single(decision.Diagnostics).Code);
    }

    static TransitionCompilationResult CompileOutcome(
        Expr expression,
        ValueContract outcome,
        ValueContract? input = null)
    {
        var definition = Definition(expression, outcome, input);

        return TransitionStaticCompiler.Compile(Document(definition));
    }

    static FunctionCase Function(
        string name,
        ValueContract contract,
        ObservationValue expected,
        params Expr[] arguments)
    {
        var executionContract = Permissive(contract);
        return new(
            name,
            new CallExpr(name, [.. arguments], executionContract.Type),
            executionContract,
            expected);
    }

    static ValueContract Permissive(ValueContract contract) => new(
        contract.Type,
        contract.Shape,
        contract.Cardinality,
        FieldPresence.Optional,
        FieldNullability.Nullable);

    static void AssertExpressionExecutes(
        string name,
        Expr expression,
        ValueContract contract,
        ObservationValue expected)
    {
        var compilation = CompileOutcome(expression, contract);
        Assert.True(compilation.IsSuccessful, $"{name}:{Environment.NewLine}{Format(compilation.Validation)}");
        var decision = TransitionReferenceInterpreter.DecideFullState(
            compilation.Plan!,
            new($"activation/{name}"),
            PortableValue.Concrete(EmptyObjectContract, ObservationValue.EmptyObject),
            PortableValue.Concrete(EmptyObjectContract, ObservationValue.EmptyObject));

        Assert.Empty(decision.Diagnostics);
        Assert.Equal(PortableValue.Concrete(contract, expected), decision.Outcome);
    }

    static CanonicalTransitionDefinition Definition(
        Expr expression,
        ValueContract outcome,
        ValueContract? input = null,
        ValueContract? observation = null) => new(
            input ?? EmptyObjectContract,
            observation ?? EmptyObjectContract,
            outcome,
            [],
            new(
                new("root"),
                [new OutcomeTransitionNode(new("outcome"), TransitionOutcomeDisposition.Applied, expression)]));

    static ExecutionDefinitionDocument Document(CanonicalTransitionDefinition definition) =>
        TransitionDefinitionDocuments.Create(
            new("transition/expression-language-closure"),
            new("revision/1"),
            definition,
            new(
                new("transition-expression-language-closure-tests", "1"),
                new("tests/execution-kernel/transition-expression-language-closure"),
                DocumentOrigin.Generated));

    static ValueContract ObjectContract(params ObjectFieldTypeDef[] fields) =>
        new(new ObjectTypeRef([.. fields]));

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed record FunctionCase(
        string Name,
        Expr Expression,
        ValueContract Contract,
        ObservationValue Expected);
}
