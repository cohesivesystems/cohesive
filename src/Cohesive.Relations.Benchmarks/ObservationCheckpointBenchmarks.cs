using System.Text;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Whole durable checkpoint serialization and fresh recovery admission.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationCheckpointBenchmarks
{
    ProcessDurableCheckpoint checkpoint = null!;
    CompiledProcessPlan plan = null!;
    string json = null!;

    /// <summary>Input payload structure retained in a complete initial checkpoint.</summary>
    [Params("flat", "nested", "collection", "large")]
    public string Workload { get; set; } = "flat";

    /// <summary>Gets the complete canonical checkpoint size, including start/control/continuation envelopes.</summary>
    public int WireBytes { get; private set; }

    /// <summary>Gets SHA-256 of the exact complete canonical UTF-8 checkpoint bytes.</summary>
    public string WireHash { get; private set; } = string.Empty;

    /// <summary>Compiles a simple portable Process and verifies exact recovery bytes outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var payload = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["name"] = ObservationValue.FromString("load <&> café 🚚"), ["amount"] = ObservationValue.FromInt64(12)
        });
        if (Workload == "nested")
            for (var level = 0; level < 16; level++)
                payload = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["child"] = payload });
        if (Workload is "collection" or "large")
            payload = ObservationValue.FromImmutableArray([.. Enumerable.Repeat(payload, Workload == "large" ? 4096 : 128)]);
        var input = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["payload"] = payload });
        var contract = new ValueContract(new JsonTypeRef(JsonTypeKind.Object));
        var definition = new ProcessDefinition(contract, contract, new("return"),
            [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))], ProcessRecoveryPolicy.ContinueAttempt);
        var document = ProcessDefinitionDocuments.Create(new("benchmark/checkpoint"), new("revision/1"), definition,
            new(new("benchmark", "1"), new("benchmark/checkpoint"), DocumentOrigin.Generated));
        if (!InteractionContractCatalog.TryCreate([], out var catalog).IsValid) throw new InvalidOperationException("Invalid catalog.");
        var compilation = ProcessStaticCompiler.Compile(document, new([], catalog!));
        if (!compilation.IsSuccessful) throw new InvalidOperationException(string.Join("; ", compilation.Validation.Diagnostics));
        plan = compilation.Plan!;
        ProcessContinuationIdentity identity = new(new("benchmark/instance"), new("attempt/1"));
        var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var start = new ProcessStartReceipt(new(ProcessStartRequest.CurrentSchemaVersion, plan.DefinitionReference,
            new(new("benchmark/command"), new("benchmark/idempotency"), identity.ProcessInstanceId,
                new("benchmark/operator", new("benchmark/authority", "benchmark/tenant"), "benchmark/policy"), now, plan.Document.Metadata.Provenance),
            identity, PortableValue.Concrete(contract, input)), now);
        checkpoint = new(ProcessDurableCheckpoint.CurrentSchemaVersion, start, ProcessReferenceInterpreter.Create(plan, start),
            start.CreateInitialState(), createdAtUtc: now, updatedAtUtc: now);
        json = Encode();
        WireBytes = Encoding.UTF8.GetByteCount(json);
        WireHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (json != ProcessDurableCheckpointJsonSerializer.Serialize(DecodeAndAdmit()))
            throw new InvalidOperationException("Checkpoint round trip changed canonical bytes.");
    }

    /// <summary>Produces owned canonical checkpoint text for a text-based durable store.</summary>
    /// <returns>The complete canonical checkpoint.</returns>
    [Benchmark]
    public string Encode() => ProcessDurableCheckpointJsonSerializer.Serialize(checkpoint);

    /// <summary>Parses and freshly admits a complete checkpoint against its exact compiled plan.</summary>
    /// <returns>Independent recovered checkpoint storage.</returns>
    [Benchmark]
    public ProcessDurableCheckpoint DecodeAndAdmit() => ProcessDurableCheckpointJsonSerializer.Deserialize(json, plan);
}
