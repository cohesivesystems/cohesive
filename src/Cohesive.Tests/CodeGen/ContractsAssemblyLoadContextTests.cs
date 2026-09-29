using System.Reflection;
using System.Runtime.Loader;
using Cohesive.CodeGen.Cli;

namespace Cohesive.Tests.CodeGen;

public sealed class ContractsAssemblyLoadContextTests
{
    [Fact]
    public void Load_SharesAvailableDefaultDependencyBeforeItHasBeenLoaded()
    {
        // A local copy must not create a second type identity merely because the default
        // context has not needed this dependency yet. Choose a cold platform dependency
        // rather than depending on test ordering for a particular assembly's first use.
        var loaded = AssemblyLoadContext.Default.Assemblies.Select(a => a.GetName().Name).ToHashSet();
        var candidate = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .First(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal)
                && !loaded.Contains(Path.GetFileNameWithoutExtension(path)));
        var directory = Path.Combine(Path.GetTempPath(), "cohesive-contract-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ContractsAssemblyLoadContext? context = null;
        try
        {
            var main = Path.Combine(directory, "contracts.dll");
            File.Copy(typeof(ContractsAssemblyLoadContextTests).Assembly.Location, main);
            File.Copy(candidate, Path.Combine(directory, Path.GetFileName(candidate)));
            context = new ContractsAssemblyLoadContext(main);
            var dependency = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(candidate));
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(dependency));
        }
        finally
        {
            context?.Unload();
            Directory.Delete(directory, recursive: true);
        }
    }
}
