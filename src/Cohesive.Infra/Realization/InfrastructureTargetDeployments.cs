using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Infra.Configuration;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>One declarative workload placement supplied by a target adapter.</summary>
public sealed record InfrastructureTargetWorkloadDeployment
{
    /// <summary>Creates one workload deployment declaration.</summary>
    /// <param name="workload">Canonical logical workload.</param>
    /// <param name="facility">Target facility that materializes the workload.</param>
    /// <param name="physicalResource">Exact target-native deployment identity.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <exception cref="ArgumentException">An identity or source reference is invalid or missing.</exception>
    [JsonConstructor]
    public InfrastructureTargetWorkloadDeployment(
        InfrastructureNodeId workload,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        ImmutableArray<SourceReference> sourceReferences)
    {
        if (string.IsNullOrWhiteSpace(workload.Value))
            throw new ArgumentException("A target workload deployment requires a logical workload.", nameof(workload));
        if (string.IsNullOrWhiteSpace(facility.Value))
            throw new ArgumentException("A target workload deployment requires a facility.", nameof(facility));
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("A target workload deployment requires a physical resource.", nameof(physicalResource));

        Workload = workload;
        Facility = facility;
        PhysicalResource = physicalResource;
        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
    }

    /// <summary>Canonical logical workload.</summary>
    public InfrastructureNodeId Workload { get; }

    /// <summary>Target facility that materializes the workload.</summary>
    public InfrastructureTargetFacilityId Facility { get; }

    /// <summary>Exact target-native deployment identity.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Attributable sources in canonical order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Compares workload deployment declarations structurally.</summary>
    /// <param name="other">Other declaration.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureTargetWorkloadDeployment? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Workload == other.Workload
        && Facility == other.Facility
        && PhysicalResource == other.PhysicalResource
        && SourceReferences.SequenceEqual(other.SourceReferences);

    /// <summary>Returns a structural hash code for this declaration.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Workload);
        hash.Add(Facility);
        hash.Add(PhysicalResource);
        foreach (var source in SourceReferences)
            hash.Add(source);
        return hash.ToHashCode();
    }
}

/// <summary>One declarative resource placement and lifecycle authority supplied by a target adapter.</summary>
public sealed record InfrastructureTargetResourceDeployment
{
    /// <summary>Creates one resource deployment declaration.</summary>
    /// <param name="resource">Canonical logical resource.</param>
    /// <param name="facility">Target facility that materializes or references the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity.</param>
    /// <param name="authority">Backend state scope or external authority that owns the resource lifecycle.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <exception cref="ArgumentException">An identity or source reference is invalid or missing.</exception>
    [JsonConstructor]
    public InfrastructureTargetResourceDeployment(
        InfrastructureNodeId resource,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sourceReferences)
    {
        if (string.IsNullOrWhiteSpace(resource.Value))
            throw new ArgumentException("A target resource deployment requires a logical resource.", nameof(resource));
        if (string.IsNullOrWhiteSpace(facility.Value))
            throw new ArgumentException("A target resource deployment requires a facility.", nameof(facility));
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("A target resource deployment requires a physical resource.", nameof(physicalResource));
        if (string.IsNullOrWhiteSpace(authority.Value))
            throw new ArgumentException("A target resource deployment requires a lifecycle authority.", nameof(authority));

        Resource = resource;
        Facility = facility;
        PhysicalResource = physicalResource;
        Authority = authority;
        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
    }

    /// <summary>Canonical logical resource.</summary>
    public InfrastructureNodeId Resource { get; }

    /// <summary>Target facility that materializes or references the resource.</summary>
    public InfrastructureTargetFacilityId Facility { get; }

    /// <summary>Exact target-native resource identity.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Backend state scope or external authority that owns the resource lifecycle.</summary>
    public InfrastructureLifecycleAuthorityId Authority { get; }

