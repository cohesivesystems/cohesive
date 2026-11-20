using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Perfolizer.Horology;

BenchmarkSwitcher.FromAssembly(typeof(ExecutionDocumentCreationBenchmarks).Assembly).Run(args,
    ManualConfig.Create(DefaultConfig.Instance).AddJob(
        Job.ShortRun.WithIterationTime(TimeInterval.FromMilliseconds(100)).WithId("ExecutionDocuments")));

[MemoryDiagnoser]
public class ExecutionDocumentCreationBenchmarks
{
    [Params("Flat128", "Nested128", "Flat4096")]
    public string Workload { get; set; } = "Flat128";

    Definition definition = null!;
    readonly ExecutionProvenance provenance = new(new("execution-document-benchmark", "1"),
        new("benchmarks/execution-documents", new(["definition"])), DocumentOrigin.Compiled);

    [GlobalSetup]
    public void Setup()
    {
        var fields = Workload == "Flat4096" ? 4096 : 128;
        object data = Enumerable.Range(0, fields).Reverse().ToDictionary(
            static index => $"field-{index:D4}",
            static index => index % 4 == 0 ? (object)$"text-{index}-é-😀" : index,
            StringComparer.Ordinal);
        if (Workload == "Nested128")
            for (var depth = 0; depth < 4; depth++)
                data = new Dictionary<string, object?>
                {
                    ["child"] = data,
                    ["sequence"] = new object?[] { false, 5, 1.25m, "escaped\"text" }
                };
        definition = new(data);
        _ = Create();
    }

    [Benchmark]
    public ExecutionDefinitionDocument Create() => ExecutionDefinitionDocument.Create(
        new("transition"), new("definition/benchmark"), new("revision/1"), definition, provenance);

    public sealed record Definition(object Data);
}
