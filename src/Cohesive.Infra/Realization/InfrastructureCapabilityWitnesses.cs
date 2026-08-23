using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Stable diagnostics emitted while joining capability proofs to selected physical resources.</summary>
public static class InfrastructureCapabilityWitnessDiagnosticCodes
{
    /// <summary>A placement names an unknown node or a resource instead of a workload.</summary>
    public const string WorkloadPlacementUnknown = "infra.witnesses.workloadPlacement.unknown";

    /// <summary>A declared workload has no selected physical deployment resource.</summary>
    public const string WorkloadPlacementMissing = "infra.witnesses.workloadPlacement.missing";

    /// <summary>A witness names a requirement absent from the exact capability closure.</summary>
    public const string RequirementUnknown = "infra.witnesses.requirement.unknown";

    /// <summary>A witness cites evidence not selected by the exact requirement decision.</summary>
    public const string EvidenceUnexpected = "infra.witnesses.evidence.unexpected";

    /// <summary>An unavailable or unresolved capability decision claims physical evidence.</summary>
    public const string WitnessForUnavailableDecision = "infra.witnesses.decision.unavailable";

    /// <summary>An available capability decision lacks a demand-scoped witness for selected evidence.</summary>
    public const string EvidenceWitnessMissing = "infra.witnesses.evidence.missing";

    /// <summary>Selected proof witnesses do not cover every logical subject's physical resource.</summary>
    public const string SubjectPhysicalResourceMissing = "infra.witnesses.subject.physicalResourceMissing";
}

/// <summary>Selected physical deployment resource for one logical workload.</summary>
public sealed record InfrastructureWorkloadPlacement
{
    /// <summary>Creates a workload placement.</summary>
    /// <param name="workload">Exact logical workload node.</param>
    /// <param name="physicalResource">Selected target-native deployment-resource identity.</param>
    /// <param name="interpreter">Interpreter that selected or materialized the deployment resource.</param>
    /// <param name="sourceReferences">Attributable plan, artifact, provider, or import references.</param>
    /// <exception cref="ArgumentException">An identity or source reference is invalid or missing.</exception>
    [JsonConstructor]
    public InfrastructureWorkloadPlacement(
        InfrastructureNodeId workload,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureTargetId interpreter,
        ImmutableArray<string> sourceReferences)
    {
        if (string.IsNullOrWhiteSpace(workload.Value))
            throw new ArgumentException("A workload placement requires a logical workload identity.", nameof(workload));
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("A workload placement requires a physical deployment identity.", nameof(physicalResource));
        if (string.IsNullOrWhiteSpace(interpreter.Value))
            throw new ArgumentException("A workload placement requires an interpreter identity.", nameof(interpreter));

        Workload = workload;
        PhysicalResource = physicalResource;
        Interpreter = interpreter;
        SourceReferences = InfrastructureCapabilityCollections.StringSet(
            sourceReferences,
            nameof(sourceReferences),
            requireNonEmpty: true);
    }

    /// <summary>Exact logical workload node.</summary>
    public InfrastructureNodeId Workload { get; }

    /// <summary>Selected target-native deployment-resource identity.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Interpreter that selected or materialized the deployment resource.</summary>
    public InfrastructureTargetId Interpreter { get; }

    /// <summary>Attributable plan, artifact, provider, or import references in ordinal order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    /// <summary>Compares workload placements structurally.</summary>
    /// <param name="other">Other placement.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureWorkloadPlacement? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Workload == other.Workload
        && PhysicalResource == other.PhysicalResource
        && Interpreter == other.Interpreter
        && SourceReferences.SequenceEqual(other.SourceReferences, StringComparer.Ordinal);

    /// <summary>Returns a structural hash code for this placement.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Workload);
        hash.Add(PhysicalResource);
        hash.Add(Interpreter);
        foreach (var source in SourceReferences)
            hash.Add(source, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>Demand-scoped applicability witness for one selected capability-evidence assertion.</summary>
public sealed record InfrastructureCapabilityEvidenceWitness
{
    /// <summary>Creates a demand-scoped evidence witness.</summary>
    /// <param name="requirement">Exact declared or binding-derived requirement.</param>
    /// <param name="evidence">Selected reusable capability-evidence identity.</param>
    /// <param name="physicalResources">Physical resources to which the evidence applies for this demand.</param>
    /// <param name="sourceReferences">Attributable plan, artifact, provider, conformance, or import references.</param>
    /// <exception cref="ArgumentException">An identity, physical resource, or source reference is invalid, missing, or duplicated.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityEvidenceWitness(
        InfrastructureRequirementId requirement,
        InfrastructureCapabilityEvidenceId evidence,
        ImmutableArray<InfrastructurePhysicalResourceId> physicalResources,
        ImmutableArray<string> sourceReferences)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A capability witness requires an exact requirement identity.", nameof(requirement));
        if (string.IsNullOrWhiteSpace(evidence.Value))
            throw new ArgumentException("A capability witness requires an evidence identity.", nameof(evidence));

        Requirement = requirement;
        Evidence = evidence;
        PhysicalResources = InfrastructureCapabilityCollections.IdentitySet(
            physicalResources,
            static resource => resource.Value,
            nameof(physicalResources),
            requireNonEmpty: true);
        SourceReferences = InfrastructureCapabilityCollections.StringSet(
            sourceReferences,
            nameof(sourceReferences),
            requireNonEmpty: true);
    }

