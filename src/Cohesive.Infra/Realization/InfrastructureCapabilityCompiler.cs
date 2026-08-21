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

    /// <summary>A binding contract has not yet been elaborated into its induced capability and assurance obligations.</summary>
    public const string BindingElaborationUnavailable = "infra.bindings.elaboration.unavailable";

    /// <summary>A constrained proof requires exact environment-policy acceptance before it can close.</summary>
    public const string OperatingBoundaryAcceptanceRequired = "infra.capabilities.boundary.acceptanceRequired";

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
        ImmutableArray<InfrastructureCapabilityId> preservedGuarantees = default)
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

    /// <summary>Whether the decision supplies an available exact realization.</summary>
    public bool IsAvailable => Realization is not CapabilityRealizationKind.Unavailable and not CapabilityRealizationKind.Unknown;

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
        && PreservedGuarantees.SequenceEqual(other.PreservedGuarantees);

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
    /// <param name="definition">Exact fingerprinted infrastructure definition.</param>
    /// <param name="profile">Exact selected capability-profile reference.</param>
    /// <param name="target">Stable interpretation-target identity.</param>
    /// <param name="variant">Selected coherent target variant.</param>
    /// <param name="decisions">One decision for every exact definition requirement.</param>
    /// <param name="diagnostics">Structured capability-closure diagnostics.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="profile"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity, decision collection, or coverage invariant is invalid.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityClosureReport(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfileReference profile,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<InfrastructureCapabilityDecision> decisions = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        Definition = Guard.RequireNotNull(definition);
        Profile = Guard.RequireNotNull(profile);
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("A capability-closure report requires a target identity.", nameof(target));
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("A capability-closure report requires a coherent variant identity.", nameof(variant));

        Target = target;
        Variant = variant;
        Decisions = NormalizeDecisions(decisions);
        Diagnostics = NormalizeDiagnostics(diagnostics);
        ValidateCoverage();
    }

    /// <summary>Exact fingerprinted infrastructure definition.</summary>
    public InfrastructureDefinitionDocument Definition { get; }

    /// <summary>Exact schema, identity, and fingerprint of the selected capability profile.</summary>
    public InfrastructureCapabilityProfileReference Profile { get; }

    /// <summary>Selected interpretation-target identity.</summary>
    public InfrastructureTargetId Target { get; }

    /// <summary>Selected coherent target variant.</summary>
    public InfrastructureCapabilityVariantId Variant { get; }

    /// <summary>One decision per exact requirement in requirement-identity order.</summary>
    public ImmutableArray<InfrastructureCapabilityDecision> Decisions { get; }

    /// <summary>Structured diagnostics in deterministic portable-document order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether every requirement is available and no error diagnostic remains.</summary>
    public bool IsClosed =>
        Decisions.All(static decision => decision.IsAvailable)
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>Compares capability-closure reports structurally.</summary>
    /// <param name="other">Other report.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureCapabilityClosureReport? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Definition == other.Definition
        && Profile == other.Profile
        && Target == other.Target
        && Variant == other.Variant
        && Decisions.SequenceEqual(other.Decisions)
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code for this report.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Definition);
        hash.Add(Profile);
        hash.Add(Target);
        hash.Add(Variant);
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
            .OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (expected.Length != Decisions.Length)
            throw new ArgumentException("A capability-closure report requires one decision for every definition requirement.", nameof(Decisions));

        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index].Id != Decisions[index].Requirement
                || expected[index].Capability != Decisions[index].Capability)
            {
                throw new ArgumentException(
                    $"Capability decision '{Decisions[index].Requirement.Value}' does not match the exact definition requirement.",
                    nameof(Decisions));
            }
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

    static ImmutableArray<DocumentValidationDiagnostic> NormalizeDiagnostics(
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
            return [];
        if (diagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Infrastructure capability diagnostics cannot contain null.", nameof(diagnostics));
        return diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
    }
}

