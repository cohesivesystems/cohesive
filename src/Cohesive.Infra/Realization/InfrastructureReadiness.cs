using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>One canonical readiness dependency lowered to exact physical resources.</summary>
public sealed record InfrastructureReadinessObligation
{
    /// <summary>Creates an exact physical readiness obligation.</summary>
    /// <param name="dependency">Canonical semantic readiness-dependency identity.</param>
    /// <param name="subject">Logical node whose readiness is gated.</param>
    /// <param name="subjectPhysicalResource">Exact physical realization of the subject.</param>
    /// <param name="requiredNode">Logical dependency that must be ready.</param>
    /// <param name="requiredPhysicalResource">Exact physical realization of the dependency.</param>
    /// <exception cref="ArgumentException">Any identity is default or the logical obligation is self-referential.</exception>
    [JsonConstructor]
    public InfrastructureReadinessObligation(
        InfrastructureReadinessDependencyId dependency,
        InfrastructureNodeId subject,
        InfrastructurePhysicalResourceId subjectPhysicalResource,
        InfrastructureNodeId requiredNode,
        InfrastructurePhysicalResourceId requiredPhysicalResource)
    {
        if (string.IsNullOrWhiteSpace(dependency.Value))
            throw new ArgumentException("A readiness obligation requires its semantic dependency identity.", nameof(dependency));

        if (string.IsNullOrWhiteSpace(subject.Value))
            throw new ArgumentException("A readiness obligation requires a logical subject.", nameof(subject));

        if (string.IsNullOrWhiteSpace(subjectPhysicalResource.Value))
            throw new ArgumentException("A readiness obligation requires the subject's physical resource.", nameof(subjectPhysicalResource));

        if (string.IsNullOrWhiteSpace(requiredNode.Value))
            throw new ArgumentException("A readiness obligation requires a logical dependency.", nameof(requiredNode));

        if (string.IsNullOrWhiteSpace(requiredPhysicalResource.Value))
            throw new ArgumentException("A readiness obligation requires the dependency's physical resource.", nameof(requiredPhysicalResource));

        if (subject == requiredNode)
            throw new ArgumentException("A readiness obligation cannot require its own subject.", nameof(requiredNode));

        Dependency = dependency;
        Subject = subject;
        SubjectPhysicalResource = subjectPhysicalResource;
        RequiredNode = requiredNode;
        RequiredPhysicalResource = requiredPhysicalResource;
    }

    /// <summary>Canonical semantic readiness-dependency identity.</summary>
    public InfrastructureReadinessDependencyId Dependency { get; }

    /// <summary>Logical node whose readiness is gated.</summary>
    public InfrastructureNodeId Subject { get; }

    /// <summary>Exact physical realization of the subject.</summary>
    public InfrastructurePhysicalResourceId SubjectPhysicalResource { get; }

    /// <summary>Logical dependency that must be ready.</summary>
    public InfrastructureNodeId RequiredNode { get; }

    /// <summary>Exact physical realization of the dependency.</summary>
    public InfrastructurePhysicalResourceId RequiredPhysicalResource { get; }
}

static class InfrastructureReadinessObligationCompiler
{
    internal static ImmutableArray<InfrastructureReadinessObligation> Compile(
        InfrastructureDefinition definition,
        InfrastructureLifecyclePlan lifecycle,
        ImmutableArray<InfrastructureWorkloadPlacement> placements,
        ImmutableArray<InfrastructureWorkloadNonParticipation> nonParticipatingWorkloads)
    {
        var physicalResources = PhysicalResources(definition, lifecycle, placements);
        var excluded = nonParticipatingWorkloads.Select(static decision => decision.Workload).ToHashSet();

        var obligations = ImmutableArray.CreateBuilder<InfrastructureReadinessObligation>(
            ParticipatingDependencyCount(definition, nonParticipatingWorkloads));
        foreach (var dependency in definition.ReadinessDependencies)
        {
            if (excluded.Contains(dependency.Subject)
                || !physicalResources.TryGetValue(dependency.Subject, out var subject)
                || !physicalResources.TryGetValue(dependency.Dependency, out var required))
            {
                continue;
            }

            obligations.Add(new(
                dependency.Id,
                dependency.Subject,
                subject,
                dependency.Dependency,
                required));
        }
        return obligations.Count == obligations.Capacity
            ? obligations.MoveToImmutable()
            : obligations.ToImmutable();
    }

