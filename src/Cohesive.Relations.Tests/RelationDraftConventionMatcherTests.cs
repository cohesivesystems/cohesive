using System.Globalization;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationDraftConventionMatcherTests
{
    static readonly GraphId GraphId = new("relation-draft-conventions/v1");
    static readonly QualifiedShapeId SourceShapeId = new(GraphId, new ShapeId("Load"));
    static readonly QualifiedShapeId TargetShapeId = new(GraphId, new ShapeId("LoadDto"));
    static readonly ValueBindingId SourceBinding = new("load");

    [Fact]
    public void ExactOrdinalMatches_ProduceCompleteDeterministicDraft()
    {
        FieldDefinition[] sourceFields = [StringField("Name"), StringField("Id")];
        FieldDefinition[] targetFields = [StringField("Id"), StringField("Name")];

        var first = Match(sourceFields, targetFields);
        var second = Match(sourceFields, targetFields);

        Assert.True(first.IsComplete);
        Assert.NotNull(first.Draft);
        Assert.Empty(first.Diagnostics);
        Assert.Equal(2, first.Draft.Projection.Assignments.Length);

        foreach (var target in new[] { "Id", "Name" })
        {
            var slot = GetSlot(first, target);
            var field = GetSelectedField(slot);
            var expectedSlotId = RelationDraftIdentityConvention.CreateAssignmentSlotId(
                TargetShapeId,
                FieldPath.FromField(target)
                );
            var expectedCandidateId = RelationDraftIdentityConvention.CreateCandidateId(
                expectedSlotId,
                Expr.Field(SourceBinding, target)
                );

            Assert.Equal(expectedSlotId, slot.Id);
            Assert.Equal(FieldPath.FromField(target), field.Path);
            Assert.Equal(SourceBinding, field.Binding);
            Assert.Equal(expectedCandidateId, Assert.Single(slot.Candidates).Id);

            var decision = Assert.Single(first.Decisions, decision => decision.SlotId == slot.Id);
            Assert.Equal(DirectFieldRelationDraftConventionMatcher.ExactOrdinalRuleId, decision.RuleId);
            Assert.Equal(FieldPath.FromField(target), decision.Source);
            Assert.Equal(slot.Target, decision.Target);
        }

        Assert.Equal(
            RelationDraftFingerprinter.Compute(first.Draft),
            RelationDraftFingerprinter.Compute(Assert.IsType<RelationDraft>(second.Draft)));
    }

    [Fact]
    public void ExplicitAliases_TakePrecedence_AndUnsafeAliasesDoNotFallBackToExactNames()
    {
        FieldDefinition[] sourceFields =
        [
            StringField("Name"),
            StringField("DisplayName"),
            StringField("Code"),
            StringField(
                "MaybeCode",
                presence: FieldPresence.Optional,
                nullability: FieldNullability.Nullable)
        ];
        FieldDefinition[] targetFields = [StringField("Name"), StringField("Code")];
        RelationDraftFieldAlias[] aliases =
        [
            new(FieldPath.FromField("Name"), FieldPath.FromField("DisplayName")),
            new(FieldPath.FromField("Code"), FieldPath.FromField("MaybeCode"))
        ];

        var result = Match(sourceFields, targetFields, aliases);

        Assert.False(result.IsComplete);

        var name = GetSlot(result, "Name");
        Assert.Equal(FieldPath.FromField("DisplayName"), GetSelectedField(name).Path);
        Assert.Equal(
            DirectFieldRelationDraftConventionMatcher.ExplicitAliasRuleId,
            Assert.Single(result.Decisions, decision => decision.SlotId == name.Id).RuleId);

        var code = GetSlot(result, "Code");
        Assert.IsType<UnresolvedRelationDraftAssignmentResolution>(code.Resolution);
        var codeCandidate = Assert.Single(code.Candidates);
        var codeField = Assert.IsType<FieldExpr>(codeCandidate.Value);
        Assert.Equal(FieldPath.FromField("MaybeCode"), codeField.Path);
        Assert.DoesNotContain(
            code.Candidates,
            candidate => candidate.Value is FieldExpr { Path: var path }
                         && path == FieldPath.FromField("Code"));
        Assert.Equal(
            DirectFieldRelationDraftConventionMatcher.ExplicitAliasRuleId,
            Assert.Single(result.Decisions, decision => decision.SlotId == code.Id).RuleId);
        AssertDiagnostic(result, "relationDraft.assignment.presenceUnsafe");
        AssertDiagnostic(result, "relationDraft.assignment.nullabilityUnsafe");
    }

    [Fact]
    public void OrdinalIgnoreCaseMatching_IsIndependentOfTurkishCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var turkish = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;

            var result = Match([StringField("FILE")], [StringField("file")]);

            Assert.True(result.IsComplete);
            var slot = GetSlot(result, "file");
            Assert.Equal(FieldPath.FromField("FILE"), GetSelectedField(slot).Path);
            Assert.Equal(
                DirectFieldRelationDraftConventionMatcher.OrdinalIgnoreCaseRuleId,
                Assert.Single(result.Decisions).RuleId);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void MultipleOrdinalIgnoreCaseMatches_RemainExplicitlyAmbiguous()
    {
        var result = Match(
            [StringField("name"), StringField("NAME")],
            [StringField("Name")]);

        Assert.False(result.IsComplete);
        var slot = GetSlot(result, "Name");
        var resolution = Assert.IsType<AmbiguousRelationDraftAssignmentResolution>(slot.Resolution);

        Assert.Equal(2, slot.Candidates.Length);
        Assert.Equal(
            slot.Candidates.Select(static candidate => candidate.Id),
            resolution.CandidateIds);
        Assert.All(
            result.Decisions,
            decision => Assert.Equal(
                DirectFieldRelationDraftConventionMatcher.OrdinalIgnoreCaseRuleId,
                decision.RuleId));
        Assert.Equal(
            ["NAME", "name"],
            result.Decisions.Select(static decision => decision.Source?.ToString() ?? string.Empty).ToArray());
        AssertDiagnostic(result, "relationDraft.convention.multipleCandidates");
    }

    [Fact]
    public void MissingRequiredAndOptionalTargets_RemainUnresolved()
    {
        var result = Match(
            [StringField("Id")],
            [
                StringField("RequiredValue"),
                StringField(
                    "OptionalValue",
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable)
            ]);

        Assert.False(result.IsComplete);
        Assert.Equal(2, Assert.IsType<RelationDraft>(result.Draft).Projection.Assignments.Length);
        foreach (var target in new[] { "OptionalValue", "RequiredValue" })
        {
            var slot = GetSlot(result, target);
            Assert.Empty(slot.Candidates);
            Assert.IsType<UnresolvedRelationDraftAssignmentResolution>(slot.Resolution);
            var decision = Assert.Single(result.Decisions, decision => decision.SlotId == slot.Id);
            Assert.Equal(DirectFieldRelationDraftConventionMatcher.NoCandidateRuleId, decision.RuleId);
            Assert.Null(decision.CandidateId);
        }

        Assert.Equal(
            2,
            result.Diagnostics.Count(diagnostic =>
                diagnostic.Code == "relationDraft.convention.noCandidate"));
    }

    [Fact]
    public void FieldDeclarationOrder_DoesNotChangeIdsDecisionsOrDraftFingerprint()
    {
        var first = Match(
            [StringField("Name"), StringField("Id")],
            [StringField("Name"), StringField("Id")]);
        var reordered = Match(
            [StringField("Id"), StringField("Name")],
            [StringField("Id"), StringField("Name")]);

        Assert.True(first.IsComplete);
        Assert.True(reordered.IsComplete);
        Assert.Equal(GetIdentitySnapshot(first), GetIdentitySnapshot(reordered));
        Assert.Equal(GetDecisionSnapshot(first), GetDecisionSnapshot(reordered));
        Assert.Equal(
            RelationDraftFingerprinter.Compute(Assert.IsType<RelationDraft>(first.Draft)),
            RelationDraftFingerprinter.Compute(Assert.IsType<RelationDraft>(reordered.Draft)));
    }

    [Fact]
    public void ExactStructuredValueCopy_DoesNotRequireNestedMapping()
    {
        var arrayType = new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.String));
        var result = Match(
            [new FieldDefinition(new FieldName("Tags"), arrayType)],
            [new FieldDefinition(new FieldName("Tags"), arrayType)]);

        Assert.True(result.IsComplete, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(FieldPath.FromField("Tags"), GetSelectedField(GetSlot(result, "Tags")).Path);
    }

    [Fact]
    public void StructuredValueContainingOpaqueRuntimeType_RemainsUnresolved()
    {
        var opaqueArray = new ArrayTypeRef(new OpaqueRuntimeTypeRef("System.Object"));
        var result = Match(
            [new FieldDefinition(new FieldName("Values"), opaqueArray)],
            [new FieldDefinition(new FieldName("Values"), opaqueArray)]);

        Assert.False(result.IsComplete);
        var unresolved = Assert.IsType<UnresolvedRelationDraftAssignmentResolution>(
            GetSlot(result, "Values").Resolution);
        Assert.Contains(RelationDraftUnresolvedReason.UnsupportedTransformation, unresolved.Reasons);
        AssertDiagnostic(result, "relationDraft.assignment.transformationUnsupported");
    }

    [Fact]
    public void NestedExplicitAlias_RemainsAnUnsupportedStructuralHoleWithoutFallback()
    {
        var nestedSource = new FieldPath(
        [
            FieldPathSegment.ForField("Customer"),
            FieldPathSegment.ForField("Name")
        ]);
        var result = Match(
            [StringField("CustomerName")],
            [StringField("CustomerName")],
            [new(FieldPath.FromField("CustomerName"), nestedSource)]);

        Assert.False(result.IsComplete);
        var slot = GetSlot(result, "CustomerName");
        Assert.Empty(slot.Candidates);
        var unresolved = Assert.IsType<UnresolvedRelationDraftAssignmentResolution>(slot.Resolution);
        Assert.Equal(
            RelationDraftUnresolvedReason.UnsupportedStructure,
            Assert.Single(unresolved.Reasons));
        Assert.Null(Assert.Single(result.Decisions).CandidateId);
        AssertDiagnostic(result, "relationDraft.convention.aliasSourceNotTopLevel");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "relationDraft.convention.aliasSourceUnknown");
    }

    [Fact]
    public void EmptyTargetShape_CannotProduceACompleteDraft()
    {
        var result = Match([StringField("Id")], []);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Draft);
        AssertDiagnostic(result, "relationDraft.projection.assignmentsEmpty");
    }

    [Fact]
    public void AliasTargetingComputedField_IsRejectedAsInvalidPolicy()
    {
        var computed = new FieldDefinition(
            new FieldName("DisplayName"),
            new ScalarTypeRef(ScalarTypeKind.String),
            role: FieldRole.Computed,
            mutability: FieldMutability.Computed,
            compute: new ComputeDefinition(Expr.Const("computed")));
        var result = Match(
            [StringField("DisplayName")],
            [computed],
            [new(FieldPath.FromField("DisplayName"), FieldPath.FromField("DisplayName"))]);

        Assert.False(result.IsComplete);
        AssertDiagnostic(result, "relationDraft.convention.aliasTargetComputed");
    }

    [Fact]
    public void InvalidShapeGraph_CannotProduceACompleteDraft()
    {
        var graph = new ShapeGraph(
            GraphId,
            [
                new Shape(SourceShapeId.ShapeId, [StringField("Id")]),
                new Shape(SourceShapeId.ShapeId, [StringField("OtherId")]),
                new Shape(TargetShapeId.ShapeId, [StringField("Id")])
            ]);

        var result = DirectFieldRelationDraftConventionMatcher.Match(
            new DirectFieldRelationDraftConventionRequest(
                new RelationDraftId("invalid-graph-draft"),
                new RelationId("invalid-graph-relation"),
                new RelationName("Invalid graph relation"),
                new SourceQueryNode(new QueryNodeId("source"), SourceBinding, SourceShapeId),
                new QueryNodeId("project"),
                new ValueBindingId("result"),
                TargetShapeId),
            [graph]);

        Assert.False(result.IsComplete);
        AssertDiagnostic(result, "relationDraft.shapeGraph.invalid");
    }

    static RelationDraftConventionMatchResult Match(
        IEnumerable<FieldDefinition> sourceFields,
        IEnumerable<FieldDefinition> targetFields,
        IEnumerable<RelationDraftFieldAlias>? aliases = null)
    {
        var graph = new ShapeGraph(
            GraphId,
            [
                new Shape(SourceShapeId.ShapeId, [.. sourceFields]),
                new Shape(TargetShapeId.ShapeId, [.. targetFields])
            ]);
        var source = new SourceQueryNode(new QueryNodeId("source"), SourceBinding, SourceShapeId);
        var request = new DirectFieldRelationDraftConventionRequest(
            new RelationDraftId("load-to-dto-draft"),
            new RelationId("load-to-dto"),
            new RelationName("Load to DTO"),
            source,
            new QueryNodeId("project"),
            new ValueBindingId("result"),
            TargetShapeId,
            aliases: aliases is null ? [] : [.. aliases]);

        return DirectFieldRelationDraftConventionMatcher.Match(request, [graph]);
    }

    static FieldDefinition StringField(
        string name,
        FieldPresence presence = FieldPresence.Required,
        FieldNullability nullability = FieldNullability.NonNullable) =>
        new(
            new FieldName(name),
            new ScalarTypeRef(ScalarTypeKind.String),
            presence: presence,
            nullability: nullability);

    static RelationDraftAssignmentSlot GetSlot(RelationDraftConventionMatchResult result, string target) =>
        Assert.Single(
            Assert.IsType<RelationDraft>(result.Draft).Projection.Assignments,
            slot => slot.Target == FieldPath.FromField(target)
            );

    static FieldExpr GetSelectedField(RelationDraftAssignmentSlot slot)
    {
        var selected = Assert.IsType<SelectedRelationDraftAssignmentResolution>(slot.Resolution);
        var candidate = Assert.Single(slot.Candidates, candidate => candidate.Id == selected.CandidateId);
        return Assert.IsType<FieldExpr>(candidate.Value);
    }

    static (string Slot, string Candidate)[] GetIdentitySnapshot(
        RelationDraftConventionMatchResult result) =>
        [
            .. Assert.IsType<RelationDraft>(result.Draft).Projection.Assignments.SelectMany(
                static slot => slot.Candidates.Select(candidate => (slot.Id.Value, candidate.Id.Value)))
        ];

    static (string Rule, string Slot, string? Candidate, string? Source, string Target)[] GetDecisionSnapshot(
        RelationDraftConventionMatchResult result) =>
        [
            .. result.Decisions.Select(static decision =>
                (
                    decision.RuleId,
                    decision.SlotId.Value,
                    decision.CandidateId?.Value,
                    decision.Source?.ToString(),
                    decision.Target.ToString()))
        ];

    static void AssertDiagnostic(RelationDraftConventionMatchResult result, string code) =>
        Assert.Contains(
            result.Diagnostics,
            diagnostic => string.Equals(diagnostic.Code, code, StringComparison.Ordinal));
}
