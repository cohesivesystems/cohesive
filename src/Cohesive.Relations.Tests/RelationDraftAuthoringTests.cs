using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Coverage for <see cref="RelationQueryAuthoringCore.BuildDraft{TInput}"/>, which lets draft producers author the
/// pre-projection input through the core instead of constructing it directly.
/// </summary>
public sealed class RelationDraftAuthoringTests
{
    static readonly GraphId GraphId = new("relation-draft-authoring/v1");
    static readonly QualifiedShapeId LoadShapeId = new(GraphId, new ShapeId("Load"));
    static readonly QualifiedShapeId LoadDtoShapeId = new(GraphId, new ShapeId("LoadDto"));
    static readonly ShapeGraph Graph = new(
        GraphId,
        [
            new Shape(new ShapeId("Load"), [String("Id"), String("Status")]),
            new Shape(new ShapeId("LoadDto"), [String("Id")]),
        ]);

    [Fact]
    public void BuildDraft_reproduces_a_hand_built_draft_with_the_same_identities()
    {
        var author = RelationQuery.Structural();
        var source = author.Source(LoadShapeId, nodeId: new("source"), bindingId: new("source"));
        var filtered = author.Filter(source.Node, ActivePredicate(source.Binding.Field("Status")), nodeId: new("filter"));

        var built = author.BuildDraft(
            new("draft_loads"),
            new("rel_loads"),
            new("Loads"),
            source.Binding,
            filtered,
            LoadDtoShapeId,
            [IdAssignment(source.Binding.Field("Id"))],
            projectionId: new("project"),
            resultBindingId: new("result"));

        Assert.True(built.Validation.IsValid, Format(built.Validation));
        Assert.Equal(RelationDraftFingerprinter.Compute(HandBuiltDraft()), RelationDraftFingerprinter.Compute(built.Draft));
    }

    [Fact]
    public void A_built_draft_accepts_into_the_same_relation_as_the_hand_built_draft()
    {
        var author = RelationQuery.Structural();
        var source = author.Source(LoadShapeId, nodeId: new("source"), bindingId: new("source"));
        var filtered = author.Filter(source.Node, ActivePredicate(source.Binding.Field("Status")), nodeId: new("filter"));
        var built = author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, filtered, LoadDtoShapeId,
            [IdAssignment(source.Binding.Field("Id"))], projectionId: new("project"), resultBindingId: new("result"));

        var accepted = RelationDraftAcceptor.Accept(built.Draft, [Graph]);
        var reference = RelationDraftAcceptor.Accept(HandBuiltDraft(), [Graph]);

