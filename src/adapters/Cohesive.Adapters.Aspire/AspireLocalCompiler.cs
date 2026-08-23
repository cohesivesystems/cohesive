using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Aspire;

/// <summary>Deterministically projects exact local Infra realizations into Aspire resource graphs.</summary>
public static class AspireLocalCompiler
{
    const string Stage = "aspire-local-compilation";
    const string TargetReference = "aspire/13.5.2";

    /// <summary>Stable adapter diagnostic codes.</summary>
    public static class DiagnosticCodes
    {
        /// <summary>The local realization contains an unresolved error.</summary>
        public const string SourceInvalid = "infra.aspire.source.invalid";
        /// <summary>Two canonical identities normalize to the same Aspire resource name.</summary>
        public const string NameCollision = "infra.aspire.name.collision";
        /// <summary>An identity cannot be represented as an Aspire resource name.</summary>
        public const string NameInvalid = "infra.aspire.name.invalid";
        /// <summary>A local value cannot be represented by the Aspire adapter.</summary>
        public const string ValueUnsupported = "infra.aspire.value.unsupported";
        /// <summary>A command health probe has no exact explicit Aspire override.</summary>
        public const string CommandHealthOverrideRequired = "infra.aspire.health.commandOverrideRequired";
        /// <summary>A command health override does not match exact source evidence.</summary>
        public const string CommandHealthOverrideUnused = "infra.aspire.health.commandOverrideUnused";
        /// <summary>A command health override references an unsuitable endpoint.</summary>
        public const string CommandHealthEndpointInvalid = "infra.aspire.health.commandEndpointInvalid";
        /// <summary>A stop duration cannot be projected without precision loss.</summary>
        public const string DurationUnsupported = "infra.aspire.duration.unsupported";
        /// <summary>An operation placement cannot be executed by the current Aspire command surface.</summary>
        public const string OperationPlacementUnsupported = "infra.aspire.operation.placementUnsupported";
        /// <summary>An isolated anonymous volume is referenced by more than one service.</summary>
        public const string EphemeralSharedVolumeUnsupported = "infra.aspire.volume.ephemeralSharedUnsupported";
    }

    /// <summary>Compiles one exact local realization without starting Aspire or performing container I/O.</summary>
    /// <param name="source">Validated exact local realization.</param>
    /// <param name="options">Explicit Aspire lowering policy.</param>
    /// <returns>An exact fingerprinted Aspire projection, or fail-closed diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public static AspireLocalCompilation Compile(
        InfrastructureLocalRealizationDocument source,
        AspireLocalCompilerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= AspireLocalCompilerOptions.Default;

        List<DocumentValidationDiagnostic> diagnostics =
        [
            .. source.Configuration.Diagnostics,
            .. source.Diagnostics
        ];
        if (!source.IsValid)
        {
            Add(
                diagnostics,
                DiagnosticCodes.SourceInvalid,
                "Aspire projection requires a valid exact local realization.",
                "/source",
                source.Fingerprint.Value,
                "Resolve the local-realization diagnostics before selecting an Aspire target.");
            return new(projection: null, diagnostics: [.. diagnostics]);
        }

        var effective = source.Configuration.Configuration.ToDictionary(static item => (item.Subject, item.Setting));
        var projectName = Effective(
            source.Environment.ConfigurationSubject,
            source.Environment.ProjectNameSetting,
            effective).Value;
        var serviceNames = Names(
            source.Topology.Services.Select(static service => (service.PhysicalResource.Value, service.PhysicalResource)),
            static identity => identity.Value,
            "service",
            diagnostics);
        const string controlResourceName = "materialization-workflow";
        if (serviceNames.Values.Contains(controlResourceName, StringComparer.Ordinal))
        {
            Add(
                diagnostics,
                DiagnosticCodes.NameCollision,
                $"Aspire control resource name '{controlResourceName}' collides with a projected service.",
                "/topology/services",
                controlResourceName,
                "Rename the canonical service whose normalized name collides with the adapter control resource.");
        }