    /// <summary>Attributable sources in canonical order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Compares resource deployment declarations structurally.</summary>
    /// <param name="other">Other declaration.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureTargetResourceDeployment? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Resource == other.Resource
        && Facility == other.Facility
        && PhysicalResource == other.PhysicalResource
        && Authority == other.Authority
        && SourceReferences.SequenceEqual(other.SourceReferences);

    /// <summary>Returns a structural hash code for this declaration.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Resource);
        hash.Add(Facility);
        hash.Add(PhysicalResource);
        hash.Add(Authority);
        foreach (var source in SourceReferences)
            hash.Add(source);
        return hash.ToHashCode();
    }
}

/// <summary>Deterministic fingerprint of one exact target-deployment manifest.</summary>
public sealed record InfrastructureTargetDeploymentManifestFingerprint
{
    /// <summary>Digest algorithm used by the current manifest fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current manifest fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-target-deployment/v1-c14n/v1";

    /// <summary>Creates target-deployment manifest fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureTargetDeploymentManifestFingerprint(string algorithm, string canonicalization, string value)
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

/// <summary>Payload-free exact reference to one target-deployment manifest.</summary>
public sealed record InfrastructureTargetDeploymentManifestReference
{
    /// <summary>Creates an exact target-deployment manifest reference.</summary>
    /// <param name="schemaVersion">Exact persisted manifest schema version.</param>
    /// <param name="id">Stable versioned manifest identity.</param>
    /// <param name="definition">Exact infrastructure definition fenced by the manifest.</param>
    /// <param name="targetFacilities">Exact target-facility manifest used by the deployment.</param>
    /// <param name="fingerprint">Exact canonical deployment-manifest fingerprint.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema or identity is empty.</exception>
    [JsonConstructor]
    public InfrastructureTargetDeploymentManifestReference(
        string schemaVersion,
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionReference definition,
        InfrastructureTargetFacilityManifestReference targetFacilities,
        InfrastructureTargetDeploymentManifestFingerprint fingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A target-deployment manifest reference requires an identity.", nameof(id));
        Id = id;
        Definition = Guard.RequireNotNull(definition);
        TargetFacilities = Guard.RequireNotNull(targetFacilities);
        Fingerprint = Guard.RequireNotNull(fingerprint);
    }

    /// <summary>Exact persisted manifest schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned manifest identity.</summary>
    public InfrastructureTargetDeploymentManifestId Id { get; }

    /// <summary>Exact infrastructure definition fenced by the manifest.</summary>
    public InfrastructureDefinitionReference Definition { get; }

    /// <summary>Exact target-facility manifest used by the deployment.</summary>
    public InfrastructureTargetFacilityManifestReference TargetFacilities { get; }

    /// <summary>Exact canonical deployment-manifest fingerprint.</summary>
    public InfrastructureTargetDeploymentManifestFingerprint Fingerprint { get; }
}

/// <summary>
/// Portable adapter declaration that binds canonical nodes and target facilities to exact physical resources.
/// </summary>
/// <remarks>
/// This manifest is declarative input. Capability discharge, facility selection, lifecycle validation, physical
/// witness construction, and diagnostics are derived by <see cref="InfrastructureTargetDeploymentCompiler"/>.
/// </remarks>
public sealed record InfrastructureTargetDeploymentManifest
{
    /// <summary>Current persisted target-deployment manifest schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive.infra.target-deployment/1";

