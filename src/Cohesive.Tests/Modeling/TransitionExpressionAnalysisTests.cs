using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Tests.Modeling;

public sealed class TransitionExpressionAnalysisTests
{
    [Fact]
    public void Analyze_UsesSharedScopesTargetContractsAndRuntimeProfile()
    {
        var entity = BuildValidEntity();

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        Assert.True(analysis.IsValid, FormatDiagnostics(analysis));
        Assert.Equal(
            analysis.Sites.Select(static site => site.Site.Id.Value).Order(StringComparer.Ordinal).ToArray(),
            analysis.Sites.Select(static site => site.Site.Id.Value).ToArray());

        var precondition = Site(analysis, "/precondition/AmountMustBeNonnegative");
        var amountUpdate = Site(analysis, "/update/Amount");
        var tagsUpdate = Site(analysis, "/update/Tags");
        var computed = Site(analysis, "/computed/ProjectedAmount");
        var invariant = Site(analysis, "/invariant/ProjectedAmountMustBeNonnegative");

        Assert.Equal(TransitionExpressionAnalyzer.EntityStateBinding, precondition.Site.Scope.ImplicitBinding);
        Assert.Contains(precondition.Site.Scope.Parameters, parameter => parameter.Name == "amount");
        Assert.Contains("amount", precondition.Requirements.Parameters);
        Assert.Contains("amount", amountUpdate.Requirements.Parameters);
        Assert.Empty(computed.Site.Scope.Parameters);
        Assert.Empty(invariant.Site.Scope.Parameters);

        var amountContract = Assert.IsType<ExprValueContract>(amountUpdate.Site.Expectation.Value);
        Assert.IsType<ScalarTypeRef>(amountContract.GetEffectiveType());
        Assert.Equal(FieldCardinality.Single, amountContract.Cardinality);
        Assert.Equal(FieldPresence.Required, amountContract.Presence);
        Assert.Equal(FieldNullability.NonNullable, amountContract.Nullability);

        var tagsContract = Assert.IsType<ExprValueContract>(tagsUpdate.Site.Expectation.Value);
        var tagsType = Assert.IsType<ArrayTypeRef>(tagsContract.GetEffectiveType());
        Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(tagsType.ElementType).Kind);
        Assert.Equal(FieldCardinality.Many, tagsContract.Cardinality);
        Assert.Equal(FieldPresence.Optional, tagsContract.Presence);
        Assert.Equal(FieldNullability.Nullable, tagsContract.Nullability);

