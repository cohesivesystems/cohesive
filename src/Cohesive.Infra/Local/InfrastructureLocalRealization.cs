using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Local;

/// <summary>Deterministic fingerprint of one exact local infrastructure realization.</summary>
public sealed record InfrastructureLocalRealizationFingerprint
{
    /// <summary>Current digest algorithm.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Current canonicalization profile.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-local-realization/v1-c14n/v1";

    /// <summary>Creates local-realization fingerprint metadata.</summary>
    /// <param name="algorithm">Digest algorithm.</param>
    /// <param name="canonicalization">Canonicalization profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalRealizationFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Digest algorithm.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Portable, exact local construction realization shared by lifecycle adapters.</summary>
/// <remarks>
/// This artifact is construction input, not a backend artifact, execution plan, receipt, or observation. Compose and
/// Aspire adapters must fence their outputs to this exact fingerprint and the referenced physical realization.
/// </remarks>
public sealed record InfrastructureLocalRealizationDocument
{
    /// <summary>Current portable document schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-infra-local-realization/v1";

    /// <summary>Creates or restores an exact local realization document.</summary>
    /// <param name="schemaVersion">Exact document schema.</param>
    /// <param name="realization">Exact physical-applicability realization fence.</param>
    /// <param name="environment">Selected local environment policy.</param>
    /// <param name="topology">Canonical target-neutral local construction topology.</param>
    /// <param name="configuration">Resolved effective configuration and attribution.</param>
    /// <param name="diagnostics">Structured deterministic compiler diagnostics.</param>
    /// <param name="fingerprint">Persisted fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema or supplied fingerprint is not canonical.</exception>
    [JsonConstructor]
    public InfrastructureLocalRealizationDocument(
        string schemaVersion,
        InfrastructureRealizationReference realization,
        InfrastructureLocalEnvironmentProfile environment,
        InfrastructureLocalTopology topology,
        InfrastructureConventionResolution configuration,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        InfrastructureLocalRealizationFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Local realization schema '{SchemaVersion}' is unsupported.", nameof(schemaVersion));

        Realization = Guard.RequireNotNull(realization);
        Environment = Guard.RequireNotNull(environment);
        Topology = Guard.RequireNotNull(topology);
        Configuration = Guard.RequireNotNull(configuration);
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
        var computed = ComputeFingerprint(SchemaVersion, Realization, Environment, Topology, Configuration, Diagnostics);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied local-realization fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact portable document schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact physical-applicability realization fence.</summary>
    public InfrastructureRealizationReference Realization { get; }

    /// <summary>Selected local environment policy.</summary>
    public InfrastructureLocalEnvironmentProfile Environment { get; }

    /// <summary>Canonical target-neutral local construction topology.</summary>
    public InfrastructureLocalTopology Topology { get; }

    /// <summary>Resolved effective configuration and attribution.</summary>
    public InfrastructureConventionResolution Configuration { get; }

    /// <summary>Structured deterministic compiler diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Exact local-realization fingerprint.</summary>
    public InfrastructureLocalRealizationFingerprint Fingerprint { get; }

    /// <summary>Whether the local construction realization is complete enough for adapter projection.</summary>
    [JsonIgnore]
    public bool IsValid => Configuration.IsValid
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    static InfrastructureLocalRealizationFingerprint ComputeFingerprint(
        string schemaVersion,
        InfrastructureRealizationReference realization,
        InfrastructureLocalEnvironmentProfile environment,
        InfrastructureLocalTopology topology,
        InfrastructureConventionResolution configuration,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(schemaVersion, realization, environment, topology, configuration, diagnostics),
            StrictDocumentJson.CreateOptions());
        return new(
            algorithm: InfrastructureLocalRealizationFingerprint.CurrentAlgorithm,
            canonicalization: InfrastructureLocalRealizationFingerprint.CurrentCanonicalization,
            value: Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        InfrastructureRealizationReference Realization,
        InfrastructureLocalEnvironmentProfile Environment,
        InfrastructureLocalTopology Topology,
        InfrastructureConventionResolution Configuration,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);
}

/// <summary>Compiles an exact physical realization and local topology into a validated adapter-neutral artifact.</summary>
public static class InfrastructureLocalRealizationCompiler
{
    const string Stage = "infrastructure-local-realization";

