using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Tests;

public sealed class RelationDraftAcceptanceTests
{
    static readonly GraphId GraphId = new("relation-draft-acceptance/v1");
    static readonly QualifiedShapeId LoadShapeId = Qualified("Load");
    static readonly QualifiedShapeId LoadDtoShapeId = Qualified("LoadDto");
    static readonly QualifiedShapeId CustomerShapeId = Qualified("Customer");
    static readonly QualifiedShapeId LoadSearchDtoShapeId = Qualified("LoadSearchDto");
    static readonly ValueBindingId LoadBinding = new("load");
    static readonly ValueBindingId CustomerBinding = new("customer");
    static readonly ValueBindingId ResultBinding = new("result");

    [Fact]
    public void DirectMapping_AcceptsCanonicalRelation()
    {
        var graph = new ShapeGraph(
            GraphId,
            [
                ShapeOf("Load", RequiredString("Id"), RequiredString("CustomerId")),
                ShapeOf("LoadDto", RequiredString("Id"), RequiredString("CustomerId"))
            ]);
        var matched = DirectFieldRelationDraftConventionMatcher.Match(
            DirectRequest(LoadShapeId, LoadDtoShapeId),
            [graph]);

        var draft = Assert.IsType<RelationDraft>(matched.Draft);
        var accepted = RelationDraftAcceptor.Accept(draft, [graph]);

        Assert.True(matched.IsComplete, FormatDiagnostics(matched.Diagnostics));
        Assert.True(accepted.IsAccepted, FormatDiagnostics(accepted.Diagnostics));
        Assert.NotNull(accepted.Definition);
        Assert.Equal(draft.RelationId, accepted.Definition.Id);
        Assert.Equal(
            RelationQueryDefinitionFingerprinter.Compute(accepted.Definition),
            accepted.DefinitionFingerprint);
        Assert.Equal(
            RelationQueryDefinitionFingerprinter.Compute(CreateHandAuthoredRelation(draft)),
            accepted.DefinitionFingerprint);
        Assert.Equal(
            RelationDraftFingerprinter.Compute(draft),
            accepted.Provenance.DraftFingerprint);
        var projection = Assert.Single(accepted.Definition.Body.Nodes.OfType<ProjectQueryNode>());
        Assert.Equal(2, projection.Assignments.Length);
    }

    [Fact]
    public void RequiredTarget_UnresolvedOrOmitted_PreventsAcceptance()
    {
        var graph = new ShapeGraph(
            GraphId,
            [
                ShapeOf("Load", RequiredString("Id"), RequiredString("CustomerId")),
                ShapeOf("LoadDto", RequiredString("Id"), RequiredString("CustomerId"))
            ]);
        var original = Assert.IsType<RelationDraft>(DirectFieldRelationDraftConventionMatcher.Match(
            DirectRequest(LoadShapeId, LoadDtoShapeId),
            [graph]).Draft);
        var targetSlot = original.Projection.Assignments.Single(static slot =>
            slot.Target == FieldPath.FromField("CustomerId"));
        var unresolved = WithResolution(
            original,
            targetSlot.Id,
            new UnresolvedRelationDraftAssignmentResolution(
                [RelationDraftUnresolvedReason.NoCandidate]));
        var omitted = WithResolution(
            original,
            targetSlot.Id,
            OmittedRelationDraftAssignmentResolution.Instance);

        var unresolvedResult = RelationDraftAcceptor.Accept(unresolved, [graph]);
        var omittedResult = RelationDraftAcceptor.Accept(omitted, [graph]);

        Assert.False(unresolvedResult.IsAccepted);
        AssertDiagnostic(unresolvedResult.Validation, "relationDraft.resolution.unresolved");
        Assert.False(omittedResult.IsAccepted);
        AssertDiagnostic(omittedResult.Validation, "relationDraft.resolution.requiredOmitted");
    }