    /// <summary>Creates or restores one exactly fingerprinted target-deployment manifest.</summary>
    /// <param name="schemaVersion">Exact persisted schema version.</param>
    /// <param name="id">Stable versioned manifest identity.</param>
    /// <param name="definition">Exact infrastructure definition fenced by this declaration.</param>
    /// <param name="targetFacilities">Exact target facilities available to the deployment.</param>
    /// <param name="workloads">Declared workload deployments.</param>
    /// <param name="resources">Declared resource deployments.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema, identity, deployment collection, facility, or fingerprint is invalid.</exception>
    [JsonConstructor]
    public InfrastructureTargetDeploymentManifest(
        string schemaVersion,
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionReference definition,
        InfrastructureTargetFacilityManifest targetFacilities,
        ImmutableArray<InfrastructureTargetWorkloadDeployment> workloads = default,
        ImmutableArray<InfrastructureTargetResourceDeployment> resources = default,
        InfrastructureTargetDeploymentManifestFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Target-deployment manifest schema '{SchemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A target-deployment manifest requires a stable identity.", nameof(id));

        Id = id;
        Definition = Guard.RequireNotNull(definition);
        TargetFacilities = Guard.RequireNotNull(targetFacilities);
        Workloads = NormalizeWorkloads(workloads);
        Resources = NormalizeResources(resources);
        ValidateFacilities();

        var computed = InfrastructureTargetDeploymentFingerprinting.Compute(
            SchemaVersion,
            Id,
            Definition,
            TargetFacilities.ToReference(),
            Workloads,
            Resources);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied target-deployment manifest fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact persisted schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned manifest identity.</summary>
    public InfrastructureTargetDeploymentManifestId Id { get; }

    /// <summary>Exact infrastructure definition fenced by this declaration.</summary>
    public InfrastructureDefinitionReference Definition { get; }

    /// <summary>Exact target facilities available to the deployment.</summary>
    public InfrastructureTargetFacilityManifest TargetFacilities { get; }

    /// <summary>Declared workload deployments in logical-workload order.</summary>
    public ImmutableArray<InfrastructureTargetWorkloadDeployment> Workloads { get; }

    /// <summary>Declared resource deployments in logical-resource order.</summary>
    public ImmutableArray<InfrastructureTargetResourceDeployment> Resources { get; }

    /// <summary>Deterministic fingerprint of the complete declaration.</summary>
    public InfrastructureTargetDeploymentManifestFingerprint Fingerprint { get; }

    /// <summary>Creates an exact payload-free reference to this manifest.</summary>
    /// <returns>The schema, identity, definition, target-facility, and fingerprint fence.</returns>
    public InfrastructureTargetDeploymentManifestReference ToReference() => new(
        SchemaVersion,
        Id,
        Definition,
        TargetFacilities.ToReference(),
        Fingerprint);

    /// <summary>Finds the exact physical declaration for one canonical workload.</summary>
    /// <param name="workload">Canonical workload identity.</param>
    /// <returns>The matching workload deployment.</returns>
    /// <exception cref="ArgumentException"><paramref name="workload"/> is a default identity.</exception>
    /// <exception cref="KeyNotFoundException">The workload has no deployment declaration.</exception>
    public InfrastructureTargetWorkloadDeployment FindWorkload(InfrastructureNodeId workload)
    {
        if (string.IsNullOrWhiteSpace(workload.Value))
            throw new ArgumentException("A default workload cannot be resolved.", nameof(workload));
        var index = CanonicalDocumentCollections.BinarySearchIndex(
            Workloads,
            workload,
            static (deployment, sought) =>
                StringComparer.Ordinal.Compare(deployment.Workload.Value, sought.Value));
        return index < 0
            ? throw new KeyNotFoundException($"Target deployment contains no workload '{workload.Value}'.")
            : Workloads[index];
    }

    /// <summary>Finds the exact physical declaration for one canonical resource.</summary>
    /// <param name="resource">Canonical resource identity.</param>
    /// <returns>The matching resource deployment.</returns>
    /// <exception cref="ArgumentException"><paramref name="resource"/> is a default identity.</exception>
    /// <exception cref="KeyNotFoundException">The resource has no deployment declaration.</exception>
    public InfrastructureTargetResourceDeployment FindResource(InfrastructureNodeId resource)
    {
        if (string.IsNullOrWhiteSpace(resource.Value))
            throw new ArgumentException("A default resource cannot be resolved.", nameof(resource));
        var index = CanonicalDocumentCollections.BinarySearchIndex(
            Resources,
            resource,
            static (deployment, sought) =>
                StringComparer.Ordinal.Compare(deployment.Resource.Value, sought.Value));
        return index < 0
            ? throw new KeyNotFoundException($"Target deployment contains no resource '{resource.Value}'.")
            : Resources[index];
    }

    /// <summary>Compares target-deployment manifests structurally.</summary>
    /// <param name="other">Other manifest.</param>
    /// <returns><see langword="true"/> when every semantic field is equal.</returns>
    public bool Equals(InfrastructureTargetDeploymentManifest? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && Id == other.Id
        && Definition == other.Definition
        && TargetFacilities == other.TargetFacilities
        && Workloads.SequenceEqual(other.Workloads)
        && Resources.SequenceEqual(other.Resources)
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for this manifest.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(Id);
        hash.Add(Definition);
        hash.Add(TargetFacilities);
        foreach (var workload in Workloads)
            hash.Add(workload);
        foreach (var resource in Resources)
            hash.Add(resource);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructureTargetWorkloadDeployment> NormalizeWorkloads(
        ImmutableArray<InfrastructureTargetWorkloadDeployment> workloads)
    {
        if (workloads.IsDefaultOrEmpty)
            return [];
        if (workloads.Any(static deployment => deployment is null))
            throw new ArgumentException("Target workload deployments cannot contain null.", nameof(workloads));
        var ordered = workloads.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Workload.Value, right.Workload.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Workload == ordered[index].Workload)
                throw new ArgumentException($"Target workload deployment '{ordered[index].Workload.Value}' is duplicated.", nameof(workloads));
        }
        return ordered;
    }

