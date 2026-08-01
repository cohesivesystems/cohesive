using System.Buffers;
using System.Collections.Immutable;
using System.Data;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;
using NpgsqlTypes;

namespace Cohesive.Adapters.Postgres;

internal sealed class PostgresNpgsqlLogicalReplicationProtocol : IPostgresLogicalReplicationProtocol
{
    const string EvidencePrefix = "cohesive.adapters.postgres/npgsql-logical-replication/v1";
    const string PublicationInspectionSql = """
        WITH target AS
        (
            SELECT
                c.oid,
                (
                    SELECT count(*)::integer
                    FROM pg_catalog.pg_attribute AS attribute
                    WHERE attribute.attrelid = c.oid
                      AND attribute.attnum > 0
                      AND NOT attribute.attisdropped
                ) AS column_count
            FROM pg_catalog.pg_class AS c
            INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema_name
              AND c.relname = @table_name
              AND c.relkind = 'r'
        )
        SELECT
            p.pubinsert,
            p.pubupdate,
            p.pubdelete,
            COALESCE((pg_catalog.to_jsonb(p) ->> 'pubtruncate')::boolean, FALSE),
            COALESCE((pg_catalog.to_jsonb(p) ->> 'pubviaroot')::boolean, FALSE),
            target.oid IS NOT NULL AND published.details IS NOT NULL AS includes_table,
            published.details ->> 'rowfilter' IS NOT NULL AS has_row_filter,
            CASE
                WHEN published.details ->> 'attnames' IS NULL THEN TRUE
                ELSE pg_catalog.jsonb_array_length(published.details -> 'attnames') = target.column_count
            END AS includes_all_columns
        FROM pg_catalog.pg_publication AS p
        LEFT JOIN target ON TRUE
        LEFT JOIN LATERAL
        (
            SELECT pg_catalog.to_jsonb(publication_table) AS details
            FROM pg_catalog.pg_publication_tables AS publication_table
            WHERE publication_table.pubname = p.pubname
              AND publication_table.schemaname = @schema_name
              AND publication_table.tablename = @table_name
            LIMIT 1
        ) AS published ON TRUE
        WHERE p.pubname = @publication_name
        """;
    const string TableInspectionSql = """
        SELECT
            c.relreplident::text,
            identity_index.relname,
            a.attname,
            a.atttypid::bigint,
            resolved_type.oid::bigint,
            a.atttypmod,
            c.relreplident = 'f'
                OR COALESCE(
                    EXISTS
                    (
                        SELECT 1
                        FROM pg_catalog.unnest(identity_definition.indkey::smallint[])
                            WITH ORDINALITY AS identity_attribute(attnum, ordinal)
                        WHERE identity_attribute.ordinal <= identity_definition.indnkeyatts
                          AND identity_attribute.attnum = a.attnum
                    ),
                    FALSE) AS is_replica_identity
        FROM pg_catalog.pg_class AS c
        INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        INNER JOIN pg_catalog.pg_attribute AS a
          ON a.attrelid = c.oid
         AND a.attnum > 0
         AND NOT a.attisdropped
        INNER JOIN LATERAL
        (
            WITH RECURSIVE base_type AS
            (
                SELECT catalog_type.oid, catalog_type.typbasetype
                FROM pg_catalog.pg_type AS catalog_type
                WHERE catalog_type.oid = a.atttypid

                UNION ALL

                SELECT catalog_type.oid, catalog_type.typbasetype
                FROM pg_catalog.pg_type AS catalog_type
                INNER JOIN base_type AS prior ON catalog_type.oid = prior.typbasetype
                WHERE prior.typbasetype <> 0
            )
            SELECT base_type.oid
            FROM base_type
            WHERE base_type.typbasetype = 0
            LIMIT 1
        ) AS resolved_type ON TRUE
        LEFT JOIN LATERAL
        (
            SELECT i.indexrelid, i.indkey, i.indnkeyatts
            FROM pg_catalog.pg_index AS i
            WHERE i.indrelid = c.oid
              AND ((c.relreplident = 'i' AND i.indisreplident)
                OR (c.relreplident = 'd' AND i.indisprimary))
            ORDER BY i.indexrelid
            LIMIT 1
        ) AS identity_definition ON TRUE
        LEFT JOIN pg_catalog.pg_class AS identity_index
          ON identity_index.oid = identity_definition.indexrelid
        WHERE n.nspname = @schema_name
          AND c.relname = @table_name
          AND c.relkind = 'r'
        ORDER BY a.attnum
        """;
    const string SlotInspectionSql = """
        SELECT
            s.plugin,
            s.slot_type,
            s.database,
            s.temporary,
            COALESCE((pg_catalog.to_jsonb(s) ->> 'two_phase')::boolean, FALSE),
            s.active,
            COALESCE(s.restart_lsn::text, '0/0'),
            COALESCE(s.confirmed_flush_lsn::text, '0/0'),
            COALESCE(pg_catalog.to_jsonb(s) ->> 'wal_status', 'unknown'),
            (pg_catalog.to_jsonb(s) ->> 'safe_wal_size')::bigint,
            (pg_catalog.to_jsonb(s) ->> 'inactive_since')::timestamptz,
            pg_catalog.to_jsonb(s) ->> 'invalidation_reason'
        FROM pg_catalog.pg_replication_slots AS s
        WHERE s.slot_name = @slot_name
        """;
    const string ConfirmedFlushInspectionSql = """
        SELECT COALESCE(s.confirmed_flush_lsn::text, '0/0')
        FROM pg_catalog.pg_replication_slots AS s
        WHERE s.slot_name = @slot_name
        """;

    readonly PostgresNpgsqlRuntimeBinding runtimeBinding;
    readonly PostgresLogicalReplicationBinding binding;
    readonly PostgresRelationQueryTableBinding table;
    PostgresLogicalReplicationDeployment? inspectedDeployment;

    internal PostgresNpgsqlLogicalReplicationProtocol(
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresLogicalReplicationBinding binding,
        PostgresRelationQueryTableBinding table)
    {
        this.runtimeBinding = Guard.RequireNotNull(runtimeBinding);
        this.binding = Guard.RequireNotNull(binding);
        this.table = Guard.RequireNotNull(table);
        if (!runtimeBinding.SupportsLogicalReplication)
        {
            throw new ArgumentException(
                "The PostgreSQL runtime binding must supply logical-replication connections.",
                nameof(runtimeBinding));
        }
    }

