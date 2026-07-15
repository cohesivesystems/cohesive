using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using static Cohesive.Relations.Tests.TemporalRelationQueryFixture;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Contract tests for canonical temporal-join persistence, analysis, and static compilation.
/// </summary>
public sealed class TemporalRelationQueryContractTests
{
    [Fact]
    public void TemporalJoinDocument_RoundTripsMatchAndBoundDiscriminators()
    {
        var match = new TemporalPointInIntervalMatch(
            Expr.Field(Event, OccurredAt),
            new TemporalInterval(
                new ExpressionTemporalIntervalBound(
                    Expr.Field(VersionBinding, ValidFrom),
                    TemporalBoundaryInclusion.Inclusive),
                new UnboundedTemporalIntervalBound()));
        var document = RelationQueryDocument.FromDefinition(CreateQuery(match));

        var json = RelationQueryJsonSerializer.Serialize(document, indented: false);
        var roundTripped = RelationQueryJsonSerializer.Deserialize(json);

        Assert.Contains("\"$node\":\"temporalJoin\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$temporalMatch\":\"pointInInterval\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$temporalBound\":\"expression\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$temporalBound\":\"unbounded\"", json, StringComparison.Ordinal);
        Assert.Equal(document.DefinitionFingerprint, roundTripped.DefinitionFingerprint);

        var query = Assert.IsType<QueryDefinition>(roundTripped.Definition);
        var join = Assert.Single(query.Body.Nodes.OfType<TemporalJoinQueryNode>());
        var point = Assert.IsType<TemporalPointInIntervalMatch>(join.Match);
        var lower = Assert.IsType<ExpressionTemporalIntervalBound>(point.Interval.Lower);
        Assert.Equal(TemporalBoundaryInclusion.Inclusive, lower.Inclusion);
        Assert.Equal(TemporalNullBoundBehavior.Invalid, lower.NullBehavior);
        Assert.IsType<UnboundedTemporalIntervalBound>(point.Interval.Upper);

        AssertTemporalDiscriminatorIsRequired(json, "$temporalMatch", static node =>
            node["definition"]!["body"]!["nodes"]!.AsArray()
                .Single(item => item![RelationQueryWireNames.NodeDiscriminator]!.GetValue<string>()
                    == RelationQueryWireNames.TemporalJoinNode)!["match"]!.AsObject());
        AssertTemporalDiscriminatorIsRequired(json, "$temporalBound", static node =>
            node["definition"]!["body"]!["nodes"]!.AsArray()
                .Single(item => item![RelationQueryWireNames.NodeDiscriminator]!.GetValue<string>()
                    == RelationQueryWireNames.TemporalJoinNode)!["match"]!["interval"]!["lower"]!.AsObject());
    }

    [Fact]
    public void TemporalJoinDocument_RoundTripsOverlapAndRejectsUnknownTemporalDiscriminator()
    {
        var document = CreateQueryDocument(CreateOverlapMatch());
        var json = RelationQueryJsonSerializer.Serialize(document, indented: false);
        var roundTripped = RelationQueryJsonSerializer.Deserialize(json);

        Assert.Contains("\"$temporalMatch\":\"intervalOverlap\"", json, StringComparison.Ordinal);
        Assert.Equal(document.DefinitionFingerprint, roundTripped.DefinitionFingerprint);
        var query = Assert.IsType<QueryDefinition>(roundTripped.Definition);
        Assert.IsType<TemporalIntervalOverlapMatch>(
            Assert.Single(query.Body.Nodes.OfType<TemporalJoinQueryNode>()).Match);

        var root = JsonNode.Parse(json)!.AsObject();
        var match = root["definition"]!["body"]!["nodes"]!.AsArray()
            .Single(item => item![RelationQueryWireNames.NodeDiscriminator]!.GetValue<string>()
                == RelationQueryWireNames.TemporalJoinNode)!["match"]!.AsObject();
        match[RelationQueryWireNames.TemporalMatchDiscriminator] = "futureTemporalMatch";

        var validation = RelationQueryJsonSerializer.TryDeserialize(root.ToJsonString(), out var rejected);

        Assert.False(validation.IsValid);
        Assert.Null(rejected);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.deserialize.invalid");
    }

