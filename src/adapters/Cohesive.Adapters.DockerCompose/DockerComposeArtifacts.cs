using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Infra;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.DockerCompose;

/// <summary>Exact SHA-256 fingerprint of emitted Docker Compose YAML bytes.</summary>
public sealed record DockerComposeArtifactFingerprint
{
    /// <summary>Current digest algorithm.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Current byte canonicalization profile.</summary>
    public const string CurrentCanonicalization = "cohesive-docker-compose/yaml-utf8-lf/v1";

    /// <summary>Creates Compose artifact fingerprint metadata.</summary>
    /// <param name="algorithm">Digest algorithm.</param>
    /// <param name="canonicalization">Byte canonicalization profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument is empty or white-space.</exception>
    [JsonConstructor]
    public DockerComposeArtifactFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        if (!string.Equals(Algorithm, CurrentAlgorithm, StringComparison.Ordinal))
            throw new ArgumentException($"Compose fingerprint algorithm '{Algorithm}' is unsupported.", nameof(algorithm));
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        if (!string.Equals(Canonicalization, CurrentCanonicalization, StringComparison.Ordinal))
            throw new ArgumentException($"Compose fingerprint canonicalization '{Canonicalization}' is unsupported.", nameof(canonicalization));
        Value = Guard.RequireNotNullOrWhiteSpace(value);
        if (Value.Length != 64 || Value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A Compose SHA-256 fingerprint must be 64 lowercase hexadecimal characters.", nameof(value));
    }

    /// <summary>Digest algorithm.</summary>
    public string Algorithm { get; }

    /// <summary>Byte canonicalization profile.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }

    /// <summary>Computes the exact fingerprint of canonical Compose YAML.</summary>
    /// <param name="yaml">Canonical LF-terminated YAML.</param>
    /// <returns>SHA-256 metadata for the exact UTF-8 bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="yaml"/> is <see langword="null"/>.</exception>
    public static DockerComposeArtifactFingerprint Compute(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new(
            algorithm: CurrentAlgorithm,
            canonicalization: CurrentCanonicalization,
            value: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))));
    }
}

/// <summary>Stable mapping from canonical Infra resource identity to emitted Compose service name.</summary>
public sealed record DockerComposeServiceMapping
{
    /// <summary>Creates a service mapping.</summary>
    /// <param name="resource">Canonical logical resource.</param>
    /// <param name="physicalResource">Exact physical service identity.</param>
    /// <param name="serviceName">Emitted Compose service name.</param>
    /// <exception cref="ArgumentException">An identity or name is empty.</exception>
    [JsonConstructor]
    public DockerComposeServiceMapping(
        InfrastructureNodeId resource,
        InfrastructurePhysicalResourceId physicalResource,
        string serviceName)
    {
        if (string.IsNullOrWhiteSpace(resource.Value))
            throw new ArgumentException("A Compose service mapping requires a logical resource.", nameof(resource));
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("A Compose service mapping requires a physical resource.", nameof(physicalResource));
        Resource = resource;
        PhysicalResource = physicalResource;
        ServiceName = Guard.RequireNotNullOrWhiteSpace(serviceName);
    }

    /// <summary>Canonical logical resource.</summary>
    public InfrastructureNodeId Resource { get; }

    /// <summary>Exact physical service identity.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Emitted Compose service name.</summary>
    public string ServiceName { get; }
}

/// <summary>Resolved emitted endpoint address with canonical source identity.</summary>
public sealed record DockerComposeEndpointMapping
{
    /// <summary>Creates a resolved endpoint mapping.</summary>
    /// <param name="physicalResource">Exact physical service identity.</param>
    /// <param name="endpoint">Service-local endpoint identity.</param>
    /// <param name="exposure">Endpoint exposure semantics.</param>
    /// <param name="role">Endpoint user-facing role.</param>
    /// <param name="serviceAddress">Address available to services in the Compose network.</param>
    /// <param name="hostAddress">Published host-loopback address, when exposed.</param>
    /// <exception cref="ArgumentException">An identity or required address is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An endpoint policy is unsupported.</exception>
    [JsonConstructor]
    public DockerComposeEndpointMapping(
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLocalEndpointId endpoint,
        InfrastructureLocalEndpointExposure exposure,
        InfrastructureLocalEndpointRole role,
        string serviceAddress,
        string? hostAddress)
    {
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("A Compose endpoint mapping requires a physical resource.", nameof(physicalResource));
        if (string.IsNullOrWhiteSpace(endpoint.Value))
            throw new ArgumentException("A Compose endpoint mapping requires an endpoint.", nameof(endpoint));
        if (!Enum.IsDefined(exposure))
            throw new ArgumentOutOfRangeException(nameof(exposure), exposure, "Unsupported endpoint exposure.");
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported endpoint role.");
        PhysicalResource = physicalResource;
        Endpoint = endpoint;
        Exposure = exposure;
        Role = role;
        ServiceAddress = Guard.RequireNotNullOrWhiteSpace(serviceAddress);
        HostAddress = hostAddress is null ? null : Guard.RequireNotNullOrWhiteSpace(hostAddress);
    }

