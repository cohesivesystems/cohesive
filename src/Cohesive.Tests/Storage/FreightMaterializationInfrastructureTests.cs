using System.Text.Json;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Storage;

public sealed class FreightMaterializationInfrastructureTests
{
    [Fact]
    public void Canonical_fixture_covers_the_datastores_browser_interfaces_and_harness_operations()
    {
        var document = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.InteractiveProfile);

        Assert.True(document.IsValid, string.Join(Environment.NewLine, document.Diagnostics.Select(static item => item.Message)));
        Assert.Equal(5, document.Topology.Services.Length);
        Assert.Equal(4, document.Topology.Volumes.Length);
        Assert.Equal(
            ["inspect", "materialize", "reset", "seed", "start", "status", "stop", "verify"],
            document.Topology.Operations.Select(static operation => operation.Id.Value));
        Assert.Equal(
            ["cosmos", "explorer", "health"],
            document.Topology.Services.Single(service => service.PhysicalResource == FreightMaterializationInfrastructure.CosmosService)
                .Endpoints.Select(static endpoint => endpoint.Id.Value));
        Assert.Equal(
            InfrastructureLocalEndpointRole.UserInterface,
            document.Topology.Services.Single(service => service.PhysicalResource == FreightMaterializationInfrastructure.PgAdminService)
                .Endpoints.Single().Role);
        Assert.Equal(
            InfrastructureLocalEndpointRole.UserInterface,
            document.Topology.Services.Single(service => service.PhysicalResource == FreightMaterializationInfrastructure.KibanaService)
                .Endpoints.Single().Role);
        var cosmosHealth = document.Topology.Services.Single(service =>
            service.PhysicalResource == FreightMaterializationInfrastructure.CosmosService).Health;
        Assert.NotNull(cosmosHealth);
        Assert.Equal(TimeSpan.FromSeconds(3), cosmosHealth.Interval);
        Assert.Equal(TimeSpan.FromSeconds(5), cosmosHealth.Timeout);
        Assert.Equal(60, cosmosHealth.Retries);
        Assert.Equal(TimeSpan.FromSeconds(20), cosmosHealth.StartPeriod);

        var generated = Assert.Single(document.Topology.Files);
        var content = string.Concat(generated.Content.Select(segment => segment switch
        {
            InfrastructureLocalLiteralValue literal => literal.Value,
            InfrastructureLocalConfigurationValue reference => document.Configuration.Configuration.Single(value =>
                value.Subject == reference.Subject && value.Setting == reference.Setting).Value,
            _ => throw new InvalidOperationException($"Unexpected generated-file segment '{segment.GetType().Name}'.")
        }));
        using var parsed = JsonDocument.Parse(content);
        Assert.Equal(
            "cohesive_materialization",
            parsed.RootElement.GetProperty("Servers").GetProperty("1").GetProperty("MaintenanceDB").GetString());
    }

    [Fact]
    public void Interactive_and_isolated_profiles_have_distinct_explicit_retention_policy()
    {
        var interactive = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.InteractiveProfile);
        var isolated = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.IsolatedTestProfile,
            FreightMaterializationInfrastructure.CreateIsolatedProjectConfiguration("materialization-tests-01"));
        var unsafeIsolated = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.IsolatedTestProfile);

        Assert.Equal(InfrastructureLocalDataLifetime.Persistent, interactive.Environment.DataLifetime);
        Assert.Null(interactive.Environment.MaximumLifetime);
        Assert.Equal(InfrastructureLocalDataLifetime.Ephemeral, isolated.Environment.DataLifetime);
        Assert.Equal(TimeSpan.FromMinutes(30), isolated.Environment.MaximumLifetime);
        Assert.NotEqual(interactive.Fingerprint, isolated.Fingerprint);
        Assert.Contains(
            unsafeIsolated.Diagnostics,
            diagnostic => diagnostic.Code == InfrastructureLocalRealizationCompiler.DiagnosticCodes.IsolationConfigurationRequired);
    }

    [Fact]
    public void Explicit_worktree_configuration_overrides_defaults_with_attribution()
    {
        var overrides = new InfrastructureConventionProfile(
            id: new("worktree/ports/v1"),
            candidates:
            [
                new(
                    subject: FreightMaterializationInfrastructure.ConfigurationSubject,
                    setting: FreightMaterializationInfrastructure.Settings.PostgresPort,
                    value: "65432",
                    origin: EffectiveConfigurationOrigin.Explicit,
                    authority: "worktree/ports/v1"),
                new(
                    subject: FreightMaterializationInfrastructure.ConfigurationSubject,
                    setting: FreightMaterializationInfrastructure.Settings.CosmosPort,
                    value: "65081",
                    origin: EffectiveConfigurationOrigin.Explicit,
                    authority: "worktree/ports/v1")
            ]);

        var document = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.InteractiveProfile,
            overrides);
        var effective = document.Configuration.Configuration.Single(item =>
            item.Setting == FreightMaterializationInfrastructure.Settings.PostgresPort);
        var cosmos = document.Topology.Services.Single(service =>
            service.PhysicalResource == FreightMaterializationInfrastructure.CosmosService);
        var cosmosEnvironmentPort = Assert.IsType<InfrastructureLocalConfigurationValue>(
            cosmos.Environment.Single(variable => variable.Name == "PORT").Value);
        var cosmosEndpointPort = cosmos.Endpoints.Single(endpoint => endpoint.Id.Value == "cosmos").ServicePort;

        Assert.True(document.IsValid);
        Assert.Equal("65432", effective.Value);
        Assert.Equal(EffectiveConfigurationOrigin.Explicit, effective.Attribution.Origin);
        Assert.Equal("worktree/ports/v1", effective.Attribution.Authority);
        Assert.Equal(FreightMaterializationInfrastructure.Settings.CosmosPort, cosmosEnvironmentPort.Setting);
        Assert.Equal(FreightMaterializationInfrastructure.Settings.CosmosPort, cosmosEndpointPort.Configuration?.Setting);
        Assert.Equal(65081, cosmosEndpointPort.Resolve(document.Configuration));
    }

    [Fact]
    public void Canonical_fixture_round_trips_with_the_same_exact_fingerprint()
    {
        var first = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.InteractiveProfile);
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(first, options);
        var restored = JsonSerializer.Deserialize<InfrastructureLocalRealizationDocument>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(first.Fingerprint, restored.Fingerprint);
        Assert.Equal(first.Realization, restored.Realization);
        Assert.Equal(first.Topology.Services.Length, restored.Topology.Services.Length);
    }
}
