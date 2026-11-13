using System.Text;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Three-valued trace semantics; pending is explained separately.</summary>
public enum ClaimVerdict
{
    /// <summary>Every admitted completion satisfies the claim in the declared scope.</summary>
    Satisfied,
    /// <summary>Every admitted completion violates the claim in the declared scope.</summary>
    Violated,
    /// <summary>Available observations do not establish either result.</summary>
    Inconclusive
}

/// <summary>Reference evaluator limits; exhaustion never produces a definitive aggregate verdict.</summary>
/// <param name="MaxAlternatives">Maximum joint alternatives evaluated.</param>
/// <param name="MaxObservations">Maximum total observation/frontier records accepted, including duplicate deliveries.</param>
/// <param name="MaxPredicateEvaluations">Maximum predicate evaluations, including event candidate comparisons.</param>
/// <param name="MaxEventCandidateVisits">Maximum eligible event candidates compared; binary-search skips outside the temporal window are not charged.</param>
public sealed record TraceEvaluationLimits(int MaxAlternatives = 1024, int MaxObservations = 100_000,
    int MaxPredicateEvaluations = 1_000_000, int MaxEventCandidateVisits = 1_000_000);

/// <summary>One actionable gap or invalid observation.</summary>
/// <param name="Code">Stable diagnostic identity.</param>
/// <param name="Location">Observation or predicate location.</param>
/// <param name="Message">Explanation of the missing evidence or unsupported behavior.</param>
public sealed record TraceEvaluationDiagnostic(string Code, string Location, string Message);

/// <summary>Result for one selected state, commit, or trigger in one compatible alternative.</summary>
/// <param name="Occurrence">Stable triggering occurrence identity.</param>
/// <param name="Partition">Triggering source partition.</param>
/// <param name="Step">Triggering semantic step.</param>
/// <param name="Verdict">Individual obligation verdict, independent of enclosing stream closure.</param>
/// <param name="WitnessOccurrence">Matching witness occurrence, if found.</param>
/// <param name="WitnessPartition">Matching witness source, if found.</param>
/// <param name="Reason">Stable explanation code, including pending lifecycle.</param>
public sealed record ClaimObligationResult(string Occurrence, string Partition, long Step,
    ClaimVerdict Verdict, string? WitnessOccurrence, string? WitnessPartition, string Reason);

/// <summary>Coverage and outcomes for one alternative; counts are not global population percentages.</summary>
/// <param name="Alternative">Exact alternative artifact.</param>
/// <param name="Verdict">Verdict for this alternative, including closure.</param>
/// <param name="Obligations">All evaluated occurrences in semantic order.</param>
/// <param name="DuplicateDeliveries">Identical deliveries ignored without multiplying obligations.</param>
/// <param name="SelectionClosed">Whether required sources close the selected finite scope.</param>
/// <param name="Vacuous">True only for a closed, complete selection with no obligations.</param>
public sealed record TraceAlternativeResult(ClaimReference Alternative, ClaimVerdict Verdict,
    ImmutableArray<ClaimObligationResult> Obligations, int DuplicateDeliveries, bool SelectionClosed, bool Vacuous);

/// <summary>Immutable reference-evaluation evidence. A trace success is never a universal implementation proof.</summary>
/// <param name="Claim">Exact derived claim and authoritative source provenance.</param>
/// <param name="Batch">Retained observations, exact target, mapping and completeness evidence.</param>
/// <param name="Limits">Effective evaluation bounds.</param>
/// <param name="Status">Attempt outcome, separate from verdict.</param>
/// <param name="Verdict">Verdict over every compatible alternative under declared completeness.</param>
/// <param name="Alternatives">Per-alternative witnesses, coverage and obligation outcomes.</param>
/// <param name="Diagnostics">Missing evidence, unsupported semantics or invalid observation details.</param>
public sealed record TraceEvaluation(ClaimDocument Claim, TraceObservationBatch Batch, TraceEvaluationLimits Limits,
    AssuranceAttemptStatus Status, ClaimVerdict Verdict, ImmutableArray<TraceAlternativeResult> Alternatives,
    ImmutableArray<TraceEvaluationDiagnostic> Diagnostics)
{
    static readonly JsonSerializerOptions Options = AssuranceJson.Options;

    // Reference identity is essential: public record construction and `with` copies are not trusted.
    // Weak keys retain no discarded evaluations and are never persisted or used as semantic identity.
    static readonly ConditionalWeakTable<TraceEvaluation, object> Verified = new();
    static readonly object VerificationMarker = new();

    internal TraceEvaluation MarkReferenceEvaluated()
    {
        Verified.TryAdd(this, VerificationMarker);
        return this;
    }

    internal static TraceEvaluation Verify(TraceEvaluation evaluation)
    {
        if (Verified.TryGetValue(evaluation, out _)) return evaluation;
        if (evaluation.Claim is null || evaluation.Batch is null || evaluation.Limits is null)
            throw new JsonException("Retained evidence must include the claim, observation batch and effective limits.");
        var recomputed = TraceClaimEvaluator.Evaluate(evaluation.Claim, evaluation.Batch, evaluation.Limits);
        if (!string.Equals(evaluation.Serialize(), recomputed.Serialize(), StringComparison.Ordinal))
            throw new JsonException("Retained evidence does not agree with the reference evaluation of its inputs.");
        return recomputed;
    }

    /// <summary>Versioned reference evaluation method; not a proof-system identity.</summary>
    public const string MethodId = "cohesive.assurance.batch/v2";

    /// <summary>Versioned method identity retained in each evaluation.</summary>
    public string Method => MethodId;

    /// <summary>Serializes the complete retained evaluation into canonical JSON.</summary>
    /// <returns>Portable result and input evidence, suitable for independent re-evaluation.</returns>
    /// <exception cref="System.Text.Json.JsonException">An observation cannot be encoded portably.</exception>
    /// <exception cref="NotSupportedException">An observation representation is unsupported by canonical JSON.</exception>
    public string Serialize() => Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(this, Options));

    /// <summary>Reads retained canonical evidence and independently recomputes its result.</summary>
    /// <param name="json">Canonical evidence produced by this evaluator version.</param>
    /// <returns>The recomputed result, after exact agreement with retained verdicts and provenance.</returns>
    /// <exception cref="JsonException">The evidence is malformed, noncanonical, incompatible or disagrees with re-evaluation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Retained evaluation limits are invalid.</exception>
    public static TraceEvaluation Reevaluate(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, Options, "trace evaluation", out TraceEvaluation? retained, out var error)
            || retained is null)
            throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        var result = Verify(retained);
        if (!string.Equals(json, result.Serialize(), StringComparison.Ordinal))
            throw new JsonException("Retained evaluation JSON is not the exact canonical reference output.");
        return result;
    }

    /// <summary>Hashes the complete evaluation evidence, including inputs, limits and diagnostics.</summary>
    /// <returns>SHA-256 of canonical evidence bytes; integrity identity, not a signature.</returns>
    /// <exception cref="System.Text.Json.JsonException">An observation cannot be encoded portably.</exception>
    /// <exception cref="NotSupportedException">An observation representation is unsupported.</exception>
    public string Fingerprint() => Convert.ToHexString(StrictDocumentJson.ComputeCanonicalSha256(this, Options));
}
