using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Infra;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Aspire;

/// <summary>Strategy explicitly selected when Aspire cannot natively reproduce a command health probe.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum AspireCommandHealthOverrideStrategy
{
    /// <summary>Establish readiness by opening a TCP connection to an exact projected endpoint.</summary>
    TcpConnect = 0
}

/// <summary>How a canonical local operation is surfaced by the Aspire interpretation.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum AspireOperationRealization
{
    /// <summary>The host operation is exposed as an Aspire UI/API process command.</summary>
    ProcessCommand = 0,

    /// <summary>The operation remains inspectable but is controlled by the Aspire lifecycle rather than invoked as a process command.</summary>
    LifecycleControl = 1
}

/// <summary>TLS identity policy for Aspire's local Developer Control Plane connection.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum AspireDcpTlsCertificateMode
{
    /// <summary>DCP generates an ephemeral self-signed TLS identity for each AppHost run.</summary>
    EphemeralSelfSigned = 0,

    /// <summary>DCP reuses and exports the host's trusted ASP.NET Core developer certificate.</summary>
    HostDeveloperCertificate = 1
}

/// <summary>Explicit, provenance-bearing replacement for one exact command health probe.</summary>
public sealed record AspireCommandHealthOverride
{
    /// <summary>Creates an exact command-health override.</summary>
    /// <param name="physicalResource">Canonical service whose command probe is replaced.</param>
    /// <param name="executable">Exact canonical probe executable.</param>
    /// <param name="arguments">Exact canonical probe arguments.</param>
    /// <param name="strategy">Aspire-side readiness strategy.</param>
    /// <param name="endpoint">Exact endpoint used by the replacement strategy.</param>
    /// <param name="rationale">Human-readable reason the override is required.</param>
    /// <param name="sourceReferences">Attributable source or decision references.</param>
    /// <exception cref="ArgumentException">An identity, executable, rationale, argument, or source reference is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="strategy"/> is unsupported.</exception>
    [JsonConstructor]
    public AspireCommandHealthOverride(
        InfrastructurePhysicalResourceId physicalResource,
        string executable,
        ImmutableArray<string> arguments,
        AspireCommandHealthOverrideStrategy strategy,
        InfrastructureLocalEndpointId endpoint,
        string rationale,
        ImmutableArray<SourceReference> sourceReferences)
    {
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
        {
            throw new ArgumentException("An Aspire health override requires a physical resource.", nameof(physicalResource));
        }

        if (string.IsNullOrWhiteSpace(endpoint.Value))
        {
            throw new ArgumentException("An Aspire health override requires an endpoint.", nameof(endpoint));
        }

        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported Aspire command-health override strategy.");
        }

        if (!arguments.IsDefaultOrEmpty && arguments.Any(static argument => argument is null))
        {
            throw new ArgumentException("Aspire health override arguments cannot contain null.", nameof(arguments));
        }

        if (sourceReferences.IsDefaultOrEmpty
            || sourceReferences.Any(static reference => string.IsNullOrWhiteSpace(reference.Value)))
        {
            throw new ArgumentException("An Aspire health override requires non-empty source references.", nameof(sourceReferences));
        }

        var normalizedSourceReferences = sourceReferences.Sort();
        for (var index = 1; index < normalizedSourceReferences.Length; index++)
        {
            if (normalizedSourceReferences[index - 1] == normalizedSourceReferences[index])
            {
                throw new ArgumentException("Aspire health override source references cannot be duplicated.", nameof(sourceReferences));
            }
        }

        PhysicalResource = physicalResource;
        Executable = Guard.RequireNotNullOrWhiteSpace(executable);
        Arguments = arguments.IsDefaultOrEmpty ? [] : arguments;
        Strategy = strategy;
        Endpoint = endpoint;
        Rationale = Guard.RequireNotNullOrWhiteSpace(rationale);
        SourceReferences = normalizedSourceReferences;
    }

    /// <summary>Canonical service whose command probe is replaced.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Exact canonical probe executable.</summary>
    public string Executable { get; }

    /// <summary>Exact canonical probe arguments.</summary>
    public ImmutableArray<string> Arguments { get; }

    /// <summary>Aspire-side readiness strategy.</summary>
    public AspireCommandHealthOverrideStrategy Strategy { get; }

    /// <summary>Exact endpoint used by the replacement strategy.</summary>
    public InfrastructureLocalEndpointId Endpoint { get; }

    /// <summary>Human-readable reason the override is required.</summary>
    public string Rationale { get; }

    /// <summary>Attributable source or decision references.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }
}

