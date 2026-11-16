using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

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
    ImmutableArray<ObservationRequirement> Requirements)
{
    /// <summary>Compares exact contract content without relying on immutable-array instance identity.</summary>
    /// <param name="other">Contract to compare; null never matches.</param>
    /// <returns>Whether references and ordered requirements/partitions match exactly. Default arrays differ from empty arrays.</returns>
    /// <remarks>This is representation equality, not equivalence under reordered requirements. It does not validate a contract.</remarks>
    public bool SameContent(TraceObservationContract? other)
    {
        if (other is null || Reference != other.Reference || Scope != other.Scope
            || Requirements.IsDefault != other.Requirements.IsDefault) return false;
        if (Requirements.IsDefault) return true;
        if (Requirements.Length != other.Requirements.Length) return false;
        for (var i = 0; i < Requirements.Length; i++)
        {
            var left = Requirements[i];
            var right = other.Requirements[i];
            if (left is null || right is null)
            {
                if (left != right) return false;
                continue;
            }
            if (left.Selector != right.Selector || left.Partitions.IsDefault != right.Partitions.IsDefault) return false;
            if (!left.Partitions.IsDefault && !left.Partitions.AsSpan().SequenceEqual(right.Partitions.AsSpan())) return false;
        }
        return true;
    }
}

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
    ImmutableDictionary<ClaimInputRole, ObservationValue> Values, ObservationValue? EventPayload = null)
{
    // Occurrence identity and content must agree independently of dictionary instance identity.
    internal bool SameContent(SemanticObservation other) =>
        Occurrence == other.Occurrence && Partition == other.Partition
        && Step == other.Step && Ticks == other.Ticks && Kind == other.Kind && Selector == other.Selector
        && Nullable.Equals(EventPayload, other.EventPayload)
        && Values.Count == other.Values.Count
        && Values.All(x => other.Values.TryGetValue(x.Key, out var value) && x.Value.Equals(value));
}

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
    EnumerationCompleteness Enumeration, ClaimReference EnumerationEvidence)
{
    sealed record Wire(string Schema, TraceObservationBatch Batch);
    const string WireSchema = "cohesive-assurance-observations/v1";

    /// <summary>Encodes a standalone, versioned observation-input document with the shared canonical JSON policy.</summary>
    /// <remarks>Use this format for transport of unevaluated inputs. Evaluation/checkpoint artifacts embed the same batch
    /// payload under their own versioned envelope; use their Serialize/Deserialize APIs for retained results instead.</remarks>
    /// <returns>Versioned canonical observation document; serialization does not establish semantic validity or completeness.</returns>
    /// <exception cref="JsonException">A retained observation cannot be encoded.</exception>
    /// <exception cref="NotSupportedException">A representation is unsupported.</exception>
    public string Serialize() => StrictDocumentJson.GetCanonicalText(new Wire(WireSchema, this), AssuranceJson.Options);

    /// <summary>Reads a canonical observation document without evaluating or trusting its assertions.</summary>
    /// <param name="json">Canonical versioned observation JSON.</param>
    /// <returns>Untrusted observation inputs; an evaluator or monitor must validate them against the exact claim.</returns>
    /// <exception cref="JsonException">The document is malformed, noncanonical or uses an unsupported version.</exception>
    public static TraceObservationBatch Deserialize(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, AssuranceJson.Options, "observations",
                out Wire? wire, out var error) || wire is null || wire.Batch is null)
            throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        if (wire.Schema != WireSchema) throw new JsonException("Unsupported observation wire schema.");
        return wire.Batch;
    }
}
