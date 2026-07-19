using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryBoundRequirementAssessmentTests
{
    static readonly RelationQueryNativeResultBranchId Branch = new("branch/loads");
    static readonly RelationQueryAdapterDecisionCode DecisionCode = new("COSMOS-BOUND-001");
    static readonly RelationQueryTargetCapabilityEvidenceId EvidenceA = new("evidence/a");
    static readonly RelationQueryTargetCapabilityEvidenceId EvidenceZ = new("evidence/z");
    static readonly RelationQueryOperatingBoundaryId Boundary = new("boundary/single-container");

    [Fact]
    public void Constructor_NormalizesAndRoundTripsStructuredFailureEvidence()
    {
        var assessment = CreateFailure(
            RelationQueryBoundAssessmentStatus.Unavailable,
            missingCapabilityEvidence: [EvidenceZ, EvidenceA],
            failedOperatingBoundary: Boundary,
            failedConfigurationSetting: "field/status");

        Assert.Equal(DecisionCode, assessment.AdapterDecisionCode);
        Assert.True(assessment.MissingCapabilityEvidence.SequenceEqual([EvidenceA, EvidenceZ]));
        Assert.Equal(Boundary, assessment.FailedOperatingBoundary);
        Assert.Equal("field/status", assessment.FailedConfigurationSetting);

        var options = RelationQueryJsonSerializer.CreateOptions();
        var json = JsonSerializer.Serialize(assessment, options);
        var roundTrip = JsonSerializer.Deserialize<RelationQueryBoundRequirementAssessment>(json, options);

        Assert.NotNull(roundTrip);
        Assert.Equal(assessment.AdapterDecisionCode, roundTrip.AdapterDecisionCode);
        Assert.True(assessment.MissingCapabilityEvidence.SequenceEqual(roundTrip.MissingCapabilityEvidence));
        Assert.Equal(assessment.FailedOperatingBoundary, roundTrip.FailedOperatingBoundary);
        Assert.Equal(assessment.FailedConfigurationSetting, roundTrip.FailedConfigurationSetting);
        Assert.Equal(json, JsonSerializer.Serialize(roundTrip, options));
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("resolution")]
    [InlineData("decision-code")]
    [InlineData("missing-evidence")]
    [InlineData("failed-boundary")]
    [InlineData("failed-setting")]
    [InlineData("blocking-decision")]
    public void AvailableAssessment_RejectsFailureMetadata(string metadata)
    {
        Assert.Throws<ArgumentException>(() => new RelationQueryBoundRequirementAssessment(
            new("assessment/available"),
            Branch,
            new("requirement/available"),
            RelationQueryBoundAssessmentStatus.Available,
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            "tests/adapter",
            unavailableReason: metadata == "reason" ? RelationQueryUnavailableReason.PolicyRejected : null,
            resolution: metadata == "resolution" ? "Change the binding." : null,
            adapterDecisionCode: metadata == "decision-code" ? DecisionCode : null,
            missingCapabilityEvidence: metadata == "missing-evidence" ? [EvidenceA] : [],
            failedOperatingBoundary: metadata == "failed-boundary" ? Boundary : null,
            failedConfigurationSetting: metadata == "failed-setting" ? "field/status" : null,
            blockedBy: metadata == "blocking-decision" ? new("assessment/failure") : null));
    }

    [Theory]
    [InlineData(RelationQueryBoundAssessmentStatus.Unavailable)]
    [InlineData(RelationQueryBoundAssessmentStatus.Invalid)]
    public void ExaminedFailure_RequiresOwnFailureContractAndStableAdapterCode(
        RelationQueryBoundAssessmentStatus status)
    {
        Assert.Throws<ArgumentException>(() => new RelationQueryBoundRequirementAssessment(
            new("assessment/failure"),
            Branch,
            new("requirement/failure"),
            status,
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            "tests/adapter",
            unavailableReason: RelationQueryUnavailableReason.PolicyRejected,
            resolution: "Change the binding."));

        Assert.Throws<ArgumentException>(() => new RelationQueryBoundRequirementAssessment(
            new("assessment/failure"),
            Branch,
            new("requirement/failure"),
            status,
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            "tests/adapter",
            unavailableReason: RelationQueryUnavailableReason.PrerequisiteBlocked,
            resolution: "Correct the prerequisite decision.",
            adapterDecisionCode: DecisionCode));
    }

    [Fact]
    public void BlockedAssessment_ReferencesTheActualPriorDecisionIndependentOfCanonicalOrder()
    {
        var failure = CreateFailure(
            RelationQueryBoundAssessmentStatus.Unavailable,
            id: new("assessment/z-failure"),
            requirement: new("requirement/z-failure"));
        var blocked = CreateBlocked(
            blockedBy: failure.Id,
            id: new("assessment/a-blocked"),
            requirement: new("requirement/a-blocked"));

        RelationQueryContextualEvidenceProjection projection = new(
            CreateBindingReference(),
            [failure, blocked]);

        Assert.Equal([blocked.Id, failure.Id], projection.Assessments.Select(static item => item.Id));
        Assert.Equal(failure.Id, projection.Assessments[0].BlockedBy);

        var options = RelationQueryJsonSerializer.CreateOptions();
        var json = JsonSerializer.Serialize(projection, options);
        var roundTrip = JsonSerializer.Deserialize<RelationQueryContextualEvidenceProjection>(json, options);

        Assert.NotNull(roundTrip);
        Assert.Equal([blocked.Id, failure.Id], roundTrip.Assessments.Select(static item => item.Id));
        Assert.Equal(json, JsonSerializer.Serialize(roundTrip, options));
    }

    [Fact]
    public void BlockedAssessment_CannotClaimExaminedProofOrDirectFailureFacts()
    {
        var blockingId = new RelationQueryContextEvidenceId("assessment/failure");

        Assert.Throws<ArgumentException>(() => CreateBlocked(
            blockingId,
            capabilityEvidence: [EvidenceA]));
        Assert.Throws<ArgumentException>(() => CreateBlocked(
            blockingId,
            operatingBoundaries: [Boundary]));
        Assert.Throws<ArgumentException>(() => CreateBlocked(
            blockingId,
            preservedGuarantees: [RelationQueryGuaranteeCapabilityKind.JoinMembership]));
        Assert.Throws<ArgumentException>(() => CreateBlocked(
            blockingId,
            missingCapabilityEvidence: [EvidenceA]));
        Assert.Throws<ArgumentException>(() => CreateBlocked(
            blockingId,
            failedOperatingBoundary: Boundary));
        Assert.Throws<ArgumentException>(() => CreateBlocked(
            blockingId,
            failedConfigurationSetting: "field/status"));
    }

    [Fact]
    public void Projection_RejectsBlockedAssessmentWithoutMatchingFailedDecisionOnSameBranch()
    {
        var failure = CreateFailure(
            RelationQueryBoundAssessmentStatus.Unavailable,
            id: new("assessment/failure"));
        var blocked = CreateBlocked(blockedBy: failure.Id);
        var binding = CreateBindingReference();

        Assert.Throws<ArgumentException>(() => new RelationQueryContextualEvidenceProjection(binding, [blocked]));

        var otherBranchFailure = CreateFailure(
            RelationQueryBoundAssessmentStatus.Invalid,
            id: failure.Id,
            branch: new("branch/customers"));
        Assert.Throws<ArgumentException>(() => new RelationQueryContextualEvidenceProjection(
            binding,
            [otherBranchFailure, blocked]));

        var available = CreateAvailable(id: failure.Id);
        Assert.Throws<ArgumentException>(() => new RelationQueryContextualEvidenceProjection(
            binding,
            [available, blocked]));
    }

    static RelationQueryBoundRequirementAssessment CreateAvailable(
        RelationQueryContextEvidenceId? id = null) =>
        new(
            id ?? new("assessment/available"),
            Branch,
            new("requirement/available"),
            RelationQueryBoundAssessmentStatus.Available,
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            "tests/adapter");

    static RelationQueryBoundRequirementAssessment CreateFailure(
        RelationQueryBoundAssessmentStatus status,
        RelationQueryContextEvidenceId? id = null,
        RelationQueryNativeResultBranchId? branch = null,
        RelationQueryRealizationRequirementId? requirement = null,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> missingCapabilityEvidence = default,
        RelationQueryOperatingBoundaryId? failedOperatingBoundary = null,
        string? failedConfigurationSetting = null) =>
        new(
            id ?? new("assessment/failure"),
            branch ?? Branch,
            requirement ?? new("requirement/failure"),
            status,
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            "tests/adapter",
            unavailableReason: RelationQueryUnavailableReason.CapabilityEvidenceInvalid,
            message: "The adapter could not prove the required capability.",
            resolution: "Supply exact capability evidence.",
            adapterDecisionCode: DecisionCode,
            missingCapabilityEvidence: missingCapabilityEvidence,
            failedOperatingBoundary: failedOperatingBoundary,
            failedConfigurationSetting: failedConfigurationSetting);

    static RelationQueryBoundRequirementAssessment CreateBlocked(
        RelationQueryContextEvidenceId blockedBy,
        RelationQueryContextEvidenceId? id = null,
        RelationQueryRealizationRequirementId? requirement = null,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> capabilityEvidence = default,
        ImmutableArray<RelationQueryOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees = default,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> missingCapabilityEvidence = default,
        RelationQueryOperatingBoundaryId? failedOperatingBoundary = null,
        string? failedConfigurationSetting = null) =>
        new(
            id ?? new("assessment/blocked"),
            Branch,
            requirement ?? new("requirement/blocked"),
            RelationQueryBoundAssessmentStatus.Blocked,
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            "tests/adapter",
            capabilityEvidence,
            operatingBoundaries,
            preservedGuarantees,
            unavailableReason: RelationQueryUnavailableReason.PrerequisiteBlocked,
            message: "The adapter did not examine this requirement after a prerequisite failure.",
            resolution: "Correct the prerequisite adapter decision and realize the binding again.",
            adapterDecisionCode: new("CONTEXT-BLOCKED"),
            missingCapabilityEvidence: missingCapabilityEvidence,
            failedOperatingBoundary: failedOperatingBoundary,
            failedConfigurationSetting: failedConfigurationSetting,
            blockedBy: blockedBy);

    static RelationQueryAdapterBindingReference CreateBindingReference() =>
        new(
            "relation-query-adapter-binding/v1",
            "tests/binding",
            new("tests"),
            new("tests/profile/v1"),
            new("sha256", "tests/v1", new string('a', 64)));
}
