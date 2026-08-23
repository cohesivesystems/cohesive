using System.Text;
using Cohesive.Adapters.DockerCompose;
using Cohesive.MaterializationHarness.Model;

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
        FreightMaterializationInfrastructure.CreateRuntimeConfiguration())
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
