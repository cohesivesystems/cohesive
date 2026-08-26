using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Realization;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Local;

/// <summary>Whether an environment retains managed data after its normal stop operation.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureLocalDataLifetime
{
    /// <summary>Managed data survives ordinary environment stops.</summary>
    Persistent = 0,

    /// <summary>Managed data is isolated to one environment run and may be removed when stopped.</summary>
    Ephemeral = 1
}

/// <summary>Whether a local environment may share or must isolate its lifecycle namespace.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureLocalEnvironmentIsolation
{
    /// <summary>The environment may use a conventional stable lifecycle namespace.</summary>
    Shared = 0,

    /// <summary>The environment requires an explicitly or profile-scoped lifecycle namespace.</summary>
    Isolated = 1
}

/// <summary>Exposure semantics for a local endpoint.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureLocalEndpointExposure
{
    /// <summary>The endpoint is available only to services inside the environment.</summary>
    Internal = 0,

    /// <summary>The endpoint is published on the host loopback interface.</summary>
    HostLoopback = 1
}

/// <summary>User-facing role of a local endpoint.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureLocalEndpointRole
{
    /// <summary>Application or infrastructure data-plane endpoint.</summary>
    Data = 0,

    /// <summary>Operator-facing management endpoint.</summary>
    Management = 1,

    /// <summary>Human-facing data explorer or dashboard.</summary>
    UserInterface = 2
}

/// <summary>Address surface selected when deriving a URI from a local endpoint.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureLocalEndpointAddress
{
    /// <summary>Use the service-discovery address available inside the local environment.</summary>
    ServiceNetwork = 0,

    /// <summary>Use the published host-loopback address.</summary>
    HostLoopback = 1
}

/// <summary>Canonical text encoding for a derived endpoint value.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureLocalEndpointValueFormat
{
    /// <summary>Encode one absolute endpoint URI.</summary>
    Uri = 0,

    /// <summary>Encode one endpoint URI as a single-element JSON string array.</summary>
    JsonUriArray = 1
}

/// <summary>Whether a local operation executes on the host or inside one managed service.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureLocalExecutionPlacement
{
    /// <summary>The operation executes on the developer or test host.</summary>
    Host = 0,

    /// <summary>The operation executes inside a managed service.</summary>
    ManagedService = 1
}

/// <summary>Whether a local operation observes or mutates environment state.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureLocalOperationEffect
{
    /// <summary>The operation observes state without intentionally changing it.</summary>
    ReadOnly = 0,

    /// <summary>The operation mutates application data while preserving managed infrastructure.</summary>
    ApplicationMutation = 1,

    /// <summary>The operation mutates the lifecycle or retained data of managed infrastructure.</summary>
    EnvironmentMutation = 2
}

/// <summary>Versioned local environment policy shared by lifecycle interpreters.</summary>
public sealed record InfrastructureLocalEnvironmentProfile
{
    /// <summary>Creates a local environment profile.</summary>
    /// <param name="id">Stable, versioned profile identity.</param>
    /// <param name="authority">Lifecycle authority owning managed resources.</param>
    /// <param name="configurationSubject">Configuration subject for environment-scoped settings.</param>
    /// <param name="projectNameSetting">Effective setting that supplies the isolated environment name.</param>
    /// <param name="dataLifetime">Managed-data retention policy.</param>
    /// <param name="isolation">Lifecycle namespace isolation policy.</param>
    /// <param name="maximumLifetime">Optional maximum run duration for automatically bounded environments.</param>
    /// <exception cref="ArgumentException">An identity is default or the duration is not positive.</exception>
    [JsonConstructor]
    public InfrastructureLocalEnvironmentProfile(
        InfrastructureLocalEnvironmentProfileId id,
        InfrastructureLifecycleAuthorityId authority,
        InfrastructureConfigurationSubject configurationSubject,
        InfrastructureSettingId projectNameSetting,
        InfrastructureLocalDataLifetime dataLifetime,
        InfrastructureLocalEnvironmentIsolation isolation,
        TimeSpan? maximumLifetime = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A local environment profile requires an identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(authority.Value))
            throw new ArgumentException("A local environment profile requires a lifecycle authority.", nameof(authority));
        if (string.IsNullOrWhiteSpace(configurationSubject.Value))
            throw new ArgumentException("A local environment profile requires a configuration subject.", nameof(configurationSubject));
        if (string.IsNullOrWhiteSpace(projectNameSetting.Value))
            throw new ArgumentException("A local environment profile requires a project-name setting.", nameof(projectNameSetting));
        if (!Enum.IsDefined(dataLifetime))
            throw new ArgumentOutOfRangeException(nameof(dataLifetime), dataLifetime, "Unsupported data-lifetime policy.");
        if (!Enum.IsDefined(isolation))
            throw new ArgumentOutOfRangeException(nameof(isolation), isolation, "Unsupported environment isolation policy.");
        if (maximumLifetime.HasValue && maximumLifetime.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumLifetime), maximumLifetime, "Maximum lifetime must be positive.");

        Id = id;
        Authority = authority;
        ConfigurationSubject = configurationSubject;
        ProjectNameSetting = projectNameSetting;
        DataLifetime = dataLifetime;
        Isolation = isolation;
        MaximumLifetime = maximumLifetime;
    }

    /// <summary>Stable, versioned profile identity.</summary>
    public InfrastructureLocalEnvironmentProfileId Id { get; }

    /// <summary>Lifecycle authority owning managed resources.</summary>
    public InfrastructureLifecycleAuthorityId Authority { get; }

    /// <summary>Configuration subject for environment-scoped settings.</summary>
    public InfrastructureConfigurationSubject ConfigurationSubject { get; }

    /// <summary>Effective setting that supplies the isolated environment name.</summary>
    public InfrastructureSettingId ProjectNameSetting { get; }

    /// <summary>Managed-data retention policy.</summary>
    public InfrastructureLocalDataLifetime DataLifetime { get; }

    /// <summary>Lifecycle namespace isolation policy.</summary>
    public InfrastructureLocalEnvironmentIsolation Isolation { get; }

    /// <summary>Optional maximum automatically enforced run duration.</summary>
    public TimeSpan? MaximumLifetime { get; }
}

