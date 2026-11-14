using BenchmarkDotNet.Attributes;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Separates absent-member lookup from materialization; neither candidate retains negative lookup evidence.</summary>
[MemoryDiagnoser]
public class MissingOptionalConstraintBenchmarks
{
    [Params(16, 1024)] public int OtherFields { get; set; }
    [Params(false, true)] public bool OwnedStorage { get; set; }
    IReadOnlyDictionary<string, ObservationValue> fields = null!;
    ObservationValue value;
    ValueContract contract = null!;

    [GlobalSetup]
    public void Setup()
    {
        var input = Enumerable.Range(0, OtherFields).ToDictionary(i => "other" + i, _ => ObservationValue.FromString("value"));
        value = ObservationValue.FromObject(input);
        fields = OwnedStorage ? value.Fields! : input;
        contract = new(new ObjectTypeRef([new("id", new ScalarTypeRef(ScalarTypeKind.String), presence: FieldPresence.Optional)]),
            constraints: [new MaxLengthConstraint(2, FieldPath.FromField("id"))]);
        if (!ValidateContract() || Lookup()) throw new InvalidOperationException("Invalid optional-field fixture.");
    }

    // Previous physical fallback retained only as a benchmark control.
    [Benchmark(Baseline = true)]
    public bool InterfaceScan() => PreviousLookup(fields, "id");

    static bool PreviousLookup(IReadOnlyDictionary<string, ObservationValue> fields, string name)
    {
        if (fields.TryGetValue(name, out _)) return true;
        foreach (var field in fields)
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    [Benchmark]
    public bool Lookup() => ConstraintSemantics.TryGetField(fields, "id", out _);

    [Benchmark]
    public bool ValidateContract() => contract.IsSatisfiedByConstant(value);
}
