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
    [InlineData("wrong-fallback", "relationDraft.constant.incompatible")]
    [InlineData("null-fallback", "relationDraft.constant.incompatible")]
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
