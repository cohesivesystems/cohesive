using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable identity of one logical interaction emission across retries and replay.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct EmissionId
{
    /// <summary>Creates an emission identity.</summary>
    /// <param name="value">Stable logical emission identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public EmissionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw logical emission identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw logical emission identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity grouping causally related interactions.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InteractionCorrelationId
{
    /// <summary>Creates an interaction-correlation identity.</summary>
    /// <param name="value">Stable correlation identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InteractionCorrelationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw correlation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw correlation identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable logical deduplication basis for one interaction.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InteractionIdempotencyKey
{
    /// <summary>Creates an interaction-idempotency key.</summary>
    /// <param name="value">Stable logical deduplication key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InteractionIdempotencyKey(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw logical deduplication key.</summary>
    public string Value { get; }

    /// <summary>Returns the raw logical deduplication key.</summary>
    /// <returns>The value supplied when this key was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>Exact semantic revision of an interaction payload or result schema.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InteractionValueSchemaRevision
{
    /// <summary>Creates an interaction-value schema revision.</summary>
    /// <param name="value">Stable schema-revision identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InteractionValueSchemaRevision(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw schema-revision identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw schema-revision identity.</summary>
    /// <returns>The value supplied when this revision was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one terminal result variant declared by a Request.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RequestTerminalOutcomeId
{
    /// <summary>Creates a request terminal-outcome identity.</summary>
    /// <param name="value">Stable outcome-variant identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public RequestTerminalOutcomeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw terminal-outcome identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw terminal-outcome identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>Portable identity of one authoritative entity subject.</summary>
public sealed record InteractionEntityReference
{
    /// <summary>Creates an interaction entity reference.</summary>
    /// <param name="entityType">Semantic entity type.</param>
    /// <param name="entityId">Stable entity identity.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="entityType"/> or <paramref name="entityId"/> is a default value.
    /// </exception>
    [JsonConstructor]
    public InteractionEntityReference(EntityTypeName entityType, EntityId entityId)
    {
        if (string.IsNullOrWhiteSpace(entityType.Value))
            throw new ArgumentException("An interaction entity requires a non-default entity type.", nameof(entityType));
        if (string.IsNullOrWhiteSpace(entityId.Value))
            throw new ArgumentException("An interaction entity requires a non-default entity identity.", nameof(entityId));

        EntityType = entityType;
        EntityId = entityId;
    }

    /// <summary>Semantic entity type.</summary>
    public EntityTypeName EntityType { get; }

    /// <summary>Stable entity identity.</summary>
    public EntityId EntityId { get; }
}