    internal static int ParticipatingDependencyCount(
        InfrastructureDefinition definition,
        ImmutableArray<InfrastructureWorkloadNonParticipation> nonParticipatingWorkloads)
    {
        var excluded = nonParticipatingWorkloads.Select(static decision => decision.Workload).ToHashSet();
        return definition.ReadinessDependencies.Count(dependency => !excluded.Contains(dependency.Subject));
    }

    internal static Dictionary<InfrastructureNodeId, InfrastructurePhysicalResourceId> PhysicalResources(
        InfrastructureDefinition definition,
        InfrastructureLifecyclePlan lifecycle,
        ImmutableArray<InfrastructureWorkloadPlacement> placements)
    {
        var nodes = definition.Workloads.Select(static workload => workload.Id)
            .Concat(definition.Resources.Select(static resource => resource.Id))
            .ToHashSet();
        var physicalResources = new Dictionary<InfrastructureNodeId, InfrastructurePhysicalResourceId>(nodes.Count);
        foreach (var placement in placements)
        {
            if (nodes.Contains(placement.Workload))
                physicalResources.TryAdd(placement.Workload, placement.PhysicalResource);
        }
        foreach (var binding in lifecycle.Bindings)
        {
            if (nodes.Contains(binding.Resource))
                physicalResources.TryAdd(binding.Resource, binding.PhysicalResource);
        }
        return physicalResources;
    }

    internal static ImmutableArray<InfrastructureReadinessObligation> Normalize(
        ImmutableArray<InfrastructureReadinessObligation> obligations)
    {
        if (obligations.IsDefaultOrEmpty)
            return [];

        if (obligations.Any(static obligation => obligation is null))
            throw new ArgumentException("Infrastructure readiness obligations cannot contain null.", nameof(obligations));

        var ordered = CanonicalDocumentCollections.SortIfNeeded(
            obligations,
            static (left, right) => StringComparer.Ordinal.Compare(left.Dependency.Value, right.Dependency.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Dependency == ordered[index].Dependency)
                throw new ArgumentException($"Readiness obligation '{ordered[index].Dependency.Value}' is duplicated.", nameof(obligations));
        }
        return ordered;
    }
}

/// <summary>One attributable observation of an exact physical infrastructure resource.</summary>
public sealed record InfrastructureResourceObservation
{
    /// <summary>Creates one physical-resource observation.</summary>
    /// <param name="physicalResource">Exact physical resource being observed.</param>
    /// <param name="health">Observed operational health.</param>
    /// <param name="readiness">Observed ability to admit or continue work.</param>
    /// <param name="observedAtUtc">UTC time of the source observation.</param>
    /// <param name="sourceReferences">Attributable provider or runtime evidence sources.</param>
    /// <param name="diagnostics">Optional structured adapter diagnostics.</param>
    /// <exception cref="ArgumentException">An identity, time, source, or diagnostic collection is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="health"/> or <paramref name="readiness"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureResourceObservation(
        InfrastructurePhysicalResourceId physicalResource,
        ExecutionHealthStatus health,
        ExecutionReadinessStatus readiness,
        DateTimeOffset observedAtUtc,
        ImmutableArray<SourceReference> sourceReferences,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("An infrastructure observation requires a physical resource.", nameof(physicalResource));

        if (!Enum.IsDefined(health))
            throw new ArgumentOutOfRangeException(nameof(health), health, "Unsupported infrastructure health observation.");

        if (!Enum.IsDefined(readiness))
            throw new ArgumentOutOfRangeException(nameof(readiness), readiness, "Unsupported infrastructure readiness observation.");

        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Infrastructure observations must use UTC.", nameof(observedAtUtc));

        PhysicalResource = physicalResource;
        Health = health;
        Readiness = readiness;
        ObservedAtUtc = observedAtUtc;
        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
    }

    /// <summary>Exact physical resource being observed.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Observed operational health.</summary>
    public ExecutionHealthStatus Health { get; }

    /// <summary>Observed ability to admit or continue work.</summary>
    public ExecutionReadinessStatus Readiness { get; }

    /// <summary>UTC time of the source observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Attributable provider or runtime evidence sources in deterministic order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Structured adapter diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Compares observations structurally.</summary>
    /// <param name="other">Other physical-resource observation.</param>
    /// <returns><see langword="true"/> when every scalar and normalized collection is equal.</returns>
    public bool Equals(InfrastructureResourceObservation? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && PhysicalResource == other.PhysicalResource
        && Health == other.Health
        && Readiness == other.Readiness
        && ObservedAtUtc == other.ObservedAtUtc
        && SourceReferences.SequenceEqual(other.SourceReferences)
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code for this observation.</summary>
    /// <returns>A hash derived from every scalar and normalized collection.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PhysicalResource);
        hash.Add(Health);
        hash.Add(Readiness);
        hash.Add(ObservedAtUtc);
        Add(ref hash, SourceReferences);
        Add(ref hash, Diagnostics);
        return hash.ToHashCode();
    }

    static void Add<T>(ref HashCode hash, ImmutableArray<T> values)
    {
        foreach (var value in values)
            hash.Add(value);
    }
}

