using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Deterministic fingerprint of one exact physical-applicability realization candidate.</summary>
public sealed record InfrastructureRealizationFingerprint
{
    /// <summary>Digest algorithm used by the current realization fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current realization fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-realization/v1-c14n/v4";

    /// <summary>Creates realization fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureRealizationFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Stable digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Stable canonicalization-profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Payload-free exact reference to one infrastructure realization candidate.</summary>
public sealed record InfrastructureRealizationReference
{
    /// <summary>Creates an exact realization reference.</summary>
    /// <param name="definition">Exact canonical definition reference.</param>
    /// <param name="profile">Exact capability-profile reference.</param>
    /// <param name="target">Selected interpretation target.</param>
    /// <param name="variant">Selected coherent target variant.</param>
    /// <param name="fingerprint">Exact realization fingerprint.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A target or variant identity is default.</exception>
    [JsonConstructor]
    public InfrastructureRealizationReference(
        InfrastructureDefinitionReference definition,
        InfrastructureCapabilityProfileReference profile,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        InfrastructureRealizationFingerprint fingerprint)
    {
        Definition = Guard.RequireNotNull(definition);
        Profile = Guard.RequireNotNull(profile);
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("An infrastructure-realization reference requires a target.", nameof(target));
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("An infrastructure-realization reference requires a variant.", nameof(variant));

        Target = target;
        Variant = variant;
        Fingerprint = Guard.RequireNotNull(fingerprint);
    }

    /// <summary>Exact canonical definition reference.</summary>
    public InfrastructureDefinitionReference Definition { get; }

    /// <summary>Exact capability-profile reference.</summary>
    public InfrastructureCapabilityProfileReference Profile { get; }

    /// <summary>Selected interpretation target.</summary>
    public InfrastructureTargetId Target { get; }

    /// <summary>Selected coherent target variant.</summary>
    public InfrastructureCapabilityVariantId Variant { get; }

    /// <summary>Exact realization fingerprint.</summary>
    public InfrastructureRealizationFingerprint Fingerprint { get; }
}

/// <summary>Exactly fingerprinted physical-applicability candidate for one infrastructure definition.</summary>
/// <remarks>
/// This record joins target-strategy capability closure to selected workload deployments, logical-resource lifecycle
/// identities, and demand-scoped physical evidence. It is still not deployment authority: backend artifacts,
/// previews, receipts, observed state, and drift remain separate later interpretations.
/// </remarks>
public sealed record InfrastructureRealization
{
    /// <summary>Creates or restores an exact infrastructure realization candidate.</summary>
    /// <param name="capabilityClosure">Exact target-strategy capability-closure report.</param>
    /// <param name="lifecycle">Physical resource identities and lifecycle ownership partition.</param>
    /// <param name="workloadPlacements">Selected physical deployment resources for logical workloads.</param>
    /// <param name="readinessObligations">Canonical readiness dependencies lowered to exact physical resources.</param>
    /// <param name="capabilityWitnesses">Demand-scoped applicability witnesses for selected capability evidence.</param>
    /// <param name="witnessDecisions">One derived physical-applicability decision per exact capability demand.</param>
    /// <param name="diagnostics">Structured witness diagnostics in deterministic order.</param>
    /// <param name="fingerprint">Persisted exact realization fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="capabilityClosure"/> or <paramref name="lifecycle"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Inputs reference different definitions; a collection is malformed; derived decisions or diagnostics differ
    /// from the authoritative evaluation; or <paramref name="fingerprint"/> is not canonical.
    /// </exception>
    [JsonConstructor]
    public InfrastructureRealization(
        InfrastructureCapabilityClosureReport capabilityClosure,
        InfrastructureLifecyclePlan lifecycle,
        ImmutableArray<InfrastructureWorkloadPlacement> workloadPlacements,
        ImmutableArray<InfrastructureReadinessObligation> readinessObligations,
        ImmutableArray<InfrastructureCapabilityEvidenceWitness> capabilityWitnesses,
        ImmutableArray<InfrastructureCapabilityWitnessDecision> witnessDecisions,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        InfrastructureRealizationFingerprint? fingerprint = null)
    {
        CapabilityClosure = Guard.RequireNotNull(capabilityClosure);
        Lifecycle = Guard.RequireNotNull(lifecycle);
        if (CapabilityClosure.Definition != Lifecycle.Definition)
        {
            throw new ArgumentException(
                "Infrastructure capability closure and lifecycle ownership must reference the same exact definition.",
                nameof(lifecycle));
        }

        WorkloadPlacements = InfrastructureCapabilityWitnessCollections.NormalizePlacements(workloadPlacements);
        ReadinessObligations = InfrastructureReadinessObligationCompiler.Normalize(readinessObligations);
        CapabilityWitnesses = InfrastructureCapabilityWitnessCollections.NormalizeWitnesses(capabilityWitnesses);
        WitnessDecisions = InfrastructureCapabilityWitnessCollections.NormalizeDecisions(witnessDecisions);
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);

