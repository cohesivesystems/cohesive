using System.Collections.Immutable;
using System.Diagnostics;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Relations.Model;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;
using Microsoft.Data.Sqlite;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteEntityOutboxRepositoryTests
{
    static readonly OperationContext Context = OperationContext.Create();
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract StateContract = new(new ObjectTypeRef(
        [new("id", StringContract.Type!), new("tenant", StringContract.Type!), new("status", StringContract.Type!)]));

    [Fact]
    public async Task KilledWriterRollsBackStateAndReceiptPagesBeforeReopenAndRetry()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var write = Write(repository);
        var original = await repository.UpsertWithOutbox(Context, new(write, [Envelope("durable", write.Entity)]));
        // Force dirty pages into WAL before killing the process. Receipt payload need not decode: it must never commit.
        var sql = $"UPDATE {Quote(repository.Mapping.TableName)} SET status = 'uncommitted'; "
            + $"INSERT INTO {Quote(repository.ReceiptsTable)} (id, kind, content, hash) VALUES ('uncommitted', 1, zeroblob(8388608), 'uncommitted');";
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(SqliteCrashWorker).Assembly.Location);
        start.ArgumentList.Add("--sqlite-crash-worker");
        start.ArgumentList.Add(file.Path);
        start.ArgumentList.Add(sql);
        using var worker = System.Diagnostics.Process.Start(start)!;
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal("uncommitted", await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, worker.ExitCode);
            var reopened = Reopen(file, repository);
            Assert.Equal(original.Entity, await reopened.TryGet(Context, "customer/1"));
            Assert.Equal(original.Entity, Assert.Single(await reopened.ReadOutbox(Context)).Commit.Entity);
            Assert.Equal(1L, Count(file, repository.ReceiptsTable));
            Assert.Equal(original.Entity, (await reopened.UpsertWithOutbox(Context, new(write, [Envelope("durable", write.Entity)]))).Entity);
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(string.IsNullOrEmpty(await errors), await errors);
        }
    }

    [Fact]
    public async Task DirectRetryAfterLostAcknowledgementAndLaterMutationReturnsOriginalSnapshot()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var initial = await repository.Upsert(Context, Write(repository));
        var write = Write(repository, status: "approved", version: 1) with { ExpectedConcurrencyToken = initial.ConcurrencyToken };
        var commit = new EntityOutboxCommit(write, [Envelope("e/1", write.Entity), Envelope("e/2", write.Entity)]);
        EntityCommitResult? acknowledgedByStorage = null;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            acknowledgedByStorage = await repository.UpsertWithOutbox(Context, commit);
            throw new IOException("Connection lost after commit, before the caller received its acknowledgement.");
        });
        var advanced = await repository.Upsert(Context, Write(repository, status: "later", version: 2));
        var reopened = Reopen(file, repository);
        var replay = await reopened.UpsertWithOutbox(Context, commit);
        Assert.Equal(acknowledgedByStorage!.Entity, replay.Entity);
        Assert.Equal("approved", replay.Entity.Entity.Observation.GetField("status").GetRequiredString());
        Assert.Equal(advanced, await reopened.TryGet(Context, "customer/1"));
        var entry = Assert.Single(await reopened.ReadOutbox(Context));
        Assert.Equal(replay.Entity, entry.Commit.Entity);
        Assert.Equal(new[] { "e/1", "e/2" }, entry.Commit.Envelopes.Select(e => e.Context.EmissionId.Value));
        Assert.Empty(await reopened.ReadOutbox(Context, afterSequence: entry.Sequence));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("state")]
    [InlineData("partial")]
    [InlineData("order")]
    public async Task DirectRetryRejectsChangedCanonicalContentAndCommitMembership(string change)
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var write = Write(repository);
        ImmutableArray<InteractionEnvelope> envelopes = [Envelope("a", write.Entity), Envelope("b", write.Entity)];
        var committed = await repository.UpsertWithOutbox(Context, new(write, envelopes));
        var candidate = change == "state" ? Write(repository, status: "different") : write;
        var changed = change switch
        {
            "payload" => [Envelope("a", write.Entity, "changed"), envelopes[1]],
            "partial" => [envelopes[0], Envelope("unseen", write.Entity)],
            "order" => [envelopes[1], envelopes[0]],
            _ => envelopes
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpsertWithOutbox(Context, new(candidate, changed)));
        Assert.Equal(committed.Entity, await repository.TryGet(Context, "customer/1"));
        Assert.Single(await repository.ReadOutbox(Context));
    }

    [Fact]
    public async Task DirectConcurrentDuplicatesCommitOneOriginalSnapshotAndOneOutboxEntry()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var initial = await repository.Upsert(Context, Write(repository));
        var write = Write(repository, status: "approved") with { ExpectedConcurrencyToken = initial.ConcurrencyToken };
        var commit = new EntityOutboxCommit(write, [Envelope("race", write.Entity)]);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await Reopen(file, repository).UpsertWithOutbox(Context, commit);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(results[0].Entity, result.Entity));
        Assert.Single(await repository.ReadOutbox(Context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateEnvelopeOrReceiptFailureRollsBackEverythingAcrossReopen(bool process)
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var initial = await repository.Upsert(Context, Write(repository));
        var table = process ? repository.ReceiptsTable : repository.EmissionsTable;
        Execute(file, $"CREATE TRIGGER fail_late BEFORE INSERT ON {Quote(table)} BEGIN SELECT RAISE(ABORT, 'injected late failure'); END");
        var operation = Operation(repository, initial.ConcurrencyToken);
        if (process)
            await Assert.ThrowsAsync<SqliteException>(() => repository.CommitTransitionOperation(Context, operation));
        else
            await Assert.ThrowsAsync<SqliteException>(() => repository.UpsertWithOutbox(Context,
                new(operation.Write, [Envelope("late", operation.Write.Entity)])));
        var reopened = Reopen(file, repository);
        Assert.Equal(initial, await reopened.TryGet(Context, "customer/1"));
        Assert.Empty(await reopened.ReadOutbox(Context));
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, (await reopened.TryGetTransitionOperation(Context, operation.Request)).Disposition);
        Assert.Equal(0L, Count(file, repository.ReceiptsTable));
        Assert.Equal(0L, Count(file, repository.EmissionsTable));
        Execute(file, "DROP TRIGGER fail_late");
        Assert.Equal(EntityTransitionOperationDisposition.Committed, (await reopened.CommitTransitionOperation(Context, operation)).Disposition);
    }

    [Fact]
    public async Task ProcessReceiptReopensAndReplaysOriginalStateWithoutPublishingFromEntityOutbox()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var initial = await repository.Upsert(Context, Write(repository));
        var commit = Operation(repository, initial.ConcurrencyToken);
        var committed = await repository.CommitTransitionOperation(Context, commit);
        Assert.Equal(EntityTransitionOperationDisposition.Committed, committed.Disposition);
        var advanced = await repository.Upsert(Context, Write(repository, status: "later", version: 9));
        var reopened = Reopen(file, repository);
        var replay = await reopened.CommitTransitionOperation(Context, commit);
        var lookup = await reopened.TryGetTransitionOperation(Context, commit.Request);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Disposition);
        Assert.Equal(committed.Receipt!.Entity, replay.Receipt!.Entity);
        Assert.Equal(committed.Receipt.CommittedAtUtc, replay.Receipt.CommittedAtUtc);
        Assert.Equal(commit.Fingerprint, replay.Receipt.Commit.Fingerprint);
        Assert.Equal(committed.Receipt.Entity, lookup.Receipt!.Entity);
        Assert.Single(replay.Receipt.Result.Emissions);
        Assert.Equal(EntityTransitionEmissionPublicationAuthority.ProcessOutbox, replay.Receipt.PublicationAuthority);
        Assert.Equal(advanced, await reopened.TryGet(Context, "customer/1"));
        Assert.Empty(await reopened.ReadOutbox(Context));
    }

    [Fact]
    public async Task ProcessDuplicateRaceReplaysButChangedInputResultAndFenceConflict()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var initial = await repository.Upsert(Context, Write(repository));
        var commit = Operation(repository, initial.ConcurrencyToken);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await Reopen(file, repository).CommitTransitionOperation(Context, commit);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r.Disposition == EntityTransitionOperationDisposition.Committed);
        Assert.Equal(3, results.Count(r => r.Disposition == EntityTransitionOperationDisposition.Replayed));
        Assert.All(results, r => Assert.Equal(results[0].Receipt!.Entity, r.Receipt!.Entity));
        var changedInput = new EntityTransitionOperationRequest(commit.Request.Operation, commit.Request.AuthorityScope,
            commit.Request.Transition, commit.Request.Subject, StringValue("different-input"));
        Assert.Equal(EntityTransitionOperationDisposition.IdentityConflict, (await repository.TryGetTransitionOperation(Context, changedInput)).Disposition);
        var changedResult = new EntityTransitionOperationCommit(commit.Request, commit.Write, commit.DecisionKind,
            ProcessOperationResult.Completed(StringValue("different-result"), commit.Result.Emissions), commit.GuaranteeDemands, commit.Evidence);
        Assert.Equal(EntityTransitionOperationDisposition.IdentityConflict, (await repository.CommitTransitionOperation(Context, changedResult)).Disposition);
        var stale = await repository.CommitTransitionOperation(Context, Operation(repository, initial.ConcurrencyToken, occurrence: 1));
        Assert.Equal(EntityTransitionOperationDisposition.ConcurrencyConflict, stale.Disposition);
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict, Assert.Single(stale.Diagnostics).Code);
        Assert.Equal(1L, Count(file, repository.ReceiptsTable));
    }

    [Fact]
    public async Task CreationIsUniqueAcrossPartitionsAndReplacementAttemptsRetainOriginalReceipt()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var commit = Operation(repository, token: null, creation: true);
        var committed = await repository.CommitTransitionOperation(Context, commit);
        Assert.Equal(EntityTransitionOperationDisposition.Committed, committed.Disposition);
        var replacement = Operation(repository, token: null, creation: true, occurrence: 1);
        var reopened = Reopen(file, repository);
        Assert.Equal(committed.Receipt!.Entity, (await reopened.TryGetCreationTransitionOperation(Context, replacement.Request)).Receipt!.Entity);
        var replay = await reopened.CommitTransitionOperation(Context, replacement);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Disposition);
        Assert.Equal(commit.Request.Operation, replay.Receipt!.Request.Operation);
        Assert.Empty(await reopened.ReadOutbox(Context));
        Assert.Equal(1L, Count(file, repository.CreationsTable));

        using var ordinaryFile = new DatabaseFixture();
        var ordinary = Repository(ordinaryFile);
        await ordinary.Upsert(Context, Write(ordinary, tenant: "another-partition"));
        Assert.Equal(EntityTransitionOperationDisposition.SubjectStateConflict,
            (await ordinary.CommitTransitionOperation(Context, Operation(ordinary, token: null, creation: true))).Disposition);
        Assert.Equal(0L, Count(ordinaryFile, ordinary.ReceiptsTable));
    }

    [Fact]
    public async Task ReceiptLimitAndCorruptionFailExplicitlyWithoutChangingState()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var initial = await repository.Upsert(Context, Write(repository));
        var commit = Operation(repository, initial.ConcurrencyToken);
        var small = new SqliteEntityOutboxRepository(file.Database, repository.Mapping, maximumReceiptBytes: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => small.CommitTransitionOperation(Context, commit));
        Assert.Equal(initial, await repository.TryGet(Context, "customer/1"));
        Assert.Equal(0L, Count(file, repository.ReceiptsTable));
        await repository.CommitTransitionOperation(Context, commit);
        Execute(file, $"UPDATE {Quote(repository.ReceiptsTable)} SET hash = 'corrupted'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reopen(file, repository).TryGetTransitionOperation(Context, commit.Request));
    }

    [Fact]
    public async Task LateCreationIndexFailureAndPrecommitCancellationLeaveNoSubjectOrReceipt()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var commit = Operation(repository, token: null, creation: true);
        Execute(file, $"CREATE TRIGGER fail_creation BEFORE INSERT ON {Quote(repository.CreationsTable)} BEGIN SELECT RAISE(ABORT, 'late creation failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => repository.CommitTransitionOperation(Context, commit));
        Assert.Null(await Reopen(file, repository).TryGet(Context, "customer/1"));
        Assert.Equal(0L, Count(file, repository.ReceiptsTable));
        Execute(file, "DROP TRIGGER fail_creation");
        using var cancellation = new CancellationTokenSource();
        var clock = new CancellingClock(cancellation);
        var context = OperationContext.Create(clock, cancellationToken: cancellation.Token);
        clock.Armed = true; // Receipt time is observed after the entity write, inside the transaction.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CommitTransitionOperation(context, commit));
        Assert.Null(await Reopen(file, repository).TryGet(Context, "customer/1"));
        Assert.Equal(0L, Count(file, repository.ReceiptsTable));
        Assert.Equal(0L, Count(file, repository.CreationsTable));
        Assert.Equal(EntityTransitionOperationDisposition.Committed, (await repository.CommitTransitionOperation(Context, commit)).Disposition);
    }

    [Fact]
    public async Task ReadByteBudgetReturnsWholeCommitsAndRejectsAnOversizedFirstReceipt()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var write = Write(repository);
        for (var i = 0; i < 2; i++) await repository.UpsertWithOutbox(Context, new(write, [Envelope($"byte/{i}", write.Entity)]));
        long size;
        using (var connection = file.Database.OpenConnection())
        using (var command = file.Database.CreateCommand(connection, null, $"SELECT MAX(length(content)) FROM {Quote(repository.ReceiptsTable)}"))
            size = (long)command.ExecuteScalar()!;
        var bounded = new SqliteEntityOutboxRepository(file.Database, repository.Mapping, maximumReceiptBytes: checked((int)size));
        var first = Assert.Single(await bounded.ReadOutbox(Context));
        Assert.Single(await bounded.ReadOutbox(Context, first.Sequence));
        var tooSmall = new SqliteEntityOutboxRepository(file.Database, repository.Mapping, maximumReceiptBytes: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tooSmall.ReadOutbox(Context));
    }

    [Fact]
    public async Task CursorPagesExcludeProcessReceiptsAndEmptyEnvelopeWritesHaveNoReplayIdentity()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var write = Write(repository);
        var first = await repository.UpsertWithOutbox(Context, new(write, []));
        var second = await repository.UpsertWithOutbox(Context, new(write, []));
        Assert.NotEqual(first.Entity.ConcurrencyToken, second.Entity.ConcurrencyToken);
        Assert.Empty(await repository.ReadOutbox(Context));
        await repository.CommitTransitionOperation(Context, Operation(repository, second.Entity.ConcurrencyToken));
        for (var i = 0; i < 3; i++) await repository.UpsertWithOutbox(Context, new(write, [Envelope($"page/{i}", write.Entity)]));
        var page = await repository.ReadOutbox(Context, maximumCommits: 2);
        Assert.Equal(2, page.Length);
        var final = Assert.Single(await repository.ReadOutbox(Context, afterSequence: page[^1].Sequence));
        Assert.Equal("page/2", Assert.Single(final.Commit.Envelopes).Context.EmissionId.Value);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.UpsertWithOutbox(OperationContext.Create(cancellationToken: cancelled.Token), new(write, [Envelope("cancel", write.Entity)])));
        Assert.Equal(4L, Count(file, repository.ReceiptsTable));
    }

    static SqliteEntityOutboxRepository Repository(DatabaseFixture file)
    {
        var definition = new EntityDefinition(new("customer"),
            [new(new("id"), StringContract.Type!), new(new("tenant"), StringContract.Type!), new(new("status"), StringContract.Type!)]);
        var mapping = new SqliteEntityRepositoryMapping(definition, identityField: "id", partitionField: "tenant", tableName: "customer\"records");
        var repository = new SqliteEntityOutboxRepository(file.Database, mapping);
        new SqliteSchema("customer-state", [mapping.InitialMigration]).Apply(file.Database);
        new SqliteSchema("customer-outbox", [repository.InitialMigration]).Apply(file.Database);
        return repository;
    }

    static SqliteEntityOutboxRepository Reopen(DatabaseFixture file, SqliteEntityOutboxRepository repository) => new(new(new(file.Path)), repository.Mapping);
    static EntityWriteRequest Write(SqliteEntityOutboxRepository repository, string status = "pending", long version = 0, string tenant = "tenant/a") =>
        new(repository.EntityDefinition.CreateState("customer/1", new Dictionary<string, ObservationValue>
        {
            ["id"] = ObservationValue.FromString("customer/1"), ["tenant"] = ObservationValue.FromString(tenant), ["status"] = ObservationValue.FromString(status)
        }, version).Snapshot);

    static EntityTransitionOperationCommit Operation(SqliteEntityOutboxRepository repository, EntityConcurrencyToken? token, bool creation = false, int occurrence = 0)
    {
        var operation = new ProcessOperationOccurrence(new(new("process/customer"), new($"attempt/{occurrence}")),
            new($"activation/{occurrence}"), new("token/approve"), new("invoke/approve"), occurrence);
        var state = Write(repository).Entity;
        var input = PortableValue.Concrete(StateContract, ObservationValue.FromObject(state.Observation.Fields));
        var definition = new CanonicalTransitionDefinition(StateContract, StateContract, StringContract, [],
            new SequenceTransitionNode(new("body"),
            [
                new UpdateTransitionNode(new("update"), FieldPath.FromField("status"), new SetTransitionPatch(Expr.Const("approved"))),
                new EmitTransitionNode(new("emit"), Definition("event/approved"), Expr.Const("customer/1")),
                new OutcomeTransitionNode(new("outcome"), TransitionOutcomeDisposition.Applied, Expr.Const("accepted"))
            ]), subjectCreation: creation ? new(new("initialize"), Expr.BoundValue(TransitionBindingIds.Input)) : null);
        var document = TransitionDefinitionDocuments.Create(new("transition/approve"), new("revision/1"), definition, Provenance());
        var compilation = TransitionStaticCompiler.Compile(document);
        Assert.True(compilation.IsSuccessful, string.Join("\n", compilation.Validation.Diagnostics.Select(d => d.Message)));
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        var decision = creation ? TransitionReferenceInterpreter.DecideCreation(plan, operation.Activation, input)
            : TransitionReferenceInterpreter.DecideFullState(plan, operation.Activation, input, input);
        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        var request = new EntityTransitionOperationRequest(operation, new("authority/tests", "tenant/a"), decision.Evidence.Definition,
            new(new(repository.EntityType), state.EntityId), input);
        var candidate = Write(repository, status: "approved", version: 1).Entity;
        var envelope = Envelope($"process-emission/{occurrence}", candidate, origin: new ProcessInteractionOrigin(Definition("process/customer"),
            operation.Node, operation.Continuation, operation.Activation, operation.Token, request.Subject, request.Transition, new("outcome"), new("emit")));
        return new(request, new(candidate, token), decision.Kind, ProcessOperationResult.Completed(StringValue("accepted"), [envelope]),
            decision.GuaranteeDemands, decision.Evidence, creation ? EntityTransitionSubjectCondition.MustBeAbsent : EntityTransitionSubjectCondition.MustExist);
    }

    static DomainEventEnvelope Envelope(string id, EntityObservationSnapshot entity, string payload = "customer/1", InteractionOrigin? origin = null) => new(
        InteractionEnvelope.CurrentSchemaVersion,
        new(new(id), origin ?? new TransitionInteractionOrigin(Definition("transition/approve"), new("emit"),
                new(new(entity.Observation.ShapeId.ShapeId.Value), entity.EntityId), new("outcome")),
            new("correlation/customer"), causationId: null, new("authority/tests", "tenant/a"), new($"idempotency/{id}"), ordering: null,
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), Provenance()),
        new(Definition("event/approved")), StringValue(payload));
    static PortableValue StringValue(string value) => PortableValue.Concrete(StringContract, ObservationValue.FromString(value));
    static ExecutionDefinitionReference Definition(string id) => new(new(id), new("revision/1"),
        new(ExecutionDefinitionFingerprinter.Algorithm, ExecutionDefinitionFingerprinter.Canonicalization, new string('a', 64)));
    static ExecutionProvenance Provenance() => new(new("sqlite-outbox-tests", "1"), new("tests/sqlite/outbox"), DocumentOrigin.Generated);
    static string Quote(string name) => SqliteDatabase.QuoteIdentifier(name);
    static void Execute(DatabaseFixture file, string sql)
    {
        using var connection = file.Database.OpenConnection();
        using var command = file.Database.CreateCommand(connection, null, sql);
        command.ExecuteNonQuery();
    }
    static long Count(DatabaseFixture file, string table)
    {
        using var connection = file.Database.OpenConnection();
        using var command = file.Database.CreateCommand(connection, null, $"SELECT COUNT(*) FROM {Quote(table)}");
        return (long)command.ExecuteScalar()!;
    }

    sealed class CancellingClock(CancellationTokenSource cancellation) : TimeProvider
    {
        internal bool Armed { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            if (Armed) cancellation.Cancel();
            return DateTimeOffset.UtcNow;
        }
    }
}
