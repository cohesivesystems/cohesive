namespace Cohesive.Storage;

/// <summary>
/// Options for adapting observation repositories to process entity storage.
/// </summary>
public sealed record ObservationProcessEntityRepositoryOptions
{
    /// <summary>
    /// Persists transition-emitted effects into the observation outbox when supported.
    /// </summary>
    public bool PersistEffectsInOutbox { get; init; }

    /// <summary>
    /// Logical outbox stream used for persisted process effects.
    /// </summary>
    public string EffectOutboxStreamName { get; init; } = "process-effects";

    /// <summary>
    /// Observation shape written for persisted process effects.
    /// </summary>
    public string EffectObservationType { get; init; } = "PersistedProcessEffect";
}
