using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Stable diagnostics emitted while checking infrastructure capability closure.</summary>
public static class InfrastructureCapabilityDiagnosticCodes
{
    /// <summary>The capability profile uses a schema unsupported by this compiler.</summary>
    public const string ProfileSchemaUnsupported = "infra.capabilities.profile.schemaUnsupported";

    /// <summary>The target profile does not understand the supplied infrastructure-definition schema.</summary>
    public const string DefinitionSchemaUnsupported = "infra.capabilities.definition.schemaUnsupported";

    /// <summary>The selected coherent target variant is absent from the supplied profile.</summary>
    public const string VariantUnavailable = "infra.capabilities.variant.unavailable";

    /// <summary>No evidence or complete composition rule preserves one exact requirement.</summary>
    public const string RequirementUnavailable = "infra.capabilities.requirement.unavailable";

    /// <summary>A constrained proof requires exact environment-policy acceptance before it can close.</summary>
    public const string OperatingBoundaryAcceptanceRequired = "infra.capabilities.boundary.acceptanceRequired";

    /// <summary>The supplied boundary-acceptance policy uses a schema unsupported by this compiler.</summary>
    public const string BoundaryAcceptancePolicySchemaUnsupported = "infra.capabilities.boundary.policySchemaUnsupported";

    /// <summary>A boundary acceptance names a requirement absent from the exact compiled demand set.</summary>
    public const string BoundaryAcceptanceRequirementUnknown = "infra.capabilities.boundary.requirementUnknown";

    /// <summary>A boundary acceptance does not belong to the selected constrained proof.</summary>
    public const string BoundaryAcceptanceUnexpected = "infra.capabilities.boundary.unexpectedAcceptance";

    /// <summary>The supplied policy is fenced to a different exact compiler authority.</summary>
    public const string BoundaryAcceptancePolicyFenceMismatch = "infra.capabilities.boundary.policyFenceMismatch";

    /// <summary>Several valid proofs preserve one requirement and no policy selected between them.</summary>
    public const string RequirementAmbiguous = "infra.capabilities.requirement.ambiguous";

    /// <summary>Capability-composition rules contain a recursive proof cycle.</summary>
    public const string CompositionCycle = "infra.capabilities.composition.cycle";

    /// <summary>Composed capability evidence contains a recursive auxiliary-evidence cycle.</summary>
    public const string EvidenceCycle = "infra.capabilities.evidence.cycle";
}

/// <summary>One exact evidence-backed, unavailable, or unresolved decision for an infrastructure requirement.</summary>
public sealed record InfrastructureCapabilityDecision
{
    /// <summary>Creates a capability-closure decision.</summary>
    /// <param name="requirement">Exact definition-local requirement identity.</param>
    /// <param name="capability">Canonical requirement-shaped capability.</param>
    /// <param name="realization">Native, composed, constrained, override, unavailable, or unknown classification.</param>
    /// <param name="evidence">Transitive capability evidence identities.</param>
    /// <param name="rules">Transitive capability-composition rule identities.</param>
    /// <param name="operatingBoundaries">Transitive operating-boundary identities.</param>
    /// <param name="preservedGuarantees">Guarantee capabilities explicitly preserved by selected rules.</param>
    /// <param name="acceptedOperatingBoundaries">Demand-scoped boundaries accepted by exact policy.</param>
    /// <param name="missingOperatingBoundaries">Selected boundaries still requiring acceptance.</param>
    /// <exception cref="ArgumentException">An identity, collection, or realization-specific invariant is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="realization"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityDecision(
        InfrastructureRequirementId requirement,
        InfrastructureCapabilityId capability,
        CapabilityRealizationKind realization,
        ImmutableArray<InfrastructureCapabilityEvidenceId> evidence = default,
        ImmutableArray<InfrastructureCapabilityRuleId> rules = default,
        ImmutableArray<InfrastructureOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<InfrastructureCapabilityId> preservedGuarantees = default,
        ImmutableArray<InfrastructureOperatingBoundaryId> acceptedOperatingBoundaries = default,
        ImmutableArray<InfrastructureOperatingBoundaryId> missingOperatingBoundaries = default)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("An infrastructure capability decision requires a requirement identity.", nameof(requirement));
        if (string.IsNullOrWhiteSpace(capability.Value))
            throw new ArgumentException("An infrastructure capability decision requires a capability identity.", nameof(capability));
        if (!Enum.IsDefined(realization))
            throw new ArgumentOutOfRangeException(nameof(realization), realization, "Unsupported capability realization kind.");

        Requirement = requirement;
        Capability = capability;
        Realization = realization;
        Evidence = InfrastructureCapabilityCollections.IdentitySet(
            evidence,
            static identity => identity.Value,
            nameof(evidence));
        Rules = InfrastructureCapabilityCollections.IdentitySet(
            rules,
            static identity => identity.Value,
            nameof(rules));
        OperatingBoundaries = InfrastructureCapabilityCollections.IdentitySet(
            operatingBoundaries,
            static identity => identity.Value,
            nameof(operatingBoundaries));
        PreservedGuarantees = InfrastructureCapabilityCollections.IdentitySet(
            preservedGuarantees,
            static identity => identity.Value,
            nameof(preservedGuarantees));
        AcceptedOperatingBoundaries = InfrastructureCapabilityCollections.IdentitySet(
            acceptedOperatingBoundaries,
            static identity => identity.Value,
            nameof(acceptedOperatingBoundaries));
        MissingOperatingBoundaries = acceptedOperatingBoundaries.IsDefault
            && missingOperatingBoundaries.IsDefault
            && realization == CapabilityRealizationKind.Constrained
                ? OperatingBoundaries
                : InfrastructureCapabilityCollections.IdentitySet(
                    missingOperatingBoundaries,
                    static identity => identity.Value,
                    nameof(missingOperatingBoundaries));

        var available = realization is not CapabilityRealizationKind.Unavailable and not CapabilityRealizationKind.Unknown;
        if (available && Evidence.IsDefaultOrEmpty)
            throw new ArgumentException("An available capability decision requires evidence.", nameof(evidence));
        if (!available
            && (!Evidence.IsDefaultOrEmpty
                || !Rules.IsDefaultOrEmpty
                || !OperatingBoundaries.IsDefaultOrEmpty
                || !PreservedGuarantees.IsDefaultOrEmpty))
        {
            throw new ArgumentException("Unavailable or unknown decisions cannot claim proof evidence.", nameof(realization));
        }
        if (realization == CapabilityRealizationKind.Native
            && (!Rules.IsDefaultOrEmpty || !OperatingBoundaries.IsDefaultOrEmpty))
        {
            throw new ArgumentException("Native capability decisions cannot claim rules or operating boundaries.", nameof(realization));
        }
        if (realization == CapabilityRealizationKind.Composed && Rules.IsDefaultOrEmpty && Evidence.Length < 2)
            throw new ArgumentException("Composed capability decisions require a rule or several evidence assertions.", nameof(realization));
        if (realization == CapabilityRealizationKind.Constrained && OperatingBoundaries.IsDefaultOrEmpty)
            throw new ArgumentException("Constrained capability decisions require an operating boundary.", nameof(realization));
        if (realization != CapabilityRealizationKind.Constrained
            && (!AcceptedOperatingBoundaries.IsDefaultOrEmpty || !MissingOperatingBoundaries.IsDefaultOrEmpty))
        {
            throw new ArgumentException(
                "Only constrained capability decisions can claim boundary-acceptance policy.",
                nameof(realization));
        }
        if (realization == CapabilityRealizationKind.Constrained
            && (AcceptedOperatingBoundaries.Any(boundary => !OperatingBoundaries.Contains(boundary))
                || MissingOperatingBoundaries.Any(boundary => !OperatingBoundaries.Contains(boundary))
                || AcceptedOperatingBoundaries.Any(MissingOperatingBoundaries.Contains)
                || OperatingBoundaries.Any(boundary =>
                    !AcceptedOperatingBoundaries.Contains(boundary) && !MissingOperatingBoundaries.Contains(boundary))))
        {
            throw new ArgumentException(
                "Accepted and missing operating boundaries must exactly partition the constrained proof boundaries.",
                nameof(acceptedOperatingBoundaries));
        }
    }

    /// <summary>Exact definition-local requirement identity.</summary>
    public InfrastructureRequirementId Requirement { get; }

    /// <summary>Canonical requirement-shaped capability.</summary>
    public InfrastructureCapabilityId Capability { get; }

    /// <summary>Selected realization classification.</summary>
    public CapabilityRealizationKind Realization { get; }

    /// <summary>Transitive evidence identities in ordinal order.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidenceId> Evidence { get; }

    /// <summary>Transitive composition-rule identities in ordinal order.</summary>
    public ImmutableArray<InfrastructureCapabilityRuleId> Rules { get; }

    /// <summary>Transitive operating-boundary identities in ordinal order.</summary>
    public ImmutableArray<InfrastructureOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Guarantee capabilities explicitly preserved by selected rules in ordinal order.</summary>
    public ImmutableArray<InfrastructureCapabilityId> PreservedGuarantees { get; }

    /// <summary>Demand-scoped operating boundaries accepted by exact policy in ordinal order.</summary>
    public ImmutableArray<InfrastructureOperatingBoundaryId> AcceptedOperatingBoundaries { get; }

    /// <summary>Selected operating boundaries still requiring exact acceptance in ordinal order.</summary>
    public ImmutableArray<InfrastructureOperatingBoundaryId> MissingOperatingBoundaries { get; }

    /// <summary>Whether the target strategy supplies proof evidence, independent of boundary acceptance.</summary>
    public bool IsAvailable => Realization is not CapabilityRealizationKind.Unavailable and not CapabilityRealizationKind.Unknown;

    /// <summary>Whether proof evidence is available and every constrained boundary is accepted.</summary>
    public bool IsAdmissible => IsAvailable && MissingOperatingBoundaries.IsDefaultOrEmpty;

    /// <summary>Compares capability decisions structurally.</summary>
    /// <param name="other">Other decision.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureCapabilityDecision? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Requirement == other.Requirement
        && Capability == other.Capability
        && Realization == other.Realization
        && Evidence.SequenceEqual(other.Evidence)
        && Rules.SequenceEqual(other.Rules)
        && OperatingBoundaries.SequenceEqual(other.OperatingBoundaries)
        && PreservedGuarantees.SequenceEqual(other.PreservedGuarantees)
        && AcceptedOperatingBoundaries.SequenceEqual(other.AcceptedOperatingBoundaries)
        && MissingOperatingBoundaries.SequenceEqual(other.MissingOperatingBoundaries);

    /// <summary>Returns a structural hash code for this decision.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Requirement);
        hash.Add(Capability);
        hash.Add(Realization);
        foreach (var item in Evidence)
            hash.Add(item);
        foreach (var item in Rules)
            hash.Add(item);
        foreach (var item in OperatingBoundaries)
            hash.Add(item);
        foreach (var item in PreservedGuarantees)
            hash.Add(item);
        foreach (var item in AcceptedOperatingBoundaries)
            hash.Add(item);
        foreach (var item in MissingOperatingBoundaries)
            hash.Add(item);
        return hash.ToHashCode();
    }
}