        var volumeNames = Names(
            source.Topology.Volumes.Select(static volume => (volume.Id.Value, volume.Id)),
            static identity => identity.Value,
            "volume",
            diagnostics);
        if (source.Environment.DataLifetime == InfrastructureLocalDataLifetime.Ephemeral)
        {
            foreach (var volume in source.Topology.Volumes)
            {
                var owners = source.Topology.Services.Count(service => service.Mounts.Any(mount => mount.Volume == volume.Id));
                if (owners > 1)
                {
                    Add(
                        diagnostics,
                        DiagnosticCodes.EphemeralSharedVolumeUnsupported,
                        $"Ephemeral volume '{volume.Id.Value}' is mounted by {owners.ToString(CultureInfo.InvariantCulture)} services and cannot be represented by one anonymous container volume.",
                        $"/topology/volumes/{volume.Id.Value}",
                        volume.Id.Value,
                        "Select persistent named data or introduce an explicit isolated-volume lifecycle override.");
                }
            }
        }

        foreach (var service in source.Topology.Services)
        {
            if (service.StopGracePeriod is { } duration && duration.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                Add(
                    diagnostics,
                    DiagnosticCodes.DurationUnsupported,
                    $"Aspire container-runtime stop timeout cannot preserve sub-second duration '{duration}'.",
                    $"/topology/services/{service.PhysicalResource.Value}/stopGracePeriod",
                    service.PhysicalResource.Value,
                    "Use a whole-second stop grace period or add an explicit target extension.");
            }
            foreach (var variable in service.Environment)
                ValidateValue(variable.Value, source, serviceNames, diagnostics, $"/topology/services/{service.PhysicalResource.Value}/environment/{variable.Name}");
        }