/// <summary>Portable source for a local environment or command value.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "source")]
[JsonDerivedType(typeof(InfrastructureLocalLiteralValue), "literal")]
[JsonDerivedType(typeof(InfrastructureLocalConfigurationValue), "configuration")]
[JsonDerivedType(typeof(InfrastructureLocalSecretValue), "secret")]
[JsonDerivedType(typeof(InfrastructureLocalEndpointValue), "endpoint")]
public abstract record InfrastructureLocalValue;

/// <summary>Non-sensitive literal local value.</summary>
public sealed record InfrastructureLocalLiteralValue : InfrastructureLocalValue
{
    /// <summary>Creates a literal value.</summary>
    /// <param name="value">Literal non-sensitive value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public InfrastructureLocalLiteralValue(string value) => Value = Guard.RequireNotNull(value);

    /// <summary>Literal value.</summary>
    public string Value { get; }
}

/// <summary>Reference to one value owned by effective infrastructure configuration.</summary>
public sealed record InfrastructureLocalConfigurationValue : InfrastructureLocalValue
{
    /// <summary>Creates an effective-configuration reference.</summary>
    /// <param name="subject">Configuration subject.</param>
    /// <param name="setting">Configuration setting.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    [JsonConstructor]
    public InfrastructureLocalConfigurationValue(
        InfrastructureConfigurationSubject subject,
        InfrastructureSettingId setting)
    {
        if (string.IsNullOrWhiteSpace(subject.Value))
            throw new ArgumentException("A local configuration value requires a subject.", nameof(subject));
        if (string.IsNullOrWhiteSpace(setting.Value))
            throw new ArgumentException("A local configuration value requires a setting.", nameof(setting));
        Subject = subject;
        Setting = setting;
    }

    /// <summary>Configuration subject.</summary>
    public InfrastructureConfigurationSubject Subject { get; }

    /// <summary>Configuration setting.</summary>
    public InfrastructureSettingId Setting { get; }
}

/// <summary>Literal or effective-configuration source for a service listener port.</summary>
public sealed record InfrastructureLocalPort
{
    /// <summary>Creates a listener-port source.</summary>
    /// <param name="literal">Literal port, when the listener is fixed.</param>
    /// <param name="configuration">Effective-configuration reference, when the listener is environment-specific.</param>
    /// <exception cref="ArgumentException">Both or neither source is supplied.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="literal"/> is outside 1-65535.</exception>
    [JsonConstructor]
    public InfrastructureLocalPort(
        int? literal,
        InfrastructureLocalConfigurationValue? configuration)
    {
        if (literal.HasValue == (configuration is not null))
            throw new ArgumentException("A local port requires exactly one literal or configuration source.", nameof(configuration));
        if (literal is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(literal), literal, "A port must be between 1 and 65535.");
        Literal = literal;
        Configuration = configuration;
    }

    /// <summary>Creates a fixed listener port.</summary>
    /// <param name="literal">Literal port.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="literal"/> is outside 1-65535.</exception>
    public InfrastructureLocalPort(int literal)
        : this(literal: literal, configuration: null)
    {
    }

    /// <summary>Creates an environment-specific listener port.</summary>
    /// <param name="configuration">Effective-configuration reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public InfrastructureLocalPort(InfrastructureLocalConfigurationValue configuration)
        : this(literal: null, configuration: Guard.RequireNotNull(configuration))
    {
    }

    /// <summary>Literal port, when fixed.</summary>
    public int? Literal { get; }

    /// <summary>Effective-configuration reference, when environment-specific.</summary>
    public InfrastructureLocalConfigurationValue? Configuration { get; }