/// <summary>Capability-planning closure report for one exact definition and coherent target variant.</summary>
/// <remarks>
/// Decisions prove target-strategy availability, not applicability to a selected physical instance. Structured
/// diagnostics retain binding and boundary residuals; a backend interpreter must later materialize physical witnesses.
/// </remarks>
public sealed record InfrastructureCapabilityClosureReport
{
    /// <summary>Creates a capability-closure report.</summary>
    /// <param name="profile">Exact selected capability-profile reference.</param>
    /// <param name="bindingElaboration">Exact binding obligations and per-binding explanation decisions.</param>
    /// <param name="target">Stable interpretation-target identity.</param>
    /// <param name="variant">Selected coherent target variant.</param>
    /// <param name="decisions">One decision for every declared or binding-derived requirement.</param>
    /// <param name="diagnostics">Structured capability-closure diagnostics.</param>
    /// <param name="boundaryAcceptancePolicy">Exact policy used to accept constrained boundaries, when supplied.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="profile"/> or <paramref name="bindingElaboration"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity, decision collection, or coverage invariant is invalid.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityClosureReport(
        InfrastructureCapabilityProfileReference profile,
        InfrastructureBindingElaborationReport bindingElaboration,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<InfrastructureCapabilityDecision> decisions = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default,
        InfrastructureBoundaryAcceptancePolicyReference? boundaryAcceptancePolicy = null)
    {
        Profile = Guard.RequireNotNull(profile);
        BindingElaboration = Guard.RequireNotNull(bindingElaboration);
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("A capability-closure report requires a target identity.", nameof(target));
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("A capability-closure report requires a coherent variant identity.", nameof(variant));

        Target = target;
        Variant = variant;
        BoundaryAcceptancePolicy = boundaryAcceptancePolicy;
        Decisions = NormalizeDecisions(decisions);
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
        ValidateCoverage();
        ValidatePolicyFence();
    }

    /// <summary>Exact schema, identity, and fingerprint of the selected capability profile.</summary>
    public InfrastructureCapabilityProfileReference Profile { get; }

    /// <summary>Exact binding obligations and machine-readable per-binding explanation decisions.</summary>
    public InfrastructureBindingElaborationReport BindingElaboration { get; }

    /// <summary>Exact fingerprinted infrastructure definition owned by the binding-elaboration stage.</summary>
    [JsonIgnore]
    public InfrastructureDefinitionDocument Definition => BindingElaboration.Definition;

    /// <summary>Selected interpretation-target identity.</summary>
    public InfrastructureTargetId Target { get; }

    /// <summary>Selected coherent target variant.</summary>
    public InfrastructureCapabilityVariantId Variant { get; }

    /// <summary>Exact policy used to accept constrained operating boundaries, when supplied.</summary>
    public InfrastructureBoundaryAcceptancePolicyReference? BoundaryAcceptancePolicy { get; }

    /// <summary>One decision per declared or binding-derived requirement in requirement-identity order.</summary>
    public ImmutableArray<InfrastructureCapabilityDecision> Decisions { get; }

    /// <summary>Structured diagnostics in deterministic portable-document order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether every requirement is available and no error diagnostic remains.</summary>
    [JsonIgnore]
    public bool IsClosed =>
        BindingElaboration.IsComplete
        && Decisions.All(static decision => decision.IsAdmissible)
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>Finds the capability decision for one declared or binding-derived requirement.</summary>
    /// <param name="requirement">Exact definition-local requirement identity.</param>
    /// <returns>The matching decision, or <see langword="null"/> when the requirement is absent.</returns>
    /// <exception cref="ArgumentException"><paramref name="requirement"/> is a default identity.</exception>
    public InfrastructureCapabilityDecision? FindDecision(InfrastructureRequirementId requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A default requirement identity cannot be explained.", nameof(requirement));

        var index = CanonicalDocumentCollections.BinarySearchIndex(
            Decisions,
            requirement,
            static (decision, sought) =>
                StringComparer.Ordinal.Compare(decision.Requirement.Value, sought.Value));
        return index < 0 ? null : Decisions[index];
    }

    /// <summary>Compares capability-closure reports structurally.</summary>
    /// <param name="other">Other report.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureCapabilityClosureReport? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Profile == other.Profile
        && BindingElaboration == other.BindingElaboration
        && Target == other.Target
        && Variant == other.Variant
        && BoundaryAcceptancePolicy == other.BoundaryAcceptancePolicy
        && Decisions.SequenceEqual(other.Decisions)
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code for this report.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Profile);
        hash.Add(BindingElaboration);
        hash.Add(Target);
        hash.Add(Variant);
        hash.Add(BoundaryAcceptancePolicy);
        foreach (var decision in Decisions)
            hash.Add(decision);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        return hash.ToHashCode();
    }

    void ValidateCoverage()
    {
        var expected = Definition.Definition.Workloads
            .SelectMany(static workload => workload.Requirements)
            .Concat(Definition.Definition.Resources.SelectMany(static resource => resource.Requirements))
            .Concat(BindingElaboration.Obligations.Select(static obligation => obligation.Requirement))
            .OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (expected.Length != Decisions.Length)
            throw new ArgumentException(
                "A capability-closure report requires one decision for every declared or binding-derived requirement.",
                nameof(Decisions));

        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index].Id != Decisions[index].Requirement
                || expected[index].Capability != Decisions[index].Capability)
            {
                throw new ArgumentException(
                    $"Capability decision '{Decisions[index].Requirement.Value}' does not match the exact compiled requirement.",
                    nameof(Decisions));
            }
        }
    }

    void ValidatePolicyFence()
    {
        if (BoundaryAcceptancePolicy is null)
            return;
        if (BoundaryAcceptancePolicy.Definition != Definition.ToReference()
            || BoundaryAcceptancePolicy.Profile != Profile
            || BoundaryAcceptancePolicy.BindingProfile != BindingElaboration.Profile
            || BoundaryAcceptancePolicy.Target != Target
            || BoundaryAcceptancePolicy.Variant != Variant)
        {
            throw new ArgumentException(
                "The boundary-acceptance policy reference does not match the exact capability-closure fence.",
                nameof(BoundaryAcceptancePolicy));
        }
    }

    static ImmutableArray<InfrastructureCapabilityDecision> NormalizeDecisions(
        ImmutableArray<InfrastructureCapabilityDecision> decisions)
    {
        if (decisions.IsDefaultOrEmpty)
            return [];
        if (decisions.Any(static decision => decision is null))
            throw new ArgumentException("Infrastructure capability decisions cannot contain null.", nameof(decisions));

        var ordered = decisions.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Requirement.Value, right.Requirement.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Requirement == ordered[index].Requirement)
                throw new ArgumentException($"Capability decision '{ordered[index].Requirement.Value}' is duplicated.", nameof(decisions));
        }
        return ordered;
    }

}