        var computedContract = Assert.IsType<ExprValueContract>(computed.Site.Expectation.Value);
        Assert.Equal(ScalarTypeKind.Decimal, Assert.IsType<ScalarTypeRef>(computedContract.Type).Kind);
        Assert.Equal(ExprResultCategory.Boolean, invariant.Site.Expectation.Category);
        Assert.True(precondition.Site.Scope.HasAmbientCapability(ExprCapabilities.EntityIdentity));
        Assert.True(precondition.Site.CapabilityProfile.Supports(ExprCapabilities.ForFunction(ExprFunctionNames.Append)));
        Assert.True(precondition.Site.CapabilityProfile.Supports(ExprCapabilities.Conditional));
        Assert.False(precondition.Site.CapabilityProfile.Supports(ExprCapabilities.CurrentItem));
        Assert.False(precondition.Site.CapabilityProfile.Supports(ExprCapabilities.ForFunction(ExprFunctionNames.Contains)));
        Assert.Contains("amount", analysis.Requirements.Parameters);
    }

    [Fact]
    public void Analyze_ExposesParametersOnlyToTransitionSites()
    {
        Expr sharedParameter = Expr.Param("flag");
        var flag = FieldDefinition.Create(
            new("Flag"),
            new ScalarTypeRef(ScalarTypeKind.Bool));
        var computed = FieldDefinition.Create(
            new("ComputedFlag"),
            new ScalarTypeRef(ScalarTypeKind.Bool),
            mutability: FieldMutability.Computed,
            compute: new(sharedParameter));
        var transition = new TransitionDefinition(
            name: "SetFlag",
            inputs: [new("flag", new ScalarTypeRef(ScalarTypeKind.Bool))],
            preconditions: [new("FlagInputMustBeTrue", sharedParameter)],
            updates: [new("Flag", sharedParameter)]);
        var entity = new EntityDefinition(
            new("Probe"),
            fields: [flag, computed],
            invariants: [new("ComputedFlagMustBeTrue", sharedParameter)],
            transitions: [transition]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        var precondition = Site(analysis, "/precondition/FlagInputMustBeTrue");
        var update = Site(analysis, "/update/Flag");
        var computedSite = Site(analysis, "/computed/ComputedFlag");
        var invariant = Site(analysis, "/invariant/ComputedFlagMustBeTrue");
        Assert.True(precondition.IsValid, FormatDiagnostics(precondition));
        Assert.True(update.IsValid, FormatDiagnostics(update));
        Assert.Contains(
            computedSite.Validation.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.ParameterNotDeclared);
        Assert.Contains(
            invariant.Validation.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.ParameterNotDeclared);
        Assert.Empty(computedSite.Site.Scope.Parameters);
        Assert.Empty(invariant.Site.Scope.Parameters);
    }

    [Fact]
    public void Analyze_RejectsForeignBindingAndCurrentItem()
    {
        var transition = new TransitionDefinition(name: "Probe") with
        {
            Preconditions =
            [
                new(
                    "ForeignBinding",
                    Expr.Eq(
                        Expr.Field(new ValueBindingId("foreign"), "Value"),
                        Expr.Const("ready"))),
                new(
                    "CurrentItem",
                    Expr.Eq(Expr.CurrentItem(), Expr.Const("ready")))
            ]
        };
        var entity = new EntityDefinition(
            new("Probe"),
            fields:
            [
                FieldDefinition.Create(
                    new("Value"),
                    new ScalarTypeRef(ScalarTypeKind.String))
            ],
            transitions: [transition]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        var foreign = Site(analysis, "/precondition/ForeignBinding");
        var currentItem = Site(analysis, "/precondition/CurrentItem");
        Assert.Contains(
            foreign.Validation.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.BindingNotVisible);
        Assert.Contains(
            currentItem.Validation.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.CurrentItemUnavailable);
        Assert.Contains(
            currentItem.Validation.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.CapabilityUnsupported);
        Assert.True(currentItem.Requirements.RequiresCurrentItem);
    }

    [Fact]
    public void Analyze_DefaultExplicitBindingProducesStructuredDiagnostic()
    {
        var expression = new FieldExpr(
            FieldPath.FromField("Value"),
            default(ValueBindingId));
        var entity = new EntityDefinition(
            new("Probe"),
            fields:
            [
                FieldDefinition.Create(
                    new("Value"),
                    new ScalarTypeRef(ScalarTypeKind.String))
            ],
            invariants: [new("BindingIsValid", Expr.Eq(expression, Expr.Const("ready")))]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        Assert.Contains(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.BindingInvalid);
    }

    [Fact]
    public void Analyze_MalformedEntityIdentityAndShapeProduceStructuredDiagnostics()
    {
        var entity = BuildValidEntity();

        var missingIdentity = TransitionExpressionAnalyzer.Analyze(entity with { Name = default });
        var missingShape = TransitionExpressionAnalyzer.Analyze(entity with { Shape = null! });

        Assert.Contains(
            missingIdentity.Diagnostics,
            static diagnostic => diagnostic.Code ==
                TransitionExpressionAnalysisDiagnosticCodes.EntityIdentityMissing);
        Assert.Contains(
            missingShape.Diagnostics,
            static diagnostic => diagnostic.Code ==
                TransitionExpressionAnalysisDiagnosticCodes.EntityShapeMissing);
        Assert.Empty(missingShape.Sites);
    }

    [Fact]
    public void Analyze_InvalidEntityFieldValueMetadataProducesStructuredDiagnostics()
    {
        var entity = BuildValidEntity();
        var malformedField = entity.Shape.Fields[0] with
        {
            Type = null!,
            Cardinality = (FieldCardinality)997,
            Presence = (FieldPresence)998,
            Nullability = (FieldNullability)999
        };
        var malformedShape = new Shape(
            entity.Shape.Id,
            [malformedField, .. entity.Shape.Fields.Skip(1)],
            entity.Shape.Constraints,
            entity.Shape.Annotations);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity with { Shape = malformedShape });

        Assert.Equal(4, analysis.Diagnostics.Count(static diagnostic =>
            diagnostic.Code == TransitionExpressionAnalysisDiagnosticCodes.EntityShapeInvalid));
        Assert.Empty(analysis.Sites);
    }

    [Fact]
    public void Analyze_RejectsPortableNodesThatTransitionRuntimeDoesNotImplement()
    {
        var boolean = new ScalarTypeRef(ScalarTypeKind.Bool);
        var entity = new EntityDefinition(
            new("Probe"),
            fields: [FieldDefinition.Create(new("Flag"), boolean)],
            invariants:
            [
                new("TypedField", new FieldRefExpr(FieldPath.FromField("Flag"), boolean)),
                new("TypedLiteral", new LiteralExpr(boolean, ObservationValue.FromBool(true)))
            ]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        foreach (var site in analysis.Sites)
        {
            Assert.Contains(
                site.Validation.Diagnostics,
                diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.CapabilityUnsupported);
        }
    }

    [Fact]
    public void Analyze_RejectsNestedFieldPathsThatTransitionRuntimeCannotEvaluate()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var addressType = new ObjectTypeRef(
        [
            new ObjectFieldTypeDef("City", stringType)
        ]);
        var entity = new EntityDefinition(
            new("Probe"),
            fields: [FieldDefinition.Create(new("Address"), addressType)],
            invariants:
            [
                new(
                    "CityRequired",
                    Expr.Eq(
                        Expr.Field("Address.City"),
                        Expr.Const("Seattle")))
            ]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);
        var invariant = Site(analysis, "/invariant/CityRequired");

        Assert.Contains(
            invariant.Validation.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.CapabilityUnsupported);
    }

    [Fact]
    public void Analyze_IsDeterministicAcrossDeclarationOrder()
    {
        var forward = TransitionExpressionAnalyzer.Analyze(BuildOrderedEntity(reverse: false));
        var reverse = TransitionExpressionAnalyzer.Analyze(BuildOrderedEntity(reverse: true));

        Assert.Equal(
            forward.Sites.Select(static site => site.Site.Id.Value).ToArray(),
            reverse.Sites.Select(static site => site.Site.Id.Value).ToArray());
        Assert.Equal(forward.Requirements.Fields.ToArray(), reverse.Requirements.Fields.ToArray());
        Assert.Equal(forward.Requirements.Bindings.ToArray(), reverse.Requirements.Bindings.ToArray());
        Assert.Equal(forward.Requirements.Parameters.ToArray(), reverse.Requirements.Parameters.ToArray());
        Assert.Equal(
            forward.Diagnostics.Select(static diagnostic => (diagnostic.Location, diagnostic.Code, diagnostic.Message)).ToArray(),
            reverse.Diagnostics.Select(static diagnostic => (diagnostic.Location, diagnostic.Code, diagnostic.Message)).ToArray());
    }

    [Fact]
    public void Analyze_DiagnosesDuplicateSemanticSiteIdentitiesWithoutOrderSuffixes()
    {
        var transition = new TransitionDefinition(
            name: "Probe",
            preconditions:
            [
                new("Duplicate", Expr.Const(true)),
                new("Duplicate", Expr.Const(false))
            ]);
        var entity = new EntityDefinition(
            new("Probe"),
            fields:
            [
                FieldDefinition.Create(
                    new("Value"),
                    new ScalarTypeRef(ScalarTypeKind.String))
            ],
            transitions: [transition]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        var diagnostic = Assert.Single(
            analysis.Diagnostics,
            item => item.Code == TransitionExpressionAnalysisDiagnosticCodes.DefinitionIdentityDuplicate);
        Assert.Contains("Duplicate", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            analysis.Sites,
            site => site.Site.Id.Value.EndsWith("/precondition/Duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_DiagnosesUnknownUpdateTargetAndStillAnalyzesItsExpression()
    {
        var transition = new TransitionDefinition(
            name: "Probe",
            inputs: [new("value", new ScalarTypeRef(ScalarTypeKind.String))],
            updates: [new("Missing", Expr.Param("value"))]);
        var entity = new EntityDefinition(
            new("Probe"),
            fields:
            [
                FieldDefinition.Create(
                    new("Value"),
                    new ScalarTypeRef(ScalarTypeKind.String))
            ],
            transitions: [transition]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        var site = Site(analysis, "/update/Missing");
        Assert.Contains("value", site.Requirements.Parameters);
        var diagnostic = Assert.Single(
            analysis.Diagnostics,
            item => item.Code == TransitionExpressionAnalysisDiagnosticCodes.UpdateTargetMissing);
        Assert.Contains("Missing", diagnostic.Message, StringComparison.Ordinal);
        Assert.False(analysis.IsValid);
    }

    [Fact]
    public void Analyze_MalformedExpressionPayloadsProduceDiagnosticsWithoutThrowing()
    {
        var malformedCall = new CallExpr(
            ExprFunctionNames.Count,
            [Expr.Const(ObservationValue.FromArray([]))]) with
        {
            Arguments = default
        };
        var missingUpdate = new FieldUpdateDefinition("Value", Expr.Const("value")) with
        {
            ValueExpression = null!
        };
        var transition = new TransitionDefinition(
            "Inspect",
            preconditions: [new("Valid", Expr.Const(true))],
            updates: [new("Value", Expr.Const("value"))]) with
        {
            Preconditions = [new("MalformedCall", malformedCall)],
            Updates = [missingUpdate]
        };
        var entity = new EntityDefinition(
            new("Probe"),
            fields:
            [
                FieldDefinition.Create(
                    new("Value"),
                    new ScalarTypeRef(ScalarTypeKind.String))
            ],
            transitions: [transition]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.FunctionArityInvalid);
        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.ExpressionMissing);
    }

    [Fact]
    public void Analyze_MalformedDefinitionCollectionsAndFieldPathsDoNotThrow()
    {
        var malformedTransition = new TransitionDefinition("Inspect") with
        {
            Inputs = [null!],
            Preconditions = [null!],
            Updates = [null!]
        };
        var entity = new EntityDefinition(
            new("Probe"),
            fields:
            [
                FieldDefinition.Create(
                    new("Value"),
                    new ScalarTypeRef(ScalarTypeKind.String))
            ]) with
        {
            Invariants =
            [
                new("MalformedPath", new FieldExpr(default))
            ],
            Transitions = [malformedTransition]
        };

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == TransitionExpressionAnalysisDiagnosticCodes.DefinitionEntryMissing);
        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.FieldPathInvalid);
    }

    [Fact]
    public void Analyze_AmbiguousTransitionInputsAreDiagnosedAndOmittedFromScope()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var transition = new TransitionDefinition(
            "Inspect",
            inputs: [new("value", stringType)],
            preconditions: [new("HasValue", Expr.Eq(Expr.Param("value"), Expr.Const("x")))]) with
        {
            Inputs =
            [
                new("value", stringType),
                new("value", new ScalarTypeRef(ScalarTypeKind.Int64))
            ]
        };
        var entity = new EntityDefinition(
            new("Probe"),
            fields: [FieldDefinition.Create(new("Value"), stringType)]) with
        {
            Transitions = [transition]
        };

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == TransitionExpressionAnalysisDiagnosticCodes.DefinitionIdentityDuplicate);
        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.ParameterNotDeclared);
        Assert.Empty(Site(analysis, "/precondition/HasValue").Site.Scope.Parameters);
    }

    [Fact]
    public void Analyze_ArrayInputCanPopulateManyValuedField()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var transition = new TransitionDefinition(
            "SetTags",
            inputs: [new("tags", new ArrayTypeRef(stringType))],
            updates: [new("Tags", Expr.Param("tags"))]);
        var entity = new EntityDefinition(
            new("Probe"),
            fields:
            [
                FieldDefinition.Create(
                    new("Tags"),
                    stringType,
                    cardinality: FieldCardinality.Many)
            ],
            transitions: [transition]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        Assert.True(analysis.IsValid, FormatDiagnostics(analysis));
    }

    [Fact]
    public void Analyze_IntegerArithmeticDoesNotGuessAConversionResultType()
    {
        var intType = new ScalarTypeRef(ScalarTypeKind.Int32);
        var transition = new TransitionDefinition(
            "Increment",
            inputs: [new("delta", intType)],
            updates: [new("Count", Expr.Add(Expr.Field("Count"), Expr.Param("delta")))]);
        var entity = new EntityDefinition(
            new("Counter"),
            fields: [FieldDefinition.Create(new("Count"), intType)],
            transitions: [transition]);

        var analysis = TransitionExpressionAnalyzer.Analyze(entity);

        Assert.True(analysis.IsValid, FormatDiagnostics(analysis));
        Assert.Equal(
            ExprResultCategory.Numeric,
            Site(analysis, "/update/Count").ResultCategory);
        Assert.Null(Site(analysis, "/update/Count").KnownResult);
    }

    [Fact]
    public void Analyze_DoesNotChangeTransitionRuntimeExecution()
    {
        var entity = BuildValidEntity();
        var analysis = TransitionExpressionAnalyzer.Analyze(entity);
        var runtime = new DeclarativeEntityRuntime(entity);
        var state = entity.CreateState(new
        {
            Amount = 1m,
            Tags = new[] { "priority" },
            ProjectedAmount = 1m
        });

        var result = runtime.Apply(
            entityId: "probe-1",
            state,
            version: 3,
            transitionName: "SetAmount",
            input: ObservationValue.FromObject(new { amount = 12m }));

        Assert.True(analysis.IsValid, FormatDiagnostics(analysis));
        Assert.Equal(12m, result.NewState.Fields["Amount"].GetDecimal());
        Assert.Equal(12m, result.NewState.Fields["ProjectedAmount"].GetDecimal());
        Assert.Equal(4, result.NewVersion);
    }

    [Fact]
    public void Analyze_TemporalOrderingRemainsPortableAndExecutable()
    {
        var dateType = new ScalarTypeRef(ScalarTypeKind.DateTime);
        var entity = new EntityDefinition(
            new("Probe"),
            fields: [FieldDefinition.Create(new("EffectiveDate"), dateType)],
            invariants:
            [
                new(
                    "EffectiveDateIsCurrent",
                    Expr.Ge(
                        Expr.Field("EffectiveDate"),
                        Expr.Const("2026-01-01T00:00:00.0000000+00:00")))
            ],
            transitions: [new("Inspect")]);
        var analysis = TransitionExpressionAnalyzer.Analyze(entity);
        var runtime = new DeclarativeEntityRuntime(entity);
        var state = entity.CreateState(new
        {
            EffectiveDate = "2026-07-14T00:00:00.0000000+00:00"
        });

        var result = runtime.Apply(
            "probe-1",
            state,
            1,
            "Inspect",
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>()));

        Assert.True(analysis.IsValid, FormatDiagnostics(analysis));
        Assert.Equal(2, result.NewVersion);
    }

    static EntityDefinition BuildValidEntity()
    {
        var decimalType = new ScalarTypeRef(ScalarTypeKind.Decimal);
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var amount = FieldDefinition.Create(new("Amount"), decimalType);
        var tags = FieldDefinition.Create(
            new("Tags"),
            stringType,
            cardinality: FieldCardinality.Many,
            presence: FieldPresence.Optional);
        var projectedAmount = FieldDefinition.Create(
            new("ProjectedAmount"),
            decimalType,
            mutability: FieldMutability.Computed,
            compute: new(Expr.Field("Amount")));
        var nonnegative = Expr.Ge(
            Expr.Field("ProjectedAmount"),
            Expr.Const(ObservationValue.FromDecimal(0m)));
        var transition = new TransitionDefinition(
            name: "SetAmount",
            inputs: [new("amount", decimalType)],
            preconditions:
            [
                new(
                    "AmountMustBeNonnegative",
                    Expr.Ge(
                        Expr.Param("amount"),
                        Expr.Const(ObservationValue.FromDecimal(0m))))
            ],
            updates:
            [
                new("Amount", Expr.Param("amount")),
                new("Tags", Expr.Field("Tags"))
            ]);
        return new(
            new("Probe"),
            fields: [amount, tags, projectedAmount],
            invariants: [new("ProjectedAmountMustBeNonnegative", nonnegative)],
            transitions: [transition]);
    }

    static EntityDefinition BuildOrderedEntity(bool reverse)
    {
        ImmutableArray<TransitionPreconditionDefinition> preconditions =
        [
            new("Alpha", Expr.Eq(Expr.Field("Value"), Expr.Const("a"))),
            new("Zulu", Expr.Eq(Expr.Field("Value"), Expr.Const("z")))
        ];
        if (reverse)
            preconditions = [.. preconditions.Reverse()];

        return new(
            new("Probe"),
            fields:
            [
                FieldDefinition.Create(
                    new("Value"),
                    new ScalarTypeRef(ScalarTypeKind.String))
            ],
            transitions: [new TransitionDefinition("Inspect", preconditions: preconditions)]);
    }

    static ExprAnalysisResult Site(
        TransitionExpressionAnalysisResult analysis,
        string idSuffix) => Assert.Single(
        analysis.Sites,
        site => site.Site.Id.Value.EndsWith(idSuffix, StringComparison.Ordinal));

    static string FormatDiagnostics(TransitionExpressionAnalysisResult analysis) =>
        string.Join(Environment.NewLine, analysis.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));

    static string FormatDiagnostics(ExprAnalysisResult analysis) =>
        string.Join(Environment.NewLine, analysis.Validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
}
