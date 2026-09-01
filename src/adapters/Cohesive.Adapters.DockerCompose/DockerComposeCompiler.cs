using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.DockerCompose;

/// <summary>Deterministically projects exact local Infra realizations into Docker Compose artifacts.</summary>
public static class DockerComposeCompiler
{
    const string Stage = "docker-compose-compilation";
    const string TargetReference = "https://docs.docker.com/compose/compose-file/";

    /// <summary>Stable adapter diagnostic codes.</summary>
    public static class DiagnosticCodes
    {
        /// <summary>The local realization contains an unresolved error.</summary>
        public const string SourceInvalid = "infra.compose.source.invalid";
        /// <summary>Two canonical identities normalize to the same Compose name.</summary>
        public const string NameCollision = "infra.compose.name.collision";
        /// <summary>A project or topology identity cannot be represented as a Compose name.</summary>
        public const string NameInvalid = "infra.compose.name.invalid";
        /// <summary>A value source cannot be represented by the Compose adapter.</summary>
        public const string ValueUnsupported = "infra.compose.value.unsupported";
        /// <summary>A health probe cannot be represented by the Compose adapter.</summary>
        public const string HealthProbeUnsupported = "infra.compose.health.unsupported";
        /// <summary>A duration cannot be represented without precision loss.</summary>
        public const string DurationUnsupported = "infra.compose.duration.unsupported";
        /// <summary>A local service construction source cannot be represented by Docker Compose.</summary>
        public const string ServiceSourceUnsupported = "infra.compose.service.sourceUnsupported";
    }