/// <summary>Computes capability-planning closure against one coherent configured target variant.</summary>
public static class InfrastructureCapabilityCompiler
{
    const string ProfileSelectionStage = "infrastructure-capability-profile-selection";
    const string CapabilityMatchingStage = "infrastructure-capability-matching";

    /// <summary>Compiles one exact infrastructure definition against a selected coherent target variant.</summary>
    /// <param name="definition">Exact fingerprinted infrastructure definition.</param>
    /// <param name="profile">Versioned target capability profile.</param>
    /// <param name="variant">Exact coherent target variant to use; evidence from other variants is excluded.</param>
    /// <returns>One exact decision per requirement and structured closure diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="variant"/> is a default uninitialized identity.</exception>
    public static InfrastructureCapabilityClosureReport Compile(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariantId variant) =>
        Compile(definition, profile, variant, InfrastructureBindingElaborationProfile.Empty);

    /// <summary>Compiles one exact definition using attributable constrained-boundary acceptance policy.</summary>
    /// <param name="definition">Exact fingerprinted infrastructure definition.</param>
    /// <param name="profile">Versioned target capability profile.</param>
    /// <param name="variant">Exact coherent target variant.</param>
    /// <param name="boundaryAcceptancePolicy">Exact demand-scoped operating-boundary acceptance policy.</param>
    /// <returns>One exact decision per requirement and structured closure diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The variant is default.</exception>
    public static InfrastructureCapabilityClosureReport Compile(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariantId variant,
        InfrastructureBoundaryAcceptancePolicy boundaryAcceptancePolicy) =>
        Compile(
            definition,
            profile,
            variant,
            InfrastructureBindingElaborationProfile.Empty,
            boundaryAcceptancePolicy);

    /// <summary>
    /// Compiles one exact infrastructure definition and its binding-induced obligations against a coherent target.
    /// </summary>
    /// <param name="definition">Exact fingerprinted infrastructure definition.</param>
    /// <param name="profile">Versioned target capability profile.</param>
    /// <param name="variant">Exact coherent target variant to use; evidence from other variants is excluded.</param>
    /// <param name="bindingElaborationProfile">Exact provider-neutral rules used to elaborate binding contracts.</param>
    /// <returns>One exact decision per declared or binding-derived requirement and structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="profile"/>, or <paramref name="bindingElaborationProfile"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="variant"/> is a default uninitialized identity.</exception>
    public static InfrastructureCapabilityClosureReport Compile(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariantId variant,
        InfrastructureBindingElaborationProfile bindingElaborationProfile) =>
        CompileCore(
            definition,
            profile,
            variant,
            bindingElaborationProfile,
            boundaryAcceptancePolicy: null,
            selectedEvidence: null);

    /// <summary>
    /// Compiles exact declared and binding-induced requirements using attributable boundary-acceptance policy.
    /// </summary>
    /// <param name="definition">Exact fingerprinted infrastructure definition.</param>
    /// <param name="profile">Versioned target capability profile.</param>
    /// <param name="variant">Exact coherent target variant.</param>
    /// <param name="bindingElaborationProfile">Exact rules used to elaborate binding contracts.</param>
    /// <param name="boundaryAcceptancePolicy">Exact demand-scoped operating-boundary acceptance policy.</param>
    /// <returns>One exact decision per declared or binding-derived requirement and structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The variant is default.</exception>
    public static InfrastructureCapabilityClosureReport Compile(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariantId variant,
        InfrastructureBindingElaborationProfile bindingElaborationProfile,
        InfrastructureBoundaryAcceptancePolicy boundaryAcceptancePolicy)
    {
        ArgumentNullException.ThrowIfNull(boundaryAcceptancePolicy);
        return CompileCore(
            definition,
            profile,
            variant,
            bindingElaborationProfile,
            boundaryAcceptancePolicy,
            selectedEvidence: null);
    }