    /// <summary>Resolves the exact listener port from effective configuration.</summary>
    /// <param name="resolution">Effective configuration and attribution.</param>
    /// <returns>The exact port in the range 1-65535.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The configured value is missing or is outside 1-65535.</exception>
    public int Resolve(InfrastructureConventionResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (Literal is { } literal)
            return literal;
        var reference = Configuration!;
        var effective = resolution.Configuration.FirstOrDefault(item =>
            item.Subject == reference.Subject && item.Setting == reference.Setting);
        if (effective is null)
        {
            throw new InvalidOperationException(
                $"Local port configuration '{reference.Subject.Value}/{reference.Setting.Value}' is unresolved.");
        }
        if (!int.TryParse(effective.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Effective local port '{effective.Value}' for '{reference.Subject.Value}/{reference.Setting.Value}' is outside 1-65535.");
        }
        return port;
    }

    /// <summary>Converts a literal integer to a fixed local port.</summary>
    /// <param name="literal">Literal port.</param>
    /// <returns>A validated fixed listener-port source.</returns>
    public static implicit operator InfrastructureLocalPort(int literal) => new(literal);

    /// <summary>Converts an effective-configuration reference to an environment-specific local port.</summary>
    /// <param name="configuration">Effective-configuration reference.</param>
    /// <returns>A configured listener-port source.</returns>
    public static implicit operator InfrastructureLocalPort(InfrastructureLocalConfigurationValue configuration) => new(configuration);
}

/// <summary>Reference to a secret supplied at lifecycle execution time.</summary>
public sealed record InfrastructureLocalSecretValue : InfrastructureLocalValue
{
    /// <summary>Creates a secret reference.</summary>
    /// <param name="name">Stable external secret name; never the secret payload.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalSecretValue(string name) => Name = Guard.RequireNotNullOrWhiteSpace(name);

    /// <summary>Stable external secret name.</summary>
    public string Name { get; }
}

/// <summary>Reference to a service endpoint derived by a lifecycle interpreter.</summary>
public sealed record InfrastructureLocalEndpointValue : InfrastructureLocalValue
{
    /// <summary>Creates an endpoint reference.</summary>
    /// <param name="service">Physical service identity.</param>
    /// <param name="endpoint">Service-local endpoint identity.</param>
    /// <param name="address">Address surface to derive.</param>
    /// <param name="format">Canonical textual encoding.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    [JsonConstructor]
    public InfrastructureLocalEndpointValue(
        InfrastructurePhysicalResourceId service,
        InfrastructureLocalEndpointId endpoint,
        InfrastructureLocalEndpointAddress address,
        InfrastructureLocalEndpointValueFormat format = InfrastructureLocalEndpointValueFormat.Uri)
    {
        if (string.IsNullOrWhiteSpace(service.Value))
            throw new ArgumentException("A local endpoint value requires a service.", nameof(service));
        if (string.IsNullOrWhiteSpace(endpoint.Value))
            throw new ArgumentException("A local endpoint value requires an endpoint.", nameof(endpoint));
        if (!Enum.IsDefined(address))
            throw new ArgumentOutOfRangeException(nameof(address), address, "Unsupported endpoint address surface.");
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported endpoint value format.");
        Service = service;
        Endpoint = endpoint;
        Address = address;
        Format = format;
    }

    /// <summary>Physical service identity.</summary>
    public InfrastructurePhysicalResourceId Service { get; }

    /// <summary>Service-local endpoint identity.</summary>
    public InfrastructureLocalEndpointId Endpoint { get; }

    /// <summary>Address surface to derive.</summary>
    public InfrastructureLocalEndpointAddress Address { get; }

    /// <summary>Canonical textual encoding.</summary>
    public InfrastructureLocalEndpointValueFormat Format { get; }
}

/// <summary>One local service environment variable.</summary>
public sealed record InfrastructureLocalEnvironmentVariable
{
    /// <summary>Creates an environment-variable binding.</summary>
    /// <param name="name">Exact variable name.</param>
    /// <param name="value">Portable value source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalEnvironmentVariable(string name, InfrastructureLocalValue value)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Value = Guard.RequireNotNull(value);
    }

    /// <summary>Exact variable name.</summary>
    public string Name { get; }

    /// <summary>Portable value source.</summary>
    public InfrastructureLocalValue Value { get; }
}

/// <summary>One endpoint exposed by a local service.</summary>
public sealed record InfrastructureLocalEndpoint
{
    /// <summary>Creates an endpoint.</summary>
    /// <param name="id">Stable service-local identity.</param>
    /// <param name="scheme">URI scheme.</param>
    /// <param name="containerPort">Literal or configured port inside the service environment.</param>
    /// <param name="exposure">Endpoint exposure semantics.</param>
    /// <param name="role">Endpoint user-facing role.</param>
    /// <param name="hostPort">Effective host-port setting when exposed on loopback.</param>
    /// <exception cref="ArgumentException">An identity is default or exposure and host-port semantics conflict.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A port, exposure, or role is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureLocalEndpoint(
        InfrastructureLocalEndpointId id,
        string scheme,
        InfrastructureLocalPort containerPort,
        InfrastructureLocalEndpointExposure exposure,
        InfrastructureLocalEndpointRole role,
        InfrastructureLocalConfigurationValue? hostPort = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A local endpoint requires an identity.", nameof(id));
        if (!Enum.IsDefined(exposure))
            throw new ArgumentOutOfRangeException(nameof(exposure), exposure, "Unsupported endpoint exposure.");
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported endpoint role.");
        if ((exposure == InfrastructureLocalEndpointExposure.HostLoopback) != (hostPort is not null))
            throw new ArgumentException("Only host-loopback endpoints require a host-port configuration reference.", nameof(hostPort));

