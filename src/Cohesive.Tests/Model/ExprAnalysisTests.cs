using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ExprAnalysisTests
{
    static readonly ScalarTypeRef StringType = new(ScalarTypeKind.String);
    static readonly ScalarTypeRef Int64Type = new(ScalarTypeKind.Int64);
    static readonly ValueBindingId LoadBinding = new("load");

    [Fact]
    public void Analyze_GuardsNarrowOnlyTheSelectedBranchAndRetainOptionalFieldContracts()
    {
        var scope = Scope(bindings:
        [new(LoadBinding, new(new ObjectTypeRef(
        [
            new("Id", StringType), new("Value", Int64Type, nullability: FieldNullability.Nullable),
            new("Optional", Int64Type, presence: FieldPresence.Optional)
        ])), ExprBindingAvailability.MayBeAbsent)]);
        var present = Expr.Eq(Expr.Field(LoadBinding, "Id"), Expr.Const("known"));
        var value = Expr.Field(LoadBinding, "Value");
        var compare = Expr.Le(value, Expr.Const(10L));
        var notNull = Expr.Ne(value, Expr.Null());
        var guarded = Expr.And(present, Expr.And(notNull, compare));
        Assert.True(Analyze(guarded, scope, "guarded").IsValid);
        Assert.True(Analyze(Expr.And(present, Expr.Or(Expr.Eq(value, Expr.Null()), compare)), scope, "false-guard").IsValid);
        Assert.True(Analyze(Expr.And(present, Expr.And(Expr.Not(Expr.Eq(value, Expr.Null())), compare)), scope, "negated-guard").IsValid);
        Assert.False(Analyze(Expr.And(notNull, compare), scope, "missing-is-not-null").IsValid);
        Assert.False(Analyze(Expr.Or(guarded, compare), scope, "facts-do-not-escape").IsValid);
        Assert.False(Analyze(Expr.And(present, Expr.Le(Expr.Field(LoadBinding, "Optional"), Expr.Const(10L))),
            scope, "optional-field-stays-optional").IsValid);
        Assert.False(Analyze(compare, scope, "independent-analysis").IsValid);
    }

    [Fact]
    public void Analyze_SameExpressionUnderDifferentScopes_DoesNotMutateExpression()
    {
        var expression = Expr.Field(LoadBinding, "Id");
        var visible = Analyze(
            expression,
            Scope(bindings: [Binding(LoadBinding)]),
            id: "visible");
        var hidden = Analyze(
            expression,
            ExprScope.Empty,
            id: "hidden");

        Assert.True(visible.IsValid);
        Assert.Equal(StringType, visible.KnownResult?.Type);
        Assert.Equal([LoadBinding], visible.Requirements.Bindings.ToArray());
        AssertDiagnostic(hidden, ExprAnalysisDiagnosticCodes.BindingNotVisible);
        Assert.Equal(expression, visible.Site.Expression);
        Assert.Equal(expression, hidden.Site.Expression);
    }

    [Fact]
    public void Analyze_ExposesKnownConstantsForBooleanConstantsAndTypedLiterals()
    {
        var constant = Analyze(Expr.Const(true), ExprScope.Empty, "constant-boolean");
        var literal = Analyze(
            new LiteralExpr(
                new ScalarTypeRef(ScalarTypeKind.Bool),
                ObservationValue.FromBool(false)),
            ExprScope.Empty,
            "literal-boolean");

        Assert.Equal(ObservationValue.FromBool(true), constant.KnownConstant);
        Assert.Equal(ObservationValue.FromBool(false), literal.KnownConstant);
    }

    [Fact]
    public void Analyze_DoesNotInventAConstantForNonconstantBooleanInput()
    {
        var result = Analyze(
            Expr.Param("flag"),
            Scope(parameters: [new("flag", new ScalarTypeRef(ScalarTypeKind.Bool))]),
            "nonconstant-boolean");

        Assert.True(result.IsValid);
        Assert.Null(result.KnownConstant);
    }

    [Fact]
    public void Analyze_UnqualifiedField_RequiresExplicitImplicitBinding()
    {
        var expression = Expr.Field("Id");
        var ambiguous = Analyze(
            expression,
            Scope(bindings: [Binding(LoadBinding)]),
            id: "ambiguous");
        var implicitScope = Analyze(
            expression,
            Scope(bindings: [Binding(LoadBinding)], implicitBinding: LoadBinding),
            id: "implicit");

        AssertDiagnostic(ambiguous, ExprAnalysisDiagnosticCodes.ImplicitBindingUnavailable);
        Assert.True(implicitScope.IsValid);
        var field = Assert.Single(implicitScope.Requirements.Fields);
        Assert.Equal(LoadBinding, field.Binding);
        Assert.True(field.WasUnqualified);
    }

    [Fact]
    public void Analyze_InvalidFieldPaths_ReturnDiagnosticsInsteadOfRequirementsOrExceptions()
    {
        FieldPath[] paths =
        [
            default,
            new([default]),
            new([new((SegmentKind)999)])
        ];

        foreach (var (path, index) in paths.Select(static (path, index) => (path, index)))
        {
            var result = Analyze(
                new FieldExpr(path),
                Scope(bindings: [Binding(LoadBinding)], implicitBinding: LoadBinding),
                id: $"invalid-path-{index}");

            AssertDiagnostic(result, ExprAnalysisDiagnosticCodes.FieldPathInvalid);
            Assert.Empty(result.Requirements.Fields);
        }
    }

    [Fact]
    public void Analyze_DefaultExplicitBindingProducesDiagnosticInsteadOfThrowing()
    {
        var expression = new FieldExpr(
            FieldPath.FromField("Id"),
            default(ValueBindingId));

        var result = Analyze(expression, ExprScope.Empty, id: "invalid-binding");

        AssertDiagnostic(result, ExprAnalysisDiagnosticCodes.BindingInvalid);
        Assert.Empty(result.Requirements.Fields);
        Assert.Empty(result.Requirements.Bindings);
    }

    [Fact]
    public void Analyze_ParametersAndCurrentItem_RespectTheirDeclaredScopes()
    {
        var parameter = Expr.Param("status");
        var declared = Analyze(
            parameter,
            Scope(parameters: [new("status", StringType)]),
            id: "declared");
        var undeclared = Analyze(parameter, ExprScope.Empty, id: "undeclared");
        var current = Analyze(
            Expr.CurrentItem(),
            Scope(currentItem: new(StringType)),
            id: "current");
        var noCurrent = Analyze(Expr.CurrentItem(), ExprScope.Empty, id: "no-current");

        Assert.True(declared.IsValid);
        Assert.Equal(["status"], declared.Requirements.Parameters.ToArray());
        AssertDiagnostic(undeclared, ExprAnalysisDiagnosticCodes.ParameterNotDeclared);
        Assert.True(current.IsValid);
        Assert.True(current.Requirements.RequiresCurrentItem);
        AssertDiagnostic(noCurrent, ExprAnalysisDiagnosticCodes.CurrentItemUnavailable);
    }

    [Fact]
    public void Analyze_ScopedFunctionArgument_IntroducesCurrentItem()
    {
        var expression = Expr.Call(
            ExprFunctionNames.Select,
            Expr.Const(ObservationValue.FromArray([ObservationValue.FromString("a")])),
            Expr.CurrentItem());

        var result = Analyze(expression, ExprScope.Empty, id: "select");

        Assert.True(result.IsValid);
        Assert.True(result.Requirements.RequiresCurrentItem);
        Assert.Equal(ExprResultCategory.Collection, result.ResultCategory);
        Assert.Contains(
            result.Requirements.Capabilities,
            requirement => requirement.Capability == ExprCapabilities.ForFunction(ExprFunctionNames.Select));
    }

    [Fact]
    public void Analyze_ScopedFunctionsPreserveStructuredCurrentItemsAndSelectorResults()
    {
        var itemShape = new Shape(
            new("Item"),
            [new FieldDefinition(new("Name"), StringType)]);
        var itemContract = ValueContract.FromShape(itemShape);
        var shapedCollection = new ValueContract(
            type: itemContract.Type,
            cardinality: FieldCardinality.Many);
        var scope = Scope(currentItem: shapedCollection);
        var identitySelect = Analyze(
            Expr.Call(
                ExprFunctionNames.Select,
                Expr.CurrentItem(),
                Expr.CurrentItem()),
            scope,
            "shape-only-select");
        var missingField = Analyze(
            Expr.Call(
                ExprFunctionNames.Select,
                Expr.CurrentItem(),
                Expr.Field($"{ExprFieldRoots.CurrentItem}.Missing")),
            scope,
            "shape-only-select-missing");

        Assert.True(identitySelect.IsValid);
        Assert.Equal(FieldCardinality.Many, identitySelect.KnownResult?.Cardinality);
        Assert.Equal(itemContract.Type, identitySelect.KnownResult?.Type);
        AssertDiagnostic(missingField, ExprAnalysisDiagnosticCodes.FieldPathUnknown);
    }

    [Fact]
    public void Analyze_ReservedItemFieldRoot_ResolvesAgainstScopedCurrentItem()
    {
        var itemType = new ObjectTypeRef(
        [
            new ObjectFieldTypeDef("Name", StringType)
        ]);
        var expression = Expr.Call(
            ExprFunctionNames.Select,
            new LiteralExpr(
                new ArrayTypeRef(itemType),
                ObservationValue.FromArray(
                [
                    ObservationValue.FromObject(new Dictionary<string, ObservationValue>
                    {
                        ["Name"] = ObservationValue.FromString("first")
                    })
                ])),
            Expr.Field($"{ExprFieldRoots.CurrentItem}{FieldPath.Separator}Name"));

        var result = Analyze(expression, ExprScope.Empty, id: "select-item-member");

        Assert.True(result.IsValid);
        Assert.True(result.Requirements.RequiresCurrentItem);
        Assert.Equal(ExprDependencyKind.CurrentItem, result.Requirements.Dependencies);
        Assert.Empty(result.Requirements.Bindings);
        var field = Assert.Single(result.Requirements.Fields);
        Assert.Equal(ExprFieldRootKind.CurrentItem, field.Root);
        Assert.Null(field.Binding);
        Assert.Equal("item.Name", field.Path.ToString());
        Assert.Equal(StringType, result.KnownResult?.Type);
        Assert.Equal(FieldCardinality.Many, result.KnownResult?.Cardinality);

        var restricted = ExprAnalyzer.Analyze(new(
            new("select-item-member-restricted"),
            expression,
            ExprScope.Empty,
            new(allowedDependencies: ExprDependencyKind.CurrentItem)));
        Assert.DoesNotContain(
            restricted.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.DependencyNotAllowed);
    }

    [Fact]
    public void ValueContract_RejectsConflictingQualifiedShapeIdentity()
    {
        var shape = new Shape(new("actual"), []);

        var exception = Assert.Throws<ArgumentException>(() => ValueContract.FromShape(
            shape,
            new(new("graph"), new("other"))));

        Assert.Equal("qualifiedShape", exception.ParamName);
    }

    [Fact]
    public void ValueContract_ClassifiesKnownCardinalityAndEmptyObjectShape()
    {
        var shape = new Shape(new("Empty"), []);

        Assert.Equal(
            ExprResultCategory.Collection,
            new ValueContract(cardinality: FieldCardinality.Many).GetResultCategory());
        Assert.Equal(
            ExprResultCategory.Object,
            ValueContract.FromShape(shape).GetResultCategory());
    }

    [Fact]
    public void ValueContract_EmptyObjectShapePreservesBehaviorAcrossJsonRoundTrip()
    {
        var contract = ValueContract.FromShape(new Shape(new("Empty"), []));
        var roundTrip = RoundTrip(contract);

        Assert.Equal(contract, roundTrip);
        Assert.Empty(Assert.IsType<ObjectTypeRef>(contract.Type).Fields);
        Assert.Empty(Assert.IsType<ObjectTypeRef>(roundTrip.Type).Fields);
        Assert.Equal(contract.GetResultCategory(), roundTrip.GetResultCategory());
        Assert.True(roundTrip.IsSatisfiedByConstant(
            ObservationValue.FromObject(ImmutableDictionary<string, ObservationValue>.Empty)));
        Assert.False(roundTrip.IsSatisfiedByConstant(ObservationValue.FromString("not-an-object")));
    }

    [Fact]
    public void ValueContract_NullableOptionalObjectFieldPreservesBehaviorAcrossJsonRoundTrip()
    {
        var field = new FieldDefinition(
            new("Maybe"),
            StringType,
            presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable);
        var contract = ValueContract.FromShape(new Shape(new("Optional"), [field]));
        var roundTrip = RoundTrip(contract);
        var projectedField = Assert.Single(Assert.IsType<ObjectTypeRef>(roundTrip.Type).Fields);

        Assert.Equal(contract, roundTrip);
        Assert.Equal(FieldPresence.Optional, projectedField.Presence);
        Assert.Equal(FieldNullability.Nullable, projectedField.Nullability);
        Assert.True(roundTrip.IsSatisfiedByConstant(
            ObservationValue.FromObject(ImmutableDictionary<string, ObservationValue>.Empty)));
        Assert.True(roundTrip.IsSatisfiedByConstant(ObservationValue.FromObject(
            new Dictionary<string, ObservationValue> { ["Maybe"] = ObservationValue.Null })));
        Assert.True(roundTrip.IsSatisfiedByConstant(ObservationValue.FromObject(
            new Dictionary<string, ObservationValue> { ["Maybe"] = ObservationValue.Undefined })));

        var originalAnalysis = Analyze(
            Expr.Field(LoadBinding, "Maybe"),
            Scope(bindings: [new ExprScopeBinding(LoadBinding, contract)]),
            "nullable-optional-original");
        var roundTripAnalysis = Analyze(
            Expr.Field(LoadBinding, "Maybe"),
            Scope(bindings: [new ExprScopeBinding(LoadBinding, roundTrip)]),
            "nullable-optional-round-trip");

        Assert.Equal(originalAnalysis.KnownResult, roundTripAnalysis.KnownResult);
        Assert.Equal(FieldPresence.Optional, roundTripAnalysis.KnownResult?.Presence);
        Assert.Equal(FieldNullability.Nullable, roundTripAnalysis.KnownResult?.Nullability);
    }

    [Fact]
    public void ValueContract_ObjectFieldsDistinguishSingleArrayFromManyValuesAcrossJsonRoundTrip()
    {
        var contract = ValueContract.FromShape(new Shape(
            new("Collections"),
            [
                new FieldDefinition(
                    new("SingleArray"),
                    new ArrayTypeRef(StringType),
                    cardinality: FieldCardinality.Single),
                new FieldDefinition(
                    new("ManyStrings"),
                    StringType,
                    cardinality: FieldCardinality.Many)
            ]));
        var roundTrip = RoundTrip(contract);
        var objectType = Assert.IsType<ObjectTypeRef>(roundTrip.Type);
        var singleArray = Assert.Single(objectType.Fields, static field => field.Name == "SingleArray");
        var manyStrings = Assert.Single(objectType.Fields, static field => field.Name == "ManyStrings");

        Assert.Equal(contract, roundTrip);
        Assert.Equal(FieldCardinality.Single, singleArray.Cardinality);
        Assert.IsType<ArrayTypeRef>(singleArray.Type);
        Assert.Equal(FieldCardinality.Many, manyStrings.Cardinality);
        Assert.Equal(StringType, manyStrings.Type);

        var scope = Scope(bindings: [new ExprScopeBinding(LoadBinding, roundTrip)]);
        var singleArrayResult = Analyze(
            Expr.Field(LoadBinding, "SingleArray"),
            scope,
            "single-array-field");
        var manyStringsResult = Analyze(
            Expr.Field(LoadBinding, "ManyStrings"),
            scope,
            "many-string-field");

        Assert.Equal(FieldCardinality.Single, singleArrayResult.KnownResult?.Cardinality);
        Assert.IsType<ArrayTypeRef>(singleArrayResult.KnownResult?.Type);
        Assert.Equal(FieldCardinality.Many, manyStringsResult.KnownResult?.Cardinality);
        Assert.Equal(StringType, manyStringsResult.KnownResult?.Type);
    }

    [Fact]
    public void Analyze_FunctionsExposeOperationAndAmbientCapabilities()
    {
        var expression = Expr.Call(ExprFunctionNames.EntityId);
        var unavailable = Analyze(expression, ExprScope.Empty, id: "ambient-missing");
        var available = Analyze(
            expression,
            Scope(ambient: [ExprCapabilities.EntityIdentity]),
            id: "ambient-present");
        var unsupported = ExprAnalyzer.Analyze(new(
            new("profile-missing"),
            expression,
            Scope(ambient: [ExprCapabilities.EntityIdentity]),
            capabilityProfile: ExprCapabilityProfile.None));

        AssertDiagnostic(unavailable, ExprAnalysisDiagnosticCodes.AmbientCapabilityUnavailable);
        Assert.True(available.IsValid);
        Assert.Contains(
            available.Requirements.Capabilities,
            requirement => requirement == new ExprCapabilityRequirement(
                ExprCapabilities.EntityIdentity,
                ExprCapabilityRequirementKind.Ambient));
        AssertDiagnostic(unsupported, ExprAnalysisDiagnosticCodes.CapabilityUnsupported);
    }

    [Fact]
    public void Analyze_UnknownAndInvalidArityFunctions_ProduceStructuredDiagnostics()
    {
        var unknown = Analyze(Expr.Call("custom.normalize", Expr.Const("x")), ExprScope.Empty, "unknown");
        var invalidArity = Analyze(
            Expr.Call(ExprFunctionNames.Object, Expr.Const("key")),
            ExprScope.Empty,
            "arity");

        AssertDiagnostic(unknown, ExprAnalysisDiagnosticCodes.FunctionUnknown);
        Assert.Contains(
            unknown.Requirements.Capabilities,
            requirement => requirement.Capability == ExprCapabilities.ForFunction("custom.normalize"));
        AssertDiagnostic(invalidArity, ExprAnalysisDiagnosticCodes.FunctionArityInvalid);
    }

    [Fact]
    public void Analyze_BuiltInSemanticContractsRejectKnownInvalidOperands()
    {
        var numeric = Analyze(Expr.Add(Expr.Const(1L), Expr.Const(2L)), ExprScope.Empty, "numeric-add");
        var textAdd = Analyze(Expr.Add(Expr.Const("a"), Expr.Const("b")), ExprScope.Empty, "text-add");
        var scalarCount = Analyze(
            Expr.Call(ExprFunctionNames.Count, Expr.Const(5L)),
            ExprScope.Empty,
            "scalar-count");
        var nonTextObjectKey = Analyze(
            Expr.Call(ExprFunctionNames.Object, Expr.Const(1L), Expr.Const("value")),
            ExprScope.Empty,
            "object-key");

        Assert.True(numeric.IsValid);
        Assert.Equal(ExprResultCategory.Numeric, numeric.ResultCategory);
        Assert.Null(numeric.KnownResult);
        AssertDiagnostic(textAdd, ExprAnalysisDiagnosticCodes.ResultCategoryMismatch);
        AssertDiagnostic(scalarCount, ExprAnalysisDiagnosticCodes.ResultCategoryMismatch);
        AssertDiagnostic(nonTextObjectKey, ExprAnalysisDiagnosticCodes.ResultCategoryMismatch);
    }

    [Fact]
    public void Analyze_AverageRequiresTheCanonicalDecimalResultContract()
    {
        var decimalType = new ScalarTypeRef(ScalarTypeKind.Decimal);
        var values = Expr.Const(ObservationValue.FromArray(
        [
            ObservationValue.FromDecimal(1m),
            ObservationValue.FromDecimal(2m)
        ]));

        Assert.True(ExprSemanticsCatalog.Default.TryGetAggregate(
            AggregateOperator.Average,
            out var aggregateDefinition));
        Assert.Equal(decimalType, aggregateDefinition.FixedResult?.Type);
        Assert.True(ExprSemanticsCatalog.Default.TryGetFunction(
            ExprFunctionNames.Avg,
            out var functionDefinition));
        Assert.Equal(ExprFunctionResultRule.Fixed, functionDefinition.ResultRule);
        Assert.Equal(decimalType, functionDefinition.FixedResult?.Type);

        var validAggregate = Analyze(
            new AggregateExpr(AggregateOperator.Average, values, decimalType),
            ExprScope.Empty,
            "average-aggregate-decimal");
        var invalidAggregate = Analyze(
            new AggregateExpr(AggregateOperator.Average, values, Int64Type),
            ExprScope.Empty,
            "average-aggregate-int64");
        var validFunction = Analyze(
            new CallExpr(ExprFunctionNames.Avg, [values], decimalType),
            ExprScope.Empty,
            "average-function-decimal");
        var inferredFunction = Analyze(
            Expr.Call(ExprFunctionNames.Avg, values),
            ExprScope.Empty,
            "average-function-inferred");
        var invalidFunction = Analyze(
            new CallExpr(ExprFunctionNames.Avg, [values], Int64Type),
            ExprScope.Empty,
            "average-function-int64");

        Assert.True(validAggregate.IsValid);
        Assert.Equal(decimalType, validAggregate.KnownResult?.Type);
        Assert.Equal(FieldPresence.Optional, validAggregate.KnownResult?.Presence);
        Assert.True(validFunction.IsValid);
        Assert.Equal(decimalType, validFunction.KnownResult?.Type);
        Assert.Equal(FieldPresence.Optional, validFunction.KnownResult?.Presence);
        Assert.True(inferredFunction.IsValid);
        Assert.Equal(decimalType, inferredFunction.KnownResult?.Type);
        AssertDiagnostic(invalidAggregate, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        Assert.Equal(decimalType, invalidAggregate.KnownResult?.Type);
        AssertDiagnostic(invalidFunction, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        Assert.Equal(decimalType, invalidFunction.KnownResult?.Type);
    }

    [Fact]
    public void Analyze_EndsWithDeclaresExactTextPredicateSemanticsAndCapability()
    {
        Assert.True(ExprSemanticsCatalog.Default.TryGetFunction(
            ExprFunctionNames.EndsWith,
            out var definition));
        Assert.Equal(new ExprFunctionArity(2, 2), definition.Arity);
        Assert.Equal(
            [ExprResultCategory.Text, ExprResultCategory.Text],
            definition.ArgumentCategories.ToArray());
        Assert.Equal(ExprResultCategory.Boolean, definition.ResultCategory);
        Assert.Equal(ExprFunctionResultRule.Fixed, definition.ResultRule);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.Bool), definition.FixedResult?.Type);

        var valid = Analyze(
            Expr.EndsWith(Expr.Const("Load-ABC"), Expr.Const("ABC")),
            ExprScope.Empty,
            "ends-with");
        var nonText = Analyze(
            Expr.EndsWith(Expr.Const(1), Expr.Const("1")),
            ExprScope.Empty,
            "ends-with-non-text");
        var nullish = Analyze(
            Expr.EndsWith(Expr.Null(), Expr.Const("suffix")),
            ExprScope.Empty,
            "ends-with-nullish");
        var invalidArity = Analyze(
            Expr.Call(ExprFunctionNames.EndsWith, Expr.Const("value")),
            ExprScope.Empty,
            "ends-with-arity");

        Assert.True(valid.IsValid);
        Assert.Equal(ExprResultCategory.Boolean, valid.ResultCategory);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.Bool), valid.KnownResult?.Type);
        Assert.Equal(FieldPresence.Required, valid.KnownResult?.Presence);
        Assert.Equal(FieldNullability.NonNullable, valid.KnownResult?.Nullability);
        Assert.Contains(
            valid.Requirements.Capabilities,
            requirement => requirement == new ExprCapabilityRequirement(
                ExprCapabilities.ForFunction(ExprFunctionNames.EndsWith),
                ExprCapabilityRequirementKind.Operation));
        AssertDiagnostic(nonText, ExprAnalysisDiagnosticCodes.ResultCategoryMismatch);
        AssertDiagnostic(nullish, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        AssertDiagnostic(invalidArity, ExprAnalysisDiagnosticCodes.FunctionArityInvalid);
    }

    [Fact]
    public void Analyze_ConstrainedOperationCategoriesRejectNullishOrMaybeNullOperands()
    {
        var boolType = new ScalarTypeRef(ScalarTypeKind.Bool);
        var intType = new ScalarTypeRef(ScalarTypeKind.Int64);
        var shape = new Shape(
            new("MaybeValues"),
            [
                new FieldDefinition(
                    new("Flag"),
                    boolType,
                    nullability: FieldNullability.Nullable),
                new FieldDefinition(
                    new("Count"),
                    intType,
                    presence: FieldPresence.Optional)
            ]);
        var scope = Scope(
            bindings:
            [
                new(LoadBinding, ValueContract.FromShape(shape))
            ],
            implicitBinding: LoadBinding);

        ExprAnalysisResult[] results =
        [
            Analyze(Expr.Not(Expr.Null()), ExprScope.Empty, "not-null"),
            Analyze(
                Expr.Add(Expr.Const(ObservationValue.Undefined), Expr.Const(1L)),
                ExprScope.Empty,
                "add-undefined"),
            Analyze(Expr.Not(Expr.Field("Flag")), scope, "not-maybe-null"),
            Analyze(Expr.Add(Expr.Field("Count"), Expr.Const(1L)), scope, "add-maybe-absent")
        ];

        Assert.All(results, result => AssertDiagnostic(
            result,
            ExprAnalysisDiagnosticCodes.ResultTypeMismatch));
    }

    [Fact]
    public void Analyze_FunctionResultRulesRemainAuthoritativeOverDeclaredReturnTypes()
    {
        var valuesType = new ArrayTypeRef(StringType);
        var scope = Scope(parameters: [new("values", valuesType)]);
        var append = Analyze(
            new CallExpr(
                ExprFunctionNames.Append,
                [Expr.Param("values"), Expr.Const("x")],
                returnType: StringType),
            scope,
            "append-return");
        var groupedRows = Analyze(
            new CallExpr(
                ExprFunctionNames.GroupByRows,
                [Expr.Param("values"), Expr.CurrentItem()]),
            scope,
            "grouped-rows");

        AssertDiagnostic(append, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        Assert.Equal(valuesType, append.KnownResult?.GetEffectiveType());
        Assert.Equal(ExprResultCategory.Collection, groupedRows.ResultCategory);
        Assert.Null(groupedRows.KnownResult);
    }

    [Fact]
    public void Analyze_DeclaredCallTypeDoesNotInventPresenceOrNullabilityGuarantees()
    {
        var rowsType = new ArrayTypeRef(StringType);
        var expression = new CallExpr(
            ExprFunctionNames.SourceRows,
            [],
            rowsType);
        var scope = Scope(ambient: [ExprCapabilities.SourceSet]);

        var unconstrained = Analyze(expression, scope, "source-rows");
        var required = Analyze(
            expression,
            scope,
            "required-source-rows",
            new(value: new(rowsType)));

        Assert.True(unconstrained.IsValid);
        Assert.Equal(FieldPresence.Optional, unconstrained.KnownResult?.Presence);
        Assert.Equal(FieldNullability.Nullable, unconstrained.KnownResult?.Nullability);
        AssertDiagnostic(required, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
    }

    [Fact]
    public void Analyze_DiagnosesTypedMetadataThatContradictsKnownSemantics()
    {
        var boolean = new ScalarTypeRef(ScalarTypeKind.Bool);
        var scope = Scope(bindings: [Binding(LoadBinding)], implicitBinding: LoadBinding);
        Expr[] expressions =
        [
            new FieldRefExpr(FieldPath.FromField("Id"), boolean),
            new LiteralExpr(boolean, ObservationValue.FromString("not-boolean")),
            new ConditionalExpr(Expr.Const(true), Expr.Const("yes"), Expr.Const("no"), boolean),
            new AggregateExpr(
                AggregateOperator.Count,
                Expr.Const(ObservationValue.FromArray([ObservationValue.FromString("x")])),
                StringType)
        ];

        var results = expressions
            .Select((expression, index) => Analyze(expression, scope, $"metadata-{index}"))
            .ToArray();

        Assert.All(results, result => AssertDiagnostic(result, ExprAnalysisDiagnosticCodes.ResultTypeMismatch));
        Assert.Equal(ExprResultCategory.Text, results[0].ResultCategory);
        Assert.Equal(ExprResultCategory.Text, results[1].ResultCategory);
        Assert.Equal(ExprResultCategory.Text, results[2].ResultCategory);
        Assert.Equal(ExprResultCategory.Integer, results[3].ResultCategory);
    }

    [Fact]
    public void Analyze_TypedLiteralsRejectIncompatibleConstantPayloads()
    {
        var int32 = new ScalarTypeRef(ScalarTypeKind.Int32);
        var int32Array = new ArrayTypeRef(int32);
        var objectType = new ObjectTypeRef(
        [
            new ObjectFieldTypeDef("Count", int32)
        ]);
        Expr[] expressions =
        [
            new LiteralExpr(int32, ObservationValue.FromInt64(long.MaxValue)),
            new LiteralExpr(
                new ScalarTypeRef(ScalarTypeKind.Int64),
                ObservationValue.FromDouble(9_223_372_036_854_775_808d)),
            new LiteralExpr(
                int32Array,
                ObservationValue.FromArray([ObservationValue.FromInt64(long.MaxValue)])),
            new LiteralExpr(
                objectType,
                ObservationValue.FromObject(new Dictionary<string, ObservationValue>
                {
                    ["Count"] = ObservationValue.FromString("not-a-number")
                }))
        ];

        var results = expressions
            .Select((expression, index) => Analyze(
                expression,
                ExprScope.Empty,
                $"invalid-literal-{index}"))
            .ToArray();

        Assert.All(results, result => AssertDiagnostic(
            result,
            ExprAnalysisDiagnosticCodes.ResultTypeMismatch));
    }

    [Fact]
    public void Analyze_ConstantTypingDoesNotInferStringToBooleanOrNumericConversions()
    {
        (string Value, ScalarTypeKind Target)[] cases =
        [
            ("true", ScalarTypeKind.Bool),
            ("42", ScalarTypeKind.Int32),
            ("42", ScalarTypeKind.Int64),
            ("42.5", ScalarTypeKind.Decimal)
        ];

        var results = cases
            .Select((item, index) => Analyze(
                Expr.Const(item.Value),
                ExprScope.Empty,
                $"no-string-conversion-{index}",
                new(value: new(new ScalarTypeRef(item.Target)))))
            .ToArray();

        Assert.All(results, result => AssertDiagnostic(
            result,
            ExprAnalysisDiagnosticCodes.ResultTypeMismatch));
    }

    [Fact]
    public void Analyze_HighPrecisionDecimalConstant_PreservesNumericValueContract()
    {
        const decimal expected = 12345678901234567890.123456789m;
        var decimalResult = Analyze(
            Expr.Const(expected),
            ExprScope.Empty,
            "decimal-constant",
            new(value: new(new ScalarTypeRef(ScalarTypeKind.Decimal))));
        var jsonNumberResult = Analyze(
            Expr.Const(expected),
            ExprScope.Empty,
            "json-number-constant",
            new(value: new(new JsonTypeRef(JsonTypeKind.Number))));

        Assert.True(decimalResult.IsValid);
        Assert.True(jsonNumberResult.IsValid);
        Assert.Equal(
            ScalarTypeKind.Decimal,
            Assert.IsType<ScalarTypeRef>(decimalResult.KnownResult?.Type).Kind);
    }

    [Fact]
    public void ValueContractsValidateKnownCompositeStructureAroundUnresolvedNestedTypes()
    {
        var unresolved = new NamedTypeRef(new("Unresolved"));
        var arrayContract = new ValueContract(new ArrayTypeRef(unresolved));

        Assert.False(arrayContract.IsSatisfiedByConstant(ObservationValue.FromString("not-an-array")));
        Assert.True(arrayContract.IsSatisfiedByConstant(ObservationValue.FromArray(
        [
            ObservationValue.FromString("externally-resolved-item")
        ])));

        var literal = Analyze(
            new LiteralExpr(
                new ArrayTypeRef(unresolved),
                ObservationValue.FromString("not-an-array")),
            ExprScope.Empty,
            "invalid-unresolved-array");
        AssertDiagnostic(literal, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);

        var malformedNestedType = new ValueContract(new ArrayTypeRef(null!));
        Assert.True(malformedNestedType.IsSatisfiedByConstant(ObservationValue.FromArray(
        [
            ObservationValue.FromString("requires-structural-validation")
        ])));
    }

    [Fact]
    public void Analyze_CanonicalStringTemporalLiteralSatisfiesExactTargetCategoryAndType()
    {
        var date = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Date));

        var result = Analyze(
            Expr.Const("2026-07-14"),
            ExprScope.Empty,
            "date-literal",
            new(ExprResultCategory.Temporal, date));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("2026-07-17T12:34:56Z", true)]
    [InlineData("2026-07-17T12:34:56-07:00", true)]
    [InlineData("2026-07-17T12:34:56", false)]
    public void Analyze_InstantStringLiteralRequiresExplicitOffset(string text, bool expected)
    {
        var instant = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Instant));

        var result = Analyze(
            Expr.Const(text),
            ExprScope.Empty,
            "instant-literal",
            new(ExprResultCategory.Temporal, instant));

        Assert.Equal(expected, result.IsValid);
    }

    [Fact]
    public void Analyze_ConditionalsJoinBranchGuaranteesWithoutInventingAConstant()
    {
        var nullableString = Analyze(
            new ConditionalExpr(
                Expr.Const(true),
                Expr.Const("value"),
                Expr.Null(),
                StringType),
            ExprScope.Empty,
            "nullable-conditional",
            new(value: new(StringType)));
        var wideInteger = Analyze(
            new ConditionalExpr(
                Expr.Const(true),
                Expr.Const(1L),
                Expr.Const(long.MaxValue),
                Int64Type),
            ExprScope.Empty,
            "wide-conditional",
            new(value: new(new ScalarTypeRef(ScalarTypeKind.Int32))));

        Assert.Equal(FieldNullability.Nullable, nullableString.KnownResult?.Nullability);
        AssertDiagnostic(nullableString, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        AssertDiagnostic(wideInteger, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
    }

    [Fact]
    public void Analyze_ConditionalReturnTypeDoesNotInventPresenceOrNullabilityGuarantees()
    {
        var result = Analyze(
            new ConditionalExpr(
                Expr.Const(true),
                Expr.Const("value"),
                Expr.Null(),
                StringType),
            ExprScope.Empty,
            "nullable-conditional-without-site-guarantees");

        Assert.True(result.IsValid);
        Assert.Equal(StringType, result.KnownResult?.Type);
        Assert.Equal(FieldNullability.Nullable, result.KnownResult?.Nullability);
    }

    [Fact]
    public void Analyze_NullableConditionalPreservesNonNullBranchShape()
    {
        var shape = new Shape(
            new("Result"),
            [new FieldDefinition(new("Value"), StringType)]);
        var shapedValue = ValueContract.FromShape(shape);
        var result = Analyze(
            new ConditionalExpr(
                Expr.Const(true),
                Expr.CurrentItem(),
                Expr.Null(),
                shapedValue.Type),
            Scope(currentItem: shapedValue),
            "nullable-shaped-conditional");

        Assert.True(result.IsValid);
        Assert.Equal(shapedValue.Type, result.KnownResult?.Type);
        Assert.Equal(FieldNullability.Nullable, result.KnownResult?.Nullability);
    }

    [Fact]
    public void Analyze_ConditionalBranchesMustEachSatisfyDeclaredResultType()
    {
        var result = Analyze(
            new ConditionalExpr(
                Expr.Const(true),
                Expr.Const("value"),
                Expr.Const(1L),
                StringType),
            ExprScope.Empty,
            "mixed-conditional");

        AssertDiagnostic(result, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        Assert.Contains(
            result.Validation.Diagnostics,
            static diagnostic => diagnostic.SchemaLocation == "/ifFalse");
    }

    [Fact]
    public void Analyze_ConditionalPreservesValidatedEncodedBranchType()
    {
        var date = new ScalarTypeRef(ScalarTypeKind.Date);
        var result = Analyze(
            new ConditionalExpr(
                Expr.Const(true),
                Expr.Const("2026-07-14"),
                Expr.Const("2026-07-15"),
                date),
            ExprScope.Empty,
            "encoded-date-conditional");

        Assert.True(result.IsValid);
        Assert.Equal(date, result.KnownResult?.Type);
        Assert.Equal(ExprResultCategory.Temporal, result.ResultCategory);
    }

    [Fact]
    public void Analyze_DeclaredUnknownTypeDoesNotEraseProvenOperationCategory()
    {
        var named = new NamedTypeRef(new("ExternalResult"));
        var result = Analyze(
            new AggregateExpr(
                AggregateOperator.Any,
                Expr.Const(ObservationValue.FromArray([ObservationValue.FromBool(true)])),
                named),
            ExprScope.Empty,
            "named-aggregate-result");

        Assert.True(result.IsValid);
        Assert.Equal(ExprResultCategory.Boolean, result.ResultCategory);
        Assert.Equal(named, result.KnownResult?.Type);
    }

    [Fact]
    public void Analyze_ObjectConstructionIsPresentNonNullAndRetainsItsDeclaredStructuralType()
    {
        var objectType = new ObjectTypeRef(
        [
            new("Name", StringType)
        ]);
        var expected = new ValueContract(objectType);
        var result = Analyze(
            new CallExpr(
                ExprFunctionNames.Object,
                [Expr.Const("Name"), Expr.Const("Ada")],
                objectType),
            ExprScope.Empty,
            "object-construction",
            new(value: expected));

        Assert.True(result.IsValid);
        Assert.Equal(objectType, result.KnownResult?.Type);
        Assert.Equal(FieldPresence.Required, result.KnownResult?.Presence);
        Assert.Equal(FieldNullability.NonNullable, result.KnownResult?.Nullability);
    }

    [Fact]
    public void ValueContract_RejectsMalformedShapeFieldMetadataAtConstructionBoundary()
    {
        var malformedField = new FieldDefinition(
            new("Value"),
            StringType) with
        {
            Presence = (FieldPresence)999
        };
        var shape = new Shape(new("Malformed"), [malformedField]);

        var exception = Assert.Throws<ArgumentException>(() => ValueContract.FromShape(shape));
        var defaultIdentityException = Assert.Throws<ArgumentException>(() => new ValueContract(
            shape: default(QualifiedShapeId)));

        Assert.Equal("shape", exception.ParamName);
        Assert.Equal("shape", defaultIdentityException.ParamName);
    }

    [Fact]
    public void Analyze_ConditionalDoesNotGuessTypeFromNonconstantUnknownBranch()
    {
        var maybe = new ValueBindingId("maybe");
        var scope = Scope(
            bindings:
            [
                new ExprScopeBinding(
                    maybe,
                    new ValueContract(),
                    ExprBindingAvailability.MayBeAbsent)
            ]);

        var result = Analyze(
            Expr.If(
                Expr.Const(true),
                Expr.Field(maybe, "Unknown"),
                Expr.Const(1L)),
            scope,
            "unknown-conditional-branch");

        Assert.Null(result.KnownResult?.Type);
        Assert.Equal(FieldPresence.Optional, result.KnownResult?.Presence);
        Assert.Equal(ExprResultCategory.Any, result.ResultCategory);
    }

    [Fact]
    public void Analyze_ResultExpectationsValidateKnownCategoryAndType()
    {
        var category = Analyze(
            Expr.Const("not boolean"),
            ExprScope.Empty,
            "category",
            ExprExpectation.Boolean);
        var type = Analyze(
            Expr.Const(42L),
            ExprScope.Empty,
            "type",
            new(value: new(StringType)));

        AssertDiagnostic(category, ExprAnalysisDiagnosticCodes.ResultCategoryMismatch);
        AssertDiagnostic(type, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
    }

    [Fact]
    public void Analyze_ConstantValuesCanRefineTheirPortableTargetTypes()
    {
        var guid = Guid.Parse("37bde78c-ff55-4984-b96f-f266cde342e6");
        var instant = DateTimeOffset.Parse(
            "2026-07-14T12:34:56.0000000+00:00",
            CultureInfo.InvariantCulture);
        (Expr Expression, TypeRef ExpectedType)[] cases =
        [
            (Expr.Const(1), new ScalarTypeRef(ScalarTypeKind.Int32)),
            (Expr.Const(guid), new ScalarTypeRef(ScalarTypeKind.Guid)),
            (Expr.Const(instant), new ScalarTypeRef(ScalarTypeKind.Instant))
        ];

        var results = cases
            .Select((item, index) => Analyze(
                item.Expression,
                ExprScope.Empty,
                $"constant-refinement-{index}",
                new(value: new(item.ExpectedType))))
            .ToArray();

        Assert.All(results, result => Assert.True(
            result.IsValid,
            string.Join("; ", result.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message))));
    }

    [Fact]
    public void Analyze_NullAndUndefinedConstantsPreserveValueGuarantees()
    {
        var required = new ValueContract(StringType);
        var nullable = new ValueContract(
            StringType,
            nullability: FieldNullability.Nullable);
        var nullAtRequiredSite = Analyze(
            Expr.Null(),
            ExprScope.Empty,
            "null-required",
            new(value: required));
        var nullAtNullableSite = Analyze(
            Expr.Null(),
            ExprScope.Empty,
            "null-nullable",
            new(value: nullable));
        var undefinedAtRequiredSite = Analyze(
            Expr.Const(ObservationValue.Undefined),
            ExprScope.Empty,
            "undefined-required",
            new(value: required));

        AssertDiagnostic(nullAtRequiredSite, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        Assert.True(nullAtNullableSite.IsValid);
        Assert.Equal(FieldNullability.Nullable, nullAtNullableSite.KnownResult?.Nullability);
        AssertDiagnostic(undefinedAtRequiredSite, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        Assert.Equal(FieldPresence.Optional, undefinedAtRequiredSite.KnownResult?.Presence);
    }

    [Fact]
    public void Analyze_OrderingSupportsTemporalValuesAndRejectsUndefinedOrderingSemantics()
    {
        var date = Expr.Gt(
            Expr.Const(ObservationValue.FromDateOnly(new(2026, 7, 14))),
            Expr.Const(ObservationValue.FromDateOnly(new(2026, 7, 13))));
        var bytes = Expr.Lt(
            Expr.Const(ObservationValue.FromBytes(new byte[] { 1 })),
            Expr.Const(ObservationValue.FromBytes(new byte[] { 2 })));

        Assert.True(Analyze(date, ExprScope.Empty, "date-ordering").IsValid);
        AssertDiagnostic(
            Analyze(bytes, ExprScope.Empty, "bytes-ordering"),
            ExprAnalysisDiagnosticCodes.ResultCategoryMismatch);
    }

    [Fact]
    public void Analyze_MaybeAbsentBinding_ProducesAnOptionalFieldResult()
    {
        var result = Analyze(
            Expr.Field(LoadBinding, "Id"),
            Scope(bindings: [Binding(LoadBinding, ExprBindingAvailability.MayBeAbsent)]),
            "outer-join-field",
            new(value: new(StringType, presence: FieldPresence.Required)));

        Assert.Equal(FieldPresence.Optional, result.KnownResult?.Presence);
        AssertDiagnostic(result, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
    }

    [Fact]
    public void Analyze_NestedPathsComposeParentPresenceAndNullability()
    {
        var addressType = new ObjectTypeRef(
        [
            new ObjectFieldTypeDef("City", StringType)
        ]);
        var loadShape = new Shape(
            new("load"),
            [
                new FieldDefinition(
                    new("Address"),
                    addressType,
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable)
            ]);
        var scope = Scope(
            bindings:
            [
                new(
                    LoadBinding,
                    ValueContract.FromShape(loadShape))
            ]);
        var result = Analyze(
            Expr.Field(LoadBinding, "Address.City"),
            scope,
            "nested-guarantees",
            new(value: new(StringType)));

        Assert.Equal(FieldPresence.Optional, result.KnownResult?.Presence);
        Assert.Equal(FieldNullability.Nullable, result.KnownResult?.Nullability);
        AssertDiagnostic(result, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
    }

    [Fact]
    public void Analyze_UnresolvedPathsPreserveProvenOptionalAndNullableGuarantees()
    {
        var related = new ValueBindingId("customer");
        var unavailable = Analyze(
            Expr.Field(related, "Name"),
            Scope(bindings:
            [
                new(
                    related,
                    new ValueContract(shape: new(new("domain"), new("Customer"))),
                    ExprBindingAvailability.MayBeAbsent)
            ]),
            "unresolved-related",
            new(value: new(StringType)));
        var typedNested = Analyze(
            new FieldRefExpr(FieldPath.Parse("Customer.Name"), StringType),
            Scope(
                bindings:
                [
                    new(
                        LoadBinding,
                        new ValueContract(new ObjectTypeRef(
                        [
                            new ObjectFieldTypeDef(
                                "Customer",
                                new NamedTypeRef(new("Customer")),
                                presence: FieldPresence.Optional)
                        ])))
                ],
                implicitBinding: LoadBinding),
            "unresolved-nested",
            new(value: new(StringType)));

        Assert.Equal(FieldPresence.Optional, unavailable.KnownResult?.Presence);
        AssertDiagnostic(unavailable, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        Assert.Equal(StringType, typedNested.KnownResult?.Type);
        Assert.Equal(FieldPresence.Optional, typedNested.KnownResult?.Presence);
        AssertDiagnostic(typedNested, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
    }

    [Fact]
    public void Analyze_EquivalentArrayContractsAndCardinalityNeutralConstantsSatisfyCollectionTargets()
    {
        var expected = new ValueContract(
            StringType,
            cardinality: FieldCardinality.Many,
            nullability: FieldNullability.Nullable);
        var arrayParameter = Analyze(
            Expr.Param("values"),
            Scope(parameters: [new("values", new ArrayTypeRef(StringType))]),
            "array-parameter",
            new(value: expected));
        var arrayConstant = Analyze(
            Expr.Const(ObservationValue.FromArray([ObservationValue.FromString("value")])),
            ExprScope.Empty,
            "array-constant",
            new(value: expected));
        var nullConstant = Analyze(
            Expr.Null(),
            ExprScope.Empty,
            "array-null",
            new(value: expected));

        Assert.True(arrayParameter.IsValid);
        Assert.True(arrayConstant.IsValid);
        Assert.True(nullConstant.IsValid);
    }

    [Fact]
    public void Analyze_RequiredBooleanExpectationsRejectNullAndOptionalResults()
    {
        var nullable = Analyze(Expr.Null(), ExprScope.Empty, "null-predicate", ExprExpectation.Boolean);
        var optional = Analyze(
            Expr.Param("predicate"),
            Scope(parameters:
            [
                new(
                    "predicate",
                    new ValueContract(
                        new ScalarTypeRef(ScalarTypeKind.Bool),
                        presence: FieldPresence.Optional),
                    FieldPresence.Optional)
            ]),
            "optional-predicate",
            ExprExpectation.Boolean);

        AssertDiagnostic(nullable, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        AssertDiagnostic(optional, ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
    }

    [Fact]
    public void Analyze_RetainsSemanticsAndStructuredCapabilityUseProvenance()
    {
        var semantics = ExprSemanticsCatalog.Default;
        var result = ExprAnalyzer.Analyze(
            new(
                new("unsupported-add"),
                Expr.Add(Expr.Const(1), Expr.Const(2)),
                ExprScope.Empty,
                capabilityProfile: ExprCapabilityProfile.None),
            semantics);

        Assert.Same(semantics, result.Semantics);
        Assert.Contains(
            result.CapabilityUses,
            use => use.Requirement.Capability == ExprCapabilities.ForBinary(BinaryOperator.Add)
                && use.ExpressionPath == "/"
                && !use.IsSatisfied);
    }

    [Fact]
    public void Analyze_RetainsEveryFieldOccurrenceAtItsDeterministicExpressionPath()
    {
        var fieldPath = FieldPath.FromField("Id");
        var expression = new ConditionalExpr(
            Expr.Eq(Expr.Field("Id"), Expr.Const("selected")),
            Expr.Field("Id"),
            new FieldRefExpr(fieldPath, StringType),
            StringType);
        var result = Analyze(
            expression,
            Scope(bindings: [Binding(LoadBinding)], implicitBinding: LoadBinding),
            "field-occurrences");

        Assert.True(result.IsValid);
        var aggregate = Assert.Single(result.Requirements.Fields);
        Assert.Equal(LoadBinding, aggregate.Binding);
        Assert.True(aggregate.WasUnqualified);
        Assert.Collection(
            result.FieldUses,
            use =>
            {
                Assert.Equal("/ifFalse", use.ExpressionPath);
                Assert.Equal(aggregate, use.Requirement);
            },
            use =>
            {
                Assert.Equal("/ifTrue", use.ExpressionPath);
                Assert.Equal(aggregate, use.Requirement);
            },
            use =>
            {
                Assert.Equal("/test/left", use.ExpressionPath);
                Assert.Equal(aggregate, use.Requirement);
            });
        Assert.Empty(result.BindingUses);
    }

    [Fact]
    public void Analyze_DistinguishesWholeBindingOccurrencesFromFieldRootBindings()
    {
        var expression = Expr.Eq(
            new BindingExpr(LoadBinding),
            new BindingExpr(LoadBinding));
        var result = Analyze(
            expression,
            Scope(bindings: [Binding(LoadBinding)]),
            "binding-occurrences");

        Assert.True(result.IsValid);
        Assert.Equal([LoadBinding], result.Requirements.Bindings.ToArray());
        Assert.Empty(result.Requirements.Fields);
        Assert.Empty(result.FieldUses);
        Assert.Collection(
            result.BindingUses,
            use =>
            {
                Assert.Equal(LoadBinding, use.Binding);
                Assert.Equal("/left", use.ExpressionPath);
            },
            use =>
            {
                Assert.Equal(LoadBinding, use.Binding);
                Assert.Equal("/right", use.ExpressionPath);
            });
    }

    [Fact]
    public void AnalysisResult_NormalizesAndValidatesOccurrenceProjectionsAgainstRequirements()
    {
        var site = new ExprSite(new("occurrences"), Expr.Const(true), ExprScope.Empty);
        ExprFieldRequirement field = new(
            FieldPath.FromField("Id"),
            ExprFieldRootKind.Binding,
            LoadBinding);
        ExprRequirements requirements = new(fields: [field], bindings: [LoadBinding]);
        var result = new ExprAnalysisResult(
            site,
            ExprSemanticsCatalog.Default,
            ExprResultCategory.Boolean,
            new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)),
            requirements,
            [],
            DocumentValidationResult.Valid,
            knownConstant: ObservationValue.FromBool(true),
            fieldUses: [new(field, "/z"), new(field, "/a")],
            bindingUses: [new(LoadBinding, "/y"), new(LoadBinding, "/b")]);

        Assert.Equal(["/a", "/z"], result.FieldUses.Select(static use => use.ExpressionPath).ToArray());
        Assert.Equal(["/b", "/y"], result.BindingUses.Select(static use => use.ExpressionPath).ToArray());

        Assert.Throws<ArgumentException>(() => new ExprAnalysisResult(
            site,
            ExprSemanticsCatalog.Default,
            ExprResultCategory.Boolean,
            knownResult: null,
            requirements,
            [],
            DocumentValidationResult.Valid,
            fieldUses: [new(field, "/same"), new(field, "/same")]));
        Assert.Throws<ArgumentException>(() => new ExprAnalysisResult(
            site,
            ExprSemanticsCatalog.Default,
            ExprResultCategory.Boolean,
            knownResult: null,
            requirements,
            [],
            DocumentValidationResult.Valid,
            bindingUses: [new(LoadBinding, " ")]));
        Assert.Throws<ArgumentException>(() => new ExprAnalysisResult(
            site,
            ExprSemanticsCatalog.Default,
            ExprResultCategory.Boolean,
            knownResult: null,
            ExprRequirements.Empty,
            [],
            DocumentValidationResult.Valid,
            fieldUses: [new(field, "/field")]));
    }

    [Fact]
    public void Analyze_DisallowedDependenciesAreDiagnosedAfterRequirementsAreDerived()
    {
        var scope = Scope(
            bindings: [Binding(LoadBinding)],
            implicitBinding: LoadBinding,
            parameters: [new("cursor", StringType)]);
        var expression = Expr.Eq(Expr.Field("Id"), Expr.Param("cursor"));
        var result = Analyze(
            expression,
            scope,
            "boundary",
            new(allowedDependencies: ExprDependencyKind.Parameter));

        Assert.Equal(ExprDependencyKind.Binding | ExprDependencyKind.Parameter, result.Requirements.Dependencies);
        AssertDiagnostic(result, ExprAnalysisDiagnosticCodes.DependencyNotAllowed);
    }

    [Fact]
    public void Analyze_CoversEveryCanonicalExpressionNode()
    {
        var scope = Scope(
            bindings: [Binding(LoadBinding)],
            implicitBinding: LoadBinding,
            parameters: [new("status", StringType)],
            currentItem: new(StringType));
        Expr[] expressions =
        [
            Expr.Field(LoadBinding, "Id"),
            new FieldRefExpr(FieldPath.FromField("Id"), StringType),
            Expr.CurrentItem(),
            Expr.Param("status"),
            Expr.Const(true),
            new LiteralExpr(StringType, ObservationValue.FromString("literal")),
            Expr.Not(Expr.Const(false)),
            Expr.Eq(Expr.Const(1), Expr.Const(1)),
            new ConditionalExpr(Expr.Const(true), Expr.Const("yes"), Expr.Const("no"), StringType),
            new CallExpr(ExprFunctionNames.Count,
                [Expr.Const(ObservationValue.FromArray([ObservationValue.FromString("x")]))]),
            new AggregateExpr(
                AggregateOperator.Count,
                Expr.Const(ObservationValue.FromArray([ObservationValue.FromString("x")])),
                Int64Type)
        ];

        var results = expressions
            .Select((expression, index) => Analyze(expression, scope, $"node-{index}"))
            .ToArray();

        Assert.All(results, result => Assert.DoesNotContain(
            result.Validation.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.NodeUnsupported));
        Assert.All(results, result => Assert.True(result.IsValid, string.Join("; ", result.Validation.Diagnostics.Select(x => x.Message))));
    }

    [Fact]
    public void DefaultCatalog_DefinesEveryBuiltInFunctionName()
    {
        var names = typeof(ExprFunctionNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.True(
            ExprSemanticsCatalog.Default.TryGetFunction(name, out _),
            $"Missing semantics for function '{name}'."));
    }

    [Fact]
    public void SemanticContractsRejectInternallyContradictoryDefinitions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExprFunctionArity(1, 1, 2));
        Assert.Throws<InvalidOperationException>(() => default(ExprFunctionArity).Accepts(0));
        Assert.Throws<InvalidOperationException>(() => default(ExprFunctionArity).Describe());
        Assert.Throws<ArgumentException>(() => new ExprFunctionDefinition(
            new("invalid.first"),
            new(0),
            resultRule: ExprFunctionResultRule.FirstArgument));
        Assert.Throws<ArgumentException>(() => new ExprFunctionDefinition(
            new("invalid.unconstrained-first"),
            new(1, 1),
            resultCategory: ExprResultCategory.Collection,
            resultRule: ExprFunctionResultRule.FirstArgument));
        Assert.Throws<ArgumentException>(() => new ExprFunctionDefinition(
            new("invalid.selector"),
            new(1),
            resultCategory: ExprResultCategory.Boolean,
            resultRule: ExprFunctionResultRule.CollectionOfSelector));
        Assert.Throws<ArgumentException>(() => new ExprFunctionDefinition(
            new("invalid.multiple-selectors"),
            new(3, 3),
            argumentCategories:
            [
                ExprResultCategory.Collection,
                ExprResultCategory.Any,
                ExprResultCategory.Any
            ],
            resultCategory: ExprResultCategory.Collection,
            resultRule: ExprFunctionResultRule.CollectionOfSelector,
            scopedArguments: [new(1, 0), new(2, 0)]));
        Assert.Throws<ArgumentException>(() => new ExprFunctionDefinition(
            new("invalid.missing-selector-source"),
            new(1, 3),
            argumentCategories:
            [
                ExprResultCategory.Any,
                ExprResultCategory.Any,
                ExprResultCategory.Collection
            ],
            scopedArguments: [new(1, 2)]));
        Assert.Throws<ArgumentException>(() => new ExprSemanticsCatalog(
            unaryOperators:
            [
                new(
                    UnaryOperator.Not,
                    ExprResultCategory.Boolean,
                    ExprResultCategory.Boolean,
                    new ValueContract(StringType))
            ]));
        Assert.Throws<ArgumentException>(() => new ExprSemanticsCatalog(
            binaryOperators:
            [
                new(
                    BinaryOperator.Add,
                    ExprResultCategory.Numeric,
                    ExprResultCategory.Numeric,
                    ExprResultCategory.Numeric,
                new ValueContract(StringType))
            ]));
        Assert.Throws<ArgumentException>(() => new ExprSemanticsCatalog(
            aggregateOperators:
            [
                new(
                    AggregateOperator.Average,
                    ExprResultCategory.Collection,
                    ExprResultCategory.Numeric,
                    new ValueContract(StringType))
            ]));
        Assert.Throws<ArgumentException>(() => new ExprExpectation(
            ExprResultCategory.Boolean,
            new ValueContract(StringType)));
        var site = new ExprSite(new("invalid-result"), Expr.Const(true), ExprScope.Empty);
        Assert.Throws<ArgumentException>(() => new ExprAnalysisResult(
            site,
            ExprSemanticsCatalog.Default,
            ExprResultCategory.Boolean,
            new ValueContract(StringType),
            ExprRequirements.Empty,
            [],
            DocumentValidationResult.Valid));
        Assert.Throws<ArgumentException>(() => new ExprAnalysisResult(
            site,
            ExprSemanticsCatalog.Default,
            ExprResultCategory.Text,
            new ValueContract(StringType),
            ExprRequirements.Empty,
            [],
            DocumentValidationResult.Valid,
            ObservationValue.FromBool(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExprSemanticsCatalog.Default.Functions[0].GetArgumentCategory(-1));
    }

    [Fact]
    public void ScopeAndAnalysis_AreDeterministicAcrossDeclarationOrderAndCulture()
    {
        var customer = new ValueBindingId("customer");
        var expression = Expr.And(
            Expr.Eq(Expr.Field(LoadBinding, "Id"), Expr.Param("loadId")),
            Expr.Eq(Expr.Field(customer, "Id"), Expr.Param("customerId")));

        var firstScope = Scope(
            bindings: [Binding(LoadBinding), Binding(customer)],
            parameters: [new("loadId", StringType), new("customerId", StringType)]);
        var secondScope = Scope(
            bindings: [Binding(customer), Binding(LoadBinding)],
            parameters: [new("customerId", StringType), new("loadId", StringType)]);

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var first = Analyze(expression, firstScope, "deterministic");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var second = Analyze(expression, secondScope, "deterministic");

            Assert.Equal(first.Requirements.Fields.ToArray(), second.Requirements.Fields.ToArray());
            Assert.Equal(first.Requirements.Bindings.ToArray(), second.Requirements.Bindings.ToArray());
            Assert.Equal(first.Requirements.Parameters.ToArray(), second.Requirements.Parameters.ToArray());
            Assert.Equal(first.Requirements.Capabilities.ToArray(), second.Requirements.Capabilities.ToArray());
            Assert.Equal(first.Validation.Diagnostics.ToArray(), second.Validation.Diagnostics.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    static ExprAnalysisResult Analyze(
        Expr expression,
        ExprScope scope,
        string id,
        ExprExpectation? expectation = null) =>
        ExprAnalyzer.Analyze(new(new(id), expression, scope, expectation));

    static ValueContract RoundTrip(ValueContract contract) =>
        JsonSerializer.Deserialize<ValueContract>(JsonSerializer.Serialize(contract))
        ?? throw new InvalidOperationException("A serialized value contract produced a null result.");

    static ExprScope Scope(
        IEnumerable<ExprScopeBinding>? bindings = null,
        ValueBindingId? implicitBinding = null,
        IEnumerable<ExprScopeParameter>? parameters = null,
        ValueContract? currentItem = null,
        IEnumerable<ExprCapabilityId>? ambient = null) =>
        new(bindings, implicitBinding, parameters, currentItem, ambient);

    static ExprScopeBinding Binding(
        ValueBindingId id,
        ExprBindingAvailability availability = ExprBindingAvailability.AlwaysPresent) => new(
        id,
        new ValueContract(
            new ObjectTypeRef(
            [
                new ObjectFieldTypeDef("Id", StringType),
                new ObjectFieldTypeDef("Count", Int64Type)
            ])),
        availability);

    static void AssertDiagnostic(ExprAnalysisResult result, string code) =>
        Assert.Contains(result.Validation.Diagnostics, diagnostic => diagnostic.Code == code);
}
