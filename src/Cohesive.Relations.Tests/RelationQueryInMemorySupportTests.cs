using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryInMemorySupportTests
{
    [Fact]
    public void DefaultProfileAdvertisesNestedFieldsAndCollectionElementsForCurrentItemReads()
    {
        var structuralCapabilities = RelationQueryInMemoryInterpreter.DefaultTargetProfile.Capabilities
            .Select(static evidence => evidence.Capability)
            .OfType<StructuralRelationQueryCapability>()
            .ToArray();

        Assert.Contains(
            structuralCapabilities,
            static capability => capability is
            {
                Role: RelationQueryStructuralCapabilityRole.CurrentItemRead,
                PathKind: RelationQueryStructuralPathKind.CollectionElement
            });
        Assert.Contains(
            structuralCapabilities,
            static capability => capability is
            {
                Role: RelationQueryStructuralCapabilityRole.CurrentItemRead,
                PathKind: RelationQueryStructuralPathKind.NestedField
            });
        Assert.DoesNotContain(
            structuralCapabilities,
            static capability => capability.PathKind == RelationQueryStructuralPathKind.CollectionElement
                && capability.Role != RelationQueryStructuralCapabilityRole.CurrentItemRead);
        Assert.DoesNotContain(
            structuralCapabilities,
            static capability => capability.PathKind == RelationQueryStructuralPathKind.NestedCollectionElement);
        Assert.Equal(
            "cohesive.relations.in-memory/realization-v2",
            RelationQueryInMemoryInterpreter.DefaultTargetProfile.Id.Value);
        Assert.Equal(
            "cohesive.relations.in-memory/realization-policy-v2",
            RelationQueryInMemoryInterpreter.DefaultRealizationPolicy.Id.Value);
    }

    [Fact]
    public void Realize_RepresentativePlanClassifiesEveryDemandedRequirementThroughSharedProfile()
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.ExplicitJoinQueryDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(compilation.IsSuccessful);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);

        var report = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var cachedReport = RelationQueryInMemoryInterpreter.Default.Realize(plan);

        Assert.True(report.IsRealizable);
        Assert.Same(report, cachedReport);
        Assert.Equal(RelationQueryRealizationStatus.Realizable, report.Status);
        Assert.Same(RelationQueryInMemoryInterpreter.Default.TargetProfile, report.TargetProfile);
        Assert.Same(RelationQueryInMemoryInterpreter.DefaultRealizationPolicy, report.Policy);
        Assert.NotEmpty(report.Requirements);
        Assert.Equal(report.Requirements.Length, report.Decisions.Length);
        Assert.All(
            report.Decisions,
            static decision => Assert.IsType<NativeRelationQueryRealizationDecision>(decision));
        Assert.Empty(report.Diagnostics);
    }

    [Fact]
    public void Realize_IdOnlyDemandDoesNotReintroducePrunedCustomerSemantics()
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.OptionalTraversalRelationDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument,
            RelationQueryCompilationDemand.ForRelationFields(
            [
                new(
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    LoadCustomerRelationFixture.SearchIdPath)
            ])));
        Assert.True(compilation.IsSuccessful);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);

        var report = RelationQueryInMemoryInterpreter.Default.Realize(plan);

        Assert.True(report.IsRealizable);
        Assert.DoesNotContain(
            report.Requirements,
            static requirement => requirement.Origin?.Node
                == LoadCustomerRelationFixture.CustomerTraversalNodeId);
        Assert.DoesNotContain(
            report.Requirements,
            static requirement => requirement.Origin?.FieldPath
                == LoadCustomerRelationFixture.SearchCustomerNamePath);
    }

    [Fact]
    public void RuntimeCapabilityEvidenceRemainsDistinctFromNativeTargetSupport()
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument,
            RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    LoadCustomerRelationFixture.AggregationResultId,
                    [
                        new(
                            LoadCustomerRelationFixture.LoadAggregateShapeId,
                            LoadCustomerRelationFixture.AggregateLoadCountPath)
                    ])
            ])));
        Assert.True(compilation.IsSuccessful);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var capability = Assert.Single(
            plan.InputContract.Capabilities,
            static candidate => candidate.Input.Capability.Capability
                == ExprCapabilities.ForAggregate(AggregateOperator.Count));
        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/in-memory-realization-runtime-capability"),
            plan,
            RelationQueryEvidenceCompleteness.Partial,
            capabilities:
            [
                new(
                    capability.Input.Id,
                    RelationQueryCapabilityEvidenceState.Unavailable,
                    "tests/runtime-capability-probe")
            ]);

        var report = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var gapAnalysis = RelationRequirementGapAnalyzer.Analyze(plan, evidence);

        Assert.True(report.IsRealizable);
        var aggregateRequirement = Assert.Single(
            report.Requirements,
            static requirement => requirement.Capability is LogicalRelationQueryCapability
            {
                Kind: RelationQueryLogicalCapabilityKind.CountAggregate
            });
        Assert.Contains(
            report.Decisions,
            decision => decision.Requirement == aggregateRequirement.Id
                && decision.Kind == CapabilityRealizationKind.Native);
        var gap = Assert.Single(gapAnalysis.Gaps);
        Assert.Equal(RelationRequirementGapCause.CapabilityUnavailable, gap.Cause);
        Assert.Equal(capability.Input.Id, gap.Input.Id);
    }

    [Fact]
    public void Analyze_PreservesRequirementAndGlobalPlanningCausesWithoutDuplicateRuntimeDiagnostics()
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(compilation.IsSuccessful);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var planReference = RelationQueryCompiledPlanReference.From(plan);
        var capability = new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Join);
        var requirement = new RelationQueryRealizationRequirement(
            new("requirement/tests/unsupported-join"),
            capability);
        var invalidRule = new RelationQueryCompositionRule(
            new("rule/tests/missing-boundary/v1"),
            capability,
            [new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.KeyExtraction)],
            [new("boundary/tests/not-declared")]);
        var profile = new RelationQueryTargetCapabilityProfile(
            new("target/tests/no-capabilities"),
            new("target/tests/no-capabilities/v1"),
            [planReference.DefinitionSchemaVersion],
            [planReference.CompilerProfile]);
        var policy = new RelationQueryRealizationPolicy(
            new("policy/tests/missing-boundary/v1"),
            "conventions/tests/v1",
            compositionRules: [invalidRule]);
        var report = RelationQueryRealizationCompiler.Match(
            planReference,
            [requirement],
            profile,
            policy);

        var diagnostics = RelationQueryInMemorySupportAnalyzer.Analyze(
            report,
            new("tests/in-memory-planning-causes"));

        Assert.Equal(RelationQueryRealizationStatus.Invalid, report.Status);
        Assert.Equal(2, diagnostics.Length);
        Assert.All(
            diagnostics,
            static diagnostic => Assert.Equal(
                RelationRuntimeDiagnosticCodes.ExecutionTargetCapabilityUnsupported,
                diagnostic.Code));
        var unavailable = Assert.Single(
            diagnostics,
            static diagnostic => diagnostic.Message.Contains(
                RelationQueryRealizationDiagnosticCodes.RequirementUnavailable,
                StringComparison.Ordinal));
        Assert.Contains(
            nameof(RelationQueryUnavailableReason.CapabilityNotAdvertised),
            unavailable.Message,
            StringComparison.Ordinal);
        Assert.Single(
            diagnostics,
            static diagnostic => diagnostic.Message.Contains(
                RelationQueryRealizationDiagnosticCodes.OperatingBoundaryMissing,
                StringComparison.Ordinal));
        Assert.Equal(
            diagnostics.Length,
            diagnostics.Select(static diagnostic => diagnostic.Message).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ExpressionCapabilities_DeclareExactImplementedCanonicalFunctionSurface()
    {
        string[] expectedDeferredFunctions =
        [
            ExprFunctionNames.EntityId,
            ExprFunctionNames.GroupBy,
            ExprFunctionNames.GroupByRows,
            ExprFunctionNames.Join,
            ExprFunctionNames.Key,
            ExprFunctionNames.SourceRows
        ];

        var profile = RelationQueryInMemoryInterpreter.ExpressionCapabilities;
        var deferredFunctions = ExprSemanticsCatalog.Default.Functions
            .Where(function => !profile.Supports(function.OperationCapability))
            .Select(static function => function.Id.Value)
            .ToArray();

        Assert.Equal(expectedDeferredFunctions, deferredFunctions);
        Assert.True(profile.Supports(ExprCapabilities.ForFunction(ExprFunctionNames.Concat)));
        Assert.False(profile.Supports(ExprCapabilities.ForFunction(ExprFunctionNames.GroupByRows)));
        Assert.All(
            ExprSemanticsCatalog.Default.UnaryOperators,
            definition => Assert.True(profile.Supports(definition.OperationCapability)));
        Assert.All(
            ExprSemanticsCatalog.Default.BinaryOperators,
            definition => Assert.True(profile.Supports(definition.OperationCapability)));
        Assert.All(
            ExprSemanticsCatalog.Default.AggregateOperators,
            definition => Assert.True(profile.Supports(definition.OperationCapability)));
    }
}