    static ImmutableArray<InfrastructureTargetResourceDeployment> NormalizeResources(
        ImmutableArray<InfrastructureTargetResourceDeployment> resources)
    {
        if (resources.IsDefaultOrEmpty)
            return [];
        if (resources.Any(static deployment => deployment is null))
            throw new ArgumentException("Target resource deployments cannot contain null.", nameof(resources));
        var ordered = resources.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Resource.Value, right.Resource.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Resource == ordered[index].Resource)
                throw new ArgumentException($"Target resource deployment '{ordered[index].Resource.Value}' is duplicated.", nameof(resources));
        }
        return ordered;
    }

    void ValidateFacilities()
    {
        var facilities = TargetFacilities.Facilities.ToDictionary(static facility => facility.Id);
        foreach (var workload in Workloads)
        {
            if (!facilities.TryGetValue(workload.Facility, out var facility)
                || facility.NodeKind != InfrastructureNodeKind.Workload)
            {
                throw new ArgumentException(
                    $"Workload deployment '{workload.Workload.Value}' references unknown or incompatible facility '{workload.Facility.Value}'.",
                    nameof(Workloads));
            }
        }
        foreach (var resource in Resources)
        {
            if (!facilities.TryGetValue(resource.Facility, out var facility)
                || facility.NodeKind != InfrastructureNodeKind.Resource)
            {
                throw new ArgumentException(
                    $"Resource deployment '{resource.Resource.Value}' references unknown or incompatible facility '{resource.Facility.Value}'.",
                    nameof(Resources));
            }
        }
    }
}

/// <summary>
/// Coordinated result of compiling a declarative target deployment without introducing another semantic authority.
/// </summary>
public sealed class InfrastructureTargetDeploymentPlan
{
    internal InfrastructureTargetDeploymentPlan(
        InfrastructureTargetDeploymentManifest manifest,
        InfrastructureTargetFacilityPlan facilityPlan,
        InfrastructureRealization? realization,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        Manifest = manifest;
        FacilityPlan = facilityPlan;
        Realization = realization;
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
    }

    /// <summary>Exact declarative physical deployment input.</summary>
    public InfrastructureTargetDeploymentManifest Manifest { get; }

