using System.Text.Json;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DirectTransitionExecutorTests
{
    static readonly InteractionAuthorityScope Authority = new("direct-tests", "tenant/a");
    static readonly InteractionEntityReference Subject = new(new(RunControlFixture.Entity.Shape.Id.Value), new("run/1"));
    static readonly CompiledTransitionPlan Plan = RunControlFixture.Start.Compile().Plan!;
    static PortableValue Input(long attempt = 0) => PortableValue.Concrete(Plan.Definition.Input,
        ObservationValue.FromObject(new StartRun(attempt)));

    [Fact]
    public async Task Applied_receipt_replays_exactly_after_later_write_with_direct_emission_provenance()
    {
        var context = OperationContext.Create();
        var repository = Repository();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var executor = Executor(repository);
        var operation = new DirectTransitionOperation(new("start/1"));
        var first = await executor.ExecuteAsync(context, operation, Subject, Input(), before.ConcurrencyToken);
        Assert.Equal(EntityTransitionOperationDisposition.Committed, first.Operation!.Disposition);
        var receipt = Assert.IsType<EntityTransitionOperationReceipt>(first.Receipt);
        Assert.Equal(TransitionDecisionKind.Applied, receipt.Commit.DecisionKind);
        Assert.Null(receipt.Request.Operation.Continuation);
        Assert.Equal(operation.Activation, receipt.Commit.Evidence.Activation);
        Assert.Equal(EntityTransitionEmissionPublicationAuthority.ReceiptHandoff, receipt.PublicationAuthority);
        var emission = Assert.Single(receipt.Result.Emissions);
        var origin = Assert.IsType<TransitionInteractionOrigin>(emission.Context.Origin);
        Assert.Equal(Plan.DefinitionReference, origin.Definition);
        Assert.Equal(Subject, origin.Entity);
        Assert.Equal(Authority, emission.Context.AuthorityScope);
        Assert.Equal(receipt.Request.Reference, EntityTransitionReceiptReferences.Read(first.Result.ReceiptReference!));

        var later = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Status = "completed" }, version: 9));
        var replay = await executor.ExecuteAsync(context, operation, Subject, Input(), before.ConcurrencyToken);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Operation!.Disposition);
        Assert.Same(receipt, replay.Receipt);
        Assert.Equal(later, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        Assert.Equal(EntityTransitionOperationDisposition.IdentityConflict,
            (await executor.ExecuteAsync(context, operation, Subject, Input(1))).Operation!.Disposition);

        var json = EntityStorageJson.CreateOptions();
        var restored = JsonSerializer.Deserialize<EntityTransitionOperationReceipt>(JsonSerializer.Serialize(receipt, json), json)!;
        Assert.Equal(receipt.Commit.Fingerprint, restored.Commit.Fingerprint);
        Assert.Equal(receipt.Request.Reference, restored.Request.Reference);
        Assert.Equal(JsonSerializer.Serialize(emission, json), JsonSerializer.Serialize(Assert.Single(restored.Result.Emissions), json));
    }

    [Fact]
    public async Task Rejected_admission_is_retained_and_does_not_become_allowed_on_retry()
    {
        var context = OperationContext.Create();
        var repository = Repository();
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Enabled = false }));
        var executor = Executor(repository);
        var operation = new DirectTransitionOperation(new("rejected/1"));
        var first = await executor.ExecuteAsync(context, operation, Subject, Input());
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, first.Receipt!.Commit.DecisionKind);
        Assert.Empty(first.Result.Emissions);
        Assert.Equal("scheduled", first.Receipt.Entity.Entity.Observation.Fields[nameof(RunControl.Status)].GetString());
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var replay = await executor.ExecuteAsync(context, operation, Subject, Input());
        Assert.Same(first.Receipt, replay.Receipt);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Operation!.Disposition);
    }

    [Fact]
    public async Task Concurrent_change_at_commit_returns_conflict_without_receipt_or_emissions()
    {
        var context = OperationContext.Create();
        InMemoryEntityOutboxRepository? repository = null;
        EntitySnapshot? winner = null;
        repository = new(RunControlFixture.Entity, EntityPartitionKeyPolicy.Fixed("tenant/a"), phase =>
        {
            if (phase == EntityTransitionOperationCommitPhase.BeforeAtomicCommit && winner is null)
                winner = repository!.Upsert(context,
                    RunControlFixture.Write(RunControlFixture.Initial() with { Status = "cancelled" }, version: 3)).GetAwaiter().GetResult();
        });
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var result = await Executor(repository).ExecuteAsync(context, new(new("conflict/1")), Subject, Input());
        Assert.Equal(EntityTransitionOperationDisposition.ConcurrencyConflict, result.Operation!.Disposition);
        Assert.Null(result.Receipt);
        Assert.Empty(result.Result.Emissions);
        Assert.Equal(winner, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
    }

    [Fact]
    public async Task Lost_acknowledgement_replays_retained_decision_and_emission_without_a_second_commit()
    {
        var context = OperationContext.Create();
        var commits = 0;
        var repository = new InMemoryEntityOutboxRepository(RunControlFixture.Entity, EntityPartitionKeyPolicy.Fixed("tenant/a"), phase =>
        {
            if (phase == EntityTransitionOperationCommitPhase.AfterAtomicCommitBeforeReturn)
            {
                commits++;
                throw new IOException("Lost acknowledgement");
            }
        });
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var executor = Executor(repository);
        var operation = new DirectTransitionOperation(new("lost/1"));
        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(context, operation, Subject, Input()).AsTask());
        var replay = await executor.ExecuteAsync(context, operation, Subject, Input());
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Operation!.Disposition);
        Assert.Single(replay.Result.Emissions);
        Assert.Equal(1, commits);
    }

    [Fact]
    public async Task Authorization_is_fresh_on_replay_and_denial_cannot_mutate_or_disclose_receipts()
    {
        var context = OperationContext.Create();
        var repository = Repository();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var allowed = false;
        var calls = 0;
        var executor = new DirectTransitionExecutor(Plan, repository, RunControlFixture.Contracts(), Authority,
            (_, request) => { calls++; Assert.Equal(Authority, request.AuthorityScope); return ValueTask.FromResult(allowed); });
        var operation = new DirectTransitionOperation(new("authorization/1"));
        var denied = await executor.ExecuteAsync(context, operation, Subject, Input());
        Assert.False(denied.Result.IsSuccessful);
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.AuthorizationDenied, denied.Result.Failure!.Code);
        Assert.Null(denied.Receipt);
        Assert.Equal(before, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        allowed = true;
        Assert.NotNull((await executor.ExecuteAsync(context, operation, Subject, Input())).Receipt);
        allowed = false;
        Assert.Null((await executor.ExecuteAsync(context, operation, Subject, Input())).Receipt);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task No_change_and_domain_rejection_are_durable_decisions()
    {
        var context = OperationContext.Create();
        var repository = Repository();
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        foreach (var disposition in new[] { TransitionOutcomeDisposition.NoChange, TransitionOutcomeDisposition.DomainRejected })
        {
            var transition = TransitionAuthoring.Create<RunControl, StartRun, string>(RunControlFixture.Entity.Shape,
                new("direct/" + disposition), new("1"), author => author.Return(new("outcome"), disposition, "unchanged"));
            var executor = new DirectTransitionExecutor(transition.Compile().Plan!, repository, RunControlFixture.Contracts(), Authority,
                static (_, _) => ValueTask.FromResult(true));
            var operation = new DirectTransitionOperation(new(disposition.ToString()));
            var first = await executor.ExecuteAsync(context, operation, Subject, Input());
            Assert.Equal(disposition.ToString(), first.Receipt!.Commit.DecisionKind.ToString());
            Assert.Same(first.Receipt, (await executor.ExecuteAsync(context, operation, Subject, Input())).Receipt);
        }
    }

    [Fact]
    public async Task Stale_read_fence_does_not_commit_a_new_operation()
    {
        var context = OperationContext.Create();
        var repository = Repository();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var newer = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial(), version: 2));
        var result = await Executor(repository).ExecuteAsync(context, new(new("stale/1")), Subject, Input(), before.ConcurrencyToken);
        Assert.Null(result.Receipt);
        Assert.Equal(ProcessTransitionOperationAdapterDiagnosticCodes.SubjectChanged, result.Result.Failure!.Code);
        Assert.Equal(newer, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
    }

    [Fact]
    public void Process_identity_retains_exact_storage_key_bytes()
    {
        var process = new ProcessOperationOccurrence(new(new("instance"), new("attempt")), new("activation"), new("token"), new("node"), 3);
        EntityTransitionOperationIdentity identity = process;
        var options = EntityStorageJson.CreateOptions();
        Assert.Equal(StrictDocumentJson.GetCanonicalBytes(process, options), StrictDocumentJson.GetCanonicalBytes(identity, options));
        Assert.Equal(identity, JsonSerializer.Deserialize<EntityTransitionOperationIdentity>(JsonSerializer.Serialize(process, options), options));
    }

    [Fact]
    public void Partial_process_identity_and_direct_creation_are_rejected_at_setup()
    {
        Assert.Throws<ArgumentException>(() => new EntityTransitionOperationIdentity(new("activation"), token: new("token")));
        var creation = TransitionAuthoring.Create<RunControl, RunControl, string>(RunControlFixture.Entity.Shape,
            new("direct/create"), new("1"), author => author.CreatesFrom(new("initial"), input => new RunControl(input.Id, input.Tenant, input.Status, input.Attempt, input.Enabled, input.Limit, input.ScheduledAt, input.InputDigest)).Return("created"));
        Assert.Throws<InvalidOperationException>(() => new DirectTransitionExecutor(creation.Compile().Plan!, Repository(),
            RunControlFixture.Contracts(), Authority, static (_, _) => ValueTask.FromResult(true)));
    }

    static InMemoryEntityOutboxRepository Repository() => new(RunControlFixture.Entity, EntityPartitionKeyPolicy.Fixed("tenant/a"));
    static DirectTransitionExecutor Executor(IEntityRepository repository) => new(Plan, repository,
        RunControlFixture.Contracts(), Authority, static (_, _) => ValueTask.FromResult(true));
}
