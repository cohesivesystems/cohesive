using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;
using Npgsql;
using NpgsqlTypes;

namespace Cohesive.Adapters.Postgres;

/// <summary>Durable PostgreSQL realization of the atomic provider-neutral Process store.</summary>
/// <remarks>
/// <para>
/// The portable <see cref="ProcessDurableStoreDocument"/> remains the semantic authority. PostgreSQL projects each
/// aggregate into one independently fenced instance root and deterministic content-addressed pages. Mutations write
/// only pages whose canonical content has not already been retained, then compare-and-swap the small root row in the
/// same transaction. Different Process instances therefore do not share one serialization lock.
/// </para>
/// <para>
/// <see cref="EnsureCreatedAsync"/> transactionally imports aggregates from the first-generation single-document
/// table when it exists. The legacy row is retained as migration evidence but is no longer mutated by this adapter.
/// Run migration with exclusive ownership of the configured authority; concurrent first-generation writers are not
/// supported after the normalized projection becomes authoritative.
/// </para>
/// <para>
/// The caller owns the supplied <see cref="NpgsqlDataSource"/>. Call <see cref="EnsureCreatedAsync"/> explicitly
/// during bootstrap; ordinary store operations do not perform schema DDL or migration.
/// </para>
/// </remarks>
public sealed class PostgresProcessDurableStore : IProcessDurableStore
{
    const int MaximumCompareAndSwapAttempts = 32;
    readonly NpgsqlDataSource dataSource;
    readonly PostgresProcessDurableStoreOptions options;
    readonly PostgresProcessDurableStoreSql sql;

