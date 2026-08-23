using System.Text.Json;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureLocalRealizationTests
{
    static readonly JsonSerializerOptions JsonOptions = StrictDocumentJson.CreateOptions();
    static readonly InfrastructureConfigurationSubject Subject = new("environment/materialization");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("local/materialization");
    static readonly InfrastructureSettingId ProjectName = new("project-name");
    static readonly InfrastructureSettingId PostgresPort = new("postgres-port");

    [Fact]
    public void Fluent_and_direct_topologies_compile_to_the_same_exact_document()
    {
        var realization = Realization();
        var profile = Environment(InfrastructureLocalDataLifetime.Persistent);
        var direct = DirectTopology();
        var fluent = InfrastructureLocal.Define(local => local
            .Volume(new("postgres-data"))
            .Service(new("resource/postgres"), new("physical/postgres"), "postgres:17.10-alpine3.24", postgres => postgres
                .Environment("POSTGRES_PASSWORD", new InfrastructureLocalSecretValue("POSTGRES_PASSWORD"))
                .Endpoint(
                    id: new("postgres"),
                    scheme: "postgresql",
                    containerPort: 5432,
                    exposure: InfrastructureLocalEndpointExposure.HostLoopback,
                    role: InfrastructureLocalEndpointRole.Data,
                    hostPort: new(Subject, PostgresPort))
                .Mount(new("postgres-data"), "/var/lib/postgresql/data")
                .CommandHealth("pg_isready", ["-U", "postgres"])
                .HealthTiming(interval: TimeSpan.FromSeconds(2), timeout: TimeSpan.FromSeconds(3), retries: 30)
                .StopGrace(TimeSpan.FromSeconds(30)))
            .Operation(
                id: new("verify"),
                placement: InfrastructureLocalExecutionPlacement.Host,
                effect: InfrastructureLocalOperationEffect.ReadOnly,
                executable: "eng/materialization-harness/harness.sh",
                arguments: ["verify"],
                requiredServices: [new("physical/postgres")]));
        var configuration = Configuration((ProjectName, "materialization"), (PostgresPort, "55432"));

        var first = InfrastructureLocalRealizationCompiler.Compile(realization, profile, direct, [configuration]);
        var second = InfrastructureLocalRealizationCompiler.Compile(realization, profile, fluent, [configuration]);
        var roundTrip = JsonSerializer.Deserialize<InfrastructureLocalRealizationDocument>(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonOptions);

        Assert.True(first.IsValid);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Fingerprint, roundTrip?.Fingerprint);
        Assert.Equal(realization.ToReference(), first.Realization);
        Assert.Equal(EffectiveConfigurationOrigin.ScopedProfile, first.Configuration.Configuration[0].Attribution.Origin);
    }

    [Fact]
    public void Environment_policy_is_part_of_the_exact_fingerprint()
    {
        var realization = Realization();
        var topology = DirectTopology();
        var configuration = Configuration((ProjectName, "materialization"), (PostgresPort, "55432"));

        var interactive = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(InfrastructureLocalDataLifetime.Persistent),
            topology,
            [configuration]);
        var isolated = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(InfrastructureLocalDataLifetime.Ephemeral, TimeSpan.FromMinutes(20)),
            topology,
            [configuration]);

        Assert.NotEqual(interactive.Fingerprint, isolated.Fingerprint);
        Assert.True(interactive.IsValid);
        Assert.True(isolated.IsValid);
    }

    [Fact]
    public void Missing_configuration_unpinned_images_and_port_collisions_are_diagnostics()
    {
        var realization = Realization(includeElastic: true);
        var topology = new InfrastructureLocalTopology(
            services:
            [
                DirectPostgres("postgres:latest"),
                new(
                    resource: new("resource/elastic"),
                    physicalResource: new("physical/elastic"),
                    image: "elasticsearch",
                    endpoints:
                    [
                        new(
                            id: new("http"),
                            scheme: "http",
                            containerPort: 9200,
                            exposure: InfrastructureLocalEndpointExposure.HostLoopback,
                            role: InfrastructureLocalEndpointRole.Data,
                            hostPort: new(Subject, new("elastic-port")))
                    ])
            ],
            volumes: [new(new("postgres-data"))]);
        var configuration = Configuration(
            (ProjectName, "materialization"),
            (PostgresPort, "55432"),
            (new("elastic-port"), "55432"));

        var document = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(InfrastructureLocalDataLifetime.Persistent),
            topology,
            [configuration]);

        Assert.False(document.IsValid);
        Assert.Equal(2, document.Diagnostics.Count(diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.ImageNotPinned));
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.HostPortInvalid);
    }

    [Fact]
    public void Readiness_cycles_and_cross_environment_mutations_fail_closed()
    {
        var realization = Realization(includeElastic: true);
        var topology = new InfrastructureLocalTopology(
            services:
            [
                new(
                    resource: new("resource/postgres"),
                    physicalResource: new("physical/postgres"),
                    image: "postgres:17.10",
                    readyDependencies: [new("physical/elastic")]),
                new(
                    resource: new("resource/elastic"),
                    physicalResource: new("physical/elastic"),
                    image: "elasticsearch:8.19.13",
                    readyDependencies: [new("physical/postgres")])
            ],
            operations:
            [
                new(
                    id: new("reset"),
                    placement: InfrastructureLocalExecutionPlacement.Host,
                    effect: InfrastructureLocalOperationEffect.EnvironmentMutation,
                    executable: "harness.sh",
                    mutationAuthority: new("local/other"))
            ]);

        var document = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(InfrastructureLocalDataLifetime.Ephemeral),
            topology,
            [Configuration((ProjectName, "materialization"))]);

        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.ReadinessCycle);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.DependencyHealthMissing);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.MutationAuthorityMismatch);
    }

    [Fact]
    public void Fluent_health_authoring_requires_both_probes_and_timing()
    {
        Assert.Throws<InvalidOperationException>(() => InfrastructureLocal.Define(local => local
            .Service(new("resource/postgres"), new("physical/postgres"), "postgres:17.10", postgres => postgres
                .CommandHealth(executable: "pg_isready"))));
        Assert.Throws<InvalidOperationException>(() => InfrastructureLocal.Define(local => local
            .Service(new("resource/postgres"), new("physical/postgres"), "postgres:17.10", postgres => postgres
                .HealthTiming(
                    interval: TimeSpan.FromSeconds(2),
                    timeout: TimeSpan.FromSeconds(3),
                    retries: 30))));
    }

    static InfrastructureLocalTopology DirectTopology() => new(
        services: [DirectPostgres("postgres:17.10-alpine3.24")],
        volumes: [new(new("postgres-data"))],
        operations:
        [
            new(
                id: new("verify"),
                placement: InfrastructureLocalExecutionPlacement.Host,
                effect: InfrastructureLocalOperationEffect.ReadOnly,
                executable: "eng/materialization-harness/harness.sh",
                arguments: ["verify"],
                requiredServices: [new("physical/postgres")])
        ]);

    static InfrastructureLocalService DirectPostgres(string image) => new(
        resource: new("resource/postgres"),
        physicalResource: new("physical/postgres"),
        image: image,
        environment: [new("POSTGRES_PASSWORD", new InfrastructureLocalSecretValue("POSTGRES_PASSWORD"))],
        endpoints:
        [
            new(
                id: new("postgres"),
                scheme: "postgresql",
                containerPort: 5432,
                exposure: InfrastructureLocalEndpointExposure.HostLoopback,
                role: InfrastructureLocalEndpointRole.Data,
                hostPort: new(Subject, PostgresPort))
        ],
        mounts: [new(new("postgres-data"), "/var/lib/postgresql/data")],
        health: new(
            probes: [new InfrastructureLocalCommandHealthProbe("pg_isready", ["-U", "postgres"])],
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromSeconds(3),
            retries: 30),
        stopGracePeriod: TimeSpan.FromSeconds(30));

    static InfrastructureLocalEnvironmentProfile Environment(
        InfrastructureLocalDataLifetime lifetime,
        TimeSpan? maximumLifetime = null) => new(
        id: new(lifetime == InfrastructureLocalDataLifetime.Persistent ? "interactive/v1" : "isolated-test/v1"),
        authority: Authority,
        configurationSubject: Subject,
        projectNameSetting: ProjectName,
        dataLifetime: lifetime,
        isolation: lifetime == InfrastructureLocalDataLifetime.Persistent
            ? InfrastructureLocalEnvironmentIsolation.Shared
            : InfrastructureLocalEnvironmentIsolation.Isolated,
        maximumLifetime: maximumLifetime);

    static InfrastructureConventionProfile Configuration(params (InfrastructureSettingId Setting, string Value)[] values) => new(
        id: new("materialization/config/v1"),
        candidates:
        [
            .. values.Select(value => new InfrastructureConfigurationCandidate(
                subject: Subject,
                setting: value.Setting,
                value: value.Value,
                origin: EffectiveConfigurationOrigin.ScopedProfile,
                authority: "materialization/config/v1"))
        ]);

    static InfrastructureRealization Realization(bool includeElastic = false)
    {
        InfrastructureResourceDefinition[] resources = includeElastic
            ?
            [
                new(new("resource/postgres"), InfrastructureResourceLifecycle.Persistent),
                new(new("resource/elastic"), InfrastructureResourceLifecycle.Persistent)
            ]
            : [new(new("resource/postgres"), InfrastructureResourceLifecycle.Persistent)];
        var definition = InfrastructureDefinitionDocument.FromDefinition(new(
            id: new("local-realization-tests"),
            revision: new("v1"),
            resources: [.. resources]));
        InfrastructureCapabilityVariantId variant = new("local");
        var profile = new InfrastructureCapabilityProfile(
            schemaVersion: InfrastructureCapabilityProfile.CurrentSchemaVersion,
            id: new("profiles/local-tests/v1"),
            target: new("local-orchestration"),
            supportedDefinitionSchemaVersions: [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            variants: [new(variant)]);
        var closure = InfrastructureCapabilityCompiler.Compile(definition, profile, variant);
        var lifecycle = new InfrastructureLifecyclePlan(
            definition: definition,
            bindings:
            [
                .. resources.Select(resource => new InfrastructureResourceLifecycleBinding(
                    resource: resource.Id,
                    physicalResource: new($"physical/{resource.Id.Value["resource/".Length..]}"),
                    interpreter: new("local-orchestration"),
                    authority: Authority,
                    disposition: InfrastructureLifecycleDisposition.Managed))
            ]);
        return InfrastructureRealizationCompiler.Compile(closure, lifecycle);
    }
}
