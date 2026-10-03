using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra;

/// <summary>Fluent producer for canonical target-deployment manifests.</summary>
public static class InfrastructureTargetDeployments
{
    /// <summary>Materializes one deterministic target-deployment manifest.</summary>
    /// <param name="id">Stable versioned deployment identity.</param>
    /// <param name="definition">Exact canonical definition being deployed.</param>
    /// <param name="targetFacilities">Declarative target facilities available to the deployment.</param>
    /// <param name="configure">Synchronous physical-resource and lifecycle declaration.</param>
    /// <returns>An immutable, normalized, exactly fingerprinted deployment manifest.</returns>
    /// <exception cref="ArgumentNullException">A reference argument or <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A supplied identity, deployment, facility, or source reference is invalid.</exception>
    public static InfrastructureTargetDeploymentManifest Define(
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest targetFacilities,
        Action<InfrastructureTargetDeploymentManifestBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(targetFacilities);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new InfrastructureTargetDeploymentManifestBuilder(id, definition, targetFacilities);
        configure(builder);
        return builder.Build();
    }
    /// <summary>Authors implementation selections and physical placements together, without a separate facility inventory.</summary>
    /// <param name="id">Stable deployment identity.</param>
    /// <param name="definition">Exact definition being projected.</param>
    /// <param name="facilityManifestId">Identity of the derived facility manifest.</param>
    /// <param name="profileId">Identity of the derived capability profile.</param>
    /// <param name="target">Native interpreter identity.</param>
    /// <param name="variant">Coherent target variant.</param>
    /// <param name="supportedDefinitionSchemaVersions">Schemas understood by this target.</param>
    /// <param name="configure">Synchronous placements, implementation selections and capability rules.</param>
    /// <returns>The ordinary portable deployment manifest; no authoring callbacks survive.</returns>
    /// <exception cref="ArgumentNullException">Definition or configure is null.</exception>
    /// <exception cref="ArgumentException">An identity, selection, evidence or placement is invalid or conflicting.</exception>
    public static InfrastructureTargetDeploymentManifest Define(
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifestId facilityManifestId,
        InfrastructureCapabilityProfileId profileId,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<string> supportedDefinitionSchemaVersions,
        Action<InfrastructureTargetDeploymentManifestBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(configure);
        var facilities = new InfrastructureTargetFacilityManifestBuilder(facilityManifestId, profileId, target,
            variant, supportedDefinitionSchemaVersions);
        var builder = new InfrastructureTargetDeploymentManifestBuilder(id, definition, facilities);
        configure(builder);
        return builder.Build();
    }

}

/// <summary>Fluent producer for one canonical target-deployment manifest.</summary>
public sealed class InfrastructureTargetDeploymentManifestBuilder
{
    readonly InfrastructureTargetDeploymentManifestId id;
    readonly InfrastructureDefinitionDocument definition;
    readonly InfrastructureTargetFacilityManifest? targetFacilities;
    readonly InfrastructureTargetFacilityManifestBuilder? inlineFacilities;
    readonly Dictionary<InfrastructureTargetFacilityId, InfrastructureTargetImplementation> implementations = [];
    readonly List<InfrastructureTargetWorkloadDeployment> workloads = [];
    readonly List<InfrastructureTargetResourceDeployment> resources = [];
    readonly List<InfrastructureWorkloadNonParticipation> nonParticipatingWorkloads = [];
    readonly List<InfrastructureTargetBoundaryAcceptance> boundaryAcceptances = [];
    readonly List<InfrastructureSourceProvenance> sourceMap = [];

    internal InfrastructureTargetDeploymentManifestBuilder(
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest targetFacilities)
    {
        this.id = id;
        this.definition = definition;
        this.targetFacilities = targetFacilities;
    }

    internal InfrastructureTargetDeploymentManifestBuilder(
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifestBuilder facilities)
    {
        this.id = id;
        this.definition = definition;
        inlineFacilities = facilities;
    }

    /// <summary>Adds a composition rule to an inline target profile.</summary>
    /// <param name="rule">Attributable cross-facility capability rule.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">This builder uses a separately authored facility manifest.</exception>
    /// <exception cref="ArgumentNullException">Rule is null.</exception>
    public InfrastructureTargetDeploymentManifestBuilder Composes(InfrastructureCapabilityRule rule)
    {
        RequireInline().Composes(rule);
        return this;
    }

