using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cohesive.Tests.Analyzers;

/// <summary>
/// Compile-time coverage for union generation and analyzer diagnostics.
/// </summary>
public sealed class UnionGeneratorCompileTimeTests
{
    [Fact]
    public void UnionGenerator_EmitsExpectedSourceSurface()
    {
        var source = """
                     using System;
                     namespace Cohesive.Prelude;
                     [AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
                     public sealed class UnionAttribute : Attribute
                     {
                         public UnionAttribute(string discriminatorPropertyName = "Type") => DiscriminatorPropertyName = discriminatorPropertyName;
                         public string DiscriminatorPropertyName { get; }
                     }
                     
                     namespace Sample;
                     public enum LocalResultType
                     {
                         Ok = 1,
                         Err = 2
                     }
                     
                     [Cohesive.Prelude.Union]
                     public readonly partial record struct LocalResult(LocalResultType Type, int? Ok, string? Err);
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation);
        var generatedSources = runResult.Results
            .SelectMany(selector: static result => result.GeneratedSources)
            .ToArray();
        var generatedText = string.Join(
            separator: Environment.NewLine,
            values: generatedSources.Select(selector: static sourceText => sourceText.SourceText.ToString()));

        Assert.Contains(expectedSubstring: "public enum Either2Type", actualString: generatedText);
        Assert.Contains(expectedSubstring: "None = 0", actualString: generatedText);
        Assert.Contains(expectedSubstring: "public bool IsOk()", actualString: generatedText);
        Assert.Contains(expectedSubstring: "public bool IsErr()", actualString: generatedText);
        Assert.Contains(expectedSubstring: "IDiscriminatedUnion.CaseIndex", actualString: generatedText);
        Assert.Contains(expectedSubstring: "MaybeNullWhen(returnValue: false)] out int?", actualString: generatedText);
        Assert.Contains(expectedSubstring: "EitherJsonConverter", actualString: generatedText);
        Assert.Contains(expectedSubstring: "WriteString(propertyName: \"type\", value: \"Case1\")", actualString: generatedText);
        Assert.Contains(expectedSubstring: "DebuggerDisplay", actualString: generatedText);
        Assert.DoesNotContain(expectedSubstring: "GetType(", actualString: generatedText);
        Assert.DoesNotContain(expectedSubstring: "System.Reflection", actualString: generatedText);
    }

    [Fact]
    public void UnionGenerator_ReportsDiagnostic_WhenUnionTypeIsNotPartial()
    {
        var source = """
                     using System;
                     namespace Cohesive.Prelude;
                     [AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
                     public sealed class UnionAttribute : Attribute
                     {
                         public UnionAttribute(string discriminatorPropertyName = "Type") => DiscriminatorPropertyName = discriminatorPropertyName;
                         public string DiscriminatorPropertyName { get; }
                     }
                     
                     namespace Sample;
                     [Cohesive.Prelude.Union]
                     public readonly record struct InvalidUnion(int Value);
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation);
        var diagnostics = runResult.Results.SelectMany(selector: static result => result.Diagnostics).ToArray();

        Assert.Contains(collection: diagnostics, filter: static diagnostic => diagnostic.Id == "COHDU001");
    }

    [Fact]
    public async Task UnionUsageAnalyzer_ReportsDiagnostic_ForCatchAllSwitchOnUnionAsync()
    {
        var source = """
                     using System;
                     
                     namespace Cohesive.Prelude;
                     public interface IDiscriminatedUnion
                     {
                         int CaseCount { get; }
                         int CaseIndex { get; }
                         object? CaseValue { get; }
                     }
                     
                     namespace Sample;
                     public readonly struct LocalUnion : Cohesive.Prelude.IDiscriminatedUnion
                     {
                         public int CaseCount => 2;
                         public int CaseIndex => 0;
                         public object? CaseValue => 1;
                         public int Match(Func<int, int> onA, Func<string, int> onB) => onA(1);
                     }
                     
                     public static class Usage
                     {
                         public static int Evaluate(LocalUnion value) => value switch { _ => 0 };
                     }
                     """;

        var diagnostics = await RunAnalyzerAsync(source: source);
        Assert.Contains(collection: diagnostics, filter: static diagnostic => diagnostic.Id == "COHDU100");
    }

