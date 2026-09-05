using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Infra.Configuration;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Well-known convention key used to select one target facility for an exact logical node.</summary>
public static class InfrastructureTargetFacilitySelection
{
    /// <summary>Setting whose value is an exact <see cref="InfrastructureTargetFacilityId"/>.</summary>
    public static InfrastructureSettingId Setting { get; } = new("target/facility");

    /// <summary>Projects a canonical logical node into its facility-selection configuration subject.</summary>
    /// <param name="node">Canonical logical node.</param>
    /// <returns>The exact node-scoped configuration subject.</returns>
    /// <exception cref="ArgumentException"><paramref name="node"/> is default.</exception>
    public static InfrastructureConfigurationSubject Subject(InfrastructureNodeId node)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A facility-selection subject requires a logical node.", nameof(node));
        return new(node.Value);
    }
}

/// <summary>One logical node's selected target facility.</summary>
public sealed record InfrastructureTargetFacilityDecision
{
    /// <summary>Creates a selected target-facility decision.</summary>
    /// <param name="node">Canonical logical node.</param>
    /// <param name="nodeKind">Canonical node family.</param>
    /// <param name="facility">Selected target facility.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeKind"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureTargetFacilityDecision(
        InfrastructureNodeId node,
        InfrastructureNodeKind nodeKind,
        InfrastructureTargetFacilityId facility)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A target-facility decision requires a logical node.", nameof(node));
        if (!Enum.IsDefined(nodeKind))
            throw new ArgumentOutOfRangeException(nameof(nodeKind), nodeKind, "Unsupported infrastructure node kind.");
        if (string.IsNullOrWhiteSpace(facility.Value))
            throw new ArgumentException("A target-facility decision requires a selected facility.", nameof(facility));

        Node = node;
        NodeKind = nodeKind;
        Facility = facility;
    }

    /// <summary>Canonical logical node.</summary>
    public InfrastructureNodeId Node { get; }

    /// <summary>Canonical node family.</summary>
    public InfrastructureNodeKind NodeKind { get; }

    /// <summary>Selected target facility.</summary>
    public InfrastructureTargetFacilityId Facility { get; }
}

/// <summary>Deterministic fingerprint of one exact target-facility compilation.</summary>
public sealed record InfrastructureTargetFacilityPlanFingerprint
{
    /// <summary>Digest algorithm used by the current plan fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current plan fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-target-facility-plan/v1-c14n/v1";

    /// <summary>Creates target-facility plan fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureTargetFacilityPlanFingerprint(string algorithm, string canonicalization, string value)
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

/// <summary>
/// Exact result of matching one canonical definition to a target's declarative facility and capability manifest.
/// </summary>
public sealed record InfrastructureTargetFacilityPlan
{
    /// <summary>Current persisted target-facility plan schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive.infra.target-facility-plan/1";

