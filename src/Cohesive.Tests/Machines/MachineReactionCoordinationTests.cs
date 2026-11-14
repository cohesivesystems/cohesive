using Cohesive.Execution;
using Cohesive.Adapters.Postgres;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Machines;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Npgsql;
using Fixture = Cohesive.Tests.Machines.MachineReactionHostTests.Fixture;

namespace Cohesive.Tests.Machines;

public sealed class MachineReactionCoordinationTests
{
    [Fact]
    public async Task Confirmation_waits_across_restart_then_replays_lost_ack_without_duplicate_mutation()
    {
        var f = await Fixture.Create();
        var receipt = await ConfirmationReceipt(f);
        var target = ConfirmationTarget(f);
        var id = Assert.Single(await ConfirmationHost(f, target).AdmitAsync(f.Context, receipt));
        Assert.Equal(MachineReactionStatus.Pending, (await ConfirmationHost(f, target).RecoverAsync(f.Context, id))!.Status);
        var waiting = (await ConfirmationHost(f, target).RecoverAsync(f.Context, id))!;
        Assert.Equal("machine.reaction.prerequisite.waiting", waiting.Diagnostic!.Code);
        Assert.Empty(waiting.Attempts);
        var pending = (await f.Store.LoadAsync(f.Context, id))!;
        Assert.True(Assert.Single(pending.Checkpoint.Continuation.Waits).Active);
        Assert.Empty(pending.Checkpoint.Operations);
        Assert.Equal("mem:1", (await f.Repository.TryGet(f.Context, "load-1", EntityReadOptions.Full.WithPartitionKey("test")))!.ConcurrencyToken!.Value);
        Restart(f);
        var acceptance = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        Assert.Equal(MachineReactionStatus.Indeterminate, (await f.Host(new LostAcknowledgement(f.Target)).RecoverAsync(f.Context, acceptance))!.Status);
        var stillWaiting = (await ConfirmationHost(f, target).RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Pending, stillWaiting.Status);
        Assert.Equal("machine.reaction.prerequisite.indeterminate", stillWaiting.Diagnostic!.Code);
        Assert.Empty(stillWaiting.Attempts);
        Assert.Equal(MachineReactionStatus.Completed, (await f.Host().RecoverAsync(f.Context, acceptance))!.Status);
        var uncertain = await ConfirmationHost(f, new LostAcknowledgement(target)).RecoverAsync(f.Context, id);
        Assert.Equal(MachineReactionStatus.Indeterminate, uncertain!.Status);
        Restart(f);
        var recovered = (await ConfirmationHost(f, target).RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Completed, recovered.Status);
        Assert.Equal("mem:3", recovered.Receipt!.Entity.ConcurrencyToken!.Value);
        Assert.Equal(2, recovered.Attempts.Length);
        Assert.Equal(recovered.Attempts[0].Invocation, recovered.Attempts[1].Invocation);
        Assert.Single((await f.Store.LoadAsync(f.Context, id))!.Checkpoint.Inbox);
        Assert.Equal(id, Assert.Single(await ConfirmationHost(f, target).AdmitAsync(f.Context, receipt)));
        Assert.Empty((await new MachineReactionRecovery(f.Store, [f.Host(), ConfirmationHost(f, target)]).RunPageAsync(f.Context)).Progress);
    }

    [Fact]
    public async Task Rejected_acceptance_blocks_confirmation_without_target_dispatch()
    {
        var f = await Fixture.Create(allowed: false);
        var host = ConfirmationHost(f, ConfirmationTarget(f));
        var id = Assert.Single(await host.AdmitAsync(f.Context, await ConfirmationReceipt(f)));
        await host.RecoverAsync(f.Context, id);
        var acceptance = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        Assert.Equal(MachineReactionStatus.Rejected, (await f.Host().RecoverAsync(f.Context, acceptance))!.Status);
        Restart(f);
        var blocked = (await ConfirmationHost(f, ConfirmationTarget(f)).RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Failed, blocked.Status);
        Assert.Equal("machine.reaction.prerequisite.rejected", blocked.Diagnostic!.Code);
        Assert.Empty(blocked.Attempts);
        Assert.Empty((await f.Store.LoadAsync(f.Context, id))!.Checkpoint.Inbox);
        Assert.Equal(MachineReactionStatus.Failed, (await ConfirmationHost(f, ConfirmationTarget(f)).InspectAsync(f.Context, id))!.Status);
    }

