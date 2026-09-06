using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Adapters.Sql;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Microsoft.Data.Sqlite;
using static Cohesive.Adapters.SQLite.SqliteEntityOutboxSql;

namespace Cohesive.Adapters.SQLite;

/// <summary>One retained direct-Transition commit in the local entity outbox.</summary>
/// <param name="Sequence">Monotonic database cursor; gaps are allowed. Resume strictly after this value.</param>
/// <param name="Commit">Original committed snapshot and ordered canonical envelopes.</param>
public sealed record SqliteEntityOutboxEntry(long Sequence, EntityCommitResult Commit);

/// <summary>Atomically persists entity state, direct-Transition envelopes, and Process handoff receipts in SQLite.</summary>
/// <remarks>
/// Operations own fresh connections and immediate write transactions. Exact retries return retained snapshots,
/// independent of subsequent entity mutations. Process receipt envelopes are handoff evidence and never appear
/// in the entity outbox. Persistence does not acknowledge delivery or make external effects exactly once.
/// Schemas must be explicitly applied. Native I/O is synchronous; cancellation is checked before commit, never
/// reclassified after a successful commit. This immutable repository can be shared across threads.
/// </remarks>
public sealed class SqliteEntityOutboxRepository : IEntityOutboxRepository, IEntityTransitionOperationRepository
{
    const string DirectIdPrefix = "direct/v1/";
    const string ProcessIdPrefix = "process/v1/";
    static readonly JsonSerializerOptions JsonOptions = EntityStorageJson.CreateOptions();
    readonly SqliteDatabase database;
    readonly SqliteEntityRepository entities;
    readonly SqliteEntityOutboxSql sql;

