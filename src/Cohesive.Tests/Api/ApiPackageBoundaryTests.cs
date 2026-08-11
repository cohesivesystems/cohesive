using System.Xml.Linq;
using Cohesive.Adapters.AspNet;
using Cohesive.Api;
using Cohesive.Api.Execution;

namespace Cohesive.Tests.Api;

public sealed class ApiPackageBoundaryTests
{
    [Fact]
    public void GenericApiAssembly_DoesNotReferenceAspNet()
    {
        var assembly = typeof(ApiDefinition).Assembly;
        var references = assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            references,
            static reference => reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(
            assembly.GetExportedTypes(),
            static type => type.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void GenericApiProject_DoesNotDeclareFrameworkReferences()
    {
        var root = RepositoryRoot();
        var apiProject = Path.Combine(root, "src", "Cohesive.Api", "Cohesive.Api.csproj");
        var document = XDocument.Load(apiProject);

        Assert.Empty(document.Descendants("FrameworkReference"));
    }

    [Fact]
    public void GenericApiAssembly_DoesNotReferenceProcesses()
    {
        var references = typeof(ApiDefinition).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            references,
            static reference => string.Equals(reference.Name, "Cohesive.Processes", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericApiProjectReferenceClosure_DoesNotAcquireProcesses()
    {
        var root = RepositoryRoot();
        var apiProject = Path.Combine(root, "src", "Cohesive.Api", "Cohesive.Api.csproj");
        var closure = ProjectReferenceClosure(apiProject);

        Assert.DoesNotContain(
            closure,
            static project => string.Equals(
                Path.GetFileName(project),
                "Cohesive.Processes.csproj",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ExecutionApiAssembly_ComposesGenericApiAndProcesses()
    {
        var assembly = typeof(ExecutionControlApiCatalog).Assembly;
        var references = assembly.GetReferencedAssemblies();

        Assert.Equal("Cohesive.Api.Execution", assembly.GetName().Name);
        Assert.Contains(
            references,
            static reference => string.Equals(reference.Name, "Cohesive.Api", StringComparison.Ordinal));
        Assert.Contains(
            references,
            static reference => string.Equals(reference.Name, "Cohesive.Processes", StringComparison.Ordinal));
        Assert.Contains(
            references,
            static reference => string.Equals(reference.Name, "Cohesive.Storage", StringComparison.Ordinal));
        Assert.Same(assembly, typeof(ExecutionControlResult).Assembly);
        Assert.Same(assembly, typeof(InMemoryExecutionControlApiAdapter).Assembly);
    }

    [Fact]
    public void AspNetAdapterAssembly_OwnsMinimalApiProjection()
    {
        var assembly = typeof(ApiEndpointRouteBuilderExtensions).Assembly;

        Assert.Equal("Cohesive.Adapters.AspNet", assembly.GetName().Name);
        Assert.Same(assembly, typeof(AspNetAuthorizationPolicyResolver).Assembly);
        Assert.NotSame(typeof(ApiDefinition).Assembly, assembly);
    }

    static IReadOnlySet<string> ProjectReferenceClosure(string rootProject)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(rootProject));

        while (pending.TryPop(out var project))
        {
            if (!projects.Add(project))
                continue;

            var projectDirectory = Path.GetDirectoryName(project)
                ?? throw new InvalidOperationException($"Project '{project}' has no containing directory.");
            var document = XDocument.Load(project);
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                    continue;

                var relativePath = include
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                pending.Push(Path.GetFullPath(Path.Combine(projectDirectory, relativePath)));
            }
        }

        return projects;
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cohesive.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Cohesive repository root.");
    }
}