/// <summary>Derived readiness decision for one logical node and its exact physical realization.</summary>
public sealed record InfrastructureReadinessDecision
{
    /// <summary>Creates one normalized readiness decision.</summary>
    /// <param name="node">Canonical logical node.</param>
    /// <param name="kind">Canonical node family.</param>
    /// <param name="physicalResource">Exact physical realization of the node.</param>
    /// <param name="observedHealth">Health observed directly for the physical resource.</param>
    /// <param name="observedReadiness">Readiness observed directly for the physical resource.</param>
    /// <param name="effectiveReadiness">Readiness after applying exact dependency obligations.</param>
    /// <param name="blockingDependencies">Exact dependencies that are not ready.</param>
    /// <param name="unknownDependencies">Exact dependencies whose readiness is not established.</param>
    /// <exception cref="ArgumentException">An identity or dependency collection is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A kind, health, or readiness value is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureReadinessDecision(
        InfrastructureNodeId node,
        InfrastructureNodeKind kind,
        InfrastructurePhysicalResourceId physicalResource,
        ExecutionHealthStatus observedHealth,
        ExecutionReadinessStatus observedReadiness,
        ExecutionReadinessStatus effectiveReadiness,
        ImmutableArray<InfrastructurePhysicalResourceId> blockingDependencies = default,
        ImmutableArray<InfrastructurePhysicalResourceId> unknownDependencies = default)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A readiness decision requires a logical node.", nameof(node));

        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported infrastructure node kind.");

        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("A readiness decision requires an exact physical resource.", nameof(physicalResource));

        if (!Enum.IsDefined(observedHealth))
            throw new ArgumentOutOfRangeException(nameof(observedHealth), observedHealth, "Unsupported observed health.");

        if (!Enum.IsDefined(observedReadiness))
            throw new ArgumentOutOfRangeException(nameof(observedReadiness), observedReadiness, "Unsupported observed readiness.");

        if (!Enum.IsDefined(effectiveReadiness))
            throw new ArgumentOutOfRangeException(nameof(effectiveReadiness), effectiveReadiness, "Unsupported effective readiness.");

        Node = node;
        Kind = kind;
        PhysicalResource = physicalResource;
        ObservedHealth = observedHealth;
        ObservedReadiness = observedReadiness;
        EffectiveReadiness = effectiveReadiness;
        BlockingDependencies = NormalizePhysicalResources(blockingDependencies, nameof(blockingDependencies));
        UnknownDependencies = NormalizePhysicalResources(unknownDependencies, nameof(unknownDependencies));
    }

    /// <summary>Canonical logical node.</summary>
    public InfrastructureNodeId Node { get; }

    /// <summary>Canonical node family.</summary>
    public InfrastructureNodeKind Kind { get; }

    /// <summary>Exact physical realization of the node.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Health observed directly for the physical resource.</summary>
    public ExecutionHealthStatus ObservedHealth { get; }

    /// <summary>Readiness observed directly for the physical resource.</summary>
    public ExecutionReadinessStatus ObservedReadiness { get; }

    /// <summary>Readiness after applying exact dependency obligations.</summary>
    public ExecutionReadinessStatus EffectiveReadiness { get; }

    /// <summary>Exact dependencies that are not ready.</summary>
    public ImmutableArray<InfrastructurePhysicalResourceId> BlockingDependencies { get; }

    /// <summary>Exact dependencies whose readiness is not established.</summary>
    public ImmutableArray<InfrastructurePhysicalResourceId> UnknownDependencies { get; }

    /// <summary>Whether the node is effectively ready.</summary>
    [JsonIgnore]
    public bool IsReady => EffectiveReadiness == ExecutionReadinessStatus.Ready;

    /// <summary>Compares readiness decisions structurally.</summary>
    /// <param name="other">Other decision.</param>
    /// <returns><see langword="true"/> when every scalar and normalized dependency collection is equal.</returns>
    public bool Equals(InfrastructureReadinessDecision? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Node == other.Node
        && Kind == other.Kind
        && PhysicalResource == other.PhysicalResource
        && ObservedHealth == other.ObservedHealth
        && ObservedReadiness == other.ObservedReadiness
        && EffectiveReadiness == other.EffectiveReadiness
        && BlockingDependencies.SequenceEqual(other.BlockingDependencies)
        && UnknownDependencies.SequenceEqual(other.UnknownDependencies);

    /// <summary>Returns a structural hash code for this decision.</summary>
    /// <returns>A hash derived from every scalar and normalized dependency collection.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Node);
        hash.Add(Kind);
        hash.Add(PhysicalResource);
        hash.Add(ObservedHealth);
        hash.Add(ObservedReadiness);
        hash.Add(EffectiveReadiness);
        foreach (var dependency in BlockingDependencies)
            hash.Add(dependency);

        foreach (var dependency in UnknownDependencies)
            hash.Add(dependency);

        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructurePhysicalResourceId> NormalizePhysicalResources(
        ImmutableArray<InfrastructurePhysicalResourceId> values,
        string parameterName)
    {
        if (values.IsDefaultOrEmpty)
            return [];

        if (values.Any(static value => string.IsNullOrWhiteSpace(value.Value)))
            throw new ArgumentException("Readiness decision dependencies cannot be default.", parameterName);

        var ordered = CanonicalDocumentCollections.SortIfNeeded(
            values,
            static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1] == ordered[index])
                throw new ArgumentException($"Readiness decision dependency '{ordered[index].Value}' is duplicated.", parameterName);
        }
        return ordered;
    }
}

