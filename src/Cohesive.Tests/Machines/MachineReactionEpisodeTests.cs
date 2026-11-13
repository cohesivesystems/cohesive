using Cohesive.Execution;
using Cohesive.Adapters.Postgres;
using Npgsql;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Model;
using Cohesive.Machines;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Machines;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Machines;

namespace Cohesive.Tests.Machines;

public sealed class MachineReactionEpisodeTests
{
    [Fact]
    public async Task Two_cycles_correlate_their_own_acceptance_and_reject_late_old_confirmation()
    {
        var f = await Fixture.Create();
        var receipts = await f.TwoCycles();
        var secondConfirmation = Assert.Single(await f.Confirmation().AdmitAsync(f.Context, receipts.Confirmation2));
        await f.Confirmation().RecoverAsync(f.Context, secondConfirmation);
        var firstAcceptance = Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance1));
        Assert.Equal(MachineReactionStatus.Completed, (await f.Acceptance().RecoverAsync(f.Context, firstAcceptance))!.Status);
        var stillWaiting = (await f.Confirmation().RecoverAsync(f.Context, secondConfirmation))!;
        Assert.Equal(MachineReactionStatus.Pending, stillWaiting.Status);
        Assert.NotEqual(firstAcceptance, stillWaiting.Prerequisite);
        Assert.Empty(stillWaiting.Attempts);

        f.Restart();
        await f.Execute(f.Loads, f.Model.Load, "load-1", f.Model.ReviseLoad, new EpisodeCommand(2));
        var secondAcceptance = Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance2));
        Assert.Equal(stillWaiting.Prerequisite, secondAcceptance);
        Assert.NotEqual(firstAcceptance, secondAcceptance);
        Assert.Equal(MachineReactionStatus.Completed, (await f.Acceptance().RecoverAsync(f.Context, secondAcceptance))!.Status);
        Assert.Equal(secondAcceptance, Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance2)));

        // Both load and old confirmation are in otherwise admissible states: the episode guard rejects it.
        var firstConfirmation = Assert.Single(await f.Confirmation().AdmitAsync(f.Context, receipts.Confirmation1));
        await f.Confirmation().RecoverAsync(f.Context, firstConfirmation);
        var stale = (await f.Confirmation().RecoverAsync(f.Context, firstConfirmation))!;
        Assert.Equal(MachineReactionStatus.Rejected, stale.Status);
        Assert.Equal(firstAcceptance, stale.Prerequisite);
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, stale.Receipt!.Commit.DecisionKind);

        var lost = new LostAcknowledgement(f.ConfirmTarget);
        Assert.Equal(MachineReactionStatus.Indeterminate, (await f.Confirmation(lost).RecoverAsync(f.Context, secondConfirmation))!.Status);
        f.Restart();
        var completed = (await f.Confirmation().RecoverAsync(f.Context, secondConfirmation))!;
        Assert.Equal(MachineReactionStatus.Completed, completed.Status);
        Assert.Equal(2, completed.Attempts.Length);
        Assert.Equal(completed.Attempts[0].Invocation, completed.Attempts[1].Invocation);
        Assert.Equal(secondConfirmation, Assert.Single(await f.Confirmation().AdmitAsync(f.Context, receipts.Confirmation2)));
        var load = (await f.Loads.TryGet(f.Context, "load-1", EntityReadOptions.Full.WithPartitionKey("test")))!;
        Assert.Equal(2, load.Entity.Observation.GetField(nameof(EpisodeLoad.AcceptanceEpisode)).GetInt32());
        Assert.Equal(EpisodeLoadStatus.Confirmed.ToString(), load.Entity.Observation.GetField(nameof(EpisodeLoad.Status)).GetString());
        var inspection = (await f.Confirmation().InspectLifecycleAsync(f.Context, secondConfirmation))!;
        Assert.Equal(2, inspection.Episode!.Value!.Value.GetInt32());
        var recovery = new MachineReactionRecovery(f.Store, [f.Acceptance(), f.Confirmation()]);
        Assert.Equal(secondConfirmation, Assert.Single((await recovery.InspectDependentsAsync(f.Context, secondAcceptance)).Progress).Key);
        Assert.Empty((await recovery.RunPageAsync(f.Context)).Progress);
    }

    [Fact]
    public async Task Abandoned_first_episode_does_not_block_the_second()
    {
        var f = await Fixture.Create(loadEpisode: 2);
        var receipts = await f.TwoCycles();
        var first = Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance1));
        var inspected = (await f.Acceptance().InspectLifecycleAsync(f.Context, first))!;
        var cancel = new CancelProcessCommand(ProcessControlCommand.CurrentSchemaVersion,
            new(new("abandon-first"), new("abandon-first"), first, new("operator", f.Authority, "test"), f.Context.UtcNow,
                f.Model.Acceptance.Document.Metadata.Provenance),
            new(new(first, inspected.Control.CurrentAttempt.AttemptId), inspected.Control.Revision), new("tender.revised"));
        await f.Acceptance().AbandonAsync(f.Context, cancel, (_, _, _) => ValueTask.FromResult(true));
        var oldConfirmation = Assert.Single(await f.Confirmation().AdmitAsync(f.Context, receipts.Confirmation1));
        var blocked = (await f.Confirmation().RecoverAsync(f.Context, oldConfirmation))!;
        Assert.Equal("machine.reaction.prerequisite.abandoned", blocked.Diagnostic!.Code);
        var next = Assert.Single(await f.Confirmation().AdmitAsync(f.Context, receipts.Confirmation2));
        await f.Confirmation().RecoverAsync(f.Context, next);
        var accepted = Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance2));
        await f.Acceptance().RecoverAsync(f.Context, accepted);
        Assert.Equal(MachineReactionStatus.Completed, (await f.Confirmation().RecoverAsync(f.Context, next))!.Status);
    }

    [Fact]
    public void Episode_selector_roundtrips_is_inherited_and_rejects_incompatible_or_computed_keys()
    {
        var model = new TenderEpisodeModel();
        var confirmationPlan = model.ConfirmReceipt.Compile().Plan!;
        var staleSource = TransitionReferenceInterpreter.DecideFullState(confirmationPlan, new("late-source-confirmation"),
            PortableValue.Concrete(confirmationPlan.Definition.Input, ObservationValue.FromObject(new EpisodeCommand(1))),
            PortableValue.Concrete(confirmationPlan.Definition.Observation,
                ObservationValue.FromObject(new EpisodeTender(EpisodeTenderStatus.Pending, "load-1", 2))));
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, staleSource.Kind);
        var restored = MachineReaction.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(model.Confirmation.Document));
        Assert.Equal(model.Confirmation.Document.Reference, restored.Document.Reference);
        Assert.Equal(model.Acceptance.Payload.EpisodeKey, restored.Payload.EpisodeKey);
        Assert.Equal(model.Confirmation.Document.Reference, new TenderEpisodeModel().Confirmation.Document.Reference);
        Assert.Throws<ArgumentException>(() => MachineReaction.On<EpisodeTender>(model.Tender, model.Confirmed, "mismatch", "1")
            .OncePerEpisode(state => state.LoadId).After(model.Acceptance));
        Assert.Throws<ArgumentException>(() => MachineReaction.On<EpisodeTender>(model.Tender, model.Accepted, "computed", "1")
            .OncePerEpisode(state => state.AcceptanceEpisode + 1));
        Assert.Throws<InvalidOperationException>(() => MachineReaction.On<EpisodeTender>(model.Tender, model.Confirmed, "overwrite", "1")
            .After(model.Acceptance).OncePerSourceEntity());
        var malformed = model.Confirmation.Payload with { EpisodeKey = FieldPath.FromField(nameof(EpisodeTender.LoadId)) };
        var document = ExecutionDefinitionDocument.Create(MachineReaction.Kind, new("malformed"), new("1"), malformed,
            model.Confirmation.Document.Metadata.Provenance);
        Assert.Throws<InvalidOperationException>(() => MachineReaction.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(document)));
        foreach (var invalid in new[]
        {
            model.Acceptance.Payload with { EpisodeKey = null },
            model.Acceptance.Payload with { IdentityScope = MachineReactionIdentityScope.SourceOccurrence },
            model.Acceptance.Payload with { EpisodeKey = FieldPath.FromField("missing") }
        })
        {
            var invalidDocument = ExecutionDefinitionDocument.Create(MachineReaction.Kind, new("invalid-episode"), new("1"), invalid,
                model.Acceptance.Document.Metadata.Provenance);
            Assert.Throws<InvalidOperationException>(() => MachineReaction.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(invalidDocument)));
        }
    }

    [Fact]
    public async Task Same_episode_conflicts_on_distinct_receipts_and_restore_rejects_a_changed_key()
    {
        var f = await Fixture.Create();
        var receipts = await f.TwoCycles();
        var id = Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance1));
        var alternate = await Fixture.Create();
        alternate.OperationPrefix = "different-source-operation";
        var different = await alternate.Execute(alternate.Tenders, alternate.Model.Tender, "tender-1", alternate.Model.Accept, new EpisodeCommand(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Acceptance().AdmitAsync(f.Context, different));
        var start = (await f.Store.LoadAsync(f.Context, id))!.Checkpoint.Start.Request;
        var changed = new ProcessStartRequest(start.SchemaVersion, start.Definition, start.Context, start.InitialContinuation,
            PortableValue.Concrete(start.Input!.Contract,
                ObservationValue.FromObject(new EpisodeTender(EpisodeTenderStatus.Pending, "load-1", 2))), start.Causation);
        Assert.Throws<InvalidOperationException>(() => MachineReactionInvocation.Restore(f.Model.Acceptance, changed));
        Assert.Equal(1, MachineReactionInvocation.Restore(f.Model.Acceptance, start).Episode!.Value!.Value.GetInt32());
    }

    [Fact]
    public async Task Episode_identity_retains_the_authority_boundary()
    {
        var first = await Fixture.Create();
        var other = await Fixture.Create(authority: new("tests", "another-tenant"));
        var firstReceipt = await first.Execute(first.Tenders, first.Model.Tender, "tender-1", first.Model.Accept, new EpisodeCommand(1));
        var otherReceipt = await other.Execute(other.Tenders, other.Model.Tender, "tender-1", other.Model.Accept, new EpisodeCommand(1));
        Assert.NotEqual(Assert.Single(await first.Acceptance().AdmitAsync(first.Context, firstReceipt)),
            Assert.Single(await other.Acceptance().AdmitAsync(other.Context, otherReceipt)));
        Assert.Empty(await first.Acceptance().AdmitAsync(first.Context, otherReceipt));
    }

    [Fact]
    public void Scalar_episode_keys_are_typed_canonical_and_reject_missing_or_blank_values()
    {
        var guid = Guid.NewGuid();
        var guidPolicy = KeyPolicy<Guid>();
        Assert.Equal(Select(guidPolicy, ObservationValue.FromString(guid.ToString("D"))),
            Select(guidPolicy, ObservationValue.FromString(guid.ToString("B").ToUpperInvariant())));
        var integerPolicy = KeyPolicy<int>();
        Assert.Equal(Select(integerPolicy, ObservationValue.FromInt64(12)), Select(integerPolicy, ObservationValue.FromDecimal(12m)));
        Assert.Equal(ProcessStorageContentFingerprints.Value(Select(integerPolicy, ObservationValue.FromInt64(12))!),
            ProcessStorageContentFingerprints.Value(Select(integerPolicy, ObservationValue.FromDecimal(12m))!));
        Assert.Throws<InvalidOperationException>(() => Select(integerPolicy, ObservationValue.FromString("12")));
        var stringPolicy = KeyPolicy<string>();
        Assert.NotEqual(Select(stringPolicy, ObservationValue.FromString("A")), Select(stringPolicy, ObservationValue.FromString("a")));
        Assert.Throws<InvalidOperationException>(() => Select(stringPolicy, ObservationValue.FromString(" ")));
        Assert.Throws<InvalidOperationException>(() => stringPolicy.SelectEpisode(PortableValue.Concrete(
            ValueContract.FromShape(stringPolicy.Payload.Source), ObservationValue.EmptyObject)));
        Assert.Throws<InvalidOperationException>(() => KeyPolicy<bool>());
        Assert.Equal(long.MaxValue, Select(KeyPolicy<long>(), ObservationValue.FromInt64(long.MaxValue))!.Value!.Value.GetInt64());
    }

    [Fact]
    public async Task Release_proof_from_another_episode_cannot_bypass_the_wait()
    {
        var f = await Fixture.Create();
        var receipts = await f.TwoCycles();
        var second = Assert.Single(await f.Confirmation().AdmitAsync(f.Context, receipts.Confirmation2));
        await f.Confirmation().RecoverAsync(f.Context, second);
        var first = Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance1));
        var success = (await f.Acceptance().RecoverAsync(f.Context, first))!;
        var snapshot = (await f.Store.LoadAsync(f.Context, second))!;
        var wait = Assert.Single(snapshot.Checkpoint.Continuation.Waits);
        var target = new ProcessTokenInteractionTarget(snapshot.Checkpoint.ContinuationIdentity, wait.Token, wait.RegistrationId);
        var operation = success.Receipt!.Request.Operation;
        var identity = second.Value + "/prerequisite";
        var signal = new SignalEnvelope(InteractionEnvelope.CurrentSchemaVersion,
            new(new(identity), new ProcessInteractionOrigin(f.Model.Acceptance.Payload.Process.Reference,
                operation.Node, operation.Continuation, operation.Activation, operation.Token),
                new(identity), null, f.Authority, new(identity), null,
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), f.Model.Confirmation.Document.Metadata.Provenance),
            new SignalContractReference(f.Model.Confirmation.PrerequisiteSignal!.Reference),
            PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.String)), ObservationValue.FromString(success.Receipt.Commit.Fingerprint.Value)), target);
        await f.Store.AdmitInputAsync(f.Context, second, new(target, signal), f.Context.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Confirmation().RecoverAsync(f.Context, second));
        Assert.Empty(ProcessTransitionAttempt.Read((await f.Store.LoadAsync(f.Context, second))!));
    }

    [Cohesive.Tests.Postgres.PostgresProcessDurableStoreTests.PostgresFact]
    public async Task Postgres_reopens_episode_wait_and_requires_the_exact_cycle()
    {
        var connection = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!;
        await using var pool = NpgsqlDataSource.Create(connection);
        var options = new PostgresProcessDurableStoreOptions("reaction-episodes/" + Guid.NewGuid().ToString("N"));
        var f = await Fixture.Create();
        var store = new PostgresProcessDurableStore(pool, options);
        await store.EnsureCreatedAsync(f.Context);
        f.Store = store;
        var receipts = await f.TwoCycles();
        var next = Assert.Single(await f.Confirmation().AdmitAsync(f.Context, receipts.Confirmation2));
        await f.Confirmation().RecoverAsync(f.Context, next);
        var first = Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance1));
        await f.Acceptance().RecoverAsync(f.Context, first);
        await using var replacement = NpgsqlDataSource.Create(connection);
        f.Store = new PostgresProcessDurableStore(replacement, options);
        var waiting = (await f.Confirmation().RecoverAsync(f.Context, next))!;
        Assert.Equal(MachineReactionStatus.Pending, waiting.Status);
        Assert.NotEqual(first, waiting.Prerequisite);
        await f.Execute(f.Loads, f.Model.Load, "load-1", f.Model.ReviseLoad, new EpisodeCommand(2));
        var second = Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance2));
        Assert.Equal(waiting.Prerequisite, second);
        await f.Acceptance().RecoverAsync(f.Context, second);
        Assert.Equal(MachineReactionStatus.Completed, (await f.Confirmation().RecoverAsync(f.Context, next))!.Status);
        Assert.Equal(2, (await f.Confirmation().InspectLifecycleAsync(f.Context, next))!.Episode!.Value!.Value.GetInt32());
    }

    static PortableValue? Select(MachineReaction policy, ObservationValue key) => policy.SelectEpisode(PortableValue.Concrete(
        ValueContract.FromShape(policy.Payload.Source), ObservationValue.FromFieldAssignments([
            new(FieldPath.FromField(nameof(KeyFacts<int>.Episode)), key),
            new(FieldPath.FromField(nameof(KeyFacts<int>.LoadId)), ObservationValue.FromString("load-1"))])));

    sealed record KeyFacts<TKey>(TKey Episode, string LoadId) where TKey : notnull;

    static MachineReaction KeyPolicy<TKey>() where TKey : notnull
    {
        var machine = Machine.Define("episode-key", "1");
        var ready = machine.State("ready").Initial();
        var move = machine.Edge("record").From(ready).To(ready);
        var source = new MachinePredicateBinding<KeyFacts<TKey>, EpisodeCommand>(machine.Build())
            .State(ready, _ => true).Bind(ObjectEntityDefinition.For<KeyFacts<TKey>>());
        return MachineReaction.On<KeyFacts<TKey>>(source, move, "episode-key.policy", "1")
            .OncePerEpisode(state => state.Episode)
            .Request(new TenderEpisodeModel().MarkPending, state => state.LoadId, _ => new EpisodeCommand(1));
    }

    internal sealed class Fixture
    {
        internal TenderEpisodeModel Model { get; } = new();
        internal OperationContext Context { get; } = OperationContext.Create();
        internal InteractionAuthorityScope Authority { get; init; } = new("tests", "test");
        internal IProcessDurableStore Store = new InMemoryProcessDurableStore();
        internal InMemoryEntityOutboxRepository Tenders = null!;
        internal InMemoryEntityOutboxRepository Loads = null!;
        internal MachineReactionTarget AcceptTarget = null!;
        internal MachineReactionTarget ConfirmTarget = null!;
        int operation;
        internal string OperationPrefix = "source";

        internal static async Task<Fixture> Create(int loadEpisode = 1, InteractionAuthorityScope? authority = null)
        {
            var f = new Fixture { Authority = authority ?? new("tests", "test") };
            f.Tenders = new(f.Model.Tender, EntityPartitionKeyPolicy.Fixed("test"));
            f.Loads = new(f.Model.Load, EntityPartitionKeyPolicy.Fixed("test"));
            await f.Tenders.Upsert(f.Context, new(f.Model.Tender.CreateState("tender-1",
                ObservationValue.FromObject(new EpisodeTender(EpisodeTenderStatus.Offered, "load-1", 1)).Fields!).Snapshot));
            await f.Loads.Upsert(f.Context, new(f.Model.Load.CreateState("load-1",
                ObservationValue.FromObject(new EpisodeLoad(EpisodeLoadStatus.Ready, loadEpisode)).Fields!).Snapshot));
            Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
            f.AcceptTarget = MachineReactionTarget.Local(new(f.Model.MarkPending.Compile().Plan!, f.Loads, contracts!), f.Authority,
                (_, _) => ValueTask.FromResult(true));
            f.ConfirmTarget = MachineReactionTarget.Local(new(f.Model.ConfirmLoad.Compile().Plan!, f.Loads, contracts!), f.Authority,
                (_, _) => ValueTask.FromResult(true));
            return f;
        }

        internal MachineReactionHost Acceptance() => Host(Model.Acceptance, AcceptTarget);
        internal MachineReactionHost Confirmation(MachineReactionTarget? target = null) => Host(Model.Confirmation, target ?? ConfirmTarget, Acceptance());
        MachineReactionHost Host(MachineReaction policy, MachineReactionTarget target, MachineReactionHost? prerequisite = null) => new(
            policy, Store, target, (_, _) => ValueTask.FromResult<ProcessControlAuthorizationContext?>(new("worker", Authority, "test")),
            new("worker", TimeSpan.FromMinutes(5)), new((_, _) => ValueTask.FromResult(true), retryDelay: TimeSpan.Zero), prerequisite);

        internal void Restart() => Store = new InMemoryProcessDurableStore(ProcessDurableStoreJsonSerializer.Deserialize(
            ProcessDurableStoreJsonSerializer.Serialize(((InMemoryProcessDurableStore)Store).CaptureDocument())));

        internal async Task<(EntityTransitionOperationReceipt Acceptance1, EntityTransitionOperationReceipt Confirmation1,
            EntityTransitionOperationReceipt Acceptance2, EntityTransitionOperationReceipt Confirmation2)> TwoCycles()
        {
            var a1 = await Execute(Tenders, Model.Tender, "tender-1", Model.Accept, new EpisodeCommand(1));
            var c1 = await Execute(Tenders, Model.Tender, "tender-1", Model.ConfirmReceipt, new EpisodeCommand(1));
            await Execute(Tenders, Model.Tender, "tender-1", Model.Resubmit, new EpisodeCommand(2));
            var a2 = await Execute(Tenders, Model.Tender, "tender-1", Model.Accept, new EpisodeCommand(2));
            var c2 = await Execute(Tenders, Model.Tender, "tender-1", Model.ConfirmReceipt, new EpisodeCommand(2));
            return (a1, c1, a2, c2);
        }

        internal async Task<EntityTransitionOperationReceipt> Execute<TState>(InMemoryEntityOutboxRepository repository,
            EntityDefinition entity, string id, Transition<TState, EpisodeCommand, string> transition, EpisodeCommand command) where TState : notnull
        {
            var before = (await repository.TryGet(Context, id, EntityReadOptions.Full.WithPartitionKey("test")))!;
            var plan = transition.Compile().Plan!;
            var occurrence = new ProcessOperationOccurrence(new(new(OperationPrefix + "/" + ++operation), new("attempt")),
                new("activation"), new("token"), new("invoke"), occurrence: 0);
            var input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(command));
            var decision = TransitionReferenceInterpreter.DecideFullState(plan, occurrence.Activation, input,
                PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(before.Entity.Observation.Fields)));
            Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
            var request = new EntityTransitionOperationRequest(occurrence, Authority, decision.Evidence.Definition,
                new(new(repository.EntityType), before.Entity.EntityId), input);
            var candidate = TransitionStateProjector.ApplyToEntity(entity, id, decision, before.Entity);
            var commit = new EntityTransitionOperationCommit(request, new(candidate.Snapshot, before.ConcurrencyToken),
                decision.Kind, ProcessOperationResult.Completed(decision.Outcome!), decision.GuaranteeDemands, decision.Evidence);
            return (await repository.CommitTransitionOperation(Context, commit)).Receipt!;
        }
    }

    sealed class LostAcknowledgement(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        { await inner.ExecuteAsync(context, invocation); throw new TimeoutException("Lost confirmation acknowledgement"); }
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }
}