/// <summary>Computes capability-planning closure against one coherent configured target variant.</summary>
public static class InfrastructureCapabilityCompiler
{
    const string ProfileSelectionStage = "infrastructure-capability-profile-selection";
    const string BindingElaborationStage = "infrastructure-binding-elaboration";
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
        InfrastructureCapabilityVariantId variant)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("Infrastructure capability compilation requires a coherent variant identity.", nameof(variant));

        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        if (!string.Equals(
                profile.SchemaVersion,
                InfrastructureCapabilityProfile.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                InfrastructureCapabilityDiagnosticCodes.ProfileSchemaUnsupported,
                DiagnosticSeverity.Error,
                $"Capability profile schema '{profile.SchemaVersion}' is unsupported; expected '{InfrastructureCapabilityProfile.CurrentSchemaVersion}'.",
                Location: "/schemaVersion",
                Evidence: new(
                    stage: ProfileSelectionStage,
                    subject: profile.Id.Value,
                    sourceReferences: [ProfileReference(profile)],
                    resolutionOptions: ["Select an exact capability profile using a schema supported by this compiler."],
                    expected: InfrastructureCapabilityProfile.CurrentSchemaVersion,
                    observed: profile.SchemaVersion)));
        }
        if (!profile.SupportedDefinitionSchemaVersions.Contains(definition.SchemaVersion, StringComparer.Ordinal))
        {
            diagnostics.Add(new(
                InfrastructureCapabilityDiagnosticCodes.DefinitionSchemaUnsupported,
                DiagnosticSeverity.Error,
                $"Target '{profile.Target.Value}' does not support infrastructure definition schema '{definition.SchemaVersion}'.",
                Location: "/schemaVersion",
                Evidence: new(
                    stage: ProfileSelectionStage,
                    subject: $"{definition.Definition.Id.Value}@{definition.Definition.Revision.Value}",
                    sourceReferences: [DefinitionReference(definition), ProfileReference(profile)],
                    resolutionOptions:
                    [
                        "Select a capability profile that supports the exact infrastructure-definition schema.",
                        "Recompile the definition through a supported schema migration."
                    ],
                    expected: string.Join(", ", profile.SupportedDefinitionSchemaVersions),
                    observed: definition.SchemaVersion)));
        }

        var selected = profile.FindVariant(variant);
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
                    sourceReferences: [ProfileReference(profile)],
                    resolutionOptions: ["Select one coherent target variant advertised by the exact capability profile."],
                    expected: "a coherent variant advertised by the exact capability profile",
                    observed: "variant not advertised")));
        }

        var requirements = RequirementSites(definition.Definition);
        var decisions = ImmutableArray.CreateBuilder<InfrastructureCapabilityDecision>(requirements.Length);
        var schemasSupported = diagnostics.Count == 0;

        for (var bindingIndex = 0; bindingIndex < definition.Definition.Bindings.Length; bindingIndex++)
        {
            var binding = definition.Definition.Bindings[bindingIndex];
            diagnostics.Add(new(
                InfrastructureCapabilityDiagnosticCodes.BindingElaborationUnavailable,
                DiagnosticSeverity.Error,
                $"Binding contract '{binding.Contract.Value}' has not been elaborated into capability and assurance obligations by this compiler version.",
                Location: $"/definition/bindings/{bindingIndex.ToString(CultureInfo.InvariantCulture)}/contract",
                SchemaLocation: binding.Contract.Value,
                Evidence: new(
                    stage: BindingElaborationStage,
                    subject: binding.Id.Value,
                    sourceReferences: [DefinitionReference(definition)],
                    resolutionOptions:
                    [
                        "Register a deterministic elaborator for the exact binding contract.",
                        "Replace the binding contract with one supported by the selected compiler profile."
                    ],
                    expected: "capability and assurance obligations induced by the exact binding contract",
                    observed: "binding contract not elaborated")));
        }

        foreach (var site in requirements)
        {
            var requirement = site.Requirement;
            if (selected is null || !schemasSupported)
            {
                decisions.Add(new(
                    requirement.Id,
                    requirement.Capability,
                    CapabilityRealizationKind.Unknown));
                continue;
            }

            var resolution = CapabilityResolver.Resolve(selected, requirement.Capability);
            var decision = ToDecision(requirement, resolution);
            decisions.Add(decision);
            if (resolution.Status != CapabilityResolutionStatus.Success)
                diagnostics.Add(ToDiagnostic(requirement, site.Location, resolution, profile, selected));
            else if (decision.Realization == CapabilityRealizationKind.Constrained)
            {
                diagnostics.Add(new(
                    InfrastructureCapabilityDiagnosticCodes.OperatingBoundaryAcceptanceRequired,
                    DiagnosticSeverity.Error,
                    $"Capability '{requirement.Capability.Value}' is supported only within boundaries {string.Join(", ", decision.OperatingBoundaries.Select(static boundary => $"'{boundary.Value}'"))}; exact environment policy must accept them or supply an explicit override.",
                    Location: site.Location,
                    SchemaLocation: requirement.Capability.Value,
                    Evidence: new(
                        stage: CapabilityMatchingStage,
                        subject: requirement.Id.Value,
                        relatedLocations: DecisionLocations(decision),
                        sourceReferences: DecisionSourceReferences(profile, selected, decision),
                        resolutionOptions:
                        [
                            "Accept every exact operating boundary through attributable environment policy.",
                            "Select an unconstrained target proof that preserves the requirement.",
                            "Supply an explicit local override with its own attributable evidence."
                        ],
                        expected: "accepted operating boundaries or an unconstrained exact proof",
                        observed: "constrained proof with unaccepted operating boundaries")));
            }
        }

        return new(
            definition,
            profile.ToReference(),
            profile.Target,
            variant,
            decisions.MoveToImmutable(),
            diagnostics.Count == 0 ? [] : diagnostics.ToImmutable());
    }

    static InfrastructureCapabilityDecision ToDecision(
        InfrastructureCapabilityRequirement requirement,
        CapabilityResolution resolution)
    {
        if (resolution.Status != CapabilityResolutionStatus.Success || resolution.Proof is null)
        {
            return new(
                requirement.Id,
                requirement.Capability,
                resolution.Status == CapabilityResolutionStatus.Unavailable
                    ? CapabilityRealizationKind.Unavailable
                    : CapabilityRealizationKind.Unknown);
        }

        var proof = resolution.Proof;
        return new(
            requirement.Id,
            requirement.Capability,
            proof.Realization,
            proof.Evidence,
            proof.Rules,
            proof.OperatingBoundaries,
            proof.PreservedGuarantees);
    }

    static DocumentValidationDiagnostic ToDiagnostic(
        InfrastructureCapabilityRequirement requirement,
        string location,
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
            Location: location,
            SchemaLocation: requirement.Capability.Value,
            Evidence: new(
                stage: CapabilityMatchingStage,
                subject: requirement.Id.Value,
                relatedLocations: CandidateLocations(variant, requirement.Capability),
                sourceReferences: CandidateSourceReferences(profile, variant, requirement.Capability),
                resolutionOptions: resolutionOptions,
                expected: requirement.Capability.Value,
                observed: resolution.Status switch
                {
                    CapabilityResolutionStatus.Unavailable => "unavailable",
                    CapabilityResolutionStatus.Ambiguous => "ambiguous",
                    CapabilityResolutionStatus.CompositionCycle => "composition cycle",
                    CapabilityResolutionStatus.EvidenceCycle => "evidence cycle",
                    _ => throw new InvalidOperationException($"Unsupported capability resolution status '{resolution.Status}'.")
                }));
    }

    static ImmutableArray<(InfrastructureCapabilityRequirement Requirement, string Location)> RequirementSites(
        InfrastructureDefinition definition)
    {
        var count = definition.Workloads.Sum(static workload => workload.Requirements.Length)
            + definition.Resources.Sum(static resource => resource.Requirements.Length);
        var sites = new List<(InfrastructureCapabilityRequirement Requirement, string Location)>(count);
        for (var workloadIndex = 0; workloadIndex < definition.Workloads.Length; workloadIndex++)
        {
            var workload = definition.Workloads[workloadIndex];
            for (var requirementIndex = 0; requirementIndex < workload.Requirements.Length; requirementIndex++)
            {
                sites.Add((
                    workload.Requirements[requirementIndex],
                    $"/definition/workloads/{workloadIndex.ToString(CultureInfo.InvariantCulture)}/requirements/{requirementIndex.ToString(CultureInfo.InvariantCulture)}/capability"));
            }
        }
        for (var resourceIndex = 0; resourceIndex < definition.Resources.Length; resourceIndex++)
        {
            var resource = definition.Resources[resourceIndex];
            for (var requirementIndex = 0; requirementIndex < resource.Requirements.Length; requirementIndex++)
            {
                sites.Add((
                    resource.Requirements[requirementIndex],
                    $"/definition/resources/{resourceIndex.ToString(CultureInfo.InvariantCulture)}/requirements/{requirementIndex.ToString(CultureInfo.InvariantCulture)}/capability"));
            }
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
            .Concat(variant.Rules
                .Where(item => item.ProvidedCapability == capability)
                .SelectMany(static item => item.SourceReferences))
            .Append(ProfileReference(profile))
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
                .Concat(variant.Rules
                    .Where(item => rules.Contains(item.Id))
                    .SelectMany(static item => item.SourceReferences))
                .Concat(variant.OperatingBoundaries
                    .Where(item => boundaries.Contains(item.Id))
                    .SelectMany(static item => item.SourceReferences))
                .Append(ProfileReference(profile))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    static string DefinitionReference(InfrastructureDefinitionDocument document) =>
        $"{document.Definition.Id.Value}@{document.Definition.Revision.Value}"
        + $"#{document.Fingerprint.Algorithm}:{document.Fingerprint.Canonicalization}:{document.Fingerprint.Value}";

    static string ProfileReference(InfrastructureCapabilityProfile profile) =>
        $"{profile.Id.Value}"
        + $"#{profile.Fingerprint.Algorithm}:{profile.Fingerprint.Canonicalization}:{profile.Fingerprint.Value}";
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