/// <summary>Compiler policy for projecting a local realization into Aspire.</summary>
public sealed record AspireLocalCompilerOptions
{
    /// <summary>Creates Aspire compiler policy.</summary>
    /// <param name="commandHealthOverrides">Explicit replacements for unsupported command health probes.</param>
    /// <param name="dcpTlsCertificateMode">Explicit DCP TLS identity policy.</param>
    /// <exception cref="ArgumentException">The override collection contains null or duplicate command evidence.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dcpTlsCertificateMode"/> is unsupported.</exception>
    [JsonConstructor]
    public AspireLocalCompilerOptions(
        ImmutableArray<AspireCommandHealthOverride> commandHealthOverrides,
        AspireDcpTlsCertificateMode dcpTlsCertificateMode = AspireDcpTlsCertificateMode.EphemeralSelfSigned)
    {
        if (!Enum.IsDefined(dcpTlsCertificateMode))
            throw new ArgumentOutOfRangeException(nameof(dcpTlsCertificateMode), dcpTlsCertificateMode, "Unsupported Aspire DCP TLS certificate mode.");
        if (!commandHealthOverrides.IsDefaultOrEmpty && commandHealthOverrides.Any(static item => item is null))
            throw new ArgumentException("Aspire compiler options cannot contain null health overrides.", nameof(commandHealthOverrides));
        DcpTlsCertificateMode = dcpTlsCertificateMode;
        CommandHealthOverrides = commandHealthOverrides.IsDefaultOrEmpty
            ? []
            : commandHealthOverrides.Sort(static (left, right) => StringComparer.Ordinal.Compare(Key(left), Key(right)));
        for (var index = 1; index < CommandHealthOverrides.Length; index++)
        {
            if (string.Equals(Key(CommandHealthOverrides[index - 1]), Key(CommandHealthOverrides[index]), StringComparison.Ordinal))
                throw new ArgumentException($"Aspire command-health override '{Key(CommandHealthOverrides[index])}' is duplicated.", nameof(commandHealthOverrides));
        }
    }

    /// <summary>Empty policy that permits only native Aspire health probes.</summary>
    public static AspireLocalCompilerOptions Default { get; } = new(
        commandHealthOverrides: [],
        dcpTlsCertificateMode: AspireDcpTlsCertificateMode.EphemeralSelfSigned);

    /// <summary>Explicit replacements for unsupported command health probes.</summary>
    public ImmutableArray<AspireCommandHealthOverride> CommandHealthOverrides { get; }

    /// <summary>Explicit DCP TLS identity policy.</summary>
    public AspireDcpTlsCertificateMode DcpTlsCertificateMode { get; }

    static string Key(AspireCommandHealthOverride item) =>
        $"{item.PhysicalResource.Value}/{item.Executable}/{string.Join('\u001f', item.Arguments)}";
}

/// <summary>One canonical service and its deterministic Aspire resource identity.</summary>
public sealed record AspireServiceProjection
{
    /// <summary>Creates a service projection.</summary>
    /// <param name="resourceName">Deterministic Aspire resource name.</param>
    /// <param name="service">Canonical local service semantics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is empty or white-space.</exception>
    [JsonConstructor]
    public AspireServiceProjection(string resourceName, InfrastructureLocalService service)
    {
        ResourceName = Guard.RequireNotNullOrWhiteSpace(resourceName);
        Service = Guard.RequireNotNull(service);
    }

    /// <summary>Deterministic Aspire resource name.</summary>
    public string ResourceName { get; }

    /// <summary>Canonical local service semantics.</summary>
    public InfrastructureLocalService Service { get; }
}