        foreach (var file in source.Topology.Files)
        {
            for (var index = 0; index < file.Content.Length; index++)
                ValidateValue(file.Content[index], source, serviceNames, diagnostics, $"/topology/files/{file.Id.Value}/content/{index.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (var operation in source.Topology.Operations)
        {
            if (operation.Placement == InfrastructureLocalExecutionPlacement.ManagedService)
            {
                Add(
                    diagnostics,
                    DiagnosticCodes.OperationPlacementUnsupported,
                    $"Managed-service operation '{operation.Id.Value}' has no current Aspire command realization.",
                    $"/topology/operations/{operation.Id.Value}",
                    operation.Id.Value,
                    "Use a host operation or add an explicit managed-container execution interpretation.");
            }
        }

        var acceptedOverrides = AcceptHealthOverrides(source, options, diagnostics);
        if (HasErrors(diagnostics))
            return new(projection: null, diagnostics: [.. diagnostics]);

        var endpoints = EndpointMappings(source, effective, serviceNames);
        var sourceReference = $"local-realization:{source.Fingerprint.Value}";
        List<AspireTargetDecision> decisions =
        [
            Decision(
                concern: "aspire/dashboard-observability",
                kind: CapabilityRealizationKind.Native,
                rationale: "Aspire/DCP owns the dashboard, health display, resource logs, and OpenTelemetry collection.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "aspire/orchestration/dcp-tls",
                kind: CapabilityRealizationKind.Native,
                rationale: options.DcpTlsCertificateMode == AspireDcpTlsCertificateMode.EphemeralSelfSigned
                    ? "DCP generates an ephemeral self-signed TLS identity so AppHost orchestration does not export host private-key material."
                    : "DCP reuses the host ASP.NET Core developer certificate for its local TLS identity.",
                boundaries: options.DcpTlsCertificateMode == AspireDcpTlsCertificateMode.EphemeralSelfSigned
                    ? ["The DCP TLS identity is regenerated for each AppHost run."]
                    : ["The host developer-certificate private key must be exportable to the AppHost process."],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "aspire/endpoints-and-discovery",
                kind: CapabilityRealizationKind.Native,
                rationale: "Canonical endpoints become proxyless Aspire endpoints and derived values use those same endpoint references.",
                boundaries: ["Host-loopback ports remain fixed by exact effective Infra configuration."],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "aspire/health/http",
                kind: CapabilityRealizationKind.Native,
                rationale: "Canonical HTTP probes become Aspire endpoint health checks with exact paths and expected status codes.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "aspire/health/timing",
                kind: CapabilityRealizationKind.Constrained,
                rationale: "Aspire owns health-check scheduling while the exact canonical timing policy remains inspectable in every service projection.",
                boundaries: ["Aspire 13.5.2 stable HTTP health APIs do not expose per-resource interval, timeout, retry, or start-period policy."],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "aspire/operations/host",
                kind: CapabilityRealizationKind.Native,
                rationale: "Read-only and application-mutation host operations become Aspire process commands visible to UI and API clients.",
                boundaries: ["Environment mutations are lifecycle-controlled and are retained but not executed as nested harness processes."],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "aspire/readiness",
                kind: CapabilityRealizationKind.Native,
                rationale: "Canonical ready dependencies become Aspire WaitFor relationships.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "aspire/volume-lifetime",
                kind: CapabilityRealizationKind.Composed,
                rationale: source.Environment.DataLifetime == InfrastructureLocalDataLifetime.Persistent
                    ? "Persistent local data uses deterministic named container volumes retained across ordinary AppHost stops."
                    : "Isolated ephemeral local data uses anonymous container volumes removed with the exact AppHost environment.",
                boundaries: source.Environment.DataLifetime == InfrastructureLocalDataLifetime.Persistent
                    ? []
                    : ["An anonymous volume may be mounted by only one service."],
                sourceReferences: [sourceReference, TargetReference])
        ];
        decisions.AddRange(acceptedOverrides.Select(item => Decision(
            concern: $"aspire/health/command/{item.PhysicalResource.Value}/{item.Executable}",
            kind: CapabilityRealizationKind.Override,
            rationale: item.Rationale,
            boundaries: ["TCP connectivity proves listener readiness, not command-level database acceptance semantics."],
            sourceReferences: [sourceReference, TargetReference, .. item.SourceReferences])));

        var projection = new AspireLocalProjectionDocument(
            schemaVersion: AspireLocalProjectionDocument.CurrentSchemaVersion,
            compiler: AspireLocalProjectionDocument.CurrentCompiler,
            sourceRealization: source.Realization,
            localRealization: source.Fingerprint,
            environment: source.Environment,
            projectName: projectName,
            configuration: source.Configuration,
            dcpTlsCertificateMode: options.DcpTlsCertificateMode,
            controlResourceName: controlResourceName,
            services:
            [
                .. source.Topology.Services.Select(service => new AspireServiceProjection(
                    resourceName: serviceNames[service.PhysicalResource],
                    service: service))
            ],
            endpoints: endpoints,
            volumes:
            [
                .. source.Topology.Volumes.Select(volume => new AspireVolumeProjection(
                    volume: volume.Id,
                    volumeName: source.Environment.DataLifetime == InfrastructureLocalDataLifetime.Persistent
                        ? $"{projectName}-{volumeNames[volume.Id]}"
                        : null))
            ],
            files:
            [
                .. source.Topology.Files.Select(file => new AspireFileProjection(
                    file: file.Id,
                    contents: ResolveFile(file, source, effective, serviceNames)))
            ],
            secrets:
            [
                .. source.Topology.Services
                    .SelectMany(static service => service.Environment)
                    .Select(static variable => variable.Value)
                    .OfType<InfrastructureLocalSecretValue>()
                    .Select(static secret => secret.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Select(secretName => new AspireSecretProjection(
                        secretName: secretName,
                        parameterName: $"secret-{NormalizeName(secretName)}"))
            ],
            operations:
            [
                .. source.Topology.Operations.Select(operation => new AspireOperationProjection(
                    operation: operation,
                    realization: operation.Effect == InfrastructureLocalOperationEffect.EnvironmentMutation
                        ? AspireOperationRealization.LifecycleControl
                        : AspireOperationRealization.ProcessCommand,
                    requiredResources: [.. operation.RequiredServices.Select(service => serviceNames[service])]))
            ],
            commandHealthOverrides: acceptedOverrides,
            decisions: [.. decisions]);
        return new(projection: projection, diagnostics: [.. diagnostics]);
    }

    static ImmutableArray<AspireCommandHealthOverride> AcceptHealthOverrides(
        InfrastructureLocalRealizationDocument source,
        AspireLocalCompilerOptions options,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        HashSet<AspireCommandHealthOverride> accepted = [];
        foreach (var service in source.Topology.Services)
        {
            foreach (var probe in service.Health?.Probes.OfType<InfrastructureLocalCommandHealthProbe>() ?? [])
            {
                var matches = options.CommandHealthOverrides.Where(candidate =>
                    candidate.PhysicalResource == service.PhysicalResource
                    && string.Equals(candidate.Executable, probe.Executable, StringComparison.Ordinal)
                    && candidate.Arguments.SequenceEqual(probe.Arguments, StringComparer.Ordinal)).ToArray();
                if (matches.Length != 1)
                {
                    Add(
                        diagnostics,
                        DiagnosticCodes.CommandHealthOverrideRequired,
                        $"Command health probe '{service.PhysicalResource.Value}/{probe.Executable}' requires one exact explicit Aspire override.",
                        $"/topology/services/{service.PhysicalResource.Value}/health",
                        service.PhysicalResource.Value,
                        "Supply an override fenced to the exact service, executable, argument vector, endpoint, rationale, and source references.");
                    continue;
                }

                var match = matches[0];
                var endpoint = service.Endpoints.FirstOrDefault(candidate => candidate.Id == match.Endpoint);
                if (endpoint is null)
                {
                    Add(
                        diagnostics,
                        DiagnosticCodes.CommandHealthEndpointInvalid,
                        $"Command health override endpoint '{match.Endpoint.Value}' is not declared by service '{service.PhysicalResource.Value}'.",
                        $"/options/commandHealthOverrides/{service.PhysicalResource.Value}",
                        match.Endpoint.Value,
                        "Select an endpoint declared by the exact canonical service.");
                    continue;
                }
                accepted.Add(match);
            }
        }

        foreach (var supplied in options.CommandHealthOverrides)
        {
            if (accepted.Contains(supplied))
                continue;
            Add(
                diagnostics,
                DiagnosticCodes.CommandHealthOverrideUnused,
                $"Command health override '{supplied.PhysicalResource.Value}/{supplied.Executable}' did not match exact source evidence.",
                $"/options/commandHealthOverrides/{supplied.PhysicalResource.Value}",
                supplied.PhysicalResource.Value,
                "Remove the stale override or fence it to the exact canonical command probe.");
        }
        return [.. accepted];
    }

    static ImmutableArray<AspireEndpointProjection> EndpointMappings(
        InfrastructureLocalRealizationDocument source,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, string> serviceNames)
    {
        List<AspireEndpointProjection> endpoints = [];
        foreach (var service in source.Topology.Services)
        {
            var resourceName = serviceNames[service.PhysicalResource];
            foreach (var endpoint in service.Endpoints)
            {
                var containerPort = endpoint.ContainerPort.Resolve(source.Configuration);
                int? hostPort = endpoint.HostPort is null
                    ? null
                    : int.Parse(Effective(endpoint.HostPort.Subject, endpoint.HostPort.Setting, effective).Value, NumberStyles.None, CultureInfo.InvariantCulture);
                endpoints.Add(new(
                    physicalResource: service.PhysicalResource,
                    resourceName: resourceName,
                    endpoint: endpoint,
                    hostPort: hostPort,
                    serviceAddress: $"{endpoint.Scheme}://{resourceName}:{containerPort.ToString(CultureInfo.InvariantCulture)}",
                    hostAddress: hostPort is null ? null : $"{endpoint.Scheme}://localhost:{hostPort.Value.ToString(CultureInfo.InvariantCulture)}"));
            }
        }
        return [.. endpoints];
    }

    static string ResolveFile(
        InfrastructureLocalFile file,
        InfrastructureLocalRealizationDocument source,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, string> serviceNames)
    {
        StringBuilder contents = new();
        foreach (var value in file.Content)
            contents.Append(ResolveValue(value, source, effective, serviceNames));
        return contents.ToString();
    }

    internal static string ResolveValue(
        InfrastructureLocalValue value,
        AspireLocalProjectionDocument projection)
    {
        var effective = projection.Configuration.Configuration.ToDictionary(static item => (item.Subject, item.Setting));
        var services = projection.Services.ToDictionary(static item => item.Service.PhysicalResource, static item => item.ResourceName);
        return ResolveValue(value, projection, effective, services);
    }

    static string ResolveValue(
        InfrastructureLocalValue value,
        object source,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, string> serviceNames)
    {
        return value switch
        {
            InfrastructureLocalLiteralValue literal => literal.Value,
            InfrastructureLocalConfigurationValue configuration => Effective(configuration.Subject, configuration.Setting, effective).Value,
            InfrastructureLocalSecretValue secret => secret.Name,
            InfrastructureLocalEndpointValue endpointValue => ResolveEndpointValue(endpointValue, source, effective, serviceNames),
            _ => throw new InvalidOperationException($"Unsupported local value '{value.GetType().Name}' passed validated Aspire compilation.")
        };
    }

    static string ResolveEndpointValue(
        InfrastructureLocalEndpointValue value,
        object source,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, string> serviceNames)
    {
        InfrastructureLocalEndpoint endpoint;
        InfrastructureConventionResolution configuration;
        string? hostAddress;
        if (source is InfrastructureLocalRealizationDocument realization)
        {
            endpoint = realization.Topology.Services.Single(service => service.PhysicalResource == value.Service)
                .Endpoints.Single(candidate => candidate.Id == value.Endpoint);
            configuration = realization.Configuration;
            int? hostPort = endpoint.HostPort is null
                ? null
                : int.Parse(
                    Effective(endpoint.HostPort.Subject, endpoint.HostPort.Setting, effective).Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture);
            hostAddress = hostPort is null
                ? null
                : $"{endpoint.Scheme}://localhost:{hostPort.Value.ToString(CultureInfo.InvariantCulture)}";
        }
        else if (source is AspireLocalProjectionDocument projection)
        {
            var mapping = projection.Endpoints.Single(candidate =>
                candidate.PhysicalResource == value.Service && candidate.Endpoint.Id == value.Endpoint);
            endpoint = mapping.Endpoint;
            configuration = projection.Configuration;
            hostAddress = mapping.HostAddress;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(source), source.GetType(), "Unsupported Aspire value-resolution source.");
        }

        var uri = value.Address switch
        {
            InfrastructureLocalEndpointAddress.ServiceNetwork =>
                $"{endpoint.Scheme}://{serviceNames[value.Service]}:{endpoint.ContainerPort.Resolve(configuration).ToString(CultureInfo.InvariantCulture)}",
            InfrastructureLocalEndpointAddress.HostLoopback when hostAddress is not null => hostAddress,
            InfrastructureLocalEndpointAddress.HostLoopback => throw new InvalidOperationException("Host endpoint value requires a resolved Aspire projection."),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Address, "Unsupported endpoint address surface.")
        };
        return value.Format switch
        {
            InfrastructureLocalEndpointValueFormat.Uri => uri,
            InfrastructureLocalEndpointValueFormat.JsonUriArray => System.Text.Json.JsonSerializer.Serialize(new[] { uri }),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Format, "Unsupported endpoint value format.")
        };
    }

    static void ValidateValue(
        InfrastructureLocalValue value,
        InfrastructureLocalRealizationDocument source,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, string> serviceNames,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string location)
    {
        if (value is InfrastructureLocalEndpointValue endpoint
            && (!serviceNames.ContainsKey(endpoint.Service)
                || !source.Topology.Services.Single(service => service.PhysicalResource == endpoint.Service)
                    .Endpoints.Any(candidate => candidate.Id == endpoint.Endpoint)))
        {
            Add(
                diagnostics,
                DiagnosticCodes.ValueUnsupported,
                $"Endpoint value '{endpoint.Service.Value}/{endpoint.Endpoint.Value}' cannot be projected into Aspire.",
                location,
                endpoint.Service.Value,
                "Reference an endpoint in the exact local service graph.");
        }
        else if (value is not InfrastructureLocalLiteralValue
                 and not InfrastructureLocalConfigurationValue
                 and not InfrastructureLocalSecretValue
                 and not InfrastructureLocalEndpointValue)
        {
            Add(
                diagnostics,
                DiagnosticCodes.ValueUnsupported,
                $"Local value '{value.GetType().Name}' is unsupported by Aspire.",
                location,
                value.GetType().FullName ?? value.GetType().Name,
                "Select a supported local value or add an explicit Aspire value projection.");
        }
    }

    static InfrastructureEffectiveConfiguration Effective(
        InfrastructureConfigurationSubject subject,
        InfrastructureSettingId setting,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective) =>
        effective[(subject, setting)];

    static Dictionary<TIdentity, string> Names<TIdentity>(
        IEnumerable<(string Source, TIdentity Identity)> sources,
        Func<TIdentity, string> identity,
        string kind,
        ICollection<DocumentValidationDiagnostic> diagnostics)
        where TIdentity : notnull
    {
        Dictionary<TIdentity, string> names = [];
        Dictionary<string, string> owners = new(StringComparer.Ordinal);
        foreach (var source in sources.OrderBy(static item => item.Source, StringComparer.Ordinal))
        {
            var name = NormalizeName(source.Source);
            if (name.Length == 0 || !char.IsAsciiLetter(name[0]))
            {
                Add(
                    diagnostics,
                    DiagnosticCodes.NameInvalid,
                    $"Aspire {kind} identity '{source.Source}' does not produce a resource name beginning with an ASCII letter.",
                    $"/topology/{kind}s",
                    identity(source.Identity),
                    "Rename the canonical identity so its final segment begins with an ASCII letter or add an explicit naming policy.");
                continue;
            }
            if (owners.TryGetValue(name, out var owner))
            {
                Add(
                    diagnostics,
                    DiagnosticCodes.NameCollision,
                    $"Aspire {kind} name '{name}' is shared by '{owner}' and '{source.Source}'.",
                    $"/topology/{kind}s",
                    identity(source.Identity),
                    "Rename one canonical identity or add an explicit collision-free naming policy.");
                continue;
            }
            owners.Add(name, source.Source);
            names.Add(source.Identity, name);
        }
        return names;
    }

    static string NormalizeName(string source)
    {
        var segment = source[(source.LastIndexOf('/') + 1)..];
        StringBuilder normalized = new(segment.Length);
        var separator = false;
        foreach (var character in segment)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else if (!separator && normalized.Length > 0)
            {
                normalized.Append('-');
                separator = true;
            }
        }
        return normalized.ToString().TrimEnd('-');
    }

    static AspireTargetDecision Decision(
        string concern,
        CapabilityRealizationKind kind,
        string rationale,
        ImmutableArray<string> boundaries,
        ImmutableArray<string> sourceReferences) => new(
        concern: concern,
        kind: kind,
        rationale: rationale,
        boundaries: boundaries,
        sourceReferences: sourceReferences);

    static void Add(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string code,
        string message,
        string location,
        string subject,
        string resolution) => diagnostics.Add(new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location,
            SchemaLocation: subject,
            Evidence: new(
                stage: Stage,
                subject: subject,
                sourceReferences: [AspireLocalProjectionDocument.CurrentCompiler, TargetReference],
                resolutionOptions: [resolution])));

    static bool HasErrors(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
