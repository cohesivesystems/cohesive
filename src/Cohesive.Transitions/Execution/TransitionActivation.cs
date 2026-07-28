using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Transitions.Execution;

/// <summary>One explicitly observed aggregate access supplied to a Transition activation.</summary>
public sealed record TransitionObservationEntry
{
    /// <summary>Creates one exact observation entry.</summary>
    /// <param name="access">Complete or aggregate-relative access represented by this entry.</param>
    /// <param name="value">Observed value state and payload.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="access"/> or <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> uses <see cref="PortableValueState.Missing"/>. Key absence is the sole
    /// representation of an unobserved access in a frame.
    /// </exception>
    public TransitionObservationEntry(
        TransitionObservationAccess access,
        PortableValue value)
    {
        Access = Guard.RequireNotNull(access);
        Value = Guard.RequireNotNull(value);
        if (value.State == PortableValueState.Missing)
        {
            throw new ArgumentException(
                "A supplied observation entry cannot be Missing; omit the access to represent unobserved state.",
                nameof(value));
        }
    }

    /// <summary>Complete or aggregate-relative access represented by this entry.</summary>
    public TransitionObservationAccess Access { get; }

    /// <summary>Observed value state and payload.</summary>
    public PortableValue Value { get; }
}

/// <summary>
/// Immutable finite aggregate observation supplied to a Transition reference interpreter.
/// </summary>
/// <remarks>
/// A full-state frame contains <see cref="TransitionObservationAccess.Whole"/>. A sparse frame contains only
/// explicitly acquired accesses. Key absence means unobserved and is distinct from an observed
/// <see cref="PortableValueState.Absent"/>, <see cref="PortableValueState.Null"/>,
/// <see cref="PortableValueState.Unknown"/>, or <see cref="PortableValueState.Failed"/> value.
/// </remarks>
public sealed class TransitionObservationFrame
{
    readonly Dictionary<TransitionObservationAccess, PortableValue> values;

    TransitionObservationFrame(ImmutableArray<TransitionObservationEntry> entries)
    {
        var normalized = entries
            .OrderBy(static entry => entry.Access, TransitionStructuralOrdering.ObservationAccesses)
            .ToImmutableArray();
        values = new(normalized.Length);
        foreach (var entry in normalized)
        {
            if (!values.TryAdd(entry.Access, entry.Value))
            {
                throw new ArgumentException(
                    $"Observation access '{entry.Access}' is supplied more than once.",
                    nameof(entries));
            }
        }

        Entries = normalized;
    }

    /// <summary>Observed accesses in deterministic whole-before-path order.</summary>
    public ImmutableArray<TransitionObservationEntry> Entries { get; }

    /// <summary>Creates a complete coherent aggregate-state frame.</summary>
    /// <param name="state">Concrete complete aggregate state.</param>
    /// <returns>A frame containing exactly the complete observation access.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not concrete.</exception>
    public static TransitionObservationFrame Full(PortableValue state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.State != PortableValueState.Concrete)
            throw new ArgumentException("A full aggregate state must be concrete.", nameof(state));
        return new([new(TransitionObservationAccess.Whole, state)]);
    }

    /// <summary>Creates an exact sparse observation frame.</summary>
    /// <param name="entries">Explicit finite observation accesses.</param>
    /// <returns>An immutable frame normalized independently of producer order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is null, an access is duplicated or overlapping, an entry selects the complete state, or an entry
    /// uses the Missing state.
    /// </exception>
    public static TransitionObservationFrame Sparse(
        IEnumerable<TransitionObservationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var values = entries.ToImmutableArray();
        if (values.Any(static entry => entry is null))
            throw new ArgumentException("A sparse observation frame cannot contain null entries.", nameof(entries));
        if (values.Any(static entry => entry.Access.IsWhole))
        {
            throw new ArgumentException(
                "A sparse observation frame cannot contain the complete-state access; use Full instead.",
                nameof(entries));
        }
        for (var rightIndex = 0; rightIndex < values.Length; rightIndex++)
        {
            for (var leftIndex = 0; leftIndex < rightIndex; leftIndex++)
            {
                if (values[leftIndex].Access.Path!.Value.Overlaps(values[rightIndex].Access.Path!.Value))
                {
                    throw new ArgumentException(
                        $"Sparse observation accesses '{values[leftIndex].Access}' and "
                        + $"'{values[rightIndex].Access}' overlap and could carry contradictory evidence.",
                        nameof(entries));
                }
            }
        }
        return new(values);
    }

    internal bool TryGetExact(
        TransitionObservationAccess access,
        out PortableValue value) => values.TryGetValue(access, out value!);
}

/// <summary>
/// Complete explicit input to one finite, deterministic direct Transition activation.
/// </summary>
public sealed record TransitionActivation
{
    /// <summary>Creates a direct Transition activation.</summary>
    /// <param name="id">Caller-supplied stable activation identity.</param>
    /// <param name="input">Typed invocation input.</param>
    /// <param name="observation">Finite evaluation observation.</param>
    /// <param name="commitObservation">
    /// Optional fresh finite observation used to validate every actual read that must remain coherent through
    /// commit. Supplying it performs no I/O; it is caller-acquired evidence.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    public TransitionActivation(
        ActivationId id,
        PortableValue input,
        TransitionObservationFrame observation,
        TransitionObservationFrame? commitObservation = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A Transition activation requires a stable identity.", nameof(id));
        Id = id;
        Input = Guard.RequireNotNull(input);
        Observation = Guard.RequireNotNull(observation);
        CommitObservation = commitObservation;
    }

    /// <summary>Caller-supplied stable activation identity.</summary>
    public ActivationId Id { get; }

    /// <summary>Typed invocation input.</summary>
    public PortableValue Input { get; }

    /// <summary>Finite evaluation observation.</summary>
    public TransitionObservationFrame Observation { get; }

    /// <summary>Optional fresh finite observation used for commit-time coherence validation.</summary>
    public TransitionObservationFrame? CommitObservation { get; }
}
