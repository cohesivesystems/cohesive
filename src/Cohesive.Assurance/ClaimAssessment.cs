using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Explicit acceptance of one method kind, implementation and configuration for an assessment.</summary>
/// <param name="Kind">Accepted evidence kind; accepting a proof checker does not accept provider declarations.</param>
/// <param name="Method">Exact trusted producer/verifier identity.</param>
/// <param name="Configuration">Exact accepted verifier/mapping configuration.</param>
public sealed record EvidenceAcceptance(EvidenceKind Kind, ClaimReference Method, ClaimReference Configuration)
{
    /// <summary>Explicitly selects the kind, method and configuration of a report for acceptance.</summary>
    /// <param name="evidence">Report whose interpretation tuple the caller chooses to trust.</param>
    /// <returns>An acceptance rule; creating it is a policy decision, not proof of producer authenticity.</returns>
    /// <exception cref="ArgumentNullException">Evidence is null.</exception>
    public static EvidenceAcceptance Of(ClaimEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new(evidence.Kind, evidence.Method, evidence.Configuration);
    }
}

/// <summary>Explanation of one report's contribution to a proof assessment.</summary>
/// <param name="Evidence">Fingerprint of the immutable report.</param>
/// <param name="State">Contribution under the assessment's explicit acceptance policy and dependencies.</param>
/// <param name="Reason">Stable reason code.</param>
public sealed record EvidenceContribution(string Evidence, ProofState State, string Reason);

/// <summary>Reproducible assessment of immutable reports for an exact claim, target and obligation.</summary>
/// <remarks>
/// This is an acceptance calculation, not a verifier invocation or readiness policy engine. Acceptance rules
/// are caller-supplied, attributable policy inputs; their provenance must be authorized outside this library.
/// Contradictory accepted evidence is retained and a valid refutation takes precedence. No vote or evidence
/// count establishes independence. Shared dependencies are preserved in the underlying reports.
/// </remarks>
public sealed class ClaimAssessment
{
    static readonly JsonSerializerOptions Options = AssuranceJson.Options;

    [JsonConstructor]
    internal ClaimAssessment(ClaimDocument claim, EvidenceSubject subject, ClaimReference policy,
        ImmutableArray<EvidenceAcceptance> acceptance, ImmutableArray<ClaimEvidence> evidence,
        ImmutableArray<ClaimReference> unavailableDependencies, ClaimDisposition disposition, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ClaimEvidence.ValidateSubject(subject);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(disposition);
        Claim = claim; Subject = subject; Policy = policy; AsOf = asOf;
        if (acceptance.IsDefault) acceptance = [];
        if (evidence.IsDefault) evidence = [];
        if (acceptance.Any(x => x is null || !Enum.IsDefined(x.Kind) || x.Method is null || x.Configuration is null)
            || evidence.Any(x => x is null))
            throw new ArgumentException("Acceptance entries and evidence must contain exact non-null values.");
        if (evidence.Any(x => x.Claim.Fingerprint != claim.Fingerprint || x.Subject != subject))
            throw new ArgumentException("Every report must match the assessment's exact claim and subject; partition reports before assessment.", nameof(evidence));
        Acceptance = [.. acceptance.Distinct().OrderBy(x => x.Kind)
            .ThenBy(x => x.Method, ClaimReference.CanonicalComparer)
            .ThenBy(x => x.Configuration, ClaimReference.CanonicalComparer)];
        // Compute each retained-artifact identity once, not once per policy or predicate check.
        var reports = evidence.Select(x => (Report: x, Id: x.Fingerprint())).DistinctBy(x => x.Id)
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        Evidence = [.. reports.Select(x => x.Report)];
        UnavailableDependencies = ClaimReference.NormalizeSet(unavailableDependencies);
        var unavailable = UnavailableDependencies.ToHashSet();
        var accepted = Acceptance.ToHashSet();
        Contributions = [.. reports.Select(x => Classify(x.Report, x.Id, accepted, unavailable))];
        var refuted = Contributions.Any(x => x.State == ProofState.Disproven);
        var proven = Contributions.Any(x => x.State == ProofState.Proven);
        var conditional = Contributions.Any(x => x.State == ProofState.Conditional);
        // A restricted proof and a refutation may concern disjoint subdomains.
        // Only full-scope establishment guarantees overlap with every admissible counterexample.
        ConflictingEvidence = refuted && proven;
        State = refuted ? ProofState.Disproven : proven ? ProofState.Proven : conditional ? ProofState.Conditional : ProofState.Unknown;
        Disposition = disposition;
        ValidateDisposition(disposition);
        DispositionEffective = disposition.Kind != ClaimDispositionKind.None
            && (disposition.Kind != ClaimDispositionKind.Waived || asOf < disposition.ExpiresAt);
    }

