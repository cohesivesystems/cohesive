using System.Collections.Immutable;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Cohesive.Adapters.Aspire;
using Cohesive.Adapters.DockerCompose;
using Cohesive.Execution;
using Cohesive.Infra;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

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
            decision.Concern == "local/health/timing"
            && decision.Kind == CapabilityRealizationKind.Constrained);
        Assert.Contains(projection.Decisions, decision =>
            decision.Concern == "local/health/command/local/postgres/pg_isready"
            && decision.Kind == CapabilityRealizationKind.Override);
        Assert.Contains(projection.Decisions, decision =>
            decision.Concern == "local/observability"
            && decision.Kind == CapabilityRealizationKind.Native);
        Assert.Equal(AspireDcpTlsCertificateMode.EphemeralSelfSigned, projection.DcpTlsCertificateMode);
        Assert.Contains(projection.Decisions, decision =>
            decision.Concern == "local/orchestration-control-plane"
            && decision.Kind == CapabilityRealizationKind.Native
            && decision.Boundaries.IsEmpty
            && decision.Rationale.Contains("ephemeral self-signed TLS identity", StringComparison.Ordinal));
        Assert.Contains(projection.Operations, operation =>
            operation.Operation.Id.Value == "seed"
            && operation.Realization == AspireOperationRealization.ProcessCommand);
        Assert.Contains(projection.Operations, operation =>
            operation.Operation.Id.Value == "reset"
            && operation.Realization == AspireOperationRealization.LifecycleControl);
    }

    [Fact]
    public void Compose_and_aspire_projections_satisfy_one_exact_local_conformance_contract()
    {
        var source = InteractiveSource();
        var compose = DockerComposeCompiler.Compile(source: source).Artifact!;
        var aspire = Compile(source).Projection!;

        Assert.Equal(source.Realization, compose.Manifest.SourceRealization);
        Assert.Equal(source.Realization, aspire.SourceRealization);
        Assert.Equal(source.Fingerprint, compose.Manifest.LocalRealization);
        Assert.Equal(source.Fingerprint, aspire.LocalRealization);
        Assert.All(compose.Manifest.Decisions, static item =>
            Assert.Equal(DockerComposeArtifactManifest.CurrentTarget, item.Target));
        Assert.All(aspire.Decisions, static item =>
            Assert.Equal(AspireLocalProjectionDocument.CurrentTarget, item.Target));
        Assert.Equal(compose.Manifest.Environment, aspire.Environment.Id);
        Assert.Equal(compose.Manifest.LifecycleAuthority, aspire.Environment.Authority);
        Assert.Equal(compose.Manifest.DataLifetime, aspire.Environment.DataLifetime);
        Assert.Equal(compose.Manifest.Isolation, aspire.Environment.Isolation);
        Assert.Equal(compose.Manifest.MaximumLifetime, aspire.Environment.MaximumLifetime);
        Assert.Equal(compose.Manifest.ProjectName, aspire.ProjectName);
        Assert.Equal(compose.Manifest.Configuration, aspire.Configuration);

        var composeServices = compose.Manifest.Services.ToDictionary(static item => item.PhysicalResource);
        var aspireServices = aspire.Services.ToDictionary(static item => item.Service.PhysicalResource);
        Assert.Equal(source.Topology.Services.Length, composeServices.Count);
        Assert.Equal(source.Topology.Services.Length, aspireServices.Count);
        foreach (var service in source.Topology.Services)
        {
            var composeService = composeServices[service.PhysicalResource];
            var aspireService = aspireServices[service.PhysicalResource];
            Assert.Equal(service.Node, composeService.Resource);
            Assert.Equal(service.Node, aspireService.Service.Node);
            Assert.Same(service, aspireService.Service);
            Assert.Equal(composeService.ServiceName, aspireService.ResourceName);
            var container = Assert.IsType<InfrastructureLocalContainerSource>(service.Source);
            Assert.Contains($"{composeService.ServiceName}:\n    image: '{container.Image}'", compose.Yaml, StringComparison.Ordinal);
        }

        var composeEndpoints = compose.Manifest.Endpoints.ToDictionary(static item => (item.PhysicalResource, item.Endpoint));
        var aspireEndpoints = aspire.Endpoints.ToDictionary(static item => (item.PhysicalResource, item.Endpoint.Id));
        Assert.Equal(
            composeEndpoints.Keys.Select(static item => $"{item.PhysicalResource.Value}/{item.Endpoint.Value}").Order(StringComparer.Ordinal),
            aspireEndpoints.Keys.Select(static item => $"{item.PhysicalResource.Value}/{item.Id.Value}").Order(StringComparer.Ordinal));
        foreach (var (identity, composeEndpoint) in composeEndpoints)
        {
            var aspireEndpoint = aspireEndpoints[identity];
            Assert.Equal(composeEndpoint.Exposure, aspireEndpoint.Endpoint.Exposure);
            Assert.Equal(composeEndpoint.Role, aspireEndpoint.Endpoint.Role);
            Assert.Equal(composeEndpoint.ServiceAddress, aspireEndpoint.ServiceAddress);
            Assert.Equal(composeEndpoint.HostAddress, aspireEndpoint.HostAddress);
        }

        Assert.Equal(
            compose.Manifest.Volumes.Select(static item => item.Volume.Value).Order(StringComparer.Ordinal),
            aspire.Volumes.Select(static item => item.Volume.Value).Order(StringComparer.Ordinal));
        Assert.Equal(
            compose.Manifest.Configs.Select(static item => item.File.Value).Order(StringComparer.Ordinal),
            aspire.Files.Select(static item => item.File.Value).Order(StringComparer.Ordinal));
        Assert.Equal(
            compose.Manifest.Operations.Select(static item => item.Operation.Value).Order(StringComparer.Ordinal),
            aspire.Operations.Select(static item => item.Operation.Id.Value).Order(StringComparer.Ordinal));
        Assert.Equal(
            compose.Manifest.Endpoints.Where(static item => item.Role == InfrastructureLocalEndpointRole.UserInterface)
                .Select(static item => item.HostAddress).Order(),
            aspire.Endpoints.Where(static item => item.Endpoint.Role == InfrastructureLocalEndpointRole.UserInterface)
                .Select(static item => item.HostAddress).Order());

        Assert.Equal(
            compose.Manifest.Decisions.Select(static item => item.Concern).Order(StringComparer.Ordinal),
            aspire.Decisions.Select(static item => item.Concern).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(compose.Manifest.Decisions, static item =>
            item.Kind is CapabilityRealizationKind.Unavailable or CapabilityRealizationKind.Unknown);
        Assert.DoesNotContain(aspire.Decisions, static item =>
            item.Kind is CapabilityRealizationKind.Unavailable or CapabilityRealizationKind.Unknown);
        Assert.All(compose.Manifest.Decisions, decision => Assert.Contains(
            decision.SourceReferences,
            reference => reference.Value.Contains(source.Fingerprint.Value, StringComparison.Ordinal)));
        Assert.All(aspire.Decisions, decision => Assert.Contains(
            decision.SourceReferences,
            reference => reference.Value.Contains(source.Fingerprint.Value, StringComparison.Ordinal)));
        Assert.Contains(compose.Manifest.Decisions, static decision =>
            decision.Concern == "local/health/timing" && decision.Kind == CapabilityRealizationKind.Native);
        Assert.Contains(aspire.Decisions, static decision =>
            decision.Concern == "local/health/timing" && decision.Kind == CapabilityRealizationKind.Constrained);
        Assert.Contains(compose.Manifest.Decisions, static decision =>
            decision.Concern == "local/observability" && decision.Kind == CapabilityRealizationKind.Constrained);
        Assert.Contains(aspire.Decisions, static decision =>
            decision.Concern == "local/observability" && decision.Kind == CapabilityRealizationKind.Native);
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
                        sourceReferences: ["test://stale"])
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
        var servicePort = cosmos.Service.Endpoints.Single(endpoint => endpoint.Id.Value == "cosmos").ServicePort;

        Assert.Equal(65081, servicePort.Resolve(projection.Configuration));
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
            var container = Assert.IsType<InfrastructureLocalContainerSource>(service.Service.Source);
            Assert.Equal(container.Image, image);
            var identity = Assert.Single(resource.Annotations.OfType<AspireInfraIdentityAnnotation>());
            Assert.Equal(service.Service.Node, identity.LogicalNode);
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
        Assert.Throws<ArgumentException>(() => new AspireLocalApplication(
            projection,
            ImmutableDictionary<InfrastructurePhysicalResourceId, IResourceBuilder<IResource>>.Empty,
            applied.ControlResource));
    }

    [Fact]
    public void Aspire_constructs_repository_projects_while_compose_fails_closed()
    {
        var source = ProjectWorkloadSource();
        var aspire = AspireLocalCompiler.Compile(source);
        var compose = DockerComposeCompiler.Compile(source);

        Assert.True(aspire.IsSuccess, string.Join(Environment.NewLine, aspire.Diagnostics.Select(static item => item.Message)));
        Assert.False(compose.IsSuccess);
        Assert.Contains(compose.Diagnostics, diagnostic =>
            diagnostic.Code == DockerComposeCompiler.DiagnosticCodes.ServiceSourceUnsupported);
        Assert.Contains(aspire.Projection!.Decisions, decision =>
            decision.Concern == "local/service-construction/repository-project"
            && decision.Kind == CapabilityRealizationKind.Native);

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(AspireLocalCompilerTests).Assembly.GetName().Name,
            ProjectDirectory = FindRepositoryRoot(),
            DisableDashboard = true
        });
        var applied = builder.AddCohesiveLocalInfrastructure(
            projection: aspire.Projection,
            options: new AspireLocalApplicationOptions(
                operationWorkingDirectory: FindRepositoryRoot(),
                resolveSecret: static _ => null));

        var project = Assert.IsType<ProjectResource>(applied.Services[new("local/ari-training-api")].Resource);
        var sourceAnnotation = Assert.Single(project.Annotations.OfType<AspireInfraIdentityAnnotation>());
        Assert.Equal(new InfrastructureNodeId("workload/ari-training-api"), sourceAnnotation.LogicalNode);
        Assert.IsType<InfrastructureLocalProjectSource>(aspire.Projection!.Services[0].Service.Source);
    }

    [Fact]
    public async Task Aspire_projects_foreign_managed_services_without_taking_lifecycle_ownership()
    {
        var source = ReferencedServiceSource(AspireLocalProjectionDocument.CurrentTargetId);
        var aspire = AspireLocalCompiler.Compile(source);
        var compose = DockerComposeCompiler.Compile(source);

        Assert.True(source.IsValid, string.Join(Environment.NewLine, source.Diagnostics.Select(static item => item.Message)));
        Assert.True(aspire.IsSuccess, string.Join(Environment.NewLine, aspire.Diagnostics.Select(static item => item.Message)));
        Assert.False(compose.IsSuccess);
        Assert.Contains(compose.Diagnostics, static diagnostic =>
            diagnostic.Code == DockerComposeCompiler.DiagnosticCodes.ServiceSourceUnsupported);
        var projection = aspire.Projection!;
        Assert.Contains(projection.Decisions, static decision =>
            decision.Concern == "local/service-realization/referenced"
            && decision.Kind == CapabilityRealizationKind.Native);
        Assert.Contains(projection.Endpoints, static endpoint =>
            endpoint.PhysicalResource == new InfrastructurePhysicalResourceId("local/cosmos")
            && endpoint.Endpoint.Id == new InfrastructureLocalEndpointId("gateway")
            && endpoint.ServiceAddress == "https://localhost:58081"
            && endpoint.HostAddress == "https://localhost:58081");

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(AspireLocalCompilerTests).Assembly.GetName().Name,
            ProjectDirectory = FindRepositoryRoot(),
            DisableDashboard = true
        });
        var applied = builder.AddCohesiveLocalInfrastructure(
            projection,
            new AspireLocalApplicationOptions(
                operationWorkingDirectory: FindRepositoryRoot(),
                resolveSecret: static _ => null));

        var external = Assert.IsType<ExternalServiceResource>(applied.Services[new("local/cosmos")].Resource);
        Assert.Equal(new Uri("http://localhost:58082"), external.Uri);
        Assert.False(external.TryGetContainerImageName(out _));
        Assert.NotEmpty(external.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Empty(external.Annotations.OfType<EndpointAnnotation>());
        var project = Assert.IsType<ProjectResource>(applied.Services[new("local/api")].Resource);
        Assert.Single(project.Annotations.OfType<WaitAnnotation>());
        Assert.NotEmpty(project.Annotations.OfType<EnvironmentCallbackAnnotation>());
        Assert.Contains(
            project.Annotations.OfType<ResourceRelationshipAnnotation>(),
            relationship => ReferenceEquals(relationship.Resource, external)
                && string.Equals(relationship.Type, "Reference", StringComparison.Ordinal));
        var executionConfiguration = await ExecutionConfigurationBuilder
            .Create(project)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                NullLogger.Instance,
                CancellationToken.None);
        var environment = executionConfiguration.EnvironmentVariables.ToDictionary(
            static variable => variable.Key,
            static variable => variable.Value,
            StringComparer.Ordinal);
        Assert.Equal("https://localhost:58081", environment["COSMOS_ENDPOINT"]);
        Assert.Equal("http://localhost:58082/", environment["COSMOS_HEALTH_ENDPOINT"]);
        var endpointValues = projection.Services.Single(candidate =>
                candidate.Service.PhysicalResource == new InfrastructurePhysicalResourceId("local/api"))
            .Service.Environment.Select(static variable => Assert.IsType<InfrastructureLocalEndpointValue>(variable.Value))
            .ToArray();
        Assert.All(endpointValues, static endpointValue =>
        {
            Assert.Equal(new InfrastructurePhysicalResourceId("local/cosmos"), endpointValue.Service);
            Assert.Equal(InfrastructureLocalEndpointAddress.ServiceNetwork, endpointValue.Address);
        });
        Assert.Contains(endpointValues, static endpointValue => endpointValue.Endpoint == new InfrastructureLocalEndpointId("gateway"));
        Assert.Contains(endpointValues, static endpointValue => endpointValue.Endpoint == new InfrastructureLocalEndpointId("health"));
    }

    [Fact]
    public void Aspire_rejects_a_foreign_endpoint_without_a_concrete_host_address()
    {
        var source = ReferencedServiceSource(
            consumer: AspireLocalProjectionDocument.CurrentTargetId,
            exposeGatewayOnHost: false);

        var compilation = AspireLocalCompiler.Compile(source);

        Assert.True(source.IsValid, string.Join(Environment.NewLine, source.Diagnostics.Select(static item => item.Message)));
        Assert.False(compilation.IsSuccess);
        Assert.Null(compilation.Projection);
        Assert.Contains(compilation.Diagnostics, static diagnostic =>
            diagnostic.Code == AspireLocalCompiler.DiagnosticCodes.ReferencedServiceEndpointUnsupported
            && diagnostic.Location == "/topology/services/local/cosmos/endpoints/gateway"
            && diagnostic.Evidence?.Expected == "host-loopback exposure with host-port configuration"
            && diagnostic.Evidence.Observed == "endpoint exposure is Internal");
    }

    [Fact]
    public void Aspire_rejects_references_for_another_interpreter_and_non_representative_health()
    {
        var source = ReferencedServiceSource(
            consumer: new("tests/other-local-interpreter"),
            healthEndpoint: new("gateway"));

        var compilation = AspireLocalCompiler.Compile(source);

        Assert.True(source.IsValid, string.Join(Environment.NewLine, source.Diagnostics.Select(static item => item.Message)));
        Assert.False(compilation.IsSuccess);
        Assert.Null(compilation.Projection);
        Assert.Contains(compilation.Diagnostics, static diagnostic =>
            diagnostic.Code == AspireLocalCompiler.DiagnosticCodes.ReferencedServiceTargetMismatch
            && diagnostic.Evidence?.Expected == AspireLocalProjectionDocument.CurrentTarget
            && diagnostic.Evidence.Observed == "tests/other-local-interpreter");
        Assert.Contains(compilation.Diagnostics, static diagnostic =>
            diagnostic.Code == AspireLocalCompiler.DiagnosticCodes.ReferencedServiceHealthEndpointUnsupported
            && diagnostic.Evidence?.Expected == "health"
            && diagnostic.Evidence.Observed == "gateway");
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

    [Fact]
    public async Task Current_healthy_notifications_become_ordered_attributable_observations_and_a_ready_assessment()
    {
        var (app, applied) = CreateAppliedApplication();
        await using (app)
        {
            foreach (var service in applied.Services.Values)
                await PublishAsync(app.ResourceNotifications, service.Resource, KnownResourceStates.Running, HealthStatus.Healthy);

            var observedAt = new DateTimeOffset(2026, 9, 5, 23, 30, 0, TimeSpan.Zero);
            var observations = AspireInfrastructureObservations.CaptureCurrent(
                applied,
                app.ResourceNotifications,
                observedAt);
            var assessment = AspireInfrastructureObservations.AssessCurrent(
                applied,
                app.ResourceNotifications,
                FreightMaterializationInfrastructure.CreatePhysicalRealization(),
                observedAt);

            Assert.Equal(applied.Services.Count, observations.Length);
            Assert.Equal(
                observations.Select(static observation => observation.PhysicalResource.Value).Order(StringComparer.Ordinal),
                observations.Select(static observation => observation.PhysicalResource.Value));
            Assert.All(observations, observation =>
            {
                Assert.Equal(ExecutionHealthStatus.Healthy, observation.Health);
                Assert.Equal(ExecutionReadinessStatus.Ready, observation.Readiness);
                Assert.Equal(observedAt, observation.ObservedAtUtc);
                Assert.Empty(observation.Diagnostics);
                Assert.Contains(observation.SourceReferences, reference =>
                    reference.Value.StartsWith("aspire-resource://", StringComparison.Ordinal));
                Assert.Contains(observation.SourceReferences, reference =>
                    reference.Value == $"aspire://{AspireLocalProjectionDocument.CurrentAspireVersion}");
                Assert.Contains(observation.SourceReferences, reference =>
                    reference.Value.Contains(applied.Projection.Fingerprint.Value, StringComparison.Ordinal));
            });
            Assert.True(assessment.IsReady, string.Join(Environment.NewLine, assessment.Diagnostics.Select(static item => item.Message)));
            Assert.True(observations.SequenceEqual(assessment.Observations));
        }
    }

    [Fact]
    public async Task Missing_current_notifications_remain_absent_for_canonical_missing_observation_diagnostics()
    {
        var (app, applied) = CreateAppliedApplication();
        await using (app)
        {
            var missing = applied.Services.OrderBy(static service => service.Key.Value, StringComparer.Ordinal).Last();
            foreach (var service in applied.Services.Where(service => service.Key != missing.Key))
                await PublishAsync(app.ResourceNotifications, service.Value.Resource, KnownResourceStates.Running, HealthStatus.Healthy);

            var assessment = AspireInfrastructureObservations.AssessCurrent(
                applied,
                app.ResourceNotifications,
                FreightMaterializationInfrastructure.CreatePhysicalRealization(),
                new DateTimeOffset(2026, 9, 5, 23, 31, 0, TimeSpan.Zero));

            Assert.DoesNotContain(assessment.Observations, observation => observation.PhysicalResource == missing.Key);
            Assert.Contains(assessment.Diagnostics, diagnostic =>
                diagnostic.Code == InfrastructureReadinessEvaluator.DiagnosticCodes.ObservationMissing
                && diagnostic.Evidence?.SourceReferences.Contains(
                    InfrastructureSourceReferences.PhysicalResource(missing.Key).Value,
                    StringComparer.Ordinal) == true);
        }
    }

    [Fact]
    public async Task Aspire_lifecycle_and_health_evidence_preserve_not_ready_reasons()
    {
        var (app, applied) = CreateAppliedApplication();
        await using (app)
        {
            var service = applied.Services.OrderBy(static item => item.Key.Value, StringComparer.Ordinal).First();
            var observedAt = new DateTimeOffset(2026, 9, 5, 23, 32, 0, TimeSpan.Zero);

            await PublishAsync(app.ResourceNotifications, service.Value.Resource, KnownResourceStates.Starting);
            var starting = Find(AspireInfrastructureObservations.CaptureCurrent(applied, app.ResourceNotifications, observedAt), service.Key);
            Assert.Equal(ExecutionHealthStatus.Unknown, starting.Health);
            Assert.Equal(ExecutionReadinessStatus.NotReady, starting.Readiness);
            Assert.Contains(starting.Diagnostics, diagnostic => diagnostic.Code == AspireInfrastructureObservations.DiagnosticCodes.ResourceNotReady);

            await PublishAsync(app.ResourceNotifications, service.Value.Resource, KnownResourceStates.FailedToStart);
            var failed = Find(AspireInfrastructureObservations.CaptureCurrent(applied, app.ResourceNotifications, observedAt), service.Key);
            Assert.Equal(ExecutionHealthStatus.Unhealthy, failed.Health);
            Assert.Equal(ExecutionReadinessStatus.NotReady, failed.Readiness);

            await PublishAsync(app.ResourceNotifications, service.Value.Resource, KnownResourceStates.Exited);
            var exited = Find(AspireInfrastructureObservations.CaptureCurrent(applied, app.ResourceNotifications, observedAt), service.Key);
            Assert.Equal(ExecutionHealthStatus.Unknown, exited.Health);
            Assert.Equal(ExecutionReadinessStatus.NotReady, exited.Readiness);
            Assert.Contains(exited.Diagnostics, diagnostic => diagnostic.Code == AspireInfrastructureObservations.DiagnosticCodes.ResourceNotReady);

            await PublishAsync(app.ResourceNotifications, service.Value.Resource, KnownResourceStates.Running, HealthStatus.Degraded);
            var degraded = Find(AspireInfrastructureObservations.CaptureCurrent(applied, app.ResourceNotifications, observedAt), service.Key);
            Assert.Equal(ExecutionHealthStatus.Degraded, degraded.Health);
            Assert.Equal(ExecutionReadinessStatus.NotReady, degraded.Readiness);
            Assert.Contains(degraded.Diagnostics, diagnostic =>
                diagnostic.Code == AspireInfrastructureObservations.DiagnosticCodes.HealthNotReady
                && diagnostic.Evidence?.Observed?.Contains("adapter-test=Degraded", StringComparison.Ordinal) == true);

            await PublishAsync(app.ResourceNotifications, service.Value.Resource, KnownResourceStates.Running, HealthStatus.Unhealthy);
            var unhealthy = Find(AspireInfrastructureObservations.CaptureCurrent(applied, app.ResourceNotifications, observedAt), service.Key);
            Assert.Equal(ExecutionHealthStatus.Unhealthy, unhealthy.Health);
            Assert.Equal(ExecutionReadinessStatus.NotReady, unhealthy.Readiness);
        }
    }

    [Fact]
    public async Task Missing_and_custom_states_are_observed_as_unknown_with_distinct_diagnostics()
    {
        var (app, applied) = CreateAppliedApplication();
        await using (app)
        {
            var service = applied.Services.OrderBy(static item => item.Key.Value, StringComparer.Ordinal).First();
            var observedAt = new DateTimeOffset(2026, 9, 5, 23, 33, 0, TimeSpan.Zero);

            await PublishAsync(app.ResourceNotifications, service.Value.Resource, state: null);
            var missing = Find(AspireInfrastructureObservations.CaptureCurrent(applied, app.ResourceNotifications, observedAt), service.Key);
            Assert.Equal(ExecutionReadinessStatus.Unknown, missing.Readiness);
            Assert.Contains(missing.Diagnostics, diagnostic =>
                diagnostic.Code == AspireInfrastructureObservations.DiagnosticCodes.StateMissing
                && diagnostic.Severity == DiagnosticSeverity.Warning);

            await PublishAsync(app.ResourceNotifications, service.Value.Resource, "Draining");
            var custom = Find(AspireInfrastructureObservations.CaptureCurrent(applied, app.ResourceNotifications, observedAt), service.Key);
            Assert.Equal(ExecutionReadinessStatus.Unknown, custom.Readiness);
            Assert.Contains(custom.Diagnostics, diagnostic =>
                diagnostic.Code == AspireInfrastructureObservations.DiagnosticCodes.StateUnsupported
                && diagnostic.Evidence?.Observed == "Draining");
        }
    }

    [Fact]
    public async Task Stale_resource_identity_fails_closed_without_reassigning_the_observation()
    {
        var (app, applied) = CreateAppliedApplication();
        await using (app)
        {
            var service = applied.Services.OrderBy(static item => item.Key.Value, StringComparer.Ordinal).First();
            var identity = Assert.Single(service.Value.Resource.Annotations.OfType<AspireInfraIdentityAnnotation>());
            service.Value.Resource.Annotations.Remove(identity);
            service.Value.Resource.Annotations.Add(new AspireInfraIdentityAnnotation(
                identity.LogicalNode,
                new InfrastructurePhysicalResourceId("local/stale-resource"),
                identity.LocalRealization,
                identity.Projection));
            await PublishAsync(app.ResourceNotifications, service.Value.Resource, KnownResourceStates.Running, HealthStatus.Healthy);

            var observation = Find(
                AspireInfrastructureObservations.CaptureCurrent(
                    applied,
                    app.ResourceNotifications,
                    new DateTimeOffset(2026, 9, 5, 23, 34, 0, TimeSpan.Zero)),
                service.Key);

            Assert.Equal(service.Key, observation.PhysicalResource);
            Assert.Equal(ExecutionHealthStatus.Unknown, observation.Health);
            Assert.Equal(ExecutionReadinessStatus.Unknown, observation.Readiness);
            Assert.Contains(observation.Diagnostics, diagnostic =>
                diagnostic.Code == AspireInfrastructureObservations.DiagnosticCodes.IdentityMismatch);

            var staleIdentity = Assert.Single(service.Value.Resource.Annotations.OfType<AspireInfraIdentityAnnotation>());
            service.Value.Resource.Annotations.Remove(staleIdentity);
            service.Value.Resource.Annotations.Add(new AspireInfraIdentityAnnotation(
                identity.LogicalNode,
                service.Key,
                identity.LocalRealization,
                new AspireLocalProjectionFingerprint(
                    AspireLocalProjectionFingerprint.CurrentAlgorithm,
                    AspireLocalProjectionFingerprint.CurrentCanonicalization,
                    new string('0', 64))));
            var staleProjection = Find(
                AspireInfrastructureObservations.CaptureCurrent(
                    applied,
                    app.ResourceNotifications,
                    new DateTimeOffset(2026, 9, 5, 23, 35, 0, TimeSpan.Zero)),
                service.Key);
            Assert.Contains(staleProjection.Diagnostics, diagnostic =>
                diagnostic.Code == AspireInfrastructureObservations.DiagnosticCodes.IdentityMismatch
                && diagnostic.Evidence?.Observed?.Contains(new string('0', 64), StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public void Observation_capture_rejects_non_utc_time_even_when_no_notification_exists()
    {
        var (app, applied) = CreateAppliedApplication();
        using (app)
        {
            Assert.Throws<ArgumentException>(() => AspireInfrastructureObservations.CaptureCurrent(
                applied,
                app.ResourceNotifications,
                new DateTimeOffset(2026, 9, 5, 16, 35, 0, TimeSpan.FromHours(-7))));
        }
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
                sourceReferences: ["linear://ARI-467", "test://aspire-command-health"])
        ]);

    static (DistributedApplication Application, AspireLocalApplication Applied) CreateAppliedApplication()
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
            projection,
            new AspireLocalApplicationOptions(
                operationWorkingDirectory: FindRepositoryRoot(),
                resolveSecret: static _ => "test-secret"));
        return (builder.Build(), applied);
    }

    static Task PublishAsync(
        ResourceNotificationService notifications,
        IResource resource,
        string? state,
        HealthStatus? health = null) => notifications.PublishUpdateAsync(
        resource,
        snapshot => (snapshot with { State = state }).WithHealthReports(
            health is null
                ? []
                : [new HealthReportSnapshot("adapter-test", health, Description: null, ExceptionText: null)]));

    static InfrastructureResourceObservation Find(
        ImmutableArray<InfrastructureResourceObservation> observations,
        InfrastructurePhysicalResourceId physicalResource) => Assert.Single(
        observations,
        observation => observation.PhysicalResource == physicalResource);

    static InfrastructureLocalRealizationDocument ProjectWorkloadSource()
    {
        InfrastructureNodeId workload = new("workload/ari-training-api");
        InfrastructurePhysicalResourceId physical = new("local/ari-training-api");
        var project = new InfrastructureLocalProjectSource(
            new("cohesive/infra-tests"),
            new("src/Cohesive.Infra.Tests/Cohesive.Infra.Tests.csproj"));
        var definition = InfrastructureDefinitionDocument.FromDefinition(new(
            id: new("ari-training-project-application-test"),
            revision: new("v1"),
            workloads: [new(workload)]));
        InfrastructureCapabilityVariantId variant = new("aspire-local");
        var profile = new InfrastructureCapabilityProfile(
            schemaVersion: InfrastructureCapabilityProfile.CurrentSchemaVersion,
            id: new("tests/ari-training/aspire-local/v1"),
            target: new("aspire"),
            supportedDefinitionSchemaVersions: [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            variants: [new(variant)]);
        var closure = InfrastructureCapabilityCompiler.Compile(definition, profile, variant);
        var realization = InfrastructureRealizationCompiler.Compile(
            closure,
            new InfrastructureLifecyclePlan(definition),
            workloadPlacements:
            [
                new(
                    workload: workload,
                    physicalResource: physical,
                    interpreter: new("aspire"),
                    sourceReferences: [project.Reference])
            ]);
        InfrastructureConfigurationSubject subject = new("environment/ari-training");
        InfrastructureSettingId projectName = new("project-name");
        var environment = new InfrastructureLocalEnvironmentProfile(
            id: new("tests/ari-training/aspire-local/v1"),
            authority: new("local/ari-training"),
            configurationSubject: subject,
            projectNameSetting: projectName,
            dataLifetime: InfrastructureLocalDataLifetime.Ephemeral,
            isolation: InfrastructureLocalEnvironmentIsolation.Shared);
        var topology = InfrastructureLocal.Define(local => local.ProjectService(
            workload: workload,
            physicalResource: physical,
            project: project));
        var configuration = new InfrastructureConventionProfile(
            id: new("ari-training/aspire-local/test/v1"),
            candidates:
            [
                new(
                    subject: subject,
                    setting: projectName,
                    value: "ari-training-local",
                    origin: EffectiveConfigurationOrigin.ScopedProfile,
                    authority: "ari-training/aspire-local/test/v1")
            ]);
        return InfrastructureLocalRealizationCompiler.Compile(realization, environment, topology, [configuration]);
    }

    static InfrastructureLocalRealizationDocument ReferencedServiceSource(
        InfrastructureTargetId consumer,
        InfrastructureLocalEndpointId? healthEndpoint = null,
        bool exposeGatewayOnHost = true)
    {
        InfrastructureNodeId workload = new("workload/api");
        InfrastructureNodeId cosmos = new("resource/cosmos");
        InfrastructurePhysicalResourceId apiPhysical = new("local/api");
        InfrastructurePhysicalResourceId cosmosPhysical = new("local/cosmos");
        InfrastructureLocalEndpointId representative = new("health");
        InfrastructureLocalEndpointId gateway = new("gateway");
        var project = new InfrastructureLocalProjectSource(
            new("cohesive/infra-tests"),
            new("src/Cohesive.Infra.Tests/Cohesive.Infra.Tests.csproj"));
        var definition = InfrastructureDefinitionDocument.FromDefinition(new(
            id: new("referenced-service-aspire-tests"),
            revision: new("v1"),
            workloads: [new(workload)],
            resources: [new(cosmos, InfrastructureResourceLifecycle.Ephemeral)],
            readinessDependencies:
            [
                new(
                    InfrastructureReadinessDependency.DeriveId(workload, cosmos),
                    workload,
                    cosmos)
            ]));
        InfrastructureCapabilityVariantId variant = new("aspire-local");
        var profile = new InfrastructureCapabilityProfile(
            schemaVersion: InfrastructureCapabilityProfile.CurrentSchemaVersion,
            id: new("tests/referenced-service/aspire-local/v1"),
            target: consumer,
            supportedDefinitionSchemaVersions: [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            variants: [new(variant)]);
        var closure = InfrastructureCapabilityCompiler.Compile(definition, profile, variant);
        InfrastructureLifecycleAuthorityId authority = new("local/ari");
        var realization = InfrastructureRealizationCompiler.Compile(
            closure,
            new InfrastructureLifecyclePlan(
                definition,
                bindings:
                [
                    new(
                        resource: cosmos,
                        physicalResource: cosmosPhysical,
                        interpreter: new("host/emulator-manager"),
                        authority: authority,
                        disposition: InfrastructureLifecycleDisposition.Managed),
                    new(
                        resource: cosmos,
                        physicalResource: cosmosPhysical,
                        interpreter: consumer,
                        authority: authority,
                        disposition: InfrastructureLifecycleDisposition.Referenced)
                ]),
            workloadPlacements:
            [
                new(
                    workload: workload,
                    physicalResource: apiPhysical,
                    interpreter: consumer,
                    sourceReferences: [project.Reference])
            ]);
        InfrastructureConfigurationSubject subject = new("environment/ari");
        InfrastructureSettingId projectName = new("project-name");
        InfrastructureSettingId gatewayPort = new("cosmos-gateway-port");
        InfrastructureSettingId healthPort = new("cosmos-health-port");
        var environment = new InfrastructureLocalEnvironmentProfile(
            id: new("tests/referenced-service/aspire-local/v1"),
            authority: authority,
            configurationSubject: subject,
            projectNameSetting: projectName,
            dataLifetime: InfrastructureLocalDataLifetime.Persistent,
            isolation: InfrastructureLocalEnvironmentIsolation.Shared);
        var topology = InfrastructureLocal.Define(local => local
            .ReferencedService(
                resource: cosmos,
                physicalResource: cosmosPhysical,
                interpreter: consumer,
                representativeEndpoint: representative,
                configure: service => service
                    .Endpoint(
                        id: gateway,
                        scheme: "https",
                        servicePort: 8081,
                        exposure: exposeGatewayOnHost
                            ? InfrastructureLocalEndpointExposure.HostLoopback
                            : InfrastructureLocalEndpointExposure.Internal,
                        role: InfrastructureLocalEndpointRole.Data,
                        hostPort: exposeGatewayOnHost ? new(subject, gatewayPort) : null)
                    .Endpoint(
                        id: representative,
                        scheme: "http",
                        servicePort: 8082,
                        exposure: InfrastructureLocalEndpointExposure.HostLoopback,
                        role: InfrastructureLocalEndpointRole.Management,
                        hostPort: new(subject, healthPort))
                    .HttpHealth(healthEndpoint ?? representative, "/health")
                    .HealthTiming(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), retries: 30))
            .ProjectService(
                workload: workload,
                physicalResource: apiPhysical,
                project: project,
                configure: api => api
                    .Environment(
                        "COSMOS_ENDPOINT",
                        new InfrastructureLocalEndpointValue(
                            service: cosmosPhysical,
                            endpoint: gateway,
                            address: InfrastructureLocalEndpointAddress.ServiceNetwork))
                    .Environment(
                        "COSMOS_HEALTH_ENDPOINT",
                        new InfrastructureLocalEndpointValue(
                            service: cosmosPhysical,
                            endpoint: representative,
                            address: InfrastructureLocalEndpointAddress.ServiceNetwork))));
        var configuration = new InfrastructureConventionProfile(
            id: new("tests/referenced-service/config/v1"),
            candidates:
            [
                new(subject, projectName, "ari-local", EffectiveConfigurationOrigin.ScopedProfile, "tests/referenced-service/config/v1"),
                new(subject, gatewayPort, "58081", EffectiveConfigurationOrigin.ScopedProfile, "tests/referenced-service/config/v1"),
                new(subject, healthPort, "58082", EffectiveConfigurationOrigin.ScopedProfile, "tests/referenced-service/config/v1")
            ]);
        return InfrastructureLocalRealizationCompiler.Compile(realization, environment, topology, [configuration]);
    }

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
