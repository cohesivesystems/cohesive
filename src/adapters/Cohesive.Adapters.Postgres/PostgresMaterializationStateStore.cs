using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Control;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>
/// PostgreSQL durability for materialization progress, synchronization work, and Control state.
/// </summary>
/// <remarks>
/// Each authority retains canonical applied operations as a bounded JSONB reference ledger. On access, the adapter
/// replays that ledger through the corresponding in-memory reference implementation, applies the requested
/// operation, and appends only a newly applied mutation under one serializable row lock. This keeps mutation,
/// fencing, replay, and compare-and-swap semantics in one provider-neutral implementation while the first
/// PostgreSQL projection favors inspectability over high mutation throughput.
///
/// The caller owns the supplied <see cref="NpgsqlDataSource"/>. Call <see cref="EnsureCreatedAsync"/> explicitly
/// during bootstrap; normal state operations never perform schema DDL.
/// </remarks>
public sealed class PostgresMaterializationStateStore :
    IMaterializationProgressStore,
    IMaterializationSynchronizationWorkStore,
    IMaterializationIndexSyncControlStateStore
{
    const string ProgressLedger = "progress";
    const string SynchronizationLedger = "synchronization";
    const string ControlLedger = "control";
    const string ProgressSchema = "cohesive-postgres-materialization-progress-ledger/v1";
    const string SynchronizationSchema = "cohesive-postgres-materialization-synchronization-ledger/v1";
    const string ControlSchema = "cohesive-postgres-materialization-control-ledger/v1";
    static readonly JsonSerializerOptions JsonOptions = StrictDocumentJson.CreateOptions();

    readonly PostgresMaterializationDocumentAuthority authority;

    /// <summary>Creates PostgreSQL materialization state authorities over a caller-owned data source.</summary>
    /// <param name="dataSource">Caller-owned PostgreSQL connection pool.</param>
    /// <param name="options">Exact authority rows, table binding, and physical document limit.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PostgresMaterializationStateStore(
        NpgsqlDataSource dataSource,
        PostgresMaterializationStateStoreOptions options)
    {
        authority = new(
            dataSource: dataSource ?? throw new ArgumentNullException(nameof(dataSource)),
            options: options ?? throw new ArgumentNullException(nameof(options)));
    }

    /// <summary>Creates the configured schema and ledger table when absent.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <returns>A task completing after DDL commits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before DDL commits.</exception>
    /// <exception cref="NpgsqlException">PostgreSQL rejects or cannot execute the DDL.</exception>
    public Task EnsureCreatedAsync(OperationContext context) => authority.EnsureCreatedAsync(context);

    /// <inheritdoc />
    public Task<MaterializationProgressSnapshot?> LoadAsync(
        OperationContext context,
        MaterializationProgressKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return authority.AccessAsync(
            context: context,
            authorityKind: ProgressLedger,
            empty: ProgressLedgerDocument.Empty(),
            deserialize: Deserialize<ProgressLedgerDocument>,
            serialize: static document => JsonSerializer.Serialize(document, JsonOptions),
            operation: async (document, providerContext) =>
            {
                var store = await ReplayProgressAsync(document, providerContext).ConfigureAwait(false);
                var result = await store.LoadAsync(providerContext, key).ConfigureAwait(false);
                return (result, document);
            });
    }

    /// <inheritdoc />
    public Task<MaterializationProgressMutationResult> AcquireFenceAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision? expectedRevision,
        string owner) =>
        MutateProgressAsync(
            context,
            ProgressOperation.Acquire(key, mutationId, expectedRevision, owner));

    /// <inheritdoc />
    public Task<MaterializationProgressMutationResult> SaveCheckpointAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationApplicationCheckpoint checkpoint) =>
        MutateProgressAsync(
            context,
            ProgressOperation.Checkpoint(
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                checkpoint));

    /// <inheritdoc />
    public Task<MaterializationProgressMutationResult> SaveSettlementAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationSourceSettlement settlement) =>
        MutateProgressAsync(
            context,
            ProgressOperation.Settlement(
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                settlement));

    /// <inheritdoc />
    Task<MaterializationSynchronizationWorkSnapshot?> IMaterializationSynchronizationWorkStore.LoadAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return authority.AccessAsync(
            context: context,
            authorityKind: SynchronizationLedger,
            empty: SynchronizationLedgerDocument.Empty(),
            deserialize: Deserialize<SynchronizationLedgerDocument>,
            serialize: static document => JsonSerializer.Serialize(document, JsonOptions),
            operation: async (document, providerContext) =>
            {
                var store = await ReplaySynchronizationAsync(document, providerContext).ConfigureAwait(false);
                var result = await store.LoadAsync(providerContext, key).ConfigureAwait(false);
                return (result, document);
            });
    }

    /// <inheritdoc />
    Task<MaterializationSynchronizationWorkMutationResult>
        IMaterializationSynchronizationWorkStore.AcquireFenceAsync(
            OperationContext context,
            MaterializationSynchronizationWorkKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision? expectedRevision,
            string owner) =>
        MutateSynchronizationAsync(
            context,
            SynchronizationOperation.Acquire(key, mutationId, expectedRevision, owner));

    /// <inheritdoc />
    public Task<MaterializationSynchronizationWorkMutationResult> PrepareAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationSynchronizationWorkIntent intent) =>
        MutateSynchronizationAsync(
            context,
            SynchronizationOperation.Prepare(
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                intent));

    /// <inheritdoc />
    public Task<MaterializationSynchronizationWorkMutationResult> CompleteAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationProgressMutationId preparationId,
        MaterializationItemVersion? version) =>
        MutateSynchronizationAsync(
            context,
            SynchronizationOperation.Complete(
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                preparationId,
                version));

    /// <inheritdoc />
    public Task<MaterializationSynchronizationWorkMutationResult> SaveActivationAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationGenerationActivationState activation) =>
        MutateSynchronizationAsync(
            context,
            SynchronizationOperation.Activation(
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                activation));

    /// <inheritdoc />
    public async ValueTask<ControlLoopState?> ReadAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return await authority.AccessAsync(
                context: context,
                authorityKind: ControlLedger,
                empty: ControlLedgerDocument.Empty(),
                deserialize: Deserialize<ControlLedgerDocument>,
                serialize: static document => JsonSerializer.Serialize(document, JsonOptions),
                operation: async (document, providerContext) =>
                {
                    var store = await ReplayControlAsync(document, providerContext).ConfigureAwait(false);
                    var result = await store.ReadAsync(providerContext, key).ConfigureAwait(false);
                    return (result, document);
                })
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationIndexSyncControlWriteResult> ReadMutationAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(key);
        return await authority.AccessAsync(
                context: context,
                authorityKind: ControlLedger,
                empty: ControlLedgerDocument.Empty(),
                deserialize: Deserialize<ControlLedgerDocument>,
                serialize: static document => JsonSerializer.Serialize(document, JsonOptions),
                operation: async (document, providerContext) =>
                {
                    var store = await ReplayControlAsync(document, providerContext).ConfigureAwait(false);
                    var result = await store.ReadMutationAsync(
                            providerContext,
                            key,
                            mutationId,
                            mutationFingerprint)
                        .ConfigureAwait(false);
                    return (result, document);
                })
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<MaterializationIndexSyncControlWriteResult> CreateAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint,
        ControlLoopState state) =>
        MutateControlAsync(
            context,
            ControlOperation.Create(key, mutationId, mutationFingerprint, state));

    /// <inheritdoc />
    public ValueTask<MaterializationIndexSyncControlWriteResult> CompareExchangeAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint,
        ControlRevision expectedRevision,
        ControlLoopState state) =>
        MutateControlAsync(
            context,
            ControlOperation.CompareExchange(
                key,
                mutationId,
                mutationFingerprint,
                expectedRevision,
                state));

    Task<MaterializationProgressMutationResult> MutateProgressAsync(
        OperationContext context,
        ProgressOperation operation) =>
        authority.AccessAsync(
            context: context,
            authorityKind: ProgressLedger,
            empty: ProgressLedgerDocument.Empty(),
            deserialize: Deserialize<ProgressLedgerDocument>,
            serialize: static document => JsonSerializer.Serialize(document, JsonOptions),
            operation: async (document, providerContext) =>
            {
                var store = await ReplayProgressAsync(document, providerContext).ConfigureAwait(false);
                var result = await operation.ApplyAsync(store, providerContext).ConfigureAwait(false);
                return (
                    result,
                    result.Disposition == MaterializationProgressMutationDisposition.Applied
                        ? document.Append(operation)
                        : document);
            });

    Task<MaterializationSynchronizationWorkMutationResult> MutateSynchronizationAsync(
        OperationContext context,
        SynchronizationOperation operation) =>
        authority.AccessAsync(
            context: context,
            authorityKind: SynchronizationLedger,
            empty: SynchronizationLedgerDocument.Empty(),
            deserialize: Deserialize<SynchronizationLedgerDocument>,
            serialize: static document => JsonSerializer.Serialize(document, JsonOptions),
            operation: async (document, providerContext) =>
            {
                var store = await ReplaySynchronizationAsync(document, providerContext).ConfigureAwait(false);
                var result = await operation.ApplyAsync(store, providerContext).ConfigureAwait(false);
                return (
                    result,
                    result.Disposition == MaterializationSynchronizationWorkMutationDisposition.Applied
                        ? document.Append(operation)
                        : document);
            });

    async ValueTask<MaterializationIndexSyncControlWriteResult> MutateControlAsync(
        OperationContext context,
        ControlOperation operation) =>
        await authority.AccessAsync(
                context: context,
                authorityKind: ControlLedger,
                empty: ControlLedgerDocument.Empty(),
                deserialize: Deserialize<ControlLedgerDocument>,
                serialize: static document => JsonSerializer.Serialize(document, JsonOptions),
                operation: async (document, providerContext) =>
                {
                    var store = await ReplayControlAsync(document, providerContext).ConfigureAwait(false);
                    var result = await operation.ApplyAsync(store, providerContext).ConfigureAwait(false);
                    return (
                        result,
                        result.Disposition == MaterializationIndexSyncControlWriteDisposition.Applied
                            ? document.Append(operation)
                            : document);
                })
            .ConfigureAwait(false);

    static T Deserialize<T>(string json)
        where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new JsonException($"A PostgreSQL materialization ledger '{typeof(T).Name}' cannot be null.");

    static async Task<InMemoryMaterializationProgressStore> ReplayProgressAsync(
        ProgressLedgerDocument document,
        OperationContext context)
    {
        var store = new InMemoryMaterializationProgressStore();
        foreach (var operation in document.Operations)
        {
            var result = await operation.ApplyAsync(store, context).ConfigureAwait(false);
            if (result.Disposition != MaterializationProgressMutationDisposition.Applied)
            {
                throw new InvalidOperationException(
                    "A retained PostgreSQL progress operation did not replay as an applied prefix.");
            }
        }
        return store;
    }

    static async Task<InMemoryMaterializationSynchronizationWorkStore> ReplaySynchronizationAsync(
        SynchronizationLedgerDocument document,
        OperationContext context)
    {
        var store = new InMemoryMaterializationSynchronizationWorkStore();
        foreach (var operation in document.Operations)
        {
            var result = await operation.ApplyAsync(store, context).ConfigureAwait(false);
            if (result.Disposition != MaterializationSynchronizationWorkMutationDisposition.Applied)
            {
                throw new InvalidOperationException(
                    "A retained PostgreSQL synchronization operation did not replay as an applied prefix.");
            }
        }
        return store;
    }

    static async Task<InMemoryMaterializationIndexSyncControlStateStore> ReplayControlAsync(
        ControlLedgerDocument document,
        OperationContext context)
    {
        var store = new InMemoryMaterializationIndexSyncControlStateStore();
        foreach (var operation in document.Operations)
        {
            var result = await operation.ApplyAsync(store, context).ConfigureAwait(false);
            if (result.Disposition != MaterializationIndexSyncControlWriteDisposition.Applied)
            {
                throw new InvalidOperationException(
                    "A retained PostgreSQL Control operation did not replay as an applied prefix.");
            }
        }
        return store;
    }

    sealed record ProgressLedgerDocument
    {
        public ProgressLedgerDocument(
            string schemaVersion,
            ImmutableArray<ProgressOperation> operations)
        {
            if (!string.Equals(schemaVersion, ProgressSchema, StringComparison.Ordinal))
                throw new ArgumentException("Unsupported PostgreSQL progress-ledger schema.", nameof(schemaVersion));
            SchemaVersion = schemaVersion;
            Operations = operations.IsDefault ? [] : operations;
        }

        public string SchemaVersion { get; }

        public ImmutableArray<ProgressOperation> Operations { get; }

        internal static ProgressLedgerDocument Empty() => new(ProgressSchema, []);

        internal ProgressLedgerDocument Append(ProgressOperation operation) =>
            new(
                schemaVersion: SchemaVersion,
                operations: Operations.Add(operation));
    }

    enum ProgressOperationKind
    {
        Acquire = 0,
        Checkpoint = 1,
        Settlement = 2
    }

    sealed record ProgressOperation(
        ProgressOperationKind Kind,
        MaterializationProgressKey Key,
        MaterializationProgressMutationId MutationId,
        MaterializationProgressRevision? ExpectedRevision,
        string Owner,
        MaterializationProgressFence? Fence,
        MaterializationApplicationCheckpoint? ApplicationCheckpoint,
        MaterializationSourceSettlement? SourceSettlement)
    {
        internal static ProgressOperation Acquire(
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision? expectedRevision,
            string owner) =>
            new(
                Kind: ProgressOperationKind.Acquire,
                Key: key,
                MutationId: mutationId,
                ExpectedRevision: expectedRevision,
                Owner: owner,
                Fence: null,
                ApplicationCheckpoint: null,
                SourceSettlement: null);

        internal static ProgressOperation Checkpoint(
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationApplicationCheckpoint checkpoint) =>
            new(
                Kind: ProgressOperationKind.Checkpoint,
                Key: key,
                MutationId: mutationId,
                ExpectedRevision: expectedRevision,
                Owner: owner,
                Fence: fence,
                ApplicationCheckpoint: checkpoint,
                SourceSettlement: null);

        internal static ProgressOperation Settlement(
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationSourceSettlement settlement) =>
            new(
                Kind: ProgressOperationKind.Settlement,
                Key: key,
                MutationId: mutationId,
                ExpectedRevision: expectedRevision,
                Owner: owner,
                Fence: fence,
                ApplicationCheckpoint: null,
                SourceSettlement: settlement);

        internal Task<MaterializationProgressMutationResult> ApplyAsync(
            InMemoryMaterializationProgressStore store,
            OperationContext context) =>
            Kind switch
            {
                ProgressOperationKind.Acquire => store.AcquireFenceAsync(
                    context, Key, MutationId, ExpectedRevision, Owner),
                ProgressOperationKind.Checkpoint => store.SaveCheckpointAsync(
                    context,
                    Key,
                    MutationId,
                    ExpectedRevision!.Value,
                    Owner,
                    Fence!.Value,
                    ApplicationCheckpoint!),
                ProgressOperationKind.Settlement => store.SaveSettlementAsync(
                    context,
                    Key,
                    MutationId,
                    ExpectedRevision!.Value,
                    Owner,
                    Fence!.Value,
                    SourceSettlement!),
                _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported progress operation.")
            };
    }

    sealed record SynchronizationLedgerDocument
    {
        public SynchronizationLedgerDocument(
            string schemaVersion,
            ImmutableArray<SynchronizationOperation> operations)
        {
            if (!string.Equals(schemaVersion, SynchronizationSchema, StringComparison.Ordinal))
                throw new ArgumentException("Unsupported PostgreSQL synchronization-ledger schema.", nameof(schemaVersion));
            SchemaVersion = schemaVersion;
            Operations = operations.IsDefault ? [] : operations;
        }

        public string SchemaVersion { get; }

        public ImmutableArray<SynchronizationOperation> Operations { get; }

        internal static SynchronizationLedgerDocument Empty() => new(SynchronizationSchema, []);

        internal SynchronizationLedgerDocument Append(SynchronizationOperation operation) =>
            new(
                schemaVersion: SchemaVersion,
                operations: Operations.Add(operation));
    }

    enum SynchronizationOperationKind
    {
        Acquire = 0,
        Prepare = 1,
        Complete = 2,
        Activation = 3
    }

    sealed record SynchronizationOperation(
        SynchronizationOperationKind Kind,
        MaterializationSynchronizationWorkKey Key,
        MaterializationProgressMutationId MutationId,
        MaterializationProgressRevision? ExpectedRevision,
        string Owner,
        MaterializationProgressFence? Fence,
        MaterializationSynchronizationWorkIntent? Intent,
        MaterializationProgressMutationId? PreparationId,
        MaterializationItemVersion? Version,
        MaterializationGenerationActivationState? ActivationState)
    {
        internal static SynchronizationOperation Acquire(
            MaterializationSynchronizationWorkKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision? expectedRevision,
            string owner) =>
            new(
                Kind: SynchronizationOperationKind.Acquire,
                Key: key,
                MutationId: mutationId,
                ExpectedRevision: expectedRevision,
                Owner: owner,
                Fence: null,
                Intent: null,
                PreparationId: null,
                Version: null,
                ActivationState: null);

        internal static SynchronizationOperation Prepare(
            MaterializationSynchronizationWorkKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationSynchronizationWorkIntent intent) =>
            new(
                Kind: SynchronizationOperationKind.Prepare,
                Key: key,
                MutationId: mutationId,
                ExpectedRevision: expectedRevision,
                Owner: owner,
                Fence: fence,
                Intent: intent,
                PreparationId: null,
                Version: null,
                ActivationState: null);

        internal static SynchronizationOperation Complete(
            MaterializationSynchronizationWorkKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationProgressMutationId preparationId,
            MaterializationItemVersion? version) =>
            new(
                Kind: SynchronizationOperationKind.Complete,
                Key: key,
                MutationId: mutationId,
                ExpectedRevision: expectedRevision,
                Owner: owner,
                Fence: fence,
                Intent: null,
                PreparationId: preparationId,
                Version: version,
                ActivationState: null);

        internal static SynchronizationOperation Activation(
            MaterializationSynchronizationWorkKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationGenerationActivationState activation) =>
            new(
                Kind: SynchronizationOperationKind.Activation,
                Key: key,
                MutationId: mutationId,
                ExpectedRevision: expectedRevision,
                Owner: owner,
                Fence: fence,
                Intent: null,
                PreparationId: null,
                Version: null,
                ActivationState: activation);

        internal Task<MaterializationSynchronizationWorkMutationResult> ApplyAsync(
            InMemoryMaterializationSynchronizationWorkStore store,
            OperationContext context) =>
            Kind switch
            {
                SynchronizationOperationKind.Acquire => store.AcquireFenceAsync(
                    context, Key, MutationId, ExpectedRevision, Owner),
                SynchronizationOperationKind.Prepare => store.PrepareAsync(
                    context,
                    Key,
                    MutationId,
                    ExpectedRevision!.Value,
                    Owner,
                    Fence!.Value,
                    Intent!),
                SynchronizationOperationKind.Complete => store.CompleteAsync(
                    context,
                    Key,
                    MutationId,
                    ExpectedRevision!.Value,
                    Owner,
                    Fence!.Value,
                    PreparationId!.Value,
                    Version),
                SynchronizationOperationKind.Activation => store.SaveActivationAsync(
                    context,
                    Key,
                    MutationId,
                    ExpectedRevision!.Value,
                    Owner,
                    Fence!.Value,
                    ActivationState!),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(Kind),
                    Kind,
                    "Unsupported synchronization operation.")
            };
    }

    sealed record ControlLedgerDocument
    {
        public ControlLedgerDocument(
            string schemaVersion,
            ImmutableArray<ControlOperation> operations)
        {
            if (!string.Equals(schemaVersion, ControlSchema, StringComparison.Ordinal))
                throw new ArgumentException("Unsupported PostgreSQL Control-ledger schema.", nameof(schemaVersion));
            SchemaVersion = schemaVersion;
            Operations = operations.IsDefault ? [] : operations;
        }

        public string SchemaVersion { get; }

        public ImmutableArray<ControlOperation> Operations { get; }

        internal static ControlLedgerDocument Empty() => new(ControlSchema, []);

        internal ControlLedgerDocument Append(ControlOperation operation) =>
            new(
                schemaVersion: SchemaVersion,
                operations: Operations.Add(operation));
    }

    enum ControlOperationKind
    {
        Create = 0,
        CompareExchange = 1
    }

    sealed record ControlOperation(
        ControlOperationKind Kind,
        MaterializationIndexSyncControlStateKey Key,
        string MutationId,
        string MutationFingerprint,
        ControlRevision? ExpectedRevision,
        ControlLoopState State)
    {
        internal static ControlOperation Create(
            MaterializationIndexSyncControlStateKey key,
            string mutationId,
            string mutationFingerprint,
            ControlLoopState state) =>
            new(
                Kind: ControlOperationKind.Create,
                Key: key,
                MutationId: mutationId,
                MutationFingerprint: mutationFingerprint,
                ExpectedRevision: null,
                State: state);

        internal static ControlOperation CompareExchange(
            MaterializationIndexSyncControlStateKey key,
            string mutationId,
            string mutationFingerprint,
            ControlRevision expectedRevision,
            ControlLoopState state) =>
            new(
                Kind: ControlOperationKind.CompareExchange,
                Key: key,
                MutationId: mutationId,
                MutationFingerprint: mutationFingerprint,
                ExpectedRevision: expectedRevision,
                State: state);

        internal ValueTask<MaterializationIndexSyncControlWriteResult> ApplyAsync(
            InMemoryMaterializationIndexSyncControlStateStore store,
            OperationContext context) =>
            Kind switch
            {
                ControlOperationKind.Create => store.CreateAsync(
                    context, Key, MutationId, MutationFingerprint, State),
                ControlOperationKind.CompareExchange => store.CompareExchangeAsync(
                    context,
                    Key,
                    MutationId,
                    MutationFingerprint,
                    ExpectedRevision!.Value,
                    State),
                _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported Control operation.")
            };
    }
}
