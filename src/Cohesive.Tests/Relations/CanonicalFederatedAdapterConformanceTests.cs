using System.Collections.Immutable;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Explain;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Tests.Relations;

public sealed class CanonicalFederatedAdapterConformanceTests
{
    static readonly RelationQueryNativeResultBranchId AggregationBranch = new(
        $"query:{FederatedLoadRelationFixture.AggregationResultId.Value}");
    static readonly RelationQueryNativeResultBranchId RowsBranch = new(
        $"query:{FederatedLoadRelationFixture.RowsResultId.Value}");
    static readonly PostgresRelationQueryTextSemantics OrdinalText = new(
        "C",
        PostgresRelationQueryTextEqualitySemantics.Ordinal);
    static readonly PostgresRelationQueryColumnOptions OrdinalTextOptions = new(
        scalarType: PostgresRelationQueryScalarType.Text,
        textSemantics: OrdinalText);

    [Fact]
    public void SupportedCanonicalBranches_ProduceExactCapabilityBackedArtifactsWithCompleteAffinity()
    {
        CanonicalAdapterObservation[] observations =
        [
            ObserveSupportedCosmosAggregation(),
            ObserveSupportedElasticAggregation(),
            ObserveSupportedPostgresQuery()
        ];

        foreach (var observation in observations)
        {
            var context = $"{observation.Adapter}: {Format(observation.Bound.Diagnostics)}";
            Assert.True(observation.Bound.IsRealizable, context);
            Assert.All(observation.Bound.Evidence.Assessments, static assessment =>
            {
                Assert.Equal(RelationQueryBoundAssessmentStatus.Available, assessment.Status);
                Assert.NotEmpty(assessment.CapabilityEvidence);
            });
            Assert.NotEmpty(observation.Artifacts);
            RelationQueryNativeResultBranchId[] expectedBranches = observation.Adapter == "PostgreSQL"
                ? [AggregationBranch, RowsBranch]
                : [AggregationBranch];
            Assert.Equal(expectedBranches, observation.SelectedBranches.ToArray());
            Assert.Equal(observation.SelectedBranches, observation.Artifacts.Select(static artifact => artifact.Branch));
            AssertDeterministicNativeLowering(observation);
            Assert.All(observation.Artifacts, artifact =>
            {
                var provenance = artifact.Provenance;
                Assert.Equal(observation.Plan, provenance.Plan);
                Assert.Equal(artifact.Branch, provenance.Branch);
                Assert.Equal(observation.Bound.Fingerprint, provenance.BoundRealization);
                Assert.Equal(observation.Placement, provenance.Placement);
                Assert.True(observation.Bound.Evidence.Binding.HasSameSemantics(provenance.AdapterBinding));
                Assert.NotEmpty(provenance.ContextEvidence);
                Assert.NotEmpty(provenance.RealizationDecisions);
                Assert.All(provenance.RealizationDecisions, static decision =>
                    Assert.NotEmpty(decision.CapabilityEvidence));
            });
        }
    }

