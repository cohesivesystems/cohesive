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
        var all = new RelationQueryNativeCompilationRequest(plan, realization, placement);
        var reversedIds = all.Branches
            .Select(static branch => branch.Id)
            .Reverse()
            .ToImmutableArray();

        var selected = new RelationQueryNativeCompilationRequest(
            plan,
            realization,
            placement,
            reversedIds);

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

        Assert.Throws<ArgumentNullException>(() => new RelationQueryNativeCompilationRequest(
            null!,
            realization,
            placement));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationRequest(
            plan,
            realization,
            placement,
            []));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationRequest(
            plan,
            realization,
            placement,
            [default]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationRequest(
            plan,
            realization,
            placement,
            [all.Branches[0].Id, all.Branches[0].Id]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationRequest(
            plan,
            realization,
            placement,
            [new("query:absent")]));
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

        var staleRealization = new RelationQueryNativeCompilationRequest(
            queryPlan,
            relationRealization,
            queryPlacement).ValidateInputs();
        var stalePlacement = new RelationQueryNativeCompilationRequest(
            queryPlan,
            queryRealization,
            relationPlacement).ValidateInputs();
        var unavailable = new RelationQueryNativeCompilationRequest(
            queryPlan,
            unavailableRealization,
            queryPlacement).ValidateInputs();

        Assert.Equal(
            RelationQueryNativeCompilationDiagnosticCodes.RealizationPlanMismatch,
            Assert.Single(staleRealization).Code);
        Assert.Equal(
            RelationQueryNativeCompilationDiagnosticCodes.PlacementPlanMismatch,
            Assert.Single(stalePlacement).Code);
        Assert.Equal(
            RelationQueryNativeCompilationDiagnosticCodes.RealizationUnavailable,
            Assert.Single(unavailable).Code);
    }

    [Fact]
    public void DecisionReference_ValidatesProofShapeAndNormalizesProofIdentityOrder()
    {
        var decision = new RelationQueryNativeCompilationDecisionReference(
            new("requirement/z"),
            RelationQueryRealizationDecisionKind.Constrained,
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
            RelationQueryRealizationDecisionKind.Native,
            [new("evidence/a")]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            RelationQueryRealizationDecisionKind.Native));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            RelationQueryRealizationDecisionKind.Unavailable,
            [new("evidence/a")]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            RelationQueryRealizationDecisionKind.Composed,
            [new("evidence/a")]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            RelationQueryRealizationDecisionKind.Native,
            [new("evidence/a")],
            [new("rule/a")]));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            RelationQueryRealizationDecisionKind.Override));
        Assert.Throws<ArgumentException>(() => new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            RelationQueryRealizationDecisionKind.Native,
            [new("evidence/a"), new("evidence/a")]));
    }

    [Fact]
    public void Provenance_NormalizesAttributionAndDerivesDecisionSummaries()
    {
        var plan = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var realization = Realize(plan, RelationQueryResultObservability.NotRequested);
        var placement = CreatePlacement(plan);
        var branch = Assert.Single(
            new RelationQueryNativeCompilationRequest(
                plan,
                realization,
                placement,
                [new($"query:{LoadCustomerRelationFixture.RowsResultId.Value}")])
                .Branches);
        var firstDecision = new RelationQueryNativeCompilationDecisionReference(
            new("requirement/a"),
            RelationQueryRealizationDecisionKind.Native,
            [new("evidence/a")]);
        var secondDecision = new RelationQueryNativeCompilationDecisionReference(
            new("requirement/z"),
            RelationQueryRealizationDecisionKind.Constrained,
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
        ImmutableArray<RelationQueryInputId>? inputFields = null) =>
        new(
            RelationQueryCompiledPlanReference.From(plan),
            branch,
            new("tests/native-target"),
            new("tests/native-target/v1"),
            realization.Fingerprint,
            placement.Fingerprint,
            "tests/native-compiler/v1",
            "tests/native-conventions/v1",
            coveredNodes ?? [new("node/z"), new("node/a")],
            [new("assignment/z"), new("assignment/a")],
            inputFields ?? [.. RelationQueryCompiledPlanReference.From(plan).Inputs.Reverse()],
            decisions);

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
