using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra.Local;

/// <summary>Coordinated authoring projection for one target-local infrastructure refinement.</summary>
public static class InfrastructureLocalDeployments
{
    /// <summary>
    /// Materializes an exact target-deployment manifest and local topology from one coordinated refinement.
    /// </summary>
    /// <param name="id">Stable versioned target-deployment identity.</param>
    /// <param name="definition">Exact canonical infrastructure definition being refined.</param>
    /// <param name="targetFacilities">Declarative target facilities available to the refinement.</param>
    /// <param name="configure">Synchronous physical placement and local construction declaration.</param>
    /// <returns>The independently portable target-deployment and local-topology artifacts.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="targetFacilities"/>, or <paramref name="configure"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A supplied identity, placement, source, or topology value is invalid.</exception>
    public static InfrastructureLocalDeploymentAuthoringResult Define(
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest targetFacilities,
        Action<InfrastructureLocalDeploymentBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(targetFacilities);
        ArgumentNullException.ThrowIfNull(configure);

        InfrastructureLocalDeploymentBuilder builder = new(id, definition, targetFacilities);
        configure(builder);
        return builder.Build();
    }
}

/// <summary>
/// Immutable output of one coordinated target-deployment and local-topology authoring session.
/// </summary>
/// <remarks>
/// This value groups two independently portable canonical artifacts. It is not another semantic authority:
/// target-deployment and local-realization compilers continue to consume <see cref="TargetDeployment"/> and
/// <see cref="Topology"/> through their existing exact contracts.
/// </remarks>
public sealed record InfrastructureLocalDeploymentAuthoringResult
{
    /// <summary>Creates a coordinated local-deployment authoring result.</summary>
    /// <param name="targetDeployment">Exactly fingerprinted target-deployment manifest.</param>
    /// <param name="topology">Normalized target-neutral local topology.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="targetDeployment"/> or <paramref name="topology"/> is <see langword="null"/>.
    /// </exception>
    public InfrastructureLocalDeploymentAuthoringResult(
        InfrastructureTargetDeploymentManifest targetDeployment,
        InfrastructureLocalTopology topology)
    {
        TargetDeployment = Guard.RequireNotNull(targetDeployment);
        Topology = Guard.RequireNotNull(topology);
    }

    /// <summary>Exactly fingerprinted physical refinement of the canonical infrastructure definition.</summary>
    public InfrastructureTargetDeploymentManifest TargetDeployment { get; }

    /// <summary>Target-neutral local construction topology derived during the same authoring session.</summary>
    public InfrastructureLocalTopology Topology { get; }
}

/// <summary>
/// Mutable producer that associates every service-backed logical node with one physical service exactly once.
/// </summary>
public sealed class InfrastructureLocalDeploymentBuilder
{
    readonly InfrastructureDefinitionDocument definition;
    readonly InfrastructureTargetDeploymentManifestBuilder targetDeployment;
    readonly InfrastructureTargetId target;
    readonly InfrastructureLocalBuilder topology = new();
    readonly HashSet<InfrastructureNodeId> participatingWorkloads = [];
    readonly HashSet<InfrastructureNodeId> explicitNonParticipatingWorkloads = [];
    RemainingWorkloadPolicy? remainingWorkloadPolicy;

    internal InfrastructureLocalDeploymentBuilder(
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest targetFacilities)
    {
        this.definition = definition;
        target = targetFacilities.Profile.Target;
        targetDeployment = new(id, definition, targetFacilities);
    }

