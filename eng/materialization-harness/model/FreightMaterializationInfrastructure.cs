using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.MaterializationHarness.Model;

/// <summary>Canonical infrastructure and local construction fixture for the freight materialization harness.</summary>
public static class FreightMaterializationInfrastructure
{
    /// <summary>Environment-scoped effective configuration subject.</summary>
    public static InfrastructureConfigurationSubject ConfigurationSubject { get; } = new("environment/materialization-harness");

    /// <summary>Lifecycle authority shared by local orchestration adapters.</summary>
    public static InfrastructureLifecycleAuthorityId LifecycleAuthority { get; } = new("local/materialization-harness");

    /// <summary>Logical PostgreSQL resource.</summary>
    public static InfrastructureNodeId PostgresResource { get; } = new("resource/postgres");

    /// <summary>Logical Cosmos resource.</summary>
    public static InfrastructureNodeId CosmosResource { get; } = new("resource/cosmos");

    /// <summary>Logical Elasticsearch resource.</summary>
    public static InfrastructureNodeId ElasticsearchResource { get; } = new("resource/elasticsearch");

    /// <summary>Logical pgAdmin resource.</summary>
    public static InfrastructureNodeId PgAdminResource { get; } = new("resource/pgadmin");

    /// <summary>Logical Kibana resource.</summary>
    public static InfrastructureNodeId KibanaResource { get; } = new("resource/kibana");

    /// <summary>Physical PostgreSQL service identity.</summary>
    public static InfrastructurePhysicalResourceId PostgresService { get; } = new("local/postgres");

    /// <summary>Physical Cosmos service identity.</summary>
    public static InfrastructurePhysicalResourceId CosmosService { get; } = new("local/cosmos");

    /// <summary>Physical Elasticsearch service identity.</summary>
    public static InfrastructurePhysicalResourceId ElasticsearchService { get; } = new("local/elasticsearch");

    /// <summary>Physical pgAdmin service identity.</summary>
    public static InfrastructurePhysicalResourceId PgAdminService { get; } = new("local/pgadmin");

    /// <summary>Physical Kibana service identity.</summary>
    public static InfrastructurePhysicalResourceId KibanaService { get; } = new("local/kibana");

    /// <summary>Interactive profile preserving state across ordinary stops.</summary>
    public static InfrastructureLocalEnvironmentProfile InteractiveProfile { get; } = new(
        id: new("materialization-harness/interactive/v1"),
        authority: LifecycleAuthority,
        configurationSubject: ConfigurationSubject,
        projectNameSetting: Settings.ProjectName,
        dataLifetime: InfrastructureLocalDataLifetime.Persistent,
        isolation: InfrastructureLocalEnvironmentIsolation.Shared);

    /// <summary>Isolated test profile whose managed state is disposable and time-bounded.</summary>
    public static InfrastructureLocalEnvironmentProfile IsolatedTestProfile { get; } = new(
        id: new("materialization-harness/isolated-test/v1"),
        authority: LifecycleAuthority,
        configurationSubject: ConfigurationSubject,
        projectNameSetting: Settings.ProjectName,
        dataLifetime: InfrastructureLocalDataLifetime.Ephemeral,
        isolation: InfrastructureLocalEnvironmentIsolation.Isolated,
        maximumLifetime: TimeSpan.FromMinutes(30));

