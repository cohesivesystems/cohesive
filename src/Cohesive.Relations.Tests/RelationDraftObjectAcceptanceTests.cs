using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationDraftObjectAcceptanceTests
{
    static readonly ScalarTypeRef Text = new(ScalarTypeKind.String);
    static readonly ValueBindingId SourceBinding = new("source");
    static readonly QualifiedShapeId SourceShape = new(new("source/v1"), new("Source"));
    static readonly QualifiedShapeId TargetShape = new(new("target/v1"), new("Target"));
    static Expr SourceId => Expr.Field(SourceBinding, FieldPath.Parse("Header.Id"));
    static Expr Object(params Expr[] arguments) => Expr.Call(ExprFunctionNames.Object, [.. arguments]);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task ConstructedObject_AcceptsRoundtripsAndExecutes(bool named, bool nested, bool declaredType)
    {
        var expression = nested ? Object(Expr.Const("Address"), Object(Expr.Const("Id"), SourceId)) : Object(Expr.Const("Id"), SourceId);
        var (graphs, draft) = Fixture(expression, named: named, nested: nested, declareType: declaredType);
        var document = RelationDraftDocument.FromDraft(draft);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, graphs);
        Assert.True(accepted.IsAccepted, Diagnostics(accepted));
        Assert.Equal(document.DraftFingerprint, accepted.Provenance.DraftFingerprint);
        Assert.Equal(accepted.DefinitionFingerprint, RelationDraftAcceptor.Accept(draft, graphs).DefinitionFingerprint);
        var values = ImmutableDictionary<string, ObservationValue>.Empty.Add("Header",
            ObservationValue.FromObject(ImmutableDictionary<string, ObservationValue>.Empty.Add("Id", ObservationValue.FromString("shipment-42"))));
        var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
            .Evaluate(new("tests/object-draft"), [.. graphs.Select(graph => ShapeGraphDocument.FromGraph(graph))])
            .Supply([new RelationQuerySuppliedRoot("root-1", SourceShape, values)])
            .Build();
        var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
        Assert.True(outcome.IsSuccessful);
        var row = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows);
        var party = row.Value.GetProperty("Party");
        if (nested) party = party.GetProperty("Address");
        Assert.Equal("shipment-42", party.GetProperty("Id").GetString());
        Assert.False(party.TryGetProperty("Note", out _)); // Optional child omission is explicit in the authored object.
    }

    [Theory]
    [InlineData("missing", "relationDraft.object.fieldRequired")]
    [InlineData("unknown", "relationDraft.object.fieldUnknown")]
    [InlineData("duplicate", "relationDraft.object.keyDuplicate")]
    [InlineData("dynamic", "relationDraft.object.keyUnsupported")]
    [InlineData("empty", "relationDraft.object.keyUnsupported")]
    [InlineData("odd", "relationDraft.object.argumentsInvalid")]
    [InlineData("unknown-source", "relationDraft.candidate.pathUnknown")]
    [InlineData("unbound-source", "relationDraft.candidate.expressionUnsupported")]
    [InlineData("return-type", "relationDraft.object.returnTypeMismatch")]
    [InlineData("collection-path", "relationDraft.assignment.structureUnsupported")]
    public void ConstructedObject_RejectsUnprovenOrMalformedChildren(string scenario, string diagnostic)
    {
        var expression = scenario switch
        {
            "missing" => Object(),
            "unknown" => Object(Expr.Const("Id"), SourceId, Expr.Const("Secret"), SourceId),
            "duplicate" => Object(Expr.Const("Id"), SourceId, Expr.Const("Id"), SourceId),
            "dynamic" => Object(SourceId, SourceId),
            "empty" => Object(Expr.Const(" "), SourceId),
            "odd" => Object(Expr.Const("Id")),
            "unknown-source" => Object(Expr.Const("Id"), Expr.Field(SourceBinding, FieldPath.Parse("Header.Missing"))),
            "unbound-source" => Object(Expr.Const("Id"), Expr.Field(FieldPath.Parse("Header.Id"))),
            "return-type" => new CallExpr(ExprFunctionNames.Object, [Expr.Const("Id"), SourceId], Text),
            "collection-path" => Object(Expr.Const("Id"), Expr.Field(SourceBinding, FieldPath.Parse("Header.[].Id"))),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var (graphs, draft) = Fixture(expression);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(result.IsAccepted);
        Assert.Contains(result.Diagnostics, d => d.Code == diagnostic);
        Assert.Null(result.DefinitionFingerprint);
        Assert.Equal(RelationDraftFingerprinter.Compute(draft), result.Provenance.DraftFingerprint);
    }

    [Theory]
    [InlineData(FieldPresence.Optional, FieldNullability.NonNullable, "relationDraft.assignment.presenceUnsafe")]
    [InlineData(FieldPresence.Required, FieldNullability.Nullable, "relationDraft.assignment.nullabilityUnsafe")]
    public void OptionalContainingTarget_DoesNotWeakenRequiredChild(FieldPresence presence, FieldNullability nullability, string diagnostic)
    {
        var (graphs, draft) = Fixture(Object(Expr.Const("Id"), SourceId), sourcePresence: presence, sourceNullability: nullability);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(result.IsAccepted);
        var error = Assert.Single(result.Diagnostics, d => d.Code == diagnostic);
        Assert.Contains("Party.Id", error.Message);
        Assert.EndsWith("/value/arguments/1", error.Location);
    }

    [Theory]
    [InlineData(FieldCardinality.Single, FieldCardinality.Many, false)]
    [InlineData(FieldCardinality.Many, FieldCardinality.Single, false)]
    [InlineData(FieldCardinality.Many, FieldCardinality.Many, true)]
    public void ConstructedChild_PreservesCardinality(FieldCardinality source, FieldCardinality target, bool expected)
    {
        var (graphs, draft) = Fixture(Object(Expr.Const("Id"), SourceId), sourceCardinality: source, childCardinality: target);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.Equal(expected, result.IsAccepted);
        if (!expected) Assert.Contains(result.Diagnostics, d => d.Code == "relationDraft.assignment.cardinalityUnsafe");
    }

    [Fact]
    public void ObjectCannotPopulateCollectionTarget()
    {
        var (graphs, draft) = Fixture(Object(Expr.Const("Id"), SourceId), targetCardinality: FieldCardinality.Many);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(result.IsAccepted);
        Assert.Contains(result.Diagnostics, d => d.Code == "relationDraft.object.targetUnsupported");
    }

    [Fact]
    public void ConstructedChild_CanRetainOptionalNullableSourceContract()
    {
        var (graphs, draft) = Fixture(Object(Expr.Const("Id"), SourceId),
            sourcePresence: FieldPresence.Optional, sourceNullability: FieldNullability.Nullable,
            childPresence: FieldPresence.Optional, childNullability: FieldNullability.Nullable);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.True(result.IsAccepted, Diagnostics(result));
    }

    [Fact]
    public void ConstructedChild_DoesNotEquateGraphLocalNamedTypes()
    {
        var code = new TypeDefinition.Structural(new("Code"), [new StructuralField(new("Value"), Text)]);
        var (graphs, draft) = Fixture(Object(Expr.Const("Id"), SourceId), childType: new NamedTypeRef(code.Id), extraType: code);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(result.IsAccepted);
        Assert.Contains(result.Diagnostics, d => d.Code == "relationDraft.assignment.typeIncompatible"
            && d.Message.Contains("graph-local"));
    }

    [Fact]
    public void MalformedInlineTarget_ReturnsDiagnosticInsteadOfThrowing()
    {
        var (graphs, draft) = Fixture(Object(Expr.Const("Id"), SourceId));
        var ambiguous = new ObjectTypeRef([new("Id", Text), new("Id", Text)]);
        graphs[1] = new ShapeGraph(TargetShape.GraphId,
            [new Shape(TargetShape.ShapeId, [new FieldDefinition(new("Party"), ambiguous)])]);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(result.IsAccepted);
        Assert.Contains(result.Diagnostics, d => d.Code == "relationDraft.object.targetInvalid");
    }

    [Fact]
    public void ConstructedObject_DoesNotOverrideComputedChildren()
    {
        var (graphs, draft) = Fixture(Object(Expr.Const("Id"), SourceId), named: true);
        var party = Assert.IsType<TypeDefinition.Structural>(Assert.Single(graphs[1].NamedTypes));
        graphs[1] = new ShapeGraph(TargetShape.GraphId, graphs[1].Shapes,
            [new TypeDefinition.Structural(party.Id, [.. party.Fields.Select(field => field.Name.Value == "Id"
                ? field with { Role = FieldRole.Computed } : field)])]);
        var result = RelationDraftAcceptor.Accept(draft, graphs);
        Assert.False(result.IsAccepted);
        Assert.Contains(result.Diagnostics, d => d.Code == "relationDraft.object.fieldComputed");
    }

    static (ShapeGraph[] Graphs, RelationDraft Draft) Fixture(Expr expression, bool named = false, bool nested = false,
        FieldPresence sourcePresence = FieldPresence.Required, FieldNullability sourceNullability = FieldNullability.NonNullable,
        FieldCardinality sourceCardinality = FieldCardinality.Single, FieldCardinality childCardinality = FieldCardinality.Single,
        FieldCardinality targetCardinality = FieldCardinality.Single,
        FieldPresence childPresence = FieldPresence.Required, FieldNullability childNullability = FieldNullability.NonNullable,
        TypeRef? childType = null, TypeDefinition? extraType = null, bool declareType = false)
    {
        childType ??= Text;
        var source = new ShapeGraph(SourceShape.GraphId,
            [new Shape(SourceShape.ShapeId, [new FieldDefinition(new("Header"), new NamedTypeRef(new("Header")),
                presence: sourcePresence, nullability: sourceNullability)])],
            [new TypeDefinition.Structural(new("Header"), [new StructuralField(new("Id"), childType, cardinality: sourceCardinality)]),
             .. extraType is null ? Array.Empty<TypeDefinition>() : [extraType]]);
        ObjectFieldTypeDef[] leaves = [new("Id", childType, cardinality: childCardinality, presence: childPresence, nullability: childNullability), new("Note", Text, presence: FieldPresence.Optional)];
        ImmutableArray<ObjectFieldTypeDef> children = nested ? [new("Address", new ObjectTypeRef([.. leaves]))] : [.. leaves];
        var targetType = named ? (TypeRef)new NamedTypeRef(new("Party")) : new ObjectTypeRef(children);
        ImmutableArray<TypeDefinition> targetTypes = named
            ? [new TypeDefinition.Structural(new("Party"), [.. children.Select(child => new StructuralField(new(child.Name), child.Type,
                child.Cardinality, child.Presence, child.Nullability))])]
            : [];
        if (extraType is not null) targetTypes = targetTypes.Add(extraType);
        var target = new ShapeGraph(TargetShape.GraphId,
            [new Shape(TargetShape.ShapeId, [new FieldDefinition(new("Party"), targetType,
                presence: FieldPresence.Optional, cardinality: targetCardinality)])], targetTypes);
        var initial = DirectFieldRelationDraftConventionMatcher.Match(new(new("draft"), new("relation"), new("ConstructParty"),
            new SourceQueryNode(new("source"), SourceBinding, SourceShape), new("project"), new("result"), TargetShape), [source, target]).Draft!;
        if (declareType) expression = ((CallExpr)expression) with { ReturnType = targetType };
        var slotId = RelationDraftIdentityConvention.CreateAssignmentSlotId(TargetShape, FieldPath.Parse("Party"));
        var candidateId = RelationDraftIdentityConvention.CreateCandidateId(slotId, expression);
        var draft = initial with
        {
            Projection = initial.Projection with
            {
                Assignments = [new(slotId, FieldPath.Parse("Party"), [new(candidateId, expression)], new SelectedRelationDraftAssignmentResolution(candidateId))]
            }
        };
        return ([source, target], draft);
    }

    static string Diagnostics(RelationDraftAcceptanceResult result) => string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
