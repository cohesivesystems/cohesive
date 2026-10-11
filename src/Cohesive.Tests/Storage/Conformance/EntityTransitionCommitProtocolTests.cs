using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
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

    static async Task<(EntityTransitionOperationCommit, EntityTransitionOperationReceipt)> Evidence()
    {
        var repository = new InMemoryEntityOutboxRepository(RunControlFixture.Entity, EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));
        var context = OperationContext.Create();
        var initial = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
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