    /// <summary>Facility selection and capability-discharge result.</summary>
    public InfrastructureTargetFacilityPlan FacilityPlan { get; }

    /// <summary>Compiled lifecycle, placements, and physical evidence, or <see langword="null"/> for an invalid deployment declaration.</summary>
    public InfrastructureRealization? Realization { get; }

    /// <summary>Combined target-selection, declaration, capability, and physical-witness diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether facility selection and physical capability witnessing completed without errors.</summary>
    public bool IsComplete =>
        FacilityPlan.IsComplete
        && Realization?.IsCapabilityWitnessComplete == true
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}

/// <summary>Compiles declarative target-deployment manifests into lifecycle and physical-capability realizations.</summary>
public static class InfrastructureTargetDeploymentCompiler
{
    const string Stage = "infrastructure-target-deployment";

    /// <summary>Stable diagnostics emitted while joining a deployment declaration to canonical nodes.</summary>
    public static class DiagnosticCodes
    {
        /// <summary>A canonical workload has no physical deployment declaration.</summary>
        public const string WorkloadMissing = "infra.target.deployment.workload.missing";

        /// <summary>A canonical resource has no physical deployment declaration.</summary>
        public const string ResourceMissing = "infra.target.deployment.resource.missing";

        /// <summary>A deployment declaration names no canonical node of the expected kind.</summary>
        public const string NodeUnknown = "infra.target.deployment.node.unknown";
    }

    /// <summary>Compiles one exact application definition through an adapter-authored target deployment.</summary>
    /// <param name="semantic">Canonical definition and binding-elaboration policy.</param>
    /// <param name="manifest">Declarative target facilities, physical resources, and lifecycle authorities.</param>
    /// <returns>Facility, capability, lifecycle, placement, witness, and diagnostic artifacts.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The manifest is fenced to another exact definition.</exception>
    public static InfrastructureTargetDeploymentPlan Compile(
        InfrastructureAuthoringResult semantic,
        InfrastructureTargetDeploymentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(semantic);
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Definition != semantic.Definition.ToReference())
            throw new ArgumentException("The target deployment manifest references another exact definition.", nameof(manifest));

        var authority = $"infrastructure-target-deployment/{manifest.Id.Value}/{manifest.Fingerprint.Value}";
        var selections = manifest.Workloads
            .Select(static deployment => (deployment.Workload, deployment.Facility))
            .Concat(manifest.Resources.Select(static deployment => (deployment.Resource, deployment.Facility)))
            .Select(selection => new InfrastructureConfigurationCandidate(
                InfrastructureTargetFacilitySelection.Subject(selection.Item1),
                InfrastructureTargetFacilitySelection.Setting,
                selection.Facility.Value,
                EffectiveConfigurationOrigin.Explicit,
                authority))
            .ToImmutableArray();
        var facilityPlan = InfrastructureTargetCompiler.Compile(
            semantic.Definition,
            manifest.TargetFacilities,
            semantic.BindingElaborationProfile,
            [new(new($"{manifest.Id.Value}/facility-selections"), selections)]);

        var diagnostics = ValidateDeployments(semantic.Definition, manifest);
        InfrastructureRealization? realization = null;
        if (!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            realization = CompileRealization(semantic.Definition, manifest, facilityPlan);

        return new(
            manifest,
            facilityPlan,
            realization,
            [.. facilityPlan.Diagnostics, .. diagnostics, .. realization?.Diagnostics ?? []]);
    }

