using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Semantics;

[MemoryDiagnoser]
public class WorkspaceBenchmarks
{
    string root = null!;
    SemanticWorkspace workspace = null!;
    string snapshot = null!;

    [Params(8, 128, 4096)]
    public int InputCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "cohesive-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inputs = Enumerable.Range(0, InputCount).Select(i => $"input{i}.cs").ToArray();
        foreach (var input in inputs) File.WriteAllText(Path.Combine(root, input), new string('x', 4096));
        var payloadBytes = InputCount switch { 8 => 16 * 1024, 128 => 1024 * 1024, _ => 15 * 1024 * 1024 };
        var json = SemanticWorkspaceExport.Create(root, inputs, "cost-probe/v1", revision =>
            [ExecutionDefinitionDocument.Create(new("probe"), new("subject"), new(revision),
                new { Value = new string('x', payloadBytes) },
                new(new("probe", "1"), new(inputs[0]), DocumentOrigin.User))]);
        File.WriteAllText(Path.Combine(root, "workspace.json"), json);
        workspace = new SemanticWorkspace(root, "workspace.json");
        snapshot = workspace.Describe().SnapshotId;
    }

    [Benchmark]
    public WorkspaceResult<WorkspacePage<WorkspaceSubject>> Describe() => workspace.Describe();

    [Benchmark]
    public WorkspaceResult<WorkspaceInspection> Inspect() => workspace.Inspect("subject", snapshot, "/metadata/definitionId");

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);
}
