using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cohesive.Tests.Analyzers;

/// <summary>
/// Compile-time coverage for quantity wrapper source generation.
/// </summary>
public sealed class QuantityWrapperGeneratorCompileTimeTests
{
    [Fact]
    public void QuantityWrapperGenerator_EmitsExpectedSurface()
    {
        var source = """
                     using System;
                     using System.Numerics;
                     
                     namespace Cohesive.Domain;
                     public interface IQuantityDimension;
                     public interface IQuantityUnit<TDimension, TRep> where TDimension : struct, IQuantityDimension where TRep : IFloatingPoint<TRep>
                     {
                         static abstract string Symbol { get; }
                         static abstract TRep ToBase(TRep value);
                         static abstract TRep FromBase(TRep value);
                     }
                     
                     public readonly record struct Quantity<TDimension, TRep>(TRep BaseValue) where TDimension : struct, IQuantityDimension where TRep : IFloatingPoint<TRep>
                     {
                         public static Quantity<TDimension, TRep> Zero => new(TRep.Zero);
                         public static Quantity<TDimension, TRep> From<TUnit>(TRep value) where TUnit : struct, IQuantityUnit<TDimension, TRep> => new(TUnit.ToBase(value));
                         public static Quantity<TDimension, TRep> operator +(Quantity<TDimension, TRep> left, Quantity<TDimension, TRep> right) => new(left.BaseValue + right.BaseValue);
                         public static Quantity<TDimension, TRep> operator -(Quantity<TDimension, TRep> left, Quantity<TDimension, TRep> right) => new(left.BaseValue - right.BaseValue);
                         public static Quantity<TDimension, TRep> operator -(Quantity<TDimension, TRep> value) => new(-value.BaseValue);
                         public static Quantity<TDimension, TRep> operator *(Quantity<TDimension, TRep> value, TRep scalar) => new(value.BaseValue * scalar);
                         public static Quantity<TDimension, TRep> operator /(Quantity<TDimension, TRep> value, TRep scalar) => new(value.BaseValue / scalar);
                         public static TRep operator /(Quantity<TDimension, TRep> left, Quantity<TDimension, TRep> right) => left.BaseValue / right.BaseValue;
                     }
                     
                     public interface IStructuredQuantity<TSelf, TDimension, TRep>
                         where TSelf : struct, IStructuredQuantity<TSelf, TDimension, TRep>
                         where TDimension : struct, IQuantityDimension
                         where TRep : IFloatingPoint<TRep>
                     {
                         Quantity<TDimension, TRep> Value { get; }
                         static abstract TSelf FromValue(Quantity<TDimension, TRep> value);
                     }
                     
                     public static class QuantityMath
                     {
                         public static TRep As<TQuantity, TDimension, TRep, TUnit>(TQuantity quantity)
                             where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
                             where TDimension : struct, IQuantityDimension
                             where TRep : IFloatingPoint<TRep>
                             where TUnit : struct, IQuantityUnit<TDimension, TRep>
                             => TRep.Zero;
                     
                         public static string Format<TQuantity, TDimension, TRep, TUnit>(TQuantity quantity, string? format = null, IFormatProvider? formatProvider = null)
                             where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
                             where TDimension : struct, IQuantityDimension
                             where TRep : IFloatingPoint<TRep>
                             where TUnit : struct, IQuantityUnit<TDimension, TRep>
                             => string.Empty;
                     
                         public static TQuantity Add<TQuantity, TDimension, TRep>(TQuantity left, TQuantity right)
                             where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
                             where TDimension : struct, IQuantityDimension
                             where TRep : IFloatingPoint<TRep>
                             => TQuantity.FromValue(left.Value + right.Value);
                     
                         public static TQuantity Subtract<TQuantity, TDimension, TRep>(TQuantity left, TQuantity right)
                             where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
                             where TDimension : struct, IQuantityDimension
                             where TRep : IFloatingPoint<TRep>
                             => TQuantity.FromValue(left.Value - right.Value);
                     
                         public static TQuantity Negate<TQuantity, TDimension, TRep>(TQuantity value)
                             where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
                             where TDimension : struct, IQuantityDimension
                             where TRep : IFloatingPoint<TRep>
                             => TQuantity.FromValue(-value.Value);
                     
                         public static TQuantity Scale<TQuantity, TDimension, TRep>(TQuantity value, TRep scalar)
                             where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
                             where TDimension : struct, IQuantityDimension
                             where TRep : IFloatingPoint<TRep>
                             => TQuantity.FromValue(value.Value * scalar);
                     
                         public static TQuantity Divide<TQuantity, TDimension, TRep>(TQuantity value, TRep scalar)
                             where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
                             where TDimension : struct, IQuantityDimension
                             where TRep : IFloatingPoint<TRep>
                             => TQuantity.FromValue(value.Value / scalar);
                     
                         public static TRep Ratio<TQuantity, TDimension, TRep>(TQuantity left, TQuantity right)
                             where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
                             where TDimension : struct, IQuantityDimension
                             where TRep : IFloatingPoint<TRep>
                             => left.Value / right.Value;
                     
                         public static int Compare<TQuantity, TDimension, TRep>(TQuantity left, TQuantity right)
                             where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
                             where TDimension : struct, IQuantityDimension
                             where TRep : IFloatingPoint<TRep>
                             => 0;
                     }
                     
                     [AttributeUsage(validOn: AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
                     public sealed class QuantityAttribute : Attribute
                     {
                         public QuantityAttribute(Type defaultUnitType, string defaultFormat = "0.###") { }
                     }
                     
                     [AttributeUsage(validOn: AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
                     public sealed class QuantityUnitMemberAttribute : Attribute
                     {
                         public QuantityUnitMemberAttribute(Type unitType, string memberName) { }
                     }
                     
                     namespace Sample;
                     using System.Numerics;
                     
                     public readonly record struct LengthDimension : Cohesive.Domain.IQuantityDimension;
                     public readonly record struct Meter<TRep> : Cohesive.Domain.IQuantityUnit<LengthDimension, TRep> where TRep : IFloatingPoint<TRep>
                     {
                         public static string Symbol => "m";
                         public static TRep ToBase(TRep value) => value;
                         public static TRep FromBase(TRep value) => value;
                     }
                     
                     public readonly record struct Kilometer<TRep> : Cohesive.Domain.IQuantityUnit<LengthDimension, TRep> where TRep : IFloatingPoint<TRep>
                     {
                         public static string Symbol => "km";
                         public static TRep ToBase(TRep value) => value;
                         public static TRep FromBase(TRep value) => value;
                     }
                     
                     [Cohesive.Domain.Quantity(defaultUnitType: typeof(Kilometer<decimal>), defaultFormat: "0.###")]
                     [Cohesive.Domain.QuantityUnitMember(unitType: typeof(Meter<decimal>), memberName: "Meters")]
                     [Cohesive.Domain.QuantityUnitMember(unitType: typeof(Kilometer<decimal>), memberName: "Kilometers")]
                     public readonly partial record struct Distance(Cohesive.Domain.Quantity<LengthDimension, decimal> Value)
                         : Cohesive.Domain.IStructuredQuantity<Distance, LengthDimension, decimal>, IComparable<Distance>
                     {
                     }
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation);
        var generatedText = string.Join(
            separator: Environment.NewLine,
            values: runResult.Results.SelectMany(selector: static result => result.GeneratedSources).Select(selector: static source => source.SourceText.ToString()));

        Assert.Contains(expectedSubstring: "FromMeters(", actualString: generatedText);
        Assert.Contains(expectedSubstring: "Meters =>", actualString: generatedText);
        Assert.Contains(expectedSubstring: "AdditiveIdentity =>", actualString: generatedText);
        Assert.Contains(expectedSubstring: "operator +(", actualString: generatedText);
        Assert.Contains(expectedSubstring: "CompareTo(", actualString: generatedText);
    }

    [Fact]
    public void QuantityWrapperGenerator_ReportsDiagnostic_WhenWrapperIsNotPartial()
    {
        var source = """
                     using System;
                     using System.Numerics;
                     
                     namespace Cohesive.Domain;
                     public interface IQuantityDimension;
                     public interface IQuantityUnit<TDimension, TRep> where TDimension : struct, IQuantityDimension where TRep : IFloatingPoint<TRep> { }
                     public readonly record struct Quantity<TDimension, TRep>(TRep BaseValue) where TDimension : struct, IQuantityDimension where TRep : IFloatingPoint<TRep>;
                     [AttributeUsage(validOn: AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
                     public sealed class QuantityAttribute : Attribute
                     {
                         public QuantityAttribute(Type defaultUnitType, string defaultFormat = "0.###") { }
                     }
                     [AttributeUsage(validOn: AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
                     public sealed class QuantityUnitMemberAttribute : Attribute
                     {
                         public QuantityUnitMemberAttribute(Type unitType, string memberName) { }
                     }
                     
                     namespace Sample;
                     public readonly record struct LengthDimension : Cohesive.Domain.IQuantityDimension;
                     public readonly record struct Meter<TRep> : Cohesive.Domain.IQuantityUnit<LengthDimension, TRep> where TRep : IFloatingPoint<TRep> { }
                     
                     [Cohesive.Domain.Quantity(defaultUnitType: typeof(Meter<decimal>))]
                     [Cohesive.Domain.QuantityUnitMember(unitType: typeof(Meter<decimal>), memberName: "Meters")]
                     public readonly record struct InvalidDistance(Cohesive.Domain.Quantity<LengthDimension, decimal> Value);
                     """;

        var compilation = CreateCompilation(source: source);
        var runResult = RunGenerator(compilation: compilation);
        var diagnostics = runResult.Results.SelectMany(selector: static result => result.Diagnostics).ToArray();

        Assert.Contains(collection: diagnostics, filter: static diagnostic => diagnostic.Id == "COHQTY001");
    }

    static GeneratorDriverRunResult RunGenerator(CSharpCompilation compilation)
    {
        IIncrementalGenerator generator = new QuantityWrapperSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());
        driver = driver.RunGenerators(compilation: compilation);
        return driver.GetRunResult();
    }

    static CSharpCompilation CreateCompilation(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(text: source, options: parseOptions);
        var compilationOptions = new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        var references = GetMetadataReferences();
        return CSharpCompilation.Create(
            assemblyName: "QuantityCompileTimeTests",
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
                        or "System.Runtime.Extensions"
                        or "System.Numerics")
                .Append(element: typeof(Binder).Assembly)
                .Append(element: typeof(QuantityWrapperSourceGenerator).Assembly)
                .Distinct()
                .Select(selector: static assembly => MetadataReference.CreateFromFile(path: assembly.Location));

        return [..assemblies];
    }
}