    static InfrastructureRealization CompileRealization(
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetDeploymentManifest manifest,
        InfrastructureTargetFacilityPlan facilityPlan)
    {
        var target = manifest.TargetFacilities.Profile.Target;
        var lifecycleByResource = definition.Definition.Resources.ToDictionary(static resource => resource.Id);
        var lifecycle = new InfrastructureLifecyclePlan(
            definition,
            [
                .. manifest.Resources.Select(deployment => new InfrastructureResourceLifecycleBinding(
                    deployment.Resource,
                    deployment.PhysicalResource,
                    target,
                    deployment.Authority,
                    lifecycleByResource[deployment.Resource].Lifecycle == InfrastructureResourceLifecycle.External
                        ? InfrastructureLifecycleDisposition.Referenced
                        : InfrastructureLifecycleDisposition.Managed))
            ]);
        ImmutableArray<InfrastructureWorkloadPlacement> placements =
        [
            .. manifest.Workloads.Select(deployment => new InfrastructureWorkloadPlacement(
                deployment.Workload,
                deployment.PhysicalResource,
                target,
                deployment.SourceReferences))
        ];
        var provisional = InfrastructureRealizationCompiler.Compile(
            facilityPlan.CapabilityClosure,
            lifecycle,
            placements);
        var witnesses = CreateWitnesses(manifest, provisional);
        return InfrastructureRealizationCompiler.Compile(
            facilityPlan.CapabilityClosure,
            lifecycle,
            placements,
            witnesses);
    }

    static ImmutableArray<InfrastructureCapabilityEvidenceWitness> CreateWitnesses(
        InfrastructureTargetDeploymentManifest manifest,
        InfrastructureRealization provisional)
    {
        var evidenceOwners = manifest.TargetFacilities.Facilities
            .SelectMany(facility => facility.Evidence.Select(evidence => (Evidence: evidence, Facility: facility.Id)))
            .ToDictionary(static item => item.Evidence, static item => item.Facility);
        var deployments = manifest.Workloads
            .Select(static item => new Deployment(item.Workload, item.Facility, item.PhysicalResource, item.SourceReferences))
            .Concat(manifest.Resources.Select(static item =>
                new Deployment(item.Resource, item.Facility, item.PhysicalResource, item.SourceReferences)))
            .ToImmutableArray();
        var source = SourceReference.Create("infrastructure-target-deployment", manifest.Fingerprint.Value);
        var witnesses = ImmutableArray.CreateBuilder<InfrastructureCapabilityEvidenceWitness>(
            provisional.WitnessDecisions.Sum(static decision => decision.RequiredEvidence.Length));

        foreach (var decision in provisional.WitnessDecisions)
        {
            if (decision.Realization is CapabilityRealizationKind.Unavailable or CapabilityRealizationKind.Unknown)
                continue;

            foreach (var evidence in decision.RequiredEvidence)
            {
                var facility = evidenceOwners[evidence];
                var facilityDeployments = deployments
                    .Where(deployment => deployment.Facility == facility)
                    .ToImmutableArray();
                if (facilityDeployments.IsDefaultOrEmpty)
                    continue;

                var applicable = facilityDeployments
                    .Where(deployment => decision.Subjects.Contains(deployment.Node))
                    .ToImmutableArray();
                if (applicable.IsDefaultOrEmpty)
                    applicable = facilityDeployments;
                var subjectDeployments = deployments
                    .Where(deployment => decision.ExpectedPhysicalResources.Contains(deployment.PhysicalResource));

                // Preserve the evidence-owning facility (including an auxiliary facility) while fencing the
                // witness to every physical workload/resource subject governed by this exact demand.
                witnesses.Add(new(
                    decision.Requirement,
                    evidence,
                    [
                        .. decision.ExpectedPhysicalResources
                            .Concat(applicable.Select(static deployment => deployment.PhysicalResource))
                            .Distinct()
                    ],
                    [
                        .. applicable.Concat(subjectDeployments)
                            .SelectMany(static deployment => deployment.SourceReferences)
                            .Append(source)
                            .Distinct()
                    ]));
            }
        }
        return witnesses.ToImmutable();
    }

    static ImmutableArray<DocumentValidationDiagnostic> ValidateDeployments(
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetDeploymentManifest manifest)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        var workloads = definition.Definition.Workloads.Select(static item => item.Id).ToHashSet();
        var resources = definition.Definition.Resources.Select(static item => item.Id).ToHashSet();
        var declaredWorkloads = manifest.Workloads.Select(static item => item.Workload).ToHashSet();
        var declaredResources = manifest.Resources.Select(static item => item.Resource).ToHashSet();

