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
    static readonly InfrastructureSettingId ElasticContainerPort = new("elastic-container-port");

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
                            containerPort: new InfrastructureLocalConfigurationValue(Subject, ElasticContainerPort),
                            exposure: InfrastructureLocalEndpointExposure.HostLoopback,
                            role: InfrastructureLocalEndpointRole.Data,
                            hostPort: new(Subject, new("elastic-port")))
                    ])
            ],
            volumes: [new(new("postgres-data"))]);
        var configuration = Configuration(
            (ProjectName, "materialization"),
            (PostgresPort, "55432"),
            (ElasticContainerPort, "70000"),
            (new("elastic-port"), "55432"));

        var document = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(InfrastructureLocalDataLifetime.Persistent),
            topology,
            [configuration]);

        Assert.False(document.IsValid);
        Assert.Equal(2, document.Diagnostics.Count(diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.ImageNotPinned));
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.ContainerPortInvalid);
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

    [Fact]
    public void Repository_project_workload_uses_exact_placement_and_round_trips()
    {
        var projectSource = new InfrastructureLocalProjectSource(
            new("ari/training-api"),
            new("src/Ari.Training.Api/Ari.Training.Api.csproj"),
            "https");
        var realization = WorkloadRealization(projectSource.Reference);
        var directProjectTopology = new InfrastructureLocalTopology(
            services:
            [
                new(
                    node: new("workload/api"),
                    physicalResource: new("physical/api"),
                    source: projectSource)
            ]);
        var fluentProjectTopology = InfrastructureLocal.Define(local => local.ProjectService(
            workload: new("workload/api"),
            physicalResource: new("physical/api"),
            project: projectSource));
        var projectConfiguration = Configuration((ProjectName, "ari-local"));
        var directProject = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(InfrastructureLocalDataLifetime.Persistent),
            directProjectTopology,
            [projectConfiguration]);
        var fluentProject = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(InfrastructureLocalDataLifetime.Persistent),
            fluentProjectTopology,
            [projectConfiguration]);

        Assert.True(directProject.IsValid);
        Assert.Equal(directProject.Fingerprint, fluentProject.Fingerprint);

        var readinessRealization = WorkloadRealization(projectSource.Reference, includeReadiness: true);
        var topology = InfrastructureLocal.Define(local => local
            .Service(new("resource/scheduler"), new("physical/scheduler"), "mcr.microsoft.com/dts/dts-emulator@sha256:abc", scheduler => scheduler
                .Endpoint(
                    id: new("dashboard"),
                    scheme: "http",
                    containerPort: 8082,
                    exposure: InfrastructureLocalEndpointExposure.HostLoopback,
                    role: InfrastructureLocalEndpointRole.Management,
                    hostPort: new(Subject, new("scheduler-port")))
                .HttpHealth(new("dashboard"), "/")
                .HealthTiming(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), retries: 30))
            .ProjectService(
                workload: new("workload/api"),
                physicalResource: new("physical/api"),
                project: projectSource,
                configure: api => api
                    .Endpoint(
                        id: new("https"),
                        scheme: "https",
                        containerPort: 7443,
                        exposure: InfrastructureLocalEndpointExposure.HostLoopback,
                        role: InfrastructureLocalEndpointRole.Data,
                        hostPort: new(Subject, new("api-port")))
                    .HttpHealth(new("https"), "/health")
                    .HealthTiming(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), retries: 30)));
        var configuration = Configuration(
            (ProjectName, "ari-local"),
            (new("scheduler-port"), "8082"),
            (new("api-port"), "7443"));

        var document = InfrastructureLocalRealizationCompiler.Compile(
            readinessRealization,
            Environment(InfrastructureLocalDataLifetime.Persistent),
            topology,
            [configuration]);
        var roundTrip = JsonSerializer.Deserialize<InfrastructureLocalRealizationDocument>(
            JsonSerializer.Serialize(document, JsonOptions),
            JsonOptions);

        Assert.True(document.IsValid);
        Assert.True(document.Topology.Services.Single(service => service.Node == new InfrastructureNodeId("workload/api"))
            .ReadyDependencies.SequenceEqual([new InfrastructurePhysicalResourceId("physical/scheduler")]));
        var project = Assert.IsType<InfrastructureLocalProjectSource>(document.Topology.Services.Single(service => service.Node == new InfrastructureNodeId("workload/api")).Source);
        Assert.Equal("ari/training-api", project.Id.Value);
        Assert.Equal("src/Ari.Training.Api/Ari.Training.Api.csproj", project.ProjectPath.Value);
        Assert.Equal("project://ari/training-api", project.Reference.Value);
        Assert.Equal("https", project.LaunchProfile);
        Assert.Equal(document.Fingerprint, roundTrip?.Fingerprint);
        var roundTripProject = Assert.IsType<InfrastructureLocalProjectSource>(
            Assert.Single(roundTrip!.Topology.Services, service => service.Node == new InfrastructureNodeId("workload/api")).Source);
        Assert.Equal(projectSource, roundTripProject);

        var unattributed = InfrastructureLocalRealizationCompiler.Compile(
            WorkloadRealization(),
            Environment(InfrastructureLocalDataLifetime.Persistent),
            directProjectTopology,
            [projectConfiguration]);
        Assert.Contains(
            unattributed.Diagnostics,
            static diagnostic => diagnostic.Code ==
                InfrastructureLocalRealizationCompiler.DiagnosticCodes.ProjectPlacementReferenceMissing);
    }

    [Fact]
    public void Workload_placement_source_and_dependency_mismatches_are_structured_diagnostics()
    {
        var realization = WorkloadRealization();
        var topology = new InfrastructureLocalTopology(
            services:
            [
                new(
                    node: new("resource/scheduler"),
                    physicalResource: new("physical/scheduler"),
                    source: new InfrastructureLocalProjectSource(
                        new("scheduler"),
                        new("src/Scheduler/Scheduler.csproj"))),
                new(
                    node: new("workload/api"),
                    physicalResource: new("physical/not-api"),
                    source: new InfrastructureLocalProjectSource(
                        new("ari/training-api"),
                        new("src/Ari.Training.Api/Ari.Training.Api.csproj")),
                    readyDependencies: [new("physical/scheduler")])
            ]);

        var document = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(InfrastructureLocalDataLifetime.Persistent),
            topology,
            [Configuration((ProjectName, "ari-local"))]);

        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.ServiceBindingMismatch);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.ServiceSourceMismatch);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.DependencyHealthMissing);
        Assert.All(document.Diagnostics, diagnostic =>
        {
            Assert.Equal("infrastructure-local-realization", diagnostic.Evidence?.Stage);
            Assert.NotEmpty(diagnostic.Evidence!.SourceReferences);
            Assert.NotEmpty(diagnostic.Evidence.ResolutionOptions);
            Assert.NotNull(diagnostic.Evidence.Expected);
            Assert.NotNull(diagnostic.Evidence.Observed);
        });
    }

    [Fact]
    public void Repository_project_source_must_remain_inside_the_repository()
    {
        Assert.Throws<ArgumentException>(() => new InfrastructureLocalProjectId("my project"));
        Assert.Throws<ArgumentException>(() => new RepositoryPath("../Ari.Api/Ari.Api.csproj"));
        Assert.Throws<ArgumentException>(() => new RepositoryPath("/src/Ari.Api/Ari.Api.csproj"));
        Assert.Throws<ArgumentException>(() => new RepositoryPath("C:\\src\\Ari.Api\\Ari.Api.csproj"));
        Assert.Throws<ArgumentException>(() => new RepositoryPath("./src/Ari.Api/Ari.Api.csproj"));
        Assert.Throws<ArgumentException>(() => new InfrastructureLocalProjectSource(
            new("ari/api"),
            new("src/Ari.Api/Program.cs")));
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

    static InfrastructureRealization WorkloadRealization(
        SourceReference? projectReference = null,
        bool includeReadiness = false)
    {
        var placementReference = projectReference ?? new SourceReference("fixture://local-workload-tests/v1");
        var definition = InfrastructureDefinitionDocument.FromDefinition(new(
            id: new("local-workload-tests"),
            revision: new("v1"),
            workloads: [new(new("workload/api"))],
            resources: [new(new("resource/scheduler"), InfrastructureResourceLifecycle.Ephemeral)],
            readinessDependencies: includeReadiness
                ?
                [
                    new(
                        InfrastructureReadinessDependency.DeriveId(new("workload/api"), new("resource/scheduler")),
                        new("workload/api"),
                        new("resource/scheduler"))
                ]
                : []));
        InfrastructureCapabilityVariantId variant = new("local");
        var profile = new InfrastructureCapabilityProfile(
            schemaVersion: InfrastructureCapabilityProfile.CurrentSchemaVersion,
            id: new("profiles/local-workload-tests/v1"),
            target: new("local-orchestration"),
            supportedDefinitionSchemaVersions: [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            variants: [new(variant)]);
        var closure = InfrastructureCapabilityCompiler.Compile(definition, profile, variant);
        var lifecycle = new InfrastructureLifecyclePlan(
            definition: definition,
            bindings:
            [
                new(
                    resource: new("resource/scheduler"),
                    physicalResource: new("physical/scheduler"),
                    interpreter: new("local-orchestration"),
                    authority: Authority,
                    disposition: InfrastructureLifecycleDisposition.Managed)
            ]);
        return InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            workloadPlacements:
            [
                new(
                    workload: new("workload/api"),
                    physicalResource: new("physical/api"),
                    interpreter: new("local-orchestration"),
                    sourceReferences: [placementReference])
            ]);
    }
}
