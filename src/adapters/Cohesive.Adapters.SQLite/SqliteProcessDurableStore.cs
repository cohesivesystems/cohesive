using System.Security.Cryptography;
using System.Text;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Storage.Commits;

namespace Cohesive.Adapters.SQLite;

/// <summary>Bounded SQLite persistence for canonical Process aggregates, receipts, inbox, outbox and worker fences.</summary>
/// <remarks>
/// Apply Schema explicitly. Each instance occupies one portable document in the shared commit schema. The existing
/// reference store owns mutation semantics; an immediate SQLite transaction serializes reduction and publication.
/// Local values belong to the Process aggregate, not arbitrary application tables. Calls may run concurrently;
/// SQLite serializes writers. The adapter uses the local system UTC clock for physical lease checks, not the
/// caller's replay clock. This bounded profile rewrites the complete instance including historical receipts.
/// Keep receipts for the recovery horizon; no pruning, distributed clock qualification or scheduler is provided.
/// Native I/O is synchronous. Failures around commit require exact retry, never a fresh operation identity.
/// </remarks>
public sealed class SqliteProcessDurableStore : IProcessDurableStore
{
    const string Target = "cohesive.processes.aggregate/v1";
    static readonly ValueContract Text = new(new ScalarTypeRef(ScalarTypeKind.String));
    readonly SqliteDatabase database;
    readonly SqliteStorageCommitExecutor executor;
    readonly string authorityId;
    readonly int maximumAggregateBytes;

