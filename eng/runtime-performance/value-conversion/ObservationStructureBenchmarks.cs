using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;


[MemoryDiagnoser]
public class ObservationStructureBenchmarks
{
    [Params(8, 128, 1024)] public int Fields { get; set; }
    [Params("flat", "nested", "collection")] public string Shape { get; set; } = "flat";
    readonly ValueContract contract = new(new JsonTypeRef());
    PortableValue value = null!;

    [GlobalSetup]
    public void Setup()
    {
        value = Create();
        if (!Repeated().IsValid) throw new InvalidOperationException("Invalid fixture.");
    }

    PortableValue Create()
    {
        var fields = new Dictionary<string, ObservationValue>(StringComparer.Ordinal);
        for (var i = 0; i < Fields; i++)
        {
            var item = ObservationValue.FromString("payload");
            if (Shape != "flat") item = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = item });
            if (Shape == "collection") item = ObservationValue.FromArray([item, item]);
            fields.Add($"f{i}", item);
        }
        return PortableValue.Concrete(contract, ObservationValue.FromObject(fields));
    }

    [Benchmark] public DocumentValidationResult Repeated() => PortableExecutionValidator.Validate(value);
    // Construction is included deliberately: one-shot values must not pay unbounded preparation cost.
    [Benchmark] public DocumentValidationResult CreateAndValidate() => PortableExecutionValidator.Validate(Create());
}
