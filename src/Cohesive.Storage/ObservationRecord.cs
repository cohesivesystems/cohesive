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
    /// <summary>Represents the entity change option.</summary>
    EntityChange = 0,
    
    /// <summary>Represents the outbox event option.</summary>
    OutboxEvent = 1
}