    EvidenceContribution Classify(ClaimEvidence report, string id, HashSet<EvidenceAcceptance> accepted,
        HashSet<ClaimReference> unavailable)
    {
        EvidenceContribution Unknown(string reason) => new(id, ProofState.Unknown, reason);
        if (!report.DependenciesComplete) return Unknown("evidence.dependencies-unknown");
        if (report.Dependencies.Any(unavailable.Contains)) return Unknown("evidence.dependency-unavailable");
        if (!accepted.Contains(EvidenceAcceptance.Of(report)))
            return Unknown("evidence.method-not-accepted");
        if (report.Support != InterpretationSupport.Supported) return Unknown("evidence.interpretation-unsupported");
        if (report.Status != AssuranceAttemptStatus.Completed) return Unknown("evidence.attempt-not-completed");
        if (report.Verdict == ClaimVerdict.Inconclusive) return Unknown("evidence.inconclusive");
        if (report.Verdict == ClaimVerdict.Violated) return new(id, ProofState.Disproven, "evidence.refuted");
        if (report.Kind == EvidenceKind.TraceObservation || report.Coverage == EvidenceCoverage.Sampled)
            return Unknown("evidence.sampled-success");
        return report.Coverage == EvidenceCoverage.RestrictedScope
            ? new(id, ProofState.Conditional, "evidence.residuals")
            : new(id, ProofState.Proven, "evidence.full-scope");
    }

    static void ValidateDisposition(ClaimDisposition value)
    {
        if (!Enum.IsDefined(value.Kind)) throw new ArgumentException("Unknown disposition kind.");
        if (value.Kind == ClaimDispositionKind.None)
        {
            if (value.Authority is not null || value.Reason is not null || value.ExpiresAt is not null || value.Revision is not null)
                throw new ArgumentException("An empty disposition cannot carry waiver or revision fields.");
        }
        else if (value.Authority is null || string.IsNullOrWhiteSpace(value.Reason)
            || (value.Kind == ClaimDispositionKind.Waived && (value.ExpiresAt is null || value.Revision is not null))
            || (value.Kind == ClaimDispositionKind.RequirementRevised && (value.Revision is null || value.ExpiresAt is not null)))
            throw new ArgumentException("A disposition requires authority, reason and either waiver expiry or accepted revision.");
    }

    /// <summary>Persisted assessment schema.</summary>
    public string Schema => "cohesive-assurance-assessment/v2";
    /// <summary>Exact assessed claim including source provenance.</summary>
    public ClaimDocument Claim { get; }
    /// <summary>Exact target, configuration, scope and obligation.</summary>
    public EvidenceSubject Subject { get; }
    /// <summary>Attribution for the acceptance policy; not an authentication token.</summary>
    public ClaimReference Policy { get; }
    /// <summary>Effective policy rules retained for independent assessment.</summary>
    public ImmutableArray<EvidenceAcceptance> Acceptance { get; }
    /// <summary>Retained reports, deduplicated and ordered by exact fingerprint.</summary>
    public ImmutableArray<ClaimEvidence> Evidence { get; }
    /// <summary>Prior exact dependencies now changed, refuted or otherwise unavailable.</summary>
    public ImmutableArray<ClaimReference> UnavailableDependencies { get; }
    /// <summary>Disposition independent of proof state.</summary>
    public ClaimDisposition Disposition { get; }
    /// <summary>Explicit assessment clock, used for waiver expiry without ambient time.</summary>
    public DateTimeOffset AsOf { get; }
    /// <summary>Proof state derived from accepted reports for this boundary.</summary>
    public ProofState State { get; }
    /// <summary>Per-report decisions, including unsupported methods and stale dependencies.</summary>
    public ImmutableArray<EvidenceContribution> Contributions { get; }
    /// <summary>Whether accepted full-scope establishment and refutation conflict; restricted-domain overlap is not inferred.</summary>
    public bool ConflictingEvidence { get; }
    /// <summary>Whether the disposition is effective at the explicit assessment time; not a readiness decision.</summary>
    public bool DispositionEffective { get; }

