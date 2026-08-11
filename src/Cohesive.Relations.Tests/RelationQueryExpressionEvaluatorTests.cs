using System.Collections.ObjectModel;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionEvaluatorTests
{
    static readonly ValueBindingId LoadBinding = new("load");
    static readonly ValueBindingId CustomerBinding = new("customer");
    readonly RelationQueryExpressionEvaluator evaluator = new();

    [Fact]
    public void Evaluate_FieldExpressionsUseExactBindingsAndPreserveMissingAndNull()
    {
        var context = Context(
            implicitBinding: LoadBinding,
            bindings: new Dictionary<ValueBindingId, RelationQueryExpressionBinding>
            {
                [LoadBinding] = new(Object(
                    ("Id", ObservationValue.FromString("L1")),
                    ("Customer", Object(("Name", ObservationValue.FromString("Acme")))),
                    ("NullableCustomer", ObservationValue.Null))),
                [CustomerBinding] = RelationQueryExpressionBinding.Absent
            });

        Assert.Equal(
            "Acme",
            evaluator.Evaluate(Expr.Field(LoadBinding, "Customer.Name"), context).GetString());
        Assert.Equal(
            "L1",
            evaluator.Evaluate(new FieldRefExpr(FieldPath.FromField("Id"), new ScalarTypeRef(ScalarTypeKind.String)), context).GetString());
        Assert.Equal(
            ObservationValueKind.Undefined,
            evaluator.Evaluate(Expr.Field(LoadBinding, "Customer.Unknown"), context).Kind);
        Assert.Equal(
            ObservationValueKind.Null,
            evaluator.Evaluate(Expr.Field(LoadBinding, "NullableCustomer.Name"), context).Kind);
        Assert.Equal(
            ObservationValueKind.Undefined,
            evaluator.Evaluate(Expr.Field(CustomerBinding, "Name"), context).Kind);
    }

    [Fact]
    public void Evaluate_UnqualifiedFieldWithoutImplicitBindingFails()
    {
        var exception = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Field("Id"), new RelationQueryExpressionContext()));

        Assert.Equal(
            RelationQueryExpressionEvaluationError.EvaluationContextUnavailable,
            exception.Error);
    }

    [Fact]
    public void Evaluate_FieldElementSegmentIsExplicitlyUnsupported()
    {
        var expression = Expr.Field(
            LoadBinding,
            new FieldPath(
            [
                FieldPathSegment.ForField("Items"),
                FieldPathSegment.Element()
            ]));
        var context = Context(
            bindings: new Dictionary<ValueBindingId, RelationQueryExpressionBinding>
            {
                [LoadBinding] = new(Object(("Items", Array(1, 2))))
            });

        var exception = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(expression, context));

        Assert.Equal(RelationQueryExpressionEvaluationError.UnsupportedFieldPath, exception.Error);
    }

    [Fact]
    public void Evaluate_ParametersAndScopedCurrentItemsRemainDistinct()
    {
        var context = Context(parameters: new Dictionary<string, ObservationValue>
        {
            ["status"] = ObservationValue.FromString("Booked")
        });
        var source = ObservationValue.FromArray(
        [
            Object(("Name", ObservationValue.FromString("first"))),
            Object(("Name", ObservationValue.FromString("second")))
        ]);
        var select = Expr.Call(
            ExprFunctionNames.Select,
            Expr.Const(source),
            Expr.Field("item.Name"));

        Assert.Equal("Booked", evaluator.Evaluate(Expr.Param("status"), context).GetString());
        Assert.Equal(ObservationValueKind.Undefined, evaluator.Evaluate(Expr.Param("missing"), context).Kind);
        Assert.Equal(
            ["first", "second"],
            evaluator.Evaluate(select, context).EnumerateArray().Select(static value => value.GetRequiredString()).ToArray());

        var exception = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.CurrentItem(), context));
        Assert.Equal(
            RelationQueryExpressionEvaluationError.EvaluationContextUnavailable,
            exception.Error);
    }

    [Fact]
    public void Evaluate_SelectPreservesSameItemCorrelationForNestedCurrentItemReads()
    {
        var source = ObservationValue.FromArray(
        [
            Object(
                ("Id", ObservationValue.FromString("first")),
                ("Decision", Object(
                    ("Chosen", ObservationValue.FromBool(false)),
                    ("Rank", ObservationValue.FromInt64(2))))),
            Object(
                ("Id", ObservationValue.FromString("second")),
                ("Decision", Object(
                    ("Chosen", ObservationValue.FromBool(true)),
                    ("Rank", ObservationValue.FromInt64(1)))))
        ]);
        var select = Expr.Call(
            ExprFunctionNames.Select,
            Expr.Const(source),
            Expr.Call(
                ExprFunctionNames.Object,
                Expr.Const("Id"),
                Expr.Field("item.Id"),
                Expr.Const("Chosen"),
                Expr.Field("item.Decision.Chosen"),
                Expr.Const("Rank"),
                Expr.Field("item.Decision.Rank")));

        var results = evaluator.Evaluate(select, new RelationQueryExpressionContext()).EnumerateArray().ToArray();

        Assert.Collection(
            results,
            first =>
            {
                Assert.Equal("first", first.Fields!["Id"].GetString());
                Assert.False(first.Fields["Chosen"].Bool);
                Assert.Equal(2, first.Fields["Rank"].Int64);
            },
            second =>
            {
                Assert.Equal("second", second.Fields!["Id"].GetString());
                Assert.True(second.Fields["Chosen"].Bool);
                Assert.Equal(1, second.Fields["Rank"].Int64);
            });
    }

    [Fact]
    public void Evaluate_LogicalAndConditionalExpressionsAreStrictAndShortCircuit()
    {
        var context = new RelationQueryExpressionContext();
        var unsupported = Expr.Call("not-defined");

        Assert.False(evaluator.Evaluate(Expr.And(Expr.Const(false), unsupported), context).Bool);
        Assert.True(evaluator.Evaluate(Expr.Or(Expr.Const(true), unsupported), context).Bool);
        Assert.Equal(
            "selected",
            evaluator.Evaluate(
                Expr.If(Expr.Const(false), unsupported, Expr.Const("selected")),
                context).GetString());

        var exception = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Not(Expr.Const(1)), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, exception.Error);
    }

    [Fact]
    public void Evaluate_EqualityIsDeepAndDistinguishesNullFromMissing()
    {
        var context = new RelationQueryExpressionContext();
        var nestedLeft = Object(
            ("a", ObservationValue.FromInt64(1)),
            ("items", Array(2, 3)));
        var nestedRight = ObservationValue.FromObject(
            new ReadOnlyDictionary<string, ObservationValue>(
                new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["items"] = Array(2, 3),
                    ["a"] = ObservationValue.FromDouble(1d)
                }));

        Assert.True(evaluator.Evaluate(Expr.Eq(Expr.Const(nestedLeft), Expr.Const(nestedRight)), context).Bool);
        Assert.True(evaluator.Evaluate(Expr.Eq(Expr.Const(1L), Expr.Const(1d)), context).Bool);
        Assert.False(evaluator.Evaluate(
            Expr.Eq(Expr.Null(), Expr.Const(ObservationValue.Undefined)),
            context).Bool);
    }

    [Fact]
    public void ValueSemanticsUseOrdinalObjectKeysAndCompatibleHashes()
    {
        var left = ObservationValue.FromObject(
            new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["A"] = ObservationValue.FromInt64(1)
            });
        var differentlyCased = ObservationValue.FromObject(
            new Dictionary<string, ObservationValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = ObservationValue.FromInt64(1)
            });
        var equal = ObservationValue.FromObject(
            new Dictionary<string, ObservationValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = ObservationValue.FromDouble(1d)
            });

        Assert.False(RelationQueryValueSemantics.Equals(left, differentlyCased));
        Assert.True(RelationQueryValueSemantics.Equals(left, equal));
        Assert.Equal(
            RelationQueryValueSemantics.GetHashCode(left),
            RelationQueryValueSemantics.GetHashCode(equal));
    }

    [Fact]
    public void Evaluate_ComparisonAndOrderingUseStrictDeterministicDomains()
    {
        var context = new RelationQueryExpressionContext();
        Assert.True(evaluator.Evaluate(Expr.Gt(Expr.Const("b"), Expr.Const("a")), context).Bool);
        Assert.True(evaluator.Evaluate(Expr.Gt(Expr.Const(9_007_199_254_740_993L), Expr.Const(9_007_199_254_740_992d)), context).Bool);
        var nextAfterOne = Math.BitIncrement(1d);
        Assert.False(RelationQueryValueSemantics.Equals(
            ObservationValue.FromInt64(1),
            ObservationValue.FromDouble(nextAfterOne)));
        Assert.True(RelationQueryValueSemantics.Compare(
            ObservationValue.FromInt64(1),
            ObservationValue.FromDouble(nextAfterOne)) < 0);
        Assert.True(RelationQueryValueSemantics.Compare(
            ObservationValue.FromDouble(nextAfterOne),
            ObservationValue.FromInt64(1)) > 0);
        Assert.Equal(
            -1,
            RelationQueryValueSemantics.CompareForOrdering(
                ObservationValue.Null,
                ObservationValue.FromInt64(1),
                QuerySortDirection.Descending,
                QueryNullPlacement.First));
        Assert.Equal(
            0,
            RelationQueryValueSemantics.CompareForOrdering(
                ObservationValue.Null,
                ObservationValue.Undefined,
                QuerySortDirection.Ascending,
                QueryNullPlacement.Last));

        var mixed = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Gt(Expr.Const("1"), Expr.Const(1)), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, mixed.Error);
        var nullish = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Gt(Expr.Null(), Expr.Const(1)), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, nullish.Error);
    }

    [Fact]
    public void Evaluate_ArithmeticIsNumericOnlyAndReportsNumericFailures()
    {
        var context = new RelationQueryExpressionContext();

        Assert.Equal(5L, evaluator.Evaluate(Expr.Add(Expr.Const(2), Expr.Const(3)), context).Int64);
        Assert.Equal(2.5m, evaluator.Evaluate(Expr.Div(Expr.Const(5), Expr.Const(2)), context).GetDecimal());

        var text = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Add(Expr.Const("2"), Expr.Const(3)), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, text.Error);
        var divideByZero = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Div(Expr.Const(1), Expr.Const(0)), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.NumericFailure, divideByZero.Error);
        var nonFinite = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Add(Expr.Const(double.PositiveInfinity), Expr.Const(1)), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, nonFinite.Error);
    }

    [Fact]
    public void Evaluate_DecimalArithmeticAndEquality_RemainExact()
    {
        const decimal highPrecision = 12345678901234567890.123456789m;
        var context = new RelationQueryExpressionContext();

        var sum = evaluator.Evaluate(
            Expr.Add(Expr.Const(highPrecision), Expr.Const(0.000000001m)),
            context);

        Assert.Equal(ObservationValueKind.Decimal, sum.Kind);
        Assert.Equal(12345678901234567890.123456790m, sum.Decimal);
        Assert.True(evaluator.Evaluate(
            Expr.Eq(Expr.Const(0.125m), Expr.Const(0.125d)),
            context).Bool);
        Assert.True(evaluator.Evaluate(
            Expr.Eq(Expr.Const(0.1m), Expr.Const(0.1d)),
            context).Bool);
        Assert.Equal(0, RelationQueryValueSemantics.Compare(
            ObservationValue.FromDecimal(0.1m),
            ObservationValue.FromDouble(0.1d)));
        Assert.Equal(
            RelationQueryValueSemantics.GetHashCode(ObservationValue.FromDecimal(0.125m)),
            RelationQueryValueSemantics.GetHashCode(ObservationValue.FromDouble(0.125d)));
    }

    [Fact]
    public void Evaluate_LargeIntegralDouble_UsesPersistedCanonicalNumericSemantics()
    {
        const long CanonicalInteger = 1_000_000_000_000_000_100L;
        var floatingPoint = ObservationValue.FromDouble(Math.BitIncrement(1e18));
        var integer = ObservationValue.FromInt64(CanonicalInteger);
        var context = new RelationQueryExpressionContext();

        Assert.Equal(CanonicalInteger, floatingPoint.GetDecimal());
        Assert.True(evaluator.Evaluate(
            Expr.Eq(Expr.Const(floatingPoint), Expr.Const(integer)),
            context).Bool);
        Assert.True(evaluator.Evaluate(
            Expr.Call(
                ExprFunctionNames.Contains,
                Expr.Const(ObservationValue.FromArray([floatingPoint])),
                Expr.Const(integer)),
            context).Bool);
        Assert.Equal(0, RelationQueryValueSemantics.CompareForOrdering(
            floatingPoint,
            integer,
            QuerySortDirection.Ascending,
            QueryNullPlacement.Last));
        Assert.Equal(
            RelationQueryValueSemantics.GetHashCode(floatingPoint),
            RelationQueryValueSemantics.GetHashCode(integer));

        var sum = evaluator.Evaluate(
            Expr.Add(Expr.Const(floatingPoint), Expr.Const(0m)),
            context);
        Assert.Equal(ObservationValueKind.Int64, sum.Kind);
        Assert.Equal(CanonicalInteger, sum.Int64);
    }

    [Fact]
    public void Evaluate_CollectionAndObjectFunctionsAreStrictAndImmutable()
    {
        var context = new RelationQueryExpressionContext();
        var source = Expr.Const(Array(1, 2));

        Assert.True(evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Contains, source, Expr.Const(1d)),
            context).Bool);
        Assert.Equal(2L, evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Count, source),
            context).Int64);
        AssertValues(
            evaluator.Evaluate(Expr.Call(ExprFunctionNames.Append, source, Expr.Const(3)), context),
            1, 2, 3);
        AssertValues(
            evaluator.Evaluate(
                Expr.Call(ExprFunctionNames.AppendRange, source, Expr.Const(Array(3, 4))),
                context),
            1, 2, 3, 4);
        AssertValues(
            evaluator.Evaluate(
                Expr.Call(ExprFunctionNames.InsertAt, source, Expr.Const(1), Expr.Const(9)),
                context),
            1, 9, 2);
        AssertValues(
            evaluator.Evaluate(
                Expr.Call(ExprFunctionNames.InsertRangeAt, source, Expr.Const(1), Expr.Const(Array(8, 9))),
                context),
            1, 8, 9, 2);
        Assert.Equal(
            "load-1",
            evaluator.Evaluate(
                Expr.Call(ExprFunctionNames.Concat, Expr.Const("load"), Expr.Const("-"), Expr.Const("1")),
                context).GetString());
        Assert.Equal(
            "Acme",
            evaluator.Evaluate(
                Expr.Call(
                    ExprFunctionNames.Object,
                    Expr.Const("Name"),
                    Expr.Const("Acme")),
                context).GetProperty("Name").GetString());

        var scalarContains = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(
                Expr.Call(ExprFunctionNames.Contains, Expr.Const("abc"), Expr.Const("a")),
                context));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, scalarContains.Error);
        var duplicateKey = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(
                Expr.Call(
                    ExprFunctionNames.Object,
                    Expr.Const("a"), Expr.Const(1),
                    Expr.Const("a"), Expr.Const(2)),
                context));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, duplicateKey.Error);
    }

    [Theory]
    [InlineData("Load-ABC", "ABC", true)]
    [InlineData("Load-ABC", "abc", false)]
    [InlineData("Load-ABC", "", true)]
    [InlineData("", "", true)]
    [InlineData("a", "longer", false)]
    [InlineData("caf\u00E9", "\u00E9", true)]
    [InlineData("caf\u00E9", "e\u0301", false)]
    [InlineData("cafe\u0301", "e\u0301", true)]
    [InlineData("load\U0001F69A", "\U0001F69A", true)]
    [InlineData("id*?\\", "*?\\", true)]
    public void Evaluate_EndsWithUsesOrdinalCaseSensitiveTextSemantics(
        string value,
        string suffix,
        bool expected)
    {
        var context = new RelationQueryExpressionContext();

        var result = evaluator.Evaluate(
            Expr.EndsWith(Expr.Const(value), Expr.Const(suffix)),
            context);

        Assert.Equal(expected, result.Bool);
    }

    [Fact]
    public void Evaluate_EndsWithRejectsNullUndefinedAndNonTextOperands()
    {
        var context = new RelationQueryExpressionContext();
        Expr[] invalid =
        [
            Expr.EndsWith(Expr.Null(), Expr.Const("suffix")),
            Expr.EndsWith(Expr.Const(ObservationValue.Undefined), Expr.Const("suffix")),
            Expr.EndsWith(Expr.Const(1), Expr.Const("1")),
            Expr.EndsWith(Expr.Const("value"), Expr.Null()),
            Expr.EndsWith(Expr.Const("value"), Expr.Const(false))
        ];

        Assert.All(invalid, expression =>
        {
            var exception = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
                evaluator.Evaluate(expression, context));
            Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, exception.Error);
        });
    }

    [Fact]
    public void Evaluate_SequenceAggregatesUseScopedSelectorsAndDefinedEmptyIdentities()
    {
        var context = new RelationQueryExpressionContext();
        var source = Expr.Const(ObservationValue.FromArray(
        [
            Object(
                ("Amount", ObservationValue.FromInt64(2)),
                ("Active", ObservationValue.FromBool(true))),
            Object(
                ("Amount", ObservationValue.FromInt64(3)),
                ("Active", ObservationValue.FromBool(false)))
        ]));

        Assert.Equal(5L, evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Sum, source, Expr.Field("item.Amount")), context).Int64);
        Assert.Equal(2L, evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Min, source, Expr.Field("item.Amount")), context).Int64);
        Assert.Equal(3L, evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Max, source, Expr.Field("item.Amount")), context).Int64);
        Assert.Equal(2.5m, evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Avg, source, Expr.Field("item.Amount")), context).GetDecimal());
        Assert.True(evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Any, source, Expr.Field("item.Active")), context).Bool);
        Assert.False(evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.All, source, Expr.Field("item.Active")), context).Bool);

        var empty = Expr.Const(ObservationValue.FromArray([]));
        Assert.Equal(0L, evaluator.Evaluate(Expr.Call(ExprFunctionNames.Sum, empty), context).Int64);
        Assert.Equal(ObservationValueKind.Undefined, evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Min, empty), context).Kind);
        Assert.Equal(ObservationValueKind.Undefined, evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Avg, empty), context).Kind);
        Assert.False(evaluator.Evaluate(Expr.Call(ExprFunctionNames.Any, empty), context).Bool);
        Assert.True(evaluator.Evaluate(Expr.Call(ExprFunctionNames.All, empty), context).Bool);
    }

    [Fact]
    public void Evaluate_AnyAndAllShortCircuitBeforeInvalidLaterSelectors()
    {
        var context = new RelationQueryExpressionContext();
        var anySource = Expr.Const(ObservationValue.FromArray(
        [
            Object(("Match", ObservationValue.FromBool(true))),
            Object(("Match", ObservationValue.FromString("not-a-boolean")))
        ]));
        var allSource = Expr.Const(ObservationValue.FromArray(
        [
            Object(("Match", ObservationValue.FromBool(false))),
            Object(("Match", ObservationValue.FromString("not-a-boolean")))
        ]));

        Assert.True(evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.Any, anySource, Expr.Field("item.Match")),
            context).Bool);
        Assert.False(evaluator.Evaluate(
            Expr.Call(ExprFunctionNames.All, allSource, Expr.Field("item.Match")),
            context).Bool);
    }

    [Fact]
    public void Evaluate_StructuredAnyRequiresOneElementToSatisfyTheCompletePredicate()
    {
        var expression = Expr.Any(
            Expr.Field(LoadBinding, "Stops"),
            Expr.And(
                Expr.Eq(Expr.Field("item.Location"), Expr.Const("Seattle")),
                Expr.Eq(Expr.Field("item.Type"), Expr.Const("Pickup"))));
        var splitMatch = StructuredStopsContext(
            Object(
                ("Location", ObservationValue.FromString("Seattle")),
                ("Type", ObservationValue.FromString("Delivery"))),
            Object(
                ("Location", ObservationValue.FromString("Portland")),
                ("Type", ObservationValue.FromString("Pickup"))));
        var sameElementMatch = StructuredStopsContext(
            Object(
                ("Location", ObservationValue.FromString("Seattle")),
                ("Type", ObservationValue.FromString("Pickup"))),
            Object(
                ("Location", ObservationValue.FromString("Portland")),
                ("Type", ObservationValue.FromString("Delivery"))));

        Assert.False(evaluator.Evaluate(expression, splitMatch).Bool);
        Assert.True(evaluator.Evaluate(expression, sameElementMatch).Bool);
    }

    [Fact]
    public void Evaluate_StructuredAnyDefinesEmptyNullMissingAndInvalidOperandSemantics()
    {
        var predicate = Expr.Const(true);

        Assert.False(evaluator.Evaluate(
            Expr.Any(
                Expr.Field(LoadBinding, "Stops"),
                predicate),
            StructuredStopsContext()).Bool);

        ObservationValue[] invalidCollections =
        [
            ObservationValue.Null,
            ObservationValue.Undefined,
            ObservationValue.FromString("not-a-collection")
        ];
        Assert.All(invalidCollections, collection =>
        {
            var exception = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
                evaluator.Evaluate(
                    Expr.Any(Expr.Const(collection), predicate),
                    new RelationQueryExpressionContext()));
            Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, exception.Error);
        });

        var nonBooleanPredicate = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(
                Expr.Any(
                    Expr.Const(ObservationValue.FromArray([ObservationValue.FromInt64(1)])),
                    Expr.CurrentItem()),
                new RelationQueryExpressionContext()));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, nonBooleanPredicate.Error);
    }

    [Fact]
    public void Aggregate_SupportsUngroupedAggregateExprAndLogicalAggregateValues()
    {
        var expression = new AggregateExpr(
            AggregateOperator.Sum,
            Expr.Const(Array(2, 3)),
            new ScalarTypeRef(ScalarTypeKind.Decimal));

        Assert.Equal(5L, evaluator.Evaluate(expression, new RelationQueryExpressionContext()).Int64);
        Assert.Equal(2L, evaluator.Aggregate(
            AggregateOperator.Count,
            [ObservationValue.Null, ObservationValue.Undefined]).Int64);
        Assert.Equal(ObservationValueKind.Undefined, evaluator.Aggregate(
            AggregateOperator.Max,
            []).Kind);

        var average = evaluator.Evaluate(
            new AggregateExpr(
                AggregateOperator.Average,
                Expr.Const(ObservationValue.FromArray(
                [
                    ObservationValue.FromDecimal(0.1m),
                    ObservationValue.FromDecimal(0.2m)
                ])),
                new ScalarTypeRef(ScalarTypeKind.Decimal)),
            new RelationQueryExpressionContext());
        Assert.Equal(ObservationValueKind.Decimal, average.Kind);
        Assert.Equal(0.15m, average.Decimal);
        Assert.Equal(
            ObservationValueKind.Undefined,
            evaluator.Aggregate(AggregateOperator.Average, []).Kind);

        var grouped = expression with { GroupBy = [Expr.Const("group")] };
        var exception = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(grouped, new RelationQueryExpressionContext()));
        Assert.Equal(RelationQueryExpressionEvaluationError.UnsupportedExpression, exception.Error);
    }

    [Fact]
    public void Evaluate_UnknownAndAmbientFunctionsFailWithTypedErrors()
    {
        var context = Context(rootIdentity: "L1", sourceRows: [Object(("Id", ObservationValue.FromString("L1")))]);

        var unknown = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Call("unknown"), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.UnsupportedFunction, unknown.Error);
        var ambient = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Call(ExprFunctionNames.EntityId), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.UnsupportedFunction, ambient.Error);
        var arity = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Call(ExprFunctionNames.Count), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidFunctionArity, arity.Error);
    }

    static RelationQueryExpressionContext Context(
        IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding>? bindings = null,
        ValueBindingId? implicitBinding = null,
        IReadOnlyDictionary<string, ObservationValue>? parameters = null,
        string? rootIdentity = null,
        IReadOnlyList<ObservationValue>? sourceRows = null) =>
        new(
            bindings,
            implicitBinding,
            parameters,
            currentItem: null,
            rootIdentity,
            sourceRows);

    static RelationQueryExpressionContext StructuredStopsContext(params ObservationValue[] stops) =>
        Context(
            bindings: new Dictionary<ValueBindingId, RelationQueryExpressionBinding>
            {
                [LoadBinding] = new(Object(("Stops", ObservationValue.FromArray(stops))))
            });

    static ObservationValue Object(params (string Name, ObservationValue Value)[] fields) =>
        ObservationValue.FromObject(
            new ReadOnlyDictionary<string, ObservationValue>(
                fields.ToDictionary(
                    static field => field.Name,
                    static field => field.Value,
                    StringComparer.Ordinal)));

    static ObservationValue Array(params long[] values) =>
        ObservationValue.FromArray([.. values.Select(ObservationValue.FromInt64)]);

    static void AssertValues(ObservationValue actual, params long[] expected) =>
        Assert.Equal(expected, actual.EnumerateArray().Select(static value => value.Int64).ToArray());
}
