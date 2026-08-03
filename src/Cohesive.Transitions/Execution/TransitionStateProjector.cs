using Cohesive.Execution;

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