        Id = id;
        Scheme = Guard.RequireNotNullOrWhiteSpace(scheme);
        ContainerPort = Guard.RequireNotNull(containerPort);
        Exposure = exposure;
        Role = role;
        HostPort = hostPort;
    }

    /// <summary>Stable service-local identity.</summary>
    public InfrastructureLocalEndpointId Id { get; }

    /// <summary>URI scheme.</summary>
    public string Scheme { get; }

    /// <summary>Literal or configured port inside the service environment.</summary>
    public InfrastructureLocalPort ContainerPort { get; }

    /// <summary>Endpoint exposure semantics.</summary>
    public InfrastructureLocalEndpointExposure Exposure { get; }

    /// <summary>Endpoint user-facing role.</summary>
    public InfrastructureLocalEndpointRole Role { get; }

    /// <summary>Effective host-port setting, when exposed on loopback.</summary>
    public InfrastructureLocalConfigurationValue? HostPort { get; }
}

/// <summary>One volume mount on a local service.</summary>
public sealed record InfrastructureLocalVolumeMount
{
    /// <summary>Creates a volume mount.</summary>
    /// <param name="volume">Referenced local volume.</param>
    /// <param name="targetPath">Absolute target path inside the service.</param>
    /// <param name="readOnly">Whether the mount forbids service writes.</param>
    /// <exception cref="ArgumentException">The volume is default or target path is not absolute.</exception>
    [JsonConstructor]
    public InfrastructureLocalVolumeMount(
        InfrastructureLocalVolumeId volume,
        string targetPath,
        bool readOnly = false)
    {
        if (string.IsNullOrWhiteSpace(volume.Value))
            throw new ArgumentException("A local volume mount requires a volume.", nameof(volume));
        TargetPath = Guard.RequireNotNullOrWhiteSpace(targetPath);
        if (!TargetPath.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("A local volume target path must be absolute.", nameof(targetPath));
        Volume = volume;
        ReadOnly = readOnly;
    }

    /// <summary>Referenced local volume.</summary>
    public InfrastructureLocalVolumeId Volume { get; }

    /// <summary>Absolute target path inside the service.</summary>
    public string TargetPath { get; }

    /// <summary>Whether the mount forbids service writes.</summary>
    public bool ReadOnly { get; }
}

/// <summary>One generated-file mount on a local service.</summary>
public sealed record InfrastructureLocalFileMount
{
    /// <summary>Creates a generated-file mount.</summary>
    /// <param name="file">Generated file identity.</param>
    /// <param name="targetPath">Absolute target path inside the service.</param>
    /// <exception cref="ArgumentException">The identity is default or target path is not absolute.</exception>
    [JsonConstructor]
    public InfrastructureLocalFileMount(InfrastructureLocalFileId file, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(file.Value))
            throw new ArgumentException("A local file mount requires a generated file.", nameof(file));
        TargetPath = Guard.RequireNotNullOrWhiteSpace(targetPath);
        if (!TargetPath.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("A local generated-file target path must be absolute.", nameof(targetPath));
        File = file;
    }

    /// <summary>Generated file identity.</summary>
    public InfrastructureLocalFileId File { get; }

    /// <summary>Absolute target path inside the service.</summary>
    public string TargetPath { get; }
}

/// <summary>Portable health probe for a local service.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InfrastructureLocalHttpHealthProbe), "http")]
[JsonDerivedType(typeof(InfrastructureLocalCommandHealthProbe), "command")]
public abstract record InfrastructureLocalHealthProbe;

/// <summary>HTTP health probe against one service endpoint.</summary>
public sealed record InfrastructureLocalHttpHealthProbe : InfrastructureLocalHealthProbe
{
    /// <summary>Creates an HTTP health probe.</summary>
    /// <param name="endpoint">Endpoint to probe.</param>
    /// <param name="path">Absolute HTTP path.</param>
    /// <param name="expectedStatus">Expected HTTP status code.</param>
    /// <exception cref="ArgumentException">The endpoint is default or path is not absolute.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The status code is invalid.</exception>
    [JsonConstructor]
    public InfrastructureLocalHttpHealthProbe(
        InfrastructureLocalEndpointId endpoint,
        string path,
        int expectedStatus = 200)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Value))
            throw new ArgumentException("An HTTP probe requires an endpoint.", nameof(endpoint));
        Path = Guard.RequireNotNullOrWhiteSpace(path);
        if (!Path.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("An HTTP health path must be absolute.", nameof(path));
        if (expectedStatus is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(expectedStatus), expectedStatus, "An HTTP status must be between 100 and 599.");
        Endpoint = endpoint;
        ExpectedStatus = expectedStatus;
    }

    /// <summary>Endpoint to probe.</summary>
    public InfrastructureLocalEndpointId Endpoint { get; }

    /// <summary>Absolute HTTP path.</summary>
    public string Path { get; }

    /// <summary>Expected HTTP status.</summary>
    public int ExpectedStatus { get; }
}

