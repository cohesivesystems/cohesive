using System.Text;
using System.Text.Json.Serialization;
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

/// <summary>Evaluator-owned immutable result. Only reference evaluation or verified restoration can produce this type.</summary>
/// <remarks>Inputs retain core immutable observation snapshots; arbitrary CLR payload objects are projected before evaluation.</remarks>
[JsonConverter(typeof(TraceEvaluationJsonConverter))]
public sealed class TraceEvaluation
{
    static readonly JsonSerializerOptions Options = AssuranceJson.Options;

    internal TraceEvaluation(ClaimDocument claim, TraceObservationBatch batch, TraceEvaluationLimits limits,
        AssuranceAttemptStatus status, ClaimVerdict verdict, ImmutableArray<TraceAlternativeResult> alternatives,
        ImmutableArray<TraceEvaluationDiagnostic> diagnostics)
    {
        Claim = claim; Batch = batch; Limits = limits; Status = status; Verdict = verdict;
        Alternatives = alternatives; Diagnostics = diagnostics;
    }

    /// <summary>Exact derived claim and authoring provenance.</summary>
    public ClaimDocument Claim { get; }
    /// <summary>Retained immutable observations and completeness declarations.</summary>
    public TraceObservationBatch Batch { get; }
    /// <summary>Effective evaluation bounds.</summary>
    public TraceEvaluationLimits Limits { get; }
    /// <summary>Attempt status independent of semantic verdict.</summary>
    public AssuranceAttemptStatus Status { get; }
    /// <summary>Verdict over every compatible alternative under declared completeness.</summary>
    public ClaimVerdict Verdict { get; }
    /// <summary>Per-alternative witnesses, coverage and outcomes.</summary>
    public ImmutableArray<TraceAlternativeResult> Alternatives { get; }
    /// <summary>Gaps, unsupported behavior and invalid observations.</summary>
    public ImmutableArray<TraceEvaluationDiagnostic> Diagnostics { get; }
    /// <summary>Versioned reference method, unchanged by the ownership boundary.</summary>
    public const string MethodId = "cohesive.assurance.batch/v2";
    /// <summary>Versioned method retained in evidence.</summary>
    public string Method => MethodId;

    // Wire data is untrusted. This private DTO is never the public evaluator-owned result.
    sealed record Retained(ClaimDocument Claim, TraceObservationBatch Batch, TraceEvaluationLimits Limits,
        AssuranceAttemptStatus Status, ClaimVerdict Verdict, ImmutableArray<TraceAlternativeResult> Alternatives,
        ImmutableArray<TraceEvaluationDiagnostic> Diagnostics, string Method);
    Retained Payload() => new(Claim, Batch, Limits, Status, Verdict, Alternatives, Diagnostics, Method);

    /// <summary>Serializes retained evidence canonically.</summary>
    /// <returns>Portable input and output evidence for independent evaluation.</returns>
    /// <exception cref="JsonException">An observation cannot be encoded.</exception>
    /// <exception cref="NotSupportedException">A representation is unsupported.</exception>
    public string Serialize() => Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(Payload(), Options));

    /// <summary>Independently evaluates retained inputs and requires exact canonical agreement.</summary>
    /// <param name="json">Canonical evidence from this method version.</param>
    /// <returns>An evaluator-owned result, never an unchecked deserialized record.</returns>
    /// <exception cref="JsonException">Evidence is malformed or disagrees with evaluation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Retained limits are nonpositive.</exception>
    public static TraceEvaluation Reevaluate(string json)
    {
        var result = ReadVerified(json);
        if (!string.Equals(json, result.Serialize(), StringComparison.Ordinal))
            throw new JsonException("Retained evaluation JSON is not the exact canonical reference output.");
        return result;
    }

    internal static TraceEvaluation ReadVerified(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, Options, "trace evaluation", out Retained? retained, out var error)
            || retained is null)
            throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        if (retained.Claim is null || retained.Batch is null || retained.Limits is null)
            throw new JsonException("Retained evaluation requires claim, batch and limits.");
        var result = TraceClaimEvaluator.Evaluate(retained.Claim, retained.Batch, retained.Limits);
        var expected = StrictDocumentJson.GetCanonicalBytes(retained, Options);
        var actual = StrictDocumentJson.GetCanonicalBytes(result.Payload(), Options);
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new JsonException("Retained evidence disagrees with independent reference evaluation.");
        return result;
    }

    internal void Write(Utf8JsonWriter writer, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, Payload(), options);

    /// <summary>Hashes the complete evidence through the shared canonical fingerprint helper.</summary>
    /// <returns>SHA-256 integrity identity, not a signature.</returns>
    /// <exception cref="JsonException">An observation cannot be encoded.</exception>
    /// <exception cref="NotSupportedException">A representation is unsupported.</exception>
    public string Fingerprint() => Convert.ToHexString(StrictDocumentJson.ComputeCanonicalSha256(Payload(), Options));
}

// The serializer cannot bypass evaluation by invoking a result constructor.
internal sealed class TraceEvaluationJsonConverter : JsonConverter<TraceEvaluation>
{
    public override TraceEvaluation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return TraceEvaluation.ReadVerified(document.RootElement.GetRawText());
    }
    public override void Write(Utf8JsonWriter writer, TraceEvaluation value, JsonSerializerOptions options) => value.Write(writer, options);
}
