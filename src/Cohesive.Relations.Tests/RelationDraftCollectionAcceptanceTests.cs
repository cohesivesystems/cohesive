using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationDraftCollectionAcceptanceTests
{
    static readonly ScalarTypeRef Text = new(ScalarTypeKind.String);
    static readonly ValueBindingId Binding = new("source");
    static readonly QualifiedShapeId Source = new(new("source/v1"), new("Source"));
    static readonly QualifiedShapeId Target = new(new("target/v1"), new("Target"));
    static Expr SourceItems => Expr.Field(Binding, FieldPath.Parse("Items"));
    static Expr ItemId => Expr.Field(FieldPath.Parse("item.Id"));
    static Expr Object(params Expr[] args) => Expr.Call(ExprFunctionNames.Object, args);
    static Expr Select(Expr source, Expr selector) => Expr.Call(ExprFunctionNames.Select, source, selector);
    static TypeRef ItemType => new ObjectTypeRef([new("Id", Text)]);

    [Fact]
    public async Task SingleConstructedSelection_PreservesRequiredChildrenAndCardinality()
    {
        var expression = Expr.Call(ExprFunctionNames.Single, Select(SourceItems, Object(Expr.Const("Code"), ItemId)));
        var (graphs, draft) = Fixture(expression);
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId, [new(new("Results"), new ObjectTypeRef([new("Code", Text)]))])]);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(RelationDraftDocument.FromDraft(draft)));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        foreach (var count in new[] { 0, 1, 2 })
        {
            var values = ImmutableDictionary<string, ObservationValue>.Empty.Add("Items", ObservationValue.FromArray(
                Enumerable.Range(0, count).Select(_ => ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Id"] = ObservationValue.FromString("id") })).ToArray()));
            var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
                .Evaluate(new("tests/single-object"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
                .Supply([new RelationQuerySuppliedRoot("row", Source, values)]).Build();
            var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
            Assert.Equal(count == 1, outcome.IsSuccessful);
            if (count == 1)
                Assert.Equal("id", Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows).Value.GetProperty("Results").GetProperty("Code").GetString());
        }
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId, [new(new("Results"), new ObjectTypeRef([new("Code", Text), new("Required", Text)]))])]);
        Assert.False(RelationDraftAcceptor.Accept(draft, graphs).IsAccepted);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task Select_RoundtripsAcceptsAndExecutesInOrder(bool named, bool arrayType, bool constructed)
    {
        var selector = constructed ? Object(Expr.Const("Code"), ItemId) : ItemId;
        var outputType = constructed ? new ObjectTypeRef([new("Code", Text)]) : (TypeRef)Text;
        var (graphs, draft) = Fixture(Select(SourceItems, selector), named: named, arrayType: arrayType, targetItem: outputType);
        var document = RelationDraftDocument.FromDraft(draft);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        Assert.Equal(document.DraftFingerprint, accepted.Provenance.DraftFingerprint);
        Assert.Equal(accepted.DefinitionFingerprint, RelationDraftAcceptor.Accept(draft, graphs).DefinitionFingerprint);
        foreach (var ids in new[] { Array.Empty<string>(), new[] { "second", "first", "second" } })
        {
            var values = ImmutableDictionary<string, ObservationValue>.Empty.Add("Items", ObservationValue.FromArray(
                ids.Select(id => ObservationValue.FromObject(ImmutableDictionary<string, ObservationValue>.Empty.Add("Id", ObservationValue.FromString(id)))).ToArray()));
            var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
                .Evaluate(new("tests/collection-draft"), [.. graphs.Select(graph => ShapeGraphDocument.FromGraph(graph))])
                .Supply([new RelationQuerySuppliedRoot("root-1", Source, values)]).Build();
            var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
            Assert.True(outcome.IsSuccessful, outcome.ToString());
            var row = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows);
            Assert.Equal(ids, row.Value.GetProperty("Results").EnumerateArray().Select(value =>
                constructed ? value.GetProperty("Code").GetString() : value.GetString()));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructedSelector_UsesNamedTargetChildContracts(bool optionalChild)
    {
        var itemType = new ObjectTypeRef([new("Id", Text,
            presence: optionalChild ? FieldPresence.Optional : FieldPresence.Required)]);
        var namedTarget = new TypeDefinition.Structural(new("Result"), [new(new("Code"), Text)]);
        var (graphs, draft) = Fixture(Select(SourceItems, Object(Expr.Const("Code"), ItemId)),
            sourceItem: itemType, targetItem: new NamedTypeRef(namedTarget.Id));
        graphs[1] = new ShapeGraph(Target.GraphId, graphs[1].Shapes, [namedTarget]);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.Equal(!optionalChild, accepted.IsAccepted);
        if (optionalChild)
            Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.assignment.presenceUnsafe" && d.Message.Contains("Results.Code"));
    }

    [Fact]
    public void Select_AcceptsExactDeclaredCollectionType()
    {
        var expression = new CallExpr(ExprFunctionNames.Select, [SourceItems, ItemId], new ArrayTypeRef(Text));
        var (graphs, draft) = Fixture(expression);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
    }

    [Fact]
    public void CurrentItem_CopiesExactScalarElements()
    {
        var (graphs, draft) = Fixture(Select(SourceItems, Expr.CurrentItem()), sourceItem: Text);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
    }

    [Theory]
    [InlineData("optional-source", "relationDraft.select.sourceMayBeAbsent")]
    [InlineData("nullable-source", "relationDraft.select.sourceMayBeAbsent")]
    [InlineData("single-source", "relationDraft.select.sourceUnsupported")]
    [InlineData("single-target", "relationDraft.select.targetUnsupported")]
    [InlineData("optional-child", "relationDraft.assignment.presenceUnsafe")]
    [InlineData("nullable-child", "relationDraft.assignment.nullabilityUnsafe")]
    [InlineData("wrong-type", "relationDraft.assignment.typeIncompatible")]
    [InlineData("missing-child", "relationDraft.candidate.pathUnknown")]
    [InlineData("unscoped-item", "relationDraft.candidate.itemScopeMissing")]
    [InlineData("unbound-source", "relationDraft.candidate.expressionUnsupported")]
    [InlineData("element-path", "relationDraft.assignment.structureUnsupported")]
    [InlineData("arity", "relationDraft.select.argumentsInvalid")]
    [InlineData("return-type", "relationDraft.select.returnTypeMismatch")]
    [InlineData("constant-selector", "relationDraft.constant.incompatible")]
    public void Select_RejectsUnsafeOrUnsupportedContracts(string scenario, string diagnostic)
    {
        var expression = scenario switch
        {
            "missing-child" => Select(SourceItems, Expr.Field(FieldPath.Parse("item.Missing"))),
            "unscoped-item" => ItemId,
            "unbound-source" => Select(Expr.Field(FieldPath.Parse("Items")), ItemId),
            "element-path" => Select(Expr.Field(Binding, FieldPath.Parse("Items.[].Id")), ItemId),
            "arity" => Expr.Call(ExprFunctionNames.Select, SourceItems),
            "return-type" => new CallExpr(ExprFunctionNames.Select, [SourceItems, ItemId], Text),
            "constant-selector" => Select(SourceItems, Expr.Const(123)),
            _ => Select(SourceItems, ItemId)
        };
        var itemType = new ObjectTypeRef([new("Id", Text,
            presence: scenario == "optional-child" ? FieldPresence.Optional : FieldPresence.Required,
            nullability: scenario == "nullable-child" ? FieldNullability.Nullable : FieldNullability.NonNullable)]);
        var (graphs, draft) = Fixture(expression, sourceItem: itemType,
            sourcePresence: scenario == "optional-source" ? FieldPresence.Optional : FieldPresence.Required,
            sourceNullability: scenario == "nullable-source" ? FieldNullability.Nullable : FieldNullability.NonNullable,
            sourceCardinality: scenario == "single-source" ? FieldCardinality.Single : FieldCardinality.Many,
            targetCardinality: scenario == "single-target" ? FieldCardinality.Single : FieldCardinality.Many,
            targetItem: scenario == "wrong-type" ? new ScalarTypeRef(ScalarTypeKind.Int32) : Text);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Null(accepted.DefinitionFingerprint);
        Assert.Contains(accepted.Diagnostics, d => d.Code == diagnostic);
        Assert.Equal(RelationDraftFingerprinter.Compute(draft), accepted.Provenance.DraftFingerprint);
    }

    [Fact]
    public async Task NestedSelect_UsesTheNearestItemAndPreservesCorrelation()
    {
        var child = new ObjectTypeRef([new("Id", Text)]);
        var parent = new ObjectTypeRef([new("Id", Text), new("Children", child, cardinality: FieldCardinality.Many)]);
        var targetItem = new ObjectTypeRef([new("Parent", Text), new("Children", Text, cardinality: FieldCardinality.Many)]);
        var expression = Select(SourceItems, Object(Expr.Const("Parent"), ItemId, Expr.Const("Children"),
            Select(Expr.Field(FieldPath.Parse("item.Children")), ItemId)));
        var (graphs, draft) = Fixture(expression, sourceItem: parent, targetItem: targetItem);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        static ObservationValue Leaf(string id) => ObservationValue.FromObject(
            ImmutableDictionary<string, ObservationValue>.Empty.Add("Id", ObservationValue.FromString(id)));
        static ObservationValue Row(string id, params ObservationValue[] children) => ObservationValue.FromObject(
            ImmutableDictionary<string, ObservationValue>.Empty.Add("Id", ObservationValue.FromString(id))
                .Add("Children", ObservationValue.FromArray(children)));
        var values = ImmutableDictionary<string, ObservationValue>.Empty.Add("Items",
            ObservationValue.FromArray([Row("parent-1", Leaf("a"), Leaf("b")), Row("parent-2", Leaf("c"))]));
        var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
            .Evaluate(new("tests/nested-collection-draft"), [.. graphs.Select(graph => ShapeGraphDocument.FromGraph(graph))])
            .Supply([new RelationQuerySuppliedRoot("root-1", Source, values)]).Build();
        var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
        Assert.True(outcome.IsSuccessful, outcome.ToString());
        var row = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows);
        var results = row.Value.GetProperty("Results").EnumerateArray().ToArray();
        Assert.Equal("parent-1", results[0].GetProperty("Parent").GetString());
        Assert.Equal(new[] { "a", "b" }, results[0].GetProperty("Children").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("parent-2", results[1].GetProperty("Parent").GetString());
        Assert.Equal("c", Assert.Single(results[1].GetProperty("Children").EnumerateArray()).GetString());
    }

    [Fact]
    public void OptionalContainingSource_IsNotTreatedAsAnEmptyCollection()
    {
        var (graphs, draft) = Fixture(Select(Expr.Field(Binding, FieldPath.Parse("Envelope.Items")), ItemId));
        graphs[0] = new ShapeGraph(Source.GraphId, [new Shape(Source.ShapeId,
            [new FieldDefinition(new("Envelope"), new ObjectTypeRef([new("Items", ItemType, cardinality: FieldCardinality.Many)]),
                presence: FieldPresence.Optional)])]);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.select.sourceMayBeAbsent");
    }

    [Fact]
    public void WholeNamedItem_DoesNotEquateGraphLocalTypes()
    {
        var type = new TypeDefinition.Structural(new("Item"), [new(new("Id"), Text)]);
        var (graphs, draft) = Fixture(Select(SourceItems, Expr.CurrentItem()), named: true, targetItem: new NamedTypeRef(type.Id));
        graphs[1] = new ShapeGraph(Target.GraphId, graphs[1].Shapes, [type]);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.assignment.typeIncompatible");
    }

    [Fact]
    public void NestedSelect_RejectsOptionalInnerCollection()
    {
        var item = new ObjectTypeRef([new("Children", Text, cardinality: FieldCardinality.Many, presence: FieldPresence.Optional)]);
        var (graphs, draft) = Fixture(Select(SourceItems, Select(Expr.Field(FieldPath.Parse("item.Children")), Expr.CurrentItem())),
            sourceItem: item, targetItem: new ArrayTypeRef(Text));
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.select.sourceMayBeAbsent" && d.Location!.EndsWith("/arguments/1/arguments/0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Count_UsesCollectionCardinalityIncludingEmpty(bool optional)
    {
        var collection = optional ? Expr.Coalesce(SourceItems, Expr.Const(ObservationValue.FromArray([]))) : SourceItems;
        var (graphs, draft) = Fixture(Expr.Call(ExprFunctionNames.Count, collection),
            sourcePresence: optional ? FieldPresence.Optional : FieldPresence.Required,
            targetItem: new ScalarTypeRef(ScalarTypeKind.Int64), targetCardinality: FieldCardinality.Single);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        foreach (var count in new[] { 0, 1, 3 })
        {
            var values = ImmutableDictionary<string, ObservationValue>.Empty.Add("Items", ObservationValue.FromArray(
                Enumerable.Range(0, count).Select(_ => ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Id"] = ObservationValue.FromString("id") })).ToArray()));
            var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
                .Evaluate(new("tests/count"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
                .Supply([new RelationQuerySuppliedRoot("row", Source, values)]).Build();
            var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
            Assert.True(outcome.IsSuccessful);
            Assert.Equal(count, Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows).Value.GetProperty("Results").GetInt64());
        }
    }

    [Theory]
    [InlineData("count", "text", "relationDraft.conversion.sourceUnsupported")]
    [InlineData("count", "optional", "relationDraft.conversion.sourceMayBeAbsent")]
    [InlineData("count", "arity", "relationDraft.conversion.argumentsInvalid")]
    [InlineData("count", "return", "relationDraft.conversion.returnTypeMismatch")]
    [InlineData("concat", "number", "relationDraft.conversion.sourceUnsupported")]
    [InlineData("concat", "opaque", "relationDraft.conversion.sourceUnsupported")]
    [InlineData("concat", "arity", "relationDraft.conversion.argumentsInvalid")]
    [InlineData("concat", "return", "relationDraft.conversion.returnTypeMismatch")]
    public void UnaryCollectionAndTextOperations_RejectUnsafeContracts(string function, string scenario, string code)
    {
        Expr expression = new CallExpr(function, scenario == "arity" ? [SourceItems, SourceItems] : [SourceItems],
            scenario == "return" ? new ScalarTypeRef(ScalarTypeKind.Bool) : new OpaqueRuntimeTypeRef("unknown"));
        var sourceType = scenario == "number" ? new ScalarTypeRef(ScalarTypeKind.Int64)
            : scenario == "opaque" ? (TypeRef)new OpaqueRuntimeTypeRef("PrivateType") : Text;
        var (graphs, draft) = Fixture(expression, sourceItem: sourceType,
            sourceCardinality: function == ExprFunctionNames.Count && scenario != "text" ? FieldCardinality.Many : FieldCardinality.Single,
            sourcePresence: scenario == "optional" ? FieldPresence.Optional : FieldPresence.Required,
            targetItem: function == ExprFunctionNames.Count ? new ScalarTypeRef(ScalarTypeKind.Int64) : Text, targetCardinality: FieldCardinality.Single);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public async Task UnaryConcat_ExplicitlyProjectsNamedStringCodeToText()
    {
        var (graphs, draft) = Fixture(Expr.Call(ExprFunctionNames.Concat, SourceItems),
            sourceItem: new NamedTypeRef(new("Code")), sourceCardinality: FieldCardinality.Single, targetCardinality: FieldCardinality.Single);
        graphs[0] = new(Source.GraphId, graphs[0].Shapes, [new TypeDefinition.Enum(new("Code"), PrimitiveType.String, [new("Appointment", "37")])]);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
            .Evaluate(new("tests/text"), [.. graphs.Select(g => ShapeGraphDocument.FromGraph(g))])
            .Supply([new RelationQuerySuppliedRoot("row", Source, ImmutableDictionary<string, ObservationValue>.Empty.Add("Items", ObservationValue.FromString("37")))]).Build();
        var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
        Assert.True(outcome.IsSuccessful);
        Assert.Equal("37", Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows).Value.GetProperty("Results").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedNullGuard_RequiresExplicitPresentValueBeforeConversion(bool explicitlyRequired)
    {
        var value = explicitlyRequired ? Expr.Call(ExprFunctionNames.RequireValue, ItemId) : ItemId;
        var expression = Select(SourceItems, Object(Expr.Const("Amount"),
            Expr.If(Expr.Eq(Expr.Coalesce(ItemId, Expr.Null()), Expr.Null()), Expr.Null(), Expr.Call(ExprFunctionNames.ParseDecimal, value))));
        var (graphs, draft) = Fixture(expression,
            sourceItem: new ObjectTypeRef([new("Id", Text, presence: FieldPresence.Optional)]),
            targetItem: new ObjectTypeRef([new("Amount", new ScalarTypeRef(ScalarTypeKind.Decimal), nullability: FieldNullability.Nullable)]));
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.Equal(explicitlyRequired, accepted.IsAccepted);
        if (!explicitlyRequired)
            Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.conversion.sourceMayBeAbsent");
    }

    [Theory]
    [InlineData("optional-array", "relationDraft.select.sourceMayBeAbsent")]
    [InlineData("optional-child", "relationDraft.assignment.presenceUnsafe")]
    [InlineData("forged-result", "relationDraft.conversion.returnTypeMismatch")]
    public void SingleConstructedSelection_DoesNotRelaxInputOrChildContracts(string scenario, string code)
    {
        var resultType = new ObjectTypeRef([new("Code", Text)]);
        var expression = new CallExpr(ExprFunctionNames.Single, [Select(SourceItems, Object(Expr.Const("Code"), ItemId))],
            scenario == "forged-result" ? Text : new OpaqueRuntimeTypeRef("unknown"));
        var (graphs, draft) = Fixture(expression,
            sourcePresence: scenario == "optional-array" ? FieldPresence.Optional : FieldPresence.Required,
            sourceItem: new ObjectTypeRef([new("Id", Text, presence: scenario == "optional-child" ? FieldPresence.Optional : FieldPresence.Required)]),
            targetCardinality: FieldCardinality.Single, targetItem: resultType);
        // A nullable/optional outer result never makes its structural children optional.
        graphs[1] = new(Target.GraphId, [new Shape(Target.ShapeId,
            [new(new("Results"), resultType, presence: FieldPresence.Optional, nullability: FieldNullability.Nullable)])]);
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public void ItemPresenceGuard_DoesNotRefineAnotherSelectorScope()
    {
        var child = new ObjectTypeRef([new("Id", Text, presence: FieldPresence.Optional)]);
        var item = new ObjectTypeRef([new("Id", Text, presence: FieldPresence.Optional), new("Children", child, cardinality: FieldCardinality.Many)]);
        var expression = Select(SourceItems, Expr.If(Expr.Eq(Expr.Coalesce(ItemId, Expr.Null()), Expr.Null()),
            Expr.Const(ObservationValue.FromArray([])),
            Select(Expr.Field("item.Children"), Expr.Call(ExprFunctionNames.ParseDecimal, ItemId))));
        var (graphs, draft) = Fixture(expression, sourceItem: item, targetItem: new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.Decimal)));
        var accepted = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, d => d.Code == "relationDraft.conversion.sourceMayBeAbsent");
    }

    static (ShapeGraph[] Graphs, RelationDraft Draft) Fixture(Expr expression, bool named = false, bool arrayType = false,
        TypeRef? sourceItem = null, TypeRef? targetItem = null,
        FieldPresence sourcePresence = FieldPresence.Required, FieldNullability sourceNullability = FieldNullability.NonNullable,
        FieldCardinality sourceCardinality = FieldCardinality.Many, FieldCardinality targetCardinality = FieldCardinality.Many)
    {
        sourceItem ??= ItemType;
        targetItem ??= Text;
        ImmutableArray<TypeDefinition> types = named
            ? [new TypeDefinition.Structural(new("Item"), [new(new("Id"), Text)])] : [];
        if (named) sourceItem = new NamedTypeRef(new("Item"));
        var source = new ShapeGraph(Source.GraphId, [new Shape(Source.ShapeId,
            [new FieldDefinition(new("Items"), arrayType ? new ArrayTypeRef(sourceItem) : sourceItem,
                cardinality: arrayType ? FieldCardinality.Single : sourceCardinality,
                presence: sourcePresence, nullability: sourceNullability)])], types);
        var target = new ShapeGraph(Target.GraphId, [new Shape(Target.ShapeId,
            [new FieldDefinition(new("Results"), arrayType ? new ArrayTypeRef(targetItem) : targetItem,
                cardinality: arrayType ? FieldCardinality.Single : targetCardinality)])]);
        var initial = DirectFieldRelationDraftConventionMatcher.Match(new(new("draft"), new("relation"), new("ProjectItems"),
            new SourceQueryNode(new("source"), Binding, Source), new("project"), new("result"), Target), [source, target]).Draft!;
        var slot = RelationDraftIdentityConvention.CreateAssignmentSlotId(Target, FieldPath.Parse("Results"));
        var candidate = RelationDraftIdentityConvention.CreateCandidateId(slot, expression);
        return ([source, target], initial with { Projection = initial.Projection with {
            Assignments = [new(slot, FieldPath.Parse("Results"), [new(candidate, expression)], new SelectedRelationDraftAssignmentResolution(candidate))] } });
    }

    static string Diagnostics(RelationDraftAcceptanceResult result) => string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
