using Cohesive.Execution;
using Cohesive.Adapters.Postgres;
using Npgsql;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using static Cohesive.Tests.Machines.MachineReactionCoordinationTests;
using Fixture = Cohesive.Tests.Machines.MachineReactionHostTests.Fixture;

namespace Cohesive.Tests.Machines;

public sealed class MachineReactionLifecycleTests
{
    [Fact]
    public async Task Abandonment_survives_restart_replays_and_explains_blocked_dependents()
    {
        var f = await Fixture.Create();
        var host = f.Host();
        var confirmation = ConfirmationHost(f, ConfirmationTarget(f));
        var dependent = Assert.Single(await confirmation.AdmitAsync(f.Context, await ConfirmationReceipt(f)));
        await confirmation.RecoverAsync(f.Context, dependent);
        var id = Assert.Single(await host.AdmitAsync(f.Context, f.Receipt));
        var inspected = (await host.InspectLifecycleAsync(f.Context, id))!;
        var command = Command(f, inspected);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied,
            (await host.AbandonAsync(f.Context, command, Allow)).Disposition);
        f.Store = new InMemoryProcessDurableStore(ProcessDurableStoreJsonSerializer.Deserialize(
            ProcessDurableStoreJsonSerializer.Serialize(((InMemoryProcessDurableStore)f.Store).CaptureDocument())));
        host = f.Host();
        confirmation = ConfirmationHost(f, ConfirmationTarget(f));
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed,
            (await host.AbandonAsync(f.Context, command, Allow)).Disposition);
        var abandoned = (await host.InspectLifecycleAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Abandoned, abandoned.Progress.Status);
        Assert.Equal(ProcessControlMode.Cancelled, abandoned.Control.Mode);
        Assert.Contains(abandoned.Control.Receipts, receipt => receipt.Command == command);
        Assert.Empty(abandoned.Progress.Attempts);
        Assert.Equal(id, Assert.Single(await host.AdmitAsync(f.Context, f.Receipt)));
        Assert.Equal(MachineReactionStatus.Abandoned, (await host.RecoverAsync(f.Context, id))!.Status);
        var blocked = (await confirmation.InspectLifecycleAsync(f.Context, dependent))!;
        Assert.Equal("machine.reaction.prerequisite.abandoned", blocked.Progress.Diagnostic!.Code);
        Assert.Equal(MachineReactionStatus.Abandoned, blocked.PrerequisiteProgress!.Status);
        Assert.NotEqual(ProcessControlMode.Cancelled, blocked.Control.Mode);
        var recovery = new MachineReactionRecovery(f.Store, [host, confirmation]);
        var page = await recovery.InspectDependentsAsync(f.Context, id);
        Assert.Equal(dependent, Assert.Single(page.Progress).Key);
        Assert.Empty((await recovery.InspectDependentsAsync(f.Context, id, page.NextCursor)).Progress);
        ProcessInstanceId? cursor = null;
        var matches = 0;
        do
        {
            var bounded = await recovery.InspectDependentsAsync(f.Context, id, cursor, maximumInstances: 1);
            matches += bounded.Progress.Count;
            cursor = bounded.NextCursor;
        } while (cursor is not null);
        Assert.Equal(1, matches);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied,
            (await confirmation.AbandonAsync(f.Context, Command(f, blocked, "abandon-dependent"), Allow)).Disposition);
        Assert.Empty((await recovery.RunPageAsync(f.Context)).Progress);
    }

    [Fact]
    public async Task Operator_authorization_is_separate_and_inspection_requires_fresh_permission()
    {
        var f = await Fixture.Create();
        var host = f.Host();
        var id = Assert.Single(await host.AdmitAsync(f.Context, f.Receipt));
        var before = (await host.InspectLifecycleAsync(f.Context, id))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.AbandonAsync(f.Context, Command(f, before),
            (_, _, _) => ValueTask.FromResult(false)));
        Assert.Equal(before.Control, (await host.InspectLifecycleAsync(f.Context, id))!.Control);
        f.RecoveryAllowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.InspectLifecycleAsync(f.Context, id));
    }

    [Fact]
    public async Task Abandonment_does_not_erase_uncertain_commitment_or_resend_it()
    {
        var f = await Fixture.Create();
        var target = new LostAcknowledgement(f.Target);
        var host = f.Host(target);
        var id = Assert.Single(await host.AdmitAsync(f.Context, f.Receipt));
        Assert.Equal(MachineReactionStatus.Indeterminate, (await host.RecoverAsync(f.Context, id))!.Status);
        var before = (await host.InspectLifecycleAsync(f.Context, id))!;
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied,
            (await host.AbandonAsync(f.Context, Command(f, before), Allow)).Disposition);
        var after = (await host.InspectLifecycleAsync(f.Context, id))!;
        Assert.Equal(ProcessControlMode.Cancelled, after.Control.Mode);
        Assert.Equal(MachineReactionStatus.Indeterminate, after.Progress.Status);
        Assert.Single(after.Progress.Attempts);
        Assert.Equal(MachineReactionStatus.Indeterminate, (await host.RecoverAsync(f.Context, id))!.Status);
        Assert.Equal(1, target.Calls);
    }

    [Fact]
    public async Task Conclusive_rejection_is_not_new_work_and_stale_abandonment_is_rejected()
    {
        var f = await Fixture.Create(allowed: false);
        var host = f.Host();
        var id = Assert.Single(await host.AdmitAsync(f.Context, f.Receipt));
        var command = Command(f, (await host.InspectLifecycleAsync(f.Context, id))!);
        var rejected = (await host.RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Rejected, rejected.Status);
        Assert.Equal(ProcessDurableRuntimeDisposition.Rejected, (await host.AbandonAsync(f.Context, command, Allow)).Disposition);
        Assert.Equal(rejected.Operation, (await host.RecoverAsync(f.Context, id))!.Operation);
        Assert.Single((await host.InspectLifecycleAsync(f.Context, id))!.Progress.Attempts);
    }

    [Fact]
    public async Task Concurrent_identical_abandonment_converges_and_changed_intent_conflicts()
    {
        var f = await Fixture.Create();
        var id = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        var command = Command(f, (await f.Host().InspectLifecycleAsync(f.Context, id))!);
        await Task.WhenAll(f.Host().AbandonAsync(f.Context, command, Allow), f.Host().AbandonAsync(f.Context, command, Allow));
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, (await f.Host().AbandonAsync(f.Context, command, Allow)).Disposition);
        var changed = new CancelProcessCommand(command.SchemaVersion, command.Context, command.Expectation!, new("different-reason"));
        Assert.Equal(ProcessDurableRuntimeDisposition.Rejected, (await f.Host().AbandonAsync(f.Context, changed, Allow)).Disposition);
        var inspected = (await f.Host().InspectLifecycleAsync(f.Context, id))!;
        Assert.Single(inspected.Control.Receipts.Where(receipt => receipt.Command is CancelProcessCommand));
        Assert.Empty(inspected.Progress.Attempts);
    }

    [Cohesive.Tests.Postgres.PostgresProcessDurableStoreTests.PostgresFact]
    public async Task Postgres_abandonment_reopens_with_receipt_and_leaves_pending_discovery()
    {
        var connection = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!;
        await using var pool = NpgsqlDataSource.Create(connection);
        var options = new PostgresProcessDurableStoreOptions("reaction-lifecycle/" + Guid.NewGuid().ToString("N"));
        var f = await Fixture.Create();
        var store = new PostgresProcessDurableStore(pool, options);
        await store.EnsureCreatedAsync(f.Context);
        f.Store = store;
        var host = ConfirmationHost(f, ConfirmationTarget(f));
        var id = Assert.Single(await host.AdmitAsync(f.Context, await ConfirmationReceipt(f)));
        await host.RecoverAsync(f.Context, id);
        var command = Command(f, (await host.InspectLifecycleAsync(f.Context, id))!);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, (await host.AbandonAsync(f.Context, command, Allow)).Disposition);
        await using var replacement = NpgsqlDataSource.Create(connection);
        f.Store = new PostgresProcessDurableStore(replacement, options);
        host = ConfirmationHost(f, ConfirmationTarget(f));
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, (await host.AbandonAsync(f.Context, command, Allow)).Disposition);
        Assert.Equal(MachineReactionStatus.Abandoned, (await host.InspectLifecycleAsync(f.Context, id))!.Progress.Status);
        Assert.Empty(await ((IProcessInstanceSource)f.Store).ListPendingInstancesAsync(f.Context, null, 10));
    }

    static ValueTask<bool> Allow(OperationContext context, ProcessStartRequest start, CancelProcessCommand command) => ValueTask.FromResult(true);

    static CancelProcessCommand Command(Fixture f, MachineReactionInspection inspection, string key = "abandon") => new(
        ProcessControlCommand.CurrentSchemaVersion,
        new(new(key), new(key), inspection.Instance, new("operator", f.Target.Authority, "test/operator"),
            f.Context.UtcNow, f.Model.PendingAcceptance.Document.Metadata.Provenance),
        new(new(inspection.Instance, inspection.Control.CurrentAttempt.AttemptId), inspection.Control.Revision), new("tender.withdrawn"));

    sealed class LostAcknowledgement(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        internal int Calls;
        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        { Calls++; await inner.ExecuteAsync(context, invocation); throw new TimeoutException("Lost response after commit"); }
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }
}
