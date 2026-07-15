using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;
using static Cohesive.Relations.Tests.TemporalRelationQueryFixture;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryTemporalExecutionCapabilityTests
{
    [Fact]
    public void DefaultProfile_SupportsEveryCanonicalTemporalCapabilitySeparatelyFromExpressions()
    {
        var profile = RelationQueryInMemoryInterpreter.DefaultTemporalCapabilities;

        Assert.Same(RelationQueryTemporalExecutionCapabilityProfile.All, profile);
        Assert.Same(profile, RelationQueryInMemoryInterpreter.Default.TemporalCapabilities);
        Assert.All(
            Enum.GetValues<RelationQueryTemporalExecutionCapability>(),
            capability => Assert.True(profile.Supports(capability)));
        Assert.IsType<ExprCapabilityProfile>(RelationQueryInMemoryInterpreter.ExpressionCapabilities);
    }

    [Fact]
    public void CapabilityProfile_NormalizesValuesAndRejectsUnknownCapabilities()
    {
        var profile = new RelationQueryTemporalExecutionCapabilityProfile(
        [
            RelationQueryTemporalExecutionCapability.InstantDomain,
            RelationQueryTemporalExecutionCapability.PointInInterval,
            RelationQueryTemporalExecutionCapability.InstantDomain
        ]);

        Assert.Equal(
        [
            RelationQueryTemporalExecutionCapability.PointInInterval,
            RelationQueryTemporalExecutionCapability.InstantDomain
        ],
            profile.SupportedCapabilities.ToArray());
        Assert.True(profile.Supports(RelationQueryTemporalExecutionCapability.InstantDomain));
        Assert.False(profile.Supports(RelationQueryTemporalExecutionCapability.IntervalOverlap));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RelationQueryTemporalExecutionCapabilityProfile(
            [
                (RelationQueryTemporalExecutionCapability)int.MaxValue
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            profile.Supports((RelationQueryTemporalExecutionCapability)int.MaxValue));
    }

    [Fact]
    public void Analyze_TemporalProfileDoesNotConstrainOrdinaryExpressionExecution()
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.ExplicitJoinQueryDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(compilation.IsSuccessful);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);

        var diagnostics = RelationQueryInMemorySupportAnalyzer.Analyze(
            plan,
            new("tests/non-temporal-capability-analysis"),
            RelationQueryTemporalExecutionCapabilityProfile.None);

        Assert.Empty(diagnostics);
        Assert.Empty(plan.InputContract.TemporalCapabilities);
        Assert.NotEmpty(plan.ExecutionSlice.ExpressionSites);
        Assert.DoesNotContain(plan.ExecutionSlice.Nodes, static node => node.TemporalJoin is not null);
    }

    [Fact]
    public void Analyze_MissingTemporalCapabilitiesProduceAttributableDiagnostics()
    {
        var plan = Compile(CreateQueryDocument(
            CreatePointMatch(upperNullBehavior: TemporalNullBoundBehavior.Unbounded),
            JoinKind.Left));
        var profile = Without(
            RelationQueryTemporalExecutionCapability.ExclusiveBoundary,
            RelationQueryTemporalExecutionCapability.NullAsUnbounded,
            RelationQueryTemporalExecutionCapability.InstantDomain,
            RelationQueryTemporalExecutionCapability.LeftOuterJoin);
        var temporal = Assert.Single(plan.ExecutionSlice.Nodes, static node => node.TemporalJoin is not null)
            .TemporalJoin!;
        var upperSite = temporal.Intervals[0].Upper.ValueSite!.Analysis.Site.Id.Value;

        Assert.Empty(RelationQueryInMemorySupportAnalyzer.Analyze(
            plan,
            new("tests/default-temporal-capability-analysis"),
            RelationQueryInMemoryInterpreter.DefaultTemporalCapabilities));

        var diagnostics = RelationQueryInMemorySupportAnalyzer.Analyze(
            plan,
            new("tests/temporal-capability-analysis"),
            profile);

        Assert.Equal(4, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(RelationRuntimeDiagnosticCodes.ExecutionTargetCapabilityUnsupported, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.NotNull(diagnostic.Input);
            Assert.Equal(TemporalJoin, diagnostic.Node);
            var requirement = Assert.Single(
                plan.InputContract.TemporalCapabilities,
                candidate => candidate.Id == diagnostic.Input);
            Assert.Equal(diagnostic.Node, requirement.Node);
            Assert.Equal(diagnostic.SemanticSite, requirement.SemanticSite);
            Assert.Contains(
                requirement.Capability.ToString(),
                diagnostic.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                RelationQueryRealizationDiagnosticCodes.RequirementUnavailable,
                diagnostic.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                nameof(RelationQueryUnavailableReason.CapabilityNotAdvertised),
                diagnostic.Message,
                StringComparison.Ordinal);
        });
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.SemanticSite == temporal.CorrelationSite.Analysis.Site.Id.Value
                && diagnostic.Message.Contains(
                    nameof(RelationQueryTemporalExecutionCapability.LeftOuterJoin),
                    StringComparison.Ordinal));
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.SemanticSite == upperSite
                && diagnostic.Message.Contains(
                    nameof(RelationQueryTemporalExecutionCapability.ExclusiveBoundary),
                    StringComparison.Ordinal));
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.SemanticSite == upperSite
                && diagnostic.Message.Contains(
                    nameof(RelationQueryTemporalExecutionCapability.NullAsUnbounded),
                    StringComparison.Ordinal));
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.SemanticSite == temporal.PointSite!.Analysis.Site.Id.Value
                && diagnostic.Message.Contains(
                    nameof(RelationQueryTemporalExecutionCapability.InstantDomain),
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Execute_InjectedProfileRejectsUnsupportedTemporalMatchDuringPreflight()
    {
        var plan = Compile(CreateQueryDocument(CreatePointMatch(), JoinKind.Inner));
        var profile = Without(RelationQueryTemporalExecutionCapability.PointInInterval);
        var interpreter = new RelationQueryInMemoryInterpreter(profile);
        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/temporal-capability-preflight"),
            plan,
            RelationQueryEvidenceCompleteness.Partial);
        var temporal = Assert.Single(plan.ExecutionSlice.Nodes, static node => node.TemporalJoin is not null)
            .TemporalJoin!;
        var pointSite = temporal.PointSite!.Analysis.Site.Id.Value;
        var expectedMatchSite = pointSite[..^"/point".Length];

        var realization = interpreter.Realize(plan);
        var result = interpreter.Execute(new(plan, evidence));

        Assert.False(realization.IsRealizable);
        var unavailable = Assert.Single(
            realization.Decisions.OfType<UnavailableRelationQueryRealizationDecision>(),
            decision => realization.Requirements.Single(requirement => requirement.Id == decision.Requirement)
                .Capability is TemporalRelationQueryCapability
            {
                Capability: RelationQueryTemporalExecutionCapability.PointInInterval
            });
        var realizationRequirement = Assert.Single(
            realization.Requirements,
            requirement => requirement.Id == unavailable.Requirement);
        Assert.Equal(TemporalJoin, realizationRequirement.Origin?.Node);
        Assert.Equal(expectedMatchSite, realizationRequirement.Origin?.SemanticSite);
        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Null(result.Relation);
        Assert.Empty(result.QueryResults);
        Assert.Empty(result.RequirementGapAnalysis.Gaps);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionTargetCapabilityUnsupported);
        var requirement = Assert.Single(
            plan.InputContract.TemporalCapabilities,
            candidate => candidate.Id == diagnostic.Input);
        Assert.Equal(RelationQueryTemporalExecutionCapability.PointInInterval, requirement.Capability);
        Assert.Equal(TemporalJoin, diagnostic.Node);
        Assert.Equal(expectedMatchSite, diagnostic.SemanticSite);
        Assert.Contains(
            nameof(RelationQueryTemporalExecutionCapability.PointInInterval),
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_IntervalOverlapUsesItsDistinctTargetCapability()
    {
        var plan = Compile(CreateQueryDocument(CreateOverlapMatch(), JoinKind.Inner));
        var profile = Without(RelationQueryTemporalExecutionCapability.IntervalOverlap);

        var diagnostics = RelationQueryInMemorySupportAnalyzer.Analyze(
            plan,
            new("tests/temporal-overlap-capability-analysis"),
            profile);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(RelationRuntimeDiagnosticCodes.ExecutionTargetCapabilityUnsupported, diagnostic.Code);
        Assert.Equal(TemporalJoin, diagnostic.Node);
        Assert.True(
            diagnostic.SemanticSite?.EndsWith(
                "/temporalJoin/intervalOverlap",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            nameof(RelationQueryTemporalExecutionCapability.IntervalOverlap),
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_StructurallyUnboundedEndpointHasStableCapabilityAttribution()
    {
        var match = new TemporalPointInIntervalMatch(
            Expr.Field(Event, OccurredAt),
            new TemporalInterval(
                new ExpressionTemporalIntervalBound(
                    Expr.Field(VersionBinding, ValidFrom),
                    TemporalBoundaryInclusion.Inclusive),
                new UnboundedTemporalIntervalBound()));
        var plan = Compile(CreateQueryDocument(match));

        var diagnostic = Assert.Single(RelationQueryInMemorySupportAnalyzer.Analyze(
            plan,
            new("tests/unbounded-temporal-capability-analysis"),
            Without(RelationQueryTemporalExecutionCapability.UnboundedBoundary)));

        Assert.Equal(RelationRuntimeDiagnosticCodes.ExecutionTargetCapabilityUnsupported, diagnostic.Code);
        Assert.Equal(TemporalJoin, diagnostic.Node);
        Assert.True(
            diagnostic.SemanticSite?.EndsWith(
                "/temporalJoin/pointInInterval/interval/0/upper",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            nameof(RelationQueryTemporalExecutionCapability.UnboundedBoundary),
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    static RelationQueryTemporalExecutionCapabilityProfile Without(
        params RelationQueryTemporalExecutionCapability[] excluded) =>
        new(Enum.GetValues<RelationQueryTemporalExecutionCapability>().Except(excluded));

    static CompiledRelationQueryPlan Compile(RelationQueryDocument document)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            [CreateShapeGraphDocument()]));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }
}