    /// <summary>Compiles one exact local realization without performing Docker I/O.</summary>
    /// <param name="source">Validated exact local realization.</param>
    /// <returns>An exact YAML artifact and provenance manifest, or fail-closed diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public static DockerComposeCompilation Compile(InfrastructureLocalRealizationDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
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
                "Docker Compose projection requires a valid exact local realization.",
                "/source",
                source.Fingerprint.Value);
            return new(artifact: null, diagnostics: [.. diagnostics]);
        }

        var effective = source.Configuration.Configuration.ToDictionary(static item => (item.Subject, item.Setting));
        var projectName = Effective(
            source.Environment.ConfigurationSubject,
            source.Environment.ProjectNameSetting,
            effective).Value;
        if (!DockerComposeNames.IsProjectName(projectName))
        {
            Add(
                diagnostics,
                DiagnosticCodes.NameInvalid,
                $"Compose project name '{projectName}' must start with a lowercase letter or digit and contain only lowercase letters, digits, hyphens, and underscores.",
                "/environment/projectNameSetting",
                projectName);
        }
        var serviceNames = Names(
            source.Topology.Services.Select(static service => (service.PhysicalResource.Value, service.PhysicalResource)),
            static item => item.Value,
            "service",
            diagnostics);
        foreach (var service in source.Topology.Services.Where(static service => service.Source is not InfrastructureLocalContainerSource))
        {
            Add(
                diagnostics,
                DiagnosticCodes.ServiceSourceUnsupported,
                $"Docker Compose cannot preserve repository-project construction for service '{service.PhysicalResource.Value}'.",
                $"/topology/services/{service.PhysicalResource.Value}/source",
                service.PhysicalResource.Value);
        }
        var volumeNames = Names(
            source.Topology.Volumes.Select(static volume => (volume.Id.Value, volume.Id)),
            static item => item.Value,
            "volume",
            diagnostics);
        var configNames = Names(
            source.Topology.Files.Select(static file => (file.Id.Value, file.Id)),
            static item => item.Value,
            "config",
            diagnostics);

        if (HasErrors(diagnostics))
            return new(artifact: null, diagnostics: [.. diagnostics]);

        var yaml = EmitYaml(source, effective, serviceNames, volumeNames, configNames, diagnostics);
        if (HasErrors(diagnostics))
            return new(artifact: null, diagnostics: [.. diagnostics]);

        var yamlFingerprint = DockerComposeArtifactFingerprint.Compute(yaml);
        var manifest = new DockerComposeArtifactManifest(
            schemaVersion: DockerComposeArtifactManifest.CurrentSchemaVersion,
            compiler: DockerComposeArtifactManifest.CurrentCompiler,
            sourceRealization: source.Realization,
            localRealization: source.Fingerprint,
            environment: source.Environment.Id,
            lifecycleAuthority: source.Environment.Authority,
            dataLifetime: source.Environment.DataLifetime,
            isolation: source.Environment.Isolation,
            projectName: projectName,
            configuration: source.Configuration,
            yamlFingerprint: yamlFingerprint,
            services:
            [
                .. source.Topology.Services.Select(service => new DockerComposeServiceMapping(
                    resource: service.Node,
                    physicalResource: service.PhysicalResource,
                    serviceName: serviceNames[service.PhysicalResource]))
            ],
            endpoints: [.. EndpointMappings(source, effective, serviceNames)],
            volumes:
            [
                .. source.Topology.Volumes.Select(volume => new DockerComposeVolumeMapping(
                    volume: volume.Id,
                    volumeName: volumeNames[volume.Id]))
            ],
            configs:
            [
                .. source.Topology.Files.Select(file => new DockerComposeConfigMapping(
                    file: file.Id,
                    configName: configNames[file.Id]))
            ],
            operations:
            [
                .. source.Topology.Operations.Select(operation => new DockerComposeOperationMapping(
                    operation: operation.Id,
                    placement: operation.Placement,
                    effect: operation.Effect,
                    executable: operation.Executable,
                    arguments: operation.Arguments,
                    mutationAuthority: operation.MutationAuthority))
            ],
            decisions: Decisions(source),
            maximumLifetime: source.Environment.MaximumLifetime);
        return new(
            artifact: new(yaml: yaml, manifest),
            diagnostics: [.. diagnostics]);
    }

    static ImmutableArray<InfrastructureLocalTargetDecision> Decisions(InfrastructureLocalRealizationDocument source)
    {
        var sourceReference = $"local-realization:{source.Fingerprint.Value}";
        return
        [
            Decision(
                concern: "local/observability",
                kind: CapabilityRealizationKind.Constrained,
                rationale: "Docker Compose exposes container state and logs while the canonical UI services provide data inspection; it does not supply a unified dashboard or OpenTelemetry collector.",
                boundaries: ["Runtime telemetry and cross-resource dashboard behavior require an additional explicit facility."],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "local/orchestration-control-plane",
                kind: CapabilityRealizationKind.Native,
                rationale: "Docker Compose v2 owns project-scoped container lifecycle through the local Docker engine.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "local/endpoints-and-discovery",
                kind: CapabilityRealizationKind.Native,
                rationale: "Canonical endpoints become project-network service addresses and fixed host-loopback publications.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "local/health/http",
                kind: CapabilityRealizationKind.Native,
                rationale: "Canonical HTTP probes become exact Compose healthcheck commands.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "local/health/timing",
                kind: CapabilityRealizationKind.Native,
                rationale: "Compose healthchecks preserve the canonical interval, timeout, retries, and start period.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "local/health/command/local/postgres/pg_isready",
                kind: CapabilityRealizationKind.Native,
                rationale: "The canonical PostgreSQL command probe executes directly in the Compose container.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "local/operations/host",
                kind: CapabilityRealizationKind.Composed,
                rationale: "The adjacent manifest retains canonical host operations and the harness SDK/CLI wrapper executes them against the exact Compose project.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "local/readiness",
                kind: CapabilityRealizationKind.Native,
                rationale: "Canonical ready dependencies become service_healthy Compose dependencies.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference]),
            Decision(
                concern: "local/volume-lifetime",
                kind: CapabilityRealizationKind.Composed,
                rationale: source.Environment.DataLifetime == InfrastructureLocalDataLifetime.Persistent
                    ? "Persistent data uses project-scoped named volumes retained by ordinary Compose down operations."
                    : "Isolated data uses project-scoped volumes removed only by the exact environment teardown.",
                boundaries: [],
                sourceReferences: [sourceReference, TargetReference])
        ];
    }

    static InfrastructureLocalTargetDecision Decision(
        string concern,
        CapabilityRealizationKind kind,
        string rationale,
        ImmutableArray<string> boundaries,
        ImmutableArray<SourceReference> sourceReferences) => new(
        target: DockerComposeArtifactManifest.CurrentTarget,
        concern: concern,
        kind: kind,
        rationale: rationale,
        boundaries: boundaries,
        sourceReferences: sourceReferences);

    static string EmitYaml(
        InfrastructureLocalRealizationDocument source,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, string> serviceNames,
        IReadOnlyDictionary<InfrastructureLocalVolumeId, string> volumeNames,
        IReadOnlyDictionary<InfrastructureLocalFileId, string> configNames,
        ICollection<DocumentValidationDiagnostic> diagnostics
        )
    {
        StringBuilder yaml = new();
        var projectName = Effective(
            source.Environment.ConfigurationSubject,
            source.Environment.ProjectNameSetting,
            effective).Value;
        Line(yaml, 0, $"name: {Quoted(projectName)}");
        Line(yaml, 0, "services:");
        foreach (var service in source.Topology.Services.OrderBy(service => serviceNames[service.PhysicalResource], StringComparer.Ordinal))
        {
            var serviceName = serviceNames[service.PhysicalResource];
            var container = (InfrastructureLocalContainerSource)service.Source;
            Line(yaml, 1, $"{serviceName}:");
            Line(yaml, 2, $"image: {Quoted(ComposeValue(container.Image))}");
            if (!service.Environment.IsEmpty)
            {
                Line(yaml, 2, "environment:");
                foreach (var variable in service.Environment)
                {
                    var value = ResolveValue(variable.Value, source, effective, serviceNames, diagnostics,
                        $"/topology/services/{service.PhysicalResource.Value}/environment/{variable.Name}");
                    Line(yaml, 3, $"{QuotedKey(variable.Name)}: {EnvironmentScalar(variable.Value, value)}");
                }
            }
            if (!service.Command.IsEmpty)
            {
                Line(yaml, 2, "command:");
                foreach (var argument in service.Command)
                    Line(yaml, 3, $"- {Quoted(ComposeValue(argument))}");
            }
            if (service.Endpoints.Any(static endpoint => endpoint.Exposure == InfrastructureLocalEndpointExposure.HostLoopback))
            {
                Line(yaml, 2, "ports:");
                foreach (var endpoint in service.Endpoints.Where(static endpoint => endpoint.Exposure == InfrastructureLocalEndpointExposure.HostLoopback))
                {
                    var hostPort = Effective(endpoint.HostPort!.Subject, endpoint.HostPort.Setting, effective).Value;
                    var containerPort = endpoint.ContainerPort.Resolve(source.Configuration);
                    Line(yaml, 3, $"- {DoubleQuoted($"127.0.0.1:{hostPort}:{containerPort.ToString(CultureInfo.InvariantCulture)}")}");
                }
            }
            if (!service.Mounts.IsEmpty)
            {
                Line(yaml, 2, "volumes:");
                foreach (var mount in service.Mounts)
                {
                    var suffix = mount.ReadOnly ? ":ro" : string.Empty;
                    Line(yaml, 3, $"- {Quoted(ComposeValue($"{volumeNames[mount.Volume]}:{mount.TargetPath}{suffix}"))}");
                }
            }
            if (!service.FileMounts.IsEmpty)
            {
                Line(yaml, 2, "configs:");
                foreach (var mount in service.FileMounts)
                {
                    Line(yaml, 3, $"- source: {Quoted(configNames[mount.File])}");
                    Line(yaml, 4, $"target: {Quoted(ComposeValue(mount.TargetPath))}");
                }
            }
            if (!service.ReadyDependencies.IsEmpty)
            {
                Line(yaml, 2, "depends_on:");
                foreach (var dependency in service.ReadyDependencies.OrderBy(item => serviceNames[item], StringComparer.Ordinal))
                {
                    Line(yaml, 3, $"{serviceNames[dependency]}:");
                    Line(yaml, 4, "condition: service_healthy");
                }
            }
            if (service.Health is { } health)
                EmitHealth(yaml, service, health, source.Configuration, diagnostics);
            if (service.StopGracePeriod is { } grace)
            {
                var duration = Duration(grace, diagnostics, $"/topology/services/{service.PhysicalResource.Value}/stopGracePeriod");
                Line(yaml, 2, $"stop_grace_period: {Quoted(duration)}");
            }
        }

        if (!source.Topology.Files.IsEmpty)
        {
            Line(yaml, 0, string.Empty);
            Line(yaml, 0, "configs:");
            foreach (var file in source.Topology.Files.OrderBy(file => configNames[file.Id], StringComparer.Ordinal))
            {
                Line(yaml, 1, $"{configNames[file.Id]}:");
                Line(yaml, 2, "content: |-");
                var content = ResolveFile(file, source, effective, serviceNames, diagnostics).Replace("$", "$$", StringComparison.Ordinal);
                foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                    Line(yaml, 3, line);
            }
        }

        if (!source.Topology.Volumes.IsEmpty)
        {
            Line(yaml, 0, string.Empty);
            Line(yaml, 0, "volumes:");
            foreach (var volume in source.Topology.Volumes.OrderBy(volume => volumeNames[volume.Id], StringComparer.Ordinal))
                Line(yaml, 1, $"{volumeNames[volume.Id]}: {{}}");
        }
        return yaml.ToString();
    }

    static void EmitHealth(
        StringBuilder yaml,
        InfrastructureLocalService service,
        InfrastructureLocalHealthPolicy health,
        InfrastructureConventionResolution configuration,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        List<string> probes = [];
        foreach (var probe in health.Probes)
        {
            switch (probe)
            {
                case InfrastructureLocalCommandHealthProbe command:
                    probes.Add(string.Join(' ', new[] { command.Executable }.Concat(command.Arguments).Select(ShellArgument)));
                    break;
                case InfrastructureLocalHttpHealthProbe http:
                    var endpoint = service.Endpoints.Single(candidate => candidate.Id == http.Endpoint);
                    var containerPort = endpoint.ContainerPort.Resolve(configuration);
                    var uri = $"{endpoint.Scheme}://localhost:{containerPort.ToString(CultureInfo.InvariantCulture)}{http.Path}";
                    var expectedStatus = http.ExpectedStatus.ToString(CultureInfo.InvariantCulture);
                    probes.Add($"if command -v curl >/dev/null 2>&1; then status=$(curl --silent --output /dev/null --write-out '%{{http_code}}' {ShellLiteral(uri)}) && [ \"$status\" -eq {expectedStatus} ]; else wget --quiet --server-response --spider {ShellLiteral(uri)} 2>&1 | awk '/^  HTTP\\// {{ status=$2 }} END {{ exit status == {expectedStatus} ? 0 : 1 }}'; fi");
                    break;
                default:
                    Add(diagnostics, DiagnosticCodes.HealthProbeUnsupported, $"Health probe '{probe.GetType().Name}' is unsupported.", $"/topology/services/{service.PhysicalResource.Value}/health", service.PhysicalResource.Value);
                    break;
            }
        }

        Line(yaml, 2, "healthcheck:");
        Line(yaml, 3, "test:");
        Line(yaml, 4, $"- {Quoted("CMD-SHELL")}");
        Line(yaml, 4, $"- {Quoted(string.Join(" && ", probes).Replace("$", "$$", StringComparison.Ordinal))}");
        Line(yaml, 3, $"interval: {Quoted(Duration(health.Interval, diagnostics, $"/topology/services/{service.PhysicalResource.Value}/health/interval"))}");
        Line(yaml, 3, $"timeout: {Quoted(Duration(health.Timeout, diagnostics, $"/topology/services/{service.PhysicalResource.Value}/health/timeout"))}");
        Line(yaml, 3, $"retries: {health.Retries.ToString(CultureInfo.InvariantCulture)}");
        if (health.StartPeriod is { } startPeriod)
            Line(yaml, 3, $"start_period: {Quoted(Duration(startPeriod, diagnostics, $"/topology/services/{service.PhysicalResource.Value}/health/startPeriod"))}");
    }

    static IEnumerable<DockerComposeEndpointMapping> EndpointMappings(
        InfrastructureLocalRealizationDocument source,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, string> serviceNames)
    {
        foreach (var service in source.Topology.Services)
        {
            foreach (var endpoint in service.Endpoints)
            {
                var containerPort = endpoint.ContainerPort.Resolve(source.Configuration);
                var serviceAddress = $"{endpoint.Scheme}://{serviceNames[service.PhysicalResource]}:{containerPort.ToString(CultureInfo.InvariantCulture)}";
                var hostAddress = endpoint.HostPort is null
                    ? null
                    : $"{endpoint.Scheme}://localhost:{Effective(endpoint.HostPort.Subject, endpoint.HostPort.Setting, effective).Value}";
                yield return new(
                    physicalResource: service.PhysicalResource,
                    endpoint: endpoint.Id,
                    exposure: endpoint.Exposure,
                    role: endpoint.Role,
                    serviceAddress: serviceAddress,
                    hostAddress: hostAddress);
            }
        }
    }

    static string ResolveFile(
        InfrastructureLocalFile file,
        InfrastructureLocalRealizationDocument source,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, string> serviceNames,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        StringBuilder content = new();
        for (var index = 0; index < file.Content.Length; index++)
        {
            content.Append(ResolveValue(
                file.Content[index],
                source,
                effective,
                serviceNames,
                diagnostics,
                $"/topology/files/{file.Id.Value}/content/{index.ToString(CultureInfo.InvariantCulture)}"));
        }
        return content.ToString();
    }

    static string ResolveValue(
        InfrastructureLocalValue value,
        InfrastructureLocalRealizationDocument source,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, string> serviceNames,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string location)
    {
        switch (value)
        {
            case InfrastructureLocalLiteralValue literal:
                return literal.Value;
            case InfrastructureLocalConfigurationValue configuration:
                return Effective(configuration.Subject, configuration.Setting, effective).Value;
            case InfrastructureLocalSecretValue secret:
                if (!DockerComposeNames.IsEnvironmentVariableName(secret.Name))
                    return UnsupportedValue(secret, diagnostics, location);
                return $"${{{secret.Name}:?required secret {secret.Name}}}";
            case InfrastructureLocalEndpointValue endpointValue:
                var service = source.Topology.Services.Single(candidate => candidate.PhysicalResource == endpointValue.Service);
                var endpoint = service.Endpoints.Single(candidate => candidate.Id == endpointValue.Endpoint);
                var host = endpointValue.Address == InfrastructureLocalEndpointAddress.ServiceNetwork
                    ? serviceNames[service.PhysicalResource]
                    : "localhost";
                var port = endpointValue.Address == InfrastructureLocalEndpointAddress.ServiceNetwork
                    ? endpoint.ContainerPort.Resolve(source.Configuration).ToString(CultureInfo.InvariantCulture)
                    : Effective(endpoint.HostPort!.Subject, endpoint.HostPort.Setting, effective).Value;
                var uri = $"{endpoint.Scheme}://{host}:{port}";
                return endpointValue.Format switch
                {
                    InfrastructureLocalEndpointValueFormat.Uri => uri,
                    InfrastructureLocalEndpointValueFormat.JsonUriArray => JsonSerializer.Serialize(new[] { uri }),
                    _ => UnsupportedValue(endpointValue, diagnostics, location)
                };
            default:
                return UnsupportedValue(value, diagnostics, location);
        }
    }

    static string UnsupportedValue(
        InfrastructureLocalValue value,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string location)
    {
        Add(diagnostics, DiagnosticCodes.ValueUnsupported, $"Local value '{value.GetType().Name}' is unsupported.", location, value.GetType().FullName ?? value.GetType().Name);
        return string.Empty;
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
            if (name.Length == 0)
            {
                Add(diagnostics, DiagnosticCodes.NameInvalid, $"Compose {kind} identity '{source.Source}' does not contain a representable name segment.", $"/topology/{kind}s", identity(source.Identity));
                continue;
            }
            if (owners.TryGetValue(name, out var owner))
            {
                Add(diagnostics, DiagnosticCodes.NameCollision, $"Compose {kind} name '{name}' is shared by '{owner}' and '{source.Source}'.", $"/topology/{kind}s", identity(source.Identity));
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

    static string Duration(
        TimeSpan duration,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string location)
    {
        if (duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            Add(diagnostics, DiagnosticCodes.DurationUnsupported, $"Duration '{duration}' has sub-millisecond precision.", location, duration.ToString());
            return string.Empty;
        }
        if (duration.Ticks % TimeSpan.TicksPerSecond == 0)
            return $"{duration.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s";
        return $"{duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms";
    }

    static string EnvironmentScalar(InfrastructureLocalValue source, string value) =>
        source is InfrastructureLocalSecretValue ? DoubleQuoted(value) : Quoted(ComposeValue(value));

    static string QuotedKey(string value) => value.All(static character =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')
        ? value
        : Quoted(value);

    static string Quoted(string value) => value.Any(static character => char.IsControl(character))
        ? DoubleQuoted(value)
        : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    static string DoubleQuoted(string value)
    {
        StringBuilder quoted = new(value.Length + 2);
        quoted.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': quoted.Append("\\\\"); break;
                case '"': quoted.Append("\\\""); break;
                case '\0': quoted.Append("\\0"); break;
                case '\a': quoted.Append("\\a"); break;
                case '\b': quoted.Append("\\b"); break;
                case '\t': quoted.Append("\\t"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\v': quoted.Append("\\v"); break;
                case '\f': quoted.Append("\\f"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\u001b': quoted.Append("\\e"); break;
                case < ' ' or '\u007f': quoted.Append($"\\x{(int)character:x2}"); break;
                default: quoted.Append(character); break;
            }
        }
        return quoted.Append('"').ToString();
    }

    static string ComposeValue(string value) => value.Replace("$", "$$", StringComparison.Ordinal);

    static string ShellArgument(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)}\"";

    static string ShellLiteral(string value) => Quoted(value);

    static void Line(StringBuilder target, int indentation, string value)
    {
        target.Append(' ', indentation * 2);
        target.Append(value);
        target.Append('\n');
    }

    static void Add(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string code,
        string message,
        string location,
        string subject) => diagnostics.Add(new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location,
            SchemaLocation: subject,
            Evidence: new(
                stage: Stage,
                subject: subject,
                sourceReferences: [DockerComposeArtifactManifest.CurrentCompiler],
                resolutionOptions: ["Change the local realization or select an explicit supported Compose override."])));

    static bool HasErrors(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
