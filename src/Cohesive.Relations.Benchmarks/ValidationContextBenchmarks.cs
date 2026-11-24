using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm validation workspace costs with preconstructed values and complete validation enabled.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
public class ValidationContextBenchmarks
{
    PortableValue value = null!;

    /// <summary>Scalar, flat object, nested object, or bounded collection input.</summary>
    [Params("scalar", "flat", "nested", "collection", "large-collection")]
    public string Workload { get; set; } = "scalar";

    /// <summary>Constructs and warms a valid value; construction and cache population are excluded from timing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        TypeRef type = new ScalarTypeRef(ScalarTypeKind.Int64);
        var observation = ObservationValue.FromInt64(1);
        var depth = Workload switch { "scalar" => 0, "flat" => 1, "nested" => 8, _ => 2 };
        for (var index = 0; index < depth; index++)
        {
            type = new ObjectTypeRef([new("value", type)]);
            observation = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = observation });
        }
        if (Workload is "collection" or "large-collection")
        {
            type = new ArrayTypeRef(type);
            observation = ObservationValue.FromArray([.. Enumerable.Repeat(observation, Workload == "collection" ? 64 : 4096)]);
        }
        value = PortableValue.Concrete(new(type), observation);
        for (var index = 0; index < 128; index++)
            if (!Validate().IsValid) throw new InvalidOperationException("Invalid validation fixture.");
    }

    /// <summary>Validates the complete value using the standard public boundary.</summary>
    /// <returns>The validation result with deterministic diagnostics on failure.</returns>
    [Benchmark]
    public DocumentValidationResult Validate() => PortableExecutionValidator.Validate(value);
}