    [Fact]
    public void UnsupportedCanonicalContexts_ProduceAttributableUnavailableDecisionsAndNoArtifacts()
    {
        CanonicalAdapterRejection[] rejections =
        [
            ObserveCosmosWithoutExactCountBound(),
            ObserveElasticWithContributorObservability(),
            ObservePostgresAcrossExecutionDomains()
        ];

        foreach (var rejection in rejections)
        {
            var context = $"{rejection.Adapter}: {Format(rejection.Bound.Diagnostics)}";
            Assert.Equal(RelationQueryRealizationStatus.NotRealizable, rejection.Bound.Status);
            var unavailable = Assert.Single(
                rejection.Bound.Evidence.Assessments,
                static assessment => assessment.Status == RelationQueryBoundAssessmentStatus.Unavailable);
            Assert.Equal(rejection.ExpectedDecisionCode, unavailable.AdapterDecisionCode);
            Assert.False(string.IsNullOrWhiteSpace(unavailable.Authority));
            Assert.True(
                unavailable.Node is not null
                || unavailable.Input is not null
                || unavailable.PlacementBinding is not null
                || unavailable.FailedConfigurationSetting is not null,
                context);
            Assert.Contains(rejection.Bound.Diagnostics, diagnostic =>
                diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable
                && diagnostic.AdapterDecisionCode == unavailable.AdapterDecisionCode);
            Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, rejection.Status);
            Assert.Equal(0, rejection.ArtifactCount);
        }
    }

    [Fact]
    public void CrossSourceRows_StopAtProfileFeasibilityBeforeBindingOrNativeCompilation()
    {
        var cosmos = ObserveUnavailableRowsProfile(
            "Cosmos",
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy);
        var elastic = ObserveUnavailableRowsProfile(
            "Elasticsearch",
            ElasticRelationQueryTargetProfile.Default,
            ElasticRelationQueryTargetProfile.Policy);

        AssertUnavailableRowsProfile(cosmos);
        AssertUnavailableRowsProfile(elastic);
    }

    [Fact]
    public void PostgresCosmosGuide_CustomerOnlyRowsUseOnePostgresJoinAndRejectNativeCosmosTraversal()
    {
        var context = CreatePostgresContext(
            splitExecutionDomains: false,
            CustomerRowsDemand());
        var customerTraversal = Assert.Single(context.Plan.InputContract.Traversals);
        Assert.Equal(
            FederatedLoadRelationFixture.LoadCustomerRelationshipId,
            customerTraversal.Definition.Id);
        var realization = Realize(
            context.Plan,
            PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy);
        var request = new RelationQueryBoundRealizationRequest(
            context.Plan,
            realization,
            context.Placement.Placement);
        PostgresRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, context.Storage);
        Assert.True(bound.IsRealizable, Format(bound.Diagnostics));
        Assert.All(bound.Evidence.Assessments, static assessment =>
        {
            Assert.Equal(RelationQueryBoundAssessmentStatus.Available, assessment.Status);
            Assert.NotEmpty(assessment.CapabilityEvidence);
        });
        var nativeRequest = new RelationQueryNativeCompilationRequest(
            context.Plan,
            bound,
            context.Placement.Placement);
        var native = compiler.Compile(nativeRequest, context.Storage);
        Assert.True(native.IsSuccessful, Format(native.Diagnostics));
        var artifact = Assert.Single(native.Artifacts);
        Assert.Equal(RowsBranch, artifact.Branch.Id);
        Assert.Empty(artifact.Parameters);
        var statement = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>());
        var compilerLiteral = Assert.Single(statement.Parameters);
        Assert.Null(compilerLiteral.Binding);
        Assert.Equal(true, compilerLiteral.Value);
        Assert.Equal(1, artifact.Statement.Text.Split("LEFT JOIN", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("equipment", artifact.Statement.Text, StringComparison.OrdinalIgnoreCase);
        string[] expectedSelectedFields =
        [
            FieldKey(new(
                FederatedLoadRelationFixture.LoadShapeId,
                FederatedLoadRelationFixture.LoadIdPath)),
            FieldKey(new(
                FederatedLoadRelationFixture.LoadShapeId,
                FederatedLoadRelationFixture.LoadCustomerIdPath)),
            FieldKey(new(
                FederatedLoadRelationFixture.CustomerShapeId,
                FederatedLoadRelationFixture.CustomerNamePath))
        ];
        Assert.Equal(
            expectedSelectedFields.Order(StringComparer.Ordinal),
            artifact.SelectedFields
                .Select(static field => FieldKey(field.Field))
                .Order(StringComparer.Ordinal));

        var cosmos = RelationQueryRealizationCompiler.Compile(
            context.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, cosmos.Status);
        var requirements = cosmos.Requirements.ToDictionary(static requirement => requirement.Id);
        Assert.Contains(
            cosmos.Decisions.OfType<UnavailableRelationQueryRealizationDecision>(),
            decision => requirements[decision.Requirement].Origin?.Node
                == FederatedLoadRelationFixture.CustomerTraversalNodeId);

        var explain = PostgresRelationQueryExplainProjector.Project(nativeRequest, native);
        Assert.Equal(RelationQueryExplainStageStatus.Complete, explain.Status);
        Assert.Equal(bound.Fingerprint, explain.Attempt.BoundRealization);
        Assert.Single(explain.Compilation.Artifacts);
    }

    static CanonicalAdapterObservation ObserveSupportedCosmosAggregation()
    {
        var context = CreateSingleSourceContext(
            CosmosRelationQueryTargetProfile.Default,
            "conformance/cosmos/loads");
        var storage = CosmosRelationQueryBinding.For(
                context.Input,
                explicitAuthority: "conformance/cosmos/binding/v1")
            .Account(new Uri("https://tests.invalid"))
            .Database("operations")
            .Container("loads")
            .IdentityDocumentPath(FederatedLoadRelationFixture.LoadIdPath)
            .FieldsBySemanticPath()
            .MaximumInputRows(10_000)
            .Build()
            .RequireValue();
        var realization = Realize(
            context.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy);
        var request = new RelationQueryBoundRealizationRequest(
            context.Plan,
            realization,
            context.Placement.Placement);
        CosmosRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, storage);
        var result = compiler.Compile(
            new RelationQueryNativeCompilationRequest(context.Plan, bound, context.Placement.Placement),
            storage);
        var repeated = compiler.Compile(
            new RelationQueryNativeCompilationRequest(context.Plan, bound, context.Placement.Placement),
            storage);
        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        Assert.True(repeated.IsSuccessful, Format(repeated.Diagnostics));
        return Observation(
            "Cosmos",
            context.Plan,
            context.Placement.Placement,
            request,
            bound,
            [.. result.Artifacts.Select(Project)],
            [.. repeated.Artifacts.Select(Project)]);
    }

    static CanonicalAdapterObservation ObserveSupportedElasticAggregation()
    {
        var context = CreateSingleSourceContext(
            ElasticRelationQueryTargetProfile.Default,
            "conformance/elastic/loads");
        var storage = ElasticRelationQueryBinding.For(
                context.Input,
                explicitAuthority: "conformance/elastic/binding/v1")
            .Index("federated-loads")
            .FieldsBySemanticPath()
            .Build()
            .RequireValue();
        var realization = Realize(
            context.Plan,
            ElasticRelationQueryTargetProfile.Default,
            ElasticRelationQueryTargetProfile.Policy);
        var request = new RelationQueryBoundRealizationRequest(
            context.Plan,
            realization,
            context.Placement.Placement);
        ElasticRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, storage);
        var result = compiler.Compile(
            new RelationQueryNativeCompilationRequest(context.Plan, bound, context.Placement.Placement),
            storage);
        var repeated = compiler.Compile(
            new RelationQueryNativeCompilationRequest(context.Plan, bound, context.Placement.Placement),
            storage);
        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        Assert.True(repeated.IsSuccessful, Format(repeated.Diagnostics));
        return Observation(
            "Elasticsearch",
            context.Plan,
            context.Placement.Placement,
            request,
            bound,
            [.. result.Artifacts.Select(Project)],
            [.. repeated.Artifacts.Select(Project)]);
    }

    static CanonicalAdapterObservation ObserveSupportedPostgresQuery()
    {
        var context = CreatePostgresContext(splitExecutionDomains: false);
        var realization = Realize(
            context.Plan,
            PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy);
        var request = new RelationQueryBoundRealizationRequest(
            context.Plan,
            realization,
            context.Placement.Placement);
        PostgresRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, context.Storage);
        Assert.True(bound.IsRealizable, Format(bound.Diagnostics));
        var compilationRequest = new RelationQueryNativeCompilationRequest(
            context.Plan,
            bound,
            context.Placement.Placement);
        var result = compiler.Compile(compilationRequest, context.Storage);
        var repeated = compiler.Compile(compilationRequest, context.Storage);
        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        Assert.True(repeated.IsSuccessful, Format(repeated.Diagnostics));
        AssertPostgresRowsSemantics(context.Plan, result.Artifacts);
        return Observation(
            "PostgreSQL",
            context.Plan,
            context.Placement.Placement,
            request,
            bound,
            [.. result.Artifacts.Select(Project)],
            [.. repeated.Artifacts.Select(Project)]);
    }

    static CanonicalAdapterRejection ObserveCosmosWithoutExactCountBound()
    {
        var context = CreateSingleSourceContext(
            CosmosRelationQueryTargetProfile.Default,
            "conformance/cosmos/loads-without-count-bound");
        var storage = CosmosRelationQueryBinding.For(
                context.Input,
                explicitAuthority: "conformance/cosmos/missing-count-bound/v1")
            .Account(new Uri("https://tests.invalid"))
            .Database("operations")
            .Container("loads")
            .IdentityDocumentPath(FederatedLoadRelationFixture.LoadIdPath)
            .FieldsBySemanticPath()
            .Build()
            .RequireValue();
        var realization = Realize(
            context.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy);
        var request = new RelationQueryBoundRealizationRequest(
            context.Plan,
            realization,
            context.Placement.Placement);
        CosmosRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, storage);
        var result = compiler.Compile(request, storage);
        return new(
            "Cosmos",
            new(CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported),
            bound,
            result.Status,
            result.Artifacts.Length);
    }

    static CanonicalAdapterRejection ObserveElasticWithContributorObservability()
    {
        var context = CreateSingleSourceContext(
            ElasticRelationQueryTargetProfile.Default,
            "conformance/elastic/loads-with-contributors");
        var storage = ElasticRelationQueryBinding.For(
                context.Input,
                explicitAuthority: "conformance/elastic/contributors/v1")
            .Index("federated-loads")
            .FieldsBySemanticPath()
            .Build()
            .RequireValue();
        var realization = RealizeWithUnavailableOverrides(
            context.Plan,
            ElasticRelationQueryTargetProfile.Default,
            ElasticRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.ExactContributors,
            "conformance/elastic/contributors");
        var request = new RelationQueryBoundRealizationRequest(
            context.Plan,
            realization,
            context.Placement.Placement);
        ElasticRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, storage);
        var result = compiler.Compile(request, storage);
        return new(
            "Elasticsearch",
            new(ElasticRelationQueryCompilationDiagnosticCodes.ResultObservabilityUnsupported),
            bound,
            result.Status,
            result.Artifacts.Length);
    }

    static CanonicalAdapterRejection ObservePostgresAcrossExecutionDomains()
    {
        var context = CreatePostgresContext(splitExecutionDomains: true);
        var realization = Realize(
            context.Plan,
            PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy);
        var request = new RelationQueryBoundRealizationRequest(
            context.Plan,
            realization,
            context.Placement.Placement);
        PostgresRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, context.Storage);
        var result = compiler.Compile(request, context.Storage);
        return new(
            "PostgreSQL",
            new(PostgresRelationQueryCompilationDiagnosticCodes.CrossSourceJoin),
            bound,
            result.Status,
            result.Artifacts.Length);
    }

    static CanonicalRowsProfileRejection ObserveUnavailableRowsProfile(
        string adapter,
        RelationQueryTargetCapabilityProfile profile,
        RelationQueryRealizationPolicy policy)
    {
        var plan = CompileRowsPlan();
        var result = Assert.Single(plan.ExecutionSlice.QueryResults);
        Assert.Equal(FederatedLoadRelationFixture.RowsResultId, result.Id);
        var realization = RelationQueryRealizationCompiler.Compile(
            plan,
            profile,
            policy,
            RelationQueryResultObservability.NotRequested);
        if (realization.IsRealizable)
        {
            throw new InvalidOperationException(
                $"{adapter} unexpectedly passed profile feasibility; binding and native compilation were not expected.");
        }

        return new(adapter, plan, realization, NativeArtifactCount: 0);
    }

    static void AssertUnavailableRowsProfile(CanonicalRowsProfileRejection rejection)
    {
        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, rejection.Realization.Status);
        Assert.Equal(0, rejection.NativeArtifactCount);
        var requirements = rejection.Realization.Requirements.ToDictionary(static requirement => requirement.Id);
        AssertUnavailableTraversal(
            rejection,
            FederatedLoadRelationFixture.CustomerTraversalNodeId,
            QueryInputRequirement.Required,
            requirements);
        AssertUnavailableTraversal(
            rejection,
            FederatedLoadRelationFixture.EquipmentTraversalNodeId,
            QueryInputRequirement.Optional,
            requirements);
    }

    static void AssertUnavailableTraversal(
        CanonicalRowsProfileRejection rejection,
        QueryNodeId node,
        QueryInputRequirement requirement,
        IReadOnlyDictionary<RelationQueryRealizationRequirementId, RelationQueryRealizationRequirement> requirements)
    {
        RelationQueryLogicalCapabilityKind requirementKind = requirement == QueryInputRequirement.Required
            ? RelationQueryLogicalCapabilityKind.RequiredRelationshipTraversal
            : RelationQueryLogicalCapabilityKind.OptionalRelationshipTraversal;
        RelationQueryLogicalCapabilityKind[] expectedKinds =
        [
            RelationQueryLogicalCapabilityKind.RelationshipTraversal,
            RelationQueryLogicalCapabilityKind.ForwardRelationshipTraversal,
            RelationQueryLogicalCapabilityKind.AtMostOneRelationshipTraversal,
            requirementKind,
            RelationQueryLogicalCapabilityKind.LeftOuterJoin
        ];
        var traversalRequirements = requirements.Values
            .Where(candidate => candidate.Origin?.Node == node)
            .Where(static candidate => candidate.Capability is LogicalRelationQueryCapability)
            .OrderBy(static candidate => ((LogicalRelationQueryCapability)candidate.Capability).Kind)
            .ToArray();
        Assert.Equal(
            expectedKinds.Order(),
            traversalRequirements.Select(static candidate =>
                ((LogicalRelationQueryCapability)candidate.Capability).Kind));

        var traversalInput = rejection.Plan.InputContract.Traversals
            .Single(candidate => candidate.Input.Traversal == node)
            .Input.Id;
        foreach (var traversalRequirement in traversalRequirements)
        {
            Assert.Equal(traversalInput, traversalRequirement.Origin?.Input);
            Assert.NotEmpty(traversalRequirement.Uses);
            var logicalCapability = Assert.IsType<LogicalRelationQueryCapability>(
                traversalRequirement.Capability);
            Assert.Equal(
                ExpectedTraversalGuarantees(logicalCapability.Kind).Distinct().Order(),
                traversalRequirement.RequiredGuarantees.Order());
            var unavailable = Assert.IsType<UnavailableRelationQueryRealizationDecision>(
                rejection.Realization.Decisions.Single(decision =>
                    decision.Requirement == traversalRequirement.Id));
            Assert.Equal(RelationQueryUnavailableReason.CapabilityNotAdvertised, unavailable.Reason);
            var missingCapability = Assert.IsType<LogicalRelationQueryCapability>(
                Assert.Single(unavailable.MissingCapabilities));
            Assert.Equal(logicalCapability.Kind, missingCapability.Kind);
        }
    }

    static RelationQueryGuaranteeCapabilityKind[] ExpectedTraversalGuarantees(
        RelationQueryLogicalCapabilityKind capability)
    {
        RelationQueryGuaranteeCapabilityKind[] additional = capability switch
        {
            RelationQueryLogicalCapabilityKind.RelationshipTraversal
                or RelationQueryLogicalCapabilityKind.LeftOuterJoin =>
            [
                RelationQueryGuaranteeCapabilityKind.JoinMembership,
                RelationQueryGuaranteeCapabilityKind.Cardinality
            ],
            RelationQueryLogicalCapabilityKind.ForwardRelationshipTraversal =>
                [RelationQueryGuaranteeCapabilityKind.RelationshipDirection],
            RelationQueryLogicalCapabilityKind.AtMostOneRelationshipTraversal =>
            [
                RelationQueryGuaranteeCapabilityKind.Cardinality,
                RelationQueryGuaranteeCapabilityKind.RelationshipMultiplicity
            ],
            RelationQueryLogicalCapabilityKind.RequiredRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.OptionalRelationshipTraversal =>
            [
                RelationQueryGuaranteeCapabilityKind.Cardinality,
                RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                "Unexpected traversal capability.")
        };
        return
        [
            RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
            RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
            RelationQueryGuaranteeCapabilityKind.DeterministicResult,
            RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
            RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence,
            .. additional
        ];
    }

    static SingleSourceContext CreateSingleSourceContext(
        RelationQueryTargetCapabilityProfile targetProfile,
        string sourceKey)
    {
        var plan = CompileAggregationPlan();
        var placementBuilder = RelationQueryPlacement.For(plan);
        var source = placementBuilder.Source(sourceKey, targetProfile);
        var placed = placementBuilder.PlaceSource(source)
            .Identity(FederatedLoadRelationFixture.LoadIdFieldName)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        return new(plan, placement, placement.GetInput(placed));
    }

    static PostgresContext CreatePostgresContext(
        bool splitExecutionDomains,
        RelationQueryCompilationDemand? demand = null)
    {
        var plan = demand is null
            ? CompileAllBranchesPlan()
            : Compile(demand);
        var sourceContract = Assert.Single(plan.InputContract.Sources);
        var customerTraversal = plan.InputContract.Traversals.Single(traversal =>
            traversal.Definition.Id == FederatedLoadRelationFixture.LoadCustomerRelationshipId);
        var equipmentTraversal = plan.InputContract.Traversals.SingleOrDefault(traversal =>
            traversal.Definition.Id == FederatedLoadRelationFixture.LoadEquipmentRelationshipId);
        RelationQueryExecutionDomainId primary = new("conformance/postgres/primary");
        RelationQueryExecutionDomainId related = splitExecutionDomains
            ? new("conformance/postgres/related")
            : primary;
        var placementBuilder = RelationQueryPlacement.For(plan);
        var loadSource = placementBuilder.Source(
            "conformance/postgres/loads",
            PostgresRelationQueryTargetProfile.Default,
            primary);
        var customerSource = placementBuilder.Source(
            "conformance/postgres/customers",
            PostgresRelationQueryTargetProfile.Default,
            related);
        var loadHandle = placementBuilder.Place(sourceContract, loadSource)
            .Identity(FederatedLoadRelationFixture.LoadIdFieldName)
            .FieldsBySemanticPath();
        var customerHandle = placementBuilder.Place(customerTraversal, customerSource)
            .Identity(FederatedLoadRelationFixture.CustomerIdFieldName)
            .FieldsBySemanticPath();
        RelationQueryPlacementInputBuilder? equipmentHandle = null;
        if (equipmentTraversal is not null)
        {
            var equipmentSource = placementBuilder.Source(
                "conformance/postgres/equipment",
                PostgresRelationQueryTargetProfile.Default,
                related);
            equipmentHandle = placementBuilder.Place(equipmentTraversal, equipmentSource)
                .Identity(FederatedLoadRelationFixture.EquipmentIdFieldName)
                .FieldsBySemanticPath();
        }
        var placement = placementBuilder.Build().RequireValue();
        PostgresRelationQueryStorageBinding storage;
        if (!splitExecutionDomains)
        {
            var load = placement.GetInput(loadHandle);
            var customer = placement.GetInput(customerHandle);
            var binding = PostgresRelationQueryBinding.For(
                    placement,
                    explicitAuthority: "conformance/postgres/binding/v1")
                .Database(new("conformance/postgres/primary"));
            binding.Table(
                load,
                "loads",
                table =>
                {
                    var configured = table
                        .ColumnsExplicitly()
                        .Column(FederatedLoadRelationFixture.LoadIdPath, "load_id", OrdinalTextOptions)
                        .Column(FederatedLoadRelationFixture.LoadCustomerIdPath, "customer_id", OrdinalTextOptions)
                        .Identity(FederatedLoadRelationFixture.LoadIdPath, "load_id", OrdinalTextOptions)
                        .RelationshipReference(
                            customerTraversal.Input.Id,
                            FederatedLoadRelationFixture.LoadCustomerIdPath,
                            "customer_id",
                            OrdinalTextOptions);
                    if (equipmentTraversal is not null)
                    {
                        configured
                            .Column(
                                FederatedLoadRelationFixture.LoadEquipmentIdPath,
                                "equipment_id",
                                OrdinalTextOptions)
                            .RelationshipReference(
                                equipmentTraversal.Input.Id,
                                FederatedLoadRelationFixture.LoadEquipmentIdPath,
                                "equipment_id",
                                OrdinalTextOptions);
                    }
                });
            binding.Table(
                customer,
                "customers",
                table => table
                    .ColumnsExplicitly()
                    .Column(FederatedLoadRelationFixture.CustomerNamePath, "customer_name", OrdinalTextOptions)
                    .Identity(FederatedLoadRelationFixture.CustomerIdPath, "customer_id", OrdinalTextOptions));
            if (equipmentHandle is not null)
            {
                var equipment = placement.GetInput(equipmentHandle);
                binding.Table(
                    equipment,
                    "equipment",
                    table => table
                        .ColumnsExplicitly()
                        .Column(FederatedLoadRelationFixture.EquipmentNumberPath, "equipment_number", OrdinalTextOptions)
                        .Identity(FederatedLoadRelationFixture.EquipmentIdPath, "equipment_id", OrdinalTextOptions));
            }
            storage = binding
                .Build()
                .RequireValue();
        }
        else
        {
            var colocated = CreatePostgresContext(splitExecutionDomains: false, demand).Storage;
            storage = new(
                new("conformance/postgres/cross-domain-binding/v1"),
                colocated.Database,
                colocated.Target,
                colocated.TargetProfile,
                colocated.Tables,
                PostgresRelationQueryBindingOrigin.Explicit,
                colocated.ConventionSetVersion,
                colocated.ConfigurationDecisions,
                RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                    RelationQueryCompiledPlanReference.From(plan)),
                placement.Placement.Fingerprint);
        }
        return new(plan, placement, storage);
    }

    static CompiledRelationQueryPlan CompileAggregationPlan() => Compile(
        RelationQueryCompilationDemand.ForQueryResults(
        [
            QueryResultDemand.AllFields(FederatedLoadRelationFixture.AggregationResultId)
        ]));

    static CompiledRelationQueryPlan CompileRowsPlan() => Compile(
        RelationQueryCompilationDemand.ForQueryResults(
        [
            QueryResultDemand.AllFields(FederatedLoadRelationFixture.RowsResultId)
        ]));

    static RelationQueryCompilationDemand CustomerRowsDemand() =>
        RelationQueryCompilationDemand.ForQueryResults(
        [
            QueryResultDemand.SelectedFields(
                FederatedLoadRelationFixture.RowsResultId,
                [
                    new(
                        FederatedLoadRelationFixture.LoadSearchShapeId,
                        FederatedLoadRelationFixture.SearchIdPath),
                    new(
                        FederatedLoadRelationFixture.LoadSearchShapeId,
                        FederatedLoadRelationFixture.SearchCustomerNamePath)
                ])
        ]);

    static CompiledRelationQueryPlan CompileAllBranchesPlan() => Compile(demand: null);

    static CompiledRelationQueryPlan Compile(RelationQueryCompilationDemand? demand)
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            FederatedLoadRelationFixture.ConformanceQueryDocument,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument,
            demand));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        return Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
    }

    static RelationQueryRealizationReport Realize(
        CompiledRelationQueryPlan plan,
        RelationQueryTargetCapabilityProfile profile,
        RelationQueryRealizationPolicy policy)
    {
        var realization = RelationQueryRealizationCompiler.Compile(
            plan,
            profile,
            policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
        return realization;
    }

    static RelationQueryRealizationReport RealizeWithUnavailableOverrides(
        CompiledRelationQueryPlan plan,
        RelationQueryTargetCapabilityProfile profile,
        RelationQueryRealizationPolicy policy,
        RelationQueryResultObservability observability,
        string identityPrefix)
    {
        var baseline = RelationQueryRealizationCompiler.Compile(plan, profile, policy, observability);
        if (baseline.IsRealizable)
            return baseline;

        var requirements = baseline.Requirements.ToDictionary(static requirement => requirement.Id);
        ImmutableArray<RelationQueryRealizationOverride> overrides =
        [
            .. baseline.Decisions
                .OfType<UnavailableRelationQueryRealizationDecision>()
                .Select((decision, index) => new RelationQueryRealizationOverride(
                    new($"{identityPrefix}/override/{index:D4}"),
                    decision.Requirement,
                    requirements[decision.Requirement].Capability,
                    preservedGuarantees: requirements[decision.Requirement].RequiredGuarantees,
                    justification: "Exercise contextual fail-closed adapter conformance."))
        ];
        var overriddenPolicy = new RelationQueryRealizationPolicy(
            new($"{identityPrefix}/policy/v1"),
            policy.ConventionSetVersion,
            constrainedRealizations: RelationQueryConstrainedRealizationPolicy.AllowValidated,
            overrides: overrides);
        var realization = RelationQueryRealizationCompiler.Compile(
            plan,
            profile,
            overriddenPolicy,
            observability);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
        return realization;
    }

    static void AssertDeterministicNativeLowering(CanonicalAdapterObservation observation)
    {
        Assert.Equal(observation.Artifacts.Length, observation.RepeatedArtifacts.Length);
        var repeatedByBranch = observation.RepeatedArtifacts.ToDictionary(static artifact => artifact.Branch);
        foreach (var artifact in observation.Artifacts)
        {
            var repeated = repeatedByBranch[artifact.Branch];
            Assert.Equal(artifact.Fingerprint, repeated.Fingerprint);
            Assert.Equal(artifact.NativeShape, repeated.NativeShape);
            Assert.Equal(
                artifact.SelectedFields.Select(FieldKey),
                repeated.SelectedFields.Select(FieldKey));
            Assert.Equal(
                artifact.ResultFields.Select(FieldKey),
                repeated.ResultFields.Select(FieldKey));
            Assert.Equal(
                artifact.Provenance.CoveredNodes.ToArray(),
                repeated.Provenance.CoveredNodes.ToArray());
            Assert.Equal(
                artifact.Provenance.CoveredAssignments.ToArray(),
                repeated.Provenance.CoveredAssignments.ToArray());
            Assert.Equal(
                artifact.Provenance.InputFields.ToArray(),
                repeated.Provenance.InputFields.ToArray());
        }
    }

    static void AssertPostgresRowsSemantics(
        CompiledRelationQueryPlan plan,
        ImmutableArray<PostgresRelationQueryCompiledArtifact> artifacts)
    {
        var rows = artifacts.Single(static artifact => artifact.Branch.Id == RowsBranch);
        RelationQueryFieldReference[] expectedSelectedFields =
        [
            new(FederatedLoadRelationFixture.LoadShapeId, FederatedLoadRelationFixture.LoadIdPath),
            new(FederatedLoadRelationFixture.LoadShapeId, FederatedLoadRelationFixture.LoadCustomerIdPath),
            new(FederatedLoadRelationFixture.LoadShapeId, FederatedLoadRelationFixture.LoadEquipmentIdPath),
            new(FederatedLoadRelationFixture.CustomerShapeId, FederatedLoadRelationFixture.CustomerNamePath),
            new(FederatedLoadRelationFixture.EquipmentShapeId, FederatedLoadRelationFixture.EquipmentNumberPath)
        ];
        Assert.Equal(
            expectedSelectedFields.Select(FieldKey).Order(StringComparer.Ordinal),
            rows.SelectedFields.Select(static field => FieldKey(field.Field)).Order(StringComparer.Ordinal));
        var expectedInputFieldKeys = expectedSelectedFields.Select(FieldKey).ToHashSet(StringComparer.Ordinal);
        var expectedInputFields = plan.InputContract.Sources
            .SelectMany(static source => source.Fields)
            .Concat(plan.InputContract.Traversals.SelectMany(static traversal => traversal.Fields))
            .Where(field => expectedInputFieldKeys.Contains(FieldKey(field.Input.Field)))
            .Select(static field => field.Input.Id)
            .OrderBy(static input => input.Value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedInputFields, rows.Provenance.InputFields.ToArray());

        RelationQueryFieldReference[] expectedResultFields =
        [
            new(FederatedLoadRelationFixture.LoadSearchShapeId, FederatedLoadRelationFixture.SearchIdPath),
            new(FederatedLoadRelationFixture.LoadSearchShapeId, FederatedLoadRelationFixture.SearchCustomerNamePath),
            new(FederatedLoadRelationFixture.LoadSearchShapeId, FederatedLoadRelationFixture.SearchEquipmentNumberPath)
        ];
        Assert.Equal(
            expectedResultFields.Select(FieldKey).Order(StringComparer.Ordinal),
            rows.ResultFields.Select(static field => FieldKey(field.Field)).Order(StringComparer.Ordinal));

        var customer = plan.InputContract.Traversals.Single(traversal =>
            traversal.Definition.Id == FederatedLoadRelationFixture.LoadCustomerRelationshipId);
        var equipment = plan.InputContract.Traversals.Single(traversal =>
            traversal.Definition.Id == FederatedLoadRelationFixture.LoadEquipmentRelationshipId);
        Assert.Equal(QueryInputRequirement.Required, customer.Requirement);
        Assert.Equal(QueryInputRequirement.Optional, equipment.Requirement);
        Assert.Equal(JoinKind.Left, customer.JoinKind);
        Assert.Equal(JoinKind.Left, equipment.JoinKind);

        var joins = rows.LoweringDecisions
            .Where(static decision =>
                decision.Kind == PostgresRelationQueryLoweringDecisionKind.RelationshipTraversalJoin)
            .ToArray();
        Assert.Equal(2, joins.Length);
        Assert.Contains(joins, decision =>
            decision.Node == FederatedLoadRelationFixture.CustomerTraversalNodeId
            && decision.Relationship == FederatedLoadRelationFixture.LoadCustomerRelationshipId
            && decision.Strategy == "postgres/relationship-forward-identity-join/v1"
            && !decision.PlacementBindings.IsDefaultOrEmpty);
        Assert.Contains(joins, decision =>
            decision.Node == FederatedLoadRelationFixture.EquipmentTraversalNodeId
            && decision.Relationship == FederatedLoadRelationFixture.LoadEquipmentRelationshipId
            && decision.Strategy == "postgres/relationship-forward-identity-join/v1"
            && !decision.PlacementBindings.IsDefaultOrEmpty);
        Assert.Equal(2, rows.Statement.Text.Split("LEFT JOIN", StringSplitOptions.None).Length - 1);
    }

    static CanonicalArtifact Project(CosmosRelationQueryCompiledArtifact artifact) => new(
        artifact.Branch.Id,
        $"{artifact.Fingerprint.Algorithm}/{artifact.Fingerprint.Canonicalization}/{artifact.Fingerprint.Value}",
        artifact.Statement.Text,
        [.. artifact.SelectedFields.Select(static field => field.Field)],
        [.. artifact.ResultFields.Select(static field => field.Field)],
        artifact.Provenance);

    static CanonicalArtifact Project(ElasticRelationQueryCompiledArtifact artifact)
    {
        var template = artifact.RequestTemplate;
        var nativeShape = string.Join(
            '|',
            template.Index,
            $"query:{template.Query.Kind}",
            $"page:{template.Page.Kind}",
            $"aggregation:{template.Aggregation.Kind}",
            $"sources:{string.Join(',', template.SourceIncludes)}",
            $"sorts:{template.Sorts.Length}");
        return new(
            artifact.Branch.Id,
            $"{artifact.Fingerprint.Algorithm}/{artifact.Fingerprint.Canonicalization}/{artifact.Fingerprint.Value}",
            nativeShape,
            [.. artifact.SelectedFields.Select(static field => field.Field)],
            [.. artifact.ResultFields.Select(static field => field.Field)],
            artifact.Provenance);
    }

    static CanonicalArtifact Project(PostgresRelationQueryCompiledArtifact artifact) => new(
        artifact.Branch.Id,
        $"{artifact.Fingerprint.Algorithm}/{artifact.Fingerprint.Canonicalization}/{artifact.Fingerprint.Value}",
        artifact.Statement.Text,
        [.. artifact.SelectedFields.Select(static field => field.Field)],
        [.. artifact.ResultFields.Select(static field => field.Field)],
        artifact.Provenance);

    static string FieldKey(RelationQueryFieldReference field) =>
        $"{field.Shape.GraphId.Value}/{field.Shape.ShapeId.Value}/{field.Path}";

    static CanonicalAdapterObservation Observation(
        string adapter,
        CompiledRelationQueryPlan plan,
        RelationQuerySourcePlacement placement,
        RelationQueryBoundRealizationRequest request,
        RelationQueryBoundRealizationReport bound,
        ImmutableArray<CanonicalArtifact> artifacts,
        ImmutableArray<CanonicalArtifact> repeatedArtifacts) => new(
        adapter,
        RelationQueryCompiledPlanReference.From(plan),
        placement.Fingerprint,
        [.. request.Branches.Select(static branch => branch.Id)],
        bound,
        artifacts,
        repeatedArtifacts);

    static string Format<T>(IEnumerable<T> diagnostics) => string.Join(Environment.NewLine, diagnostics);

    sealed record SingleSourceContext(
        CompiledRelationQueryPlan Plan,
        RelationQueryAuthoredPlacement Placement,
        RelationQueryPlacedInput Input);

    sealed record PostgresContext(
        CompiledRelationQueryPlan Plan,
        RelationQueryAuthoredPlacement Placement,
        PostgresRelationQueryStorageBinding Storage);

    sealed record CanonicalAdapterObservation(
        string Adapter,
        RelationQueryCompiledPlanReference Plan,
        RelationQuerySourcePlacementFingerprint Placement,
        ImmutableArray<RelationQueryNativeResultBranchId> SelectedBranches,
        RelationQueryBoundRealizationReport Bound,
        ImmutableArray<CanonicalArtifact> Artifacts,
        ImmutableArray<CanonicalArtifact> RepeatedArtifacts);

    sealed record CanonicalArtifact(
        RelationQueryNativeResultBranchId Branch,
        string Fingerprint,
        string NativeShape,
        ImmutableArray<RelationQueryFieldReference> SelectedFields,
        ImmutableArray<RelationQueryFieldReference> ResultFields,
        RelationQueryNativeCompilationProvenance Provenance);

    sealed record CanonicalAdapterRejection(
        string Adapter,
        RelationQueryAdapterDecisionCode ExpectedDecisionCode,
        RelationQueryBoundRealizationReport Bound,
        RelationQueryNativeCompilationStatus Status,
        int ArtifactCount);

    sealed record CanonicalRowsProfileRejection(
        string Adapter,
        CompiledRelationQueryPlan Plan,
        RelationQueryRealizationReport Realization,
        int NativeArtifactCount);
}
