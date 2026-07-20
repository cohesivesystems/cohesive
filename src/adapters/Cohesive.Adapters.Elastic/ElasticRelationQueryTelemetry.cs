using Cohesive.Relations.Observability;

namespace Cohesive.Adapters.Elastic;

/// <summary>Stable tracing and metric registration names emitted by the Elasticsearch relation/query adapter.</summary>
public static class ElasticRelationQueryTelemetry
{
    /// <summary>Package-owned activity-source and meter name.</summary>
    public const string InstrumentationName = "Cohesive.Adapters.Elastic.Relations";

    internal static readonly RelationQueryTelemetryEmitter Emitter = new(
        InstrumentationName,
        typeof(ElasticRelationQueryTelemetry).Assembly.GetName().Version?.ToString());
}