    /// <summary>Declares one repository-project-backed workload placement and local service.</summary>
    /// <param name="workload">Canonical logical workload.</param>
    /// <param name="facility">Target facility materializing the workload.</param>
    /// <param name="physicalResource">Exact target-native workload identity.</param>
    /// <param name="project">Structured project source used by the local service and placement attribution.</param>
    /// <param name="configure">Optional local endpoint, environment, health, and readiness configuration.</param>
    /// <param name="sourceReferences">Additional attributable target, environment, or adapter sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid.</exception>
    public InfrastructureLocalDeploymentBuilder ProjectService(
        InfrastructureNodeId workload,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLocalProjectSource project,
        Action<InfrastructureLocalServiceBuilder>? configure = null,
        ImmutableArray<SourceReference> sourceReferences = default,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(project);
        targetDeployment.Workload(
            workload,
            facility,
            physicalResource,
            Include(sourceReferences, project.Reference),
            sourceFile,
            sourceLine,
            sourceMember);
        topology.ProjectService(workload, physicalResource, project, configure);
        participatingWorkloads.Add(workload);
        return this;
    }

    /// <summary>Declares one pinned-container-backed workload placement and local service.</summary>
    /// <param name="workload">Canonical logical workload.</param>
    /// <param name="facility">Target facility materializing the workload.</param>
    /// <param name="physicalResource">Exact target-native workload identity.</param>
    /// <param name="image">Pinned container image used to construct the local service.</param>
    /// <param name="sourceReferences">Non-empty attributable target, artifact, environment, or adapter sources.</param>
    /// <param name="configure">Optional local command, endpoint, environment, health, mount, and readiness configuration.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity, image, or source-reference collection is invalid.</exception>
    public InfrastructureLocalDeploymentBuilder ContainerWorkload(
        InfrastructureNodeId workload,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        string image,
        ImmutableArray<SourceReference> sourceReferences,
        Action<InfrastructureLocalServiceBuilder>? configure = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        targetDeployment.Workload(
            workload,
            facility,
            physicalResource,
            sourceReferences,
            sourceFile,
            sourceLine,
            sourceMember);
        topology.ContainerService(workload, physicalResource, image, configure);
        participatingWorkloads.Add(workload);
        return this;
    }

    /// <summary>Declares one managed, pinned-container-backed resource placement and local service.</summary>
    /// <param name="resource">Canonical logical resource.</param>
    /// <param name="facility">Target facility materializing the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity.</param>
    /// <param name="authority">Lifecycle authority that manages the resource.</param>
    /// <param name="image">Pinned container image used to construct the local service.</param>
    /// <param name="sourceReferences">Non-empty attributable target, artifact, environment, or adapter sources.</param>
    /// <param name="configure">Optional local command, endpoint, environment, health, mount, and readiness configuration.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity, image, or source-reference collection is invalid.</exception>
    public InfrastructureLocalDeploymentBuilder ContainerResource(
        InfrastructureNodeId resource,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLifecycleAuthorityId authority,
        string image,
        ImmutableArray<SourceReference> sourceReferences,
        Action<InfrastructureLocalServiceBuilder>? configure = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        targetDeployment.Resource(
            resource,
            facility,
            physicalResource,
            authority,
            sourceReferences,
            sourceFile,
            sourceLine,
            sourceMember);
        topology.ContainerService(resource, physicalResource, image, configure);
        return this;
    }

    /// <summary>Declares one foreign-managed resource placement and referenced local service.</summary>
    /// <param name="resource">Canonical logical resource.</param>
    /// <param name="facility">Target facility referencing the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity.</param>
    /// <param name="managingInterpreter">Foreign interpreter that manages the resource lifecycle.</param>
    /// <param name="authority">Lifecycle authority owned by the managing interpreter.</param>
    /// <param name="representativeEndpoint">Host-loopback endpoint representing the foreign service.</param>
    /// <param name="sourceReferences">Non-empty attributable target, artifact, environment, or adapter sources.</param>
    /// <param name="configure">Optional endpoint, health, and readiness configuration.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid.</exception>
    public InfrastructureLocalDeploymentBuilder ReferencedResourceService(
        InfrastructureNodeId resource,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureTargetId managingInterpreter,
        InfrastructureLifecycleAuthorityId authority,
        InfrastructureLocalEndpointId representativeEndpoint,
        ImmutableArray<SourceReference> sourceReferences,
        Action<InfrastructureLocalServiceBuilder>? configure = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        targetDeployment.ReferencedResource(
            resource,
            facility,
            physicalResource,
            managingInterpreter,
            authority,
            sourceReferences,
            sourceFile,
            sourceLine,
            sourceMember);
        topology.ReferencedService(
            resource,
            physicalResource,
            target,
            representativeEndpoint,
            configure);
        return this;
    }

