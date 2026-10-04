using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures snapshot fingerprint reuse and first-use overhead with bounded portable inputs.</summary>
[MemoryDiagnoser, CategoriesColumn, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ProcessContinuationFingerprintBenchmarks
{
    CompiledProcessPlan plan = null!;
    PortableValue input = null!;
    ProcessContinuationState snapshot = null!;
    Func<ProcessContinuationState, ProcessContinuationFingerprint> fingerprint = null!;
    readonly JsonSerializerOptions options = ProcessDurableCheckpointJsonSerializer.CreateOptions();

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        // Resolve the internal storage operation once, without expanding its public API for benchmarking.
        fingerprint = typeof(InMemoryProcessDurableStore).Assembly
            .GetType("Cohesive.Storage.Processes.ProcessStorageContentFingerprints", throwOnError: true)!
            .GetMethod("Continuation", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<ProcessContinuationState, ProcessContinuationFingerprint>>();
        var contract = new ValueContract(new JsonTypeRef(JsonTypeKind.Object));
        var definition = new ProcessDefinition(contract, contract, new("return"),
            [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))],
            ProcessRecoveryPolicy.ContinueAttempt);
        var document = ProcessDefinitionDocuments.Create(new("benchmark/continuation"), new("revision/1"),
            definition, new(new("benchmark", "1"), new("benchmark/continuation"), DocumentOrigin.Generated));
        if (!InteractionContractCatalog.TryCreate([], out var contracts).IsValid)
            throw new InvalidOperationException("Cannot prepare interaction catalog.");
        var compilation = ProcessStaticCompiler.Compile(document, new([], contracts!));
        if (!compilation.IsSuccessful)
            throw new InvalidOperationException(string.Join("; ", compilation.Validation.Diagnostics));
        plan = compilation.Plan!;
        var row = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["name"] = ObservationValue.FromString("example"),
            ["sequence"] = ObservationValue.FromInt64(123),
            ["enabled"] = ObservationValue.FromBool(true)
        });
        var payload = row;
        if (Shape == "nested")
            for (var index = 0; index < 24; index++)
                payload = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["child"] = payload });
        if (Shape is "collection" or "large")
            payload = ObservationValue.FromArray(Enumerable.Repeat(row, Shape == "large" ? 4096 : 128).ToArray());
        input = PortableValue.Concrete(contract, ObservationValue.FromObject(
            new Dictionary<string, ObservationValue> { ["payload"] = payload }));
        snapshot = NewSnapshot();
        options.MakeReadOnly(populateMissingResolver: true);
        if (Reference(snapshot) != fingerprint(snapshot))
            throw new InvalidOperationException("Continuation fingerprints differ.");
    }

    [Benchmark(Baseline = true), BenchmarkCategory("same snapshot")]
    public ProcessContinuationFingerprint RepeatedReference() => Reference(snapshot);

    [Benchmark, BenchmarkCategory("same snapshot")]
    public ProcessContinuationFingerprint RepeatedMemoized() => fingerprint(snapshot);

    [Benchmark(Baseline = true), BenchmarkCategory("fresh snapshot")]
    public ProcessContinuationFingerprint FreshReference() => Reference(NewSnapshot());

    [Benchmark, BenchmarkCategory("fresh snapshot")]
    public ProcessContinuationFingerprint FreshMemoized() => fingerprint(NewSnapshot());

    ProcessContinuationState NewSnapshot() => ProcessReferenceInterpreter.Create(plan,
        new(new("benchmark/instance"), new("attempt/1")), input);

    ProcessContinuationFingerprint Reference(ProcessContinuationState value) => new("sha256-v1:" +
        Convert.ToHexStringLower(SHA256.HashData(StrictDocumentJson.GetCanonicalBytes(value, options))));
}