    /// <summary>Exact physical service identity.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Service-local endpoint identity.</summary>
    public InfrastructureLocalEndpointId Endpoint { get; }

    /// <summary>Endpoint exposure semantics.</summary>
    public InfrastructureLocalEndpointExposure Exposure { get; }

    /// <summary>Endpoint user-facing role.</summary>
    public InfrastructureLocalEndpointRole Role { get; }

    /// <summary>Address available to services in the Compose network.</summary>
    public string ServiceAddress { get; }

    /// <summary>Published host-loopback address, when exposed.</summary>
    public string? HostAddress { get; }
}

/// <summary>Stable mapping from canonical local volume identity to emitted Compose volume name.</summary>
public sealed record DockerComposeVolumeMapping
{
    /// <summary>Creates a volume mapping.</summary>
    /// <param name="volume">Canonical local volume identity.</param>
    /// <param name="volumeName">Emitted Compose volume name.</param>
    /// <exception cref="ArgumentException">The identity or name is empty.</exception>
    [JsonConstructor]
    public DockerComposeVolumeMapping(InfrastructureLocalVolumeId volume, string volumeName)
    {
        if (string.IsNullOrWhiteSpace(volume.Value))
            throw new ArgumentException("A Compose volume mapping requires a volume.", nameof(volume));
        Volume = volume;
        VolumeName = Guard.RequireNotNullOrWhiteSpace(volumeName);
    }

    /// <summary>Canonical local volume identity.</summary>
    public InfrastructureLocalVolumeId Volume { get; }

    /// <summary>Emitted Compose volume name.</summary>
    public string VolumeName { get; }
}

/// <summary>Stable mapping from canonical generated-file identity to emitted Compose config name.</summary>
public sealed record DockerComposeConfigMapping
{
    /// <summary>Creates a generated-config mapping.</summary>
    /// <param name="file">Canonical generated-file identity.</param>
    /// <param name="configName">Emitted Compose config name.</param>
    /// <exception cref="ArgumentException">The identity or name is empty.</exception>
    [JsonConstructor]
    public DockerComposeConfigMapping(InfrastructureLocalFileId file, string configName)
    {
        if (string.IsNullOrWhiteSpace(file.Value))
            throw new ArgumentException("A Compose config mapping requires a generated file.", nameof(file));
        File = file;
        ConfigName = Guard.RequireNotNullOrWhiteSpace(configName);
    }

    /// <summary>Canonical generated-file identity.</summary>
    public InfrastructureLocalFileId File { get; }

    /// <summary>Emitted Compose config name.</summary>
    public string ConfigName { get; }
}

/// <summary>Retained harness operation intent adjacent to the Compose artifact.</summary>
public sealed record DockerComposeOperationMapping
{
    /// <summary>Creates an operation mapping.</summary>
    /// <param name="operation">Canonical operation identity.</param>
    /// <param name="placement">Operation execution placement.</param>
    /// <param name="effect">Declared operation effect.</param>
    /// <param name="executable">Exact operation executable.</param>
    /// <param name="arguments">Exact operation argument vector.</param>
    /// <param name="mutationAuthority">Required lifecycle authority for environment mutations.</param>
    /// <exception cref="ArgumentException">An identity or executable is empty, or an argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An operation policy is unsupported.</exception>
    [JsonConstructor]
    public DockerComposeOperationMapping(
        InfrastructureLocalOperationId operation,
        InfrastructureLocalExecutionPlacement placement,
        InfrastructureLocalOperationEffect effect,
        string executable,
        ImmutableArray<string> arguments,
        InfrastructureLifecycleAuthorityId? mutationAuthority)
    {
        if (string.IsNullOrWhiteSpace(operation.Value))
            throw new ArgumentException("A Compose operation mapping requires an operation.", nameof(operation));
        if (!Enum.IsDefined(placement))
            throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unsupported operation placement.");
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unsupported operation effect.");
        if (!arguments.IsDefaultOrEmpty && arguments.Any(static argument => argument is null))
            throw new ArgumentException("Compose operation arguments cannot contain null.", nameof(arguments));
        if (mutationAuthority is { } authority && string.IsNullOrWhiteSpace(authority.Value))
            throw new ArgumentException("A Compose operation mutation authority cannot be default.", nameof(mutationAuthority));
        Operation = operation;
        Placement = placement;
        Effect = effect;
        Executable = Guard.RequireNotNullOrWhiteSpace(executable);
        Arguments = arguments.IsDefaultOrEmpty ? [] : arguments;
        MutationAuthority = mutationAuthority;
    }

