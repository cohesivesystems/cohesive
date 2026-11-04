using System.Diagnostics;
using System.Runtime.InteropServices;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Semantics;

// Dependency-free cost probe, not a statistically controlled latency benchmark.
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine("inputs,bundle_bytes,operation,median_ms,p95_ms,allocated_bytes_per_call");
foreach (var (count, payloadBytes) in new[] { (8, 16 * 1024), (128, 1024 * 1024), (4096, 15 * 1024 * 1024) })
{
    var root = Path.Combine(Path.GetTempPath(), "cohesive-cost-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var inputs = Enumerable.Range(0, count).Select(i => $"input{i}.cs").ToArray();
        foreach (var input in inputs) File.WriteAllText(Path.Combine(root, input), new string('x', 4096));
        var json = SemanticWorkspaceExport.Create(root, inputs, "cost-probe/v1", revision =>
            [ExecutionDefinitionDocument.Create(new("probe"), new("subject"), new(revision),
                new { Value = new string('x', payloadBytes) },
                new(new("probe", "1"), new(inputs[0]), DocumentOrigin.User))]);
        File.WriteAllText(Path.Combine(root, "workspace.json"), json);
        var workspace = new SemanticWorkspace(root, "workspace.json");
        var snapshot = workspace.Describe().SnapshotId;
        Measure("workspace", () => workspace.Describe());
        Measure("inspect", () => workspace.Inspect("subject", snapshot, "/metadata/definitionId"));

        void Measure(string operation, Func<object> call)
        {
            for (var i = 0; i < 5; i++) GC.KeepAlive(call());
            const int samples = 20;
            var timings = new double[samples];
            var allocated = 0L;
            for (var i = 0; i < samples; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                GC.KeepAlive(call());
                timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Array.Sort(timings);
            Console.WriteLine($"{count},{new FileInfo(Path.Combine(root, "workspace.json")).Length},{operation},{timings[samples / 2]:F3},{timings[18]:F3},{allocated / samples}");
        }
    }
    finally { Directory.Delete(root, recursive: true); }
}