    /// <summary>Exact declared or binding-derived requirement.</summary>
    public InfrastructureRequirementId Requirement { get; }

    /// <summary>Selected reusable capability-evidence identity.</summary>
    public InfrastructureCapabilityEvidenceId Evidence { get; }

    /// <summary>Physical resources to which this evidence applies in physical-identity order.</summary>
    public ImmutableArray<InfrastructurePhysicalResourceId> PhysicalResources { get; }

    /// <summary>Attributable plan, artifact, provider, conformance, or import references in ordinal order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    /// <summary>Compares evidence witnesses structurally.</summary>
    /// <param name="other">Other witness.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureCapabilityEvidenceWitness? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Requirement == other.Requirement
        && Evidence == other.Evidence
        && PhysicalResources.SequenceEqual(other.PhysicalResources)
        && SourceReferences.SequenceEqual(other.SourceReferences, StringComparer.Ordinal);

    /// <summary>Returns a structural hash code for this witness.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Requirement);
        hash.Add(Evidence);
        foreach (var resource in PhysicalResources)
            hash.Add(resource);
        foreach (var source in SourceReferences)
            hash.Add(source, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>Machine-readable physical-applicability decision for one exact capability requirement.</summary>
public sealed record InfrastructureCapabilityWitnessDecision
{
    /// <summary>Creates a physical-applicability decision.</summary>
    /// <param name="requirement">Exact declared or binding-derived requirement.</param>
    /// <param name="capability">Provider-neutral demanded capability.</param>
    /// <param name="realization">Capability-closure disposition being witnessed.</param>
    /// <param name="subjects">Logical workload or resource subjects owned by the demand.</param>
    /// <param name="requiredEvidence">Complete transitive evidence selected by capability closure.</param>
    /// <param name="witnessedEvidence">Demand-scoped evidence identities supplied by the physical interpreter.</param>
    /// <param name="unexpectedEvidence">Witnessed identities absent from the selected capability proof.</param>
    /// <param name="expectedPhysicalResources">Selected physical resources that the proof must cover.</param>
    /// <param name="observedPhysicalResources">Physical resources cited by the supplied evidence witnesses.</param>
    /// <param name="unplacedSubjects">Logical workload subjects without a selected physical placement.</param>
    /// <param name="missingEvidence">Selected evidence identities without demand-scoped witnesses.</param>
    /// <param name="missingPhysicalResources">Subject resources absent from the complete witnessed proof.</param>
    /// <exception cref="ArgumentException">An identity, collection, or subset invariant is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="realization"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityWitnessDecision(
        InfrastructureRequirementId requirement,
        InfrastructureCapabilityId capability,
        CapabilityRealizationKind realization,
        ImmutableArray<InfrastructureNodeId> subjects,
        ImmutableArray<InfrastructureCapabilityEvidenceId> requiredEvidence = default,
        ImmutableArray<InfrastructureCapabilityEvidenceId> witnessedEvidence = default,
        ImmutableArray<InfrastructureCapabilityEvidenceId> unexpectedEvidence = default,
        ImmutableArray<InfrastructurePhysicalResourceId> expectedPhysicalResources = default,
        ImmutableArray<InfrastructurePhysicalResourceId> observedPhysicalResources = default,
        ImmutableArray<InfrastructureNodeId> unplacedSubjects = default,
        ImmutableArray<InfrastructureCapabilityEvidenceId> missingEvidence = default,
        ImmutableArray<InfrastructurePhysicalResourceId> missingPhysicalResources = default)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A capability-witness decision requires a requirement identity.", nameof(requirement));
        if (string.IsNullOrWhiteSpace(capability.Value))
            throw new ArgumentException("A capability-witness decision requires a capability identity.", nameof(capability));
        if (!Enum.IsDefined(realization))
            throw new ArgumentOutOfRangeException(nameof(realization), realization, "Unsupported capability realization kind.");

        Requirement = requirement;
        Capability = capability;
        Realization = realization;
        Subjects = InfrastructureCapabilityCollections.IdentitySet(
            subjects,
            static subject => subject.Value,
            nameof(subjects),
            requireNonEmpty: true);
        RequiredEvidence = EvidenceSet(requiredEvidence, nameof(requiredEvidence));
        WitnessedEvidence = EvidenceSet(witnessedEvidence, nameof(witnessedEvidence));
        UnexpectedEvidence = EvidenceSet(unexpectedEvidence, nameof(unexpectedEvidence));
        ExpectedPhysicalResources = PhysicalSet(expectedPhysicalResources, nameof(expectedPhysicalResources));
        ObservedPhysicalResources = PhysicalSet(observedPhysicalResources, nameof(observedPhysicalResources));
        UnplacedSubjects = InfrastructureCapabilityCollections.IdentitySet(
            unplacedSubjects,
            static subject => subject.Value,
            nameof(unplacedSubjects));
        MissingEvidence = EvidenceSet(missingEvidence, nameof(missingEvidence));
        MissingPhysicalResources = PhysicalSet(missingPhysicalResources, nameof(missingPhysicalResources));

        if (UnplacedSubjects.Any(subject => !Subjects.Contains(subject)))
            throw new ArgumentException("Unplaced subjects must belong to the exact capability demand.", nameof(unplacedSubjects));
        if (MissingEvidence.Any(evidence => !RequiredEvidence.Contains(evidence)))
            throw new ArgumentException("Missing evidence must belong to the selected capability proof.", nameof(missingEvidence));
        if (UnexpectedEvidence.Any(RequiredEvidence.Contains))
            throw new ArgumentException("Unexpected evidence cannot belong to the selected capability proof.", nameof(unexpectedEvidence));
        if (MissingPhysicalResources.Any(resource => !ExpectedPhysicalResources.Contains(resource)))
            throw new ArgumentException("Missing physical resources must belong to the demand's expected subject resources.", nameof(missingPhysicalResources));
        if (!UnexpectedEvidence.SequenceEqual(WitnessedEvidence.Where(evidence => !RequiredEvidence.Contains(evidence))))
            throw new ArgumentException("Unexpected evidence must exactly describe witnessed evidence outside the selected proof.", nameof(unexpectedEvidence));
        if (!MissingEvidence.SequenceEqual(RequiredEvidence.Where(evidence => !WitnessedEvidence.Contains(evidence))))
            throw new ArgumentException("Missing evidence must exactly describe selected evidence without a witness.", nameof(missingEvidence));
        if (!MissingPhysicalResources.SequenceEqual(ExpectedPhysicalResources.Where(resource => !ObservedPhysicalResources.Contains(resource))))
            throw new ArgumentException("Missing physical resources must exactly describe expected resources absent from the witnessed proof.", nameof(missingPhysicalResources));
    }

