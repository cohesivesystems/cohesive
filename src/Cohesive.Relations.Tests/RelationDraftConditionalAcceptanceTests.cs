using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationDraftConditionalAcceptanceTests
{
    static readonly ScalarTypeRef Text = new(ScalarTypeKind.String);
    static readonly ValueBindingId Binding = new("source");
    static readonly QualifiedShapeId Source = new(new("source"), new("root"));
    static readonly QualifiedShapeId Target = new(new("target"), new("root"));
    static Expr Read => Expr.Field(Binding, "value");
    static Expr Translate(Expr read) => Expr.If(Expr.Eq(read, Expr.Const("00")), Expr.Const("Original"),
        Expr.If(Expr.Eq(read, Expr.Const("01")), Expr.Const("Cancel"), Expr.Const("Unknown")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCodes_RoundtripAcceptAndExecuteWithScopedDefaults(bool collection)
    {
        var read = collection ? Expr.Field("item.code") : Read;
        var expression = Translate(Expr.Coalesce(read, Expr.Const("missing")));
        if (collection) expression = Expr.Call(ExprFunctionNames.Select, Read, expression);
        var (graphs, draft) = Fixture(expression, collection);
        var document = RelationDraftDocument.FromDraft(draft);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        Assert.Equal(document.DraftFingerprint, accepted.Provenance.DraftFingerprint);
        Assert.Equal(accepted.DefinitionFingerprint, RelationDraftAcceptor.Accept(draft, graphs).DefinitionFingerprint);
        foreach (var (value, expected) in new[] {
            (ObservationValue.FromString("00"), "Original"), (ObservationValue.FromString("01"), "Cancel"),
            (ObservationValue.FromString("other"), "Unknown"), (ObservationValue.FromString(""), "Unknown"),
            (ObservationValue.Null, "Unknown"), (ObservationValue.Undefined, "Unknown") })
        {
            var fields = value.Kind == ObservationValueKind.Undefined ? ImmutableDictionary<string, ObservationValue>.Empty
                : ImmutableDictionary<string, ObservationValue>.Empty.Add(collection ? "code" : "value", value);
            if (collection) fields = ImmutableDictionary<string, ObservationValue>.Empty.Add("value",
                ObservationValue.FromArray([ObservationValue.FromObject(fields), ObservationValue.FromObject(fields)]));
            var result = await Execute(accepted, graphs, fields);
            Assert.Equal(collection ? new[] { expected, expected } : [expected],
                collection ? result.EnumerateArray().Select(v => v.GetString()) : [result.GetString()]);
        }
    }

    [Theory]
    [InlineData("missing", "relationDraft.conditional.sourceMayBeAbsent")]
    [InlineData("key-type", "relationDraft.constant.incompatible")]
    [InlineData("null-key", "relationDraft.conditional.testUnsupported")]
    [InlineData("computed-key", "relationDraft.conditional.testUnsupported")]
    [InlineData("predicate", "relationDraft.conditional.testUnsupported")]
    [InlineData("true-type", "relationDraft.constant.incompatible")]
    [InlineData("false-type", "relationDraft.constant.incompatible")]
    [InlineData("no-refinement", "relationDraft.assignment.presenceUnsafe")]
    [InlineData("unknown", "relationDraft.candidate.pathUnknown")]
    [InlineData("return-type", "relationDraft.conditional.returnTypeMismatch")]
    public void Conditional_RejectsUnprovenContractsInEitherBranch(string scenario, string code)
    {
        var read = Expr.Coalesce(Read, Expr.Const("missing"));
        Expr expression = scenario switch
        {
            "missing" => Translate(Read),
            "key-type" => Expr.If(Expr.Eq(read, Expr.Const(7)), Expr.Const("yes"), Expr.Const("no")),
            "null-key" => Expr.If(Expr.Eq(read, Expr.Null()), Expr.Const("yes"), Expr.Const("no")),
            "computed-key" => Expr.If(Expr.Eq(read, read), Expr.Const("yes"), Expr.Const("no")),
            "predicate" => Expr.If(Expr.Const(true), Expr.Const("yes"), Expr.Const("no")),
            "true-type" => Expr.If(Expr.Eq(read, Expr.Const("00")), Expr.Const(7), Expr.Const("no")),
            "false-type" => Expr.If(Expr.Eq(read, Expr.Const("00")), Expr.Const("yes"), Expr.Const(7)),
            "no-refinement" => Expr.If(Expr.Eq(read, Expr.Const("00")), Read, Expr.Const("no")),
            "unknown" => Translate(Expr.Field(Binding, "unknown")),
            _ => new ConditionalExpr(Expr.Eq(read, Expr.Const("00")), Expr.Const("yes"), Expr.Const("no"), new ScalarTypeRef(ScalarTypeKind.Int32))
        };
        var (graphs, draft) = Fixture(expression);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(result.IsAccepted);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public async Task PresentNullableCode_SelectsElseForNullWithoutInventingAValue()
    {
        var (graphs, draft) = Fixture(Translate(Read), optional: false);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        var result = await Execute(accepted, graphs, ImmutableDictionary<string, ObservationValue>.Empty.Add("value", ObservationValue.Null));
        Assert.Equal("Unknown", result.GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryTranslatedLiteralMustBelongToTheTargetEnum(bool includeUnknown)
    {
        var target = new EnumTypeRef("Purpose", includeUnknown ? ["Original", "Cancel", "Unknown"] : ["Original", "Cancel"]);
        var (graphs, draft) = Fixture(Translate(Expr.Coalesce(Read, Expr.Const("missing"))), targetType: target);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.Equal(includeUnknown, accepted.IsAccepted);
        if (!includeUnknown)
            Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.constant.incompatible" && d.Location?.EndsWith("/ifFalse/ifFalse", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalObjectBranchesKeepRequiredChildren(bool incompleteElse)
    {
        var target = new ObjectTypeRef([new("code", Text)]);
        var expression = Expr.If(Expr.Eq(Expr.Coalesce(Read, Expr.Const("missing")), Expr.Const("00")),
            Expr.Call(ExprFunctionNames.Object, Expr.Const("code"), Expr.Const("Original")),
            incompleteElse ? Expr.Call(ExprFunctionNames.Object) : Expr.Call(ExprFunctionNames.Object, Expr.Const("code"), Expr.Const("Other")));
        var (graphs, draft) = Fixture(expression, targetType: target);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.Equal(!incompleteElse, accepted.IsAccepted);
        if (incompleteElse)
            Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.object.fieldRequired");
        else
        {
            var result = await Execute(accepted, graphs, ImmutableDictionary<string, ObservationValue>.Empty);
            Assert.Equal("Other", result.GetProperty("code").GetString());
        }
    }

    static async Task<ObservationValue> Execute(RelationDraftAcceptanceResult accepted, ShapeGraph[] graphs,
        ImmutableDictionary<string, ObservationValue> fields)
    {
        var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
            .Evaluate(new("tests/code-translation"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
            .Supply([new RelationQuerySuppliedRoot("row", Source, fields)]).Build();
        var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
        Assert.True(outcome.IsSuccessful, outcome.ToString());
        return Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows).Value.GetProperty("value");
    }

    static (ShapeGraph[] Graphs, RelationDraft Draft) Fixture(Expr expression, bool collection = false, bool optional = true, TypeRef? targetType = null)
    {
        var presence = optional ? FieldPresence.Optional : FieldPresence.Required;
        var sourceType = collection ? (TypeRef)new ArrayTypeRef(new ObjectTypeRef([
            new("code", Text, presence: presence, nullability: FieldNullability.Nullable)])) : Text;
        var source = new ShapeGraph(Source.GraphId, [new Shape(Source.ShapeId,
            [new FieldDefinition(new("value"), sourceType, presence: collection ? FieldPresence.Required : presence,
                nullability: collection ? FieldNullability.NonNullable : FieldNullability.Nullable)])]);
        var target = new ShapeGraph(Target.GraphId, [new Shape(Target.ShapeId,
            [new FieldDefinition(new("value"), collection ? new ArrayTypeRef(Text) : targetType ?? Text)])]);
        var initial = DirectFieldRelationDraftConventionMatcher.Match(new(new("draft"), new("relation"), new("TranslateCodes"),
            new SourceQueryNode(new("source"), Binding, Source), new("project"), new("result"), Target), [source, target]).Draft!;
        var slot = RelationDraftIdentityConvention.CreateAssignmentSlotId(Target, FieldPath.Parse("value"));
        var candidate = RelationDraftIdentityConvention.CreateCandidateId(slot, expression);
        return ([source, target], initial with { Projection = initial.Projection with {
            Assignments = [new(slot, FieldPath.Parse("value"), [new(candidate, expression)], new SelectedRelationDraftAssignmentResolution(candidate))] } });
    }

    static string Diagnostics(RelationDraftAcceptanceResult result) => string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