    /// <summary>Stable diagnostics emitted by local-realization compilation.</summary>
    public static class DiagnosticCodes
    {
        /// <summary>The fenced physical realization is incomplete.</summary>
        public const string PhysicalRealizationIncomplete = "infra.local.realization.incomplete";
        /// <summary>A service is not backed by the exact lifecycle realization.</summary>
        public const string ServiceBindingMismatch = "infra.local.service.bindingMismatch";
        /// <summary>A container image is not pinned to a non-latest tag or digest.</summary>
        public const string ImageNotPinned = "infra.local.service.imageNotPinned";
        /// <summary>A referenced effective setting has no resolved value.</summary>
        public const string ConfigurationMissing = "infra.local.configuration.missing";
        /// <summary>A host port is invalid or duplicates another host port.</summary>
        public const string HostPortInvalid = "infra.local.endpoint.hostPortInvalid";
        /// <summary>A configured service listener port is invalid.</summary>
        public const string ContainerPortInvalid = "infra.local.endpoint.containerPortInvalid";
        /// <summary>A service, endpoint, or volume reference cannot be resolved.</summary>
        public const string ReferenceUnknown = "infra.local.reference.unknown";
        /// <summary>A readiness dependency graph contains a cycle.</summary>
        public const string ReadinessCycle = "infra.local.readiness.cycle";
        /// <summary>An environment mutation is not fenced to the selected lifecycle authority.</summary>
        public const string MutationAuthorityMismatch = "infra.local.operation.mutationAuthorityMismatch";
        /// <summary>A likely secret environment variable embeds or references ordinary configuration.</summary>
        public const string SecretValueRequired = "infra.local.environment.secretValueRequired";
        /// <summary>An isolated environment relies on a global or adapter convention for its lifecycle namespace.</summary>
        public const string IsolationConfigurationRequired = "infra.local.environment.isolationConfigurationRequired";
        /// <summary>A ready dependency has no health policy to establish readiness.</summary>
        public const string DependencyHealthMissing = "infra.local.readiness.dependencyHealthMissing";
    }

    /// <summary>Compiles one exact local realization.</summary>
    /// <param name="realization">Exact physical realization.</param>
    /// <param name="environment">Local environment policy.</param>
    /// <param name="topology">Target-neutral local topology.</param>
    /// <param name="configurationProfiles">Configuration candidates resolved by shared authority precedence.</param>
    /// <returns>A fingerprinted local realization with structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public static InfrastructureLocalRealizationDocument Compile(
        InfrastructureRealization realization,
        InfrastructureLocalEnvironmentProfile environment,
        InfrastructureLocalTopology topology,
        IEnumerable<InfrastructureConventionProfile> configurationProfiles)
    {
        ArgumentNullException.ThrowIfNull(realization);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(configurationProfiles);

        var configuration = InfrastructureConventionResolver.Resolve(configurationProfiles);
        List<DocumentValidationDiagnostic> diagnostics = [];
        Validate(realization, environment, topology, configuration, diagnostics);
        return new(
            schemaVersion: InfrastructureLocalRealizationDocument.CurrentSchemaVersion,
            realization: realization.ToReference(),
            environment: environment,
            topology: topology,
            configuration: configuration,
            diagnostics: [.. diagnostics]);
    }

