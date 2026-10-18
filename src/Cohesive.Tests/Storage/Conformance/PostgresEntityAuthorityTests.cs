using Cohesive.Adapters.Marten;
using Marten;
using JasperFx.Events;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Execution;

namespace Cohesive.Tests.Storage.Conformance;

public sealed partial class PostgresRepositoryConformanceTests
{
    [PostgresTheory]
    [InlineData(EntityStateAuthority.State, EntityReconstitutionMode.FullReplay)]
    [InlineData(EntityStateAuthority.Events, EntityReconstitutionMode.FullReplay)]
    [InlineData(EntityStateAuthority.Events, EntityReconstitutionMode.CheckpointAndTail)]
    public Task Same_transition_commits_and_replays_under_each_authority(EntityStateAuthority authority, EntityReconstitutionMode mode)
        => WithRepository(async (repository, database, schema) =>
    {
        var context = OperationContext.Create();
        var before = await repository.Create(context, RunControlFixture.Write(RunControlFixture.Initial()).Entity, EntityCreationPolicy.IfAbsent);
        var evidence = RunControlFixture.Prepare(before);
        var commit = RunControlFixture.Commit(evidence, evidence.Decision, RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        var first = await repository.CommitTransitionOperation(context, commit);
        Assert.Equal(EntityTransitionOperationDisposition.Committed, first.Disposition);
        var after = (await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")))!;
        Assert.Equal("running", after.Entity.Observation.GetField("Status").GetRequiredString());
        Assert.Equal(1, after.Entity.Version);
        await repository.CommitTransitionOperation(context, commit);
        await using (var count = database.CreateCommand($"SELECT count(*) FROM {schema}.mt_events"))
            Assert.Equal(2L, await count.ExecuteScalarAsync());
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Upsert(context,
            RunControlFixture.Write(RunControlFixture.Initial() with { Status = "stale" }, 1, before.ConcurrencyToken)));
        // A mapped row is expendable only for event authority. Read-side rebuilding is explicit, not a hidden write.
        await using (var delete = database.CreateCommand($"DELETE FROM {schema}.controls")) await delete.ExecuteNonQueryAsync();
        var reconstructed = await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a"));
        if (authority == EntityStateAuthority.State) Assert.Null(reconstructed);
        else
        {
            Assert.Equal(after, reconstructed);
            Assert.Equal(after, await repository.RebuildStateProjection(context, "run/1", "tenant/a"));
            await using (var count = database.CreateCommand($"SELECT count(*) FROM {schema}.mt_events"))
                Assert.Equal(2L, await count.ExecuteScalarAsync());
            await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Create(context,
                before.Entity, EntityCreationPolicy.IfAbsent));
            var next = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Status = "completed" }, 2, after.ConcurrencyToken));
            Assert.Equal(2, next.Entity.Version);
            Assert.Equal(next, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        }
    }, authority: authority, reconstitution: mode);

    [PostgresTheory]
    [InlineData(EntityStateAuthority.State)]
    [InlineData(EntityStateAuthority.Events)]
    public Task Receipt_failure_rolls_back_state_action_and_row(EntityStateAuthority authority) => WithRepository(async (repository, database, schema) =>
    {
        var context = OperationContext.Create();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        var commit = RunControlFixture.Commit(evidence, evidence.Decision, RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CommitTransitionOperation(context, commit));
        Assert.Equal(before, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        await using var count = database.CreateCommand($"SELECT count(*) FROM {schema}.mt_events");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }, maximumReceiptBytes: 1, authority: authority);

    [PostgresTheory]
    [InlineData(EntityReconstitutionMode.FullReplay)]
    [InlineData(EntityReconstitutionMode.CheckpointAndTail)]
    public Task Corrupt_or_missing_action_is_not_silently_treated_as_absence(EntityReconstitutionMode mode) => WithRepository(async (repository, database, schema) =>
    {
        var context = OperationContext.Create();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Status = "running" }, 1, before.ConcurrencyToken));
        await using (var corrupt = database.CreateCommand($"UPDATE {schema}.mt_events SET data=jsonb_set(data, '{{ContentHash}}', '\"corrupt\"') WHERE version=2")) await corrupt.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        await using (var missing = database.CreateCommand($"DELETE FROM {schema}.mt_events WHERE version=2")) await missing.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
    }, authority: EntityStateAuthority.Events, reconstitution: mode);

    [PostgresFact]
    public Task Concurrent_stream_writers_have_one_winner() => WithRepository(async (repository, database, schema) =>
    {
        var context = OperationContext.Create();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        async Task<bool> Attempt()
        {
            try
            {
                await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Status = "running" }, 1, before.ConcurrencyToken));
                return true;
            }
            catch (ObservationConcurrencyConflictException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Attempt())), x => x);
        await using var count = database.CreateCommand($"SELECT count(*) FROM {schema}.mt_events");
        Assert.Equal(2L, await count.ExecuteScalarAsync());
    }, authority: EntityStateAuthority.Events);

    [PostgresTheory]
    [InlineData(EntityStateAuthority.State)]
    [InlineData(EntityStateAuthority.Events)]
    public Task Direct_outbox_is_atomic_and_replays_without_adding_state_actions(EntityStateAuthority authority) => WithRepository(async (repository, database, schema) =>
    {
        var options = new PostgresEntityOutboxOptions(schema, "outbox");
        await using (var create = database.CreateCommand(options.SchemaSql)) await create.ExecuteNonQueryAsync();
        var outbox = new PostgresEntityOutboxRepository(repository, await options.BindAsync(database));
        var context = OperationContext.Create();
        var before = await outbox.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        var candidate = TransitionStateProjector.ApplyToEntity(repository.EntityDefinition, "run/1", evidence.Decision, before.Entity).Snapshot;
        var envelopes = RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts(), direct: true);
        var commit = new EntityOutboxCommit(new(candidate, before.ConcurrencyToken), envelopes);
        var result = await outbox.UpsertWithOutbox(context, commit);
        Assert.Equal(result.Entity, (await outbox.UpsertWithOutbox(context, commit)).Entity);
        Assert.Single(await outbox.ReadOutbox(context));
        await using var count = database.CreateCommand($"SELECT count(*) FROM {schema}.mt_events");
        Assert.Equal(2L, await count.ExecuteScalarAsync());
        var changed = new EntityOutboxCommit(new(before.Entity), envelopes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.UpsertWithOutbox(context, changed));
        Assert.Equal(result.Entity, await outbox.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
    }, authority: authority);

    [PostgresFact]
    public Task Authority_cannot_be_changed_without_migration() => WithRepository(async (repository, database, schema) =>
    {
        var context = OperationContext.Create();
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        using var store = HistoryStore(database, schema);
        var history = await MartenEntityHistoryStorage.BindAsync(database, store, EntityStateAuthority.State);
        var changed = new PostgresEntityRepository(RunControlFixture.Entity, new(new("test"), database, "test"), Mapping(schema), stateHistory: history);
        await Assert.ThrowsAsync<InvalidOperationException>(() => changed.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial())));
    }, authority: EntityStateAuthority.Events);
    [PostgresFact]
    public Task Appending_does_not_replay_the_whole_history() => WithRepository(async (repository, database, schema) =>
    {
        var context = OperationContext.Create();
        var current = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        for (var version = 1; version <= 20; version++)
            current = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial(), version, current.ConcurrencyToken));
        // A full read exceeds one action, but all twenty appends succeeded with the same one-action acquisition bound.
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        await using var count = database.CreateCommand($"SELECT count(*) FROM {schema}.mt_events");
        Assert.Equal(21L, await count.ExecuteScalarAsync());
    }, authority: EntityStateAuthority.Events, reconstitution: EntityReconstitutionMode.FullReplay, maximumReplayActions: 1);

    [PostgresTheory]
    [InlineData(EntityStateAuthority.State)]
    [InlineData(EntityStateAuthority.Events)]
    public Task Outbox_failure_rolls_back_the_history_and_projection(EntityStateAuthority authority) => WithRepository(async (repository, database, schema) =>
    {
        var options = new PostgresEntityOutboxOptions(schema, "outbox", maximumCommitBytes: 1);
        await using (var create = database.CreateCommand(options.SchemaSql)) await create.ExecuteNonQueryAsync();
        var outbox = new PostgresEntityOutboxRepository(repository, await options.BindAsync(database));
        var context = OperationContext.Create();
        var before = await outbox.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        var candidate = TransitionStateProjector.ApplyToEntity(repository.EntityDefinition, "run/1", evidence.Decision, before.Entity).Snapshot;
        var commit = new EntityOutboxCommit(new(candidate, before.ConcurrencyToken), RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts(), direct: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.UpsertWithOutbox(context, commit));
        Assert.Equal(before, await outbox.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        Assert.Empty(await outbox.ReadOutbox(context));
        await using var count = database.CreateCommand($"SELECT count(*) FROM {schema}.mt_events");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }, authority: authority);

    [PostgresFact]
    public Task Existing_rows_require_explicit_migration() => WithRepository(async (repository, database, schema) =>
    {
        var context = OperationContext.Create();
        await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        using var store = HistoryStore(database, schema);
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        var history = await MartenEntityHistoryStorage.BindAsync(database, store, EntityStateAuthority.Events);
        var events = new PostgresEntityRepository(RunControlFixture.Entity, new(new("test"), database, "test"), Mapping(schema), stateHistory: history);
        await Assert.ThrowsAsync<InvalidOperationException>(() => events.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => events.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial())));
    });

    [PostgresFact]
    public Task Marten_binding_rejects_a_different_data_source_before_schema_access() => WithRepository(async (_, database, schema) =>
    {
        using var store = HistoryStore(database, schema);
        await using var other = Npgsql.NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => MartenEntityHistoryStorage.BindAsync(other, store, EntityStateAuthority.Events));
        Assert.Equal("store", error.ParamName);
    });

    [PostgresFact]
    public Task Missing_Marten_schema_fails_at_binding_without_implicit_migration() => WithRepository(async (_, database, schema) =>
    {
        using var store = HistoryStore(database, schema);
        await Assert.ThrowsAsync<Weasel.Core.Migrations.DatabaseValidationException>(() => MartenEntityHistoryStorage.BindAsync(database, store, EntityStateAuthority.Events));
        await using var exists = database.CreateCommand("SELECT to_regclass($1) IS NULL");
        exists.Parameters.AddWithValue(schema + ".mt_events");
        Assert.Equal(true, await exists.ExecuteScalarAsync());
    });

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Deferred_commit_failure_rolls_back_Marten_relational_state_and_retained_evidence(bool processReceipt)
        => WithRepository(async (repository, database, schema) =>
    {
        var context = OperationContext.Create();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        var outboxOptions = new PostgresEntityOutboxOptions(schema, "outbox");
        await using (var create = database.CreateCommand(outboxOptions.SchemaSql)) await create.ExecuteNonQueryAsync();
        var outbox = new PostgresEntityOutboxRepository(repository, await outboxOptions.BindAsync(database));
        var table = processReceipt ? "receipts" : "outbox_commits";
        // A deferred trigger fails COMMIT after every participant has executed its writes.
        await using (var fault = database.CreateCommand($"""
            CREATE FUNCTION {schema}.reject_commit() RETURNS trigger LANGUAGE plpgsql AS
            'BEGIN RAISE EXCEPTION ''injected commit failure''; END';
            CREATE CONSTRAINT TRIGGER fail_commit AFTER INSERT ON {schema}.{table}
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {schema}.reject_commit();
            """)) await fault.ExecuteNonQueryAsync();
        if (processReceipt)
        {
            var commit = RunControlFixture.Commit(evidence, evidence.Decision,
                RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => repository.CommitTransitionOperation(context, commit));
        }
        else
        {
            var candidate = TransitionStateProjector.ApplyToEntity(repository.EntityDefinition, "run/1", evidence.Decision, before.Entity).Snapshot;
            var commit = new EntityOutboxCommit(new(candidate, before.ConcurrencyToken),
                RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts(), direct: true));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => outbox.UpsertWithOutbox(context, commit));
        }
        Assert.Equal(before, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        await using (var count = database.CreateCommand($"SELECT count(*) FROM {schema}.mt_events"))
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        await using (var count = database.CreateCommand($"SELECT count(*) FROM {schema}.{table}"))
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        Assert.Empty(await outbox.ReadOutbox(context));
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, (await repository.TryGetTransitionOperation(context, evidence.Request)).Disposition);
        await using var row = database.CreateCommand($"SELECT \"Status\" FROM {schema}.controls");
        Assert.Equal(before.Entity.Observation.GetField("Status").GetRequiredString(), await row.ExecuteScalarAsync());
    }, authority: EntityStateAuthority.Events);

    static IDocumentStore HistoryStore(Npgsql.NpgsqlDataSource database, string schema) => DocumentStore.For(options =>
    {
        options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
                options.Connection(database); options.DatabaseSchemaName = schema;
        options.Events.StreamIdentity = StreamIdentity.AsString;
        options.Events.AddEventType<EntityStateEvent>();
    });

    [PostgresFact]
    public Task Outbox_does_not_require_or_imply_event_authority() => WithRepository(async (repository, database, schema) =>
    {
        var options = new PostgresEntityOutboxOptions(schema, "outbox");
        await using (var create = database.CreateCommand(options.SchemaSql)) await create.ExecuteNonQueryAsync();
        var outbox = new PostgresEntityOutboxRepository(repository, await options.BindAsync(database));
        var context = OperationContext.Create();
        var before = await outbox.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        var candidate = TransitionStateProjector.ApplyToEntity(repository.EntityDefinition, "run/1", evidence.Decision, before.Entity).Snapshot;
        await outbox.UpsertWithOutbox(context, new(new(candidate, before.ConcurrencyToken),
            RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts(), direct: true)));
        Assert.Single(await outbox.ReadOutbox(context));
        await using var remove = database.CreateCommand($"DELETE FROM {schema}.controls");
        await remove.ExecuteNonQueryAsync();
        Assert.Null(await outbox.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        Assert.Single(await outbox.ReadOutbox(context));
    });

}
