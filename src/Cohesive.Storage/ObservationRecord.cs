using Cohesive.Relations.Model;

namespace Cohesive.Storage;

/// <summary>
/// One streamed observation record.
/// </summary>
public sealed record ObservationRecord(
    ObservationStreamRecordKind Kind,
    Observation Observation,
    string PartitionKey,
    string DocumentId,
    string? StreamName = null,
    string? SubjectType = null,
    string? SubjectId = null,
    long? SubjectVersion = null,
    string? CorrelationId = null,
    DateTimeOffset? OccurredAtUtc = null,
    EntityConcurrencyToken? ConcurrencyToken = null
    );

/// <summary>
/// Observation stream record kind.
/// </summary>
public enum ObservationStreamRecordKind
{
    EntityChange = 0,
    OutboxEvent = 1
}