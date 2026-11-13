using System.Text.Json;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Machines;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Machines;

namespace Cohesive.Tests.Machines;

public sealed class EntityMachineOccurrenceTests
{
    static readonly TenderState Offered = new("load-1", false, false, false, "");

    [Fact]
    public async Task Occurrence_is_implicit_in_atomic_receipt_and_stable_across_replay()
    {
        var model = new TenderEntityModel();
        var (repository, context, commit) = await Prepare(model.Entity, model.AcceptPending, Offered, new SendResult(false));
        Assert.Equal(EntityTransitionOperationDisposition.NotFound,
            (await repository.TryGetTransitionOperation(context, commit.Request)).Disposition);
        var committed = await repository.CommitTransitionOperation(context, commit);
        var receipt = Assert.IsType<EntityTransitionOperationReceipt>(committed.Receipt);
        var occurrence = Assert.Single(EntityMachineOccurrence.FromReceipt(receipt));
        Assert.Same(receipt, occurrence.Receipt);
        Assert.Equal(model.Machine.Definition.Document.Reference, occurrence.Movement.Contract);
        Assert.Equal(model.Machine.SendPending.Id, occurrence.Movement.Edge!.Value.Value);
        Assert.Equal(commit.Request.Reference, occurrence.Source);
        Assert.Empty(receipt.Commit.Result.Emissions);
        Assert.True(occurrence.Matches(model.Machine.SendPending));
        Assert.False(occurrence.Matches(model.Machine.SendConfirmed));
        Assert.True(receipt.Entity.Entity.Observation.GetField(nameof(TenderState.PendingReceipt)).GetBoolean());

        var replay = await repository.CommitTransitionOperation(context, commit);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Disposition);
        var repeated = Assert.Single(EntityMachineOccurrence.FromReceipt(replay.Receipt!));
        Assert.Equal(occurrence.Source, repeated.Source);
        Assert.Equal(occurrence.Movement.Node, repeated.Movement.Node);
        Assert.Equal(occurrence.Receipt.CommittedAtUtc, repeated.Receipt.CommittedAtUtc);
    }

    [Fact]
    public async Task Receipt_roundtrip_retains_occurrence_without_current_entity_read()
    {
        var model = new TenderEntityModel();
        var (repository, context, commit) = await Prepare(model.Entity, model.AcceptPending, Offered, new SendResult(false));
        var receipt = (await repository.CommitTransitionOperation(context, commit)).Receipt!;
        var restored = JsonSerializer.Deserialize<EntityTransitionOperationReceipt>(JsonSerializer.Serialize(receipt))!;
        var before = Assert.Single(EntityMachineOccurrence.FromReceipt(receipt));
        var after = Assert.Single(EntityMachineOccurrence.FromReceipt(restored));
        Assert.Equal(before.Source, after.Source);
        Assert.Equal(before.Movement.Contract, after.Movement.Contract);
        Assert.Equal(before.Movement.Edge, after.Movement.Edge);
        Assert.Equal(before.Movement.Node, after.Movement.Node);
    }

    [Fact]
    public async Task Guard_rejection_and_preservation_do_not_induce_occurrences()
    {
        var model = new TenderEntityModel();
        var rejected = await Prepare(model.Entity, model.AcceptPending, Offered, new SendResult(true));
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, rejected.Commit.DecisionKind);
        Assert.Empty(EntityMachineOccurrence.FromReceipt(
            (await rejected.Repository.CommitTransitionOperation(rejected.Context, rejected.Commit)).Receipt!));
        var preserved = await Prepare(model.Entity, model.EditNote, Offered, new UpdateNote("checked"));
        Assert.Empty(EntityMachineOccurrence.FromReceipt(
            (await preserved.Repository.CommitTransitionOperation(preserved.Context, preserved.Commit)).Receipt!));
    }

    [Fact]
    public async Task Lost_concurrency_fence_has_no_occurrence_receipt()
    {
        var model = new TenderEntityModel();
        var (repository, context, commit) = await Prepare(model.Entity, model.AcceptPending, Offered, new SendResult(false));
        await repository.Upsert(context, new(commit.Write.Entity));
        var result = await repository.CommitTransitionOperation(context, commit);
        Assert.Equal(EntityTransitionOperationDisposition.ConcurrencyConflict, result.Disposition);
        Assert.Null(result.Receipt);
    }

    [Fact]
    public async Task Typed_matching_requires_exact_revision_even_when_edge_names_match()
    {
        var model = new TenderEntityModel();
        var (repository, context, commit) = await Prepare(model.Entity, model.AcceptPending, Offered, new SendResult(false));
        var occurrence = Assert.Single(EntityMachineOccurrence.FromReceipt(
            (await repository.CommitTransitionOperation(context, commit)).Receipt!));
        var different = Machine.Define(model.Machine.Definition.Document.Reference.DefinitionId.Value, "different-revision");
        var ready = different.State("ready").Initial();
        var movement = different.Edge(model.Machine.SendPending.Id).From(ready).To(ready);
        Assert.False(occurrence.Matches(movement));
    }

    [Fact]
    public async Task Malformed_receipt_cannot_project_colliding_occurrence_identities()
    {
        var model = new TenderEntityModel();
        var (repository, context, commit) = await Prepare(model.Entity, model.AcceptPending, Offered, new SendResult(false));
        var receipt = (await repository.CommitTransitionOperation(context, commit)).Receipt!;
        var moved = Assert.Single(commit.Evidence.Trace.Where(item => item.Kind == TransitionTraceEventKind.MachineMoved));
        var evidence = new TransitionExecutionEvidence(commit.Evidence.Definition, commit.Evidence.Activation,
            [.. commit.Evidence.Trace, moved], commit.Evidence.MachineBinding);
        var malformed = new EntityTransitionOperationCommit(commit.Request, commit.Write, commit.DecisionKind,
            commit.Result, commit.GuaranteeDemands, evidence);
        Assert.Throws<InvalidOperationException>(() => EntityMachineOccurrence.FromReceipt(
            new(malformed, receipt.Entity, receipt.CommittedAtUtc)));
    }

    [Theory]
    [InlineData(TransitionOutcomeDisposition.Applied, 1)]
    [InlineData(TransitionOutcomeDisposition.NoChange, 1)]
    [InlineData(TransitionOutcomeDisposition.DomainRejected, 0)]
    public async Task Named_self_loop_is_observable_only_when_the_complete_operation_succeeds(
        TransitionOutcomeDisposition outcome, int expected)
    {
        var source = Machine.Define("loop", "1");
        var ready = source.State("ready").Initial();
        var record = source.Edge("record").From(ready).To(ready);
        var entity = new MachinePredicateBinding<TenderState, UpdateNote>(source.Build())
            .State(ready, state => true).Bind(ObjectEntityDefinition.For<TenderState>());
        var transition = TransitionAuthoring.Create<TenderState, UpdateNote, string>(entity.Shape,
            new("record"), new("1"), t => t.Move(record, (state, input) => "Rejected")
                .Return(new("result"), outcome, "Result"));
        var (repository, context, commit) = await Prepare(entity, transition, Offered, new UpdateNote(""));
        Assert.Contains(commit.Evidence.Trace, item => item.Kind == TransitionTraceEventKind.MachineMoved);
        var receipt = (await repository.CommitTransitionOperation(context, commit)).Receipt!;
        Assert.Equal(expected, EntityMachineOccurrence.FromReceipt(receipt).Length);
    }

    internal static async Task<(InMemoryEntityOutboxRepository Repository, OperationContext Context, EntityTransitionOperationCommit Commit)>
        Prepare<TInput>(EntityDefinition entity, Transition<TenderState, TInput, string> transition, TenderState state, TInput input, string processId = "process-1", string sourceId = "tender-1")
    {
        var context = OperationContext.Create();
        var repository = new InMemoryEntityOutboxRepository(entity, EntityPartitionKeyPolicy.Fixed("test"));
        var initial = await repository.Upsert(context, new(entity.CreateState(sourceId, ObservationValue.FromObject(state).Fields!).Snapshot));
        var compilation = transition.Compile();
        Assert.True(compilation.IsSuccessful);
        var plan = compilation.Plan!;
        var operation = new ProcessOperationOccurrence(new(new(processId), new("attempt-1")),
            new("activation-1"), new("token-1"), new("invoke"), occurrence: 0);
        var value = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(input));
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, operation.Activation, value,
            PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(state)));
        var request = new EntityTransitionOperationRequest(operation, new("tests", "test"),
            decision.Evidence.Definition, new(new(repository.EntityType), initial.Entity.EntityId), value);
        var candidate = TransitionStateProjector.ApplyToEntity(entity, sourceId, decision, initial.Entity);
        var commit = new EntityTransitionOperationCommit(request, new(candidate.Snapshot, initial.ConcurrencyToken),
            decision.Kind, ProcessOperationResult.Completed(decision.Outcome!), decision.GuaranteeDemands, decision.Evidence);
        return (repository, context, commit);
    }
}