        foreach (var deployment in manifest.Workloads)
        {
            if (!workloads.Contains(deployment.Workload))
                diagnostics.Add(Unknown(deployment.Workload, InfrastructureNodeKind.Workload, deployment.SourceReferences));
        }
        foreach (var deployment in manifest.Resources)
        {
            if (!resources.Contains(deployment.Resource))
                diagnostics.Add(Unknown(deployment.Resource, InfrastructureNodeKind.Resource, deployment.SourceReferences));
        }
        foreach (var workload in workloads.Where(workload => !declaredWorkloads.Contains(workload)))
            diagnostics.Add(Missing(workload, InfrastructureNodeKind.Workload));
        foreach (var resource in resources.Where(resource => !declaredResources.Contains(resource)))
            diagnostics.Add(Missing(resource, InfrastructureNodeKind.Resource));

        return DocumentValidationDiagnostics.Normalize(diagnostics.ToImmutable());
    }

    static DocumentValidationDiagnostic Unknown(
        InfrastructureNodeId node,
        InfrastructureNodeKind kind,
        ImmutableArray<SourceReference> sources) => new(
        DiagnosticCodes.NodeUnknown,
        DiagnosticSeverity.Error,
        $"Target deployment declaration '{node.Value}' is not a canonical {kind.ToString().ToLowerInvariant()}.",
        SchemaLocation: node.Value,
        Evidence: new(
            stage: Stage,
            subject: node.Value,
            sourceReferences: [.. sources.Select(static source => source.Value)],
            resolutionOptions: ["Remove the stale deployment or bind it to an exact canonical node."],
            expected: $"a canonical {kind.ToString().ToLowerInvariant()} identity",
            observed: "unknown or wrong-kind node"));

    static DocumentValidationDiagnostic Missing(InfrastructureNodeId node, InfrastructureNodeKind kind) => new(
        kind == InfrastructureNodeKind.Workload ? DiagnosticCodes.WorkloadMissing : DiagnosticCodes.ResourceMissing,
        DiagnosticSeverity.Error,
        $"Canonical {kind.ToString().ToLowerInvariant()} '{node.Value}' has no target deployment declaration.",
        SchemaLocation: node.Value,
        Evidence: new(
            stage: Stage,
            subject: node.Value,
            resolutionOptions: ["Declare an exact physical deployment through the selected target adapter."],
            expected: "one attributable physical deployment",
            observed: "no deployment declaration"));

    sealed record Deployment(
        InfrastructureNodeId Node,
        InfrastructureTargetFacilityId Facility,
        InfrastructurePhysicalResourceId PhysicalResource,
        ImmutableArray<SourceReference> SourceReferences);
}

static class InfrastructureTargetDeploymentFingerprinting
{
    internal static InfrastructureTargetDeploymentManifestFingerprint Compute(
        string schemaVersion,
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionReference definition,
        InfrastructureTargetFacilityManifestReference targetFacilities,
        ImmutableArray<InfrastructureTargetWorkloadDeployment> workloads,
        ImmutableArray<InfrastructureTargetResourceDeployment> resources)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(schemaVersion, id, definition, targetFacilities, workloads, resources),
            StrictDocumentJson.CreateOptions());
        return new(
            InfrastructureTargetDeploymentManifestFingerprint.CurrentAlgorithm,
            InfrastructureTargetDeploymentManifestFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        InfrastructureTargetDeploymentManifestId Id,
        InfrastructureDefinitionReference Definition,
        InfrastructureTargetFacilityManifestReference TargetFacilities,
        ImmutableArray<InfrastructureTargetWorkloadDeployment> Workloads,
        ImmutableArray<InfrastructureTargetResourceDeployment> Resources);
}
