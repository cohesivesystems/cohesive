using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryRealizationFingerprintTests
{
    [Fact]
    public void Compute_IsInvariantToDeclarationOrderAndCurrentCulture()
    {
        var expected = BuildReport(CreateInputs(metadata: "baseline"));
        var priorCulture = CultureInfo.CurrentCulture;
        var priorUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");

            var actual = BuildReport(CreateInputs(metadata: "baseline", reverseDeclarations: true));

            Assert.Equal(expected.Fingerprint, actual.Fingerprint);
            Assert.Equal(actual.Fingerprint, RelationQueryRealizationFingerprinter.Compute(actual));
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
            CultureInfo.CurrentUICulture = priorUiCulture;
        }
    }

    [Fact]
    public void Compute_MalformedSeparatorBearingDeclarationsRemainOrderIndependentAndSequenceDistinct()
    {
        foreach (var separator in new[] { '\u001e', '\u001f' })
        {
            Assert.NotEqual(
                RelationQueryRealizationOrdering.SequenceKey(["left", "right"]),
                RelationQueryRealizationOrdering.SequenceKey([$"left{separator}right"]));
        }

        var plan = new RelationQueryCompiledPlanReference(
            "compiler/v1",
            "relation-query/v1",
            new("sha256", "relation-query-definition/v1", "definition-hash"),
            new("sha256", "relation-query-plan-shapes/v1", "shapes-hash"),
            null,
            new("sha256", "relation-query-plan-demand/v1", "demand-hash"),
            [new("input/load")]);
        var capability = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Join);
        var requirement = new RelationQueryRealizationRequirement(new("requirement/join"), capability);
        RelationQueryTargetCapabilityEvidenceId conflictId = new("evidence/conflict\u001e\u001f");
        RelationQueryOperatingBoundaryId[] boundaryIds =
        [
            new("boundary/record"),
            new("boundary/sequence"),
            new("boundary/record\u001esequence"),
            new("boundary/unit"),
            new("boundary/separator"),
            new("boundary/unit\u001fseparator")
        ];
        var boundaries = boundaryIds
            .Select(static id => new RelationQueryOperatingBoundary(
                id,
                RelationQueryOperatingBoundaryKind.MaterializedInputs))
            .ToImmutableArray();
        ImmutableArray<RelationQueryTargetCapabilityEvidence> declarations =
        [
            new(conflictId, capability, [boundaryIds[0], boundaryIds[1]]),
            new(conflictId, capability, [boundaryIds[2]]),
            new(conflictId, capability, [boundaryIds[3], boundaryIds[4]]),
            new(conflictId, capability, [boundaryIds[5]])
        ];
        var policy = new RelationQueryRealizationPolicy(new("policy/v1"), "conventions/v1");

        var first = Match(declarations);
        var second = Match([.. declarations.Reverse()]);

        Assert.Equal(RelationQueryRealizationStatus.Invalid, first.Status);
        Assert.Equal(first.Diagnostics.ToArray(), second.Diagnostics.ToArray());
        var firstDecision = Assert.IsType<UnavailableRelationQueryRealizationDecision>(
            Assert.Single(first.Decisions));
        var secondDecision = Assert.IsType<UnavailableRelationQueryRealizationDecision>(
            Assert.Single(second.Decisions));
        Assert.Equal(firstDecision.Requirement, secondDecision.Requirement);
        Assert.Equal(firstDecision.Reason, secondDecision.Reason);
        Assert.Equal(
            firstDecision.MissingCapabilities.ToArray(),
            secondDecision.MissingCapabilities.ToArray());
        Assert.Equal(first.Fingerprint, second.Fingerprint);

        RelationQueryRealizationReport Match(
            ImmutableArray<RelationQueryTargetCapabilityEvidence> evidence) =>
            RelationQueryRealizationCompiler.Match(
                plan,
                [requirement],
                new(
                    new("target/test"),
                    new("target/test/v1"),
                    [plan.DefinitionSchemaVersion],
                    [plan.CompilerProfile],
                    evidence,
                    boundaries),
                policy);
    }

    [Fact]
    public void Compute_MatchesKnownCanonicalVector()
    {
        var report = BuildReport(CreateInputs(metadata: "known-vector"));

        Assert.Equal(RelationQueryRealizationFingerprinter.Algorithm, report.Fingerprint.Algorithm);
        Assert.Equal(RelationQueryRealizationFingerprinter.Canonicalization, report.Fingerprint.Canonicalization);
        Assert.Equal(
            "982bcb6a935fd4317a1ee6d68f61163fceab1c548d3d79a753a8acb7991a77cb",
            report.Fingerprint.Value);
    }

    [Fact]
    public void Compute_ExcludesHumanFacingAndUnusedDescriptiveMetadata()
    {
        var first = BuildReport(CreateInputs(metadata: "first description and justification"));
        var second = BuildReport(CreateInputs(metadata: "second description and justification"));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Compute_ChangesForSemanticProfilePolicyAndDecisionInputs()
    {
        var baselineInputs = CreateInputs(metadata: "sensitivity");
        var baseline = BuildReport(baselineInputs).Fingerprint;

        var originalRequirement = baselineInputs.Requirements[0];
        var originalOrigin = originalRequirement.Origin!;
        var changedRequirements = baselineInputs.Requirements.SetItem(
            0,
            new RelationQueryRealizationRequirement(
                originalRequirement.Id,
                originalRequirement.Capability,
                new(
                    originalOrigin.Input,
                    originalOrigin.Node,
                    originalOrigin.SemanticSite + "/changed",
                    originalOrigin.ExpressionPath,
                    originalOrigin.FieldPath,
                    originalOrigin.Binding),
                originalRequirement.Uses,
                originalRequirement.RequiredGuarantees,
                originalRequirement.StaticFacts));
        var changedProfile = CopyProfile(
            baselineInputs.TargetProfile,
            id: new("target/profile/v2"));
        var changedPolicy = CopyPolicy(
            baselineInputs.Policy,
            preference: RelationQueryRealizationPreference.PreferComposed);
        var native = Assert.IsType<NativeRelationQueryRealizationDecision>(baselineInputs.Decisions[0]);
        var changedDecisions = baselineInputs.Decisions.SetItem(
            0,
            new NativeRelationQueryRealizationDecision(
                native.Requirement,
                [new("evidence/filter-alternate")]));

        Assert.NotEqual(
            baseline,
            BuildReport(baselineInputs with { Requirements = changedRequirements }).Fingerprint);
        Assert.NotEqual(
            baseline,
            BuildReport(baselineInputs with { TargetProfile = changedProfile }).Fingerprint);
        Assert.NotEqual(
            baseline,
            BuildReport(baselineInputs with { Policy = changedPolicy }).Fingerprint);
        Assert.NotEqual(
            baseline,
            BuildReport(baselineInputs with { Decisions = changedDecisions }).Fingerprint);
    }

    [Fact]
    public void Compute_IncludesEveryCompiledPlanReferenceComponent()
    {
        var inputs = CreateInputs(metadata: "plan-components");
        var plan = inputs.Plan;
        var baseline = BuildReport(inputs).Fingerprint;
        RelationQueryCompiledPlanReference[] changedPlans =
        [
            new(
                "compiler/v2",
                plan.DefinitionSchemaVersion,
                plan.DefinitionFingerprint,
                plan.ShapeSnapshotsFingerprint,
                plan.RelationshipCatalogFingerprint,
                plan.DemandFingerprint,
                plan.Inputs),
            new(
                plan.CompilerProfile,
                "relation-query/v2",
                plan.DefinitionFingerprint,
                plan.ShapeSnapshotsFingerprint,
                plan.RelationshipCatalogFingerprint,
                plan.DemandFingerprint,
                plan.Inputs),
            new(
                plan.CompilerProfile,
                plan.DefinitionSchemaVersion,
                new("sha256", "relation-query-definition/v1", "changed-definition-hash"),
                plan.ShapeSnapshotsFingerprint,
                plan.RelationshipCatalogFingerprint,
                plan.DemandFingerprint,
                plan.Inputs),
            new(
                plan.CompilerProfile,
                plan.DefinitionSchemaVersion,
                plan.DefinitionFingerprint,
                new("sha256", "relation-query-plan-shapes/v1", "changed-shapes-hash"),
                plan.RelationshipCatalogFingerprint,
                plan.DemandFingerprint,
                plan.Inputs),
            new(
                plan.CompilerProfile,
                plan.DefinitionSchemaVersion,
                plan.DefinitionFingerprint,
                plan.ShapeSnapshotsFingerprint,
                new("sha256", "relationship-catalog/v1", "catalog-hash"),
                plan.DemandFingerprint,
                plan.Inputs),
            new(
                plan.CompilerProfile,
                plan.DefinitionSchemaVersion,
                plan.DefinitionFingerprint,
                plan.ShapeSnapshotsFingerprint,
                plan.RelationshipCatalogFingerprint,
                new("sha256", "relation-query-plan-demand/v1", "changed-demand-hash"),
                plan.Inputs),
            new(
                plan.CompilerProfile,
                plan.DefinitionSchemaVersion,
                plan.DefinitionFingerprint,
                plan.ShapeSnapshotsFingerprint,
                plan.RelationshipCatalogFingerprint,
                plan.DemandFingerprint,
                [new("input/load"), new("input/equipment")])
        ];

        foreach (var changedPlan in changedPlans)
        {
            var compatibleProfile = new RelationQueryTargetCapabilityProfile(
                inputs.TargetProfile.Target,
                inputs.TargetProfile.Id,
                [changedPlan.DefinitionSchemaVersion],
                [changedPlan.CompilerProfile],
                inputs.TargetProfile.Capabilities,
                inputs.TargetProfile.OperatingBoundaries,
                inputs.TargetProfile.Description);
            Assert.NotEqual(
                baseline,
                BuildReport(inputs with { Plan = changedPlan, TargetProfile = compatibleProfile }).Fingerprint);
        }
    }

    [Fact]
    public void Compute_DistinguishesStructuralOriginsByBinding()
    {
        var baselineInputs = CreateInputs(metadata: "binding");
        var structuralIndex = Enumerable.Range(0, baselineInputs.Requirements.Length).Single(index =>
            baselineInputs.Requirements[index].Id == StructuralRequirementId);
        var structural = baselineInputs.Requirements[structuralIndex];
        var originalOrigin = structural.Origin!;
        var changedOrigin = new RelationQueryRealizationRequirementOrigin(
            originalOrigin.Input,
            originalOrigin.Node,
            originalOrigin.SemanticSite,
            originalOrigin.ExpressionPath,
            originalOrigin.FieldPath,
            new("right"));
        var changedRequirements = baselineInputs.Requirements.SetItem(
            structuralIndex,
            new RelationQueryRealizationRequirement(
                structural.Id,
                structural.Capability,
                changedOrigin,
                structural.Uses));

        Assert.NotEqual(
            BuildReport(baselineInputs).Fingerprint,
            BuildReport(baselineInputs with { Requirements = changedRequirements }).Fingerprint);
    }

    [Fact]
    public void Policy_NormalizesAndRoundTripsCompositionRuleSelections()
    {
        var firstCapability = new GuaranteeRelationQueryCapability(
            RelationQueryGuaranteeCapabilityKind.DeterministicResult);
        var secondCapability = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter);
        var policy = new RelationQueryRealizationPolicy(
            new("policy/selection-test"),
            "conventions/v1",
            compositionRuleSelections:
            [
                new(secondCapability, new("rule/filter")),
                new(firstCapability, new("rule/determinism"))
            ]);

        Assert.Equal(secondCapability, policy.CompositionRuleSelections[0].Capability);
        Assert.Equal(firstCapability, policy.CompositionRuleSelections[1].Capability);
        Assert.Throws<ArgumentException>(() => new RelationQueryRealizationPolicy(
            new("policy/duplicate-selection"),
            "conventions/v1",
            compositionRuleSelections:
            [
                new(firstCapability, new("rule/one")),
                new(firstCapability, new("rule/two"))
            ]));

        var options = RelationQueryJsonSerializer.CreateOptions();
        var json = JsonSerializer.Serialize(policy, options);
        var roundTrip = JsonSerializer.Deserialize<RelationQueryRealizationPolicy>(json, options);

        Assert.NotNull(roundTrip);
        Assert.Equal(
            policy.CompositionRuleSelections.Select(static selection => selection.Capability),
            roundTrip.CompositionRuleSelections.Select(static selection => selection.Capability));
        Assert.Equal(
            policy.CompositionRuleSelections.Select(static selection => selection.Rule),
            roundTrip.CompositionRuleSelections.Select(static selection => selection.Rule));
    }

    [Fact]
    public void PrimitiveCapability_BatchedPredicateLookupPreservesWireIdentityAndRoundTrips()
    {
        RelationQueryCapability capability = new PrimitiveRelationQueryCapability(
            RelationQueryPrimitiveCapabilityKind.BatchedPredicateLookup);
        var options = RelationQueryJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(capability, options);
        var roundTrip = JsonSerializer.Deserialize<RelationQueryCapability>(json, options);

        Assert.Equal(18, (int)RelationQueryPrimitiveCapabilityKind.BatchedPredicateLookup);
        Assert.Contains("\"kind\":\"BatchedPredicateLookup\"", json, StringComparison.Ordinal);
        Assert.Equal(capability, roundTrip);
    }

    static readonly RelationQueryRealizationRequirementId LogicalRequirementId =
        new("requirement/logical/filter");
    static readonly RelationQueryRealizationRequirementId GuaranteeRequirementId =
        new("requirement/guarantee/deterministic-result");
    static readonly RelationQueryRealizationRequirementId StructuralRequirementId =
        new("requirement/structural/projection-target/nested-field");

    static ReportInputs CreateInputs(string metadata, bool reverseDeclarations = false)
    {
        var filter = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter);
        var determinism = new GuaranteeRelationQueryCapability(
            RelationQueryGuaranteeCapabilityKind.DeterministicResult);
        var structural = new StructuralRelationQueryCapability(
            RelationQueryStructuralCapabilityRole.ProjectionTarget,
            RelationQueryStructuralPathKind.NestedField);
        var stableSort = new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.StableSort);
        var unused = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Source);
        var outputShape = new QualifiedShapeId(new("dto"), new("LoadSearchDto"));
        var output = new RelationQueryRealizationOutputReference(
            new("output/query/rows/customer-name"),
            RelationQueryOutputReferenceKind.QueryResult,
            new("node/projection"),
            outputShape,
            queryResult: new("rows"),
            field: new(outputShape, FieldPath.Parse("Customer.Name")));
        var filterTrace = new RelationQueryRealizationTrace(
        [
            new(RelationQueryRealizationTraceStepKind.Terminal, new("node/result")),
            new(RelationQueryRealizationTraceStepKind.Structural, new("node/projection")),
            new(
                RelationQueryRealizationTraceStepKind.ExpressionSite,
                new("node/filter"),
                RelationQueryExpressionSiteKind.FilterPredicate,
                new("site/filter/predicate"),
                ordinal: 0)
        ]);
        var structuralTrace = new RelationQueryRealizationTrace(
        [
            new(RelationQueryRealizationTraceStepKind.Terminal, new("node/result")),
            new(RelationQueryRealizationTraceStepKind.Structural, new("node/projection"))
        ]);
        ImmutableArray<RelationQueryRealizationRequirement> requirements =
        [
            new(
                LogicalRequirementId,
                filter,
                new(
                    input: new("input/load"),
                    node: new("node/filter"),
                    semanticSite: "filter/predicate",
                    expressionPath: "$.arguments[0]",
                    fieldPath: FieldPath.Parse("Customer.Id"),
                    binding: new("left")),
                [
                    new(
                        output,
                        RelationQueryRequirementEffect.Membership,
                        QueryInputRequirement.Required,
                        [filterTrace])
                ]),
            new(GuaranteeRequirementId, determinism),
            new(
                StructuralRequirementId,
                structural,
                new(
                    node: new("node/projection"),
                    semanticSite: "projection/customer-name",
                    fieldPath: FieldPath.Parse("Customer.Name"),
                    binding: new("left")),
                [
                    new(
                        output,
                        RelationQueryRequirementEffect.Value,
                        QueryInputRequirement.Required,
                        [structuralTrace])
                ])
        ];

        var batchBoundaryId = new RelationQueryOperatingBoundaryId("boundary/max-input-rows");
        var providerBoundaryId = new RelationQueryOperatingBoundaryId("boundary/deterministic-provider");
        var unusedBoundaryId = new RelationQueryOperatingBoundaryId("boundary/unused-page-size");
        var boundaries = ImmutableArray.Create(
            new RelationQueryOperatingBoundary(
                batchBoundaryId,
                RelationQueryOperatingBoundaryKind.MaximumInputRows,
                1024,
                $"batch {metadata}"),
            new RelationQueryOperatingBoundary(
                providerBoundaryId,
                RelationQueryOperatingBoundaryKind.DeterministicProvider,
                description: $"provider {metadata}"),
            new RelationQueryOperatingBoundary(
                unusedBoundaryId,
                RelationQueryOperatingBoundaryKind.MaximumPageSize,
                50,
                $"unused boundary {metadata}"));
        var filterEvidenceId = new RelationQueryTargetCapabilityEvidenceId("evidence/filter");
        var stableSortEvidenceId = new RelationQueryTargetCapabilityEvidenceId("evidence/stable-sort");
        var structuralEvidenceId = new RelationQueryTargetCapabilityEvidenceId("evidence/structural");
        var batchValidationEvidenceId = new RelationQueryTargetCapabilityEvidenceId("evidence/batch-validation");
        var providerValidationEvidenceId = new RelationQueryTargetCapabilityEvidenceId("evidence/provider-validation");
        var evidence = ImmutableArray.Create(
            new RelationQueryTargetCapabilityEvidence(
                filterEvidenceId,
                filter,
                description: $"filter evidence {metadata}"),
            new RelationQueryTargetCapabilityEvidence(
                new("evidence/filter-alternate"),
                filter,
                description: $"alternate filter evidence {metadata}"),
            new RelationQueryTargetCapabilityEvidence(
                stableSortEvidenceId,
                stableSort,
                [batchBoundaryId],
                $"sort evidence {metadata}"),
            new RelationQueryTargetCapabilityEvidence(
                structuralEvidenceId,
                structural,
                [providerBoundaryId],
                $"structural evidence {metadata}"),
            new RelationQueryTargetCapabilityEvidence(
                batchValidationEvidenceId,
                new OperatingBoundaryValidationRelationQueryCapability(batchBoundaryId)),
            new RelationQueryTargetCapabilityEvidence(
                providerValidationEvidenceId,
                new OperatingBoundaryValidationRelationQueryCapability(providerBoundaryId)),
            new RelationQueryTargetCapabilityEvidence(
                new("evidence/unused"),
                unused,
                [unusedBoundaryId],
                $"unused evidence {metadata}"));
        var profile = new RelationQueryTargetCapabilityProfile(
            new("target/test"),
            new("target/profile/v1"),
            ["relation-query/v1"],
            ["compiler/v1"],
            evidence,
            boundaries,
            $"profile {metadata}");

        var determinismRuleId = new RelationQueryCompositionRuleId("rule/determinism/v1");
        var rules = ImmutableArray.Create(
            new RelationQueryCompositionRule(
                determinismRuleId,
                determinism,
                [stableSort],
                [batchBoundaryId],
                [RelationQueryGuaranteeCapabilityKind.DeterministicResult],
                $"determinism rule {metadata}"),
            new RelationQueryCompositionRule(
                new("rule/unused/v1"),
                new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Aggregation),
                [new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.LocalAggregation)],
                description: $"unused rule {metadata}"));
        var structuralOverrideId = new RelationQueryRealizationOverrideId("override/structural");
        var overrides = ImmutableArray.Create(
            new RelationQueryRealizationOverride(
                structuralOverrideId,
                StructuralRequirementId,
                structural,
                [structuralEvidenceId, providerValidationEvidenceId],
                [providerBoundaryId],
                [RelationQueryGuaranteeCapabilityKind.OutputIdentity],
                $"structural override {metadata}"),
            new RelationQueryRealizationOverride(
                new("override/unused"),
                new("requirement/unused"),
                unused,
                justification: $"unused override {metadata}"));
        var selections = ImmutableArray.Create(
            new RelationQueryCompositionRuleSelection(determinism, determinismRuleId));
        var severityOverrides = ImmutableArray.Create(
            new RelationQueryRealizationDiagnosticSeverityOverride("REL2997", DiagnosticSeverity.Info),
            new RelationQueryRealizationDiagnosticSeverityOverride("REL2998", DiagnosticSeverity.Warning),
            new RelationQueryRealizationDiagnosticSeverityOverride("REL2999", DiagnosticSeverity.Info));
        var policy = new RelationQueryRealizationPolicy(
            new("policy/test/v1"),
            "conventions/v1",
            RelationQueryRealizationPreference.PreferNative,
            RelationQueryConstrainedRealizationPolicy.AllowValidated,
            rules,
            selections,
            overrides,
            severityOverrides);
        ImmutableArray<RelationQueryRealizationDecision> decisions =
        [
            new NativeRelationQueryRealizationDecision(
                LogicalRequirementId,
                [filterEvidenceId]),
            new ConstrainedRelationQueryRealizationDecision(
                GuaranteeRequirementId,
                [stableSortEvidenceId, batchValidationEvidenceId],
                [
                    new(
                        batchBoundaryId,
                        RelationQueryOperatingBoundaryValidationKind.TargetEnforced,
                        batchValidationEvidenceId)
                ],
                [determinismRuleId],
                [RelationQueryGuaranteeCapabilityKind.DeterministicResult]),
            new OverrideRelationQueryRealizationDecision(
                StructuralRequirementId,
                structuralOverrideId,
                [structuralEvidenceId, providerValidationEvidenceId],
                [
                    new(
                        providerBoundaryId,
                        RelationQueryOperatingBoundaryValidationKind.TargetEnforced,
                        providerValidationEvidenceId)
                ],
                [RelationQueryGuaranteeCapabilityKind.OutputIdentity])
        ];
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics =
        [
            new(
                "REL2998",
                DiagnosticSeverity.Warning,
                $"human-facing rule diagnostic {metadata}",
                GuaranteeRequirementId,
                stableSortEvidenceId,
                determinismRuleId,
                batchBoundaryId,
                node: new("node/order"),
                semanticSite: "ordering"),
            new(
                "REL2999",
                DiagnosticSeverity.Info,
                $"human-facing override diagnostic {metadata}",
                StructuralRequirementId,
                structuralEvidenceId,
                operatingBoundary: providerBoundaryId,
                @override: structuralOverrideId,
                node: new("node/projection"),
                semanticSite: "projection/customer-name")
        ];
        var plan = new RelationQueryCompiledPlanReference(
            "compiler/v1",
            "relation-query/v1",
            new("sha256", "relation-query-definition/v1", "definition-hash"),
            new("sha256", "relation-query-plan-shapes/v1", "shapes-hash"),
            null,
            new("sha256", "relation-query-plan-demand/v1", "demand-hash"),
            [new("input/load"), new("input/customer")]);

        if (reverseDeclarations)
        {
            requirements = [.. requirements.Reverse()];
            decisions = [.. decisions.Reverse()];
            diagnostics = [.. diagnostics.Reverse()];
            profile = CopyProfile(
                profile,
                capabilities: [.. profile.Capabilities.Reverse()],
                boundaries: [.. profile.OperatingBoundaries.Reverse()]);
            policy = CopyPolicy(
                policy,
                compositionRules: [.. policy.CompositionRules.Reverse()],
                compositionRuleSelections: [.. policy.CompositionRuleSelections.Reverse()],
                overrides: [.. policy.Overrides.Reverse()],
                diagnosticSeverityOverrides: [.. policy.DiagnosticSeverityOverrides.Reverse()]);
        }

        return new(
            plan,
            profile,
            policy,
            requirements,
            decisions,
            diagnostics,
            RelationQueryRealizationStatus.Realizable);
    }

    static RelationQueryRealizationReport BuildReport(ReportInputs inputs)
    {
        var fingerprint = RelationQueryRealizationFingerprinter.Compute(
            inputs.Plan,
            inputs.TargetProfile,
            inputs.Policy,
            inputs.Requirements,
            inputs.Decisions,
            inputs.Diagnostics,
            inputs.Status);
        return new(
            inputs.Plan,
            inputs.TargetProfile,
            inputs.Policy,
            inputs.Requirements,
            inputs.Decisions,
            inputs.Diagnostics,
            inputs.Status,
            fingerprint);
    }

    static RelationQueryTargetCapabilityProfile CopyProfile(
        RelationQueryTargetCapabilityProfile source,
        RelationQueryTargetProfileId? id = null,
        ImmutableArray<RelationQueryTargetCapabilityEvidence>? capabilities = null,
        ImmutableArray<RelationQueryOperatingBoundary>? boundaries = null) =>
        new(
            source.Target,
            id ?? source.Id,
            source.SupportedDefinitionSchemaVersions,
            source.SupportedCompilerProfiles,
            capabilities ?? source.Capabilities,
            boundaries ?? source.OperatingBoundaries,
            source.Description);

    static RelationQueryRealizationPolicy CopyPolicy(
        RelationQueryRealizationPolicy source,
        RelationQueryRealizationPreference? preference = null,
        ImmutableArray<RelationQueryCompositionRule>? compositionRules = null,
        ImmutableArray<RelationQueryCompositionRuleSelection>? compositionRuleSelections = null,
        ImmutableArray<RelationQueryRealizationOverride>? overrides = null,
        ImmutableArray<RelationQueryRealizationDiagnosticSeverityOverride>? diagnosticSeverityOverrides = null) =>
        new(
            source.Id,
            source.ConventionSetVersion,
            preference ?? source.Preference,
            source.ConstrainedRealizations,
            compositionRules ?? source.CompositionRules,
            compositionRuleSelections ?? source.CompositionRuleSelections,
            overrides ?? source.Overrides,
            diagnosticSeverityOverrides ?? source.DiagnosticSeverityOverrides);

    sealed record ReportInputs(
        RelationQueryCompiledPlanReference Plan,
        RelationQueryTargetCapabilityProfile TargetProfile,
        RelationQueryRealizationPolicy Policy,
        ImmutableArray<RelationQueryRealizationRequirement> Requirements,
        ImmutableArray<RelationQueryRealizationDecision> Decisions,
        ImmutableArray<RelationQueryRealizationDiagnostic> Diagnostics,
        RelationQueryRealizationStatus Status);
}