    /// <summary>Creates a bounded Process authority without opening or migrating its database.</summary>
    /// <param name="database">Caller-owned database configured for FULL durability.</param>
    /// <param name="authorityId">Stable namespace isolating independent Process authorities in the same file.</param>
    /// <param name="maximumAggregateBytes">Positive maximum UTF-8 canonical document size per instance, including receipts; defaults to 4 MiB.</param>
    /// <exception cref="ArgumentNullException">Database or authority is null.</exception>
    /// <exception cref="ArgumentException">Authority is blank/invalid or durability is weaker than FULL.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The size bound is not positive.</exception>
    public SqliteProcessDurableStore(SqliteDatabase database, string authorityId, int maximumAggregateBytes = 4 * 1024 * 1024)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        _ = new StorageCommitAddress(Target, authorityId, "validation");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAggregateBytes);
        this.authorityId = authorityId;
        this.maximumAggregateBytes = maximumAggregateBytes;
        // A JSON string may escape each byte as six characters. Bound native reads before portable-value decoding.
        executor = new(database, maximumStoredPayloadBytes: (long)maximumAggregateBytes * 6 + 128 * 1024);
        Capabilities = new(SupportsAtomicAggregateCommit: true, SupportsCompareAndSwap: true,
            SupportsWorkerFencing: true, MaxCommitBytes: maximumAggregateBytes);
    }

    /// <summary>Shared atomic item/receipt schema; apply once during bootstrap.</summary>
    public static SqliteSchema Schema => SqliteStorageCommitExecutor.Schema;

    /// <inheritdoc />
    public ProcessDurableStoreCapabilities Capabilities { get; }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">Stored evidence is corrupt or exceeds the configured aggregate bound.</exception>
    /// <exception cref="System.Text.Json.JsonException">Stored canonical JSON is invalid.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Native database access or commit fails; exact retry may be required.</exception>
    public Task<ProcessDurableStoreSnapshot?> LoadAsync(
        OperationContext context,
        ProcessInstanceId instanceId) =>
        AccessAsync(
            context: context,
            instanceId: instanceId,
            operation: (store, providerContext) => store.LoadAsync(
                context: providerContext,
                instanceId: instanceId), mutate: false);

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">Stored evidence is corrupt or exceeds the configured aggregate bound.</exception>
    /// <exception cref="System.Text.Json.JsonException">Stored canonical JSON is invalid.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Native database access or commit fails; exact retry may be required.</exception>
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
    /// <exception cref="InvalidDataException">Stored evidence is corrupt or exceeds the configured aggregate bound.</exception>
    /// <exception cref="System.Text.Json.JsonException">Stored canonical JSON is invalid.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Native database access or commit fails; exact retry may be required.</exception>
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
    /// <exception cref="InvalidDataException">Stored evidence is corrupt or exceeds the configured aggregate bound.</exception>
    /// <exception cref="System.Text.Json.JsonException">Stored canonical JSON is invalid.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Native database access or commit fails; exact retry may be required.</exception>
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
    /// <exception cref="InvalidDataException">Stored evidence is corrupt or exceeds the configured aggregate bound.</exception>
    /// <exception cref="System.Text.Json.JsonException">Stored canonical JSON is invalid.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Native database access or commit fails; exact retry may be required.</exception>
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
    /// <exception cref="InvalidDataException">Stored evidence is corrupt or exceeds the configured aggregate bound.</exception>
    /// <exception cref="System.Text.Json.JsonException">Stored canonical JSON is invalid.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Native database access or commit fails; exact retry may be required.</exception>
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

    async Task<TResult> AccessAsync<TResult>(OperationContext context, ProcessInstanceId instanceId,
        Func<InMemoryProcessDurableStore, OperationContext, Task<TResult>> operation, bool mutate = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = new StorageCommitAddress(Target, authorityId, instanceId.Value);
        context.ThrowIfCancellationRequested();
        using var connection = database.OpenConnection(context.CancellationToken);
        using var transaction = connection.BeginTransaction(deferred: !mutate);
        using var commands = new SqliteCommandScope(database, connection, transaction);
        var item = executor.ReadInTransaction(context, commands, address);
        string? original = null;
        var document = ProcessDurableStoreDocument.Empty();
        if (item is not null)
        {
            original = item.Value.Value?.String ?? throw new InvalidDataException("Process aggregate must be a portable string.");
            RequireBound(original);
            document = ProcessDurableStoreJsonSerializer.Deserialize(original);
            if (document.Aggregates.Length != 1 || document.Aggregates[0].InstanceId != instanceId)
                throw new InvalidDataException("Process aggregate belongs to another instance.");
            var digest = Digest(original);
            var receipt = executor.ReconcileInTransaction(context, commands, new(ReceiptAddress(instanceId, digest), item.Token.Value));
            if (receipt.Disposition != StorageCommitDisposition.Replayed || receipt.Receipt!.Result != Value(digest))
                throw new InvalidDataException("Process aggregate differs from its original atomic receipt.");
        }
        var reference = new InMemoryProcessDurableStore(document);
        var result = await operation(reference, context with { TimeProvider = TimeProvider.System }).ConfigureAwait(false);
        if (!mutate) return result;
        var replacement = reference.CaptureDocument();
        if (replacement.Aggregates.IsEmpty) return result;
        if (replacement.Aggregates.Length != 1 || replacement.Aggregates[0].InstanceId != instanceId)
            throw new InvalidOperationException("A Process mutation must retain exactly its addressed instance.");
        var json = ProcessDurableStoreJsonSerializer.Serialize(replacement);
        if (json == original) return result;
        RequireBound(json);
        var hash = Digest(json);
        var intent = new StorageCommitIntent(ReceiptAddress(instanceId, hash), [new(address, Value(json), item?.Token)], Value(hash));
        var committed = executor.CommitInTransaction(context, commands, intent);
        if (committed.Disposition != StorageCommitDisposition.Committed)
            throw new InvalidDataException("Process aggregate atomic commit was rejected: " + committed.Disposition);
        context.ThrowIfCancellationRequested();
        transaction.Commit();
        return result;
    }

    void RequireBound(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > maximumAggregateBytes)
            throw new InvalidDataException("Process aggregate exceeds the configured UTF-8 byte limit; no mutation was committed.");
    }

    StorageCommitAddress ReceiptAddress(ProcessInstanceId instance, string digest) =>
        new(Target, authorityId, instance.Value + "/" + digest);
    static string Digest(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    static PortableValue Value(string text) => PortableValue.Concrete(Text, ObservationValue.FromString(text));
}