    [Fact]
    public void OptionalTarget_ExplicitOmission_AcceptsAlongsideSelectedField()
    {
        var graph = new ShapeGraph(
            GraphId,
            [
                ShapeOf("Load", RequiredString("Id")),
                ShapeOf(
                    "LoadDto",
                    RequiredString("Id"),
                    new FieldDefinition(
                        new("Note"),
                        new ScalarTypeRef(ScalarTypeKind.String),
                        presence: FieldPresence.Optional,
                        nullability: FieldNullability.Nullable))
            ]);
        var matched = DirectFieldRelationDraftConventionMatcher.Match(
            DirectRequest(LoadShapeId, LoadDtoShapeId),
            [graph]);
        var original = Assert.IsType<RelationDraft>(matched.Draft);
        var optionalSlot = original.Projection.Assignments.Single(static slot =>
            slot.Target == FieldPath.FromField("Note"));
        var draft = WithResolution(
            original,
            optionalSlot.Id,
            OmittedRelationDraftAssignmentResolution.Instance);

        var accepted = RelationDraftAcceptor.Accept(draft, [graph]);

        Assert.True(accepted.IsAccepted, FormatDiagnostics(accepted.Diagnostics));
        var projection = Assert.Single(accepted.Definition!.Body.Nodes.OfType<ProjectQueryNode>());
        var assignment = Assert.Single(projection.Assignments);
        Assert.Equal(FieldPath.FromField("Id"), assignment.Target);
    }

    [Fact]
    public void UnsafeTypeCardinalityPresenceAndNullability_AreDiagnosedAfterExplicitSelection()
    {
        var graph = new ShapeGraph(
            GraphId,
            [
                ShapeOf(
                    "Load",
                    new FieldDefinition(new("TypeMismatch"), new ScalarTypeRef(ScalarTypeKind.Int32)),
                    new FieldDefinition(
                        new("CardinalityMismatch"),
                        new ScalarTypeRef(ScalarTypeKind.String),
                        cardinality: FieldCardinality.Many),
                    new FieldDefinition(
                        new("PresenceMismatch"),
                        new ScalarTypeRef(ScalarTypeKind.String),
                        presence: FieldPresence.Optional),
                    new FieldDefinition(
                        new("NullabilityMismatch"),
                        new ScalarTypeRef(ScalarTypeKind.String),
                        nullability: FieldNullability.Nullable)),
                ShapeOf(
                    "LoadDto",
                    RequiredString("TypeMismatch"),
                    RequiredString("CardinalityMismatch"),
                    RequiredString("PresenceMismatch"),
                    RequiredString("NullabilityMismatch"))
            ]);
        var original = Assert.IsType<RelationDraft>(DirectFieldRelationDraftConventionMatcher.Match(
            DirectRequest(LoadShapeId, LoadDtoShapeId),
            [graph]).Draft);
        var explicitlySelected = original with
        {
            Projection = original.Projection with
            {
                Assignments =
                [
                    .. original.Projection.Assignments.Select(static slot => slot with
                    {
                        Resolution = new SelectedRelationDraftAssignmentResolution(
                            Assert.Single(slot.Candidates).Id)
                    })
                ]
            }
        };

        var accepted = RelationDraftAcceptor.Accept(explicitlySelected, [graph]);

        Assert.False(accepted.IsAccepted);
        AssertDiagnostic(accepted.Validation, "relationDraft.assignment.typeIncompatible");
        AssertDiagnostic(accepted.Validation, "relationDraft.assignment.conversionRequired");
        AssertDiagnostic(accepted.Validation, "relationDraft.assignment.cardinalityUnsafe");
        AssertDiagnostic(accepted.Validation, "relationDraft.assignment.presenceUnsafe");
        AssertDiagnostic(accepted.Validation, "relationDraft.assignment.nullabilityUnsafe");
    }

