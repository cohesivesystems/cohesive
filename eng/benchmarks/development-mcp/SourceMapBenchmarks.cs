using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

[MemoryDiagnoser]
public class SourceMapBenchmarks
{
    static readonly System.Text.Json.JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    [Params(1, 16, 256)]
    public int Entries { get; set; }

    [Benchmark(Baseline = true)]
    public byte[] ReferenceOnly() => BuildAndSerialize(withLocations: false);

    [Benchmark]
    public byte[] StructuredLocations() => BuildAndSerialize(withLocations: true);

    byte[] BuildAndSerialize(bool withLocations)
    {
        var entries = ImmutableArray.CreateBuilder<ExecutionSourceProvenance>(Entries);
        for (var i = 0; i < Entries; i++)
            entries.Add(new("definition#source", new(["body", i.ToString(System.Globalization.CultureInfo.InvariantCulture)]),
                location: withLocations ? ExecutionSourceLocation.FromCompiler("/_/src/Model.cs", i + 1) : null));
        return StrictDocumentJson.GetCanonicalBytes(new ExecutionSourceMap(entries.MoveToImmutable()), Options);
    }
}
