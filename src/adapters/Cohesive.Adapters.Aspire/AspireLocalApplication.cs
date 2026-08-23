using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Cohesive.Infra;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cohesive.Adapters.Aspire;

/// <summary>Runtime-only policy used when applying a deterministic Aspire projection.</summary>
public sealed record AspireLocalApplicationOptions
{
    /// <summary>Creates runtime application policy.</summary>
    /// <param name="operationWorkingDirectory">Absolute directory used to resolve repository-relative host operations.</param>
    /// <param name="resolveSecret">Runtime resolver for external secret identities.</param>
    /// <param name="operationEnvironment">Additional environment variables supplied only to host operations.</param>
    /// <exception cref="ArgumentException"><paramref name="operationWorkingDirectory"/> is not absolute, or an operation environment name or value is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolveSecret"/> is <see langword="null"/>.</exception>
    public AspireLocalApplicationOptions(
        string operationWorkingDirectory,
        Func<string, string?> resolveSecret,
        IReadOnlyDictionary<string, string>? operationEnvironment = null)
    {
        Guard.RequireNotNullOrWhiteSpace(operationWorkingDirectory);
        if (!Path.IsPathFullyQualified(operationWorkingDirectory))
            throw new ArgumentException("The Aspire operation working directory must be absolute.", nameof(operationWorkingDirectory));
        if (operationEnvironment?.Any(static variable => string.IsNullOrWhiteSpace(variable.Key) || variable.Value is null) == true)
            throw new ArgumentException("Aspire operation environment names and values cannot be null, empty, or white-space.", nameof(operationEnvironment));
        OperationWorkingDirectory = Path.GetFullPath(operationWorkingDirectory);
        ResolveSecret = Guard.RequireNotNull(resolveSecret);
        OperationEnvironment = operationEnvironment is null
            ? ImmutableDictionary<string, string>.Empty
            : operationEnvironment.ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>Absolute directory used to resolve repository-relative host operations.</summary>
    public string OperationWorkingDirectory { get; }

    /// <summary>Runtime resolver for external secret identities.</summary>
    public Func<string, string?> ResolveSecret { get; }

    /// <summary>Additional environment variables supplied only to host operations.</summary>
    public ImmutableDictionary<string, string> OperationEnvironment { get; }
}

/// <summary>Infra identity and projection provenance retained as an Aspire resource annotation.</summary>
public sealed record AspireInfraIdentityAnnotation : IResourceAnnotation
{
    /// <summary>Creates an Infra identity annotation.</summary>
    /// <param name="logicalResource">Canonical logical resource, when the resource is a projected service.</param>
    /// <param name="physicalResource">Canonical physical resource, when the resource is a projected service.</param>
    /// <param name="localRealization">Exact local realization fingerprint.</param>
    /// <param name="projection">Exact Aspire projection fingerprint.</param>
    /// <exception cref="ArgumentException">A supplied canonical identity is default.</exception>
    /// <exception cref="ArgumentNullException">A fingerprint is <see langword="null"/>.</exception>
    public AspireInfraIdentityAnnotation(
        InfrastructureNodeId? logicalResource,
        InfrastructurePhysicalResourceId? physicalResource,
        InfrastructureLocalRealizationFingerprint localRealization,
        AspireLocalProjectionFingerprint projection)
    {
        if (logicalResource is { } logical && string.IsNullOrWhiteSpace(logical.Value))
            throw new ArgumentException("An Aspire Infra annotation logical resource cannot be default.", nameof(logicalResource));
        if (physicalResource is { } physical && string.IsNullOrWhiteSpace(physical.Value))
            throw new ArgumentException("An Aspire Infra annotation physical resource cannot be default.", nameof(physicalResource));
        if (logicalResource.HasValue != physicalResource.HasValue)
            throw new ArgumentException("Aspire Infra annotations require both logical and physical identity or neither.", nameof(physicalResource));
        LogicalResource = logicalResource;
        PhysicalResource = physicalResource;
        LocalRealization = Guard.RequireNotNull(localRealization);
        Projection = Guard.RequireNotNull(projection);
    }

    /// <summary>Canonical logical resource, when the resource is a projected service.</summary>
    public InfrastructureNodeId? LogicalResource { get; }

    /// <summary>Canonical physical resource, when the resource is a projected service.</summary>
    public InfrastructurePhysicalResourceId? PhysicalResource { get; }

    /// <summary>Exact local realization fingerprint.</summary>
    public InfrastructureLocalRealizationFingerprint LocalRealization { get; }