    /// <summary>Creates or restores an exactly fingerprinted target-facility plan.</summary>
    /// <param name="schemaVersion">Exact persisted plan schema version.</param>
    /// <param name="definition">Exact canonical infrastructure definition.</param>
    /// <param name="manifest">Exact target-facility manifest.</param>
    /// <param name="configuration">Resolved attributable convention and explicit-selection values.</param>
    /// <param name="capabilityClosure">Capability closure compiled through the evidence owned by selected facilities.</param>
    /// <param name="decisions">Successfully selected facilities.</param>
    /// <param name="diagnostics">Normalized facility-selection and capability diagnostics.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema, exact fences, decisions, diagnostic coverage, or fingerprint is invalid.
    /// </exception>
    [JsonConstructor]
    public InfrastructureTargetFacilityPlan(
        string schemaVersion,
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest manifest,
        InfrastructureConventionResolution configuration,
        InfrastructureCapabilityClosureReport capabilityClosure,
        ImmutableArray<InfrastructureTargetFacilityDecision> decisions = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default,
        InfrastructureTargetFacilityPlanFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Target-facility plan schema '{SchemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        Definition = Guard.RequireNotNull(definition);
        Manifest = Guard.RequireNotNull(manifest);
        Configuration = Guard.RequireNotNull(configuration);
        CapabilityClosure = Guard.RequireNotNull(capabilityClosure);
        Decisions = NormalizeDecisions(decisions);
        ValidateDecisionNodes();
        if (CapabilityClosure.Definition != Definition)
            throw new ArgumentException("The capability closure was compiled from another definition.", nameof(capabilityClosure));
        if (CapabilityClosure.Profile != Manifest.Profile.ToReference()
            || CapabilityClosure.Target != Manifest.Profile.Target
            || CapabilityClosure.Variant != Manifest.Variant)
        {
            throw new ArgumentException(
                "The capability closure does not match the exact target-facility manifest profile and variant.",
                nameof(capabilityClosure));
        }
        var selectedEvidence = InfrastructureTargetEvidenceSelection.Select(Manifest, Decisions).ToHashSet();
        if (CapabilityClosure.Decisions.Any(decision =>
                decision.Evidence.Any(evidence => !selectedEvidence.Contains(evidence))))
        {
            throw new ArgumentException(
                "The capability closure cites evidence outside the selected target facilities.",
                nameof(capabilityClosure));
        }
        SelectedEvidence = Manifest.Profile.FindVariant(Manifest.Variant)!.Evidence
            .Where(evidence => selectedEvidence.Contains(evidence.Id))
            .ToImmutableArray();

        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
        var computed = InfrastructureTargetFacilityPlanFingerprinting.Compute(
            SchemaVersion,
            Definition.ToReference(),
            Manifest.ToReference(),
            Configuration,
            CapabilityClosure,
            Decisions,
            Diagnostics);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied target-facility plan fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact persisted plan schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact canonical infrastructure definition.</summary>
    public InfrastructureDefinitionDocument Definition { get; }

    /// <summary>Exact target-facility manifest.</summary>
    public InfrastructureTargetFacilityManifest Manifest { get; }

    /// <summary>Resolved attributable conventions and explicit facility selections.</summary>
    public InfrastructureConventionResolution Configuration { get; }

    /// <summary>Complete capability profile owned by the exact target manifest.</summary>
    [JsonIgnore]
    public InfrastructureCapabilityProfile CapabilityProfile => Manifest.Profile;

    /// <summary>Capability evidence admitted by the selected facilities, including required auxiliary evidence.</summary>
    [JsonIgnore]
    public ImmutableArray<InfrastructureCapabilityEvidence> SelectedEvidence { get; }

    /// <summary>Capability closure compiled through selected evidence from the manifest's exact profile and variant.</summary>
    public InfrastructureCapabilityClosureReport CapabilityClosure { get; }

    /// <summary>Successfully selected facilities in logical-node order.</summary>
    public ImmutableArray<InfrastructureTargetFacilityDecision> Decisions { get; }

    /// <summary>Normalized facility-selection and capability diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Deterministic fingerprint of this exact target-facility compilation.</summary>
    public InfrastructureTargetFacilityPlanFingerprint Fingerprint { get; }

    /// <summary>Whether every node has one facility, capability closure is complete, and no errors remain.</summary>
    [JsonIgnore]
    public bool IsComplete =>
        Decisions.Length == Definition.Definition.Workloads.Length + Definition.Definition.Resources.Length
        && CapabilityClosure.IsClosed
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>Finds the selected facility decision for one canonical logical node.</summary>
    /// <param name="node">Canonical logical node.</param>
    /// <returns>The selected facility decision.</returns>
    /// <exception cref="ArgumentException"><paramref name="node"/> is default.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="node"/> has no selected facility.</exception>
    public InfrastructureTargetFacilityDecision FindDecision(InfrastructureNodeId node)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A default logical node cannot be resolved.", nameof(node));
        var index = CanonicalDocumentCollections.BinarySearchIndex(
            Decisions,
            node,
            static (decision, sought) => StringComparer.Ordinal.Compare(decision.Node.Value, sought.Value));
        return index < 0
            ? throw new KeyNotFoundException($"Target-facility plan contains no decision for node '{node.Value}'.")
            : Decisions[index];
    }