    /// <summary>Declares a non-service resource placement in the target refinement.</summary>
    /// <param name="resource">Canonical logical resource.</param>
    /// <param name="facility">Target facility materializing or referencing the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity.</param>
    /// <param name="authority">Lifecycle authority associated with the resource.</param>
    /// <param name="sourceReferences">Non-empty attributable target, artifact, environment, or adapter sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid.</exception>
    public InfrastructureLocalDeploymentBuilder Resource(
        InfrastructureNodeId resource,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        targetDeployment.Resource(
            resource,
            facility,
            physicalResource,
            authority,
            sourceReferences,
            sourceFile,
            sourceLine,
            sourceMember);
        return this;
    }

    /// <summary>Declares a foreign-managed non-service resource placement in the target refinement.</summary>
    /// <param name="resource">Canonical logical resource.</param>
    /// <param name="facility">Target facility referencing the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity.</param>
    /// <param name="managingInterpreter">Foreign interpreter that manages the resource lifecycle.</param>
    /// <param name="authority">Lifecycle authority owned by the managing interpreter.</param>
    /// <param name="sourceReferences">Non-empty attributable target, artifact, environment, or adapter sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid.</exception>
    public InfrastructureLocalDeploymentBuilder ReferencedResource(
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
        targetDeployment.ReferencedResource(
            resource,
            facility,
            physicalResource,
            managingInterpreter,
            authority,
            sourceReferences,
            sourceFile,
            sourceLine,
            sourceMember);
        return this;
    }

    /// <summary>Declares one canonical workload non-participating in this environment.</summary>
    /// <param name="workload">Canonical logical workload.</param>
    /// <param name="rationale">Human-reviewable environment-policy rationale.</param>
    /// <param name="sourceReferences">Non-empty attributable environment or policy sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity, rationale, or source-reference collection is invalid.</exception>
    public InfrastructureLocalDeploymentBuilder NonParticipatingWorkload(
        InfrastructureNodeId workload,
        string rationale,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        targetDeployment.NonParticipatingWorkload(
            workload,
            rationale,
            sourceReferences,
            sourceFile,
            sourceLine,
            sourceMember);
        explicitNonParticipatingWorkloads.Add(workload);
        return this;
    }

