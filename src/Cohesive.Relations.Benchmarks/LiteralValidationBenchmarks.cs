using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm literal selection and diagnostics; declarations and values are constructed outside measurement.</summary>
[MemoryDiagnoser]
public class LiteralValidationBenchmarks
{
    TypeRef type = null!;
    ShapeGraph? graph;
    ObservationValue value;
    [Params("enum-first", "enum-last", "enum-invalid", "named-first", "named-last", "named-alias", "named-invalid", "union-first", "union-last", "union-invalid", "object-first-error", "object-last-error", "object-valid")]
    public string Scenario { get; set; } = "enum-first";
    [GlobalSetup]
    public void Setup()
    {
        if (Scenario.StartsWith("enum"))
        {
            type = new EnumTypeRef("codes", [..Enumerable.Range(0, 128).Select(i => $"code{i}")]);
            value = ObservationValue.FromString(Scenario.EndsWith("first") ? "code0" : Scenario.EndsWith("last") ? "code127" : "unknown");
        }
        else if (Scenario.StartsWith("named"))
        {
            type = new NamedTypeRef(new("enum"));
            graph = new(new("enum"), [], [new TypeDefinition.Enum(new("enum"), PrimitiveType.String,
                [..Enumerable.Range(0, 128).Select(i => new EnumValue($"label{i}", $"code{i}"))])]);
            value = ObservationValue.FromString(Scenario.EndsWith("first") ? "code0" : Scenario.EndsWith("last") ? "code127"
                : Scenario.EndsWith("alias") ? "label127" : "unknown");
        }
        else if (Scenario.StartsWith("union"))
        {
            type = new NamedTypeRef(new("union"));
            var definition = new TypeDefinition.Union(new("union"), new UnionDiscriminator("kind"),
                [..Enumerable.Range(0, 128).Select(i => new UnionCase($"case{i}", new ObjectTypeRef([]), $"code{i}"))]);
            graph = new(new("union"), [], [definition]);
            value = ObservationValue.FromObject(ImmutableDictionary<string, ObservationValue>.Empty.Add("kind", ObservationValue.FromString(
                Scenario.EndsWith("first") ? "code0" : Scenario.EndsWith("last") ? "code127" : "unknown")));
        }
        else
        {
            type = new ObjectTypeRef([..Enumerable.Range(0, 128).Select(i => new ObjectFieldTypeDef($"field{i}", new ScalarTypeRef(ScalarTypeKind.String)))]);
            value = ObservationValue.FromObject(Enumerable.Range(0, 128).ToImmutableDictionary(i => $"field{i}", i =>
                (Scenario == "object-first-error" && i == 0) || (Scenario == "object-last-error" && i == 127)
                    ? ObservationValue.FromInt64(1) : ObservationValue.FromString("ok")));
        }
        for (var i = 0; i < 64; i++) _ = Validate();
    }
    [Benchmark]
    public bool Validate() => ObservationValidator.TryValidateAgainstType(value, type, out _, graph);
}