    internal static InfrastructureCapabilityClosureReport CompileWithEvidenceSelection(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariantId variant,
        InfrastructureBindingElaborationProfile bindingElaborationProfile,
        ImmutableArray<InfrastructureCapabilityEvidenceId> selectedEvidence,
        InfrastructureBoundaryAcceptancePolicy? boundaryAcceptancePolicy)
    {
        if (selectedEvidence.IsDefault)
            throw new ArgumentException("Selected capability evidence cannot be default.", nameof(selectedEvidence));
        return CompileCore(
            definition,
            profile,
            variant,
            bindingElaborationProfile,
            boundaryAcceptancePolicy,
            selectedEvidence.ToHashSet());
    }

    static InfrastructureCapabilityClosureReport CompileCore(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariantId variant,
        InfrastructureBindingElaborationProfile bindingElaborationProfile,
        InfrastructureBoundaryAcceptancePolicy? boundaryAcceptancePolicy,
        IReadOnlySet<InfrastructureCapabilityEvidenceId>? selectedEvidence)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(bindingElaborationProfile);
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("Infrastructure capability compilation requires a coherent variant identity.", nameof(variant));
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        var boundaryAcceptancePolicyFenceMatches = boundaryAcceptancePolicy is null
            || AddBoundaryAcceptancePolicyFenceDiagnostics(
                definition,
                profile,
                variant,
                bindingElaborationProfile,
                boundaryAcceptancePolicy,
                diagnostics);
        var boundaryAcceptancePolicySupported = boundaryAcceptancePolicy is null
            || string.Equals(
                boundaryAcceptancePolicy.SchemaVersion,
                InfrastructureBoundaryAcceptancePolicy.CurrentSchemaVersion,
                StringComparison.Ordinal);
        if (!boundaryAcceptancePolicySupported)
        {
            diagnostics.Add(new(
                InfrastructureCapabilityDiagnosticCodes.BoundaryAcceptancePolicySchemaUnsupported,
                DiagnosticSeverity.Error,
                $"Boundary-acceptance policy schema '{boundaryAcceptancePolicy!.SchemaVersion}' is unsupported; expected '{InfrastructureBoundaryAcceptancePolicy.CurrentSchemaVersion}'.",
                Location: "/schemaVersion",
                Evidence: new(
                    stage: CapabilityMatchingStage,
                    subject: boundaryAcceptancePolicy.Id.Value,
                    sourceReferences: [InfrastructureDiagnosticReferences.BoundaryAcceptancePolicy(boundaryAcceptancePolicy)],
                    resolutionOptions: ["Select an exact boundary-acceptance policy using a schema supported by this compiler."],
                    expected: InfrastructureBoundaryAcceptancePolicy.CurrentSchemaVersion,
                    observed: boundaryAcceptancePolicy.SchemaVersion)));
        }
        var capabilitySchemasSupported = true;
        if (!string.Equals(
                profile.SchemaVersion,
                InfrastructureCapabilityProfile.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            capabilitySchemasSupported = false;
            diagnostics.Add(new(
                InfrastructureCapabilityDiagnosticCodes.ProfileSchemaUnsupported,
                DiagnosticSeverity.Error,
                $"Capability profile schema '{profile.SchemaVersion}' is unsupported; expected '{InfrastructureCapabilityProfile.CurrentSchemaVersion}'.",
                Location: "/schemaVersion",
                Evidence: new(
                    stage: ProfileSelectionStage,
                    subject: profile.Id.Value,
                    sourceReferences: [InfrastructureDiagnosticReferences.CapabilityProfile(profile)],
                    resolutionOptions: ["Select an exact capability profile using a schema supported by this compiler."],
                    expected: InfrastructureCapabilityProfile.CurrentSchemaVersion,
                    observed: profile.SchemaVersion)));
        }
        if (!profile.SupportedDefinitionSchemaVersions.Contains(definition.SchemaVersion, StringComparer.Ordinal))
        {
            capabilitySchemasSupported = false;
            diagnostics.Add(new(
                InfrastructureCapabilityDiagnosticCodes.DefinitionSchemaUnsupported,
                DiagnosticSeverity.Error,
                $"Target '{profile.Target.Value}' does not support infrastructure definition schema '{definition.SchemaVersion}'.",
                Location: "/schemaVersion",
                Evidence: new(
                    stage: ProfileSelectionStage,
                    subject: $"{definition.Definition.Id.Value}@{definition.Definition.Revision.Value}",
                    sourceReferences:
                    [
                        InfrastructureDiagnosticReferences.Definition(definition),
                        InfrastructureDiagnosticReferences.CapabilityProfile(profile)
                    ],
                    resolutionOptions:
                    [
                        "Select a capability profile that supports the exact infrastructure-definition schema.",
                        "Recompile the definition through a supported schema migration."
                    ],
                    expected: string.Join(", ", profile.SupportedDefinitionSchemaVersions),
                    observed: definition.SchemaVersion)));
        }

        var selected = profile.FindVariant(variant);
        if (selected is not null && selectedEvidence is not null)
        {
            selected = new(
                selected.Id,
                selected.Evidence.Where(evidence => selectedEvidence.Contains(evidence.Id)).ToImmutableArray(),
                selected.Rules,
                selected.OperatingBoundaries);
        }
        if (selected is null)
        {
            diagnostics.Add(new(
                InfrastructureCapabilityDiagnosticCodes.VariantUnavailable,
                DiagnosticSeverity.Error,
                $"Capability variant '{variant.Value}' is unavailable in profile '{profile.Id.Value}'.",
                Location: "/variants",
                Evidence: new(
                    stage: ProfileSelectionStage,
                    subject: variant.Value,
                    sourceReferences: [InfrastructureDiagnosticReferences.CapabilityProfile(profile)],
                    resolutionOptions: ["Select one coherent target variant advertised by the exact capability profile."],
                    expected: "a coherent variant advertised by the exact capability profile",
                    observed: "variant not advertised")));
        }

        var bindingElaboration = InfrastructureBindingElaborator.Elaborate(definition, bindingElaborationProfile);
        diagnostics.AddRange(bindingElaboration.Diagnostics);
        var requirements = RequirementSites(definition, bindingElaboration);
        var decisions = ImmutableArray.CreateBuilder<InfrastructureCapabilityDecision>(requirements.Length);

        foreach (var site in requirements)
        {
            var requirement = site.Requirement;
            if (selected is null || !capabilitySchemasSupported)
            {
                decisions.Add(new(
                    requirement.Id,
                    requirement.Capability,
                    CapabilityRealizationKind.Unknown));
                continue;
            }

            var resolution = CapabilityResolver.Resolve(selected, requirement.Capability);
            var acceptedBoundaries = resolution.Proof is { Realization: CapabilityRealizationKind.Constrained } proof
                && boundaryAcceptancePolicySupported
                && boundaryAcceptancePolicyFenceMatches
                && boundaryAcceptancePolicy is not null
                    ? proof.OperatingBoundaries
                        .Where(boundary => boundaryAcceptancePolicy.FindAcceptance(requirement.Id, boundary) is not null)
                        .ToImmutableArray()
                    : [];
            var decision = ToDecision(requirement, resolution, acceptedBoundaries);
            decisions.Add(decision);
            if (resolution.Status != CapabilityResolutionStatus.Success)
                diagnostics.Add(ToDiagnostic(site, resolution, profile, selected));
            else if (decision.Realization == CapabilityRealizationKind.Constrained
                     && !decision.MissingOperatingBoundaries.IsDefaultOrEmpty)
            {
                diagnostics.Add(new(
                    InfrastructureCapabilityDiagnosticCodes.OperatingBoundaryAcceptanceRequired,
                    DiagnosticSeverity.Error,
                    $"Capability '{requirement.Capability.Value}' is supported only within boundaries {DisplayBoundaries(decision.OperatingBoundaries)}; exact policy must accept the missing boundaries {DisplayBoundaries(decision.MissingOperatingBoundaries)} or supply an explicit override.",
                    Location: site.Location,
                    SchemaLocation: requirement.Capability.Value,
                    Evidence: new(
                        stage: CapabilityMatchingStage,
                        subject: requirement.Id.Value,
                        relatedLocations: MergeOrdinalSets(site.RelatedLocations, DecisionLocations(decision)),
                        sourceReferences: MergeOrdinalSets(
                            MergeOrdinalSets(
                                site.SourceReferences,
                                DecisionSourceReferences(profile, selected, decision)),
                            BoundaryAcceptanceSourceReferences(
                                boundaryAcceptancePolicy,
                                requirement.Id,
                                decision.AcceptedOperatingBoundaries)),
                        resolutionOptions:
                        [
                            "Accept every exact operating boundary through attributable environment policy.",
                            "Select an unconstrained target proof that preserves the requirement.",
                            "Supply an explicit local override with its own attributable evidence."
                        ],
                        expected: $"accepted boundaries: {DisplayBoundaries(decision.OperatingBoundaries)}",
                        observed: decision.AcceptedOperatingBoundaries.IsDefaultOrEmpty
                            ? "constrained proof with unaccepted operating boundaries"
                            : $"accepted: {DisplayBoundaries(decision.AcceptedOperatingBoundaries)}; missing: {DisplayBoundaries(decision.MissingOperatingBoundaries)}")));
            }
        }

        var normalizedDecisions = decisions.MoveToImmutable();
        if (boundaryAcceptancePolicySupported
            && boundaryAcceptancePolicyFenceMatches
            && boundaryAcceptancePolicy is not null)
        {
            AddUnexpectedAcceptanceDiagnostics(
                boundaryAcceptancePolicy,
                normalizedDecisions,
                requirements,
                diagnostics);
        }

        return new(
            profile: profile.ToReference(),
            bindingElaboration: bindingElaboration,
            target: profile.Target,
            variant: variant,
            decisions: normalizedDecisions,
            diagnostics: diagnostics.Count == 0 ? [] : diagnostics.ToImmutable(),
            boundaryAcceptancePolicy: boundaryAcceptancePolicyFenceMatches
                ? boundaryAcceptancePolicy?.ToReference()
                : null);
    }

