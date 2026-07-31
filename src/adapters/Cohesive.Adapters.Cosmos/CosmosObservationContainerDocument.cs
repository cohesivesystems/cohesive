using System.Text.Json.Serialization;
using Cohesive.Model;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Adapter-local wire representation shared by Cosmos observation persistence and materialization change delivery.
/// </summary>
[JsonSerializable(typeof(CosmosObservationContainerDocument))]
sealed record CosmosObservationContainerDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("partitionKey")] string PartitionKey,
    [property: JsonPropertyName("documentKind")] string DocumentKind,
    [property: JsonPropertyName("observationType")] string ObservationType,
    [property: JsonPropertyName("observationId")] string ObservationId,
    [property: JsonPropertyName("observationVersion")] long ObservationVersion,
    [property: JsonPropertyName("observation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, ObservationValue>? Observation = null,
    [property: JsonPropertyName("streamName")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? StreamName = null,
    [property: JsonPropertyName("subjectType")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SubjectType = null,
    [property: JsonPropertyName("subjectId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SubjectId = null,
    [property: JsonPropertyName("subjectVersion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? SubjectVersion = null,
    [property: JsonPropertyName("correlationId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CorrelationId = null,
    [property: JsonPropertyName("occurredAtUtc")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? OccurredAtUtc = null,
    [property: JsonPropertyName("traceId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TraceId = null,
    [property: JsonPropertyName("spanId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SpanId = null,
    [property: JsonPropertyName("_etag")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ETag = null);