    [Fact]
    public async Task Another_tender_cannot_release_the_wait_and_second_occurrence_conflicts()
    {
        var f = await Fixture.Create();
        var host = ConfirmationHost(f, ConfirmationTarget(f));
        var id = Assert.Single(await host.AdmitAsync(f.Context, await ConfirmationReceipt(f)));
        await host.RecoverAsync(f.Context, id);
        var other = await Fixture.Create(loadId: "load-other", processId: "other");
        await f.Repository.Upsert(f.Context, new(f.Model.Load.CreateState("load-other",
            new LoadAcknowledgementState(LoadAcknowledgementStatus.Ready, true)).Snapshot));
        var otherId = Assert.Single(await f.Host().AdmitAsync(f.Context, other.Receipt));
        Assert.Equal(MachineReactionStatus.Completed, (await f.Host().RecoverAsync(f.Context, otherId))!.Status);
        Assert.Equal(MachineReactionStatus.Pending, (await host.RecoverAsync(f.Context, id))!.Status);
        await f.Host().AdmitAsync(f.Context, f.Receipt);
        var (repository, context, commit) = await EntityMachineOccurrenceTests.Prepare(f.Model.Tender.Entity,
            f.Model.Tender.AcceptPending, new TenderState("load-1", false, false, false, ""), new SendResult(false), "second-acceptance");
        var conflicting = (await repository.CommitTransitionOperation(context, commit)).Receipt!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host().AdmitAsync(f.Context, conflicting));
    }

    [Fact]
    public void Coordinated_policy_roundtrips_and_requires_exact_host_binding()
    {
        var model = new TenderReactionModel();
        var policy = model.ReceiptConfirmation;
        var restored = MachineReaction.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(policy.Document));
        Assert.Equal(policy.Document.Reference, restored.Document.Reference);
        Assert.Equal(model.PendingAcceptance.Document.Reference, restored.Payload.Prerequisite!.Reference);
        Assert.Equal(MachineReaction.PrerequisiteNode, restored.Compile().Plan!.Definition.Entry);
        Assert.Equal(policy.Document.Reference, new TenderReactionModel().ReceiptConfirmation.Document.Reference);
        var bypass = policy.Payload with { Process = model.PendingAcceptance.Payload.Process };
        var malformed = MachineReactionTests.Persist(policy, bypass);
        Assert.Throws<InvalidOperationException>(() => MachineReaction.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(malformed)));
    }

    [Fact]
    public async Task Release_from_another_source_instance_is_rejected_before_dispatch()
    {
        var f = await Fixture.Create();
        var host = ConfirmationHost(f, ConfirmationTarget(f));
        var id = Assert.Single(await host.AdmitAsync(f.Context, await ConfirmationReceipt(f)));
        await host.RecoverAsync(f.Context, id);
        var snapshot = (await f.Store.LoadAsync(f.Context, id))!;
        var wait = Assert.Single(snapshot.Checkpoint.Continuation.Waits);
        var target = new ProcessTokenInteractionTarget(snapshot.Checkpoint.ContinuationIdentity, wait.Token, wait.RegistrationId);
        var identity = id.Value + "/prerequisite";
        var forged = new SignalEnvelope(InteractionEnvelope.CurrentSchemaVersion,
            new(new(identity), new ProcessInteractionOrigin(f.Model.PendingAcceptance.Payload.Process.Reference,
                MachineReaction.InvocationNode, new(new("other-tender"), new("attempt")), new("activation"), wait.Token),
                new(identity), null, f.Target.Authority, new(identity), null,
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), host.Policy.Document.Metadata.Provenance),
            new SignalContractReference(host.Policy.PrerequisiteSignal!.Reference),
            PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.String)), ObservationValue.FromString("not-this-tenders-receipt")), target);
        await f.Store.AdmitInputAsync(f.Context, id, new(target, forged), f.Context.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RecoverAsync(f.Context, id));
        Assert.Empty(ProcessTransitionAttempt.Read((await f.Store.LoadAsync(f.Context, id))!));
    }

    [Fact]
    public async Task Reading_prerequisite_evidence_requires_its_own_fresh_authorization()
    {
        var f = await Fixture.Create();
        var accepted = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        await f.Host().RecoverAsync(f.Context, accepted);
        var denied = new MachineReactionHost(f.Model.PendingAcceptance, f.Store, f.Target,
            (_, _) => throw new InvalidOperationException(), new("worker", TimeSpan.FromMinutes(5)),
            new((_, _) => ValueTask.FromResult(false)));
        var host = new MachineReactionHost(f.Model.ReceiptConfirmation, f.Store, ConfirmationTarget(f),
            (_, receipt) => ValueTask.FromResult<ProcessControlAuthorizationContext?>(new("worker", receipt.Request.AuthorityScope, "test")),
            new("worker", TimeSpan.FromMinutes(5)), new((_, _) => ValueTask.FromResult(true)), prerequisite: denied);
        var id = Assert.Single(await host.AdmitAsync(f.Context, await ConfirmationReceipt(f)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.RecoverAsync(f.Context, id));
        Assert.Empty(ProcessTransitionAttempt.Read((await f.Store.LoadAsync(f.Context, id))!));
    }

    [Fact]
    public async Task Successful_prerequisite_does_not_bypass_the_confirmation_guard()
    {
        var f = await Fixture.Create();
        var host = ConfirmationHost(f, ConfirmationTarget(f));
        var id = Assert.Single(await host.AdmitAsync(f.Context, await ConfirmationReceipt(f)));
        await host.RecoverAsync(f.Context, id);
        var accepted = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        await f.Host().RecoverAsync(f.Context, accepted);
        await f.Repository.Upsert(f.Context, new(f.Model.Load.CreateState("load-1",
            new LoadAcknowledgementState(LoadAcknowledgementStatus.Pending, false)).Snapshot));
        var result = (await host.RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Rejected, result.Status);
        Assert.Equal(accepted, result.Prerequisite);
        Assert.Single(result.Attempts);
    }

    [Fact]
    public async Task Concurrent_release_converges_and_registration_requires_exact_prerequisite()
    {
        var f = await Fixture.Create();
        var target = ConfirmationTarget(f);
        Assert.Throws<ArgumentException>(() => new MachineReactionHost(f.Model.ReceiptConfirmation, f.Store, target,
            (_, _) => throw new InvalidOperationException(), new("worker", TimeSpan.FromMinutes(5))));
        var host = ConfirmationHost(f, target);
        var id = Assert.Single(await host.AdmitAsync(f.Context, await ConfirmationReceipt(f)));
        await host.RecoverAsync(f.Context, id);
        var accepted = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        await f.Host().RecoverAsync(f.Context, accepted);
        await Task.WhenAll(host.RecoverAsync(f.Context, id), ConfirmationHost(f, target).RecoverAsync(f.Context, id));
        var result = (await host.RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Completed, result.Status);
        Assert.Equal("mem:3", result.Receipt!.Entity.ConcurrencyToken!.Value);
        Assert.Single((await f.Store.LoadAsync(f.Context, id))!.Checkpoint.Inbox);
    }

    [Cohesive.Tests.Postgres.PostgresProcessDurableStoreTests.PostgresFact]
    public async Task Postgres_reopens_the_wait_and_releases_only_after_acceptance()
    {
        var connection = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!;
        await using var pool = NpgsqlDataSource.Create(connection);
        var options = new PostgresProcessDurableStoreOptions("causal-test/" + Guid.NewGuid().ToString("N"));
        var f = await Fixture.Create();
        var store = new PostgresProcessDurableStore(pool, options);
        await store.EnsureCreatedAsync(f.Context);
        f.Store = store;
        var id = Assert.Single(await ConfirmationHost(f, ConfirmationTarget(f)).AdmitAsync(f.Context, await ConfirmationReceipt(f)));
        await ConfirmationHost(f, ConfirmationTarget(f)).RecoverAsync(f.Context, id);
        await using var replacementPool = NpgsqlDataSource.Create(connection);
        f.Store = new PostgresProcessDurableStore(replacementPool, options);
        Assert.True(Assert.Single((await f.Store.LoadAsync(f.Context, id))!.Checkpoint.Continuation.Waits).Active);
        var accepted = Assert.Single(await f.Host().AdmitAsync(f.Context, f.Receipt));
        await f.Host().RecoverAsync(f.Context, accepted);
        var completed = (await ConfirmationHost(f, ConfirmationTarget(f)).RecoverAsync(f.Context, id))!;
        Assert.Equal(MachineReactionStatus.Completed, completed.Status);
        Assert.Equal("mem:3", completed.Receipt!.Entity.ConcurrencyToken!.Value);
        Assert.Empty(await ((IProcessInstanceSource)f.Store).ListPendingInstancesAsync(f.Context, null, 10));
    }

    internal static async Task<EntityTransitionOperationReceipt> ConfirmationReceipt(Fixture f)
    {
        var (repository, context, commit) = await EntityMachineOccurrenceTests.Prepare(f.Model.Tender.Entity,
            f.Model.Tender.ConfirmReceipt, new TenderState("load-1", true, true, false, ""), new Receipt(true, true), "confirmation");
        return (await repository.CommitTransitionOperation(context, commit)).Receipt!;
    }

    internal static MachineReactionTarget ConfirmationTarget(Fixture f)
    {
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        return MachineReactionTarget.Local(new(f.Model.Confirm.Compile().Plan!, f.Repository, contracts!), f.Target.Authority,
            (_, _) => ValueTask.FromResult(f.TargetAllowed));
    }

    internal static MachineReactionHost ConfirmationHost(Fixture f, MachineReactionTarget target) => new(
        f.Model.ReceiptConfirmation, f.Store, target,
        (_, receipt) => ValueTask.FromResult<ProcessControlAuthorizationContext?>(new("worker", receipt.Request.AuthorityScope, "test")),
        new("worker", TimeSpan.FromMinutes(5)), new((_, _) => ValueTask.FromResult(f.RecoveryAllowed), retryDelay: TimeSpan.Zero), prerequisite: f.Host());

    static void Restart(Fixture f) => f.Store = new InMemoryProcessDurableStore(ProcessDurableStoreJsonSerializer.Deserialize(
        ProcessDurableStoreJsonSerializer.Serialize(((InMemoryProcessDurableStore)f.Store).CaptureDocument())));

    sealed class LostAcknowledgement(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        { await inner.ExecuteAsync(context, invocation); throw new TimeoutException("Lost acknowledgement"); }
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }
}