    /// <summary>Stable setting identities owned by the fixture.</summary>
    public static class Settings
    {
        /// <summary>Isolated local project name.</summary>
        public static InfrastructureSettingId ProjectName { get; } = new("project-name");
        /// <summary>PostgreSQL host port.</summary>
        public static InfrastructureSettingId PostgresPort { get; } = new("postgres-port");
        /// <summary>PostgreSQL database.</summary>
        public static InfrastructureSettingId PostgresDatabase { get; } = new("postgres-database");
        /// <summary>PostgreSQL user.</summary>
        public static InfrastructureSettingId PostgresUser { get; } = new("postgres-user");
        /// <summary>Cosmos data-plane host port.</summary>
        public static InfrastructureSettingId CosmosPort { get; } = new("cosmos-port");
        /// <summary>Cosmos health host port.</summary>
        public static InfrastructureSettingId CosmosHealthPort { get; } = new("cosmos-health-port");
        /// <summary>Cosmos Data Explorer host port.</summary>
        public static InfrastructureSettingId CosmosExplorerPort { get; } = new("cosmos-explorer-port");
        /// <summary>Elasticsearch host port.</summary>
        public static InfrastructureSettingId ElasticsearchPort { get; } = new("elasticsearch-port");
        /// <summary>Elasticsearch JVM options.</summary>
        public static InfrastructureSettingId ElasticsearchJavaOptions { get; } = new("elasticsearch-java-options");
        /// <summary>Kibana host port.</summary>
        public static InfrastructureSettingId KibanaPort { get; } = new("kibana-port");
        /// <summary>pgAdmin host port.</summary>
        public static InfrastructureSettingId PgAdminPort { get; } = new("pgadmin-port");
        /// <summary>pgAdmin login email.</summary>
        public static InfrastructureSettingId PgAdminEmail { get; } = new("pgadmin-email");
    }

    /// <summary>Creates the canonical portable infrastructure definition.</summary>
    /// <returns>Exact current-version infrastructure definition document.</returns>
    public static InfrastructureDefinitionDocument CreateDefinition() => InfrastructureDefinitionDocument.FromDefinition(new(
        id: new("materialization-harness"),
        revision: new("local-infrastructure-v1"),
        resources:
        [
            new(PostgresResource, InfrastructureResourceLifecycle.Persistent),
            new(CosmosResource, InfrastructureResourceLifecycle.Persistent),
            new(ElasticsearchResource, InfrastructureResourceLifecycle.Persistent),
            new(PgAdminResource, InfrastructureResourceLifecycle.Ephemeral),
            new(KibanaResource, InfrastructureResourceLifecycle.Ephemeral)
        ]));