    /// <summary>Canonical operation identity.</summary>
    public InfrastructureLocalOperationId Operation { get; }

    /// <summary>Operation execution placement.</summary>
    public InfrastructureLocalExecutionPlacement Placement { get; }

    /// <summary>Declared operation effect.</summary>
    public InfrastructureLocalOperationEffect Effect { get; }

    /// <summary>Exact operation executable.</summary>
    public string Executable { get; }

    /// <summary>Exact operation argument vector.</summary>
    public ImmutableArray<string> Arguments { get; }

    /// <summary>Required lifecycle authority for environment mutations.</summary>
    public InfrastructureLifecycleAuthorityId? MutationAuthority { get; }
}

/// <summary>Canonical provenance manifest adjacent to emitted Compose YAML.</summary>
public sealed record DockerComposeArtifactManifest
{
    /// <summary>Current Docker Compose lifecycle-interpreter identity.</summary>
    public const string CurrentTarget = "docker-compose/v2";

    /// <summary>Current manifest schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-docker-compose-manifest/v2";

    /// <summary>Current deterministic compiler identity.</summary>
    public const string CurrentCompiler = "cohesive.adapters.docker-compose/v2";

    /// <summary>Creates a Compose artifact manifest.</summary>
    /// <param name="schemaVersion">Exact manifest schema.</param>
    /// <param name="compiler">Exact compiler identity.</param>
    /// <param name="sourceRealization">Exact physical realization fence.</param>
    /// <param name="localRealization">Exact local construction-realization fingerprint.</param>
    /// <param name="environment">Selected environment profile identity.</param>
    /// <param name="lifecycleAuthority">Lifecycle authority owning managed resources.</param>
    /// <param name="dataLifetime">Managed-data retention semantics.</param>
    /// <param name="isolation">Lifecycle namespace isolation semantics.</param>
    /// <param name="projectName">Effective Compose project name.</param>
    /// <param name="configuration">Exact effective configuration with attribution.</param>
    /// <param name="yamlFingerprint">Exact emitted YAML fingerprint.</param>
    /// <param name="services">Canonical service mappings.</param>
    /// <param name="endpoints">Canonical endpoint mappings.</param>
    /// <param name="volumes">Canonical volume mappings.</param>
    /// <param name="configs">Canonical generated-config mappings.</param>
    /// <param name="operations">Retained operation mappings.</param>
    /// <param name="decisions">Inspectable target lowering decisions.</param>
    /// <param name="maximumLifetime">Optional environment deadline requiring lifecycle-runner enforcement.</param>
    /// <exception cref="ArgumentNullException">A required reference or string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A schema, compiler, project name, or collection is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A local environment policy is unsupported.</exception>
    [JsonConstructor]
    public DockerComposeArtifactManifest(
        string schemaVersion,
        string compiler,
        InfrastructureRealizationReference sourceRealization,
        InfrastructureLocalRealizationFingerprint localRealization,
        InfrastructureLocalEnvironmentProfileId environment,
        InfrastructureLifecycleAuthorityId lifecycleAuthority,
        InfrastructureLocalDataLifetime dataLifetime,
        InfrastructureLocalEnvironmentIsolation isolation,
        string projectName,
        InfrastructureConventionResolution configuration,
        DockerComposeArtifactFingerprint yamlFingerprint,
        ImmutableArray<DockerComposeServiceMapping> services,
        ImmutableArray<DockerComposeEndpointMapping> endpoints,
        ImmutableArray<DockerComposeVolumeMapping> volumes,
        ImmutableArray<DockerComposeConfigMapping> configs,
        ImmutableArray<DockerComposeOperationMapping> operations,
        ImmutableArray<InfrastructureLocalTargetDecision> decisions,
        TimeSpan? maximumLifetime = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Compose manifest schema '{SchemaVersion}' is unsupported.", nameof(schemaVersion));
        Compiler = Guard.RequireNotNullOrWhiteSpace(compiler);
        if (!string.Equals(Compiler, CurrentCompiler, StringComparison.Ordinal))
            throw new ArgumentException($"Compose manifest compiler '{Compiler}' is unsupported.", nameof(compiler));
        SourceRealization = Guard.RequireNotNull(sourceRealization);
        LocalRealization = Guard.RequireNotNull(localRealization);
        if (string.IsNullOrWhiteSpace(environment.Value))
            throw new ArgumentException("A Compose manifest requires an environment profile.", nameof(environment));
        if (string.IsNullOrWhiteSpace(lifecycleAuthority.Value))
            throw new ArgumentException("A Compose manifest requires a lifecycle authority.", nameof(lifecycleAuthority));
        if (!Enum.IsDefined(dataLifetime))
            throw new ArgumentOutOfRangeException(nameof(dataLifetime), dataLifetime, "Unsupported local data-lifetime policy.");
        if (!Enum.IsDefined(isolation))
            throw new ArgumentOutOfRangeException(nameof(isolation), isolation, "Unsupported local isolation policy.");
        Environment = environment;
        LifecycleAuthority = lifecycleAuthority;
        DataLifetime = dataLifetime;
        Isolation = isolation;
        ProjectName = Guard.RequireNotNullOrWhiteSpace(projectName);
        if (!DockerComposeNames.IsProjectName(ProjectName))
            throw new ArgumentException("A Compose project name must satisfy the lowercase Compose name grammar.", nameof(projectName));
        Configuration = Guard.RequireNotNull(configuration);
        YamlFingerprint = Guard.RequireNotNull(yamlFingerprint);
        Services = Normalize(services, static item => item.ServiceName, nameof(services));
        Endpoints = Normalize(endpoints, static item => $"{item.PhysicalResource.Value}/{item.Endpoint.Value}", nameof(endpoints));
        Volumes = Normalize(volumes, static item => item.VolumeName, nameof(volumes));
        Configs = Normalize(configs, static item => item.ConfigName, nameof(configs));
        Operations = Normalize(operations, static item => item.Operation.Value, nameof(operations));
        Decisions = Normalize(decisions, static item => item.Concern, nameof(decisions));
        if (Decisions.Any(static decision => !string.Equals(decision.Target, CurrentTarget, StringComparison.Ordinal)))
            throw new ArgumentException("Compose manifest decisions must identify the current Docker Compose target.", nameof(decisions));
        if (maximumLifetime.HasValue && maximumLifetime.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumLifetime), maximumLifetime, "Maximum lifetime must be positive.");
        MaximumLifetime = maximumLifetime;
    }

    /// <summary>Exact manifest schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact compiler identity.</summary>
    public string Compiler { get; }

    /// <summary>Exact physical realization fence.</summary>
    public InfrastructureRealizationReference SourceRealization { get; }

    /// <summary>Exact local construction-realization fingerprint.</summary>
    public InfrastructureLocalRealizationFingerprint LocalRealization { get; }

    /// <summary>Selected environment profile identity.</summary>
    public InfrastructureLocalEnvironmentProfileId Environment { get; }

    /// <summary>Lifecycle authority owning managed resources.</summary>
    public InfrastructureLifecycleAuthorityId LifecycleAuthority { get; }

    /// <summary>Managed-data retention semantics.</summary>
    public InfrastructureLocalDataLifetime DataLifetime { get; }

    /// <summary>Lifecycle namespace isolation semantics.</summary>
    public InfrastructureLocalEnvironmentIsolation Isolation { get; }

    /// <summary>Effective Compose project name.</summary>
    public string ProjectName { get; }

    /// <summary>Exact effective configuration with attribution.</summary>
    public InfrastructureConventionResolution Configuration { get; }

    /// <summary>Exact emitted YAML fingerprint.</summary>
    public DockerComposeArtifactFingerprint YamlFingerprint { get; }

    /// <summary>Service mappings in emitted-name order.</summary>
    public ImmutableArray<DockerComposeServiceMapping> Services { get; }

    /// <summary>Endpoint mappings in physical-resource and endpoint order.</summary>
    public ImmutableArray<DockerComposeEndpointMapping> Endpoints { get; }

    /// <summary>Volume mappings in emitted-name order.</summary>
    public ImmutableArray<DockerComposeVolumeMapping> Volumes { get; }

    /// <summary>Generated-config mappings in emitted-name order.</summary>
    public ImmutableArray<DockerComposeConfigMapping> Configs { get; }

    /// <summary>Retained operation mappings in operation-identity order.</summary>
    public ImmutableArray<DockerComposeOperationMapping> Operations { get; }

    /// <summary>Inspectable target lowering decisions.</summary>
    public ImmutableArray<InfrastructureLocalTargetDecision> Decisions { get; }

    /// <summary>Optional environment deadline requiring lifecycle-runner enforcement.</summary>
    public TimeSpan? MaximumLifetime { get; }

    /// <summary>Serializes the canonical adjacent manifest.</summary>
    /// <param name="formatting">Compact or indented portable JSON formatting.</param>
    /// <returns>Deterministic manifest JSON.</returns>
    public string ToJson(PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented) =>
        JsonSerializer.Serialize(this, StrictDocumentJson.CreateOptions(formatting));

    static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values, Func<T, string> key, string parameterName)
        where T : class
    {
        if (values.IsDefaultOrEmpty)
            return [];
        if (values.Any(static item => item is null))
            throw new ArgumentException("Compose manifest collections cannot contain null.", parameterName);
        var ordered = values.Sort((left, right) => StringComparer.Ordinal.Compare(key(left), key(right)));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(key(ordered[index - 1]), key(ordered[index]), StringComparison.Ordinal))
                throw new ArgumentException($"Compose manifest identity '{key(ordered[index])}' is duplicated.", parameterName);
        }
        return ordered;
    }
}