    /// <summary>Adds an operating boundary to an inline target profile.</summary>
    /// <param name="boundary">Attributable boundary referenced by evidence or rules.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">This builder uses a separately authored facility manifest.</exception>
    /// <exception cref="ArgumentNullException">Boundary is null.</exception>
    public InfrastructureTargetDeploymentManifestBuilder Within(InfrastructureOperatingBoundary boundary)
    {
        RequireInline().Within(boundary);
        return this;
    }

    InfrastructureTargetFacilityManifestBuilder RequireInline() => inlineFacilities ??
        throw new InvalidOperationException("Implementation selection requires inline target authoring.");

    InfrastructureTargetFacilityId Select(InfrastructureTargetImplementation implementation, InfrastructureNodeKind kind,
        string file, int line, string member)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        var facilities = RequireInline();
        if (implementation.Facility.NodeKind != kind)
            throw new ArgumentException("Implementation kind does not match the declaration.", nameof(implementation));
        if (implementations.TryGetValue(implementation.Facility.Id, out var previous))
        {
            if (!previous.Facility.Equals(implementation.Facility) || !previous.Evidence.SequenceEqual(implementation.Evidence))
                throw new ArgumentException("Conflicting evidence for an already selected implementation.", nameof(implementation));
            return implementation.Facility.Id;
        }
        var facility = kind == InfrastructureNodeKind.Workload
            ? facilities.Workload(implementation.Facility.Id, file, line, member)
            : facilities.Resource(implementation.Facility.Id, file, line, member);
        foreach (var evidence in implementation.Evidence) facility.Provides(evidence, file, line, member);
        implementations.Add(implementation.Facility.Id, implementation);
        return implementation.Facility.Id;
    }

    /// <summary>Declares one exact workload deployment.</summary>
    /// <param name="workload">Canonical workload identity.</param>
    /// <param name="implementation">Target facility materializing the workload.</param>
    /// <param name="physicalResource">Exact target-native deployment identity.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder uses a separately authored facility manifest.</exception>
    /// <exception cref="ArgumentNullException">Implementation is null.</exception>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder WorkloadUsing(
        InfrastructureNodeId workload,
        InfrastructureTargetImplementation implementation,
        InfrastructurePhysicalResourceId physicalResource,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Workload(workload, Select(implementation, InfrastructureNodeKind.Workload, sourceFile, sourceLine, sourceMember), physicalResource, sourceReferences, sourceFile, sourceLine, sourceMember);

    /// <summary>Declares one exact workload deployment.</summary>
    /// <param name="workload">Canonical workload identity.</param>
    /// <param name="facility">Target facility materializing the workload.</param>
    /// <param name="physicalResource">Exact target-native deployment identity.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder Workload(
        InfrastructureNodeId workload,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        workloads.Add(new(workload, facility, physicalResource, sourceReferences));
        sourceMap.Add(Capture(InfrastructureSourceReferences.Node(workload), sourceFile, sourceLine, sourceMember));
        return this;
    }

    /// <summary>Declares that one canonical workload intentionally does not participate in this deployment.</summary>
    /// <param name="workload">Canonical workload identity.</param>
    /// <param name="rationale">Human-legible environment or subsystem rationale.</param>
    /// <param name="sourceReferences">Attributable environment, subsystem, policy, or deployment sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity, rationale, or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder NonParticipatingWorkload(
        InfrastructureNodeId workload,
        string rationale,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        nonParticipatingWorkloads.Add(new(workload, rationale, sourceReferences));
        sourceMap.Add(Capture(InfrastructureSourceReferences.Node(workload), sourceFile, sourceLine, sourceMember));
        return this;
    }

    /// <summary>Accepts one named operating boundary wherever selected target evidence uses it.</summary>
    /// <param name="boundary">Operating boundary accepted by this target deployment.</param>
    /// <param name="rationale">Human-reviewable environment-policy rationale.</param>
    /// <param name="sourceReferences">Attributable policy, approval, or specification sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The boundary, rationale, or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder AcceptBoundary(
        InfrastructureOperatingBoundaryId boundary,
        string rationale,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        boundaryAcceptances.Add(new(boundary, rationale, sourceReferences));
        sourceMap.Add(Capture(InfrastructureSourceReferences.OperatingBoundary(boundary), sourceFile, sourceLine, sourceMember));
        return this;
    }

    /// <summary>Declares one exact resource deployment and lifecycle authority.</summary>
    /// <param name="resource">Canonical resource identity.</param>
    /// <param name="implementation">Target facility materializing or referencing the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity.</param>
    /// <param name="authority">Backend state scope or external authority that owns the resource lifecycle.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder uses a separately authored facility manifest.</exception>
    /// <exception cref="ArgumentNullException">Implementation is null.</exception>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder ResourceUsing(
        InfrastructureNodeId resource,
        InfrastructureTargetImplementation implementation,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Resource(resource, Select(implementation, InfrastructureNodeKind.Resource, sourceFile, sourceLine, sourceMember), physicalResource, authority, sourceReferences, sourceFile, sourceLine, sourceMember);

    /// <summary>Declares one exact resource deployment and lifecycle authority.</summary>
    /// <param name="resource">Canonical resource identity.</param>
    /// <param name="facility">Target facility materializing or referencing the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity.</param>
    /// <param name="authority">Backend state scope or external authority that owns the resource lifecycle.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder Resource(
        InfrastructureNodeId resource,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        resources.Add(new(resource, facility, physicalResource, authority, sourceReferences));
        sourceMap.Add(Capture(InfrastructureSourceReferences.Node(resource), sourceFile, sourceLine, sourceMember));
        return this;
    }

    /// <summary>Declares a resource consumed by this target but managed by another lifecycle interpreter.</summary>
    /// <param name="resource">Canonical resource identity.</param>
    /// <param name="implementation">Target facility referencing the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity shared with the managing interpreter.</param>
    /// <param name="managingInterpreter">Exact foreign interpreter that manages the resource lifecycle.</param>
    /// <param name="authority">Backend state scope owned by the managing interpreter.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The builder uses a separately authored facility manifest.</exception>
    /// <exception cref="ArgumentNullException">Implementation is null.</exception>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder ReferencedResourceUsing(
        InfrastructureNodeId resource,
        InfrastructureTargetImplementation implementation,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureTargetId managingInterpreter,
        InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        ReferencedResource(resource, Select(implementation, InfrastructureNodeKind.Resource, sourceFile, sourceLine, sourceMember), physicalResource, managingInterpreter, authority, sourceReferences, sourceFile, sourceLine, sourceMember);

    /// <summary>Declares a resource consumed by this target but managed by another lifecycle interpreter.</summary>
    /// <param name="resource">Canonical resource identity.</param>
    /// <param name="facility">Target facility referencing the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity shared with the managing interpreter.</param>
    /// <param name="managingInterpreter">Exact foreign interpreter that manages the resource lifecycle.</param>
    /// <param name="authority">Backend state scope owned by the managing interpreter.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder ReferencedResource(
        InfrastructureNodeId resource,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureTargetId managingInterpreter,
        InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        resources.Add(new(
            resource,
            facility,
            physicalResource,
            authority,
            sourceReferences,
            managingInterpreter));
        sourceMap.Add(Capture(InfrastructureSourceReferences.Node(resource), sourceFile, sourceLine, sourceMember));
        return this;
    }

    internal InfrastructureTargetDeploymentManifest Build() => new(
        InfrastructureTargetDeploymentManifest.CurrentSchemaVersion,
        id,
        definition.ToReference(),
        targetFacilities ?? inlineFacilities!.Build(),
        [.. workloads],
        [.. resources],
        [.. nonParticipatingWorkloads],
        [.. boundaryAcceptances],
        sourceMap: new([.. sourceMap]));

    InfrastructureSourceProvenance Capture(
        SourceReference subject,
        string sourceFile,
        int sourceLine,
        string sourceMember) => InfrastructureAuthoringSource.Capture(
            subject,
            InfrastructureSourceReferences.TargetDeploymentManifest(id),
            sourceFile,
            sourceLine,
            sourceMember);
}