    /// <summary>Creates an outbox repository over explicitly initialized entity and auxiliary schemas.</summary>
    /// <param name="database">Immutable connection runtime; determines durability and bounded lock retry policy.</param>
    /// <param name="mapping">Canonical entity definition and physical scalar mapping.</param>
    /// <param name="maximumReceiptBytes">Maximum canonical bytes per receipt and per outbox read; defaults to 16 MiB.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The byte limit is not positive.</exception>
    /// <exception cref="ArgumentException">A derived auxiliary table name is invalid.</exception>
    /// <exception cref="EntityShapeGraphValidationException">The mapping's canonical entity graph is invalid.</exception>
    public SqliteEntityOutboxRepository(SqliteDatabase database, SqliteEntityRepositoryMapping mapping,
        int maximumReceiptBytes = 16 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReceiptBytes);
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        entities = new(database, mapping);
        sql = new(mapping.TableName);
        MaximumReceiptBytes = maximumReceiptBytes;
    }

    /// <summary>Physical entity mapping; its migration must be applied separately from the auxiliary migration.</summary>
    public SqliteEntityRepositoryMapping Mapping => entities.Mapping;
    /// <summary>Original version-one auxiliary migration; use <see cref="Migrations"/> to initialize or upgrade a repository.</summary>
    public SqliteMigration InitialMigration => sql.InitialMigration;
    /// <summary>Complete ordered auxiliary schema plan, including the explicit receipt-encoding revision.</summary>
    public ImmutableArray<SqliteMigration> Migrations => [sql.InitialMigration, sql.EncodingMigration];
    /// <summary>Table containing versioned canonical receipt payloads; shared by both publication authorities.</summary>
    public string ReceiptsTable => sql.ReceiptsTable;
    /// <summary>Unique emission-to-receipt index for direct-Transition commits.</summary>
    public string EmissionsTable => sql.EmissionsTable;
    /// <summary>Unique subject-to-receipt index for Process-invoked entity creation.</summary>
    public string CreationsTable => sql.CreationsTable;
    /// <summary>Maximum canonical payload bytes per receipt and aggregate bytes per outbox read.</summary>
    public int MaximumReceiptBytes { get; }
    /// <inheritdoc />
    public EntityDefinition EntityDefinition => entities.EntityDefinition;
    /// <inheritdoc />
    public string EntityType => entities.EntityType;
    /// <inheritdoc />
    public EntityBatchCapabilities BatchCapabilities => entities.BatchCapabilities;
    /// <inheritdoc />
    public EntityTransitionOperationCapabilities TransitionOperationCapabilities => EntityTransitionOperationCapabilities.AtomicStateAndReceipt;
    /// <inheritdoc />
    public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => entities.TryGet(context, id, options);
    /// <inheritdoc />
    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => entities.Upsert(context, write);
    /// <inheritdoc />
    public Task<EntityBatchWriteResult> UpsertBatch(OperationContext context, EntityBatchWriteRequest request) => entities.UpsertBatch(context, request);

    /// <summary>Commits state and direct-Transition envelopes, or replays their exact original atomic result.</summary>
    /// <param name="context">Cancellation, attribution, and operation time.</param>
    /// <param name="commit">Canonical candidate and ordered durable direct-Transition envelopes.</param>
    /// <returns>Original committed snapshot and envelopes, including on retry after later state changes.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Candidate keys, shape, or scalar values are invalid.</exception>
    /// <exception cref="InvalidOperationException">Emission identities conflict, retained evidence is invalid, the byte limit is exceeded, or stored shape differs.</exception>
    /// <exception cref="ObservationConcurrencyConflictException">A first-time write has a stale or absent CAS target.</exception>
    /// <exception cref="NotSupportedException">A retained receipt uses an unsupported encoding; replay requires an explicit evidence migration.</exception>
    /// <exception cref="SemanticRuleViolationException">Candidate state violates the canonical entity definition.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before commit.</exception>
    /// <exception cref="SqliteException">SQL, locking, storage, or commit fails; all uncommitted changes roll back.</exception>
    public Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        context.ThrowIfCancellationRequested();
        var partition = entities.ValidateWrite(commit.Write);
        using var connection = database.OpenConnection(context.CancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        string? retainedId = null;
        var retainedCount = 0;
        foreach (var envelope in commit.Envelopes)
        {
            context.ThrowIfCancellationRequested();
            var id = ReadIndex(connection, transaction, sql.ReadEmission, envelope.Context.EmissionId.Value);
            if (id is null) continue;
            if (retainedId is not null && id != retainedId) throw OutboxConflict();
            retainedId = id;
            retainedCount++;
        }
        if (retainedId is not null)
        {
            if (retainedCount != commit.Envelopes.Length) throw OutboxConflict();
            ValidateDirectId(retainedId);
            var retained = ReadReceipt<EntityCommitResult>(connection, transaction, retainedId, Direct)
                ?? throw InvalidReceipt();
            ValidateDirect(retained);
            if (retained.Entity.Entity != commit.Write.Entity
                || !SameEnvelopes(retained.Envelopes, commit.Envelopes))
                throw OutboxConflict();
            context.ThrowIfCancellationRequested();
            return Task.FromResult(retained);
        }
        var snapshot = entities.UpsertCore(context, connection, transaction, commit.Write, partition);
        var result = new EntityCommitResult(snapshot, commit.Envelopes);
        // Without emissions there is no stable caller-supplied retry identity: ordinary write semantics apply.
        if (!commit.Envelopes.IsEmpty)
        {
            var id = DirectIdPrefix + Guid.NewGuid().ToString("N");
            InsertReceipt(connection, transaction, id, Direct, result);
            foreach (var envelope in commit.Envelopes)
                InsertIndex(connection, transaction, sql.InsertEmission, envelope.Context.EmissionId.Value, id);
        }
        context.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The request subject belongs to another entity type or its identity has invalid text.</exception>
    /// <exception cref="InvalidOperationException">Retained canonical evidence or its mapped shape is invalid, or exceeds the byte limit.</exception>
    /// <exception cref="NotSupportedException">A retained receipt uses an unsupported encoding and needs explicit migration.</exception>
    /// <exception cref="SemanticRuleViolationException">Retained state violates the canonical entity definition.</exception>
    /// <exception cref="SqliteException">Opening or reading the database fails.</exception>
    public Task<EntityTransitionOperationResult> TryGetTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) =>
        Lookup(context, request, creation: false);
    /// <inheritdoc />
    /// <exception cref="ArgumentException">The request subject belongs to another entity type or its identity has invalid text.</exception>
    /// <exception cref="InvalidOperationException">The creation index or retained evidence is invalid, or exceeds the byte limit.</exception>
    /// <exception cref="NotSupportedException">A retained receipt uses an unsupported encoding and needs explicit migration.</exception>
    /// <exception cref="SemanticRuleViolationException">Retained state violates the canonical entity definition.</exception>
    /// <exception cref="SqliteException">Opening or reading the database fails.</exception>
    public Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) =>
        Lookup(context, request, creation: true);

    /// <summary>Commits candidate state and exact Process handoff evidence under one SQLite transaction.</summary>
    /// <param name="context">Cancellation and UTC physical commit observation.</param>
    /// <param name="commit">Canonical operation intent, state, result, and provenance.</param>
    /// <returns>Committed or replayed original receipt, or structured identity, subject-state, or CAS conflict.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Candidate keys, scalar values, or subject mapping are invalid.</exception>
    /// <exception cref="InvalidOperationException">Retained evidence is invalid, the byte limit is exceeded, or stored shape differs.</exception>
    /// <exception cref="NotSupportedException">A retained receipt uses an unsupported encoding and needs explicit migration.</exception>
    /// <exception cref="SemanticRuleViolationException">The candidate violates the canonical entity definition.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before commit.</exception>
    /// <exception cref="SqliteException">SQL, locking, storage, or commit fails; state and receipt roll back together.</exception>
    public Task<EntityTransitionOperationResult> CommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        ValidateRequest(commit.Request);
        context.ThrowIfCancellationRequested();
        var partition = entities.ValidateWrite(commit.Write);
        using var connection = database.OpenConnection(context.CancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var id = OperationId(commit.Request);
        var retained = ReadOperation(connection, transaction, id);
        if (retained is not null) return Complete(retained.Replay(commit));
        if (commit.SubjectCondition == EntityTransitionSubjectCondition.MustBeAbsent)
        {
            retained = ReadCreation(connection, transaction, commit.Request);
            if (retained is not null)
            {
                var replay = retained.ReplayCreation(commit.Request);
                // Match the reference in-memory creation policy: replacement attempts keep original handoff evidence.
                if (replay.Receipt is not null && (retained.Entity.Entity != commit.Write.Entity
                    || retained.Commit.DecisionKind != commit.DecisionKind || retained.Result.Value != commit.Result.Value))
                    replay = Rejected(EntityTransitionOperationDisposition.IdentityConflict,
                        EntityTransitionOperationDiagnosticCodes.IdentityConflict, "Creation intent has a different candidate state or typed result.", "/commit");
                return Complete(replay);
            }
            if (entities.Exists(connection, transaction, commit.Request.Subject.EntityId.Value))
                return Complete(Rejected(EntityTransitionOperationDisposition.SubjectStateConflict,
                    EntityTransitionOperationDiagnosticCodes.SubjectStateConflict, "The creation subject already exists.", "/write/subjectCondition"));
        }
        EntitySnapshot snapshot;
        try { snapshot = entities.UpsertCore(context, connection, transaction, commit.Write, partition); }
        catch (ObservationConcurrencyConflictException)
        {
            return Complete(Rejected(EntityTransitionOperationDisposition.ConcurrencyConflict,
                EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict, "The subject no longer matches its storage concurrency fence.", "/write/expectedConcurrencyToken"));
        }
        var receipt = new EntityTransitionOperationReceipt(commit, snapshot, context.UtcNow);
        InsertReceipt(connection, transaction, id, Process, receipt);
        if (commit.SubjectCondition == EntityTransitionSubjectCondition.MustBeAbsent)
            InsertIndex(connection, transaction, sql.InsertCreation, commit.Request.Subject.EntityId.Value, id);
        context.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.FromResult(EntityTransitionOperationResult.Committed(receipt));

        Task<EntityTransitionOperationResult> Complete(EntityTransitionOperationResult result)
        {
            context.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    /// <summary>Reads retained direct-Transition commits in cursor order with bounded count and canonical byte budget.</summary>
    /// <param name="context">Cancellation checked while reading and before return.</param>
    /// <param name="afterSequence">Exclusive nonnegative cursor; zero starts at the beginning.</param>
    /// <param name="maximumCommits">Maximum returned commits, from one through 1000; the byte budget can shorten a page.</param>
    /// <returns>Complete commits up to MaximumReceiptBytes in aggregate; empty when caught up. No delivery is acknowledged.</returns>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The cursor or count is outside its documented range.</exception>
    /// <exception cref="InvalidOperationException">Retained evidence is corrupt or exceeds the configured byte limit.</exception>
    /// <exception cref="NotSupportedException">A retained receipt uses an unsupported encoding and needs explicit migration.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before returning the page.</exception>
    /// <exception cref="SqliteException">Reading the database fails.</exception>
    public Task<ImmutableArray<SqliteEntityOutboxEntry>> ReadOutbox(OperationContext context, long afterSequence = 0, int maximumCommits = 100)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (maximumCommits is < 1 or > MaximumReadCommits) throw new ArgumentOutOfRangeException(nameof(maximumCommits));
        using var connection = database.OpenConnection(context.CancellationToken);
        using var command = Command(connection, null, sql.ReadOutbox, (Kind, Direct), (Sequence, afterSequence));
        using var reader = command.ExecuteReader();
        var result = ImmutableArray.CreateBuilder<SqliteEntityOutboxEntry>();
        long bytes = 0;
        while (result.Count < maximumCommits && reader.Read())
        {
            context.ThrowIfCancellationRequested();
            var length = PayloadLength(reader, ordinal: 3);
            if (bytes + length > MaximumReceiptBytes) break;
            ValidateDirectId(reader.GetString(1));
            var commit = Decode<EntityCommitResult>(reader, kindOrdinal: 2, expectedKind: Direct);
            ValidateDirect(commit);
            result.Add(new(reader.GetInt64(0), commit));
            bytes += length;
        }
        context.ThrowIfCancellationRequested();
        return Task.FromResult(result.ToImmutable());
    }

    Task<EntityTransitionOperationResult> Lookup(OperationContext context, EntityTransitionOperationRequest request, bool creation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateRequest(request);
        using var connection = database.OpenConnection(context.CancellationToken);
        // One read snapshot covers the index and receipt, even with future explicit retention maintenance.
        using var transaction = connection.BeginTransaction(deferred: true);
        var receipt = creation ? ReadCreation(connection, transaction, request) : ReadOperation(connection, transaction, OperationId(request));
        context.ThrowIfCancellationRequested();
        return Task.FromResult(receipt is null ? EntityTransitionOperationResult.NotFound()
            : creation ? receipt.ReplayCreation(request) : receipt.Replay(request));
    }

    EntityTransitionOperationReceipt? ReadCreation(SqliteConnection connection, SqliteTransaction transaction, EntityTransitionOperationRequest request)
    {
        var id = ReadIndex(connection, transaction, sql.ReadCreation, request.Subject.EntityId.Value);
        if (id is null) return null;
        var receipt = ReadOperation(connection, transaction, id) ?? throw InvalidReceipt();
        if (receipt.Commit.SubjectCondition != EntityTransitionSubjectCondition.MustBeAbsent || receipt.Request.Subject != request.Subject)
            throw InvalidReceipt();
        return receipt;
    }

    EntityTransitionOperationReceipt? ReadOperation(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        var receipt = ReadReceipt<EntityTransitionOperationReceipt>(connection, transaction, id, Process);
        if (receipt is not null)
        {
            ValidateSnapshot(receipt.Entity);
            ValidateRequest(receipt.Request);
            if (OperationId(receipt.Request) != id) throw InvalidReceipt();
        }
        return receipt;
    }

    T? ReadReceipt<T>(SqliteConnection connection, SqliteTransaction transaction, string id, int kind) where T : class
    {
        using var command = Command(connection, transaction, sql.ReadReceipt, (Id, id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Decode<T>(reader, kindOrdinal: 0, kind) : null;
    }

    T Decode<T>(SqliteDataReader reader, int kindOrdinal, int expectedKind) where T : class
    {
        if (reader.GetInt32(kindOrdinal) != expectedKind) throw InvalidReceipt();
        if (reader.GetInt32(kindOrdinal + 3) != EntityStorageJson.FormatVersion)
            throw new NotSupportedException("This SQLite receipt uses an unsupported encoding. Migrate retained evidence with its original shape before retrying; it cannot be treated as a missing operation.");
        var length = PayloadLength(reader, kindOrdinal + 1);
        var bytes = new byte[length];
        if (reader.GetBytes(kindOrdinal + 1, 0, bytes, 0, length) != length || HashBytes(bytes) != reader.GetString(kindOrdinal + 2))
            throw InvalidReceipt();
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw InvalidReceipt();
            if (!bytes.AsSpan().SequenceEqual(StrictDocumentJson.GetCanonicalBytes(value, JsonOptions))) throw InvalidReceipt();
            return value;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { throw new InvalidOperationException("The retained SQLite receipt has invalid canonical evidence.", exception); }
    }

    int PayloadLength(SqliteDataReader reader, int ordinal)
    {
        var length = reader.GetBytes(ordinal, 0, buffer: null, 0, 0);
        if (length is <= 0 || length > MaximumReceiptBytes) throw InvalidReceipt();
        return checked((int)length);
    }

    void InsertReceipt<T>(SqliteConnection connection, SqliteTransaction transaction, string id, int kind, T value) where T : class
    {
        var bytes = StrictDocumentJson.GetCanonicalBytes(value, JsonOptions);
        if (bytes.Length > MaximumReceiptBytes) throw new InvalidOperationException($"Canonical SQLite receipt exceeds {MaximumReceiptBytes} bytes.");
        using var command = Command(connection, transaction, sql.InsertReceipt, (Id, id), (Kind, kind), (Content, bytes), (Hash, HashBytes(bytes)), (Format, EntityStorageJson.FormatVersion));
        command.ExecuteNonQuery();
    }

    string? ReadIndex(SqliteConnection connection, SqliteTransaction transaction, SqlCommandTemplate template, string id)
    {
        using var command = Command(connection, transaction, template, (Id, id));
        return command.ExecuteScalar() as string;
    }

    void InsertIndex(SqliteConnection connection, SqliteTransaction transaction, SqlCommandTemplate template, string id, string receipt)
    {
        using var command = Command(connection, transaction, template, (Id, id), (Receipt, receipt));
        command.ExecuteNonQuery();
    }

    SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, SqlCommandTemplate template,
        params ReadOnlySpan<(string Binding, object Value)> values)
    {
        var command = database.CreateCommand(connection, transaction, template.Text);
        try
        {
            foreach (var slot in template.Parameters)
            {
                object? value = slot.ConstantValue;
                if (slot.Kind == SqlParameterBindingKind.Runtime)
                {
                    foreach (var binding in values)
                        if (binding.Binding == slot.Binding) { value = binding.Value; break; }
                    if (value is null) throw new InvalidOperationException($"Missing SQLite receipt parameter '{slot.Binding}'.");
                }
                command.Parameters.Add(new SqliteParameter(slot.Placeholder, value));
            }
            return command;
        }
        catch { command.Dispose(); throw; }
    }

    void ValidateRequest(EntityTransitionOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Subject.EntityType.Value != EntityType) throw new ArgumentException("The operation subject belongs to another entity type.", nameof(request));
        SqliteScalarCodec.RequireText(request.Subject.EntityId.Value);
    }

    void ValidateSnapshot(EntitySnapshot snapshot)
    {
        var partition = entities.ValidateWrite(new(snapshot.Entity, snapshot.ConcurrencyToken));
        if (partition != snapshot.PartitionKey || snapshot.LoadedFields is not null) throw InvalidReceipt();
    }

    void ValidateDirect(EntityCommitResult result)
    {
        ValidateSnapshot(result.Entity);
        _ = new EntityOutboxCommit(new(result.Entity.Entity), result.Envelopes);
        if (result.Envelopes.IsEmpty) throw InvalidReceipt();
    }

    static void ValidateDirectId(string id)
    {
        if (!id.StartsWith(DirectIdPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(id.AsSpan(DirectIdPrefix.Length), "N", out _)) throw InvalidReceipt();
    }

    static string OperationId(EntityTransitionOperationRequest request) => ProcessIdPrefix + HashBytes(StrictDocumentJson.GetCanonicalBytes(request.Operation, JsonOptions));
    static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static bool SameEnvelopes(ImmutableArray<InteractionEnvelope> left, ImmutableArray<InteractionEnvelope> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(left[index])
                != InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(right[index])) return false;
        return true;
    }
    static InvalidOperationException InvalidReceipt() => new("The retained SQLite receipt is invalid, incomplete, or exceeds the configured byte limit.");
    static InvalidOperationException OutboxConflict() => new("Entity outbox emission identities are retained with different state, ordered envelopes, or commit membership.");
    static EntityTransitionOperationResult Rejected(EntityTransitionOperationDisposition disposition, string code, string message, string location) =>
        EntityTransitionOperationResult.Rejected(disposition, new(code, DiagnosticSeverity.Error, message, location));
}
