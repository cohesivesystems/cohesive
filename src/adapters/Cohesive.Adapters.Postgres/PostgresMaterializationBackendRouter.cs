using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>
/// PostgreSQL-backed linearizable materialization routing authority over one exact backend-pool definition.
/// </summary>
/// <remarks>
/// PostgreSQL owns atomic persistence and process-restart durability. The provider-neutral
/// <see cref="InMemoryMaterializationBackendRouter"/> remains the sole authority for routing transition semantics:
/// each locked access restores its complete portable document, evaluates one operation, and persists the captured
/// replacement before committing. Rejected commands are captured because accepting an intent or a newer fence is
/// itself durable authority state.
///
/// The caller owns the supplied <see cref="NpgsqlDataSource"/> and target pool. Call
/// <see cref="EnsureCreatedAsync"/> explicitly during bootstrap; routing operations never perform schema DDL.
/// </remarks>
public sealed class PostgresMaterializationBackendRouter : IMaterializationBackendRouter
{
    const string RoutingAuthorityKind = "backend-routing";

    readonly PostgresMaterializationDocumentAuthority authority;
    readonly IMaterializationTargetPool targets;
    readonly TimeProvider timeProvider;

    /// <summary>Creates one durable routing authority over a caller-owned PostgreSQL data source.</summary>
    /// <param name="dataSource">Caller-owned PostgreSQL connection pool.</param>
    /// <param name="options">Exact authority row, table binding, and physical document limit.</param>
    /// <param name="document">Canonical backend-pool document governed by this router.</param>
    /// <param name="targets">Exact-ID target dependencies implementing <paramref name="document"/>.</param>
    /// <param name="timeProvider">Clock used to timestamp newly committed routing receipts.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="targets"/> implements another pool definition.</exception>
    public PostgresMaterializationBackendRouter(
        NpgsqlDataSource dataSource,
        PostgresMaterializationStateStoreOptions options,
        MaterializationBackendPoolDocument document,
        IMaterializationTargetPool targets,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        Document = document ?? throw new ArgumentNullException(nameof(document));
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (MaterializationBackendPoolFingerprinter.Compute(targets.Definition) != document.DefinitionFingerprint)
        {
            throw new ArgumentException(
                "The target pool must implement the exact routed backend-pool definition.",
                nameof(targets));
        }

        authority = new(dataSource: dataSource, options: options);
    }

    /// <summary>Canonical backend-pool document governing this router.</summary>
    public MaterializationBackendPoolDocument Document { get; }

    /// <summary>Creates the configured PostgreSQL schema and authority table when absent.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <returns>A task completing after DDL commits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before DDL commits.</exception>
    /// <exception cref="NpgsqlException">PostgreSQL rejects or cannot execute the DDL.</exception>
    public Task EnsureCreatedAsync(OperationContext context) => authority.EnsureCreatedAsync(context);

