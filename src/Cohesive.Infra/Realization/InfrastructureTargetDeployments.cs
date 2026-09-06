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

/// <summary>One declarative resource placement and lifecycle ownership supplied by a target adapter.</summary>
public sealed record InfrastructureTargetResourceDeployment
{
    /// <summary>Creates one resource deployment declaration.</summary>
    /// <param name="resource">Canonical logical resource.</param>
    /// <param name="facility">Target facility that materializes or references the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity.</param>
    /// <param name="authority">Backend state scope or external authority that owns the resource lifecycle.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <param name="managingInterpreter">
    /// Exact foreign lifecycle interpreter when the selected target only references the resource; otherwise
    /// <see langword="null"/> so canonical lifecycle intent determines whether the selected target manages or references it.
    /// </param>
    /// <exception cref="ArgumentException">An identity or source reference is invalid or missing.</exception>
    [JsonConstructor]
    public InfrastructureTargetResourceDeployment(
        InfrastructureNodeId resource,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sourceReferences,
        InfrastructureTargetId? managingInterpreter = null)
    {
        if (string.IsNullOrWhiteSpace(resource.Value))
            throw new ArgumentException("A target resource deployment requires a logical resource.", nameof(resource));

        if (string.IsNullOrWhiteSpace(facility.Value))
            throw new ArgumentException("A target resource deployment requires a facility.", nameof(facility));

        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("A target resource deployment requires a physical resource.", nameof(physicalResource));

        if (string.IsNullOrWhiteSpace(authority.Value))
            throw new ArgumentException("A target resource deployment requires a lifecycle authority.", nameof(authority));

        if (managingInterpreter is { } manager && string.IsNullOrWhiteSpace(manager.Value))
        {
            throw new ArgumentException(
                "A foreign managing interpreter cannot be a default identity.",
                nameof(managingInterpreter));
        }

        Resource = resource;
        Facility = facility;
        PhysicalResource = physicalResource;
        Authority = authority;
        ManagingInterpreter = managingInterpreter;
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

    /// <summary>
    /// Exact foreign lifecycle interpreter, or <see langword="null"/> when ownership follows canonical lifecycle intent.
    /// </summary>
    public InfrastructureTargetId? ManagingInterpreter { get; }

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
        && ManagingInterpreter == other.ManagingInterpreter
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
        hash.Add(ManagingInterpreter);
        foreach (var source in SourceReferences)
            hash.Add(source);

        return hash.ToHashCode();
    }
}

/// <summary>
/// Attributable target policy accepting one named operating boundary wherever the selected capability proof uses it.
/// </summary>
public sealed record InfrastructureTargetBoundaryAcceptance
{
    /// <summary>Creates one declarative target-boundary acceptance.</summary>
    /// <param name="boundary">Operating boundary accepted by this target deployment.</param>
    /// <param name="rationale">Human-reviewable environment-policy rationale.</param>
    /// <param name="sourceReferences">Non-empty policy, approval, or specification references.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rationale"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The boundary, rationale, or source-reference collection is invalid or missing.</exception>
    [JsonConstructor]
    public InfrastructureTargetBoundaryAcceptance(
        InfrastructureOperatingBoundaryId boundary,
        string rationale,
        ImmutableArray<SourceReference> sourceReferences)
    {
        if (string.IsNullOrWhiteSpace(boundary.Value))
            throw new ArgumentException("A target boundary acceptance requires an operating boundary.", nameof(boundary));

        Boundary = boundary;
        Rationale = Guard.RequireNotNullOrWhiteSpace(rationale);
        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
    }

    /// <summary>Operating boundary accepted by this target deployment.</summary>
    public InfrastructureOperatingBoundaryId Boundary { get; }

    /// <summary>Human-reviewable environment-policy rationale.</summary>
    public string Rationale { get; }

    /// <summary>Attributable policy, approval, or specification references in canonical order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Compares target-boundary acceptances structurally.</summary>
    /// <param name="other">Other declaration.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureTargetBoundaryAcceptance? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Boundary == other.Boundary
        && string.Equals(Rationale, other.Rationale, StringComparison.Ordinal)
        && SourceReferences.SequenceEqual(other.SourceReferences);

