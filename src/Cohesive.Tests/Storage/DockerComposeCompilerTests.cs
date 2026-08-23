using Cohesive.Adapters.DockerCompose;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Local;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;

namespace Cohesive.Tests.Storage;

public sealed class DockerComposeCompilerTests
{
    [Fact]
    public void Same_exact_realization_emits_byte_identical_yaml_and_manifest()
    {
        var source = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.InteractiveProfile);

        var first = DockerComposeCompiler.Compile(source);
        var second = DockerComposeCompiler.Compile(source);

        Assert.True(first.IsSuccess, string.Join(Environment.NewLine, first.Diagnostics.Select(static item => item.Message)));
        Assert.Equal(first.Artifact!.Yaml, second.Artifact!.Yaml);
        Assert.Equal(first.Artifact.ManifestJson, second.Artifact.ManifestJson);
        Assert.Equal(first.Artifact.Manifest.YamlFingerprint, second.Artifact.Manifest.YamlFingerprint);
        Assert.Equal(source.Fingerprint, first.Artifact.Manifest.LocalRealization);
        Assert.Equal(source.Realization, first.Artifact.Manifest.SourceRealization);
    }

    [Fact]
    public void Default_artifact_exactly_matches_checked_in_golden_and_manifest()
    {
        var source = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.InteractiveProfile);
        var artifact = DockerComposeCompiler.Compile(source).Artifact!;
        var harnessRoot = Path.Combine(FindRepositoryRoot(), "eng", "materialization-harness");

        Assert.Equal(
            File.ReadAllText(Path.Combine(harnessRoot, "compose.generated.yaml")),
            artifact.Yaml);
        Assert.Equal(
            File.ReadAllText(Path.Combine(harnessRoot, "compose.generated.manifest.json")),
            artifact.ManifestJson + "\n");
    }

    [Fact]
    public void Freight_fixture_projects_every_current_compose_invariant()
    {
        var source = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.InteractiveProfile);
        var artifact = DockerComposeCompiler.Compile(source).Artifact!;

        Assert.Contains("name: 'cohesive-materialization-local'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("postgres:\n    image: 'postgres:17.10-alpine3.24'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("cosmos:\n    image: 'mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260810'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("elasticsearch:\n    image: 'docker.elastic.co/elasticsearch/elasticsearch:8.19.13'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("pgadmin:\n    image: 'dpage/pgadmin4:9.17'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("kibana:\n    image: 'docker.elastic.co/kibana/kibana:8.19.13'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("\"127.0.0.1:55432:5432\"", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("\"127.0.0.1:58082:1234\"", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("condition: service_healthy", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("interval: '3s'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("timeout: '5s'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("start_period: '20s'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("stop_grace_period: '30s'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("status=$$(curl", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("http://localhost:8080/ready", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("http://localhost:1234/", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("status == 200", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("postgres-data:/var/lib/postgresql/data", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("content: |-", artifact.Yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("cohesive-local-only", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("${COHESIVE_HARNESS_POSTGRES_PASSWORD:?", artifact.Yaml, StringComparison.Ordinal);
        Assert.Equal(5, artifact.Manifest.Services.Length);
        Assert.Equal(7, artifact.Manifest.Endpoints.Length);
        Assert.Equal(4, artifact.Manifest.Volumes.Length);
        Assert.Equal(8, artifact.Manifest.Operations.Length);
        Assert.Equal(InfrastructureLocalDataLifetime.Persistent, artifact.Manifest.DataLifetime);
        Assert.Equal(InfrastructureLocalEnvironmentIsolation.Shared, artifact.Manifest.Isolation);
        Assert.Equal(FreightMaterializationInfrastructure.LifecycleAuthority, artifact.Manifest.LifecycleAuthority);
    }

    [Fact]
    public void Explicit_profile_values_drive_project_ports_and_generated_configuration()
    {
        var overrideProfile = FreightMaterializationInfrastructure.CreateIsolatedProjectConfiguration(
            "materialization-tests-42");
        var source = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.IsolatedTestProfile,
            overrideProfile);

        var artifact = DockerComposeCompiler.Compile(source).Artifact!;

        Assert.Equal("materialization-tests-42", artifact.Manifest.ProjectName);
        Assert.Equal(TimeSpan.FromMinutes(30), artifact.Manifest.MaximumLifetime);
        Assert.Equal(InfrastructureLocalDataLifetime.Ephemeral, artifact.Manifest.DataLifetime);
        Assert.Equal(InfrastructureLocalEnvironmentIsolation.Isolated, artifact.Manifest.Isolation);
        Assert.Contains("name: 'materialization-tests-42'", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("\"MaintenanceDB\": \"cohesive_materialization\"", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains("\"Username\": \"cohesive\"", artifact.Yaml, StringComparison.Ordinal);
        Assert.Contains(artifact.Manifest.Operations, operation =>
            operation.Effect == InfrastructureLocalOperationEffect.EnvironmentMutation
            && operation.MutationAuthority == artifact.Manifest.LifecycleAuthority);
    }

    [Fact]
    public void Invalid_source_and_compose_name_collisions_fail_without_artifacts()
    {
        var invalidSource = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.IsolatedTestProfile);
        var invalid = DockerComposeCompiler.Compile(invalidSource);
        Assert.False(invalid.IsSuccess);
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Code == DockerComposeCompiler.DiagnosticCodes.SourceInvalid);

        var canonical = FreightMaterializationInfrastructure.CreateTopology();
        var collidingTopology = new InfrastructureLocalTopology(
            services: canonical.Services,
            volumes: [.. canonical.Volumes, new(new("a/shared")), new(new("b/shared"))],
            files: canonical.Files,
            operations: canonical.Operations);
        var collidingSource = InfrastructureLocalRealizationCompiler.Compile(
            realization: FreightMaterializationInfrastructure.CreatePhysicalRealization(),
            environment: FreightMaterializationInfrastructure.InteractiveProfile,
            topology: collidingTopology,
            configurationProfiles: [FreightMaterializationInfrastructure.CreateDefaultConfiguration()]);

        var collision = DockerComposeCompiler.Compile(collidingSource);

        Assert.False(collision.IsSuccess);
        Assert.Contains(collision.Diagnostics, diagnostic => diagnostic.Code == DockerComposeCompiler.DiagnosticCodes.NameCollision);

        var invalidNameProfile = new InfrastructureConventionProfile(
            id: new("materialization-harness/invalid-project/v1"),
            candidates:
            [
                new(
                    subject: FreightMaterializationInfrastructure.ConfigurationSubject,
                    setting: FreightMaterializationInfrastructure.Settings.ProjectName,
                    value: "Invalid Project",
                    origin: EffectiveConfigurationOrigin.Explicit,
                    authority: "materialization-harness/invalid-project/v1")
            ]);
        var invalidNameSource = FreightMaterializationInfrastructure.CreateLocalRealization(
            FreightMaterializationInfrastructure.InteractiveProfile,
            invalidNameProfile);

        var invalidName = DockerComposeCompiler.Compile(invalidNameSource);

        Assert.False(invalidName.IsSuccess);
        Assert.Contains(invalidName.Diagnostics, diagnostic => diagnostic.Code == DockerComposeCompiler.DiagnosticCodes.NameInvalid);
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
        throw new InvalidOperationException($"Cannot locate the repository root from '{AppContext.BaseDirectory}'.");
    }
}