/// <summary>One exact emitted Compose YAML artifact and adjacent provenance manifest.</summary>
public sealed record DockerComposeArtifact
{
    /// <summary>Creates an emitted artifact and validates its exact fingerprint.</summary>
    /// <param name="yaml">Canonical LF-terminated Compose YAML.</param>
    /// <param name="manifest">Adjacent exact provenance manifest.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">YAML is not LF-terminated or its fingerprint differs from the manifest.</exception>
    public DockerComposeArtifact(string yaml, DockerComposeArtifactManifest manifest)
    {
        Yaml = Guard.RequireNotNull(yaml);
        if (!Yaml.EndsWith('\n') || Yaml.Contains('\r'))
            throw new ArgumentException("Canonical Compose YAML must use LF line endings and end with one LF.", nameof(yaml));
        Manifest = Guard.RequireNotNull(manifest);
        var computed = DockerComposeArtifactFingerprint.Compute(Yaml);
        if (computed != Manifest.YamlFingerprint)
            throw new ArgumentException("Compose YAML does not match the adjacent manifest fingerprint.", nameof(manifest));
    }

    /// <summary>Canonical LF-terminated Compose YAML.</summary>
    public string Yaml { get; }

    /// <summary>Adjacent exact provenance manifest.</summary>
    public DockerComposeArtifactManifest Manifest { get; }