    [Fact]
    public void Analyze_PointContainmentUsesStableSitesAndSideScopesAndOmitsUnboundedBound()
    {
        var match = new TemporalPointInIntervalMatch(
            Expr.Field(Event, OccurredAt),
            new TemporalInterval(
                new ExpressionTemporalIntervalBound(
                    Expr.Field(VersionBinding, ValidFrom),
                    TemporalBoundaryInclusion.Inclusive),
                new UnboundedTemporalIntervalBound()));

        var analysis = RelationQueryExpressionAnalyzer.Analyze(
            CreateQuery(match),
            [CreateShapeGraph()]);

        Assert.True(analysis.IsValid, FormatDiagnostics(analysis.Validation));
        var temporalSites = TemporalSites(analysis);
        Assert.Equal(
        [
            "query/temporal-query/node/temporal/temporalJoin/correlation",
            "query/temporal-query/node/temporal/temporalJoin/pointInInterval/interval/0/lower",
            "query/temporal-query/node/temporal/temporalJoin/pointInInterval/point"
        ],
            temporalSites.Select(static site => site.Analysis.Site.Id.Value));
        Assert.Equal(
        [
            (RelationQueryExpressionSiteKind.TemporalJoinCorrelation, (int?)null),
            (RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound, 0),
            (RelationQueryExpressionSiteKind.TemporalJoinPoint, (int?)null)
        ],
            temporalSites.Select(static site => (site.Kind, site.Ordinal)));

        Assert.Equal(
            [Event, VersionBinding],
            Site(temporalSites, RelationQueryExpressionSiteKind.TemporalJoinCorrelation)
                .Analysis.Site.Scope.Bindings.Select(static binding => binding.Id));
        Assert.Equal(
            [Event],
            Site(temporalSites, RelationQueryExpressionSiteKind.TemporalJoinPoint)
                .Analysis.Site.Scope.Bindings.Select(static binding => binding.Id));
        Assert.Equal(
            [VersionBinding],
            Site(temporalSites, RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound, ordinal: 0)
                .Analysis.Site.Scope.Bindings.Select(static binding => binding.Id));
        Assert.DoesNotContain(
            temporalSites,
            static site => site.Kind == RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound);
    }

    [Fact]
    public void Analyze_IntervalOverlapUsesOrderedSideScopedIntervalSites()
    {
        var match = new TemporalIntervalOverlapMatch(
            TemporalInterval.HalfOpen(
                Expr.Field(Event, EventStart),
                Expr.Field(Event, EventEnd)),
            TemporalInterval.HalfOpen(
                Expr.Field(VersionBinding, ValidFrom),
                Expr.Field(VersionBinding, ValidTo)));

        var analysis = RelationQueryExpressionAnalyzer.Analyze(
            CreateQuery(match),
            [CreateShapeGraph()]);

        Assert.True(analysis.IsValid, FormatDiagnostics(analysis.Validation));
        var temporalSites = TemporalSites(analysis);
        Assert.Equal(
        [
            "query/temporal-query/node/temporal/temporalJoin/correlation",
            "query/temporal-query/node/temporal/temporalJoin/intervalOverlap/interval/0/lower",
            "query/temporal-query/node/temporal/temporalJoin/intervalOverlap/interval/0/upper",
            "query/temporal-query/node/temporal/temporalJoin/intervalOverlap/interval/1/lower",
            "query/temporal-query/node/temporal/temporalJoin/intervalOverlap/interval/1/upper"
        ],
            temporalSites.Select(static site => site.Analysis.Site.Id.Value));
        Assert.Equal(
        [
            (RelationQueryExpressionSiteKind.TemporalJoinCorrelation, (int?)null),
            (RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound, 0),
            (RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound, 0),
            (RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound, 1),
            (RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound, 1)
        ],
            temporalSites.Select(static site => (site.Kind, site.Ordinal)));

        foreach (var site in temporalSites.Where(static site => site.Ordinal == 0))
        {
            Assert.Equal(
                [Event],
                site.Analysis.Site.Scope.Bindings.Select(static binding => binding.Id));
        }
        foreach (var site in temporalSites.Where(static site => site.Ordinal == 1))
        {
            Assert.Equal(
                [VersionBinding],
                site.Analysis.Site.Scope.Bindings.Select(static binding => binding.Id));
        }

        var versionBinding = Assert.Single(
            analysis.BindingShapes,
            binding => binding.Node == TemporalJoin && binding.Binding == VersionBinding);
        Assert.Equal(RelationQueryBindingAvailability.MayBeAbsent, versionBinding.Availability);
    }

