using System.Collections.Immutable;
using Cohesive.Infra.Realization;

namespace Cohesive.Infra.Local;

/// <summary>Fluent authoring projection for canonical local infrastructure topology.</summary>
public static class InfrastructureLocal
{
    /// <summary>Authors and materializes a canonical local topology.</summary>
    /// <param name="configure">Builder actions that do not survive into canonical IR.</param>
    /// <returns>A normalized immutable local topology.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    public static InfrastructureLocalTopology Define(Action<InfrastructureLocalBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        InfrastructureLocalBuilder builder = new();
        configure(builder);
        return builder.Build();
    }
}

/// <summary>Mutable producer for one immutable <see cref="InfrastructureLocalTopology"/>.</summary>
public sealed class InfrastructureLocalBuilder
{
    readonly List<InfrastructureLocalService> services = [];
    readonly List<InfrastructureLocalVolume> volumes = [];
    readonly List<InfrastructureLocalFile> files = [];
    readonly List<InfrastructureLocalOperation> operations = [];

    /// <summary>Adds a named local volume.</summary>
    /// <param name="id">Stable topology-local volume identity.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalBuilder Volume(InfrastructureLocalVolumeId id)
    {
        volumes.Add(new(id));
        return this;
    }

    /// <summary>Adds a deterministic generated configuration file.</summary>
    /// <param name="id">Stable topology-local file identity.</param>
    /// <param name="content">Exact non-secret UTF-8 text content.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalBuilder File(InfrastructureLocalFileId id, string content)
    {
        files.Add(new(id, [new InfrastructureLocalLiteralValue(content)]));
        return this;
    }

    /// <summary>Adds a deterministic generated configuration file from literal and reference segments.</summary>
    /// <param name="id">Stable topology-local file identity.</param>
    /// <param name="content">Ordered non-secret content segments.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalBuilder File(
        InfrastructureLocalFileId id,
        ImmutableArray<InfrastructureLocalValue> content)
    {
        files.Add(new(id, content));
        return this;
    }

    /// <summary>Adds a container-backed service.</summary>
    /// <param name="resource">Canonical logical resource.</param>
    /// <param name="physicalResource">Exact physical resource identity.</param>
    /// <param name="image">Pinned container image.</param>
    /// <param name="configure">Optional service configuration.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalBuilder Service(
        InfrastructureNodeId resource,
        InfrastructurePhysicalResourceId physicalResource,
        string image,
        Action<InfrastructureLocalServiceBuilder>? configure = null)
    {
        InfrastructureLocalServiceBuilder builder = new(resource, physicalResource, image);
        configure?.Invoke(builder);
        services.Add(builder.Build());
        return this;
    }

    /// <summary>Adds an executable harness operation.</summary>
    /// <param name="id">Stable application-owned operation intent.</param>
    /// <param name="placement">Execution placement.</param>
    /// <param name="effect">Expected state effect.</param>
    /// <param name="executable">Exact executable or repository-relative artifact.</param>
    /// <param name="arguments">Exact argument vector.</param>
    /// <param name="requiredServices">Services that must be ready before execution.</param>
    /// <param name="service">Managed-service target, when applicable.</param>
    /// <param name="mutationAuthority">Lifecycle authority fenced by an environment mutation.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalBuilder Operation(
        InfrastructureLocalOperationId id,
        InfrastructureLocalExecutionPlacement placement,
        InfrastructureLocalOperationEffect effect,
        string executable,
        ImmutableArray<string> arguments = default,
        ImmutableArray<InfrastructurePhysicalResourceId> requiredServices = default,
        InfrastructurePhysicalResourceId? service = null,
        InfrastructureLifecycleAuthorityId? mutationAuthority = null)
    {
        operations.Add(new(
            id: id,
            placement: placement,
            effect: effect,
            executable: executable,
            arguments: arguments,
            requiredServices: requiredServices,
            service: service,
            mutationAuthority: mutationAuthority));
        return this;
    }

    /// <summary>Materializes canonical local topology IR.</summary>
    /// <returns>Normalized immutable topology.</returns>
    public InfrastructureLocalTopology Build() => new(
        services: [.. services],
        volumes: [.. volumes],
        files: [.. files],
        operations: [.. operations]);
}

/// <summary>Mutable producer for one immutable <see cref="InfrastructureLocalService"/>.</summary>
public sealed class InfrastructureLocalServiceBuilder
{
    readonly InfrastructureNodeId resource;
    readonly InfrastructurePhysicalResourceId physicalResource;
    readonly string image;
    readonly List<string> command = [];
    readonly List<InfrastructureLocalEnvironmentVariable> environment = [];
    readonly List<InfrastructureLocalEndpoint> endpoints = [];
    readonly List<InfrastructureLocalVolumeMount> mounts = [];
    readonly List<InfrastructureLocalFileMount> fileMounts = [];
    readonly List<InfrastructureLocalHealthProbe> health = [];
    readonly List<InfrastructurePhysicalResourceId> dependencies = [];
    TimeSpan? stopGracePeriod;
    (TimeSpan Interval, TimeSpan Timeout, int Retries, TimeSpan? StartPeriod)? healthTiming;

    internal InfrastructureLocalServiceBuilder(
        InfrastructureNodeId resource,
        InfrastructurePhysicalResourceId physicalResource,
        string image)
    {
        this.resource = resource;
        this.physicalResource = physicalResource;
        this.image = image;
    }