    /// <summary>Canonical indented manifest JSON.</summary>
    [JsonIgnore]
    public string ManifestJson => Manifest.ToJson();
}

/// <summary>Result of compiling one exact local realization into Docker Compose.</summary>
public sealed record DockerComposeCompilation
{
    /// <summary>Creates a Compose compilation result.</summary>
    /// <param name="artifact">Emitted artifact, or <see langword="null"/> on any error.</param>
    /// <param name="diagnostics">Structured deterministic adapter diagnostics.</param>
    /// <exception cref="ArgumentException">An artifact is retained with errors or omitted without errors.</exception>
    public DockerComposeCompilation(
        DockerComposeArtifact? artifact,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        Artifact = artifact;
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
        var hasErrors = Diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error);
        if ((Artifact is null) != hasErrors)
            throw new ArgumentException("Compose compilation must emit exactly when no error diagnostic remains.", nameof(artifact));
    }

    /// <summary>Emitted artifact, or <see langword="null"/> on any error.</summary>
    public DockerComposeArtifact? Artifact { get; }

    /// <summary>Structured deterministic adapter diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether compilation emitted an exact artifact.</summary>
    [JsonIgnore]
    public bool IsSuccess => Artifact is not null;
}

static class DockerComposeNames
{
    internal static bool IsProjectName(string value) => value.Length > 0
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(static character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character is '-' or '_');

    internal static bool IsEnvironmentVariableName(string value) => value.Length > 0
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.Skip(1).All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