    /// <summary>Exact declared or binding-derived requirement.</summary>
    public InfrastructureRequirementId Requirement { get; }

    /// <summary>Provider-neutral demanded capability.</summary>
    public InfrastructureCapabilityId Capability { get; }

    /// <summary>Capability-closure disposition being witnessed.</summary>
    public CapabilityRealizationKind Realization { get; }

    /// <summary>Logical workload or resource subjects in node-identity order.</summary>
    public ImmutableArray<InfrastructureNodeId> Subjects { get; }

    /// <summary>Complete transitive evidence selected by capability closure.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidenceId> RequiredEvidence { get; }

    /// <summary>Demand-scoped evidence identities supplied by the physical interpreter.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidenceId> WitnessedEvidence { get; }

    /// <summary>Witnessed identities absent from the selected capability proof.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidenceId> UnexpectedEvidence { get; }

    /// <summary>Selected subject resources that the proof must cover.</summary>
    public ImmutableArray<InfrastructurePhysicalResourceId> ExpectedPhysicalResources { get; }

    /// <summary>All physical resources cited by supplied evidence witnesses.</summary>
    public ImmutableArray<InfrastructurePhysicalResourceId> ObservedPhysicalResources { get; }

    /// <summary>Logical workload subjects without a selected physical placement.</summary>
    public ImmutableArray<InfrastructureNodeId> UnplacedSubjects { get; }

    /// <summary>Selected evidence identities without demand-scoped witnesses.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidenceId> MissingEvidence { get; }

    /// <summary>Expected subject resources absent from the complete witnessed proof.</summary>
    public ImmutableArray<InfrastructurePhysicalResourceId> MissingPhysicalResources { get; }

