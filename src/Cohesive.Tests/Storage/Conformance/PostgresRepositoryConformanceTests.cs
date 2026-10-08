using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Sql;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Npgsql;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Prelude;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Execution;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Execution;

namespace Cohesive.Tests.Storage.Conformance;

public sealed class PostgresRepositoryConformanceTests
{
    const string ConnectionVariable = "COHESIVE_POSTGRES_TEST_CONNECTION_STRING";

    [PostgresTheory]
    [MemberData(nameof(EntityRepositoryConformance.BasicCases), MemberType = typeof(EntityRepositoryConformance))]
    public async Task Conforms(RepositoryProbe probe)
        => await WithRepository((repository, _, _) => EntityRepositoryConformance.Verify(repository, probe));

    static async Task WithRepository(Func<PostgresEntityRepository, NpgsqlDataSource, string, Task> verify, int maximumReceiptBytes = 1_048_576)
    {
        await using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var schema = "adoption_" + Guid.NewGuid().ToString("N");
        var mapping = new PostgresEntityRepositoryMapping(new SqlQualifiedTable(schema, "controls"),
        [
            new(nameof(RunControl.Id), "Id", PostgresRelationQueryScalarType.Text),
            new(nameof(RunControl.Tenant), "Tenant", PostgresRelationQueryScalarType.Text),
            new(nameof(RunControl.Status), "Status", PostgresRelationQueryScalarType.Text),
            new(nameof(RunControl.Attempt), "Attempt", PostgresRelationQueryScalarType.Int64),
            new(nameof(RunControl.Enabled), "Enabled", PostgresRelationQueryScalarType.Boolean),
            new(nameof(RunControl.Limit), "Limit", PostgresRelationQueryScalarType.Numeric),
            new(nameof(RunControl.ScheduledAt), "ScheduledAt", PostgresRelationQueryScalarType.TimestampWithTimeZone),
            new(nameof(RunControl.InputDigest), "InputDigest", PostgresRelationQueryScalarType.Bytea)
        ], identityField: nameof(RunControl.Id), partitionField: nameof(RunControl.Tenant));
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection)) await create.ExecuteNonQueryAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"""
                CREATE TABLE {schema}.controls (
                    "Id" text NOT NULL, "Tenant" text NOT NULL, "Status" text NOT NULL,
                    "Attempt" bigint NOT NULL, "Enabled" boolean NOT NULL, "Limit" numeric NOT NULL,
                    "ScheduledAt" timestamptz NOT NULL, "InputDigest" bytea NOT NULL,
                    observation_version bigint NOT NULL, PRIMARY KEY ("Tenant", "Id"))
                """, connection)) await create.ExecuteNonQueryAsync();
            var receipts = new PostgresTransitionReceiptOptions(schema, "receipts", "tenant/a", maximumReceiptBytes);
            await using (var create = new NpgsqlCommand(receipts.SchemaSql, connection)) await create.ExecuteNonQueryAsync();
            await receipts.ValidateSchemaAsync(dataSource);
            var repository = new PostgresEntityRepository(RunControlFixture.Entity,
                new(new("adoption/postgres"), dataSource, "cohesive.conformance"), mapping, receipts);
            await verify(repository, dataSource, schema);
        }
        finally
        {
            // The random schema belongs exclusively to this test invocation.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }



    [PostgresFact]
    public Task Oversized_receipt_rolls_back_state_and_leaves_no_replay_evidence() => WithRepository(async (repository, _, _) =>
    {
        var context = OperationContext.Create();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        var commit = RunControlFixture.Commit(evidence, evidence.Decision,
            RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CommitTransitionOperation(context, commit));
        Assert.Equal(before, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        Assert.Equal(EntityTransitionOperationDisposition.NotFound,
            (await repository.TryGetTransitionOperation(context, evidence.Request)).Disposition);
    }, maximumReceiptBytes: 1);

    [PostgresFact]
    public Task Competing_create_if_absent_never_overwrites_the_winner() => WithRepository(async (repository, _, _) =>
    {
        var context = OperationContext.Create();
        var state = RunControlFixture.Write(RunControlFixture.Initial()).Entity;
        async Task<bool> Attempt()
        {
            try { await repository.Create(context, state, EntityCreationPolicy.IfAbsent); return true; }
            catch (ObservationConcurrencyConflictException) { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Attempt()));
        Assert.Single(results, result => result);
        var winner = await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a"));
        Assert.Equal(state, winner!.Entity);
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Create(context,
            RunControlFixture.Write(RunControlFixture.Initial() with { Status = "must-not-overwrite" }).Entity, EntityCreationPolicy.IfAbsent));
        Assert.Equal(winner, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
    });



    [PostgresFact]
    public Task Corrupt_receipt_is_rejected_instead_of_replayed() => WithRepository(async (repository, database, schema) =>
    {
        var context = OperationContext.Create();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        var commit = RunControlFixture.Commit(evidence, evidence.Decision,
            RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        await repository.CommitTransitionOperation(context, commit);
        await using var corrupt = database.CreateCommand($"UPDATE {schema}.receipts SET content_hash='corrupt'");
        await corrupt.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryGetTransitionOperation(context, evidence.Request));
        var stored = await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a"));
        Assert.Equal(1, stored!.Entity.Version);
    });



    [PostgresFact]
    public Task Receipt_schema_is_checked_and_column_order_is_irrelevant() => WithRepository(async (repository, database, schema) =>
    {
        await using (var reorder = database.CreateCommand($"""
            DROP TABLE {schema}.receipts;
            CREATE TABLE {schema}.receipts (
                format_version integer NOT NULL, content_hash text NOT NULL, content bytea NOT NULL,
                creation_subject text NULL, subject_id text NOT NULL, operation_id text NOT NULL,
                partition_key text NOT NULL, entity_type text NOT NULL,
                PRIMARY KEY(entity_type, partition_key, operation_id),
                UNIQUE(entity_type, partition_key, creation_subject));
            """)) await reorder.ExecuteNonQueryAsync();
        var options = new PostgresTransitionReceiptOptions(schema, "receipts", "tenant/a");
        await options.ValidateSchemaAsync(database);
        await EntityTransitionReceiptConformance.ConcurrentReplay(repository);
        await using (var change = database.CreateCommand($"ALTER TABLE {schema}.receipts ALTER COLUMN format_version TYPE bigint"))
            await change.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => options.ValidateSchemaAsync(database));
    });

    [PostgresFact]
    public Task Receipt_schema_requires_both_uniqueness_fences() => WithRepository(async (_, database, schema) =>
    {
        await using var change = database.CreateCommand($"ALTER TABLE {schema}.receipts DROP CONSTRAINT receipts_entity_type_partition_key_creation_subject_key");
        await change.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresTransitionReceiptOptions(schema, "receipts", "tenant/a").ValidateSchemaAsync(database));
    });

    sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to run PostgreSQL repository conformance.";
        }
    }

    sealed class PostgresTheoryAttribute : TheoryAttribute
    {
        public PostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to run PostgreSQL repository conformance.";
        }
    }
}