    static InfrastructureCapabilityDecision ToDecision(
        InfrastructureCapabilityRequirement requirement,
        CapabilityResolution resolution,
        ImmutableArray<InfrastructureOperatingBoundaryId> acceptedOperatingBoundaries)
    {
        if (resolution.Status != CapabilityResolutionStatus.Success || resolution.Proof is null)
        {
            return new(
                requirement: requirement.Id,
                capability: requirement.Capability,
                realization: resolution.Status == CapabilityResolutionStatus.Unavailable
                    ? CapabilityRealizationKind.Unavailable
                    : CapabilityRealizationKind.Unknown);
        }

        var proof = resolution.Proof;
        var missingOperatingBoundaries = proof.Realization == CapabilityRealizationKind.Constrained
            ? proof.OperatingBoundaries.Where(boundary => !acceptedOperatingBoundaries.Contains(boundary)).ToImmutableArray()
            : [];
        return new(
            requirement: requirement.Id,
            capability: requirement.Capability,
            realization: proof.Realization,
            evidence: proof.Evidence,
            rules: proof.Rules,
            operatingBoundaries: proof.OperatingBoundaries,
            preservedGuarantees: proof.PreservedGuarantees,
            acceptedOperatingBoundaries: acceptedOperatingBoundaries,
            missingOperatingBoundaries: missingOperatingBoundaries);
    }

    static bool AddBoundaryAcceptancePolicyFenceDiagnostics(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariantId variant,
        InfrastructureBindingElaborationProfile bindingProfile,
        InfrastructureBoundaryAcceptancePolicy boundaryAcceptancePolicy,
        ImmutableArray<DocumentValidationDiagnostic>.Builder diagnostics)
    {
        var matches = true;
        var definitionReference = definition.ToReference();
        if (boundaryAcceptancePolicy.Definition != definitionReference)
        {
            AddBoundaryAcceptancePolicyFenceDiagnostic(
                policy: boundaryAcceptancePolicy,
                fence: "definition",
                expected: InfrastructureDiagnosticReferences.DefinitionReference(definitionReference),
                observed: InfrastructureDiagnosticReferences.DefinitionReference(boundaryAcceptancePolicy.Definition),
                diagnostics: diagnostics);
            matches = false;
        }
        var profileReference = profile.ToReference();
        if (boundaryAcceptancePolicy.Profile != profileReference)
        {
            AddBoundaryAcceptancePolicyFenceDiagnostic(
                policy: boundaryAcceptancePolicy,
                fence: "profile",
                expected: InfrastructureDiagnosticReferences.CapabilityProfileReference(profileReference),
                observed: InfrastructureDiagnosticReferences.CapabilityProfileReference(boundaryAcceptancePolicy.Profile),
                diagnostics: diagnostics);
            matches = false;
        }
        var bindingProfileReference = bindingProfile.ToReference();
        if (boundaryAcceptancePolicy.BindingProfile != bindingProfileReference)
        {
            AddBoundaryAcceptancePolicyFenceDiagnostic(
                policy: boundaryAcceptancePolicy,
                fence: "bindingProfile",
                expected: InfrastructureDiagnosticReferences.BindingProfileReference(bindingProfileReference),
                observed: InfrastructureDiagnosticReferences.BindingProfileReference(boundaryAcceptancePolicy.BindingProfile),
                diagnostics: diagnostics);
            matches = false;
        }
        if (boundaryAcceptancePolicy.Target != profile.Target)
        {
            AddBoundaryAcceptancePolicyFenceDiagnostic(
                policy: boundaryAcceptancePolicy,
                fence: "target",
                expected: profile.Target.Value,
                observed: boundaryAcceptancePolicy.Target.Value,
                diagnostics: diagnostics);
            matches = false;
        }
        if (boundaryAcceptancePolicy.Variant != variant)
        {
            AddBoundaryAcceptancePolicyFenceDiagnostic(
                policy: boundaryAcceptancePolicy,
                fence: "variant",
                expected: variant.Value,
                observed: boundaryAcceptancePolicy.Variant.Value,
                diagnostics: diagnostics);
            matches = false;
        }
        return matches;
    }

