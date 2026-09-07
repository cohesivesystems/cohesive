using BenchmarkDotNet.Attributes;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm portable validation over flat, nested and bounded collection values.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
public class ValueContractValidationBenchmarks
{
    ValueContract contract = null!;
    ObservationValue value;

    /// <summary>Representative validation structure; construction is excluded from timing.</summary>
    [Params("scalar", "nested", "collection", "large-collection")]
    public string Workload { get; set; } = "scalar";

    /// <summary>Constructs a scalar, eight nested objects, or a collection of 64/4,096 two-level objects.</summary>
    [GlobalSetup]
    public void Setup()
    {
        TypeRef type = new ScalarTypeRef(ScalarTypeKind.Int64);
        value = ObservationValue.FromInt64(1);
        var depth = Workload == "scalar" ? 0 : Workload == "nested" ? 8 : 2;
        for (var index = 0; index < depth; index++)
        {
            type = new ObjectTypeRef([new("value", type)]);
            value = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = value });
        }
        if (Workload is "collection" or "large-collection")
        {
            type = new ArrayTypeRef(type);
            value = ObservationValue.FromArray([.. Enumerable.Repeat(value, Workload == "collection" ? 64 : 4096)]);
        }
        contract = new(type);
        if (!contract.IsSatisfiedByConstant(value)) throw new InvalidOperationException("Invalid validation fixture.");
    }

    /// <summary>Checks the retained portable value without constructing diagnostic or callback objects.</summary>
    /// <returns>True for the successfully validated fixture.</returns>
    [Benchmark]
    public bool Validate() => contract.IsSatisfiedByConstant(value);
}
