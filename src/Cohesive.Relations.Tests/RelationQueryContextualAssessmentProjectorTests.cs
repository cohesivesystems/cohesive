using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryContextualAssessmentProjectorTests
{
    const string EvidenceNamespace = "tests/contextual-assessment";
    static readonly RelationQueryAdapterDecisionCode FailureCode = new("TEST-CONTEXT-001");
    static readonly RelationQueryTargetCapabilityEvidenceId MissingEvidence = new("evidence/context-missing");
    static readonly RelationQueryOperatingBoundaryId FailedBoundary = new("boundary/context-failed");

    [Fact]
    public void Project_FirstFailureProducesOnePrimaryAndBlocksEveryUnexaminedRequirement()
    {
        var fixture = CreateFixture();
        var failure = new RelationQueryContextualBranchFailure(
            RelationQueryBoundAssessmentStatus.Unavailable,
            RelationQueryUnavailableReason.OperatingBoundaryInvalid,
            FailureCode,
            "The exact adapter binding cannot preserve the selected branch.",
            "Correct the failed binding evidence.",
            node: fixture.NodeMatchedRequirement.Origin!.Node,
            input: fixture.InputMatchedRequirement.Origin!.Input,
            missingCapabilityEvidence: [MissingEvidence],
            failedOperatingBoundary: FailedBoundary,
            failedConfigurationSetting: "field/missing");
        var selectorCalls = 0;

        var first = Project(fixture, _ =>
        {
            selectorCalls++;
            return failure;
        });
        var second = Project(fixture, _ => failure);

        Assert.Equal(fixture.Request.Branches.Length, selectorCalls);
        Assert.Equal(first.Select(AssessmentSignature), second.Select(AssessmentSignature));
        Assert.Equal(
            fixture.Request.Selection.Branches.SelectMany(static branch => branch.Requirements)
                .Select(static requirement => requirement.Id),
            first.Select(static assessment => assessment.Requirement));
        Assert.DoesNotContain(first, static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Available);

        var primary = Assert.Single(first, static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Unavailable);
        Assert.Equal(fixture.InputMatchedRequirement.Id, primary.Requirement);
        Assert.Equal(FailureCode, primary.AdapterDecisionCode);
        Assert.True(primary.MissingCapabilityEvidence.SequenceEqual([MissingEvidence]));
        Assert.Equal(FailedBoundary, primary.FailedOperatingBoundary);
        Assert.Equal("field/missing", primary.FailedConfigurationSetting);
        Assert.Equal("field/governing", primary.ConfigurationSetting);
        Assert.Empty(primary.CapabilityEvidence);
        Assert.Empty(primary.OperatingBoundaries);
        Assert.Empty(primary.PreservedGuarantees);

        var blocked = first.Where(static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Blocked).ToArray();
        Assert.Equal(first.Length - 1, blocked.Length);
        Assert.All(blocked, assessment =>
        {
            Assert.Equal(primary.Id, assessment.BlockedBy);
            Assert.Equal(FailureCode, assessment.AdapterDecisionCode);
            Assert.Equal(RelationQueryUnavailableReason.PrerequisiteBlocked, assessment.UnavailableReason);
            Assert.Equal(failure.Resolution, assessment.Resolution);
            Assert.Empty(assessment.CapabilityEvidence);
            Assert.Empty(assessment.OperatingBoundaries);
            Assert.Empty(assessment.PreservedGuarantees);
            Assert.Empty(assessment.MissingCapabilityEvidence);
            Assert.Null(assessment.FailedOperatingBoundary);
            Assert.Null(assessment.FailedConfigurationSetting);
        });
    }

    [Fact]
    public void Project_SuccessRetainsCompleteProfileDecisionProof()
    {
        var fixture = CreateFixture();
        var assessments = Project(fixture, static _ => null);
        var decisions = fixture.Request.ProfileFeasibility.Decisions
            .ToDictionary(static decision => decision.Requirement);

        Assert.All(assessments, assessment =>
        {
            Assert.Equal(RelationQueryBoundAssessmentStatus.Available, assessment.Status);
            var decision = decisions[assessment.Requirement];
            Assert.True(decision.GetCapabilityEvidence().SequenceEqual(assessment.CapabilityEvidence));
            Assert.True(decision.GetTargetEnforcedBoundaries().SequenceEqual(assessment.OperatingBoundaries));
            Assert.True(decision.GetPreservedGuarantees().SequenceEqual(assessment.PreservedGuarantees));
            Assert.NotEmpty(assessment.CapabilityEvidence);
            Assert.Null(assessment.AdapterDecisionCode);
            Assert.Null(assessment.BlockedBy);
        });
    }

    [Fact]
    public void Project_SelectsExplicitThenDecisionEvidenceBeforeStructuralSite()
    {
        var fixture = CreateFixture();
        var decisions = fixture.Request.ProfileFeasibility.Decisions
            .ToDictionary(static decision => decision.Requirement);
        var evidenceMatched = Assert.Single(
            decisions[fixture.EvidenceMatchedRequirement.Id].GetCapabilityEvidence());
        var byEvidence = new RelationQueryContextualBranchFailure(
            RelationQueryBoundAssessmentStatus.Invalid,
            RelationQueryUnavailableReason.CapabilityEvidenceInvalid,
            FailureCode,
            "Evidence failed.",
            "Correct the evidence.",
            input: fixture.InputMatchedRequirement.Origin!.Input,
            missingCapabilityEvidence: [evidenceMatched]);
        var explicitRequirement = new RelationQueryContextualBranchFailure(
            RelationQueryBoundAssessmentStatus.Invalid,
            RelationQueryUnavailableReason.CapabilityEvidenceInvalid,
            FailureCode,
            "Explicit decision failed.",
            "Correct the explicit decision.",
            input: fixture.InputMatchedRequirement.Origin!.Input,
            requirement: fixture.NodeMatchedRequirement.Id,
            missingCapabilityEvidence: [evidenceMatched]);

        var evidencePrimary = Assert.Single(Project(fixture, _ => byEvidence), static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Invalid);
        var explicitPrimary = Assert.Single(Project(fixture, _ => explicitRequirement), static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Invalid);

        Assert.Equal(fixture.EvidenceMatchedRequirement.Id, evidencePrimary.Requirement);
        Assert.Equal(fixture.NodeMatchedRequirement.Id, explicitPrimary.Requirement);
    }

    static ImmutableArray<RelationQueryBoundRequirementAssessment> Project(
        Fixture fixture,
        Func<RelationQueryNativeResultBranch, RelationQueryContextualBranchFailure?> selectFailure) =>
        RelationQueryContextualAssessmentProjector.Project(
            fixture.Request,
            EvidenceNamespace,
            selectFailure,
            static (_, requirement, failure) => new(
                RelationQueryConfigurationValueOrigin.AdapterConvention,
                "tests/contextual-assessment/v1",
                node: failure?.Node ?? requirement.Origin?.Node,
                input: failure?.Input ?? requirement.Origin?.Input,
                configurationSetting: failure is null ? null : "field/governing"));

    static object AssessmentSignature(RelationQueryBoundRequirementAssessment assessment) => new
    {
        assessment.Id,
        assessment.Branch,
        assessment.Requirement,
        assessment.Status,
        assessment.UnavailableReason,
        assessment.AdapterDecisionCode,
        assessment.BlockedBy,
        CapabilityEvidence = string.Join('|', assessment.CapabilityEvidence.Select(static item => item.Value)),
        Boundaries = string.Join('|', assessment.OperatingBoundaries.Select(static item => item.Value)),
        Guarantees = string.Join('|', assessment.PreservedGuarantees.Select(static item => (int)item)),
        MissingEvidence = string.Join('|', assessment.MissingCapabilityEvidence.Select(static item => item.Value)),
        assessment.FailedOperatingBoundary,
        assessment.FailedConfigurationSetting,
        assessment.ConfigurationSetting,
        assessment.Message,
        assessment.Resolution
    };

    static Fixture CreateFixture()
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(
            compilation.IsSuccessful,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var branch = Assert.Single(RelationQueryNativeCompilationRequest.CreateBranches(plan.ExecutionSlice));
        var fieldInput = plan.InputContract.Requirements.Inputs.OfType<RelationQueryFieldInput>().First();
        var otherNode = plan.ExecutionSlice.LogicalPlan.Nodes
            .Select(static node => node.Node)
            .First(node => node != fieldInput.Producer);

        var nodeMatched = new RelationQueryRealizationRequirement(
            new("requirement/a-node"),
            new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.FieldProjection),
            new(node: otherNode));
        var inputMatched = new RelationQueryRealizationRequirement(
            new("requirement/b-input"),
            new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.PredicateRead),
            new(input: fieldInput.Id, node: fieldInput.Producer));
        var evidenceMatched = new RelationQueryRealizationRequirement(
            new("requirement/c-evidence"),
            new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.StableSort),
            new(node: branch.Node));
        var planReference = RelationQueryCompiledPlanReference.From(plan);
        var profile = new RelationQueryTargetCapabilityProfile(
            new("target/contextual-assessment-tests"),
            new("target/contextual-assessment-tests/v1"),
            [planReference.DefinitionSchemaVersion],
            [planReference.CompilerProfile],
            capabilities:
            [
                new(new("evidence/field-projection"), nodeMatched.Capability),
                new(new("evidence/predicate-read"), inputMatched.Capability),
                new(new("evidence/stable-sort"), evidenceMatched.Capability)
            ]);
        var feasibility = RelationQueryRealizationCompiler.Match(
            planReference,
            [nodeMatched, inputMatched, evidenceMatched],
            profile,
            new(new("policy/contextual-assessment-tests/v1"), "conventions/contextual-assessment-tests/v1"));
        Assert.True(feasibility.IsRealizable);
        var placement = CreatePlacement(plan);
        return new(new(plan, feasibility, placement), nodeMatched, inputMatched, evidenceMatched);
    }

    static RelationQuerySourcePlacement CreatePlacement(CompiledRelationQueryPlan plan)
    {
        var source = Assert.Single(plan.InputContract.Sources);
        RelationQuerySourceInstanceId sourceId = new("source/contextual-assessment-tests");
        var sourceInstance = new RelationQuerySourceInstance(
            sourceId,
            new("domain/contextual-assessment-tests"),
            RelationQueryInMemoryInterpreter.DefaultTargetProfile,
            new(100, 1_000, 100, 4));
        var placement = new RelationQuerySourcePlacementBinding(
            new("placement/contextual-assessment-tests"),
            source.Input.Id,
            source.Node,
            source.Binding,
            source.Shape,
            sourceId,
            RelationQuerySourcePlacementBindingKind.SourceSet,
            RelationQuerySourceAcquisitionKind.Supplied,
            RelationQuerySourcePlacementOrigin.Explicit,
            fields:
            [
                .. source.Fields.Select(static field => new RelationQuerySourceFieldBinding(
                    field.Input.Id,
                    field.Input.Field.Path,
                    $"field/{Uri.EscapeDataString(field.Input.Id.Value)}"))
            ]);
        return new(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan),
            "tests/contextual-assessment-placement/v1",
            [sourceInstance],
            [placement]);
    }

    sealed record Fixture(
        RelationQueryBoundRealizationRequest Request,
        RelationQueryRealizationRequirement NodeMatchedRequirement,
        RelationQueryRealizationRequirement InputMatchedRequirement,
        RelationQueryRealizationRequirement EvidenceMatchedRequirement);
}