/// <summary>Deterministic fingerprint of an exact infrastructure-readiness assessment.</summary>
public sealed record InfrastructureReadinessAssessmentFingerprint
{
    /// <summary>Digest algorithm used by the current assessment fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current assessment fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-readiness-assessment/v1-c14n/v1";

    /// <summary>Creates readiness-assessment fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureReadinessAssessmentFingerprint(string algorithm, string canonicalization, string value)
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

/// <summary>Portable assessment of exact infrastructure observations against canonical readiness obligations.</summary>
public sealed record InfrastructureReadinessAssessment
{
    /// <summary>Current portable assessment schema.</summary>
    public const string CurrentSchemaVersion = "cohesive.infra.readiness-assessment/1";

    /// <summary>Creates or restores an exact readiness assessment.</summary>
    /// <param name="schemaVersion">Exact portable assessment schema.</param>
    /// <param name="realization">Exact physical realization being assessed.</param>
    /// <param name="observations">Attributable physical-resource observations.</param>
    /// <param name="decisions">Derived node decisions.</param>
    /// <param name="diagnostics">Structured assessment and adapter diagnostics.</param>
    /// <param name="fingerprint">Persisted assessment fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema, collection, or supplied fingerprint is not canonical.</exception>
    [JsonConstructor]
    public InfrastructureReadinessAssessment(
        string schemaVersion,
        InfrastructureRealizationReference realization,
        ImmutableArray<InfrastructureResourceObservation> observations,
        ImmutableArray<InfrastructureReadinessDecision> decisions,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        InfrastructureReadinessAssessmentFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Readiness-assessment schema '{SchemaVersion}' is unsupported.", nameof(schemaVersion));

        Realization = Guard.RequireNotNull(realization);
        Observations = NormalizeObservations(observations);
        Decisions = NormalizeDecisions(decisions);
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);

        var computed = ComputeFingerprint(SchemaVersion, Realization, Observations, Decisions, Diagnostics);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied readiness-assessment fingerprint is not canonical.", nameof(fingerprint));

        Fingerprint = computed;
    }

    /// <summary>Exact portable assessment schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact physical realization being assessed.</summary>
    public InfrastructureRealizationReference Realization { get; }

    /// <summary>Attributable observations in physical-resource order.</summary>
    public ImmutableArray<InfrastructureResourceObservation> Observations { get; }

    /// <summary>Derived decisions in logical-node order.</summary>
    public ImmutableArray<InfrastructureReadinessDecision> Decisions { get; }

    /// <summary>Structured assessment and adapter diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Exact readiness-assessment fingerprint.</summary>
    public InfrastructureReadinessAssessmentFingerprint Fingerprint { get; }

    /// <summary>Whether every workload is ready and no error diagnostic remains.</summary>
    [JsonIgnore]
    public bool IsReady =>
        Decisions.Where(static decision => decision.Kind == InfrastructureNodeKind.Workload)
            .All(static decision => decision.IsReady)
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>Finds the readiness decision for one canonical logical node.</summary>
    /// <param name="node">Logical node identity.</param>
    /// <returns>The matching decision, or <see langword="null"/> when the node has no exact physical placement.</returns>
    /// <exception cref="ArgumentException"><paramref name="node"/> is default.</exception>
    public InfrastructureReadinessDecision? FindDecision(InfrastructureNodeId node)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A readiness-decision lookup requires a logical node.", nameof(node));