/// <summary>Command health probe executed inside a local service.</summary>
public sealed record InfrastructureLocalCommandHealthProbe : InfrastructureLocalHealthProbe
{
    /// <summary>Creates a command health probe.</summary>
    /// <param name="executable">Probe executable.</param>
    /// <param name="arguments">Exact argument vector.</param>
    /// <exception cref="ArgumentException">The executable is empty or an argument is <see langword="null"/>.</exception>
    [JsonConstructor]
    public InfrastructureLocalCommandHealthProbe(string executable, ImmutableArray<string> arguments = default)
    {
        Executable = Guard.RequireNotNullOrWhiteSpace(executable);
        Arguments = InfrastructureLocalCollections.Strings(arguments, nameof(arguments));
    }

    /// <summary>Probe executable.</summary>
    public string Executable { get; }

    /// <summary>Exact argument vector.</summary>
    public ImmutableArray<string> Arguments { get; }
}

/// <summary>Complete local health policy used for readiness and lifecycle supervision.</summary>
public sealed record InfrastructureLocalHealthPolicy
{
    /// <summary>Creates a local health policy.</summary>
    /// <param name="probes">Probes that must all succeed.</param>
    /// <param name="interval">Delay between probe attempts.</param>
    /// <param name="timeout">Maximum duration of one attempt.</param>
    /// <param name="retries">Consecutive failures allowed before unhealthy state.</param>
    /// <param name="startPeriod">Optional initialization grace period.</param>
    /// <exception cref="ArgumentException">No probe is supplied or a collection entry is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A duration or retry count is not positive.</exception>
    [JsonConstructor]
    public InfrastructureLocalHealthPolicy(
        ImmutableArray<InfrastructureLocalHealthProbe> probes,
        TimeSpan interval,
        TimeSpan timeout,
        int retries,
        TimeSpan? startPeriod = null)
    {
        if (probes.IsDefaultOrEmpty)
            throw new ArgumentException("A local health policy requires at least one probe.", nameof(probes));
        Probes = InfrastructureLocalCollections.Items(probes, nameof(probes));
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Health interval must be positive.");
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Health timeout must be positive.");
        if (retries <= 0)
            throw new ArgumentOutOfRangeException(nameof(retries), retries, "Health retries must be positive.");
        if (startPeriod.HasValue && startPeriod.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startPeriod), startPeriod, "Health start period must be positive.");

        Interval = interval;
        Timeout = timeout;
        Retries = retries;
        StartPeriod = startPeriod;
    }

    /// <summary>Probes that must all succeed.</summary>
    public ImmutableArray<InfrastructureLocalHealthProbe> Probes { get; }

    /// <summary>Delay between probe attempts.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Maximum duration of one attempt.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Consecutive failures allowed before unhealthy state.</summary>
    public int Retries { get; }

    /// <summary>Optional initialization grace period.</summary>
    public TimeSpan? StartPeriod { get; }
}

/// <summary>Closed construction source for one executable local service.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InfrastructureLocalContainerSource), "container")]
[JsonDerivedType(typeof(InfrastructureLocalProjectSource), "project")]
public abstract record InfrastructureLocalServiceSource;

/// <summary>A local service constructed from one pinned container image.</summary>
public sealed record InfrastructureLocalContainerSource : InfrastructureLocalServiceSource
{
    /// <summary>Creates a container construction source.</summary>
    /// <param name="image">Pinned container image reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="image"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalContainerSource(string image) =>
        Image = Guard.RequireNotNullOrWhiteSpace(image);

    /// <summary>Pinned container image reference.</summary>
    public string Image { get; }
}

/// <summary>A local service constructed from one repository project.</summary>
public sealed record InfrastructureLocalProjectSource : InfrastructureLocalServiceSource
{
    /// <summary>Creates a repository-project construction source.</summary>
    /// <param name="projectPath">Repository-relative project path.</param>
    /// <param name="launchProfile">Optional project launch-profile name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projectPath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="projectPath"/> is empty or white-space, is absolute, escapes the repository, or does not name a project file;
    /// or <paramref name="launchProfile"/> is empty or white-space.
    /// </exception>
    [JsonConstructor]
    public InfrastructureLocalProjectSource(string projectPath, string? launchProfile = null)
    {
        ProjectPath = Guard.RequireNotNullOrWhiteSpace(projectPath).Replace('\\', '/');
        var pathSegments = ProjectPath.Split('/');
        var isWindowsAbsolute = ProjectPath.Length >= 3
            && char.IsAsciiLetter(ProjectPath[0])
            && ProjectPath[1] == ':'
            && ProjectPath[2] == '/';
        if (Path.IsPathRooted(ProjectPath)
            || isWindowsAbsolute
            || pathSegments.Any(static segment => segment.Length == 0 || segment is "." or "..")
            || !ProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A local project source must be a repository-relative .csproj path that does not escape the repository.", nameof(projectPath));
        }
        if (launchProfile is not null && string.IsNullOrWhiteSpace(launchProfile))
            throw new ArgumentException("A local project launch profile cannot be empty or white-space.", nameof(launchProfile));
        LaunchProfile = launchProfile;
    }

    /// <summary>Normalized repository-relative project path.</summary>
    public string ProjectPath { get; }