    static void AddBoundaryAcceptancePolicyFenceDiagnostic(
        InfrastructureBoundaryAcceptancePolicy policy,
        string fence,
        string expected,
        string observed,
        ImmutableArray<DocumentValidationDiagnostic>.Builder diagnostics) => diagnostics.Add(new(
        InfrastructureCapabilityDiagnosticCodes.BoundaryAcceptancePolicyFenceMismatch,
        DiagnosticSeverity.Error,
        $"Boundary-acceptance policy '{policy.Id.Value}' has a mismatched {fence} fence.",
        Location: $"/{fence}",
        SchemaLocation: fence,
        Evidence: new(
            stage: CapabilityMatchingStage,
            subject: policy.Id.Value,
            sourceReferences: [InfrastructureDiagnosticReferences.BoundaryAcceptancePolicy(policy)],
            resolutionOptions:
            [
                "Regenerate the boundary-acceptance policy from the exact definition, profiles, target, and variant supplied to this compilation."
            ],
            expected: expected,
            observed: observed)));

    static void AddUnexpectedAcceptanceDiagnostics(
        InfrastructureBoundaryAcceptancePolicy policy,
        ImmutableArray<InfrastructureCapabilityDecision> decisions,
        ImmutableArray<RequirementSite> requirements,
        ImmutableArray<DocumentValidationDiagnostic>.Builder diagnostics)
    {
        var decisionsByRequirement = decisions.ToDictionary(static decision => decision.Requirement);
        var sitesByRequirement = requirements.ToDictionary(static site => site.Requirement.Id);
        for (var index = 0; index < policy.Acceptances.Length; index++)
        {
            var acceptance = policy.Acceptances[index];
            if (!decisionsByRequirement.TryGetValue(acceptance.Requirement, out var decision))
            {
                diagnostics.Add(new(
                    InfrastructureCapabilityDiagnosticCodes.BoundaryAcceptanceRequirementUnknown,
                    DiagnosticSeverity.Error,
                    $"Boundary acceptance requirement '{acceptance.Requirement.Value}' is absent from the exact compiled demand set.",
                    Location: $"/acceptances/{index.ToString(CultureInfo.InvariantCulture)}/requirement",
                    SchemaLocation: acceptance.Requirement.Value,
                    Evidence: new(
                        stage: CapabilityMatchingStage,
                        subject: acceptance.Requirement.Value,
                        relatedLocations: [$"operating-boundary/{Uri.EscapeDataString(acceptance.Boundary.Value)}"],
                        sourceReferences: MergeOrdinalSets(
                            [InfrastructureDiagnosticReferences.BoundaryAcceptancePolicy(policy)],
                            acceptance.SourceReferences.Select(static reference => reference.Value).ToImmutableArray()),
                        resolutionOptions:
                        ["Remove the stale acceptance or regenerate policy from the exact compiled requirement set."],
                        expected: "a requirement in the exact definition and binding-elaboration result",
                        observed: "unknown requirement")));
                continue;
            }

            if (decision.Realization == CapabilityRealizationKind.Constrained
                && decision.OperatingBoundaries.Contains(acceptance.Boundary))
            {
                continue;
            }

            var site = sitesByRequirement[acceptance.Requirement];
            diagnostics.Add(new(
                InfrastructureCapabilityDiagnosticCodes.BoundaryAcceptanceUnexpected,
                DiagnosticSeverity.Error,
                $"Boundary acceptance '{acceptance.Boundary.Value}' does not belong to the selected constrained proof for requirement '{acceptance.Requirement.Value}'.",
                Location: $"/acceptances/{index.ToString(CultureInfo.InvariantCulture)}/boundary",
                SchemaLocation: acceptance.Boundary.Value,
                Evidence: new(
                    stage: CapabilityMatchingStage,
                    subject: acceptance.Requirement.Value,
                    relatedLocations: MergeOrdinalSets(site.RelatedLocations, DecisionLocations(decision)),
                    sourceReferences: MergeOrdinalSets(
                        [InfrastructureDiagnosticReferences.BoundaryAcceptancePolicy(policy)],
                        acceptance.SourceReferences.Select(static reference => reference.Value).ToImmutableArray()),
                    resolutionOptions:
                    ["Remove the stale acceptance or select the exact constrained proof that requires it."],
                    expected: decision.Realization == CapabilityRealizationKind.Constrained
                        ? DisplayBoundaries(decision.OperatingBoundaries)
                        : "no boundary acceptance for an unconstrained or unresolved proof",
                    observed: acceptance.Boundary.Value)));
        }
    }

    static ImmutableArray<string> BoundaryAcceptanceSourceReferences(
        InfrastructureBoundaryAcceptancePolicy? policy,
        InfrastructureRequirementId requirement,
        ImmutableArray<InfrastructureOperatingBoundaryId> acceptedBoundaries)
    {
        if (policy is null)
            return [];

        return
        [
            InfrastructureDiagnosticReferences.BoundaryAcceptancePolicy(policy),
            .. acceptedBoundaries.SelectMany(boundary =>
                    policy.FindAcceptance(requirement, boundary)?.SourceReferences ?? [])
                .Select(static reference => reference.Value)
        ];
    }

    static string DisplayBoundaries(IEnumerable<InfrastructureOperatingBoundaryId> boundaries) =>
        string.Join(", ", boundaries.Select(static boundary => $"'{boundary.Value}'"));

    static DocumentValidationDiagnostic ToDiagnostic(
        RequirementSite site,
        CapabilityResolution resolution,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariant variant)
    {
        var code = resolution.Status switch
        {
            CapabilityResolutionStatus.Unavailable => InfrastructureCapabilityDiagnosticCodes.RequirementUnavailable,
            CapabilityResolutionStatus.Ambiguous => InfrastructureCapabilityDiagnosticCodes.RequirementAmbiguous,
            CapabilityResolutionStatus.CompositionCycle => InfrastructureCapabilityDiagnosticCodes.CompositionCycle,
            CapabilityResolutionStatus.EvidenceCycle => InfrastructureCapabilityDiagnosticCodes.EvidenceCycle,
            _ => throw new InvalidOperationException($"Unsupported capability resolution status '{resolution.Status}'.")
        };
        ImmutableArray<string> resolutionOptions = resolution.Status switch
        {
            CapabilityResolutionStatus.Unavailable =>
            [
                "Select a coherent target variant that supplies or composes the exact capability.",
                "Add attributable capability evidence without weakening the requirement."
            ],
            CapabilityResolutionStatus.Ambiguous =>
            ["Configure explicit compiler policy to select one complete proof."],
            CapabilityResolutionStatus.CompositionCycle =>
            ["Correct the capability-composition rules so the proof graph is acyclic."],
            CapabilityResolutionStatus.EvidenceCycle =>
            ["Correct auxiliary capability evidence so the evidence graph is acyclic."],
            _ => throw new InvalidOperationException($"Unsupported capability resolution status '{resolution.Status}'.")
        };
        return new(
            code,
            DiagnosticSeverity.Error,
            resolution.Message,
            Location: site.Location,
            SchemaLocation: site.Requirement.Capability.Value,
            Evidence: new(
                stage: CapabilityMatchingStage,
                subject: site.Requirement.Id.Value,
                relatedLocations: MergeOrdinalSets(
                    site.RelatedLocations,
                    CandidateLocations(variant, site.Requirement.Capability)),
                sourceReferences: MergeOrdinalSets(
                    site.SourceReferences,
                    CandidateSourceReferences(profile, variant, site.Requirement.Capability)),
                resolutionOptions: resolutionOptions,
                expected: site.Requirement.Capability.Value,
                observed: resolution.Status switch
                {
                    CapabilityResolutionStatus.Unavailable => "unavailable",
                    CapabilityResolutionStatus.Ambiguous => "ambiguous",
                    CapabilityResolutionStatus.CompositionCycle => "composition cycle",
                    CapabilityResolutionStatus.EvidenceCycle => "evidence cycle",
                    _ => throw new InvalidOperationException($"Unsupported capability resolution status '{resolution.Status}'.")
                }));
    }

