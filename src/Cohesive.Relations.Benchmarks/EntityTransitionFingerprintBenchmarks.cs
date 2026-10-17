using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Storage;

namespace Cohesive.Relations.Benchmarks;

// Fresh candidates share immutable prepared payloads, never receipt results. The baseline explicitly
// requests the two digests previously computed by constructors. Both use the real native fence protocol.
/// <summary>Compares eager digest preparation with native success retaining its exact immutable candidate.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class EntityTransitionFingerprintBenchmarks
{
    /// <summary>Prepared entity binary payload size in bytes; excluded from per-operation construction.</summary>
    [Params(32, 16_384, 1_048_576)]
    public int PayloadBytes { get; set; }

    EntityTransitionOperationCommit template = null!;
    EntitySnapshot snapshot = null!;
    InMemoryEntityOutboxRepository repository = null!;
    OperationContext context = null!;
    static readonly DateTimeOffset CommittedAt = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Prepares the reference decision, candidate payload, and warm serializer metadata.</summary>
    /// <returns>Completion after fixture construction and both mechanisms have been warmed.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        context = OperationContext.Create();
        repository = new(RunControlFixture.Entity, EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));
        var initial = await repository.Upsert(context, RunControlFixture.Write(
            RunControlFixture.Initial() with { InputDigest = new byte[PayloadBytes] }));
        var evidence = RunControlFixture.Prepare(initial);
        template = RunControlFixture.Commit(evidence, evidence.Decision,
            RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        snapshot = new(template.Write.Entity, "tenant/a", new("mem:2"));
        _ = EagerFingerprints();
        _ = FencedFreshCommit();
    }

    /// <summary>Explicitly requests both formerly eager digests before native acceptance.</summary>
    /// <returns>The successful native receipt.</returns>
    [Benchmark(Baseline = true)]
    public EntityTransitionOperationResult EagerFingerprints()
    {
        var commit = Candidate();
        _ = commit.Request.IntentFingerprint;
        _ = commit.Fingerprint;
        return Accept(commit);
    }

    /// <summary>Constructs and accepts fresh candidate evidence using its existing native fence.</summary>
    /// <returns>The successful native receipt without preparing unused intent/commit digests.</returns>
    [Benchmark]
    public EntityTransitionOperationResult FencedFreshCommit() => Accept(Candidate());

    EntityTransitionOperationCommit Candidate()
    {
        var request = template.Request;
        return new(new(request.Operation, request.AuthorityScope, request.Transition, request.Subject, request.Input),
            template.Write, template.DecisionKind, template.Result, template.GuaranteeDemands, template.Evidence,
            template.SubjectCondition);
    }

    EntityTransitionOperationResult Accept(EntityTransitionOperationCommit commit) =>
        EntityTransitionCommitProtocol.CommitAsync(repository, context, commit, (_, candidate) =>
            Task.FromResult(EntityTransitionOperationResult.Committed(new(candidate, snapshot, CommittedAt))))
            .GetAwaiter().GetResult();
}
