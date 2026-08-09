using System.Data;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Processes.Distribution;
using Npgsql;
using NpgsqlTypes;

namespace Cohesive.Adapters.Postgres;

/// <summary>Durable PostgreSQL competing-consumer realization of portable Process distribution.</summary>
/// <remarks>
/// The first production reference realization stores one complete provider-neutral ledger per authority row and
/// locks that row only while making a placement or lifecycle decision. Executions occur outside the transaction,
/// so adding worker processes increases in-flight throughput without a singleton coordinator or direct node
/// address. The aggregate row deliberately favors semantic correctness and inspectability over high claim-rate
/// scale; providers may later project the same ledger contract into sharded transactional rows.
///
/// The caller owns the supplied <see cref="NpgsqlDataSource"/>. Call <see cref="EnsureCreatedAsync"/> explicitly
/// during deployment or bootstrap; normal work operations never perform schema DDL. This adapter atomically commits
/// its distribution aggregate, but it does not by itself share a transaction with a separate Process-state store,
/// so <see cref="ProcessDistributionStoreCapabilities.SupportsAtomicProcessCommit"/> is false and production
/// validation requiring that composition fails closed.
/// </remarks>
public sealed class PostgresProcessDistributionStore : IProcessDistributionStore
{
    readonly NpgsqlDataSource dataSource;
    readonly PostgresProcessDistributionStoreOptions options;

    /// <summary>Creates a PostgreSQL distribution authority over a caller-owned data source.</summary>
    /// <param name="dataSource">Caller-owned PostgreSQL connection pool.</param>
    /// <param name="options">Exact authority row, table binding, and physical document limit.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PostgresProcessDistributionStore(
        NpgsqlDataSource dataSource,
        PostgresProcessDistributionStoreOptions options)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        Capabilities = new(
            isDurable: true,
            supportsAtomicClaim: true,
            supportsCompareAndSwap: true,
            supportsWorkerLeases: true,
            supportsClaimRenewal: true,
            supportsMonotonicFencing: true,
            supportsRunnableDiscovery: true,
            supportsCapacityReservations: true,
            supportsPoisonWork: true,
            supportsAtomicProcessCommit: false,
            maximumAuthorityStateBytes: options.MaximumLedgerBytes);
    }

    /// <inheritdoc />
    public ProcessDistributionStoreCapabilities Capabilities { get; }

    /// <summary>Creates the configured schema and aggregate table when absent.</summary>
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
            CREATE SCHEMA IF NOT EXISTS "{{options.Schema}}";
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
    public Task<ProcessDistributionMutationResult> EnsurePoolAsync(
        OperationContext context,
        ProcessWorkerPoolDefinition pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return MutateAsync(context, (store, providerContext) => store.EnsurePoolAsync(providerContext, pool));
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> SubmitAsync(
        OperationContext context,
        ProcessWorkSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return MutateAsync(context, (store, providerContext) => store.SubmitAsync(providerContext, submission));
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> RegisterWorkerAsync(
        OperationContext context,
        ProcessWorkerOffer offer,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return MutateAsync(
            context,
            (store, providerContext) => store.RegisterWorkerAsync(providerContext, offer, observedAtUtc));
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> RenewWorkerAsync(
        OperationContext context,
        ProcessWorkerIncarnationId worker,
        ProcessWorkerHealth health,
        DateTimeOffset observedAtUtc) =>
        MutateAsync(
            context,
            (store, providerContext) => store.RenewWorkerAsync(
                providerContext,
                worker,
                health,
                observedAtUtc));

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> SetWorkerDrainingAsync(
        OperationContext context,
        ProcessWorkerIncarnationId worker,
        bool draining,
        DateTimeOffset observedAtUtc) =>
        MutateAsync(
            context,
            (store, providerContext) => store.SetWorkerDrainingAsync(
                providerContext,
                worker,
                draining,
                observedAtUtc));

    /// <inheritdoc />
    public Task<ProcessWorkClaimResult> ClaimAsync(
        OperationContext context,
        ProcessWorkerPoolId pool,
        ProcessWorkerIncarnationId worker,
        ProcessWorkClaimRequestId request,
        DateTimeOffset observedAtUtc) =>
        MutateAsync(
            context,
            (store, providerContext) => store.ClaimAsync(
                providerContext,
                pool,
                worker,
                request,
                observedAtUtc));

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> RenewClaimAsync(
        OperationContext context,
        ProcessWorkClaim claim,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return MutateAsync(
            context,
            (store, providerContext) => store.RenewClaimAsync(providerContext, claim, observedAtUtc));
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> CompleteAsync(
        OperationContext context,
        ProcessWorkCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return MutateAsync(
            context,
            (store, providerContext) => store.CompleteAsync(providerContext, completion));
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> ReleaseAsync(
        OperationContext context,
        ProcessWorkRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return MutateAsync(
            context,
            (store, providerContext) => store.ReleaseAsync(providerContext, release));
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> ReconcileAsync(
        OperationContext context,
        ProcessWorkReconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        return MutateAsync(
            context,
            (store, providerContext) => store.ReconcileAsync(providerContext, reconciliation));
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> RequestCancellationAsync(
        OperationContext context,
        ProcessWorkId work,
        string reasonCode,
        DateTimeOffset observedAtUtc) =>
        MutateAsync(
            context,
            (store, providerContext) => store.RequestCancellationAsync(
                providerContext,
                work,
                reasonCode,
                observedAtUtc));

    /// <inheritdoc />
    public Task<ProcessWorkRecord?> InspectWorkAsync(OperationContext context, ProcessWorkId work) =>
        MutateAsync(
            context,
            (store, providerContext) => store.InspectWorkAsync(providerContext, work));

    /// <inheritdoc />
    public Task<ProcessWorkerPoolSnapshot?> InspectPoolAsync(
        OperationContext context,
        ProcessWorkerPoolId pool,
        DateTimeOffset observedAtUtc) =>
        MutateAsync(
            context,
            (store, providerContext) => store.InspectPoolAsync(providerContext, pool, observedAtUtc));

    async Task<TResult> MutateAsync<TResult>(
        OperationContext context,
        Func<InMemoryProcessDistributionStore, OperationContext, Task<TResult>> operation)
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

        var emptyJson = ProcessDistributionJsonSerializer.SerializeLedger(ProcessDistributionLedgerDocument.Empty());
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
                throw new InvalidOperationException("The PostgreSQL distribution authority row disappeared during initialization.");
            revision = reader.GetInt64(0);
            documentJson = reader.GetString(1);
            fingerprint = reader.GetString(2);
            var databaseNow = reader.GetFieldValue<DateTime>(3);
            providerNow = new DateTimeOffset(DateTime.SpecifyKind(databaseNow, DateTimeKind.Utc));
        }

        var reference = new InMemoryProcessDistributionStore(
            ProcessDistributionJsonSerializer.DeserializeLedger(documentJson));
        var providerContext = context with { TimeProvider = new FixedTimeProvider(providerNow) };
        var result = await operation(reference, providerContext).ConfigureAwait(false);
        var replacementJson = ProcessDistributionJsonSerializer.SerializeLedger(reference.CaptureLedger());
        var replacementBytes = Encoding.UTF8.GetByteCount(replacementJson);
        if (replacementBytes > options.MaximumLedgerBytes)
        {
            throw new InvalidOperationException(
                $"The distribution ledger requires {replacementBytes} UTF-8 bytes, exceeding the configured maximum of {options.MaximumLedgerBytes} bytes.");
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
                throw new DBConcurrencyException("The PostgreSQL distribution aggregate revision changed during a locked mutation.");
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
