using Cohesive.Execution;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Execution;

/// <summary>Projects a canonical Transition decision's committable patch onto aggregate state.</summary>
public static class TransitionStateProjector
{
    /// <summary>Applies the decision patch in execution order after verifying its before-value evidence.</summary>
    /// <param name="state">Concrete complete aggregate state used to produce the decision.</param>
    /// <param name="decision">Canonical non-committing decision to project.</param>
    /// <returns>The candidate aggregate state after applying every retained patch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not an object value.</exception>
    /// <exception cref="InvalidOperationException">
    /// The supplied state does not match the decision's before-value evidence, or a retained patch contains a
    /// value state that cannot be committed.
    /// </exception>
    /// <exception cref="NotSupportedException">A retained patch contains collection-element path navigation.</exception>
    public static ObservationValue Apply(ObservationValue state, TransitionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (state.Kind != ObservationValueKind.Object)
        {
            throw new ArgumentException("Transition state projection requires a concrete object value.", nameof(state));
        }

        var candidate = state;
        foreach (var patch in decision.Patch)
        {
            var observed = Read(candidate, patch.Path, patch.Before.Contract);
            if (observed != patch.Before)
            {
                throw new InvalidOperationException(
                    $"Transition patch '{patch.Node.Value}' cannot be projected because state at "
                    + $"'{patch.Path}' does not match its before-value evidence.");
            }

            candidate = patch.After.State switch
            {
                PortableValueState.Concrete => candidate.WithField(patch.Path, patch.After.Value!.Value),
                PortableValueState.Null => candidate.WithField(patch.Path, ObservationValue.Null),
                PortableValueState.Absent => candidate.WithoutField(patch.Path),
                _ => throw new InvalidOperationException(
                    $"Transition patch '{patch.Node.Value}' produced non-committable value state "
                    + $"'{patch.After.State}'.")
            };
        }

        return candidate;
    }

    /// <summary>Prepares one entity candidate from a canonical decision without committing or delivering effects.</summary>
    /// <param name="entity">Canonical entity authority used to validate the resulting observation.</param>
    /// <param name="entityId">Exact subject identity selected by the invoking boundary.</param>
    /// <param name="decision">Decision made from the supplied current observation, or a successful creation decision.</param>
    /// <param name="current">Existing subject; null requires retained creation evidence. Absence must still be enforced at commit.</param>
    /// <returns>The validated candidate. Creation starts at version zero; applied changes advance the current version, while no-change and rejected decisions preserve it.</returns>
    /// <exception cref="ArgumentNullException">Entity or decision is null.</exception>
    /// <exception cref="ArgumentException">Subject identity is empty or differs from the current subject.</exception>
    /// <exception cref="InvalidOperationException">Decision is not preparable, creation evidence is missing, or before-value evidence does not match.</exception>
    /// <exception cref="SemanticRuleViolationException">The observation violates the entity definition.</exception>
    /// <exception cref="OverflowException">The existing version cannot be incremented.</exception>
    /// <exception cref="NotSupportedException">A patch uses unsupported collection-element navigation.</exception>
    /// <remarks>This is state application, not history replay or commit evidence. Callers retain authorization,
    /// concurrency fences, emission ownership, and atomic persistence responsibilities.</remarks>
    public static EntityState ApplyToEntity(EntityDefinition entity, string entityId,
        TransitionDecision decision, EntityObservationSnapshot? current = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        if (decision.Kind is not (TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange
            or TransitionDecisionKind.AdmissionRejected or TransitionDecisionKind.DomainRejected))
            throw new InvalidOperationException("Only accepted or domain-rejected decisions can prepare entity state.");

        ObservationValue initial;
        long version;
        if (current is null)
        {
            if (decision.Kind != TransitionDecisionKind.Applied
                || decision.Evidence.InitialObservation is not { State: PortableValueState.Concrete, Value: { Fields: not null } value })
                throw new InvalidOperationException("An absent subject requires a successful creation decision with complete initial-observation evidence.");
            initial = value;
            version = 0;
        }
        else
        {
            if (current.EntityId.Value != entityId)
                throw new ArgumentException("The current snapshot belongs to a different subject.", nameof(current));
            if (decision.Evidence.InitialObservation is not null)
                throw new InvalidOperationException("A creation decision cannot be applied to an existing subject.");
            var state = entity.CreateState(current);
            initial = ObservationValue.FromObject(state.Fields);
            version = decision.Kind == TransitionDecisionKind.Applied ? checked(state.Version + 1) : state.Version;
        }
        var projected = Apply(initial, decision);
        var candidate = entity.CreateState(entityId, projected.Fields!, version);
        if (current is null)
            entity.ValidateState(candidate);
        return candidate;
    }

    static PortableValue Read(ObservationValue state, FieldPath path, ValueContract contract)
    {
        if (!state.TryGetField(path, out var value) || value.Kind == ObservationValueKind.Undefined)
        {
            return PortableValue.Absent(contract);
        }

        if (value.Kind == ObservationValueKind.Null)
        {
            return PortableValue.Null(contract);
        }

        return PortableValue.Concrete(contract, value);
    }
}
