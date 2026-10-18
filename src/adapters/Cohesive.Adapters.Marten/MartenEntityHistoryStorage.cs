using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Adapters.Postgres;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using JasperFx.Events;
using Marten;
using Marten.Services;
using Npgsql;

namespace Cohesive.Adapters.Marten;

/// <summary>Marten event streams participating in the existing relational entity commit transaction.</summary>
/// <remarks>The caller owns both native services and applies Marten schema changes before serving requests.
/// Streams retain canonical replacement actions, independently of outbox delivery. Checkpoints are exact
/// retained replacement events; no parallel head table or separately mutable snapshot is maintained.</remarks>
public sealed class MartenEntityHistoryStorage : PostgresEntityHistoryStorage
{
    readonly IDocumentStore store;
    readonly EntityReconstitutionMode mode;
    readonly int checkpointInterval, maximumReplayActions, maximumActionBytes;
    readonly long maximumReplayBytes;
    static readonly JsonSerializerOptions Json = CreateJson();
    static JsonSerializerOptions CreateJson()
    {
        var value = EntityStorageJson.CreateOptions();
        value.MakeReadOnly(populateMissingResolver: true);
        return value;
    }

    /// <summary>Attaches a caller-configured Marten store after validating its schema; performs no migration.</summary>
    /// <param name="dataSource">Exact native source used by the relational repository.</param>
    /// <param name="store">Caller-owned Marten store configured with string stream identities and an installed schema.</param>
    /// <param name="authority">Immutable authority recorded in every event.</param>
    /// <param name="reconstitution">Full replay or retained replacement checkpoint plus tail.</param>
    /// <param name="checkpointInterval">Checkpoint cadence in stream positions.</param>
    /// <param name="maximumReplayActions">Maximum actions admitted for one read.</param>
    /// <param name="maximumActionBytes">Maximum canonical payload size per append/read.</param>
    /// <param name="maximumReplayBytes">Maximum canonical bytes decoded per read.</param>
    /// <param name="cancellationToken">Cancellation of setup validation.</param>
    /// <returns>A validated transaction attachment; the caller retains ownership of both services.</returns>
    /// <exception cref="ArgumentException">The store uses a different data source or non-string stream identities.</exception>
    /// <exception cref="Weasel.Core.Migrations.DatabaseValidationException">The installed schema differs from Marten configuration.</exception>
    /// <exception cref="NpgsqlException">Native schema validation fails.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound or mode is invalid.</exception>
    public static async Task<MartenEntityHistoryStorage> BindAsync(NpgsqlDataSource dataSource, IDocumentStore store, EntityStateAuthority authority,
        EntityReconstitutionMode reconstitution = EntityReconstitutionMode.CheckpointAndTail,
        int checkpointInterval = 100, int maximumReplayActions = 10_000, int maximumActionBytes = 1_048_576,
        long maximumReplayBytes = 16_777_216, CancellationToken cancellationToken = default)
    {
        var storage = new MartenEntityHistoryStorage(dataSource, store, authority, reconstitution,
            checkpointInterval, maximumReplayActions, maximumActionBytes, maximumReplayBytes);
        RequireNativeStore(dataSource, store);
        await store.Storage.Database.AssertDatabaseMatchesConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return storage;
    }

    internal static void RequireNativeStore(NpgsqlDataSource dataSource, IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(store);
        if (store.Storage.Database is not Weasel.Postgresql.PostgresqlDatabase native
            || !ReferenceEquals(native.DataSource, dataSource))
            throw new ArgumentException("Marten must use the exact relational NpgsqlDataSource instance.", nameof(store));
        if (store.Options.Events.StreamIdentity != StreamIdentity.AsString)
            throw new ArgumentException("Entity history requires Marten string stream identities.", nameof(store));
    }