    /// <summary>Returns a structural hash code for this declaration.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Boundary);
        hash.Add(Rationale, StringComparer.Ordinal);
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
    public const string CurrentCanonicalization = "cohesive-infra-target-deployment/v4-c14n/v1";

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
    public const string CurrentSchemaVersion = "cohesive.infra.target-deployment/4";

    /// <summary>Creates or restores one exactly fingerprinted target-deployment manifest.</summary>
    /// <param name="schemaVersion">Exact persisted schema version.</param>
    /// <param name="id">Stable versioned manifest identity.</param>
    /// <param name="definition">Exact infrastructure definition fenced by this declaration.</param>
    /// <param name="targetFacilities">Exact target facilities available to the deployment.</param>
    /// <param name="workloads">Declared workload deployments.</param>
    /// <param name="resources">Declared resource deployments.</param>
    /// <param name="nonParticipatingWorkloads">Attributable decisions for canonical workloads absent from this deployment.</param>
    /// <param name="boundaryAcceptances">Attributable operating-boundary acceptances compiled into exact demand-scoped policy.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <param name="sourceMap">Optional non-semantic producer attribution.</param>
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
        ImmutableArray<InfrastructureWorkloadNonParticipation> nonParticipatingWorkloads = default,
        ImmutableArray<InfrastructureTargetBoundaryAcceptance> boundaryAcceptances = default,
        InfrastructureTargetDeploymentManifestFingerprint? fingerprint = null,
        InfrastructureSourceMap? sourceMap = null)
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
        NonParticipatingWorkloads = InfrastructureCapabilityWitnessCollections.NormalizeNonParticipations(
            nonParticipatingWorkloads);
        BoundaryAcceptances = NormalizeBoundaryAcceptances(boundaryAcceptances);
        SourceMap = sourceMap ?? InfrastructureSourceMap.Empty;
        ValidateFacilities();

        var computed = InfrastructureTargetDeploymentFingerprinting.Compute(
            SchemaVersion,
            Id,
            Definition,
            TargetFacilities.ToReference(),
            Workloads,
            Resources,
            NonParticipatingWorkloads,
            BoundaryAcceptances);
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

    /// <summary>Attributable non-participation decisions in workload-identity order.</summary>
    public ImmutableArray<InfrastructureWorkloadNonParticipation> NonParticipatingWorkloads { get; }

    /// <summary>Attributable target-boundary acceptances in boundary-identity order.</summary>
    public ImmutableArray<InfrastructureTargetBoundaryAcceptance> BoundaryAcceptances { get; }

    /// <summary>Deterministic fingerprint of the complete declaration.</summary>
    public InfrastructureTargetDeploymentManifestFingerprint Fingerprint { get; }

    /// <summary>Non-semantic producer attribution excluded from <see cref="Fingerprint"/>.</summary>
    public InfrastructureSourceMap SourceMap { get; }

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
        && NonParticipatingWorkloads.SequenceEqual(other.NonParticipatingWorkloads)
        && BoundaryAcceptances.SequenceEqual(other.BoundaryAcceptances)
        && Fingerprint == other.Fingerprint
        && SourceMap == other.SourceMap;

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

        foreach (var nonParticipation in NonParticipatingWorkloads)
            hash.Add(nonParticipation);

        foreach (var boundaryAcceptance in BoundaryAcceptances)
            hash.Add(boundaryAcceptance);

        hash.Add(Fingerprint);
        hash.Add(SourceMap);
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

    static ImmutableArray<InfrastructureTargetBoundaryAcceptance> NormalizeBoundaryAcceptances(
        ImmutableArray<InfrastructureTargetBoundaryAcceptance> boundaryAcceptances)
    {
        if (boundaryAcceptances.IsDefaultOrEmpty)
            return [];

        if (boundaryAcceptances.Any(static acceptance => acceptance is null))
            throw new ArgumentException("Target boundary acceptances cannot contain null.", nameof(boundaryAcceptances));

        var ordered = boundaryAcceptances.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Boundary.Value, right.Boundary.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Boundary == ordered[index].Boundary)
            {
                throw new ArgumentException(
                    $"Target boundary acceptance '{ordered[index].Boundary.Value}' is duplicated.",
                    nameof(boundaryAcceptances));
            }
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
        InfrastructureBoundaryAcceptancePolicy? boundaryAcceptancePolicy,
        InfrastructureRealization? realization,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        Manifest = manifest;
        FacilityPlan = facilityPlan;
        BoundaryAcceptancePolicy = boundaryAcceptancePolicy;
        Realization = realization;
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
    }

    /// <summary>Exact declarative physical deployment input.</summary>
    public InfrastructureTargetDeploymentManifest Manifest { get; }

    /// <summary>Facility selection and capability-discharge result.</summary>
    public InfrastructureTargetFacilityPlan FacilityPlan { get; }

    /// <summary>
    /// Exact demand-scoped policy compiled from target-boundary declarations, or <see langword="null"/> when none were declared.
    /// </summary>
    public InfrastructureBoundaryAcceptancePolicy? BoundaryAcceptancePolicy { get; }

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

        /// <summary>A non-participation decision names no canonical workload.</summary>
        public const string WorkloadNonParticipationUnknown = "infra.target.deployment.workloadNonParticipation.unknown";

        /// <summary>A workload is both deployed and declared non-participating.</summary>
        public const string WorkloadParticipationConflict = "infra.target.deployment.workloadParticipation.conflict";

        /// <summary>A target-boundary acceptance names no operating boundary in the selected target variant.</summary>
        public const string BoundaryAcceptanceUnknown = "infra.target.deployment.boundaryAcceptance.unknown";

        /// <summary>A target-boundary acceptance does not govern any selected capability demand.</summary>
        public const string BoundaryAcceptanceUnused = "infra.target.deployment.boundaryAcceptance.unused";

        /// <summary>A foreign lifecycle manager redundantly names the selected target.</summary>
        public const string ResourceManagerSelfReference = "infra.target.deployment.resourceManager.selfReference";

        /// <summary>An external canonical resource incorrectly declares a lifecycle manager.</summary>
        public const string ExternalResourceManager = "infra.target.deployment.resourceManager.external";

        /// <summary>The declared ownership cannot form a valid canonical lifecycle plan.</summary>
        public const string ResourceLifecycleInvalid = "infra.target.deployment.resourceLifecycle.invalid";
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
        InfrastructureConventionProfile selectionProfile = new(
            new($"{manifest.Id.Value}/facility-selections"),
            selections);
        var provisionalFacilityPlan = InfrastructureTargetCompiler.Compile(
            semantic.Definition,
            manifest.TargetFacilities,
            semantic.BindingElaborationProfile,
            [selectionProfile]);
        var (boundaryAcceptancePolicy, boundaryDiagnostics) = CompileBoundaryAcceptancePolicy(
            semantic,
            manifest,
            provisionalFacilityPlan);
        var facilityPlan = boundaryAcceptancePolicy is null
            ? provisionalFacilityPlan
            : InfrastructureTargetCompiler.Compile(
                semantic.Definition,
                manifest.TargetFacilities,
                semantic.BindingElaborationProfile,
                [selectionProfile],
                boundaryAcceptancePolicy);

        ImmutableArray<DocumentValidationDiagnostic> diagnostics =
        [
            .. ValidateDeployments(semantic.Definition, manifest),
            .. boundaryDiagnostics
        ];
        InfrastructureRealization? realization = null;
        if (!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            try
            {
                var lifecycle = CompileLifecycle(semantic.Definition, manifest);
                realization = CompileRealization(manifest, facilityPlan, lifecycle);
            }
            catch (ArgumentException exception) when (exception.ParamName == nameof(InfrastructureLifecyclePlan.Bindings))
            {
                diagnostics = [.. diagnostics, ResourceLifecycleInvalid(exception, manifest)];
            }
        }

        return new(
            manifest,
            facilityPlan,
            boundaryAcceptancePolicy,
            realization,
            [.. facilityPlan.Diagnostics, .. diagnostics, .. realization?.Diagnostics ?? []]);
    }

    static (InfrastructureBoundaryAcceptancePolicy? Policy, ImmutableArray<DocumentValidationDiagnostic> Diagnostics)
        CompileBoundaryAcceptancePolicy(
            InfrastructureAuthoringResult semantic,
            InfrastructureTargetDeploymentManifest manifest,
            InfrastructureTargetFacilityPlan provisionalFacilityPlan)
    {
        if (manifest.BoundaryAcceptances.IsDefaultOrEmpty)
            return (null, []);

        var declarations = manifest.BoundaryAcceptances.ToDictionary(static acceptance => acceptance.Boundary);
        var selectedVariant = manifest.TargetFacilities.Profile.FindVariant(manifest.TargetFacilities.Variant)!;
        var knownBoundaries = selectedVariant.OperatingBoundaries.Select(static boundary => boundary.Id).ToHashSet();
        var usedBoundaries = new HashSet<InfrastructureOperatingBoundaryId>();
        var acceptances = ImmutableArray.CreateBuilder<InfrastructureBoundaryAcceptance>();
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();

        foreach (var declaration in manifest.BoundaryAcceptances)
        {
            if (!knownBoundaries.Contains(declaration.Boundary))
                diagnostics.Add(BoundaryAcceptanceDiagnostic(declaration, manifest, isUnknown: true));
        }

        foreach (var decision in provisionalFacilityPlan.CapabilityClosure.Decisions)
        {
            foreach (var boundary in decision.OperatingBoundaries)
            {
                if (!declarations.TryGetValue(boundary, out var declaration)
                    || !knownBoundaries.Contains(boundary))
                {
                    continue;
                }

                usedBoundaries.Add(boundary);
                acceptances.Add(new(
                    decision.Requirement,
                    boundary,
                    declaration.Rationale,
                    declaration.SourceReferences));
            }
        }

        foreach (var declaration in manifest.BoundaryAcceptances)
        {
            if (knownBoundaries.Contains(declaration.Boundary) && !usedBoundaries.Contains(declaration.Boundary))
                diagnostics.Add(BoundaryAcceptanceDiagnostic(declaration, manifest, isUnknown: false));
        }

        var policy = InfrastructureBoundaryAcceptancePolicy.Create(
            new($"{manifest.Id.Value}/boundary-acceptance-policy"),
            semantic.Definition,
            manifest.TargetFacilities.Profile,
            semantic.BindingElaborationProfile,
            manifest.TargetFacilities.Variant,
            acceptances.ToImmutable());
        return (policy, DocumentValidationDiagnostics.Normalize(diagnostics.ToImmutable()));
    }

    static DocumentValidationDiagnostic BoundaryAcceptanceDiagnostic(
        InfrastructureTargetBoundaryAcceptance declaration,
        InfrastructureTargetDeploymentManifest manifest,
        bool isUnknown)
    {
        var manifestReference = InfrastructureSourceReferences.TargetDeploymentManifest(manifest.ToReference()).Value;
        var authoringSources = manifest.SourceMap.Resolve(
            InfrastructureSourceReferences.OperatingBoundary(declaration.Boundary));
        return new(
            isUnknown ? DiagnosticCodes.BoundaryAcceptanceUnknown : DiagnosticCodes.BoundaryAcceptanceUnused,
            isUnknown ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            isUnknown
                ? $"Target boundary acceptance '{declaration.Boundary.Value}' is absent from selected target variant '{manifest.TargetFacilities.Variant.Value}'."
                : $"Target boundary acceptance '{declaration.Boundary.Value}' does not govern any selected capability demand.",
            SchemaLocation: declaration.Boundary.Value,
            Evidence: new(
                stage: Stage,
                subject: declaration.Boundary.Value,
                sourceReferences:
                [
                    .. declaration.SourceReferences
                        .Concat(authoringSources)
                        .Select(static source => source.Value)
                        .Append(manifestReference)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                ],
                resolutionOptions: isUnknown
                    ?
                    [
                        "Declare an operating boundary supplied by the selected target variant.",
                        "Remove the stale target-boundary acceptance from this deployment."
                    ]
                    :
                    [
                        "Remove the unused target-boundary acceptance.",
                        "Retain it only if the deployment intentionally anticipates a declared capability demand."
                    ],
                expected: isUnknown ? "an operating boundary declared by the selected target variant" : null,
                observed: declaration.Boundary.Value));
    }

    static InfrastructureLifecyclePlan CompileLifecycle(
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetDeploymentManifest manifest)
    {
        var target = manifest.TargetFacilities.Profile.Target;
        var lifecycleByResource = definition.Definition.Resources.ToDictionary(static resource => resource.Id);
        var lifecycleBindings = ImmutableArray.CreateBuilder<InfrastructureResourceLifecycleBinding>(
            manifest.Resources.Length
            + manifest.Resources.Count(static deployment => deployment.ManagingInterpreter is not null));
        foreach (var deployment in manifest.Resources)
        {
            var canonicalLifecycle = lifecycleByResource[deployment.Resource].Lifecycle;
            if (canonicalLifecycle == InfrastructureResourceLifecycle.External)
            {
                lifecycleBindings.Add(new(
                    deployment.Resource,
                    deployment.PhysicalResource,
                    target,
                    deployment.Authority,
                    InfrastructureLifecycleDisposition.Referenced));
                continue;
            }

            if (deployment.ManagingInterpreter is { } managingInterpreter)
            {
                lifecycleBindings.Add(new(
                    deployment.Resource,
                    deployment.PhysicalResource,
                    managingInterpreter,
                    deployment.Authority,
                    InfrastructureLifecycleDisposition.Managed));
                lifecycleBindings.Add(new(
                    deployment.Resource,
                    deployment.PhysicalResource,
                    target,
                    deployment.Authority,
                    InfrastructureLifecycleDisposition.Referenced));
                continue;
            }

            lifecycleBindings.Add(new(
                deployment.Resource,
                deployment.PhysicalResource,
                target,
                deployment.Authority,
                InfrastructureLifecycleDisposition.Managed));
        }

        return new InfrastructureLifecyclePlan(
            definition,
            lifecycleBindings.MoveToImmutable());
    }

    static InfrastructureRealization CompileRealization(
        InfrastructureTargetDeploymentManifest manifest,
        InfrastructureTargetFacilityPlan facilityPlan,
        InfrastructureLifecyclePlan lifecycle)
    {
        var target = manifest.TargetFacilities.Profile.Target;
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
            placements,
            nonParticipatingWorkloads: manifest.NonParticipatingWorkloads);
        var witnesses = CreateWitnesses(manifest, provisional);
        return InfrastructureRealizationCompiler.Compile(
            facilityPlan.CapabilityClosure,
            lifecycle,
            placements,
            witnesses,
            manifest.NonParticipatingWorkloads);
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
        var resources = definition.Definition.Resources.ToDictionary(static item => item.Id);
        var declaredWorkloads = manifest.Workloads.Select(static item => item.Workload).ToHashSet();
        var declaredResources = manifest.Resources.Select(static item => item.Resource).ToHashSet();
        var nonParticipatingWorkloads = manifest.NonParticipatingWorkloads
            .Select(static item => item.Workload)
            .ToHashSet();

        foreach (var deployment in manifest.Workloads)
        {
            if (!workloads.Contains(deployment.Workload))
            {
                diagnostics.Add(Unknown(
                    deployment.Workload,
                    InfrastructureNodeKind.Workload,
                    deployment.SourceReferences,
                    manifest.SourceMap));
            }
        }
        foreach (var deployment in manifest.Resources)
        {
            if (!resources.TryGetValue(deployment.Resource, out var resource))
            {
                diagnostics.Add(Unknown(
                    deployment.Resource,
                    InfrastructureNodeKind.Resource,
                    deployment.SourceReferences,
                    manifest.SourceMap));
                continue;
            }

            if (deployment.ManagingInterpreter == manifest.TargetFacilities.Profile.Target)
                diagnostics.Add(ResourceManagerSelfReference(deployment, manifest));
            if (resource.Lifecycle == InfrastructureResourceLifecycle.External
                && deployment.ManagingInterpreter is not null)
            {
                diagnostics.Add(ExternalResourceManager(deployment, manifest));
            }
        }

        foreach (var decision in manifest.NonParticipatingWorkloads)
        {
            if (!workloads.Contains(decision.Workload))
            {
                diagnostics.Add(UnknownNonParticipation(decision, manifest));
            }
            else if (declaredWorkloads.Contains(decision.Workload))
            {
                diagnostics.Add(ParticipationConflict(decision, manifest));
            }
        }
        foreach (var workload in workloads.Where(workload =>
                     !declaredWorkloads.Contains(workload)
                     && !nonParticipatingWorkloads.Contains(workload)))
        {
            diagnostics.Add(Missing(workload, InfrastructureNodeKind.Workload, manifest));
        }

        foreach (var resource in resources.Keys.Where(resource => !declaredResources.Contains(resource)))
            diagnostics.Add(Missing(resource, InfrastructureNodeKind.Resource, manifest));

        return DocumentValidationDiagnostics.Normalize(diagnostics.ToImmutable());
    }

    static DocumentValidationDiagnostic ResourceManagerSelfReference(
        InfrastructureTargetResourceDeployment deployment,
        InfrastructureTargetDeploymentManifest manifest) => new(
        DiagnosticCodes.ResourceManagerSelfReference,
        DiagnosticSeverity.Error,
        $"Resource '{deployment.Resource.Value}' names selected target '{deployment.ManagingInterpreter!.Value.Value}' as a foreign lifecycle manager.",
        SchemaLocation: deployment.Resource.Value,
        Evidence: ResourceOwnershipEvidence(
            deployment.Resource,
            deployment.SourceReferences,
            manifest,
            resolutionOptions: ["Declare the resource with Resource(...) so the selected target manages it directly."],
            expected: "a foreign lifecycle interpreter distinct from the selected target",
            observed: deployment.ManagingInterpreter.Value.Value));

    static DocumentValidationDiagnostic ExternalResourceManager(
        InfrastructureTargetResourceDeployment deployment,
        InfrastructureTargetDeploymentManifest manifest) => new(
        DiagnosticCodes.ExternalResourceManager,
        DiagnosticSeverity.Error,
        $"External resource '{deployment.Resource.Value}' cannot declare lifecycle manager '{deployment.ManagingInterpreter!.Value.Value}'.",
        SchemaLocation: deployment.Resource.Value,
        Evidence: ResourceOwnershipEvidence(
            deployment.Resource,
            deployment.SourceReferences,
            manifest,
            resolutionOptions:
            [
                "Declare the external resource with Resource(...) so the selected target only references it.",
                "Change the canonical resource lifecycle when this realization is responsible for management."
            ],
            expected: "no lifecycle manager for an external canonical resource",
            observed: deployment.ManagingInterpreter.Value.Value));

    static DocumentValidationDiagnostic ResourceLifecycleInvalid(
        ArgumentException exception,
        InfrastructureTargetDeploymentManifest manifest) => new(
        DiagnosticCodes.ResourceLifecycleInvalid,
        DiagnosticSeverity.Error,
        exception.Message,
        SchemaLocation: manifest.Id.Value,
        Evidence: new(
            stage: Stage,
            subject: manifest.Id.Value,
            sourceReferences:
            [
                .. manifest.Resources
                    .SelectMany(deployment => deployment.SourceReferences.Concat(
                        manifest.SourceMap.Resolve(InfrastructureSourceReferences.Node(deployment.Resource))))
                    .Select(static source => source.Value)
                    .Append(InfrastructureSourceReferences.TargetDeploymentManifest(manifest.ToReference()).Value)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ],
            resolutionOptions:
            [
                "Use one physical identity, lifecycle authority, and managing interpreter for every logical alias.",
                "Use distinct physical-resource identities when declarations have independent lifecycle ownership."
            ],
            expected: "a valid lifecycle plan with exactly one manager for every managed physical resource",
            observed: exception.Message));

    static DocumentDiagnosticEvidence ResourceOwnershipEvidence(
        InfrastructureNodeId subject,
        ImmutableArray<SourceReference> sources,
        InfrastructureTargetDeploymentManifest manifest,
        ImmutableArray<string> resolutionOptions,
        string expected,
        string observed) => new(
        stage: Stage,
        subject: subject.Value,
        sourceReferences:
        [
            .. sources
                .Concat(manifest.SourceMap.Resolve(InfrastructureSourceReferences.Node(subject)))
                .Select(static source => source.Value)
                .Append(InfrastructureSourceReferences.TargetDeploymentManifest(manifest.ToReference()).Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ],
        resolutionOptions: resolutionOptions,
        expected: expected,
        observed: observed);

    static DocumentValidationDiagnostic UnknownNonParticipation(
        InfrastructureWorkloadNonParticipation decision,
        InfrastructureTargetDeploymentManifest manifest) => new(
        DiagnosticCodes.WorkloadNonParticipationUnknown,
        DiagnosticSeverity.Error,
        $"Workload non-participation decision '{decision.Workload.Value}' is not a canonical workload.",
        SchemaLocation: decision.Workload.Value,
        Evidence: new(
            stage: Stage,
            subject: decision.Workload.Value,
            sourceReferences:
            [
                .. decision.SourceReferences.Select(static source => source.Value),
                InfrastructureSourceReferences.TargetDeploymentManifest(manifest.ToReference()).Value
            ],
            resolutionOptions: ["Remove the stale decision or bind it to an exact canonical workload."],
            expected: "a canonical workload identity",
            observed: "unknown or wrong-kind node"));

    static DocumentValidationDiagnostic ParticipationConflict(
        InfrastructureWorkloadNonParticipation decision,
        InfrastructureTargetDeploymentManifest manifest) => new(
        DiagnosticCodes.WorkloadParticipationConflict,
        DiagnosticSeverity.Error,
        $"Workload '{decision.Workload.Value}' is both deployed and declared non-participating.",
        SchemaLocation: decision.Workload.Value,
        Evidence: new(
            stage: Stage,
            subject: decision.Workload.Value,
            sourceReferences:
            [
                .. decision.SourceReferences.Select(static source => source.Value),
                .. manifest.FindWorkload(decision.Workload).SourceReferences.Select(static source => source.Value),
                InfrastructureSourceReferences.TargetDeploymentManifest(manifest.ToReference()).Value
            ],
            resolutionOptions:
            [
                "Remove the non-participation decision when this workload is deployed.",
                "Remove the deployment when this workload is intentionally absent."
            ],
            expected: "exactly one participation state",
            observed: "deployed and non-participating"));

    static DocumentValidationDiagnostic Unknown(
        InfrastructureNodeId node,
        InfrastructureNodeKind kind,
        ImmutableArray<SourceReference> sources,
        InfrastructureSourceMap sourceMap) => new(
        DiagnosticCodes.NodeUnknown,
        DiagnosticSeverity.Error,
        $"Target deployment declaration '{node.Value}' is not a canonical {kind.ToString().ToLowerInvariant()}.",
        SchemaLocation: node.Value,
        Evidence: new(
            stage: Stage,
            subject: node.Value,
            sourceReferences:
            [
                .. sources
                    .Concat(sourceMap.Resolve(InfrastructureSourceReferences.Node(node)))
                    .Select(static source => source.Value)
                    .Distinct()
            ],
            resolutionOptions: ["Remove the stale deployment or bind it to an exact canonical node."],
            expected: $"a canonical {kind.ToString().ToLowerInvariant()} identity",
            observed: "unknown or wrong-kind node"));

    static DocumentValidationDiagnostic Missing(
        InfrastructureNodeId node,
        InfrastructureNodeKind kind,
        InfrastructureTargetDeploymentManifest manifest) => new(
        kind == InfrastructureNodeKind.Workload ? DiagnosticCodes.WorkloadMissing : DiagnosticCodes.ResourceMissing,
        DiagnosticSeverity.Error,
        $"Canonical {kind.ToString().ToLowerInvariant()} '{node.Value}' has no target deployment declaration.",
        SchemaLocation: node.Value,
        Evidence: new(
            stage: Stage,
            subject: node.Value,
            sourceReferences: [InfrastructureSourceReferences.TargetDeploymentManifest(manifest.ToReference()).Value],
            resolutionOptions: kind == InfrastructureNodeKind.Workload
                ?
                [
                    "Declare an exact physical deployment through the selected target adapter.",
                    "Declare attributable workload non-participation when this deployment intentionally excludes it."
                ]
                : ["Declare an exact physical deployment through the selected target adapter."],
            expected: kind == InfrastructureNodeKind.Workload
                ? "one attributable physical deployment or non-participation decision"
                : "one attributable physical deployment",
            observed: kind == InfrastructureNodeKind.Workload
                ? "no participation decision"
                : "no deployment declaration"));

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
        ImmutableArray<InfrastructureTargetResourceDeployment> resources,
        ImmutableArray<InfrastructureWorkloadNonParticipation> nonParticipatingWorkloads,
        ImmutableArray<InfrastructureTargetBoundaryAcceptance> boundaryAcceptances)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                schemaVersion,
                id,
                definition,
                targetFacilities,
                workloads,
                resources,
                nonParticipatingWorkloads,
                boundaryAcceptances),
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
        ImmutableArray<InfrastructureTargetResourceDeployment> Resources,
        ImmutableArray<InfrastructureWorkloadNonParticipation> NonParticipatingWorkloads,
        ImmutableArray<InfrastructureTargetBoundaryAcceptance> BoundaryAcceptances);
}
