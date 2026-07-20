using Cohesive.Relations.Observability;

namespace Cohesive.Adapters.Postgres;

/// <summary>Stable tracing and metric registration names emitted by the PostgreSQL relation/query adapter.</summary>
public static class PostgresRelationQueryTelemetry
{
    /// <summary>Package-owned activity-source and meter name.</summary>
    public const string InstrumentationName = "Cohesive.Adapters.Postgres.Relations";

    internal static readonly RelationQueryTelemetryEmitter Emitter = new(
        InstrumentationName,
        typeof(PostgresRelationQueryTelemetry).Assembly.GetName().Version?.ToString());
}
