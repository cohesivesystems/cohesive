using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryBoundRealizationCompilerTests
{
    const string AssessmentAuthority = "tests/bound-realization-evidence/v1";
    static readonly LogicalRelationQueryCapability Join = new(RelationQueryLogicalCapabilityKind.Join);
    static readonly RelationQueryGuaranteeCapabilityKind JoinMembership =
        RelationQueryGuaranteeCapabilityKind.JoinMembership;
    static readonly RelationQueryOperatingBoundaryId MaterializedInputs = new("boundary/materialized-inputs");
    static readonly RelationQueryOperatingBoundaryId UnrelatedBoundary = new("boundary/unrelated");
    static readonly RelationQueryTargetCapabilityEvidenceId UnrelatedEvidence = new("evidence/unrelated");

    [Fact]
    public void AdapterBindingReference_SemanticEqualityComparesCanonicalEvidenceByValue()
    {
        var request = CreateRequest(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var expected = CreateBinding(request);
        RelationQueryAdapterBindingReference equivalent = new(
            expected.SchemaVersion,
            expected.BindingId,
            expected.Target,
            expected.TargetProfile,
            expected.Fingerprint with { },
            expected.CompiledPlanFingerprint! with { },
            expected.PlacementFingerprint! with { },
            [.. expected.Sources],
            [.. expected.PlacementBindings],
            [.. expected.ConfigurationDecisions]);
        var changed = CreateBinding(
            request,
            configurationDecisions:
            [
                new(
                    "compilerProfile",
                    RelationQueryConfigurationValueOrigin.AdapterConvention,
                    "tests/compiler-v2")
            ]);

        Assert.True(expected.HasSameSemantics(equivalent));
        Assert.True(equivalent.HasSameSemantics(expected));
        Assert.False(expected.HasSameSemantics(changed));
        Assert.False(expected.HasSameSemantics(null));
    }

    [Fact]
    public void Compile_AcceptsCompleteEvidenceAndFingerprintsIndependentOfAssessmentOrder()
    {
        var request = CreateRequest(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var projection = CreateCompleteProjection(request);
        Assert.True(projection.Assessments.Length > 1);

        var first = RelationQueryBoundRealizationCompiler.Compile(request, projection);
        var second = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(projection.Binding, [.. projection.Assessments.Reverse()]));

        Assert.True(first.IsRealizable);
        Assert.Equal(RelationQueryRealizationStatus.Realizable, first.Status);
        Assert.Empty(first.Diagnostics);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Fingerprint, RelationQueryBoundRealizationFingerprinter.Compute(first));

        var json = JsonSerializer.Serialize(first, RelationQueryJsonSerializer.CreateOptions());
        var roundTrip = JsonSerializer.Deserialize<RelationQueryBoundRealizationReport>(
            json,
            RelationQueryJsonSerializer.CreateOptions());
        Assert.NotNull(roundTrip);
        Assert.Equal(first.Fingerprint, roundTrip.Fingerprint);
        Assert.Equal(json, JsonSerializer.Serialize(roundTrip, RelationQueryJsonSerializer.CreateOptions()));
    }

    [Fact]
    public void Compile_ReportsUnavailableEvidenceWithExactAttribution()
    {
        var request = CreateRequest(LoadCustomerRelationFixture.BaselineRelationDocument);
        var projection = CreateCompleteProjection(request);
        var available = Assert.Single(projection.Assessments);
        var missingEvidence = available.CapabilityEvidence[0];
        RelationQueryAdapterDecisionCode adapterDecisionCode = new("tests/context/REL-TARGET-001");
        var unavailable = CopyAssessment(
            available,
            status: RelationQueryBoundAssessmentStatus.Unavailable,
            unavailableReason: RelationQueryUnavailableReason.OperatingBoundaryInvalid,
            resolution: "Bind the relation to a source that guarantees materialized inputs.",
            adapterDecisionCode: adapterDecisionCode,
            missingCapabilityEvidence: [missingEvidence],
            failedConfigurationSetting: "field/missing-input");

        var report = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(projection.Binding, [unavailable]));

        Assert.False(report.IsRealizable);
        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, report.Status);
        var diagnostic = Assert.Single(report.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
        Assert.Equal(unavailable.Id, diagnostic.ContextEvidence);
        Assert.Equal(unavailable.Branch, diagnostic.Branch);
        Assert.Equal(unavailable.Requirement, diagnostic.Requirement);
        Assert.Equal(unavailable.Resolution, diagnostic.Resolution);
        Assert.Equal(unavailable.Origin, diagnostic.ConfigurationOrigin);
        Assert.Equal(unavailable.Authority, diagnostic.ConfigurationAuthority);
        Assert.Equal(missingEvidence, diagnostic.CapabilityEvidence);
        Assert.Equal("field/missing-input", diagnostic.BindingSetting);
        Assert.Equal(adapterDecisionCode, diagnostic.AdapterDecisionCode);

        var nativeDiagnostics = RelationQueryNativeCompilationDiagnostic.FromBoundRealizationFailure(report);
        var nativeDiagnostic = Assert.Single(nativeDiagnostics, item => item.Code == diagnostic.Code);
        Assert.Equal(unavailable.Id, nativeDiagnostic.ContextEvidence);
        Assert.Equal(unavailable.Branch, nativeDiagnostic.Branch);
        Assert.Equal(unavailable.Requirement, nativeDiagnostic.Requirement);
        Assert.Equal(unavailable.Resolution, nativeDiagnostic.Resolution);
        Assert.Equal(unavailable.Origin, nativeDiagnostic.ConfigurationOrigin);
        Assert.Equal(unavailable.Authority, nativeDiagnostic.ConfigurationAuthority);
        Assert.Equal(missingEvidence, nativeDiagnostic.CapabilityEvidence);
        Assert.Equal("field/missing-input", nativeDiagnostic.BindingSetting);
        Assert.Equal(adapterDecisionCode, nativeDiagnostic.AdapterDecisionCode);
    }

    [Theory]
    [InlineData("assessment")]
    [InlineData("capability")]
    [InlineData("boundary")]
    [InlineData("guarantee")]
    public void Compile_FailsClosedWhenRequiredContextualProofIsIncomplete(string omittedProof)
    {
        var request = CreateRequest(LoadCustomerRelationFixture.BaselineRelationDocument);
        var projection = CreateCompleteProjection(request);
        var complete = Assert.Single(projection.Assessments);
        ImmutableArray<RelationQueryBoundRequirementAssessment> assessments = omittedProof switch
        {
            "assessment" => [],
            "capability" => [CopyAssessment(complete, capabilityEvidence: [])],
            "boundary" => [CopyAssessment(complete, operatingBoundaries: [])],
            "guarantee" => [CopyAssessment(complete, preservedGuarantees: [])],
            _ => throw new ArgumentOutOfRangeException(nameof(omittedProof), omittedProof, null)
        };

        var report = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(projection.Binding, assessments));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        var diagnostic = Assert.Single(report.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextEvidenceIncomplete);
        Assert.Equal(complete.Branch, diagnostic.Branch);
        Assert.Equal(complete.Requirement, diagnostic.Requirement);
        Assert.DoesNotContain(report.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Compile_RejectsMissingOrStaleAdapterBindingAffinity(bool includeAffinity, bool staleAffinity)
    {
        var request = CreateRequest(LoadCustomerRelationFixture.BaselineRelationDocument);
        var complete = CreateCompleteProjection(request);
        var binding = CreateBinding(request, includeAffinity, staleAffinity);

        var report = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(binding, complete.Assessments));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.Contains(report.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextAffinityMismatch);
    }

    [Fact]
    public void Compile_RejectsBindingReferenceThatOmitsSelectedAcquiredSourcesAndPlacements()
    {
        var request = CreateRequest(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var complete = CreateCompleteProjection(request);
        var binding = complete.Binding;
        var incomplete = new RelationQueryAdapterBindingReference(
            binding.SchemaVersion,
            binding.BindingId,
            binding.Target,
            binding.TargetProfile,
            binding.Fingerprint,
            binding.CompiledPlanFingerprint,
            binding.PlacementFingerprint,
            sources: [],
            placementBindings: [],
            configurationDecisions: binding.ConfigurationDecisions);

        var report = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(incomplete, complete.Assessments));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        var diagnostic = Assert.Single(report.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextAffinityMismatch);
        Assert.Contains("coverage", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RejectsForeignConfigurationAttribution()
    {
        const string setting = "binding/collection-scope";
        var request = CreateRequest(LoadCustomerRelationFixture.BaselineRelationDocument);
        var complete = CreateCompleteProjection(request);
        var assessment = Assert.Single(complete.Assessments);
        var binding = CreateBinding(
            request,
            configurationDecisions:
            [
                new(
                    setting,
                    RelationQueryConfigurationValueOrigin.Explicit,
                    "tests/exact-local-binding/v1")
            ]);
        var foreign = CopyAssessment(
            assessment,
            origin: RelationQueryConfigurationValueOrigin.AdapterConvention,
            authority: "tests/foreign-adapter-convention/v1",
            configurationSetting: setting);

        var report = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(binding, [foreign]));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        var diagnostic = Assert.Single(report.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextInvalid);
        Assert.Equal(foreign.Id, diagnostic.ContextEvidence);
        Assert.Equal(setting, diagnostic.BindingSetting);
    }

    [Fact]
    public void Compile_RejectsProofIdentifiersUnrelatedToTheRequirementDecision()
    {
        var request = CreateRequest(LoadCustomerRelationFixture.BaselineRelationDocument);
        var projection = CreateCompleteProjection(request);
        var assessment = Assert.Single(projection.Assessments);
        var unrelated = CopyAssessment(
            assessment,
            capabilityEvidence: [.. assessment.CapabilityEvidence, UnrelatedEvidence],
            operatingBoundaries: [.. assessment.OperatingBoundaries, UnrelatedBoundary]);

        var report = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(projection.Binding, [unrelated]));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        var diagnostic = Assert.Single(report.Diagnostics, static item =>
            item.Code == RelationQueryRealizationDiagnosticCodes.ContextInvalid);
        Assert.Equal(unrelated.Id, diagnostic.ContextEvidence);
    }

    [Fact]
    public void Compile_RejectsFieldAndPlacementEvidenceThatDoesNotOwnTheAttributedInput()
    {
        var request = CreateRequest(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var projection = CreateCompleteProjection(request);
        var assessment = projection.Assessments[0];
        var source = Assert.Single(request.Plan.InputContract.Sources);
        Assert.True(source.Fields.Length > 1);
        var firstField = source.Fields[0].Input;
        var secondField = source.Fields[1].Input;
        var mismatchedField = CopyAssessment(
            assessment,
            node: firstField.Producer,
            input: firstField.Id,
            field: secondField.Field.Path,
            placementBinding: request.Placement.Bindings[0].Id);

        var fieldReport = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(projection.Binding, [mismatchedField, .. projection.Assessments.Skip(1)]));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, fieldReport.Status);
        Assert.Contains(fieldReport.Diagnostics, item =>
            item.Code == RelationQueryRealizationDiagnosticCodes.ContextInvalid
            && item.ContextEvidence == mismatchedField.Id);

        var traversalField = Assert.Single(request.Plan.InputContract.Traversals).Fields[0].Input;
        var mismatchedPlacement = CopyAssessment(
            assessment,
            node: traversalField.Producer,
            input: traversalField.Id,
            field: traversalField.Field.Path,
            placementBinding: request.Placement.Bindings[0].Id);
        var placementReport = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(projection.Binding, [mismatchedPlacement, .. projection.Assessments.Skip(1)]));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, placementReport.Status);
        Assert.Contains(placementReport.Diagnostics, item =>
            item.Code == RelationQueryRealizationDiagnosticCodes.ContextInvalid
            && item.ContextEvidence == mismatchedPlacement.Id);
    }

    [Fact]
    public void Fingerprint_NormalizesDiagnosticOrderAndCoversEveryAttributionField()
    {
        var request = CreateRequest(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var projection = CreateCompleteProjection(request);
        var branch = request.Branches[0];
        var requirement = request.GetRequirementsForBranch(branch)[0];
        var sourceFields = Assert.Single(request.Plan.InputContract.Sources).Fields;
        Assert.True(sourceFields.Length > 1);
        var firstField = sourceFields[0].Input;
        var secondField = sourceFields[1].Input;

        RelationQueryRealizationDiagnostic Diagnostic(
            string code = "REL2990",
            DiagnosticSeverity severity = DiagnosticSeverity.Warning,
            string? requirementId = null,
            string capability = "evidence/a",
            string composition = "composition/a",
            string boundary = "boundary/a",
            string @override = "override/a",
            string? node = null,
            string semanticSite = "site/a",
            string context = "context/a",
            string? branchId = null,
            string? input = null,
            FieldPath? field = null,
            string? placement = null,
            string bindingSetting = "binding/a",
            string message = "Diagnostic prose.",
            string resolution = "Resolution prose.",
            RelationQueryConfigurationValueOrigin configurationOrigin =
                RelationQueryConfigurationValueOrigin.AdapterConvention,
            string configurationAuthority = "authority/a",
            string adapterDecisionCode = "adapter-decision/a") => new(
            code,
            severity,
            message,
            new(requirementId ?? requirement.Id.Value),
            new(capability),
            new(composition),
            new(boundary),
            new(@override),
            new(node ?? firstField.Producer.Value),
            semanticSite,
            new(context),
            new(branchId ?? branch.Id.Value),
            new(input ?? firstField.Id.Value),
            field ?? firstField.Field.Path,
            new(placement ?? request.Placement.Bindings[0].Id.Value),
            bindingSetting,
            resolution,
            configurationOrigin,
            configurationAuthority,
            new(adapterDecisionCode));

        RelationQueryBoundRealizationFingerprint Compute(
            ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics) =>
            RelationQueryBoundRealizationFingerprinter.Compute(
                request.ProfileFeasibility,
                request.Placement.Fingerprint,
                [.. request.Branches.Select(static item => item.Id)],
                projection,
                diagnostics,
                RelationQueryRealizationStatus.Realizable);

        var first = Diagnostic();
        var second = Diagnostic(capability: "evidence/b", message: "Second diagnostic.");
        Assert.Equal(Compute([first, second]), Compute([second, first]));

        var baseline = Compute([first]);
        RelationQueryRealizationDiagnostic[] attributionChanges =
        [
            Diagnostic(code: "REL2991"),
            Diagnostic(severity: DiagnosticSeverity.Info),
            Diagnostic(requirementId: "requirement/other"),
            Diagnostic(capability: "evidence/other"),
            Diagnostic(composition: "composition/other"),
            Diagnostic(boundary: "boundary/other"),
            Diagnostic(@override: "override/other"),
            Diagnostic(node: "node/other"),
            Diagnostic(semanticSite: "site/other"),
            Diagnostic(context: "context/other"),
            Diagnostic(branchId: "branch/other"),
            Diagnostic(input: secondField.Id.Value),
            Diagnostic(field: secondField.Field.Path),
            Diagnostic(placement: "placement/other"),
            Diagnostic(bindingSetting: "binding/other"),
            Diagnostic(configurationOrigin: RelationQueryConfigurationValueOrigin.ScopedProfile),
            Diagnostic(configurationAuthority: "authority/other"),
            Diagnostic(adapterDecisionCode: "adapter-decision/other")
        ];
        Assert.All(attributionChanges, diagnostic => Assert.NotEqual(baseline, Compute([diagnostic])));
        Assert.Equal(
            baseline,
            Compute([Diagnostic(message: "Different prose.", resolution: "Different resolution prose.")]));
        Assert.Throws<ArgumentException>(() => new RelationQueryRealizationDiagnostic(
            "REL2992",
            DiagnosticSeverity.Error,
            "Invalid attribution.",
            configurationOrigin: RelationQueryConfigurationValueOrigin.Explicit));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDiagnostic(
            "REL2993",
            DiagnosticSeverity.Error,
            "Invalid attribution.",
            configurationAuthority: "authority/unpaired"));
    }

    [Fact]
    public void Fingerprint_CoversTypedContextualFailureAttribution()
    {
        var request = CreateRequest(LoadCustomerRelationFixture.BaselineRelationDocument);
        var projection = CreateCompleteProjection(request);
        var available = Assert.Single(projection.Assessments);
        var decision = Assert.Single(request.ProfileFeasibility.Decisions);
        var evidence = decision.GetCapabilityEvidence()[0];
        var boundary = Assert.Single(decision.GetTargetEnforcedBoundaries());

        RelationQueryBoundRequirementAssessment Failure(
            string adapterDecisionCode = "tests/context-unavailable/a",
            ImmutableArray<RelationQueryTargetCapabilityEvidenceId>? missingCapabilityEvidence = null,
            RelationQueryOperatingBoundaryId? failedOperatingBoundary = null,
            string failedConfigurationSetting = "binding/setting-a") => CopyAssessment(
            available,
            status: RelationQueryBoundAssessmentStatus.Unavailable,
            capabilityEvidence: [],
            operatingBoundaries: [],
            preservedGuarantees: [],
            unavailableReason: RelationQueryUnavailableReason.CapabilityNotAdvertised,
            adapterDecisionCode: new(adapterDecisionCode),
            missingCapabilityEvidence: missingCapabilityEvidence ?? [evidence],
            failedOperatingBoundary: failedOperatingBoundary ?? boundary,
            failedConfigurationSetting: failedConfigurationSetting);

        RelationQueryBoundRealizationFingerprint Compute(
            RelationQueryBoundRequirementAssessment assessment) =>
            RelationQueryBoundRealizationFingerprinter.Compute(
                request.ProfileFeasibility,
                request.Placement.Fingerprint,
                [.. request.Branches.Select(static branch => branch.Id)],
                new(projection.Binding, [assessment]),
                [],
                RelationQueryRealizationStatus.NotRealizable);

        var baseline = Compute(Failure());
        Assert.NotEqual(baseline, Compute(Failure(adapterDecisionCode: "tests/context-unavailable/b")));
        Assert.NotEqual(baseline, Compute(Failure(missingCapabilityEvidence: [])));
        Assert.NotEqual(
            baseline,
            Compute(Failure(failedOperatingBoundary: UnrelatedBoundary)));
        Assert.NotEqual(
            baseline,
            Compute(Failure(failedConfigurationSetting: "binding/setting-b")));
    }

    [Fact]
    public void Compile_AppliesGlobalRequirementsToEverySelectedBranch()
    {
        var allBranches = CreateRequest(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        Assert.True(allBranches.Branches.Length > 1);
        var selected = new RelationQueryBoundRealizationRequest(
            allBranches.Plan,
            allBranches.ProfileFeasibility,
            allBranches.Placement,
            [allBranches.Branches[0].Id]);
        var selectedProjection = CreateCompleteProjection(selected);

        var selectedReport = RelationQueryBoundRealizationCompiler.Compile(selected, selectedProjection);
        var allReport = RelationQueryBoundRealizationCompiler.Compile(
            allBranches,
            new(CreateBinding(allBranches), selectedProjection.Assessments));

        Assert.True(selectedReport.IsRealizable);
        Assert.Equal(RelationQueryRealizationStatus.Invalid, allReport.Status);
        Assert.Equal(
            allBranches.Branches.Length - 1,
            allReport.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextEvidenceIncomplete));
    }

    static RelationQueryBoundRealizationRequest CreateRequest(
        RelationQueryDocument document,
        ImmutableArray<RelationQueryNativeResultBranchId> branches = default)
    {
        var plan = Compile(document);
        var realization = CreateProfileFeasibility(plan);
        Assert.True(realization.IsRealizable);
        Assert.IsType<ConstrainedRelationQueryRealizationDecision>(Assert.Single(realization.Decisions));
        return new(plan, realization, CreatePlacement(plan), branches);
    }

    static CompiledRelationQueryPlan Compile(RelationQueryDocument document)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static RelationQueryRealizationReport CreateProfileFeasibility(CompiledRelationQueryPlan plan)
    {
        var planReference = RelationQueryCompiledPlanReference.From(plan);
        var requirement = new RelationQueryRealizationRequirement(
            new("requirement/global-join"),
            Join,
            requiredGuarantees: [JoinMembership]);
        var validator = new OperatingBoundaryValidationRelationQueryCapability(MaterializedInputs);
        var profile = new RelationQueryTargetCapabilityProfile(
            new("target/bound-realization-tests"),
            new("target/bound-realization-tests/v1"),
            [planReference.DefinitionSchemaVersion],
            [planReference.CompilerProfile],
            capabilities:
            [
                new(new("evidence/join"), Join, [MaterializedInputs]),
                new(new("evidence/join-membership"), new GuaranteeRelationQueryCapability(JoinMembership)),
                new(new("evidence/materialized-inputs"), validator),
                new(UnrelatedEvidence, new PrimitiveRelationQueryCapability(
                    RelationQueryPrimitiveCapabilityKind.FieldProjection)),
                new(new("evidence/unrelated-boundary"),
                    new OperatingBoundaryValidationRelationQueryCapability(UnrelatedBoundary))
            ],
            operatingBoundaries:
            [
                new(MaterializedInputs, RelationQueryOperatingBoundaryKind.MaterializedInputs),
                new(UnrelatedBoundary, RelationQueryOperatingBoundaryKind.MaterializedInputs)
            ]);
        var policy = new RelationQueryRealizationPolicy(
            new("policy/bound-realization-tests/v1"),
            "conventions/bound-realization-tests/v1",
            constrainedRealizations: RelationQueryConstrainedRealizationPolicy.AllowValidated);
        return RelationQueryRealizationCompiler.Match(planReference, [requirement], profile, policy);
    }

    static RelationQueryContextualEvidenceProjection CreateCompleteProjection(
        RelationQueryBoundRealizationRequest request)
    {
        var decisions = request.ProfileFeasibility.Decisions.ToDictionary(static decision => decision.Requirement);
        ImmutableArray<RelationQueryBoundRequirementAssessment>.Builder assessments =
            ImmutableArray.CreateBuilder<RelationQueryBoundRequirementAssessment>();
        foreach (var branch in request.Branches)
        {
            foreach (var requirement in request.GetRequirementsForBranch(branch))
            {
                var decision = decisions[requirement.Id];
                assessments.Add(new(
                    new($"context/{Uri.EscapeDataString(branch.Id.Value)}/{Uri.EscapeDataString(requirement.Id.Value)}"),
                    branch.Id,
                    requirement.Id,
                    RelationQueryBoundAssessmentStatus.Available,
                    RelationQueryConfigurationValueOrigin.AdapterConvention,
                    AssessmentAuthority,
                    decision.GetCapabilityEvidence(),
                    decision.GetTargetEnforcedBoundaries(),
                    decision.GetPreservedGuarantees(),
                    message: "The exact test binding preserves this branch requirement."));
            }
        }

        return new(CreateBinding(request), assessments.ToImmutable());
    }

    static RelationQueryAdapterBindingReference CreateBinding(
        RelationQueryBoundRealizationRequest request,
        bool includeAffinity = true,
        bool staleAffinity = false,
        ImmutableArray<RelationQueryConfigurationDecision> configurationDecisions = default)
    {
        var planFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.PlanReference);
        RelationQueryPlanComponentFingerprint? boundPlan = includeAffinity
            ? staleAffinity
                ? planFingerprint with { Value = new string('b', 64) }
                : planFingerprint
            : null;
        RelationQuerySourcePlacementFingerprint? placement = includeAffinity
            ? request.Placement.Fingerprint
            : null;
        return new(
            "tests/bound-realization-binding/v1",
            "binding/bound-realization-tests",
            request.ProfileFeasibility.TargetProfile.Target,
            request.ProfileFeasibility.TargetProfile.Id,
            new(
                "sha256",
                "tests/bound-realization-binding/v1-c14n/v1",
                new string('a', 64)),
            boundPlan,
            placement,
            [.. request.Placement.SourceInstances.Select(static source => source.Id)],
            [.. request.Placement.Bindings.Select(static binding => binding.Id)],
            configurationDecisions);
    }

    static RelationQueryBoundRequirementAssessment CopyAssessment(
        RelationQueryBoundRequirementAssessment assessment,
        RelationQueryBoundAssessmentStatus? status = null,
        RelationQueryConfigurationValueOrigin? origin = null,
        string? authority = null,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId>? capabilityEvidence = null,
        ImmutableArray<RelationQueryOperatingBoundaryId>? operatingBoundaries = null,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind>? preservedGuarantees = null,
        RelationQueryUnavailableReason? unavailableReason = null,
        QueryNodeId? node = null,
        RelationQueryInputId? input = null,
        FieldPath? field = null,
        RelationQuerySourcePlacementBindingId? placementBinding = null,
        string? configurationSetting = null,
        string? resolution = null,
        RelationQueryAdapterDecisionCode? adapterDecisionCode = null,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId>? missingCapabilityEvidence = null,
        RelationQueryOperatingBoundaryId? failedOperatingBoundary = null,
        string? failedConfigurationSetting = null,
        RelationQueryContextEvidenceId? blockedBy = null)
    {
        var effectiveStatus = status ?? assessment.Status;
        return new(
            assessment.Id,
            assessment.Branch,
            assessment.Requirement,
            effectiveStatus,
            origin ?? assessment.Origin,
            authority ?? assessment.Authority,
            capabilityEvidence ?? assessment.CapabilityEvidence,
            operatingBoundaries ?? assessment.OperatingBoundaries,
            preservedGuarantees ?? assessment.PreservedGuarantees,
            effectiveStatus == RelationQueryBoundAssessmentStatus.Available
                ? null
                : unavailableReason ?? RelationQueryUnavailableReason.CapabilityNotAdvertised,
            node ?? assessment.Node,
            input ?? assessment.Input,
            field ?? assessment.Field,
            placementBinding ?? assessment.PlacementBinding,
            configurationSetting ?? assessment.ConfigurationSetting,
            effectiveStatus == RelationQueryBoundAssessmentStatus.Available
                ? assessment.Message
                : "The exact adapter facts cannot preserve this requirement.",
            effectiveStatus == RelationQueryBoundAssessmentStatus.Available
                ? null
                : resolution ?? "Change the binding or select a capable target.",
            effectiveStatus == RelationQueryBoundAssessmentStatus.Available
                ? null
                : adapterDecisionCode ?? assessment.AdapterDecisionCode ?? new("tests/context-unavailable"),
            missingCapabilityEvidence ?? assessment.MissingCapabilityEvidence,
            failedOperatingBoundary ?? assessment.FailedOperatingBoundary,
            failedConfigurationSetting ?? assessment.FailedConfigurationSetting,
            blockedBy ?? assessment.BlockedBy);
    }

    static RelationQuerySourcePlacement CreatePlacement(CompiledRelationQueryPlan plan)
    {
        var source = Assert.Single(plan.InputContract.Sources);
        RelationQuerySourceInstanceId sourceId = new("source/bound-realization-tests");
        var sourceInstance = new RelationQuerySourceInstance(
            sourceId,
            new("domain/bound-realization-tests"),
            RelationQueryInMemoryInterpreter.DefaultTargetProfile,
            new(100, 1_000, 100, 4));
        var binding = new RelationQuerySourcePlacementBinding(
            new("placement/bound-realization-tests"),
            source.Input.Id,
            source.Node,
            source.Binding,
            source.Shape,
            sourceId,
            RelationQuerySourcePlacementBindingKind.SourceSet,
            source.Role == RelationQuerySourceInputRole.RelationRoot
                ? RelationQuerySourceAcquisitionKind.Supplied
                : RelationQuerySourceAcquisitionKind.BoundedEnumeration,
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
            "tests/bound-realization-placement/v1",
            [sourceInstance],
            [binding]);
    }
}