    /// <summary>
    /// Applies one attributable closed-world policy to every canonical workload not placed or explicitly classified.
    /// </summary>
    /// <param name="rationale">Human-reviewable reason unplaced workloads do not participate.</param>
    /// <param name="sourceReferences">Non-empty attributable environment or policy sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A remaining-workload policy was already declared.</exception>
    /// <exception cref="ArgumentException">The rationale or source-reference collection is invalid.</exception>
    public InfrastructureLocalDeploymentBuilder NonParticipatingWorkloadsByDefault(
        string rationale,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        if (remainingWorkloadPolicy is not null)
            throw new InvalidOperationException("A default non-participation policy is already declared.");

        remainingWorkloadPolicy = new(
            Guard.RequireNotNullOrWhiteSpace(rationale),
            SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true),
            sourceFile,
            sourceLine,
            sourceMember);
        return this;
    }

    /// <summary>Accepts one named operating boundary for the target refinement.</summary>
    /// <param name="boundary">Operating boundary accepted by environment policy.</param>
    /// <param name="rationale">Human-reviewable environment-policy rationale.</param>
    /// <param name="sourceReferences">Non-empty attributable policy, approval, or specification sources.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The boundary, rationale, or source-reference collection is invalid.</exception>
    public InfrastructureLocalDeploymentBuilder AcceptBoundary(
        InfrastructureOperatingBoundaryId boundary,
        string rationale,
        ImmutableArray<SourceReference> sourceReferences,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        targetDeployment.AcceptBoundary(
            boundary,
            rationale,
            sourceReferences,
            sourceFile,
            sourceLine,
            sourceMember);
        return this;
    }

    /// <summary>Adds a named local volume used by service configuration.</summary>
    /// <param name="id">Stable topology-local volume identity.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalDeploymentBuilder Volume(InfrastructureLocalVolumeId id)
    {
        topology.Volume(id);
        return this;
    }

    /// <summary>Adds a deterministic generated configuration file.</summary>
    /// <param name="id">Stable topology-local file identity.</param>
    /// <param name="content">Exact non-secret UTF-8 text content.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalDeploymentBuilder File(InfrastructureLocalFileId id, string content)
    {
        topology.File(id, content);
        return this;
    }

    /// <summary>Adds a deterministic generated configuration file from portable value segments.</summary>
    /// <param name="id">Stable topology-local file identity.</param>
    /// <param name="content">Ordered non-secret content segments.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalDeploymentBuilder File(
        InfrastructureLocalFileId id,
        ImmutableArray<InfrastructureLocalValue> content)
    {
        topology.File(id, content);
        return this;
    }

    /// <summary>Adds one executable local lifecycle operation.</summary>
    /// <param name="id">Stable application-owned operation intent.</param>
    /// <param name="placement">Execution placement.</param>
    /// <param name="effect">Expected state effect.</param>
    /// <param name="executable">Exact executable or repository-relative artifact.</param>
    /// <param name="arguments">Exact argument vector.</param>
    /// <param name="requiredServices">Services that must be ready before execution.</param>
    /// <param name="service">Managed-service execution target, when applicable.</param>
    /// <param name="mutationAuthority">Lifecycle authority fenced by an environment mutation.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalDeploymentBuilder Operation(
        InfrastructureLocalOperationId id,
        InfrastructureLocalExecutionPlacement placement,
        InfrastructureLocalOperationEffect effect,
        string executable,
        ImmutableArray<string> arguments = default,
        ImmutableArray<InfrastructurePhysicalResourceId> requiredServices = default,
        InfrastructurePhysicalResourceId? service = null,
        InfrastructureLifecycleAuthorityId? mutationAuthority = null)
    {
        topology.Operation(
            id,
            placement,
            effect,
            executable,
            arguments,
            requiredServices,
            service,
            mutationAuthority);
        return this;
    }

    internal InfrastructureLocalDeploymentAuthoringResult Build()
    {
        if (remainingWorkloadPolicy is { } policy)
        {
            foreach (var workload in definition.Definition.Workloads)
            {
                if (participatingWorkloads.Contains(workload.Id)
                    || explicitNonParticipatingWorkloads.Contains(workload.Id))
                {
                    continue;
                }

                targetDeployment.NonParticipatingWorkload(
                    workload.Id,
                    policy.Rationale,
                    policy.SourceReferences,
                    policy.SourceFile,
                    policy.SourceLine,
                    policy.SourceMember);
            }
        }

        return new(targetDeployment.Build(), topology.Build());
    }

    static ImmutableArray<SourceReference> Include(
        ImmutableArray<SourceReference> sourceReferences,
        SourceReference required)
    {
        if (sourceReferences.IsDefaultOrEmpty)
            return [required];
        if (sourceReferences.Contains(required))
            return sourceReferences;
        return [.. sourceReferences, required];
    }

    sealed record RemainingWorkloadPolicy(
        string Rationale,
        ImmutableArray<SourceReference> SourceReferences,
        string SourceFile,
        int SourceLine,
        string SourceMember);
}