    static void Validate(
        InfrastructureRealization realization,
        InfrastructureLocalEnvironmentProfile environment,
        InfrastructureLocalTopology topology,
        InfrastructureConventionResolution configuration,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (!realization.IsCapabilityWitnessComplete)
            Add(diagnostics, DiagnosticCodes.PhysicalRealizationIncomplete, "The exact physical realization is not capability-witness complete.", "/realization", realization.Fingerprint.Value);

        var effective = configuration.Configuration.ToDictionary(static item => (item.Subject, item.Setting));
        if (RequireConfiguration(environment.ConfigurationSubject, environment.ProjectNameSetting, "/environment/projectNameSetting", effective, diagnostics, out _, out var projectConfiguration)
            && environment.Isolation == InfrastructureLocalEnvironmentIsolation.Isolated
            && projectConfiguration!.Attribution.Origin is EffectiveConfigurationOrigin.AdapterConvention or EffectiveConfigurationOrigin.FrameworkDefault)
        {
            Add(diagnostics, DiagnosticCodes.IsolationConfigurationRequired,
                "An isolated local environment requires an explicit or scoped-profile project name.",
                "/environment/projectNameSetting", environment.Id.Value);
        }

        var serviceIds = topology.Services.Select(static service => service.PhysicalResource).ToHashSet();
        var volumeIds = topology.Volumes.Select(static volume => volume.Id).ToHashSet();
        var fileIds = topology.Files.Select(static file => file.Id).ToHashSet();
        Dictionary<int, string> hostPorts = [];

        foreach (var file in topology.Files)
        {
            for (var index = 0; index < file.Content.Length; index++)
                ValidateValue(file.Content[index], $"/topology/files/{file.Id.Value}/content/{index}", topology, effective, diagnostics);
        }

        foreach (var service in topology.Services)
        {
            var lifecycle = realization.Lifecycle.Bindings.FirstOrDefault(binding =>
                binding.Resource == service.Resource
                && binding.PhysicalResource == service.PhysicalResource
                && binding.Authority == environment.Authority
                && binding.Disposition == InfrastructureLifecycleDisposition.Managed);
            if (lifecycle is null)
            {
                Add(diagnostics, DiagnosticCodes.ServiceBindingMismatch,
                    $"Service '{service.PhysicalResource.Value}' is not a managed binding for logical resource '{service.Resource.Value}' under authority '{environment.Authority.Value}'.",
                    $"/topology/services/{service.PhysicalResource.Value}", service.PhysicalResource.Value);
            }

            if (!IsPinnedImage(service.Image))
                Add(diagnostics, DiagnosticCodes.ImageNotPinned, $"Service image '{service.Image}' is not pinned.", $"/topology/services/{service.PhysicalResource.Value}/image", service.PhysicalResource.Value);

            foreach (var variable in service.Environment)
            {
                ValidateValue(variable.Value, $"/topology/services/{service.PhysicalResource.Value}/environment/{variable.Name}", topology, effective, diagnostics);
                if (LooksSensitive(variable.Name) && variable.Value is not InfrastructureLocalSecretValue)
                {
                    Add(diagnostics, DiagnosticCodes.SecretValueRequired,
                        $"Likely secret environment variable '{variable.Name}' must retain an external secret reference.",
                        $"/topology/services/{service.PhysicalResource.Value}/environment/{variable.Name}", variable.Name);
                }
            }

            foreach (var endpoint in service.Endpoints)
            {
                if (endpoint.ContainerPort.Configuration is { } containerPortReference)
                {
                    var containerPortLocation = $"/topology/services/{service.PhysicalResource.Value}/endpoints/{endpoint.Id.Value}/containerPort";
                    if (RequireConfiguration(
                            containerPortReference.Subject,
                            containerPortReference.Setting,
                            containerPortLocation,
                            effective,
                            diagnostics,
                            out var containerPortValue)
                        && (!int.TryParse(containerPortValue, NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort)
                            || containerPort is < 1 or > 65535))
                    {
                        Add(
                            diagnostics,
                            DiagnosticCodes.ContainerPortInvalid,
                            $"Effective container port '{containerPortValue}' is outside 1-65535.",
                            containerPortLocation,
                            endpoint.Id.Value);
                    }
                }
                if (endpoint.HostPort is null)
                    continue;
                var location = $"/topology/services/{service.PhysicalResource.Value}/endpoints/{endpoint.Id.Value}/hostPort";
                if (!RequireConfiguration(endpoint.HostPort.Subject, endpoint.HostPort.Setting, location, effective, diagnostics, out var value))
                    continue;
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                {
                    Add(diagnostics, DiagnosticCodes.HostPortInvalid, $"Effective host port '{value}' is outside 1-65535.", location, endpoint.Id.Value);
                }
                else if (hostPorts.TryGetValue(port, out var first))
                {
                    Add(diagnostics, DiagnosticCodes.HostPortInvalid, $"Host port '{port}' is also used by '{first}'.", location, endpoint.Id.Value);
                }
                else
                {
                    hostPorts.Add(port, $"{service.PhysicalResource.Value}/{endpoint.Id.Value}");
                }
            }

            foreach (var mount in service.Mounts)
            {
                if (!volumeIds.Contains(mount.Volume))
                    Add(diagnostics, DiagnosticCodes.ReferenceUnknown, $"Volume '{mount.Volume.Value}' is not declared.", $"/topology/services/{service.PhysicalResource.Value}/mounts/{mount.TargetPath}", mount.Volume.Value);
            }

            foreach (var mount in service.FileMounts)
            {
                if (!fileIds.Contains(mount.File))
                    Add(diagnostics, DiagnosticCodes.ReferenceUnknown, $"Generated file '{mount.File.Value}' is not declared.", $"/topology/services/{service.PhysicalResource.Value}/fileMounts/{mount.TargetPath}", mount.File.Value);
            }

            foreach (var dependency in service.ReadyDependencies)
            {
                if (!serviceIds.Contains(dependency))
                    Add(diagnostics, DiagnosticCodes.ReferenceUnknown, $"Ready dependency '{dependency.Value}' is not a service.", $"/topology/services/{service.PhysicalResource.Value}/readyDependencies", dependency.Value);
                else if (topology.Services.Single(candidate => candidate.PhysicalResource == dependency).Health is null)
                    Add(diagnostics, DiagnosticCodes.DependencyHealthMissing, $"Ready dependency '{dependency.Value}' has no health policy.", $"/topology/services/{service.PhysicalResource.Value}/readyDependencies", dependency.Value);
            }

            foreach (var probe in service.Health?.Probes.OfType<InfrastructureLocalHttpHealthProbe>() ?? [])
            {
                if (!service.Endpoints.Any(endpoint => endpoint.Id == probe.Endpoint))
                    Add(diagnostics, DiagnosticCodes.ReferenceUnknown, $"Health endpoint '{probe.Endpoint.Value}' is not exposed by the service.", $"/topology/services/{service.PhysicalResource.Value}/healthProbes", probe.Endpoint.Value);
            }
        }

        ValidateReadiness(topology, diagnostics);
        foreach (var operation in topology.Operations)
        {
            foreach (var required in operation.RequiredServices)
            {
                if (!serviceIds.Contains(required))
                    Add(diagnostics, DiagnosticCodes.ReferenceUnknown, $"Required service '{required.Value}' is not declared.", $"/topology/operations/{operation.Id.Value}/requiredServices", required.Value);
            }
            if (operation.Service is { } placement && !serviceIds.Contains(placement))
                Add(diagnostics, DiagnosticCodes.ReferenceUnknown, $"Execution service '{placement.Value}' is not declared.", $"/topology/operations/{operation.Id.Value}/service", placement.Value);
            if (operation.MutationAuthority is { } authority && authority != environment.Authority)
                Add(diagnostics, DiagnosticCodes.MutationAuthorityMismatch, $"Operation mutation authority '{authority.Value}' differs from environment authority '{environment.Authority.Value}'.", $"/topology/operations/{operation.Id.Value}/mutationAuthority", operation.Id.Value);
        }
    }

