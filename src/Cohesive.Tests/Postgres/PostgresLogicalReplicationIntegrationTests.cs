using System.Text.Json.Serialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Npgsql;
using Npgsql.Replication;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresLogicalReplicationIntegrationTests
{
    const string ConnectionStringEnvironmentVariable =
        "COHESIVE_POSTGRES_LOGICAL_REPLICATION_TEST_CONNECTION_STRING";
    static readonly byte[] PositionAuthenticationKey = Convert.FromHexString(
        "60C402E42AB0A92EC28968938BFF38F26B7C013350F629A70CB08EF12DD3EB92");

    [PostgresLogicalReplicationFact]
    public async Task LocalPostgres_StreamsTransactionAlignedChangesAndSettlesOnlyAfterExplicitRequest()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "The PostgreSQL logical-replication integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var suffix = Guid.NewGuid().ToString("N");
        var schemaName = $"ari188_lr_{suffix}";
        const string tableName = "logical_items";
        var publicationName = $"ari188_lr_pub_{suffix}";
        var slotName = $"ari188_lr_slot_{suffix}";

        try
        {
            await RequireLogicalWalLevelAsync(dataSource, timeout.Token);
            await ProvisionAsync(
                dataSource,
                schemaName,
                tableName,
                publicationName,
                slotName,
                PostgresLogicalReplicationReplicaIdentityKind.Full,
                timeout.Token);
            var initiallyConfirmedPosition = await ReadConfirmedFlushPositionAsync(
                dataSource,
                slotName,
                timeout.Token);
            var fixture = CreateFixture(
                dataSource,
                connectionString,
                schemaName,
                tableName);
            var source = await CreateChangeSourceAsync(
                fixture,
                publicationName,
                slotName,
                PostgresLogicalReplicationReplicaIdentityKind.Full,
                timeout.Token);
            var initialHealth = await source.InspectHealthAsync(
                OperationContext.Create(cancellationToken: timeout.Token),
                source.Scope);
            Assert.Equal(source.Scope, initialHealth.Scope);
            Assert.True(
                initialHealth.State is PostgresLogicalReplicationHealthState.Healthy
                    or PostgresLogicalReplicationHealthState.Inactive,
                $"Unexpected initial logical-replication health '{initialHealth.State}'.");
            var startPosition = await source.CaptureCurrentPositionAsync(
                OperationContext.Create(cancellationToken: timeout.Token),
                source.Scope);

            await WriteChangesAsync(
                dataSource,
                schemaName,
                tableName,
                timeout.Token);

            var pages = await ReadThroughCatchUpAsync(
                source,
                startPosition,
                timeout.Token);
            var relevantPages = pages
                .Where(static page => !page.Deliveries.IsDefaultOrEmpty)
                .ToArray();

            Assert.Equal(3, relevantPages.Length);
            Assert.Equal(
                [MaterializationChangeKind.Create, MaterializationChangeKind.Update],
                relevantPages[0].Deliveries.Select(static delivery => delivery.Change.Kind));
            Assert.Equal(
                ["item-a", "item-a"],
                relevantPages[0].Deliveries.Select(static delivery => delivery.Change.SubjectIdentity));
            Assert.Equal(
                relevantPages[0].Deliveries[0].Change.Position,
                relevantPages[0].Deliveries[1].Change.Position);
            Assert.Null(relevantPages[0].Deliveries[0].Change.Before);
            Assert.Equal("Alpha", ReadName(relevantPages[0].Deliveries[0].Change.After));
            Assert.Equal("Alpha", ReadName(relevantPages[0].Deliveries[1].Change.Before));
            Assert.Equal("Beta", ReadName(relevantPages[0].Deliveries[1].Change.After));

            Assert.Equal(
                [MaterializationChangeKind.Delete, MaterializationChangeKind.Create],
                relevantPages[1].Deliveries.Select(static delivery => delivery.Change.Kind));
            Assert.Equal(
                ["item-a", "item-b"],
                relevantPages[1].Deliveries.Select(static delivery => delivery.Change.SubjectIdentity));
            Assert.Equal(
                relevantPages[1].Deliveries[0].Change.Position,
                relevantPages[1].Deliveries[1].Change.Position);
            Assert.Equal("Beta", ReadName(relevantPages[1].Deliveries[0].Change.Before));
            Assert.Null(relevantPages[1].Deliveries[0].Change.After);
            Assert.Null(relevantPages[1].Deliveries[1].Change.Before);
            Assert.Equal("Gamma", ReadName(relevantPages[1].Deliveries[1].Change.After));

            var finalDelete = Assert.Single(relevantPages[2].Deliveries);
            Assert.Equal(MaterializationChangeKind.Delete, finalDelete.Change.Kind);
            Assert.Equal("item-b", finalDelete.Change.SubjectIdentity);
            Assert.Equal("Gamma", ReadName(finalDelete.Change.Before));
            Assert.Null(finalDelete.Change.After);
            Assert.Equal(
                initiallyConfirmedPosition,
                await ReadConfirmedFlushPositionAsync(dataSource, slotName, timeout.Token));

            var finalPosition = pages[^1].ThroughPosition;
            MaterializationCheckpointId checkpoint =
                new("tests/postgres/logical-replication/checkpoint-1");
            var settlement = await source.SettleAsync(
                OperationContext.Create(cancellationToken: timeout.Token),
                new(
                    id: PostgresLogicalReplicationMaterializationChangeSource.CreateSettlementId(
                        checkpoint,
                        finalPosition),
                    checkpoint: checkpoint,
                    position: finalPosition,
                    requestedAtUtc: DateTimeOffset.UtcNow));

            Assert.Equal(MaterializationSourceSettlementDisposition.Acknowledged, settlement.Disposition);
            Assert.NotNull(settlement.Receipt);
            Assert.True(await HasSlotAdvancedAsync(
                dataSource,
                slotName,
                initiallyConfirmedPosition,
                timeout.Token));
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await CleanupAsync(
                dataSource,
                schemaName,
                publicationName,
                slotName,
                cleanupTimeout.Token);
        }
    }

    [PostgresLogicalReplicationFact]
    public async Task LocalPostgres_DefaultIdentityMapsKeyTuplesForKeyChangesAndDeletes()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "The PostgreSQL logical-replication integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var suffix = Guid.NewGuid().ToString("N");
        var schemaName = $"ari188_lr_default_{suffix}";
        const string tableName = "logical_items";
        var publicationName = $"ari188_lr_default_pub_{suffix}";
        var slotName = $"ari188_lr_default_slot_{suffix}";

        try
        {
            await RequireLogicalWalLevelAsync(dataSource, timeout.Token);
            await ProvisionDefaultIdentityAsync(
                dataSource,
                schemaName,
                tableName,
                publicationName,
                slotName,
                timeout.Token);
            var fixture = CreateDefaultIdentityFixture(
                dataSource,
                connectionString,
                schemaName,
                tableName);
            Assert.Equal(
                PostgresRelationQueryScalarType.Uuid,
                fixture.Reader.StorageBinding.ResolveTable(fixture.Placement.Id).Identity!.ScalarType);
            var source = await CreateChangeSourceAsync(
                fixture,
                publicationName,
                slotName,
                PostgresLogicalReplicationReplicaIdentityKind.Default,
                timeout.Token);
            var startPosition = await source.CaptureCurrentPositionAsync(
                OperationContext.Create(cancellationToken: timeout.Token),
                source.Scope);

            await WriteDefaultIdentityChangesAsync(
                dataSource,
                schemaName,
                tableName,
                timeout.Token);

            var pages = await ReadThroughCatchUpAsync(
                source,
                startPosition,
                timeout.Token);
            var relevantPages = pages
                .Where(static page => !page.Deliveries.IsDefaultOrEmpty)
                .ToArray();

            Assert.Equal(3, relevantPages.Length);
            var insert = Assert.Single(relevantPages[0].Deliveries);
            Assert.Equal(MaterializationChangeKind.Create, insert.Change.Kind);
            Assert.Equal("11111111-1111-1111-1111-111111111111", insert.Change.SubjectIdentity);

            Assert.Equal(
                [MaterializationChangeKind.Delete, MaterializationChangeKind.Create],
                relevantPages[1].Deliveries.Select(static delivery => delivery.Change.Kind));
            Assert.Equal(
                [
                    "11111111-1111-1111-1111-111111111111",
                    "22222222-2222-2222-2222-222222222222"
                ],
                relevantPages[1].Deliveries.Select(static delivery => delivery.Change.SubjectIdentity));
            Assert.Null(relevantPages[1].Deliveries[0].Change.Before);
            Assert.Null(relevantPages[1].Deliveries[0].Change.After);
            Assert.Null(relevantPages[1].Deliveries[1].Change.Before);
            Assert.NotNull(relevantPages[1].Deliveries[1].Change.After);

            var delete = Assert.Single(relevantPages[2].Deliveries);
            Assert.Equal(MaterializationChangeKind.Delete, delete.Change.Kind);
            Assert.Equal("22222222-2222-2222-2222-222222222222", delete.Change.SubjectIdentity);
            Assert.Null(delete.Change.Before);
            Assert.Null(delete.Change.After);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await CleanupAsync(
                dataSource,
                schemaName,
                publicationName,
                slotName,
                cleanupTimeout.Token);
        }
    }

    [PostgresLogicalReplicationFact]
    public async Task LocalPostgres_IndexIdentityCatalogExcludesIncludedColumnsFromKeyEvidence()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "The PostgreSQL logical-replication integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var suffix = Guid.NewGuid().ToString("N");
        var schemaName = $"ari188_lr_index_{suffix}";
        const string tableName = "logical_items";
        var indexName = $"ari188_lr_index_key_{suffix}";
        var publicationName = $"ari188_lr_index_pub_{suffix}";
        var slotName = $"ari188_lr_index_slot_{suffix}";

        try
        {
            await RequireLogicalWalLevelAsync(dataSource, timeout.Token);
            await ProvisionIndexIdentityAsync(
                dataSource: dataSource,
                schemaName: schemaName,
                tableName: tableName,
                indexName: indexName,
                publicationName: publicationName,
                slotName: slotName,
                cancellationToken: timeout.Token);
            var fixture = CreateDefaultIdentityFixture(
                dataSource: dataSource,
                connectionString: connectionString,
                schemaName: schemaName,
                tableName: tableName);
            var expectedIdentity = new PostgresLogicalReplicationReplicaIdentityBinding(
                kind: PostgresLogicalReplicationReplicaIdentityKind.Index,
                indexName: indexName);
            var binding = new PostgresLogicalReplicationBinding(
                publicationName: publicationName,
                slotName: slotName,
                slotGeneration: $"tests/{slotName}@generation-1",
                expectedReplicaIdentity: expectedIdentity);
            var protocol = new PostgresNpgsqlLogicalReplicationProtocol(
                runtimeBinding: fixture.RuntimeBinding,
                binding: binding,
                table: fixture.Reader.StorageBinding.ResolveTable(fixture.Placement.Id));

            var deployment = await protocol.InspectAsync(timeout.Token);

            Assert.Equal(expectedIdentity, deployment.ReplicaIdentity);
            var identityColumn = Assert.Single(
                deployment.Columns,
                static column => column.Name == "load_id");
            var includedColumn = Assert.Single(
                deployment.Columns,
                static column => column.Name == "load_value");
            Assert.True(identityColumn.IsReplicaIdentity);
            Assert.False(includedColumn.IsReplicaIdentity);
            Assert.Equal(
                "load_id",
                Assert.Single(
                    deployment.Columns,
                    static column => column.IsReplicaIdentity).Name);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await CleanupAsync(
                dataSource: dataSource,
                schemaName: schemaName,
                publicationName: publicationName,
                slotName: slotName,
                cancellationToken: cleanupTimeout.Token);
        }
    }

    [PostgresLogicalReplicationFact]
    public async Task LocalPostgres_ExportedSnapshotHandoffSeparatesBaselineFromLaterChanges()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "The PostgreSQL logical-replication integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var suffix = Guid.NewGuid().ToString("N");
        var schemaName = $"ari188_lr_handoff_{suffix}";
        const string tableName = "logical_items";
        var publicationName = $"ari188_lr_handoff_pub_{suffix}";
        var slotName = $"ari188_lr_handoff_slot_{suffix}";
        PostgresLogicalReplicationBaselineHandoff? handoff = null;

        try
        {
            await RequireLogicalWalLevelAsync(dataSource, timeout.Token);
            await ProvisionPublicationAsync(
                dataSource,
                schemaName,
                tableName,
                publicationName,
                PostgresLogicalReplicationReplicaIdentityKind.Full,
                timeout.Token);
            await InsertItemAsync(
                dataSource,
                schemaName,
                tableName,
                identity: "item-a",
                name: "Alpha",
                cancellationToken: timeout.Token);
            await InsertItemAsync(
                dataSource,
                schemaName,
                tableName,
                identity: "item-b",
                name: "Beta",
                cancellationToken: timeout.Token);
            var fixture = CreateFixture(
                dataSource,
                connectionString,
                schemaName,
                tableName);
            var binding = new PostgresLogicalReplicationBinding(
                publicationName: publicationName,
                slotName: slotName,
                slotGeneration: $"tests/{slotName}@generation-1",
                expectedReplicaIdentity: new(
                    kind: PostgresLogicalReplicationReplicaIdentityKind.Full),
                beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required);
            try
            {
                handoff = await PostgresLogicalReplicationBaselineHandoff.CreateAsync(
                    context: OperationContext.Create(cancellationToken: timeout.Token),
                    reader: fixture.Reader,
                    placement: fixture.Placement,
                    runtimeBinding: fixture.RuntimeBinding,
                    binding: binding,
                    positionAuthenticationKey: PositionAuthenticationKey,
                    policy: CreateLogicalReplicationPolicy());
            }
            catch (PostgresLogicalReplicationException exception)
            {
                throw new InvalidOperationException(
                    $"Logical-replication snapshot handoff failed as {exception.FailureKind} at '{exception.Observation.EvidenceReference}'.",
                    exception);
            }

            Assert.False(await ReadSlotTemporaryAsync(dataSource, slotName, timeout.Token));
            await InsertItemAsync(
                dataSource,
                schemaName,
                tableName,
                identity: "item-c",
                name: "Gamma",
                cancellationToken: timeout.Token);

            Assert.Equal(
                ["item-a", "item-b"],
                await ReadBaselineIdentitiesAsync(
                    handoff,
                    fixture.BaselineRead,
                    timeout.Token));

            var pages = await ReadThroughCatchUpAsync(
                handoff.ChangeSource,
                handoff.ChangeStartPosition,
                timeout.Token);
            var delivery = Assert.Single(
                pages.SelectMany(static page => page.Deliveries));
            Assert.Equal(MaterializationChangeKind.Create, delivery.Change.Kind);
            Assert.Equal("item-c", delivery.Change.SubjectIdentity);
            Assert.Equal("Gamma", ReadName(delivery.Change.After));
        }
        finally
        {
            if (handoff is not null)
                await handoff.DisposeAsync();
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await CleanupAsync(
                dataSource,
                schemaName,
                publicationName,
                slotName,
                cleanupTimeout.Token);
        }
    }

    static async Task<IReadOnlyList<MaterializationChangePage>> ReadThroughCatchUpAsync(
        PostgresLogicalReplicationMaterializationChangeSource source,
        MaterializationSourcePosition afterPosition,
        CancellationToken cancellationToken)
    {
        List<MaterializationChangePage> pages = [];
        for (var pageNumber = 0; pageNumber < 8; pageNumber++)
        {
            MaterializationChangePage page;
            try
            {
                page = await source.ReadChangesAsync(
                    OperationContext.Create(cancellationToken: cancellationToken),
                    new(
                        scope: source.Scope,
                        afterPosition: afterPosition,
                        maximumDeliveries: 1,
                        maximumBytes: 1_000_000));
            }
            catch (PostgresLogicalReplicationException exception)
            {
                throw new InvalidOperationException(
                    $"Logical-replication integration read failed as {exception.FailureKind} at '{exception.Observation.EvidenceReference}'.",
                    exception);
            }
            pages.Add(page);
            afterPosition = page.ThroughPosition;
            if (page.State == MaterializationChangePageState.CaughtUp)
            {
                return pages;
            }
        }

        throw new InvalidOperationException(
            "The PostgreSQL logical-replication integration source did not reach its bounded current cut.");
    }

    static string ReadName(RelationQuerySourceReadObservation? observation)
    {
        var present = Assert.IsType<RelationQuerySourceReadObservation>(observation);
        var field = Assert.Single(
            present.Fields,
            static candidate => candidate.Field.SemanticPath == FieldPath.FromField("name"));
        Assert.Equal(RelationQuerySourceReadFieldState.Value, field.State);
        Assert.True(field.Value.HasValue);
        return field.Value.Value.String
            ?? throw new InvalidOperationException("The logical-replication name field was not text.");
    }

    static async Task RequireLogicalWalLevelAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SHOW wal_level;");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(value as string, "logical", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The opt-in PostgreSQL logical-replication test requires wal_level=logical; the server reported '{value ?? "null"}'.");
        }
    }

    static async Task ProvisionAsync(
        NpgsqlDataSource dataSource,
        string schemaName,
        string tableName,
        string publicationName,
        string slotName,
        PostgresLogicalReplicationReplicaIdentityKind replicaIdentityKind,
        CancellationToken cancellationToken)
    {
        await ProvisionPublicationAsync(
            dataSource,
            schemaName,
            tableName,
            publicationName,
            replicaIdentityKind,
            cancellationToken);
        await CreateLogicalSlotAsync(
            dataSource: dataSource,
            slotName: slotName,
            cancellationToken: cancellationToken);
    }

    static async Task ProvisionPublicationAsync(
        NpgsqlDataSource dataSource,
        string schemaName,
        string tableName,
        string publicationName,
        PostgresLogicalReplicationReplicaIdentityKind replicaIdentityKind,
        CancellationToken cancellationToken)
    {
        var schema = QuoteIdentifier(schemaName);
        var table = QuoteIdentifier(tableName);
        var publication = QuoteIdentifier(publicationName);
        var replicaIdentity = replicaIdentityKind switch
        {
            PostgresLogicalReplicationReplicaIdentityKind.Default => string.Empty,
            PostgresLogicalReplicationReplicaIdentityKind.Full =>
                $"ALTER TABLE {schema}.{table} REPLICA IDENTITY FULL;",
            _ => throw new ArgumentOutOfRangeException(
                nameof(replicaIdentityKind),
                replicaIdentityKind,
                "The live logical-replication fixture supports DEFAULT and FULL replica identity.")
        };
        await using (var setup = dataSource.CreateCommand($$"""
            CREATE SCHEMA {{schema}};
            CREATE TABLE {{schema}}.{{table}} (
                "load_id" text COLLATE "C" PRIMARY KEY,
                "load_name" text NOT NULL,
                CONSTRAINT "ck_logical_items_id_ascii"
                    CHECK (octet_length("load_id") = length("load_id"))
            );
            {{replicaIdentity}}
            CREATE PUBLICATION {{publication}}
                FOR TABLE {{schema}}.{{table}}
                WITH (publish = 'insert, update, delete', publish_via_partition_root = false);
            """))
        {
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    static async Task ProvisionDefaultIdentityAsync(
        NpgsqlDataSource dataSource,
        string schemaName,
        string tableName,
        string publicationName,
        string slotName,
        CancellationToken cancellationToken)
    {
        var schema = QuoteIdentifier(schemaName);
        var table = QuoteIdentifier(tableName);
        var publication = QuoteIdentifier(publicationName);
        await using (var setup = dataSource.CreateCommand($$"""
            CREATE SCHEMA {{schema}};
            CREATE TABLE {{schema}}.{{table}} (
                "load_id" uuid PRIMARY KEY,
                "load_value" integer NOT NULL
            );
            CREATE PUBLICATION {{publication}}
                FOR TABLE {{schema}}.{{table}}
                WITH (publish = 'insert, update, delete', publish_via_partition_root = false);
            """))
        {
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }
        await CreateLogicalSlotAsync(dataSource, slotName, cancellationToken);
    }

    static async Task ProvisionIndexIdentityAsync(
        NpgsqlDataSource dataSource,
        string schemaName,
        string tableName,
        string indexName,
        string publicationName,
        string slotName,
        CancellationToken cancellationToken)
    {
        var schema = QuoteIdentifier(schemaName);
        var table = QuoteIdentifier(tableName);
        var index = QuoteIdentifier(indexName);
        var publication = QuoteIdentifier(publicationName);
        await using (var setup = dataSource.CreateCommand($$"""
            CREATE SCHEMA {{schema}};
            CREATE TABLE {{schema}}.{{table}} (
                "load_id" uuid NOT NULL,
                "load_value" integer NOT NULL
            );
            CREATE UNIQUE INDEX {{index}}
                ON {{schema}}.{{table}} ("load_id")
                INCLUDE ("load_value");
            ALTER TABLE {{schema}}.{{table}}
                REPLICA IDENTITY USING INDEX {{index}};
            CREATE PUBLICATION {{publication}}
                FOR TABLE {{schema}}.{{table}}
                WITH (publish = 'insert, update, delete', publish_via_partition_root = false);
            """))
        {
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }
        await CreateLogicalSlotAsync(
            dataSource: dataSource,
            slotName: slotName,
            cancellationToken: cancellationToken);
    }

    static async Task CreateLogicalSlotAsync(
        NpgsqlDataSource dataSource,
        string slotName,
        CancellationToken cancellationToken)
    {
        await using var createSlot = dataSource.CreateCommand("""
            SELECT slot_name
            FROM pg_create_logical_replication_slot(@slot_name, 'pgoutput');
            """);
        createSlot.Parameters.AddWithValue("slot_name", slotName);
        _ = await createSlot.ExecuteScalarAsync(cancellationToken);
    }

    static async ValueTask<PostgresLogicalReplicationMaterializationChangeSource> CreateChangeSourceAsync(
        Fixture fixture,
        string publicationName,
        string slotName,
        PostgresLogicalReplicationReplicaIdentityKind replicaIdentityKind,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(
                reader: fixture.Reader,
                placement: fixture.Placement,
                runtimeBinding: fixture.RuntimeBinding,
                binding: new(
                    publicationName: publicationName,
                    slotName: slotName,
                    slotGeneration: $"tests/{slotName}@generation-1",
                    expectedReplicaIdentity: new(kind: replicaIdentityKind),
                    beforeImageRequirement:
                        replicaIdentityKind == PostgresLogicalReplicationReplicaIdentityKind.Full
                            ? PostgresLogicalReplicationBeforeImageRequirement.Required
                            : PostgresLogicalReplicationBeforeImageRequirement.NotRequired),
                positionAuthenticationKey: PositionAuthenticationKey,
                policy: CreateLogicalReplicationPolicy(),
                cancellationToken: cancellationToken);
        }
        catch (PostgresLogicalReplicationException exception)
        {
            throw new InvalidOperationException(
                $"Logical-replication integration preflight failed as {exception.FailureKind} at '{exception.Observation.EvidenceReference}'.",
                exception);
        }
    }

    static PostgresLogicalReplicationSourcePolicy CreateLogicalReplicationPolicy() => new(
        maximumTransactionChanges: 100,
        maximumTransactionBytes: 1_000_000,
        maximumTransactionsPerRead: 1,
        maximumReconnectAttempts: 1,
        reconnectDelay: TimeSpan.FromMilliseconds(10),
        readInactivityTimeout: TimeSpan.FromSeconds(3),
        settlementConfirmationTimeout: TimeSpan.FromSeconds(10),
        settlementConfirmationPollInterval: TimeSpan.FromMilliseconds(20));

    static async Task WriteChangesAsync(
        NpgsqlDataSource dataSource,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var qualifiedTable = string.Concat(
            QuoteIdentifier(schemaName),
            ".",
            QuoteIdentifier(tableName));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await using var insertAndUpdate = new NpgsqlCommand($$"""
                INSERT INTO {{qualifiedTable}} ("load_id", "load_name")
                VALUES ('item-a', 'Alpha');
                UPDATE {{qualifiedTable}}
                SET "load_name" = 'Beta'
                WHERE "load_id" = 'item-a';
                """, connection, transaction);
            await insertAndUpdate.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await using var keyChange = new NpgsqlCommand($$"""
                UPDATE {{qualifiedTable}}
                SET "load_id" = 'item-b', "load_name" = 'Gamma'
                WHERE "load_id" = 'item-a';
                """, connection, transaction);
            await keyChange.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await using var delete = new NpgsqlCommand($$"""
                DELETE FROM {{qualifiedTable}}
                WHERE "load_id" = 'item-b';
                """, connection, transaction);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    static async Task WriteDefaultIdentityChangesAsync(
        NpgsqlDataSource dataSource,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var qualifiedTable = string.Concat(
            QuoteIdentifier(schemaName),
            ".",
            QuoteIdentifier(tableName));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var insert = new NpgsqlCommand($$"""
            INSERT INTO {{qualifiedTable}} ("load_id", "load_value")
            VALUES ('11111111-1111-1111-1111-111111111111', 10);
            """, connection))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var keyChange = new NpgsqlCommand($$"""
            UPDATE {{qualifiedTable}}
            SET "load_id" = '22222222-2222-2222-2222-222222222222', "load_value" = 20
            WHERE "load_id" = '11111111-1111-1111-1111-111111111111';
            """, connection))
        {
            await keyChange.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var delete = new NpgsqlCommand($$"""
            DELETE FROM {{qualifiedTable}}
            WHERE "load_id" = '22222222-2222-2222-2222-222222222222';
            """, connection);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task InsertItemAsync(
        NpgsqlDataSource dataSource,
        string schemaName,
        string tableName,
        string identity,
        string name,
        CancellationToken cancellationToken)
    {
        var qualifiedTable = string.Concat(
            QuoteIdentifier(schemaName),
            ".",
            QuoteIdentifier(tableName));
        await using var insert = dataSource.CreateCommand($$"""
            INSERT INTO {{qualifiedTable}} ("load_id", "load_name")
            VALUES (@identity, @name);
            """);
        insert.Parameters.AddWithValue("identity", identity);
        insert.Parameters.AddWithValue("name", name);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task<IReadOnlyList<string>> ReadBaselineIdentitiesAsync(
        PostgresLogicalReplicationBaselineHandoff handoff,
        RelationQuerySourceReadRequest read,
        CancellationToken cancellationToken)
    {
        List<string> identities = [];
        MaterializationSourceContinuation? continuation = null;
        for (var pageNumber = 0; pageNumber < 8; pageNumber++)
        {
            var page = await handoff.ReadPageAsync(
                OperationContext.Create(cancellationToken: cancellationToken),
                new(
                    read,
                    handoff.Scope,
                    continuation,
                    maximumItems: 1,
                    maximumBytes: 1_000_000));
            identities.AddRange(
                page.Read.Observations.Select(static observation => observation.Identity));
            continuation = page.Continuation;
            if (page.State == MaterializationSourcePageState.Exhausted)
                return identities;
        }

        throw new InvalidOperationException(
            "The PostgreSQL exported-snapshot baseline did not exhaust its bounded read.");
    }

    static async Task<bool> ReadSlotTemporaryAsync(
        NpgsqlDataSource dataSource,
        string slotName,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT temporary
            FROM pg_catalog.pg_replication_slots
            WHERE slot_name = @slot_name;
            """);
        command.Parameters.AddWithValue("slot_name", slotName);
        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            bool temporary => temporary,
            _ => throw new InvalidOperationException(
                $"The PostgreSQL exported-snapshot handoff did not retain slot '{slotName}'.")
        };
    }

    static async Task<string> ReadConfirmedFlushPositionAsync(
        NpgsqlDataSource dataSource,
        string slotName,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT confirmed_flush_lsn::text
            FROM pg_catalog.pg_replication_slots
            WHERE slot_name = @slot_name;
            """);
        command.Parameters.AddWithValue("slot_name", slotName);
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException(
                $"The PostgreSQL logical-replication slot '{slotName}' has no confirmed position.");
    }

    static async Task<bool> HasSlotAdvancedAsync(
        NpgsqlDataSource dataSource,
        string slotName,
        string priorPosition,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT pg_catalog.pg_wal_lsn_diff(
                confirmed_flush_lsn,
                CAST(@prior_position AS pg_lsn)) > 0
            FROM pg_catalog.pg_replication_slots
            WHERE slot_name = @slot_name;
            """);
        command.Parameters.AddWithValue("prior_position", priorPosition);
        command.Parameters.AddWithValue("slot_name", slotName);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    static async Task CleanupAsync(
        NpgsqlDataSource dataSource,
        string schemaName,
        string publicationName,
        string slotName,
        CancellationToken cancellationToken)
    {
        var slotInactive = false;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var inspect = dataSource.CreateCommand("""
                SELECT NOT active
                FROM pg_catalog.pg_replication_slots
                WHERE slot_name = @slot_name;
                """);
            inspect.Parameters.AddWithValue("slot_name", slotName);
            var result = await inspect.ExecuteScalarAsync(cancellationToken);
            if (result is null || result is DBNull)
            {
                slotInactive = true;
                break;
            }
            if (result is true)
            {
                slotInactive = true;
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        if (!slotInactive)
        {
            throw new InvalidOperationException(
                $"The test-owned PostgreSQL logical-replication slot '{slotName}' remained active during cleanup.");
        }

        await using (var dropSlot = dataSource.CreateCommand("""
            SELECT pg_drop_replication_slot(slot_name)
            FROM pg_catalog.pg_replication_slots
            WHERE slot_name = @slot_name
              AND NOT active;
            """))
        {
            dropSlot.Parameters.AddWithValue("slot_name", slotName);
            await dropSlot.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var cleanup = dataSource.CreateCommand($$"""
            DROP PUBLICATION IF EXISTS {{QuoteIdentifier(publicationName)}};
            DROP SCHEMA IF EXISTS {{QuoteIdentifier(schemaName)}} CASCADE;
            """);
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
    }

    static Fixture CreateFixture(
        NpgsqlDataSource dataSource,
        string connectionString,
        string schemaName,
        string tableName)
    {
        var author = RelationQuery.Expression();
        var itemShape = author.Clr.Shape<LogicalItem>();
        var items = author.Source(itemShape);
        var projected = author.Project(
            items.Node,
            (LogicalItem item) => new LogicalRow { Id = item.Id, Name = item.Name },
            items.Binding);
        var query = author.BuildQuery(
            id: new("postgres-logical-replication-integration"),
            name: new("PostgresLogicalReplicationIntegration"),
            author.Rows(projected.Node, projected.Binding, id: "rows"));
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments));
        var plan = compilation.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));

        var placementBuilder = RelationQueryPlacement.For(plan);
        var sourceHandle = placementBuilder.Source(
            sourceKey: "tests/postgres/logical-replication/items",
            targetProfile: PostgresRelationQuerySourceTargetProfile.Default,
            limits: new(
                maximumBatchSize: 32,
                maximumBufferedRows: 32,
                maximumFanOut: 32,
                maximumConcurrency: 1));
        var placed = placementBuilder
            .PlaceSource(sourceHandle, itemShape)
            .Identity(item => item.Id)
            .FieldsBySemanticPath();
        var authoredPlacement = placementBuilder.Build().RequireValue();
        var placedInput = authoredPlacement.GetInput(placed);
        var ordering = new PostgresRelationQueryTextSemantics(
            collation: "C",
            equality: PostgresRelationQueryTextEqualitySemantics.Ordinal,
            ordering: PostgresRelationQueryTextOrderingSemantics.Ordinal,
            orderingDomain: new(
                validatedConstraintName: "ck_logical_items_id_ascii",
                authority: "tests/postgres/logical-replication/v1"));
        var identityOptions = new PostgresRelationQueryColumnOptions(
            scalarType: PostgresRelationQueryScalarType.Text,
            textSemantics: ordering,
            ordering: PostgresRelationQueryOrderingCapability.Exact
                | PostgresRelationQueryOrderingCapability.StableUnique);
        var storage = PostgresRelationQueryBinding.For(
                authoredPlacement,
                explicitAuthority: "tests/postgres/logical-replication/v1")
            .Database(new("tests/postgres/logical-replication/database"))
            .Table(
                placedInput,
                tableName,
                table => table
                    .Schema(schemaName)
                    .ColumnsExplicitly()
                    .Column(item => item.Id, "load_id", identityOptions)
                    .Column(item => item.Name, "load_name")
                    .Identity(item => item.Id, "load_id", identityOptions))
            .Build()
            .RequireValue();
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var physical = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            authoredPlacement.Placement,
            new(
                id: new("tests/postgres/logical-replication/source-policy/v1"),
                conventionSetVersion: authoredPlacement.Placement.ConventionSetVersion,
                maximumBatchSize: 32,
                maximumBufferedRows: 32,
                maximumLocalRows: 32,
                maximumFanOut: 32,
                maximumReferenceKeysPerObservation: 32,
                maximumConcurrency: 1));
        var physicalPlan = physical.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, physical.Diagnostics));
        var runtimeBinding = new PostgresNpgsqlRuntimeBinding(
            database: storage.Database,
            dataSource: dataSource,
            authority: "cohesive.tests/postgres/logical-replication/runtime/v1",
            logicalReplicationConnectionFactory: () =>
                new LogicalReplicationConnection(connectionString));
        var reader = new PostgresRelationQuerySourceReader(
            plan: plan,
            physicalPlan: physicalPlan,
            source: sourceHandle.Id,
            storage: storage,
            dataSource: dataSource,
            runtimeBinding: runtimeBinding,
            policy: new(
                maximumBatchKeys: 32,
                maximumRowsPerRead: 32,
                maximumPageItems: 32,
                maximumPageBytes: 1_000_000));
        var placement = Assert.Single(physicalPlan.Placement.Bindings);
        var sourceStage = Assert.Single(
            physicalPlan.Stages,
            static stage => stage.Kind == RelationQueryPhysicalStageKind.SourceRead);
        var baselineRead = new RelationQuerySourceReadRequest(
            physicalPlan.Fingerprint,
            sourceStage.Id,
            placement.Id,
            sourceHandle.Id,
            placement.Shape,
            placement.Identity!.SourceSelector,
            [
                .. placement.Fields.Select(static field => new RelationQuerySourceReadField(
                    field.Input,
                    field.SemanticPath,
                    field.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInput))
            ],
            new RelationQueryBoundedEnumeration(maximumRows: 32),
            maximumBufferedRows: 32);
        return new(
            Reader: reader,
            Placement: placement,
            RuntimeBinding: runtimeBinding,
            BaselineRead: baselineRead);
    }

    static Fixture CreateDefaultIdentityFixture(
        NpgsqlDataSource dataSource,
        string connectionString,
        string schemaName,
        string tableName)
    {
        var author = RelationQuery.Expression();
        var itemShape = author.Clr.Shape<DefaultIdentityLogicalItem>();
        var items = author.Source(itemShape);
        var projected = author.Project(
            items.Node,
            (DefaultIdentityLogicalItem item) => new DefaultIdentityLogicalRow
            {
                Id = item.Id,
                Value = item.Value
            },
            items.Binding);
        var query = author.BuildQuery(
            id: new("postgres-logical-replication-default-identity-integration"),
            name: new("PostgresLogicalReplicationDefaultIdentityIntegration"),
            author.Rows(projected.Node, projected.Binding, id: "rows"));
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments));
        var plan = compilation.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));

        var placementBuilder = RelationQueryPlacement.For(plan);
        var sourceHandle = placementBuilder.Source(
            sourceKey: "tests/postgres/logical-replication/default-identity-items",
            targetProfile: PostgresRelationQuerySourceTargetProfile.Default,
            limits: new(
                maximumBatchSize: 32,
                maximumBufferedRows: 32,
                maximumFanOut: 32,
                maximumConcurrency: 1));
        var placed = placementBuilder
            .PlaceSource(sourceHandle, itemShape)
            .Identity(item => item.Id)
            .FieldsBySemanticPath();
        var authoredPlacement = placementBuilder.Build().RequireValue();
        var placedInput = authoredPlacement.GetInput(placed);
        var identityOptions = new PostgresRelationQueryColumnOptions(
            scalarType: PostgresRelationQueryScalarType.Uuid,
            ordering: PostgresRelationQueryOrderingCapability.Exact
                | PostgresRelationQueryOrderingCapability.StableUnique);
        var valueOptions = new PostgresRelationQueryColumnOptions(
            scalarType: PostgresRelationQueryScalarType.Int32);
        var storage = PostgresRelationQueryBinding.For(
                authoredPlacement,
                explicitAuthority: "tests/postgres/logical-replication/default-identity/v1")
            .Database(new("tests/postgres/logical-replication/database"))
            .Table(
                placedInput,
                tableName,
                table => table
                    .Schema(schemaName)
                    .ColumnsExplicitly()
                    .Column(item => item.Id, "load_id", identityOptions)
                    .Column(item => item.Value, "load_value", valueOptions)
                    .Identity(item => item.Id, "load_id", identityOptions))
            .Build()
            .RequireValue();
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var physical = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            authoredPlacement.Placement,
            new(
                id: new("tests/postgres/logical-replication/default-identity-policy/v1"),
                conventionSetVersion: authoredPlacement.Placement.ConventionSetVersion,
                maximumBatchSize: 32,
                maximumBufferedRows: 32,
                maximumLocalRows: 32,
                maximumFanOut: 32,
                maximumReferenceKeysPerObservation: 32,
                maximumConcurrency: 1));
        var physicalPlan = physical.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, physical.Diagnostics));
        var runtimeBinding = new PostgresNpgsqlRuntimeBinding(
            database: storage.Database,
            dataSource: dataSource,
            authority: "cohesive.tests/postgres/logical-replication/default-identity-runtime/v1",
            logicalReplicationConnectionFactory: () =>
                new LogicalReplicationConnection(connectionString));
        var reader = new PostgresRelationQuerySourceReader(
            plan: plan,
            physicalPlan: physicalPlan,
            source: sourceHandle.Id,
            storage: storage,
            dataSource: dataSource,
            runtimeBinding: runtimeBinding,
            policy: new(
                maximumBatchKeys: 32,
                maximumRowsPerRead: 32,
                maximumPageItems: 32,
                maximumPageBytes: 1_000_000));
        var placement = Assert.Single(physicalPlan.Placement.Bindings);
        var sourceStage = Assert.Single(
            physicalPlan.Stages,
            static stage => stage.Kind == RelationQueryPhysicalStageKind.SourceRead);
        var baselineRead = new RelationQuerySourceReadRequest(
            physicalPlan.Fingerprint,
            sourceStage.Id,
            placement.Id,
            sourceHandle.Id,
            placement.Shape,
            placement.Identity!.SourceSelector,
            [
                .. placement.Fields.Select(static field => new RelationQuerySourceReadField(
                    field.Input,
                    field.SemanticPath,
                    field.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInput))
            ],
            new RelationQueryBoundedEnumeration(maximumRows: 32),
            maximumBufferedRows: 32);
        return new(
            Reader: reader,
            Placement: placement,
            RuntimeBinding: runtimeBinding,
            BaselineRead: baselineRead);
    }

    static string QuoteIdentifier(string value) => string.Concat(
        '"',
        value.Replace("\"", "\"\"", StringComparison.Ordinal),
        '"');

    sealed record Fixture(
        PostgresRelationQuerySourceReader Reader,
        RelationQuerySourcePlacementBinding Placement,
        PostgresNpgsqlRuntimeBinding RuntimeBinding,
        RelationQuerySourceReadRequest BaselineRead);

    sealed class LogicalItem
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class LogicalRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class DefaultIdentityLogicalItem
    {
        [JsonPropertyName("id")]
        public required Guid Id { get; init; }

        [JsonPropertyName("value")]
        public required int Value { get; init; }
    }

    sealed class DefaultIdentityLogicalRow
    {
        [JsonPropertyName("id")]
        public required Guid Id { get; init; }

        [JsonPropertyName("value")]
        public required int Value { get; init; }
    }

    sealed class PostgresLogicalReplicationFactAttribute : FactAttribute
    {
        public PostgresLogicalReplicationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)))
            {
                Skip = $"Set {ConnectionStringEnvironmentVariable} or run eng/test-postgres-logical-replication-integration.sh.";
            }
        }
    }
}