    /// <summary>Whether an available capability proof is completely witnessed for every exact subject.</summary>
    [JsonIgnore]
    public bool IsComplete =>
        Realization is not CapabilityRealizationKind.Unavailable and not CapabilityRealizationKind.Unknown
        && UnplacedSubjects.IsDefaultOrEmpty
        && MissingEvidence.IsDefaultOrEmpty
        && MissingPhysicalResources.IsDefaultOrEmpty
        && UnexpectedEvidence.IsDefaultOrEmpty;

    /// <summary>Compares witness decisions structurally.</summary>
    /// <param name="other">Other decision.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureCapabilityWitnessDecision? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Requirement == other.Requirement
        && Capability == other.Capability
        && Realization == other.Realization
        && Subjects.SequenceEqual(other.Subjects)
        && RequiredEvidence.SequenceEqual(other.RequiredEvidence)
        && WitnessedEvidence.SequenceEqual(other.WitnessedEvidence)
        && UnexpectedEvidence.SequenceEqual(other.UnexpectedEvidence)
        && ExpectedPhysicalResources.SequenceEqual(other.ExpectedPhysicalResources)
        && ObservedPhysicalResources.SequenceEqual(other.ObservedPhysicalResources)
        && UnplacedSubjects.SequenceEqual(other.UnplacedSubjects)
        && MissingEvidence.SequenceEqual(other.MissingEvidence)
        && MissingPhysicalResources.SequenceEqual(other.MissingPhysicalResources);

    /// <summary>Returns a structural hash code for this decision.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Requirement);
        hash.Add(Capability);
        hash.Add(Realization);
        Add(ref hash, Subjects);
        Add(ref hash, RequiredEvidence);
        Add(ref hash, WitnessedEvidence);
        Add(ref hash, UnexpectedEvidence);
        Add(ref hash, ExpectedPhysicalResources);
        Add(ref hash, ObservedPhysicalResources);
        Add(ref hash, UnplacedSubjects);
        Add(ref hash, MissingEvidence);
        Add(ref hash, MissingPhysicalResources);
        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructureCapabilityEvidenceId> EvidenceSet(
        ImmutableArray<InfrastructureCapabilityEvidenceId> values,
        string parameterName) =>
        InfrastructureCapabilityCollections.IdentitySet(
            values,
            static evidence => evidence.Value,
            parameterName);

    static ImmutableArray<InfrastructurePhysicalResourceId> PhysicalSet(
        ImmutableArray<InfrastructurePhysicalResourceId> values,
        string parameterName) =>
        InfrastructureCapabilityCollections.IdentitySet(
            values,
            static resource => resource.Value,
            parameterName);

    static void Add<T>(ref HashCode hash, ImmutableArray<T> values)
    {
        foreach (var value in values)
            hash.Add(value);
    }
}

/// <summary>Deterministically joins capability closure to selected physical-resource witnesses.</summary>
public static class InfrastructureRealizationCompiler
{
    const string WitnessStage = "infrastructure-capability-witnessing";

    /// <summary>Compiles one exact physical-applicability realization candidate.</summary>
    /// <param name="capabilityClosure">Exact target-strategy capability closure.</param>
    /// <param name="lifecycle">Exact logical-resource lifecycle and physical-identity partition.</param>
    /// <param name="workloadPlacements">Selected physical deployment resource for each workload.</param>
    /// <param name="capabilityWitnesses">Demand-scoped physical witnesses for selected capability evidence.</param>
    /// <returns>An exactly fingerprinted realization candidate with structured witness decisions and diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="capabilityClosure"/> or <paramref name="lifecycle"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Inputs reference different definitions, or a placement or witness collection contains malformed duplicates.
    /// </exception>
    public static InfrastructureRealization Compile(
        InfrastructureCapabilityClosureReport capabilityClosure,
        InfrastructureLifecyclePlan lifecycle,
        ImmutableArray<InfrastructureWorkloadPlacement> workloadPlacements = default,
        ImmutableArray<InfrastructureCapabilityEvidenceWitness> capabilityWitnesses = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityClosure);
        ArgumentNullException.ThrowIfNull(lifecycle);
        if (capabilityClosure.Definition != lifecycle.Definition)
        {
            throw new ArgumentException(
                "Infrastructure capability closure and lifecycle ownership must reference the same exact definition.",
                nameof(lifecycle));
        }

