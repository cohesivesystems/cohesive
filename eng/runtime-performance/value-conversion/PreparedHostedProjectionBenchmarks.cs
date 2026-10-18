using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Execution;

// Measures the real adapter, including fresh portable admission, not a copied adapter oracle.
[MemoryDiagnoser]
[ShortRunJob]
public class PreparedHostedProjectionBenchmarks
{
    static readonly ValueContract Contract = new(new JsonTypeRef());
    PortableValue input = null!;

    [Params(1, 1024)] public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var schema = new ImmutableSchema("spec", [.. Enumerable.Range(0, 128).Select(i => $"field{i:D3}")]);
        input = PortableValue.Concrete(Contract, ObservationValue.FromObject(new Envelope(schema,
            Enumerable.Range(0, Rows).Select(i => new Row(i, "value")).ToArray())));
        var first = Decode().Success!;
        var second = Decode().Success!;
        if (!ReferenceEquals(first.Schema, second.Schema) || ReferenceEquals(first.Values, second.Values))
            throw new InvalidOperationException("Projection ownership differs.");
    }

    [Benchmark]
    public Result<Envelope, DocumentValidationDiagnostic> Decode() => HostedQueryValueAdapter.Decode<Envelope>(input, Contract);

    public sealed record ImmutableSchema(string Name, ImmutableArray<string> Fields) : IImmutableObservationProjection;
    public sealed record Envelope(ImmutableSchema Schema, Row[] Values);
    public sealed record Row(long Number, string Text);
}
