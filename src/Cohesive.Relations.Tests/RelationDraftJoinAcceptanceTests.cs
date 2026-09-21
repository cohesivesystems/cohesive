using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationDraftJoinAcceptanceTests
{
    static readonly ScalarTypeRef Text = new(ScalarTypeKind.String);
    static readonly ValueBindingId Binding = new("source");
    static readonly QualifiedShapeId Source = new(new("source"), new("root"));
    static readonly QualifiedShapeId Target = new(new("target"), new("root"));
    static Expr Read => Expr.Field(Binding, "items");
    static Expr Key => Expr.Field("item.qualifier");
    static Expr Join(Expr? source = null, Expr? key = null, Expr? left = null) =>
        Expr.Join(left ?? Expr.Const("PO"), key ?? Key, source ?? Read);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QualifiedCollection_RoundtripsAndExecutesOrderedMatches(bool project, bool array)
    {
        var expression = project ? Expr.Call(ExprFunctionNames.Select, Join(), Expr.Field("item.value")) : Join();
        var (graphs, draft) = Fixture(expression, project, array: array);
        var document = RelationDraftDocument.FromDraft(draft);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        Assert.Equal(document.DraftFingerprint, accepted.Provenance.DraftFingerprint);
        Assert.Equal(accepted.DefinitionFingerprint, RelationDraftAcceptor.Accept(draft, graphs).DefinitionFingerprint);
        foreach (var values in new[] { new[] { Item("PO", "b"), Item("BN", "ignore"), Item("PO", "a"), Item("PO", "b"), Item(null, "absent") }, new[] { Item("BN", "skip") }, System.Array.Empty<ObservationValue>() })
        {
            var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
                .Evaluate(new("tests/join"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
                .Supply([new RelationQuerySuppliedRoot("row", Source,
                    ImmutableDictionary<string, ObservationValue>.Empty.Add("items", ObservationValue.FromArray(values)))]).Build();
            var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
            Assert.True(outcome.IsSuccessful, outcome.ToString());
            var result = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows).Value.GetProperty("items");
            Assert.Equal(values.Length == 5 ? new[] { "b", "a", "b" } : [],
                result.EnumerateArray().Select(v => (project ? v : v.GetProperty("value")).GetString()));
        }
    }

    [Theory]
    [InlineData("missing-source", "relationDraft.join.sourceMayBeAbsent")]
    [InlineData("null-key", "relationDraft.join.keyUnsupported")]
    [InlineData("computed-key", "relationDraft.join.keyUnsupported")]
    [InlineData("wrong-key-type", "relationDraft.constant.incompatible")]
    [InlineData("unknown-path", "relationDraft.candidate.pathUnknown")]
    [InlineData("arity", "relationDraft.join.argumentsInvalid")]
    [InlineData("return-type", "relationDraft.join.returnTypeMismatch")]
    public void UnsafeOrUnsupportedJoinsRemainRejected(string scenario, string code)
    {
        var expression = scenario switch
        {
            "null-key" => Join(left: Expr.Null()),
            "computed-key" => Join(left: Expr.Field(Binding, "other")),
            "wrong-key-type" => Join(left: Expr.Const(7)),
            "unknown-path" => Join(key: Expr.Field("item.unknown")),
            "arity" => Expr.Call(ExprFunctionNames.Join, Expr.Const("PO")),
            "return-type" => new CallExpr(ExprFunctionNames.Join, [Expr.Const("PO"), Key, Read], new ArrayTypeRef(Text)),
            _ => Join()
        };
        var (graphs, draft) = Fixture(expression, optional: scenario == "missing-source");
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public void ExplicitDefaultCanSupplyCollectionButFilteringDoesNotRefineItemPresence()
    {
        var source = Expr.Coalesce(Read, Expr.Const(ObservationValue.FromArray([])));
        var (graphs, draft) = Fixture(Join(source), optional: true);
        Assert.True(RelationDraftAcceptor.Accept(draft, graphs).IsAccepted);
        var projected = Expr.Call(ExprFunctionNames.Select, Join(source), Key);
        (graphs, draft) = Fixture(projected, project: true, optional: true);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(result.IsAccepted);
        Assert.Contains(result.Diagnostics, d => d.Code == "relationDraft.assignment.presenceUnsafe");
    }

    static ObservationValue Item(string? qualifier, string value) => ObservationValue.FromObject(
        qualifier is null ? new Dictionary<string, ObservationValue> { ["value"] = ObservationValue.FromString(value) }
        : new Dictionary<string, ObservationValue> { ["qualifier"] = ObservationValue.FromString(qualifier), ["value"] = ObservationValue.FromString(value) });

    static (ShapeGraph[] Graphs, RelationDraft Draft) Fixture(Expr expression, bool project = false, bool optional = false, bool array = false)
    {
        var item = new ObjectTypeRef([new("qualifier", Text, presence: FieldPresence.Optional, nullability: FieldNullability.Nullable), new("value", Text)]);
        var source = new ShapeGraph(Source.GraphId, [new Shape(Source.ShapeId,
            [new FieldDefinition(new("items"), array ? new ArrayTypeRef(item) : item, cardinality: array ? FieldCardinality.Single : FieldCardinality.Many,
                presence: optional ? FieldPresence.Optional : FieldPresence.Required)])]);
        var target = new ShapeGraph(Target.GraphId, [new Shape(Target.ShapeId,
            [new FieldDefinition(new("items"), array ? new ArrayTypeRef(project ? Text : item) : project ? Text : item, cardinality: array ? FieldCardinality.Single : FieldCardinality.Many)])]);
        var initial = DirectFieldRelationDraftConventionMatcher.Match(new(new("draft"), new("relation"), new("QualifiedReferences"),
            new SourceQueryNode(new("source"), Binding, Source), new("project"), new("result"), Target), [source, target]).Draft!;
        var path = FieldPath.Parse("items");
        var slot = RelationDraftIdentityConvention.CreateAssignmentSlotId(Target, path);
        var candidate = RelationDraftIdentityConvention.CreateCandidateId(slot, expression);
        return ([source, target], initial with { Projection = initial.Projection with {
            Assignments = [new(slot, path, [new(candidate, expression)], new SelectedRelationDraftAssignmentResolution(candidate))] } });
    }

    static string Diagnostics(RelationDraftAcceptanceResult result) => string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