    /// <summary>Assesses evidence without executing a verifier or mutating prior reports.</summary>
    /// <param name="claim">Exact claim being assessed.</param>
    /// <param name="subject">Exact target and obligation; model proof cannot discharge realization.</param>
    /// <param name="policy">Exact authority artifact for the effective acceptance rules.</param>
    /// <param name="acceptance">Explicit trusted method/configuration/kind tuples; empty accepts no methods.</param>
    /// <param name="evidence">Reports; every exact claim and subject must match or construction fails.</param>
    /// <param name="asOf">Explicit time at which dispositions are evaluated.</param>
    /// <param name="unavailableDependencies">Exact prior references that changed or were refuted.</param>
    /// <param name="disposition">Optional authorized exception or revision; null means none.</param>
    /// <returns>Immutable derived assessment with explainable per-report contributions.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">An assessment input or disposition is invalid.</exception>
    public static ClaimAssessment Evaluate(ClaimDocument claim, EvidenceSubject subject, ClaimReference policy,
        ImmutableArray<EvidenceAcceptance> acceptance, ImmutableArray<ClaimEvidence> evidence, DateTimeOffset asOf,
        ImmutableArray<ClaimReference> unavailableDependencies = default, ClaimDisposition? disposition = null) =>
        new(claim, subject, policy, acceptance, evidence, unavailableDependencies,
            disposition ?? new(ClaimDispositionKind.None), asOf);

    /// <summary>Assesses one report using its own exact claim and subject, avoiding duplicated boundary wiring.</summary>
    /// <param name="evidence">Report to assess.</param>
    /// <param name="policy">Exact policy attribution.</param>
    /// <param name="acceptance">Explicitly chosen trusted interpretation tuples; no automatic acceptance is inferred.</param>
    /// <param name="asOf">Explicit clock for disposition expiry.</param>
    /// <param name="unavailableDependencies">Changed or refuted prior dependencies.</param>
    /// <param name="disposition">Optional authorized disposition.</param>
    /// <returns>Assessment bound to the report's exact claim and subject.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Policy entries or disposition are invalid.</exception>
    public static ClaimAssessment Evaluate(ClaimEvidence evidence, ClaimReference policy,
        ImmutableArray<EvidenceAcceptance> acceptance, DateTimeOffset asOf,
        ImmutableArray<ClaimReference> unavailableDependencies = default, ClaimDisposition? disposition = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Evaluate(evidence.Claim, evidence.Subject, policy, acceptance, [evidence], asOf,
            unavailableDependencies, disposition);
    }

    /// <summary>Retains policy, exact inputs and derived results together.</summary>
    /// <returns>Canonical portable assessment JSON.</returns>
    /// <exception cref="JsonException">An embedded representation cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An embedded representation is unsupported.</exception>
    public string Serialize() => Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(this, Options));

    /// <summary>Restores inputs and recomputes the assessment, rejecting altered derived results.</summary>
    /// <param name="json">Previously serialized assessment.</param>
    /// <returns>Independently recomputed assessment at its retained time, not the current clock time.</returns>
    /// <exception cref="JsonException">The document is malformed or disagrees with recomputation.</exception>
    public static ClaimAssessment Reassess(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, Options, "claim assessment", out ClaimAssessment? result, out var error)
            || result is null)
            throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        return result;
    }
}
