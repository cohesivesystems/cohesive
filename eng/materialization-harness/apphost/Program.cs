using System.Collections.Immutable;
using System.Text;
using Aspire.Hosting;
using Cohesive.Adapters.Aspire;
using Cohesive.Infra.Local;
using Cohesive.MaterializationHarness.Model;

var projectionOnly = args.Length == 1 && string.Equals(args[0], "--projection", StringComparison.Ordinal);
if (args.Contains("--projection", StringComparer.Ordinal) && !projectionOnly)
{
    Console.Error.WriteLine("Usage: dotnet run --project eng/materialization-harness/apphost [--projection]");
    return 2;
}

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var profileName = Environment.GetEnvironmentVariable("COHESIVE_HARNESS_ASPIRE_PROFILE") ?? "interactive";
InfrastructureLocalEnvironmentProfile environment = profileName switch
{
    "interactive" => FreightMaterializationInfrastructure.InteractiveProfile,
    "isolated" => FreightMaterializationInfrastructure.IsolatedTestProfile,
    _ => throw new InvalidOperationException(
        $"Unsupported COHESIVE_HARNESS_ASPIRE_PROFILE '{profileName}'; expected 'interactive' or 'isolated'.")
};
var runtimeConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COHESIVE_HARNESS_PROJECT_NAME"));
if (environment.Isolation == InfrastructureLocalEnvironmentIsolation.Isolated && !runtimeConfigured)
    throw new InvalidOperationException("The isolated Aspire profile requires explicit harness runtime configuration.");
var source = runtimeConfigured
    ? FreightMaterializationInfrastructure.CreateLocalRealization(
        environment: environment,
        additionalConfiguration: FreightMaterializationInfrastructure.CreateRuntimeConfiguration())
    : FreightMaterializationInfrastructure.CreateLocalRealization(environment: environment);
var compilerOptions = new AspireLocalCompilerOptions(
    commandHealthOverrides:
    [
        new(
            physicalResource: FreightMaterializationInfrastructure.PostgresService,
            executable: "pg_isready",
            arguments: ["--dbname=$POSTGRES_DB", "--username=$POSTGRES_USER"],
            strategy: AspireCommandHealthOverrideStrategy.TcpConnect,
            endpoint: new("postgres"),
            rationale: "Aspire 13.5.2 has no stable command-probe API; a fenced TCP listener check supplies AppHost readiness while the exact pg_isready contract remains in canonical Infra.",
            sourceReferences: ["ARI-467", "aspire-health-api/stable/13.5.2"])
    ]);
var compilation = AspireLocalCompiler.Compile(source: source, options: compilerOptions);
if (!compilation.IsSuccess)
{
    foreach (var diagnostic in compilation.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
    return 1;
}

var projection = compilation.Projection!;
if (projectionOnly)
{
    Console.WriteLine(projection.ToJson());
    return 0;
}

var runtimeRoot = Path.Combine(repositoryRoot, "eng", "materialization-harness", ".runtime");
Directory.CreateDirectory(runtimeRoot);
File.WriteAllText(
    path: Path.Combine(runtimeRoot, "aspire.manifest.json"),
    contents: projection.ToJson() + "\n",
    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    AllowUnsecuredTransport = false,
    TrustDeveloperCertificate = false,
    DeveloperCertificateDefaultHttpsTerminationEnabled = false
});
builder.AddCohesiveLocalInfrastructure(
    projection: projection,
    options: new AspireLocalApplicationOptions(
        operationWorkingDirectory: repositoryRoot,
        resolveSecret: Environment.GetEnvironmentVariable,
        operationEnvironment: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COHESIVE_HARNESS_SKIP_INFRA_UP"] = "true"
        }));
var application = builder.Build();
Console.WriteLine($"aspire-projection={projection.Fingerprint.Value}");
if (projection.Environment.MaximumLifetime is not { } maximumLifetime)
{
    await application.RunAsync();
    return 0;
}

using CancellationTokenSource maximumLifetimeCancellation = new(maximumLifetime);
Console.WriteLine($"aspire-maximum-lifetime={maximumLifetime}");
await application.RunAsync(maximumLifetimeCancellation.Token);
return 0;

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
