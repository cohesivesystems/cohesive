using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Explain;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryCapabilitySummaryProjectorTests
{
    static readonly RelationQueryOperatingBoundaryId Boundary = new("boundary/test");
    static readonly RelationQueryRealizationRequirementId FilterRequirement = new("requirement/filter");
    static readonly RelationQueryRealizationRequirementId ProjectionRequirement = new("requirement/projection");

    [Fact]
    public void Project_ProfilePreservesEveryCanonicalCapabilityVariantInCanonicalOrder()
    {
        RelationQueryCapability[] capabilities =
        [
            new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.FieldProjection),
            new OperatingBoundaryValidationRelationQueryCapability(Boundary),
            new GuaranteeRelationQueryCapability(RelationQueryGuaranteeCapabilityKind.DeterministicResult),
            new StructuralRelationQueryCapability(
                RelationQueryStructuralCapabilityRole.BindingRead,
                RelationQueryStructuralPathKind.NestedField),
            new TemporalRelationQueryCapability(RelationQueryTemporalExecutionCapability.IntervalOverlap),
            new ExpressionRelationQueryCapability(ExprCapabilities.Field, ExprCapabilityRequirementKind.Operation),
            new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter)
        ];
        var profile = Profile(capabilities);

        var summary = RelationQueryCapabilitySummaryProjector.Project(profile);

        Assert.Equal(profile.Target, summary.Target);
        Assert.Equal(profile.Id, summary.TargetProfile);
        Assert.Null(summary.Policy);
        Assert.Null(summary.ProfileFeasibility);
        Assert.Null(summary.BoundRealization);
        Assert.Equal(Boundary, Assert.Single(summary.OperatingBoundaries));
        Assert.Collection(
            summary.Entries,
            static entry => Assert.IsType<LogicalRelationQueryCapability>(entry.Capability),
            static entry => Assert.IsType<ExpressionRelationQueryCapability>(entry.Capability),
            static entry => Assert.IsType<TemporalRelationQueryCapability>(entry.Capability),
            static entry => Assert.IsType<StructuralRelationQueryCapability>(entry.Capability),
            static entry => Assert.IsType<GuaranteeRelationQueryCapability>(entry.Capability),
            static entry => Assert.IsType<OperatingBoundaryValidationRelationQueryCapability>(entry.Capability),
            static entry => Assert.IsType<PrimitiveRelationQueryCapability>(entry.Capability));

        var evidence = profile.Capabilities.Select(static item => item.Id).ToHashSet();
        Assert.All(summary.Entries, entry =>
        {
            Assert.Empty(entry.Requirements);
            Assert.Empty(entry.MissingForRequirements);
            Assert.Empty(entry.ContextEvidence);
            Assert.All(entry.CapabilityEvidence, item => Assert.Contains(item, evidence));
        });
    }

    [Fact]
    public void Project_ProfileReportIndexesDemandedAndMissingCapabilitiesWithResolvableEvidence()
    {
        var filter = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter);
        var missing = new TemporalRelationQueryCapability(RelationQueryTemporalExecutionCapability.IntervalOverlap);
        var profile = Profile([filter]);
        var policy = new RelationQueryRealizationPolicy(
            new("policy/capability-summary/v1"),
            "conventions/capability-summary/v1");
        var report = RelationQueryRealizationCompiler.Match(
            PlanReference(),
            [
                new(FilterRequirement, filter),
                new(ProjectionRequirement, missing)
            ],
            profile,
            policy);
        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, report.Status);

        var summary = RelationQueryCapabilitySummaryProjector.Project(report);

        Assert.Equal(policy.Id, summary.Policy);
        Assert.Equal(report.Fingerprint, summary.ProfileFeasibility);
        Assert.Null(summary.BoundRealization);
        var filterEntry = Assert.Single(summary.Entries, entry => entry.Capability == filter);
        Assert.Equal(FilterRequirement, Assert.Single(filterEntry.Requirements));
        Assert.Empty(filterEntry.MissingForRequirements);
        Assert.Equal(
            profile.Capabilities.Where(item => item.Capability == filter).Select(static item => item.Id),
            filterEntry.CapabilityEvidence);
        var missingEntry = Assert.Single(summary.Entries, entry => entry.Capability == missing);
        Assert.Equal(ProjectionRequirement, Assert.Single(missingEntry.Requirements));
        Assert.Equal(ProjectionRequirement, Assert.Single(missingEntry.MissingForRequirements));
        Assert.Empty(missingEntry.CapabilityEvidence);

        var knownEvidence = profile.Capabilities.Select(static item => item.Id).ToHashSet();
        Assert.All(
            summary.Entries.SelectMany(static entry => entry.CapabilityEvidence),
            evidence => Assert.Contains(evidence, knownEvidence));
    }

    [Fact]
    public void Project_BoundReportIndexesContextWithoutCollapsingUnavailableAndBlockedAssessments()
    {
        var filter = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter);
        var projection = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Projection);
        var profile = Profile([projection, filter]);
        var policy = new RelationQueryRealizationPolicy(
            new("policy/capability-summary-bound/v1"),
            "conventions/capability-summary-bound/v1");
        var feasibility = RelationQueryRealizationCompiler.Match(
            PlanReference(),
            [
                new(FilterRequirement, filter),
                new(ProjectionRequirement, projection)
            ],
            profile,
            policy);
        Assert.True(feasibility.IsRealizable);

        RelationQueryNativeResultBranchId branch = new("branch/rows");
        RelationQueryContextEvidenceId unavailableId = new("context/filter");
        RelationQueryContextEvidenceId blockedId = new("context/projection");
        RelationQueryAdapterDecisionCode unavailableCode = new("tests/unavailable");
        RelationQueryAdapterDecisionCode blockedCode = new("tests/blocked");
        var unavailable = new RelationQueryBoundRequirementAssessment(
            unavailableId,
            branch,
            FilterRequirement,
            RelationQueryBoundAssessmentStatus.Unavailable,
            EffectiveConfigurationOrigin.AdapterConvention,
            "tests/capability-summary/v1",
            unavailableReason: RelationQueryUnavailableReason.CapabilityNotAdvertised,
            message: "The binding cannot realize the filter.",
            resolution: "Bind a source that supports filtering.",
            adapterDecisionCode: unavailableCode);
        var blocked = new RelationQueryBoundRequirementAssessment(
            blockedId,
            branch,
            ProjectionRequirement,
            RelationQueryBoundAssessmentStatus.Blocked,
            EffectiveConfigurationOrigin.AdapterConvention,
            "tests/capability-summary/v1",
            unavailableReason: RelationQueryUnavailableReason.PrerequisiteBlocked,
            message: "Projection was not examined after filter failure.",
            resolution: "Resolve the filter failure first.",
            adapterDecisionCode: blockedCode,
            blockedBy: unavailableId);
        var evidence = new RelationQueryContextualEvidenceProjection(
            BindingReference(profile),
            [blocked, unavailable]);
        RelationQuerySourcePlacementFingerprint placement = new(
            "sha256",
            "tests/placement/v1-c14n/v1",
            new string('c', 64));
        ImmutableArray<RelationQueryNativeResultBranchId> branches = [branch];
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics = [];
        var fingerprint = RelationQueryBoundRealizationFingerprinter.Compute(
            feasibility,
            placement,
            branches,
            evidence,
            diagnostics,
            RelationQueryRealizationStatus.NotRealizable);
        var bound = new RelationQueryBoundRealizationReport(
            feasibility,
            placement,
            branches,
            evidence,
            diagnostics,
            RelationQueryRealizationStatus.NotRealizable,
            fingerprint);

        var summary = RelationQueryCapabilitySummaryProjector.Project(bound);

        Assert.Equal(feasibility.Fingerprint, summary.ProfileFeasibility);
        Assert.Equal(bound.Fingerprint, summary.BoundRealization);
        Assert.Equal(
            unavailableId,
            Assert.Single(Assert.Single(summary.Entries, entry => entry.Capability == filter).ContextEvidence));
        Assert.Equal(
            blockedId,
            Assert.Single(Assert.Single(summary.Entries, entry => entry.Capability == projection).ContextEvidence));
        var resolvable = bound.Evidence.Assessments.Select(static assessment => assessment.Id).ToHashSet();
        Assert.All(
            summary.Entries.SelectMany(static entry => entry.ContextEvidence),
            context => Assert.Contains(context, resolvable));
        Assert.Equal(RelationQueryBoundAssessmentStatus.Unavailable, unavailable.Status);
        Assert.Equal(RelationQueryBoundAssessmentStatus.Blocked, blocked.Status);
    }

    [Fact]
    public void Projection_IsDeterministicAcrossEquivalentProfileDeclarationOrder()
    {
        RelationQueryCapability[] capabilities =
        [
            new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter),
            new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.FieldProjection),
            new GuaranteeRelationQueryCapability(RelationQueryGuaranteeCapabilityKind.DeterministicResult)
        ];
        var first = Profile(capabilities);
        var second = Profile([.. capabilities.Reverse()]);

        var firstSummary = RelationQueryCapabilitySummaryProjector.Project(first);
        var secondSummary = RelationQueryCapabilitySummaryProjector.Project(second);
        var firstJson = JsonSerializer.Serialize(
            firstSummary,
            RelationQueryJsonSerializer.CreateOptions());
        var secondJson = JsonSerializer.Serialize(
            secondSummary,
            RelationQueryJsonSerializer.CreateOptions());

        Assert.True(firstSummary.HasSameSemantics(secondSummary));
        Assert.Equal(firstJson, secondJson);
    }

    [Fact]
    public void Constructors_RejectRepeatedReferencesAndUndeclaredEntryBoundaries()
    {
        var capability = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter);

        Assert.Throws<ArgumentException>(() => new RelationQueryCapabilitySummaryEntry(
            capability,
            requirements: [FilterRequirement, FilterRequirement]));
        var entry = new RelationQueryCapabilitySummaryEntry(
            capability,
            operatingBoundaries: [Boundary]);
        Assert.Throws<ArgumentException>(() => new RelationQueryCapabilitySummary(
            new("target/capability-summary"),
            new("target/capability-summary/v1"),
            entries: [entry]));
    }

    static RelationQueryTargetCapabilityProfile Profile(IEnumerable<RelationQueryCapability> capabilities)
    {
        var evidence = capabilities
            .Select(capability => new RelationQueryTargetCapabilityEvidence(
                new($"evidence/{Uri.EscapeDataString(RelationQueryRealizationOrdering.CapabilityKey(capability))}"),
                capability))
            .ToImmutableArray();
        return new(
            new("target/capability-summary"),
            new("target/capability-summary/v1"),
            ["relation-query/v1"],
            ["compiler/v1"],
            evidence,
            [new(Boundary, RelationQueryOperatingBoundaryKind.SingleSource)]);
    }

    static RelationQueryCompiledPlanReference PlanReference()
    {
        RelationQueryPlanComponentFingerprint component = new(
            "sha256",
            "tests/component/v1-c14n/v1",
            new string('a', 64));
        return new(
            "compiler/v1",
            "relation-query/v1",
            new("sha256", "tests/definition/v1-c14n/v1", new string('b', 64)),
            component,
            relationshipCatalogFingerprint: null,
            component,
            [new("input/root")]);
    }

    static RelationQueryAdapterBindingReference BindingReference(RelationQueryTargetCapabilityProfile profile) =>
        new(
            "tests/binding/v1",
            "binding/capability-summary",
            profile.Target,
            profile.Id,
            new(
                "sha256",
                "tests/binding/v1-c14n/v1",
                new string('d', 64)));
}
