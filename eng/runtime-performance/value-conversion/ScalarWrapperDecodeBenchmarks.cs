using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Execution;

[MemoryDiagnoser]
public class ScalarWrapperDecodeBenchmarks
{
    static readonly ValueContract Contract = new(new JsonTypeRef());
    PortableValue wrappers = null!;

    [Params(1, 128, 1024)] public int Fields { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        wrappers = PortableValue.Concrete(Contract, ObservationValue.FromObject(new
        { Values = Enumerable.Range(0, Fields).Select(i => $"field{i}").ToArray() }));
        if (DecodeWrappers().Success?.Values.Length != Fields)
            throw new InvalidOperationException("Decode benchmark differs from the declared contract.");
    }

    [Benchmark]
    public Cohesive.Prelude.Result<WrapperCollection, DocumentValidationDiagnostic> DecodeWrappers() =>
        HostedQueryValueAdapter.Decode<WrapperCollection>(wrappers, Contract);

    public sealed record WrapperCollection(ImmutableArray<ShapeId> Values);
}