    /// <summary>Optional project launch-profile name.</summary>
    public string? LaunchProfile { get; }
}

/// <summary>One exact executable service in a local topology.</summary>
public sealed record InfrastructureLocalService
{
    /// <summary>Creates a container-backed local service.</summary>
    /// <param name="resource">Canonical logical resource represented by the service.</param>
    /// <param name="physicalResource">Exact physical identity from the fenced realization.</param>
    /// <param name="image">Pinned container image reference.</param>
    /// <param name="command">Optional command argument vector.</param>
    /// <param name="environment">Environment-variable bindings.</param>
    /// <param name="endpoints">Service endpoints.</param>
    /// <param name="mounts">Volume mounts.</param>
    /// <param name="fileMounts">Generated-file mounts.</param>
    /// <param name="health">Complete health and readiness policy.</param>
    /// <param name="readyDependencies">Services that must become ready first.</param>
    /// <param name="stopGracePeriod">Grace period before forced termination.</param>
    /// <exception cref="ArgumentException">An identity is default or a collection is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stopGracePeriod"/> is negative.</exception>
    public InfrastructureLocalService(
        InfrastructureNodeId resource,
        InfrastructurePhysicalResourceId physicalResource,
        string image,
        ImmutableArray<string> command = default,
        ImmutableArray<InfrastructureLocalEnvironmentVariable> environment = default,
        ImmutableArray<InfrastructureLocalEndpoint> endpoints = default,
        ImmutableArray<InfrastructureLocalVolumeMount> mounts = default,
        ImmutableArray<InfrastructureLocalFileMount> fileMounts = default,
        InfrastructureLocalHealthPolicy? health = null,
        ImmutableArray<InfrastructurePhysicalResourceId> readyDependencies = default,
        TimeSpan? stopGracePeriod = null)
        : this(
            node: resource,
            physicalResource: physicalResource,
            source: new InfrastructureLocalContainerSource(image),
            command: command,
            environment: environment,
            endpoints: endpoints,
            mounts: mounts,
            fileMounts: fileMounts,
            health: health,
            readyDependencies: readyDependencies,
            stopGracePeriod: stopGracePeriod)
    {
    }

    /// <summary>Creates a local service.</summary>
    /// <param name="node">Canonical logical workload or resource represented by the service.</param>
    /// <param name="physicalResource">Exact physical identity from the fenced realization.</param>
    /// <param name="source">Closed construction source for the executable service.</param>
    /// <param name="command">Optional command argument vector.</param>
    /// <param name="environment">Environment-variable bindings.</param>
    /// <param name="endpoints">Service endpoints.</param>
    /// <param name="mounts">Volume mounts.</param>
    /// <param name="fileMounts">Generated-file mounts.</param>
    /// <param name="health">Complete health and readiness policy.</param>
    /// <param name="readyDependencies">Services that must become ready first.</param>
    /// <param name="stopGracePeriod">Grace period before forced termination.</param>
    /// <exception cref="ArgumentException">An identity is default or a collection is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stopGracePeriod"/> is negative.</exception>
    [JsonConstructor]
    public InfrastructureLocalService(
        InfrastructureNodeId node,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureLocalServiceSource source,
        ImmutableArray<string> command = default,
        ImmutableArray<InfrastructureLocalEnvironmentVariable> environment = default,
        ImmutableArray<InfrastructureLocalEndpoint> endpoints = default,
        ImmutableArray<InfrastructureLocalVolumeMount> mounts = default,
        ImmutableArray<InfrastructureLocalFileMount> fileMounts = default,
        InfrastructureLocalHealthPolicy? health = null,
        ImmutableArray<InfrastructurePhysicalResourceId> readyDependencies = default,
        TimeSpan? stopGracePeriod = null)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A local service requires a logical node.", nameof(node));
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("A local service requires a physical resource.", nameof(physicalResource));
        if (stopGracePeriod.HasValue && stopGracePeriod.Value < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stopGracePeriod), stopGracePeriod, "Stop grace period cannot be negative.");

        Node = node;
        PhysicalResource = physicalResource;
        Source = Guard.RequireNotNull(source);
        Command = InfrastructureLocalCollections.Strings(command, nameof(command));
        Environment = InfrastructureLocalCollections.Normalize(environment, static item => item.Name, nameof(environment));
        Endpoints = InfrastructureLocalCollections.Normalize(endpoints, static item => item.Id.Value, nameof(endpoints));
        Mounts = InfrastructureLocalCollections.Normalize(mounts, static item => item.TargetPath, nameof(mounts));
        FileMounts = InfrastructureLocalCollections.Normalize(fileMounts, static item => item.TargetPath, nameof(fileMounts));
        Health = health;
        ReadyDependencies = InfrastructureLocalCollections.Identities(readyDependencies, static item => item.Value, nameof(readyDependencies));
        StopGracePeriod = stopGracePeriod;
    }

    /// <summary>Canonical logical workload or resource represented by the service.</summary>
    public InfrastructureNodeId Node { get; }

    /// <summary>Exact physical identity from the fenced realization.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Closed construction source for the executable service.</summary>
    public InfrastructureLocalServiceSource Source { get; }

    /// <summary>Optional command argument vector.</summary>
    public ImmutableArray<string> Command { get; }

    /// <summary>Environment-variable bindings in variable-name order.</summary>
    public ImmutableArray<InfrastructureLocalEnvironmentVariable> Environment { get; }

    /// <summary>Endpoints in endpoint-identity order.</summary>
    public ImmutableArray<InfrastructureLocalEndpoint> Endpoints { get; }

    /// <summary>Volume mounts in target-path order.</summary>
    public ImmutableArray<InfrastructureLocalVolumeMount> Mounts { get; }

    /// <summary>Generated-file mounts in target-path order.</summary>
    public ImmutableArray<InfrastructureLocalFileMount> FileMounts { get; }

    /// <summary>Complete health and readiness policy, when the service exposes one.</summary>
    public InfrastructureLocalHealthPolicy? Health { get; }

    /// <summary>Physical services that must become ready first.</summary>
    public ImmutableArray<InfrastructurePhysicalResourceId> ReadyDependencies { get; }

    /// <summary>Optional grace period before forced termination.</summary>
    public TimeSpan? StopGracePeriod { get; }
}