    /// <summary>Exact Aspire projection fingerprint.</summary>
    public AspireLocalProjectionFingerprint Projection { get; }
}

/// <summary>Applied Aspire application model fenced to one exact projection.</summary>
public sealed record AspireLocalApplication
{
    /// <summary>Creates an applied application result.</summary>
    /// <param name="projection">Exact applied projection.</param>
    /// <param name="services">Projected service resource builders by canonical physical identity.</param>
    /// <param name="controlResource">Resource exposing retained operation commands.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public AspireLocalApplication(
        AspireLocalProjectionDocument projection,
        ImmutableDictionary<InfrastructurePhysicalResourceId, IResourceBuilder<ContainerResource>> services,
        IResourceBuilder<AspireLocalOperationsResource> controlResource)
    {
        Projection = Guard.RequireNotNull(projection);
        Services = Guard.RequireNotNull(services);
        ControlResource = Guard.RequireNotNull(controlResource);
    }

    /// <summary>Exact applied projection.</summary>
    public AspireLocalProjectionDocument Projection { get; }

    /// <summary>Projected service resource builders by canonical physical identity.</summary>
    public ImmutableDictionary<InfrastructurePhysicalResourceId, IResourceBuilder<ContainerResource>> Services { get; }

    /// <summary>Resource exposing retained operation commands.</summary>
    public IResourceBuilder<AspireLocalOperationsResource> ControlResource { get; }
}

/// <summary>Non-executable Aspire resource that groups local Infra workflow commands.</summary>
public sealed class AspireLocalOperationsResource : Resource, IResourceWithWaitSupport
{
    /// <summary>Creates an operation command resource.</summary>
    /// <param name="name">Aspire resource name.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white-space.</exception>
    public AspireLocalOperationsResource(string name)
        : base(Guard.RequireNotNullOrWhiteSpace(name))
    {
    }
}

/// <summary>Applies deterministic local Infra projections to Aspire application builders.</summary>
public static class AspireLocalApplicationBuilderExtensions
{
    const int MaximumOperationStreamCharacters = 128 * 1024;

    /// <summary>Applies one exact projection to an Aspire application model.</summary>
    /// <param name="builder">Aspire distributed application builder.</param>
    /// <param name="projection">Exact deterministic projection.</param>
    /// <param name="options">Runtime-only operation and secret policy.</param>
    /// <returns>Applied resources fenced to the exact projection.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A required external secret is unavailable.</exception>
    public static AspireLocalApplication AddCohesiveLocalInfrastructure(
        this IDistributedApplicationBuilder builder,
        AspireLocalProjectionDocument projection,
        AspireLocalApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(options);

        builder.Configuration["ASPIRE_DCP_USE_DEVELOPER_CERTIFICATE"] =
            projection.DcpTlsCertificateMode == AspireDcpTlsCertificateMode.HostDeveloperCertificate
                ? "true"
                : "false";

        var secretParameters = AddSecretParameters(builder, projection, options);
        ImmutableDictionary<InfrastructurePhysicalResourceId, IResourceBuilder<ContainerResource>>.Builder services =
            ImmutableDictionary.CreateBuilder<InfrastructurePhysicalResourceId, IResourceBuilder<ContainerResource>>();
        foreach (var item in projection.Services)
        {
            var resource = builder.AddContainer(name: item.ResourceName, image: item.Service.Image)
                .WithAnnotation(new AspireInfraIdentityAnnotation(
                    logicalResource: item.Service.Resource,
                    physicalResource: item.Service.PhysicalResource,
                    localRealization: projection.LocalRealization,
                    projection: projection.Fingerprint));
            services.Add(item.Service.PhysicalResource, resource);
        }

        foreach (var item in projection.Services)
        {
            var resource = services[item.Service.PhysicalResource];
            if (!item.Service.Command.IsEmpty)
                resource.WithArgs([.. item.Service.Command]);
            AddEndpoints(resource, item, projection);
            AddEnvironment(resource, item, projection, services, secretParameters);
            AddVolumes(resource, item, projection);
            AddFiles(resource, item, projection);
            if (item.Service.StopGracePeriod is { } stopGracePeriod)
            {
                resource.WithContainerRuntimeArgs(
                    "--stop-timeout",
                    stopGracePeriod.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));
            }
        }

        foreach (var item in projection.Services)
        {
            var resource = services[item.Service.PhysicalResource];
            AddHealth(builder, resource, item, projection);
            foreach (var dependency in item.Service.ReadyDependencies)
                resource.WaitFor(services[dependency]);
        }

        var control = builder.AddResource(new AspireLocalOperationsResource(projection.ControlResourceName))
            .WithAnnotation(new AspireInfraIdentityAnnotation(
                logicalResource: null,
                physicalResource: null,
                localRealization: projection.LocalRealization,
                projection: projection.Fingerprint));
        AddOperations(control, projection, options);
        foreach (var required in projection.Operations.SelectMany(static item => item.RequiredResources).Distinct(StringComparer.Ordinal))
            control.WaitFor(services.Values.Single(service => string.Equals(service.Resource.Name, required, StringComparison.Ordinal)));