    [Fact]
    public void IndependentAssignmentErrors_AreReportedAlongsideAnUnresolvedHole()
    {
        var graph = new ShapeGraph(
            GraphId,
            [
                ShapeOf(
                    "Load",
                    RequiredString("Id"),
                    new FieldDefinition(
                        new FieldName("Code"),
                        new ScalarTypeRef(ScalarTypeKind.Int32))),
                ShapeOf("LoadDto", RequiredString("Id"), RequiredString("Code"))
            ]);
        var original = Assert.IsType<RelationDraft>(DirectFieldRelationDraftConventionMatcher.Match(
            DirectRequest(LoadShapeId, LoadDtoShapeId),
            [graph]).Draft);
        var draft = original with
        {
            Projection = original.Projection with
            {
                Assignments =
                [
                    .. original.Projection.Assignments.Select(slot => slot.Target.ToString() switch
                    {
                        "Id" => slot with
                        {
                            Resolution = new UnresolvedRelationDraftAssignmentResolution(
                                [RelationDraftUnresolvedReason.NoCandidate])
                        },
                        "Code" => slot with
                        {
                            Resolution = new SelectedRelationDraftAssignmentResolution(
                                Assert.Single(slot.Candidates).Id)
                        },
                        _ => slot
                    })
                ]
            }
        };

        var accepted = RelationDraftAcceptor.Accept(draft, [graph]);

        Assert.False(accepted.IsAccepted);
        AssertDiagnostic(accepted.Validation, "relationDraft.resolution.unresolved");
        AssertDiagnostic(accepted.Validation, "relationDraft.assignment.typeIncompatible");
        AssertDiagnostic(accepted.Validation, "relationDraft.assignment.conversionRequired");
    }

    [Fact]
    public void InvalidExplicitJoinKind_IsRejectedByCanonicalValidation()
    {
        var left = new SourceQueryNode(new QueryNodeId("left"), new ValueBindingId("left"), LoadShapeId);
        var right = new SourceQueryNode(new QueryNodeId("right"), new ValueBindingId("right"), CustomerShapeId);
        var join = new JoinQueryNode(
            new QueryNodeId("join"),
            left.Id,
            right.Id,
            JoinKind.Inner,
            Expr.Const(true)) with
        {
            Kind = (JoinKind)99
        };
        var project = new ProjectQueryNode(
            new QueryNodeId("project"),
            join.Id,
            ResultBinding,
            LoadDtoShapeId,
            [
                new ProjectionAssignment(
                    new QueryAssignmentId("id"),
                    FieldPath.FromField("Id"),
                    Expr.Field(left.Binding, "Id"))
            ]);
        var definition = new Cohesive.Relations.IR.RelationDefinition(
            new RelationId("invalid-join"),
            new RelationName("Invalid join"),
            new LogicalQueryDefinition([left, right, join, project]),
            left.Binding,
            new RelationOutputDefinition(project.Id, project.ResultShape, RelationOutputMode.ManyPerRoot));

        var validation = RelationQueryDefinitionValidator.Validate(definition);

        AssertDiagnostic(validation, "relationQuery.join.kindInvalid");
    }

    [Fact]
    public void TraversalProjection_AcceptsFlatDtoWithExactCatalog()
    {
        var fixture = CreateTraversalFixture();

        var accepted = RelationDraftAcceptor.Accept(
            fixture.Draft,
            [fixture.Graph],
            fixture.Catalog);

        Assert.True(accepted.IsAccepted, FormatDiagnostics(accepted.Diagnostics));
        Assert.Equal(
            fixture.Catalog.CatalogFingerprint,
            accepted.Provenance.RelationshipCatalogFingerprint);
        var definition = Assert.IsType<Cohesive.Relations.IR.RelationDefinition>(accepted.Definition);
        Assert.Contains(definition.Body.Nodes, static node => node is TraverseRelationshipQueryNode);
        var projection = Assert.Single(definition.Body.Nodes.OfType<ProjectQueryNode>());
        Assert.Equal(4, projection.Assignments.Length);
        Assert.Contains(projection.Assignments, static assignment =>
            assignment.Target == FieldPath.FromField("CustomerName")
            && assignment.Value is FieldExpr { Binding: { } binding }
            && binding == CustomerBinding);
    }

