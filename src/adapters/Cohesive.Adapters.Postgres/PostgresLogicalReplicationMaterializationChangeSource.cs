using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>
/// Transaction-aligned retained PostgreSQL logical-replication changes composed with one exact baseline source.
/// </summary>
/// <remarks>
/// Reads never acknowledge PostgreSQL slot progress. Callers first durably commit the returned application position
/// and then invoke <see cref="SettleAsync"/> explicitly. The dedicated slot, publication, database incarnation,
/// physical plan, placement, and operator-owned slot generation are authenticated into every durable position.
/// </remarks>
public sealed class PostgresLogicalReplicationMaterializationChangeSource :
    IMaterializationRetainedChangeSource,
    IMaterializationSettlingSource
{
    /// <summary>Current authenticated PostgreSQL WAL-position format.</summary>
    public const int PositionFormatVersion = 1;

    const string PositionPrefix = "postgres-logical-position/v1/";
    const string EvidencePrefix = "cohesive.adapters.postgres/logical-replication-source/v1";
    const string OutputPlugin = "pgoutput";
    static ReadOnlySpan<byte> PositionAuthenticationDomain =>
        "cohesive.adapters.postgres/logical-replication-position/v1\0"u8;
    static readonly JsonSerializerOptions CanonicalJsonOptions =
        MaterializationJsonSerializer.CreateOptions();

    readonly PostgresMaterializationSource baseline;
    readonly PostgresRelationQuerySourceReader reader;
    readonly PostgresNpgsqlRuntimeBinding runtimeBinding;
    readonly PostgresLogicalReplicationBinding binding;
    readonly PostgresLogicalReplicationSourcePolicy policy;
    readonly IPostgresLogicalReplicationProtocol protocol;
    readonly IPostgresLogicalReplicationObserver? observer;
    readonly PostgresRelationQueryTableBinding table;
    readonly RelationQuerySourcePlacementBinding placement;
    readonly ImmutableArray<ChangeProjectionColumn> projection;
    readonly MaterializationAuthenticatedValueCodec positionCodec;
    readonly DeploymentAffinity affinity;
    readonly SemaphoreSlim operationGate = new(initialCount: 1, maxCount: 1);
    readonly object settlementGate = new();
    readonly Dictionary<MaterializationSettlementId, SettlementRecord> settlements = [];

    PostgresLogicalReplicationMaterializationChangeSource(
        PostgresMaterializationSource baseline,
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresLogicalReplicationBinding binding,
        PostgresLogicalReplicationSourcePolicy policy,
        IPostgresLogicalReplicationProtocol protocol,
        IPostgresLogicalReplicationObserver? observer,
        PostgresRelationQueryTableBinding table,
        PostgresLogicalReplicationDeployment deployment,
        ReadOnlySpan<byte> positionAuthenticationKey)
    {
        this.baseline = baseline;
        this.reader = reader;
        this.placement = placement;
        this.runtimeBinding = runtimeBinding;
        this.binding = binding;
        this.policy = policy;
        this.protocol = protocol;
        this.observer = observer;
        this.table = table;
        projection = CreateProjection(placement, table);
        affinity = DeploymentAffinity.From(
            deployment,
            runtimeBinding.Database,
            reader.StorageBinding.Fingerprint.Value,
            binding.SlotGeneration);
        positionCodec = new(
            PositionPrefix,
            PositionAuthenticationDomain,
            positionAuthenticationKey,
            policy.MaximumPositionCharacters);
        Descriptor = new(reader, CreateCapabilityProfile(baseline, reader, placement, table, binding, policy, affinity));
    }

    /// <summary>Creates and preflights one exact PostgreSQL logical-replication change source.</summary>
    /// <param name="reader">Plan-affine Npgsql Relations reader used by the composed baseline source.</param>
    /// <param name="placement">Exact PostgreSQL table placement observed by the publication.</param>
    /// <param name="runtimeBinding">
    /// Exact runtime binding already retained by <paramref name="reader"/> and capable of creating logical
    /// replication connections.
    /// </param>
    /// <param name="binding">Exact publication, dedicated slot, generation, and replica-identity contract.</param>
    /// <param name="positionAuthenticationKey">
    /// Caller-owned secret used to authenticate durable continuations and WAL positions. The source copies it.
    /// </param>
    /// <param name="policy">Bounded logical-replication policy, or <see langword="null"/> for the default.</param>
    /// <param name="observer">Optional non-throwing typed operation and health observer.</param>
    /// <param name="cancellationToken">Cancellation for provider preflight.</param>
    /// <returns>A fully preflighted baseline and retained-change source.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Runtime, placement, authentication-key, publication, table, slot, replica-identity, or column affinity is
    /// invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The runtime has no logical-replication connection factory.
    /// </exception>
    /// <exception cref="PostgresLogicalReplicationException">
    /// Provider state cannot satisfy the exact publication, slot, table, replica-identity, or WAL contract.
    /// </exception>
    /// <exception cref="OperationCanceledException">Provider preflight is canceled.</exception>
    public static ValueTask<PostgresLogicalReplicationMaterializationChangeSource> CreateAsync(
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresLogicalReplicationBinding binding,
        ReadOnlyMemory<byte> positionAuthenticationKey,
        PostgresLogicalReplicationSourcePolicy? policy = null,
        IPostgresLogicalReplicationObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(runtimeBinding);
        ArgumentNullException.ThrowIfNull(binding);
        if (!ReferenceEquals(reader.RuntimeBinding, runtimeBinding))
        {
            throw new ArgumentException(
                "The logical-replication source requires the exact runtime binding retained by its Relations reader.",
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
                "The PostgreSQL storage binding does not contain the requested logical-replication placement.",
                nameof(placement),
                exception);
        }
        var scope = new PostgresMaterializationSource(
            reader,
            placement,
            positionAuthenticationKey.Span).Scope;
        var protocol = new PostgresNpgsqlLogicalReplicationProtocol(runtimeBinding, binding, table);
        return CreatePublicAsync(
            reader,
            placement,
            runtimeBinding,
            binding,
            protocol,
            scope,
            positionAuthenticationKey,
            policy,
            observer,
            cancellationToken);
    }

    static async ValueTask<PostgresLogicalReplicationMaterializationChangeSource> CreatePublicAsync(
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresLogicalReplicationBinding binding,
        IPostgresLogicalReplicationProtocol protocol,
        MaterializationSourceScope scope,
        ReadOnlyMemory<byte> positionAuthenticationKey,
        PostgresLogicalReplicationSourcePolicy? policy,
        IPostgresLogicalReplicationObserver? observer,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            return await CreateAsync(
                reader,
                placement,
                runtimeBinding,
                binding,
                protocol,
                positionAuthenticationKey,
                policy,
                observer,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresLogicalReplicationProtocolException exception)
        {
            var observation = new PostgresLogicalReplicationOperationObservation(
                PostgresLogicalReplicationOperationKind.HealthInspection,
                PostgresLogicalReplicationOperationDisposition.Failed,
                scope,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                attempt: 1,
                transactionCount: 0,
                changeCount: 0,
                canonicalByteCount: 0,
                string.Concat(EvidencePrefix, "/preflight/", exception.EvidenceReference),
                exception.FailureKind);
            ObserveSafely(observer, observation);
            throw new PostgresLogicalReplicationException(
                "The PostgreSQL logical-replication source preflight failed closed; inspect its typed operation evidence.",
                exception.FailureKind,
                observation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveSafely(
                observer,
                new(
                    PostgresLogicalReplicationOperationKind.HealthInspection,
                    PostgresLogicalReplicationOperationDisposition.Canceled,
                    scope,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    attempt: 1,
                    transactionCount: 0,
                    changeCount: 0,
                    canonicalByteCount: 0,
                    string.Concat(EvidencePrefix, "/preflight/canceled")));
            throw;
        }
    }

    internal static async ValueTask<PostgresLogicalReplicationMaterializationChangeSource> CreateAsync(
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresLogicalReplicationBinding binding,
        IPostgresLogicalReplicationProtocol protocol,
        ReadOnlyMemory<byte> positionAuthenticationKey,
        PostgresLogicalReplicationSourcePolicy? policy = null,
        IPostgresLogicalReplicationObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(runtimeBinding);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(protocol);
        if (!ReferenceEquals(reader.RuntimeBinding, runtimeBinding))
        {
            throw new ArgumentException(
                "The logical-replication source requires the exact runtime binding retained by its Relations reader.",
                nameof(runtimeBinding));
        }
        if (positionAuthenticationKey.Length < MaterializationAuthenticatedValueCodec.MinimumAuthenticationKeyBytes)
        {
            throw new ArgumentException(
                $"PostgreSQL logical-replication position authentication requires at least {MaterializationAuthenticatedValueCodec.MinimumAuthenticationKeyBytes} secret bytes.",
                nameof(positionAuthenticationKey));
        }

        var effectivePolicy = policy ?? PostgresLogicalReplicationSourcePolicy.Default;
        var baseline = new PostgresMaterializationSource(
            reader,
            placement,
            positionAuthenticationKey.Span);
        PostgresRelationQueryTableBinding table;
        try
        {
            table = reader.StorageBinding.ResolveTable(placement.Id);
        }
        catch (KeyNotFoundException exception)
        {
            throw new ArgumentException(
                "The PostgreSQL storage binding does not contain the requested logical-replication placement.",
                nameof(placement),
                exception);
        }

        var deployment = await protocol.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireDeployment(
            deployment,
            expectedAffinity: null,
            reader,
            placement,
            runtimeBinding,
            binding,
            table);
        return new(
            baseline,
            reader,
            placement,
            runtimeBinding,
            binding,
            effectivePolicy,
            protocol,
            observer,
            table,
            deployment,
            positionAuthenticationKey.Span);
    }

    /// <inheritdoc />
    public MaterializationQuerySourceDescriptor Descriptor { get; }

    /// <summary>Exact source-feed scope shared by baseline reads and logical-replication positions.</summary>
    public MaterializationSourceScope Scope => baseline.Scope;

    /// <summary>Exact logical-replication operating policy.</summary>
    public PostgresLogicalReplicationSourcePolicy Policy => policy;

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request) => baseline.ReadPageAsync(context, request);

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope) => CapturePositionAsync(
            context,
            scope,
            retainedStart: false);

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePosition> CaptureRetainedStartPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope) => CapturePositionAsync(
            context,
            scope,
            retainedStart: true);

    /// <inheritdoc />
    public ValueTask<MaterializationChangePage> ReadChangesAsync(
        OperationContext context,
        MaterializationChangeReadRequest request) => ReadChangesCoreAsync(context, request);

    /// <inheritdoc />
    public ValueTask<MaterializationSourceSettlementResult> SettleAsync(
        OperationContext context,
        MaterializationSourceSettlementRequest request) => SettleCoreAsync(context, request);

    /// <summary>Creates the conventional deterministic identity for one exact PostgreSQL source settlement.</summary>
    /// <param name="checkpoint">Already-durable application checkpoint cited by the settlement.</param>
    /// <param name="position">Exact authenticated PostgreSQL source position proven by the checkpoint.</param>
    /// <returns>A stable settlement identity derived from the checkpoint and complete opaque source position.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="checkpoint"/> is default.</exception>
    public static MaterializationSettlementId CreateSettlementId(
        MaterializationCheckpointId checkpoint,
        MaterializationSourcePosition position)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.Value))
            throw new ArgumentException("A PostgreSQL settlement requires a durable checkpoint identity.", nameof(checkpoint));
        ArgumentNullException.ThrowIfNull(position);
        var text = string.Concat(
            checkpoint.Value, "\0",
            position.FormatVersion.ToString(CultureInfo.InvariantCulture), "\0",
            position.Scope.PhysicalPlan.Algorithm, "\0",
            position.Scope.PhysicalPlan.Canonicalization, "\0",
            position.Scope.PhysicalPlan.Value, "\0",
            position.Scope.Placement.Id.Value, "\0",
            position.Scope.Partition.Value, "\0",
            position.Scope.OrderingScope.Value, "\0",
            position.Value);
        return new(string.Concat(
            "postgres-settlement/v1/",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))));
    }

    /// <summary>Polls attributable slot lag, retention, inactivity, and loss state for this exact source scope.</summary>
    /// <param name="context">Operation context carrying time, attribution, and cancellation.</param>
    /// <param name="scope">Exact logical-replication source scope to inspect.</param>
    /// <returns>A provider-neutral health observation suitable for Control polling.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="scope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="scope"/> targets another source.</exception>
    /// <exception cref="PostgresLogicalReplicationException">
    /// Publication, database, table, replica-identity, or other immutable deployment affinity drifted.
    /// </exception>
    /// <exception cref="OperationCanceledException">Health inspection is canceled.</exception>
    public async ValueTask<PostgresLogicalReplicationHealthObservation> InspectHealthAsync(
        OperationContext context,
        MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireScope(scope, nameof(scope));
        context.CancellationToken.ThrowIfCancellationRequested();
        var startedAtUtc = context.UtcNow.ToUniversalTime();
        try
        {
            var (result, attempt) = await ExecuteWithRetryAsync(
                context,
                PostgresLogicalReplicationOperationKind.HealthInspection,
                startedAtUtc,
                cancellationToken => InspectHealthAttemptAsync(context, cancellationToken)).ConfigureAwait(false);
            var observation = new PostgresLogicalReplicationHealthObservation(
                result.State,
                Scope,
                result.ObservedAtUtc,
                result.PendingWalBytes,
                result.RetainedWalBytes,
                result.SafeWalBytes,
                estimatedLag: null,
                result.Inactivity,
                Evidence("health-inspection"));
            Observe(observation);
            ObserveOperation(
                PostgresLogicalReplicationOperationKind.HealthInspection,
                PostgresLogicalReplicationOperationDisposition.Complete,
                startedAtUtc,
                result.ObservedAtUtc,
                attempt,
                transactionCount: 0,
                changeCount: 0,
                canonicalByteCount: 0,
                "health-inspection");
            return observation;
        }
        catch (PostgresLogicalReplicationException exception) when (
            exception.FailureKind is PostgresLogicalReplicationFailureKind.SlotUnavailable
                or PostgresLogicalReplicationFailureKind.PositionUnavailable)
        {
            var state = exception.FailureKind == PostgresLogicalReplicationFailureKind.SlotUnavailable
                ? PostgresLogicalReplicationHealthState.SlotLost
                : PostgresLogicalReplicationHealthState.Unavailable;
            var observation = new PostgresLogicalReplicationHealthObservation(
                state,
                Scope,
                context.UtcNow.ToUniversalTime(),
                estimatedPendingWalBytes: null,
                retainedWalBytes: null,
                remainingSafeWalBytes: null,
                estimatedLag: null,
                inactivity: null,
                Evidence("health-inspection-unavailable"));
            Observe(observation);
            return observation;
        }
    }

    async ValueTask<MaterializationSourceSettlementResult> SettleCoreAsync(
        OperationContext context,
        MaterializationSourceSettlementRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        RequireScope(request.Position.Scope, nameof(request));
        // Reject malformed, unauthenticated, or foreign positions before inspection or replication feedback.
        var position = DecodePosition(request.Position, nameof(request));
        context.CancellationToken.ThrowIfCancellationRequested();
        var startedAtUtc = context.UtcNow.ToUniversalTime();

        lock (settlementGate)
        {
            if (settlements.TryGetValue(request.Id, out var prior))
            {
                if (prior.Request == request)
                {
                    ObserveOperation(
                        PostgresLogicalReplicationOperationKind.SourceSettlement,
                        PostgresLogicalReplicationOperationDisposition.Replayed,
                        startedAtUtc,
                        context.UtcNow,
                        attempt: 1,
                        transactionCount: 0,
                        changeCount: 0,
                        canonicalByteCount: 0,
                        "settlement-local-replay");
                    return new(
                        MaterializationSourceSettlementDisposition.Replayed,
                        prior.Receipt);
                }

                return SettlementRejected(
                    MaterializationSourceSettlementDisposition.IdentityConflict,
                    MaterializationSourceDiagnosticCodes.SettlementIdentityConflict,
                    "The settlement identity was already used for a different PostgreSQL acknowledgement request.",
                    request,
                    expected: "an unused settlement identity or an exact replay of its prior request",
                    observed: "the identity is bound to another checkpoint or position");
            }
        }

        var settledAtUtc = context.UtcNow.ToUniversalTime();
        if (settledAtUtc < request.RequestedAtUtc)
        {
            return SettlementRejected(
                MaterializationSourceSettlementDisposition.Rejected,
                MaterializationSourceDiagnosticCodes.SettlementClockRegression,
                "The PostgreSQL acknowledgement clock precedes its request time.",
                request,
                expected: $"settledAtUtc>={request.RequestedAtUtc.ToString("O", CultureInfo.InvariantCulture)}",
                observed: settledAtUtc.ToString("O", CultureInfo.InvariantCulture));
        }

        await operationGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            lock (settlementGate)
            {
                if (settlements.TryGetValue(request.Id, out var raced))
                {
                    if (raced.Request != request)
                    {
                        return SettlementRejected(
                            MaterializationSourceSettlementDisposition.IdentityConflict,
                            MaterializationSourceDiagnosticCodes.SettlementIdentityConflict,
                            "The settlement identity was concurrently used for another PostgreSQL acknowledgement request.",
                            request,
                            expected: "one exact checkpoint and position per settlement identity",
                            observed: "a conflicting request won the local settlement race");
                    }
                    ObserveOperation(
                        PostgresLogicalReplicationOperationKind.SourceSettlement,
                        PostgresLogicalReplicationOperationDisposition.Replayed,
                        startedAtUtc,
                        context.UtcNow,
                        attempt: 1,
                        transactionCount: 0,
                        changeCount: 0,
                        canonicalByteCount: 0,
                        "settlement-local-replay");
                    return new(
                        MaterializationSourceSettlementDisposition.Replayed,
                        raced.Receipt);
                }
            }
            var admissibleAtUtc = settledAtUtc >= startedAtUtc
                ? settledAtUtc
                : startedAtUtc;
            var (feedback, attempt) = await ExecuteWithRetryAsync(
                context,
                PostgresLogicalReplicationOperationKind.SourceSettlement,
                startedAtUtc,
                async cancellationToken =>
                {
                    var deployment = await protocol.InspectAsync(cancellationToken).ConfigureAwait(false);
                    RequireDeployment(
                        deployment,
                        affinity,
                        reader,
                        placement,
                        runtimeBinding,
                        binding,
                        table);
                    RequireSettleablePosition(position, deployment);
                    var result = await protocol.SettleAsync(
                        position.WalPosition,
                        policy.SettlementConfirmationTimeout,
                        policy.SettlementConfirmationPollInterval,
                        cancellationToken).ConfigureAwait(false);
                    var newlyConfirmed =
                        result.Disposition == PostgresLogicalReplicationFeedbackDisposition.Confirmed
                        && result.PriorConfirmedPosition == deployment.ConfirmedFlushPosition
                        && result.PriorConfirmedPosition < position.WalPosition
                        && result.ConfirmedPosition == position.WalPosition;
                    var alreadyConfirmed =
                        result.Disposition == PostgresLogicalReplicationFeedbackDisposition.AlreadyConfirmed
                        && result.PriorConfirmedPosition >= position.WalPosition
                        && result.ConfirmedPosition == result.PriorConfirmedPosition;
                    if (!newlyConfirmed && !alreadyConfirmed)
                    {
                        throw ProtocolFailure(
                            PostgresLogicalReplicationFailureKind.SettlementUnconfirmed,
                            "settlement-confirmation-not-exact");
                    }
                    return result;
                }).ConfigureAwait(false);

            settledAtUtc = context.UtcNow.ToUniversalTime();
            if (settledAtUtc < admissibleAtUtc)
                settledAtUtc = admissibleAtUtc;
            var receipt = new MaterializationSourceSettlement(
                id: request.Id,
                checkpoint: request.Checkpoint,
                position: request.Position,
                settledAtUtc: settledAtUtc,
                evidenceReference: Evidence(string.Concat(
                    "settlement-confirmed/",
                    position.WalPosition.ToString())));
            var disposition = feedback.Disposition == PostgresLogicalReplicationFeedbackDisposition.Confirmed
                ? MaterializationSourceSettlementDisposition.Acknowledged
                : MaterializationSourceSettlementDisposition.Replayed;
            lock (settlementGate)
            {
                if (settlements.TryGetValue(request.Id, out var raced))
                {
                    if (raced.Request != request)
                    {
                        return SettlementRejected(
                            MaterializationSourceSettlementDisposition.IdentityConflict,
                            MaterializationSourceDiagnosticCodes.SettlementIdentityConflict,
                            "The settlement identity was concurrently used for another PostgreSQL acknowledgement request.",
                            request,
                            expected: "one exact checkpoint and position per settlement identity",
                            observed: "a conflicting request won the local settlement race");
                    }
                    receipt = raced.Receipt;
                    disposition = MaterializationSourceSettlementDisposition.Replayed;
                }
                else
                {
                    settlements.Add(request.Id, new(request, receipt));
                }
            }
            ObserveOperation(
                PostgresLogicalReplicationOperationKind.SourceSettlement,
                disposition == MaterializationSourceSettlementDisposition.Acknowledged
                    ? PostgresLogicalReplicationOperationDisposition.Acknowledged
                    : PostgresLogicalReplicationOperationDisposition.Replayed,
                startedAtUtc,
                settledAtUtc,
                attempt,
                transactionCount: 0,
                changeCount: 0,
                canonicalByteCount: 0,
                "settlement-confirmed");
            return new(disposition, receipt);
        }
        finally
        {
            operationGate.Release();
        }
    }

    async ValueTask<MaterializationSourcePosition> CapturePositionAsync(
        OperationContext context,
        MaterializationSourceScope scope,
        bool retainedStart)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireScope(scope, nameof(scope));
        context.CancellationToken.ThrowIfCancellationRequested();
        var operation = PostgresLogicalReplicationOperationKind.CaptureCurrentPosition;
        var startedAtUtc = context.UtcNow.ToUniversalTime();
        await operationGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var (deployment, attempt) = await ExecuteWithRetryAsync(
                context,
                operation,
                startedAtUtc,
                InspectDeploymentAsync).ConfigureAwait(false);
            var walPosition = retainedStart
                ? deployment.ConfirmedFlushPosition
                : deployment.CurrentWalPosition;
            var position = CreatePosition(PostgresLogicalReplicationPositionKind.WalCut, walPosition);
            ObserveOperation(
                operation,
                PostgresLogicalReplicationOperationDisposition.Complete,
                startedAtUtc,
                context.UtcNow,
                attempt,
                transactionCount: 0,
                changeCount: 0,
                canonicalByteCount: 0,
                retainedStart ? "retained-start-captured" : "current-position-captured");
            return position;
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal MaterializationSourcePosition CreatePosition(
        PostgresLogicalReplicationPositionKind kind,
        PostgresLogicalReplicationWalPosition walPosition)
    {
        var payload = new PositionPayload(
            PlanAlgorithm: Scope.PhysicalPlan.Algorithm,
            PlanCanonicalization: Scope.PhysicalPlan.Canonicalization,
            PlanValue: Scope.PhysicalPlan.Value,
            Placement: placement.Id.Value,
            RuntimeDatabase: runtimeBinding.Database.Value,
            StorageBinding: reader.StorageBinding.Fingerprint.Value,
            SystemIdentifier: affinity.SystemIdentifier,
            Timeline: affinity.Timeline,
            DatabaseName: affinity.DatabaseName,
            Publication: binding.PublicationName,
            Slot: binding.SlotName,
            SlotGeneration: binding.SlotGeneration,
            Kind: (int)kind,
            WalPosition: walPosition.ToString());
        return new(
            PositionFormatVersion,
            Scope,
            positionCodec.Encode(JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJsonOptions)));
    }

    PositionCursor DecodePosition(MaterializationSourcePosition position, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.Scope != Scope || position.FormatVersion != PositionFormatVersion)
        {
            throw new ArgumentException(
                "The PostgreSQL logical-replication position belongs to another scope or format.",
                parameterName);
        }

        var payloadBytes = positionCodec.Decode(position.Value, parameterName, "PostgreSQL WAL position");
        PositionPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<PositionPayload>(payloadBytes, CanonicalJsonOptions)
                ?? throw new JsonException("The PostgreSQL WAL-position payload is null.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The PostgreSQL logical-replication position payload is malformed.",
                parameterName,
                exception);
        }
        if (!Enum.IsDefined(typeof(PostgresLogicalReplicationPositionKind), payload.Kind)
            || !PostgresLogicalReplicationWalPosition.TryParse(payload.WalPosition, out var walPosition)
            || !MatchesPositionAffinity(payload)
            || !payloadBytes.AsSpan().SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJsonOptions)))
        {
            throw new ArgumentException(
                "The PostgreSQL logical-replication position is noncanonical or conflicts with its exact source affinity.",
                parameterName);
        }
        return new((PostgresLogicalReplicationPositionKind)payload.Kind, walPosition);
    }

    async ValueTask<MaterializationChangePage> ReadChangesCoreAsync(
        OperationContext context,
        MaterializationChangeReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        RequireScope(request.Scope, nameof(request));
        MaterializationCapabilityLimits.RequireSupportedBounds(
            Descriptor.CapabilityProfile,
            MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationLimitKind.ChangeItems,
            request.MaximumDeliveries,
            MaterializationLimitKind.ReadBytes,
            request.MaximumBytes,
            nameof(request));

        // Authentication, canonicalization, and immutable affinity are verified before any provider I/O.
        var after = DecodePosition(request.AfterPosition, nameof(request));
        context.CancellationToken.ThrowIfCancellationRequested();
        var startedAtUtc = context.UtcNow.ToUniversalTime();
        await operationGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var (result, attempt) = await ExecuteWithRetryAsync(
                context,
                PostgresLogicalReplicationOperationKind.ChangeRead,
                startedAtUtc,
                cancellationToken => ReadChangesAttemptAsync(
                    context,
                    request,
                    after,
                    cancellationToken)).ConfigureAwait(false);
            ObserveOperation(
                PostgresLogicalReplicationOperationKind.ChangeRead,
                result.Page.State switch
                {
                    MaterializationChangePageState.CaughtUp =>
                        PostgresLogicalReplicationOperationDisposition.CaughtUp,
                    MaterializationChangePageState.Progressed =>
                        PostgresLogicalReplicationOperationDisposition.Progressed,
                    _ => PostgresLogicalReplicationOperationDisposition.Partial
                },
                startedAtUtc,
                context.UtcNow,
                attempt,
                result.TransactionCount,
                result.Page.Deliveries.Length,
                result.CanonicalBytes,
                result.EvidenceReference);
            return result.Page;
        }
        finally
        {
            operationGate.Release();
        }
    }

    async ValueTask<ChangeReadResult> ReadChangesAttemptAsync(
        OperationContext context,
        MaterializationChangeReadRequest request,
        PositionCursor after,
        CancellationToken cancellationToken)
    {
        var deployment = await protocol.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireDeployment(
            deployment,
            affinity,
            reader,
            placement,
            runtimeBinding,
            binding,
            table);
        RequireReadablePosition(after, deployment);
        if (after.WalPosition == deployment.CurrentWalPosition)
        {
            return new(
                new MaterializationChangePage(
                    [],
                    CreatePosition(after.Kind, after.WalPosition),
                    MaterializationChangePageState.CaughtUp),
                TransactionCount: 0,
                CanonicalBytes: 0,
                EvidenceReference: "already-at-current-wal-boundary");
        }

        var provider = await protocol.ReadAsync(
            after.WalPosition,
            deployment.CurrentWalPosition,
            policy.MaximumTransactionsPerRead,
            request.MaximumDeliveries,
            request.MaximumBytes,
            policy.MaximumTransactionChanges,
            policy.MaximumTransactionBytes,
            policy.ReadInactivityTimeout,
            cancellationToken).ConfigureAwait(false);
        ValidateProviderBatch(provider, after.WalPosition, deployment.CurrentWalPosition);

        var deliveries = ImmutableArray.CreateBuilder<MaterializationChangeDelivery>();
        var admittedTransactions = 0;
        long canonicalBytes = 0;
        var admittedThrough = after.WalPosition;
        var allProviderTransactionsAdmitted = true;
        foreach (var transaction in provider.Transactions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projected = ProjectTransaction(context, transaction);
            long transactionBytes = 0;
            foreach (var delivery in projected)
            {
                transactionBytes = checked(transactionBytes + CanonicalByteCount(delivery));
            }
            if (projected.Length > policy.MaximumTransactionChanges
                || transactionBytes > policy.MaximumTransactionBytes)
            {
                throw ProtocolFailure(
                    PostgresLogicalReplicationFailureKind.TransactionLimitExceeded,
                    "canonical-transaction-hard-limit-exceeded");
            }

            var crossesItemBudget = projected.Length > request.MaximumDeliveries - deliveries.Count;
            var crossesByteBudget = transactionBytes > request.MaximumBytes - canonicalBytes;
            if (admittedTransactions > 0 && (crossesItemBudget || crossesByteBudget))
            {
                allProviderTransactionsAdmitted = false;
                break;
            }

            deliveries.AddRange(projected);
            canonicalBytes = checked(canonicalBytes + transactionBytes);
            admittedTransactions++;
            admittedThrough = transaction.EndPosition;
            if (crossesItemBudget || crossesByteBudget)
            {
                allProviderTransactionsAdmitted = admittedTransactions == provider.Transactions.Length;
                break;
            }
        }

        var materialized = deliveries.ToImmutable();
        var scannedThrough = allProviderTransactionsAdmitted
            ? provider.ScannedThrough
            : admittedThrough;
        if (!allProviderTransactionsAdmitted && admittedTransactions == 0)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.ProtocolViolation,
                "transaction-admission-made-no-progress");
        }
        var throughKind = allProviderTransactionsAdmitted && provider.ReachedUpperBoundary
            ? PostgresLogicalReplicationPositionKind.WalCut
            : PostgresLogicalReplicationPositionKind.TransactionEnd;
        var caughtUp = allProviderTransactionsAdmitted && provider.ReachedUpperBoundary;
        var state = caughtUp
            ? MaterializationChangePageState.CaughtUp
            : materialized.IsDefaultOrEmpty
                ? MaterializationChangePageState.Progressed
                : MaterializationChangePageState.MoreAvailable;
        return new(
            new MaterializationChangePage(
                materialized,
                CreatePosition(throughKind, scannedThrough),
                state),
            admittedTransactions,
            canonicalBytes,
            caughtUp ? "change-read-caught-up" : materialized.IsDefaultOrEmpty
                ? "change-read-progressed"
                : "change-read-partial");
    }

    void RequireReadablePosition(
        PositionCursor position,
        PostgresLogicalReplicationDeployment deployment)
    {
        if (position.WalPosition < deployment.RestartPosition)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.PositionUnavailable,
                "position-precedes-restart-lsn");
        }
        if (position.WalPosition < deployment.ConfirmedFlushPosition)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.PositionUnavailable,
                "slot-confirmed-ahead-of-position");
        }
        if (position.WalPosition > deployment.CurrentWalPosition)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.PositionUnavailable,
                "position-ahead-of-current-wal");
        }
    }

    async ValueTask<PostgresLogicalReplicationDeployment> InspectDeploymentAsync(
        CancellationToken cancellationToken)
    {
        var deployment = await protocol.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireDeployment(
            deployment,
            affinity,
            reader,
            placement,
            runtimeBinding,
            binding,
            table);
        return deployment;
    }

    async ValueTask<HealthInspectionResult> InspectHealthAttemptAsync(
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var deployment = await protocol.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireDeployment(
            deployment,
            affinity,
            reader,
            placement,
            runtimeBinding,
            binding,
            table,
            allowActiveSlot: true,
            allowUnhealthyWal: true);
        var now = context.UtcNow.ToUniversalTime();
        if (deployment.SafeWalBytes < 0)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.ProtocolViolation,
                "slot-safe-wal-evidence-invalid");
        }
        var inactivity = deployment.InactiveSinceUtc switch
        {
            null => (TimeSpan?)null,
            { } inactive when inactive.Offset != TimeSpan.Zero || inactive > now =>
                throw ProtocolFailure(
                    PostgresLogicalReplicationFailureKind.ProtocolViolation,
                    "slot-inactivity-evidence-invalid"),
            { } inactive => now - inactive
        };
        var retainedBytes = WalDistance(deployment.RestartPosition, deployment.CurrentWalPosition);
        var pendingBytes = WalDistance(
            deployment.ConfirmedFlushPosition,
            deployment.CurrentWalPosition);
        var state = deployment.WalState is PostgresLogicalReplicationWalState.Lost
                or PostgresLogicalReplicationWalState.Unreserved
                or PostgresLogicalReplicationWalState.Unknown
            || deployment.InvalidationReason is not null
            ? PostgresLogicalReplicationHealthState.SlotLost
            : retainedBytes >= policy.RetentionDangerBytes
                || inactivity >= policy.RetentionDangerTime
                || deployment.SafeWalBytes == 0
                ? PostgresLogicalReplicationHealthState.RetentionDanger
                : !deployment.IsActive && inactivity.HasValue
                    ? PostgresLogicalReplicationHealthState.Inactive
                    : PostgresLogicalReplicationHealthState.Healthy;
        return new(
            state,
            now,
            pendingBytes,
            retainedBytes,
            deployment.SafeWalBytes,
            inactivity);
    }

    static void RequireSettleablePosition(
        PositionCursor position,
        PostgresLogicalReplicationDeployment deployment)
    {
        if (position.WalPosition > deployment.CurrentWalPosition)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.PositionUnavailable,
                "settlement-position-ahead-of-current-wal");
        }
        if (position.WalPosition < deployment.RestartPosition
            && position.WalPosition > deployment.ConfirmedFlushPosition)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.PositionUnavailable,
                "unconfirmed-settlement-position-not-retained");
        }
    }

    void ValidateProviderBatch(
        PostgresLogicalReplicationReadBatch batch,
        PostgresLogicalReplicationWalPosition after,
        PostgresLogicalReplicationWalPosition upperBoundary)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Transactions.IsDefault
            || batch.Transactions.Length > policy.MaximumTransactionsPerRead
            || batch.ScannedThrough < after
            || batch.ScannedThrough > upperBoundary
            || batch.ReachedUpperBoundary && batch.ScannedThrough != upperBoundary
            || batch.Transactions.IsDefaultOrEmpty
                && batch.ScannedThrough == after
                && !batch.ReachedUpperBoundary)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.ProtocolViolation,
                "provider-batch-boundary-invalid");
        }

        var priorEnd = after;
        foreach (var transaction in batch.Transactions)
        {
            if (transaction is null
                || transaction.TransactionId == 0
                || transaction.EndPosition <= priorEnd
                || transaction.EndPosition > batch.ScannedThrough
                || transaction.FinalPosition != transaction.CommitPosition
                || transaction.FinalPosition <= priorEnd
                || transaction.FinalPosition > transaction.EndPosition
                || transaction.CommittedAtUtc.Offset != TimeSpan.Zero
                || transaction.RetainedBytes < 0
                || transaction.Mutations.IsDefault
                || transaction.Mutations.IsDefaultOrEmpty)
            {
                throw ProtocolFailure(
                    PostgresLogicalReplicationFailureKind.ProtocolViolation,
                    "provider-transaction-invalid");
            }
            if (transaction.RetainedBytes > policy.MaximumTransactionBytes
                || transaction.Mutations.Length > policy.MaximumTransactionChanges)
            {
                throw ProtocolFailure(
                    PostgresLogicalReplicationFailureKind.TransactionLimitExceeded,
                    "provider-transaction-hard-limit-exceeded");
            }

            var expectedOrdinal = 0;
            foreach (var mutation in transaction.Mutations)
            {
                if (mutation is null
                    || mutation.Ordinal != expectedOrdinal
                    || !Enum.IsDefined(mutation.Kind)
                    || !Enum.IsDefined(mutation.ReplicaIdentity)
                    || mutation.ReplicaIdentity != binding.ExpectedReplicaIdentity.Kind)
                {
                    throw ProtocolFailure(
                        PostgresLogicalReplicationFailureKind.ProtocolViolation,
                        "provider-mutation-order-invalid");
                }
                expectedOrdinal++;
            }
            priorEnd = transaction.EndPosition;
        }
    }

    ImmutableArray<MaterializationChangeDelivery> ProjectTransaction(
        OperationContext context,
        PostgresLogicalReplicationTransaction transaction)
    {
        var capacity = checked(Math.Min(
            policy.MaximumTransactionChanges,
            transaction.Mutations.Length * 2));
        var deliveries = ImmutableArray.CreateBuilder<MaterializationChangeDelivery>(capacity);
        var observedAtUtc = context.UtcNow.ToUniversalTime();
        if (observedAtUtc < transaction.CommittedAtUtc)
            observedAtUtc = transaction.CommittedAtUtc;
        var position = CreatePosition(
            PostgresLogicalReplicationPositionKind.TransactionEnd,
            transaction.EndPosition);
        foreach (var mutation in transaction.Mutations)
        {
            switch (mutation.Kind)
            {
                case PostgresLogicalReplicationMutationKind.Insert:
                    {
                        if (mutation.OldRow is not null || mutation.NewRow is null)
                        {
                            throw ChangeEvidenceFailure("insert-row-image-invalid");
                        }
                        var after = ProjectObservation(
                            mutation.NewRow,
                            unchangedToastSource: null,
                            allowUnchangedToast: false,
                            "insert-after");
                        deliveries.Add(CreateDelivery(
                            transaction,
                            mutation,
                            subordinal: 0,
                            MaterializationChangeKind.Create,
                            before: null,
                            after,
                            after.Identity,
                            position,
                            observedAtUtc));
                        break;
                    }
                case PostgresLogicalReplicationMutationKind.Update:
                    {
                        if (mutation.NewRow is null
                            || binding.ExpectedReplicaIdentity.ProvidesCompleteBeforeImage
                                && mutation.OldRow is null)
                        {
                            throw ChangeEvidenceFailure("update-row-image-invalid");
                        }
                        var before = binding.ExpectedReplicaIdentity.ProvidesCompleteBeforeImage
                            ? ProjectObservation(
                                mutation.OldRow!,
                                unchangedToastSource: null,
                                allowUnchangedToast: false,
                                "update-before")
                            : null;
                        var after = ProjectObservation(
                            mutation.NewRow,
                            unchangedToastSource: mutation.OldRow,
                            allowUnchangedToast: binding.ExpectedReplicaIdentity.ProvidesCompleteBeforeImage,
                            "update-after");
                        var oldIdentity = before?.Identity
                            ?? (mutation.OldRow is null ? after.Identity : ProjectIdentity(mutation.OldRow));
                        if (string.Equals(oldIdentity, after.Identity, StringComparison.Ordinal))
                        {
                            deliveries.Add(CreateDelivery(
                                transaction,
                                mutation,
                                subordinal: 0,
                                MaterializationChangeKind.Update,
                                before,
                                after,
                                after.Identity,
                                position,
                                observedAtUtc));
                        }
                        else
                        {
                            deliveries.Add(CreateDelivery(
                                transaction,
                                mutation,
                                subordinal: 0,
                                MaterializationChangeKind.Delete,
                                before,
                                after: null,
                                oldIdentity,
                                position,
                                observedAtUtc));
                            deliveries.Add(CreateDelivery(
                                transaction,
                                mutation,
                                subordinal: 1,
                                MaterializationChangeKind.Create,
                                before: null,
                                after,
                                after.Identity,
                                position,
                                observedAtUtc));
                        }
                        break;
                    }
                case PostgresLogicalReplicationMutationKind.Delete:
                    {
                        if (mutation.OldRow is null || mutation.NewRow is not null)
                        {
                            throw ChangeEvidenceFailure("delete-row-image-invalid");
                        }
                        var before = binding.ExpectedReplicaIdentity.ProvidesCompleteBeforeImage
                            ? ProjectObservation(
                                mutation.OldRow,
                                unchangedToastSource: null,
                                allowUnchangedToast: false,
                                "delete-before")
                            : null;
                        var identity = before?.Identity ?? ProjectIdentity(mutation.OldRow);
                        deliveries.Add(CreateDelivery(
                            transaction,
                            mutation,
                            subordinal: 0,
                            MaterializationChangeKind.Delete,
                            before,
                            after: null,
                            identity,
                            position,
                            observedAtUtc));
                        break;
                    }
                default:
                    throw ProtocolFailure(
                        PostgresLogicalReplicationFailureKind.ProtocolViolation,
                        "unsupported-mutation-kind");
            }
        }
        return deliveries.Count == deliveries.Capacity
            ? deliveries.MoveToImmutable()
            : deliveries.ToImmutable();
    }

    MaterializationChangeDelivery CreateDelivery(
        PostgresLogicalReplicationTransaction transaction,
        PostgresLogicalReplicationMutation mutation,
        int subordinal,
        MaterializationChangeKind kind,
        RelationQuerySourceReadObservation? before,
        RelationQuerySourceReadObservation? after,
        string subjectIdentity,
        MaterializationSourcePosition position,
        DateTimeOffset observedAtUtc)
    {
        var stableIdentity = StableChangeIdentity(
            transaction,
            mutation.Ordinal,
            subordinal);
        var evidence = Evidence(string.Concat(
            "transaction/", transaction.EndPosition.ToString(),
            "/xid/", transaction.TransactionId.ToString(CultureInfo.InvariantCulture),
            "/mutation/", mutation.Ordinal.ToString(CultureInfo.InvariantCulture),
            "/subordinal/", subordinal.ToString(CultureInfo.InvariantCulture)));
        var change = new MaterializationChangeEnvelope(
            new(string.Concat("postgres-change/v1/", stableIdentity)),
            subjectIdentity,
            Scope,
            Scope.Shape,
            position,
            kind,
            before,
            after,
            transaction.CommittedAtUtc,
            observedAtUtc,
            evidence);
        return new(
            new(string.Concat("postgres-delivery/v1/", stableIdentity)),
            change,
            observedAtUtc,
            evidence);
    }

    string StableChangeIdentity(
        PostgresLogicalReplicationTransaction transaction,
        int ordinal,
        int subordinal)
    {
        var text = string.Concat(
            reader.StorageBinding.Fingerprint.Value, "\0",
            Scope.PhysicalPlan.Algorithm, "\0",
            Scope.PhysicalPlan.Canonicalization, "\0",
            Scope.PhysicalPlan.Value, "\0",
            placement.Id.Value, "\0",
            affinity.SystemIdentifier, "\0",
            affinity.Timeline.ToString(CultureInfo.InvariantCulture), "\0",
            affinity.DatabaseName, "\0",
            binding.PublicationName, "\0",
            binding.SlotName, "\0",
            binding.SlotGeneration, "\0",
            transaction.EndPosition.ToString(), "\0",
            transaction.TransactionId.ToString(CultureInfo.InvariantCulture), "\0",
            ordinal.ToString(CultureInfo.InvariantCulture), "\0",
            subordinal.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    RelationQuerySourceReadObservation ProjectObservation(
        PostgresLogicalReplicationRow row,
        PostgresLogicalReplicationRow? unchangedToastSource,
        bool allowUnchangedToast,
        string evidenceSuffix)
    {
        var cells = ValidateRow(row, "projected-row-invalid");
        var oldCells = unchangedToastSource is null
            ? null
            : ValidateRow(unchangedToastSource, "unchanged-toast-source-invalid");
        var identity = ProjectIdentity(cells, oldCells, allowUnchangedToast);
        var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(projection.Length);
        foreach (var column in projection)
        {
            if (!cells.TryGetValue(column.ColumnName, out var cell))
            {
                throw ChangeEvidenceFailure("projected-column-missing");
            }
            if (cell.Kind == PostgresLogicalReplicationCellKind.UnchangedToast)
            {
                if (!allowUnchangedToast
                    || oldCells is null
                    || !oldCells.TryGetValue(column.ColumnName, out cell)
                    || cell.Kind == PostgresLogicalReplicationCellKind.UnchangedToast)
                {
                    throw ChangeEvidenceFailure("unchanged-toast-evidence-unavailable");
                }
            }
            fields.Add(ProjectField(column, cell, evidenceSuffix));
        }
        return new(identity, Scope.Shape, fields.MoveToImmutable());
    }

    string ProjectIdentity(PostgresLogicalReplicationRow row) =>
        ProjectIdentity(ValidateRow(row, "identity-row-invalid"));

    string ProjectIdentity(
        IReadOnlyDictionary<string, PostgresLogicalReplicationCell> cells,
        IReadOnlyDictionary<string, PostgresLogicalReplicationCell>? unchangedToastSource = null,
        bool allowUnchangedToast = false)
    {
        var identityBinding = table.Identity!;
        if (!cells.TryGetValue(identityBinding.ColumnName, out var identity))
            throw ChangeEvidenceFailure("observation-identity-unavailable");
        if (identity.Kind == PostgresLogicalReplicationCellKind.UnchangedToast)
        {
            if (!allowUnchangedToast
                || unchangedToastSource is null
                || !unchangedToastSource.TryGetValue(identityBinding.ColumnName, out identity)
                || identity.Kind == PostgresLogicalReplicationCellKind.UnchangedToast)
            {
                throw ChangeEvidenceFailure("unchanged-toast-identity-evidence-unavailable");
            }
        }
        if (identity.Kind != PostgresLogicalReplicationCellKind.Value
            || identity.Value is null)
        {
            throw ChangeEvidenceFailure("observation-identity-unavailable");
        }
        try
        {
            var formatted = PostgresRelationQueryScalarCatalog.FormatKey(
                identity.Value,
                identityBinding.ScalarType);
            if (string.IsNullOrWhiteSpace(formatted))
                throw new FormatException("An observation identity cannot be empty.");
            return formatted;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new PostgresLogicalReplicationProtocolException(
                PostgresLogicalReplicationFailureKind.ChangeEvidenceUnavailable,
                isTransient: false,
                Evidence("observation-identity-invalid"),
                exception);
        }
    }

    RelationQuerySourceReadFieldResult ProjectField(
        ChangeProjectionColumn column,
        PostgresLogicalReplicationCell cell,
        string evidenceSuffix)
    {
        var evidence = Evidence(string.Concat(
            evidenceSuffix,
            "/field/",
            Uri.EscapeDataString(column.Field.SemanticPath.ToString())));
        if (cell.Kind == PostgresLogicalReplicationCellKind.Null)
        {
            if (column.MissingValueEncoding == PostgresRelationQueryMissingValueEncoding.SqlNull)
                return new(column.Field, RelationQuerySourceReadFieldState.Missing, evidenceReference: evidence);
            if (column.NullValueEncoding == PostgresRelationQueryNullValueEncoding.SqlNull)
                return new(column.Field, RelationQuerySourceReadFieldState.Null, evidenceReference: evidence);
            throw ChangeEvidenceFailure("null-value-contract-violation");
        }
        if (cell.Kind != PostgresLogicalReplicationCellKind.Value || cell.Value is null)
        {
            throw ChangeEvidenceFailure("field-value-unavailable");
        }
        try
        {
            return new(
                column.Field,
                RelationQuerySourceReadFieldState.Value,
                PostgresRelationQueryScalarCatalog.ToObservationValue(cell.Value, column.ScalarType),
                evidence);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new PostgresLogicalReplicationProtocolException(
                PostgresLogicalReplicationFailureKind.ChangeEvidenceUnavailable,
                isTransient: false,
                Evidence("field-value-invalid"),
                exception);
        }
    }

    Dictionary<string, PostgresLogicalReplicationCell> ValidateRow(
        PostgresLogicalReplicationRow row,
        string evidenceSuffix)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Cells.IsDefault)
            throw ChangeEvidenceFailure(evidenceSuffix);
        Dictionary<string, PostgresLogicalReplicationCell> cells = new(
            row.Cells.Length,
            StringComparer.Ordinal);
        foreach (var cell in row.Cells)
        {
            if (cell is null
                || string.IsNullOrWhiteSpace(cell.ColumnName)
                || !Enum.IsDefined(cell.Kind)
                || cell.EncodedBytes < 0
                || cell.Kind == PostgresLogicalReplicationCellKind.Value && cell.Value is null
                || cell.Kind != PostgresLogicalReplicationCellKind.Value && cell.Value is not null
                || !cells.TryAdd(cell.ColumnName, cell))
            {
                throw ChangeEvidenceFailure(evidenceSuffix);
            }
        }
        return cells;
    }

    PostgresLogicalReplicationProtocolException ChangeEvidenceFailure(string evidenceSuffix) => new(
        PostgresLogicalReplicationFailureKind.ChangeEvidenceUnavailable,
        isTransient: false,
        Evidence(evidenceSuffix));

    static ImmutableArray<ChangeProjectionColumn> CreateProjection(
        RelationQuerySourcePlacementBinding placement,
        PostgresRelationQueryTableBinding table)
    {
        var columns = ImmutableArray.CreateBuilder<ChangeProjectionColumn>(
            table.Fields.Length + table.RelationshipReferences.Length);
        HashSet<string> includedPhysicalColumns = new(StringComparer.Ordinal);
        foreach (var field in table.Fields)
        {
            var placed = placement.Fields.SingleOrDefault(candidate =>
                candidate.Input == field.Input
                && candidate.SemanticPath == field.SemanticPath)
                ?? throw new ArgumentException(
                    "The PostgreSQL placement and table disagree on a logical-replication semantic field selector.",
                    nameof(placement));
            var alsoCorrelation = table.RelationshipReferences.Any(reference =>
                string.Equals(reference.ColumnName, field.ColumnName, StringComparison.Ordinal)
                && reference.SemanticPath == field.SemanticPath)
                && placement.RelationshipKeys.Any(key =>
                    key.SemanticPath == field.SemanticPath);
            columns.Add(new(
                new(
                    placed.Input,
                    placed.SemanticPath,
                    placed.SourceSelector,
                    alsoCorrelation
                        ? RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation
                        : RelationQuerySourceReadFieldPurpose.SemanticInput),
                field.ColumnName,
                field.ScalarType,
                field.MissingValueEncoding,
                field.NullValueEncoding));
            includedPhysicalColumns.Add(field.ColumnName);
        }
        foreach (var reference in table.RelationshipReferences)
        {
            if (includedPhysicalColumns.Contains(reference.ColumnName))
                continue;
            var placed = placement.RelationshipKeys.SingleOrDefault(candidate =>
                candidate.Input == reference.Input
                && candidate.SemanticPath == reference.SemanticPath)
                ?? throw new ArgumentException(
                    "The PostgreSQL placement and table disagree on a logical-replication relationship selector.",
                    nameof(placement));
            columns.Add(new(
                new(
                    input: null,
                    placed.SemanticPath,
                    placed.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.Correlation),
                reference.ColumnName,
                reference.ScalarType,
                reference.MissingValueEncoding,
                reference.NullValueEncoding));
            includedPhysicalColumns.Add(reference.ColumnName);
        }
        return columns.Count == columns.Capacity
            ? columns.MoveToImmutable()
            : columns.ToImmutable();
    }

    static long CanonicalByteCount<T>(T value) where T : class =>
        StrictDocumentJson.GetCanonicalBytes(value, CanonicalJsonOptions).LongLength;

    bool MatchesPositionAffinity(PositionPayload payload) =>
        string.Equals(payload.PlanAlgorithm, Scope.PhysicalPlan.Algorithm, StringComparison.Ordinal)
        && string.Equals(payload.PlanCanonicalization, Scope.PhysicalPlan.Canonicalization, StringComparison.Ordinal)
        && string.Equals(payload.PlanValue, Scope.PhysicalPlan.Value, StringComparison.Ordinal)
        && string.Equals(payload.Placement, placement.Id.Value, StringComparison.Ordinal)
        && string.Equals(payload.RuntimeDatabase, runtimeBinding.Database.Value, StringComparison.Ordinal)
        && string.Equals(payload.StorageBinding, reader.StorageBinding.Fingerprint.Value, StringComparison.Ordinal)
        && string.Equals(payload.SystemIdentifier, affinity.SystemIdentifier, StringComparison.Ordinal)
        && payload.Timeline == affinity.Timeline
        && string.Equals(payload.DatabaseName, affinity.DatabaseName, StringComparison.Ordinal)
        && string.Equals(payload.Publication, binding.PublicationName, StringComparison.Ordinal)
        && string.Equals(payload.Slot, binding.SlotName, StringComparison.Ordinal)
        && string.Equals(payload.SlotGeneration, binding.SlotGeneration, StringComparison.Ordinal);

    static MaterializationCapabilityProfile CreateCapabilityProfile(
        PostgresMaterializationSource baseline,
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        PostgresRelationQueryTableBinding table,
        PostgresLogicalReplicationBinding binding,
        PostgresLogicalReplicationSourcePolicy policy,
        DeploymentAffinity affinity)
    {
        var references = ImmutableArray.Create(
            EvidencePrefix,
            $"postgres-binding/sha256/{reader.StorageBinding.Fingerprint.Value}",
            $"postgres-runtime/{reader.RuntimeEvidenceReference ?? "unattested"}",
            $"postgres-publication/{Uri.EscapeDataString(binding.PublicationName)}",
            $"postgres-slot/{Uri.EscapeDataString(binding.SlotName)}/generation/{Uri.EscapeDataString(binding.SlotGeneration)}",
            $"postgres-system/{Uri.EscapeDataString(affinity.SystemIdentifier)}/timeline/{affinity.Timeline.ToString(CultureInfo.InvariantCulture)}/database/{Uri.EscapeDataString(affinity.DatabaseName)}",
            $"postgres-table/{Uri.EscapeDataString(table.SchemaName)}/{Uri.EscapeDataString(table.TableName)}");
        var changeGuarantees = ImmutableArray.CreateBuilder<MaterializationGuaranteeKind>(7);
        changeGuarantees.Add(MaterializationGuaranteeKind.StableOrdering);
        changeGuarantees.Add(MaterializationGuaranteeKind.BaselinePlusCatchUp);
        changeGuarantees.Add(MaterializationGuaranteeKind.AtLeastOnceDelivery);
        changeGuarantees.Add(MaterializationGuaranteeKind.RetainedHistoryStart);
        changeGuarantees.Add(MaterializationGuaranteeKind.CompleteMutationDelivery);
        changeGuarantees.Add(MaterializationGuaranteeKind.TransactionAlignedDelivery);
        if (binding.ExpectedReplicaIdentity.ProvidesCompleteBeforeImage)
            changeGuarantees.Add(MaterializationGuaranteeKind.BeforeImage);
        var changeEvidence = new MaterializationCapabilityEvidence(
            new("cohesive.adapters.postgres/logical-replication/change-delivery/v1"),
            MaterializationCapabilityKind.SourceChangeDelivery,
            CapabilityRealizationKind.Constrained,
            changeGuarantees.Count == changeGuarantees.Capacity
                ? changeGuarantees.MoveToImmutable()
                : changeGuarantees.ToImmutable(),
            [
                new(MaterializationLimitKind.ChangeItems, policy.MaximumTransactionChanges),
                new(MaterializationLimitKind.ReadBytes, policy.MaximumTransactionBytes),
                new(MaterializationLimitKind.TransactionItems, policy.MaximumTransactionChanges),
                new(MaterializationLimitKind.TransactionBytes, policy.MaximumTransactionBytes),
                new(MaterializationLimitKind.Parallelism, 1)
            ],
            references,
            "Complete pgoutput mutations delivered in indivisible committed-transaction pages with explicit WAL retention bounds.");
        var settlementEvidence = new MaterializationCapabilityEvidence(
            new("cohesive.adapters.postgres/logical-replication/settlement/v1"),
            MaterializationCapabilityKind.SourceSettlement,
            CapabilityRealizationKind.Native,
            [MaterializationGuaranteeKind.ExplicitSettlement],
            [new MaterializationOperatingLimit(MaterializationLimitKind.Parallelism, 1)],
            references,
            "Explicit confirmed_flush_lsn advancement only after application checkpointing.");
        return new(
            new(string.Concat(
                "cohesive.adapters.postgres/logical-replication-source/v1/",
                reader.StorageBinding.Fingerprint.Value,
                "/baseline/", Uri.EscapeDataString(baseline.Descriptor.CapabilityProfile.Id.Value),
                "/placement/", Uri.EscapeDataString(placement.Id.Value),
                "/system/", Uri.EscapeDataString(affinity.SystemIdentifier),
                "/timeline/", affinity.Timeline.ToString(CultureInfo.InvariantCulture),
                "/database/", Uri.EscapeDataString(affinity.DatabaseName),
                "/publication/", Uri.EscapeDataString(binding.PublicationName),
                "/slot/", Uri.EscapeDataString(binding.SlotName),
                "/generation/", Uri.EscapeDataString(binding.SlotGeneration),
                "/policy/", policy.MaximumTransactionChanges.ToString(CultureInfo.InvariantCulture), "-",
                policy.MaximumTransactionBytes.ToString(CultureInfo.InvariantCulture), "-",
                policy.MaximumTransactionsPerRead.ToString(CultureInfo.InvariantCulture))),
            MaterializationEndpointRole.Source,
            reader.Descriptor.Source.Value,
            [.. baseline.Descriptor.CapabilityProfile.Evidence, changeEvidence, settlementEvidence],
            "PostgreSQL baseline reads plus retained transaction-aligned pgoutput changes and explicit slot settlement.");
    }

    static void RequireDeployment(
        PostgresLogicalReplicationDeployment deployment,
        DeploymentAffinity? expectedAffinity,
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresLogicalReplicationBinding binding,
        PostgresRelationQueryTableBinding table,
        bool allowActiveSlot = false,
        bool allowUnhealthyWal = false)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (runtimeBinding.Database != reader.StorageBinding.Database
            || placement.Source != reader.Descriptor.Source
            || table.Source != placement.Source
            || table.PlacementBinding != placement.Id
            || table.Shape != placement.Shape
            || table.Identity is null)
        {
            throw new ArgumentException(
                "The PostgreSQL logical-replication runtime, reader, placement, and table lack exact affinity.",
                nameof(placement));
        }
        if (string.IsNullOrWhiteSpace(deployment.SystemIdentifier)
            || deployment.Timeline == 0
            || string.IsNullOrWhiteSpace(deployment.DatabaseName))
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.Terminal,
                "deployment-identity-invalid");
        }
        if (expectedAffinity is not null
            && !expectedAffinity.Equals(DeploymentAffinity.From(
                deployment,
                runtimeBinding.Database,
                reader.StorageBinding.Fingerprint.Value,
                binding.SlotGeneration)))
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.SlotGenerationMismatch,
                "deployment-affinity-drift");
        }
        if (!string.Equals(deployment.PublicationName, binding.PublicationName, StringComparison.Ordinal)
            || !deployment.PublishesInserts
            || !deployment.PublishesUpdates
            || !deployment.PublishesDeletes
            || deployment.PublishesTruncates
            || deployment.PublishesViaPartitionRoot
            || !deployment.IncludesTable
            || deployment.HasRowFilter
            || !deployment.IncludesAllTableColumns
            || !string.Equals(deployment.SchemaName, table.SchemaName, StringComparison.Ordinal)
            || !string.Equals(deployment.TableName, table.TableName, StringComparison.Ordinal))
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.PublicationMismatch,
                "publication-contract-mismatch");
        }
        if (!Equals(deployment.ReplicaIdentity, binding.ExpectedReplicaIdentity))
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch,
                "replica-identity-mismatch");
        }
        if (!binding.ExpectedReplicaIdentity.ProvidesCompleteBeforeImage
            && PostgresRelationQueryScalarCatalog.HasProjectedPayloadThatMayUseUnchangedToast(table))
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch,
                "non-full-replica-identity-cannot-reconstruct-unchanged-toast");
        }
        if (!string.Equals(deployment.SlotName, binding.SlotName, StringComparison.Ordinal)
            || !string.Equals(deployment.OutputPlugin, OutputPlugin, StringComparison.Ordinal)
            || !deployment.IsLogicalSlot
            || deployment.IsTemporarySlot
            || deployment.IsTwoPhaseSlot
            || !allowActiveSlot && deployment.IsActive
            || !allowUnhealthyWal && deployment.InvalidationReason is not null)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.SlotUnavailable,
                "slot-contract-mismatch");
        }
        if (!allowUnhealthyWal
                && deployment.WalState is PostgresLogicalReplicationWalState.Unreserved
                    or PostgresLogicalReplicationWalState.Lost
                    or PostgresLogicalReplicationWalState.Unknown
            || deployment.RestartPosition > deployment.ConfirmedFlushPosition
            || deployment.ConfirmedFlushPosition > deployment.CurrentWalPosition)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.PositionUnavailable,
                "slot-wal-boundary-invalid");
        }

        var columns = deployment.Columns.IsDefault ? [] : deployment.Columns;
        if (columns.Any(static column => column is null)
            || columns.Any(static column =>
                column.DataTypeId == 0
                || column.DomainBaseDataTypeId is 0
                || column.DomainBaseDataTypeId == column.DataTypeId)
            || columns.GroupBy(static column => column.Name, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.ProtocolViolation,
                "deployment-columns-invalid");
        }
        Dictionary<string, PostgresRelationQueryScalarType> requiredColumns = new(StringComparer.Ordinal);
        AddRequiredColumn(table.Identity.ColumnName, table.Identity.ScalarType);
        foreach (var field in table.Fields)
            AddRequiredColumn(field.ColumnName, field.ScalarType);
        foreach (var reference in table.RelationshipReferences)
            AddRequiredColumn(reference.ColumnName, reference.ScalarType);
        if (!requiredColumns.All(required => columns.Any(column =>
                string.Equals(column.Name, required.Key, StringComparison.Ordinal)
                && PostgresRelationQueryScalarCatalog.AcceptsPostgresType(
                    required.Value,
                    column.EffectiveDataTypeId)))
            || !columns.Any(column =>
                string.Equals(column.Name, table.Identity.ColumnName, StringComparison.Ordinal)
                && column.IsReplicaIdentity)
            || binding.ExpectedReplicaIdentity.ProvidesCompleteBeforeImage
                && columns.Any(static column => !column.IsReplicaIdentity))
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch,
                "replica-identity-columns-mismatch");
        }

        void AddRequiredColumn(
            string columnName,
            PostgresRelationQueryScalarType scalarType)
        {
            if (requiredColumns.TryGetValue(columnName, out var existing) && existing != scalarType)
            {
                throw ProtocolFailure(
                    PostgresLogicalReplicationFailureKind.Terminal,
                    "binding-column-scalar-conflict");
            }
            requiredColumns[columnName] = scalarType;
        }
    }

    void RequireScope(MaterializationSourceScope scope, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope != Scope)
        {
            throw new ArgumentException(
                "The request targets another PostgreSQL logical-replication source scope.",
                parameterName);
        }
    }

    async ValueTask<(T Result, int Attempt)> ExecuteWithRetryAsync<T>(
        OperationContext context,
        PostgresLogicalReplicationOperationKind operation,
        DateTimeOffset startedAtUtc,
        Func<CancellationToken, ValueTask<T>> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await action(context.CancellationToken).ConfigureAwait(false), attempt);
            }
            catch (PostgresLogicalReplicationProtocolException exception)
                when (exception.IsTransient && attempt <= policy.MaximumReconnectAttempts)
            {
                ObserveOperation(
                    operation,
                    PostgresLogicalReplicationOperationDisposition.Retrying,
                    startedAtUtc,
                    context.UtcNow,
                    attempt,
                    transactionCount: 0,
                    changeCount: 0,
                    canonicalByteCount: 0,
                    exception.EvidenceReference,
                    exception.FailureKind,
                    policy.ReconnectDelay);
                try
                {
                    await Task.Delay(
                        policy.ReconnectDelay,
                        context.TimeProvider,
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    ObserveOperation(
                        operation,
                        PostgresLogicalReplicationOperationDisposition.Canceled,
                        startedAtUtc,
                        context.UtcNow,
                        attempt,
                        transactionCount: 0,
                        changeCount: 0,
                        canonicalByteCount: 0,
                        "retry-delay-canceled");
                    throw;
                }
            }
            catch (PostgresLogicalReplicationProtocolException exception)
            {
                throw CreateFailure(
                    operation,
                    startedAtUtc,
                    context.UtcNow,
                    attempt,
                    exception.FailureKind,
                    exception.EvidenceReference);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                ObserveOperation(
                    operation,
                    PostgresLogicalReplicationOperationDisposition.Canceled,
                    startedAtUtc,
                    context.UtcNow,
                    attempt,
                    transactionCount: 0,
                    changeCount: 0,
                    canonicalByteCount: 0,
                    "operation-canceled");
                throw;
            }
        }
    }

    PostgresLogicalReplicationException CreateFailure(
        PostgresLogicalReplicationOperationKind operation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        int attempt,
        PostgresLogicalReplicationFailureKind failureKind,
        string evidenceReference)
    {
        startedAtUtc = startedAtUtc.ToUniversalTime();
        completedAtUtc = completedAtUtc.ToUniversalTime();
        if (completedAtUtc < startedAtUtc)
            completedAtUtc = startedAtUtc;
        var observation = new PostgresLogicalReplicationOperationObservation(
            operation,
            PostgresLogicalReplicationOperationDisposition.Failed,
            Scope,
            startedAtUtc,
            completedAtUtc,
            attempt,
            transactionCount: 0,
            changeCount: 0,
            canonicalByteCount: 0,
            Evidence(evidenceReference),
            failureKind);
        Observe(observation);
        return new(
            "The PostgreSQL logical-replication operation failed closed; inspect its typed observation and health evidence.",
            failureKind,
            observation);
    }

    void ObserveOperation(
        PostgresLogicalReplicationOperationKind operation,
        PostgresLogicalReplicationOperationDisposition disposition,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        int attempt,
        long transactionCount,
        long changeCount,
        long canonicalByteCount,
        string evidenceReference,
        PostgresLogicalReplicationFailureKind? failureKind = null,
        TimeSpan? retryAfter = null)
    {
        startedAtUtc = startedAtUtc.ToUniversalTime();
        completedAtUtc = completedAtUtc.ToUniversalTime();
        if (completedAtUtc < startedAtUtc)
            completedAtUtc = startedAtUtc;
        Observe(new PostgresLogicalReplicationOperationObservation(
            operation,
            disposition,
            Scope,
            startedAtUtc,
            completedAtUtc,
            attempt,
            transactionCount,
            changeCount,
            canonicalByteCount,
            Evidence(evidenceReference),
            failureKind,
            retryAfter));
    }

    void Observe(PostgresLogicalReplicationOperationObservation observation)
    {
        try
        {
            observer?.Observe(observation);
        }
        catch
        {
            // Observation cannot change authoritative source semantics.
        }
    }

    static void ObserveSafely(
        IPostgresLogicalReplicationObserver? observer,
        PostgresLogicalReplicationOperationObservation observation)
    {
        try
        {
            observer?.Observe(observation);
        }
        catch
        {
            // Observation cannot change authoritative source semantics.
        }
    }

    void Observe(PostgresLogicalReplicationHealthObservation observation)
    {
        try
        {
            observer?.Observe(observation);
        }
        catch
        {
            // Observation cannot change authoritative source semantics.
        }
    }

    MaterializationSourceSettlementResult SettlementRejected(
        MaterializationSourceSettlementDisposition disposition,
        string code,
        string message,
        MaterializationSourceSettlementRequest request,
        string expected,
        string observed) => new(
        disposition,
        receipt: null,
        [MaterializationContract.CreateDiagnostic(
            code,
            DiagnosticSeverity.Error,
            message,
            $"/settlements/{Uri.EscapeDataString(request.Id.Value)}",
            "postgres-logical-replication-settlement",
            request.Id.Value,
            [Descriptor.CapabilityProfile.Id.Value, EvidencePrefix],
            expected,
            observed)]);

    static long WalDistance(
        PostgresLogicalReplicationWalPosition lower,
        PostgresLogicalReplicationWalPosition upper)
    {
        if (upper < lower)
        {
            throw ProtocolFailure(
                PostgresLogicalReplicationFailureKind.ProtocolViolation,
                "wal-distance-boundary-invalid");
        }
        var distance = upper.Value - lower.Value;
        return distance > long.MaxValue ? long.MaxValue : checked((long)distance);
    }

    string Evidence(string suffix) => string.Concat(
        EvidencePrefix,
        "/publication/", Uri.EscapeDataString(binding.PublicationName),
        "/slot/", Uri.EscapeDataString(binding.SlotName),
        "/generation/", Uri.EscapeDataString(binding.SlotGeneration),
        "/", Uri.EscapeDataString(suffix));

    static PostgresLogicalReplicationProtocolException ProtocolFailure(
        PostgresLogicalReplicationFailureKind kind,
        string evidenceReference) => new(
            kind,
            isTransient: false,
            string.Concat(EvidencePrefix, "/preflight/", evidenceReference));

    sealed record PositionPayload(
        string PlanAlgorithm,
        string PlanCanonicalization,
        string PlanValue,
        string Placement,
        string RuntimeDatabase,
        string StorageBinding,
        string SystemIdentifier,
        uint Timeline,
        string DatabaseName,
        string Publication,
        string Slot,
        string SlotGeneration,
        int Kind,
        string WalPosition);

    readonly record struct PositionCursor(
        PostgresLogicalReplicationPositionKind Kind,
        PostgresLogicalReplicationWalPosition WalPosition);

    sealed record ChangeProjectionColumn(
        RelationQuerySourceReadField Field,
        string ColumnName,
        PostgresRelationQueryScalarType ScalarType,
        PostgresRelationQueryMissingValueEncoding MissingValueEncoding,
        PostgresRelationQueryNullValueEncoding NullValueEncoding);

    sealed record ChangeReadResult(
        MaterializationChangePage Page,
        int TransactionCount,
        long CanonicalBytes,
        string EvidenceReference);

    sealed record HealthInspectionResult(
        PostgresLogicalReplicationHealthState State,
        DateTimeOffset ObservedAtUtc,
        long PendingWalBytes,
        long RetainedWalBytes,
        long? SafeWalBytes,
        TimeSpan? Inactivity);

    sealed record DeploymentAffinity(
        string SystemIdentifier,
        uint Timeline,
        string DatabaseName,
        string RuntimeDatabase,
        string StorageBinding,
        string Publication,
        string Slot,
        string SlotGeneration)
    {
        internal static DeploymentAffinity From(
            PostgresLogicalReplicationDeployment deployment,
            PostgresRelationQueryDatabaseId runtimeDatabase,
            string storageBinding,
            string slotGeneration) => new(
                deployment.SystemIdentifier,
                deployment.Timeline,
                deployment.DatabaseName,
                runtimeDatabase.Value,
                storageBinding,
                deployment.PublicationName,
                deployment.SlotName,
                slotGeneration);
    }

    sealed record SettlementRecord(
        MaterializationSourceSettlementRequest Request,
        MaterializationSourceSettlement Receipt);
}
