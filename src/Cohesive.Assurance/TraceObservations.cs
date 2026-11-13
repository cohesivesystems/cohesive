using System.Collections.Immutable;
using Cohesive.Model;

namespace Cohesive.Assurance;

/// <summary>Interpretation of a mapped semantic occurrence; attempts are not commits.</summary>
public enum SemanticObservationKind
{
    /// <summary>A member of the selected initial population.</summary>
    InitialState,
    /// <summary>A member of a population after a successful commit.</summary>
    CommittedState,
    /// <summary>A successful atomic transition with before/command/after input roles.</summary>
    Commit,
    /// <summary>A semantic event with a role-neutral payload.</summary>
    Event,
    /// <summary>A definitely aborted attempt, excluded from committed-transition obligations.</summary>
    AbortedAttempt,
    /// <summary>An attempt whose commit outcome is unknown.</summary>
    UnknownOutcome
}

/// <summary>Whether the contract establishes that no further selected occurrences can exist.</summary>
public enum ObservationClosure
{
    /// <summary>More occurrences may exist or arrive.</summary>
    Open,
    /// <summary>The finite selected scope is authoritatively closed.</summary>
    Closed
}

/// <summary>Whether all compatible joint assignments and trace mappings were enumerated.</summary>
public enum EnumerationCompleteness
{
    /// <summary>Additional compatible alternatives may exist.</summary>
    Partial,
    /// <summary>Every compatible alternative is retained, under the supplied evidence.</summary>
    Complete
}

/// <summary>Sources whose completeness is required for one exact selector.</summary>
/// <param name="Selector">Exact state population, transition or event selector.</param>
/// <param name="Partitions">Required source/partition/epoch keys within the batch scope.</param>
public sealed record ObservationRequirement(ClaimReference Selector, ImmutableArray<string> Partitions);

/// <summary>Declared adapter contract for an already ordered, atomic semantic scope.</summary>
/// <param name="Reference">Exact observation contract referenced by claims.</param>
/// <param name="Scope">Exact scope and linearization/clock contract; never inferred from arrival order.</param>
/// <param name="Requirements">Required partitions for each supported selector.</param>
public sealed record TraceObservationContract(ClaimReference Reference, ClaimReference Scope,
    ImmutableArray<ObservationRequirement> Requirements);

/// <summary>One mapped observation; this is an adapter input, not a Simulation run or world.</summary>
/// <param name="Occurrence">Stable source occurrence identity; retries must have distinct identities.</param>
/// <param name="Partition">Source/partition/epoch key declared by the contract.</param>
/// <param name="Step">Nonnegative semantic step order within the declared atomic scope.</param>
/// <param name="Ticks">Nonnegative monotone contract time in 100 ns units; equal steps have equal time.</param>
/// <param name="Selector">Exact selector matched by the adapter.</param>
/// <param name="Kind">State, event, successful commit or attempt classification.</param>
/// <param name="Values">State/commit role-indexed observations; events require an empty dictionary. Absent keys mean missing evidence.
/// Undefined values denote authoritative domain absence, and null denotes explicit domain null.</param>
/// <param name="EventPayload">Role-neutral event payload; null means missing evidence. Must be null for non-events.</param>
public sealed record SemanticObservation(string Occurrence, string Partition, long Step, long Ticks,
    ClaimReference Selector, SemanticObservationKind Kind,
    ImmutableDictionary<ClaimInputRole, ObservationValue> Values, ObservationValue? EventPayload = null);

/// <summary>Attributed completeness from scope origin through inclusive frontiers for one source and selector.</summary>
/// <param name="Selector">Exact selector whose occurrences and population membership are complete.</param>
/// <param name="Partition">Required source/partition/epoch key.</param>
/// <param name="ThroughStep">Inclusive semantic order frontier, from the beginning of the declared scope.</param>
/// <param name="ThroughTicks">Inclusive clock frontier; all outcomes at this time have been observed.</param>
/// <param name="Closure">Whether this entire finite source/selector scope is closed.</param>
/// <param name="Evidence">Exact adapter/protocol evidence supporting these assertions, not wall-clock time.</param>
public sealed record ObservationCompleteness(ClaimReference Selector, string Partition,
    long ThroughStep, long ThroughTicks, ObservationClosure Closure, ClaimReference Evidence);

/// <summary>One compatible ordered observation mapping or joint finite assignment.</summary>
/// <param name="Reference">Exact artifact identifying this alternative.</param>
/// <param name="Observations">Observations in declared semantic step order. Identical redeliveries are deduplicated.</param>
/// <param name="Completeness">At most one frontier per exact selector and partition.</param>
public sealed record TraceObservationAlternative(ClaimReference Reference,
    ImmutableArray<SemanticObservation> Observations, ImmutableArray<ObservationCompleteness> Completeness);

/// <summary>Finite observation input to the reference batch interpreter.</summary>
/// <param name="Contract">Exact mapping, order, clock and completeness requirements.</param>
/// <param name="Target">Exact reference model, candidate or deployment being observed.</param>
/// <param name="Epoch">Exact observation epoch; corrections are new immutable batches.</param>
/// <param name="Alternatives">Joint compatible assignments/mappings, never independent per-predicate truth values.</param>
/// <param name="Enumeration">Whether compatible alternatives were completely enumerated.</param>
/// <param name="EnumerationEvidence">Exact enumeration/mapping evidence and its assumptions.</param>
/// <remarks>Completeness declarations are conditional inputs, not independent verification of an adapter or provider.</remarks>
public sealed record TraceObservationBatch(TraceObservationContract Contract, ClaimReference Target,
    ClaimReference Epoch, ImmutableArray<TraceObservationAlternative> Alternatives,
    EnumerationCompleteness Enumeration, ClaimReference EnumerationEvidence);