        var placements = InfrastructureCapabilityWitnessCollections.NormalizePlacements(workloadPlacements);
        var witnesses = InfrastructureCapabilityWitnessCollections.NormalizeWitnesses(capabilityWitnesses);
        var evaluation = InfrastructureCapabilityWitnessEvaluator.Evaluate(
            capabilityClosure,
            lifecycle,
            placements,
            witnesses);
        return new(
            capabilityClosure,
            lifecycle,
            placements,
            witnesses,
            evaluation.Decisions,
            evaluation.Diagnostics,
            fingerprint: null);
    }

    internal static string Stage => WitnessStage;
}

static class InfrastructureCapabilityWitnessEvaluator
{
    internal static Evaluation Evaluate(
        InfrastructureCapabilityClosureReport closure,
        InfrastructureLifecyclePlan lifecycle,
        ImmutableArray<InfrastructureWorkloadPlacement> placements,
        ImmutableArray<InfrastructureCapabilityEvidenceWitness> witnesses)
    {
        var definition = closure.Definition;
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        var workloads = definition.Definition.Workloads.ToDictionary(static workload => workload.Id);
        var resources = definition.Definition.Resources.ToDictionary(static resource => resource.Id);
        var placementByWorkload = placements.ToDictionary(static placement => placement.Workload);
        var exactSources = ExactSources(closure);

        for (var index = 0; index < placements.Length; index++)
        {
            var placement = placements[index];
            if (workloads.ContainsKey(placement.Workload))
                continue;

            diagnostics.Add(new(
                InfrastructureCapabilityWitnessDiagnosticCodes.WorkloadPlacementUnknown,
                DiagnosticSeverity.Error,
                $"Workload placement '{placement.Workload.Value}' does not name a declared workload.",
                Location: $"/workloadPlacements/{index.ToString(CultureInfo.InvariantCulture)}/workload",
                SchemaLocation: placement.Workload.Value,
                Evidence: new(
                    stage: InfrastructureRealizationCompiler.Stage,
                    subject: placement.Workload.Value,
                    sourceReferences: Merge(exactSources, placement.SourceReferences),
                    resolutionOptions: ["Remove the placement or bind it to an exact declared workload."],
                    expected: "a workload in the exact infrastructure definition",
                    observed: "unknown or non-workload node")));
        }

        for (var index = 0; index < definition.Definition.Workloads.Length; index++)
        {
            var workload = definition.Definition.Workloads[index];
            if (placementByWorkload.ContainsKey(workload.Id))
                continue;

            diagnostics.Add(new(
                InfrastructureCapabilityWitnessDiagnosticCodes.WorkloadPlacementMissing,
                DiagnosticSeverity.Error,
                $"Workload '{workload.Id.Value}' has no selected physical deployment resource.",
                Location: $"/definition/workloads/{index.ToString(CultureInfo.InvariantCulture)}",
                SchemaLocation: workload.Id.Value,
                Evidence: new(
                    stage: InfrastructureRealizationCompiler.Stage,
                    subject: workload.Id.Value,
                    sourceReferences: exactSources,
                    resolutionOptions: ["Select a physical workload deployment resource through an attributable interpreter."],
                    expected: "one exact workload placement",
                    observed: "no placement")));
        }

        var contexts = RequirementContexts(closure);
        var witnessesByRequirement = witnesses
            .GroupBy(static witness => witness.Requirement)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());

        for (var index = 0; index < witnesses.Length; index++)
        {
            var witness = witnesses[index];
            if (contexts.ContainsKey(witness.Requirement))
                continue;

            diagnostics.Add(new(
                InfrastructureCapabilityWitnessDiagnosticCodes.RequirementUnknown,
                DiagnosticSeverity.Error,
                $"Capability witness requirement '{witness.Requirement.Value}' is absent from the exact capability closure.",
                Location: $"/capabilityWitnesses/{index.ToString(CultureInfo.InvariantCulture)}/requirement",
                SchemaLocation: witness.Requirement.Value,
                Evidence: new(
                    stage: InfrastructureRealizationCompiler.Stage,
                    subject: witness.Requirement.Value,
                    sourceReferences: Merge(exactSources, witness.SourceReferences),
                    resolutionOptions: ["Remove the stale witness or regenerate it from the exact capability closure."],
                    expected: "a requirement in the exact capability closure",
                    observed: "unknown requirement")));
        }