    /// <summary>Compares target-facility plans structurally.</summary>
    /// <param name="other">Other target-facility plan.</param>
    /// <returns><see langword="true"/> when every semantic field is equal.</returns>
    public bool Equals(InfrastructureTargetFacilityPlan? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && Definition == other.Definition
        && Manifest == other.Manifest
        && Configuration == other.Configuration
        && CapabilityClosure == other.CapabilityClosure
        && Decisions.SequenceEqual(other.Decisions)
        && Diagnostics.SequenceEqual(other.Diagnostics)
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for this plan.</summary>
    /// <returns>A hash code derived from every semantic field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(Definition);
        hash.Add(Manifest);
        hash.Add(Configuration);
        hash.Add(CapabilityClosure);
        foreach (var decision in Decisions)
            hash.Add(decision);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructureTargetFacilityDecision> NormalizeDecisions(
        ImmutableArray<InfrastructureTargetFacilityDecision> decisions)
    {
        if (decisions.IsDefaultOrEmpty)
            return [];
        if (decisions.Any(static item => item is null))
            throw new ArgumentException("Target-facility decisions cannot contain null.", nameof(decisions));

        var ordered = decisions.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Node.Value, right.Node.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Node == ordered[index].Node)
            {
                throw new ArgumentException(
                    $"Target-facility decision for node '{ordered[index].Node.Value}' is duplicated.",
                    nameof(decisions));
            }
        }
        return ordered;
    }

    void ValidateDecisionNodes()
    {
        var workloads = Definition.Definition.Workloads.Select(static item => item.Id).ToHashSet();
        var resources = Definition.Definition.Resources.Select(static item => item.Id).ToHashSet();
        var facilities = Manifest.Facilities.ToDictionary(static item => item.Id);
        foreach (var decision in Decisions)
        {
            var valid = decision.NodeKind switch
            {
                InfrastructureNodeKind.Workload => workloads.Contains(decision.Node),
                InfrastructureNodeKind.Resource => resources.Contains(decision.Node),
                _ => false
            };
            if (!valid)
            {
                throw new ArgumentException(
                    $"Target-facility decision '{decision.Node.Value}' does not match an exact definition node and kind.",
                    nameof(Decisions));
            }
            if (!facilities.TryGetValue(decision.Facility, out var facility)
                || facility.NodeKind != decision.NodeKind)
            {
                throw new ArgumentException(
                    $"Target-facility decision '{decision.Node.Value}' selects unknown or incompatible facility '{decision.Facility.Value}'.",
                    nameof(Decisions));
            }
        }
    }
}

/// <summary>Compiles canonical infrastructure definitions through declarative target-facility manifests.</summary>
public static class InfrastructureTargetCompiler
{
    const string Stage = "infrastructure-target-facility-selection";

    /// <summary>Stable diagnostics emitted by target-facility selection.</summary>
    public static class DiagnosticCodes
    {
        /// <summary>No target facility preserves every capability declared directly on a logical node.</summary>
        public const string FacilityUnavailable = "infra.target.facility.unavailable";

        /// <summary>Several target facilities satisfy a logical node without an explicit selection policy.</summary>
        public const string FacilityAmbiguous = "infra.target.facility.ambiguous";

        /// <summary>An explicit or conventional facility selection cannot satisfy its logical node.</summary>
        public const string FacilitySelectionInvalid = "infra.target.facility.selectionInvalid";
    }