    /// <summary>Creates a PostgreSQL Process durability authority over a caller-owned data source.</summary>
    /// <param name="dataSource">Caller-owned PostgreSQL connection pool.</param>
    /// <param name="options">Exact authority, table binding, paging policy, and optional reconstruction bound.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PostgresProcessDurableStore(
        NpgsqlDataSource dataSource,
        PostgresProcessDurableStoreOptions options)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        sql = new(options);
        Capabilities = new(
            SupportsAtomicAggregateCommit: true,
            SupportsCompareAndSwap: true,
            SupportsWorkerFencing: true,
            MaxCommitBytes: options.MaximumAggregateBytes);
    }

    /// <inheritdoc />
    public ProcessDurableStoreCapabilities Capabilities { get; }

    /// <summary>Creates normalized tables and transactionally imports any legacy authority document.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <returns>A task completing after DDL and compatible migration commit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before bootstrap commits.</exception>
    /// <exception cref="InvalidDataException">Legacy or normalized durable content fails canonical verification.</exception>
    /// <exception cref="NpgsqlException">PostgreSQL rejects or cannot execute bootstrap.</exception>
    public async Task EnsureCreatedAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        var cancellationToken = context.CancellationToken;
        await using (var command = dataSource.CreateCommand($$"""
            CREATE SCHEMA IF NOT EXISTS {{options.QualifiedSchema}};
            CREATE TABLE IF NOT EXISTS {{options.QualifiedTable}} (
                authority_id text PRIMARY KEY,
                revision bigint NOT NULL CHECK (revision > 0),
                document jsonb NOT NULL,
                document_fingerprint text NOT NULL,
                updated_at timestamptz NOT NULL
            );
            CREATE TABLE IF NOT EXISTS {{options.QualifiedPageTable}} (
                authority_id text NOT NULL,
                page_fingerprint text NOT NULL,
                content bytea NOT NULL,
                content_bytes integer NOT NULL CHECK (content_bytes > 0),
                created_at timestamptz NOT NULL,
                PRIMARY KEY (authority_id, page_fingerprint),
                CHECK (octet_length(content) = content_bytes)
            );
            CREATE TABLE IF NOT EXISTS {{options.QualifiedInstanceTable}} (
                authority_id text NOT NULL,
                instance_id text NOT NULL,
                physical_revision bigint NOT NULL CHECK (physical_revision > 0),
                storage_format text NOT NULL,
                aggregate_fingerprint text NOT NULL,
                aggregate_bytes bigint NOT NULL CHECK (aggregate_bytes > 0),
                page_manifest text NOT NULL CHECK (octet_length(page_manifest) > 0),
                page_count integer NOT NULL CHECK (page_count > 0),
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (authority_id, instance_id)
            );
            """))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await MigrateLegacyDocumentAsync(context).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ProcessDurableStoreSnapshot?> LoadAsync(
        OperationContext context,
        ProcessInstanceId instanceId) =>
        AccessAsync(
            context: context,
            instanceId: instanceId,
            operation: (store, providerContext) => store.LoadAsync(
                context: providerContext,
                instanceId: instanceId));

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> InitializeAsync(
        OperationContext context,
        ProcessCommitId commitId,
        ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var instanceId = checkpoint.ContinuationIdentity.ProcessInstanceId;
        return AccessAsync(
            context: context,
            instanceId: instanceId,
            operation: (store, providerContext) => store.InitializeAsync(
                context: providerContext,
                commitId: commitId,
                checkpoint: checkpoint));
    }

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> AdmitInputAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        ProcessActivationInput input,
        DateTimeOffset admittedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        return AccessAsync(
            context: context,
            instanceId: instanceId,
            operation: (store, providerContext) => store.AdmitInputAsync(
                context: providerContext,
                instanceId: instanceId,
                input: input,
                admittedAtUtc: admittedAtUtc));
    }

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> AcquireWorkerAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        ProcessStorageRevision expectedRevision,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc) =>
        AccessAsync(
            context: context,
            instanceId: instanceId,
            operation: (store, providerContext) => store.AcquireWorkerAsync(
                context: providerContext,
                instanceId: instanceId,
                expectedRevision: expectedRevision,
                owner: owner,
                leaseDuration: leaseDuration,
                observedAtUtc: observedAtUtc));

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> RenewWorkerAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        string owner,
        ProcessWorkerFence fence,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc) =>
        AccessAsync(
            context: context,
            instanceId: instanceId,
            operation: (store, providerContext) => store.RenewWorkerAsync(
                context: providerContext,
                instanceId: instanceId,
                owner: owner,
                fence: fence,
                leaseDuration: leaseDuration,
                observedAtUtc: observedAtUtc));

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> CommitAsync(
        OperationContext context,
        ProcessDurableCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var instanceId = commit.Checkpoint.ContinuationIdentity.ProcessInstanceId;
        return AccessAsync(
            context: context,
            instanceId: instanceId,
            operation: (store, providerContext) => store.CommitAsync(
                context: providerContext,
                commit: commit));
    }

    async Task<TResult> AccessAsync<TResult>(
        OperationContext context,
        ProcessInstanceId instanceId,
        Func<InMemoryProcessDurableStore, OperationContext, Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(instanceId.Value))
            throw new ArgumentException("A PostgreSQL Process-store operation requires an instance identity.", nameof(instanceId));
        context.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt < MaximumCompareAndSwapAttempts; attempt++)
        {
            try
            {
                return await PostgresSerializationRetrier.ExecuteAsync(
                        context: context,
                        operation: () => AccessOnceAsync(
                            context: context,
                            instanceId: instanceId,
                            operation: operation))
                    .ConfigureAwait(false);
            }
            catch (PostgresProcessDurableStoreConcurrencyException)
                when (attempt < MaximumCompareAndSwapAttempts - 1)
            {
                context.ThrowIfCancellationRequested();
            }
        }
        throw new DBConcurrencyException(
            $"The PostgreSQL Process aggregate '{instanceId.Value}' did not stabilize after {MaximumCompareAndSwapAttempts} compare-and-swap attempts.");
    }

    async Task<TResult> AccessOnceAsync<TResult>(
        OperationContext context,
        ProcessInstanceId instanceId,
        Func<InMemoryProcessDurableStore, OperationContext, Task<TResult>> operation)
    {
        var cancellationToken = context.CancellationToken;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken)
            .ConfigureAwait(false);
        var execute = PostgresNpgsqlExecution.CreateTransactionExecutor(connection, transaction);
        var root = await LoadRootAsync(execute, instanceId, cancellationToken).ConfigureAwait(false);
        var reference = root is null
            ? new InMemoryProcessDurableStore()
            : new InMemoryProcessDurableStore(new(
                schemaVersion: ProcessDurableStoreDocument.CurrentSchemaVersion,
                aggregates:
                [
                    await LoadAggregateAsync(execute, instanceId, root, cancellationToken).ConfigureAwait(false)
                ]));
        var providerNow = await ReadProviderNowAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var providerContext = context with { TimeProvider = new FixedTimeProvider(providerNow) };
        var result = await operation(reference, providerContext).ConfigureAwait(false);
        var replacement = reference.CaptureDocument().Aggregates;
        if (replacement.IsEmpty)
        {
            if (root is not null)
            {
                throw new InvalidOperationException(
                    "A PostgreSQL Process-store operation cannot remove an existing aggregate implicitly.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        if (replacement.Length != 1 || replacement[0].InstanceId != instanceId)
        {
            throw new InvalidOperationException(
                "A PostgreSQL Process-store operation must retain exactly its addressed aggregate.");
        }

        var paged = PostgresProcessDurableStorePaging.Page(replacement[0], options);
        if (root is not null
            && string.Equals(root.AggregateFingerprint, paged.AggregateFingerprint, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        await PersistMissingPagesAsync(execute, paged, cancellationToken).ConfigureAwait(false);
        var stored = await execute(
                root is null
                    ? sql.InsertRoot(options.AuthorityId, instanceId.Value, paged)
                    : sql.UpdateRoot(options.AuthorityId, instanceId.Value, root.PhysicalRevision, paged),
                cancellationToken)
            .ConfigureAwait(false);
        if (stored.Rows.Length != 1)
            throw new PostgresProcessDurableStoreConcurrencyException(instanceId);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    async Task MigrateLegacyDocumentAsync(OperationContext context)
    {
        var cancellationToken = context.CancellationToken;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken)
            .ConfigureAwait(false);
        string? documentJson = null;
        string? retainedFingerprint = null;
        await using (var command = new NpgsqlCommand($$"""
            SELECT document::text, document_fingerprint
            FROM {{options.QualifiedTable}}
            WHERE authority_id = @authority_id
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter
            {
                ParameterName = "authority_id",
                NpgsqlDbType = NpgsqlDbType.Text,
                Value = options.AuthorityId
            });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                documentJson = reader.GetString(0);
                retainedFingerprint = reader.GetString(1);
            }
        }
        if (documentJson is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var document = ProcessDurableStoreJsonSerializer.Deserialize(documentJson);
        var canonical = ProcessDurableStoreJsonSerializer.Serialize(document);
        if (!string.Equals(Fingerprint(canonical), retainedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The legacy PostgreSQL Process durable-store fingerprint does not match its canonical document.");
        }

        var execute = PostgresNpgsqlExecution.CreateTransactionExecutor(connection, transaction);
        foreach (var aggregate in document.Aggregates)
        {
            context.ThrowIfCancellationRequested();
            var paged = PostgresProcessDurableStorePaging.Page(aggregate, options);
            var existing = await LoadRootAsync(execute, aggregate.InstanceId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(
                        existing.AggregateFingerprint,
                        paged.AggregateFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Normalized Process aggregate '{aggregate.InstanceId.Value}' conflicts with its legacy authority document.");
                }
                continue;
            }

            await PersistMissingPagesAsync(execute, paged, cancellationToken).ConfigureAwait(false);
            var inserted = await execute(
                    sql.InsertRoot(options.AuthorityId, aggregate.InstanceId.Value, paged),
                    cancellationToken)
                .ConfigureAwait(false);
            if (inserted.Rows.Length != 1)
            {
                throw new DBConcurrencyException(
                    $"Process aggregate '{aggregate.InstanceId.Value}' changed during legacy migration.");
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<PostgresProcessDurableStoreRoot?> LoadRootAsync(
        PostgresNpgsqlCommandExecutor execute,
        ProcessInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        var result = await execute(
                sql.LoadRoot(options.AuthorityId, instanceId.Value),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Rows.IsEmpty)
            return null;
        if (result.Rows.Length != 1)
        {
            throw new InvalidDataException(
                $"PostgreSQL retained multiple roots for Process aggregate '{instanceId.Value}'.");
        }
        var row = result.Rows[0];
        var root = new PostgresProcessDurableStoreRoot(
            PhysicalRevision: (long)row[0]!,
            StorageFormat: (string)row[1]!,
            AggregateFingerprint: (string)row[2]!,
            AggregateBytes: (long)row[3]!,
            PageManifest: (string)row[4]!,
            PageCount: (int)row[5]!);
        if (!string.Equals(root.StorageFormat, PostgresProcessDurableStorePaging.Format, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Process aggregate '{instanceId.Value}' uses unsupported PostgreSQL storage format '{root.StorageFormat}'.");
        }
        var manifest = PostgresProcessDurableStorePaging.ParseManifest(root.PageManifest);
        if (manifest.Length != root.PageCount)
        {
            throw new InvalidDataException(
                $"Process aggregate '{instanceId.Value}' page count differs from its manifest.");
        }
        return root;
    }

    async ValueTask<ProcessDurableAggregateDocument> LoadAggregateAsync(
        PostgresNpgsqlCommandExecutor execute,
        ProcessInstanceId instanceId,
        PostgresProcessDurableStoreRoot root,
        CancellationToken cancellationToken)
    {
        var manifest = PostgresProcessDurableStorePaging.ParseManifest(root.PageManifest);
        var unique = manifest.Distinct(StringComparer.Ordinal).ToImmutableArray();
        var result = await execute(
                sql.LoadPages(options.AuthorityId, unique),
                cancellationToken)
            .ConfigureAwait(false);
        var pages = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var row in result.Rows)
        {
            var fingerprint = (string)row[0]!;
            var content = (byte[])row[1]!;
            var declaredBytes = (int)row[2]!;
            if (content.Length != declaredBytes || !pages.TryAdd(fingerprint, ImmutableArray.Create(content)))
            {
                throw new InvalidDataException(
                    $"Process aggregate '{instanceId.Value}' retained malformed or duplicate page '{fingerprint}'.");
            }
        }
        var aggregate = PostgresProcessDurableStorePaging.Reconstruct(
            aggregateFingerprint: root.AggregateFingerprint,
            aggregateBytes: root.AggregateBytes,
            manifest: root.PageManifest,
            pages: pages,
            options: options);
        if (aggregate.InstanceId != instanceId)
        {
            throw new InvalidDataException(
                $"Process aggregate root '{instanceId.Value}' reconstructed content for '{aggregate.InstanceId.Value}'.");
        }
        return aggregate;
    }

    async ValueTask PersistMissingPagesAsync(
        PostgresNpgsqlCommandExecutor execute,
        PostgresProcessDurablePagedAggregate aggregate,
        CancellationToken cancellationToken)
    {
        var byFingerprint = aggregate.Pages
            .GroupBy(static page => page.Fingerprint, StringComparer.Ordinal)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        ImmutableArray<string> fingerprints = [.. byFingerprint.Keys.Order(StringComparer.Ordinal)];
        var retained = await execute(
                sql.FindPages(options.AuthorityId, fingerprints),
                cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> existing = retained.Rows
            .Select(static row => (string)row[0]!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pair in byFingerprint.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (existing.Contains(pair.Key))
                continue;
            _ = await execute(
                    sql.InsertPage(options.AuthorityId, pair.Value),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    async ValueTask<DateTimeOffset> ReadProviderNowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.ProviderNowSql, connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL returned no Process-store clock observation.");
        var databaseNow = (DateTime)value;
        return new(DateTime.SpecifyKind(databaseNow, DateTimeKind.Utc));
    }

    static string Fingerprint(string document) =>
        $"sha256-v1:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)))}";

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

sealed record PostgresProcessDurableStoreRoot(
    long PhysicalRevision,
    string StorageFormat,
    string AggregateFingerprint,
    long AggregateBytes,
    string PageManifest,
    int PageCount);

sealed class PostgresProcessDurableStoreConcurrencyException(ProcessInstanceId instanceId)
    : InvalidOperationException(
        $"The PostgreSQL Process aggregate '{instanceId.Value}' changed before its root compare-and-swap.");