        var index = CanonicalDocumentCollections.BinarySearchIndex(
            Decisions,
            node,
            static (decision, sought) => StringComparer.Ordinal.Compare(decision.Node.Value, sought.Value));
        return index < 0 ? null : Decisions[index];
    }

    internal static ImmutableArray<InfrastructureResourceObservation> NormalizeObservations(
        ImmutableArray<InfrastructureResourceObservation> observations)
    {
        if (observations.IsDefaultOrEmpty)
            return [];

        if (observations.Any(static observation => observation is null))
            throw new ArgumentException("Infrastructure observations cannot contain null.", nameof(observations));

        var ordered = CanonicalDocumentCollections.SortIfNeeded(
            observations,
            static (left, right) => StringComparer.Ordinal.Compare(left.PhysicalResource.Value, right.PhysicalResource.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].PhysicalResource == ordered[index].PhysicalResource)
                throw new ArgumentException($"Infrastructure observation '{ordered[index].PhysicalResource.Value}' is duplicated.", nameof(observations));
        }
        return ordered;
    }

    static ImmutableArray<InfrastructureReadinessDecision> NormalizeDecisions(
        ImmutableArray<InfrastructureReadinessDecision> decisions)
    {
        if (decisions.IsDefaultOrEmpty)
            return [];

        if (decisions.Any(static decision => decision is null))
            throw new ArgumentException("Infrastructure readiness decisions cannot contain null.", nameof(decisions));

        var ordered = CanonicalDocumentCollections.SortIfNeeded(
            decisions,
            static (left, right) => StringComparer.Ordinal.Compare(left.Node.Value, right.Node.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Node == ordered[index].Node)
                throw new ArgumentException($"Infrastructure readiness decision '{ordered[index].Node.Value}' is duplicated.", nameof(decisions));
        }
        return ordered;
    }

    static InfrastructureReadinessAssessmentFingerprint ComputeFingerprint(
        string schemaVersion,
        InfrastructureRealizationReference realization,
        ImmutableArray<InfrastructureResourceObservation> observations,
        ImmutableArray<InfrastructureReadinessDecision> decisions,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(schemaVersion, realization, observations, decisions, diagnostics),
            StrictDocumentJson.CreateOptions());
        return new(
            InfrastructureReadinessAssessmentFingerprint.CurrentAlgorithm,
            InfrastructureReadinessAssessmentFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        InfrastructureRealizationReference Realization,
        ImmutableArray<InfrastructureResourceObservation> Observations,
        ImmutableArray<InfrastructureReadinessDecision> Decisions,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);
}

/// <summary>Pure deterministic assessment of observed physical state against canonical readiness obligations.</summary>
public static class InfrastructureReadinessEvaluator
{
    const string Stage = "infrastructure-readiness-assessment";

    /// <summary>Stable diagnostics emitted by readiness assessment.</summary>
    public static class DiagnosticCodes
    {
        /// <summary>The capability or exact physical realization is incomplete.</summary>
        public const string RealizationIncomplete = "infra.readiness.realization.incomplete";

        /// <summary>A canonical node has no exact physical identity.</summary>
        public const string PhysicalSubjectMissing = "infra.readiness.physicalSubject.missing";

        /// <summary>An observation names a resource outside the exact realization.</summary>
        public const string ObservationSubjectUnknown = "infra.readiness.observation.subjectUnknown";

        /// <summary>An exact physical resource has no attributable observation.</summary>
        public const string ObservationMissing = "infra.readiness.observation.missing";

        /// <summary>An observation does not establish a known health and readiness state.</summary>
        public const string ObservationUnknown = "infra.readiness.observation.unknown";

        /// <summary>A node's own physical observation establishes that it is not ready.</summary>
        public const string SubjectNotReady = "infra.readiness.subject.notReady";

        /// <summary>An exact readiness dependency is not ready.</summary>
        public const string DependencyNotReady = "infra.readiness.dependency.notReady";

        /// <summary>An exact readiness dependency has unknown effective readiness.</summary>
        public const string DependencyUnknown = "infra.readiness.dependency.unknown";
    }

