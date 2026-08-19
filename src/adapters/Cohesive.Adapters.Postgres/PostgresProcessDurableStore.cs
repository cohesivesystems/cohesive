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
/// The first PostgreSQL realization stores one complete portable authority document per configured row. A
/// serializable transaction locks that row, hydrates the in-memory reference state machine, applies one canonical
/// operation, and replaces the document atomically. This deliberately favors semantic equivalence and
/// inspectability over cross-instance write concurrency. A future row-per-instance projection may preserve the
/// same document and store contracts without changing callers.
///
/// The caller owns the supplied <see cref="NpgsqlDataSource"/>. Call <see cref="EnsureCreatedAsync"/> explicitly
/// during bootstrap; ordinary store operations do not perform schema DDL.
/// </remarks>
public sealed class PostgresProcessDurableStore : IProcessDurableStore
{
    readonly NpgsqlDataSource dataSource;
    readonly PostgresProcessDurableStoreOptions options;

    /// <summary>Creates a PostgreSQL Process durability authority over a caller-owned data source.</summary>
    /// <param name="dataSource">Caller-owned PostgreSQL connection pool.</param>
    /// <param name="options">Exact authority row, table binding, and physical document limit.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PostgresProcessDurableStore(
        NpgsqlDataSource dataSource,
        PostgresProcessDurableStoreOptions options)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        Capabilities = new(
            SupportsAtomicAggregateCommit: true,
            SupportsCompareAndSwap: true,
            SupportsWorkerFencing: true,
            MaxCommitBytes: options.MaximumDocumentBytes);
    }

    /// <inheritdoc />
    public ProcessDurableStoreCapabilities Capabilities { get; }

    /// <summary>Creates the configured schema and authority table when absent.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <returns>A task completing after DDL commits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before DDL commits.</exception>
    /// <exception cref="NpgsqlException">PostgreSQL rejects or cannot execute the DDL.</exception>
    public async Task EnsureCreatedAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        await using var command = dataSource.CreateCommand($$"""
            CREATE SCHEMA IF NOT EXISTS {{options.QualifiedSchema}};
            CREATE TABLE IF NOT EXISTS {{options.QualifiedTable}} (
                authority_id text PRIMARY KEY,
                revision bigint NOT NULL CHECK (revision > 0),
                document jsonb NOT NULL,
                document_fingerprint text NOT NULL,
                updated_at timestamptz NOT NULL
            );
            """);
        await command.ExecuteNonQueryAsync(context.CancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ProcessDurableStoreSnapshot?> LoadAsync(
        OperationContext context,
        ProcessInstanceId instanceId) =>
        MutateAsync(
            context,
            (store, providerContext) => store.LoadAsync(providerContext, instanceId));

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> InitializeAsync(
        OperationContext context,
        ProcessCommitId commitId,
        ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return MutateAsync(
            context,
            (store, providerContext) => store.InitializeAsync(
                providerContext,
                commitId,
                checkpoint));
    }

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> AdmitInputAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        ProcessActivationInput input,
        DateTimeOffset admittedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        return MutateAsync(
            context,
            (store, providerContext) => store.AdmitInputAsync(
                providerContext,
                instanceId,
                input,
                admittedAtUtc));
    }

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> AcquireWorkerAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        ProcessStorageRevision expectedRevision,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc) =>
        MutateAsync(
            context,
            (store, providerContext) => store.AcquireWorkerAsync(
                providerContext,
                instanceId,
                expectedRevision,
                owner,
                leaseDuration,
                observedAtUtc));

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> RenewWorkerAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        string owner,
        ProcessWorkerFence fence,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc) =>
        MutateAsync(
            context,
            (store, providerContext) => store.RenewWorkerAsync(
                providerContext,
                instanceId,
                owner,
                fence,
                leaseDuration,
                observedAtUtc));

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> CommitAsync(
        OperationContext context,
        ProcessDurableCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return MutateAsync(
            context,
            (store, providerContext) => store.CommitAsync(providerContext, commit));
    }

    async Task<TResult> MutateAsync<TResult>(
        OperationContext context,
        Func<InMemoryProcessDurableStore, OperationContext, Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        context.ThrowIfCancellationRequested();
        var cancellationToken = context.CancellationToken;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);

        var emptyJson = ProcessDurableStoreJsonSerializer.Serialize(ProcessDurableStoreDocument.Empty());
        var emptyFingerprint = Fingerprint(emptyJson);
        await using (var initialize = new NpgsqlCommand($$"""
            INSERT INTO {{options.QualifiedTable}}
                (authority_id, revision, document, document_fingerprint, updated_at)
            VALUES
                (@authority_id, 1, @document, @fingerprint, clock_timestamp())
            ON CONFLICT (authority_id) DO NOTHING;
            """, connection, transaction))
        {
            initialize.Parameters.AddWithValue("authority_id", options.AuthorityId);
            initialize.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb, emptyJson);
            initialize.Parameters.AddWithValue("fingerprint", emptyFingerprint);
            await initialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long revision;
        string documentJson;
        string fingerprint;
        DateTimeOffset providerNow;
        await using (var load = new NpgsqlCommand($$"""
            SELECT revision, document::text, document_fingerprint, clock_timestamp()
            FROM {{options.QualifiedTable}}
            WHERE authority_id = @authority_id
            FOR UPDATE;
            """, connection, transaction))
        {
            load.Parameters.AddWithValue("authority_id", options.AuthorityId);
            await using var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The PostgreSQL Process durability authority row disappeared during initialization.");
            }
            revision = reader.GetInt64(0);
            documentJson = reader.GetString(1);
            fingerprint = reader.GetString(2);
            var databaseNow = reader.GetFieldValue<DateTime>(3);
            providerNow = new DateTimeOffset(DateTime.SpecifyKind(databaseNow, DateTimeKind.Utc));
        }

        var reference = new InMemoryProcessDurableStore(
            ProcessDurableStoreJsonSerializer.Deserialize(documentJson));
        var providerContext = context with { TimeProvider = new FixedTimeProvider(providerNow) };
        var result = await operation(reference, providerContext).ConfigureAwait(false);
        var replacementJson = ProcessDurableStoreJsonSerializer.Serialize(reference.CaptureDocument());
        var replacementBytes = Encoding.UTF8.GetByteCount(replacementJson);
        if (replacementBytes > options.MaximumDocumentBytes)
        {
            throw new InvalidOperationException(
                $"The Process durable-store document requires {replacementBytes} UTF-8 bytes, exceeding the configured maximum of {options.MaximumDocumentBytes} bytes.");
        }

        var replacementFingerprint = Fingerprint(replacementJson);
        if (!string.Equals(fingerprint, replacementFingerprint, StringComparison.Ordinal))
        {
            await using var update = new NpgsqlCommand($$"""
                UPDATE {{options.QualifiedTable}}
                SET revision = @next_revision,
                    document = @document,
                    document_fingerprint = @fingerprint,
                    updated_at = clock_timestamp()
                WHERE authority_id = @authority_id AND revision = @expected_revision;
                """, connection, transaction);
            update.Parameters.AddWithValue("next_revision", checked(revision + 1));
            update.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb, replacementJson);
            update.Parameters.AddWithValue("fingerprint", replacementFingerprint);
            update.Parameters.AddWithValue("authority_id", options.AuthorityId);
            update.Parameters.AddWithValue("expected_revision", revision);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new DBConcurrencyException(
                    "The PostgreSQL Process durability authority revision changed during a locked mutation.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    static string Fingerprint(string document) =>
        $"sha256-v1:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)))}";

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
