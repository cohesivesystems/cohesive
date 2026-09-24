using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationDraftDefaultAcceptanceTests
{
    static readonly ScalarTypeRef Text = new(ScalarTypeKind.String);
    static readonly ValueBindingId Binding = new("source");
    static readonly QualifiedShapeId Source = new(new("source"), new("root"));
    static readonly QualifiedShapeId Target = new(new("target"), new("root"));
    static Expr Read => Expr.Field(Binding, "value");

    [Fact]
    public void RequireValue_PreservesSourceGraphForNamedTypes()
    {
        var named = new NamedTypeRef(new("Code"));
        var (graphs, draft) = Fixture(Expr.Call(ExprFunctionNames.RequireValue, Read));
        graphs[0] = new(Source.GraphId, [new Shape(Source.ShapeId, [new(new("value"), named, presence: FieldPresence.Optional)])],
            [new TypeDefinition.Enum(new("Code"), PrimitiveType.String, [new("Source", "00")])]);
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId, [new(new("value"), named)])],
            [new TypeDefinition.Enum(new("Code"), PrimitiveType.String, [new("Target", "01")])]);
        Assert.False(RelationDraftAcceptor.Accept(draft, graphs).IsAccepted);
    }

    [Theory]
    [InlineData("arity")]
    [InlineData("return")]
    [InlineData("nested")]
    [InlineData("cardinality")]
    public void RequireValue_CannotInventTypesOrNestedGuarantees(string scenario)
    {
        var expression = scenario == "arity" ? Expr.Call(ExprFunctionNames.RequireValue)
            : scenario == "return" ? new CallExpr(ExprFunctionNames.RequireValue, [Read], new ScalarTypeRef(ScalarTypeKind.Int32))
            : Expr.Call(ExprFunctionNames.RequireValue, Read);
        var (graphs, draft) = Fixture(expression);
        if (scenario == "nested")
        {
            graphs[0] = new(Source.GraphId, [new Shape(Source.ShapeId, [new(new("value"),
                new ObjectTypeRef([new("name", Text, presence: FieldPresence.Optional)]), presence: FieldPresence.Optional)])]);
            graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId, [new(new("value"),
                new ObjectTypeRef([new("name", Text)]))])]);
        }
        if (scenario == "cardinality")
            graphs[0] = new(Source.GraphId, [new Shape(Source.ShapeId, [new(new("value"), Text, cardinality: FieldCardinality.Many)])]);
        Assert.False(RelationDraftAcceptor.Accept(draft, graphs).IsAccepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequireValue_RoundtripsAndRejectsAbsentSourceWithoutInventingValue(bool collection)
    {
        var (graphs, draft) = Fixture(Expr.Call(ExprFunctionNames.RequireValue, Read), collection);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(RelationDraftDocument.FromDraft(draft)));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        Assert.Equal(RelationDraftAcceptor.Accept(draft, graphs).DefinitionFingerprint, accepted.DefinitionFingerprint);
        foreach (var input in new[] { ObservationValue.Undefined, ObservationValue.Null,
            collection ? ObservationValue.FromArray([]) : ObservationValue.FromString("") })
        {
            var fields = input.Kind == ObservationValueKind.Undefined ? ImmutableDictionary<string, ObservationValue>.Empty
                : ImmutableDictionary<string, ObservationValue>.Empty.Add("value", input);
            var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
                .Evaluate(new("tests/required-draft"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
                .Supply([new RelationQuerySuppliedRoot("row", Source, fields)]).Build();
            var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
            Assert.Equal(input.Kind is not (ObservationValueKind.Undefined or ObservationValueKind.Null), outcome.IsSuccessful);
            if (outcome.IsSuccessful)
                Assert.Equal(input, Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows).Value.GetProperty("value"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitDefault_RoundtripsAcceptsAndExecutesMissingNullAndPresent(bool collection)
    {
        var fallback = collection ? Expr.Const(ObservationValue.FromArray([])) : Expr.Const("unspecified");
        var expression = Expr.Coalesce(Read, fallback);
        if (collection) expression = Expr.Call(ExprFunctionNames.Select, expression, Expr.CurrentItem());
        var (graphs, draft) = Fixture(expression, collection);
        var document = RelationDraftDocument.FromDraft(draft);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        Assert.Equal(document.DraftFingerprint, accepted.Provenance.DraftFingerprint);
        Assert.Equal(accepted.DefinitionFingerprint, RelationDraftAcceptor.Accept(draft, graphs).DefinitionFingerprint);
        var present = collection ? ObservationValue.FromArray([ObservationValue.FromString("b"), ObservationValue.FromString("a")]) : ObservationValue.FromString("");
        foreach (var input in new[] { ObservationValue.Undefined, ObservationValue.Null, present })
        {
            var fields = input.Kind == ObservationValueKind.Undefined ? ImmutableDictionary<string, ObservationValue>.Empty
                : ImmutableDictionary<string, ObservationValue>.Empty.Add("value", input);
            var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
                .Evaluate(new("tests/default-draft"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
                .Supply([new RelationQuerySuppliedRoot("row", Source, fields)]).Build();
            var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
            Assert.True(outcome.IsSuccessful, outcome.ToString());
            var result = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows).Value.GetProperty("value");
            Assert.Equal(input.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null ? ((ConstantExpr)fallback).Value : present, result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullDefault_PreservesTypeAndProducesPresentNullForAbsentInput(bool collection)
    {
        var (graphs, draft) = Fixture(Expr.Coalesce(Read, Expr.Null()), collection);
        var cardinality = collection ? FieldCardinality.Many : FieldCardinality.Single;
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId,
            [new(new("value"), Text, cardinality: cardinality, nullability: FieldNullability.Nullable)])]);
        var document = RelationDraftDocument.FromDraft(draft);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        foreach (var input in new[] { ObservationValue.Undefined, ObservationValue.Null,
            collection ? ObservationValue.FromArray([ObservationValue.FromString("")]) : ObservationValue.FromString("") })
        {
            var fields = input.Kind == ObservationValueKind.Undefined ? ImmutableDictionary<string, ObservationValue>.Empty
                : ImmutableDictionary<string, ObservationValue>.Empty.Add("value", input);
            var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
                .Evaluate(new("tests/null-default"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
                .Supply([new RelationQuerySuppliedRoot("row", Source, fields)]).Build();
            var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
            Assert.True(outcome.IsSuccessful, outcome.ToString());
            var row = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows);
            var result = row.Value.GetProperty("value");
            Assert.Equal(input.Kind == ObservationValueKind.Undefined ? ObservationValue.Null : input, result);
            _ = Observation.Create(new(graphs[1], Target.ShapeId), row.Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreachableNullDefault_RetainsRequiredNonNullSourceGuarantee(bool convert)
    {
        Expr expression = Expr.Coalesce(Read, Expr.Null());
        if (convert) expression = Expr.Call(ExprFunctionNames.ParseDecimal, expression);
        var (graphs, draft) = Fixture(expression);
        graphs[0] = new(Source.GraphId, [new Shape(Source.ShapeId,
            [new(new("value"), Text, presence: FieldPresence.Required, nullability: FieldNullability.NonNullable)])]);
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId,
            [new(new("value"), convert ? new ScalarTypeRef(ScalarTypeKind.Decimal) : Text,
                presence: FieldPresence.Required, nullability: FieldNullability.NonNullable)])]);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(RelationDraftDocument.FromDraft(draft)));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
            .Evaluate(new("tests/unreachable-null-default"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
            .Supply([new RelationQuerySuppliedRoot("row", Source,
                ImmutableDictionary<string, ObservationValue>.Empty.Add("value", ObservationValue.FromString("0012.50")))]).Build();
        var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
        Assert.True(outcome.IsSuccessful, outcome.ToString());
        var row = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows);
        Assert.Equal(convert ? ObservationValue.FromDecimal(12.5m) : ObservationValue.FromString("0012.50"), row.Value.GetProperty("value"));
        _ = Observation.Create(new(graphs[1], Target.ShapeId), row.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullDefault_DoesNotMakeASelectSourceNonNull(bool nested)
    {
        Expr expression = Expr.Call(ExprFunctionNames.Select, Expr.Coalesce(Read, Expr.Null()), Expr.CurrentItem());
        if (nested) expression = Expr.Call(ExprFunctionNames.Single, expression);
        var (graphs, draft) = Fixture(expression, collection: true);
        if (nested) graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId, [new(new("value"), Text)])]);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.select.sourceMayBeAbsent");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullDefault_DoesNotMakeAConversionSourceNonNull(bool collection)
    {
        var expression = Expr.Call(collection ? ExprFunctionNames.Single : ExprFunctionNames.ParseDecimal,
            Expr.Coalesce(Read, Expr.Null()));
        var (graphs, draft) = Fixture(expression, collection);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.conversion.sourceMayBeAbsent");
    }

    [Theory]
    [InlineData("wrong-fallback", "relationDraft.constant.incompatible")]
    [InlineData("null-fallback", "relationDraft.assignment.nullabilityUnsafe")]
    [InlineData("computed-fallback", "relationDraft.default.fallbackUnsupported")]
    [InlineData("arity", "relationDraft.default.argumentsInvalid")]
    [InlineData("return-type", "relationDraft.default.returnTypeMismatch")]
    [InlineData("unknown-source", "relationDraft.candidate.pathUnknown")]
    public void Default_RejectsUnprovenContracts(string scenario, string code)
    {
        var expression = scenario switch
        {
            "wrong-fallback" => Expr.Coalesce(Read, Expr.Const(123)),
            "null-fallback" => Expr.Coalesce(Read, Expr.Null()),
            "computed-fallback" => Expr.Coalesce(Read, Read),
            "arity" => Expr.Call(ExprFunctionNames.Coalesce, Read),
            "return-type" => new CallExpr(ExprFunctionNames.Coalesce, [Read, Expr.Const("fallback")], new ScalarTypeRef(ScalarTypeKind.Int32)),
            _ => Expr.Coalesce(Expr.Field(Binding, "missing"), Expr.Const("fallback"))
        };
        var (graphs, draft) = Fixture(expression);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public void NoDefault_RetainsOptionalAndNullableRejection()
    {
        var (graphs, draft) = Fixture(Read);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.assignment.presenceUnsafe");
        Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.assignment.nullabilityUnsafe");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PortableLiteral_CanPopulateScalarOrEmptyCollection(bool collection)
    {
        var expression = collection ? Expr.Const(ObservationValue.FromArray([])) : Expr.Const("authored");
        var (graphs, draft) = Fixture(expression, collection);
        Assert.True(RelationDraftAcceptor.Accept(draft, graphs).IsAccepted);
    }

    [Theory]
    [InlineData("array")]
    [InlineData("object")]
    [InlineData("undefined")]
    public void StructuralAndNonportableConstantsRemainUnsupported(string kind)
    {
        var value = kind switch
        {
            "array" => ObservationValue.FromArray([ObservationValue.FromString("filled")]),
            "object" => ObservationValue.FromObject(new Dictionary<string, ObservationValue>()),
            _ => ObservationValue.Undefined
        };
        var (graphs, draft) = Fixture(Expr.Const(value), collection: kind == "array");
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(result.IsAccepted);
        Assert.Contains(result.Diagnostics, d => d.Code == "relationDraft.constant.unsupported");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedOptionalDecimal_RoundtripsAndExecutesLazily(bool equality)
    {
        var test = new BinaryExpr(equality ? BinaryOperator.Eq : BinaryOperator.Ne, Expr.Coalesce(Read, Expr.Null()), Expr.Null());
        var parse = Expr.Call(ExprFunctionNames.ParseDecimal, Read);
        var expression = Expr.If(test, equality ? Expr.Null() : parse, equality ? parse : Expr.Null());
        var (graphs, draft) = Fixture(expression);
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId,
            [new(new("value"), new ScalarTypeRef(ScalarTypeKind.Decimal), nullability: FieldNullability.Nullable)])]);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(RelationDraftDocument.FromDraft(draft)));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        foreach (var input in new[] { ObservationValue.Undefined, ObservationValue.Null, ObservationValue.FromString("0012.50"), ObservationValue.FromString("bad"), ObservationValue.FromString("") })
        {
            var fields = input.Kind == ObservationValueKind.Undefined ? ImmutableDictionary<string, ObservationValue>.Empty
                : ImmutableDictionary<string, ObservationValue>.Empty.Add("value", input);
            var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
                .Evaluate(new("tests/guarded-optional-decimal"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
                .Supply([new RelationQuerySuppliedRoot("row", Source, fields)]).Build();
            var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
            if (input.Kind == ObservationValueKind.String && input.String != "0012.50")
            {
                Assert.False(outcome.IsSuccessful);
                continue;
            }
            Assert.True(outcome.IsSuccessful, outcome.ToString());
            var row = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows);
            Assert.Equal(input.Kind == ObservationValueKind.String ? ObservationValue.FromDecimal(12.5m) : ObservationValue.Null, row.Value.GetProperty("value"));
            _ = Observation.Create(new(graphs[1], Target.ShapeId), row.Value);
        }
    }

    [Theory]
    [InlineData("wrong-branch")]
    [InlineData("different-field")]
    [InlineData("non-null-target")]
    [InlineData("invented-default")]
    public void GuardedOptionalDecimal_DoesNotWeakenUnprovenContracts(string scenario)
    {
        var test = new BinaryExpr(BinaryOperator.Ne,
            Expr.Coalesce(Read, scenario == "invented-default" ? Expr.Const("0") : Expr.Null()), Expr.Null());
        var parse = Expr.Call(ExprFunctionNames.ParseDecimal, scenario == "different-field" ? Expr.Field(Binding, "other") : Read);
        var expression = Expr.If(test, scenario == "wrong-branch" ? Expr.Null() : parse,
            scenario == "wrong-branch" ? parse : Expr.Null());
        var (graphs, draft) = Fixture(expression);
        graphs[0] = new(Source.GraphId, [new Shape(Source.ShapeId,
            [new(new("value"), Text, presence: FieldPresence.Optional), new(new("other"), Text, presence: FieldPresence.Optional)])]);
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId,
            [new(new("value"), new ScalarTypeRef(ScalarTypeKind.Decimal),
                nullability: scenario == "non-null-target" ? FieldNullability.NonNullable : FieldNullability.Nullable)])]);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == (scenario switch
        {
            "invented-default" => "relationDraft.conditional.testUnsupported",
            "non-null-target" => "relationDraft.constant.incompatible",
            _ => "relationDraft.conversion.sourceMayBeAbsent"
        }));
    }

    [Theory]
    [InlineData(ExprFunctionNames.ParseInt32, ScalarTypeKind.Int32, "direct")]
    [InlineData(ExprFunctionNames.ParseInt64, ScalarTypeKind.Int64, "direct")]
    [InlineData(ExprFunctionNames.ParseInt32, ScalarTypeKind.Int32, "selected")]
    [InlineData(ExprFunctionNames.ParseInt64, ScalarTypeKind.Int64, "selected")]
    [InlineData(ExprFunctionNames.ParseInt32, ScalarTypeKind.Int32, "guarded")]
    [InlineData(ExprFunctionNames.ParseInt64, ScalarTypeKind.Int64, "guarded")]
    public async Task IntegerConversion_RoundtripsAcceptsAndExecutes(string function, ScalarTypeKind kind, string mode)
    {
        Expr source = mode == "selected"
            ? Expr.Call(ExprFunctionNames.Single, Expr.Call(ExprFunctionNames.Select, Read, Expr.CurrentItem())) : Read;
        Expr expression = Expr.Call(function, source);
        if (mode == "guarded") expression = Expr.If(new BinaryExpr(BinaryOperator.Ne,
            Expr.Coalesce(Read, Expr.Null()), Expr.Null()), expression, Expr.Null());
        var (graphs, draft) = Fixture(expression);
        graphs[0] = new(Source.GraphId, [new Shape(Source.ShapeId,
            [new(new("value"), Text, cardinality: mode == "selected" ? FieldCardinality.Many : FieldCardinality.Single,
                presence: mode == "guarded" ? FieldPresence.Optional : FieldPresence.Required,
                nullability: mode == "guarded" ? FieldNullability.Nullable : FieldNullability.NonNullable)])]);
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId,
            [new(new("value"), new ScalarTypeRef(kind), nullability: mode == "guarded" ? FieldNullability.Nullable : FieldNullability.NonNullable)])]);
        var document = RelationDraftDocument.FromDraft(draft);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        Assert.Equal(document.DraftFingerprint, accepted.Provenance.DraftFingerprint);
        foreach (var input in mode == "guarded" ? new[] { ObservationValue.FromString("002"), ObservationValue.Null, ObservationValue.Undefined }
            : new[] { mode == "selected" ? ObservationValue.FromArray([ObservationValue.FromString("002")]) : ObservationValue.FromString("002") })
        {
            var fields = input.Kind == ObservationValueKind.Undefined ? ImmutableDictionary<string, ObservationValue>.Empty
                : ImmutableDictionary<string, ObservationValue>.Empty.Add("value", input);
            var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
                .Evaluate(new("tests/integer"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
                .Supply([new RelationQuerySuppliedRoot("row", Source, fields)]).Build();
            var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
            Assert.True(outcome.IsSuccessful, string.Join("; ", outcome.Compilation.Diagnostics.Select(d => d.Message)) + outcome.ToString());
            var row = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows);
            Assert.Equal(input.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined ? ObservationValue.Null : ObservationValue.FromInt64(2), row.Value.GetProperty("value"));
            _ = Observation.Create(new(graphs[1], Target.ShapeId), row.Value);
        }
    }

    [Theory]
    [InlineData("optional", "relationDraft.conversion.sourceMayBeAbsent")]
    [InlineData("arity", "relationDraft.conversion.argumentsInvalid")]
    [InlineData("numeric", "relationDraft.conversion.sourceUnsupported")]
    [InlineData("return", "relationDraft.conversion.returnTypeMismatch")]
    public void IntegerConversion_RejectsUnprovenContracts(string scenario, string code)
    {
        Expr expression = scenario == "arity" ? Expr.Call(ExprFunctionNames.ParseInt32)
            : scenario == "return" ? new CallExpr(ExprFunctionNames.ParseInt32, [Read], new ScalarTypeRef(ScalarTypeKind.Int64))
            : Expr.Call(ExprFunctionNames.ParseInt32, Read);
        var (graphs, draft) = Fixture(expression);
        graphs[0] = new(Source.GraphId, [new Shape(Source.ShapeId,
            [new(new("value"), scenario == "numeric" ? new ScalarTypeRef(ScalarTypeKind.Int64) : Text,
                presence: scenario == "optional" ? FieldPresence.Optional : FieldPresence.Required)])]);
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId, [new(new("value"), new ScalarTypeRef(ScalarTypeKind.Int32))])]);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == code);
    }

    static (ShapeGraph[] Graphs, RelationDraft Draft) Fixture(Expr expression, bool collection = false)
    {
        var cardinality = collection ? FieldCardinality.Many : FieldCardinality.Single;
        var source = new ShapeGraph(Source.GraphId, [new Shape(Source.ShapeId,
            [new FieldDefinition(new("value"), Text, cardinality: cardinality, presence: FieldPresence.Optional, nullability: FieldNullability.Nullable)])]);
        var target = new ShapeGraph(Target.GraphId, [new Shape(Target.ShapeId,
            [new FieldDefinition(new("value"), Text, cardinality: cardinality)])]);
        var initial = DirectFieldRelationDraftConventionMatcher.Match(new(new("draft"), new("relation"), new("ExplicitDefault"),
            new SourceQueryNode(new("source"), Binding, Source), new("project"), new("result"), Target), [source, target]).Draft!;
        var slot = RelationDraftIdentityConvention.CreateAssignmentSlotId(Target, FieldPath.Parse("value"));
        var candidate = RelationDraftIdentityConvention.CreateCandidateId(slot, expression);
        return ([source, target], initial with { Projection = initial.Projection with {
            Assignments = [new(slot, FieldPath.Parse("value"), [new(candidate, expression)], new SelectedRelationDraftAssignmentResolution(candidate))] } });
    }

    static string Diagnostics(RelationDraftAcceptanceResult result) => string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
