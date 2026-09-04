using System.Collections.Immutable;
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
}

/// <summary>Fluent producer for one canonical target-deployment manifest.</summary>
public sealed class InfrastructureTargetDeploymentManifestBuilder
{
    readonly InfrastructureTargetDeploymentManifestId id;
    readonly InfrastructureDefinitionDocument definition;
    readonly InfrastructureTargetFacilityManifest targetFacilities;
    readonly List<InfrastructureTargetWorkloadDeployment> workloads = [];
    readonly List<InfrastructureTargetResourceDeployment> resources = [];

    internal InfrastructureTargetDeploymentManifestBuilder(
        InfrastructureTargetDeploymentManifestId id,
        InfrastructureDefinitionDocument definition,
        InfrastructureTargetFacilityManifest targetFacilities)
    {
        this.id = id;
        this.definition = definition;
        this.targetFacilities = targetFacilities;
    }

    /// <summary>Declares one exact workload deployment.</summary>
    /// <param name="workload">Canonical workload identity.</param>
    /// <param name="facility">Target facility materializing the workload.</param>
    /// <param name="physicalResource">Exact target-native deployment identity.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder Workload(
        InfrastructureNodeId workload,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        ImmutableArray<SourceReference> sourceReferences)
    {
        workloads.Add(new(workload, facility, physicalResource, sourceReferences));
        return this;
    }

    /// <summary>Declares one exact resource deployment and lifecycle authority.</summary>
    /// <param name="resource">Canonical resource identity.</param>
    /// <param name="facility">Target facility materializing or referencing the resource.</param>
    /// <param name="physicalResource">Exact target-native resource identity.</param>
    /// <param name="authority">Backend state scope or external authority that owns the resource lifecycle.</param>
    /// <param name="sourceReferences">Attributable adapter, artifact, configuration, or import sources.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">An identity or source-reference collection is invalid or missing.</exception>
    public InfrastructureTargetDeploymentManifestBuilder Resource(
        InfrastructureNodeId resource,
        InfrastructureTargetFacilityId facility,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sourceReferences)
    {
        resources.Add(new(resource, facility, physicalResource, authority, sourceReferences));
        return this;
    }

    internal InfrastructureTargetDeploymentManifest Build() => new(
        InfrastructureTargetDeploymentManifest.CurrentSchemaVersion,
        id,
        definition.ToReference(),
        targetFacilities,
        [.. workloads],
        [.. resources]);
}