/// <summary>Resolved endpoint identity and addresses projected into Aspire.</summary>
public sealed record AspireEndpointProjection
{
    /// <summary>Creates an endpoint projection.</summary>
    /// <param name="physicalResource">Canonical physical service.</param>
    /// <param name="resourceName">Aspire resource name.</param>
    /// <param name="endpoint">Canonical endpoint semantics.</param>
    /// <param name="hostPort">Resolved host port, when exposed.</param>
    /// <param name="serviceAddress">Deterministic service-network address.</param>
    /// <param name="hostAddress">Deterministic host-loopback address, when exposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or address is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hostPort"/> is outside 1-65535.</exception>
    [JsonConstructor]
    public AspireEndpointProjection(
        InfrastructurePhysicalResourceId physicalResource,
        string resourceName,
        InfrastructureLocalEndpoint endpoint,
        int? hostPort,
        string serviceAddress,
        string? hostAddress)
    {
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("An Aspire endpoint projection requires a physical resource.", nameof(physicalResource));
        endpoint = Guard.RequireNotNull(endpoint);
        if (hostPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(hostPort), hostPort, "An Aspire host port must be between 1 and 65535.");
        if ((endpoint.Exposure == InfrastructureLocalEndpointExposure.HostLoopback) != hostPort.HasValue)
            throw new ArgumentException("Aspire endpoint host-port resolution must match canonical exposure semantics.", nameof(hostPort));
        if (hostPort.HasValue != (hostAddress is not null))
            throw new ArgumentException("Aspire endpoint host address and host port must be present together.", nameof(hostAddress));
        PhysicalResource = physicalResource;
        ResourceName = Guard.RequireNotNullOrWhiteSpace(resourceName);
        Endpoint = endpoint;
        HostPort = hostPort;
        ServiceAddress = Guard.RequireNotNullOrWhiteSpace(serviceAddress);
        HostAddress = hostAddress is null ? null : Guard.RequireNotNullOrWhiteSpace(hostAddress);
    }

    /// <summary>Canonical physical service.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Aspire resource name.</summary>
    public string ResourceName { get; }

    /// <summary>Canonical endpoint semantics.</summary>
    public InfrastructureLocalEndpoint Endpoint { get; }

    /// <summary>Resolved host port, when exposed.</summary>
    public int? HostPort { get; }

    /// <summary>Deterministic service-network address.</summary>
    public string ServiceAddress { get; }

    /// <summary>Deterministic host-loopback address, when exposed.</summary>
    public string? HostAddress { get; }
}

/// <summary>Volume identity and target-specific retention realization.</summary>
public sealed record AspireVolumeProjection
{
    /// <summary>Creates a volume projection.</summary>
    /// <param name="volume">Canonical local volume.</param>
    /// <param name="volumeName">Stable named volume for persistent data, or <see langword="null"/> for an isolated anonymous volume.</param>
    /// <exception cref="ArgumentException">The volume identity or supplied name is invalid.</exception>
    [JsonConstructor]
    public AspireVolumeProjection(InfrastructureLocalVolumeId volume, string? volumeName)
    {
        if (string.IsNullOrWhiteSpace(volume.Value))
            throw new ArgumentException("An Aspire volume projection requires a canonical volume.", nameof(volume));
        Volume = volume;
        VolumeName = volumeName is null ? null : Guard.RequireNotNullOrWhiteSpace(volumeName);
    }

    /// <summary>Canonical local volume.</summary>
    public InfrastructureLocalVolumeId Volume { get; }

    /// <summary>Stable named volume for persistent data, or <see langword="null"/> for an isolated anonymous volume.</summary>
    public string? VolumeName { get; }
}

/// <summary>Resolved generated-file content retained beside its canonical identity.</summary>
public sealed record AspireFileProjection
{
    /// <summary>Creates a generated-file projection.</summary>
    /// <param name="file">Canonical generated-file identity.</param>
    /// <param name="contents">Resolved deterministic contents.</param>
    /// <exception cref="ArgumentException"><paramref name="file"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public AspireFileProjection(InfrastructureLocalFileId file, string contents)
    {
        if (string.IsNullOrWhiteSpace(file.Value))
            throw new ArgumentException("An Aspire generated-file projection requires a canonical file.", nameof(file));
        File = file;
        Contents = Guard.RequireNotNull(contents);
    }

    /// <summary>Canonical generated-file identity.</summary>
    public InfrastructureLocalFileId File { get; }

    /// <summary>Resolved deterministic contents.</summary>
    public string Contents { get; }
}