    static void ValidateValue(
        InfrastructureLocalValue value,
        string location,
        InfrastructureLocalTopology topology,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (value is InfrastructureLocalConfigurationValue configured)
        {
            RequireConfiguration(configured.Subject, configured.Setting, location, effective, diagnostics);
        }
        else if (value is InfrastructureLocalEndpointValue endpoint)
        {
            var referenced = topology.Services.FirstOrDefault(service => service.PhysicalResource == endpoint.Service)?
                .Endpoints.FirstOrDefault(candidate => candidate.Id == endpoint.Endpoint);
            if (referenced is null)
            {
                Add(diagnostics, DiagnosticCodes.ReferenceUnknown, $"Endpoint '{endpoint.Service.Value}/{endpoint.Endpoint.Value}' is not declared.", location, endpoint.Endpoint.Value);
            }
            else if (endpoint.Address == InfrastructureLocalEndpointAddress.HostLoopback
                     && referenced.Exposure != InfrastructureLocalEndpointExposure.HostLoopback)
            {
                Add(diagnostics, DiagnosticCodes.ReferenceUnknown, $"Endpoint '{endpoint.Service.Value}/{endpoint.Endpoint.Value}' has no host-loopback address.", location, endpoint.Endpoint.Value);
            }
        }
    }

    static bool RequireConfiguration(
        InfrastructureConfigurationSubject subject,
        InfrastructureSettingId setting,
        string location,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        ICollection<DocumentValidationDiagnostic> diagnostics) =>
        RequireConfiguration(subject, setting, location, effective, diagnostics, out _);