    [Fact]
    public void TraversalProjection_WithoutCatalog_IsRejected()
    {
        var fixture = CreateTraversalFixture();

        var accepted = RelationDraftAcceptor.Accept(fixture.Draft, [fixture.Graph]);

        Assert.False(accepted.IsAccepted);
        AssertDiagnostic(accepted.Validation, "relationDraft.relationshipCatalog.required");
    }

    [Fact]
    public void OptionalTraversalBinding_CannotPopulateRequiredTargetField()
    {
        var fixture = CreateTraversalFixture(QueryInputRequirement.Optional);

        var accepted = RelationDraftAcceptor.Accept(
            fixture.Draft,
            [fixture.Graph],
            fixture.Catalog);

        Assert.False(accepted.IsAccepted);
        AssertDiagnostic(
            accepted.Validation,
            "relationDraft.assignment.bindingPresenceUnsafe");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedField_AcceptsInlineAndNamedStructures(bool named)
    {
        var (graph, draft) = NestedFixture(named: named);
        var document = RelationDraftDocument.FromDraft(draft);
        var restored = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document));
        var accepted = RelationDraftAcceptor.Accept(restored.Draft, [graph]);
        Assert.True(accepted.IsAccepted, FormatDiagnostics(accepted.Diagnostics));
        Assert.Equal(document.DraftFingerprint, accepted.Provenance.DraftFingerprint);
        Assert.Equal(RelationQueryDefinitionFingerprinter.Compute(CreateHandAuthoredRelation(draft)), accepted.DefinitionFingerprint);
        var values = ImmutableDictionary<string, ObservationValue>.Empty.Add("Header",
            ObservationValue.FromObject(ImmutableDictionary<string, ObservationValue>.Empty.Add("Id", ObservationValue.FromString("shipment-42"))));
        var evaluation = RelationQueryDocument.FromDefinition(accepted.Definition!)
            .Evaluate(new("tests/nested-draft"), [ShapeGraphDocument.FromGraph(graph)])
            .Supply([new RelationQuerySuppliedRoot("root-1", LoadShapeId, values)])
            .Build();
        var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
        Assert.True(outcome.IsSuccessful);
        var row = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows);
        Assert.Equal("shipment-42", row.Value.GetProperty("Id").GetString());
    }

    [Theory]
    [InlineData(FieldPresence.Optional, FieldNullability.NonNullable, "relationDraft.assignment.presenceUnsafe")]
    [InlineData(FieldPresence.Required, FieldNullability.Nullable, "relationDraft.assignment.nullabilityUnsafe")]
    public void NestedField_PropagatesAncestorWeakness(FieldPresence presence, FieldNullability nullability, string code)
    {
        var (graph, draft) = NestedFixture(parentPresence: presence, parentNullability: nullability);
        var accepted = RelationDraftAcceptor.Accept(draft, [graph]);
        Assert.False(accepted.IsAccepted);
        AssertDiagnostic(accepted.Validation, code);
    }

    [Fact]
    public void NestedField_OptionalNullableAncestorsCanPopulatePermissiveTarget()
    {
        var (graph, draft) = NestedFixture(parentPresence: FieldPresence.Optional,
            parentNullability: FieldNullability.Nullable, targetPresence: FieldPresence.Optional,
            targetNullability: FieldNullability.Nullable);
        var accepted = RelationDraftAcceptor.Accept(draft, [graph]);
        Assert.True(accepted.IsAccepted, FormatDiagnostics(accepted.Diagnostics));
    }

    [Theory]
    [InlineData("Header.Missing", FieldCardinality.Single, "relationDraft.candidate.pathUnknown")]
    [InlineData("Header.Id", FieldCardinality.Many, "relationDraft.candidate.pathUnknown")]
    [InlineData("Header.[].Id", FieldCardinality.Many, "relationDraft.assignment.structureUnsupported")]
    public void NestedField_RejectsUnknownPathsAndImplicitCollectionNavigation(string path, FieldCardinality parentCardinality, string code)
    {
        var (graph, draft) = NestedFixture(path: path, parentCardinality: parentCardinality);
        var accepted = RelationDraftAcceptor.Accept(draft, [graph]);
        Assert.False(accepted.IsAccepted);
        AssertDiagnostic(accepted.Validation, code);
    }

    [Fact]
    public void NestedField_RetainsTypeAndCardinalityChecks()
    {
        var (graph, draft) = NestedFixture(leafType: new ScalarTypeRef(ScalarTypeKind.Int64), leafCardinality: FieldCardinality.Many);
        var accepted = RelationDraftAcceptor.Accept(draft, [graph]);
        Assert.False(accepted.IsAccepted);
        AssertDiagnostic(accepted.Validation, "relationDraft.assignment.typeIncompatible");
        AssertDiagnostic(accepted.Validation, "relationDraft.assignment.cardinalityUnsafe");
    }

    [Fact]
    public void NestedField_CanCopyAWholeCollectionWithoutElementTraversal()
    {
        var (graph, draft) = NestedFixture(leafCardinality: FieldCardinality.Many, targetCardinality: FieldCardinality.Many);
        var accepted = RelationDraftAcceptor.Accept(draft, [graph]);
        Assert.True(accepted.IsAccepted, FormatDiagnostics(accepted.Diagnostics));
    }

    [Fact]
    public void ConstructedObject_PreservesOptionalRelationshipBindingAbsence()
    {
        var fixture = CreateTraversalFixture(QueryInputRequirement.Optional);
        var graph = new ShapeGraph(fixture.Graph.Id, [.. fixture.Graph.Shapes.Select(shape =>
            shape.Id == LoadSearchDtoShapeId.ShapeId
                ? new Shape(shape.Id, [.. shape.Fields.Select(field => field.Name.Value == "CustomerName"
                    ? field with { Type = new ObjectTypeRef([new("Name", new ScalarTypeRef(ScalarTypeKind.String))]), Presence = FieldPresence.Optional }
                    : field)])
                : shape)]);
        var value = Expr.Call(ExprFunctionNames.Object, [Expr.Const("Name"), Expr.Field(CustomerBinding, FieldPath.Parse("Name"))]);
        var draft = fixture.Draft with
        {
            Projection = fixture.Draft.Projection with
            {
                Assignments = [.. fixture.Draft.Projection.Assignments.Select(slot =>
                {
                    if (slot.Target.ToString() != "CustomerName") return slot;
                    var candidate = RelationDraftIdentityConvention.CreateCandidateId(slot.Id, value);
                    return slot with { Candidates = [new(candidate, value)], Resolution = new SelectedRelationDraftAssignmentResolution(candidate) };
                })]
            }
        };
        var accepted = RelationDraftAcceptor.Accept(draft, [graph], fixture.Catalog);
        Assert.False(accepted.IsAccepted);
        Assert.Contains(accepted.Diagnostics, diagnostic => diagnostic.Code == "relationDraft.assignment.bindingPresenceUnsafe"
            && diagnostic.Message.Contains("CustomerName.Name") && diagnostic.Location!.EndsWith("/value/arguments/1", StringComparison.Ordinal));
    }

    static (ShapeGraph Graph, RelationDraft Draft) NestedFixture(
        bool named = false,
        string path = "Header.Id",
        FieldPresence parentPresence = FieldPresence.Required,
        FieldNullability parentNullability = FieldNullability.NonNullable,
        FieldCardinality parentCardinality = FieldCardinality.Single,
        FieldPresence targetPresence = FieldPresence.Required,
        FieldNullability targetNullability = FieldNullability.NonNullable,
        TypeRef? leafType = null,
        FieldCardinality leafCardinality = FieldCardinality.Single,
        FieldCardinality targetCardinality = FieldCardinality.Single)
    {
        leafType ??= new ScalarTypeRef(ScalarTypeKind.String);
        var typeId = new TypeId("Header");
        TypeRef header = named ? new NamedTypeRef(typeId) : new ObjectTypeRef(
            [new ObjectFieldTypeDef("Id", leafType, cardinality: leafCardinality)]);
        var graph = new ShapeGraph(GraphId,
            [ShapeOf("Load", new FieldDefinition(new("Header"), header, presence: parentPresence,
                nullability: parentNullability, cardinality: parentCardinality)),
             ShapeOf("LoadDto", new FieldDefinition(new("Id"), new ScalarTypeRef(ScalarTypeKind.String),
                presence: targetPresence, nullability: targetNullability, cardinality: targetCardinality))],
            named ? [new TypeDefinition.Structural(typeId, [new StructuralField(new("Id"), leafType, cardinality: leafCardinality)])] : []);
        var initial = DirectFieldRelationDraftConventionMatcher.Match(DirectRequest(LoadShapeId, LoadDtoShapeId), [graph]).Draft!;
        var draft = initial with
        {
            Projection = initial.Projection with
            {
                Assignments = [SelectedSlot(LoadDtoShapeId, "Id", LoadBinding, path)]
            }
        };
        return (graph, draft);
    }

    static DirectFieldRelationDraftConventionRequest DirectRequest(
        QualifiedShapeId sourceShape,
        QualifiedShapeId targetShape) =>
        new(
            new RelationDraftId("direct-draft"),
            new RelationId("direct-relation"),
            new RelationName("DirectRelation"),
            new SourceQueryNode(new QueryNodeId("source"), LoadBinding, sourceShape),
            new QueryNodeId("project"),
            ResultBinding,
            targetShape);

    static TraversalFixture CreateTraversalFixture(
        QueryInputRequirement requirement = QueryInputRequirement.Required)
    {
        var graph = new ShapeGraph(
            GraphId,
            [
                ShapeOf(
                    "Load",
                    RequiredString("Id"),
                    new FieldDefinition(
                        new("CustomerId"),
                        new ScalarTypeRef(ScalarTypeKind.String),
                        role: FieldRole.Reference)),
                new Shape(
                    new ShapeId("Customer"),
                    [RequiredString("Id"), RequiredString("Name"), RequiredString("Type")],
                    annotations: AnnotationMap.Create(
                        ShapeAnnotationKeys.EntityType,
                        AnnotationValue.FromString("Customer")),
                    role: ShapeRoles.Entity),
                ShapeOf(
                    "LoadSearchDto",
                    RequiredString("Id"),
                    RequiredString("CustomerId"),
                    RequiredString("CustomerName"),
                    RequiredString("CustomerType"))
            ]);
        var sourceReference = FieldPath.FromField("CustomerId");
        var relationship = new RelationshipDefinition(
            RelationshipIdConvention.Create(
                LoadShapeId,
                sourceReference,
                CustomerShapeId,
                ObservationIdentityRelationshipTargetKey.Instance),
            LoadShapeId,
            sourceReference,
            CustomerShapeId,
            ObservationIdentityRelationshipTargetKey.Instance);
        var catalog = RelationshipCatalogDocument.FromCatalog(new([relationship]));
        var sourceNode = new QueryNodeId("source");
        var traversalNode = new QueryNodeId("customer");
        var projectionNode = new QueryNodeId("project");
        var draft = new RelationDraft(
            new RelationDraftId("load-search-draft"),
            new RelationId("load-search"),
            new RelationName("LoadSearch"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceNode, LoadBinding, LoadShapeId),
                new TraverseRelationshipQueryNode(
                    traversalNode,
                    sourceNode,
                    LoadBinding,
                    relationship.Id,
                    RelationshipTraversalDirection.Forward,
                    CustomerBinding,
                    JoinKind.Left,
                    requirement)
            ]),
            LoadBinding,
            new RelationDraftProjection(
                projectionNode,
                traversalNode,
                ResultBinding,
                LoadSearchDtoShapeId,
                [
                    SelectedSlot(LoadSearchDtoShapeId, "Id", LoadBinding, "Id"),
                    SelectedSlot(LoadSearchDtoShapeId, "CustomerId", LoadBinding, "CustomerId"),
                    SelectedSlot(LoadSearchDtoShapeId, "CustomerName", CustomerBinding, "Name"),
                    SelectedSlot(LoadSearchDtoShapeId, "CustomerType", CustomerBinding, "Type")
                ]));
        return new(graph, catalog, draft);
    }

    static RelationDraftAssignmentSlot SelectedSlot(
        QualifiedShapeId targetShape,
        string target,
        ValueBindingId sourceBinding,
        string source)
    {
        var targetPath = FieldPath.FromField(target);
        var slotId = RelationDraftIdentityConvention.CreateAssignmentSlotId(targetShape, targetPath);
        var value = Expr.Field(sourceBinding, source);
        var candidate = new RelationDraftCandidate(
            RelationDraftIdentityConvention.CreateCandidateId(slotId, value),
            value);
        return new(
            slotId,
            targetPath,
            [candidate],
            new SelectedRelationDraftAssignmentResolution(candidate.Id));
    }

    static RelationDraft WithResolution(
        RelationDraft draft,
        QueryAssignmentId slotId,
        RelationDraftAssignmentResolution resolution) =>
        draft with
        {
            Projection = draft.Projection with
            {
                Assignments =
                [
                    .. draft.Projection.Assignments.Select(slot =>
                        slot.Id == slotId ? slot with { Resolution = resolution } : slot)
                ]
            }
        };

    static Cohesive.Relations.IR.RelationDefinition CreateHandAuthoredRelation(RelationDraft draft)
    {
        var assignments = draft.Projection.Assignments
            .Select(slot =>
            {
                var selected = Assert.IsType<SelectedRelationDraftAssignmentResolution>(slot.Resolution);
                var candidate = Assert.Single(slot.Candidates, candidate =>
                    candidate.Id == selected.CandidateId);
                return new ProjectionAssignment(slot.Id, slot.Target, candidate.Value);
            })
            .ToImmutableArray();
        var project = new ProjectQueryNode(
            draft.Projection.Id,
            draft.Projection.Input,
            draft.Projection.ResultBinding,
            draft.Projection.ResultShape,
            assignments);
        return new(
            draft.RelationId,
            draft.Name,
            new LogicalQueryDefinition([.. draft.Input.Nodes, project], draft.Input.Parameters),
            draft.RootBinding,
            new RelationOutputDefinition(
                project.Id,
                project.ResultShape,
                draft.OutputMode,
                draft.OutputKey),
            draft.Invariants);
    }

    static Shape ShapeOf(string name, params FieldDefinition[] fields) =>
        new(new ShapeId(name), [.. fields]);

    static FieldDefinition RequiredString(string name) =>
        new(new FieldName(name), new ScalarTypeRef(ScalarTypeKind.String));

    static QualifiedShapeId Qualified(string name) =>
        new(GraphId, new ShapeId(name));

    static void AssertDiagnostic(DocumentValidationResult validation, string code) =>
        Assert.Contains(validation.Diagnostics, diagnostic =>
            string.Equals(diagnostic.Code, code, StringComparison.Ordinal));

    static string FormatDiagnostics(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));

    readonly record struct TraversalFixture(
        ShapeGraph Graph,
        RelationshipCatalogDocument Catalog,
        RelationDraft Draft);
}
