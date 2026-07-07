using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Cohesive.CodeGen.Cli;

/// <summary>
/// Isolated load context for contract assemblies and their dependencies.
/// </summary>
public sealed class ContractsAssemblyLoadContext : AssemblyLoadContext
{
    readonly AssemblyDependencyResolver resolver;
    readonly Dictionary<string, string> dependencyPaths;

    /// <summary>
    /// Creates the load context.
    /// </summary>
    public ContractsAssemblyLoadContext(string mainAssemblyPath)
        : base(isCollectible: true)
    {
        resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        dependencyPaths = BuildDependencyPathIndex(mainAssemblyPath);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        foreach (var assembly in Default.Assemblies)
        {
            if (AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName))
                return assembly;
        }

        var path = resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null && assemblyName.Name is not null)
            dependencyPaths.TryGetValue(assemblyName.Name, out path);

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    static Dictionary<string, string> BuildDependencyPathIndex(string mainAssemblyPath)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assemblyDirectory = Path.GetDirectoryName(mainAssemblyPath);
        if (assemblyDirectory is null)
            return paths;

        foreach (var dllPath in Directory.EnumerateFiles(assemblyDirectory, "*.dll"))
            paths.TryAdd(Path.GetFileNameWithoutExtension(dllPath), dllPath);

        var depsPath = Path.ChangeExtension(mainAssemblyPath, ".deps.json");
        if (!File.Exists(depsPath))
            return paths;

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("targets", out var targets) || !root.TryGetProperty("libraries", out var libraries))
            return paths;

        foreach (var target in targets.EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("runtime", out var runtime))
                    continue;

                var libraryName = library.Name;
                var libraryPath = GetLibraryPath(libraries, libraryName);
                foreach (var asset in runtime.EnumerateObject())
                {
                    if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var candidatePath = ResolveRuntimeAssetPath(assemblyDirectory, libraryPath, asset.Name);
                    if (candidatePath is null)
                        continue;

                    paths.TryAdd(Path.GetFileNameWithoutExtension(asset.Name), candidatePath);
                }
            }
        }

        return paths;
    }

    static string? GetLibraryPath(JsonElement libraries, string libraryName)
    {
        if (!libraries.TryGetProperty(libraryName, out var library))
            return null;

        if (!library.TryGetProperty("type", out var type) ||
            !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase))
            return null;

        return library.TryGetProperty("path", out var path) ? path.GetString() : null;
    }

    static string? ResolveRuntimeAssetPath(string assemblyDirectory, string? libraryPath, string runtimeAssetPath)
    {
        var localPath = Path.Combine(assemblyDirectory, runtimeAssetPath);
        if (File.Exists(localPath))
            return Path.GetFullPath(localPath);

        if (libraryPath is null)
            return null;

        foreach (var packageRoot in EnumerateNuGetPackageRoots())
        {
            var packagePath = Path.Combine(packageRoot, libraryPath, runtimeAssetPath);
            if (File.Exists(packagePath))
                return Path.GetFullPath(packagePath);
        }

        return null;
    }

    static IEnumerable<string> EnumerateNuGetPackageRoots()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            yield return configuredRoot;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".nuget", "packages");
    }
}