    /// <summary>Assesses attributable resource observations against one exact physical realization.</summary>
    /// <param name="realization">Exact physical realization and compiled readiness obligations.</param>
    /// <param name="observations">Adapter-produced physical-resource observations.</param>
    /// <returns>A fingerprinted assessment with per-node decisions and actionable diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="realization"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="observations"/> is malformed or duplicated.</exception>
    public static InfrastructureReadinessAssessment Assess(
        InfrastructureRealization realization,
        ImmutableArray<InfrastructureResourceObservation> observations = default)
    {
        ArgumentNullException.ThrowIfNull(realization);
        var normalizedObservations = InfrastructureReadinessAssessment.NormalizeObservations(observations);
        var observationByPhysicalResource = normalizedObservations.ToDictionary(
            static observation => observation.PhysicalResource);
        var definition = realization.CapabilityClosure.Definition.Definition;
        var nodeContexts = definition.Workloads
            .Select(static workload => new NodeContext(workload.Id, InfrastructureNodeKind.Workload))
            .Concat(definition.Resources.Select(static resource =>
                new NodeContext(resource.Id, InfrastructureNodeKind.Resource)))
            .OrderBy(static context => context.Node.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var contextByNode = nodeContexts.ToDictionary(static context => context.Node);
        var physicalByNode = InfrastructureReadinessObligationCompiler.PhysicalResources(
            definition,
            realization.Lifecycle,
            realization.WorkloadPlacements);

        var realizationSource = SourceReference.Create(
            "infrastructure-realization",
            $"{realization.Fingerprint.Algorithm}:{realization.Fingerprint.Canonicalization}:{realization.Fingerprint.Value}");
        List<DocumentValidationDiagnostic> diagnostics =
        [
            .. realization.CapabilityClosure.Diagnostics,
            .. realization.Diagnostics
        ];
        foreach (var observation in normalizedObservations)
            diagnostics.AddRange(observation.Diagnostics);

        var completeRealization = realization.IsCapabilityWitnessComplete
                                  && realization.IsReadinessObligationComplete;
        if (!completeRealization)
        {
            diagnostics.Add(Diagnostic(
                DiagnosticCodes.RealizationIncomplete,
                "The exact infrastructure realization cannot establish readiness because capability witnesses, placements, or readiness obligations are incomplete.",
                location: "/realization",
                subject: realization.Fingerprint.Value,
                sourceReferences: [realizationSource],
                resolutionOptions:
                [
                    "Resolve capability and physical-placement diagnostics before assessing runtime readiness.",
                    "Regenerate the exact realization after every canonical readiness dependency can be lowered."
                ],
                expected: "capability-witness-complete realization with every readiness dependency physically lowered",
                observed: $"capabilityWitnessComplete={realization.IsCapabilityWitnessComplete}; readinessObligationComplete={realization.IsReadinessObligationComplete}"));
        }

        var knownPhysicalResources = physicalByNode.Values.ToHashSet();
        for (var index = 0; index < normalizedObservations.Length; index++)
        {
            var observation = normalizedObservations[index];
            if (knownPhysicalResources.Contains(observation.PhysicalResource))
                continue;

            diagnostics.Add(Diagnostic(
                DiagnosticCodes.ObservationSubjectUnknown,
                $"Observation subject '{observation.PhysicalResource.Value}' is outside the exact infrastructure realization.",
                location: $"/observations/{index}",
                subject: observation.PhysicalResource.Value,
                sourceReferences: Merge([realizationSource], observation.SourceReferences),
                resolutionOptions:
                [
                    "Remove the stale observation.",
                    "Assess it against the exact realization that owns this physical resource."
                ],
                expected: "a physical resource selected by the exact realization",
                observed: observation.PhysicalResource.Value,
                severity: DiagnosticSeverity.Warning));
        }

        foreach (var context in nodeContexts)
        {
            if (physicalByNode.ContainsKey(context.Node))
                continue;

            diagnostics.Add(Diagnostic(
                DiagnosticCodes.PhysicalSubjectMissing,
                $"Canonical node '{context.Node.Value}' has no exact physical resource to observe.",
                location: "/realization",
                subject: context.Node.Value,
                sourceReferences: [realizationSource, InfrastructureSourceReferences.Node(context.Node)],
                resolutionOptions: ["Select one exact physical resource for the canonical node and recompile the realization."],
                expected: "one exact physical resource",
                observed: "no physical placement"));
        }

        var obligationsBySubject = realization.ReadinessObligations
            .GroupBy(static obligation => obligation.Subject)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
        Dictionary<InfrastructureNodeId, InfrastructureReadinessDecision> evaluated = [];
        foreach (var context in nodeContexts)
            Evaluate(context.Node);

        var decisions = evaluated.Values
            .OrderBy(static decision => decision.Node.Value, StringComparer.Ordinal)
            .Select(decision => completeRealization || decision.EffectiveReadiness == ExecutionReadinessStatus.NotReady
                ? decision
                : new InfrastructureReadinessDecision(
                    decision.Node,
                    decision.Kind,
                    decision.PhysicalResource,
                    decision.ObservedHealth,
                    decision.ObservedReadiness,
                    ExecutionReadinessStatus.NotReady,
                    decision.BlockingDependencies,
                    decision.UnknownDependencies))
            .ToImmutableArray();

        return new(
            InfrastructureReadinessAssessment.CurrentSchemaVersion,
            realization.ToReference(),
            normalizedObservations,
            decisions,
            [.. diagnostics]);

        InfrastructureReadinessDecision? Evaluate(InfrastructureNodeId node)
        {
            if (evaluated.TryGetValue(node, out var existing))
                return existing;

            if (!physicalByNode.TryGetValue(node, out var physicalResource))
                return null;

            observationByPhysicalResource.TryGetValue(physicalResource, out var observation);
            var observedHealth = observation?.Health ?? ExecutionHealthStatus.Unknown;
            var observedReadiness = observation?.Readiness ?? ExecutionReadinessStatus.Unknown;
            var ownNotReady = observedReadiness == ExecutionReadinessStatus.NotReady
                              || observedHealth == ExecutionHealthStatus.Unhealthy;
            var ownUnknown = !ownNotReady
                             && (observedReadiness == ExecutionReadinessStatus.Unknown
                                 || observedHealth == ExecutionHealthStatus.Unknown);
            var sources = observation is null
                ? ImmutableArray.Create(realizationSource, InfrastructureSourceReferences.PhysicalResource(physicalResource))
                : Merge([realizationSource, InfrastructureSourceReferences.PhysicalResource(physicalResource)], observation.SourceReferences);

            if (observation is null)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticCodes.ObservationMissing,
                    $"Physical resource '{physicalResource.Value}' has no attributable readiness observation.",
                    location: "/observations",
                    subject: node.Value,
                    sourceReferences: sources,
                    resolutionOptions: ["Collect and supply an attributable observation from the selected infrastructure adapter."],
                    expected: "known health and readiness",
                    observed: "no observation"));
            }
            else if (ownNotReady)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticCodes.SubjectNotReady,
                    $"Physical resource '{physicalResource.Value}' is not ready.",
                    location: ObservationLocation(normalizedObservations, physicalResource),
                    subject: node.Value,
                    sourceReferences: sources,
                    resolutionOptions: ["Inspect the cited adapter evidence and restore the physical resource to a ready state."],
                    expected: $"{ExecutionHealthStatus.Healthy} or {ExecutionHealthStatus.Degraded}; {ExecutionReadinessStatus.Ready}",
                    observed: $"health={observedHealth}; readiness={observedReadiness}"));
            }
            else if (ownUnknown)
            {
                diagnostics.Add(Diagnostic(
                    DiagnosticCodes.ObservationUnknown,
                    $"Observation for physical resource '{physicalResource.Value}' does not establish readiness.",
                    location: ObservationLocation(normalizedObservations, physicalResource),
                    subject: node.Value,
                    sourceReferences: sources,
                    resolutionOptions: ["Wait for or collect an authoritative health and readiness observation."],
                    expected: "known health and readiness",
                    observed: $"health={observedHealth}; readiness={observedReadiness}"));
            }

            var blocking = ImmutableArray.CreateBuilder<InfrastructurePhysicalResourceId>();
            var unknown = ImmutableArray.CreateBuilder<InfrastructurePhysicalResourceId>();
            foreach (var obligation in obligationsBySubject.GetValueOrDefault(node, []))
            {
                var dependencyDecision = Evaluate(obligation.RequiredNode);
                var dependencyStatus = dependencyDecision?.EffectiveReadiness ?? ExecutionReadinessStatus.Unknown;
                if (dependencyStatus == ExecutionReadinessStatus.Ready)
                    continue;

                var dependencyObservation = observationByPhysicalResource.GetValueOrDefault(obligation.RequiredPhysicalResource);
                var dependencySources = dependencyObservation is null
                    ? ImmutableArray.Create(
                        realizationSource,
                        InfrastructureSourceReferences.ReadinessDependency(obligation.Dependency),
                        InfrastructureSourceReferences.PhysicalResource(obligation.RequiredPhysicalResource))
                    : Merge(
                        [
                            realizationSource,
                            InfrastructureSourceReferences.ReadinessDependency(obligation.Dependency),
                            InfrastructureSourceReferences.PhysicalResource(obligation.RequiredPhysicalResource)
                        ],
                        dependencyObservation.SourceReferences);
                var location = ReadinessLocation(definition, obligation.Dependency);
                if (dependencyStatus == ExecutionReadinessStatus.NotReady)
                {
                    blocking.Add(obligation.RequiredPhysicalResource);
                    diagnostics.Add(Diagnostic(
                        DiagnosticCodes.DependencyNotReady,
                        $"Node '{node.Value}' is blocked because dependency '{obligation.RequiredNode.Value}' is not ready.",
                        location,
                        obligation.Dependency.Value,
                        dependencySources,
                        ["Restore the exact dependency to a ready state before admitting the subject."],
                        expected: $"{obligation.RequiredPhysicalResource.Value} is {ExecutionReadinessStatus.Ready}",
                        observed: $"{dependencyStatus} for {obligation.RequiredPhysicalResource.Value}",
                        relatedLocations:
                        [
                            InfrastructureSourceReferences.Node(node).Value,
                            InfrastructureSourceReferences.PhysicalResource(physicalResource).Value
                        ]));
                }
                else
                {
                    unknown.Add(obligation.RequiredPhysicalResource);
                    diagnostics.Add(Diagnostic(
                        DiagnosticCodes.DependencyUnknown,
                        $"Node '{node.Value}' cannot establish readiness because dependency '{obligation.RequiredNode.Value}' is unknown.",
                        location,
                        obligation.Dependency.Value,
                        dependencySources,
                        ["Collect an authoritative observation for the exact dependency before admitting the subject."],
                        expected: $"{obligation.RequiredPhysicalResource.Value} is {ExecutionReadinessStatus.Ready}",
                        observed: $"{dependencyStatus} for {obligation.RequiredPhysicalResource.Value}",
                        relatedLocations:
                        [
                            InfrastructureSourceReferences.Node(node).Value,
                            InfrastructureSourceReferences.PhysicalResource(physicalResource).Value
                        ]));
                }
            }

            var effectiveReadiness = ownNotReady || blocking.Count > 0
                ? ExecutionReadinessStatus.NotReady
                : ownUnknown || unknown.Count > 0
                    ? ExecutionReadinessStatus.Unknown
                    : ExecutionReadinessStatus.Ready;
            var context = contextByNode[node];
            var decision = new InfrastructureReadinessDecision(
                node,
                context.Kind,
                physicalResource,
                observedHealth,
                observedReadiness,
                effectiveReadiness,
                blocking.ToImmutable(),
                unknown.ToImmutable());
            evaluated.Add(node, decision);
            return decision;
        }
    }

    static DocumentValidationDiagnostic Diagnostic(
        string code,
        string message,
        string location,
        string subject,
        ImmutableArray<SourceReference> sourceReferences,
        ImmutableArray<string> resolutionOptions,
        string expected,
        string observed,
        ImmutableArray<string> relatedLocations = default,
        DiagnosticSeverity severity = DiagnosticSeverity.Error) => new(
            code,
            severity,
            message,
            Location: location,
            SchemaLocation: subject,
            Evidence: new(
                stage: Stage,
                subject: subject,
                relatedLocations: relatedLocations,
                sourceReferences: sourceReferences.Select(static reference => reference.Value).ToImmutableArray(),
                resolutionOptions: resolutionOptions,
                expected: expected,
                observed: observed));

    static ImmutableArray<SourceReference> Merge(
        ImmutableArray<SourceReference> left,
        ImmutableArray<SourceReference> right) =>
        SourceReference.NormalizeSet([.. left, .. right], requireNonEmpty: true);

    static string ObservationLocation(
        ImmutableArray<InfrastructureResourceObservation> observations,
        InfrastructurePhysicalResourceId physicalResource)
    {
        var index = CanonicalDocumentCollections.BinarySearchIndex(
            observations,
            physicalResource,
            static (observation, sought) =>
                StringComparer.Ordinal.Compare(observation.PhysicalResource.Value, sought.Value));
        return index < 0 ? "/observations" : $"/observations/{index}";
    }

    static string ReadinessLocation(
        InfrastructureDefinition definition,
        InfrastructureReadinessDependencyId dependency)
    {
        var index = CanonicalDocumentCollections.BinarySearchIndex(
            definition.ReadinessDependencies,
            dependency,
            static (candidate, sought) => StringComparer.Ordinal.Compare(candidate.Id.Value, sought.Value));
        return index < 0 ? "/definition/readinessDependencies" : $"/definition/readinessDependencies/{index}";
    }

    sealed record NodeContext(InfrastructureNodeId Node, InfrastructureNodeKind Kind);
}
