using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Machines;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.IR;

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
    internal EntityTransitionClient TenderTransitions { get; }
    internal EntityTransitionClient LoadTransitions { get; }
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
        TenderTransitions = new(Tenders, Authority, authorize: static (_, _) => ValueTask.FromResult(true));
        LoadTransitions = new(Loads, Authority, authorize: static (_, _) => ValueTask.FromResult(true));
        TenderTransitions.Prepare(Model.Accept);
        TenderTransitions.Prepare(Model.ConfirmReceipt);
        TenderTransitions.Prepare(Model.Resubmit);
        LoadTransitions.Prepare(Model.ReviseLoad);
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
        var a1 = await Execute(TenderTransitions, "tender-1", Model.Accept, new EpisodeCommand(1));
        var c1 = await Execute(TenderTransitions, "tender-1", Model.ConfirmReceipt, new EpisodeCommand(1));
        await Execute(TenderTransitions, "tender-1", Model.Resubmit, new EpisodeCommand(2));
        var a2 = await Execute(TenderTransitions, "tender-1", Model.Accept, new EpisodeCommand(2));
        var c2 = await Execute(TenderTransitions, "tender-1", Model.ConfirmReceipt, new EpisodeCommand(2));
        return (a1, c1, a2, c2);
    }

    internal async Task<EntityTransitionOperationReceipt> Execute<TState>(EntityTransitionClient transitions,
        string id, Transition<TState, EpisodeCommand, string> transition, EpisodeCommand command) where TState : notnull
    {
        var result = await transitions.ExecuteAsync(transition, id, command,
            operationId: new(OperationPrefix + "/" + ++operation), context: Context);
        var receipt = result.Receipt ?? throw new InvalidOperationException("Tender replay setup did not produce an atomic receipt.");
        if (receipt.Commit.DecisionKind != TransitionDecisionKind.Applied)
            throw new InvalidOperationException("Tender replay setup transition was not applied.");
        return receipt;
    }
}
