using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Transitions.IR;

/// <summary>Canonical semantics for initializing an aggregate that is authoritatively absent.</summary>
public sealed record TransitionSubjectCreation
{
    /// <summary>Creates explicit absent-subject initialization semantics.</summary>
    /// <param name="id">Stable identity for initialization diagnostics, source maps, and traces.</param>
    /// <param name="initialObservation">
    /// Pure input-derived expression producing the complete initial aggregate observation.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="initialObservation"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public TransitionSubjectCreation(
        ExecutionNodeId id,
        Expr initialObservation)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("Transition subject creation requires a stable node identity.", nameof(id));

        Id = id;
        InitialObservation = initialObservation ?? throw new ArgumentNullException(nameof(initialObservation));
    }

    /// <summary>Stable identity for initialization diagnostics, source maps, and traces.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Pure input-derived expression producing the complete initial aggregate observation.</summary>
    public Expr InitialObservation { get; }
}

/// <summary>
/// Canonical, portable semantic definition of one deterministic aggregate transition.
/// </summary>
/// <remarks>
/// Definition identity, revision, fingerprint, provenance, and descriptive metadata belong to the surrounding
/// <see cref="ExecutionDefinitionDocument"/>. This payload contains only fingerprint-bearing Transition semantics.
/// It is a finite tree and contains no runtime callbacks, services, storage handles, or adapter state.
/// </remarks>
public sealed record TransitionDefinition
{
    /// <summary>Creates a canonical Transition IR definition.</summary>
    /// <param name="input">Typed invocation input contract.</param>
    /// <param name="observation">Typed finite aggregate observation contract.</param>
    /// <param name="outcome">Typed value contract shared by admission and body outcomes.</param>
    /// <param name="preconditions">Ordered admission rules evaluated before the body.</param>
    /// <param name="body">Finite structured transition body.</param>
    /// <param name="invariants">Ordered post-update invariants checked by an interpreter.</param>
    /// <param name="subjectCreation">
    /// Optional explicit absent-subject initialization. Omission requires an existing aggregate observation.
    /// </param>
    [JsonConstructor]
    public TransitionDefinition(
        ValueContract input,
        ValueContract observation,
        ValueContract outcome,
        ImmutableArray<TransitionAdmissionRule> preconditions,
        SequenceTransitionNode body,
        ImmutableArray<TransitionInvariant> invariants = default,
        TransitionSubjectCreation? subjectCreation = null)
    {
        Input = input;
        Observation = observation;
        Outcome = outcome;
        Preconditions = preconditions.IsDefault ? [] : preconditions;
        Body = body;
        Invariants = invariants.IsDefault ? [] : invariants;
        SubjectCreation = subjectCreation;
    }

    /// <summary>Typed invocation input contract.</summary>
    public ValueContract Input { get; }

    /// <summary>Typed finite aggregate observation contract.</summary>
    public ValueContract Observation { get; }

    /// <summary>Typed value contract shared by every admission and body outcome.</summary>
    public ValueContract Outcome { get; }

    /// <summary>Ordered admission rules evaluated before the body.</summary>
    public ImmutableArray<TransitionAdmissionRule> Preconditions { get; }

    /// <summary>Finite structured transition body.</summary>
    public SequenceTransitionNode Body { get; }

    /// <summary>Ordered post-update invariants checked by an interpreter.</summary>
    public ImmutableArray<TransitionInvariant> Invariants { get; }

    /// <summary>
    /// Explicit absent-subject initialization semantics, or <see langword="null"/> when the subject must exist.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TransitionSubjectCreation? SubjectCreation { get; }

    /// <summary>Compares definitions by complete persisted semantic value.</summary>
    /// <param name="other">Definition to compare with this value.</param>
    /// <returns><see langword="true"/> when every persisted semantic member is equal.</returns>
    public bool Equals(TransitionDefinition? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Input == other.Input
        && Observation == other.Observation
        && Outcome == other.Outcome
        && Preconditions.SequenceEqual(other.Preconditions)
        && Body == other.Body
        && Invariants.SequenceEqual(other.Invariants)
        && SubjectCreation == other.SubjectCreation;

    /// <summary>Returns a structural hash code for persisted semantic value.</summary>
    /// <returns>A hash code derived from all typed contracts, rules, body nodes, and invariants.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Input);
        hash.Add(Observation);
        hash.Add(Outcome);
        foreach (var precondition in Preconditions)
        {
            hash.Add(precondition);
        }

        hash.Add(Body);
        foreach (var invariant in Invariants)
        {
            hash.Add(invariant);
        }

        hash.Add(SubjectCreation);

        return hash.ToHashCode();
    }
}

/// <summary>
/// One ordered admission predicate and the typed rejection value returned when it does not hold.
/// </summary>
public sealed record TransitionAdmissionRule
{
    /// <summary>Creates an admission rule.</summary>
    /// <param name="id">Stable node identity used by diagnostics, source maps, and traces.</param>
    /// <param name="predicate">Pure predicate that must evaluate to true for admission.</param>
    /// <param name="rejection">Typed outcome expression returned on rejection.</param>
    [JsonConstructor]
    public TransitionAdmissionRule(ExecutionNodeId id, Expr predicate, Expr rejection)
    {
        Id = id;
        Predicate = predicate;
        Rejection = rejection;
    }

    /// <summary>Stable node identity used by diagnostics, source maps, and traces.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Pure predicate that must evaluate to true for admission.</summary>
    public Expr Predicate { get; }

    /// <summary>Typed outcome expression returned on rejection.</summary>
    public Expr Rejection { get; }
}

/// <summary>One post-update invariant over the candidate aggregate state.</summary>
public sealed record TransitionInvariant
{
    /// <summary>Creates a transition invariant.</summary>
    /// <param name="id">Stable node identity used by diagnostics, source maps, and traces.</param>
    /// <param name="predicate">Pure predicate that must hold after candidate updates are applied.</param>
    [JsonConstructor]
    public TransitionInvariant(ExecutionNodeId id, Expr predicate)
    {
        Id = id;
        Predicate = predicate;
    }

    /// <summary>Stable node identity used by diagnostics, source maps, and traces.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Pure predicate that must hold after candidate updates are applied.</summary>
    public Expr Predicate { get; }
}
