using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Cohesive.Adapters.Postgres;

/// <summary>
/// Shared PostgreSQL mechanism for one atomically fenced canonical materialization document per authority identity.
/// </summary>
/// <remarks>
/// Semantic stores own document meaning and mutation rules. This component owns only PostgreSQL row initialization,
/// serializable locking, byte limits, content fingerprints, and compare-and-swap replacement.
/// </remarks>
internal sealed class PostgresMaterializationDocumentAuthority
{
    const int MaximumSerializationAttempts = 8;

    readonly NpgsqlDataSource dataSource;
    readonly PostgresMaterializationStateStoreOptions options;

    internal PostgresMaterializationDocumentAuthority(
        NpgsqlDataSource dataSource,
        PostgresMaterializationStateStoreOptions options)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal async Task EnsureCreatedAsync(OperationContext context)
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

    internal async Task<TResult> AccessAsync<TDocument, TResult>(
        OperationContext context,
        string authorityKind,
        TDocument empty,
        Func<string, TDocument> deserialize,
        Func<TDocument, string> serialize,
        Func<TDocument, OperationContext, Task<(TResult Result, TDocument Replacement)>> operation)
        where TDocument : class
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AccessOnceAsync(
                        context: context,
                        authorityKind: authorityKind,
                        empty: empty,
                        deserialize: deserialize,
                        serialize: serialize,
                        operation: operation)
                    .ConfigureAwait(false);
            }
            catch (PostgresException exception) when (
                attempt < MaximumSerializationAttempts
                && exception.SqlState is PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected)
            {
                context.ThrowIfCancellationRequested();
            }
        }
    }

    async Task<TResult> AccessOnceAsync<TDocument, TResult>(
        OperationContext context,
        string authorityKind,
        TDocument empty,
        Func<string, TDocument> deserialize,
        Func<TDocument, string> serialize,
        Func<TDocument, OperationContext, Task<(TResult Result, TDocument Replacement)>> operation)
        where TDocument : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(empty);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(operation);
        context.ThrowIfCancellationRequested();
        var cancellationToken = context.CancellationToken;
        var authorityId = $"{options.AuthorityId}/{Guard.RequireNotNullOrWhiteSpace(authorityKind)}";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);

        var emptyJson = serialize(empty);
        var emptyFingerprint = Fingerprint(emptyJson);
        await using (var initialize = new NpgsqlCommand($$"""
            INSERT INTO {{options.QualifiedTable}}
                (authority_id, revision, document, document_fingerprint, updated_at)
            VALUES
                (@authority_id, 1, @document, @fingerprint, clock_timestamp())
            ON CONFLICT (authority_id) DO NOTHING;
            """, connection, transaction))
        {
            initialize.Parameters.AddWithValue("authority_id", authorityId);
            initialize.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb, emptyJson);
            initialize.Parameters.AddWithValue("fingerprint", emptyFingerprint);
            await initialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long revision;
        string documentJson;
        string fingerprint;
        await using (var load = new NpgsqlCommand($$"""
            SELECT revision, document::text, document_fingerprint
            FROM {{options.QualifiedTable}}
            WHERE authority_id = @authority_id
            FOR UPDATE;
            """, connection, transaction))
        {
            load.Parameters.AddWithValue("authority_id", authorityId);
            await using var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The PostgreSQL materialization-document authority row disappeared during initialization.");
            }
            revision = reader.GetInt64(0);
            documentJson = reader.GetString(1);
            fingerprint = reader.GetString(2);
        }

        var document = deserialize(documentJson);
        var canonicalLoadedJson = serialize(document);
        var canonicalLoadedFingerprint = Fingerprint(canonicalLoadedJson);
        if (!string.Equals(fingerprint, canonicalLoadedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The PostgreSQL materialization-document authority fingerprint does not match its canonical content.");
        }

        var (result, replacement) = await operation(document, context).ConfigureAwait(false);
        var replacementJson = serialize(replacement);
        var replacementBytes = Encoding.UTF8.GetByteCount(replacementJson);
        if (replacementBytes > options.MaximumDocumentBytes)
        {
            throw new InvalidOperationException(
                $"The materialization '{authorityKind}' authority requires {replacementBytes} UTF-8 bytes, exceeding the configured maximum of {options.MaximumDocumentBytes} bytes.");
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
            update.Parameters.AddWithValue("authority_id", authorityId);
            update.Parameters.AddWithValue("expected_revision", revision);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new DBConcurrencyException(
                    "The PostgreSQL materialization-document authority revision changed during a locked mutation.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    static string Fingerprint(string document) =>
        $"sha256-v1:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)))}";
}