    /// <summary>Appends an exact container command argument.</summary>
    /// <param name="argument">Command argument.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder CommandArgument(string argument)
    {
        command.Add(Guard.RequireNotNull(argument));
        return this;
    }

    /// <summary>Adds an environment-variable value binding.</summary>
    /// <param name="name">Exact environment-variable name.</param>
    /// <param name="value">Portable value source.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder Environment(string name, InfrastructureLocalValue value)
    {
        environment.Add(new(name, value));
        return this;
    }

    /// <summary>Adds a service endpoint.</summary>
    /// <param name="id">Service-local endpoint identity.</param>
    /// <param name="scheme">URI scheme.</param>
    /// <param name="containerPort">Literal or configured container port.</param>
    /// <param name="exposure">Endpoint exposure.</param>
    /// <param name="role">Endpoint role.</param>
    /// <param name="hostPort">Effective host-port reference for loopback exposure.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder Endpoint(
        InfrastructureLocalEndpointId id,
        string scheme,
        InfrastructureLocalPort containerPort,
        InfrastructureLocalEndpointExposure exposure,
        InfrastructureLocalEndpointRole role,
        InfrastructureLocalConfigurationValue? hostPort = null)
    {
        endpoints.Add(new(
            id: id,
            scheme: scheme,
            containerPort: containerPort,
            exposure: exposure,
            role: role,
            hostPort: hostPort));
        return this;
    }

    /// <summary>Adds a named-volume mount.</summary>
    /// <param name="volume">Volume identity.</param>
    /// <param name="targetPath">Absolute container path.</param>
    /// <param name="readOnly">Whether the service cannot write the mount.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder Mount(
        InfrastructureLocalVolumeId volume,
        string targetPath,
        bool readOnly = false)
    {
        mounts.Add(new(volume: volume, targetPath: targetPath, readOnly: readOnly));
        return this;
    }

    /// <summary>Adds a read-only generated configuration-file mount.</summary>
    /// <param name="file">Generated file identity.</param>
    /// <param name="targetPath">Absolute container path.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder FileMount(InfrastructureLocalFileId file, string targetPath)
    {
        fileMounts.Add(new(file: file, targetPath: targetPath));
        return this;
    }

    /// <summary>Adds an HTTP health probe.</summary>
    /// <param name="endpoint">Endpoint to probe.</param>
    /// <param name="path">Absolute HTTP path.</param>
    /// <param name="expectedStatus">Expected status code.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder HttpHealth(
        InfrastructureLocalEndpointId endpoint,
        string path,
        int expectedStatus = 200)
    {
        health.Add(new InfrastructureLocalHttpHealthProbe(
            endpoint: endpoint,
            path: path,
            expectedStatus: expectedStatus));
        return this;
    }

    /// <summary>Adds an in-service command health probe.</summary>
    /// <param name="executable">Probe executable.</param>
    /// <param name="arguments">Exact argument vector.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder CommandHealth(
        string executable,
        ImmutableArray<string> arguments = default)
    {
        health.Add(new InfrastructureLocalCommandHealthProbe(executable: executable, arguments: arguments));
        return this;
    }

    /// <summary>Completes the health policy with exact retry timing.</summary>
    /// <param name="interval">Delay between probe attempts.</param>
    /// <param name="timeout">Maximum duration of one attempt.</param>
    /// <param name="retries">Consecutive failures allowed before unhealthy state.</param>
    /// <param name="startPeriod">Optional initialization grace period.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder HealthTiming(
        TimeSpan interval,
        TimeSpan timeout,
        int retries,
        TimeSpan? startPeriod = null)
    {
        healthTiming = (interval, timeout, retries, startPeriod);
        return this;
    }

    /// <summary>Adds a ready-service dependency.</summary>
    /// <param name="service">Physical service identity.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder DependsOn(InfrastructurePhysicalResourceId service)
    {
        dependencies.Add(service);
        return this;
    }

    /// <summary>Sets graceful termination duration.</summary>
    /// <param name="duration">Grace duration.</param>
    /// <returns>This builder.</returns>
    public InfrastructureLocalServiceBuilder StopGrace(TimeSpan duration)
    {
        stopGracePeriod = duration;
        return this;
    }

    internal InfrastructureLocalService Build()
    {
        if (health.Count == 0 && healthTiming.HasValue)
            throw new InvalidOperationException("Health timing requires at least one health probe.");
        if (health.Count > 0 && !healthTiming.HasValue)
            throw new InvalidOperationException("Health probes require explicit interval, timeout, and retry timing.");

        InfrastructureLocalHealthPolicy? policy = healthTiming is { } timing
            ? new(
                probes: [.. health],
                interval: timing.Interval,
                timeout: timing.Timeout,
                retries: timing.Retries,
                startPeriod: timing.StartPeriod)
            : null;
        return new(
            resource: resource,
            physicalResource: physicalResource,
            image: image,
            command: [.. command],
            environment: [.. environment],
            endpoints: [.. endpoints],
            mounts: [.. mounts],
            fileMounts: [.. fileMounts],
            health: policy,
            readyDependencies: [.. dependencies],
            stopGracePeriod: stopGracePeriod);
    }
}