    static ImmutableArray<RequirementSite> RequirementSites(
        InfrastructureDefinitionDocument document,
        InfrastructureBindingElaborationReport bindingElaboration)
    {
        var definition = document.Definition;
        var count = definition.Workloads.Sum(static workload => workload.Requirements.Length)
            + definition.Resources.Sum(static resource => resource.Requirements.Length)
            + bindingElaboration.Obligations.Length;
        var definitionReference = InfrastructureDiagnosticReferences.Definition(document);
        var sites = new List<RequirementSite>(count);
        for (var workloadIndex = 0; workloadIndex < definition.Workloads.Length; workloadIndex++)
        {
            var workload = definition.Workloads[workloadIndex];
            for (var requirementIndex = 0; requirementIndex < workload.Requirements.Length; requirementIndex++)
            {
                sites.Add(new(
                    workload.Requirements[requirementIndex],
                    $"/definition/workloads/{workloadIndex.ToString(CultureInfo.InvariantCulture)}/requirements/{requirementIndex.ToString(CultureInfo.InvariantCulture)}/capability",
                    [],
                    []));
            }
        }
        for (var resourceIndex = 0; resourceIndex < definition.Resources.Length; resourceIndex++)
        {
            var resource = definition.Resources[resourceIndex];
            for (var requirementIndex = 0; requirementIndex < resource.Requirements.Length; requirementIndex++)
            {
                sites.Add(new(
                    resource.Requirements[requirementIndex],
                    $"/definition/resources/{resourceIndex.ToString(CultureInfo.InvariantCulture)}/requirements/{requirementIndex.ToString(CultureInfo.InvariantCulture)}/capability",
                    [],
                    []));
            }
        }

        foreach (var obligation in bindingElaboration.Obligations)
        {
            sites.Add(new(
                obligation.Requirement,
                obligation.Location,
                [
                    $"binding/{Uri.EscapeDataString(obligation.Binding.Value)}",
                    $"binding-elaboration-rule/{Uri.EscapeDataString(obligation.Rule.Value)}"
                ],
                [
                    .. obligation.SourceReferences.Select(static reference => reference.Value),
                    definitionReference,
                    InfrastructureDiagnosticReferences.BindingProfileReference(bindingElaboration.Profile)
                ]));
        }

        sites.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Requirement.Id.Value, right.Requirement.Id.Value));
        return [.. sites];
    }

    static ImmutableArray<string> CandidateLocations(
        InfrastructureCapabilityVariant variant,
        InfrastructureCapabilityId capability) =>
    [
        .. variant.Evidence
            .Where(item => item.Capability == capability)
            .Select(static item => $"capability-evidence/{Uri.EscapeDataString(item.Id.Value)}")
            .Concat(variant.Rules
                .Where(item => item.ProvidedCapability == capability)
                .Select(static item => $"capability-rule/{Uri.EscapeDataString(item.Id.Value)}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    static ImmutableArray<string> CandidateSourceReferences(
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariant variant,
        InfrastructureCapabilityId capability) =>
    [
        .. variant.Evidence
            .Where(item => item.Capability == capability)
            .SelectMany(static item => item.SourceReferences)
            .Select(static reference => reference.Value)
            .Concat(variant.Rules
                .Where(item => item.ProvidedCapability == capability)
                .SelectMany(static item => item.SourceReferences)
                .Select(static reference => reference.Value))
            .Append(InfrastructureDiagnosticReferences.CapabilityProfile(profile))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    static ImmutableArray<string> DecisionLocations(InfrastructureCapabilityDecision decision) =>
    [
        .. decision.Evidence.Select(static id => $"capability-evidence/{Uri.EscapeDataString(id.Value)}")
            .Concat(decision.Rules.Select(static id => $"capability-rule/{Uri.EscapeDataString(id.Value)}"))
            .Concat(decision.OperatingBoundaries.Select(static id => $"operating-boundary/{Uri.EscapeDataString(id.Value)}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    static ImmutableArray<string> DecisionSourceReferences(
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariant variant,
        InfrastructureCapabilityDecision decision)
    {
        var evidence = decision.Evidence.ToHashSet();
        var rules = decision.Rules.ToHashSet();
        var boundaries = decision.OperatingBoundaries.ToHashSet();
        return
        [
            .. variant.Evidence
                .Where(item => evidence.Contains(item.Id))
                .SelectMany(static item => item.SourceReferences)
                .Select(static reference => reference.Value)
                .Concat(variant.Rules
                    .Where(item => rules.Contains(item.Id))
                    .SelectMany(static item => item.SourceReferences)
                    .Select(static reference => reference.Value))
                .Concat(variant.OperatingBoundaries
                    .Where(item => boundaries.Contains(item.Id))
                    .SelectMany(static item => item.SourceReferences)
                    .Select(static reference => reference.Value))
                .Append(InfrastructureDiagnosticReferences.CapabilityProfile(profile))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<string> MergeOrdinalSets(
        ImmutableArray<string> first,
        ImmutableArray<string> second) =>
    [
        .. first.Concat(second)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    readonly record struct RequirementSite(
        InfrastructureCapabilityRequirement Requirement,
        string Location,
        ImmutableArray<string> RelatedLocations,
        ImmutableArray<string> SourceReferences);
}

enum CapabilityResolutionStatus
{
    Success,
    Unavailable,
    Ambiguous,
    CompositionCycle,
    EvidenceCycle
}

sealed record CapabilityResolution(
    CapabilityResolutionStatus Status,
    string Message,
    CapabilityProof? Proof = null);

sealed record CapabilityProof(
    CapabilityRealizationKind Realization,
    ImmutableArray<InfrastructureCapabilityEvidenceId> Evidence,
    ImmutableArray<InfrastructureCapabilityRuleId> Rules,
    ImmutableArray<InfrastructureOperatingBoundaryId> OperatingBoundaries,
    ImmutableArray<InfrastructureCapabilityId> PreservedGuarantees,
    bool ContainsOverride);

static class CapabilityResolver
{
    internal static CapabilityResolution Resolve(
        InfrastructureCapabilityVariant variant,
        InfrastructureCapabilityId capability) =>
        ResolveCapability(variant, capability, []);

    static CapabilityResolution ResolveCapability(
        InfrastructureCapabilityVariant variant,
        InfrastructureCapabilityId capability,
        ImmutableHashSet<InfrastructureCapabilityId> capabilityPath)
    {
        if (capabilityPath.Contains(capability))
        {
            return new(
                CapabilityResolutionStatus.CompositionCycle,
                $"Capability composition for '{capability.Value}' contains a recursive rule cycle.");
        }

        var nextPath = capabilityPath.Add(capability);
        var candidates = new List<CapabilityProof>();
        var sawAmbiguousProof = false;
        var sawCompositionCycle = false;
        var sawEvidenceCycle = false;

        foreach (var evidence in variant.Evidence)
        {
            if (evidence.Capability != capability)
                continue;
            var expanded = ExpandEvidence(variant, evidence, []);
            if (expanded.Status == CapabilityResolutionStatus.Success && expanded.Proof is not null)
                candidates.Add(expanded.Proof);
            else if (expanded.Status == CapabilityResolutionStatus.EvidenceCycle)
                sawEvidenceCycle = true;
        }

        foreach (var rule in variant.Rules)
        {
            if (rule.ProvidedCapability != capability)
                continue;

            var childProofs = new List<CapabilityProof>(rule.RequiredCapabilities.Length);
            var ruleValid = true;
            var ruleAmbiguous = false;
            foreach (var required in rule.RequiredCapabilities)
            {
                var child = ResolveCapability(variant, required, nextPath);
                switch (child.Status)
                {
                    case CapabilityResolutionStatus.Success when child.Proof is not null:
                        childProofs.Add(child.Proof);
                        break;
                    case CapabilityResolutionStatus.Ambiguous:
                        ruleAmbiguous = true;
                        break;
                    case CapabilityResolutionStatus.CompositionCycle:
                        sawCompositionCycle = true;
                        ruleValid = false;
                        break;
                    case CapabilityResolutionStatus.EvidenceCycle:
                        sawEvidenceCycle = true;
                        ruleValid = false;
                        break;
                    default:
                        ruleValid = false;
                        break;
                }
            }

            if (ruleValid)
            {
                if (ruleAmbiguous)
                    sawAmbiguousProof = true;
                else
                    candidates.Add(ComposeRule(rule, childProofs));
            }
        }

        if (sawAmbiguousProof || candidates.Count > 1)
        {
            return new(
                CapabilityResolutionStatus.Ambiguous,
                $"Capability '{capability.Value}' has several valid proofs; explicit compiler policy must select one.");
        }
        if (candidates.Count == 1)
            return new(CapabilityResolutionStatus.Success, "Capability proof resolved.", candidates[0]);
        if (sawEvidenceCycle)
        {
            return new(
                CapabilityResolutionStatus.EvidenceCycle,
                $"Capability '{capability.Value}' depends on cyclic auxiliary evidence.");
        }
        if (sawCompositionCycle)
        {
            return new(
                CapabilityResolutionStatus.CompositionCycle,
                $"Capability '{capability.Value}' can only be reached through a recursive composition cycle.");
        }
        return new(
            CapabilityResolutionStatus.Unavailable,
            $"No evidence in coherent variant '{variant.Id.Value}' preserves capability '{capability.Value}'.");
    }

    static CapabilityResolution ExpandEvidence(
        InfrastructureCapabilityVariant variant,
        InfrastructureCapabilityEvidence evidence,
        ImmutableHashSet<InfrastructureCapabilityEvidenceId> evidencePath)
    {
        if (evidencePath.Contains(evidence.Id))
        {
            return new(
                CapabilityResolutionStatus.EvidenceCycle,
                $"Capability evidence '{evidence.Id.Value}' contains a recursive auxiliary cycle.");
        }

        var nextPath = evidencePath.Add(evidence.Id);
        var evidenceIds = new SortedSet<InfrastructureCapabilityEvidenceId>(EvidenceIdComparer.Ordinal) { evidence.Id };
        var boundaries = new SortedSet<InfrastructureOperatingBoundaryId>(BoundaryIdComparer.Ordinal);
        boundaries.UnionWith(evidence.OperatingBoundaries);
        var containsOverride = evidence.Realization == CapabilityRealizationKind.Override;

        foreach (var auxiliaryId in evidence.Auxiliaries)
        {
            var auxiliary = variant.Evidence.First(item => item.Id == auxiliaryId);
            var expanded = ExpandEvidence(variant, auxiliary, nextPath);
            if (expanded.Status != CapabilityResolutionStatus.Success || expanded.Proof is null)
                return expanded;
            evidenceIds.UnionWith(expanded.Proof.Evidence);
            boundaries.UnionWith(expanded.Proof.OperatingBoundaries);
            containsOverride |= expanded.Proof.ContainsOverride;
        }

        var realization = containsOverride
            ? CapabilityRealizationKind.Override
            : boundaries.Count > 0
                ? CapabilityRealizationKind.Constrained
                : evidence.Realization;
        return new(
            CapabilityResolutionStatus.Success,
            "Capability evidence expanded.",
            new(
                realization,
                [.. evidenceIds],
                [],
                [.. boundaries],
                [],
                containsOverride));
    }

    static CapabilityProof ComposeRule(
        InfrastructureCapabilityRule rule,
        List<CapabilityProof> children)
    {
        var evidence = new SortedSet<InfrastructureCapabilityEvidenceId>(EvidenceIdComparer.Ordinal);
        var rules = new SortedSet<InfrastructureCapabilityRuleId>(RuleIdComparer.Ordinal) { rule.Id };
        var boundaries = new SortedSet<InfrastructureOperatingBoundaryId>(BoundaryIdComparer.Ordinal);
        var guarantees = new SortedSet<InfrastructureCapabilityId>(CapabilityIdComparer.Ordinal);
        boundaries.UnionWith(rule.OperatingBoundaries);
        guarantees.UnionWith(rule.PreservedGuarantees);
        var containsOverride = false;

        foreach (var child in children)
        {
            evidence.UnionWith(child.Evidence);
            rules.UnionWith(child.Rules);
            boundaries.UnionWith(child.OperatingBoundaries);
            guarantees.UnionWith(child.PreservedGuarantees);
            containsOverride |= child.ContainsOverride;
        }

        var realization = containsOverride
            ? CapabilityRealizationKind.Override
            : boundaries.Count > 0
                ? CapabilityRealizationKind.Constrained
                : CapabilityRealizationKind.Composed;
        return new(
            realization,
            [.. evidence],
            [.. rules],
            [.. boundaries],
            [.. guarantees],
            containsOverride);
    }
}

sealed class EvidenceIdComparer : IComparer<InfrastructureCapabilityEvidenceId>
{
    internal static EvidenceIdComparer Ordinal { get; } = new();
    public int Compare(InfrastructureCapabilityEvidenceId x, InfrastructureCapabilityEvidenceId y) =>
        StringComparer.Ordinal.Compare(x.Value, y.Value);
}

sealed class RuleIdComparer : IComparer<InfrastructureCapabilityRuleId>
{
    internal static RuleIdComparer Ordinal { get; } = new();
    public int Compare(InfrastructureCapabilityRuleId x, InfrastructureCapabilityRuleId y) =>
        StringComparer.Ordinal.Compare(x.Value, y.Value);
}

sealed class BoundaryIdComparer : IComparer<InfrastructureOperatingBoundaryId>
{
    internal static BoundaryIdComparer Ordinal { get; } = new();
    public int Compare(InfrastructureOperatingBoundaryId x, InfrastructureOperatingBoundaryId y) =>
        StringComparer.Ordinal.Compare(x.Value, y.Value);
}

sealed class CapabilityIdComparer : IComparer<InfrastructureCapabilityId>
{
    internal static CapabilityIdComparer Ordinal { get; } = new();
    public int Compare(InfrastructureCapabilityId x, InfrastructureCapabilityId y) =>
        StringComparer.Ordinal.Compare(x.Value, y.Value);
}
