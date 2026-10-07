using Cohesive.Relations.Authoring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cohesive.Tests.Relations;

public sealed class NestedQueryBuilderCompileTests
{
    [Theory]
    [InlineData("Identity(result => result.Id)")]
    [InlineData("Build(default, default, author.Parameter<string>(\"id\"))")]
    public void Mapping_and_build_are_unavailable_before_From(string operation)
    {
        var errors = Compile("author.SingleOrDefault<Result>()." + operation);
        Assert.Contains(errors, diagnostic => diagnostic.Id == "CS1061");
    }

    [Fact]
    public void Node_and_explicit_binding_both_infer_through_the_single_interface()
    {
        Assert.Empty(Compile("author.SingleOrDefault<Result>().From(rows, rows, row => row.Id).Identity(result => result.Id)"));
        Assert.Empty(Compile("author.SingleOrDefault<Result>().From(rows, rows.Binding, row => row.Id).Identity(result => result.Id)"));
    }

    static Diagnostic[] Compile(string expression)
    {
        var source = $$"""
            using Cohesive.Relations.Authoring;
            public sealed class Row { public string Id { get; set; } = ""; }
            public sealed class Result { public string Id { get; set; } = ""; }
            public static class Probe
            {
                public static object Define(RelationQueryExpressionAuthoring author, RelationQueryExpressionBoundNode<Row> rows)
                    => {{expression}};
            }
            """;
        var platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = platform.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(RelationQuery).Assembly.Location)
            .Append(typeof(Cohesive.Model.ObservationValue).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("QueryBuilderProbe", [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
    }
}
