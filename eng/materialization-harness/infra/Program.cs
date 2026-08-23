using System.Collections.Immutable;
using System.Text;
using Cohesive.Adapters.DockerCompose;
using Cohesive.Infra.Configuration;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;

var check = args.Length == 1 && string.Equals(args[0], "--check", StringComparison.Ordinal);
var runtime = args.Length == 1 && string.Equals(args[0], "--runtime", StringComparison.Ordinal);
if (args.Length > 1 || args.Length == 1 && !check && !runtime)
{
    Console.Error.WriteLine("Usage: dotnet run --project eng/materialization-harness/infra [--check|--runtime]");
    return 2;
}

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var harnessRoot = Path.Combine(repositoryRoot, "eng", "materialization-harness");
var outputRoot = runtime ? Path.Combine(harnessRoot, ".runtime") : harnessRoot;
var yamlPath = Path.Combine(outputRoot, runtime ? "compose.yaml" : "compose.generated.yaml");
var manifestPath = Path.Combine(outputRoot, runtime ? "compose.manifest.json" : "compose.generated.manifest.json");
var source = runtime
    ? FreightMaterializationInfrastructure.CreateLocalRealization(
        FreightMaterializationInfrastructure.InteractiveProfile,
        CreateRuntimeConfiguration())
    : FreightMaterializationInfrastructure.CreateLocalRealization(
        FreightMaterializationInfrastructure.InteractiveProfile);
var compilation = DockerComposeCompiler.Compile(source);
if (!compilation.IsSuccess)
{
    foreach (var diagnostic in compilation.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
    return 1;
}

var artifact = compilation.Artifact!;
var expectedYaml = artifact.Yaml;
var expectedManifest = artifact.ManifestJson + "\n";
if (check)
{
    var valid = Check(yamlPath, expectedYaml) & Check(manifestPath, expectedManifest);
    if (!valid)
    {
        Console.Error.WriteLine("Run the generator without --check and review both generated artifacts.");
        return 1;
    }
    Console.WriteLine($"compose-artifact={artifact.Manifest.YamlFingerprint.Value}");
    return 0;
}

Directory.CreateDirectory(outputRoot);
File.WriteAllText(yamlPath, expectedYaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
File.WriteAllText(manifestPath, expectedManifest, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"wrote={Path.GetRelativePath(repositoryRoot, yamlPath)}");
Console.WriteLine($"wrote={Path.GetRelativePath(repositoryRoot, manifestPath)}");
Console.WriteLine($"compose-artifact={artifact.Manifest.YamlFingerprint.Value}");
return 0;

static InfrastructureConventionProfile CreateRuntimeConfiguration()
{
    const string authority = "materialization-harness/runtime-environment/v1";
    (InfrastructureSettingId Setting, string EnvironmentVariable)[] bindings =
    [
        (FreightMaterializationInfrastructure.Settings.ProjectName, "COHESIVE_HARNESS_PROJECT_NAME"),
        (FreightMaterializationInfrastructure.Settings.PostgresPort, "COHESIVE_HARNESS_POSTGRES_PORT"),
        (FreightMaterializationInfrastructure.Settings.PostgresDatabase, "COHESIVE_HARNESS_POSTGRES_DATABASE"),
        (FreightMaterializationInfrastructure.Settings.PostgresUser, "COHESIVE_HARNESS_POSTGRES_USER"),
        (FreightMaterializationInfrastructure.Settings.CosmosPort, "COHESIVE_HARNESS_COSMOS_PORT"),
        (FreightMaterializationInfrastructure.Settings.CosmosHealthPort, "COHESIVE_HARNESS_COSMOS_HEALTH_PORT"),
        (FreightMaterializationInfrastructure.Settings.CosmosExplorerPort, "COHESIVE_HARNESS_COSMOS_EXPLORER_PORT"),
        (FreightMaterializationInfrastructure.Settings.ElasticsearchPort, "COHESIVE_HARNESS_ELASTIC_PORT"),
        (FreightMaterializationInfrastructure.Settings.ElasticsearchJavaOptions, "COHESIVE_HARNESS_ELASTIC_JAVA_OPTS"),
        (FreightMaterializationInfrastructure.Settings.KibanaPort, "COHESIVE_HARNESS_KIBANA_PORT"),
        (FreightMaterializationInfrastructure.Settings.PgAdminPort, "COHESIVE_HARNESS_PGADMIN_PORT"),
        (FreightMaterializationInfrastructure.Settings.PgAdminEmail, "COHESIVE_HARNESS_PGADMIN_EMAIL")
    ];
    var candidates = ImmutableArray.CreateBuilder<InfrastructureConfigurationCandidate>(bindings.Length);
    foreach (var binding in bindings)
    {
        var value = Environment.GetEnvironmentVariable(binding.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Runtime configuration requires environment variable '{binding.EnvironmentVariable}'.");
        candidates.Add(new(
            subject: FreightMaterializationInfrastructure.ConfigurationSubject,
            setting: binding.Setting,
            value: value,
            origin: EffectiveConfigurationOrigin.Explicit,
            authority: authority));
    }
    return new(
        id: new(authority),
        candidates: candidates.MoveToImmutable());
}

static bool Check(string path, string expected)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"missing={path}");
        return false;
    }
    var actual = File.ReadAllText(path);
    if (string.Equals(actual, expected, StringComparison.Ordinal))
        return true;
    Console.Error.WriteLine($"stale={path}");
    return false;
}

static string FindRepositoryRoot(string start)
{
    DirectoryInfo? directory = new(Path.GetFullPath(start));
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Cohesive.sln"))
            && Directory.Exists(Path.Combine(directory.FullName, "eng", "materialization-harness")))
        {
            return directory.FullName;
        }
        directory = directory.Parent;
    }
    throw new InvalidOperationException($"Cannot find the Cohesive repository root from '{start}'.");
}
