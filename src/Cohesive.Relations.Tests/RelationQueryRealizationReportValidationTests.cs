using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryRealizationReportValidationTests
{
    static readonly LogicalRelationQueryCapability Join = new(RelationQueryLogicalCapabilityKind.Join);
    static readonly LogicalRelationQueryCapability Filter = new(RelationQueryLogicalCapabilityKind.Filter);
    static readonly PrimitiveRelationQueryCapability KeyExtraction = new(
        RelationQueryPrimitiveCapabilityKind.KeyExtraction);
    static readonly PrimitiveRelationQueryCapability BatchedLookup = new(
        RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup);

    [Fact]
    public void Constructor_RejectsNativeEvidenceForADifferentCapability()
    {
        var plan = Plan();
        var requirement = Requirement(Join);
        var evidence = Evidence("evidence/filter", Filter);
        var profile = Profile(plan, [evidence]);
        var decision = new NativeRelationQueryRealizationDecision(requirement.Id, [evidence.Id]);

        var exception = Assert.Throws<ArgumentException>(() => Report(
            plan,
            profile,
            Policy(),
            requirement,
            decision));

        Assert.Equal("decisions", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsACompositionThatDoesNotCloseOverEveryRequiredCapability()
    {
        var plan = Plan();
        var requirement = Requirement(Join);
        var rule = new RelationQueryCompositionRule(
            new("rule/join/v1"),
            Join,
            [KeyExtraction, BatchedLookup]);
        var evidence = Evidence("evidence/key", KeyExtraction);
        var profile = Profile(plan, [evidence]);
        var policy = Policy(rules: [rule]);
        var decision = new ComposedRelationQueryRealizationDecision(
            requirement.Id,
            [rule.Id],
            [evidence.Id]);

        var exception = Assert.Throws<ArgumentException>(() => Report(
            plan,
            profile,
            policy,
            requirement,
            decision));

        Assert.Equal("decisions", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsSeveralRulesForOneCapabilityInsideAProof()
    {
        var plan = Plan();
        var requirement = Requirement(Join);
        var traversal = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.RelationshipTraversal);
        var rootRule = new RelationQueryCompositionRule(new("rule/join/v1"), Join, [traversal]);
        var firstTraversalRule = new RelationQueryCompositionRule(
            new("rule/traversal-key/v1"),
            traversal,
            [KeyExtraction]);
        var secondTraversalRule = new RelationQueryCompositionRule(
            new("rule/traversal-batch/v1"),
            traversal,
            [BatchedLookup]);
        var keyEvidence = Evidence("evidence/key", KeyExtraction);
        var batchEvidence = Evidence("evidence/batch", BatchedLookup);
        var profile = Profile(plan, [keyEvidence, batchEvidence]);
        var policy = Policy(rules: [rootRule, firstTraversalRule, secondTraversalRule]);
        var decision = new ComposedRelationQueryRealizationDecision(
            requirement.Id,
            [rootRule.Id, firstTraversalRule.Id, secondTraversalRule.Id],
            [keyEvidence.Id, batchEvidence.Id]);

        var exception = Assert.Throws<ArgumentException>(() => Report(
            plan,
            profile,
            policy,
            requirement,
            decision));

        Assert.Equal("decisions", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsAStaticBoundaryValidationWithAForgedMeasurement()
    {
        var plan = Plan();
        RelationQueryOperatingBoundaryId boundaryId = new("boundary/max-page-size");
        var boundary = new RelationQueryOperatingBoundary(
            boundaryId,
            RelationQueryOperatingBoundaryKind.MaximumPageSize,
            limit: 10);
        var requirement = new RelationQueryRealizationRequirement(
            new("requirement/join"),
            Join,
            staticFacts: [new(RelationQueryRealizationStaticFactKind.PageSize, 8)]);
        var evidence = Evidence("evidence/join", Join, [boundaryId]);
        var profile = Profile(plan, [evidence], [boundary]);
        var policy = Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated);
        var decision = new ConstrainedRelationQueryRealizationDecision(
            requirement.Id,
            [evidence.Id],
            [new(boundaryId, RelationQueryOperatingBoundaryValidationKind.StaticPlanFact, measuredValue: 7)]);

        var exception = Assert.Throws<ArgumentException>(() => Report(
            plan,
            profile,
            policy,
            requirement,
            decision));

        Assert.Equal("decisions", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsTargetEnforcementEvidenceForADifferentBoundary()
    {
        var plan = Plan();
        RelationQueryOperatingBoundaryId requiredBoundaryId = new("boundary/required");
        RelationQueryOperatingBoundaryId otherBoundaryId = new("boundary/other");
        var requiredBoundary = new RelationQueryOperatingBoundary(
            requiredBoundaryId,
            RelationQueryOperatingBoundaryKind.MaterializedInputs);
        var otherBoundary = new RelationQueryOperatingBoundary(
            otherBoundaryId,
            RelationQueryOperatingBoundaryKind.SingleSource);
        var requirement = Requirement(Join);
        var joinEvidence = Evidence("evidence/join", Join, [requiredBoundaryId]);
        var validatorEvidence = Evidence(
            "evidence/other-boundary-validator",
            new OperatingBoundaryValidationRelationQueryCapability(otherBoundaryId));
        var profile = Profile(
            plan,
            [joinEvidence, validatorEvidence],
            [requiredBoundary, otherBoundary]);
        var policy = Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated);
        var decision = new ConstrainedRelationQueryRealizationDecision(
            requirement.Id,
            [joinEvidence.Id, validatorEvidence.Id],
            [new(
                requiredBoundaryId,
                RelationQueryOperatingBoundaryValidationKind.TargetEnforced,
                validatorEvidence.Id)]);

        var exception = Assert.Throws<ArgumentException>(() => Report(
            plan,
            profile,
            policy,
            requirement,
            decision));

        Assert.Equal("decisions", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsAStaleOverrideCapability()
    {
        var plan = Plan();
        var requirement = Requirement(Join);
        var declared = new RelationQueryRealizationOverride(
            new("override/join"),
            requirement.Id,
            Filter,
            justification: "Application-owned implementation.");
        var policy = Policy(overrides: [declared]);
        var decision = new OverrideRelationQueryRealizationDecision(requirement.Id, declared.Id);

        var exception = Assert.Throws<ArgumentException>(() => Report(
            plan,
            Profile(plan, []),
            policy,
            requirement,
            decision));

        Assert.Equal("decisions", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsAnAvailableTargetDecisionThatBypassesAnExplicitOverride()
    {
        var plan = Plan();
        var requirement = Requirement(Join);
        var evidence = Evidence("evidence/join", Join);
        var declared = new RelationQueryRealizationOverride(
            new("override/join"),
            requirement.Id,
            Join,
            justification: "Application-owned implementation.");
        var decision = new NativeRelationQueryRealizationDecision(requirement.Id, [evidence.Id]);

        var exception = Assert.Throws<ArgumentException>(() => Report(
            plan,
            Profile(plan, [evidence]),
            Policy(overrides: [declared]),
            requirement,
            decision));

        Assert.Equal("decisions", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsAnEmptyRequirementAndDecisionSet()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RelationQueryRealizationReport(
            Plan(),
            Profile(Plan(), []),
            Policy(),
            [],
            [],
            [],
            RelationQueryRealizationStatus.Realizable,
            new(
                RelationQueryRealizationFingerprinter.Algorithm,
                RelationQueryRealizationFingerprinter.Canonicalization,
                "unused")));

        Assert.Equal("requirements", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsAnAvailableDecisionForAnInvalidTargetProfile()
    {
        var plan = Plan();
        var requirement = Requirement(Join);
        RelationQueryTargetCapabilityEvidenceId conflictId = new("evidence/conflict");
        var profile = Profile(
            plan,
            [
                new(conflictId, Join),
                new(conflictId, Filter)
            ]);
        var decision = new NativeRelationQueryRealizationDecision(requirement.Id, [conflictId]);
        var diagnostic = new RelationQueryRealizationDiagnostic(
            RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceConflict,
            DiagnosticSeverity.Error,
            "Conflicting target evidence.",
            capabilityEvidence: conflictId);
        ImmutableArray<RelationQueryRealizationRequirement> requirements = [requirement];
        ImmutableArray<RelationQueryRealizationDecision> decisions = [decision];
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics = [diagnostic];
        var fingerprint = RelationQueryRealizationFingerprinter.Compute(
            plan,
            profile,
            Policy(),
            requirements,
            decisions,
            diagnostics,
            RelationQueryRealizationStatus.Invalid);

        var exception = Assert.Throws<ArgumentException>(() => new RelationQueryRealizationReport(
            plan,
            profile,
            Policy(),
            requirements,
            decisions,
            diagnostics,
            RelationQueryRealizationStatus.Invalid,
            fingerprint));

        Assert.Equal("decisions", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsAFingerprintThatDoesNotMatchNormalizedContent()
    {
        var plan = Plan();
        var requirement = Requirement(Join);
        var evidence = Evidence("evidence/join", Join);
        var profile = Profile(plan, [evidence]);
        var policy = Policy();
        var decision = new NativeRelationQueryRealizationDecision(requirement.Id, [evidence.Id]);
        var fingerprint = new RelationQueryRealizationFingerprint(
            RelationQueryRealizationFingerprinter.Algorithm,
            RelationQueryRealizationFingerprinter.Canonicalization,
            "not-the-content-fingerprint");

        var exception = Assert.Throws<ArgumentException>(() => new RelationQueryRealizationReport(
            plan,
            profile,
            policy,
            [requirement],
            [decision],
            [],
            RelationQueryRealizationStatus.Realizable,
            fingerprint));

        Assert.Equal("fingerprint", exception.ParamName);
    }

    static RelationQueryRealizationReport Report(
        RelationQueryCompiledPlanReference plan,
        RelationQueryTargetCapabilityProfile profile,
        RelationQueryRealizationPolicy policy,
        RelationQueryRealizationRequirement requirement,
        RelationQueryRealizationDecision decision)
    {
        ImmutableArray<RelationQueryRealizationRequirement> requirements = [requirement];
        ImmutableArray<RelationQueryRealizationDecision> decisions = [decision];
        var fingerprint = RelationQueryRealizationFingerprinter.Compute(
            plan,
            profile,
            policy,
            requirements,
            decisions,
            [],
            RelationQueryRealizationStatus.Realizable);
        return new(
            plan,
            profile,
            policy,
            requirements,
            decisions,
            [],
            RelationQueryRealizationStatus.Realizable,
            fingerprint);
    }

    static RelationQueryRealizationRequirement Requirement(RelationQueryCapability capability) =>
        new(new("requirement/join"), capability);

    static RelationQueryTargetCapabilityEvidence Evidence(
        string id,
        RelationQueryCapability capability,
        ImmutableArray<RelationQueryOperatingBoundaryId> boundaries = default) =>
        new(new(id), capability, boundaries);

    static RelationQueryTargetCapabilityProfile Profile(
        RelationQueryCompiledPlanReference plan,
        ImmutableArray<RelationQueryTargetCapabilityEvidence> evidence,
        ImmutableArray<RelationQueryOperatingBoundary> boundaries = default) =>
        new(
            new("target/test"),
            new("target/test/v1"),
            [plan.DefinitionSchemaVersion],
            [plan.CompilerProfile],
            evidence,
            boundaries);

    static RelationQueryRealizationPolicy Policy(
        RelationQueryConstrainedRealizationPolicy constrained = RelationQueryConstrainedRealizationPolicy.Reject,
        ImmutableArray<RelationQueryCompositionRule> rules = default,
        ImmutableArray<RelationQueryRealizationOverride> overrides = default) =>
        new(
            new("policy/test/v1"),
            "conventions/test/v1",
            constrainedRealizations: constrained,
            compositionRules: rules,
            overrides: overrides);

    static RelationQueryCompiledPlanReference Plan() =>
        new(
            "compiler/test/v1",
            "relation-query/test/v1",
            new("sha256", "relation-query-definition/test/v1", "definition"),
            new("sha256", "relation-query-plan-shapes/test/v1", "shapes"),
            null,
            new("sha256", "relation-query-plan-demand/test/v1", "demand"),
            [new("input/root")]);
}
