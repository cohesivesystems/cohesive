using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Machines;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;

namespace Cohesive.Examples.TenderAcceptance;

// Isolated, deterministic native realization shared by the executable sketch and conformance tests.
internal sealed class TenderEpisodeRuntime
{
    internal TenderEpisodeModel Model { get; } = new();
    internal OperationContext Context { get; init; } = OperationContext.Create();
    internal InteractionAuthorityScope Authority { get; init; } = new("tests", "test");
    internal IProcessDurableStore Store = new InMemoryProcessDurableStore();
    internal InMemoryEntityOutboxRepository Tenders = null!;
    internal InMemoryEntityOutboxRepository Loads = null!;
    internal MachineReactionTarget AcceptTarget = null!;
    internal MachineReactionTarget ConfirmTarget = null!;
    int operation;
    internal string OperationPrefix = "source";

    internal static async Task<TenderEpisodeRuntime> Create(int loadEpisode = 1, InteractionAuthorityScope? authority = null, TimeProvider? timeProvider = null)
    {
        var f = new TenderEpisodeRuntime { Authority = authority ?? new("tests", "test"), Context = OperationContext.Create(timeProvider: timeProvider) };
        f.Tenders = new(f.Model.Tender, EntityPartitionKeyPolicy.Fixed("test"));
        f.Loads = new(f.Model.Load, EntityPartitionKeyPolicy.Fixed("test"));
        await f.Tenders.Upsert(f.Context, new(f.Model.Tender.CreateState("tender-1",
            ObservationValue.FromObject(new EpisodeTender(EpisodeTenderStatus.Offered, "load-1", 1)).Fields!).Snapshot));
        await f.Loads.Upsert(f.Context, new(f.Model.Load.CreateState("load-1",
            ObservationValue.FromObject(new EpisodeLoad(EpisodeLoadStatus.Ready, loadEpisode)).Fields!).Snapshot));
        if (!InteractionContractCatalog.TryCreate([], out var contracts).IsValid)
            throw new InvalidOperationException("Empty interaction catalog did not validate.");
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
        if (decision.Kind != TransitionDecisionKind.Applied)
            throw new InvalidOperationException("Tender replay setup transition was not applied.");
        var request = new EntityTransitionOperationRequest(occurrence, Authority, decision.Evidence.Definition,
            new(new(repository.EntityType), before.Entity.EntityId), input);
        var candidate = TransitionStateProjector.ApplyToEntity(entity, id, decision, before.Entity);
        var commit = new EntityTransitionOperationCommit(request, new(candidate.Snapshot, before.ConcurrencyToken),
            decision.Kind, ProcessOperationResult.Completed(decision.Outcome!), decision.GuaranteeDemands, decision.Evidence);
        return (await repository.CommitTransitionOperation(Context, commit)).Receipt!;
    }
}
