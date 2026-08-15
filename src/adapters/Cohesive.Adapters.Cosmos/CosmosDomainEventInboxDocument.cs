using System.Text.Json.Serialization;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Adapter-local wire document for one canonical domain-event inbox entry.</summary>
[JsonSerializable(typeof(CosmosDomainEventInboxDocument))]
sealed record CosmosDomainEventInboxDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("partitionKey")] string PartitionKey,
    [property: JsonPropertyName("documentKind")] string DocumentKind,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("authority")] string Authority,
    [property: JsonPropertyName("tenant")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Tenant,
    [property: JsonPropertyName("contractDefinitionId")] string ContractDefinitionId,
    [property: JsonPropertyName("contractRevisionId")] string ContractRevisionId,
    [property: JsonPropertyName("contractFingerprintAlgorithm")] string ContractFingerprintAlgorithm,
    [property: JsonPropertyName("contractFingerprintCanonicalization")] string ContractFingerprintCanonicalization,
    [property: JsonPropertyName("contractFingerprintValue")] string ContractFingerprintValue,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("envelope")] string Envelope,
    [property: JsonPropertyName("envelopeFingerprint")] string EnvelopeFingerprint,
    [property: JsonPropertyName("acceptedAtUtc")] DateTimeOffset AcceptedAtUtc,
    [property: JsonPropertyName("receipt")] string Receipt);
