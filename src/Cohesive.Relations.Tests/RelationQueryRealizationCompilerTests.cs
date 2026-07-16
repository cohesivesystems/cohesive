using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryRealizationCompilerTests
{
    static readonly LogicalRelationQueryCapability Join = new(RelationQueryLogicalCapabilityKind.Join);
    static readonly PrimitiveRelationQueryCapability KeyExtraction = new(
        RelationQueryPrimitiveCapabilityKind.KeyExtraction);
    static readonly PrimitiveRelationQueryCapability BatchedLookup = new(
        RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup);

    [Fact]
    public void Match_SelectsNativeEvidenceAndIsIndependentOfDeclarationOrder()
    {
        var plan = PlanReference();
        var firstRequirement = Requirement("requirement/filter", new LogicalRelationQueryCapability(
            RelationQueryLogicalCapabilityKind.Filter));
        var secondRequirement = Requirement("requirement/join", Join);
        var filterEvidence = Evidence("evidence/filter", firstRequirement.Capability);
        var joinEvidence = Evidence("evidence/join", Join);

        var first = RelationQueryRealizationCompiler.Match(
            plan,
            [secondRequirement, firstRequirement],
            Profile(plan, [joinEvidence, filterEvidence]),
            Policy());
        var second = RelationQueryRealizationCompiler.Match(
            plan,
            [firstRequirement, secondRequirement],
            Profile(plan, [filterEvidence, joinEvidence]),
            Policy());

        Assert.True(first.IsRealizable);
        Assert.All(first.Decisions, static decision =>
            Assert.IsType<NativeRelationQueryRealizationDecision>(decision));
        Assert.Equal(
            ["requirement/filter", "requirement/join"],
            first.Requirements.Select(static requirement => requirement.Id.Value).ToArray());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Match_ReportsOneUnavailableDecisionForAnUnadvertisedRequirement()
    {
        var plan = PlanReference();
        var requirement = Requirement("requirement/join", Join);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            Profile(plan, []),
            Policy());

        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, report.Status);
        var decision = Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Equal(RelationQueryUnavailableReason.CapabilityNotAdvertised, decision.Reason);
        Assert.Equal(Join, Assert.Single(decision.MissingCapabilities));
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal(RelationQueryRealizationDiagnosticCodes.RequirementUnavailable, diagnostic.Code);
        Assert.Equal(requirement.Id, diagnostic.Requirement);
    }

    [Fact]
    public void Compile_NotRequestedCanRealizeAggregateValuesWithoutOccurrenceCapabilities()
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(compilation.IsSuccessful);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var planReference = RelationQueryCompiledPlanReference.From(plan);
        var valueRequirements = RelationQueryRealizationRequirementProjector.Project(
            plan,
            RelationQueryResultObservability.NotRequested);
        var evidence = valueRequirements
            .Select(static requirement => requirement.Capability)
            .Distinct()
            .OrderBy(RelationQueryRealizationOrdering.CapabilityKey, StringComparer.Ordinal)
            .Select((capability, index) => Evidence($"evidence/{index:D4}", capability))
            .ToImmutableArray();
        var profile = Profile(planReference, evidence);

        var strict = RelationQueryRealizationCompiler.Compile(plan, profile, Policy());
        var valuesOnly = RelationQueryRealizationCompiler.Compile(
            plan,
            profile,
            Policy(),
            RelationQueryResultObservability.NotRequested);

        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, strict.Status);
        Assert.Contains(
            strict.Decisions.OfType<UnavailableRelationQueryRealizationDecision>()
                .SelectMany(static decision => decision.MissingCapabilities),
            static capability => capability is GuaranteeRelationQueryCapability
            {
                Kind: RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance
            });
        Assert.True(valuesOnly.IsRealizable);
        Assert.Equal(RelationQueryOccurrenceProvenanceMode.NotRequested, valuesOnly.Observability.OccurrenceProvenance);
        Assert.DoesNotContain(
            valuesOnly.Requirements,
            static requirement => requirement.Capability is GuaranteeRelationQueryCapability
            {
                Kind: RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance
            });
    }

    [Fact]
    public void Match_RejectsAnEmptySyntheticRequirementSet()
    {
        var plan = PlanReference();

        var exception = Assert.Throws<ArgumentException>(() => RelationQueryRealizationCompiler.Match(
            plan,
            [],
            Profile(plan, []),
            Policy()));

        Assert.Equal("requirements", exception.ParamName);
    }

    [Fact]
    public void Match_ComposesCapabilityClosureAndRetainsEveryRuleAndPrimitiveEvidence()
    {
        var plan = PlanReference();
        var correlation = new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.LocalCorrelation);
        var intermediate = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.RelationshipTraversal);
        var lookupRule = new RelationQueryCompositionRule(
            new("rule/traversal-from-lookup/v1"),
            intermediate,
            [KeyExtraction, BatchedLookup]);
        var joinRule = new RelationQueryCompositionRule(
            new("rule/join-from-traversal/v1"),
            Join,
            [intermediate, correlation],
            preservedGuarantees: [RelationQueryGuaranteeCapabilityKind.JoinMembership]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(
                plan,
                [
                    Evidence("evidence/key", KeyExtraction),
                    Evidence("evidence/lookup", BatchedLookup),
                    Evidence("evidence/correlation", correlation)
                ]),
            Policy(rules: [joinRule, lookupRule]));

        var decision = Assert.IsType<ComposedRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Equal(
            ["rule/join-from-traversal/v1", "rule/traversal-from-lookup/v1"],
            decision.CompositionRules.Select(static rule => rule.Value).ToArray());
        Assert.Equal(
            ["evidence/correlation", "evidence/key", "evidence/lookup"],
            decision.CapabilityEvidence.Select(static evidence => evidence.Value).ToArray());
        Assert.Equal(
            RelationQueryGuaranteeCapabilityKind.JoinMembership,
            Assert.Single(decision.PreservedGuarantees));
        Assert.True(report.IsRealizable);
    }

    [Fact]
    public void Match_RequiresPolicyPermissionAndAttributableValidationForAConstrainedStrategy()
    {
        var plan = PlanReference();
        RelationQueryOperatingBoundaryId boundaryId = new("boundary/max-batch");
        var boundary = new RelationQueryOperatingBoundary(
            boundaryId,
            RelationQueryOperatingBoundaryKind.MaximumBatchSize,
            limit: 100);
        var validator = new OperatingBoundaryValidationRelationQueryCapability(boundaryId);
        var unvalidatedProfile = Profile(
            plan,
            [Evidence("evidence/join", Join, [boundaryId])],
            [boundary]);
        var validatedProfile = Profile(
            plan,
            [
                Evidence("evidence/join", Join, [boundaryId]),
                Evidence("evidence/boundary", validator)
            ],
            [boundary]);
        var requirement = Requirement("requirement/join", Join);

        var rejected = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            validatedProfile,
            Policy());
        var unvalidated = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            unvalidatedProfile,
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));
        var allowed = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            validatedProfile,
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));

        Assert.Equal(
            RelationQueryUnavailableReason.PolicyRejected,
            Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(rejected.Decisions)).Reason);
        Assert.Equal(
            RelationQueryUnavailableReason.OperatingBoundaryInvalid,
            Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(unvalidated.Decisions)).Reason);
        var constrained = Assert.IsType<ConstrainedRelationQueryRealizationDecision>(
            Assert.Single(allowed.Decisions));
        var validation = Assert.Single(constrained.BoundaryValidations);
        Assert.Equal(boundaryId, validation.Boundary);
        Assert.Equal(RelationQueryOperatingBoundaryValidationKind.TargetEnforced, validation.Kind);
        Assert.Equal(new RelationQueryTargetCapabilityEvidenceId("evidence/boundary"), validation.CapabilityEvidence);
        Assert.True(allowed.IsRealizable);
    }

    [Fact]
    public void Match_ValidatesANumericBoundaryFromPortableRequirementFacts()
    {
        var plan = PlanReference();
        RelationQueryOperatingBoundaryId boundaryId = new("boundary/max-page");
        var boundary = new RelationQueryOperatingBoundary(
            boundaryId,
            RelationQueryOperatingBoundaryKind.MaximumPageSize,
            limit: 25);
        var requirement = Requirement(
            "requirement/page",
            new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.OffsetPaging),
            staticFacts: [new(RelationQueryRealizationStaticFactKind.PageSize, 20)]);
        var profile = Profile(
            plan,
            [Evidence("evidence/page", requirement.Capability, [boundaryId])],
            [boundary]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            profile,
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));

        var decision = Assert.IsType<ConstrainedRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        var validation = Assert.Single(decision.BoundaryValidations);
        Assert.Equal(RelationQueryOperatingBoundaryValidationKind.StaticPlanFact, validation.Kind);
        Assert.Equal(20, validation.MeasuredValue);
        Assert.Null(validation.CapabilityEvidence);
    }

    [Fact]
    public void Match_RequiresAComposedStrategyToPreserveRequirementGuarantees()
    {
        var plan = PlanReference();
        var requirement = Requirement(
            "requirement/join",
            Join,
            requiredGuarantees: [RelationQueryGuaranteeCapabilityKind.JoinMembership]);
        var rule = new RelationQueryCompositionRule(new("rule/join/v1"), Join, [KeyExtraction]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            Profile(plan, [Evidence("evidence/key", KeyExtraction)]),
            Policy(rules: [rule]));

        var decision = Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Equal(RelationQueryUnavailableReason.CompositionUnavailable, decision.Reason);
        Assert.Equal(
            new GuaranteeRelationQueryCapability(RelationQueryGuaranteeCapabilityKind.JoinMembership),
            Assert.Single(decision.MissingCapabilities));
    }

    [Fact]
    public void Match_CouplesNativeCapabilityAndGuaranteeEvidence()
    {
        var plan = PlanReference();
        var guarantee = RelationQueryGuaranteeCapabilityKind.JoinMembership;
        var requirement = Requirement("requirement/join", Join, requiredGuarantees: [guarantee]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            Profile(
                plan,
                [
                    Evidence("evidence/join", Join),
                    Evidence("evidence/guarantee", new GuaranteeRelationQueryCapability(guarantee))
                ]),
            Policy());

        var decision = Assert.IsType<NativeRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Equal(
            ["evidence/guarantee", "evidence/join"],
            decision.CapabilityEvidence.Select(static item => item.Value).ToArray());
        Assert.Equal(guarantee, Assert.Single(decision.PreservedGuarantees));
    }

    [Fact]
    public void Match_ClassifiesBoundedGuaranteeEvidenceAsAValidatedConstraint()
    {
        var plan = PlanReference();
        var guarantee = RelationQueryGuaranteeCapabilityKind.JoinMembership;
        RelationQueryOperatingBoundaryId boundaryId = new("boundary/max-page");
        var boundary = new RelationQueryOperatingBoundary(
            boundaryId,
            RelationQueryOperatingBoundaryKind.MaximumPageSize,
            limit: 20);
        var requirement = Requirement(
            "requirement/join",
            Join,
            requiredGuarantees: [guarantee],
            staticFacts: [new(RelationQueryRealizationStaticFactKind.PageSize, 10)]);
        var profile = Profile(
            plan,
            [
                Evidence("evidence/join", Join),
                Evidence(
                    "evidence/guarantee",
                    new GuaranteeRelationQueryCapability(guarantee),
                    [boundaryId])
            ],
            [boundary]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            profile,
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));

        var decision = Assert.IsType<ConstrainedRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Equal(
            ["evidence/guarantee", "evidence/join"],
            decision.CapabilityEvidence.Select(static item => item.Value).ToArray());
        Assert.Equal(RelationQueryOperatingBoundaryValidationKind.StaticPlanFact,
            Assert.Single(decision.BoundaryValidations).Kind);
        Assert.Equal(guarantee, Assert.Single(decision.PreservedGuarantees));
    }

    [Fact]
    public void Match_UsesExactTargetEnforcementForBoundedGuaranteeEvidence()
    {
        var plan = PlanReference();
        var guarantee = RelationQueryGuaranteeCapabilityKind.JoinMembership;
        RelationQueryOperatingBoundaryId boundaryId = new("boundary/materialized-inputs");
        var boundary = new RelationQueryOperatingBoundary(
            boundaryId,
            RelationQueryOperatingBoundaryKind.MaterializedInputs);
        var requirement = Requirement("requirement/join", Join, requiredGuarantees: [guarantee]);
        var validator = new OperatingBoundaryValidationRelationQueryCapability(boundaryId);
        var profile = Profile(
            plan,
            [
                Evidence("evidence/join", Join),
                Evidence(
                    "evidence/guarantee",
                    new GuaranteeRelationQueryCapability(guarantee),
                    [boundaryId]),
                Evidence("evidence/validator", validator)
            ],
            [boundary]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            profile,
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));

        var decision = Assert.IsType<ConstrainedRelationQueryRealizationDecision>(
            Assert.Single(report.Decisions));
        Assert.Equal(
            ["evidence/guarantee", "evidence/join", "evidence/validator"],
            decision.CapabilityEvidence.Select(static evidence => evidence.Value).ToArray());
        var validation = Assert.Single(decision.BoundaryValidations);
        Assert.Equal(RelationQueryOperatingBoundaryValidationKind.TargetEnforced, validation.Kind);
        Assert.Equal(new RelationQueryTargetCapabilityEvidenceId("evidence/validator"), validation.CapabilityEvidence);
        Assert.True(report.IsRealizable);
    }

    [Fact]
    public void Match_AppliesAnExactLocalOverrideBeforeTargetStrategies()
    {
        var plan = PlanReference();
        var requirement = Requirement("requirement/join", Join);
        var @override = new RelationQueryRealizationOverride(
            new("override/join"),
            requirement.Id,
            Join,
            justification: "Application-owned exact join implementation.");

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            Profile(plan, []),
            Policy(overrides: [@override]));

        var decision = Assert.IsType<OverrideRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Equal(@override.Id, decision.Override);
        Assert.True(report.IsRealizable);
    }

    [Fact]
    public void Match_RejectsAnOverrideThatOmitsABoundaryRequiredByItsEvidence()
    {
        var plan = PlanReference();
        RelationQueryOperatingBoundaryId boundaryId = new("boundary/materialized-inputs");
        var boundary = new RelationQueryOperatingBoundary(
            boundaryId,
            RelationQueryOperatingBoundaryKind.MaterializedInputs);
        var evidence = Evidence("evidence/join", Join, [boundaryId]);
        var requirement = Requirement("requirement/join", Join);
        var @override = new RelationQueryRealizationOverride(
            new("override/join"),
            requirement.Id,
            Join,
            [evidence.Id],
            justification: "Application-owned exact join implementation.");

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            Profile(plan, [evidence], [boundary]),
            Policy(
                constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated,
                overrides: [@override]));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.Equal(
            RelationQueryUnavailableReason.OverrideInvalid,
            Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions)).Reason);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.OverrideInvalid
                && diagnostic.Override == @override.Id);
    }

    [Fact]
    public void Match_DiagnosesEquallyPreferredCompositionsAsInvalid()
    {
        var plan = PlanReference();
        var firstRule = new RelationQueryCompositionRule(new("rule/join-a/v1"), Join, [KeyExtraction]);
        var secondRule = new RelationQueryCompositionRule(new("rule/join-b/v1"), Join, [KeyExtraction]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(plan, [Evidence("evidence/key", KeyExtraction)]),
            Policy(rules: [secondRule, firstRule]));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.Equal(
            RelationQueryUnavailableReason.AmbiguousStrategy,
            Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions)).Reason);
        Assert.Contains(report.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.StrategyAmbiguous);
    }

    [Fact]
    public void Match_UsesAnExplicitCompositionRuleSelectionToResolveEquivalentStrategies()
    {
        var plan = PlanReference();
        var firstRule = new RelationQueryCompositionRule(new("rule/join-a/v1"), Join, [KeyExtraction]);
        var secondRule = new RelationQueryCompositionRule(new("rule/join-b/v1"), Join, [KeyExtraction]);
        var selection = new RelationQueryCompositionRuleSelection(Join, secondRule.Id);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(plan, [Evidence("evidence/key", KeyExtraction)]),
            Policy(rules: [firstRule, secondRule], selections: [selection]));

        var decision = Assert.IsType<ComposedRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Equal(secondRule.Id, Assert.Single(decision.CompositionRules));
        Assert.True(report.IsRealizable);
    }

    [Fact]
    public void Match_AppliesNativeVersusComposedPreferenceAcrossTheProofClosure()
    {
        var plan = PlanReference();
        var traversal = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.RelationshipTraversal);
        var rootRule = new RelationQueryCompositionRule(new("rule/join/v1"), Join, [traversal]);
        var traversalRule = new RelationQueryCompositionRule(
            new("rule/traversal/v1"),
            traversal,
            [KeyExtraction]);
        var profile = Profile(
            plan,
            [
                Evidence("evidence/traversal", traversal),
                Evidence("evidence/key", KeyExtraction)
            ]);
        var requirement = Requirement("requirement/join", Join);

        var nativePreferred = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            profile,
            Policy(rules: [rootRule, traversalRule]));
        var composedPreferred = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            profile,
            Policy(
                rules: [rootRule, traversalRule],
                preference: RelationQueryRealizationPreference.PreferComposed));

        var nativeDecision = Assert.IsType<ComposedRelationQueryRealizationDecision>(
            Assert.Single(nativePreferred.Decisions));
        Assert.Equal(rootRule.Id, Assert.Single(nativeDecision.CompositionRules));
        Assert.Equal(new RelationQueryTargetCapabilityEvidenceId("evidence/traversal"),
            Assert.Single(nativeDecision.CapabilityEvidence));

        var composedDecision = Assert.IsType<ComposedRelationQueryRealizationDecision>(
            Assert.Single(composedPreferred.Decisions));
        Assert.Equal(
            new[] { rootRule.Id, traversalRule.Id }.OrderBy(static id => id.Value),
            composedDecision.CompositionRules);
        Assert.Equal(new RelationQueryTargetCapabilityEvidenceId("evidence/key"),
            Assert.Single(composedDecision.CapabilityEvidence));
    }

    [Fact]
    public void Match_IgnoresOverridesForRequirementsPrunedFromTheCurrentDemand()
    {
        var plan = PlanReference();
        var requirement = Requirement("requirement/join", Join);
        var profile = Profile(plan, [Evidence("evidence/join", Join)]);
        var baselinePolicy = Policy();
        var unusedOverride = new RelationQueryRealizationOverride(
            new("override/undemanded-filter"),
            new("requirement/filter"),
            new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter),
            justification: "Used only by a wider output demand.");

        var baseline = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            profile,
            baselinePolicy);
        var withUnusedOverride = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            profile,
            Policy(overrides: [unusedOverride]));

        Assert.True(withUnusedOverride.IsRealizable);
        Assert.Empty(withUnusedOverride.Diagnostics);
        Assert.Equal(baseline.Fingerprint, withUnusedOverride.Fingerprint);
    }

    [Fact]
    public void Match_RejectsAMismatchedRuleSelectionForADemandedCapability()
    {
        var plan = PlanReference();
        var filter = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter);
        var filterRule = new RelationQueryCompositionRule(new("rule/filter/v1"), filter, [KeyExtraction]);
        var selection = new RelationQueryCompositionRuleSelection(Join, filterRule.Id);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(plan, [Evidence("evidence/join", Join)]),
            Policy(rules: [filterRule], selections: [selection]));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.IsType<NativeRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Contains(report.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.PolicyInvalid
            && diagnostic.CompositionRule == new RelationQueryCompositionRuleId("rule/filter/v1"));
    }

    [Fact]
    public void Match_DiagnosesCyclicCompositionRulesWithoutRecursingIndefinitely()
    {
        var plan = PlanReference();
        var traversal = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.RelationshipTraversal);
        var joinRule = new RelationQueryCompositionRule(new("rule/join/v1"), Join, [traversal]);
        var traversalRule = new RelationQueryCompositionRule(new("rule/traversal/v1"), traversal, [Join]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(plan, []),
            Policy(rules: [joinRule, traversalRule]));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Equal(
            2,
            report.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == RelationQueryRealizationDiagnosticCodes.CompositionRuleInvalid));
    }

    [Fact]
    public void Match_AttributesACycleOnlyToRulesInsideTheCycle()
    {
        var plan = PlanReference();
        var traversal = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.RelationshipTraversal);
        var filter = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter);
        var rootRule = new RelationQueryCompositionRule(new("rule/join-from-traversal/v1"), Join, [traversal]);
        var traversalRule = new RelationQueryCompositionRule(new("rule/traversal-from-filter/v1"), traversal, [filter]);
        var filterRule = new RelationQueryCompositionRule(new("rule/filter-from-traversal/v1"), filter, [traversal]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(plan, [Evidence("evidence/traversal", traversal)]),
            Policy(rules: [rootRule, traversalRule, filterRule]));

        var decision = Assert.IsType<ComposedRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Equal(rootRule.Id, Assert.Single(decision.CompositionRules));
        var cycleDiagnostics = report.Diagnostics
            .Where(static diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.CompositionRuleInvalid)
            .ToArray();
        Assert.Equal(2, cycleDiagnostics.Length);
        Assert.DoesNotContain(cycleDiagnostics, diagnostic => diagnostic.CompositionRule == rootRule.Id);
    }

    [Fact]
    public void Match_RejectsUnsupportedPlanVersionsBeforeCapabilityMatching()
    {
        var plan = PlanReference();
        var profile = new RelationQueryTargetCapabilityProfile(
            new("target/test"),
            new("target/test/v1"),
            ["unsupported-schema"],
            [plan.CompilerProfile],
            [Evidence("evidence/join", Join)]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            profile,
            Policy());

        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, report.Status);
        Assert.Equal(
            RelationQueryUnavailableReason.ProfileVersionUnsupported,
            Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions)).Reason);
        Assert.Equal(
            RelationQueryRealizationDiagnosticCodes.TargetProfileVersionUnsupported,
            Assert.Single(report.Diagnostics).Code);
    }

    [Fact]
    public void Match_DiagnosesUnknownCapabilityEnumsWithoutProducingASuccessfulReport()
    {
        var plan = PlanReference();
        var unknownEvidence = Evidence(
            "evidence/unknown",
            new LogicalRelationQueryCapability((RelationQueryLogicalCapabilityKind)int.MaxValue));

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(
                plan,
                [
                    Evidence("evidence/join", Join),
                    unknownEvidence
                ]),
            Policy());

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.False(report.IsRealizable);
        var decision = Assert.IsType<UnavailableRelationQueryRealizationDecision>(
            Assert.Single(report.Decisions));
        Assert.Equal(RelationQueryUnavailableReason.CapabilityEvidenceInvalid, decision.Reason);
        Assert.Equal(Join, Assert.Single(decision.MissingCapabilities));
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid
                && diagnostic.CapabilityEvidence == unknownEvidence.Id);
    }

    [Theory]
    [InlineData(0L)]
    public void Match_DiagnosesNonPositiveBoundaryLimits(long limit)
    {
        var plan = PlanReference();
        RelationQueryOperatingBoundaryId boundaryId = new("boundary/invalid-page-size");
        var boundary = new RelationQueryOperatingBoundary(
            boundaryId,
            RelationQueryOperatingBoundaryKind.MaximumPageSize,
            limit);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(
                plan,
                [Evidence("evidence/join", Join, [boundaryId])],
                [boundary]),
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.OperatingBoundaryInvalid
                && diagnostic.OperatingBoundary == boundaryId);
        Assert.Contains(
            report.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid);
    }

    [Fact]
    public void Match_DiagnosesConflictingCapabilityEvidenceIdentities()
    {
        var plan = PlanReference();
        RelationQueryTargetCapabilityEvidenceId conflictId = new("evidence/conflict");
        var profile = Profile(
            plan,
            [
                new(conflictId, Join),
                new(
                    conflictId,
                    new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter))
            ]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            profile,
            Policy());

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceConflict
                && diagnostic.CapabilityEvidence == conflictId);
    }

    [Fact]
    public void Match_DiagnosesIncompleteCapabilityEvidence()
    {
        var plan = PlanReference();
        RelationQueryOperatingBoundaryId missingBoundary = new("boundary/not-declared");
        var evidence = Evidence("evidence/join", Join, [missingBoundary]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(plan, [evidence]),
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions));
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid
                && diagnostic.CapabilityEvidence == evidence.Id
                && diagnostic.OperatingBoundary == missingBoundary);
    }

    [Fact]
    public void Match_DiagnosesDuplicateBoundaryDeclarationsAndRepeatedEvidenceReferences()
    {
        var plan = PlanReference();
        RelationQueryOperatingBoundaryId boundaryId = new("boundary/conflict");
        var profile = Profile(
            plan,
            [Evidence("evidence/join", Join, [boundaryId, boundaryId])],
            [
                new(boundaryId, RelationQueryOperatingBoundaryKind.MaterializedInputs),
                new(boundaryId, RelationQueryOperatingBoundaryKind.SingleSource)
            ]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            profile,
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.OperatingBoundaryInvalid
                && diagnostic.OperatingBoundary == boundaryId);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid
                && diagnostic.CapabilityEvidence == new RelationQueryTargetCapabilityEvidenceId("evidence/join")
                && diagnostic.OperatingBoundary == boundaryId);
    }

    [Fact]
    public void Match_NormalizesConflictingEvidenceAcrossDeclarationOrderAndCulture()
    {
        var plan = PlanReference();
        RelationQueryTargetCapabilityEvidenceId conflictId = new("evidence/conflict");
        RelationQueryTargetCapabilityEvidence[] declarations =
        [
            new(conflictId, Join),
            new(
                conflictId,
                new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter))
        ];
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new("tr-TR");
            CultureInfo.CurrentUICulture = new("tr-TR");
            var first = RelationQueryRealizationCompiler.Match(
                plan,
                [Requirement("requirement/join", Join)],
                Profile(plan, [.. declarations]),
                Policy());
            var second = RelationQueryRealizationCompiler.Match(
                plan,
                [Requirement("requirement/join", Join)],
                Profile(plan, [.. declarations.Reverse()]),
                Policy());

            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.Equal(
                first.Diagnostics.Select(static diagnostic => (
                    diagnostic.Code,
                    diagnostic.CapabilityEvidence,
                    diagnostic.OperatingBoundary)),
                second.Diagnostics.Select(static diagnostic => (
                    diagnostic.Code,
                    diagnostic.CapabilityEvidence,
                    diagnostic.OperatingBoundary)));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Match_SelectsOneExactBoundaryValidatorWhenSeveralAreAdvertised()
    {
        var plan = PlanReference();
        RelationQueryOperatingBoundaryId boundaryId = new("boundary/materialized-inputs");
        var boundary = new RelationQueryOperatingBoundary(
            boundaryId,
            RelationQueryOperatingBoundaryKind.MaterializedInputs);
        var validator = new OperatingBoundaryValidationRelationQueryCapability(boundaryId);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(
                plan,
                [
                    Evidence("evidence/join", Join, [boundaryId]),
                    Evidence("evidence/validator-a", validator),
                    Evidence("evidence/validator-b", validator)
                ],
                [boundary]),
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));

        var decision = Assert.IsType<ConstrainedRelationQueryRealizationDecision>(
            Assert.Single(report.Decisions));
        Assert.Equal(
            ["evidence/join", "evidence/validator-a"],
            decision.CapabilityEvidence.Select(static evidence => evidence.Value).ToArray());
        Assert.Equal(
            new RelationQueryTargetCapabilityEvidenceId("evidence/validator-a"),
            Assert.Single(decision.BoundaryValidations).CapabilityEvidence);
        Assert.True(report.IsRealizable);
    }

    [Fact]
    public void Match_DoesNotConflateBoundarySetsContainingCanonicalKeyDelimiters()
    {
        var plan = PlanReference();
        RelationQueryOperatingBoundaryId firstA = new("a\u001fb");
        RelationQueryOperatingBoundaryId secondA = new("c");
        RelationQueryOperatingBoundaryId firstB = new("a");
        RelationQueryOperatingBoundaryId secondB = new("b\u001fc");
        var boundaries = new[] { firstA, secondA, firstB, secondB }
            .Select(static id => new RelationQueryOperatingBoundary(
                id,
                RelationQueryOperatingBoundaryKind.MaximumPageSize,
                limit: 10))
            .ToImmutableArray();
        var requirement = Requirement(
            "requirement/join",
            Join,
            staticFacts: [new(RelationQueryRealizationStaticFactKind.PageSize, 1)]);

        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [requirement],
            Profile(
                plan,
                [
                    Evidence("evidence/a", Join, [firstA, secondA]),
                    Evidence("evidence/b", Join, [firstB, secondB])
                ],
                boundaries),
            Policy(constrained: RelationQueryConstrainedRealizationPolicy.AllowValidated));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.Equal(
            RelationQueryUnavailableReason.AmbiguousStrategy,
            Assert.IsType<UnavailableRelationQueryRealizationDecision>(Assert.Single(report.Decisions)).Reason);
        Assert.Contains(
            report.Diagnostics,
            static diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.StrategyAmbiguous);
    }

    [Fact]
    public void Report_RoundTripsThroughThePortableRelationsJsonProfile()
    {
        var plan = PlanReference();
        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(plan, [Evidence("evidence/join", Join)]),
            Policy());
        var options = RelationQueryJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(report, options);
        var roundTrip = JsonSerializer.Deserialize<RelationQueryRealizationReport>(json, options);

        Assert.NotNull(roundTrip);
        Assert.Equal(report.Fingerprint, roundTrip.Fingerprint);
        Assert.Equal(roundTrip.Fingerprint, RelationQueryRealizationFingerprinter.Compute(roundTrip));
        Assert.IsType<NativeRelationQueryRealizationDecision>(Assert.Single(roundTrip.Decisions));
        Assert.IsType<LogicalRelationQueryCapability>(Assert.Single(roundTrip.Requirements).Capability);
    }

    [Fact]
    public void Report_NotRequestedObservabilityRoundTripsThroughThePortableRelationsJsonProfile()
    {
        var plan = PlanReference();
        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(plan, [Evidence("evidence/join", Join)]),
            Policy(),
            RelationQueryResultObservability.NotRequested);
        var options = RelationQueryJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(report, options);
        var roundTrip = JsonSerializer.Deserialize<RelationQueryRealizationReport>(json, options);

        Assert.NotNull(roundTrip);
        Assert.Equal(
            RelationQueryOccurrenceProvenanceMode.NotRequested,
            roundTrip.Observability.OccurrenceProvenance);
        Assert.Equal(report.Fingerprint, roundTrip.Fingerprint);
        Assert.Equal(roundTrip.Fingerprint, RelationQueryRealizationFingerprinter.Compute(roundTrip));
    }

    [Fact]
    public void InvalidReport_RoundTripsThroughThePortableRelationsJsonProfile()
    {
        var plan = PlanReference();
        RelationQueryTargetCapabilityEvidenceId conflictId = new("evidence/conflict");
        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(
                plan,
                [
                    new(conflictId, Join),
                    new(
                        conflictId,
                        new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter))
                ]),
            Policy());
        var options = RelationQueryJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(report, options);
        var roundTrip = JsonSerializer.Deserialize<RelationQueryRealizationReport>(json, options);

        Assert.NotNull(roundTrip);
        Assert.Equal(RelationQueryRealizationStatus.Invalid, roundTrip.Status);
        Assert.Equal(report.Fingerprint, roundTrip.Fingerprint);
        Assert.Equal(roundTrip.Fingerprint, RelationQueryRealizationFingerprinter.Compute(roundTrip));
    }

    [Fact]
    public void InvalidReport_WithRetainedUnknownNumericEnums_RoundTripsThroughThePortableRelationsJsonProfile()
    {
        var plan = PlanReference();
        var unknownLogicalKind = (RelationQueryLogicalCapabilityKind)int.MaxValue;
        var unknownExpressionRequirementKind = (ExprCapabilityRequirementKind)int.MaxValue;
        var unknownBoundaryKind = (RelationQueryOperatingBoundaryKind)int.MaxValue;
        var unknownLogicalEvidence = Evidence(
            "evidence/unknown-logical",
            new LogicalRelationQueryCapability(unknownLogicalKind));
        var unknownExpressionEvidence = Evidence(
            "evidence/unknown-expression-requirement-kind",
            new ExpressionRelationQueryCapability(
                new("expression/unknown-requirement-kind"),
                unknownExpressionRequirementKind));
        RelationQueryOperatingBoundaryId unknownBoundaryId = new("boundary/unknown-kind");
        var report = RelationQueryRealizationCompiler.Match(
            plan,
            [Requirement("requirement/join", Join)],
            Profile(
                plan,
                [
                    Evidence("evidence/join", Join),
                    unknownLogicalEvidence,
                    unknownExpressionEvidence
                ],
                [new RelationQueryOperatingBoundary(unknownBoundaryId, unknownBoundaryKind)]),
            Policy());
        var options = RelationQueryJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(report, options);
        using var document = JsonDocument.Parse(json);
        var targetProfileJson = document.RootElement.GetProperty("targetProfile");
        var capabilitiesJson = targetProfileJson
            .GetProperty("capabilities")
            .EnumerateArray()
            .ToArray();
        var unknownLogicalKindJson = capabilitiesJson
            .Single(item => item.GetProperty("id").GetString() == unknownLogicalEvidence.Id.Value)
            .GetProperty("capability")
            .GetProperty("kind");
        var unknownExpressionRequirementKindJson = capabilitiesJson
            .Single(item => item.GetProperty("id").GetString() == unknownExpressionEvidence.Id.Value)
            .GetProperty("capability")
            .GetProperty("requirementKind");
        var unknownBoundaryKindJson = targetProfileJson
            .GetProperty("operatingBoundaries")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == unknownBoundaryId.Value)
            .GetProperty("kind");
        var roundTrip = JsonSerializer.Deserialize<RelationQueryRealizationReport>(json, options);

        Assert.Equal(JsonValueKind.Number, unknownLogicalKindJson.ValueKind);
        Assert.Equal(int.MaxValue, unknownLogicalKindJson.GetInt32());
        Assert.Equal(JsonValueKind.Number, unknownExpressionRequirementKindJson.ValueKind);
        Assert.Equal(int.MaxValue, unknownExpressionRequirementKindJson.GetInt32());
        Assert.Equal(JsonValueKind.Number, unknownBoundaryKindJson.ValueKind);
        Assert.Equal(int.MaxValue, unknownBoundaryKindJson.GetInt32());
        Assert.NotNull(roundTrip);
        Assert.Equal(RelationQueryRealizationStatus.Invalid, roundTrip.Status);
        var roundTripLogicalEvidence = Assert.Single(
            roundTrip.TargetProfile.Capabilities,
            evidence => evidence.Id == unknownLogicalEvidence.Id);
        Assert.Equal(
            unknownLogicalKind,
            Assert.IsType<LogicalRelationQueryCapability>(roundTripLogicalEvidence.Capability).Kind);
        var roundTripExpressionEvidence = Assert.Single(
            roundTrip.TargetProfile.Capabilities,
            evidence => evidence.Id == unknownExpressionEvidence.Id);
        Assert.Equal(
            unknownExpressionRequirementKind,
            Assert.IsType<ExpressionRelationQueryCapability>(roundTripExpressionEvidence.Capability).RequirementKind);
        Assert.Equal(
            unknownBoundaryKind,
            Assert.Single(
                roundTrip.TargetProfile.OperatingBoundaries,
                boundary => boundary.Id == unknownBoundaryId).Kind);
        Assert.Contains(
            roundTrip.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid
                && diagnostic.CapabilityEvidence == unknownLogicalEvidence.Id);
        Assert.Contains(
            roundTrip.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid
                && diagnostic.CapabilityEvidence == unknownExpressionEvidence.Id);
        Assert.Contains(
            roundTrip.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.OperatingBoundaryInvalid
                && diagnostic.OperatingBoundary == unknownBoundaryId);
        Assert.Equal(report.Fingerprint, roundTrip.Fingerprint);
        Assert.Equal(roundTrip.Fingerprint, RelationQueryRealizationFingerprinter.Compute(roundTrip));
    }

    [Fact]
    public void CapabilityEnumJson_RejectsNumericAliasesForKnownValues()
    {
        var options = RelationQueryJsonSerializer.CreateOptions();
        var json = JsonSerializer.Serialize<RelationQueryCapability>(Join, options);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("kind").ValueKind);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RelationQueryCapability>(
            json.Replace(
                "\"Join\"",
                ((int)RelationQueryLogicalCapabilityKind.Join).ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal),
            options));
    }

    static RelationQueryRealizationRequirement Requirement(
        string id,
        RelationQueryCapability capability,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> requiredGuarantees = default,
        ImmutableArray<RelationQueryRealizationStaticFact> staticFacts = default) =>
        new(new(id), capability, requiredGuarantees: requiredGuarantees, staticFacts: staticFacts);

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
        ImmutableArray<RelationQueryCompositionRuleSelection> selections = default,
        ImmutableArray<RelationQueryRealizationOverride> overrides = default,
        RelationQueryRealizationPreference preference = RelationQueryRealizationPreference.PreferNative) =>
        new(
            new("policy/test/v1"),
            "conventions/test/v1",
            preference: preference,
            constrainedRealizations: constrained,
            compositionRules: rules,
            compositionRuleSelections: selections,
            overrides: overrides);

    static RelationQueryCompiledPlanReference PlanReference()
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(result.IsSuccessful);
        return RelationQueryCompiledPlanReference.From(Assert.IsType<CompiledRelationQueryPlan>(result.Plan));
    }
}
