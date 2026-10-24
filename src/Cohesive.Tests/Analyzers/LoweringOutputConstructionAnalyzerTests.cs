using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Analyzers;
using Cohesive.Relations.IR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Cohesive.Tests.Analyzers;

/// <summary>
/// Compile-time coverage for COHIR001, direct construction of canonical IR outside lowering.
/// </summary>
public sealed class LoweringOutputConstructionAnalyzerTests
{
    // The marker attributes and a miniature IR library, compiled as a separate assembly so that construction in the
    // consumer crosses an assembly boundary the way application code constructing Relations IR does.
    const string LibrarySource = """
        using System;
        namespace Cohesive.Prelude
        {
            [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
            public sealed class LoweringOutputAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
            public sealed class IrProducerAttribute : Attribute
            {
                public IrProducerAttribute(string reason) => Reason = reason;
                public string Reason { get; }
            }
        }

        namespace Sample.IR
        {
            [Cohesive.Prelude.LoweringOutput]
            public abstract record Node(string Id);

            public sealed record FilterNode(string Id, string Predicate) : Node(Id);

            [Cohesive.Prelude.LoweringOutput]
            public sealed record Definition(string Id);

            public sealed record PageDefinition(int Limit);

            public static class Lowering
            {
                public static Definition Lower(string id) => new(id);
            }
        }
        """;

    [Fact]
    public async Task Constructing_a_marked_type_in_another_assembly_is_reported()
    {
        var diagnostics = await AnalyzeConsumerAsync("""
            using Sample.IR;
            public static class Consumer
            {
                public static Definition Build() => new Definition("hand-built");
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("COHIR001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Definition", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Constructing_a_type_derived_from_a_marked_base_is_reported()
    {
        var diagnostics = await AnalyzeConsumerAsync("""
            using Sample.IR;
            public static class Consumer
            {
                public static Node Build() => new FilterNode("n1", "status == Active");
            }
            """);

        Assert.Equal("COHIR001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task Target_typed_construction_is_reported()
    {
        var diagnostics = await AnalyzeConsumerAsync("""
            using Sample.IR;
            public static class Consumer
            {
                public static Definition Build()
                {
                    Definition definition = new("hand-built");
                    return definition;
                }
            }
            """);

        Assert.Equal("COHIR001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task Copying_a_marked_type_with_with_is_reported()
    {
        var diagnostics = await AnalyzeConsumerAsync("""
            using Sample.IR;
            public static class Consumer
            {
                public static Definition Rename(Definition definition) => definition with { Id = "renamed" };
            }
            """);

        Assert.Equal("COHIR001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task Unmarked_value_objects_and_lowered_results_are_not_reported()
    {
        var diagnostics = await AnalyzeConsumerAsync("""
            using Sample.IR;
            public static class Consumer
            {
                public static PageDefinition Page() => new PageDefinition(2);
                public static Definition Build() => Lowering.Lower("lowered");
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task An_assembly_declaring_IrProducer_is_not_reported()
    {
        var diagnostics = await AnalyzeConsumerAsync("""
            using Sample.IR;
            [assembly: Cohesive.Prelude.IrProducer("compiler tests")]
            public static class Consumer
            {
                public static Definition Build() => new Definition("fixture");
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task The_declaring_assembly_may_construct_its_own_lowering_outputs()
    {
        var diagnostics = await AnalyzeAsync(CreateCompilation("Library", LibrarySource));

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(typeof(FilterQueryNode))]
    [InlineData(typeof(PageQueryNode))]
    [InlineData(typeof(RelationDefinition))]
    [InlineData(typeof(QueryDefinition))]
    [InlineData(typeof(LogicalQueryDefinition))]
    [InlineData(typeof(HostedQueryDefinition))]
    public void Relations_definition_roots_and_nodes_are_marked(Type type)
    {
        Assert.True(IsMarked(type), $"{type.Name} should be a lowering output.");
    }

    [Theory]
    [InlineData(typeof(OffsetPageDefinition))]
    [InlineData(typeof(QueryOrdering))]
    public void Relations_authoring_value_objects_are_not_marked(Type type)
    {
        Assert.False(IsMarked(type), $"{type.Name} is an authoring input and should not be a lowering output.");
    }

    static bool IsMarked(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetCustomAttribute<LoweringOutputAttribute>(inherit: false) is not null)
                return true;
        }

        return false;
    }

    static Task<ImmutableArray<Diagnostic>> AnalyzeConsumerAsync(string consumerSource)
    {
        var library = CreateCompilation("Library", LibrarySource);
        var consumer = CreateCompilation("Consumer", consumerSource, library.ToMetadataReference());
        return AnalyzeAsync(consumer);
    }

    static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(CSharpCompilation compilation)
    {
        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var withAnalyzers = compilation.WithAnalyzers([new LoweringOutputConstructionAnalyzer()]);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    static CSharpCompilation CreateCompilation(
        string assemblyName,
        string source,
        params MetadataReference[] additionalReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            text: source,
            options: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [syntaxTree],
            references: [.. FrameworkReferences.Value, .. additionalReferences],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    static readonly Lazy<ImmutableArray<MetadataReference>> FrameworkReferences = new(static () =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(static path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "netstandard.dll")
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ]);
}