/// <summary>External secret identity mapped to one Aspire secret parameter without retaining its value.</summary>
public sealed record AspireSecretProjection
{
    /// <summary>Creates a secret-parameter projection.</summary>
    /// <param name="secretName">Canonical external secret name.</param>
    /// <param name="parameterName">Deterministic Aspire parameter resource name.</param>
    /// <exception cref="ArgumentException">A name is empty or white-space.</exception>
    [JsonConstructor]
    public AspireSecretProjection(string secretName, string parameterName)
    {
        SecretName = Guard.RequireNotNullOrWhiteSpace(secretName);
        ParameterName = Guard.RequireNotNullOrWhiteSpace(parameterName);
    }

    /// <summary>Canonical external secret name.</summary>
    public string SecretName { get; }

    /// <summary>Deterministic Aspire parameter resource name.</summary>
    public string ParameterName { get; }
}

/// <summary>One canonical operation exposed through the Aspire command surface.</summary>
public sealed record AspireOperationProjection
{
    /// <summary>Creates an operation projection.</summary>
    /// <param name="operation">Canonical operation semantics.</param>
    /// <param name="realization">Aspire operation realization.</param>
    /// <param name="requiredResources">Aspire resources required by the operation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="requiredResources"/> contains a null, empty, or duplicate name.</exception>
    [JsonConstructor]
    public AspireOperationProjection(
        InfrastructureLocalOperation operation,
        AspireOperationRealization realization,
        ImmutableArray<string> requiredResources)
    {
        Operation = Guard.RequireNotNull(operation);
        if (!Enum.IsDefined(realization))
            throw new ArgumentOutOfRangeException(nameof(realization), realization, "Unsupported Aspire operation realization.");
        if (!requiredResources.IsDefaultOrEmpty && requiredResources.Any(static item => string.IsNullOrWhiteSpace(item)))
            throw new ArgumentException("Aspire operation resources cannot be null, empty, or white-space.", nameof(requiredResources));
        Realization = realization;
        RequiredResources = requiredResources.IsDefaultOrEmpty ? [] : requiredResources.Sort(StringComparer.Ordinal);
        if (RequiredResources.Distinct(StringComparer.Ordinal).Count() != RequiredResources.Length)
            throw new ArgumentException("Aspire operation resources cannot be duplicated.", nameof(requiredResources));
    }

    /// <summary>Canonical operation semantics.</summary>
    public InfrastructureLocalOperation Operation { get; }

    /// <summary>Aspire operation realization.</summary>
    public AspireOperationRealization Realization { get; }

    /// <summary>Aspire resources required by the operation.</summary>
    public ImmutableArray<string> RequiredResources { get; }
}

/// <summary>SHA-256 fingerprint of one canonical Aspire projection document.</summary>
public sealed record AspireLocalProjectionFingerprint
{
    /// <summary>Current digest algorithm.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Current projection canonicalization profile.</summary>
    public const string CurrentCanonicalization = "cohesive-aspire-local-projection/json-jcs/v4";

    /// <summary>Creates projection fingerprint metadata.</summary>
    /// <param name="algorithm">Digest algorithm.</param>
    /// <param name="canonicalization">Canonical byte profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentException">Metadata is unsupported or malformed.</exception>
    [JsonConstructor]
    public AspireLocalProjectionFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
        if (!string.Equals(Algorithm, CurrentAlgorithm, StringComparison.Ordinal))
            throw new ArgumentException($"Aspire projection fingerprint algorithm '{Algorithm}' is unsupported.", nameof(algorithm));
        if (!string.Equals(Canonicalization, CurrentCanonicalization, StringComparison.Ordinal))
            throw new ArgumentException($"Aspire projection canonicalization '{Canonicalization}' is unsupported.", nameof(canonicalization));
        if (Value.Length != 64 || Value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("An Aspire projection SHA-256 fingerprint must be 64 lowercase hexadecimal characters.", nameof(value));
    }

    /// <summary>Digest algorithm.</summary>
    public string Algorithm { get; }

    /// <summary>Canonical byte profile.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Canonical, inspectable projection of one exact local Infra realization into Aspire.</summary>
public sealed record AspireLocalProjectionDocument
{
    /// <summary>Current Aspire lifecycle-interpreter identity.</summary>
    public const string CurrentTarget = "aspire/dcp-13.5.2";