        var evaluation = InfrastructureCapabilityWitnessEvaluator.Evaluate(
            CapabilityClosure,
            Lifecycle,
            WorkloadPlacements,
            CapabilityWitnesses);
        if (!WitnessDecisions.SequenceEqual(evaluation.Decisions))
            throw new ArgumentException("Capability-witness decisions do not match the exact realization inputs.", nameof(witnessDecisions));
        if (!Diagnostics.SequenceEqual(evaluation.Diagnostics))
            throw new ArgumentException("Capability-witness diagnostics do not match the exact realization inputs.", nameof(diagnostics));

        var expectedReadiness = InfrastructureReadinessObligationCompiler.Compile(
            CapabilityClosure.Definition.Definition,
            Lifecycle,
            WorkloadPlacements);
        if (!ReadinessObligations.SequenceEqual(expectedReadiness))
        {
            throw new ArgumentException(
                "Readiness obligations do not match the canonical definition and exact physical placements.",
                nameof(readinessObligations));
        }

        var computed = InfrastructureRealizationFingerprinting.Compute(
            CapabilityClosure,
            Lifecycle,
            WorkloadPlacements,
            ReadinessObligations,
            CapabilityWitnesses,
            WitnessDecisions);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied infrastructure-realization fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact target-strategy capability-closure report.</summary>
    public InfrastructureCapabilityClosureReport CapabilityClosure { get; }

    /// <summary>Physical resource identities and lifecycle ownership partition.</summary>
    public InfrastructureLifecyclePlan Lifecycle { get; }

    /// <summary>Selected physical workload deployment resources in workload-identity order.</summary>
    public ImmutableArray<InfrastructureWorkloadPlacement> WorkloadPlacements { get; }

    /// <summary>Canonical readiness dependencies lowered to exact physical resources.</summary>
    public ImmutableArray<InfrastructureReadinessObligation> ReadinessObligations { get; }

    /// <summary>Demand-scoped applicability witnesses in requirement-then-evidence order.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidenceWitness> CapabilityWitnesses { get; }

    /// <summary>One derived physical-applicability decision per exact capability demand.</summary>
    public ImmutableArray<InfrastructureCapabilityWitnessDecision> WitnessDecisions { get; }

    /// <summary>Structured witness diagnostics in deterministic portable-document order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Exact fingerprint of closure, lifecycle, placements, witnesses, and derived decisions.</summary>
    public InfrastructureRealizationFingerprint Fingerprint { get; }

    /// <summary>Whether capability closure and every physical-applicability witness are complete.</summary>
    /// <remarks>This is not deployment readiness; backend artifacts and receipts are outside this model.</remarks>
    [JsonIgnore]
    public bool IsCapabilityWitnessComplete =>
        CapabilityClosure.IsClosed
        && WitnessDecisions.All(static decision => decision.IsComplete)
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>Whether every canonical readiness dependency has an exact physical obligation.</summary>
    [JsonIgnore]
    public bool IsReadinessObligationComplete =>
        ReadinessObligations.Length == CapabilityClosure.Definition.Definition.ReadinessDependencies.Length;

    /// <summary>Projects a payload-free exact reference to this realization.</summary>
    /// <returns>The exact definition, profile, target, variant, and fingerprint fence.</returns>
    public InfrastructureRealizationReference ToReference() => new(
        definition: Lifecycle.Definition.ToReference(),
        profile: CapabilityClosure.Profile,
        target: CapabilityClosure.Target,
        variant: CapabilityClosure.Variant,
        fingerprint: Fingerprint);

    /// <summary>Finds the physical-applicability decision for one exact requirement.</summary>
    /// <param name="requirement">Exact declared or binding-derived requirement identity.</param>
    /// <returns>The matching decision, or <see langword="null"/> when absent.</returns>
    /// <exception cref="ArgumentException"><paramref name="requirement"/> is a default identity.</exception>
    public InfrastructureCapabilityWitnessDecision? FindWitnessDecision(InfrastructureRequirementId requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A default requirement identity cannot be explained.", nameof(requirement));

        var index = CanonicalDocumentCollections.BinarySearchIndex(
            WitnessDecisions,
            requirement,
            static (decision, sought) =>
                StringComparer.Ordinal.Compare(decision.Requirement.Value, sought.Value));
        return index < 0 ? null : WitnessDecisions[index];
    }

    /// <summary>Compares realization candidates structurally.</summary>
    /// <param name="other">Other realization.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureRealization? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && CapabilityClosure == other.CapabilityClosure
        && Lifecycle == other.Lifecycle
        && WorkloadPlacements.SequenceEqual(other.WorkloadPlacements)
        && ReadinessObligations.SequenceEqual(other.ReadinessObligations)
        && CapabilityWitnesses.SequenceEqual(other.CapabilityWitnesses)
        && WitnessDecisions.SequenceEqual(other.WitnessDecisions)
        && Diagnostics.SequenceEqual(other.Diagnostics)
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for this realization candidate.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CapabilityClosure);
        hash.Add(Lifecycle);
        Add(ref hash, WorkloadPlacements);
        Add(ref hash, ReadinessObligations);
        Add(ref hash, CapabilityWitnesses);
        Add(ref hash, WitnessDecisions);
        Add(ref hash, Diagnostics);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    static void Add<T>(ref HashCode hash, ImmutableArray<T> values)
    {
        foreach (var value in values)
            hash.Add(value);
    }
}