/// <summary>One named volume in a local topology.</summary>
public sealed record InfrastructureLocalVolume
{
    /// <summary>Creates a local volume.</summary>
    /// <param name="id">Stable volume identity.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    [JsonConstructor]
    public InfrastructureLocalVolume(InfrastructureLocalVolumeId id)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A local volume requires an identity.", nameof(id));
        Id = id;
    }

    /// <summary>Stable volume identity.</summary>
    public InfrastructureLocalVolumeId Id { get; }
}

/// <summary>One deterministic generated configuration file in a local topology.</summary>
public sealed record InfrastructureLocalFile
{
    /// <summary>Creates a generated local file.</summary>
    /// <param name="id">Stable file identity.</param>
    /// <param name="content">Ordered non-secret literal and reference segments.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentException">Content is empty, contains null, or contains a secret reference.</exception>
    [JsonConstructor]
    public InfrastructureLocalFile(
        InfrastructureLocalFileId id,
        ImmutableArray<InfrastructureLocalValue> content)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A local generated file requires an identity.", nameof(id));
        if (content.IsDefaultOrEmpty)
            throw new ArgumentException("A local generated file requires content.", nameof(content));
        if (content.Any(static item => item is null))
            throw new ArgumentException("Local generated-file content cannot contain null.", nameof(content));
        if (content.Any(static item => item is InfrastructureLocalSecretValue))
            throw new ArgumentException("Local generated files cannot embed secret references.", nameof(content));
        Id = id;
        Content = content;
    }

    /// <summary>Stable file identity.</summary>
    public InfrastructureLocalFileId Id { get; }

    /// <summary>Ordered non-secret literal and reference segments concatenated by lifecycle adapters.</summary>
    public ImmutableArray<InfrastructureLocalValue> Content { get; }
}

/// <summary>One host or service executable operation exposed by a local harness.</summary>
public sealed record InfrastructureLocalOperation
{
    /// <summary>Creates a local operation.</summary>
    /// <param name="id">Stable application-owned operation intent.</param>
    /// <param name="placement">Execution placement.</param>
    /// <param name="effect">Expected state effect.</param>
    /// <param name="executable">Exact executable or repository-relative executable artifact.</param>
    /// <param name="arguments">Exact argument vector.</param>
    /// <param name="requiredServices">Services that must be ready before execution.</param>
    /// <param name="service">Managed-service placement target, when applicable.</param>
    /// <param name="mutationAuthority">Exact lifecycle authority fenced by an environment mutation.</param>
    /// <exception cref="ArgumentException">An identity is default or placement semantics conflict.</exception>
    [JsonConstructor]
    public InfrastructureLocalOperation(
        InfrastructureLocalOperationId id,
        InfrastructureLocalExecutionPlacement placement,
        InfrastructureLocalOperationEffect effect,
        string executable,
        ImmutableArray<string> arguments = default,
        ImmutableArray<InfrastructurePhysicalResourceId> requiredServices = default,
        InfrastructurePhysicalResourceId? service = null,
        InfrastructureLifecycleAuthorityId? mutationAuthority = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A local operation requires an identity.", nameof(id));
        if (!Enum.IsDefined(placement))
            throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unsupported execution placement.");
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unsupported operation effect.");
        if ((placement == InfrastructureLocalExecutionPlacement.ManagedService) != service.HasValue)
            throw new ArgumentException("Only managed-service operations require a service placement target.", nameof(service));
        if ((effect == InfrastructureLocalOperationEffect.EnvironmentMutation) != mutationAuthority.HasValue)
            throw new ArgumentException("Only environment mutations require an exact lifecycle authority fence.", nameof(mutationAuthority));
        if (mutationAuthority.HasValue && string.IsNullOrWhiteSpace(mutationAuthority.Value.Value))
            throw new ArgumentException("An environment mutation requires a non-default lifecycle authority.", nameof(mutationAuthority));

        Id = id;
        Placement = placement;
        Effect = effect;
        Executable = Guard.RequireNotNullOrWhiteSpace(executable);
        Arguments = InfrastructureLocalCollections.Strings(arguments, nameof(arguments));
        RequiredServices = InfrastructureLocalCollections.Identities(requiredServices, static item => item.Value, nameof(requiredServices));
        Service = service;
        MutationAuthority = mutationAuthority;
    }

