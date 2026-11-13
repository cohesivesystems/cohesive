using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Npgsql;
using Fixture = Cohesive.Tests.Machines.MachineReactionHostTests.Fixture;

namespace Cohesive.Tests.Machines;

public sealed class MachineReactionRecoveryTests
{
    [Fact]
    public async Task Generic_recovery_rejects_ambiguous_registration_and_advances_past_unregistered_work()
    {
        var f = await Fixture.Create();
        var host = f.Host();
        Assert.Throws<ArgumentException>(() => new MachineReactionRecovery(f.Store, [host, f.Host()]));
        Assert.Throws<ArgumentException>(() => new MachineReactionRecovery(new InMemoryProcessDurableStore(), [host]));
        var id = Assert.Single(await host.AdmitAsync(f.Context, f.Receipt));
        var recovery = new MachineReactionRecovery(f.Store, []);
        var page = await recovery.RunPageAsync(f.Context, maximumInstances: 1);
        Assert.Empty(page.Progress);
        Assert.Equal(id, page.NextCursor);
        Assert.Null((await recovery.RunPageAsync(f.Context, page.NextCursor)).NextCursor);
        Assert.Empty(ProcessTransitionAttempt.Read((await f.Store.LoadAsync(f.Context, id))!));
    }

    [Fact]
    public async Task Generic_recovery_preserves_fresh_authorization_and_page_retry()
    {
        var f = await Fixture.Create();
        var host = f.Host();
        var id = Assert.Single(await host.AdmitAsync(f.Context, f.Receipt));
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        var foreignTarget = MachineReactionTarget.Local(new(f.Model.MarkPending.Compile().Plan!, f.Repository, contracts!),
            new InteractionAuthorityScope("another-authority", "test"), (_, _) => throw new InvalidOperationException("Wrong target"));
        var foreignHost = new MachineReactionHost(f.Model.PendingAcceptance, f.Store, foreignTarget,
            (_, _) => throw new InvalidOperationException("Wrong source"), new("worker", TimeSpan.FromMinutes(5)),
            (_, _) => throw new InvalidOperationException("Wrong recovery authority"));
        var recovery = new MachineReactionRecovery(f.Store, [foreignHost, host]);
        f.RecoveryAllowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recovery.RunPageAsync(f.Context));
        Assert.Empty(ProcessTransitionAttempt.Read((await f.Store.LoadAsync(f.Context, id))!));
        f.RecoveryAllowed = true;
        Assert.Equal(MachineReactionStatus.Completed, (await recovery.RunPageAsync(f.Context)).Progress[id].Status);
    }

    [Fact]
    public async Task Stale_worker_discovery_cannot_exceed_the_dispatch_budget()
    {
        var f = await Fixture.Create();
        var clock = new RecoveryClock(DateTimeOffset.UtcNow);
        f.Context = f.Context with { TimeProvider = clock };
        var unavailable = new UnavailableTarget(f.Target);
        var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new MachineReactionHost(f.Model.PendingAcceptance, f.Store, unavailable,
            (_, _) => throw new InvalidOperationException(), new("stale-worker", TimeSpan.FromMinutes(5)),
            async (_, _) => { inspected.TrySetResult(); await resume.Task.WaitAsync(TimeSpan.FromSeconds(10)); return true; },
            maximumAttempts: 1, retryDelay: TimeSpan.Zero);
        var current = new MachineReactionHost(f.Model.PendingAcceptance, f.Store, unavailable,
            (_, _) => throw new InvalidOperationException(), new("current-worker", TimeSpan.FromMinutes(5)),
            (_, _) => ValueTask.FromResult(true), maximumAttempts: 1, retryDelay: TimeSpan.Zero);
        var id = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        var racing = stale.RecoverAsync(f.Context, id);
        await inspected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await current.RecoverAsync(f.Context, id);
        clock.Now += TimeSpan.FromMinutes(6);
        resume.SetResult();
        Assert.Equal(MachineReactionStatus.Failed, (await racing)!.Status);
        Assert.Equal(1, unavailable.Calls);
        Assert.Single(ProcessTransitionAttempt.Read((await f.Store.LoadAsync(f.Context, id))!));
    }

    [Fact]
    public async Task Outage_does_not_block_admission_and_worker_restart_discovers_every_tender()
    {
        var f = await Fixture.Create();
        var unavailable = new UnavailableTarget(f.Target);
        var ids = new List<ProcessInstanceId>();
        for (var index = 0; index < 3; index++)
        {
            var source = await Fixture.Create(loadId: "load-" + index, processId: "source-" + index);
            await f.Repository.Upsert(f.Context, new(f.Model.Load.CreateState("load-" + index,
                new LoadAcknowledgementState(LoadAcknowledgementStatus.Ready, true)).Snapshot));
            ids.Add(Assert.Single(await f.Host(unavailable).AdmitAsync(f.Context, source.Receipt)));
        }
        Assert.Equal(0, unavailable.Calls);
        foreach (var id in ids)
        {
            var pending = await f.Host(unavailable).RecoverAsync(f.Context, id);
            Assert.Equal(MachineReactionStatus.Pending, pending!.Status);
            Assert.Equal(DurableOperationEffectEvidence.NotExecuted, Assert.Single(pending.Attempts).Attempt.Failure!.EffectEvidence);
        }
        var retained = ProcessDurableStoreJsonSerializer.Serialize(((InMemoryProcessDurableStore)f.Store).CaptureDocument());
        f.Store = new InMemoryProcessDurableStore(ProcessDurableStoreJsonSerializer.Deserialize(retained));
        var completed = new List<ProcessInstanceId>();
        var recovery = new MachineReactionRecovery(f.Store, [f.Host()]);
        ProcessInstanceId? cursor = null;
        do
        {
            var page = await recovery.RunPageAsync(f.Context, cursor, maximumInstances: 1);
            foreach (var (id, progress) in page.Progress)
            {
                Assert.Equal(MachineReactionStatus.Completed, progress.Status);
                completed.Add(id);
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(ids.OrderBy(id => id.Value), completed);
        foreach (var id in ids)
        {
            var progress = (await f.Host().RecoverAsync(f.Context, id))!;
            Assert.Equal(2, progress.Attempts.Length);
            Assert.Equal(progress.Attempts[0].Invocation, progress.Attempts[1].Invocation);
            Assert.Equal(MachineReactionStatus.Completed, progress.Status);
        }
    }

    [Fact]
    public async Task Lost_acknowledgement_survives_serialization_and_does_not_repeat_mutation()
    {
        var f = await Fixture.Create();
        var id = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        var uncertain = (await f.Host(new LostAcknowledgement(f.Target)).RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Indeterminate, uncertain.Status);
        Assert.Single(uncertain.Attempts);
        var before = await f.Repository.TryGet(f.Context, "load-1", EntityReadOptions.Full.WithPartitionKey("test"));
        f.Store = new InMemoryProcessDurableStore(ProcessDurableStoreJsonSerializer.Deserialize(
            ProcessDurableStoreJsonSerializer.Serialize(((InMemoryProcessDurableStore)f.Store).CaptureDocument())));
        var completed = (await f.Host().RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Completed, completed.Status);
        Assert.Equal(before!.ConcurrencyToken, completed.Receipt!.Entity.ConcurrencyToken);
        Assert.Equal(2, completed.Attempts.Length);
        Assert.Equal(completed.Attempts[0].Invocation, completed.Attempts[1].Invocation);
    }

    [Fact]
    public async Task Retry_budget_and_delay_are_bounded_and_guard_rejection_is_conclusive()
    {
        var f = await Fixture.Create();
        var unavailable = new UnavailableTarget(f.Target);
        var host = new MachineReactionHost(f.Model.PendingAcceptance, f.Store, unavailable,
            (_, receipt) => ValueTask.FromResult<ProcessControlAuthorizationContext?>(new("worker", receipt.Request.AuthorityScope, "test")),
            new("worker", TimeSpan.FromMinutes(5)), (_, _) => ValueTask.FromResult(true), maximumAttempts: 1, retryDelay: TimeSpan.Zero);
        var id = Assert.Single(await host.AdmitAsync(f.Context, f.Receipt));
        await host.RecoverAsync(f.Context, id);
        var stopped = (await host.RecoverAsync(f.Context, id))!;
        Assert.Equal(1, unavailable.Calls);
        Assert.NotNull(stopped.Diagnostic);
        Assert.Equal(MachineReactionStatus.Failed, stopped.Status);
        var delayed = new MachineReactionHost(f.Model.PendingAcceptance, f.Store, unavailable,
            (_, _) => throw new InvalidOperationException(), new("worker", TimeSpan.FromMinutes(5)),
            (_, _) => ValueTask.FromResult(true), retryDelay: TimeSpan.FromHours(1));
        await delayed.RecoverAsync(f.Context, id);
        Assert.Equal(1, unavailable.Calls);
        var rejected = await Fixture.Create(allowed: false);
        var rejectedId = Assert.Single(await rejected.Host().AdmitAsync(rejected.Context, rejected.Receipt));
        Assert.Equal(MachineReactionStatus.Rejected, (await rejected.Host().RecoverAsync(rejected.Context, rejectedId))!.Status);
        Assert.Single((await rejected.Host().RecoverAsync(rejected.Context, rejectedId))!.Attempts);
    }

    [Fact]
    public async Task Recovery_requires_fresh_permission_even_for_replayed_work()
    {
        var f = await Fixture.Create();
        var id = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        f.RecoveryAllowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Host().RecoverAsync(f.Context, id));
        Assert.Empty(ProcessTransitionAttempt.Read((await f.Store.LoadAsync(f.Context, id))!));
        f.RecoveryAllowed = true;
        Assert.Equal(MachineReactionStatus.Completed, (await f.Host().RecoverAsync(f.Context, id))!.Status);
        f.RecoveryAllowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Host().RecoverAsync(f.Context, id));
    }

    [Fact]
    public async Task Tender_receipt_confirmation_is_independently_admitted_and_recovers_load_acknowledgement()
    {
        var f = await Fixture.Create();
        var pending = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        Assert.Equal(MachineReactionStatus.Completed, (await f.Host().RecoverAsync(f.Context, pending))!.Status);
        var (repository, context, commit) = await EntityMachineOccurrenceTests.Prepare(f.Model.Tender.Entity,
            f.Model.Tender.ConfirmReceipt, new TenderState("load-1", true, true, false, ""), new Receipt(true, true), "confirmation");
        var confirmation = (await repository.CommitTransitionOperation(context, commit)).Receipt!;
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        var target = MachineReactionTarget.Local(new(f.Model.Confirm.Compile().Plan!, f.Repository, contracts!),
            f.Target.Authority, (_, _) => ValueTask.FromResult(true));
        MachineReactionHost Host(MachineReactionTarget realization) => new(f.Model.ReceiptConfirmation, f.Store, realization,
            (_, receipt) => ValueTask.FromResult<ProcessControlAuthorizationContext?>(new("worker", receipt.Request.AuthorityScope, "test")),
            new("worker", TimeSpan.FromMinutes(5)), (_, _) => ValueTask.FromResult(true), retryDelay: TimeSpan.Zero);
        var id = Assert.Single(await Host(target).AdmitAsync(f.Context, confirmation));
        Assert.Equal(MachineReactionStatus.Indeterminate, (await Host(new LostAcknowledgement(target)).RecoverAsync(f.Context, id))!.Status);
        var recovery = new MachineReactionRecovery(f.Store, [f.Host(), Host(target)]);
        var page = await recovery.RunPageAsync(f.Context);
        Assert.Equal(2, page.Progress.Count);
        var recovered = page.Progress[id];
        Assert.Equal(MachineReactionStatus.Completed, recovered.Status);
        Assert.Equal("Confirmed", recovered.Receipt!.Result.Value!.Value!.Value.GetString());
        Assert.Equal("mem:3", recovered.Receipt.Entity.ConcurrencyToken!.Value);
    }

    [Fact]
    public async Task Cancelled_worker_leaves_dispatch_evidence_and_reclaims_after_lease_expiry()
    {
        var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        var clock = new RecoveryClock(DateTimeOffset.UtcNow);
        f.Context = f.Context with { TimeProvider = clock, CancellationToken = cancellation.Token };
        var id = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Host(new InterruptedTarget(f.Target, cancellation)).RecoverAsync(f.Context, id));
        f.Context = f.Context with { CancellationToken = CancellationToken.None };
        var interrupted = (await f.Host().InspectAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Indeterminate, interrupted.Status);
        Assert.Equal(DurableOperationAttemptStage.Dispatched, Assert.Single(interrupted.Attempts).Attempt.Stage);
        clock.Now += TimeSpan.FromMinutes(6);
        var recovered = (await f.Host().RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Completed, recovered.Status);
        Assert.Equal(2, recovered.Attempts.Length);
        Assert.Equal("mem:2", recovered.Receipt!.Entity.ConcurrencyToken!.Value);
    }

    [Cohesive.Tests.Postgres.PostgresProcessDurableStoreTests.PostgresFact]
    public async Task Postgres_discovery_and_attempt_history_survive_a_new_store_and_host()
    {
        var connection = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING");
        await using var dataSource = NpgsqlDataSource.Create(connection!);
        var options = new PostgresProcessDurableStoreOptions("reaction-test/" + Guid.NewGuid().ToString("N"));
        var f = await Fixture.Create();
        var store = new PostgresProcessDurableStore(dataSource, options);
        await store.EnsureCreatedAsync(f.Context);
        f.Store = store;
        var id = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        Assert.Equal(MachineReactionStatus.Indeterminate, (await f.Host(new LostAcknowledgement(f.Target)).RecoverAsync(f.Context, id))!.Status);
        await using var replacementPool = NpgsqlDataSource.Create(connection!);
        var replacement = new PostgresProcessDurableStore(replacementPool, options);
        f.Store = replacement;
        Assert.Equal(id, Assert.Single(await replacement.ListInstancesAsync(f.Context, null, 1)));
        Assert.Empty(await replacement.ListInstancesAsync(f.Context, id, 1));
        var recovered = (await f.Host().RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Completed, recovered.Status);
        Assert.Equal(2, recovered.Attempts.Length);
        Assert.Equal("mem:2", recovered.Receipt!.Entity.ConcurrencyToken!.Value);
        var other = new PostgresProcessDurableStore(replacementPool, new("unrelated-authority"));
        Assert.Empty(await other.ListInstancesAsync(f.Context, null, 10));
    }

    sealed class UnavailableTarget(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        internal int Calls;
        public override ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        { Calls++; throw new IOException("Endpoint unavailable before call"); }
        public override DurableOperationFailure DescribeFailure(Exception exception) => new(DurableOperationFailurePhase.PreCall,
            DurableOperationEffectEvidence.NotExecuted, DurableOperationFailureDisposition.Retryable, "test.target.offline");
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }

    sealed class LostAcknowledgement(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        { await inner.ExecuteAsync(context, invocation); throw new TimeoutException("Acknowledgement lost after commit"); }
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }

    sealed class InterruptedTarget(MachineReactionTarget inner, CancellationTokenSource cancellation) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        { await inner.ExecuteAsync(context, invocation); cancellation.Cancel(); context.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }

    sealed class RecoveryClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
