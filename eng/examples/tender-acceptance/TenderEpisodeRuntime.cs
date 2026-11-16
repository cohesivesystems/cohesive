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
    internal OperationContext Context { get; }
    internal InteractionAuthorityScope Authority { get; }
    internal IProcessDurableStore Store = new InMemoryProcessDurableStore();
    internal InMemoryEntityOutboxRepository Tenders { get; }
    internal InMemoryEntityOutboxRepository Loads { get; }
    internal MachineReactionTarget AcceptTarget { get; }
    internal MachineReactionTarget ConfirmTarget { get; }
    int operation;
    internal string OperationPrefix = "source";

    TenderEpisodeRuntime(InteractionAuthorityScope authority, TimeProvider? timeProvider)
    {
        Authority = authority;
        Context = OperationContext.Create(timeProvider: timeProvider);
        Tenders = new(Model.Tender, EntityPartitionKeyPolicy.Fixed("test"));
        Loads = new(Model.Load, EntityPartitionKeyPolicy.Fixed("test"));
        if (!InteractionContractCatalog.TryCreate([], out var contracts).IsValid)
            throw new InvalidOperationException("Empty interaction catalog did not validate.");
        AcceptTarget = MachineReactionTarget.Local(new(Model.MarkPending.Compile().Plan!, Loads, contracts!), Authority,
            (_, _) => ValueTask.FromResult(true));
        ConfirmTarget = MachineReactionTarget.Local(new(Model.ConfirmLoad.Compile().Plan!, Loads, contracts!), Authority,
            (_, _) => ValueTask.FromResult(true));
    }

    internal static async Task<TenderEpisodeRuntime> Create(int loadEpisode = 1, InteractionAuthorityScope? authority = null, TimeProvider? timeProvider = null)
    {
        var f = new TenderEpisodeRuntime(authority ?? new("tests", "test"), timeProvider);
        await f.Tenders.Upsert(f.Context, new(f.Model.Tender.CreateState("tender-1",
            ObservationValue.FromObject(new EpisodeTender(EpisodeTenderStatus.Offered, "load-1", 1)).Fields!).Snapshot));
        await f.Loads.Upsert(f.Context, new(f.Model.Load.CreateState("load-1",
            ObservationValue.FromObject(new EpisodeLoad(EpisodeLoadStatus.Ready, loadEpisode)).Fields!).Snapshot));
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

    // Fixture preparation only: not an application execution API. The missing direct receipt-backed
    // executor must reuse the native adapter's decision/projection/emission mechanism (see README).
    internal async Task<EntityTransitionOperationReceipt> Execute<TState>(InMemoryEntityOutboxRepository repository,
        EntityDefinition entity, string id, Transition<TState, EpisodeCommand, string> transition, EpisodeCommand command) where TState : notnull
    {
        var before = (await repository.TryGet(Context, id, EntityReadOptions.Full.WithPartitionKey("test")))!;
        var plan = transition.Compile().Plan!;
        var occurrence = new ProcessOperationOccurrence(new(new(OperationPrefix + "/" + ++operation), new("attempt")),
            new("activation"), new("token"), new("invoke"), occurrence: 0);
        var input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(command));
        var request = new EntityTransitionOperationRequest(occurrence, Authority, plan.DefinitionReference,
            new(new(repository.EntityType), before.Entity.EntityId), input);
        var result = await repository.ExecuteTransitionOperation(Context, request, () =>
        {
            var decision = TransitionReferenceInterpreter.DecideFullState(plan, occurrence.Activation, input,
                PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(before.Entity.Observation.Fields)));
            if (decision.Kind != TransitionDecisionKind.Applied)
                throw new InvalidOperationException("Tender replay setup transition was not applied.");
            var candidate = TransitionStateProjector.ApplyToEntity(entity, id, decision, before.Entity);
            return new(request, new(candidate.Snapshot, before.ConcurrencyToken),
                decision.Kind, ProcessOperationResult.Completed(decision.Outcome!), decision.GuaranteeDemands, decision.Evidence);
        });
        return result.Receipt ?? throw new InvalidOperationException("Tender replay setup did not produce an atomic receipt.");
    }
}
