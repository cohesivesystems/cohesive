using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class EntityTransitionsTests
{
    static readonly InteractionAuthorityScope Authority = new("typed-transitions", "tenant/a");
    static InMemoryEntityOutboxRepository Repository() => new(RunControlFixture.Entity, EntityPartitionKeyPolicy.Fixed("tenant/a"));
    static EntityTransitions Bind(IEntityRepository repository) => new(repository, Authority,
        static (_, _) => ValueTask.FromResult(true), RunControlFixture.Contracts());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Typed_and_portable_calls_share_the_exact_receipt_and_emissions(bool typedFirst)
    {
        var context = OperationContext.Create();
        var repository = Repository();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var typed = Bind(repository);
        var transition = RunControlFixture.Start;
        var plan = transition.Compile().Plan!;
        var direct = new DirectTransitionExecutor(plan, repository, RunControlFixture.Contracts(), Authority,
            static (_, _) => ValueTask.FromResult(true));
        var operationId = new ActivationId("start/1");
        ValueTask<EntityTransitionExecutionResult> Typed() => typed.ExecuteAsync(transition, "run/1", new StartRun(0),
            operationId, context, before.ConcurrencyToken);
        ValueTask<EntityTransitionExecutionResult> Portable() => direct.ExecuteAsync(context, new(operationId),
            new(new(repository.EntityType), new("run/1")),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new StartRun(0))), before.ConcurrencyToken);
        var committed = await (typedFirst ? Typed() : Portable());
        var later = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Status = "completed" }, version: 3));
        var replayed = await (typedFirst ? Portable() : Typed());
        Assert.Equal(EntityTransitionOperationDisposition.Committed, committed.Operation!.Disposition);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replayed.Operation!.Disposition);
        Assert.Same(committed.Receipt, replayed.Receipt);
        Assert.Equal(committed.Receipt!.Request.Reference, EntityTransitionReceiptReferences.Read(replayed.Result.ReceiptReference!));
        Assert.IsType<TransitionInteractionOrigin>(Assert.Single(replayed.Result.Emissions).Context.Origin);
        Assert.Equal(later, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
    }

    [Fact]
    public async Task Preparation_is_shared_concurrently_without_IO_but_authorization_is_always_fresh()
    {
        var repository = new ObservedRepository(Repository());
        var allowed = false;
        var authorizations = 0;
        var transitions = new EntityTransitions(repository, Authority,
            (_, _) => { authorizations++; return ValueTask.FromResult(allowed); }, RunControlFixture.Contracts());
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => transitions.Prepare(RunControlFixture.Start))));
        var definitionReads = repository.DefinitionReads;
        Assert.True(definitionReads > 0);
        transitions.Prepare(RunControlFixture.Start);
        Assert.Equal(definitionReads, repository.DefinitionReads);
        Assert.Equal(0, repository.Reads);
        Assert.Equal(0, authorizations);

        var context = OperationContext.Create();
        var denied = await transitions.ExecuteAsync(RunControlFixture.Start, "run/1", new StartRun(0), new("auth/1"), context);
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.AuthorizationDenied, denied.Result.Failure!.Code);
        Assert.Equal(0, repository.Reads);
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        allowed = true;
        Assert.NotNull((await transitions.ExecuteAsync(RunControlFixture.Start, "run/1", new StartRun(0), new("auth/1"), context)).Receipt);
        allowed = false;
        var reads = repository.Reads;
        var replayDenied = await transitions.ExecuteAsync(RunControlFixture.Start, "run/1", new StartRun(0), new("auth/1"), context);
        Assert.Null(replayDenied.Receipt);
        Assert.Equal(reads, repository.Reads);
        Assert.Equal(3, authorizations);
    }

    [Fact]
    public async Task Prepared_documents_do_not_share_authorization_or_authority_between_bindings()
    {
        var repository = Repository();
        var context = OperationContext.Create();
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var allowed = Bind(repository);
        var foreignAuthority = new InteractionAuthorityScope("other", "tenant/a");
        EntityTransitionOperationRequest? seen = null;
        var denied = new EntityTransitions(repository, foreignAuthority,
            (_, request) => { seen = request; return ValueTask.FromResult(false); }, RunControlFixture.Contracts());
        allowed.Prepare(RunControlFixture.Start);
        denied.Prepare(RunControlFixture.Start);
        Assert.NotNull((await allowed.ExecuteAsync(RunControlFixture.Start, "run/1", new StartRun(0), new("scoped/1"), context)).Receipt);
        Assert.Null((await denied.ExecuteAsync(RunControlFixture.Start, "run/1", new StartRun(0), new("scoped/1"), context)).Receipt);
        Assert.Equal(foreignAuthority, seen!.AuthorityScope);
    }

    [Fact]
    public async Task Mutable_commands_are_projected_afresh_and_revision_changes_are_not_cached_by_name()
    {
        var repository = Repository();
        var context = OperationContext.Create();
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var transitions = new EntityTransitions(repository, Authority, static (_, _) => ValueTask.FromResult(true));
        var first = Revision("1");
        var second = Revision("2");
        var command = new MutableCommand { Attempt = 1 };
        var committed = await transitions.ExecuteAsync(first, "run/1", command, new("revision/1"), context);
        Assert.NotNull(committed.Receipt);
        command.Attempt = 2;
        Assert.Equal(EntityTransitionOperationDisposition.IdentityConflict,
            (await transitions.ExecuteAsync(first, "run/1", command, new("revision/1"), context)).Operation!.Disposition);
        command.Attempt = 1;
        Assert.Same(committed.Receipt, (await transitions.ExecuteAsync(first, "run/1", command, new("revision/1"), context)).Receipt);
        Assert.Equal(EntityTransitionOperationDisposition.IdentityConflict,
            (await transitions.ExecuteAsync(second, "run/1", command, new("revision/1"), context)).Operation!.Disposition);
        var unchanged = await transitions.ExecuteAsync(second, "run/1", command, new("revision/2"), context);
        Assert.Equal(second.Reference, unchanged.Receipt!.Request.Transition);
        Assert.Equal(TransitionDecisionKind.NoChange, unchanged.Receipt.Commit.DecisionKind);
        Assert.Equal(nameof(TransitionDecisionKind.NoChange), Assert.Single(unchanged.Receipt.Commit.Evidence.Trace,
            item => item.Kind == TransitionTraceEventKind.OutcomeReturned).Detail);
        Assert.Same(unchanged.Receipt, (await transitions.ExecuteAsync(second, "run/1", command, new("revision/2"), context)).Receipt);
    }

    [Fact]
    public void Failed_preparation_preserves_diagnostics_and_does_not_poison_retry()
    {
        var repository = new ObservedRepository(Repository()) { AtomicReceipts = false };
        var transitions = Bind(repository);
        var failure = Assert.Throws<ProcessTransitionBindingException>(() => transitions.Prepare(RunControlFixture.Start));
        Assert.Contains(failure.Validation.Diagnostics, diagnostic => diagnostic.Code == ProcessTransitionBindingDiagnosticCodes.ReceiptCapabilityMissing);
        Assert.Equal(0, repository.Reads);
        repository.AtomicReceipts = true;
        transitions.Prepare(RunControlFixture.Start);
        var prepared = repository.DefinitionReads;
        transitions.Prepare(RunControlFixture.Start);
        Assert.Equal(prepared, repository.DefinitionReads);
    }

    [Fact]
    public void Invalid_compilation_and_mismatched_partition_fail_before_IO()
    {
        var repository = new ObservedRepository(Repository());
        var incomplete = TransitionAuthoring.Create<RunControl, StartRun, string>(RunControlFixture.Entity.Shape,
            new("typed/incomplete"), new("1"), author => author.Set(new("attempt"), state => state.Attempt, (state, command) => command.ExpectedAttempt));
        var failure = Assert.Throws<ProcessTransitionBindingException>(() => Bind(repository).Prepare(incomplete));
        Assert.False(failure.Validation.IsValid);
        var wrongPartition = new EntityTransitions(repository, Authority, static (_, _) => ValueTask.FromResult(true), partitionKey: "other");
        Assert.Throws<ArgumentException>(() => wrongPartition.Prepare(RunControlFixture.Start));
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task Guard_rejections_and_fences_are_preserved_and_invalid_inputs_never_reach_authorization()
    {
        var context = OperationContext.Create();
        var repository = Repository();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Enabled = false }));
        var authorizations = 0;
        var transitions = new EntityTransitions(repository, Authority,
            (_, _) => { authorizations++; return ValueTask.FromResult(true); }, RunControlFixture.Contracts());
        var rejection = await transitions.ExecuteAsync(RunControlFixture.Start, "run/1", new StartRun(0), new("rejected/1"), context);
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, rejection.Receipt!.Commit.DecisionKind);
        Assert.Empty(rejection.Result.Emissions);
        var changed = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var stale = await transitions.ExecuteAsync(RunControlFixture.Start, "run/1", new StartRun(0), new("stale/1"), context, before.ConcurrencyToken);
        Assert.Equal(ProcessTransitionOperationAdapterDiagnosticCodes.SubjectChanged, stale.Result.Failure!.Code);
        Assert.Equal(changed, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        var count = authorizations;
        await Assert.ThrowsAsync<ArgumentException>(() => transitions.ExecuteAsync(RunControlFixture.Start, "run/1", null!, new("null/1"), context).AsTask());
        Assert.Equal(count, authorizations);
    }

    static Transition<RunControl, MutableCommand, string> Revision(string revision) =>
        TransitionAuthoring.Create<RunControl, MutableCommand, string>(RunControlFixture.Entity.Shape,
            new("typed/update"), new(revision), author => author
                .Set(new("attempt"), state => state.Attempt, (state, command) => command.Attempt)
                .Return("updated"));

    public sealed class MutableCommand { public long Attempt { get; set; } }

    sealed class ObservedRepository(InMemoryEntityOutboxRepository inner) : IEntityTransitionOperationRepository
    {
        int definitionReads;
        public int DefinitionReads => definitionReads;
        public int Reads { get; private set; }
        public bool AtomicReceipts { get; set; } = true;
        public EntityDefinition EntityDefinition { get { Interlocked.Increment(ref definitionReads); return inner.EntityDefinition; } }
        public string EntityType => inner.EntityType;
        public string? IdentityField => inner.IdentityField;
        public EntityTransitionOperationCapabilities TransitionOperationCapabilities => new(AtomicReceipts, "tenant/a");
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null)
        { Reads++; return inner.TryGet(context, id, options); }
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => inner.Upsert(context, write);
        public Task<EntityTransitionOperationResult> TryGetTransitionOperation(OperationContext context, EntityTransitionOperationRequest request)
        { Reads++; return inner.TryGetTransitionOperation(context, request); }
        public Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(OperationContext context, EntityTransitionOperationRequest request)
        { Reads++; return inner.TryGetCreationTransitionOperation(context, request); }
        public Task<EntityTransitionOperationResult> CommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit) => inner.CommitTransitionOperation(context, commit);
    }
}
