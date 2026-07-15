using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Shared canonical temporal-query model used by static-compilation and interpreter tests.
/// </summary>
internal static class TemporalRelationQueryFixture
{
    public static readonly GraphId GraphId = new("temporal-contract/v1");
    public static readonly QualifiedShapeId EventShape = new(GraphId, new ShapeId("Event"));
    public static readonly QualifiedShapeId VersionShape = new(GraphId, new ShapeId("Version"));
    public static readonly QualifiedShapeId ResultShape = new(GraphId, new ShapeId("TemporalResult"));

    public static readonly ValueBindingId Event = new("event");
    public static readonly ValueBindingId VersionBinding = new("version");
    public static readonly ValueBindingId Result = new("result");

    public static readonly QueryNodeId EventSource = new("events");
    public static readonly QueryNodeId VersionSource = new("versions");
    public static readonly QueryNodeId TemporalJoin = new("temporal");
    public static readonly QueryNodeId Project = new("project");

    public static readonly FieldPath Id = FieldPath.FromField("Id");
    public static readonly FieldPath CorrelationKey = FieldPath.FromField("CorrelationKey");
    public static readonly FieldPath OccurredAt = FieldPath.FromField("OccurredAt");
    public static readonly FieldPath EventStart = FieldPath.FromField("EventStart");
    public static readonly FieldPath EventEnd = FieldPath.FromField("EventEnd");
    public static readonly FieldPath ValidFrom = FieldPath.FromField("ValidFrom");
    public static readonly FieldPath ValidTo = FieldPath.FromField("ValidTo");

    public static QueryDefinition CreateQuery(
        TemporalJoinMatch match,
        JoinKind joinKind = JoinKind.Left) =>
        new(
            new QueryId("temporal-query"),
            new QueryName("TemporalQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(EventSource, Event, EventShape),
                new SourceQueryNode(VersionSource, VersionBinding, VersionShape),
                new TemporalJoinQueryNode(
                    TemporalJoin,
                    EventSource,
                    VersionSource,
                    joinKind,
                    Expr.Eq(
                        Expr.Field(Event, CorrelationKey),
                        Expr.Field(VersionBinding, CorrelationKey)),
                    match),
                new ProjectQueryNode(
                    Project,
                    TemporalJoin,
                    Result,
                    ResultShape,
                    [new(new QueryAssignmentId("id"), Id, Expr.Field(Event, Id))])
            ]),
            [new RowsQueryResultDefinition(new QueryResultId("rows"), Project)]);

    public static RelationQueryDocument CreateQueryDocument(
        TemporalJoinMatch match,
        JoinKind joinKind = JoinKind.Left) =>
        RelationQueryDocument.FromDefinition(CreateQuery(match, joinKind));

    public static TemporalPointInIntervalMatch CreatePointMatch(
        TemporalBoundaryInclusion lowerInclusion = TemporalBoundaryInclusion.Inclusive,
        TemporalNullBoundBehavior upperNullBehavior = TemporalNullBoundBehavior.Invalid) =>
        new(
            Expr.Field(Event, OccurredAt),
            new TemporalInterval(
                new ExpressionTemporalIntervalBound(
                    Expr.Field(VersionBinding, ValidFrom),
                    lowerInclusion),
                new ExpressionTemporalIntervalBound(
                    Expr.Field(VersionBinding, ValidTo),
                    TemporalBoundaryInclusion.Exclusive,
                    upperNullBehavior)));

    public static TemporalIntervalOverlapMatch CreateOverlapMatch() =>
        new(
            TemporalInterval.HalfOpen(
                Expr.Field(Event, EventStart),
                Expr.Field(Event, EventEnd)),
            TemporalInterval.HalfOpen(
                Expr.Field(VersionBinding, ValidFrom),
                Expr.Field(VersionBinding, ValidTo)));

    public static ShapeGraph CreateShapeGraph(
        ScalarTypeKind rightTemporalDomain = ScalarTypeKind.Instant)
    {
        var text = new ScalarTypeRef(ScalarTypeKind.String);
        var instant = new ScalarTypeRef(ScalarTypeKind.Instant);
        var rightTemporal = new ScalarTypeRef(rightTemporalDomain);
        return new(
            GraphId,
            [
                new Shape(
                    EventShape.ShapeId,
                    [
                        new(new FieldName("Id"), text, role: FieldRole.Identity),
                        new(new FieldName("CorrelationKey"), text),
                        new(new FieldName("OccurredAt"), instant),
                        new(new FieldName("EventStart"), instant),
                        new(new FieldName("EventEnd"), instant)
                    ],
                    role: ShapeRoles.Entity),
                new Shape(
                    VersionShape.ShapeId,
                    [
                        new(new FieldName("Id"), text, role: FieldRole.Identity),
                        new(new FieldName("CorrelationKey"), text),
                        new(new FieldName("ValidFrom"), rightTemporal),
                        new(new FieldName("ValidTo"), rightTemporal)
                    ],
                    role: ShapeRoles.Entity),
                new Shape(
                    ResultShape.ShapeId,
                    [new(new FieldName("Id"), text, role: FieldRole.Identity)],
                    role: ShapeRoles.Dto)
            ]);
    }

    public static ShapeGraphDocument CreateShapeGraphDocument(
        ScalarTypeKind rightTemporalDomain = ScalarTypeKind.Instant) =>
        ShapeGraphDocument.FromGraph(CreateShapeGraph(rightTemporalDomain));
}