        var resourcePhysical = lifecycle.Bindings
            .GroupBy(static binding => binding.Resource)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().PhysicalResource);
        var decisions = ImmutableArray.CreateBuilder<InfrastructureCapabilityWitnessDecision>(closure.Decisions.Length);

        foreach (var capabilityDecision in closure.Decisions)
        {
            var context = contexts[capabilityDecision.Requirement];
            var requirementWitnesses = witnessesByRequirement.GetValueOrDefault(capabilityDecision.Requirement, []);
            var requiredEvidence = capabilityDecision.Evidence;
            var witnessedEvidence = EvidenceSet(requirementWitnesses.Select(static witness => witness.Evidence));
            var unexpectedEvidence = EvidenceSet(
                witnessedEvidence.Where(evidence => !requiredEvidence.Contains(evidence)));
            var missingEvidence = EvidenceSet(
                requiredEvidence.Where(evidence => !witnessedEvidence.Contains(evidence)));
            var observedPhysical = PhysicalSet(
                requirementWitnesses.SelectMany(static witness => witness.PhysicalResources));
            var expectedPhysical = ImmutableArray.CreateBuilder<InfrastructurePhysicalResourceId>();
            var unplacedSubjects = ImmutableArray.CreateBuilder<InfrastructureNodeId>();

            foreach (var subject in context.Subjects)
            {
                if (workloads.ContainsKey(subject))
                {
                    if (placementByWorkload.TryGetValue(subject, out var placement))
                        expectedPhysical.Add(placement.PhysicalResource);
                    else
                        unplacedSubjects.Add(subject);
                }
                else if (resources.ContainsKey(subject))
                {
                    expectedPhysical.Add(resourcePhysical[subject]);
                }
            }

            var normalizedExpected = PhysicalSet(expectedPhysical);
            var missingPhysical = PhysicalSet(
                normalizedExpected.Where(resource => !observedPhysical.Contains(resource)));

            if (!capabilityDecision.IsAvailable && !requirementWitnesses.IsDefaultOrEmpty)
            {
                diagnostics.Add(Diagnostic(
                    InfrastructureCapabilityWitnessDiagnosticCodes.WitnessForUnavailableDecision,
                    $"Requirement '{capabilityDecision.Requirement.Value}' is unavailable or unresolved and cannot claim physical witnesses.",
                    context,
                    exactSources,
                    requirementWitnesses,
                    expected: "no physical evidence until capability closure selects an available proof",
                    observed: $"witnessed evidence: {Display(witnessedEvidence.Select(static evidence => evidence.Value))}",
                    resolutionOptions:
                    [
                        "Remove the witnesses until capability closure selects an available exact proof.",
                        "Resolve the upstream capability mismatch before physical witnessing."
                    ]));
            }
            else if (capabilityDecision.IsAvailable)
            {
                if (!missingEvidence.IsDefaultOrEmpty)
                {
                    diagnostics.Add(Diagnostic(
                        InfrastructureCapabilityWitnessDiagnosticCodes.EvidenceWitnessMissing,
                        $"Requirement '{capabilityDecision.Requirement.Value}' lacks physical witnesses for selected evidence {Display(missingEvidence.Select(static evidence => evidence.Value))}.",
                        context,
                        exactSources,
                        requirementWitnesses,
                        expected: $"demand-scoped witnesses for {Display(requiredEvidence.Select(static evidence => evidence.Value))}",
                        observed: $"witnessed evidence: {Display(witnessedEvidence.Select(static evidence => evidence.Value))}",
                        resolutionOptions: ["Bind every selected evidence assertion to exact physical resources for this requirement."]));
                }
                if (!unexpectedEvidence.IsDefaultOrEmpty)
                {
                    diagnostics.Add(Diagnostic(
                        InfrastructureCapabilityWitnessDiagnosticCodes.EvidenceUnexpected,
                        $"Requirement '{capabilityDecision.Requirement.Value}' cites evidence absent from its selected capability proof.",
                        context,
                        exactSources,
                        requirementWitnesses,
                        expected: Display(requiredEvidence.Select(static evidence => evidence.Value)),
                        observed: Display(unexpectedEvidence.Select(static evidence => evidence.Value)),
                        resolutionOptions: ["Remove stale witnesses or recompile them from the exact capability decision."]));
                }
                if (!missingPhysical.IsDefaultOrEmpty)
                {
                    diagnostics.Add(Diagnostic(
                        InfrastructureCapabilityWitnessDiagnosticCodes.SubjectPhysicalResourceMissing,
                        $"Requirement '{capabilityDecision.Requirement.Value}' does not cover every selected subject resource.",
                        context,
                        exactSources,
                        requirementWitnesses,
                        expected: Display(normalizedExpected.Select(static resource => resource.Value)),
                        observed: Display(observedPhysical.Select(static resource => resource.Value)),
                        resolutionOptions: ["Bind the selected proof to every workload and resource physical identity owned by this demand."]));
                }
            }

            decisions.Add(new(
                capabilityDecision.Requirement,
                capabilityDecision.Capability,
                capabilityDecision.Realization,
                context.Subjects,
                requiredEvidence,
                witnessedEvidence,
                unexpectedEvidence,
                normalizedExpected,
                observedPhysical,
                unplacedSubjects.ToImmutable(),
                missingEvidence,
                missingPhysical));
        }

        return new(
            decisions.MoveToImmutable(),
            DocumentValidationDiagnostics.Normalize(diagnostics.ToImmutable()));
    }

    internal static ImmutableDictionary<InfrastructureRequirementId, RequirementContext> RequirementContexts(
        InfrastructureCapabilityClosureReport closure)
    {
        var contexts = ImmutableDictionary.CreateBuilder<InfrastructureRequirementId, RequirementContext>();
        var definition = closure.Definition.Definition;

        for (var workloadIndex = 0; workloadIndex < definition.Workloads.Length; workloadIndex++)
        {
            var workload = definition.Workloads[workloadIndex];
            for (var requirementIndex = 0; requirementIndex < workload.Requirements.Length; requirementIndex++)
            {
                var requirement = workload.Requirements[requirementIndex];
                contexts.Add(requirement.Id, new(
                    requirement,
                    [workload.Id],
                    $"/definition/workloads/{workloadIndex.ToString(CultureInfo.InvariantCulture)}/requirements/{requirementIndex.ToString(CultureInfo.InvariantCulture)}/capability"));
            }
        }

        for (var resourceIndex = 0; resourceIndex < definition.Resources.Length; resourceIndex++)
        {
            var resource = definition.Resources[resourceIndex];
            for (var requirementIndex = 0; requirementIndex < resource.Requirements.Length; requirementIndex++)
            {
                var requirement = resource.Requirements[requirementIndex];
                contexts.Add(requirement.Id, new(
                    requirement,
                    [resource.Id],
                    $"/definition/resources/{resourceIndex.ToString(CultureInfo.InvariantCulture)}/requirements/{requirementIndex.ToString(CultureInfo.InvariantCulture)}/capability"));
            }
        }

        var bindings = definition.Bindings.ToDictionary(static binding => binding.Id);
        foreach (var obligation in closure.BindingElaboration.Obligations)
        {
            var binding = bindings[obligation.Binding];
            contexts.Add(obligation.Requirement.Id, new(
                obligation.Requirement,
                NodeSet([binding.Source, binding.Target]),
                obligation.Location));
        }

        return contexts.ToImmutable();
    }

    static DocumentValidationDiagnostic Diagnostic(
        string code,
        string message,
        RequirementContext context,
        ImmutableArray<string> exactSources,
        ImmutableArray<InfrastructureCapabilityEvidenceWitness> witnesses,
        string expected,
        string observed,
        ImmutableArray<string> resolutionOptions) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            Location: context.Location,
            SchemaLocation: context.Requirement.Capability.Value,
            Evidence: new(
                stage: InfrastructureRealizationCompiler.Stage,
                subject: context.Requirement.Id.Value,
                relatedLocations:
                [
                    .. witnesses.SelectMany(static witness => witness.PhysicalResources)
                        .Select(static resource => $"physical-resource/{Uri.EscapeDataString(resource.Value)}")
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                ],
                sourceReferences: Merge(
                    exactSources,
                    witnesses.SelectMany(static witness => witness.SourceReferences).ToImmutableArray()),
                resolutionOptions: resolutionOptions,
                expected: expected,
                observed: observed));

    static ImmutableArray<string> ExactSources(InfrastructureCapabilityClosureReport closure) =>
    [
        InfrastructureDiagnosticReferences.Definition(closure.Definition),
        ProfileReference(closure.Profile),
        InfrastructureDiagnosticReferences.BindingProfileReference(closure.BindingElaboration.Profile)
    ];

    static string ProfileReference(InfrastructureCapabilityProfileReference profile) =>
        $"{profile.Id.Value}#{profile.Fingerprint.Algorithm}:{profile.Fingerprint.Canonicalization}:{profile.Fingerprint.Value}";

    static ImmutableArray<string> Merge(ImmutableArray<string> left, ImmutableArray<string> right) =>
    [
        .. left.Concat(right).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
    ];

    static ImmutableArray<InfrastructureNodeId> NodeSet(IEnumerable<InfrastructureNodeId> values) =>
        InfrastructureCapabilityCollections.IdentitySet(
            [.. values],
            static value => value.Value,
            nameof(values),
            requireNonEmpty: true);

    static ImmutableArray<InfrastructureCapabilityEvidenceId> EvidenceSet(
        IEnumerable<InfrastructureCapabilityEvidenceId> values) =>
        InfrastructureCapabilityCollections.IdentitySet(
            [.. values.Distinct()],
            static value => value.Value,
            nameof(values));

    static ImmutableArray<InfrastructurePhysicalResourceId> PhysicalSet(
        IEnumerable<InfrastructurePhysicalResourceId> values) =>
        InfrastructureCapabilityCollections.IdentitySet(
            [.. values.Distinct()],
            static value => value.Value,
            nameof(values));

    static string Display(IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0
            ? "none"
            : string.Join(", ", materialized.Select(static value => $"'{value}'"));
    }

    internal sealed record Evaluation(
        ImmutableArray<InfrastructureCapabilityWitnessDecision> Decisions,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);

    internal sealed record RequirementContext(
        InfrastructureCapabilityRequirement Requirement,
        ImmutableArray<InfrastructureNodeId> Subjects,
        string Location);
}

