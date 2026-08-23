using System.Collections.Immutable;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Cohesive.Adapters.Aspire;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class AspireLocalCompilerTests
{
    [Fact]
    public void Same_exact_realization_emits_an_identical_fingerprinted_projection()
    {
        var source = InteractiveSource();

        var first = Compile(source);
        var second = Compile(source);

        Assert.True(first.IsSuccess, string.Join(Environment.NewLine, first.Diagnostics.Select(static item => item.Message)));
        Assert.Equal(first.Projection!.ToJson(), second.Projection!.ToJson());
        Assert.Equal(first.Projection.Fingerprint, second.Projection.Fingerprint);
        Assert.Equal(source.Realization, first.Projection.SourceRealization);
        Assert.Equal(source.Fingerprint, first.Projection.LocalRealization);
        Assert.Equal(5, first.Projection.Services.Length);
        Assert.Equal(7, first.Projection.Endpoints.Length);
        Assert.Equal(4, first.Projection.Volumes.Length);
        Assert.Equal(8, first.Projection.Operations.Length);
        Assert.Equal(2, first.Projection.Secrets.Length);
    }

    [Fact]
    public void Canonical_graph_and_target_differences_are_inspectable_without_a_second_topology()
    {
        var source = InteractiveSource();
        var projection = Compile(source).Projection!;

        Assert.Equal(
            source.Topology.Services.Select(static item => item.PhysicalResource.Value),
            projection.Services.Select(static item => item.Service.PhysicalResource.Value));
        Assert.All(projection.Services, item => Assert.Same(
            source.Topology.Services.Single(service => service.PhysicalResource == item.Service.PhysicalResource),
            item.Service));
        Assert.Contains(projection.Endpoints, endpoint =>
            endpoint.PhysicalResource == FreightMaterializationInfrastructure.CosmosService
            && endpoint.Endpoint.Id.Value == "explorer"
            && endpoint.Endpoint.Role == InfrastructureLocalEndpointRole.UserInterface
            && endpoint.HostAddress == "http://localhost:58082");
        Assert.Contains(projection.Endpoints, endpoint =>
            endpoint.PhysicalResource == FreightMaterializationInfrastructure.PgAdminService
            && endpoint.HostAddress == "http://localhost:55050");
        Assert.Contains(projection.Endpoints, endpoint =>
            endpoint.PhysicalResource == FreightMaterializationInfrastructure.KibanaService
            && endpoint.HostAddress == "http://localhost:55601");
        Assert.Contains(projection.Decisions, decision =>
            decision.Concern == "aspire/health/timing"
            && decision.Kind == CapabilityRealizationKind.Constrained);
        Assert.Contains(projection.Decisions, decision =>
            decision.Concern == "aspire/health/command/local/postgres/pg_isready"
            && decision.Kind == CapabilityRealizationKind.Override);
        Assert.Contains(projection.Decisions, decision =>
            decision.Concern == "aspire/dashboard-observability"
            && decision.Kind == CapabilityRealizationKind.Native);
        Assert.Equal(AspireDcpTlsCertificateMode.EphemeralSelfSigned, projection.DcpTlsCertificateMode);
        Assert.Contains(projection.Decisions, decision =>
            decision.Concern == "aspire/orchestration/dcp-tls"
            && decision.Boundaries.Contains("The DCP TLS identity is regenerated for each AppHost run."));
        Assert.Contains(projection.Operations, operation =>
            operation.Operation.Id.Value == "seed"
            && operation.Realization == AspireOperationRealization.ProcessCommand);
        Assert.Contains(projection.Operations, operation =>
            operation.Operation.Id.Value == "reset"
            && operation.Realization == AspireOperationRealization.LifecycleControl);
    }

    [Fact]
    public void Command_health_requires_exact_evidence_and_rejects_stale_overrides()
    {
        var source = InteractiveSource();

        var missing = AspireLocalCompiler.Compile(source: source);
        var stale = AspireLocalCompiler.Compile(
            source: source,
            options: new AspireLocalCompilerOptions(
                commandHealthOverrides:
                [
                    new(
                        physicalResource: FreightMaterializationInfrastructure.PostgresService,
                        executable: "pg_isready",
                        arguments: ["--username=$POSTGRES_USER"],
                        strategy: AspireCommandHealthOverrideStrategy.TcpConnect,
                        endpoint: new("postgres"),
                        rationale: "Intentionally stale test override.",
                        sourceReferences: ["test/stale"])
                ]));

        Assert.False(missing.IsSuccess);
        Assert.Contains(missing.Diagnostics, diagnostic =>
            diagnostic.Code == AspireLocalCompiler.DiagnosticCodes.CommandHealthOverrideRequired);
        Assert.False(stale.IsSuccess);
        Assert.Contains(stale.Diagnostics, diagnostic =>
            diagnostic.Code == AspireLocalCompiler.DiagnosticCodes.CommandHealthOverrideRequired);
        Assert.Contains(stale.Diagnostics, diagnostic =>
            diagnostic.Code == AspireLocalCompiler.DiagnosticCodes.CommandHealthOverrideUnused);
    }

    [Fact]
    public void Interactive_data_is_named_and_isolated_data_is_anonymous_and_bounded()
    {
        var interactive = Compile(InteractiveSource()).Projection!;
        var isolatedSource = FreightMaterializationInfrastructure.CreateLocalRealization(
            environment: FreightMaterializationInfrastructure.IsolatedTestProfile,
            additionalConfiguration: FreightMaterializationInfrastructure.CreateIsolatedProjectConfiguration("materialization-tests-aspire-01"));
        var isolated = Compile(isolatedSource).Projection!;

        Assert.All(interactive.Volumes, volume => Assert.StartsWith("cohesive-materialization-local-", volume.VolumeName, StringComparison.Ordinal));
        Assert.All(isolated.Volumes, volume => Assert.Null(volume.VolumeName));
        Assert.Equal(InfrastructureLocalDataLifetime.Persistent, interactive.Environment.DataLifetime);
        Assert.Null(interactive.Environment.MaximumLifetime);
        Assert.Equal(InfrastructureLocalDataLifetime.Ephemeral, isolated.Environment.DataLifetime);
        Assert.Equal(TimeSpan.FromMinutes(30), isolated.Environment.MaximumLifetime);
        Assert.NotEqual(interactive.Fingerprint, isolated.Fingerprint);
    }

    [Fact]
    public void Host_port_overrides_do_not_change_container_listener_ports()
    {
        var overrides = new InfrastructureConventionProfile(
            id: new("test/aspire-host-ports/v1"),
            candidates:
            [
                new(
                    subject: FreightMaterializationInfrastructure.ConfigurationSubject,
                    setting: FreightMaterializationInfrastructure.Settings.CosmosPort,
                    value: "65081",
                    origin: EffectiveConfigurationOrigin.Explicit,
                    authority: "test/aspire-host-ports/v1")
            ]);
        var source = FreightMaterializationInfrastructure.CreateLocalRealization(
            environment: FreightMaterializationInfrastructure.InteractiveProfile,
            additionalConfiguration: [overrides]);
        var projection = Compile(source).Projection!;
        var cosmos = projection.Services.Single(service =>
            service.Service.PhysicalResource == FreightMaterializationInfrastructure.CosmosService);
        var containerPort = cosmos.Service.Endpoints.Single(endpoint => endpoint.Id.Value == "cosmos").ContainerPort;

        Assert.Equal(65081, containerPort.Resolve(projection.Configuration));
        Assert.Contains(projection.Endpoints, endpoint =>
            endpoint.PhysicalResource == FreightMaterializationInfrastructure.CosmosService
            && endpoint.Endpoint.Id.Value == "cosmos"
            && endpoint.ServiceAddress.EndsWith(":65081", StringComparison.Ordinal)
            && endpoint.HostAddress == "https://localhost:65081");
    }

    [Fact]
    public void Applied_aspire_model_preserves_resources_endpoints_waits_health_commands_and_identity()
    {
        var projection = Compile(InteractiveSource()).Projection!;
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(AspireLocalCompilerTests).Assembly.GetName().Name,
            ProjectDirectory = FindRepositoryRoot(),
            DisableDashboard = true
        });

        var applied = builder.AddCohesiveLocalInfrastructure(
            projection: projection,
            options: new AspireLocalApplicationOptions(
                operationWorkingDirectory: FindRepositoryRoot(),
                resolveSecret: static _ => "test-secret"));

        Assert.Equal("false", builder.Configuration["ASPIRE_DCP_USE_DEVELOPER_CERTIFICATE"]);
        Assert.Equal(5, applied.Services.Count);
        Assert.Equal(8, builder.Resources.Count);
        foreach (var service in projection.Services)
        {
            var resource = applied.Services[service.Service.PhysicalResource].Resource;
            Assert.True(resource.TryGetContainerImageName(out var image));
            Assert.Equal(service.Service.Image, image);
            var identity = Assert.Single(resource.Annotations.OfType<AspireInfraIdentityAnnotation>());
            Assert.Equal(service.Service.Resource, identity.LogicalResource);
            Assert.Equal(service.Service.PhysicalResource, identity.PhysicalResource);
            Assert.Equal(projection.Fingerprint, identity.Projection);
            Assert.Equal(service.Service.Endpoints.Length, resource.Annotations.OfType<EndpointAnnotation>().Count());
            Assert.Equal(service.Service.ReadyDependencies.Length, resource.Annotations.OfType<WaitAnnotation>().Count());
            if (service.Service.Health is not null)
                Assert.NotEmpty(resource.Annotations.OfType<HealthCheckAnnotation>());
        }
        Assert.Equal(
            projection.Operations.Count(static item => item.Realization == AspireOperationRealization.ProcessCommand),
            applied.ControlResource.Resource.Annotations.OfType<ResourceCommandAnnotation>().Count());
        Assert.Equal(
            projection.Operations.SelectMany(static item => item.RequiredResources).Distinct(StringComparer.Ordinal).Count(),
            applied.ControlResource.Resource.Annotations.OfType<WaitAnnotation>().Count());
    }

    [Fact]
    public void Projection_round_trip_recomputes_the_same_fingerprint_and_rejects_tampering()
    {
        var projection = Compile(InteractiveSource()).Projection!;
        var options = StrictDocumentJson.CreateOptions();
        var json = projection.ToJson(PortableDocumentJsonFormatting.Compact);

        var restored = JsonSerializer.Deserialize<AspireLocalProjectionDocument>(json, options);
        var tampered = json.Replace(
            "cohesive-materialization-local-postgres-data",
            "cohesive-materialization-local-postgres-other",
            StringComparison.Ordinal);

        Assert.NotNull(restored);
        Assert.Equal(projection.Fingerprint, restored.Fingerprint);
        Assert.Equal(projection.ToJson(), restored.ToJson());
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<AspireLocalProjectionDocument>(tampered, options));
    }

    static InfrastructureLocalRealizationDocument InteractiveSource() =>
        FreightMaterializationInfrastructure.CreateLocalRealization(
            environment: FreightMaterializationInfrastructure.InteractiveProfile);

    static AspireLocalCompilation Compile(InfrastructureLocalRealizationDocument source) =>
        AspireLocalCompiler.Compile(source: source, options: Options());

    static AspireLocalCompilerOptions Options() => new(
        commandHealthOverrides:
        [
            new(
                physicalResource: FreightMaterializationInfrastructure.PostgresService,
                executable: "pg_isready",
                arguments: ["--dbname=$POSTGRES_DB", "--username=$POSTGRES_USER"],
                strategy: AspireCommandHealthOverrideStrategy.TcpConnect,
                endpoint: new("postgres"),
                rationale: "Stable Aspire has no command health probe, so the test selects explicit TCP readiness.",
                sourceReferences: ["ARI-467", "test/aspire-command-health"])
        ]);

    static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cohesive.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"Cannot locate repository root from '{AppContext.BaseDirectory}'.");
    }
}
