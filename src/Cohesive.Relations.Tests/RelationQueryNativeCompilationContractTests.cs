using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryNativeCompilationContractTests
{
    [Fact]
    public void Request_NormalizesSelectedBranchesAndRejectsInvalidSelections()
    {
        var plan = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var realization = Realize(plan, RelationQueryResultObservability.NotRequested);
        var placement = CreatePlacement(plan);
        var all = new RelationQueryBoundRealizationRequest(plan, realization, placement);
        var reversedIds = all.Branches
            .Select(static branch => branch.Id)
            .Reverse()
            .ToImmutableArray();

        var selectedBoundRequest = new RelationQueryBoundRealizationRequest(
            plan,
            realization,
            placement,
            reversedIds);
        var selected = new RelationQueryNativeCompilationRequest(
            plan,
            Bind(selectedBoundRequest),
            placement);

        Assert.True(realization.IsRealizable);
        Assert.Equal(
            RelationQueryOccurrenceProvenanceMode.NotRequested,
            realization.Observability.OccurrenceProvenance);
        Assert.NotEmpty(selected.Branches);
        Assert.Equal(
            selected.Branches.Select(static branch => branch.Id.Value).Order(StringComparer.Ordinal),
            selected.Branches.Select(static branch => branch.Id.Value));
        Assert.All(selected.Branches, static branch => Assert.NotNull(branch.QueryResult));
        Assert.Empty(selected.ValidateInputs());

        Assert.Throws<ArgumentNullException>(() => new RelationQueryBoundRealizationRequest(
            null!,
            realization,
            placement));
        Assert.Throws<ArgumentException>(() => new RelationQueryBoundRealizationRequest(
            plan,
            realization,
            placement,
            []));
        Assert.Throws<ArgumentException>(() => new RelationQueryBoundRealizationRequest(
            plan,
            realization,
            placement,
            [default]));
        Assert.Throws<ArgumentException>(() => new RelationQueryBoundRealizationRequest(
            plan,
            realization,
            placement,
            [all.Branches[0].Id, all.Branches[0].Id]));
        Assert.Throws<ArgumentException>(() => new RelationQueryBoundRealizationRequest(
            plan,
            realization,
            placement,
            [new("query:absent")]));
    }

    [Fact]
    public void Selection_ProjectsCanonicalBranchScopesAndTheirUnionForBoundAndNativeRequests()
    {
        var plan = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var realization = Realize(plan, RelationQueryResultObservability.NotRequested);
        var placement = CreatePlacement(plan);
        var boundRequest = new RelationQueryBoundRealizationRequest(plan, realization, placement);
        var nativeRequest = new RelationQueryNativeCompilationRequest(
            plan,
            Bind(boundRequest),
            placement);

        var rows = boundRequest.Selection.GetBranch(
            new($"query:{LoadCustomerRelationFixture.RowsResultId.Value}"));
        var aggregate = boundRequest.Selection.GetBranch(
            new($"query:{LoadCustomerRelationFixture.AggregationResultId.Value}"));

        Assert.Contains(LoadCustomerRelationFixture.PageNodeId, rows.ReachableNodes);
        Assert.DoesNotContain(LoadCustomerRelationFixture.AggregateNodeId, rows.ReachableNodes);
        Assert.Contains(LoadCustomerRelationFixture.AggregateNodeId, aggregate.ReachableNodes);
        Assert.DoesNotContain(LoadCustomerRelationFixture.PageNodeId, aggregate.ReachableNodes);
        Assert.NotEmpty(rows.Fields);
        Assert.NotEmpty(aggregate.Fields);
        Assert.Equal(
            rows.Requirements.ToArray(),
            boundRequest.GetRequirementsForBranch(rows.Branch).ToArray());
        Assert.Equal(
            boundRequest.Selection.ReachableNodes.ToArray(),
            nativeRequest.Selection.ReachableNodes.ToArray());
        Assert.Equal(
            boundRequest.Selection.InputIds.ToArray(),
            nativeRequest.Selection.InputIds.ToArray());
        Assert.Equal(
            boundRequest.Selection.PlacementBindings.ToArray(),
            nativeRequest.Selection.PlacementBindings.ToArray());
        Assert.Equal(
            boundRequest.Selection.ReachableNodes.OrderBy(static node => node.Value, StringComparer.Ordinal),
            boundRequest.Selection.ReachableNodes);
        Assert.Equal(
            boundRequest.Selection.InputIds.OrderBy(static input => input.Value, StringComparer.Ordinal),
            boundRequest.Selection.InputIds);
        Assert.Contains(
            boundRequest.Selection.ReachableNodes,
            node => node == LoadCustomerRelationFixture.PageNodeId);
        Assert.Contains(
            boundRequest.Selection.ReachableNodes,
            node => node == LoadCustomerRelationFixture.AggregateNodeId);
    }

    [Fact]
    public void BranchSelection_AttributesFailuresAndValidatesInputAffinityDeterministically()
    {
        var plan = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var realization = Realize(plan, RelationQueryResultObservability.NotRequested);
        var placement = CreatePlacement(plan);
        var request = new RelationQueryBoundRealizationRequest(
            plan,
            realization,
            placement,
            [new($"query:{LoadCustomerRelationFixture.RowsResultId.Value}")]);
        var branch = Assert.Single(request.Selection.Branches);
        var attributed = branch.Requirements.First(static requirement => requirement.Origin?.Input is not null);
        var input = attributed.Origin!.Input!.Value;

        Assert.Same(attributed, branch.SelectRequirementForFailure(input, attributed.Origin.Node));
        Assert.True(branch.IsInputRelevant(input, attributed.Origin.Node, attributed));
        Assert.Same(
            branch.Requirements[0],
            branch.SelectRequirementForFailure(new("input/foreign"), new("node/foreign")));
        Assert.Throws<ArgumentException>(() => branch.IsInputRelevant(input, attributed.Origin.Node,
            realization.Requirements.First(requirement => !branch.ContainsRequirement(requirement.Id))));
        Assert.Throws<ArgumentException>(() => request.Selection.GetBranch(new("query:absent")));
    }

    [Fact]
    public void ValidateInputs_DiagnosesStaleRealizationStalePlacementAndUnavailableRealization()
    {
        var queryPlan = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var relationPlan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        var queryRealization = Realize(queryPlan, RelationQueryResultObservability.NotRequested);
        var relationRealization = Realize(relationPlan, RelationQueryResultObservability.ExactContributors);
        var queryPlacement = CreatePlacement(queryPlan);
        var relationPlacement = CreatePlacement(relationPlan);
        var unavailableRealization = RelationQueryRealizationCompiler.Compile(
            queryPlan,
            UnsupportedProfile(queryPlan),
            RelationQueryInMemoryInterpreter.DefaultRealizationPolicy,
            RelationQueryResultObservability.NotRequested);

        var staleRealizationBound = Bind(new(
            queryPlan,
            relationRealization,
            queryPlacement));
        var staleRealization = new RelationQueryNativeCompilationRequest(
            queryPlan,
            staleRealizationBound,
            queryPlacement).ValidateInputs();
        var queryBound = Bind(new(queryPlan, queryRealization, queryPlacement));
        var stalePlacement = new RelationQueryNativeCompilationRequest(
            queryPlan,
            queryBound,
            relationPlacement).ValidateInputs();
        var unavailableBound = Bind(new(queryPlan, unavailableRealization, queryPlacement));
        var unavailable = new RelationQueryNativeCompilationRequest(
            queryPlan,
            unavailableBound,
            queryPlacement).ValidateInputs();

        Assert.Contains(staleRealization, static diagnostic => diagnostic.Code ==
            RelationQueryNativeCompilationDiagnosticCodes.RealizationPlanMismatch);
        Assert.Contains(stalePlacement, static diagnostic => diagnostic.Code ==
            RelationQueryNativeCompilationDiagnosticCodes.PlacementPlanMismatch);
        Assert.Contains(stalePlacement, static diagnostic => diagnostic.Code ==
            RelationQueryNativeCompilationDiagnosticCodes.BoundRealizationPlacementMismatch);
        Assert.Contains(unavailable, static diagnostic => diagnostic.Code ==
            RelationQueryNativeCompilationDiagnosticCodes.BoundRealizationUnavailable);
    }

    [Fact]
    public void ValidateInputs_RejectsSelfConsistentButUnreproducibleBoundProof()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        var realization = Realize(plan, RelationQueryResultObservability.NotRequested);
        var placement = CreatePlacement(plan);
        var contextualRequest = new RelationQueryBoundRealizationRequest(plan, realization, placement);
        var valid = Bind(contextualRequest);
        var incompleteEvidence = new RelationQueryContextualEvidenceProjection(valid.Evidence.Binding, []);
        var forgedFingerprint = RelationQueryBoundRealizationFingerprinter.Compute(
            realization,
            placement.Fingerprint,
            valid.Branches,
            incompleteEvidence,
            [],
            RelationQueryRealizationStatus.Realizable);
        var forged = new RelationQueryBoundRealizationReport(
            realization,
            placement.Fingerprint,
            valid.Branches,
            incompleteEvidence,
            [],
            RelationQueryRealizationStatus.Realizable,
            forgedFingerprint);

        var diagnostics = new RelationQueryNativeCompilationRequest(plan, forged, placement).ValidateInputs();

        var diagnostic = Assert.Single(diagnostics, static item =>
            item.Code == RelationQueryNativeCompilationDiagnosticCodes.BoundRealizationProofInvalid);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.NotNull(diagnostic.Resolution);
    }

    [Fact]
    public void DecisionReference_ValidatesProofShapeAndNormalizesProofIdentityOrder()
    {
        var decision = new RelationQueryNativeCompilationDecisionReference(
            new("requirement/z"),
            CapabilityRealizationKind.Constrained,
            [new("evidence/z"), new("evidence/a")],
            [new("rule/z"), new("rule/a")],
            operatingBoundaries: [new("boundary/z"), new("boundary/a")],
            preservedGuarantees:
            [
                RelationQueryGuaranteeCapabilityKind.Ordering,
                RelationQueryGuaranteeCapabilityKind.Cardinality,
                RelationQueryGuaranteeCapabilityKind.Ordering
            ]);

        Assert.Equal(["evidence/a", "evidence/z"], Values(decision.CapabilityEvidence));
        Assert.Equal(["rule/a", "rule/z"], Values(decision.CompositionRules));
        Assert.Equal(["boundary/a", "boundary/z"], Values(decision.OperatingBoundaries));
        Assert.Equal(
            [RelationQueryGuaranteeCapabilityKind.Cardinality, RelationQueryGuaranteeCapabilityKind.Ordering],
            decision.PreservedGuarantees.ToArray());

        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            default,
            CapabilityRealizationKind.Native,
            [new("evidence/a")]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            CapabilityRealizationKind.Native));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            CapabilityRealizationKind.Unavailable,
            [new("evidence/a")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            CapabilityRealizationKind.Unknown,
            [new("evidence/a")]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            CapabilityRealizationKind.Composed,
            [new("evidence/a")]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            CapabilityRealizationKind.Native,
            [new("evidence/a")],
            [new("rule/a")]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            CapabilityRealizationKind.Override));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            CapabilityRealizationKind.Native,
            [new("evidence/a"), new("evidence/a")]));
    }

    [Fact]
    public void Provenance_NormalizesAttributionAndDerivesDecisionSummaries()
    {
        var plan = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var realization = Realize(plan, RelationQueryResultObservability.NotRequested);
        var placement = CreatePlacement(plan);
        var branch = Assert.Single(
            CreateNativeRequest(
                plan,
                realization,
                placement,
                [new($"query:{LoadCustomerRelationFixture.RowsResultId.Value}")])
                .Branches);
        var firstDecision = new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            CapabilityRealizationKind.Native,
            [new("evidence/a")]);
        var secondDecision = new RelationQueryNativeCompilationDecisionReference(
            new("requirement/z"),
            CapabilityRealizationKind.Constrained,
            [new("evidence/z"), new("evidence/a")],
            operatingBoundaries: [new("boundary/z")]);

        var provenance = CreateProvenance(
            plan,
            realization,
            placement,
            branch.Id,
            [secondDecision, firstDecision]);

        Assert.Equal(["node/a", "node/z"], Values(provenance.CoveredNodes));
        Assert.Equal(["assignment/a", "assignment/z"], Values(provenance.CoveredAssignments));
        Assert.Equal(
            RelationQueryCompiledPlanReference.From(plan).Inputs.Select(static input => input.Value),
            Values(provenance.InputFields));
        Assert.Equal(
            ["requirement/a", "requirement/z"],
            provenance.RealizationDecisions.Select(static decision => decision.Requirement.Value));
        Assert.Equal(["evidence/a", "evidence/z"], Values(provenance.CapabilityEvidence));
        Assert.Equal(["boundary/z"], Values(provenance.OperatingBoundaries));

        Assert.Throws<ArgumentException>(() => CreateProvenance(
            plan,
            realization,
            placement,
            branch.Id,
            []));
        Assert.Throws<ArgumentException>(() => CreateProvenance(
            plan,
            realization,
            placement,
            branch.Id,
            [firstDecision, firstDecision]));
        var emptyCoverage = Assert.Throws<ArgumentException>(() => CreateProvenance(
            plan,
            realization,
            placement,
            branch.Id,
            [firstDecision],
            coveredNodes: []));
        Assert.Equal("coveredNodes", emptyCoverage.ParamName);
        var foreignInput = Assert.Throws<ArgumentException>(() => CreateProvenance(
            plan,
            realization,
            placement,
            branch.Id,
            [firstDecision],
            inputFields: [new("input/absent-from-plan")]));
        Assert.Equal("inputFields", foreignInput.ParamName);
    }

    [Fact]
    public void Provenance_RejectsIncoherentAdapterAffinityAndMissingContextEvidence()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        var realization = Realize(plan, RelationQueryResultObservability.NotRequested);
        var placement = CreatePlacement(plan);
        var branch = Assert.Single(CreateNativeRequest(plan, realization, placement).Branches);
        var bound = Bind(new(plan, realization, placement, [branch.Id]));
        var decision = RelationQueryNativeCompilationProvenanceFactory.CreateDecisionReference(
            realization.Decisions[0]);

        RelationQueryNativeCompilationProvenance Create(
            RelationQueryTargetId target,
            RelationQueryTargetProfileId profile,
            RelationQueryAdapterBindingReference adapterBinding,
            ImmutableArray<RelationQueryContextEvidenceId> contextEvidence) => new(
            RelationQueryCompiledPlanReference.From(plan),
            branch.Id,
            target,
            profile,
            realization.Fingerprint,
            bound.Fingerprint,
            placement.Fingerprint,
            adapterBinding,
            contextEvidence,
            "tests/native-compiler/v1",
            "tests/native-conventions/v1",
            [branch.Node],
            [],
            [],
            [decision]);

        var contextEvidence = bound.Evidence.Assessments
            .Select(static assessment => assessment.Id)
            .ToImmutableArray();
        _ = Create(
            realization.TargetProfile.Target,
            realization.TargetProfile.Id,
            bound.Evidence.Binding,
            contextEvidence);

        Assert.Throws<ArgumentException>(() => Create(
            new("target/foreign"),
            realization.TargetProfile.Id,
            bound.Evidence.Binding,
            contextEvidence));
        Assert.Throws<ArgumentException>(() => Create(
            realization.TargetProfile.Target,
            new("profile/foreign"),
            bound.Evidence.Binding,
            contextEvidence));
        Assert.Throws<ArgumentException>(() => Create(
            realization.TargetProfile.Target,
            realization.TargetProfile.Id,
            CopyBinding(
                bound.Evidence.Binding,
                compiledPlanFingerprint: bound.Evidence.Binding.CompiledPlanFingerprint! with
                {
                    Value = new string('b', 64)
                }),
            contextEvidence));
        Assert.Throws<ArgumentException>(() => Create(
            realization.TargetProfile.Target,
            realization.TargetProfile.Id,
            CopyBinding(
                bound.Evidence.Binding,
                placementFingerprint: new(
                    placement.Fingerprint.Algorithm,
                    placement.Fingerprint.Canonicalization,
                    new string('c', 64))),
            contextEvidence));
        var missingContext = Assert.Throws<ArgumentException>(() => Create(
            realization.TargetProfile.Target,
            realization.TargetProfile.Id,
            bound.Evidence.Binding,
            []));
        Assert.Equal("contextEvidence", missingContext.ParamName);
    }

    [Fact]
    public void ProvenanceFactory_DerivesBranchRelevantProofFromTheValidatedRequest()
    {
        var plan = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var realization = Realize(plan, RelationQueryResultObservability.NotRequested);
        var placement = CreatePlacement(plan);
        var request = CreateNativeRequest(plan, realization, placement);
        var branch = request.Branches.Single(candidate =>
            candidate.QueryResult == LoadCustomerRelationFixture.RowsResultId);
        var outputIds = branch.Outputs.Select(static output => output.Id).ToHashSet();
        var branchInputFields = request.Plan.InputContract.Sources
            .SelectMany(static source => source.Fields)
            .Concat(request.Plan.InputContract.Traversals.SelectMany(static traversal => traversal.Fields))
            .Where(field => field.Uses.Any(use => outputIds.Contains(use.Output.Id)))
            .Select(static field => field.Input.Id)
            .ToImmutableArray();

        var provenance = RelationQueryNativeCompilationProvenanceFactory.Create(
            request,
            branch.Id,
            "tests/native-provenance-factory/v1",
            "tests/native-provenance-conventions/v1",
            [
                LoadCustomerRelationFixture.LoadSourceNodeId,
                LoadCustomerRelationFixture.StatusFilterNodeId,
                LoadCustomerRelationFixture.CustomerTraversalNodeId,
                LoadCustomerRelationFixture.ProjectionNodeId,
                LoadCustomerRelationFixture.OrderNodeId,
                LoadCustomerRelationFixture.PageNodeId
            ],
            [LoadCustomerRelationFixture.SearchIdAssignmentId],
            [.. branchInputFields.Reverse()]);

        var expectedRequirements = realization.Requirements
            .Where(requirement => requirement.Uses.IsDefaultOrEmpty
                                  || requirement.Uses.Any(use => outputIds.Contains(use.Output.Id)))
            .Select(static requirement => requirement.Id.Value)
            .Order(StringComparer.Ordinal);
        Assert.Equal(request.PlanReference, provenance.Plan);
        Assert.Equal(branch.Id, provenance.Branch);
        Assert.Equal(realization.TargetProfile.Target, provenance.Target);
        Assert.Equal(realization.TargetProfile.Id, provenance.TargetProfile);
        Assert.Equal(realization.Fingerprint, provenance.Realization);
        Assert.Equal(request.BoundRealization.Fingerprint, provenance.BoundRealization);
        Assert.Equal(placement.Fingerprint, provenance.Placement);
        Assert.Equal(request.BoundRealization.Evidence.Binding, provenance.AdapterBinding);
        Assert.NotEmpty(provenance.ContextEvidence);
        Assert.Equal(expectedRequirements, provenance.RealizationDecisions.Select(static decision =>
            decision.Requirement.Value));
        Assert.Equal(
            branchInputFields.Select(static input => input.Value).Order(StringComparer.Ordinal),
            Values(provenance.InputFields));
    }

    [Fact]
    public void ProvenanceFactory_AcceptsBranchRelevantTraversalFieldInput()
    {
        var plan = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var realization = Realize(plan, RelationQueryResultObservability.NotRequested);
        var placement = CreatePlacement(plan);
        var request = CreateNativeRequest(plan, realization, placement);
        var branch = request.Branches.Single(candidate =>
            candidate.QueryResult == LoadCustomerRelationFixture.RowsResultId);
        var outputIds = branch.Outputs.Select(static output => output.Id).ToHashSet();
        var traversalInput = Assert.Single(
            request.Plan.InputContract.Traversals.SelectMany(static traversal => traversal.Fields),
            field => field.Uses.Any(use => outputIds.Contains(use.Output.Id)));

        var provenance = RelationQueryNativeCompilationProvenanceFactory.Create(
            request,
            branch.Id,
            "tests/native-provenance-factory/v1",
            "tests/native-provenance-conventions/v1",
            [branch.Node],
            [],
            [traversalInput.Input.Id]);

        Assert.Equal(traversalInput.Input.Id, Assert.Single(provenance.InputFields));
    }

    [Fact]
    public void ProvenanceFactory_ProjectsEverySuccessfulDecisionKindAndRejectsUnavailableProof()
    {
        RelationQueryRealizationDecision[] decisions =
        [
            new NativeRelationQueryRealizationDecision(
                new("requirement/native"),
                [new("evidence/native")],
                [RelationQueryGuaranteeCapabilityKind.Ordering]),
            new ComposedRelationQueryRealizationDecision(
                new("requirement/composed"),
                [new("rule/composed")],
                [new("evidence/composed")],
                [RelationQueryGuaranteeCapabilityKind.Grouping]),
            new ConstrainedRelationQueryRealizationDecision(
                new("requirement/constrained"),
                [new("evidence/constrained")],
                [
                    new(
                        new("boundary/constrained"),
                        RelationQueryOperatingBoundaryValidationKind.StaticPlanFact,
                        measuredValue: 10)
                ],
                [new("rule/constrained")],
                [RelationQueryGuaranteeCapabilityKind.StablePaging]),
            new OverrideRelationQueryRealizationDecision(
                new("requirement/override"),
                new("override/local"),
                [new("evidence/override")],
                [
                    new(
                        new("boundary/override"),
                        RelationQueryOperatingBoundaryValidationKind.TargetEnforced,
                        new("evidence/override"))
                ],
                [RelationQueryGuaranteeCapabilityKind.Cardinality])
        ];

        var references = decisions
            .Select(RelationQueryNativeCompilationProvenanceFactory.CreateDecisionReference)
            .ToArray();

        Assert.Equal(decisions.Select(static decision => decision.Requirement),
            references.Select(static reference => reference.Requirement));
        Assert.Equal(decisions.Select(static decision => decision.Kind),
            references.Select(static reference => reference.Kind));
        Assert.Equal(new RelationQueryCompositionRuleId("rule/composed"), references[1].CompositionRules.Single());
        Assert.Equal(new RelationQueryOperatingBoundaryId("boundary/constrained"),
            references[2].OperatingBoundaries.Single());
        Assert.Equal(new RelationQueryRealizationOverrideId("override/local"), references[3].Override);

        var unavailable = new UnavailableRelationQueryRealizationDecision(
            new("requirement/unavailable"),
            RelationQueryUnavailableReason.CapabilityNotAdvertised);
        Assert.Throws<InvalidOperationException>(() =>
            RelationQueryNativeCompilationProvenanceFactory.CreateDecisionReference(unavailable));
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

    static RelationQueryRealizationReport Realize(
        CompiledRelationQueryPlan plan,
        RelationQueryResultObservability observability) =>
        RelationQueryRealizationCompiler.Compile(
            plan,
            RelationQueryInMemoryInterpreter.DefaultTargetProfile,
            RelationQueryInMemoryInterpreter.DefaultRealizationPolicy,
            observability);

    static RelationQueryTargetCapabilityProfile UnsupportedProfile(CompiledRelationQueryPlan plan)
    {
        var reference = RelationQueryCompiledPlanReference.From(plan);
        return new(
            new("tests/native-compilation-unsupported"),
            new("tests/native-compilation-unsupported/v1"),
            [reference.DefinitionSchemaVersion],
            [reference.CompilerProfile]);
    }

    static RelationQueryNativeCompilationRequest CreateNativeRequest(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement,
        ImmutableArray<RelationQueryNativeResultBranchId> branches = default)
    {
        var contextualRequest = new RelationQueryBoundRealizationRequest(
            plan,
            realization,
            placement,
            branches);
        return new(plan, Bind(contextualRequest), placement);
    }

    static RelationQueryBoundRealizationReport Bind(RelationQueryBoundRealizationRequest request)
    {
        const string authority = "tests/native-compilation-binding/v1";
        var binding = new RelationQueryAdapterBindingReference(
            "tests/native-compilation-binding/v1",
            "binding/native-compilation-tests",
            request.ProfileFeasibility.TargetProfile.Target,
            request.ProfileFeasibility.TargetProfile.Id,
            new(
                "sha256",
                "tests/native-compilation-binding/v1-c14n/v1",
                new string('a', 64)),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.PlanReference),
            request.Placement.Fingerprint,
            [.. request.Placement.SourceInstances.Select(static source => source.Id)],
            [.. request.Placement.Bindings.Select(static placement => placement.Id)]);

        var decisions = request.ProfileFeasibility.Decisions.ToDictionary(static decision => decision.Requirement);
        ImmutableArray<RelationQueryBoundRequirementAssessment>.Builder assessments =
            ImmutableArray.CreateBuilder<RelationQueryBoundRequirementAssessment>();
        if (request.ProfileFeasibility.IsRealizable)
        {
            foreach (var branch in request.Branches)
            {
                var outputs = branch.Outputs.Select(static output => output.Id).ToHashSet();
                foreach (var requirement in request.ProfileFeasibility.Requirements.Where(requirement =>
                             requirement.Uses.IsDefaultOrEmpty
                             || requirement.Uses.Any(use => outputs.Contains(use.Output.Id))))
                {
                    var decision = decisions[requirement.Id];
                    assessments.Add(new(
                        new($"context/{Uri.EscapeDataString(branch.Id.Value)}/{Uri.EscapeDataString(requirement.Id.Value)}"),
                        branch.Id,
                        requirement.Id,
                        RelationQueryBoundAssessmentStatus.Available,
                        EffectiveConfigurationOrigin.AdapterConvention,
                        authority,
                        decision.GetCapabilityEvidence(),
                        decision.GetTargetEnforcedBoundaries(),
                        decision.GetPreservedGuarantees(),
                        message: "The test binding preserves this exact branch requirement."));
                }
            }
        }

        return RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(binding, assessments.ToImmutable()));
    }

    static RelationQuerySourcePlacement CreatePlacement(CompiledRelationQueryPlan plan)
    {
        var source = Assert.Single(plan.InputContract.Sources);
        RelationQuerySourceInstanceId sourceId = new("source/native-compilation-tests");
        var sourceInstance = new RelationQuerySourceInstance(
            sourceId,
            new("domain/native-compilation-tests"),
            RelationQueryInMemoryInterpreter.DefaultTargetProfile,
            new(100, 1_000, 100, 4));
        var binding = new RelationQuerySourcePlacementBinding(
            new("placement/native-compilation-tests"),
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
            "tests/native-compilation-placement/v1",
            [sourceInstance],
            [binding]);
    }

    static RelationQueryNativeCompilationProvenance CreateProvenance(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement,
        RelationQueryNativeResultBranchId branch,
        ImmutableArray<RelationQueryNativeCompilationDecisionReference> decisions,
        ImmutableArray<QueryNodeId>? coveredNodes = null,
        ImmutableArray<RelationQueryInputId>? inputFields = null)
    {
        var bound = Bind(new(plan, realization, placement, [branch]));
        return new(
            RelationQueryCompiledPlanReference.From(plan),
            branch,
            realization.TargetProfile.Target,
            realization.TargetProfile.Id,
            realization.Fingerprint,
            bound.Fingerprint,
            placement.Fingerprint,
            bound.Evidence.Binding,
            [.. bound.Evidence.Assessments.Select(static assessment => assessment.Id)],
            "tests/native-compiler/v1",
            "tests/native-conventions/v1",
            coveredNodes ?? [new("node/z"), new("node/a")],
            [new("assignment/z"), new("assignment/a")],
            inputFields ?? [.. RelationQueryCompiledPlanReference.From(plan).Inputs.Reverse()],
            decisions);
    }

    static RelationQueryAdapterBindingReference CopyBinding(
        RelationQueryAdapterBindingReference binding,
        RelationQueryPlanComponentFingerprint? compiledPlanFingerprint = null,
        RelationQuerySourcePlacementFingerprint? placementFingerprint = null) => new(
        binding.SchemaVersion,
        binding.BindingId,
        binding.Target,
        binding.TargetProfile,
        binding.Fingerprint,
        compiledPlanFingerprint ?? binding.CompiledPlanFingerprint,
        placementFingerprint ?? binding.PlacementFingerprint,
        binding.Sources,
        binding.PlacementBindings,
        binding.ConfigurationDecisions);

    static string[] Values(IEnumerable<RelationQueryTargetCapabilityEvidenceId> values) =>
        [.. values.Select(static value => value.Value)];

    static string[] Values(IEnumerable<RelationQueryCompositionRuleId> values) =>
        [.. values.Select(static value => value.Value)];

    static string[] Values(IEnumerable<RelationQueryOperatingBoundaryId> values) =>
        [.. values.Select(static value => value.Value)];

    static string[] Values(IEnumerable<QueryNodeId> values) =>
        [.. values.Select(static value => value.Value)];

    static string[] Values(IEnumerable<QueryAssignmentId> values) =>
        [.. values.Select(static value => value.Value)];

    static string[] Values(IEnumerable<RelationQueryInputId> values) =>
        [.. values.Select(static value => value.Value)];
}