static class InfrastructureCapabilityWitnessCollections
{
    internal static ImmutableArray<InfrastructureWorkloadPlacement> NormalizePlacements(
        ImmutableArray<InfrastructureWorkloadPlacement> placements)
    {
        if (placements.IsDefaultOrEmpty)
            return [];
        if (placements.Any(static placement => placement is null))
            throw new ArgumentException("Infrastructure workload placements cannot contain null.", nameof(placements));

        var ordered = placements.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Workload.Value, right.Workload.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Workload == ordered[index].Workload)
                throw new ArgumentException($"Workload placement '{ordered[index].Workload.Value}' is duplicated.", nameof(placements));
        }
        return ordered;
    }

    internal static ImmutableArray<InfrastructureCapabilityEvidenceWitness> NormalizeWitnesses(
        ImmutableArray<InfrastructureCapabilityEvidenceWitness> witnesses)
    {
        if (witnesses.IsDefaultOrEmpty)
            return [];
        if (witnesses.Any(static witness => witness is null))
            throw new ArgumentException("Infrastructure capability witnesses cannot contain null.", nameof(witnesses));

        var ordered = witnesses.Sort(static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.Requirement.Value, right.Requirement.Value);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Evidence.Value, right.Evidence.Value);
        });
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Requirement == ordered[index].Requirement
                && ordered[index - 1].Evidence == ordered[index].Evidence)
            {
                throw new ArgumentException(
                    $"Capability witness '{ordered[index].Requirement.Value}/{ordered[index].Evidence.Value}' is duplicated.",
                    nameof(witnesses));
            }
        }
        return ordered;
    }

    internal static ImmutableArray<InfrastructureCapabilityWitnessDecision> NormalizeDecisions(
        ImmutableArray<InfrastructureCapabilityWitnessDecision> decisions)
    {
        if (decisions.IsDefaultOrEmpty)
            return [];
        if (decisions.Any(static decision => decision is null))
            throw new ArgumentException("Infrastructure capability-witness decisions cannot contain null.", nameof(decisions));

        var ordered = decisions.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Requirement.Value, right.Requirement.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Requirement == ordered[index].Requirement)
                throw new ArgumentException($"Capability-witness decision '{ordered[index].Requirement.Value}' is duplicated.", nameof(decisions));
        }
        return ordered;
    }
}