    [Fact]
    public void Analyze_RejectsCrossSideTemporalOperandReference()
    {
        var match = new TemporalIntervalOverlapMatch(
            TemporalInterval.HalfOpen(
                Expr.Field(VersionBinding, ValidFrom),
                Expr.Field(Event, EventEnd)),
            TemporalInterval.HalfOpen(
                Expr.Field(VersionBinding, ValidFrom),
                Expr.Field(VersionBinding, ValidTo)));

        var analysis = RelationQueryExpressionAnalyzer.Analyze(
            CreateQuery(match),
            [CreateShapeGraph()]);

        var diagnostic = Assert.Single(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.bindingMissing"
                && diagnostic.Location == "/definition/body/nodes/temporal/match/left/lower/value");
        Assert.Contains("version", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_RejectsNonBooleanCorrelationAndNonTemporalOperands()
    {
        var definition = CreateQuery(new TemporalPointInIntervalMatch(
            Expr.Field(Event, Id),
            TemporalInterval.HalfOpen(
                Expr.Field(VersionBinding, CorrelationKey),
                Expr.Field(VersionBinding, ValidTo))));
        var temporal = Assert.Single(definition.Body.Nodes.OfType<TemporalJoinQueryNode>());
        var invalid = definition with
        {
            Body = new LogicalQueryDefinition(
            [
                .. definition.Body.Nodes.Select(node => node.Id == temporal.Id
                    ? temporal with { Correlation = Expr.Field(Event, Id) }
                    : node)
            ],
                definition.Body.Parameters)
        };

        var analysis = RelationQueryExpressionAnalyzer.Analyze(invalid, [CreateShapeGraph()]);

        Assert.Contains(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultCategoryMismatch"
                && diagnostic.Location == "/definition/body/nodes/temporal/correlation");
        Assert.Contains(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultCategoryMismatch"
                && diagnostic.Location == "/definition/body/nodes/temporal/match/point");
        Assert.Contains(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultCategoryMismatch"
                && diagnostic.Location == "/definition/body/nodes/temporal/match/interval/lower/value");
    }

    [Fact]
    public void Validate_RejectsUnsupportedTemporalBoundaryAndNullPolicyValues()
    {
        var match = CreatePointMatch();
        var lower = Assert.IsType<ExpressionTemporalIntervalBound>(match.Interval.Lower) with
        {
            Inclusion = (TemporalBoundaryInclusion)int.MaxValue
        };
        var upper = Assert.IsType<ExpressionTemporalIntervalBound>(match.Interval.Upper) with
        {
            NullBehavior = (TemporalNullBoundBehavior)int.MaxValue
        };
        var definition = CreateQuery(match with
        {
            Interval = new TemporalInterval(lower, upper)
        });
        var document = new RelationQueryDocument(
            RelationQueryDocument.CurrentSchemaVersion,
            definition,
            new RelationQueryDefinitionFingerprint(
                RelationQueryDefinitionFingerprinter.Algorithm,
                RelationQueryDefinitionFingerprinter.Canonicalization,
                new string('0', 64)));

        var validation = RelationQueryDocumentSemanticValidator.Validate(document);

        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.temporalJoin.boundInclusionInvalid");
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.temporalJoin.nullBoundBehaviorInvalid");
    }

    [Fact]
    public void StaticCompiler_RejectsMixedExactTemporalDomains()
    {
        var definition = CreateQuery(CreatePointMatch());
        var document = RelationQueryDocument.FromDefinition(definition);
        var shapeDocument = ShapeGraphDocument.FromGraph(
            CreateShapeGraph(rightTemporalDomain: ScalarTypeKind.DateTime));

        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            [shapeDocument]));

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.temporalJoin.domainMismatch");
        Assert.Equal("/definition/body/nodes/temporal/match", diagnostic.Location);
        Assert.Contains(nameof(ScalarTypeKind.DateTime), diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ScalarTypeKind.Instant), diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticCompiler_RejectsReversedLiteralIntervalButAllowsEqualExclusiveBounds()
    {
        var instant = new ScalarTypeRef(ScalarTypeKind.Instant);
        var earlier = ObservationValue.FromString("2026-07-14T09:00:00Z");
        var later = ObservationValue.FromString("2026-07-15T09:00:00Z");

        var reversedMatch = new TemporalPointInIntervalMatch(
            Expr.Field(Event, OccurredAt),
            new TemporalInterval(
                new ExpressionTemporalIntervalBound(
                    new LiteralExpr(instant, later),
                    TemporalBoundaryInclusion.Inclusive),
                new ExpressionTemporalIntervalBound(
                    new LiteralExpr(instant, earlier),
                    TemporalBoundaryInclusion.Exclusive)));
        var reversedDefinition = CreateQuery(reversedMatch);
        var reversedDocument = new RelationQueryDocument(
            RelationQueryDocument.CurrentSchemaVersion,
            reversedDefinition,
            RelationQueryDefinitionFingerprinter.Compute(reversedDefinition));
        var reversed = RelationQueryStaticCompiler.Compile(new(
            reversedDocument,
            [CreateShapeGraphDocument()]));

        Assert.False(reversed.IsSuccessful);
        Assert.Null(reversed.Plan);
        var diagnostic = Assert.Single(
            reversed.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.temporalJoin.intervalInvalid");
        Assert.Equal("/definition/body/nodes/temporal/match/interval", diagnostic.Location);

        var equalExclusiveMatch = new TemporalPointInIntervalMatch(
            Expr.Field(Event, OccurredAt),
            new TemporalInterval(
                new ExpressionTemporalIntervalBound(
                    new LiteralExpr(instant, earlier),
                    TemporalBoundaryInclusion.Exclusive),
                new ExpressionTemporalIntervalBound(
                    new LiteralExpr(instant, earlier),
                    TemporalBoundaryInclusion.Exclusive)));
        var equalExclusive = RelationQueryStaticCompiler.Compile(new(
            CreateQueryDocument(equalExclusiveMatch),
            [CreateShapeGraphDocument()]));

        Assert.True(equalExclusive.IsSuccessful, FormatDiagnostics(equalExclusive.Validation));
    }

    [Fact]
    public void StaticCompiler_ProjectsTemporalRequirementsIntoInfluenceLineageAndDependencies()
    {
        var plan = Compile(CreatePointMatch());
        var eventKey = FieldInput(plan, EventShape, CorrelationKey);
        var versionKey = FieldInput(plan, VersionShape, CorrelationKey);
        var point = FieldInput(plan, EventShape, OccurredAt);
        var lower = FieldInput(plan, VersionShape, ValidFrom);
        var upper = FieldInput(plan, VersionShape, ValidTo);
        var projectedId = FieldInput(plan, EventShape, Id);

        var joinEffects = new[]
        {
            RelationQueryRequirementEffect.Membership,
            RelationQueryRequirementEffect.Correlation,
            RelationQueryRequirementEffect.Cardinality
        };
        Assert.Equal(joinEffects, Effects(plan, eventKey));
        Assert.Equal(joinEffects, Effects(plan, versionKey));

        var temporalEffects = new[]
        {
            RelationQueryRequirementEffect.Membership,
            RelationQueryRequirementEffect.Correlation,
            RelationQueryRequirementEffect.Cardinality,
            RelationQueryRequirementEffect.Validation
        };
        Assert.Equal(temporalEffects, Effects(plan, point));
        Assert.Equal(temporalEffects, Effects(plan, lower));
        Assert.Equal(temporalEffects, Effects(plan, upper));

        AssertTraceSite(
            plan,
            point,
            RelationQueryExpressionSiteKind.TemporalJoinPoint,
            ordinal: null);
        AssertTraceSite(
            plan,
            lower,
            RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound,
            ordinal: 0);
        AssertTraceSite(
            plan,
            upper,
            RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound,
            ordinal: 0);

        var lineageInputs = plan.Lineage.Entries
            .SelectMany(static entry => entry.Contributions)
            .Select(static contribution => contribution.Input.Id)
            .ToHashSet();
        Assert.Contains(projectedId.Id, lineageInputs);
        Assert.DoesNotContain(eventKey.Id, lineageInputs);
        Assert.DoesNotContain(versionKey.Id, lineageInputs);
        Assert.DoesNotContain(point.Id, lineageInputs);
        Assert.DoesNotContain(lower.Id, lineageInputs);
        Assert.DoesNotContain(upper.Id, lineageInputs);

        var lineageInfluences = plan.Lineage.Entries
            .SelectMany(static entry => entry.Influences)
            .GroupBy(static influence => influence.Input.Id)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static influence => influence.Effect)
                    .Distinct()
                    .OrderBy(static effect => (int)effect)
                    .ToArray());

        foreach (var input in new[] { eventKey, versionKey, point, lower, upper })
        {
            Assert.Equal(Effects(plan, input), lineageInfluences[input.Id]);
            var dependency = Assert.Single(
                plan.DependencyManifest.Entries,
                entry => entry.Input.Id == input.Id);
            Assert.Equal(Effects(plan, input), dependency.Impacts
                .Select(static impact => impact.Effect)
                .Distinct()
                .OrderBy(static effect => (int)effect));
        }

        var logicalJoin = Assert.Single(
            plan.LogicalPlan.Nodes,
            static node => node.Node == TemporalJoin);
        Assert.Equal([EventSource, VersionSource], logicalJoin.Inputs.Select(static input => input.CanonicalInput));

        var executionNode = Assert.Single(
            plan.ExecutionSlice.Nodes,
            static node => node.Id == TemporalJoin);
        var temporal = Assert.IsType<RelationQueryTemporalJoinExecution>(executionNode.TemporalJoin);
        Assert.Equal(ScalarTypeKind.Instant, temporal.Domain);
        Assert.Equal(RelationQueryExpressionSiteKind.TemporalJoinPoint, temporal.PointSite?.Kind);
        var interval = Assert.Single(temporal.Intervals);
        Assert.Equal(0, interval.Ordinal);
        Assert.Equal(RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound, interval.Lower.ValueSite?.Kind);
        Assert.Equal(RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound, interval.Upper.ValueSite?.Kind);

        Assert.Equal(
        [
            RelationQueryTemporalExecutionCapability.PointInInterval,
            RelationQueryTemporalExecutionCapability.InclusiveBoundary,
            RelationQueryTemporalExecutionCapability.ExclusiveBoundary,
            RelationQueryTemporalExecutionCapability.InstantDomain,
            RelationQueryTemporalExecutionCapability.PreserveAllMatches,
            RelationQueryTemporalExecutionCapability.LeftOuterJoin,
            RelationQueryTemporalExecutionCapability.ValidateIntervals,
            RelationQueryTemporalExecutionCapability.InconclusiveEvidence
        ],
            plan.InputContract.TemporalCapabilities
                .Select(static capability => capability.Capability)
                .OrderBy(static capability => (int)capability));
        Assert.All(plan.InputContract.TemporalCapabilities, capability =>
        {
            Assert.StartsWith("input/temporal-capability/", capability.Id.Value, StringComparison.Ordinal);
            Assert.Equal(TemporalJoin, capability.Node);
            Assert.StartsWith(
                "query/temporal-query/node/temporal/temporalJoin/",
                capability.SemanticSite,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TemporalJoinFingerprint_IsStableAndSensitiveToBoundarySemantics()
    {
        var baseline = RelationQueryDocument.FromDefinition(CreateQuery(CreatePointMatch()));
        var equivalent = RelationQueryDocument.FromDefinition(CreateQuery(CreatePointMatch()));
        var exclusiveLower = RelationQueryDocument.FromDefinition(CreateQuery(CreatePointMatch(
            lowerInclusion: TemporalBoundaryInclusion.Exclusive)));
        var nullMeansUnbounded = RelationQueryDocument.FromDefinition(CreateQuery(CreatePointMatch(
            upperNullBehavior: TemporalNullBoundBehavior.Unbounded)));

        Assert.Equal(baseline.DefinitionFingerprint, equivalent.DefinitionFingerprint);
        Assert.NotEqual(baseline.DefinitionFingerprint, exclusiveLower.DefinitionFingerprint);
        Assert.NotEqual(baseline.DefinitionFingerprint, nullMeansUnbounded.DefinitionFingerprint);
        Assert.NotEqual(exclusiveLower.DefinitionFingerprint, nullMeansUnbounded.DefinitionFingerprint);
    }

    static CompiledRelationQueryPlan Compile(TemporalJoinMatch match)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            RelationQueryDocument.FromDefinition(CreateQuery(match)),
            [ShapeGraphDocument.FromGraph(CreateShapeGraph())]));
        Assert.True(result.IsSuccessful, FormatDiagnostics(result.Validation));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static ImmutableArray<RelationQueryExpressionSiteAnalysis> TemporalSites(
        RelationQueryExpressionAnalysisResult analysis) =>
    [
        .. analysis.SiteAnalyses.Where(static site => site.Kind is
            RelationQueryExpressionSiteKind.TemporalJoinCorrelation
            or RelationQueryExpressionSiteKind.TemporalJoinPoint
            or RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound
            or RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound)
    ];

    static RelationQueryExpressionSiteAnalysis Site(
        ImmutableArray<RelationQueryExpressionSiteAnalysis> sites,
        RelationQueryExpressionSiteKind kind,
        int? ordinal = null) =>
        Assert.Single(sites, site => site.Kind == kind && site.Ordinal == ordinal);

    static RelationQueryFieldInput FieldInput(
        CompiledRelationQueryPlan plan,
        QualifiedShapeId shape,
        FieldPath path) =>
        Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Field.Shape == shape && input.Field.Path == path);

    static RelationQueryRequirementEffect[] Effects(
        CompiledRelationQueryPlan plan,
        RelationQueryRequirementInput input) =>
        plan.RequirementGraph.Edges
            .Where(edge => edge.Input.Id == input.Id)
            .Select(static edge => edge.Effect)
            .Distinct()
            .OrderBy(static effect => (int)effect)
            .ToArray();

    static void AssertTraceSite(
        CompiledRelationQueryPlan plan,
        RelationQueryRequirementInput input,
        RelationQueryExpressionSiteKind kind,
        int? ordinal)
    {
        Assert.All(
            plan.RequirementGraph.Edges.Where(edge => edge.Input.Id == input.Id),
            edge => Assert.All(
                edge.Traces,
                trace => Assert.Contains(
                    trace.Steps,
                    step => step.SiteKind == kind && step.Ordinal == ordinal)));
    }

    static void AssertTemporalDiscriminatorIsRequired(
        string json,
        string discriminator,
        Func<JsonObject, JsonObject> selectPolymorphicValue)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.True(selectPolymorphicValue(root).Remove(discriminator));

        var validation = RelationQueryJsonSerializer.TryDeserialize(root.ToJsonString(), out var document);

        Assert.False(validation.IsValid);
        Assert.Null(document);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.deserialize.invalid");
    }

    static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message} ({diagnostic.Location})"));
}