    static bool RequireConfiguration(
        InfrastructureConfigurationSubject subject,
        InfrastructureSettingId setting,
        string location,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        out string value)
        => RequireConfiguration(subject, setting, location, effective, diagnostics, out value, out _);

    static bool RequireConfiguration(
        InfrastructureConfigurationSubject subject,
        InfrastructureSettingId setting,
        string location,
        IReadOnlyDictionary<(InfrastructureConfigurationSubject, InfrastructureSettingId), InfrastructureEffectiveConfiguration> effective,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        out string value,
        out InfrastructureEffectiveConfiguration? configuration)
    {
        if (effective.TryGetValue((subject, setting), out configuration))
        {
            value = configuration.Value;
            return true;
        }
        value = string.Empty;
        configuration = null;
        Add(diagnostics, DiagnosticCodes.ConfigurationMissing, $"Configuration '{subject.Value}/{setting.Value}' has no effective value.", location, $"{subject.Value}/{setting.Value}");
        return false;
    }

    static bool IsPinnedImage(string image)
    {
        if (image.Contains('@', StringComparison.Ordinal))
            return true;
        var slash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        return colon > slash + 1
               && !string.Equals(image[(colon + 1)..], "latest", StringComparison.OrdinalIgnoreCase);
    }

    static bool LooksSensitive(string name) =>
        name.EndsWith("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("SECRET", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("KEY", StringComparison.OrdinalIgnoreCase);

    static void ValidateReadiness(
        InfrastructureLocalTopology topology,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var dependencies = topology.Services.ToDictionary(
            static service => service.PhysicalResource,
            static service => service.ReadyDependencies);
        HashSet<InfrastructurePhysicalResourceId> complete = [];
        HashSet<InfrastructurePhysicalResourceId> active = [];
        foreach (var service in topology.Services)
            Visit(service.PhysicalResource, dependencies, complete, active, diagnostics);
    }

    static void Visit(
        InfrastructurePhysicalResourceId service,
        IReadOnlyDictionary<InfrastructurePhysicalResourceId, ImmutableArray<InfrastructurePhysicalResourceId>> dependencies,
        ISet<InfrastructurePhysicalResourceId> complete,
        ISet<InfrastructurePhysicalResourceId> active,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (complete.Contains(service) || !dependencies.ContainsKey(service))
            return;
        if (!active.Add(service))
        {
            Add(diagnostics, DiagnosticCodes.ReadinessCycle, $"Readiness dependency cycle includes '{service.Value}'.", $"/topology/services/{service.Value}/readyDependencies", service.Value);
            return;
        }
        foreach (var dependency in dependencies[service])
            Visit(dependency, dependencies, complete, active, diagnostics);
        active.Remove(service);
        complete.Add(service);
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
            Evidence: new(stage: Stage, subject: subject)));
}
