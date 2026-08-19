using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Tests.ExecutionKernel;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresProcessDurableStoreTests
{
    [Fact]
    public void ExceptionClassifier_PreservesAmbiguousProviderFailuresAndRejectsLocalFailures()
    {
        var classifier = PostgresProcessStoreMutationExceptionClassifier.Instance;

        Assert.Equal(
            ProcessStoreMutationExceptionClassification.Ambiguous,
            classifier.Classify(new NpgsqlException("provider boundary")));
        Assert.Equal(
            ProcessStoreMutationExceptionClassification.Ambiguous,
            classifier.Classify(new OperationCanceledException("provider-local cancellation")));
        Assert.Equal(
            ProcessStoreMutationExceptionClassification.NotAmbiguous,
            classifier.Classify(new InvalidOperationException("local validation")));
    }

    [Fact]
    public async Task Capabilities_DeclareAtomicDurableProcessGuaranteesAndConfiguredLimit()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=unused;Username=unused;Password=unused");
        var store = new PostgresProcessDurableStore(
            dataSource,
            new(
                authorityId: "authority/process-tests",
                maximumDocumentBytes: 1024));

        Assert.True(store.Capabilities.SupportsAtomicAggregateCommit);
        Assert.True(store.Capabilities.SupportsCompareAndSwap);
        Assert.True(store.Capabilities.SupportsWorkerFencing);
        Assert.Equal(1024, store.Capabilities.MaxCommitBytes);
    }

    [Theory]
    [InlineData("", "processes")]
    [InlineData("schema", "")]
    [InlineData("schema", "bad\0table")]
    public void Options_ReuseSharedPostgresIdentifierValidation(string schema, string table)
    {
        Assert.Throws<ArgumentException>(() => new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-tests",
            schema: schema,
            table: table));
    }

    [Fact]
    public void Options_ExposeExplicitAuthorityDocumentLimitAndQualifiedTable()
    {
        var options = new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-tests",
            maximumDocumentBytes: 2048);

        Assert.Equal(2048, options.MaximumDocumentBytes);
        Assert.Equal("\"cohesive\".\"process_durable_stores\"", options.QualifiedTable);
    }

    [Fact]
    public void Options_QuoteEverySharedPostgresIdentifierCharacter()
    {
        var options = new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-tests",
            schema: "process-schema",
            table: "process\"stores");

        Assert.Equal("\"process-schema\".\"process\"\"stores\"", options.QualifiedTable);
        Assert.Equal("\"process-schema\"", options.QualifiedSchema);
    }

    [PostgresFact]
    public async Task LocalPostgres_SerializesConcurrentAuthorityAccessThroughTheLockedRow()
    {
        var connectionString = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "The PostgreSQL integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var schema = $"process_concurrency_{Guid.NewGuid():N}";
        var options = new PostgresProcessDurableStoreOptions(
            authorityId: $"authority/process-concurrency/{Guid.NewGuid():N}",
            schema: schema);
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/postgres-durable-store/concurrency",
            semanticVariant: "postgres-concurrency");
        var context = OperationContext.Create();

        try
        {
            var bootstrap = new PostgresProcessDurableStore(
                dataSource: dataSource,
                options: options);
            await bootstrap.EnsureCreatedAsync(context);
            ProcessCommitId commitId = new("commit/postgres-process-concurrency");
            var stores = Enumerable.Range(0, 12)
                .Select(_ => new PostgresProcessDurableStore(
                    dataSource: dataSource,
                    options: options))
                .ToArray();

            var results = await Task.WhenAll(stores.Select(store => store.InitializeAsync(
                context: context,
                commitId: commitId,
                checkpoint: fixture.Checkpoint)));

            Assert.Contains(results, static result => result.Disposition == ProcessStoreMutationDisposition.Applied);
            Assert.All(results, static result => Assert.Contains(
                result.Disposition,
                new[] { ProcessStoreMutationDisposition.Applied, ProcessStoreMutationDisposition.Replayed }));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task LocalPostgres_RetainsProcessSnapshotAndReplayEvidenceAcrossAdapterReconstruction()
    {
        var connectionString = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "The PostgreSQL integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var schema = $"ari403_{Guid.NewGuid():N}";
        var authorityId = $"authority/process-restart/{Guid.NewGuid():N}";
        var options = new PostgresProcessDurableStoreOptions(
            authorityId: authorityId,
            schema: schema);
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/postgres-durable-store/restart",
            semanticVariant: "postgres-restart");
        var context = OperationContext.Create();

        try
        {
            var firstHostStore = new PostgresProcessDurableStore(dataSource, options);
            await firstHostStore.EnsureCreatedAsync(context);
            ProcessCommitId initializationId = new("commit/postgres-process-restart");
            var initialized = await firstHostStore.InitializeAsync(
                context,
                initializationId,
                fixture.Checkpoint);
            var acquired = await firstHostStore.AcquireWorkerAsync(
                context,
                fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
                initialized.Snapshot!.Revision,
                owner: "worker/first-host",
                leaseDuration: TimeSpan.FromMinutes(5),
                observedAtUtc: fixture.Checkpoint.UpdatedAtUtc.AddMinutes(1));

            var restartedHostStore = new PostgresProcessDurableStore(dataSource, options);
            var loaded = await restartedHostStore.LoadAsync(
                context,
                fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId);
            var replayed = await restartedHostStore.InitializeAsync(
                context,
                initializationId,
                fixture.Checkpoint);

            Assert.NotNull(loaded);
            Assert.Equal(acquired.Snapshot!.Revision, loaded.Revision);
            Assert.Equal(acquired.Snapshot.WorkerLease, loaded.WorkerLease);
            Assert.Equal(ProcessStoreMutationDisposition.Replayed, replayed.Disposition);
            Assert.Equal(ProcessStorageRevision.Initial, replayed.Snapshot!.Revision);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")))
            {
                Skip = "Set COHESIVE_POSTGRES_TEST_CONNECTION_STRING or run the materialization harness.";
            }
        }
    }
}
