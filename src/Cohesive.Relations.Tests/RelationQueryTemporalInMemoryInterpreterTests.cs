using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using static Cohesive.Relations.Tests.TemporalRelationQueryFixture;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryTemporalInMemoryInterpreterTests
{
    [Fact]
    public void Execute_PointContainmentUsesHalfOpenBoundarySemantics()
    {
        var plan = Compile(CreateExecutionDocument(CreatePointMatch(), JoinKind.Inner));
        var evidence = CreateEvidence(
            plan,
            events:
            [
                PointEvent("lower", "a", day: 1),
                PointEvent("inside", "a", day: 15),
                PointEvent("upper", "a", day: 31)
            ],
            versions: [Version("window", "a", dayFrom: 1, dayTo: 31)]);

        var result = Execute(plan, evidence);

        AssertStatus(result, RelationQueryExecutionStatus.Succeeded);
        Assert.Empty(result.Diagnostics);
        AssertRows(
            result,
            RelationQueryExecutionOutputState.Complete,
            "event/inside|version/window",
            "event/lower|version/window");
    }

    [Fact]
    public void Execute_StructurallyUnboundedUpperEndpointMatchesLaterPoints()
    {
        var match = new TemporalPointInIntervalMatch(
            Expr.Field(Event, OccurredAt),
            new TemporalInterval(
                new ExpressionTemporalIntervalBound(
                    Expr.Field(VersionBinding, ValidFrom),
                    TemporalBoundaryInclusion.Inclusive),
                new UnboundedTemporalIntervalBound()));
        var plan = Compile(CreateExecutionDocument(match, JoinKind.Inner));
        var evidence = CreateEvidence(
            plan,
            events: [PointEvent("later", "a", day: 31)],
            versions: [Version("current", "a", dayFrom: 1, dayTo: 2)]);

        var result = Execute(plan, evidence);

        AssertStatus(result, RelationQueryExecutionStatus.Succeeded);
        AssertRows(
            result,
            RelationQueryExecutionOutputState.Complete,
            "event/later|version/current");
    }

    [Fact]
    public void Execute_HalfOpenBoundaryMatchesNextIntervalAndNotPreviousInterval()
    {
        var plan = Compile(CreateExecutionDocument(CreatePointMatch(), JoinKind.Inner));
        var evidence = CreateEvidence(
            plan,
            events: [PointEvent("boundary", "a", day: 15)],
            versions:
            [
                Version("previous", "a", dayFrom: 1, dayTo: 15),
                Version("next", "a", dayFrom: 15, dayTo: 31)
            ]);

        var result = Execute(plan, evidence);

        AssertStatus(result, RelationQueryExecutionStatus.Succeeded);
        AssertRows(
            result,
            RelationQueryExecutionOutputState.Complete,
            "event/boundary|version/next");
    }

    [Fact]
    public void Execute_IntervalOverlapUsesCanonicalOverlapSemantics()
    {
        var plan = Compile(CreateExecutionDocument(CreateOverlapMatch(), JoinKind.Inner));
        var evidence = CreateEvidence(
            plan,
            events:
            [
                IntervalEvent("overlap", "a", dayFrom: 1, dayTo: 10),
                IntervalEvent("touching", "b", dayFrom: 1, dayTo: 5),
                IntervalEvent("disjoint", "c", dayFrom: 1, dayTo: 4)
            ],
            versions:
            [
                Version("overlap", "a", dayFrom: 5, dayTo: 15),
                Version("touching", "b", dayFrom: 5, dayTo: 10),
                Version("disjoint", "c", dayFrom: 8, dayTo: 12)
            ]);

        var result = Execute(plan, evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        AssertRows(
            result,
            RelationQueryExecutionOutputState.Complete,
            "event/overlap|version/overlap");
    }

    [Theory]
    [InlineData(JoinKind.Inner, false, false)]
    [InlineData(JoinKind.Left, true, false)]
    [InlineData(JoinKind.Right, false, true)]
    [InlineData(JoinKind.Full, true, true)]
    public void Execute_TemporalJoinHonorsEveryJoinKind(
        JoinKind joinKind,
        bool includesLeftOnly,
        bool includesRightOnly)
    {
        var plan = Compile(CreateExecutionDocument(CreatePointMatch(), joinKind));
        var evidence = CreateEvidence(
            plan,
            events:
            [
                PointEvent("match", "a", day: 10),
                PointEvent("left", "b", day: 10)
            ],
            versions:
            [
                Version("match", "a", dayFrom: 1, dayTo: 20),
                Version("right", "c", dayFrom: 1, dayTo: 20)
            ]);

        var result = Execute(plan, evidence);

        List<string> expected = ["event/match|version/match"];
        if (includesLeftOnly)
            expected.Add("event/left");
        if (includesRightOnly)
            expected.Add("version/right");
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        AssertRows(result, RelationQueryExecutionOutputState.Complete, [.. expected]);
    }

    [Fact]
    public void Execute_MultipleOverlappingIntervalsEmitEveryMatchingPair()
    {
        var plan = Compile(CreateExecutionDocument(CreateOverlapMatch(), JoinKind.Inner));
        var evidence = CreateEvidence(
            plan,
            events: [IntervalEvent("event", "a", dayFrom: 12, dayTo: 18)],
            versions:
            [
                Version("wide", "a", dayFrom: 1, dayTo: 31),
                Version("narrow", "a", dayFrom: 10, dayTo: 20)
            ]);

        var result = Execute(plan, evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        AssertRows(
            result,
            RelationQueryExecutionOutputState.Complete,
            "event/event|version/narrow",
            "event/event|version/wide");
        Assert.Equal(
        [
            "event/event|version/narrow",
            "event/event|version/wide"
        ],
            Assert.Single(result.QueryResults).Rows.Select(ProvenanceKey));
    }

    [Fact]
    public void Execute_NullInvalidEndpointIsIndeterminateAndSuppressesOuterRows()
    {
        var plan = Compile(CreateExecutionDocument(
            CreatePointMatch(upperNullBehavior: TemporalNullBoundBehavior.Invalid),
            JoinKind.Full));
        var evidence = CreateEvidence(
            plan,
            events: [PointEvent("event", "a", day: 15)],
            versions:
            [
                new(
                    "version",
                    "a",
                    From: FieldValue.From(Instant(day: 1)),
                    To: FieldValue.Null)
            ]);

        var result = Execute(plan, evidence);

        AssertStatus(result, RelationQueryExecutionStatus.Incomplete);
        AssertRows(result, RelationQueryExecutionOutputState.Incomplete);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.RequirementGapRequiredValueNull);
    }

    [Fact]
    public void Execute_NullUnboundedEndpointMatchesWithoutWeakeningEvidence()
    {
        var plan = Compile(
            CreateExecutionDocument(
                CreatePointMatch(upperNullBehavior: TemporalNullBoundBehavior.Unbounded),
                JoinKind.Full),
            nullableValidTo: true);
        var evidence = CreateEvidence(
            plan,
            events: [PointEvent("event", "a", day: 15)],
            versions:
            [
                new(
                    "version",
                    "a",
                    From: FieldValue.From(Instant(day: 1)),
                    To: FieldValue.Null)
            ]);

        var result = Execute(plan, evidence);

        AssertStatus(result, RelationQueryExecutionStatus.Succeeded);
        Assert.Empty(result.Diagnostics);
        AssertRows(
            result,
            RelationQueryExecutionOutputState.Complete,
            "event/event|version/version");
    }

    [Fact]
    public void Execute_ReversedIntervalIsIndeterminateButEmptyIntervalIsConclusive()
    {
        var plan = Compile(CreateExecutionDocument(CreatePointMatch(), JoinKind.Full));
        var reversedEvidence = CreateEvidence(
            plan,
            events: [PointEvent("event", "a", day: 15)],
            versions: [Version("version", "a", dayFrom: 20, dayTo: 10)]);
        var emptyEvidence = CreateEvidence(
            plan,
            events: [PointEvent("event", "a", day: 15)],
            versions: [Version("version", "a", dayFrom: 10, dayTo: 10)]);

        var reversed = Execute(plan, reversedEvidence);
        var empty = Execute(plan, emptyEvidence);

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, reversed.Status);
        AssertRows(reversed, RelationQueryExecutionOutputState.Incomplete);
        Assert.Contains(
            reversed.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionTemporalIntervalInvalid);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, empty.Status);
        Assert.Empty(empty.Diagnostics);
        AssertRows(
            empty,
            RelationQueryExecutionOutputState.Complete,
            "event/event",
            "version/version");
    }

    [Fact]
    public void Execute_FalseCorrelationWithInconclusiveTemporalEvidence_RemainsIncomplete()
    {
        var plan = Compile(CreateExecutionDocument(CreatePointMatch(), JoinKind.Full));
        var malformed = FieldValue.Inconclusive;
        var evidence = CreateEvidence(
            plan,
            events: [PointEvent("event", "a", day: 15)],
            versions: [new("version", "b", malformed, malformed)]);

        var result = Execute(plan, evidence);

        AssertStatus(result, RelationQueryExecutionStatus.Incomplete);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                RelationRuntimeDiagnosticCodes.ExecutionTemporalOperandInvalid
                or RelationRuntimeDiagnosticCodes.ExecutionTemporalIntervalInvalid);
        AssertRows(
            result,
            RelationQueryExecutionOutputState.Complete,
            "event/event",
            "version/version");
    }

    [Fact]
    public void Execute_PartialEvidenceSuppressesUnprovenOuterRowsButKeepsProvenMatch()
    {
        var plan = Compile(CreateExecutionDocument(CreatePointMatch(), JoinKind.Full));
        var evidence = CreateEvidence(
            plan,
            events:
            [
                PointEvent("match", "a", day: 10),
                PointEvent("left", "b", day: 10)
            ],
            versions:
            [
                Version("match", "a", dayFrom: 1, dayTo: 20),
                Version("right", "c", dayFrom: 1, dayTo: 20)
            ],
            completeness: RelationQueryEvidenceCompleteness.Partial);

        var result = Execute(plan, evidence);

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
        AssertRows(
            result,
            RelationQueryExecutionOutputState.Incomplete,
            "event/match|version/match");
    }

    [Fact]
    public void Execute_IndeterminateCandidatesSuppressFalseOuterRowsWithoutDiscardingValidMatch()
    {
        var plan = Compile(CreateExecutionDocument(CreatePointMatch(), JoinKind.Full));
        var malformed = FieldValue.Inconclusive;
        var evidence = CreateEvidence(
            plan,
            events:
            [
                PointEvent("matched", "a", day: 10),
                PointEvent("indeterminate", "b", day: 10)
            ],
            versions:
            [
                Version("valid", "a", dayFrom: 1, dayTo: 20),
                new("invalid-matched", "a", malformed, malformed),
                new("invalid-unmatched", "b", malformed, malformed)
            ]);

        var result = Execute(plan, evidence);

        AssertStatus(result, RelationQueryExecutionStatus.Incomplete);
        AssertRows(
            result,
            RelationQueryExecutionOutputState.Incomplete,
            "event/matched|version/valid");
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.RequirementGapInputAcquisitionInconclusive);
    }

    [Fact]
    public void Execute_IndeterminateOnePerRootRelationRemainsIncompleteWithoutCardinalityFailure()
    {
        var plan = Compile(CreateExecutionRelationDocument(CreatePointMatch(), JoinKind.Left));
        var malformed = FieldValue.Inconclusive;
        var evidence = CreateEvidence(
            plan,
            events:
            [
                PointEvent("indeterminate", "a", day: 10),
                PointEvent("unmatched", "b", day: 10)
            ],
            versions: [new("invalid", "a", malformed, malformed)]);

        var result = Execute(plan, evidence);

        AssertStatus(result, RelationQueryExecutionStatus.Incomplete);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, relation.State);
        var row = Assert.Single(relation.Rows);
        Assert.Equal(new RelationQueryOccurrenceId("event/unmatched"), row.Root?.Id);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputCardinalityViolation);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.RequirementGapInputAcquisitionInconclusive
                && diagnostic.Occurrence == new RelationQueryOccurrenceId("version/invalid"));
    }

    [Fact]
    public void Execute_PartialTraversalSuppressesOnlyItsIncompleteRootPartition()
    {
        var relationship = new RelationshipDefinition(
            new("Event.Versions"),
            EventShape,
            CorrelationKey,
            VersionShape,
            ObservationIdentityRelationshipTargetKey.Instance);
        var catalog = Cohesive.Relations.Serialization.RelationshipCatalogDocument.FromCatalog(
            new RelationshipCatalog([relationship]));
        var plan = Compile(
            CreateTraversalBackedTemporalRelationDocument(relationship.Id),
            relationshipCatalogDocument: catalog);
        var baseEvidence = CreateEvidence(
            plan,
            events:
            [
                PointEvent("partial", "a", day: 10),
                PointEvent("complete", "b", day: 10)
            ],
            versions: []);
        var traversal = Assert.Single(plan.RequirementGraph.Inputs.OfType<RelationQueryRelationshipInput>());
        var evidence = new RelationQueryRuntimeEvidence(
            baseEvidence.Evaluation,
            plan,
            baseEvidence.Completeness,
            baseEvidence.Sources,
            baseEvidence.Fields,
            traversals:
            [
                new(
                    traversal.Id,
                    new("event/partial"),
                    RelationQueryTraversalEvidenceState.Completed,
                    results: [],
                    completeness: RelationQueryEvidenceCompleteness.Partial),
                new(
                    traversal.Id,
                    new("event/complete"),
                    RelationQueryTraversalEvidenceState.Completed,
                    results: [],
                    completeness: RelationQueryEvidenceCompleteness.Complete)
            ],
            parameters: baseEvidence.Parameters,
            capabilities: baseEvidence.Capabilities);

        var result = Execute(plan, evidence);

        AssertStatus(result, RelationQueryExecutionStatus.Incomplete);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, relation.State);
        var row = Assert.Single(relation.Rows);
        Assert.Equal(new RelationQueryOccurrenceId("event/complete"), row.Root?.Id);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputCardinalityViolation);
    }

    static RelationQueryDocument CreateExecutionDocument(TemporalJoinMatch match, JoinKind joinKind)
    {
        var definition = CreateQuery(match, joinKind);
        ImmutableArray<LogicalQueryNode> nodes =
        [
            .. definition.Body.Nodes.Select(static node => node is ProjectQueryNode
                ? new ProjectQueryNode(
                    Project,
                    TemporalJoin,
                    TemporalRelationQueryFixture.Result,
                    ResultShape,
                    [new(new QueryAssignmentId("id"), Id, Expr.Const("row"))])
                : node)
        ];
        return RelationQueryDocument.FromDefinition(new QueryDefinition(
            definition.Id,
            definition.Name,
            new LogicalQueryDefinition(nodes, definition.Body.Parameters),
            definition.Results));
    }

    static RelationQueryDocument CreateExecutionRelationDocument(
        TemporalJoinMatch match,
        JoinKind joinKind)
    {
        var query = Assert.IsType<QueryDefinition>(CreateExecutionDocument(match, joinKind).Definition);
        return RelationQueryDocument.FromDefinition(new Cohesive.Relations.IR.RelationDefinition(
            new("temporal-relation"),
            new("TemporalRelation"),
            query.Body,
            Event,
            new(
                Project,
                ResultShape,
                RelationOutputMode.OnePerRoot)));
    }

    static RelationQueryDocument CreateTraversalBackedTemporalRelationDocument(
        RelationshipId relationship)
    {
        var traversal = new QueryNodeId("versions-for-event");
        var leftProjection = new QueryNodeId("event-temporal-side");
        var rightProjection = new QueryNodeId("version-temporal-side");
        var left = new ValueBindingId("event-temporal");
        var right = new ValueBindingId("version-temporal");
        var match = new TemporalPointInIntervalMatch(
            Expr.Field(left, OccurredAt),
            TemporalInterval.HalfOpen(
                Expr.Field(right, ValidFrom),
                Expr.Field(right, ValidTo)));
        return RelationQueryDocument.FromDefinition(new Cohesive.Relations.IR.RelationDefinition(
            new("traversal-backed-temporal-relation"),
            new("TraversalBackedTemporalRelation"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(EventSource, Event, EventShape),
                new TraverseRelationshipQueryNode(
                    traversal,
                    EventSource,
                    Event,
                    relationship,
                    RelationshipTraversalDirection.Forward,
                    VersionBinding,
                    JoinKind.Inner,
                    QueryInputRequirement.Required),
                new ProjectQueryNode(
                    leftProjection,
                    EventSource,
                    left,
                    EventShape,
                    [
                        new(new("left-id"), Id, Expr.Field(Event, Id)),
                        new(new("left-key"), CorrelationKey, Expr.Field(Event, CorrelationKey)),
                        new(new("left-point"), OccurredAt, Expr.Field(Event, OccurredAt))
                    ]),
                new ProjectQueryNode(
                    rightProjection,
                    traversal,
                    right,
                    VersionShape,
                    [
                        new(new("right-key"), CorrelationKey, Expr.Field(VersionBinding, CorrelationKey)),
                        new(new("right-from"), ValidFrom, Expr.Field(VersionBinding, ValidFrom)),
                        new(new("right-to"), ValidTo, Expr.Field(VersionBinding, ValidTo))
                    ]),
                new TemporalJoinQueryNode(
                    TemporalJoin,
                    leftProjection,
                    rightProjection,
                    JoinKind.Left,
                    Expr.Eq(
                        Expr.Field(left, CorrelationKey),
                        Expr.Field(right, CorrelationKey)),
                    match),
                new ProjectQueryNode(
                    Project,
                    TemporalJoin,
                    TemporalRelationQueryFixture.Result,
                    ResultShape,
                    [new(new("result-id"), Id, Expr.Const("row"))])
            ]),
            Event,
            new(Project, ResultShape, RelationOutputMode.OnePerRoot)));
    }

    static CompiledRelationQueryPlan Compile(
        RelationQueryDocument document,
        bool nullableValidTo = false,
        Cohesive.Relations.Serialization.RelationshipCatalogDocument? relationshipCatalogDocument = null)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            [nullableValidTo ? CreateNullableValidToShapeDocument() : CreateShapeGraphDocument()],
            relationshipCatalogDocument));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static ShapeGraphDocument CreateNullableValidToShapeDocument()
    {
        var graph = CreateShapeGraph();
        ImmutableArray<Shape> shapes =
        [
            .. graph.Shapes.Select(shape => shape.Id != VersionShape.ShapeId
                ? shape
                : new Shape(
                    shape.Id,
                    [
                        .. shape.Fields.Select(field =>
                            FieldPath.FromField(field.Name.Value) == ValidTo
                                ? field with { Nullability = FieldNullability.Nullable }
                                : field)
                    ],
                    shape.Constraints,
                    shape.Annotations))
        ];
        return ShapeGraphDocument.FromGraph(new ShapeGraph(
            graph.Id,
            shapes,
            graph.NamedTypes,
            annotations: graph.Annotations));
    }

    static RelationQueryRuntimeEvidence CreateEvidence(
        CompiledRelationQueryPlan plan,
        ImmutableArray<EventSpec> events,
        ImmutableArray<VersionSpec> versions,
        RelationQueryEvidenceCompleteness completeness = RelationQueryEvidenceCompleteness.Complete)
    {
        var eventOccurrences = events.ToDictionary(
            static spec => spec.Id,
            static spec => new RelationQueryObservationOccurrence(
                new($"event/{spec.Id}"),
                Event,
                EventShape,
                spec.Id),
            StringComparer.Ordinal);
        var versionOccurrences = versions.ToDictionary(
            static spec => spec.Id,
            static spec => new RelationQueryObservationOccurrence(
                new($"version/{spec.Id}"),
                VersionBinding,
                VersionShape,
                spec.Id),
            StringComparer.Ordinal);

        ImmutableArray<RelationQuerySourceEvidence>.Builder sources =
            ImmutableArray.CreateBuilder<RelationQuerySourceEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>())
        {
            sources.Add(input.Binding == Event
                ? new(
                    input.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [.. eventOccurrences.Values])
                : new(
                    input.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [.. versionOccurrences.Values]));
        }

        ImmutableArray<RelationQueryFieldEvidence>.Builder fields =
            ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>())
        {
            if (input.Binding == Event)
            {
                foreach (var spec in events)
                {
                    fields.Add(CreateFieldEvidence(
                        input,
                        eventOccurrences[spec.Id],
                        EventField(spec, input.Field.Path)));
                }
                continue;
            }

            if (input.Binding != VersionBinding)
            {
                throw new InvalidOperationException(
                    $"Unsupported temporal fixture binding '{input.Binding.Value}'.");
            }
            foreach (var spec in versions)
            {
                fields.Add(CreateFieldEvidence(
                    input,
                    versionOccurrences[spec.Id],
                    VersionField(spec, input.Field.Path)));
            }
        }

        return new(
            new("tests/temporal-interpreter"),
            plan,
            completeness,
            sources.ToImmutable(),
            fields.ToImmutable(),
            capabilities:
            [
                .. plan.RequirementGraph.Inputs
                    .OfType<RelationQueryCapabilityInput>()
                    .Select(static input => new RelationQueryCapabilityEvidence(
                        input.Id,
                        RelationQueryCapabilityEvidenceState.Available))
            ]);
    }

    static RelationQueryFieldEvidence CreateFieldEvidence(
        RelationQueryFieldInput input,
        RelationQueryObservationOccurrence owner,
        FieldValue value) =>
        value.State == RelationQueryFieldEvidenceState.Value
            ? new(input.Id, owner.Id, value.State, value.Observation)
            : new(input.Id, owner.Id, value.State);

    static FieldValue EventField(EventSpec spec, FieldPath path)
    {
        if (path == Id)
            return FieldValue.From(ObservationValue.FromString(spec.Id));
        if (path == CorrelationKey)
            return FieldValue.From(ObservationValue.FromString(spec.Key));
        if (path == OccurredAt && spec.Point is not null)
            return spec.Point;
        if (path == EventStart && spec.Start is not null)
            return spec.Start;
        if (path == EventEnd && spec.End is not null)
            return spec.End;
        throw new InvalidOperationException(
            $"Event '{spec.Id}' has no evidence for required field '{path}'.");
    }

    static FieldValue VersionField(VersionSpec spec, FieldPath path)
    {
        if (path == Id)
            return FieldValue.From(ObservationValue.FromString(spec.Id));
        if (path == CorrelationKey)
            return FieldValue.From(ObservationValue.FromString(spec.Key));
        if (path == ValidFrom)
            return spec.From;
        if (path == ValidTo)
            return spec.To;
        throw new InvalidOperationException(
            $"Version '{spec.Id}' has no evidence for required field '{path}'.");
    }

    static RelationQueryExecutionResult Execute(
        CompiledRelationQueryPlan plan,
        RelationQueryRuntimeEvidence evidence) =>
        RelationQueryInMemoryInterpreter.Default.Execute(new(plan, evidence));

    static void AssertRows(
        RelationQueryExecutionResult result,
        RelationQueryExecutionOutputState expectedState,
        params string[] expectedProvenance)
    {
        var rows = Assert.Single(result.QueryResults);
        Assert.Equal(expectedState, rows.State);
        Assert.Equal(
            expectedProvenance.Order(StringComparer.Ordinal),
            rows.Rows
                .Select(static row => string.Join(
                    '|',
                    row.InputOccurrences.Select(static occurrence => occurrence.Id.Value)))
                .Order(StringComparer.Ordinal));
    }

    static string ProvenanceKey(RelationQueryOutputRow row) =>
        string.Join('|', row.InputOccurrences.Select(static occurrence => occurrence.Id.Value));

    static void AssertStatus(
        RelationQueryExecutionResult result,
        RelationQueryExecutionStatus expected) =>
        Assert.True(
            result.Status == expected,
            $"Expected status '{expected}', but received '{result.Status}'.{Environment.NewLine}"
            + string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Severity}: {diagnostic.Message}")));

    static EventSpec PointEvent(string id, string key, int day) =>
        new(id, key, Point: FieldValue.From(Instant(day)));

    static EventSpec IntervalEvent(string id, string key, int dayFrom, int dayTo) =>
        new(
            id,
            key,
            Start: FieldValue.From(Instant(dayFrom)),
            End: FieldValue.From(Instant(dayTo)));

    static VersionSpec Version(string id, string key, int dayFrom, int dayTo) =>
        new(
            id,
            key,
            FieldValue.From(Instant(dayFrom)),
            FieldValue.From(Instant(dayTo)));

    static ObservationValue Instant(int day) =>
        ObservationValue.FromDateTimeOffset(new(2026, 1, day, 0, 0, 0, TimeSpan.Zero));

    sealed record FieldValue(
        RelationQueryFieldEvidenceState State,
        ObservationValue? Observation = null)
    {
        public static FieldValue Null { get; } = new(RelationQueryFieldEvidenceState.Null);

        public static FieldValue Inconclusive { get; } = new(RelationQueryFieldEvidenceState.Inconclusive);

        public static FieldValue From(ObservationValue value) =>
            new(RelationQueryFieldEvidenceState.Value, value);
    }

    sealed record EventSpec(
        string Id,
        string Key,
        FieldValue? Point = null,
        FieldValue? Start = null,
        FieldValue? End = null);

    sealed record VersionSpec(
        string Id,
        string Key,
        FieldValue From,
        FieldValue To);
}
