using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Immutable, portable report for one exact claim and obligation. Reports are attribution, not signatures.</summary>
/// <remarks>
/// External adapters supply reports; this type does not invoke or authenticate a verifier. Acceptance is an
/// explicit assessment input. Existing capability declarations are referenced as artifacts, never redefined here.
/// Serialization is a retained-attempt boundary, not a per-observation operation, and materializes complete documents.
/// </remarks>
public sealed class ClaimEvidence
{
    static readonly JsonSerializerOptions Options = AssuranceJson.Options;

    /// <summary>Creates and validates an attributable interpretation report.</summary>
    /// <param name="claim">Derived claim with exact source, lowering, configuration and source-map provenance.</param>
    /// <param name="subject">Exact target, configuration, declared scope and proof obligation.</param>
    /// <param name="kind">Method strength; observations cannot establish universal success.</param>
    /// <param name="method">Exact producer/verifier version and implementation artifact.</param>
    /// <param name="configuration">Exact verifier configuration, including effective options and conventions.</param>
    /// <param name="support">Interpreter's support declaration for this claim.</param>
    /// <param name="status">Attempt completion status, independent of semantic verdict.</param>
    /// <param name="verdict">Reported result; noncompleted or unsupported attempts must remain inconclusive.</param>
    /// <param name="coverage">Declared coverage; full scope cannot have additional residual restrictions.</param>
    /// <param name="dependenciesComplete">Whether the dependency slice is known; false blocks reuse and proof acceptance.</param>
    /// <param name="dependencies">Exact dependency artifacts; claim assumptions and interpretation identities are added automatically.</param>
    /// <param name="residuals">Additional bounds or assumptions narrower than the claim; required for restricted coverage.</param>
    /// <param name="sharedDependencies">Shared predicates, lowerings or other sources of correlated evidence.</param>
    /// <param name="artifacts">Exact tool reports, encodings, seeds, schedules, faults, reductions or optional witnesses.</param>
    /// <param name="trace">Structured retained reference evaluation for trace evidence; null for other methods.</param>
    /// <exception cref="ArgumentNullException">A required reference is null.</exception>
    /// <exception cref="ArgumentException">A report is inconsistent, lacks artifacts, or uses an unsupported enum value.</exception>
    /// <exception cref="JsonException">Retained trace evidence is malformed or fails independent evaluation.</exception>
    [JsonConstructor]
    public ClaimEvidence(ClaimDocument claim, EvidenceSubject subject, EvidenceKind kind,
        ClaimReference method, ClaimReference configuration, InterpretationSupport support,
        AssuranceAttemptStatus status, ClaimVerdict verdict, EvidenceCoverage coverage, bool dependenciesComplete,
        ImmutableArray<ClaimReference> dependencies = default, ImmutableArray<ClaimReference> residuals = default,
        ImmutableArray<ClaimReference> sharedDependencies = default, ImmutableArray<ClaimReference> artifacts = default,
        TraceEvaluation? trace = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ValidateSubject(subject);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(support) || !Enum.IsDefined(status)
            || !Enum.IsDefined(verdict) || !Enum.IsDefined(coverage))
            throw new ArgumentException("Evidence uses an unsupported semantic value.");
        if ((status != AssuranceAttemptStatus.Completed || support != InterpretationSupport.Supported)
            && verdict != ClaimVerdict.Inconclusive)
            throw new ArgumentException("Unsupported or noncompleted attempts cannot report a definitive verdict.");
        Claim = claim; Subject = subject; Kind = kind; Method = method; Configuration = configuration;
        Support = support; Status = status; Verdict = verdict; Coverage = coverage;
        DependenciesComplete = dependenciesComplete;
        Residuals = ClaimReference.NormalizeSet(residuals);
        SharedDependencies = ClaimReference.NormalizeSet(sharedDependencies);
        Artifacts = ClaimReference.NormalizeSet(artifacts);
        Dependencies = ClaimReference.NormalizeSet([.. ClaimReference.NormalizeSet(dependencies),
            .. claim.ReferenceLocations().Select(x => x.Reference), .. Residuals, .. SharedDependencies, .. Artifacts,
            subject.Target, subject.Configuration, subject.Scope, method, configuration]);
        if ((coverage == EvidenceCoverage.RestrictedScope && Residuals.IsEmpty)
            || (coverage == EvidenceCoverage.FullScope && !Residuals.IsEmpty))
            throw new ArgumentException("Restricted coverage requires named residuals; full coverage cannot hide residuals.");
        if (kind == EvidenceKind.TraceObservation)
        {
            if (trace is null || coverage != EvidenceCoverage.Sampled)
                throw new ArgumentException("Trace evidence requires retained observations and sampled coverage.");
            if (subject.Obligation != ProofObligation.Realization
                || subject.Kind is not (EvidenceTargetKind.Implementation or EvidenceTargetKind.Deployment))
                throw new ArgumentException("Trace evidence supports only realization obligations on implementation or deployment targets.", nameof(subject));
            if (trace.Claim.Fingerprint != claim.Fingerprint || trace.Batch.Target != subject.Target
                || trace.Status != status || trace.Verdict != verdict
                || method != ReferenceMethod || support != TraceSupport(status))
                throw new ArgumentException("Trace provenance or outcome disagrees with its independently checked evaluation.");
            if (status == AssuranceAttemptStatus.Completed)
                Dependencies = ClaimReference.NormalizeSet([.. Dependencies, .. TraceReferences(trace)]);
            else
                DependenciesComplete = false; // A failed validation/bounded scan may not have inspected the entire mapping.
        }
        else if (trace is not null || Artifacts.IsEmpty)
            throw new ArgumentException("Nontrace reports require attributable artifacts and cannot embed a trace evaluation.");
        Trace = trace;
    }

    /// <summary>Current envelope schema.</summary>
    public string Schema => "cohesive-assurance-evidence/v2";
    /// <summary>Claim and source/lowering provenance.</summary>
    public ClaimDocument Claim { get; }
    /// <summary>Exact assessment boundary.</summary>
    public EvidenceSubject Subject { get; }
    /// <summary>Method strength without conflating declaration and verification.</summary>
    public EvidenceKind Kind { get; }
    /// <summary>Exact producer or verifier artifact.</summary>
    public ClaimReference Method { get; }
    /// <summary>Exact verifier configuration.</summary>
    public ClaimReference Configuration { get; }
    /// <summary>Interpreter support for this claim.</summary>
    public InterpretationSupport Support { get; }
    /// <summary>Attempt status, separate from verdict and proof state.</summary>
    public AssuranceAttemptStatus Status { get; }
    /// <summary>Method's semantic verdict within its coverage.</summary>
    public ClaimVerdict Verdict { get; }
    /// <summary>Explicit scope of the report.</summary>
    public EvidenceCoverage Coverage { get; }
    /// <summary>Whether the dependency slice is known; not whether its assertions are true.</summary>
    public bool DependenciesComplete { get; }
    /// <summary>Canonical exact dependencies, including assumptions and interpretation identity.</summary>
    public ImmutableArray<ClaimReference> Dependencies { get; }
    /// <summary>Named narrower bounds and assumptions that remain undischarged.</summary>
    public ImmutableArray<ClaimReference> Residuals { get; }
    /// <summary>Correlated predicate/lowering dependencies; repeated evidence is not independent support.</summary>
    public ImmutableArray<ClaimReference> SharedDependencies { get; }
    /// <summary>Referenced tool-specific artifacts and optional witnesses.</summary>
    public ImmutableArray<ClaimReference> Artifacts { get; }
    /// <summary>Full existing trace evaluation document, or null for external methods.</summary>
    public TraceEvaluation? Trace { get; }

    /// <summary>Exact built-in reference-evaluator identity; acceptance still requires explicit configuration.</summary>
    public static ClaimReference ReferenceMethod { get; } = new(new("assurance-method://reference-trace"),
        TraceEvaluation.MethodId, TraceEvaluation.MethodId);

    /// <summary>Captures a reference trace evaluation as sampled evidence.</summary>
    /// <param name="evaluation">Evaluator-owned result, reused directly; its sealed type excludes caller-constructed or record-copy results.</param>
    /// <param name="subject">Implementation/deployment realization subject; scope and configuration remain explicit adapter attribution.</param>
    /// <param name="configuration">Exact adapter/mapping and evaluator configuration artifact.</param>
    /// <param name="dependenciesComplete">Whether all relevant realization and mapping dependencies are declared.</param>
    /// <param name="dependencies">Additional exact dependencies such as storage guarantees.</param>
    /// <param name="sharedDependencies">Shared predicates or lowerings that prevent independence claims.</param>
    /// <returns>Immutable trace evidence that can refute but cannot establish universal correctness.</returns>
    /// <exception cref="ArgumentNullException">Evaluation or a required reference is null.</exception>
    /// <exception cref="ArgumentException">The subject or dependencies are invalid or inconsistent.</exception>
    /// <exception cref="JsonException">The supplied evaluation disagrees with independent re-evaluation.</exception>
    public static ClaimEvidence FromTrace(TraceEvaluation evaluation, EvidenceSubject subject,
        ClaimReference configuration, bool dependenciesComplete, ImmutableArray<ClaimReference> dependencies = default,
        ImmutableArray<ClaimReference> sharedDependencies = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return new(evaluation.Claim, subject, EvidenceKind.TraceObservation, ReferenceMethod, configuration,
            TraceSupport(evaluation.Status), evaluation.Status, evaluation.Verdict, EvidenceCoverage.Sampled,
            dependenciesComplete, dependencies, sharedDependencies: sharedDependencies, trace: evaluation);
    }

    /// <summary>Serializes the complete envelope canonically; this is not an authenticity signature.</summary>
    /// <returns>Portable evidence JSON with retained claim and trace documents.</returns>
    /// <exception cref="JsonException">An embedded representation cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An embedded representation is unsupported.</exception>
    public string Serialize() => Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(this, Options));

    /// <summary>Restores and validates evidence; trace reports are independently re-evaluated, external reports remain attribution.</summary>
    /// <param name="json">Previously serialized envelope.</param>
    /// <returns>Validated immutable evidence without invoking an external verifier.</returns>
    /// <exception cref="JsonException">The envelope is malformed, incompatible or inconsistent.</exception>
    public static ClaimEvidence Deserialize(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, Options, "claim evidence", out ClaimEvidence? result, out var error)
            || result is null)
            throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        return result;
    }

    /// <summary>Identifies this exact report, including all provenance and retained inputs.</summary>
    /// <returns>Canonical SHA-256 integrity identity, not a signature or reuse authorization.</returns>
    /// <exception cref="JsonException">An embedded representation cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An embedded representation is unsupported.</exception>
    public string Fingerprint() => Convert.ToHexString(StrictDocumentJson.ComputeCanonicalSha256(this, Options));

    static InterpretationSupport TraceSupport(AssuranceAttemptStatus status) => status switch
    {
        AssuranceAttemptStatus.Unsupported => InterpretationSupport.Unsupported,
        AssuranceAttemptStatus.InvalidObservations => InterpretationSupport.Undetermined,
        _ => InterpretationSupport.Supported
    };

    static IEnumerable<ClaimReference> TraceReferences(TraceEvaluation evaluation)
    {
        System.Diagnostics.Debug.Assert(evaluation.Status == AssuranceAttemptStatus.Completed,
            "Selector dependency shortcut requires fully validated completed observations.");
        var batch = evaluation.Batch;
        yield return batch.Contract.Reference;
        yield return batch.Contract.Scope;
        yield return batch.Epoch;
        yield return batch.EnumerationEvidence;
        foreach (var requirement in batch.Contract.Requirements) yield return requirement.Selector;
        foreach (var alternative in batch.Alternatives)
        {
            yield return alternative.Reference;
            // Completed reference evaluation already checked every observation selector against
            // Contract.Requirements. Enumerating those declarations above covers every row without rescanning it.
            foreach (var frontier in alternative.Completeness)
            {
                yield return frontier.Selector;
                yield return frontier.Evidence;
            }
        }
    }

    internal static void ValidateSubject(EvidenceSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!Enum.IsDefined(subject.Kind) || !Enum.IsDefined(subject.Obligation)
            || subject.Target is null || subject.Configuration is null || subject.Scope is null)
            throw new ArgumentException("An exact target, configuration, declared scope and obligation are required.", nameof(subject));
    }
}