    /// <summary>Stable application-owned operation intent.</summary>
    public InfrastructureLocalOperationId Id { get; }

    /// <summary>Execution placement.</summary>
    public InfrastructureLocalExecutionPlacement Placement { get; }

    /// <summary>Expected state effect.</summary>
    public InfrastructureLocalOperationEffect Effect { get; }

    /// <summary>Exact executable or repository-relative executable artifact.</summary>
    public string Executable { get; }

    /// <summary>Exact argument vector.</summary>
    public ImmutableArray<string> Arguments { get; }

    /// <summary>Services that must be ready before execution.</summary>
    public ImmutableArray<InfrastructurePhysicalResourceId> RequiredServices { get; }

    /// <summary>Managed-service placement target, when applicable.</summary>
    public InfrastructurePhysicalResourceId? Service { get; }

    /// <summary>Exact lifecycle authority fenced by an environment mutation.</summary>
    public InfrastructureLifecycleAuthorityId? MutationAuthority { get; }
}

/// <summary>Canonical target-neutral construction topology shared by local lifecycle adapters.</summary>
public sealed record InfrastructureLocalTopology
{
    /// <summary>Creates and normalizes a local topology.</summary>
    /// <param name="services">Container-backed services.</param>
    /// <param name="volumes">Named data volumes.</param>
    /// <param name="files">Deterministic generated configuration files.</param>
    /// <param name="operations">Application and environment operations.</param>
    /// <exception cref="ArgumentException">A collection contains null or duplicate identities.</exception>
    [JsonConstructor]
    public InfrastructureLocalTopology(
        ImmutableArray<InfrastructureLocalService> services = default,
        ImmutableArray<InfrastructureLocalVolume> volumes = default,
        ImmutableArray<InfrastructureLocalFile> files = default,
        ImmutableArray<InfrastructureLocalOperation> operations = default)
    {
        Services = InfrastructureLocalCollections.Normalize(services, static item => item.PhysicalResource.Value, nameof(services));
        Volumes = InfrastructureLocalCollections.Normalize(volumes, static item => item.Id.Value, nameof(volumes));
        Files = InfrastructureLocalCollections.Normalize(files, static item => item.Id.Value, nameof(files));
        Operations = InfrastructureLocalCollections.Normalize(operations, static item => item.Id.Value, nameof(operations));
    }

    /// <summary>Services in physical-resource identity order.</summary>
    public ImmutableArray<InfrastructureLocalService> Services { get; }

    /// <summary>Volumes in stable identity order.</summary>
    public ImmutableArray<InfrastructureLocalVolume> Volumes { get; }

    /// <summary>Generated configuration files in stable identity order.</summary>
    public ImmutableArray<InfrastructureLocalFile> Files { get; }

    /// <summary>Operations in stable intent order.</summary>
    public ImmutableArray<InfrastructureLocalOperation> Operations { get; }
}

static class InfrastructureLocalCollections
{
    internal static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values, Func<T, string> key, string parameterName)
        where T : class
    {
        var items = Items(values, parameterName);
        if (items.IsDefaultOrEmpty)
            return [];
        var ordered = items.Sort((left, right) => StringComparer.Ordinal.Compare(key(left), key(right)));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(key(ordered[index - 1]), key(ordered[index]), StringComparison.Ordinal))
                throw new ArgumentException($"Local infrastructure identity '{key(ordered[index])}' is duplicated.", parameterName);
        }
        return ordered;
    }

    internal static ImmutableArray<T> Items<T>(ImmutableArray<T> values, string parameterName)
        where T : class
    {
        if (values.IsDefaultOrEmpty)
            return [];
        if (values.Any(static item => item is null))
            throw new ArgumentException("Local infrastructure collections cannot contain null.", parameterName);
        return values;
    }

    internal static ImmutableArray<string> Strings(ImmutableArray<string> values, string parameterName)
    {
        if (values.IsDefaultOrEmpty)
            return [];
        if (values.Any(static item => item is null))
            throw new ArgumentException("Local infrastructure string collections cannot contain null.", parameterName);
        return values;
    }

    internal static ImmutableArray<T> Identities<T>(ImmutableArray<T> values, Func<T, string> key, string parameterName)
        where T : struct
    {
        if (values.IsDefaultOrEmpty)
            return [];
        var ordered = values.Sort((left, right) => StringComparer.Ordinal.Compare(key(left), key(right)));
        for (var index = 0; index < ordered.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(key(ordered[index])))
                throw new ArgumentException("Local infrastructure identity collections cannot contain default values.", parameterName);
            if (index > 0 && string.Equals(key(ordered[index - 1]), key(ordered[index]), StringComparison.Ordinal))
                throw new ArgumentException($"Local infrastructure identity '{key(ordered[index])}' is duplicated.", parameterName);
        }
        return ordered;
    }
}
