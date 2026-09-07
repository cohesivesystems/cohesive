using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureLocalDeploymentAuthoringTests
{
    static readonly InfrastructureCapabilityId Https = new("test/workload/https");
    static readonly InfrastructureCapabilityId Storage = new("test/resource/storage");
    static readonly InfrastructureNodeId Api = new("workloads/api");
    static readonly InfrastructureNodeId Admin = new("workloads/admin");
    static readonly InfrastructureNodeId State = new("resources/state");
    static readonly InfrastructureTargetFacilityId ProjectFacility = new("test/facilities/project");
    static readonly InfrastructureTargetFacilityId StorageFacility = new("test/facilities/storage");
    static readonly InfrastructurePhysicalResourceId ApiPhysical = new("test/services/api");
    static readonly InfrastructurePhysicalResourceId StatePhysical = new("test/services/state");
    static readonly InfrastructureTargetId Aspire = new("aspire/13.5.2");
    static readonly InfrastructureTargetId DockerCompose = new("docker-compose/2.30");
    static readonly InfrastructureLifecycleAuthorityId ComposeAuthority = new("compose/test/local");
    static readonly InfrastructureLifecycleAuthorityId AspireAuthority = new("aspire/test/local");
    static readonly InfrastructureConfigurationSubject EnvironmentSubject = new("environments/test-local");
    static readonly InfrastructureSettingId ProjectName = new("project-name");
    static readonly InfrastructureSettingId StatePort = new("ports/state");
    static readonly InfrastructureLocalEndpointId StateHealth = new("health");
    static readonly SourceReference AdapterSource = SourceReference.Create("test-adapter", "aspire-local");
    static readonly SourceReference EnvironmentSource = SourceReference.Create("environment-profile", "test/local");
    static readonly InfrastructureLocalProjectSource ApiProject = new(
        new("test/api"),
        new("src/Test.Api/Test.Api.csproj"),
        "https");

    [Fact]
    public void Coordinated_refinement_matches_direct_artifacts_and_compiles_canonical_readiness()
    {
        var semantic = Semantic();
        var facilities = Facilities();
        const string NonParticipationRationale = "The local API profile does not host other workloads.";
        var directDeployment = new InfrastructureTargetDeploymentManifest(
            InfrastructureTargetDeploymentManifest.CurrentSchemaVersion,
            new("test/deployments/aspire-local/v1"),
            semantic.Definition.ToReference(),
            facilities,
            workloads:
            [
                new(Api, ProjectFacility, ApiPhysical, [AdapterSource, ApiProject.Reference])
            ],
            resources:
            [
                new(
                    State,
                    StorageFacility,
                    StatePhysical,
                    ComposeAuthority,
                    [AdapterSource],
                    DockerCompose)
            ],
            nonParticipatingWorkloads:
            [
                new(Admin, NonParticipationRationale, [EnvironmentSource])
            ]);
        var directTopology = new InfrastructureLocalTopology(
            services:
            [
                new(
                    node: Api,
                    physicalResource: ApiPhysical,
                    source: ApiProject),
                new(
                    node: State,
                    physicalResource: StatePhysical,
                    source: new InfrastructureLocalReferencedServiceSource(Aspire, StateHealth),
                    endpoints:
                    [
                        new(
                            id: StateHealth,
                            scheme: "http",
                            servicePort: 8080,
                            exposure: InfrastructureLocalEndpointExposure.HostLoopback,
                            role: InfrastructureLocalEndpointRole.Management,
                            hostPort: new(EnvironmentSubject, StatePort))
                    ],
                    health: new(
                        probes: [new InfrastructureLocalHttpHealthProbe(StateHealth, "/ready")],
                        interval: TimeSpan.FromSeconds(2),
                        timeout: TimeSpan.FromSeconds(1),
                        retries: 30))
            ]);

        var authored = InfrastructureLocalDeployments.Define(
            directDeployment.Id,
            semantic.Definition,
            facilities,
            local => local
                .ProjectService(
                    Api,
                    ProjectFacility,
                    ApiPhysical,
                    ApiProject,
                    sourceReferences: [AdapterSource])
                .ReferencedResourceService(
                    State,
                    StorageFacility,
                    StatePhysical,
                    DockerCompose,
                    ComposeAuthority,
                    StateHealth,
                    [AdapterSource],
                    state => state
                        .Endpoint(
                            id: StateHealth,
                            scheme: "http",
                            servicePort: 8080,
                            exposure: InfrastructureLocalEndpointExposure.HostLoopback,
                            role: InfrastructureLocalEndpointRole.Management,
                            hostPort: new(EnvironmentSubject, StatePort))
                        .HttpHealth(StateHealth, "/ready")
                        .HealthTiming(
                            interval: TimeSpan.FromSeconds(2),
                            timeout: TimeSpan.FromSeconds(1),
                            retries: 30))
                .NonParticipatingWorkloadsByDefault(
                    NonParticipationRationale,
                    [EnvironmentSource]));

        Assert.Equal(directDeployment.Fingerprint, authored.TargetDeployment.Fingerprint);
        Assert.True(directDeployment.Workloads.SequenceEqual(authored.TargetDeployment.Workloads));
        Assert.True(directDeployment.Resources.SequenceEqual(authored.TargetDeployment.Resources));
        Assert.True(directDeployment.NonParticipatingWorkloads.SequenceEqual(
            authored.TargetDeployment.NonParticipatingWorkloads));
        Assert.Contains(
            ApiProject.Reference,
            authored.TargetDeployment.FindWorkload(Api).SourceReferences);
        Assert.NotEmpty(authored.TargetDeployment.SourceMap.Entries);

        var deployment = InfrastructureTargetDeploymentCompiler.Compile(semantic, authored.TargetDeployment);
        var realization = Assert.IsType<InfrastructureRealization>(deployment.Realization);
        var localRealization = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(),
            authored.Topology,
            [Conventions()]);
        var directLocalRealization = InfrastructureLocalRealizationCompiler.Compile(
            realization,
            Environment(),
            directTopology,
            [Conventions()]);

        Assert.True(deployment.IsComplete, Format(deployment.Diagnostics));
        Assert.True(localRealization.IsValid, Format(localRealization.Diagnostics));
        Assert.Equal(directLocalRealization.Fingerprint, localRealization.Fingerprint);
        var api = Assert.Single(localRealization.Topology.Services, static service => service.Node == Api);
        Assert.Equal<InfrastructurePhysicalResourceId>([StatePhysical], api.ReadyDependencies);
    }

    [Fact]
    public void Explicit_non_participation_takes_precedence_over_the_closed_world_default()
    {
        var semantic = Semantic(includeWorker: true);
        InfrastructureNodeId worker = new("workloads/worker");
        var authored = InfrastructureLocalDeployments.Define(
            new("test/deployments/participation/v1"),
            semantic.Definition,
            Facilities(),
            local => local
                .ProjectService(Api, ProjectFacility, ApiPhysical, ApiProject)
                .NonParticipatingWorkload(
                    Admin,
                    "The administration surface is tested separately.",
                    [SourceReference.Create("environment-profile", "test/admin-separate")])
                .NonParticipatingWorkloadsByDefault(
                    "The local API profile excludes all remaining workloads.",
                    [EnvironmentSource]));

        Assert.Equal(2, authored.TargetDeployment.NonParticipatingWorkloads.Length);
        Assert.Equal(
            "The administration surface is tested separately.",
            authored.TargetDeployment.NonParticipatingWorkloads.Single(item => item.Workload == Admin).Rationale);
        Assert.Equal(
            "The local API profile excludes all remaining workloads.",
            authored.TargetDeployment.NonParticipatingWorkloads.Single(item => item.Workload == worker).Rationale);
    }

    [Fact]
    public void Closed_world_non_participation_policy_can_only_be_declared_once()
    {
        Assert.Throws<InvalidOperationException>(() => InfrastructureLocalDeployments.Define(
            new("test/deployments/duplicate-policy/v1"),
            Semantic().Definition,
            Facilities(),
            local => local
                .NonParticipatingWorkloadsByDefault("First policy.", [EnvironmentSource])
                .NonParticipatingWorkloadsByDefault("Second policy.", [EnvironmentSource])));
    }

    [Fact]
    public void Coordinated_refinement_covers_container_services_non_service_resources_and_topology_assets()
    {
        InfrastructureNodeId worker = new("workloads/worker");
        InfrastructureNodeId cache = new("resources/cache");
        InfrastructureNodeId archive = new("resources/archive");
        InfrastructureNodeId secrets = new("resources/secrets");
        InfrastructurePhysicalResourceId workerPhysical = new("test/services/worker");
        InfrastructurePhysicalResourceId cachePhysical = new("test/services/cache");
        InfrastructurePhysicalResourceId archivePhysical = new("test/services/archive");
        InfrastructurePhysicalResourceId secretsPhysical = new("test/configuration/secrets");
        var semantic = Infrastructure.Define(
            new("test/local-deployment-container-shapes"),
            new("1"),
            new("test/local-deployment-container-shapes/bindings/v1"),
            infrastructure =>
            {
                _ = infrastructure.Workload(worker).Requires(Https);
                _ = infrastructure.Resource(cache).Persistent().Requires(Storage);
                _ = infrastructure.Resource(archive).Persistent().Requires(Storage);
                _ = infrastructure.Resource(secrets).Persistent().Requires(Storage);
            });

        var authored = InfrastructureLocalDeployments.Define(
            new("test/deployments/container-shapes/v1"),
            semantic.Definition,
            Facilities(),
            local => local
                .Volume(new("cache-data"))
                .File(new("worker-config"), "mode=local")
                .ContainerWorkload(
                    worker,
                    ProjectFacility,
                    workerPhysical,
                    "test-worker:1.0",
                    [AdapterSource],
                    service => service.FileMount(new("worker-config"), "/app/config"))
                .ContainerResource(
                    cache,
                    StorageFacility,
                    cachePhysical,
                    AspireAuthority,
                    "redis:8.2.1",
                    [AdapterSource],
                    service => service.Mount(new("cache-data"), "/data"))
                .ReferencedResource(
                    archive,
                    StorageFacility,
                    archivePhysical,
                    DockerCompose,
                    ComposeAuthority,
                    [AdapterSource])
                .Resource(
                    secrets,
                    StorageFacility,
                    secretsPhysical,
                    AspireAuthority,
                    [AdapterSource])
                .Operation(
                    id: new("verify"),
                    placement: InfrastructureLocalExecutionPlacement.Host,
                    effect: InfrastructureLocalOperationEffect.ReadOnly,
                    executable: "eng/verify.sh",
                    requiredServices: [workerPhysical, cachePhysical]));

        Assert.Single(authored.TargetDeployment.Workloads);
        Assert.Equal(3, authored.TargetDeployment.Resources.Length);
        Assert.Empty(authored.TargetDeployment.NonParticipatingWorkloads);
        Assert.Equal(2, authored.Topology.Services.Length);
        Assert.Single(authored.Topology.Volumes);
        Assert.Single(authored.Topology.Files);
        Assert.Single(authored.Topology.Operations);
        Assert.IsType<InfrastructureLocalContainerSource>(
            authored.Topology.Services.Single(service => service.Node == worker).Source);
        Assert.IsType<InfrastructureLocalContainerSource>(
            authored.Topology.Services.Single(service => service.Node == cache).Source);
        Assert.DoesNotContain(authored.Topology.Services, service => service.Node == archive);
        Assert.DoesNotContain(authored.Topology.Services, service => service.Node == secrets);
        Assert.Equal(
            DockerCompose,
            authored.TargetDeployment.Resources.Single(resource => resource.Resource == archive).ManagingInterpreter);
        Assert.Null(
            authored.TargetDeployment.Resources.Single(resource => resource.Resource == secrets).ManagingInterpreter);
    }

    [Fact]
    public void Wrong_node_kind_remains_a_structured_target_compiler_diagnostic()
    {
        var semantic = Semantic();
        var authored = InfrastructureLocalDeployments.Define(
            new("test/deployments/wrong-kind/v1"),
            semantic.Definition,
            Facilities(),
            local => local
                .ProjectService(State, ProjectFacility, ApiPhysical, ApiProject)
                .NonParticipatingWorkloadsByDefault(
                    "The invalid test profile excludes real workloads.",
                    [EnvironmentSource]));

        var compilation = InfrastructureTargetDeploymentCompiler.Compile(semantic, authored.TargetDeployment);

        Assert.False(compilation.IsComplete);
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.NodeUnknown
                          && diagnostic.Evidence?.Subject == State.Value);
        Assert.All(compilation.Diagnostics, static diagnostic =>
        {
            Assert.NotNull(diagnostic.Evidence);
            Assert.NotEmpty(diagnostic.Evidence!.SourceReferences);
            Assert.NotEmpty(diagnostic.Evidence.ResolutionOptions);
        });
    }

    static InfrastructureAuthoringResult Semantic(bool includeWorker = false) => Infrastructure.Define(
        new("test/local-deployment-authoring"),
        new(includeWorker ? "2" : "1"),
        new("test/local-deployment-authoring/bindings/v1"),
        infrastructure =>
        {
            var api = infrastructure.Workload(Api).Requires(Https);
            _ = infrastructure.Workload(Admin).Requires(Https);
            if (includeWorker)
            {
                _ = infrastructure.Workload(new("workloads/worker")).Requires(Https);
            }
            var state = infrastructure.Resource(State).Persistent().Requires(Storage);
            api.RequiresReady(state);
        });

    static InfrastructureTargetFacilityManifest Facilities() => InfrastructureTargetFacilities.Define(
        new("test/local-deployment-facilities/v1"),
        new("test/local-deployment-capabilities/v1"),
        Aspire,
        new("test/local"),
        [InfrastructureDefinitionDocument.CurrentSchemaVersion],
        target =>
        {
            target.Workload(ProjectFacility).Provides(Native("test/evidence/https", Https));
            target.Resource(StorageFacility).Provides(Native("test/evidence/storage", Storage));
        });

    static InfrastructureCapabilityEvidence Native(string id, InfrastructureCapabilityId capability) => new(
        new(id),
        capability,
        CapabilityRealizationKind.Native,
        sourceReferences: [AdapterSource]);

    static InfrastructureLocalEnvironmentProfile Environment() => new(
        new("test/environments/local/v1"),
        AspireAuthority,
        EnvironmentSubject,
        ProjectName,
        InfrastructureLocalDataLifetime.Ephemeral,
        InfrastructureLocalEnvironmentIsolation.Isolated,
        TimeSpan.FromHours(1));

    static InfrastructureConventionProfile Conventions() => new(
        new("test/conventions/local/v1"),
        [
            new(
                EnvironmentSubject,
                ProjectName,
                "test-local",
                EffectiveConfigurationOrigin.ScopedProfile,
                "test/conventions/local/v1"),
            new(
                EnvironmentSubject,
                StatePort,
                "58080",
                EffectiveConfigurationOrigin.ScopedProfile,
                "test/conventions/local/v1")
        ]);

    static string Format(IEnumerable<Cohesive.Model.Serialization.DocumentValidationDiagnostic> diagnostics) =>
        string.Join(System.Environment.NewLine, diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
}