    [Fact]
    public async Task UnionUsageAnalyzer_ReportsDiagnostic_ForNullMatchCallbackAsync()
    {
        var source = """
                     using System;
                     
                     namespace Cohesive.Prelude;
                     public interface IDiscriminatedUnion
                     {
                         int CaseCount { get; }
                         int CaseIndex { get; }
                         object? CaseValue { get; }
                     }
                     
                     namespace Sample;
                     public readonly struct LocalUnion : Cohesive.Prelude.IDiscriminatedUnion
                     {
                         public int CaseCount => 2;
                         public int CaseIndex => 0;
                         public object? CaseValue => 1;
                         public int Match(Func<int, int> onA, Func<string, int> onB) => onA(1);
                     }
                     
                     public static class Usage
                     {
                         public static int Evaluate(LocalUnion value) => value.Match(onA: null, onB: text => text.Length);
                     }
                     """;

        var diagnostics = await RunAnalyzerAsync(source: source);
        Assert.Contains(collection: diagnostics, filter: static diagnostic => diagnostic.Id == "COHDU101");
    }

    [Fact]
    public void UnionGenerator_AllowsUserDefinedEitherBeyondConfiguredBuiltInArity()
    {
        var source = """
                     using System;
                     namespace Cohesive.Prelude;
                     [AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
                     public sealed class UnionAttribute : Attribute
                     {
                         public UnionAttribute(string discriminatorPropertyName = "Type") => DiscriminatorPropertyName = discriminatorPropertyName;
                         public string DiscriminatorPropertyName { get; }
                     }

                     namespace Sample;
                     public enum Either17Type
                     {
                         Case1 = 1,
                         Case2 = 2,
                         Case3 = 3,
                         Case4 = 4,
                         Case5 = 5,
                         Case6 = 6,
                         Case7 = 7,
                         Case8 = 8,
                         Case9 = 9,
                         Case10 = 10,
                         Case11 = 11,
                         Case12 = 12,
                         Case13 = 13,
                         Case14 = 14,
                         Case15 = 15,
                         Case16 = 16,
                         Case17 = 17,
                     }

                     [Cohesive.Prelude.Union]
                     public readonly partial record struct Either<TCase1, TCase2, TCase3, TCase4, TCase5, TCase6, TCase7, TCase8, TCase9, TCase10, TCase11, TCase12, TCase13, TCase14, TCase15, TCase16, TCase17>(
                         Either17Type Type,
                         TCase1? Case1,
                         TCase2? Case2,
                         TCase3? Case3,
                         TCase4? Case4,
                         TCase5? Case5,
                         TCase6? Case6,
                         TCase7? Case7,
                         TCase8? Case8,
                         TCase9? Case9,
                         TCase10? Case10,
                         TCase11? Case11,
                         TCase12? Case12,
                         TCase13? Case13,
                         TCase14? Case14,
                         TCase15? Case15,
                         TCase16? Case16,
                         TCase17? Case17);
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation);
        var diagnostics = runResult.Results.SelectMany(selector: static result => result.Diagnostics).ToArray();
        Assert.DoesNotContain(collection: diagnostics, filter: static diagnostic => diagnostic.Id == "COHDU005");

        var generatedText = string.Join(
            separator: Environment.NewLine,
            values: runResult.Results.SelectMany(selector: static result => result.GeneratedSources).Select(selector: static source => source.SourceText.ToString()));
        Assert.Contains(expectedSubstring: ": global::Cohesive.Prelude.IDiscriminatedUnion", actualString: generatedText);
        Assert.DoesNotContain(
            expectedSubstring: "IEither<TCase1, TCase2, TCase3, TCase4, TCase5, TCase6, TCase7, TCase8, TCase9, TCase10, TCase11, TCase12, TCase13, TCase14, TCase15, TCase16, TCase17>",
            actualString: generatedText);
    }

    [Fact]
    public void UnionGenerator_SupportsGenericSubtypeUnion_WithTopLevelCases()
    {
        var source = """
                     using System;
                     namespace Cohesive.Prelude;
                     [AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
                     public sealed class UnionAttribute : Attribute
                     {
                         public UnionAttribute(string discriminatorPropertyName = "Type") => DiscriminatorPropertyName = discriminatorPropertyName;
                         public string DiscriminatorPropertyName { get; }
                     }

                     namespace Sample;
                     [Cohesive.Prelude.Union]
                     public abstract partial record LinkedList<T>;
                     
                     public sealed record Nil<T> : LinkedList<T>;
                     
                     public sealed record Cons<T>(T Head, LinkedList<T> Tail) : LinkedList<T>;
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation);
        var diagnostics = runResult.Results.SelectMany(selector: static result => result.Diagnostics).ToArray();
        var generatedText = string.Join(
            separator: Environment.NewLine,
            values: runResult.Results.SelectMany(selector: static result => result.GeneratedSources).Select(selector: static source => source.SourceText.ToString()));

        Assert.DoesNotContain(collection: diagnostics, filter: static diagnostic => diagnostic.Id == "COHDU003");
        Assert.DoesNotContain(
            collection: diagnostics,
            filter: static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains(expectedSubstring: "partial record LinkedList<T>", actualString: generatedText);
        Assert.Contains(expectedSubstring: "public bool IsCons()", actualString: generatedText);
        Assert.Contains(expectedSubstring: "public bool IsNil()", actualString: generatedText);
    }

    [Fact]
    public void UnionGenerator_SupportsGenericSubtypeUnion_WithNestedCases()
    {
        var source = """
                     using System;
                     namespace Cohesive.Prelude;
                     [AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
                     public sealed class UnionAttribute : Attribute
                     {
                         public UnionAttribute(string discriminatorPropertyName = "Type") => DiscriminatorPropertyName = discriminatorPropertyName;
                         public string DiscriminatorPropertyName { get; }
                     }

                     namespace Sample;
                     [Cohesive.Prelude.Union]
                     public abstract partial record LinkedList<T>
                     {
                         public sealed record Nil : LinkedList<T>;
                         public sealed record Cons(T Head, LinkedList<T> Tail) : LinkedList<T>;
                     }
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation);
        var diagnostics = runResult.Results.SelectMany(selector: static result => result.Diagnostics).ToArray();
        var generatedText = string.Join(
            separator: Environment.NewLine,
            values: runResult.Results.SelectMany(selector: static result => result.GeneratedSources).Select(selector: static source => source.SourceText.ToString()));

        Assert.DoesNotContain(collection: diagnostics, filter: static diagnostic => diagnostic.Id == "COHDU003");
        Assert.DoesNotContain(
            collection: diagnostics,
            filter: static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains(expectedSubstring: "partial record LinkedList<T>", actualString: generatedText);
        Assert.Contains(expectedSubstring: "public bool IsCons()", actualString: generatedText);
        Assert.Contains(expectedSubstring: "public bool IsNil()", actualString: generatedText);
    }

    static GeneratorDriverRunResult RunGenerator(CSharpCompilation compilation)
    {
        IIncrementalGenerator generator = new UnionSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());
        driver = driver.RunGenerators(compilation: compilation);
        return driver.GetRunResult();
    }

    static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string source)
    {
        var compilation = CreateCompilation(source: source);
        var analyzer = new UnionUsageAnalyzer();
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(analyzer);
        var diagnostics = await compilation.WithAnalyzers(analyzers: analyzers).GetAnalyzerDiagnosticsAsync();
        return diagnostics;
    }

    static CSharpCompilation CreateCompilation(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(text: source, options: parseOptions);
        var compilationOptions = new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        var references = GetMetadataReferences();
        return CSharpCompilation.Create(
            assemblyName: "UnionCompileTimeTests",
            syntaxTrees: [syntaxTree],
            references: references,
            options: compilationOptions);
    }

    static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var assemblies =
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(predicate: static assembly => !assembly.IsDynamic)
                .Where(predicate: static assembly => !string.IsNullOrWhiteSpace(value: assembly.Location))
                .Where(predicate: static assembly =>
                    assembly.GetName().Name is "System.Private.CoreLib"
                        or "System.Runtime"
                        or "netstandard"
                        or "System.Console"
                        or "System.Linq"
                        or "System.Collections"
                        or "System.ObjectModel"
                        or "System.Runtime.Extensions")
                .Append(element: typeof(Binder).Assembly)
                .Append(element: typeof(UnionSourceGenerator).Assembly)
                .Distinct()
                .Select(selector: static assembly => MetadataReference.CreateFromFile(path: assembly.Location));

        return [..assemblies];
    }
}
