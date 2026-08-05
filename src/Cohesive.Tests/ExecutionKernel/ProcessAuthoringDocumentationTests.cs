using System.ComponentModel;
using System.Reflection;
using Cohesive.Processes.Authoring;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessAuthoringDocumentationTests
{
    const string SourceStart = "// <docs:sequential-process>";
    const string SourceEnd = "// </docs:sequential-process>";
    const string ReadmeStart = "<!-- <docs:sequential-process> -->\n```csharp";
    const string ReadmeEnd = "```\n<!-- </docs:sequential-process> -->";

    [Fact]
    public void ReadmeSequentialProcess_IsTheExecutableComputationFixture()
    {
        var repository = RepositoryRoot();
        var fixture = File.ReadAllText(
            Path.Combine(
                repository,
                "src",
                "Cohesive.Tests",
                "ExecutionKernel",
                "ProcessComputationAuthoringTests.cs"));
        var readme = File.ReadAllText(
            Path.Combine(repository, "src", "Cohesive.Processes", "README.md"));

        Assert.Equal(
            Extract(fixture, SourceStart, SourceEnd),
            Extract(readme.ReplaceLineEndings("\n"), ReadmeStart, ReadmeEnd));
    }

    [Fact]
    public void BuilderSurface_IsAdvancedAndTheRedundantCollectionDslIsAbsent()
    {
        Assert.Equal(
            EditorBrowsableState.Advanced,
            typeof(ProcessBuilder<,>).GetCustomAttribute<EditorBrowsableAttribute>()?.State);

        var createMethods = typeof(ProcessAuthoring)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static method => method.Name == nameof(ProcessAuthoring.Create))
            .ToArray();
        Assert.NotEmpty(createMethods);
        Assert.All(
            createMethods,
            static method => Assert.Equal(
                EditorBrowsableState.Advanced,
                method.GetCustomAttribute<EditorBrowsableAttribute>()?.State));

        Assert.Null(
            typeof(ProcessAuthoring).Assembly.GetType(
                "Cohesive.Processes.Authoring.ProcessExpressionAuthoring`2",
                throwOnError: false));
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

    static string Extract(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing documentation marker '{startMarker}'.");
        start += startMarker.Length;

        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Missing documentation marker '{endMarker}'.");
        return text[start..end].Trim().ReplaceLineEndings("\n");
    }
}