    /// <summary>Compiles one exact definition and its binding obligations through a target-facility manifest.</summary>
    /// <param name="definition">Exact canonical infrastructure definition.</param>
    /// <param name="manifest">Declarative target facilities and exact capability profile.</param>
    /// <param name="bindingElaborationProfile">Exact provider-neutral binding elaboration rules.</param>
    /// <returns>An exact facility plan with capability closure and normalized diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public static InfrastructureTargetFacilityPlan Compile(
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest manifest,
        InfrastructureBindingElaborationProfile bindingElaborationProfile) =>
        Compile(
            definition,
            manifest,
            bindingElaborationProfile,
            new InfrastructureConventionResolution(),
            boundaryAcceptancePolicy: null);

    /// <summary>Compiles one exact definition using attributable constrained-boundary acceptance policy.</summary>
    /// <param name="definition">Exact canonical infrastructure definition.</param>
    /// <param name="manifest">Declarative target facilities and exact capability profile.</param>
    /// <param name="bindingElaborationProfile">Exact provider-neutral binding elaboration rules.</param>
    /// <param name="boundaryAcceptancePolicy">Exact demand-scoped operating-boundary acceptance policy.</param>
    /// <returns>An exact facility plan with capability closure and normalized diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public static InfrastructureTargetFacilityPlan Compile(
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest manifest,
        InfrastructureBindingElaborationProfile bindingElaborationProfile,
        InfrastructureBoundaryAcceptancePolicy boundaryAcceptancePolicy)
    {
        ArgumentNullException.ThrowIfNull(boundaryAcceptancePolicy);
        return Compile(
            definition,
            manifest,
            bindingElaborationProfile,
            new InfrastructureConventionResolution(),
            boundaryAcceptancePolicy);
    }

    /// <summary>Compiles one exact definition using convention profiles for attributable facility selection.</summary>
    /// <param name="definition">Exact canonical infrastructure definition.</param>
    /// <param name="manifest">Declarative target facilities and exact capability profile.</param>
    /// <param name="bindingElaborationProfile">Exact provider-neutral binding elaboration rules.</param>
    /// <param name="conventionProfiles">Convention and explicit candidates resolved by shared authority precedence.</param>
    /// <returns>An exact facility plan with effective configuration and normalized diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A convention profile is <see langword="null"/>.</exception>
    public static InfrastructureTargetFacilityPlan Compile(
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest manifest,
        InfrastructureBindingElaborationProfile bindingElaborationProfile,
        IEnumerable<InfrastructureConventionProfile> conventionProfiles)
    {
        ArgumentNullException.ThrowIfNull(conventionProfiles);
        return Compile(
            definition,
            manifest,
            bindingElaborationProfile,
            InfrastructureConventionResolver.Resolve(conventionProfiles),
            boundaryAcceptancePolicy: null);
    }

    /// <summary>
    /// Compiles one exact definition using attributable facility-selection conventions and boundary acceptance.
    /// </summary>
    /// <param name="definition">Exact canonical infrastructure definition.</param>
    /// <param name="manifest">Declarative target facilities and exact capability profile.</param>
    /// <param name="bindingElaborationProfile">Exact provider-neutral binding elaboration rules.</param>
    /// <param name="conventionProfiles">Convention and explicit candidates resolved by shared authority precedence.</param>
    /// <param name="boundaryAcceptancePolicy">Exact demand-scoped operating-boundary acceptance policy.</param>
    /// <returns>An exact facility plan with effective configuration, capability closure, and normalized diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A convention profile is <see langword="null"/>.</exception>
    public static InfrastructureTargetFacilityPlan Compile(
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest manifest,
        InfrastructureBindingElaborationProfile bindingElaborationProfile,
        IEnumerable<InfrastructureConventionProfile> conventionProfiles,
        InfrastructureBoundaryAcceptancePolicy boundaryAcceptancePolicy)
    {
        ArgumentNullException.ThrowIfNull(conventionProfiles);
        ArgumentNullException.ThrowIfNull(boundaryAcceptancePolicy);
        return Compile(
            definition,
            manifest,
            bindingElaborationProfile,
            InfrastructureConventionResolver.Resolve(conventionProfiles),
            boundaryAcceptancePolicy);
    }

    static InfrastructureTargetFacilityPlan Compile(
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest manifest,
        InfrastructureBindingElaborationProfile bindingElaborationProfile,
        InfrastructureConventionResolution configuration,
        InfrastructureBoundaryAcceptancePolicy? boundaryAcceptancePolicy)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(bindingElaborationProfile);
        ArgumentNullException.ThrowIfNull(configuration);

        var facilityCapabilities = manifest.Facilities.ToDictionary(
            static facility => facility.Id,
            facility => ResolvedCapabilities(manifest, facility));
        var effectiveConfiguration = configuration.Configuration.ToDictionary(static item => (item.Subject, item.Setting));
        var decisions = ImmutableArray.CreateBuilder<InfrastructureTargetFacilityDecision>(
            definition.Definition.Workloads.Length + definition.Definition.Resources.Length);
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(configuration.Diagnostics.Length);
        diagnostics.AddRange(configuration.Diagnostics);

        foreach (var workload in definition.Definition.Workloads)
        {
            Select(
                workload.Id,
                InfrastructureNodeKind.Workload,
                workload.Requirements,
                manifest,
                facilityCapabilities,
                effectiveConfiguration,
                decisions,
                diagnostics);
        }
        foreach (var resource in definition.Definition.Resources)
        {
            Select(
                resource.Id,
                InfrastructureNodeKind.Resource,
                resource.Requirements,
                manifest,
                facilityCapabilities,
                effectiveConfiguration,
                decisions,
                diagnostics);
        }

        var normalizedDecisions = decisions.ToImmutable();
        var selectedEvidence = InfrastructureTargetEvidenceSelection.Select(manifest, normalizedDecisions);
        var closure = InfrastructureCapabilityCompiler.CompileWithEvidenceSelection(
            definition,
            manifest.Profile,
            manifest.Variant,
            bindingElaborationProfile,
            selectedEvidence,
            boundaryAcceptancePolicy);
        diagnostics.AddRange(closure.Diagnostics);
        return new(
            InfrastructureTargetFacilityPlan.CurrentSchemaVersion,
            definition,
            manifest,
            configuration,
            closure,
            normalizedDecisions,
            diagnostics.ToImmutable());
    }

    static void Select(
        InfrastructureNodeId node,
        InfrastructureNodeKind nodeKind,
        ImmutableArray<InfrastructureCapabilityRequirement> requirements,
        InfrastructureTargetFacilityManifest manifest,
        IReadOnlyDictionary<InfrastructureTargetFacilityId, HashSet<InfrastructureCapabilityId>> facilityCapabilities,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject Subject, InfrastructureSettingId Setting), InfrastructureEffectiveConfiguration> configuration,
        ImmutableArray<InfrastructureTargetFacilityDecision>.Builder decisions,
        ImmutableArray<DocumentValidationDiagnostic>.Builder diagnostics)
    {
        var candidates = manifest.Facilities
            .Where(facility => facility.NodeKind == nodeKind
                && requirements.All(requirement => facilityCapabilities[facility.Id].Contains(requirement.Capability)))
            .ToImmutableArray();
        if (configuration.TryGetValue(
                (InfrastructureTargetFacilitySelection.Subject(node), InfrastructureTargetFacilitySelection.Setting),
                out var selectedConfiguration))
        {
            var selected = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Id.Value, selectedConfiguration.Value, StringComparison.Ordinal));
            if (selected is not null)
            {
                decisions.Add(new(node, nodeKind, selected.Id));
                return;
            }

            diagnostics.Add(new(
                DiagnosticCodes.FacilitySelectionInvalid,
                DiagnosticSeverity.Error,
                $"Configured target facility '{selectedConfiguration.Value}' cannot satisfy logical node '{node.Value}'.",
                SchemaLocation: node.Value,
                Evidence: new(
                    stage: Stage,
                    subject: node.Value,
                    sourceReferences: FacilitySources(manifest, nodeKind, candidates),
                    resolutionOptions:
                    [
                        "Select one of the compatible facilities reported by target planning.",
                        "Change the target manifest only when concrete evidence establishes the configured facility's capabilities."
                    ],
                    expected: candidates.IsDefaultOrEmpty
                        ? "a facility preserving every required capability"
                        : string.Join(",", candidates.Select(static candidate => candidate.Id.Value)),
                    observed: $"{selectedConfiguration.Value} from {selectedConfiguration.Attribution.Authority}")));
            return;
        }
        if (candidates.Length == 1)
        {
            decisions.Add(new(node, nodeKind, candidates[0].Id));
            return;
        }

        var ambiguous = candidates.Length > 1;
        var code = ambiguous ? DiagnosticCodes.FacilityAmbiguous : DiagnosticCodes.FacilityUnavailable;
        diagnostics.Add(new(
            code,
            DiagnosticSeverity.Error,
            ambiguous
                ? $"Several target facilities satisfy logical node '{node.Value}', and no selection policy resolves them."
                : $"No target facility preserves every capability declared directly on logical node '{node.Value}'.",
            SchemaLocation: node.Value,
            Evidence: new(
                stage: Stage,
                subject: node.Value,
                sourceReferences: FacilitySources(manifest, nodeKind, candidates),
                resolutionOptions: ambiguous
                    ? [
                        "Supply an explicit attributable facility selection through compiler policy.",
                        "Refine facility capability evidence so exactly one construction preserves the node requirements."
                    ]
                    : [
                        "Add a target facility backed by evidence for every required capability.",
                        "Select another target whose facilities preserve the node requirements."
                    ],
                expected: requirements.IsDefaultOrEmpty
                    ? $"one explicit default {nodeKind.ToString().ToLowerInvariant()} facility"
                    : string.Join(",", requirements.Select(static requirement => requirement.Capability.Value)),
                observed: candidates.IsDefaultOrEmpty
                    ? "no matching target facility"
                    : string.Join(",", candidates.Select(static candidate => candidate.Id.Value)))));
    }

    static ImmutableArray<string> FacilitySources(
        InfrastructureTargetFacilityManifest manifest,
        InfrastructureNodeKind nodeKind,
        ImmutableArray<InfrastructureTargetFacility> candidates)
    {
        var relevant = candidates.IsDefaultOrEmpty
            ? manifest.Facilities.Where(facility => facility.NodeKind == nodeKind)
            : candidates;
        var authoringSources = relevant.SelectMany(facility =>
            manifest.SourceMap.Resolve(InfrastructureSourceReferences.Facility(facility.Id))
                .Concat(facility.Evidence.SelectMany(evidence =>
                    manifest.SourceMap.Resolve(InfrastructureSourceReferences.CapabilityEvidence(evidence)))));
        return
        [
            .. authoringSources
                .Append(InfrastructureSourceReferences.TargetFacilityManifest(manifest.ToReference()))
                .Select(static source => source.Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    static HashSet<InfrastructureCapabilityId> ResolvedCapabilities(
        InfrastructureTargetFacilityManifest manifest,
        InfrastructureTargetFacility facility)
    {
        var selectedEvidence = InfrastructureTargetEvidenceSelection.Select(manifest, [facility.Id]).ToHashSet();
        var manifestVariant = manifest.Profile.FindVariant(manifest.Variant)!;
        var facilityVariant = new InfrastructureCapabilityVariant(
            manifestVariant.Id,
            manifestVariant.Evidence.Where(evidence => selectedEvidence.Contains(evidence.Id)).ToImmutableArray(),
            manifestVariant.Rules,
            manifestVariant.OperatingBoundaries);
        return facilityVariant.Evidence
            .Select(static evidence => evidence.Capability)
            .Concat(facilityVariant.Rules.Select(static rule => rule.ProvidedCapability))
            .Distinct()
            .Where(capability => CapabilityResolver.Resolve(facilityVariant, capability).Status is
                CapabilityResolutionStatus.Success or CapabilityResolutionStatus.Ambiguous)
            .ToHashSet();
    }
}

static class InfrastructureTargetFacilityPlanFingerprinting
{
    internal static InfrastructureTargetFacilityPlanFingerprint Compute(
        string schemaVersion,
        InfrastructureDefinitionReference definition,
        InfrastructureTargetFacilityManifestReference manifest,
        InfrastructureConventionResolution configuration,
        InfrastructureCapabilityClosureReport capabilityClosure,
        ImmutableArray<InfrastructureTargetFacilityDecision> decisions,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                schemaVersion,
                definition,
                manifest,
                configuration,
                capabilityClosure,
                decisions,
                diagnostics),
            StrictDocumentJson.CreateOptions());
        return new(
            InfrastructureTargetFacilityPlanFingerprint.CurrentAlgorithm,
            InfrastructureTargetFacilityPlanFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        InfrastructureDefinitionReference Definition,
        InfrastructureTargetFacilityManifestReference Manifest,
        InfrastructureConventionResolution Configuration,
        InfrastructureCapabilityClosureReport CapabilityClosure,
        ImmutableArray<InfrastructureTargetFacilityDecision> Decisions,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);
}