    public async ValueTask<PostgresLogicalReplicationDeployment> InspectAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var replicationConnection = await runtimeBinding
                .CreateLogicalReplicationConnectionAsync().ConfigureAwait(false);
            await replicationConnection.Open(cancellationToken).ConfigureAwait(false);
            await RequireCanonicalTextOutputAsync(
                replicationConnection,
                "inspect/text-output",
                cancellationToken).ConfigureAwait(false);
            var system = await replicationConnection.IdentifySystem(cancellationToken).ConfigureAwait(false);
            var databaseName = system.DbName;
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw Failure(
                    PostgresLogicalReplicationFailureKind.Terminal,
                    "inspect/identify-system/database-missing");
            }

            await using var catalogConnection = await runtimeBinding.DataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var publication = await InspectPublicationAsync(
                catalogConnection,
                cancellationToken).ConfigureAwait(false);
            RequireV1Publication(publication, "inspect/publication/incompatible");
            var tableInspection = await InspectTableAsync(
                catalogConnection,
                cancellationToken).ConfigureAwait(false);
            RequireTableBindingCoverage(
                tableInspection,
                "inspect/table/binding-columns");
            var slot = await InspectSlotAsync(
                catalogConnection,
                databaseName,
                cancellationToken).ConfigureAwait(false);
            var currentPosition = FromNpgsql(system.XLogPos);
            var deployment = new PostgresLogicalReplicationDeployment(
                SystemIdentifier: system.SystemId,
                Timeline: system.Timeline,
                DatabaseName: databaseName,
                PublicationName: binding.PublicationName,
                PublishesInserts: publication.PublishesInserts,
                PublishesUpdates: publication.PublishesUpdates,
                PublishesDeletes: publication.PublishesDeletes,
                PublishesTruncates: publication.PublishesTruncates,
                PublishesViaPartitionRoot: publication.PublishesViaPartitionRoot,
                IncludesTable: publication.IncludesTable,
                HasRowFilter: publication.HasRowFilter,
                IncludesAllTableColumns: publication.IncludesAllTableColumns,
                SchemaName: table.SchemaName,
                TableName: table.TableName,
                ReplicaIdentity: tableInspection.ReplicaIdentity,
                Columns: tableInspection.Columns,
                SlotName: binding.SlotName,
                OutputPlugin: slot.OutputPlugin,
                IsLogicalSlot: slot.IsLogicalSlot,
                IsTemporarySlot: slot.IsTemporarySlot,
                IsTwoPhaseSlot: slot.IsTwoPhaseSlot,
                IsActive: slot.IsActive,
                RestartPosition: slot.RestartPosition,
                ConfirmedFlushPosition: slot.ConfirmedFlushPosition,
                CurrentWalPosition: currentPosition,
                WalState: slot.WalState,
                SafeWalBytes: slot.SafeWalBytes,
                InactiveSinceUtc: slot.InactiveSinceUtc,
                InvalidationReason: slot.InvalidationReason);
            inspectedDeployment = deployment;
            return deployment;
        }
        catch (PostgresLogicalReplicationProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidCastException or OverflowException)
        {
            throw ProviderFailure("inspect/provider", exception);
        }
    }

    async ValueTask<PublicationInspection> InspectPublicationAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PublicationInspectionSql, connection);
        AddTextParameter(command, "publication_name", binding.PublicationName);
        AddTextParameter(command, "schema_name", table.SchemaName);
        AddTextParameter(command, "table_name", table.TableName);
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.PublicationMismatch,
                "inspect/publication/missing");
        }

        var result = new PublicationInspection(
            PublishesInserts: reader.GetBoolean(0),
            PublishesUpdates: reader.GetBoolean(1),
            PublishesDeletes: reader.GetBoolean(2),
            PublishesTruncates: reader.GetBoolean(3),
            PublishesViaPartitionRoot: reader.GetBoolean(4),
            IncludesTable: reader.GetBoolean(5),
            HasRowFilter: reader.GetBoolean(6),
            IncludesAllTableColumns: reader.GetBoolean(7));
        if (!result.IncludesTable)
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.PublicationMismatch,
                "inspect/publication/table-missing");
        }
        return result;
    }

    static async ValueTask RequireCanonicalTextOutputAsync(
        LogicalReplicationConnection connection,
        string evidenceReference,
        CancellationToken cancellationToken)
    {
        var dateStyle = await connection
            .Show("DateStyle", cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(dateStyle, "ISO", StringComparison.OrdinalIgnoreCase)
            && !dateStyle.StartsWith("ISO,", StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.Terminal,
                evidenceReference);
        }
    }

    async ValueTask<TableInspection> InspectTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(TableInspectionSql, connection);
        AddTextParameter(command, "schema_name", table.SchemaName);
        AddTextParameter(command, "table_name", table.TableName);
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);
        var columns = ImmutableArray.CreateBuilder<PostgresLogicalReplicationColumn>();
        PostgresLogicalReplicationReplicaIdentityBinding? replicaIdentity = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var currentIdentity = ParseReplicaIdentity(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
            replicaIdentity ??= currentIdentity;
            if (!Equals(replicaIdentity, currentIdentity))
            {
                throw Failure(
                    PostgresLogicalReplicationFailureKind.ProtocolViolation,
                    "inspect/table/identity-inconsistent");
            }
            var columnName = reader.GetString(2);
            var dataTypeId = checked((uint)reader.GetInt64(3));
            var baseDataTypeId = checked((uint)reader.GetInt64(4));
            columns.Add(new(
                Name: columnName,
                DataTypeId: dataTypeId,
                TypeModifier: reader.GetInt32(5),
                IsReplicaIdentity: reader.GetBoolean(6),
                DomainBaseDataTypeId: baseDataTypeId == dataTypeId ? null : baseDataTypeId));
        }
        if (replicaIdentity is null || columns.Count == 0)
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.Terminal,
                "inspect/table/missing");
        }
        if (!Equals(replicaIdentity, binding.ExpectedReplicaIdentity))
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch,
                "inspect/table/replica-identity-mismatch");
        }

        return new(replicaIdentity, columns.ToImmutable());
    }

    async ValueTask<SlotInspection> InspectSlotAsync(
        NpgsqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SlotInspectionSql, connection);
        AddTextParameter(command, "slot_name", binding.SlotName);
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.SlotUnavailable,
                "inspect/slot/missing");
        }

        var outputPlugin = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var isLogical = string.Equals(reader.GetString(1), "logical", StringComparison.Ordinal);
        var slotDatabase = reader.IsDBNull(2) ? null : reader.GetString(2);
        var isTemporary = reader.GetBoolean(3);
        var isTwoPhase = reader.GetBoolean(4);
        if (!string.Equals(outputPlugin, "pgoutput", StringComparison.Ordinal)
            || !isLogical
            || isTemporary
            || isTwoPhase
            || !string.Equals(slotDatabase, databaseName, StringComparison.Ordinal))
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.SlotUnavailable,
                "inspect/slot/incompatible");
        }

        return new(
            OutputPlugin: outputPlugin,
            IsLogicalSlot: isLogical,
            IsTemporarySlot: isTemporary,
            IsTwoPhaseSlot: isTwoPhase,
            IsActive: reader.GetBoolean(5),
            RestartPosition: ParsePosition(reader.GetString(6), "inspect/slot/restart-lsn"),
            ConfirmedFlushPosition: ParsePosition(reader.GetString(7), "inspect/slot/confirmed-flush-lsn"),
            WalState: ParseWalState(reader.GetString(8)),
            SafeWalBytes: reader.IsDBNull(9) ? null : reader.GetInt64(9),
            InactiveSinceUtc: reader.IsDBNull(10)
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc)),
            InvalidationReason: reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    public async ValueTask<PostgresLogicalReplicationReadBatch> ReadAsync(
        PostgresLogicalReplicationWalPosition afterPosition,
        PostgresLogicalReplicationWalPosition upperBoundary,
        int maximumTransactions,
        int preferredMaximumMutations,
        long preferredMaximumBytes,
        int maximumTransactionMutations,
        long maximumTransactionBytes,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (upperBoundary < afterPosition)
        {
            throw new ArgumentOutOfRangeException(
                nameof(upperBoundary),
                upperBoundary,
                "A logical-replication upper boundary cannot precede its exclusive starting position.");
        }
        if (maximumTransactions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTransactions),
                maximumTransactions,
                "A logical-replication read must admit at least one complete transaction.");
        }
        if (preferredMaximumMutations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferredMaximumMutations),
                preferredMaximumMutations,
                "A logical-replication page mutation budget must be positive.");
        }
        if (preferredMaximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferredMaximumBytes),
                preferredMaximumBytes,
                "A logical-replication page byte budget must be positive.");
        }
        if (maximumTransactionMutations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTransactionMutations),
                maximumTransactionMutations,
                "A logical-replication transaction must admit at least one mutation.");
        }
        if (maximumTransactionBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTransactionBytes),
                maximumTransactionBytes,
                "A logical-replication transaction byte bound must be positive.");
        }
        if (inactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inactivityTimeout),
                inactivityTimeout,
                "A logical-replication inactivity timeout must be positive.");
        }
        if (upperBoundary == afterPosition)
        {
            return new([], afterPosition, ReachedUpperBoundary: true);
        }

        var deployment = inspectedDeployment
            ?? await InspectAsync(cancellationToken).ConfigureAwait(false);
        if (deployment.RestartPosition.Value != 0 && afterPosition < deployment.RestartPosition)
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.PositionUnavailable,
                "read/after-position-before-restart");
        }

        try
        {
            await using var connection = await runtimeBinding
                .CreateLogicalReplicationConnectionAsync().ConfigureAwait(false);
            connection.WalReceiverStatusInterval = Timeout.InfiniteTimeSpan;
            await connection.Open(cancellationToken).ConfigureAwait(false);
            var options = CreatePgOutputOptions();
            var slot = new PgOutputReplicationSlot(binding.SlotName);
            using var inactivityCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            inactivityCancellation.CancelAfter(inactivityTimeout);
            await using var messages = connection.StartReplication(
                    slot,
                    options,
                    inactivityCancellation.Token,
                    walLocation: ToNpgsql(afterPosition))
                .GetAsyncEnumerator(inactivityCancellation.Token);
            var relations = new Dictionary<uint, RelationSnapshot>();
            var transactions = ImmutableArray.CreateBuilder<PostgresLogicalReplicationTransaction>(
                Math.Min(maximumTransactions, 64));
            var scannedThrough = afterPosition;
            var scannedTransactionCount = 0;
            long pageMutationCount = 0;
            long pageRetainedBytes = 0;
            TransactionBuffer? transaction = null;
            while (true)
            {
                PgOutputReplicationMessage message;
                try
                {
                    if (!await messages.MoveNextAsync().ConfigureAwait(false))
                    {
                        throw Failure(
                            PostgresLogicalReplicationFailureKind.ProtocolViolation,
                            "read/stream-ended");
                    }
                    message = messages.Current;
                    inactivityCancellation.CancelAfter(inactivityTimeout);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                    && inactivityCancellation.IsCancellationRequested)
                {
                    var receivedThrough = FromNpgsql(connection.LastReceivedLsn);
                    if (receivedThrough >= upperBoundary
                        && (transaction is null || transaction.FinalPosition > upperBoundary))
                    {
                        return new(
                            transactions.ToImmutable(),
                            upperBoundary,
                            ReachedUpperBoundary: true);
                    }
                    if (scannedThrough > afterPosition)
                    {
                        return new(
                            transactions.ToImmutable(),
                            scannedThrough,
                            ReachedUpperBoundary: false);
                    }
                    throw Failure(
                        PostgresLogicalReplicationFailureKind.Transient,
                        "read/inactivity-timeout",
                        isTransient: true);
                }

                switch (message)
                {
                    case BeginMessage begin:
                        if (transaction is not null)
                            throw ProtocolViolation("read/order/nested-begin");
                        transaction = TransactionBuffer.Start(
                            begin,
                            maximumTransactionMutations,
                            maximumTransactionBytes);
                        break;

                    case RelationMessage relation:
                        RequireNonStreamed(relation);
                        var snapshot = CopyRelation(relation);
                        snapshot = ValidateAndBindTargetRelation(snapshot, deployment);
                        relations[relation.RelationId] = snapshot;
                        break;

                    case InsertMessage insert:
                        RequireTransaction(transaction, "insert");
                        RequireNonStreamed(insert);
                        await AddInsertAsync(
                            transaction!,
                            insert,
                            RequireRelation(relations, insert.Relation.RelationId),
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case UpdateMessage update:
                        RequireTransaction(transaction, "update");
                        RequireNonStreamed(update);
                        await AddUpdateAsync(
                            transaction!,
                            update,
                            RequireRelation(relations, update.Relation.RelationId),
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case DeleteMessage delete:
                        RequireTransaction(transaction, "delete");
                        RequireNonStreamed(delete);
                        await AddDeleteAsync(
                            transaction!,
                            delete,
                            RequireRelation(relations, delete.Relation.RelationId),
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case TruncateMessage truncate:
                        RequireTransaction(transaction, "truncate");
                        RequireNonStreamed(truncate);
                        throw ProtocolViolation("read/truncate/unexpected");

                    case CommitMessage commit:
                        {
                            RequireTransaction(transaction, "commit");
                            var completed = transaction!.Commit(
                                commit,
                                maximumTransactionBytes);
                            transaction = null;
                            if (completed.EndPosition <= afterPosition)
                                break;
                            if (completed.EndPosition <= scannedThrough)
                                throw ProtocolViolation("read/order/non-monotonic-transaction-end");
                            if (completed.EndPosition > upperBoundary)
                            {
                                return new(
                                    transactions.ToImmutable(),
                                    upperBoundary,
                                    ReachedUpperBoundary: true);
                            }

                            scannedThrough = completed.EndPosition;
                            scannedTransactionCount++;
                            if (!completed.Mutations.IsDefaultOrEmpty)
                            {
                                transactions.Add(completed);
                                pageMutationCount += completed.Mutations.Length;
                                pageRetainedBytes = completed.RetainedBytes > long.MaxValue - pageRetainedBytes
                                    ? long.MaxValue
                                    : pageRetainedBytes + completed.RetainedBytes;
                            }
                            if (scannedThrough >= upperBoundary)
                            {
                                return new(
                                    transactions.ToImmutable(),
                                    scannedThrough,
                                    ReachedUpperBoundary: true);
                            }
                            if (pageMutationCount >= preferredMaximumMutations
                                || pageRetainedBytes >= preferredMaximumBytes)
                            {
                                return new(
                                    transactions.ToImmutable(),
                                    scannedThrough,
                                    ReachedUpperBoundary: false);
                            }
                            if (scannedTransactionCount >= maximumTransactions)
                            {
                                return new(
                                    transactions.ToImmutable(),
                                    scannedThrough,
                                    ReachedUpperBoundary: false);
                            }
                            break;
                        }

                    case OriginMessage:
                        RequireTransaction(transaction, message.GetType().Name);
                        break;

                    case TypeMessage type:
                        RequireTransaction(transaction, nameof(TypeMessage));
                        RequireNonStreamed(type);
                        break;

                    case LogicalDecodingMessage:
                        throw ProtocolViolation("read/message/unexpected-logical-message");

                    case StreamStartMessage:
                    case StreamStopMessage:
                    case StreamCommitMessage:
                    case StreamAbortMessage:
                        throw ProtocolViolation("read/message/streamed-transaction");

                    case BeginPrepareMessage:
                    case PrepareMessage:
                    case CommitPreparedMessage:
                    case RollbackPreparedMessage:
                    case StreamPrepareMessage:
                        throw ProtocolViolation("read/message/two-phase-transaction");

                    default:
                        throw ProtocolViolation("read/message/unsupported");
                }
            }
        }
        catch (PostgresLogicalReplicationProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidCastException
            or OverflowException)
        {
            throw ProviderFailure("read/provider", exception);
        }
    }

    public async ValueTask<PostgresLogicalReplicationFeedback> SettleAsync(
        PostgresLogicalReplicationWalPosition position,
        TimeSpan confirmationTimeout,
        TimeSpan confirmationPollInterval,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position.Value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "A logical-replication settlement position must be a nonzero transaction-end LSN.");
        }
        if (confirmationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confirmationTimeout),
                confirmationTimeout,
                "A settlement confirmation timeout must be positive.");
        }
        if (confirmationPollInterval <= TimeSpan.Zero
            || confirmationPollInterval > confirmationTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confirmationPollInterval),
                confirmationPollInterval,
                "A settlement confirmation poll interval must be positive and no greater than its timeout.");
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(confirmationTimeout);
        try
        {
            await using var catalogConnection = await runtimeBinding.DataSource
                .OpenConnectionAsync(timeoutCancellation.Token)
                .ConfigureAwait(false);
            var prior = await ReadConfirmedFlushPositionAsync(
                catalogConnection,
                timeoutCancellation.Token).ConfigureAwait(false);
            if (prior >= position)
            {
                return new(
                    PostgresLogicalReplicationFeedbackDisposition.AlreadyConfirmed,
                    prior,
                    prior);
            }

            await using var replicationConnection = await runtimeBinding
                .CreateLogicalReplicationConnectionAsync().ConfigureAwait(false);
            replicationConnection.WalReceiverStatusInterval = Timeout.InfiniteTimeSpan;
            await replicationConnection.Open(timeoutCancellation.Token).ConfigureAwait(false);
            var slot = new PgOutputReplicationSlot(binding.SlotName);
            using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellation.Token);
            await using var messages = replicationConnection.StartReplication(
                    slot,
                    CreatePgOutputOptions(),
                    streamCancellation.Token,
                    walLocation: ToNpgsql(prior))
                .GetAsyncEnumerator(streamCancellation.Token);
            Task<bool>? pendingMove = null;
            while (FromNpgsql(replicationConnection.LastReceivedLsn) < position)
            {
                pendingMove ??= messages.MoveNextAsync().AsTask();
                var delay = Task.Delay(
                    confirmationPollInterval,
                    timeoutCancellation.Token);
                var completed = await Task.WhenAny(pendingMove, delay).ConfigureAwait(false);
                if (completed != pendingMove)
                    continue;
                if (!await pendingMove.ConfigureAwait(false))
                {
                    throw Failure(
                        PostgresLogicalReplicationFailureKind.SettlementUnconfirmed,
                        "settle/stream-ended",
                        isTransient: true);
                }
                await ConsumeSettlementMessageAsync(
                    messages.Current,
                    timeoutCancellation.Token).ConfigureAwait(false);
                pendingMove = null;
            }

            replicationConnection.SetReplicationStatus(ToNpgsql(position));
            await replicationConnection
                .SendStatusUpdate(timeoutCancellation.Token)
                .ConfigureAwait(false);
            streamCancellation.Cancel();
            if (pendingMove is not null)
            {
                try
                {
                    if (await pendingMove.ConfigureAwait(false))
                    {
                        await ConsumeSettlementMessageAsync(
                            messages.Current,
                            timeoutCancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested)
                {
                }
            }

            while (true)
            {
                var confirmed = await ReadConfirmedFlushPositionAsync(
                    catalogConnection,
                    timeoutCancellation.Token).ConfigureAwait(false);
                if (confirmed >= position)
                {
                    var exactConfirmation = confirmed == position;
                    return new(
                        exactConfirmation
                            ? PostgresLogicalReplicationFeedbackDisposition.Confirmed
                            : PostgresLogicalReplicationFeedbackDisposition.AlreadyConfirmed,
                        exactConfirmation ? prior : confirmed,
                        confirmed);
                }
                await Task.Delay(
                    confirmationPollInterval,
                    timeoutCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (PostgresLogicalReplicationProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.SettlementUnconfirmed,
                "settle/confirmation-timeout",
                isTransient: true,
                exception);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidCastException
            or OverflowException)
        {
            throw ProviderFailure("settle/provider", exception);
        }
    }

    public async ValueTask<IPostgresLogicalReplicationSnapshotExport> CreateSnapshotExportAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogicalReplicationConnection? connection = null;
        try
        {
            await using (var catalogConnection = await runtimeBinding.DataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                var publication = await InspectPublicationAsync(
                    catalogConnection,
                    cancellationToken).ConfigureAwait(false);
                RequireV1Publication(publication, "snapshot/preflight/publication");
                var tableInspection = await InspectTableAsync(
                    catalogConnection,
                    cancellationToken).ConfigureAwait(false);
                RequireTableBindingCoverage(
                    tableInspection,
                    "snapshot/preflight/table-binding");
            }

            connection = await runtimeBinding
                .CreateLogicalReplicationConnectionAsync().ConfigureAwait(false);
            await connection.Open(cancellationToken).ConfigureAwait(false);
            await RequireCanonicalTextOutputAsync(
                connection,
                "snapshot/preflight/text-output",
                cancellationToken).ConfigureAwait(false);
            var slot = await connection.CreatePgOutputReplicationSlot(
                slotName: binding.SlotName,
                temporarySlot: false,
                slotSnapshotInitMode: LogicalSlotSnapshotInitMode.Export,
                twoPhase: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(slot.SnapshotName))
            {
                throw Failure(
                    PostgresLogicalReplicationFailureKind.ProtocolViolation,
                    "snapshot/export/name-missing");
            }

            var export = new SnapshotExport(
                connection,
                runtimeBinding.DataSource,
                slot.SnapshotName,
                FromNpgsql(slot.ConsistentPoint));
            connection = null;
            return export;
        }
        catch (PostgresLogicalReplicationProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidCastException
            or OverflowException)
        {
            throw ProviderFailure("snapshot/export/provider", exception);
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    async ValueTask<PostgresLogicalReplicationWalPosition> ReadConfirmedFlushPositionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ConfirmedFlushInspectionSql, connection);
        AddTextParameter(command, "slot_name", binding.SlotName);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string position)
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.SlotUnavailable,
                "settle/slot-missing");
        }
        return ParsePosition(position, "settle/confirmed-flush-lsn");
    }

    static async ValueTask ConsumeSettlementMessageAsync(
        PgOutputReplicationMessage message,
        CancellationToken cancellationToken)
    {
        switch (message)
        {
            case InsertMessage insert:
                await ConsumeTuplePayloadAsync(
                    insert.NewRow,
                    cancellationToken).ConfigureAwait(false);
                break;
            case DefaultUpdateMessage update:
                await ConsumeTuplePayloadAsync(
                    update.NewRow,
                    cancellationToken).ConfigureAwait(false);
                break;
            case FullUpdateMessage update:
                await ConsumeTuplePayloadAsync(
                    update.OldRow,
                    cancellationToken).ConfigureAwait(false);
                await ConsumeTuplePayloadAsync(
                    update.NewRow,
                    cancellationToken).ConfigureAwait(false);
                break;
            case IndexUpdateMessage update:
                await ConsumeTuplePayloadAsync(
                    update.Key,
                    cancellationToken).ConfigureAwait(false);
                await ConsumeTuplePayloadAsync(
                    update.NewRow,
                    cancellationToken).ConfigureAwait(false);
                break;
            case FullDeleteMessage delete:
                await ConsumeTuplePayloadAsync(
                    delete.OldRow,
                    cancellationToken).ConfigureAwait(false);
                break;
            case KeyDeleteMessage delete:
                await ConsumeTuplePayloadAsync(
                    delete.Key,
                    cancellationToken).ConfigureAwait(false);
                break;
            case TruncateMessage:
                throw ProtocolViolation("settle/truncate/unexpected");
            case LogicalDecodingMessage:
                throw ProtocolViolation("settle/message/unexpected-logical-message");
            case StreamStartMessage:
            case StreamStopMessage:
            case StreamCommitMessage:
            case StreamAbortMessage:
                throw ProtocolViolation("settle/message/streamed-transaction");
            case BeginPrepareMessage:
            case PrepareMessage:
            case CommitPreparedMessage:
            case RollbackPreparedMessage:
            case StreamPrepareMessage:
                throw ProtocolViolation("settle/message/two-phase-transaction");
        }
    }

    static async ValueTask ConsumeTuplePayloadAsync(
        ReplicationTuple tuple,
        CancellationToken cancellationToken)
    {
        await foreach (var value in tuple.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (value.Kind)
            {
                case TupleDataKind.Null:
                case TupleDataKind.UnchangedToastedValue:
                    break;
                case TupleDataKind.TextValue:
                    await DrainValueAsync(value, cancellationToken).ConfigureAwait(false);
                    break;
                case TupleDataKind.BinaryValue:
                    throw ProtocolViolation("settle/tuple/unexpected-binary-value");
                default:
                    throw ProtocolViolation("settle/tuple/unsupported-value-kind");
            }
        }
    }

    static void RequireV1Publication(
        PublicationInspection publication,
        string evidenceReference)
    {
        if (!publication.PublishesInserts
            || !publication.PublishesUpdates
            || !publication.PublishesDeletes
            || publication.PublishesTruncates
            || publication.PublishesViaPartitionRoot
            || !publication.IncludesTable
            || publication.HasRowFilter
            || !publication.IncludesAllTableColumns)
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.PublicationMismatch,
                evidenceReference);
        }
    }

    void RequireTableBindingCoverage(
        TableInspection inspection,
        string evidenceReference)
    {
        if (table.Identity is null)
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch,
                evidenceReference);
        }
        if (!inspection.ReplicaIdentity.ProvidesCompleteBeforeImage
            && PostgresRelationQueryScalarCatalog.HasProjectedPayloadThatMayUseUnchangedToast(table))
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch,
                string.Concat(evidenceReference, "/unchanged-toast"));
        }

        Dictionary<string, PostgresRelationQueryScalarType> requiredColumns = new(
            StringComparer.Ordinal);
        AddRequiredColumn(
            requiredColumns,
            table.Identity.ColumnName,
            table.Identity.ScalarType,
            evidenceReference);
        foreach (var field in table.Fields)
        {
            AddRequiredColumn(
                requiredColumns,
                field.ColumnName,
                field.ScalarType,
                evidenceReference);
        }
        foreach (var reference in table.RelationshipReferences)
        {
            AddRequiredColumn(
                requiredColumns,
                reference.ColumnName,
                reference.ScalarType,
                evidenceReference);
        }

        var identityColumnObserved = false;
        foreach (var column in inspection.Columns)
        {
            if (requiredColumns.Remove(column.Name, out var requiredScalarType)
                && !PostgresRelationQueryScalarCatalog.AcceptsPostgresType(
                    requiredScalarType,
                    column.EffectiveDataTypeId))
            {
                throw Failure(
                    PostgresLogicalReplicationFailureKind.Terminal,
                    string.Concat(evidenceReference, "/column-type"));
            }
            if (string.Equals(
                    column.Name,
                    table.Identity.ColumnName,
                    StringComparison.Ordinal)
                && column.IsReplicaIdentity)
            {
                identityColumnObserved = true;
            }
            if (inspection.ReplicaIdentity.ProvidesCompleteBeforeImage
                && !column.IsReplicaIdentity)
            {
                throw Failure(
                    PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch,
                    evidenceReference);
            }
        }
        if (requiredColumns.Count != 0 || !identityColumnObserved)
        {
            throw Failure(
                PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch,
                evidenceReference);
        }
    }

    static void AddRequiredColumn(
        IDictionary<string, PostgresRelationQueryScalarType> columns,
        string columnName,
        PostgresRelationQueryScalarType scalarType,
        string evidenceReference)
    {
        if (columns.TryGetValue(columnName, out var existingScalarType))
        {
            if (existingScalarType != scalarType)
            {
                throw Failure(
                    PostgresLogicalReplicationFailureKind.Terminal,
                    string.Concat(evidenceReference, "/binding-type-conflict"));
            }
            return;
        }
        columns.Add(columnName, scalarType);
    }

    PgOutputReplicationOptions CreatePgOutputOptions() => new(
        publicationName: binding.PublicationName,
        protocolVersion: PgOutputProtocolVersion.V1,
        binary: false,
        streamingMode: PgOutputStreamingMode.Off,
        messages: false,
        twoPhase: null);

    async ValueTask AddInsertAsync(
        TransactionBuffer transaction,
        InsertMessage message,
        RelationSnapshot relation,
        CancellationToken cancellationToken)
    {
        transaction.BeginMutation(retain: relation.IsTarget);
        if (!relation.IsTarget)
        {
            await ConsumeRowAsync(
                message.NewRow,
                relation,
                transaction,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        var newRow = await CopyRowAsync(
            message.NewRow,
            relation,
            transaction,
            cancellationToken).ConfigureAwait(false);
        transaction.Add(
            PostgresLogicalReplicationMutationKind.Insert,
            MapReplicaIdentity(relation.ReplicaIdentity),
            oldRow: null,
            newRow);
    }

    async ValueTask AddUpdateAsync(
        TransactionBuffer transaction,
        UpdateMessage message,
        RelationSnapshot relation,
        CancellationToken cancellationToken)
    {
        transaction.BeginMutation(retain: relation.IsTarget);
        if (!relation.IsTarget)
        {
            switch (message)
            {
                case DefaultUpdateMessage defaultUpdate:
                    await ConsumeRowAsync(
                        defaultUpdate.NewRow,
                        relation,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case FullUpdateMessage fullUpdate:
                    await ConsumeRowAsync(
                        fullUpdate.OldRow,
                        relation,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    await ConsumeRowAsync(
                        fullUpdate.NewRow,
                        relation,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case IndexUpdateMessage indexUpdate:
                    await ConsumeRowAsync(
                        indexUpdate.Key,
                        relation,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    await ConsumeRowAsync(
                        indexUpdate.NewRow,
                        relation,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw ProtocolViolation("read/update/unsupported-tuple-kind");
            }
            return;
        }
        PostgresLogicalReplicationRow? oldRow;
        PostgresLogicalReplicationRow newRow;
        switch (message)
        {
            case DefaultUpdateMessage defaultUpdate:
                oldRow = null;
                newRow = await CopyRowAsync(
                    defaultUpdate.NewRow,
                    relation,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                break;
            case FullUpdateMessage fullUpdate:
                oldRow = await CopyRowAsync(
                    fullUpdate.OldRow,
                    relation,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                newRow = await CopyRowAsync(
                    fullUpdate.NewRow,
                    relation,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                break;
            case IndexUpdateMessage indexUpdate:
                oldRow = await CopyRowAsync(
                    indexUpdate.Key,
                    relation,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                newRow = await CopyRowAsync(
                    indexUpdate.NewRow,
                    relation,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw ProtocolViolation("read/update/unsupported-tuple-kind");
        }
        transaction.Add(
            PostgresLogicalReplicationMutationKind.Update,
            MapReplicaIdentity(relation.ReplicaIdentity),
            oldRow,
            newRow);
    }

    async ValueTask AddDeleteAsync(
        TransactionBuffer transaction,
        DeleteMessage message,
        RelationSnapshot relation,
        CancellationToken cancellationToken)
    {
        transaction.BeginMutation(retain: relation.IsTarget);
        if (!relation.IsTarget)
        {
            switch (message)
            {
                case FullDeleteMessage fullDelete:
                    await ConsumeRowAsync(
                        fullDelete.OldRow,
                        relation,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case KeyDeleteMessage keyDelete:
                    await ConsumeRowAsync(
                        keyDelete.Key,
                        relation,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw ProtocolViolation("read/delete/unsupported-tuple-kind");
            }
            return;
        }
        var oldRow = message switch
        {
            FullDeleteMessage fullDelete => await CopyRowAsync(
                fullDelete.OldRow,
                relation,
                transaction,
                cancellationToken).ConfigureAwait(false),
            KeyDeleteMessage keyDelete => await CopyRowAsync(
                keyDelete.Key,
                relation,
                transaction,
                cancellationToken).ConfigureAwait(false),
            _ => throw ProtocolViolation("read/delete/unsupported-tuple-kind")
        };
        transaction.Add(
            PostgresLogicalReplicationMutationKind.Delete,
            MapReplicaIdentity(relation.ReplicaIdentity),
            oldRow,
            newRow: null);
    }

    static async ValueTask<PostgresLogicalReplicationRow> CopyRowAsync(
        ReplicationTuple tuple,
        RelationSnapshot relation,
        TransactionBuffer transaction,
        CancellationToken cancellationToken)
    {
        if (tuple.NumColumns != relation.Columns.Length)
            throw ProtocolViolation("read/tuple/column-count");
        transaction.Reserve(encodedBytes: 3, retain: true);
        var cells = ImmutableArray.CreateBuilder<PostgresLogicalReplicationCell>(
            relation.RequiredColumnCount);
        var ordinal = 0;
        await foreach (var value in tuple.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (ordinal >= relation.Columns.Length)
                throw ProtocolViolation("read/tuple/column-overrun");
            var column = relation.Columns[ordinal];
            var encodedBytes = GetEncodedCellBytes(value);
            if (column.RequiredScalarType is not { } scalarType)
            {
                transaction.Reserve(encodedBytes, retain: false);
                if (value.Kind == TupleDataKind.TextValue)
                    await DrainValueAsync(value, cancellationToken).ConfigureAwait(false);
                ordinal++;
                continue;
            }
            transaction.Reserve(encodedBytes, retain: true);
            var cellKind = value.Kind switch
            {
                TupleDataKind.Null => PostgresLogicalReplicationCellKind.Null,
                TupleDataKind.UnchangedToastedValue => PostgresLogicalReplicationCellKind.UnchangedToast,
                TupleDataKind.TextValue => PostgresLogicalReplicationCellKind.Value,
                _ => throw ProtocolViolation("read/tuple/unsupported-value-kind")
            };
            object? copiedValue = null;
            if (cellKind == PostgresLogicalReplicationCellKind.Value)
            {
                try
                {
                    copiedValue = await PostgresRelationQueryScalarCatalog.ReadPgOutputAsync(
                        value,
                        scalarType,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidCastException
                    or FormatException
                    or InvalidOperationException
                    or OverflowException
                    or ArgumentException)
                {
                    throw Failure(
                        PostgresLogicalReplicationFailureKind.ProtocolViolation,
                        string.Concat(
                            "read/tuple/noncanonical-",
                            scalarType.ToString().ToLowerInvariant(),
                            "-value/",
                            exception.GetType().Name),
                        innerException: exception);
                }
            }
            cells.Add(new(
                column.Physical.Name,
                cellKind,
                copiedValue,
                encodedBytes));
            ordinal++;
        }
        if (ordinal != relation.Columns.Length)
            throw ProtocolViolation("read/tuple/column-underrun");
        return new(cells.MoveToImmutable());
    }

    static async ValueTask ConsumeRowAsync(
        ReplicationTuple tuple,
        RelationSnapshot relation,
        TransactionBuffer transaction,
        CancellationToken cancellationToken)
    {
        if (tuple.NumColumns != relation.Columns.Length)
            throw ProtocolViolation("read/tuple/column-count");
        transaction.Reserve(encodedBytes: 3, retain: false);
        var ordinal = 0;
        await foreach (var value in tuple.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (ordinal >= relation.Columns.Length)
                throw ProtocolViolation("read/tuple/column-overrun");
            var encodedBytes = GetEncodedCellBytes(value);
            transaction.Reserve(encodedBytes, retain: false);
            if (value.Kind == TupleDataKind.TextValue)
                await DrainValueAsync(value, cancellationToken).ConfigureAwait(false);
            ordinal++;
        }
        if (ordinal != relation.Columns.Length)
            throw ProtocolViolation("read/tuple/column-underrun");
    }

    static int GetEncodedCellBytes(ReplicationValue value) => value.Kind switch
    {
        TupleDataKind.Null or TupleDataKind.UnchangedToastedValue => 1,
        TupleDataKind.TextValue when value.Length is >= 0 and <= int.MaxValue - 5 => 5 + value.Length,
        TupleDataKind.TextValue => throw Failure(
            PostgresLogicalReplicationFailureKind.TransactionLimitExceeded,
            "read/transaction/byte-limit"),
        TupleDataKind.BinaryValue => throw ProtocolViolation("read/tuple/unexpected-binary-value"),
        _ => throw ProtocolViolation("read/tuple/unsupported-value-kind")
    };

    static async ValueTask DrainValueAsync(
        ReplicationValue value,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Clamp(value.Length, 1, 4_096));
        try
        {
            await using var stream = value.GetStream();
            var consumed = 0;
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                consumed = checked(consumed + read);
                if (consumed > value.Length)
                    throw ProtocolViolation("read/tuple/value-length-overrun");
            }
            if (consumed != value.Length)
                throw ProtocolViolation("read/tuple/value-length-underrun");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    RelationSnapshot CopyRelation(RelationMessage message)
    {
        var columns = ImmutableArray.CreateBuilder<RelationColumnSnapshot>(
            message.Columns.Count);
        foreach (var column in message.Columns)
        {
            columns.Add(new(
                Physical: new(
                    Name: column.ColumnName,
                    DataTypeId: column.DataTypeId,
                    TypeModifier: column.TypeModifier,
                    IsReplicaIdentity:
                        (column.Flags & RelationMessage.Column.ColumnFlags.PartOfKey) != 0),
                RequiredScalarType: null));
        }
        return new(
            message.RelationId,
            message.Namespace,
            message.RelationName,
            message.ReplicaIdentity,
            columns.MoveToImmutable(),
            IsTargetRelation(message),
            RequiredColumnCount: 0);
    }

    RelationSnapshot ValidateAndBindTargetRelation(
        RelationSnapshot relation,
        PostgresLogicalReplicationDeployment deployment)
    {
        if (!relation.IsTarget)
            return relation;
        if (MapReplicaIdentity(relation.ReplicaIdentity) != deployment.ReplicaIdentity.Kind
            || relation.Columns.Length != deployment.Columns.Length)
        {
            throw ProtocolViolation("read/relation/schema-drift");
        }
        var boundColumns = ImmutableArray.CreateBuilder<RelationColumnSnapshot>(
            relation.Columns.Length);
        var requiredColumnCount = 0;
        for (var index = 0; index < relation.Columns.Length; index++)
        {
            var observed = relation.Columns[index].Physical;
            var expected = deployment.Columns[index];
            if (!string.Equals(observed.Name, expected.Name, StringComparison.Ordinal)
                || observed.DataTypeId != expected.DataTypeId
                || observed.TypeModifier != expected.TypeModifier
                || observed.IsReplicaIdentity != expected.IsReplicaIdentity)
            {
                throw ProtocolViolation("read/relation/schema-drift");
            }
            PostgresRelationQueryScalarType? requiredScalarType = null;
            if (TryResolveRequiredScalarType(expected.Name, out var resolvedScalarType))
            {
                if (!PostgresRelationQueryScalarCatalog.AcceptsPostgresType(
                    resolvedScalarType,
                    expected.EffectiveDataTypeId))
                {
                    throw ProtocolViolation("read/relation/scalar-type-drift");
                }
                requiredScalarType = resolvedScalarType;
                requiredColumnCount++;
            }
            boundColumns.Add(new(expected, requiredScalarType));
        }
        return relation with
        {
            Columns = boundColumns.MoveToImmutable(),
            RequiredColumnCount = requiredColumnCount
        };
    }

    bool TryResolveRequiredScalarType(
        string columnName,
        out PostgresRelationQueryScalarType scalarType)
    {
        scalarType = default;
        var found = false;
        if (table.Identity is { } identity
            && string.Equals(identity.ColumnName, columnName, StringComparison.Ordinal))
        {
            scalarType = identity.ScalarType;
            found = true;
        }
        foreach (var field in table.Fields)
        {
            if (!string.Equals(field.ColumnName, columnName, StringComparison.Ordinal))
                continue;
            if (found && scalarType != field.ScalarType)
                throw ProtocolViolation("read/relation/binding-type-conflict");
            scalarType = field.ScalarType;
            found = true;
        }
        foreach (var reference in table.RelationshipReferences)
        {
            if (!string.Equals(reference.ColumnName, columnName, StringComparison.Ordinal))
                continue;
            if (found && scalarType != reference.ScalarType)
                throw ProtocolViolation("read/relation/binding-type-conflict");
            scalarType = reference.ScalarType;
            found = true;
        }
        return found;
    }

    bool IsTargetRelation(RelationMessage relation) =>
        string.Equals(relation.Namespace, table.SchemaName, StringComparison.Ordinal)
        && string.Equals(relation.RelationName, table.TableName, StringComparison.Ordinal);

    static RelationSnapshot RequireRelation(
        IReadOnlyDictionary<uint, RelationSnapshot> relations,
        uint relationId) =>
        relations.TryGetValue(relationId, out var relation)
            ? relation
            : throw ProtocolViolation("read/relation/missing");

    static void RequireTransaction(TransactionBuffer? transaction, string messageKind)
    {
        if (transaction is null)
            throw ProtocolViolation(string.Concat("read/order/", messageKind, "-outside-transaction"));
    }

    static void RequireNonStreamed(TransactionalMessage message)
    {
        if (message.TransactionXid.HasValue)
            throw ProtocolViolation("read/message/streamed-transaction-xid");
    }

    static PostgresLogicalReplicationReplicaIdentityKind MapReplicaIdentity(
        RelationMessage.ReplicaIdentitySetting replicaIdentity) => replicaIdentity switch
        {
            RelationMessage.ReplicaIdentitySetting.Default =>
                PostgresLogicalReplicationReplicaIdentityKind.Default,
            RelationMessage.ReplicaIdentitySetting.AllColumns =>
                PostgresLogicalReplicationReplicaIdentityKind.Full,
            RelationMessage.ReplicaIdentitySetting.IndexWithIndIsReplIdent =>
                PostgresLogicalReplicationReplicaIdentityKind.Index,
            _ => throw ProtocolViolation("read/relation/replica-identity-unsupported")
        };

    static PostgresLogicalReplicationProtocolException ProtocolViolation(string evidenceReference) =>
        Failure(
            PostgresLogicalReplicationFailureKind.ProtocolViolation,
            evidenceReference);

    static PostgresLogicalReplicationReplicaIdentityBinding ParseReplicaIdentity(
        string value,
        string? indexName) => value switch
        {
            "d" => new(PostgresLogicalReplicationReplicaIdentityKind.Default),
            "f" => new(PostgresLogicalReplicationReplicaIdentityKind.Full),
            "i" when indexName is not null => new(
                PostgresLogicalReplicationReplicaIdentityKind.Index,
                indexName),
            _ => throw Failure(
                PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch,
                "inspect/table/replica-identity-unsupported")
        };

    static PostgresLogicalReplicationWalState ParseWalState(string value) => value switch
    {
        "reserved" => PostgresLogicalReplicationWalState.Reserved,
        "extended" => PostgresLogicalReplicationWalState.Extended,
        "unreserved" => PostgresLogicalReplicationWalState.Unreserved,
        "lost" => PostgresLogicalReplicationWalState.Lost,
        _ => PostgresLogicalReplicationWalState.Unknown
    };

    static void AddTextParameter(NpgsqlCommand command, string name, string value) =>
        command.Parameters.AddWithValue(name, NpgsqlDbType.Text, value);

    static PostgresLogicalReplicationWalPosition ParsePosition(
        string value,
        string evidenceReference) =>
        PostgresLogicalReplicationWalPosition.TryParse(value, out var position)
            ? position
            : throw Failure(
                PostgresLogicalReplicationFailureKind.ProtocolViolation,
                evidenceReference);

    static PostgresLogicalReplicationWalPosition FromNpgsql(NpgsqlLogSequenceNumber position) =>
        new((ulong)position);

    static NpgsqlLogSequenceNumber ToNpgsql(PostgresLogicalReplicationWalPosition position) =>
        new(position.Value);

    static PostgresLogicalReplicationProtocolException ProviderFailure(
        string evidenceReference,
        Exception exception)
    {
        var isTransient = exception is NpgsqlException { IsTransient: true };
        return new(
            isTransient
                ? PostgresLogicalReplicationFailureKind.Transient
                : PostgresLogicalReplicationFailureKind.Terminal,
            isTransient,
            string.Concat(EvidencePrefix, "/", evidenceReference),
            exception);
    }

    static PostgresLogicalReplicationProtocolException Failure(
        PostgresLogicalReplicationFailureKind failureKind,
        string evidenceReference,
        bool isTransient = false,
        Exception? innerException = null) => new(
            failureKind,
            isTransient,
            string.Concat(EvidencePrefix, "/", evidenceReference),
            innerException);

    sealed record RelationColumnSnapshot(
        PostgresLogicalReplicationColumn Physical,
        PostgresRelationQueryScalarType? RequiredScalarType);

    sealed record RelationSnapshot(
        uint RelationId,
        string SchemaName,
        string TableName,
        RelationMessage.ReplicaIdentitySetting ReplicaIdentity,
        ImmutableArray<RelationColumnSnapshot> Columns,
        bool IsTarget,
        int RequiredColumnCount);

    sealed class TransactionBuffer
    {
        const int BeginMessageBytes = 21;
        const int CommitMessageBytes = 26;
        readonly int maximumMutations;
        readonly long maximumBytes;
        readonly ImmutableArray<PostgresLogicalReplicationMutation>.Builder mutations;
        int rawMutationCount;
        long rawBytes;

        TransactionBuffer(
            uint transactionId,
            PostgresLogicalReplicationWalPosition finalPosition,
            DateTimeOffset committedAtUtc,
            int maximumMutations,
            long maximumBytes)
        {
            TransactionId = transactionId;
            FinalPosition = finalPosition;
            CommittedAtUtc = committedAtUtc;
            this.maximumMutations = maximumMutations;
            this.maximumBytes = maximumBytes;
            mutations = ImmutableArray.CreateBuilder<PostgresLogicalReplicationMutation>(
                Math.Min(maximumMutations, 64));
            Reserve(encodedBytes: BeginMessageBytes, retain: false);
        }

        internal uint TransactionId { get; }

        internal PostgresLogicalReplicationWalPosition FinalPosition { get; }

        internal DateTimeOffset CommittedAtUtc { get; }

        internal long RetainedBytes { get; private set; }

        internal static TransactionBuffer Start(
            BeginMessage message,
            int maximumMutations,
            long maximumBytes) => new(
                message.TransactionXid,
                FromNpgsql(message.TransactionFinalLsn),
                RequireUtc(message.TransactionCommitTimestamp, "read/begin/commit-time"),
                maximumMutations,
                maximumBytes);

        internal void BeginMutation(bool retain)
        {
            if (rawMutationCount >= maximumMutations)
            {
                throw Failure(
                    PostgresLogicalReplicationFailureKind.TransactionLimitExceeded,
                    "read/transaction/mutation-limit");
            }
            rawMutationCount++;
            if (retain && mutations.Count == 0)
                RetainedBytes += BeginMessageBytes;
            Reserve(encodedBytes: 5, retain: retain);
        }

        internal void Reserve(long encodedBytes, bool retain)
        {
            if (encodedBytes < 0 || encodedBytes > maximumBytes - rawBytes)
            {
                throw Failure(
                    PostgresLogicalReplicationFailureKind.TransactionLimitExceeded,
                    "read/transaction/byte-limit");
            }
            rawBytes += encodedBytes;
            if (retain)
                RetainedBytes += encodedBytes;
        }

        internal void Add(
            PostgresLogicalReplicationMutationKind kind,
            PostgresLogicalReplicationReplicaIdentityKind replicaIdentity,
            PostgresLogicalReplicationRow? oldRow,
            PostgresLogicalReplicationRow? newRow) =>
            mutations.Add(new(
                Ordinal: mutations.Count,
                Kind: kind,
                ReplicaIdentity: replicaIdentity,
                OldRow: oldRow,
                NewRow: newRow));

        internal PostgresLogicalReplicationTransaction Commit(
            CommitMessage message,
            long requestedMaximumBytes)
        {
            if (requestedMaximumBytes != maximumBytes)
                throw ProtocolViolation("read/transaction/limit-drift");
            var commitPosition = FromNpgsql(message.CommitLsn);
            var endPosition = FromNpgsql(message.TransactionEndLsn);
            var committedAtUtc = RequireUtc(
                message.TransactionCommitTimestamp,
                "read/commit/commit-time");
            if (message.Flags != CommitMessage.CommitFlags.None
                || commitPosition != FinalPosition
                || endPosition < commitPosition
                || committedAtUtc != CommittedAtUtc)
            {
                throw ProtocolViolation("read/commit/inconsistent");
            }
            Reserve(encodedBytes: CommitMessageBytes, retain: mutations.Count > 0);
            return new(
                TransactionId,
                FinalPosition,
                commitPosition,
                endPosition,
                committedAtUtc,
                mutations.ToImmutable(),
                RetainedBytes);
        }

        static DateTimeOffset RequireUtc(DateTime value, string evidenceReference)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw ProtocolViolation(evidenceReference);
            return new(value);
        }
    }

    sealed class SnapshotExport : IPostgresLogicalReplicationSnapshotExport
    {
        readonly SemaphoreSlim gate = new(initialCount: 1, maxCount: 1);
        readonly NpgsqlDataSource dataSource;
        LogicalReplicationConnection? connection;
        bool imported;

        internal SnapshotExport(
            LogicalReplicationConnection connection,
            NpgsqlDataSource dataSource,
            string snapshotName,
            PostgresLogicalReplicationWalPosition consistentPosition)
        {
            this.connection = connection;
            this.dataSource = dataSource;
            SnapshotName = snapshotName;
            ConsistentPosition = consistentPosition;
        }

        public string SnapshotName { get; }

        public PostgresLogicalReplicationWalPosition ConsistentPosition { get; }

        public async ValueTask<IPostgresLogicalReplicationSnapshotImport> ImportAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            NpgsqlConnection? importConnection = null;
            NpgsqlTransaction? transaction = null;
            try
            {
                if (connection is null)
                {
                    throw new ObjectDisposedException(
                        nameof(SnapshotExport),
                        "The PostgreSQL exported snapshot is no longer available.");
                }
                if (imported)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL exported snapshot has already been imported.");
                }

                importConnection = await dataSource
                    .OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                transaction = await importConnection.BeginTransactionAsync(
                    IsolationLevel.RepeatableRead,
                    cancellationToken).ConfigureAwait(false);
                await using (var command = importConnection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = CreateSnapshotImportSql(SnapshotName);
                    _ = await command
                        .ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                imported = true;
                await connection.DisposeAsync().ConfigureAwait(false);
                connection = null;
                var import = new SnapshotImport(importConnection, transaction);
                importConnection = null;
                transaction = null;
                return import;
            }
            catch (PostgresLogicalReplicationProtocolException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidCastException
                or OverflowException)
            {
                throw ProviderFailure("snapshot/import/provider", exception);
            }
            finally
            {
                if (transaction is not null)
                    await transaction.DisposeAsync().ConfigureAwait(false);
                if (importConnection is not null)
                    await importConnection.DisposeAsync().ConfigureAwait(false);
                gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    connection = null;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        static string CreateSnapshotImportSql(string snapshotName)
        {
            _ = PostgresSqlUtf8.RequireText(snapshotName, nameof(snapshotName));
            return string.Concat(
                "SET TRANSACTION SNAPSHOT '",
                snapshotName.Replace("'", "''", StringComparison.Ordinal),
                "'");
        }
    }

    sealed class SnapshotImport : IPostgresLogicalReplicationSnapshotImport
    {
        NpgsqlConnection? connection;
        NpgsqlTransaction? transaction;

        internal SnapshotImport(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
            ExecuteCommand = PostgresNpgsqlExecution.CreateTransactionExecutor(
                connection,
                transaction);
        }

        public PostgresNpgsqlCommandExecutor ExecuteCommand { get; }

        public async ValueTask DisposeAsync()
        {
            var ownedTransaction = Interlocked.Exchange(ref transaction, null);
            var ownedConnection = Interlocked.Exchange(ref connection, null);
            if (ownedTransaction is not null)
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            if (ownedConnection is not null)
                await ownedConnection.DisposeAsync().ConfigureAwait(false);
        }
    }

    readonly record struct PublicationInspection(
        bool PublishesInserts,
        bool PublishesUpdates,
        bool PublishesDeletes,
        bool PublishesTruncates,
        bool PublishesViaPartitionRoot,
        bool IncludesTable,
        bool HasRowFilter,
        bool IncludesAllTableColumns);

    readonly record struct TableInspection(
        PostgresLogicalReplicationReplicaIdentityBinding ReplicaIdentity,
        ImmutableArray<PostgresLogicalReplicationColumn> Columns);

    readonly record struct SlotInspection(
        string OutputPlugin,
        bool IsLogicalSlot,
        bool IsTemporarySlot,
        bool IsTwoPhaseSlot,
        bool IsActive,
        PostgresLogicalReplicationWalPosition RestartPosition,
        PostgresLogicalReplicationWalPosition ConfirmedFlushPosition,
        PostgresLogicalReplicationWalState WalState,
        long? SafeWalBytes,
        DateTimeOffset? InactiveSinceUtc,
        string? InvalidationReason);
}