        Assert.True(accepted.IsAccepted, string.Join("; ", accepted.Diagnostics.Select(static d => d.Message)));
        Assert.Equal(reference.DefinitionFingerprint, accepted.DefinitionFingerprint);
    }

    [Fact]
    public void Conventional_projection_identities_match_what_Project_would_select()
    {
        var drafting = RelationQuery.Structural();
        var draftSource = drafting.Source(LoadShapeId);
        var built = drafting.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), draftSource.Binding, draftSource.Node, LoadDtoShapeId,
            [IdAssignment(draftSource.Binding.Field("Id"))]);

        var projecting = RelationQuery.Structural();
        var projectSource = projecting.Source(LoadShapeId);
        var projected = projecting.Project(
            projectSource.Node,
            LoadDtoShapeId,
            [new RelationQueryProjectionAssignment(FieldPath.FromField("Id"), projectSource.Binding.Field("Id"))]);

        Assert.True(built.Validation.IsValid, Format(built.Validation));
        Assert.Equal(projected.Node.Id, built.Draft.Projection.Id);
        Assert.Equal(projected.Binding.Id, built.Draft.Projection.ResultBinding);
    }

    [Fact]
    public void Provenance_records_the_projection_identities_and_attributed_decisions()
    {
        var producer = new RelationQueryAuthoringSource("ari.inference", "mapping/loads");
        var author = RelationQuery.Structural();
        var source = author.Source(LoadShapeId, source: producer);
        var key = Expr.Call(ExprFunctionNames.Key);

        var built = author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, source.Node, LoadDtoShapeId,
            [IdAssignment(source.Binding.Field("Id"))],
            outputKey: key,
            invariants: [new InvariantDefinition("has-id", Expr.Eq(source.Binding.Field("Id"), source.Binding.Field("Id")))],
            source: producer);

        var projection = built.Draft.Projection;
        Assert.Contains(built.Provenance.Identities, identity =>
            identity.Kind == RelationQueryAuthoringIdentityKind.Node
            && identity.Value == projection.Id.Value
            && identity.Origin == RelationQueryAuthoringIdentityOrigin.Convention);
        Assert.Contains(built.Provenance.Identities, identity =>
            identity.Kind == RelationQueryAuthoringIdentityKind.Binding && identity.Value == projection.ResultBinding.Value);
        Assert.Contains(built.Provenance.Identities, identity =>
            identity.Kind == RelationQueryAuthoringIdentityKind.Node && identity.Value == source.Node.Id.Value);

        var sources = built.Provenance.Sources;
        Assert.Contains(sources, decision => decision.Kind == RelationQueryAuthoringDecisionKind.Terminal
            && decision.Target == "draft_loads" && decision.Role == RelationDraftWireNames.Draft);
        Assert.Contains(sources, decision => decision.Role == "output/key");
        Assert.Contains(sources, decision => decision.Role == "invariants/0/expression");
        Assert.Contains(sources, decision => decision.Kind == RelationQueryAuthoringDecisionKind.Node
            && decision.Target == projection.Id.Value);
        Assert.Contains(sources, decision => decision.Kind == RelationQueryAuthoringDecisionKind.Assignment
            && decision.Target == Assert.Single(projection.Assignments).Id.Value);
    }

    [Fact]
    public void Explicit_projection_identities_are_recorded_as_explicit()
    {
        var author = RelationQuery.Structural();
        var source = author.Source(LoadShapeId);

        var built = author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, source.Node, LoadDtoShapeId,
            [IdAssignment(source.Binding.Field("Id"))], projectionId: new("project"), resultBindingId: new("result"));

        Assert.Contains(built.Provenance.Identities, identity =>
            identity.Value == "project" && identity.Origin == RelationQueryAuthoringIdentityOrigin.Explicit);
        Assert.Contains(built.Provenance.Identities, identity =>
            identity.Value == "result" && identity.Origin == RelationQueryAuthoringIdentityOrigin.Explicit);
    }

    [Fact]
    public void Parameters_referenced_only_by_candidates_are_carried_into_the_input()
    {
        var author = RelationQuery.Structural();
        var fallback = author.Parameter(new ScalarTypeRef(ScalarTypeKind.String), id: new("fallbackId"));
        var source = author.Source(LoadShapeId);

        var built = author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, source.Node, LoadDtoShapeId,
            [IdAssignment(fallback.Expression)]);

        Assert.Equal(fallback.Id, Assert.Single(built.Draft.Input.Parameters).Id);
    }

    [Fact]
    public void Parameters_referenced_only_by_the_output_key_or_an_invariant_are_carried_into_the_input()
    {
        var author = RelationQuery.Structural();
        var tenant = author.Parameter(new ScalarTypeRef(ScalarTypeKind.String), id: new("tenant"));
        var minimum = author.Parameter(new ScalarTypeRef(ScalarTypeKind.String), id: new("minimum"));
        var source = author.Source(LoadShapeId);

        var built = author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, source.Node, LoadDtoShapeId,
            [IdAssignment(source.Binding.Field("Id"))],
            outputKey: tenant.Expression,
            invariants: [new InvariantDefinition("above-minimum", Expr.Eq(source.Binding.Field("Status"), minimum.Expression))]);

        Assert.Equal(
            ["minimum", "tenant"],
            built.Draft.Input.Parameters.Select(static parameter => parameter.Id.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_result_binding_identity_already_declared_in_the_core_is_rejected()
    {
        var author = RelationQuery.Structural();
        var source = author.Source(LoadShapeId, bindingId: new("load"));

        Assert.Throws<ArgumentException>(() => author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, source.Node, LoadDtoShapeId,
            [IdAssignment(source.Binding.Field("Id"))], resultBindingId: new("load")));
    }

    [Fact]
    public void Assignment_inputs_derive_the_conventional_slot_and_candidate_identities()
    {
        var author = RelationQuery.Structural();
        var source = author.Source(LoadShapeId);
        var id = source.Binding.Field("Id");
        var status = source.Binding.Field("Status");

        var built = author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, source.Node, LoadDtoShapeId,
            [RelationQueryDraftAssignment.Ambiguous(FieldPath.FromField("Id"), [id, status])]);

        var slot = Assert.Single(built.Draft.Projection.Assignments);
        var slotId = RelationDraftIdentityConvention.CreateAssignmentSlotId(LoadDtoShapeId, FieldPath.FromField("Id"));
        Assert.Equal(slotId, slot.Id);
        var ambiguous = Assert.IsType<AmbiguousRelationDraftAssignmentResolution>(slot.Resolution);
        Assert.Equal(
            new[] { RelationDraftIdentityConvention.CreateCandidateId(slotId, id), RelationDraftIdentityConvention.CreateCandidateId(slotId, status) }
                .Select(static candidate => candidate.Value).Order(StringComparer.Ordinal),
            ambiguous.CandidateIds.Select(static candidate => candidate.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_projection_identity_already_declared_in_the_core_is_rejected()
    {
        var author = RelationQuery.Structural();
        var source = author.Source(LoadShapeId, nodeId: new("source"));

        Assert.Throws<ArgumentException>(() => author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, source.Node, LoadDtoShapeId,
            [IdAssignment(source.Binding.Field("Id"))], projectionId: new("source")));
    }

    [Fact]
    public void Handles_from_another_core_are_rejected()
    {
        var author = RelationQuery.Structural();
        var foreign = RelationQuery.Structural().Source(LoadShapeId);

        Assert.Throws<ArgumentException>(() => author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), foreign.Binding, foreign.Node, LoadDtoShapeId,
            [IdAssignment(foreign.Binding.Field("Id"))]));
    }

    [Fact]
    public void Selected_rejects_a_value_repeated_among_its_alternatives()
    {
        var id = Expr.Field(new ValueBindingId("source"), "Id");

        Assert.Throws<ArgumentException>(() =>
            RelationQueryDraftAssignment.Selected(FieldPath.FromField("Id"), id, alternatives: [Expr.Field(new ValueBindingId("source"), "Id")]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Ambiguous_requires_at_least_two_candidates(int count)
    {
        var candidates = Enumerable.Range(0, count)
            .Select(index => Expr.Field(new ValueBindingId("source"), $"F{index}"))
            .ToImmutableArray();

        Assert.Throws<ArgumentException>(() => RelationQueryDraftAssignment.Ambiguous(FieldPath.FromField("Id"), candidates));
    }

    [Fact]
    public void Ambiguous_rejects_duplicate_candidates()
    {
        var binding = new ValueBindingId("source");

        Assert.Throws<ArgumentException>(() => RelationQueryDraftAssignment.Ambiguous(
            FieldPath.FromField("Id"),
            [Expr.Field(binding, "Id"), Expr.Field(binding, "Id")]));
    }

    [Fact]
    public void Unresolved_rejects_missing_undefined_or_repeated_reasons()
    {
        var target = FieldPath.FromField("Id");

        Assert.Throws<ArgumentException>(() => RelationQueryDraftAssignment.Unresolved(target, []));
        Assert.Throws<ArgumentException>(() => RelationQueryDraftAssignment.Unresolved(target, [(RelationDraftUnresolvedReason)999]));
        Assert.Throws<ArgumentException>(() => RelationQueryDraftAssignment.Unresolved(
            target,
            [RelationDraftUnresolvedReason.NoCandidate, RelationDraftUnresolvedReason.NoCandidate]));
    }

    [Fact]
    public void EnsureValid_returns_a_valid_draft_including_unresolved_slots()
    {
        var author = RelationQuery.Structural();
        var source = author.Source(LoadShapeId);

        var built = author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, source.Node, LoadDtoShapeId,
            [RelationQueryDraftAssignment.Unresolved(FieldPath.FromField("Id"), [RelationDraftUnresolvedReason.NoCandidate])]);

        Assert.Same(built.Draft, built.EnsureValid());
    }

    [Fact]
    public void EnsureValid_throws_with_the_validator_diagnostics()
    {
        var author = RelationQuery.Structural();
        var source = author.Source(LoadShapeId);

        var built = author.BuildDraft(
            new("draft_loads"), new("rel_loads"), new("Loads"), source.Binding, source.Node, LoadDtoShapeId,
            [IdAssignment(source.Binding.Field("Id")), IdAssignment(source.Binding.Field("Status"))]);

        Assert.False(built.Validation.IsValid);
        var error = Assert.Throws<InvalidOperationException>(() => built.EnsureValid());
        Assert.Contains(built.Validation.Diagnostics[0].Code, error.Message);
    }

    // The direct construction Ari's inference compiler used before BuildDraft existed; BuildDraft must reproduce it.
    static RelationDraft HandBuiltDraft()
    {
        ValueBindingId binding = new("source");
        var source = new SourceQueryNode(new("source"), binding, LoadShapeId);
        var filter = new FilterQueryNode(new("filter"), source.Id, ActivePredicate(Expr.Field(binding, "Status")));
        return new RelationDraft(
            new("draft_loads"),
            new("rel_loads"),
            new("Loads"),
            new LogicalQueryDefinition([source, filter]),
            binding,
            new RelationDraftProjection(
                new("project"), filter.Id, new("result"), LoadDtoShapeId, [IdSlot(Expr.Field(binding, "Id"))]));
    }

    static RelationQueryDraftAssignment IdAssignment(Expr value) =>
        RelationQueryDraftAssignment.Selected(FieldPath.FromField("Id"), value);

    static Expr ActivePredicate(Expr status) => Expr.Eq(status, Expr.Const("Active"));

    static RelationDraftAssignmentSlot IdSlot(Expr value)
    {
        var target = FieldPath.FromField("Id");
        var slotId = RelationDraftIdentityConvention.CreateAssignmentSlotId(LoadDtoShapeId, target);
        var candidateId = RelationDraftIdentityConvention.CreateCandidateId(slotId, value);
        return new(slotId, target, [new(candidateId, value)], new SelectedRelationDraftAssignmentResolution(candidateId));
    }

    static FieldDefinition String(string name) => new(new FieldName(name), new ScalarTypeRef(ScalarTypeKind.String));

    static string Format(DocumentValidationResult validation) =>
        string.Join("; ", validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
