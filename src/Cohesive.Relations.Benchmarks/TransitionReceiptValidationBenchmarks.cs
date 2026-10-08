using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model.Serialization;
using Cohesive.Storage;

namespace Cohesive.Relations.Benchmarks;

// Warm receipt integrity CPU/allocation boundary, excluding network, SQL and provider envelope validation.
// The decode constructor itself verifies canonical fingerprints. FullValidation additionally checks wire
// canonicality, exactly as the PostgreSQL and SQLite retained-evidence readers do. Not an alternate reader.
[MemoryDiagnoser]
public class TransitionReceiptValidationBenchmarks
{
    [Params(32, 16384)] public int PayloadBytes { get; set; }
    byte[] bytes = null!;
    string hash = null!;
    JsonSerializerOptions options = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        options = EntityStorageJson.CreateOptions();
        options.MakeReadOnly(populateMissingResolver: true);
        var repository = new InMemoryEntityOutboxRepository(RunControlFixture.Entity,
            EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));
        var context = OperationContext.Create();
        var snapshot = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { InputDigest = new byte[PayloadBytes] }));
        var evidence = RunControlFixture.Prepare(snapshot);
        var commit = RunControlFixture.Commit(evidence, evidence.Decision,
            RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        var result = await repository.CommitTransitionOperation(context, commit);
        bytes = StrictDocumentJson.GetCanonicalBytes(result.Receipt!, options);
        hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _ = FullValidation();
    }

    [Benchmark(Baseline = true)]
    public EntityTransitionOperationReceipt HashAndDecode()
    {
        if (hash != Convert.ToHexStringLower(SHA256.HashData(bytes))) throw new InvalidOperationException("Receipt hash mismatch.");
        return JsonSerializer.Deserialize<EntityTransitionOperationReceipt>(bytes, options)!;
    }

    [Benchmark]
    public EntityTransitionOperationReceipt FullValidation()
    {
        var receipt = HashAndDecode();
        if (!bytes.AsSpan().SequenceEqual(StrictDocumentJson.GetCanonicalBytes(receipt, options)))
            throw new InvalidOperationException("Receipt is not canonical.");
        return receipt;
    }
}