        return new(
            projection: projection,
            services: services.ToImmutable(),
            controlResource: control);
    }

    static IReadOnlyDictionary<string, IResourceBuilder<ParameterResource>> AddSecretParameters(
        IDistributedApplicationBuilder builder,
        AspireLocalProjectionDocument projection,
        AspireLocalApplicationOptions options)
    {
        Dictionary<string, IResourceBuilder<ParameterResource>> parameters = new(StringComparer.Ordinal);
        foreach (var secret in projection.Secrets)
        {
            var value = options.ResolveSecret(secret.SecretName);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Aspire local infrastructure requires external secret '{secret.SecretName}'.");
            builder.Configuration[$"Parameters:{secret.ParameterName}"] = value;
            parameters.Add(
                secret.SecretName,
                builder.AddParameter(name: secret.ParameterName, secret: true));
        }
        return parameters;
    }

    static void AddEndpoints(
        IResourceBuilder<ContainerResource> resource,
        AspireServiceProjection service,
        AspireLocalProjectionDocument projection)
    {
        foreach (var endpoint in projection.Endpoints.Where(item => item.PhysicalResource == service.Service.PhysicalResource))
        {
            resource.WithEndpoint(
                targetPort: endpoint.Endpoint.ContainerPort.Resolve(projection.Configuration),
                port: endpoint.HostPort,
                scheme: endpoint.Endpoint.Scheme,
                name: endpoint.Endpoint.Id.Value,
                env: null,
                isExternal: false,
                isProxied: false);
            if (endpoint.Endpoint.Role == InfrastructureLocalEndpointRole.UserInterface)
            {
                resource.WithUrlForEndpoint(
                    endpointName: endpoint.Endpoint.Id.Value,
                    callback: url => url.DisplayText = $"{service.ResourceName} {endpoint.Endpoint.Id.Value}");
            }
        }
    }

    static void AddEnvironment(
        IResourceBuilder<ContainerResource> resource,
        AspireServiceProjection service,
        AspireLocalProjectionDocument projection,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, IResourceBuilder<ContainerResource>> services,
        IReadOnlyDictionary<string, IResourceBuilder<ParameterResource>> secretParameters)
    {
        foreach (var variable in service.Service.Environment)
        {
            switch (variable.Value)
            {
                case InfrastructureLocalSecretValue secret:
                    resource.WithEnvironment(name: variable.Name, parameter: secretParameters[secret.Name]);
                    break;
                case InfrastructureLocalEndpointValue endpointValue
                    when endpointValue.Address == InfrastructureLocalEndpointAddress.ServiceNetwork:
                    var endpointReference = services[endpointValue.Service].GetEndpoint(name: endpointValue.Endpoint.Value);
                    if (endpointValue.Format == InfrastructureLocalEndpointValueFormat.Uri)
                        resource.WithEnvironment(name: variable.Name, endpointReference: endpointReference);
                    else
                        resource.WithEnvironment(name: variable.Name, value: ReferenceExpression.Create($"[\"{endpointReference}\"]"));
                    break;
                default:
                    resource.WithEnvironment(
                        name: variable.Name,
                        value: AspireLocalCompiler.ResolveValue(variable.Value, projection));
                    break;
            }
        }
    }

    static void AddVolumes(
        IResourceBuilder<ContainerResource> resource,
        AspireServiceProjection service,
        AspireLocalProjectionDocument projection)
    {
        foreach (var mount in service.Service.Mounts)
        {
            var volume = projection.Volumes.Single(candidate => candidate.Volume == mount.Volume);
            if (volume.VolumeName is null)
            {
                resource.WithVolume(target: mount.TargetPath);
            }
            else
            {
                resource.WithVolume(
                    name: volume.VolumeName,
                    target: mount.TargetPath,
                    isReadOnly: mount.ReadOnly);
            }
        }
    }

    static void AddFiles(
        IResourceBuilder<ContainerResource> resource,
        AspireServiceProjection service,
        AspireLocalProjectionDocument projection)
    {
        foreach (var mount in service.Service.FileMounts)
        {
            var file = projection.Files.Single(candidate => candidate.File == mount.File);
            var destination = Path.GetDirectoryName(mount.TargetPath);
            var name = Path.GetFileName(mount.TargetPath);
            if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException($"Generated file target '{mount.TargetPath}' is not representable by Aspire container files.");
            resource.WithContainerFiles(
                destinationPath: destination,
                entries:
                [
                    new ContainerFile
                    {
                        Name = name,
                        Contents = file.Contents
                    }
                ]);
        }
    }

    static void AddHealth(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> resource,
        AspireServiceProjection service,
        AspireLocalProjectionDocument projection)
    {
        foreach (var probe in service.Service.Health?.Probes ?? [])
        {
            switch (probe)
            {
                case InfrastructureLocalHttpHealthProbe http:
                    resource.WithHttpHealthCheck(
                        path: http.Path,
                        statusCode: http.ExpectedStatus,
                        endpointName: http.Endpoint.Value);
                    break;
                case InfrastructureLocalCommandHealthProbe command:
                    var @override = projection.CommandHealthOverrides.Single(candidate =>
                        candidate.PhysicalResource == service.Service.PhysicalResource
                        && string.Equals(candidate.Executable, command.Executable, StringComparison.Ordinal)
                        && candidate.Arguments.SequenceEqual(command.Arguments, StringComparer.Ordinal));
                    var endpoint = resource.GetEndpoint(name: @override.Endpoint.Value);
                    var key = $"cohesive-{service.ResourceName}-{command.Executable}";
                    builder.Services.AddHealthChecks().AddCheck(
                        name: key,
                        instance: new AspireTcpEndpointHealthCheck(
                            endpoint: endpoint,
                            timeout: service.Service.Health!.Timeout));
                    resource.WithHealthCheck(key: key);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported health probe '{probe.GetType().Name}' passed validated Aspire compilation.");
            }
        }
    }

    static void AddOperations(
        IResourceBuilder<AspireLocalOperationsResource> control,
        AspireLocalProjectionDocument projection,
        AspireLocalApplicationOptions options)
    {
        foreach (var item in projection.Operations.Where(static item => item.Realization == AspireOperationRealization.ProcessCommand))
        {
            var executable = Path.IsPathFullyQualified(item.Operation.Executable)
                ? item.Operation.Executable
                : Path.GetFullPath(item.Operation.Executable, options.OperationWorkingDirectory);
            control.WithCommand(
                name: item.Operation.Id.Value,
                displayName: DisplayName(item.Operation.Id.Value),
                executeCommand: context => ExecuteOperation(
                    executable: executable,
                    arguments: item.Operation.Arguments,
                    workingDirectory: options.OperationWorkingDirectory,
                    environment: options.OperationEnvironment,
                    cancellationToken: context.CancellationToken),
                commandOptions: new CommandOptions
                {
                    Visibility = ResourceCommandVisibility.UI | ResourceCommandVisibility.Api
                });
        }
    }

    static async Task<ExecuteCommandResult> ExecuteOperation(
        string executable,
        ImmutableArray<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new()
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var variable in environment)
            start.Environment[variable.Key] = variable.Value;
        using Process process = new() { StartInfo = start };
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        try
        {
            if (!process.Start())
                return CommandResults.Failure($"Could not start '{executable}'.");
            standardOutput = ReadBoundedAsync(process.StandardOutput);
            standardError = ReadBoundedAsync(process.StandardError);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = string.Concat(
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
            return process.ExitCode == 0
                ? CommandResults.Success(
                    message: $"{Path.GetFileName(executable)} completed.",
                    result: output,
                    resultFormat: CommandResultFormat.Text,
                    displayImmediately: true)
                : CommandResults.Failure(
                    errorMessage: $"{Path.GetFileName(executable)} exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.",
                    result: output,
                    resultFormat: CommandResultFormat.Text);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (standardOutput is not null && standardError is not null)
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            return CommandResults.Canceled();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResults.Failure(exception);
        }
    }

    static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        StringBuilder output = new(capacity: buffer.Length);
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            var available = MaximumOperationStreamCharacters - output.Length;
            if (available > 0)
                output.Append(buffer, startIndex: 0, charCount: Math.Min(available, count));
            if (count > available)
                truncated = true;
        }
        if (truncated)
            output.AppendLine().Append("[output truncated]");
        return output.ToString();
    }

    static string DisplayName(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-', ' '));

    sealed class AspireTcpEndpointHealthCheck : IHealthCheck
    {
        readonly EndpointReference endpoint;
        readonly TimeSpan timeout;

        internal AspireTcpEndpointHealthCheck(EndpointReference endpoint, TimeSpan timeout)
        {
            this.endpoint = endpoint;
            this.timeout = timeout;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var value = await endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                    return HealthCheckResult.Unhealthy($"Endpoint '{value}' is not an absolute URI.");
                using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);
                using TcpClient client = new();
                await client.ConnectAsync(uri.Host, uri.Port, timeoutSource.Token).ConfigureAwait(false);
                return HealthCheckResult.Healthy();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return HealthCheckResult.Unhealthy($"TCP connection did not succeed within {timeout}.");
            }
            catch (Exception exception) when (exception is SocketException or UriFormatException)
            {
                return HealthCheckResult.Unhealthy(exception.Message, exception);
            }
        }
    }
}
