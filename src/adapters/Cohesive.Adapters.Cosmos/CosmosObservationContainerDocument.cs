using System.Text.Json;
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
    [property: JsonPropertyName("envelope")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? Envelope = null,
    [property: JsonPropertyName("envelopeFingerprint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EnvelopeFingerprint = null,
    [property: JsonPropertyName("transitionRequestFingerprint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TransitionRequestFingerprint = null,
    [property: JsonPropertyName("transitionIntentFingerprint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TransitionIntentFingerprint = null,
    [property: JsonPropertyName("transitionCommitFingerprint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TransitionCommitFingerprint = null,
    [property: JsonPropertyName("transitionCommit")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? TransitionCommit = null,
    [property: JsonPropertyName("transitionCommitEncoding")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TransitionCommitEncoding = null,
    [property: JsonPropertyName("transitionCommitPayload")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TransitionCommitPayload = null,
    [property: JsonPropertyName("transitionOperationReceiptId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TransitionOperationReceiptId = null,
    [property: JsonPropertyName("_etag")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ETag = null,
    [property: JsonPropertyName("entityConcurrencyToken")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EntityConcurrencyToken = null);
