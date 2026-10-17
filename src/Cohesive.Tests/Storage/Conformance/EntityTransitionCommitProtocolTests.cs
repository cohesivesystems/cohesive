using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage;

namespace Cohesive.Tests.Storage.Conformance;

public sealed class EntityTransitionCommitProtocolTests
{
    [Fact]
    public async Task Fresh_commit_performs_one_native_attempt_and_no_receipt_reads()
    {
        var (commit, receipt) = await Evidence();
        var repository = new CountingRepository();
        var writes = 0;
        var result = await EntityTransitionCommitProtocol.CommitAsync(repository, OperationContext.Create(), commit,
            (_, _) => { writes++; return Task.FromResult(EntityTransitionOperationResult.Committed(receipt)); });
        Assert.Equal(EntityTransitionOperationDisposition.Committed, result.Disposition);
        Assert.Equal(1, writes);
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task Conditional_conflict_reads_once_and_preserves_subject_fence_and_provider_detail()
    {
        var (commit, _) = await Evidence();
        var repository = new CountingRepository();
        var result = await EntityTransitionCommitProtocol.CommitAsync(repository, OperationContext.Create(), commit,
            (_, _) => Task.FromResult(EntityTransitionCommitProtocol.Conflict(commit, "Cosmos batch status: PreconditionFailed; operations: FailedDependency,PreconditionFailed.")));
        Assert.Equal(1, repository.Reads);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("/write/expectedConcurrencyToken", diagnostic.Location);
        Assert.Contains("run/1", diagnostic.Message);
        Assert.Contains(commit.Write.ExpectedConcurrencyToken!.Value.Value, diagnostic.Message);
        Assert.Contains("PreconditionFailed", diagnostic.Message);
        Assert.Contains("FailedDependency", diagnostic.Message);
    }

    [Fact]
    public async Task Ambiguous_native_failure_does_not_retry_or_read()
    {
        var (commit, _) = await Evidence();
        var repository = new CountingRepository();
        var writes = 0;
        var failure = new IOException("ambiguous acknowledgement");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => EntityTransitionCommitProtocol.CommitAsync(repository,
            OperationContext.Create(), commit, (_, _) => { writes++; throw failure; })));
        Assert.Equal(1, writes);
        Assert.Equal(0, repository.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconstructed_native_evidence_requires_exact_content(bool changedResult)
    {
        var (commit, receipt) = await Evidence();
        var reconstructed = new EntityTransitionOperationCommit(commit.Request, commit.Write, commit.DecisionKind,
            changedResult ? ProcessOperationResult.Completed(PortableValue.Concrete(
                RunControlFixture.StringContract, ObservationValue.FromString("different")), commit.Result.Emissions) : commit.Result,
            commit.GuaranteeDemands, commit.Evidence, commit.SubjectCondition);
        var returned = new EntityTransitionOperationReceipt(reconstructed, receipt.Entity, receipt.CommittedAtUtc);
        var repository = new CountingRepository();
        var invocation = EntityTransitionCommitProtocol.CommitAsync(repository, OperationContext.Create(), commit,
            (_, _) => Task.FromResult(EntityTransitionOperationResult.Committed(returned)));
        if (changedResult)
            await Assert.ThrowsAsync<InvalidOperationException>(() => invocation);
        else
            Assert.Same(returned, (await invocation).Receipt);
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task Prepared_fingerprints_preserve_wire_record_equality_and_concurrent_reuse()
    {
        var (commit, _) = await Evidence();
        var copy = commit with { };
        var requestCopy = commit.Request with { };
        var hashCode = commit.GetHashCode();
        var requestHashCode = commit.Request.GetHashCode();
        var options = EntityStorageJson.CreateOptions();
        var fingerprints = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            (Commit: commit.Fingerprint.Value, Intent: commit.Request.IntentFingerprint.Value))));
        foreach (var fingerprint in fingerprints)
        {
            Assert.Same(fingerprints[0].Commit, fingerprint.Commit);
            Assert.Same(fingerprints[0].Intent, fingerprint.Intent);
        }
        Assert.Equal(copy, commit);
        Assert.Equal(requestCopy, commit.Request);
        Assert.Equal(hashCode, commit.GetHashCode());
        Assert.Equal(requestHashCode, commit.Request.GetHashCode());
        Assert.Equal(copy.Fingerprint, commit.Fingerprint);
        var bytes = StrictDocumentJson.GetCanonicalBytes(commit, options);
        var restored = JsonSerializer.Deserialize<EntityTransitionOperationCommit>(bytes, options)!;
        Assert.Equal(commit.Fingerprint, restored.Fingerprint);
        Assert.Equal(commit.Request.Fingerprint, restored.Request.Fingerprint);
        Assert.Equal(commit.Request.IntentFingerprint, restored.Request.IntentFingerprint);
        Assert.Equal(bytes, StrictDocumentJson.GetCanonicalBytes(restored, options));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 128; index++)
        {
            _ = commit.Fingerprint;
            _ = commit.Request.IntentFingerprint;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 64);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(1_048_576)]
    public async Task Fresh_commit_construction_has_a_fixed_allocation_budget(int payloadBytes)
    {
        var (template, _) = await Evidence(payloadBytes);
        EntityTransitionOperationCommit Create() => new(template.Request, template.Write, template.DecisionKind,
            template.Result, template.GuaranteeDemands, template.Evidence, template.SubjectCondition);
        for (var index = 0; index < 32; index++) GC.KeepAlive(Create());
        long[] samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 128; index++) GC.KeepAlive(Create());
            samples[run] = (GC.GetAllocatedBytesForCurrentThread() - before) / 128;
        }
        Array.Sort(samples);
        // Current constructor allocation is 544 B for both sizes; leave room for runtime/layout changes.
        Assert.InRange(samples[3], 0, 1_024);
    }

    [Fact]
    public async Task Fingerprints_and_canonical_commit_match_published_alpha144_vectors()
    {
        // Obtained independently by running this fixture against the published alpha.144 assemblies.
        var (commit, _) = await Evidence(payloadBytes: 32);
        Assert.Equal("sha256-v1:6f5c8d391d4c67997e1653addab049c2f42ce07b739d452a13c1cd1115ef377b",
            commit.Request.Fingerprint.Value);
        Assert.Equal("sha256-v1:229cade7573a16a4ba156fea05170ec48c01dd8b3bdd46a2d8dfe22ff3095f71",
            commit.Request.IntentFingerprint.Value);
        Assert.Equal("sha256-entity-v2:252c009beceb9ba393297cb173bad77529f90b324f3d5d6b92cb1f8b950a7a08",
            commit.Fingerprint.Value);
        Assert.Equal("ac421a408ee8932b14121f13f4892d5cd13e3321c5485a7b15bbde7edbd81a9b",
            Convert.ToHexStringLower(SHA256.HashData(StrictDocumentJson.GetCanonicalBytes(commit, EntityStorageJson.CreateOptions()))));
    }

    static async Task<(EntityTransitionOperationCommit, EntityTransitionOperationReceipt)> Evidence(int payloadBytes = 4)
    {
        var repository = new InMemoryEntityOutboxRepository(RunControlFixture.Entity, EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));
        var context = OperationContext.Create();
        var initial = await repository.Upsert(context, RunControlFixture.Write(
            RunControlFixture.Initial() with { InputDigest = new byte[payloadBytes] }));
        var evidence = RunControlFixture.Prepare(initial);
        var commit = RunControlFixture.Commit(evidence, evidence.Decision, RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        return (commit, (await repository.CommitTransitionOperation(context, commit)).Receipt!);
    }

    sealed class CountingRepository : IEntityTransitionOperationRepository
    {
        public int Reads { get; private set; }
        public EntityDefinition EntityDefinition => RunControlFixture.Entity;
        public string? IdentityField => nameof(RunControl.Id);
        public EntityTransitionOperationCapabilities TransitionOperationCapabilities => EntityTransitionOperationCapabilities.AtomicStateAndReceipt;
        public Task<EntityTransitionOperationResult> TryGetTransitionOperation(OperationContext context, EntityTransitionOperationRequest request)
        { Reads++; return Task.FromResult(EntityTransitionOperationResult.NotFound()); }
        public Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(OperationContext context, EntityTransitionOperationRequest request)
        { Reads++; return Task.FromResult(EntityTransitionOperationResult.NotFound()); }
        public Task<EntityTransitionOperationResult> CommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit) => throw new NotImplementedException();
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => throw new NotImplementedException();
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => throw new NotImplementedException();
    }
}
