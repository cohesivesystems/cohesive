using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cohesive.Tests.Analyzers;

/// <summary>
/// Compile-time coverage for code-set source generation.
/// </summary>
public sealed class CodeSetGeneratorCompileTimeTests
{
    [Fact]
    public void CodeSetGenerator_EmitsCatalogFromTypeAndFieldMetadata()
    {
        var source = """
                     using System;
                     
                     namespace Cohesive.Domain
                     {
                         [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
                         public sealed class CodeSetAttribute : Attribute
                         {
                             public CodeSetAttribute() { }
                             public CodeSetAttribute(string label) { Label = label; }
                             public string? Label { get; }
                             public string? Description { get; set; }
                         }
                         
                         public readonly record struct CodeDefinition<T>(string Name, T Value, string Label, string? Description = null);
                     }
                     
                     namespace Sample
                     {
                         [Cohesive.Domain.CodeSet]
                         public static partial class SampleCodes
                         {
                             public const string AlphaCode = "A";
                         
                             [Cohesive.Domain.CodeSet("Beta Label", Description = "Beta description")]
                             public const string BetaCode = "B";
                         }
                     }
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation, out var outputCompilation);
        var generatedText = string.Join(
            separator: Environment.NewLine,
            values: runResult.Results.SelectMany(static result => result.GeneratedSources).Select(static source => source.SourceText.ToString()));

        Assert.DoesNotContain(outputCompilation.GetDiagnostics(), static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("IReadOnlyList<string> All", generatedText);
        Assert.Contains("""new("AlphaCode", AlphaCode, "Alpha Code", null)""", generatedText);
        Assert.Contains("""new("BetaCode", BetaCode, "Beta Label", "Beta description")""", generatedText);
        Assert.Contains("public static bool TryGet(string value", generatedText);
    }

    [Fact]
    public void CodeSetGenerator_ReportsDiagnostic_WhenCodeSetIsNotPartial()
    {
        var source = """
                     using System;
                     
                     namespace Cohesive.Domain
                     {
                         [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
                         public sealed class CodeSetAttribute : Attribute
                         {
                             public CodeSetAttribute() { }
                             public CodeSetAttribute(string label) { Label = label; }
                             public string? Label { get; }
                             public string? Description { get; set; }
                         }
                         
                         public readonly record struct CodeDefinition<T>(string Name, T Value, string Label, string? Description = null);
                     }
                     
                     namespace Sample
                     {
                         [Cohesive.Domain.CodeSet]
                         public static class InvalidCodes
                         {
                             public const string One = "1";
                         }
                     }
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation, out _);
        var diagnostics = runResult.Results.SelectMany(static result => result.Diagnostics).ToArray();

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "COHCODE001");
    }

    [Fact]
    public void CodeSetGenerator_ReportsDiagnostic_ForDuplicateConstantValues()
    {
        var source = """
                     using System;
                     
                     namespace Cohesive.Domain
                     {
                         [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
                         public sealed class CodeSetAttribute : Attribute
                         {
                             public CodeSetAttribute() { }
                             public CodeSetAttribute(string label) { Label = label; }
                             public string? Label { get; }
                             public string? Description { get; set; }
                         }
                         
                         public readonly record struct CodeDefinition<T>(string Name, T Value, string Label, string? Description = null);
                     }
                     
                     namespace Sample
                     {
                         [Cohesive.Domain.CodeSet]
                         public static partial class DuplicateCodes
                         {
                             public const int First = 1;
                             public const int Second = 1;
                         }
                     }
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation, out _);
        var diagnostics = runResult.Results.SelectMany(static result => result.Diagnostics).ToArray();

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "COHCODE005");
    }

    [Fact]
    public void CodeSetGenerator_EmitsEnumCodeExtensionsFromCodeSet()
    {
        var source = """
                     using System;
                     
                     namespace Cohesive.Domain
                     {
                         [AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
                         public sealed class CodeSetAttribute : Attribute
                         {
                             public CodeSetAttribute() { }
                             public CodeSetAttribute(string label) { Label = label; }
                             public string? Label { get; }
                             public string? Description { get; set; }
                         }
                         
                         [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
                         public sealed class CodeAttribute : Attribute
                         {
                             public CodeAttribute(string code) { Code = code; }
                             public string Code { get; }
                             public string? Label { get; set; }
                             public string? Description { get; set; }
                         }
                         
                         public readonly record struct CodeDefinition<T>(string Name, T Value, string Label, string? Description = null);
                     }
                     
                     namespace Sample
                     {
                         [Cohesive.Domain.CodeSet]
                         public enum ShipmentWeightCode
                         {
                             [Cohesive.Domain.Code("R", Label = "Carrier Scale Weight", Description = "Carrier weighed")]
                             CarrierScale = 1,
                             Unannotated = 2
                         }
                         
                         public static class Usage
                         {
                             public static string Code => ShipmentWeightCode.CarrierScale.GetCode();
                             public static string Label => ShipmentWeightCode.CarrierScale.GetLabel();
                             public static string? Description => ShipmentWeightCode.CarrierScale.GetDescription();
                             public static ShipmentWeightCode Parsed => ShipmentWeightCode.Parse("R");
                             public static System.Collections.Generic.IReadOnlyList<Cohesive.Domain.CodeDefinition<string>> All => ShipmentWeightCode.GetAll();
                         }
                     }
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation, out var outputCompilation);
        var generatedText = string.Join(
            separator: Environment.NewLine,
            values: runResult.Results.SelectMany(static result => result.GeneratedSources).Select(static source => source.SourceText.ToString()));

        Assert.DoesNotContain(outputCompilation.GetDiagnostics(), static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("extension(ShipmentWeightCode value)", generatedText);
        Assert.Contains("public string GetCode()", generatedText);
        Assert.Contains("""new("CarrierScale", "R", "Carrier Scale Weight", "Carrier weighed")""", generatedText);
        Assert.Contains("""new("Unannotated", "2", "Unannotated", null)""", generatedText);
        Assert.Contains("extension(ShipmentWeightCode)", generatedText);
        Assert.Contains("public static global::Sample.ShipmentWeightCode Parse(string code)", generatedText);
        Assert.Contains("GetAll() => Definitions", generatedText);
        Assert.Contains("/// Gets the external code value for this enum value.", generatedText);
    }

    static GeneratorDriverRunResult RunGenerator(CSharpCompilation compilation, out Compilation outputCompilation)
    {
        IIncrementalGenerator generator = new CodeSetSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator.AsSourceGenerator()],
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation: compilation,
            outputCompilation: out outputCompilation,
            diagnostics: out _);
        return driver.GetRunResult();
    }

    static CSharpCompilation CreateCompilation(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(text: source, options: parseOptions);
        var compilationOptions = new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        var references = GetMetadataReferences();
        return CSharpCompilation.Create(
            assemblyName: "CodeSetCompileTimeTests",
            syntaxTrees: [syntaxTree],
            references: references,
            options: compilationOptions);
    }

    static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var assemblies =
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(static assembly => !assembly.IsDynamic)
                .Where(static assembly => !string.IsNullOrWhiteSpace(assembly.Location))
                .Where(static assembly =>
                    assembly.GetName().Name is "System.Private.CoreLib"
                        or "System.Runtime"
                        or "netstandard"
                        or "System.Console"
                        or "System.Linq"
                        or "System.Collections"
                        or "System.ObjectModel"
                        or "System.Runtime.Extensions")
                .Append(typeof(Binder).Assembly)
                .Append(typeof(CodeSetSourceGenerator).Assembly)
                .Distinct()
                .Select(static assembly => MetadataReference.CreateFromFile(assembly.Location));

        return [.. assemblies];
    }
}
