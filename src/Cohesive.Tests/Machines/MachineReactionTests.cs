using Cohesive.Examples.TenderAcceptance;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Machines;
using Cohesive.Machines;
using Cohesive.Relations.Authoring;
using Cohesive.Storage;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.Machines;

public sealed class MachineReactionTests
{
    [Fact]
    public void Typed_policy_lowers_deterministically_and_roundtrips()
    {
        var first = new TenderReactionModel().PendingAcceptance;
        var second = new TenderReactionModel().PendingAcceptance;
        Assert.Equal(first.Document.Reference, second.Document.Reference);
        var restored = MachineReaction.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(first.Document));
        Assert.Equal(first.Document.Reference, restored.Document.Reference);
        Assert.True(restored.Compile().IsSuccessful);
    }

    [Theory]
    [InlineData(true, MachineReactionStatus.Completed)]
    [InlineData(false, MachineReactionStatus.Rejected)]
    public async Task Process_interpretation_preserves_target_guards_and_requires_exact_receipt(bool allowed, MachineReactionStatus expected)
    {
        var f = await Fixture.Create(allowed);
        var initial = await f.Runtime.InitializeAsync(f.Context, f.Plan, f.Start);
        Assert.Equal(MachineReactionStatus.Pending, f.Invocation.Inspect(initial.Snapshot!.Checkpoint).Status);
        var duplicate = await f.Runtime.InitializeAsync(f.Context, f.Plan, f.Start);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, duplicate.Disposition);

        var result = await f.Runtime.ActivateAsync(f.Context, f.Plan, f.Start.Request.InitialContinuation, f.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        var checkpoint = result.Snapshot!.Checkpoint;
        Assert.Equal(MachineReactionStatus.Indeterminate, f.Invocation.Inspect(checkpoint).Status);
        var operation = Assert.Single(checkpoint.Operations);
        var reference = EntityTransitionReceiptReferences.Read(operation.Result.ReceiptReference!);
        var receipt = (await f.LoadRepository.ResolveTransitionOperation(f.Context, reference)).Receipt!;
        Assert.Equal(expected, f.Invocation.Inspect(checkpoint, receipt).Status);
        Assert.Throws<InvalidOperationException>(() => f.Invocation.Inspect(checkpoint, f.Source.Receipt));
        var recoveredCheckpoint = ProcessDurableCheckpointJsonSerializer.Deserialize(
            ProcessDurableCheckpointJsonSerializer.Serialize(checkpoint), f.Plan);
        var recovered = MachineReactionInvocation.Restore(f.Model.PendingAcceptance, recoveredCheckpoint.Start.Request);
        Assert.Equal(f.Invocation.Source, recovered.Source);
        Assert.Equal(f.Invocation.MovementNode, recovered.MovementNode);
        Assert.Equal(expected, recovered.Inspect(recoveredCheckpoint, receipt).Status);
        Assert.Equal("load-1", receipt.Request.Subject.EntityId.Value);
        Assert.Equal(f.Model.MarkPending.Reference, receipt.Request.Transition);
        var referenceHost = new ReferenceHost(operation.Result);
        var referenceDecision = ProcessReferenceInterpreter.Activate(f.Plan,
            ProcessReferenceInterpreter.Create(f.Plan, f.Start), f.Activation, referenceHost);
        Assert.Equal(result.Decision!.State.Terminal, referenceDecision.State.Terminal);
        Assert.Equal(f.Adapter.LastInvocation, referenceHost.Invocation);
        Assert.True(f.Source.Receipt.Entity.Entity.Observation.GetField(nameof(TenderState.PendingReceipt)).GetBoolean());

        var replay = await f.Runtime.ActivateAsync(f.Context, f.Plan, f.Start.Request.InitialContinuation, f.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(1, f.Adapter.Calls);
    }

    [Fact]
    public async Task Duplicate_delivery_derives_same_start_identity_and_nonmatching_movement_is_ignored()
    {
        var f = await Fixture.Create(true);
        var repeated = MachineReactionInvocation.Prepare(f.Model.PendingAcceptance, f.Source,
            f.Start.Request.Context.Authorization, DateTimeOffset.UtcNow.AddMinutes(1))!;
        Assert.Equal(f.Invocation.Start.InitialContinuation, repeated.Start.InitialContinuation);
        Assert.True(f.Invocation.Start.HasSameIdempotentIntent(repeated.Start));
        Assert.Null(MachineReactionInvocation.Prepare(f.Model.ReceiptConfirmation, f.Source,
            f.Start.Request.Context.Authorization, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => MachineReactionInvocation.Prepare(f.Model.PendingAcceptance, f.Source,
            new("worker", new("another-authority", "test"), "policy"), DateTimeOffset.UtcNow));
        var revised = MachineReaction.On<TenderState>(f.Model.Tender.Entity, f.Model.Tender.Machine.SendPending,
                "tender-to-load.pending", "2")
            .Request(f.Model.MarkPending, tender => tender.LoadId, tender => new AcknowledgeLoad(false));
        var different = MachineReactionInvocation.Prepare(revised, f.Source,
            f.Start.Request.Context.Authorization, DateTimeOffset.UtcNow)!;
        Assert.NotEqual(f.Invocation.Start.InitialContinuation, different.Start.InitialContinuation);
        Assert.Throws<InvalidOperationException>(() => MachineReactionInvocation.Restore(revised, f.Invocation.Start));
    }

    [Fact]
    public void Wrong_source_owner_and_impure_projection_fail_during_authoring()
    {
        var model = new TenderReactionModel();
        var machine = Machine.Define("foreign", "1");
        var ready = machine.State("ready").Initial();
        var edge = machine.Edge("record").From(ready).To(ready);
        Assert.Throws<ArgumentException>(() => MachineReaction.On<TenderState>(model.Tender.Entity, edge, "invalid", "1"));
        Assert.Throws<RelationQueryExpressionAuthoringException>(() =>
            MachineReaction.On<TenderState>(model.Tender.Entity, model.Tender.Machine.SendPending, "impure", "1")
                .Request(model.MarkPending, tender => Guid.NewGuid().ToString(), tender => new AcknowledgeLoad(false)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Persisted_policy_rejects_unknown_movement_or_mismatched_target(bool invalidMovement)
    {
        var model = new TenderReactionModel();
        var policy = model.PendingAcceptance;
        var payload = invalidMovement ? policy.Payload with { Movement = new("missing") }
            : policy.Payload with { Target = model.Confirm.Document };
        var document = ExecutionDefinitionDocument.Create(MachineReaction.Kind,
            policy.Document.Metadata.DefinitionId, policy.Document.Metadata.RevisionId, payload, policy.Document.Metadata.Provenance);
        Assert.Throws<InvalidOperationException>(() => MachineReaction.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(document)));
    }

    [Fact]
    public async Task Confirmation_policy_uses_committed_tender_link_and_requests_confirm_command()
    {
        var model = new TenderReactionModel();
        var (repository, context, commit) = await EntityMachineOccurrenceTests.Prepare(model.Tender.Entity,
            model.Tender.ConfirmReceipt, new TenderState("load-1", true, true, false, ""), new Receipt(true, true));
        var occurrence = Assert.Single(EntityMachineOccurrence.FromReceipt((await repository.CommitTransitionOperation(context, commit)).Receipt!));
        var invocation = MachineReactionInvocation.Prepare(model.ReceiptConfirmation, occurrence,
            new("worker", occurrence.Source.AuthorityScope, "test"), DateTimeOffset.UtcNow)!;
        var plan = model.ReceiptConfirmation.Compile().Plan!;
        var outcome = PortableValue.Concrete(plan.Definition.Result, ObservationValue.FromString("Confirmed"));
        var host = new ReferenceHost(ProcessOperationResult.Completed(outcome));
        var start = new ProcessStartReceipt(invocation.Start, DateTimeOffset.UtcNow);
        var activation = new ProcessActivation(new("confirm"), ProcessActivationCause.Start, DateTimeOffset.UtcNow,
            new(occurrence.Source.AuthorityScope, new("confirm"),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), plan.Document.Metadata.Provenance));
        var decision = ProcessReferenceInterpreter.Activate(plan, ProcessReferenceInterpreter.Create(plan, start), activation, host);
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.Equal(model.Confirm.Reference, host.Invocation!.Definition);
        Assert.Equal("load-1", host.Invocation.Subject.Value!.Value.GetRequiredString());
        Assert.True(host.Invocation.Input.Value!.Value.Fields![nameof(AcknowledgeLoad.Confirmed)].GetBoolean());
    }

    [Fact]
    public async Task Ambiguous_target_commit_is_reconciled_by_exact_operation_replay()
    {
        var f = await Fixture.Create(true);
        await f.Runtime.InitializeAsync(f.Context, f.Plan, f.Start);
        f.Adapter.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<TimeoutException>(() => f.Runtime.ActivateAsync(f.Context, f.Plan, f.Start.Request.InitialContinuation, f.Activation));
        var call = f.Adapter.LastInvocation!;
        var operation = new ProcessOperationOccurrence(call.Continuation, call.Activation, call.Token, call.Node, call.Occurrence);
        var uncertain = new DurableOperationFailure(DurableOperationFailurePhase.PostCommitPreAcknowledgement,
            DurableOperationEffectEvidence.Ambiguous, DurableOperationFailureDisposition.Retryable, "test.timeout");
        Assert.Equal(MachineReactionStatus.Indeterminate, f.Invocation.InspectFailure(operation, uncertain).Status);
        var before = await f.LoadRepository.TryGet(f.Context, "load-1", EntityReadOptions.Full.WithPartitionKey("test"));
        var resumed = await f.Runtime.ActivateAsync(f.Context, f.Plan, f.Start.Request.InitialContinuation, f.Activation);
        var retained = Assert.Single(resumed.Snapshot!.Checkpoint.Operations);
        var receipt = (await f.LoadRepository.ResolveTransitionOperation(f.Context,
            EntityTransitionReceiptReferences.Read(retained.Result.ReceiptReference!))).Receipt!;
        Assert.Equal(before!.ConcurrencyToken, receipt.Entity.ConcurrencyToken);
        Assert.Equal(MachineReactionStatus.Completed, f.Invocation.Inspect(resumed.Snapshot.Checkpoint, receipt).Status);
        var terminal = new DurableOperationFailure(DurableOperationFailurePhase.PreCall,
            DurableOperationEffectEvidence.NotExecuted, DurableOperationFailureDisposition.Terminal, "test.unavailable");
        Assert.Equal(MachineReactionStatus.Failed, f.Invocation.InspectFailure(operation, terminal).Status);
    }

    sealed class Fixture
    {
        internal TenderReactionModel Model = null!;
        internal EntityMachineOccurrence Source = null!;
        internal MachineReactionInvocation Invocation = null!;
        internal ProcessStartReceipt Start = null!;
        internal Cohesive.Processes.Compilation.CompiledProcessPlan Plan = null!;
        internal InMemoryEntityOutboxRepository LoadRepository = null!;
        internal ProcessDurableRuntime Runtime = null!;
        internal ProcessActivation Activation = null!;
        internal OperationContext Context = null!;
        internal RecordingAdapter Adapter = null!;

        internal static async Task<Fixture> Create(bool allowed)
        {
            var model = new TenderReactionModel();
            var (repository, context, commit) = await EntityMachineOccurrenceTests.Prepare(model.Tender.Entity,
                model.Tender.AcceptPending, new TenderState("load-1", false, false, false, ""), new SendResult(false));
            var source = Assert.Single(EntityMachineOccurrence.FromReceipt((await repository.CommitTransitionOperation(context, commit)).Receipt!));
            var invocation = MachineReactionInvocation.Prepare(model.PendingAcceptance, source,
                new("worker", source.Source.AuthorityScope, "reaction-policy/test"), DateTimeOffset.UtcNow)!;
            var start = new ProcessStartReceipt(invocation.Start, DateTimeOffset.UtcNow);
            var plan = model.PendingAcceptance.Compile().Plan!;
            var loadRepository = new InMemoryEntityOutboxRepository(model.Load, EntityPartitionKeyPolicy.Fixed("test"));
            await loadRepository.Upsert(context, new(model.Load.CreateState("load-1",
                new LoadAcknowledgementState(LoadAcknowledgementStatus.Ready, allowed)).Snapshot));
            Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
            var binding = new ProcessTransitionOperationBinding(model.MarkPending.Compile().Plan!, loadRepository, contracts!);
            var adapter = new RecordingAdapter(new EntityTransitionProcessOperationAdapter(call =>
                call.Definition == model.MarkPending.Reference ? binding : null));
            var runtime = new ProcessDurableRuntime(new InMemoryProcessDurableStore(), new NoOtherOperations(),
                new("reaction-worker", TimeSpan.FromMinutes(5)), transitionOperationAdapter: adapter);
            var activation = new ProcessActivation(new("activation/reaction"), ProcessActivationCause.Start, DateTimeOffset.UtcNow,
                new(source.Source.AuthorityScope, new("correlation/reaction"),
                    new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), plan.Document.Metadata.Provenance));
            return new() { Model = model, Source = source, Invocation = invocation, Start = start, Plan = plan,
                LoadRepository = loadRepository, Runtime = runtime, Activation = activation, Context = context, Adapter = adapter };
        }
    }

    sealed class RecordingAdapter(IProcessTransitionOperationAdapter inner) : IProcessTransitionOperationAdapter
    {
        internal int Calls;
        internal bool ThrowAfterCommit;
        internal ProcessTransitionInvocation? LastInvocation;
        public async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            Calls++;
            LastInvocation = invocation;
            var result = await inner.ExecuteAsync(context, invocation);
            if (ThrowAfterCommit) { ThrowAfterCommit = false; throw new TimeoutException("Simulated lost commit acknowledgement"); }
            return result;
        }
    }

    sealed class NoOtherOperations : IProcessReferenceHost
    {
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new NotSupportedException();
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new NotSupportedException();
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new NotSupportedException();
    }

    sealed class ReferenceHost(ProcessOperationResult result) : IProcessReferenceHost
    {
        internal ProcessTransitionInvocation? Invocation;
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
        { Invocation = invocation; return result; }
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new NotSupportedException();
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new NotSupportedException();
    }
}
