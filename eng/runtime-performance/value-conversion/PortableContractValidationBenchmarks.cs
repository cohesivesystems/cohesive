using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

[MemoryDiagnoser]
public class PortableContractValidationBenchmarks
{
    [Params(1, 128)] public int Fields { get; set; }
    [Params("flat", "nested", "collection")] public string Shape { get; set; } = "flat";
    ValueContract contract = null!;
    PortableValue value = null!;

    [GlobalSetup]
    public void Setup()
    {
        var choice = new EnumTypeRef("Choice", ["yes", "no"]);
        var leaf = Shape == "flat" ? (TypeRef)choice : new ObjectTypeRef([new("choice", choice)]);
        var observation = Shape == "flat" ? ObservationValue.FromString("yes")
            : ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["choice"] = ObservationValue.FromString("yes") });
        if (Shape == "collection")
        {
            leaf = new ArrayTypeRef(leaf);
            observation = ObservationValue.FromArray([observation, observation]);
        }
        contract = new(new ObjectTypeRef([.. Enumerable.Range(0, Fields).Select(i => new ObjectFieldTypeDef($"f{i}", leaf))]));
        value = PortableValue.Concrete(contract, ObservationValue.FromObject(
            Enumerable.Range(0, Fields).ToDictionary(i => $"f{i}", _ => observation)));
        if (!Contract().IsValid || !Value().IsValid) throw new InvalidOperationException("Invalid benchmark fixture.");
    }

    [Benchmark] public DocumentValidationResult Contract() => PortableExecutionValidator.Validate(contract);
    [Benchmark] public DocumentValidationResult Value() => PortableExecutionValidator.Validate(value);
}