    /// <summary>Creates the exact capability-qualified physical realization used by local adapters.</summary>
    /// <returns>Capability-witness-complete realization for every harness service.</returns>
    public static InfrastructureRealization CreatePhysicalRealization()
    {
        var definition = CreateDefinition();
        InfrastructureCapabilityVariantId variant = new("local-containers");
        var capabilityProfile = new InfrastructureCapabilityProfile(
            schemaVersion: InfrastructureCapabilityProfile.CurrentSchemaVersion,
            id: new("materialization-harness/local-containers/v1"),
            target: new("local-orchestration"),
            supportedDefinitionSchemaVersions: [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            variants: [new(variant)]);
        var closure = InfrastructureCapabilityCompiler.Compile(definition, capabilityProfile, variant);
        var lifecycle = new InfrastructureLifecyclePlan(
            definition: definition,
            bindings:
            [
                Binding(PostgresResource, PostgresService),
                Binding(CosmosResource, CosmosService),
                Binding(ElasticsearchResource, ElasticsearchService),
                Binding(PgAdminResource, PgAdminService),
                Binding(KibanaResource, KibanaService)
            ]);
        return InfrastructureRealizationCompiler.Compile(closure, lifecycle);
    }

    /// <summary>Creates the canonical local topology shared by Compose and Aspire interpretations.</summary>
    /// <returns>Normalized services, endpoints, volumes, health checks, readiness, and operations.</returns>
    public static InfrastructureLocalTopology CreateTopology() => InfrastructureLocal.Define(local => local
        .Volume(new("postgres-data"))
        .Volume(new("cosmos-data"))
        .Volume(new("elasticsearch-data"))
        .Volume(new("pgadmin-data"))
        .File(new("pgadmin-servers"), PgAdminServers())
        .Service(resource: PostgresResource, physicalResource: PostgresService, image: "postgres:17.10-alpine3.24", configure: postgres => postgres
            .Environment("POSTGRES_DB", Configuration(Settings.PostgresDatabase))
            .Environment("POSTGRES_USER", Configuration(Settings.PostgresUser))
            .Environment("POSTGRES_PASSWORD", new InfrastructureLocalSecretValue("COHESIVE_HARNESS_POSTGRES_PASSWORD"))
            .CommandArgument("postgres")
            .CommandArgument("-c").CommandArgument("wal_level=logical")
            .CommandArgument("-c").CommandArgument("max_replication_slots=20")
            .CommandArgument("-c").CommandArgument("max_wal_senders=20")
            .CommandArgument("-c").CommandArgument("wal_sender_timeout=1s")
            .Endpoint(id: new("postgres"), scheme: "postgresql", containerPort: 5432, exposure: InfrastructureLocalEndpointExposure.HostLoopback, role: InfrastructureLocalEndpointRole.Data, hostPort: Configuration(Settings.PostgresPort))
            .Mount(volume: new("postgres-data"), targetPath: "/var/lib/postgresql/data")
            .CommandHealth(executable: "pg_isready", arguments: ["--dbname=$POSTGRES_DB", "--username=$POSTGRES_USER"])
            .HealthTiming(interval: TimeSpan.FromSeconds(2), timeout: TimeSpan.FromSeconds(3), retries: 30)
            .StopGrace(TimeSpan.FromSeconds(30)))
        .Service(resource: CosmosResource, physicalResource: CosmosService, image: "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260810", configure: cosmos => cosmos
            .Environment("PROTOCOL", new InfrastructureLocalLiteralValue("https"))
            .Environment("PORT", Configuration(Settings.CosmosPort))
            .Environment("ENABLE_EXPLORER", new InfrastructureLocalLiteralValue("true"))
            .Environment("EXPLORER_PROTOCOL", new InfrastructureLocalLiteralValue("http"))
            .Environment("GATEWAY_PUBLIC_ENDPOINT", new InfrastructureLocalEndpointValue(service: CosmosService, endpoint: new("cosmos"), address: InfrastructureLocalEndpointAddress.HostLoopback))
            .Endpoint(id: new("cosmos"), scheme: "https", containerPort: Configuration(Settings.CosmosPort), exposure: InfrastructureLocalEndpointExposure.HostLoopback, role: InfrastructureLocalEndpointRole.Data, hostPort: Configuration(Settings.CosmosPort))
            .Endpoint(id: new("health"), scheme: "http", containerPort: 8080, exposure: InfrastructureLocalEndpointExposure.HostLoopback, role: InfrastructureLocalEndpointRole.Management, hostPort: Configuration(Settings.CosmosHealthPort))
            .Endpoint(id: new("explorer"), scheme: "http", containerPort: 1234, exposure: InfrastructureLocalEndpointExposure.HostLoopback, role: InfrastructureLocalEndpointRole.UserInterface, hostPort: Configuration(Settings.CosmosExplorerPort))
            .Mount(volume: new("cosmos-data"), targetPath: "/data")
            .HttpHealth(endpoint: new("health"), path: "/ready")
            .HttpHealth(endpoint: new("explorer"), path: "/")
            .HealthTiming(interval: TimeSpan.FromSeconds(3), timeout: TimeSpan.FromSeconds(5), retries: 60, startPeriod: TimeSpan.FromSeconds(20))
            .StopGrace(TimeSpan.FromSeconds(30)))
        .Service(resource: ElasticsearchResource, physicalResource: ElasticsearchService, image: "docker.elastic.co/elasticsearch/elasticsearch:8.19.13", configure: elastic => elastic
            .Environment("discovery.type", new InfrastructureLocalLiteralValue("single-node"))
            .Environment("xpack.security.enabled", new InfrastructureLocalLiteralValue("false"))
            .Environment("xpack.license.self_generated.type", new InfrastructureLocalLiteralValue("basic"))
            .Environment("ES_JAVA_OPTS", Configuration(Settings.ElasticsearchJavaOptions))
            .Endpoint(id: new("http"), scheme: "http", containerPort: 9200, exposure: InfrastructureLocalEndpointExposure.HostLoopback, role: InfrastructureLocalEndpointRole.Data, hostPort: Configuration(Settings.ElasticsearchPort))
            .Mount(volume: new("elasticsearch-data"), targetPath: "/usr/share/elasticsearch/data")
            .HttpHealth(endpoint: new("http"), path: "/_cluster/health?wait_for_status=yellow&timeout=2s")
            .HealthTiming(interval: TimeSpan.FromSeconds(3), timeout: TimeSpan.FromSeconds(5), retries: 60, startPeriod: TimeSpan.FromSeconds(20))
            .StopGrace(TimeSpan.FromSeconds(30)))
        .Service(resource: PgAdminResource, physicalResource: PgAdminService, image: "dpage/pgadmin4:9.17", configure: pgadmin => pgadmin
            .Environment("PGADMIN_DEFAULT_EMAIL", Configuration(Settings.PgAdminEmail))
            .Environment("PGADMIN_DEFAULT_PASSWORD", new InfrastructureLocalSecretValue("COHESIVE_HARNESS_PGADMIN_PASSWORD"))
            .Environment("PGADMIN_DISABLE_POSTFIX", new InfrastructureLocalLiteralValue("true"))
            .Environment("PGADMIN_REPLACE_SERVERS_ON_STARTUP", new InfrastructureLocalLiteralValue("True"))
            .Environment("PGADMIN_CONFIG_MASTER_PASSWORD_REQUIRED", new InfrastructureLocalLiteralValue("False"))
            .Environment("PGADMIN_CONFIG_UPGRADE_CHECK_ENABLED", new InfrastructureLocalLiteralValue("False"))
            .Environment("COHESIVE_HARNESS_POSTGRES_PASSWORD", new InfrastructureLocalSecretValue("COHESIVE_HARNESS_POSTGRES_PASSWORD"))
            .Endpoint(id: new("ui"), scheme: "http", containerPort: 80, exposure: InfrastructureLocalEndpointExposure.HostLoopback, role: InfrastructureLocalEndpointRole.UserInterface, hostPort: Configuration(Settings.PgAdminPort))
            .Mount(volume: new("pgadmin-data"), targetPath: "/var/lib/pgadmin")
            .FileMount(file: new("pgadmin-servers"), targetPath: "/pgadmin4/servers.json")
            .HttpHealth(endpoint: new("ui"), path: "/misc/ping")
            .HealthTiming(interval: TimeSpan.FromSeconds(5), timeout: TimeSpan.FromSeconds(5), retries: 60, startPeriod: TimeSpan.FromSeconds(20))
            .DependsOn(PostgresService)
            .StopGrace(TimeSpan.FromSeconds(30)))
        .Service(resource: KibanaResource, physicalResource: KibanaService, image: "docker.elastic.co/kibana/kibana:8.19.13", configure: kibana => kibana
            .Environment("ELASTICSEARCH_HOSTS", new InfrastructureLocalEndpointValue(service: ElasticsearchService, endpoint: new("http"), address: InfrastructureLocalEndpointAddress.ServiceNetwork, format: InfrastructureLocalEndpointValueFormat.JsonUriArray))
            .Environment("SERVER_HOST", new InfrastructureLocalLiteralValue("0.0.0.0"))
            .Environment("XPACK_SECURITY_ENABLED", new InfrastructureLocalLiteralValue("false"))
            .Environment("TELEMETRY_ENABLED", new InfrastructureLocalLiteralValue("false"))
            .Endpoint(id: new("ui"), scheme: "http", containerPort: 5601, exposure: InfrastructureLocalEndpointExposure.HostLoopback, role: InfrastructureLocalEndpointRole.UserInterface, hostPort: Configuration(Settings.KibanaPort))
            .HttpHealth(endpoint: new("ui"), path: "/api/status")
            .HealthTiming(interval: TimeSpan.FromSeconds(5), timeout: TimeSpan.FromSeconds(5), retries: 60, startPeriod: TimeSpan.FromSeconds(30))
            .DependsOn(ElasticsearchService)
            .StopGrace(TimeSpan.FromSeconds(30)))
        .Operation(id: new("start"), placement: InfrastructureLocalExecutionPlacement.Host, effect: InfrastructureLocalOperationEffect.EnvironmentMutation, executable: HarnessScript, arguments: ["up"], mutationAuthority: LifecycleAuthority)
        .Operation(id: new("stop"), placement: InfrastructureLocalExecutionPlacement.Host, effect: InfrastructureLocalOperationEffect.EnvironmentMutation, executable: HarnessScript, arguments: ["down"], mutationAuthority: LifecycleAuthority)
        .Operation(id: new("reset"), placement: InfrastructureLocalExecutionPlacement.Host, effect: InfrastructureLocalOperationEffect.EnvironmentMutation, executable: HarnessScript, arguments: ["reset"], mutationAuthority: LifecycleAuthority)
        .Operation(id: new("status"), placement: InfrastructureLocalExecutionPlacement.Host, effect: InfrastructureLocalOperationEffect.ReadOnly, executable: HarnessScript, arguments: ["status"])
        .Operation(id: new("seed"), placement: InfrastructureLocalExecutionPlacement.Host, effect: InfrastructureLocalOperationEffect.ApplicationMutation, executable: HarnessScript, arguments: ["seed"], requiredServices: [PostgresService, CosmosService])
        .Operation(id: new("materialize"), placement: InfrastructureLocalExecutionPlacement.Host, effect: InfrastructureLocalOperationEffect.ApplicationMutation, executable: HarnessScript, arguments: ["materialize"], requiredServices: [PostgresService, CosmosService, ElasticsearchService])
        .Operation(id: new("verify"), placement: InfrastructureLocalExecutionPlacement.Host, effect: InfrastructureLocalOperationEffect.ReadOnly, executable: HarnessScript, arguments: ["verify"], requiredServices: [PostgresService, CosmosService])
        .Operation(id: new("inspect"), placement: InfrastructureLocalExecutionPlacement.Host, effect: InfrastructureLocalOperationEffect.ReadOnly, executable: HarnessScript, arguments: ["process-inspect"], requiredServices: [PostgresService, CosmosService, ElasticsearchService]));

    /// <summary>Creates framework defaults matching the checked-in local harness contract.</summary>
    /// <returns>Deterministic default configuration candidates.</returns>
    public static InfrastructureConventionProfile CreateDefaultConfiguration() => new(
        id: new("materialization-harness/local-defaults/v1"),
        candidates:
        [
            Default(Settings.ProjectName, "cohesive-materialization-local"),
            Default(Settings.PostgresPort, "55432"),
            Default(Settings.PostgresDatabase, "cohesive_materialization"),
            Default(Settings.PostgresUser, "cohesive"),
            Default(Settings.CosmosPort, "58081"),
            Default(Settings.CosmosHealthPort, "58080"),
            Default(Settings.CosmosExplorerPort, "58082"),
            Default(Settings.ElasticsearchPort, "59200"),
            Default(Settings.ElasticsearchJavaOptions, "-Xms512m -Xmx512m"),
            Default(Settings.KibanaPort, "55601"),
            Default(Settings.PgAdminPort, "55050"),
            Default(Settings.PgAdminEmail, "harness@cohesivesystems.com")
        ]);

    /// <summary>Creates the required scoped namespace configuration for one isolated test environment.</summary>
    /// <param name="projectName">Unique deterministic project name assigned by the test runner or worktree.</param>
    /// <returns>A scoped-profile project-name candidate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projectName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="projectName"/> is empty or white-space.</exception>
    public static InfrastructureConventionProfile CreateIsolatedProjectConfiguration(string projectName) => new(
        id: new($"materialization-harness/isolated-project/{projectName}/v1"),
        candidates:
        [
            new(
                subject: ConfigurationSubject,
                setting: Settings.ProjectName,
                value: projectName,
                origin: EffectiveConfigurationOrigin.ScopedProfile,
                authority: $"materialization-harness/isolated-project/{projectName}/v1")
        ]);

    /// <summary>Creates explicit runtime configuration from the harness environment-variable contract.</summary>
    /// <param name="readEnvironmentVariable">Optional environment lookup; defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</param>
    /// <returns>Explicit configuration candidates for every harness runtime setting.</returns>
    /// <exception cref="ArgumentException">A required environment variable is absent, empty, or white-space.</exception>
    public static InfrastructureConventionProfile CreateRuntimeConfiguration(
        Func<string, string?>? readEnvironmentVariable = null)
    {
        const string authority = "materialization-harness/runtime-environment/v1";
        readEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        (InfrastructureSettingId Setting, string EnvironmentVariable)[] bindings =
        [
            (Settings.ProjectName, "COHESIVE_HARNESS_PROJECT_NAME"),
            (Settings.PostgresPort, "COHESIVE_HARNESS_POSTGRES_PORT"),
            (Settings.PostgresDatabase, "COHESIVE_HARNESS_POSTGRES_DATABASE"),
            (Settings.PostgresUser, "COHESIVE_HARNESS_POSTGRES_USER"),
            (Settings.CosmosPort, "COHESIVE_HARNESS_COSMOS_PORT"),
            (Settings.CosmosHealthPort, "COHESIVE_HARNESS_COSMOS_HEALTH_PORT"),
            (Settings.CosmosExplorerPort, "COHESIVE_HARNESS_COSMOS_EXPLORER_PORT"),
            (Settings.ElasticsearchPort, "COHESIVE_HARNESS_ELASTIC_PORT"),
            (Settings.ElasticsearchJavaOptions, "COHESIVE_HARNESS_ELASTIC_JAVA_OPTS"),
            (Settings.KibanaPort, "COHESIVE_HARNESS_KIBANA_PORT"),
            (Settings.PgAdminPort, "COHESIVE_HARNESS_PGADMIN_PORT"),
            (Settings.PgAdminEmail, "COHESIVE_HARNESS_PGADMIN_EMAIL")
        ];
        var candidates = ImmutableArray.CreateBuilder<InfrastructureConfigurationCandidate>(bindings.Length);
        foreach (var binding in bindings)
        {
            var value = readEnvironmentVariable(binding.EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Runtime configuration requires environment variable '{binding.EnvironmentVariable}'.", nameof(readEnvironmentVariable));
            candidates.Add(new(
                subject: ConfigurationSubject,
                setting: binding.Setting,
                value: value,
                origin: EffectiveConfigurationOrigin.Explicit,
                authority: authority));
        }
        return new(
            id: new(authority),
            candidates: candidates.MoveToImmutable());
    }

    /// <summary>Compiles the canonical fixture for one local environment policy.</summary>
    /// <param name="environment">Interactive or isolated-test environment policy.</param>
    /// <param name="additionalConfiguration">Optional higher-authority configuration profiles.</param>
    /// <returns>Exact validated local realization shared by lifecycle adapters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is <see langword="null"/>.</exception>
    public static InfrastructureLocalRealizationDocument CreateLocalRealization(
        InfrastructureLocalEnvironmentProfile environment,
        params InfrastructureConventionProfile[] additionalConfiguration) =>
        InfrastructureLocalRealizationCompiler.Compile(
            realization: CreatePhysicalRealization(),
            environment: environment,
            topology: CreateTopology(),
            configurationProfiles: [CreateDefaultConfiguration(), .. additionalConfiguration]);

    const string HarnessScript = "eng/materialization-harness/harness.sh";

    static ImmutableArray<InfrastructureLocalValue> PgAdminServers() =>
    [
        new InfrastructureLocalLiteralValue("""
            {
              "Servers": {
                "1": {
                  "Name": "Cohesive materialization harness",
                  "Group": "Cohesive",
                  "Host": "postgres",
                  "Port": 5432,
                  "MaintenanceDB": "
            """),
        Configuration(Settings.PostgresDatabase),
        new InfrastructureLocalLiteralValue("""
            ",
                  "Username": "
            """),
        Configuration(Settings.PostgresUser),
        new InfrastructureLocalLiteralValue("""
            ",
                  "PasswordExecCommand": "printenv COHESIVE_HARNESS_POSTGRES_PASSWORD",
                  "PasswordExecExpiration": 3600,
                  "ConnectionParameters": { "sslmode": "prefer", "connect_timeout": 10 }
                }
              }
            }
            """)
    ];

    static InfrastructureResourceLifecycleBinding Binding(
        InfrastructureNodeId resource,
        InfrastructurePhysicalResourceId physicalResource) => new(
        resource: resource,
        physicalResource: physicalResource,
        interpreter: new("local-orchestration"),
        authority: LifecycleAuthority,
        disposition: InfrastructureLifecycleDisposition.Managed);

    static InfrastructureLocalConfigurationValue Configuration(InfrastructureSettingId setting) =>
        new(ConfigurationSubject, setting);

    static InfrastructureConfigurationCandidate Default(InfrastructureSettingId setting, string value) => new(
        subject: ConfigurationSubject,
        setting: setting,
        value: value,
        origin: EffectiveConfigurationOrigin.FrameworkDefault,
        authority: "materialization-harness/local-defaults/v1");
}
