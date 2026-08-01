using System.Collections.Immutable;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>
/// One owned PostgreSQL exported-snapshot baseline and its exact logical-replication catch-up boundary.
/// </summary>
/// <remarks>
/// Creating a handoff creates the configured permanent logical-replication slot. The baseline is held in one
/// imported <c>REPEATABLE READ</c> transaction, so every page observes the same MVCC snapshot. Its continuations are
/// usable only by this live handoff and become invalid operationally when the handoff is disposed. The paired change
/// source remains usable after disposal and begins exclusively after <see cref="ChangeStartPosition"/>.
///
/// Creation deliberately does not retry an indeterminate slot-creation failure and never drops a created slot during
/// cleanup. PostgreSQL has no durable slot-incarnation identity, so operators must inspect and either adopt or remove
/// the slot before retrying, and must rotate the configured slot generation whenever the slot is recreated.
/// </remarks>
public sealed class PostgresLogicalReplicationBaselineHandoff :
    IMaterializationSource,
    IAsyncDisposable
{
    const string EvidencePrefix =
        "cohesive.adapters.postgres/logical-replication-baseline-handoff/v1";

    readonly PostgresMaterializationSource baseline;
    readonly IPostgresLogicalReplicationSnapshotImport snapshotImport;
    readonly SemaphoreSlim readGate = new(initialCount: 1, maxCount: 1);
    int disposed;

    PostgresLogicalReplicationBaselineHandoff(
        PostgresMaterializationSource baseline,
        PostgresLogicalReplicationMaterializationChangeSource changeSource,
        MaterializationSourcePosition changeStartPosition,
        IPostgresLogicalReplicationSnapshotImport snapshotImport,
        PostgresRelationQuerySourceReader snapshotReader,
        PostgresLogicalReplicationBinding binding,
        PostgresLogicalReplicationWalPosition consistentPosition)
    {
        this.baseline = Guard.RequireNotNull(baseline);
        ChangeSource = Guard.RequireNotNull(changeSource);
        ChangeStartPosition = Guard.RequireNotNull(changeStartPosition);
        this.snapshotImport = Guard.RequireNotNull(snapshotImport);
        if (baseline.Scope != changeSource.Scope || changeStartPosition.Scope != changeSource.Scope)
        {
            throw new ArgumentException(
                "A PostgreSQL baseline handoff requires one exact baseline, change source, and WAL-position scope.",
                nameof(changeStartPosition));
        }

        Descriptor = new(
            new SnapshotRelationReader(this, Guard.RequireNotNull(snapshotReader)),
            CreateSnapshotCapabilityProfile(
                baseline.Descriptor.CapabilityProfile,
                binding,
                consistentPosition));
    }

    /// <summary>Creates an initial exported-snapshot baseline and its exact logical-replication catch-up source.</summary>
    /// <param name="context">Operation context carrying time, attribution, and cancellation.</param>
    /// <param name="reader">Plan-affine Npgsql Relations reader used for baseline and change projection.</param>
    /// <param name="placement">Exact PostgreSQL table placement included by the publication.</param>
    /// <param name="runtimeBinding">
    /// Exact runtime binding retained by <paramref name="reader"/> and capable of creating fresh logical-replication
    /// connections.
    /// </param>
    /// <param name="binding">
    /// Exact publication, not-yet-created dedicated slot, operator-owned generation, and replica-identity contract.
    /// </param>
    /// <param name="positionAuthenticationKey">
    /// Caller-owned secret used to authenticate baseline continuations and change positions. Both sources copy it.
    /// </param>
    /// <param name="policy">Bounded logical-replication policy, or <see langword="null"/> for the default.</param>
    /// <param name="observer">Optional non-throwing typed operation and health observer.</param>
    /// <returns>
    /// An owned consistent baseline, its live retained-change source, and the exact exclusive change-start position.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Runtime, placement, authentication-key, publication, table, replica-identity, or column affinity is invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Logical replication is unavailable from the supplied runtime binding.
    /// </exception>
    /// <exception cref="PostgresLogicalReplicationException">
    /// PostgreSQL fails slot creation, reports that the configured slot already exists, cannot import the exported
    /// snapshot, or fails the post-creation source preflight operation.
    /// </exception>
    /// <exception cref="OperationCanceledException">Creation is canceled.</exception>
    public static ValueTask<PostgresLogicalReplicationBaselineHandoff> CreateAsync(
        OperationContext context,
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresLogicalReplicationBinding binding,
        ReadOnlyMemory<byte> positionAuthenticationKey,
        PostgresLogicalReplicationSourcePolicy? policy = null,
        IPostgresLogicalReplicationObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(runtimeBinding);
        ArgumentNullException.ThrowIfNull(binding);
        if (!ReferenceEquals(reader.RuntimeBinding, runtimeBinding))
        {
            throw new ArgumentException(
                "The snapshot handoff requires the exact runtime binding retained by its Relations reader.",
                nameof(runtimeBinding));
        }
        if (!runtimeBinding.SupportsLogicalReplication)
        {
            throw new InvalidOperationException(
                "The PostgreSQL runtime binding has no logical-replication connection factory.");
        }

        PostgresRelationQueryTableBinding table;
        try
        {
            table = reader.StorageBinding.ResolveTable(placement.Id);
        }
        catch (KeyNotFoundException exception)
        {
            throw new ArgumentException(
                "The PostgreSQL storage binding does not contain the requested snapshot placement.",
                nameof(placement),
                exception);
        }

        // Validate the complete local baseline contract before creating durable provider state.
        _ = new PostgresMaterializationSource(
            reader,
            placement,
            positionAuthenticationKey.Span);
        var protocol = new PostgresNpgsqlLogicalReplicationProtocol(
            runtimeBinding,
            binding,
            table);
        return CreateAsync(
            context,
            reader,
            placement,
            runtimeBinding,
            binding,
            protocol,
            positionAuthenticationKey,
            policy,
            observer);
    }

    internal static async ValueTask<PostgresLogicalReplicationBaselineHandoff> CreateAsync(
        OperationContext context,
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresLogicalReplicationBinding binding,
        IPostgresLogicalReplicationProtocol protocol,
        ReadOnlyMemory<byte> positionAuthenticationKey,
        PostgresLogicalReplicationSourcePolicy? policy = null,
        IPostgresLogicalReplicationObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(runtimeBinding);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(protocol);
        context.CancellationToken.ThrowIfCancellationRequested();

        // This also proves scope, stage, keyset, and authentication-key validity before slot creation.
        var validatedBaseline = new PostgresMaterializationSource(
            reader,
            placement,
            positionAuthenticationKey.Span);
        var startedAtUtc = context.UtcNow.ToUniversalTime();
        try
        {
            await using var export = await protocol
                .CreateSnapshotExportAsync(context.CancellationToken)
                .ConfigureAwait(false);
            IPostgresLogicalReplicationSnapshotImport? snapshotImport =
                Guard.RequireNotNull(await export
                    .ImportAsync(context.CancellationToken)
                    .ConfigureAwait(false));
            try
            {
                var snapshotReader = reader.WithCommandExecutor(snapshotImport.ExecuteCommand);
                var snapshotBaseline = new PostgresMaterializationSource(
                    snapshotReader,
                    placement,
                    positionAuthenticationKey.Span);
                var changeSource = await PostgresLogicalReplicationMaterializationChangeSource
                    .CreateAsync(
                        reader,
                        placement,
                        runtimeBinding,
                        binding,
                        protocol,
                        positionAuthenticationKey,
                        policy,
                        observer,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                var changeStartPosition = changeSource.CreatePosition(
                    PostgresLogicalReplicationPositionKind.WalCut,
                    export.ConsistentPosition);
                var handoff = new PostgresLogicalReplicationBaselineHandoff(
                    snapshotBaseline,
                    changeSource,
                    changeStartPosition,
                    snapshotImport,
                    snapshotReader,
                    binding,
                    export.ConsistentPosition);
                Observe(
                    observer,
                    new(
                        operation: PostgresLogicalReplicationOperationKind.SnapshotHandoff,
                        disposition: PostgresLogicalReplicationOperationDisposition.Complete,
                        scope: validatedBaseline.Scope,
                        startedAtUtc,
                        completedAtUtc: CompletionTime(context, startedAtUtc),
                        attempt: 1,
                        transactionCount: 0,
                        changeCount: 0,
                        canonicalByteCount: 0,
                        evidenceReference: Evidence(binding, "created")));
                snapshotImport = null;
                return handoff;
            }
            finally
            {
                if (snapshotImport is not null)
                    await snapshotImport.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (PostgresLogicalReplicationProtocolException exception)
        {
            var observation = new PostgresLogicalReplicationOperationObservation(
                operation: PostgresLogicalReplicationOperationKind.SnapshotHandoff,
                disposition: PostgresLogicalReplicationOperationDisposition.Failed,
                scope: validatedBaseline.Scope,
                startedAtUtc,
                completedAtUtc: CompletionTime(context, startedAtUtc),
                attempt: 1,
                transactionCount: 0,
                changeCount: 0,
                canonicalByteCount: 0,
                evidenceReference: Evidence(binding, exception.EvidenceReference),
                failureKind: exception.FailureKind);
            Observe(observer, observation);
            throw new PostgresLogicalReplicationException(
                "The PostgreSQL exported-snapshot handoff failed closed; inspect its typed operation evidence.",
                exception.FailureKind,
                observation);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            Observe(
                observer,
                new(
                    operation: PostgresLogicalReplicationOperationKind.SnapshotHandoff,
                    disposition: PostgresLogicalReplicationOperationDisposition.Canceled,
                    scope: validatedBaseline.Scope,
                    startedAtUtc,
                    completedAtUtc: CompletionTime(context, startedAtUtc),
                    attempt: 1,
                    transactionCount: 0,
                    changeCount: 0,
                    canonicalByteCount: 0,
                    evidenceReference: Evidence(binding, "canceled")));
            throw;
        }
    }

    /// <inheritdoc />
    public MaterializationQuerySourceDescriptor Descriptor { get; }

    /// <summary>Exact table, partition, and ordering scope shared by the snapshot and change source.</summary>
    public MaterializationSourceScope Scope => baseline.Scope;

    /// <summary>Live retained-change source paired with this baseline's consistent point.</summary>
    public PostgresLogicalReplicationMaterializationChangeSource ChangeSource { get; }

    /// <summary>Exclusive logical-replication position from which catch-up follows the imported snapshot.</summary>
    public MaterializationSourcePosition ChangeStartPosition { get; }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The snapshot handoff has been disposed.</exception>
    public async ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await readGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await baseline.ReadPageAsync(context, request).ConfigureAwait(false);
        }
        finally
        {
            readGate.Release();
        }
    }

    async ValueTask<RelationQuerySourceReadResult> ReadRelationAsync(
        PostgresRelationQuerySourceReader snapshotReader,
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await snapshotReader
                .ReadAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            readGate.Release();
        }
    }

    /// <summary>
    /// Ends the imported snapshot transaction and releases its ordinary PostgreSQL connection.
    /// </summary>
    /// <remarks>
    /// Disposal does not settle, drop, or otherwise mutate the permanent logical-replication slot. It also does not
    /// dispose <see cref="ChangeSource"/>, which retains no owned long-lived connection.
    /// </remarks>
    /// <returns>A task that completes after the snapshot transaction and connection are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await readGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await snapshotImport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            readGate.Release();
        }
    }

    void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
    }

    static MaterializationCapabilityProfile CreateSnapshotCapabilityProfile(
        MaterializationCapabilityProfile baseline,
        PostgresLogicalReplicationBinding binding,
        PostgresLogicalReplicationWalPosition consistentPosition)
    {
        var evidence = ImmutableArray.CreateBuilder<MaterializationCapabilityEvidence>(
            baseline.Evidence.Length);
        foreach (var item in baseline.Evidence)
        {
            var guarantees = ImmutableArray.CreateBuilder<MaterializationGuaranteeKind>(
                item.Guarantees.Length + 1);
            foreach (var guarantee in item.Guarantees)
            {
                if (guarantee != MaterializationGuaranteeKind.Reconciliation)
                    guarantees.Add(guarantee);
            }
            if (!guarantees.Contains(MaterializationGuaranteeKind.CoordinatedSnapshot))
                guarantees.Add(MaterializationGuaranteeKind.CoordinatedSnapshot);

            var limits = ImmutableArray.CreateBuilder<MaterializationOperatingLimit>(
                item.OperatingLimits.Length + 1);
            var foundParallelism = false;
            foreach (var limit in item.OperatingLimits)
            {
                if (limit.Kind == MaterializationLimitKind.Parallelism)
                {
                    limits.Add(new(MaterializationLimitKind.Parallelism, 1));
                    foundParallelism = true;
                }
                else
                {
                    limits.Add(limit);
                }
            }
            if (!foundParallelism)
                limits.Add(new(MaterializationLimitKind.Parallelism, 1));

            evidence.Add(new(
                id: new(string.Concat(item.Id.Value, "/exported-snapshot/v1")),
                capability: item.Capability,
                realization: item.Realization,
                guarantees: guarantees.ToImmutable(),
                operatingLimits: limits.ToImmutable(),
                sourceReferences:
                [
                    .. item.SourceReferences,
                    EvidencePrefix,
                    string.Concat(
                        "postgres-publication/",
                        Uri.EscapeDataString(binding.PublicationName)),
                    string.Concat(
                        "postgres-slot/",
                        Uri.EscapeDataString(binding.SlotName),
                        "/generation/",
                        Uri.EscapeDataString(binding.SlotGeneration)),
                    string.Concat(
                        "postgres-consistent-point/",
                        consistentPosition.ToString())
                ],
                description:
                    "One imported PostgreSQL exported snapshot retained by this handoff's repeatable-read transaction."));
        }

        return new(
            id: new(string.Concat(
                EvidencePrefix,
                "/baseline/",
                Uri.EscapeDataString(baseline.Id.Value),
                "/slot/",
                Uri.EscapeDataString(binding.SlotName),
                "/generation/",
                Uri.EscapeDataString(binding.SlotGeneration),
                "/consistent-point/",
                Uri.EscapeDataString(consistentPosition.ToString()))),
            role: MaterializationEndpointRole.Source,
            subject: baseline.Subject,
            evidence: evidence.MoveToImmutable(),
            description:
                "Session-owned PostgreSQL exported-snapshot baseline paired with an exact logical-replication catch-up position.");
    }

    static string Evidence(PostgresLogicalReplicationBinding binding, string suffix) => string.Concat(
        EvidencePrefix,
        "/publication/",
        Uri.EscapeDataString(binding.PublicationName),
        "/slot/",
        Uri.EscapeDataString(binding.SlotName),
        "/generation/",
        Uri.EscapeDataString(binding.SlotGeneration),
        "/",
        suffix);

    static DateTimeOffset CompletionTime(
        OperationContext context,
        DateTimeOffset startedAtUtc)
    {
        var completedAtUtc = context.UtcNow.ToUniversalTime();
        return completedAtUtc < startedAtUtc ? startedAtUtc : completedAtUtc;
    }

    static void Observe(
        IPostgresLogicalReplicationObserver? observer,
        PostgresLogicalReplicationOperationObservation observation)
    {
        try
        {
            observer?.Observe(observation);
        }
        catch
        {
            // Observation cannot change authoritative snapshot or slot semantics.
        }
    }

    sealed class SnapshotRelationReader(
        PostgresLogicalReplicationBaselineHandoff owner,
        PostgresRelationQuerySourceReader inner) : IRelationQuerySourceReader
    {
        public RelationQuerySourceReaderDescriptor Descriptor => inner.Descriptor;

        public ValueTask<RelationQuerySourceReadResult> ReadAsync(
            RelationQuerySourceReadRequest request,
            CancellationToken cancellationToken = default) => owner.ReadRelationAsync(
                inner,
                request,
                cancellationToken);
    }
}
