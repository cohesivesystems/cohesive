using System.Security.Cryptography;
using System.Text;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Tests.ExecutionKernel;
using Npgsql;
using NpgsqlTypes;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresProcessDurableStoreTests
{
    [Fact]
    public async Task Paging_ReconstructsCanonicalAggregateAndReusesUnchangedEvidencePages()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/postgres-durable-store/paging",
            semanticVariant: "postgres-paging");
        var reference = new InMemoryProcessDurableStore();
        var initialized = await reference.InitializeAsync(
            context: OperationContext.Create(),
            commitId: new("commit/postgres-process-paging"),
            checkpoint: fixture.Checkpoint);
        var before = Assert.Single(reference.CaptureDocument().Aggregates);
        var options = new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-paging",
            minimumPageBytes: 256,
            targetPageBytes: 512,
            maximumPageBytes: 1024);
        var first = PostgresProcessDurableStorePaging.Page(before, options);

        _ = await reference.AcquireWorkerAsync(
            context: OperationContext.Create(),
            instanceId: before.InstanceId,
            expectedRevision: initialized.Snapshot!.Revision,
            owner: "worker/paging",
            leaseDuration: TimeSpan.FromMinutes(5),
            observedAtUtc: fixture.Checkpoint.UpdatedAtUtc.AddMinutes(1));
        var after = Assert.Single(reference.CaptureDocument().Aggregates);
        var second = PostgresProcessDurableStorePaging.Page(after, options);
        var pages = second.Pages
            .GroupBy(static page => page.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Content,
                StringComparer.Ordinal);

        var reconstructed = PostgresProcessDurableStorePaging.Reconstruct(
            aggregateFingerprint: second.AggregateFingerprint,
            aggregateBytes: second.AggregateBytes,
            manifest: second.Manifest,
            pages: pages,
            options: options);
        var expectedDocument = ProcessDurableStoreJsonSerializer.Serialize(new(
            schemaVersion: ProcessDurableStoreDocument.CurrentSchemaVersion,
            aggregates: [after]));
        var reconstructedDocument = ProcessDurableStoreJsonSerializer.Serialize(new(
            schemaVersion: ProcessDurableStoreDocument.CurrentSchemaVersion,
            aggregates: [reconstructed]));
        var firstFingerprints = first.Pages
            .Select(static page => page.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
        var newlyWrittenBytes = second.Pages
            .Where(page => !firstFingerprints.Contains(page.Fingerprint))
            .Sum(static page => page.Content.Length);

        Assert.Equal(expectedDocument, reconstructedDocument);
        Assert.All(second.Pages, page => Assert.InRange(page.Content.Length, 1, options.MaximumPageBytes));
        Assert.Contains(second.Pages, page => firstFingerprints.Contains(page.Fingerprint));
        Assert.True(newlyWrittenBytes < second.AggregateBytes);

        Assert.True(pages.Remove(second.Pages[0].Fingerprint));
        Assert.Throws<InvalidDataException>(() => PostgresProcessDurableStorePaging.Reconstruct(
            aggregateFingerprint: second.AggregateFingerprint,
            aggregateBytes: second.AggregateBytes,
            manifest: second.Manifest,
            pages: pages,
            options: options));
    }

    [Fact]
    public void Paging_EnforcesTheOptionalAggregateReconstructionBound()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/postgres-durable-store/aggregate-bound",
            semanticVariant: "postgres-aggregate-bound");
        var aggregate = new ProcessDurableAggregateDocument(
            checkpoint: fixture.Checkpoint,
            revision: ProcessStorageRevision.Initial,
            workerLease: null,
            latestWorkerFence: 0,
            localState: [],
            localMutationReceipts: [],
            commitReceipts: []);
        var options = new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-aggregate-bound",
            minimumPageBytes: 1,
            targetPageBytes: 1,
            maximumPageBytes: 1,
            maximumAggregateBytes: 1);

        Assert.Throws<InvalidOperationException>(() =>
            PostgresProcessDurableStorePaging.Page(aggregate, options));
    }

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
                maximumAggregateBytes: 1024));

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
    public void Options_ExposeExplicitPagingPolicyAndQualifiedTables()
    {
        var options = new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-tests",
            minimumPageBytes: 512,
            targetPageBytes: 1024,
            maximumPageBytes: 2048,
            maximumAggregateBytes: 4096);

        Assert.Equal(512, options.MinimumPageBytes);
        Assert.Equal(1024, options.TargetPageBytes);
        Assert.Equal(2048, options.MaximumPageBytes);
        Assert.Equal(4096, options.MaximumAggregateBytes);
        Assert.Equal("\"cohesive\".\"process_durable_stores\"", options.QualifiedTable);
        Assert.Equal("\"cohesive\".\"process_durable_stores_instances\"", options.QualifiedInstanceTable);
        Assert.Equal("\"cohesive\".\"process_durable_stores_pages\"", options.QualifiedPageTable);
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

    [Fact]
    public void Options_RejectInvalidContentDefinedPagingPolicy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-tests",
            minimumPageBytes: 0));
        Assert.Throws<ArgumentException>(() => new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-tests",
            minimumPageBytes: 512,
            targetPageBytes: 768,
            maximumPageBytes: 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-tests",
            minimumPageBytes: 512,
            targetPageBytes: 1024,
            maximumPageBytes: 768));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresProcessDurableStoreOptions(
            authorityId: "authority/process-tests",
            maximumAggregateBytes: 0));
    }

    [PostgresFact]
    public async Task LocalPostgres_SerializesConcurrentSameInstanceCompareAndSwap()
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
    public async Task LocalPostgres_MigratesLegacyDocumentIntoExactInstancePages()
    {
        var connectionString = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "The PostgreSQL integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var schema = $"process_migration_{Guid.NewGuid():N}";
        var authorityId = $"authority/process-migration/{Guid.NewGuid():N}";
        var options = new PostgresProcessDurableStoreOptions(
            authorityId: authorityId,
            schema: schema,
            minimumPageBytes: 256,
            targetPageBytes: 512,
            maximumPageBytes: 1024);
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/postgres-durable-store/migration",
            semanticVariant: "postgres-migration");
        var context = OperationContext.Create();
        var legacy = new InMemoryProcessDurableStore();
        ProcessCommitId initializationId = new("commit/postgres-process-migration");
        var initialized = await legacy.InitializeAsync(context, initializationId, fixture.Checkpoint);
        var acquired = await legacy.AcquireWorkerAsync(
            context: context,
            instanceId: fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            expectedRevision: initialized.Snapshot!.Revision,
            owner: "worker/legacy",
            leaseDuration: TimeSpan.FromMinutes(5),
            observedAtUtc: fixture.Checkpoint.UpdatedAtUtc.AddMinutes(1));
        var legacyJson = ProcessDurableStoreJsonSerializer.Serialize(legacy.CaptureDocument());
        var legacyFingerprint = Fingerprint(legacyJson);

        try
        {
            await using (var create = dataSource.CreateCommand($$"""
                CREATE SCHEMA {{options.QualifiedSchema}};
                CREATE TABLE {{options.QualifiedTable}} (
                    authority_id text PRIMARY KEY,
                    revision bigint NOT NULL CHECK (revision > 0),
                    document jsonb NOT NULL,
                    document_fingerprint text NOT NULL,
                    updated_at timestamptz NOT NULL
                );
                """))
            {
                await create.ExecuteNonQueryAsync();
            }
            await using (var insert = dataSource.CreateCommand($$"""
                INSERT INTO {{options.QualifiedTable}}
                    (authority_id, revision, document, document_fingerprint, updated_at)
                VALUES (@authority_id, 2, @document, @fingerprint, clock_timestamp());
                """))
            {
                insert.Parameters.AddWithValue("authority_id", authorityId);
                insert.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb, legacyJson);
                insert.Parameters.AddWithValue("fingerprint", legacyFingerprint);
                await insert.ExecuteNonQueryAsync();
            }

            var normalized = new PostgresProcessDurableStore(dataSource, options);
            await normalized.EnsureCreatedAsync(context);
            var loaded = await normalized.LoadAsync(
                context,
                fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId);
            var replayed = await normalized.InitializeAsync(context, initializationId, fixture.Checkpoint);
            var physical = await ReadStorageAsync(dataSource, options);

            Assert.NotNull(loaded);
            Assert.Equal(acquired.Snapshot!.Revision, loaded.Revision);
            Assert.Equal(acquired.Snapshot.WorkerLease, loaded.WorkerLease);
            Assert.Equal(
                ProcessDurableCheckpointJsonSerializer.Serialize(acquired.Snapshot.Checkpoint),
                ProcessDurableCheckpointJsonSerializer.Serialize(loaded.Checkpoint));
            Assert.Equal(ProcessStoreMutationDisposition.Replayed, replayed.Disposition);
            Assert.Equal(ProcessStorageRevision.Initial, replayed.Snapshot!.Revision);
            Assert.Equal(1, physical.InstanceCount);
            Assert.True(physical.PageCount > 1);
            Assert.Equal(legacyJson, await ReadLegacyDocumentAsync(dataSource, options));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task LocalPostgres_RewritesOnlyNewBoundedPagesForLeaseMutation()
    {
        var connectionString = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "The PostgreSQL integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var schema = $"process_paging_{Guid.NewGuid():N}";
        var options = new PostgresProcessDurableStoreOptions(
            authorityId: $"authority/process-paging/{Guid.NewGuid():N}",
            schema: schema,
            minimumPageBytes: 256,
            targetPageBytes: 512,
            maximumPageBytes: 1024);
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/postgres-durable-store/page-reuse",
            semanticVariant: "postgres-page-reuse");
        var context = OperationContext.Create();

        try
        {
            var store = new PostgresProcessDurableStore(dataSource, options);
            await store.EnsureCreatedAsync(context);
            var initialized = await store.InitializeAsync(
                context: context,
                commitId: new("commit/postgres-process-page-reuse"),
                checkpoint: fixture.Checkpoint);
            var before = await ReadStorageAsync(dataSource, options);
            _ = await store.AcquireWorkerAsync(
                context: context,
                instanceId: fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
                expectedRevision: initialized.Snapshot!.Revision,
                owner: "worker/page-reuse",
                leaseDuration: TimeSpan.FromMinutes(5),
                observedAtUtc: fixture.Checkpoint.UpdatedAtUtc.AddMinutes(1));
            var after = await ReadStorageAsync(dataSource, options);
            var writtenPageBytes = after.UniquePageBytes - before.UniquePageBytes;

            Assert.True(writtenPageBytes > 0);
            Assert.True(writtenPageBytes < after.AggregateBytes);
            Assert.True(after.PageCount > before.PageCount);
            Assert.InRange(after.MaximumPageBytes, 1, options.MaximumPageBytes);
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

            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restartedHostStore.LoadAsync(
                context: context.WithCancellationToken(cancellation.Token),
                instanceId: fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId));
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

    static async Task<StorageMeasurement> ReadStorageAsync(
        NpgsqlDataSource dataSource,
        PostgresProcessDurableStoreOptions options)
    {
        await using var command = dataSource.CreateCommand($$"""
            SELECT
                (SELECT count(*) FROM {{options.QualifiedInstanceTable}} WHERE authority_id = @authority_id),
                (SELECT count(*) FROM {{options.QualifiedPageTable}} WHERE authority_id = @authority_id),
                COALESCE((SELECT sum(content_bytes) FROM {{options.QualifiedPageTable}} WHERE authority_id = @authority_id), 0),
                COALESCE((SELECT max(content_bytes) FROM {{options.QualifiedPageTable}} WHERE authority_id = @authority_id), 0),
                COALESCE((SELECT max(aggregate_bytes) FROM {{options.QualifiedInstanceTable}} WHERE authority_id = @authority_id), 0);
            """);
        command.Parameters.AddWithValue("authority_id", options.AuthorityId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(
            InstanceCount: reader.GetInt64(0),
            PageCount: reader.GetInt64(1),
            UniquePageBytes: reader.GetInt64(2),
            MaximumPageBytes: reader.GetInt32(3),
            AggregateBytes: reader.GetInt64(4));
    }

    static async Task<string> ReadLegacyDocumentAsync(
        NpgsqlDataSource dataSource,
        PostgresProcessDurableStoreOptions options)
    {
        await using var command = dataSource.CreateCommand($$"""
            SELECT document::text
            FROM {{options.QualifiedTable}}
            WHERE authority_id = @authority_id;
            """);
        command.Parameters.AddWithValue("authority_id", options.AuthorityId);
        var json = (string?)await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The legacy Process durable-store document disappeared.");
        return ProcessDurableStoreJsonSerializer.Serialize(
            ProcessDurableStoreJsonSerializer.Deserialize(json));
    }

    static string Fingerprint(string document) =>
        $"sha256-v1:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)))}";

    sealed record StorageMeasurement(
        long InstanceCount,
        long PageCount,
        long UniquePageBytes,
        int MaximumPageBytes,
        long AggregateBytes);
}