    /// <summary>Reads the complete canonical routing-authority document under the PostgreSQL row lock.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <returns>The exact portable authority state persisted for this router.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was cancelled.</exception>
    /// <exception cref="NpgsqlException">PostgreSQL rejects or cannot execute the read transaction.</exception>
    public async ValueTask<MaterializationBackendRoutingAuthorityDocument> CaptureAsync(OperationContext context) =>
        await authority.AccessAsync(
                context: context,
                authorityKind: RoutingAuthorityKind,
                empty: MaterializationBackendRoutingAuthorityDocument.Empty(Document),
                deserialize: MaterializationBackendRoutingAuthorityJsonSerializer.Deserialize,
                serialize: SerializeCompact,
                operation: static (document, _) => Task.FromResult((document, document)))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingSnapshot> InspectAsync(
        OperationContext context,
        MaterializationPlacementSliceReference placementSlice) =>
        await ReadAsync(
                context: context,
                operation: (router, providerContext) => router.InspectAsync(providerContext, placementSlice))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRouteBinding> ResolveReadAsync(
        OperationContext context,
        MaterializationPlacementSliceReference placementSlice) =>
        await ReadAsync(
                context: context,
                operation: (router, providerContext) => router.ResolveReadAsync(providerContext, placementSlice))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRouteBinding> ResolveWriteAsync(
        OperationContext context,
        MaterializationPlacementSliceReference placementSlice) =>
        await ReadAsync(
                context: context,
                operation: (router, providerContext) => router.ResolveWriteAsync(providerContext, placementSlice))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> AdmitCandidateAsync(
        OperationContext context,
        MaterializationAdmitBackendCandidateRequest request) =>
        await MutateAsync(
                context: context,
                operation: (router, providerContext) => router.AdmitCandidateAsync(providerContext, request))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> AbandonCandidateAsync(
        OperationContext context,
        MaterializationAbandonBackendCandidateRequest request) =>
        await MutateAsync(
                context: context,
                operation: (router, providerContext) => router.AbandonCandidateAsync(providerContext, request))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> SwapAsync(
        OperationContext context,
        MaterializationSwapBackendRoutingRequest request) =>
        await MutateAsync(
                context: context,
                operation: (router, providerContext) => router.SwapAsync(providerContext, request))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> CompleteDrainAsync(
        OperationContext context,
        MaterializationCompleteBackendDrainRequest request) =>
        await MutateAsync(
                context: context,
                operation: (router, providerContext) => router.CompleteDrainAsync(providerContext, request))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> RetireAsync(
        OperationContext context,
        MaterializationRetireBackendGenerationRequest request) =>
        await MutateAsync(
                context: context,
                operation: (router, providerContext) => router.RetireAsync(providerContext, request))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendCleanupReservationResult> ReserveCleanupAsync(
        OperationContext context,
        MaterializationReserveBackendCleanupRequest request) =>
        await MutateAsync(
                context: context,
                operation: (router, providerContext) => router.ReserveCleanupAsync(providerContext, request))
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> CleanupAsync(
        OperationContext context,
        MaterializationCleanupBackendGenerationRequest request) =>
        await MutateAsync(
                context: context,
                operation: (router, providerContext) => router.CleanupAsync(providerContext, request))
            .ConfigureAwait(false);

    Task<TResult> ReadAsync<TResult>(
        OperationContext context,
        Func<InMemoryMaterializationBackendRouter, OperationContext, ValueTask<TResult>> operation) =>
        authority.AccessAsync(
            context: context,
            authorityKind: RoutingAuthorityKind,
            empty: MaterializationBackendRoutingAuthorityDocument.Empty(Document),
            deserialize: MaterializationBackendRoutingAuthorityJsonSerializer.Deserialize,
            serialize: SerializeCompact,
            operation: async (document, providerContext) =>
            {
                using var router = Restore(document);
                var result = await operation(router, providerContext).ConfigureAwait(false);
                return (result, document);
            });

    Task<TResult> MutateAsync<TResult>(
        OperationContext context,
        Func<InMemoryMaterializationBackendRouter, OperationContext, ValueTask<TResult>> operation) =>
        authority.AccessAsync(
            context: context,
            authorityKind: RoutingAuthorityKind,
            empty: MaterializationBackendRoutingAuthorityDocument.Empty(Document),
            deserialize: MaterializationBackendRoutingAuthorityJsonSerializer.Deserialize,
            serialize: SerializeCompact,
            operation: async (document, providerContext) =>
            {
                using var router = Restore(document);
                var result = await operation(router, providerContext).ConfigureAwait(false);
                var replacement = await router.CaptureAsync(providerContext).ConfigureAwait(false);
                return (result, replacement);
            });

    InMemoryMaterializationBackendRouter Restore(MaterializationBackendRoutingAuthorityDocument document) =>
        new(
            document: Document,
            targets: targets,
            authority: document,
            timeProvider: timeProvider);

    static string SerializeCompact(MaterializationBackendRoutingAuthorityDocument document) =>
        MaterializationBackendRoutingAuthorityJsonSerializer.Serialize(
            document: document,
            formatting: PortableDocumentJsonFormatting.Compact);
}
