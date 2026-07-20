using Cohesive.Relations.Observability;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Stable tracing and metric registration names emitted by the Cosmos relation/query adapter.</summary>
public static class CosmosRelationQueryTelemetry
{
    /// <summary>Package-owned activity-source and meter name.</summary>
    public const string InstrumentationName = "Cohesive.Adapters.Cosmos.Relations";

    /// <summary>Adapter source-acquisition activity name and metric operation value.</summary>
    public const string SourceAcquisitionActivityName = "cohesive.relations.cosmos.source.acquire";

    internal static readonly RelationQueryTelemetryEmitter Emitter = new(
        InstrumentationName,
        typeof(CosmosRelationQueryTelemetry).Assembly.GetName().Version?.ToString());
}