    MartenEntityHistoryStorage(NpgsqlDataSource dataSource, IDocumentStore store, EntityStateAuthority authority,
        EntityReconstitutionMode reconstitution = EntityReconstitutionMode.CheckpointAndTail,
        int checkpointInterval = 100, int maximumReplayActions = 10_000, int maximumActionBytes = 1_048_576,
        long maximumReplayBytes = 16_777_216)
        : base(dataSource, authority, "marten/" + (store ?? throw new ArgumentNullException(nameof(store))).Options.Events.DatabaseSchemaName)
    {
        if (!Enum.IsDefined(reconstitution)) throw new ArgumentOutOfRangeException(nameof(reconstitution));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointInterval);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReplayActions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReplayBytes);
        this.store = store; mode = reconstitution; this.checkpointInterval = checkpointInterval;
        this.maximumReplayActions = maximumReplayActions; this.maximumActionBytes = maximumActionBytes;
        this.maximumReplayBytes = maximumReplayBytes;
    }

    /// <inheritdoc />
    public override async Task<EntityStateAction?> ReadAsync(EntityDefinition definition, NpgsqlTransaction transaction,
        string entityType, string partition, string id, string partitionField, bool latestOnly, CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession(SessionOptions.ForTransaction(transaction, shouldAutoCommit: false));
        var key = StreamKey(entityType, partition, id);
        var head = await session.Events.FetchStreamStateAsync(key, cancellationToken).ConfigureAwait(false);
        if (head is null) return null;
        var end = head.Version;
        var start = latestOnly ? end : mode == EntityReconstitutionMode.CheckpointAndTail
            ? Math.Max(1, end / checkpointInterval * checkpointInterval) : 1;
        if (end < 1 || end - start + 1 > maximumReplayActions) throw new InvalidOperationException("Reconstitution exceeds its action bound.");
        var events = await session.Events.FetchStreamAsync(key, version: end, fromVersion: start, token: cancellationToken).ConfigureAwait(false);
        EntityStateAction? current = null;
        long totalBytes = 0;
        foreach (var item in events)
        {
            if (item.Data is not EntityStateEvent retained || retained.Authority != Authority
                || retained.FormatVersion != EntityStorageJson.FormatVersion)
                throw new InvalidOperationException("Retained event authority or encoding differs; migrate explicitly.");
            var bytes = retained.Content;
            if (bytes is null || bytes.Length == 0 || bytes.Length > maximumActionBytes
                || (totalBytes += bytes.Length) > maximumReplayBytes || Hash(bytes) != retained.ContentHash)
                throw new InvalidOperationException("State action integrity or size mismatch.");
            var action = JsonSerializer.Deserialize<EntityStateAction>(bytes, Json) ?? throw new InvalidOperationException("Missing action.");
            if (action.State is null || action.Position != item.Version || action.State.EntityId.Value != id
                || action.State.Observation.GetField(partitionField).GetRequiredString() != partition)
                throw new InvalidOperationException("State action contradicts stream identity.");
            if (current is null && start > 1)
            {
                if (action.Position != start || action.State.Observation.ShapeId != definition.StateShape.QualifiedId)
                    throw new InvalidOperationException("Invalid retained checkpoint.");
                _ = definition.CreateState(action.State);
                current = action;
            }
            else current = EntityReconstitution.Apply(definition, current, action);
        }
        if (current?.Position != end) throw new InvalidOperationException("State history is incomplete.");
        return current;
    }

    /// <inheritdoc />
    public override async Task AppendAsync(NpgsqlTransaction transaction, string entityType, string partition, string id,
        EntityStateAction action, CancellationToken cancellationToken)
    {
        var bytes = StrictDocumentJson.GetCanonicalBytes(action, Json);
        if (bytes.Length > maximumActionBytes) throw new InvalidOperationException("State action exceeds its byte bound.");
        await using var session = store.LightweightSession(SessionOptions.ForTransaction(transaction, shouldAutoCommit: false));
        var retained = new EntityStateEvent(Authority, EntityStorageJson.FormatVersion, bytes, Hash(bytes));
        var key = StreamKey(entityType, partition, id);
        if (action.Position == 1) session.Events.StartStream<EntityStateEvent>(key, retained);
        else session.Events.Append(key, action.Position, retained);
        // Flush, not commit: a later receipt/outbox failure rolls the event back with the mapped row.
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    static string StreamKey(string entityType, string partition, string id) => JsonSerializer.Serialize(new[] { entityType, partition, id });
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary>Versioned Marten envelope around Cohesive's canonical lossless action encoding.</summary>
/// <param name="Authority">Immutable stream authority.</param>
/// <param name="FormatVersion">Canonical storage codec version.</param>
/// <param name="Content">Canonical action bytes.</param>
/// <param name="ContentHash">Integrity hash of the canonical bytes.</param>
public sealed record EntityStateEvent(EntityStateAuthority Authority, int FormatVersion, byte[] Content, string ContentHash);
