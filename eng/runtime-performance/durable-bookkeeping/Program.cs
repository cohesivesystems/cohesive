using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

if (args.Contains("--allocations"))
{
    Console.WriteLine($"# .NET {Environment.Version}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; median of 7 batches of 16 operations after 8 warmups");
    Console.WriteLine("rows,operation,allocated_bytes");
    foreach (var rows in new[] { 1024, 8192 })
    {
        var benchmark = new CommitPreparationBenchmarks { Rows = rows };
        benchmark.Setup();
        Measure(rows, "construct_commit", () => GC.KeepAlive(benchmark.ConstructCommit()));
        Measure(rows, "construct_and_hash_whole_commit", () => GC.KeepAlive(benchmark.ConstructAndHashWholeCommit()));
        Measure(rows, "initialize", () => GC.KeepAlive(benchmark.Fixture.Initialize()));
        foreach (var mode in Enum.GetValues<ProcessCheckpointVerification>())
            Measure(rows, $"execute_{mode}", () => GC.KeepAlive(benchmark.Fixture.Execute(mode)));
    }
    return;
}
BenchmarkRunner.Run<CommitPreparationBenchmarks>(args: args);

static void Measure(int rows, string operation, Action action)
{
    for (var i = 0; i < 8; i++) action();
    long[] samples = new long[7];
    for (var sample = 0; sample < samples.Length; sample++)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++) action();
        samples[sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / 16;
    }
    Array.Sort(samples);
    Console.WriteLine($"{rows},{operation},{samples[3]}");
}

[MemoryDiagnoser, ShortRunJob]
public class CommitPreparationBenchmarks
{
    [Params(1024, 8192)]
    public int Rows { get; set; }
    internal BookkeepingFixture Fixture { get; private set; } = null!;
    [GlobalSetup]
    public void Setup() => Fixture = new(Rows);
    [Benchmark(Baseline = true)]
    public ProcessCommitFingerprint ConstructAndHashWholeCommit() => ConstructCommit().Fingerprint;
    [Benchmark]
    public ProcessDurableCommit ConstructCommit() => new(new("commit/benchmark"), new("1"), "worker/benchmark", new("1"),
        Fixture.Checkpoint, [], Fixture.Checkpoint.UpdatedAtUtc);
}

sealed class BookkeepingFixture
{
    readonly CompiledProcessPlan plan;
    readonly ProcessActivation activation;
    readonly OperationContext context;
    internal ProcessDurableCheckpoint Checkpoint { get; }
    internal BookkeepingFixture(int rows)
    {
        ValueContract contract = new(new JsonTypeRef(JsonTypeKind.Object));
        var definition = new ProcessDefinition(contract, contract, new("return"),
            [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))], ProcessRecoveryPolicy.ContinueAttempt);
        var document = ProcessDefinitionDocuments.Create(new("benchmark/bookkeeping"), new("1"), definition,
            new(new("benchmark", "1"), new("benchmark/bookkeeping"), DocumentOrigin.Generated));
        if (!InteractionContractCatalog.TryCreate([], out var contracts).IsValid) throw new InvalidOperationException();
        var compilation = ProcessStaticCompiler.Compile(document, new([], contracts!));
        if (!compilation.IsSuccessful) throw new InvalidOperationException(string.Join(';', compilation.Validation.Diagnostics));
        plan = compilation.Plan!;
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        ProcessContinuationIdentity continuation = new(new("benchmark/instance"), new("attempt/1"));
        InteractionAuthorityScope authority = new("benchmark", "tenant/benchmark");
        var start = new ProcessStartReceipt(new(ProcessStartRequest.CurrentSchemaVersion, plan.DefinitionReference,
            new(new("start/benchmark"), new("idempotency/benchmark"), continuation.ProcessInstanceId,
                new("operator/benchmark", authority, "allow"), now, document.Metadata.Provenance), continuation,
            PortableValue.Concrete(contract, ObservationValue.FromObject(new Dictionary<string, ObservationValue>
                {
                    ["rows"] = ObservationValue.FromArray(Enumerable.Range(0, rows)
                        .Select(index => ObservationValue.FromObject(new Dictionary<string, ObservationValue>
                        {
                            ["ordinal"] = ObservationValue.FromInt64(index),
                            ["name"] = ObservationValue.FromString("item"),
                            ["enabled"] = ObservationValue.FromBool(true),
                            ["description"] = ObservationValue.FromString(new string('x', 64))
                        })).ToArray())
                }))), now);
        Checkpoint = new(ProcessDurableCheckpoint.CurrentSchemaVersion, start, ProcessReferenceInterpreter.Create(plan, start),
            start.CreateInitialState(), createdAtUtc: now, updatedAtUtc: now);
        activation = new(new("activation/benchmark"), ProcessActivationCause.Start, now,
            new(authority, new("correlation/benchmark"), new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
                document.Metadata.Provenance));
        context = OperationContext.Create(timeProvider: new Clock(now));
    }
    internal ProcessStoreMutationResult Initialize() => new InMemoryProcessDurableStore()
        .InitializeAsync(context, new("initialize"), Checkpoint).GetAwaiter().GetResult();
    internal ProcessDurableActivationResult Execute(ProcessCheckpointVerification verification)
    {
        var runtime = new ProcessDurableRuntime(new InMemoryProcessDurableStore(), EmptyHost.Instance,
            new("worker/benchmark", TimeSpan.FromMinutes(5), checkpointVerification: verification));
        runtime.InitializeAsync(context, plan, Checkpoint.Start).GetAwaiter().GetResult();
        var result = runtime.ActivateAsync(context, plan, Checkpoint.ContinuationIdentity, activation).GetAwaiter().GetResult();
        if (result.Disposition != ProcessDurableRuntimeDisposition.Applied) throw new InvalidOperationException(string.Join(';', result.Diagnostics));
        return result;
    }
    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    sealed class EmptyHost : IProcessReferenceHost
    {
        internal static readonly EmptyHost Instance = new();
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new NotSupportedException();
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new NotSupportedException();
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new NotSupportedException();
    }
}
