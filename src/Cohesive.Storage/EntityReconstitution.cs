using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

/// <summary>Which durable representation owns entity state.</summary>
public enum EntityStateAuthority
{
    /// <summary>The current entity row is authoritative; retained actions are audit evidence.</summary>
    State,
    /// <summary>The ordered state-action stream is authoritative; current rows are derived query projections.</summary>
    Events
}

/// <summary>How an event-authoritative repository acquires current state.</summary>
public enum EntityReconstitutionMode
{
    /// <summary>Validate and apply the complete bounded stream.</summary>
    FullReplay,
    /// <summary>Start at an exact retained stream checkpoint and apply its ordered tail.</summary>
    CheckpointAndTail
}

/// <summary>A committed replacement action, independent of interaction publication.</summary>
/// <param name="Position">One-based stream position, distinct from semantic observation version.</param>
/// <param name="State">Complete canonical replacement state; never a command to reevaluate.</param>
/// <remarks>The initial contract uses replacement actions, not arbitrary domain-event reducers. Reconstitution does
/// not reevaluate a transition, publish an envelope, or repeat an external effect. An unchanged decision may
/// retain a new action without advancing the semantic version.</remarks>
public sealed record EntityStateAction(long Position, EntityObservationSnapshot State);

/// <summary>Pure reconstitution of canonical replacement actions; provider adapters own durable ordering and integrity.</summary>
public static class EntityReconstitution
{
    /// <summary>Applies one consecutive replacement action to a checkpoint or preceding action.</summary>
    /// <param name="definition">Canonical state authority used for shape and entity validation.</param>
    /// <param name="previous">Previous action, or null before the first action.</param>
    /// <param name="next">Retained action to validate and apply.</param>
    /// <returns>The validated next state and stream position.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">Positions, subject, shape, or semantic versions contradict the stream.</exception>
    /// <remarks>The first action must have position one. A checkpoint is separately validated by its provider
    /// against the exact retained action before it can be supplied as previous.</remarks>
    public static EntityStateAction Apply(EntityDefinition definition, EntityStateAction? previous, EntityStateAction next)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(next.State);
        if (next.Position != checked((previous?.Position ?? 0) + 1))
            throw new InvalidOperationException("Entity history has a gap or reordered action.");
        if (next.State.Observation.ShapeId != definition.StateShape.QualifiedId
            || previous is not null && (previous.State.EntityId != next.State.EntityId || next.State.Version < previous.State.Version))
            throw new InvalidOperationException("Entity history changed subject, shape, or moved semantic version backwards.");
        _ = definition.CreateState(next.State);
        return next;
    }
}