    /// <summary>Current portable projection schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-aspire-local-projection/v4";

    /// <summary>Current deterministic compiler identity.</summary>
    public const string CurrentCompiler = "cohesive.adapters.aspire/v4";

    /// <summary>Creates or restores an exact Aspire projection.</summary>
    /// <param name="schemaVersion">Exact projection schema.</param>
    /// <param name="compiler">Exact compiler identity.</param>
    /// <param name="sourceRealization">Exact physical realization fence.</param>
    /// <param name="localRealization">Exact local realization fingerprint.</param>
    /// <param name="environment">Selected local environment policy.</param>
    /// <param name="projectName">Effective lifecycle namespace.</param>
    /// <param name="configuration">Exact effective configuration and attribution.</param>
    /// <param name="dcpTlsCertificateMode">Exact DCP TLS identity policy.</param>
    /// <param name="controlResourceName">Aspire resource exposing host operations through UI and API commands.</param>
    /// <param name="services">Projected service graph.</param>
    /// <param name="endpoints">Resolved endpoint mappings.</param>
    /// <param name="volumes">Projected volume policies.</param>
    /// <param name="files">Resolved generated files.</param>
    /// <param name="secrets">Projected external secret parameters.</param>
    /// <param name="operations">Projected operation commands.</param>
    /// <param name="commandHealthOverrides">Accepted exact command-health overrides.</param>
    /// <param name="decisions">Inspectable target lowering decisions.</param>
    /// <param name="fingerprint">Persisted fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Metadata, collections, or the supplied fingerprint are invalid.</exception>
    [JsonConstructor]
    public AspireLocalProjectionDocument(
        string schemaVersion,
        string compiler,
        InfrastructureRealizationReference sourceRealization,
        InfrastructureLocalRealizationFingerprint localRealization,
        InfrastructureLocalEnvironmentProfile environment,
        string projectName,
        InfrastructureConventionResolution configuration,
        AspireDcpTlsCertificateMode dcpTlsCertificateMode,
        string controlResourceName,
        ImmutableArray<AspireServiceProjection> services,
        ImmutableArray<AspireEndpointProjection> endpoints,
        ImmutableArray<AspireVolumeProjection> volumes,
        ImmutableArray<AspireFileProjection> files,
        ImmutableArray<AspireSecretProjection> secrets,
        ImmutableArray<AspireOperationProjection> operations,
        ImmutableArray<AspireCommandHealthOverride> commandHealthOverrides,
        ImmutableArray<InfrastructureLocalTargetDecision> decisions,
        AspireLocalProjectionFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        Compiler = Guard.RequireNotNullOrWhiteSpace(compiler);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Aspire projection schema '{SchemaVersion}' is unsupported.", nameof(schemaVersion));
        if (!string.Equals(Compiler, CurrentCompiler, StringComparison.Ordinal))
            throw new ArgumentException($"Aspire projection compiler '{Compiler}' is unsupported.", nameof(compiler));
        SourceRealization = Guard.RequireNotNull(sourceRealization);
        LocalRealization = Guard.RequireNotNull(localRealization);
        Environment = Guard.RequireNotNull(environment);
        ProjectName = Guard.RequireNotNullOrWhiteSpace(projectName);
        Configuration = Guard.RequireNotNull(configuration);
        if (!Enum.IsDefined(dcpTlsCertificateMode))
            throw new ArgumentOutOfRangeException(nameof(dcpTlsCertificateMode), dcpTlsCertificateMode, "Unsupported Aspire DCP TLS certificate mode.");
        DcpTlsCertificateMode = dcpTlsCertificateMode;
        ControlResourceName = Guard.RequireNotNullOrWhiteSpace(controlResourceName);
        Services = Normalize(services, static item => item.ResourceName, nameof(services));
        if (Services.Select(static item => item.Service.PhysicalResource).Distinct().Count() != Services.Length)
            throw new ArgumentException("Aspire service projections cannot duplicate a canonical physical resource.", nameof(services));
        Endpoints = Normalize(endpoints, static item => $"{item.PhysicalResource.Value}/{item.Endpoint.Id.Value}", nameof(endpoints));
        Volumes = Normalize(volumes, static item => item.Volume.Value, nameof(volumes));
        if (Environment.DataLifetime == InfrastructureLocalDataLifetime.Persistent
            && Volumes.Any(static item => item.VolumeName is null))
        {
            throw new ArgumentException("Persistent Aspire projections require a stable name for every volume.", nameof(volumes));
        }
        if (Environment.DataLifetime == InfrastructureLocalDataLifetime.Ephemeral
            && Volumes.Any(static item => item.VolumeName is not null))
        {
            throw new ArgumentException("Ephemeral Aspire projections require anonymous volumes.", nameof(volumes));
        }
        Files = Normalize(files, static item => item.File.Value, nameof(files));
        Secrets = Normalize(secrets, static item => item.SecretName, nameof(secrets));
        Operations = Normalize(operations, static item => item.Operation.Id.Value, nameof(operations));
        CommandHealthOverrides = Normalize(commandHealthOverrides, static item => $"{item.PhysicalResource.Value}/{item.Executable}/{string.Join('\u001f', item.Arguments)}", nameof(commandHealthOverrides));
        Decisions = Normalize(decisions, static item => item.Concern, nameof(decisions));
        if (Decisions.Any(static decision => !string.Equals(decision.Target, CurrentTarget, StringComparison.Ordinal)))
            throw new ArgumentException("Aspire projection decisions must identify the current Aspire target.", nameof(decisions));

        var computed = ComputeFingerprint(
            SchemaVersion,
            Compiler,
            SourceRealization,
            LocalRealization,
            Environment,
            ProjectName,
            Configuration,
            DcpTlsCertificateMode,
            ControlResourceName,
            Services,
            Endpoints,
            Volumes,
            Files,
            Secrets,
            Operations,
            CommandHealthOverrides,
            Decisions);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied Aspire projection fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact projection schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact deterministic compiler identity.</summary>
    public string Compiler { get; }

    /// <summary>Exact physical realization fence.</summary>
    public InfrastructureRealizationReference SourceRealization { get; }

    /// <summary>Exact local realization fingerprint.</summary>
    public InfrastructureLocalRealizationFingerprint LocalRealization { get; }

    /// <summary>Selected local environment policy.</summary>
    public InfrastructureLocalEnvironmentProfile Environment { get; }

    /// <summary>Effective lifecycle namespace.</summary>
    public string ProjectName { get; }

    /// <summary>Exact effective configuration and attribution.</summary>
    public InfrastructureConventionResolution Configuration { get; }

    /// <summary>Exact DCP TLS identity policy.</summary>
    public AspireDcpTlsCertificateMode DcpTlsCertificateMode { get; }

    /// <summary>Aspire resource exposing host operations through UI and API commands.</summary>
    public string ControlResourceName { get; }

    /// <summary>Projected service graph in Aspire resource-name order.</summary>
    public ImmutableArray<AspireServiceProjection> Services { get; }

    /// <summary>Resolved endpoints in canonical identity order.</summary>
    public ImmutableArray<AspireEndpointProjection> Endpoints { get; }

    /// <summary>Projected volumes in canonical identity order.</summary>
    public ImmutableArray<AspireVolumeProjection> Volumes { get; }

    /// <summary>Resolved generated files in canonical identity order.</summary>
    public ImmutableArray<AspireFileProjection> Files { get; }

    /// <summary>Projected external secret parameters in canonical secret-name order.</summary>
    public ImmutableArray<AspireSecretProjection> Secrets { get; }

    /// <summary>Projected operation commands in canonical identity order.</summary>
    public ImmutableArray<AspireOperationProjection> Operations { get; }

    /// <summary>Accepted exact command-health overrides.</summary>
    public ImmutableArray<AspireCommandHealthOverride> CommandHealthOverrides { get; }

    /// <summary>Inspectable target lowering decisions.</summary>
    public ImmutableArray<InfrastructureLocalTargetDecision> Decisions { get; }

    /// <summary>Exact projection fingerprint.</summary>
    public AspireLocalProjectionFingerprint Fingerprint { get; }

    /// <summary>Serializes canonical portable projection JSON.</summary>
    /// <param name="formatting">Compact or indented JSON formatting.</param>
    /// <returns>Deterministic projection JSON.</returns>
    public string ToJson(PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented) =>
        JsonSerializer.Serialize(this, StrictDocumentJson.CreateOptions(formatting));

    static AspireLocalProjectionFingerprint ComputeFingerprint(
        string schemaVersion,
        string compiler,
        InfrastructureRealizationReference sourceRealization,
        InfrastructureLocalRealizationFingerprint localRealization,
        InfrastructureLocalEnvironmentProfile environment,
        string projectName,
        InfrastructureConventionResolution configuration,
        AspireDcpTlsCertificateMode dcpTlsCertificateMode,
        string controlResourceName,
        ImmutableArray<AspireServiceProjection> services,
        ImmutableArray<AspireEndpointProjection> endpoints,
        ImmutableArray<AspireVolumeProjection> volumes,
        ImmutableArray<AspireFileProjection> files,
        ImmutableArray<AspireSecretProjection> secrets,
        ImmutableArray<AspireOperationProjection> operations,
        ImmutableArray<AspireCommandHealthOverride> commandHealthOverrides,
        ImmutableArray<InfrastructureLocalTargetDecision> decisions)
    {
        var bytes = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                schemaVersion,
                compiler,
                sourceRealization,
                localRealization,
                environment,
                projectName,
                configuration,
                dcpTlsCertificateMode,
                controlResourceName,
                services,
                endpoints,
                volumes,
                files,
                secrets,
                operations,
                commandHealthOverrides,
                decisions),
            StrictDocumentJson.CreateOptions());
        return new(
            algorithm: AspireLocalProjectionFingerprint.CurrentAlgorithm,
            canonicalization: AspireLocalProjectionFingerprint.CurrentCanonicalization,
            value: Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values, Func<T, string> key, string parameterName)
        where T : class
    {
        if (values.IsDefaultOrEmpty)
            return [];
        if (values.Any(static item => item is null))
            throw new ArgumentException("Aspire projection collections cannot contain null.", parameterName);
        var ordered = values.Sort((left, right) => StringComparer.Ordinal.Compare(key(left), key(right)));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(key(ordered[index - 1]), key(ordered[index]), StringComparison.Ordinal))
                throw new ArgumentException($"Aspire projection identity '{key(ordered[index])}' is duplicated.", parameterName);
        }
        return ordered;
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        string Compiler,
        InfrastructureRealizationReference SourceRealization,
        InfrastructureLocalRealizationFingerprint LocalRealization,
        InfrastructureLocalEnvironmentProfile Environment,
        string ProjectName,
        InfrastructureConventionResolution Configuration,
        AspireDcpTlsCertificateMode DcpTlsCertificateMode,
        string ControlResourceName,
        ImmutableArray<AspireServiceProjection> Services,
        ImmutableArray<AspireEndpointProjection> Endpoints,
        ImmutableArray<AspireVolumeProjection> Volumes,
        ImmutableArray<AspireFileProjection> Files,
        ImmutableArray<AspireSecretProjection> Secrets,
        ImmutableArray<AspireOperationProjection> Operations,
        ImmutableArray<AspireCommandHealthOverride> CommandHealthOverrides,
        ImmutableArray<InfrastructureLocalTargetDecision> Decisions);
}

/// <summary>Result of deterministically compiling one exact local realization into Aspire.</summary>
public sealed record AspireLocalCompilation
{
    /// <summary>Creates an Aspire compilation result.</summary>
    /// <param name="projection">Exact projection, or <see langword="null"/> when errors remain.</param>
    /// <param name="diagnostics">Structured deterministic adapter diagnostics.</param>
    /// <exception cref="ArgumentException">Projection presence and error diagnostics disagree.</exception>
    public AspireLocalCompilation(
        AspireLocalProjectionDocument? projection,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        Projection = projection;
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
        var hasErrors = Diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error);
        if ((Projection is null) != hasErrors)
            throw new ArgumentException("Aspire compilation must emit exactly when no error diagnostic remains.", nameof(projection));
    }

    /// <summary>Exact projection, or <see langword="null"/> when errors remain.</summary>
    public AspireLocalProjectionDocument? Projection { get; }

    /// <summary>Structured deterministic adapter diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether compilation emitted an exact projection.</summary>
    [JsonIgnore]
    public bool IsSuccess => Projection is not null;
}