static class InfrastructureRealizationFingerprinting
{
    internal static InfrastructureRealizationFingerprint Compute(
        InfrastructureCapabilityClosureReport closure,
        InfrastructureLifecyclePlan lifecycle,
        ImmutableArray<InfrastructureWorkloadPlacement> placements,
        ImmutableArray<InfrastructureCapabilityEvidenceWitness> witnesses,
        ImmutableArray<InfrastructureCapabilityWitnessDecision> decisions)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                closure.Profile,
                closure.BindingElaboration.Fingerprint,
                closure.Target,
                closure.Variant,
                closure.Decisions,
                lifecycle.Definition.ToReference(),
                lifecycle.Bindings,
                placements,
                witnesses,
                decisions),
            StrictDocumentJson.CreateOptions());
        return new(
            InfrastructureRealizationFingerprint.CurrentAlgorithm,
            InfrastructureRealizationFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        InfrastructureCapabilityProfileReference Profile,
        InfrastructureBindingElaborationFingerprint BindingElaboration,
        InfrastructureTargetId Target,
        InfrastructureCapabilityVariantId Variant,
        ImmutableArray<InfrastructureCapabilityDecision> CapabilityDecisions,
        InfrastructureDefinitionReference Definition,
        ImmutableArray<InfrastructureResourceLifecycleBinding> LifecycleBindings,
        ImmutableArray<InfrastructureWorkloadPlacement> WorkloadPlacements,
        ImmutableArray<InfrastructureCapabilityEvidenceWitness> CapabilityWitnesses,
        ImmutableArray<InfrastructureCapabilityWitnessDecision> WitnessDecisions);
}
