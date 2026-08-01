using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed partial class PostgresRelationQuerySourceReaderTests
{
    [Fact]
    public async Task LogicalReplication_KeyChangeRemainsAdjacentAndTransactionAligned()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        fixture.Protocol.Batch = new(
        [
            Transaction(
                transactionId: 17,
                endPosition: 200,
                new PostgresLogicalReplicationMutation(
                    Ordinal: 0,
                    Kind: PostgresLogicalReplicationMutationKind.Update,
                    ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Full,
                    OldRow: Row(Value("load_id", "item-old"), Value("load_name", "Old Name")),
                    NewRow: Row(Value("load_id", "item-new"), Toast("load_name"))))
        ],
            ScannedThrough: new(200),
            ReachedUpperBoundary: true);
        var context = OperationContext.Create();
        var retained = await fixture.Source.CaptureRetainedStartPositionAsync(
            context,
            fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(
            context,
            new(
                fixture.Source.Scope,
                retained,
                maximumDeliveries: 1,
                maximumBytes: fixture.Policy.MaximumTransactionBytes));

        Assert.Equal(MaterializationChangePageState.CaughtUp, page.State);
        Assert.Equal(2, page.Deliveries.Length);
        Assert.Equal(MaterializationChangeKind.Delete, page.Deliveries[0].Change.Kind);
        Assert.Equal("item-old", page.Deliveries[0].Change.SubjectIdentity);
        Assert.Equal(MaterializationChangeKind.Create, page.Deliveries[1].Change.Kind);
        Assert.Equal("item-new", page.Deliveries[1].Change.SubjectIdentity);
        Assert.Contains(
            page.Deliveries[0].Change.Before!.Fields,
            static field => field.Value?.String == "Old Name");
        Assert.Contains(
            page.Deliveries[1].Change.After!.Fields,
            static field => field.Value?.String == "Old Name");
        Assert.NotEqual(page.Deliveries[0].Id, page.Deliveries[1].Id);
        var invocation = Assert.Single(fixture.Protocol.Reads);
        Assert.Equal(1, invocation.PreferredMaximumMutations);
        Assert.Equal(fixture.Policy.MaximumTransactionChanges, invocation.MaximumTransactionMutations);
        Assert.Empty(fixture.Protocol.Settlements);
        var deliveryEvidence = Assert.Single(
            fixture.Source.Descriptor.CapabilityProfile.Evidence,
            static evidence => evidence.Capability == MaterializationCapabilityKind.SourceChangeDelivery);
        Assert.Contains(MaterializationGuaranteeKind.TransactionAlignedDelivery, deliveryEvidence.Guarantees);
        Assert.Contains(MaterializationGuaranteeKind.BeforeImage, deliveryEvidence.Guarantees);

        var replay = await fixture.Source.ReadChangesAsync(
            context,
            new(
                fixture.Source.Scope,
                retained,
                maximumDeliveries: 1,
                maximumBytes: fixture.Policy.MaximumTransactionBytes));
        Assert.Equal(
            page.Deliveries.Select(static delivery => delivery.Id),
            replay.Deliveries.Select(static delivery => delivery.Id));
    }

    [Fact]
    public async Task LogicalReplication_DefaultDeleteRetainsIdentityWithoutClaimingBeforeImage()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Default,
            fixedWidthOnly: true);
        fixture.Protocol.Batch = new(
        [
            Transaction(
                transactionId: 18,
                endPosition: 200,
                new PostgresLogicalReplicationMutation(
                    Ordinal: 0,
                    Kind: PostgresLogicalReplicationMutationKind.Delete,
                    ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Default,
                    OldRow: Row(Value("load_id", Guid.Parse("7d2cf66e-2fb7-4fa4-af87-8fd45debc764"))),
                    NewRow: null))
        ],
            ScannedThrough: new(200),
            ReachedUpperBoundary: true);
        var retained = await fixture.Source.CaptureRetainedStartPositionAsync(
            OperationContext.Create(),
            fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new(
                fixture.Source.Scope,
                retained,
                maximumDeliveries: 10,
                maximumBytes: fixture.Policy.MaximumTransactionBytes));

        var delivery = Assert.Single(page.Deliveries);
        Assert.Equal(MaterializationChangeKind.Delete, delivery.Change.Kind);
        Assert.Equal("7d2cf66e-2fb7-4fa4-af87-8fd45debc764", delivery.Change.SubjectIdentity);
        Assert.Null(delivery.Change.Before);
        Assert.Null(delivery.Change.After);
        var evidence = Assert.Single(
            fixture.Source.Descriptor.CapabilityProfile.Evidence,
            static item => item.Capability == MaterializationCapabilityKind.SourceChangeDelivery);
        Assert.DoesNotContain(MaterializationGuaranteeKind.BeforeImage, evidence.Guarantees);
    }

    [Fact]
    public async Task LogicalReplication_SettlementIsExactIdempotentAndNeverOccursDuringRead()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        fixture.Protocol.Batch = new(
        [
            Transaction(
                transactionId: 19,
                endPosition: 200,
                new PostgresLogicalReplicationMutation(
                    Ordinal: 0,
                    Kind: PostgresLogicalReplicationMutationKind.Insert,
                    ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Full,
                    OldRow: null,
                    NewRow: Row(Value("load_id", "item-a"), Value("load_name", "Alpha"))))
        ],
            ScannedThrough: new(200),
            ReachedUpperBoundary: true);
        var context = OperationContext.Create();
        var retained = await fixture.Source.CaptureRetainedStartPositionAsync(
            context,
            fixture.Source.Scope);
        var page = await fixture.Source.ReadChangesAsync(
            context,
            new(
                fixture.Source.Scope,
                retained,
                maximumDeliveries: 10,
                maximumBytes: fixture.Policy.MaximumTransactionBytes));
        Assert.Empty(fixture.Protocol.Settlements);
        var checkpoint = new MaterializationCheckpointId("checkpoint-1");
        var request = new MaterializationSourceSettlementRequest(
            PostgresLogicalReplicationMaterializationChangeSource.CreateSettlementId(
                checkpoint,
                page.ThroughPosition),
            checkpoint,
            page.ThroughPosition,
            context.UtcNow.ToUniversalTime());

        var acknowledged = await fixture.Source.SettleAsync(context, request);
        var replayed = await fixture.Source.SettleAsync(context, request);

        Assert.Equal(MaterializationSourceSettlementDisposition.Acknowledged, acknowledged.Disposition);
        Assert.Equal(MaterializationSourceSettlementDisposition.Replayed, replayed.Disposition);
        Assert.Equal(acknowledged.Receipt, replayed.Receipt);
        Assert.Equal(new PostgresLogicalReplicationWalPosition(200), Assert.Single(fixture.Protocol.Settlements));

        var alreadyConfirmedPosition = fixture.Source.CreatePosition(
            PostgresLogicalReplicationPositionKind.WalCut,
            new(150));
        var alreadyConfirmedCheckpoint = new MaterializationCheckpointId("checkpoint-2");
        var alreadyConfirmed = await fixture.Source.SettleAsync(
            context,
            new(
                PostgresLogicalReplicationMaterializationChangeSource.CreateSettlementId(
                    alreadyConfirmedCheckpoint,
                    alreadyConfirmedPosition),
                alreadyConfirmedCheckpoint,
                alreadyConfirmedPosition,
                context.UtcNow.ToUniversalTime()));
        Assert.Equal(MaterializationSourceSettlementDisposition.Replayed, alreadyConfirmed.Disposition);
        Assert.Equal(2, fixture.Protocol.Settlements.Count);
        Assert.Equal(new PostgresLogicalReplicationWalPosition(200), fixture.Protocol.Deployment.ConfirmedFlushPosition);
    }

    [Fact]
    public async Task LogicalReplication_SettlementReplayAcceptsConcurrentSlotAdvance()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var position = fixture.Source.CreatePosition(
            PostgresLogicalReplicationPositionKind.WalCut,
            new(150));
        fixture.Protocol.ConfirmedBeforeNextSettlement = new(175);
        var checkpoint = new MaterializationCheckpointId("concurrent-settlement-checkpoint");

        var result = await fixture.Source.SettleAsync(
            OperationContext.Create(),
            new(
                PostgresLogicalReplicationMaterializationChangeSource.CreateSettlementId(checkpoint, position),
                checkpoint,
                position,
                DateTimeOffset.UtcNow));

        Assert.Equal(MaterializationSourceSettlementDisposition.Replayed, result.Disposition);
        Assert.Equal(position, result.Receipt!.Position);
        Assert.Equal(new PostgresLogicalReplicationWalPosition(175), fixture.Protocol.Deployment.ConfirmedFlushPosition);
    }

    [Fact]
    public async Task LogicalReplication_SettlementReplayAcceptsPostSendConcurrentSlotAdvance()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var position = fixture.Source.CreatePosition(
            PostgresLogicalReplicationPositionKind.WalCut,
            new(150));
        fixture.Protocol.ConfirmedAfterNextSettlement = new(175);
        var checkpoint = new MaterializationCheckpointId("post-send-concurrent-settlement-checkpoint");

        var result = await fixture.Source.SettleAsync(
            OperationContext.Create(),
            new(
                PostgresLogicalReplicationMaterializationChangeSource.CreateSettlementId(checkpoint, position),
                checkpoint,
                position,
                DateTimeOffset.UtcNow));

        Assert.Equal(MaterializationSourceSettlementDisposition.Replayed, result.Disposition);
        Assert.Equal(position, result.Receipt!.Position);
        Assert.Equal(new PostgresLogicalReplicationWalPosition(175), fixture.Protocol.Deployment.ConfirmedFlushPosition);
    }

    [Fact]
    public async Task LogicalReplication_RejectsTamperingAndConfirmedAheadBeforeStreaming()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var position = fixture.Source.CreatePosition(
            PostgresLogicalReplicationPositionKind.WalCut,
            new(150));
        var tampered = new MaterializationSourcePosition(
            position.FormatVersion,
            position.Scope,
            string.Concat(position.Value.AsSpan(0, position.Value.Length - 1), "x"));
        var inspectCount = fixture.Protocol.InspectCount;

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new(
                fixture.Source.Scope,
                tampered,
                maximumDeliveries: 10,
                maximumBytes: fixture.Policy.MaximumTransactionBytes)).AsTask());
        Assert.Equal(inspectCount, fixture.Protocol.InspectCount);

        fixture.Protocol.Deployment = fixture.Protocol.Deployment with
        {
            ConfirmedFlushPosition = new(160)
        };
        var exception = await Assert.ThrowsAsync<PostgresLogicalReplicationException>(() =>
            fixture.Source.ReadChangesAsync(
                OperationContext.Create(),
                new(
                    fixture.Source.Scope,
                    position,
                    maximumDeliveries: 10,
                    maximumBytes: fixture.Policy.MaximumTransactionBytes)).AsTask());
        Assert.Equal(PostgresLogicalReplicationFailureKind.PositionUnavailable, exception.FailureKind);
        Assert.Empty(fixture.Protocol.Reads);
    }

    [Fact]
    public async Task LogicalReplication_RotatedSlotGenerationRejectsPriorPositionBeforeProviderIo()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var priorGenerationPosition = await fixture.Source.CaptureRetainedStartPositionAsync(
            OperationContext.Create(),
            fixture.Source.Scope);
        var rotatedBinding = new PostgresLogicalReplicationBinding(
            publicationName: fixture.Binding.PublicationName,
            slotName: fixture.Binding.SlotName,
            slotGeneration: "generation-2",
            expectedReplicaIdentity: fixture.Binding.ExpectedReplicaIdentity,
            beforeImageRequirement: fixture.Binding.BeforeImageRequirement);
        var rotatedSource = await PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(
            reader: fixture.Reader,
            placement: fixture.Placement,
            runtimeBinding: fixture.RuntimeBinding,
            binding: rotatedBinding,
            protocol: fixture.Protocol,
            positionAuthenticationKey: ContinuationAuthenticationKey,
            policy: fixture.Policy);
        var inspectedBeforeRead = fixture.Protocol.InspectCount;

        await Assert.ThrowsAsync<ArgumentException>(() => rotatedSource.ReadChangesAsync(
            OperationContext.Create(),
            new(
                rotatedSource.Scope,
                priorGenerationPosition,
                maximumDeliveries: 10,
                maximumBytes: fixture.Policy.MaximumTransactionBytes)).AsTask());

        Assert.Equal(inspectedBeforeRead, fixture.Protocol.InspectCount);
        Assert.Empty(fixture.Protocol.Reads);
        Assert.Empty(fixture.Protocol.Settlements);
    }

    [Fact]
    public async Task LogicalReplication_PreflightRejectsPublicationAndSlotMismatchBeforeChangeIo()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var validDeployment = fixture.Protocol.Deployment;
        var inspectCount = fixture.Protocol.InspectCount;

        fixture.Protocol.Deployment = validDeployment with { IncludesTable = false };
        var publication = await Assert.ThrowsAsync<PostgresLogicalReplicationProtocolException>(() =>
            CreateSourceAsync().AsTask());

        fixture.Protocol.Deployment = validDeployment with { OutputPlugin = "test_decoding" };
        var slot = await Assert.ThrowsAsync<PostgresLogicalReplicationProtocolException>(() =>
            CreateSourceAsync().AsTask());

        Assert.Equal(PostgresLogicalReplicationFailureKind.PublicationMismatch, publication.FailureKind);
        Assert.Equal(PostgresLogicalReplicationFailureKind.SlotUnavailable, slot.FailureKind);
        Assert.Equal(inspectCount + 2, fixture.Protocol.InspectCount);
        Assert.Empty(fixture.Protocol.Reads);
        Assert.Empty(fixture.Protocol.Settlements);

        ValueTask<PostgresLogicalReplicationMaterializationChangeSource> CreateSourceAsync() =>
            PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(
                reader: fixture.Reader,
                placement: fixture.Placement,
                runtimeBinding: fixture.RuntimeBinding,
                binding: fixture.Binding,
                protocol: fixture.Protocol,
                positionAuthenticationKey: ContinuationAuthenticationKey,
                policy: fixture.Policy);
    }

    [Fact]
    public async Task LogicalReplication_HealthPollingReportsRetentionDanger()
    {
        var policy = new PostgresLogicalReplicationSourcePolicy(
            maximumTransactionChanges: 10,
            maximumTransactionBytes: 1_000_000,
            maximumTransactionsPerRead: 4,
            maximumReconnectAttempts: 0,
            retentionDangerBytes: 100);
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full,
            policy);
        fixture.Protocol.Deployment = fixture.Protocol.Deployment with
        {
            RestartPosition = new(50),
            CurrentWalPosition = new(200),
            SafeWalBytes = 50
        };

        var health = await fixture.Source.InspectHealthAsync(
            OperationContext.Create(),
            fixture.Source.Scope);

        Assert.Equal(PostgresLogicalReplicationHealthState.RetentionDanger, health.State);
        Assert.Equal(150, health.RetainedWalBytes);
        Assert.Equal(100, health.EstimatedPendingWalBytes);
        Assert.Equal(50, health.RemainingSafeWalBytes);
    }

    [Theory]
    [InlineData(PostgresRelationQueryScalarType.Numeric, true)]
    [InlineData(PostgresRelationQueryScalarType.Text, true)]
    [InlineData(PostgresRelationQueryScalarType.Bytea, true)]
    [InlineData(PostgresRelationQueryScalarType.Boolean, false)]
    [InlineData(PostgresRelationQueryScalarType.Int32, false)]
    [InlineData(PostgresRelationQueryScalarType.Int64, false)]
    [InlineData(PostgresRelationQueryScalarType.Uuid, false)]
    public void LogicalReplication_ScalarCatalogIdentifiesUnchangedToastRisk(
        PostgresRelationQueryScalarType scalarType,
        bool expected) => Assert.Equal(
        expected,
        PostgresRelationQueryScalarCatalog.MayUseUnchangedToast(scalarType));

    [Fact]
    public async Task LogicalReplication_NonFullPreflightRejectsToastableProjection()
    {
        var exception = await Assert.ThrowsAsync<PostgresLogicalReplicationProtocolException>(async () =>
            await CreateLogicalFixtureAsync(PostgresLogicalReplicationReplicaIdentityKind.Default));

        Assert.Equal(PostgresLogicalReplicationFailureKind.ReplicaIdentityMismatch, exception.FailureKind);
        Assert.Contains("unchanged-toast", exception.EvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogicalReplication_TransientReadRetriesFromExactAfterPosition()
    {
        var observer = new CollectingObserver();
        var policy = new PostgresLogicalReplicationSourcePolicy(
            maximumTransactionChanges: 10,
            maximumTransactionBytes: 1_000_000,
            maximumTransactionsPerRead: 4,
            maximumReconnectAttempts: 1,
            reconnectDelay: TimeSpan.Zero);
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full,
            policy,
            observer: observer);
        fixture.Protocol.ReadFailures.Enqueue(new(
            PostgresLogicalReplicationFailureKind.Transient,
            isTransient: true,
            "tests/transient-read"));
        var retained = await fixture.Source.CaptureRetainedStartPositionAsync(
            OperationContext.Create(),
            fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new(fixture.Source.Scope, retained, maximumDeliveries: 10, maximumBytes: 1_000_000));

        Assert.Equal(MaterializationChangePageState.CaughtUp, page.State);
        Assert.Equal(2, fixture.Protocol.Reads.Count);
        Assert.Equal(fixture.Protocol.Reads[0].AfterPosition, fixture.Protocol.Reads[1].AfterPosition);
        Assert.Contains(observer.Operations, static observation =>
            observation.Disposition == PostgresLogicalReplicationOperationDisposition.Retrying
            && observation.FailureKind == PostgresLogicalReplicationFailureKind.Transient);
    }

    [Fact]
    public async Task LogicalReplication_PositionAffinitySurvivesPolicyRetuning()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var position = await fixture.Source.CaptureRetainedStartPositionAsync(
            OperationContext.Create(),
            fixture.Source.Scope);
        var retuned = await PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(
            fixture.Reader,
            fixture.Placement,
            fixture.RuntimeBinding,
            fixture.Binding,
            fixture.Protocol,
            ContinuationAuthenticationKey,
            new PostgresLogicalReplicationSourcePolicy(
                maximumTransactionChanges: 20,
                maximumTransactionBytes: 2_000_000,
                maximumTransactionsPerRead: 8,
                maximumReconnectAttempts: 0));

        var page = await retuned.ReadChangesAsync(
            OperationContext.Create(),
            new(retuned.Scope, position, maximumDeliveries: 10, maximumBytes: 1_000_000));

        Assert.Equal(MaterializationChangePageState.CaughtUp, page.State);
    }

    [Fact]
    public async Task LogicalReplication_CancellationCannotAdvanceReadOrSettlement()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var position = fixture.Source.CreatePosition(
            PostgresLogicalReplicationPositionKind.WalCut,
            new(150));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = OperationContext.Create(cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Source.ReadChangesAsync(
            context,
            new(fixture.Source.Scope, position, maximumDeliveries: 10, maximumBytes: 1_000_000)).AsTask());
        var checkpoint = new MaterializationCheckpointId("canceled-checkpoint");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Source.SettleAsync(
            context,
            new(
                PostgresLogicalReplicationMaterializationChangeSource.CreateSettlementId(checkpoint, position),
                checkpoint,
                position,
                DateTimeOffset.UtcNow)).AsTask());

        Assert.Empty(fixture.Protocol.Reads);
        Assert.Empty(fixture.Protocol.Settlements);
        Assert.Equal(new PostgresLogicalReplicationWalPosition(100), fixture.Protocol.Deployment.ConfirmedFlushPosition);
    }

    [Fact]
    public async Task LogicalReplication_AffinityDriftAndRetentionExpiryFailClosed()
    {
        await using var drifted = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        drifted.Protocol.Deployment = drifted.Protocol.Deployment with
        {
            SystemIdentifier = "replacement-postgres-system"
        };
        var drift = await Assert.ThrowsAsync<PostgresLogicalReplicationException>(() =>
            drifted.Source.CaptureCurrentPositionAsync(
                OperationContext.Create(),
                drifted.Source.Scope).AsTask());
        Assert.Equal(PostgresLogicalReplicationFailureKind.SlotGenerationMismatch, drift.FailureKind);

        await using var expired = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var retained = await expired.Source.CaptureRetainedStartPositionAsync(
            OperationContext.Create(),
            expired.Source.Scope);
        expired.Protocol.Deployment = expired.Protocol.Deployment with
        {
            RestartPosition = new(120),
            ConfirmedFlushPosition = new(120)
        };
        var retention = await Assert.ThrowsAsync<PostgresLogicalReplicationException>(() =>
            expired.Source.ReadChangesAsync(
                OperationContext.Create(),
                new(expired.Source.Scope, retained, maximumDeliveries: 10, maximumBytes: 1_000_000)).AsTask());
        Assert.Equal(PostgresLogicalReplicationFailureKind.PositionUnavailable, retention.FailureKind);
        Assert.Empty(expired.Protocol.Reads);
    }

    [Fact]
    public async Task LogicalReplication_PaginatesTransactionsAndReportsFilteredWalProgress()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var firstTransaction = Transaction(
            transactionId: 31,
            endPosition: 120,
            new PostgresLogicalReplicationMutation(
                Ordinal: 0,
                Kind: PostgresLogicalReplicationMutationKind.Insert,
                ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Full,
                OldRow: null,
                NewRow: Row(Value("load_id", "item-a"), Value("load_name", "Alpha"))));
        var secondTransaction = Transaction(
            transactionId: 32,
            endPosition: 160,
            new PostgresLogicalReplicationMutation(
                Ordinal: 0,
                Kind: PostgresLogicalReplicationMutationKind.Insert,
                ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Full,
                OldRow: null,
                NewRow: Row(Value("load_id", "item-b"), Value("load_name", "Beta"))));
        fixture.Protocol.Batch = new([firstTransaction, secondTransaction], new(200), ReachedUpperBoundary: true);
        var position = await fixture.Source.CaptureRetainedStartPositionAsync(
            OperationContext.Create(),
            fixture.Source.Scope);

        var first = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new(fixture.Source.Scope, position, maximumDeliveries: 1, maximumBytes: 1_000_000));
        fixture.Protocol.Batch = new([secondTransaction], new(180), ReachedUpperBoundary: false);
        var second = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new(fixture.Source.Scope, first.ThroughPosition, maximumDeliveries: 1, maximumBytes: 1_000_000));
        fixture.Protocol.Batch = new([], new(190), ReachedUpperBoundary: false);
        var filtered = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new(fixture.Source.Scope, second.ThroughPosition, maximumDeliveries: 1, maximumBytes: 1_000_000));
        fixture.Protocol.Batch = new([], new(200), ReachedUpperBoundary: true);
        var caughtUp = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new(fixture.Source.Scope, filtered.ThroughPosition, maximumDeliveries: 1, maximumBytes: 1_000_000));

        Assert.Equal("item-a", Assert.Single(first.Deliveries).Change.SubjectIdentity);
        Assert.Equal("item-b", Assert.Single(second.Deliveries).Change.SubjectIdentity);
        Assert.Equal(MaterializationChangePageState.MoreAvailable, first.State);
        Assert.Equal(MaterializationChangePageState.MoreAvailable, second.State);
        Assert.Empty(filtered.Deliveries);
        Assert.Equal(MaterializationChangePageState.Progressed, filtered.State);
        Assert.Equal(MaterializationChangePageState.CaughtUp, caughtUp.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogicalReplication_EnforcesProviderAndCanonicalTransactionCaps(bool canonicalProjection)
    {
        var policy = new PostgresLogicalReplicationSourcePolicy(
            maximumTransactionChanges: 1,
            maximumTransactionBytes: 1_000_000,
            maximumTransactionsPerRead: 4,
            maximumReconnectAttempts: 0);
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full,
            policy);
        var first = new PostgresLogicalReplicationMutation(
            Ordinal: 0,
            Kind: canonicalProjection
                ? PostgresLogicalReplicationMutationKind.Update
                : PostgresLogicalReplicationMutationKind.Insert,
            ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Full,
            OldRow: canonicalProjection
                ? Row(Value("load_id", "item-old"), Value("load_name", "Old"))
                : null,
            NewRow: Row(
                Value("load_id", canonicalProjection ? "item-new" : "item-a"),
                Value("load_name", "New")));
        var mutations = canonicalProjection
            ? new[] { first }
            : new[]
            {
                first,
                new(
                    Ordinal: 1,
                    Kind: PostgresLogicalReplicationMutationKind.Insert,
                    ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Full,
                    OldRow: null,
                    NewRow: Row(Value("load_id", "item-b"), Value("load_name", "Beta")))
            };
        fixture.Protocol.Batch = new(
            [Transaction(transactionId: 33, endPosition: 200, mutations)],
            new(200),
            ReachedUpperBoundary: true);
        var retained = await fixture.Source.CaptureRetainedStartPositionAsync(
            OperationContext.Create(),
            fixture.Source.Scope);

        var exception = await Assert.ThrowsAsync<PostgresLogicalReplicationException>(() =>
            fixture.Source.ReadChangesAsync(
                OperationContext.Create(),
                new(fixture.Source.Scope, retained, maximumDeliveries: 1, maximumBytes: 1_000_000)).AsTask());

        Assert.Equal(PostgresLogicalReplicationFailureKind.TransactionLimitExceeded, exception.FailureKind);
        Assert.Empty(fixture.Protocol.Settlements);
    }

    [Fact]
    public async Task LogicalReplication_ProjectsInsertUpdateDeleteAndKeyChangeInOrder()
    {
        await using var fixture = await CreateLogicalFixtureAsync(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        fixture.Protocol.Batch = new(
        [
            Transaction(
                transactionId: 34,
                endPosition: 200,
                new(0, PostgresLogicalReplicationMutationKind.Insert,
                    PostgresLogicalReplicationReplicaIdentityKind.Full, null,
                    Row(Value("load_id", "a"), Value("load_name", "A"))),
                new(1, PostgresLogicalReplicationMutationKind.Update,
                    PostgresLogicalReplicationReplicaIdentityKind.Full,
                    Row(Value("load_id", "b"), Value("load_name", "B")),
                    Row(Value("load_id", "b"), Value("load_name", "B2"))),
                new(2, PostgresLogicalReplicationMutationKind.Delete,
                    PostgresLogicalReplicationReplicaIdentityKind.Full,
                    Row(Value("load_id", "c"), Value("load_name", "C")), null),
                new(3, PostgresLogicalReplicationMutationKind.Update,
                    PostgresLogicalReplicationReplicaIdentityKind.Full,
                    Row(Value("load_id", "d"), Value("load_name", "D")),
                    Row(Value("load_id", "e"), Toast("load_name"))))
        ],
            new(200),
            ReachedUpperBoundary: true);
        var retained = await fixture.Source.CaptureRetainedStartPositionAsync(
            OperationContext.Create(),
            fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new(fixture.Source.Scope, retained, maximumDeliveries: 10, maximumBytes: 1_000_000));

        Assert.Equal(
            [
                MaterializationChangeKind.Create,
                MaterializationChangeKind.Update,
                MaterializationChangeKind.Delete,
                MaterializationChangeKind.Delete,
                MaterializationChangeKind.Create
            ],
            page.Deliveries.Select(static delivery => delivery.Change.Kind));
        Assert.Equal(["a", "b", "c", "d", "e"],
            page.Deliveries.Select(static delivery => delivery.Change.SubjectIdentity));
        Assert.Contains(
            page.Deliveries[^1].Change.After!.Fields,
            static field => field.Value?.String == "D");
    }

    static async ValueTask<LogicalFixture> CreateLogicalFixtureAsync(
        PostgresLogicalReplicationReplicaIdentityKind replicaIdentityKind,
        PostgresLogicalReplicationSourcePolicy? policy = null,
        bool fixedWidthOnly = false,
        IPostgresLogicalReplicationObserver? observer = null)
    {
        static ValueTask<PostgresNpgsqlCommandResult> Execute(
            PostgresNpgsqlCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PostgresNpgsqlCommandResult([]));
        }
        var canonical = fixedWidthOnly
            ? CreateFixedWidthCanonicalExecutionFixture(Execute)
            : CreateCanonicalExecutionFixture(Execute);
        var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=cohesive_tests;Username=postgres;Password=not-used;Timeout=1");
        try
        {
            var runtime = new PostgresNpgsqlRuntimeBinding(
                canonical.Storage.Database,
                dataSource,
                "tests/postgres/logical-replication/v1");
            var reader = new PostgresRelationQuerySourceReader(
                canonical.Plan,
                canonical.PhysicalPlan,
                canonical.Source,
                canonical.Storage,
                dataSource,
                runtime,
                Policy);
            var placement = Assert.Single(canonical.PhysicalPlan.Placement.Bindings);
            var table = canonical.Storage.ResolveTable(placement.Id);
            var replicaIdentity = new PostgresLogicalReplicationReplicaIdentityBinding(replicaIdentityKind);
            var binding = new PostgresLogicalReplicationBinding(
                publicationName: "cohesive_items",
                slotName: "cohesive_items_slot",
                slotGeneration: "generation-1",
                expectedReplicaIdentity: replicaIdentity,
                beforeImageRequirement: replicaIdentityKind == PostgresLogicalReplicationReplicaIdentityKind.Full
                    ? PostgresLogicalReplicationBeforeImageRequirement.Required
                    : PostgresLogicalReplicationBeforeImageRequirement.NotRequired);
            var protocol = new FakeLogicalReplicationProtocol(
                Deployment(table, binding));
            var effectivePolicy = policy ?? new PostgresLogicalReplicationSourcePolicy(
                maximumTransactionChanges: 10,
                maximumTransactionBytes: 1_000_000,
                maximumTransactionsPerRead: 4,
                maximumReconnectAttempts: 0);
            var source = await PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(
                reader,
                placement,
                runtime,
                binding,
                protocol,
                ContinuationAuthenticationKey,
                effectivePolicy,
                observer);
            return new(
                dataSource,
                reader,
                placement,
                runtime,
                binding,
                source,
                protocol,
                effectivePolicy);
        }
        catch
        {
            await dataSource.DisposeAsync();
            throw;
        }
    }

    static PostgresLogicalReplicationDeployment Deployment(
        PostgresRelationQueryTableBinding table,
        PostgresLogicalReplicationBinding binding)
    {
        var expected = table.Fields
            .Select(static field => (field.ColumnName, field.ScalarType))
            .Append((table.Identity!.ColumnName, table.Identity.ScalarType))
            .Concat(table.RelationshipReferences.Select(static reference =>
                (reference.ColumnName, reference.ScalarType)))
            .GroupBy(static column => column.ColumnName, StringComparer.Ordinal)
            .Select(static group =>
            {
                var scalarType = group.Select(static column => column.ScalarType).Distinct().Single();
                return (ColumnName: group.Key, ScalarType: scalarType);
            });
        var columns = expected
            .Select(column => new PostgresLogicalReplicationColumn(
                Name: column.ColumnName,
                DataTypeId: PostgresTypeId(column.ScalarType),
                TypeModifier: -1,
                IsReplicaIdentity: binding.ExpectedReplicaIdentity.ProvidesCompleteBeforeImage
                    || string.Equals(column.ColumnName, table.Identity!.ColumnName, StringComparison.Ordinal)))
            .ToImmutableArray();
        return new(
            SystemIdentifier: "postgres-system-1",
            Timeline: 1,
            DatabaseName: "cohesive_tests",
            PublicationName: binding.PublicationName,
            PublishesInserts: true,
            PublishesUpdates: true,
            PublishesDeletes: true,
            PublishesTruncates: false,
            PublishesViaPartitionRoot: false,
            IncludesTable: true,
            HasRowFilter: false,
            IncludesAllTableColumns: true,
            SchemaName: table.SchemaName,
            TableName: table.TableName,
            ReplicaIdentity: binding.ExpectedReplicaIdentity,
            Columns: columns,
            SlotName: binding.SlotName,
            OutputPlugin: "pgoutput",
            IsLogicalSlot: true,
            IsTemporarySlot: false,
            IsTwoPhaseSlot: false,
            IsActive: false,
            RestartPosition: new(50),
            ConfirmedFlushPosition: new(100),
            CurrentWalPosition: new(200),
            WalState: PostgresLogicalReplicationWalState.Reserved,
            SafeWalBytes: 1_000_000,
            InactiveSinceUtc: null,
            InvalidationReason: null);
    }

    static CanonicalExecutionFixture CreateFixedWidthCanonicalExecutionFixture(
        PostgresNpgsqlCommandExecutor executor)
    {
        var author = RelationQuery.Expression();
        var itemShape = author.Clr.Shape<FixedWidthItem>();
        var items = author.Source(itemShape);
        var projected = author.Project(
            items.Node,
            (FixedWidthItem item) => new FixedWidthRow { Id = item.Id, Count = item.Count },
            items.Binding);
        var rows = author.Rows(projected.Node, projected.Binding, id: "rows");
        var query = author.BuildQuery(
            new("postgres-fixed-width-source-reader"),
            new("PostgresFixedWidthSourceReader"),
            rows);
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments));
        var plan = compilation.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));

        var placementBuilder = RelationQueryPlacement.For(plan);
        var sourceHandle = placementBuilder.Source(
            "tests/postgres/fixed-items",
            PostgresRelationQuerySourceTargetProfile.Default,
            limits: new(
                maximumBatchSize: 10,
                maximumBufferedRows: 10,
                maximumFanOut: 10,
                maximumConcurrency: 2));
        var placedSource = placementBuilder.PlaceSource(sourceHandle, itemShape);
        placedSource.Identity(static item => item.Id);
        var placed = placedSource.FieldsBySemanticPath();
        var authoredPlacement = placementBuilder.Build().RequireValue();
        var placedInput = authoredPlacement.GetInput(placed);
        var identityOptions = new PostgresRelationQueryColumnOptions(
            PostgresRelationQueryScalarType.Uuid,
            ordering: PostgresRelationQueryOrderingCapability.Exact
                | PostgresRelationQueryOrderingCapability.StableUnique);
        var storage = PostgresRelationQueryBinding.For(authoredPlacement)
            .Database(new("tests-database"))
            .Table(
                placedInput,
                "fixed_items",
                table => table
                    .Schema("public")
                    .ColumnsExplicitly()
                    .Column(item => item.Id, "load_id", identityOptions)
                    .Column(item => item.Count, "load_count")
                    .Identity(item => item.Id, "load_id", identityOptions))
            .Build()
            .RequireValue();
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var physical = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            authoredPlacement.Placement,
            new(
                new("tests/postgres/fixed-source-execution-policy/v1"),
                authoredPlacement.Placement.ConventionSetVersion,
                maximumBatchSize: 10,
                maximumBufferedRows: 10,
                maximumLocalRows: 10,
                maximumFanOut: 10,
                maximumReferenceKeysPerObservation: 10,
                maximumConcurrency: 2));
        var physicalPlan = physical.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, physical.Diagnostics));
        var reader = new PostgresRelationQuerySourceReader(
            plan,
            physicalPlan,
            sourceHandle.Id,
            storage,
            executor,
            Policy);
        return new(plan, realization, physicalPlan, sourceHandle.Id, storage, reader);
    }

    static PostgresLogicalReplicationTransaction Transaction(
        uint transactionId,
        ulong endPosition,
        params PostgresLogicalReplicationMutation[] mutations) => new(
            transactionId,
            FinalPosition: new(endPosition - 1),
            CommitPosition: new(endPosition - 1),
            EndPosition: new(endPosition),
            CommittedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            Mutations: [.. mutations],
            RetainedBytes: mutations.Sum(static mutation =>
                (mutation.OldRow?.Cells.Sum(static cell => cell.EncodedBytes) ?? 0)
                + (mutation.NewRow?.Cells.Sum(static cell => cell.EncodedBytes) ?? 0)));

    static PostgresLogicalReplicationRow Row(params PostgresLogicalReplicationCell[] cells) =>
        new([.. cells]);

    static PostgresLogicalReplicationCell Value(string columnName, object value) => new(
        columnName,
        PostgresLogicalReplicationCellKind.Value,
        value,
        Encoding.UTF8.GetByteCount(value.ToString()!));

    static PostgresLogicalReplicationCell Toast(string columnName) => new(
        columnName,
        PostgresLogicalReplicationCellKind.UnchangedToast,
        Value: null,
        EncodedBytes: 1);

    static uint PostgresTypeId(PostgresRelationQueryScalarType scalarType) => scalarType switch
    {
        PostgresRelationQueryScalarType.Boolean => 16,
        PostgresRelationQueryScalarType.Bytea => 17,
        PostgresRelationQueryScalarType.Int64 => 20,
        PostgresRelationQueryScalarType.Int32 => 23,
        PostgresRelationQueryScalarType.Text => 25,
        PostgresRelationQueryScalarType.Date => 1082,
        PostgresRelationQueryScalarType.Timestamp => 1114,
        PostgresRelationQueryScalarType.TimestampWithTimeZone => 1184,
        PostgresRelationQueryScalarType.Numeric => 1700,
        PostgresRelationQueryScalarType.Uuid => 2950,
        _ => throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, null)
    };

    sealed class FixedWidthItem
    {
        [JsonPropertyName("id")]
        public required Guid Id { get; init; }

        [JsonPropertyName("count")]
        public required long Count { get; init; }
    }

    sealed class FixedWidthRow
    {
        [JsonPropertyName("id")]
        public required Guid Id { get; init; }

        [JsonPropertyName("count")]
        public required long Count { get; init; }
    }

    sealed record LogicalFixture(
        NpgsqlDataSource DataSource,
        PostgresRelationQuerySourceReader Reader,
        RelationQuerySourcePlacementBinding Placement,
        PostgresNpgsqlRuntimeBinding RuntimeBinding,
        PostgresLogicalReplicationBinding Binding,
        PostgresLogicalReplicationMaterializationChangeSource Source,
        FakeLogicalReplicationProtocol Protocol,
        PostgresLogicalReplicationSourcePolicy Policy) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }

    sealed class FakeLogicalReplicationProtocol(
        PostgresLogicalReplicationDeployment deployment) : IPostgresLogicalReplicationProtocol
    {
        internal PostgresLogicalReplicationDeployment Deployment { get; set; } = deployment;

        internal PostgresLogicalReplicationReadBatch Batch { get; set; } = new(
            [],
            deployment.CurrentWalPosition,
            ReachedUpperBoundary: true);

        internal int InspectCount { get; private set; }

        internal List<ReadInvocation> Reads { get; } = [];

        internal List<PostgresLogicalReplicationWalPosition> Settlements { get; } = [];

        internal Queue<PostgresLogicalReplicationProtocolException> ReadFailures { get; } = [];

        internal PostgresLogicalReplicationWalPosition? ConfirmedBeforeNextSettlement { get; set; }

        internal PostgresLogicalReplicationWalPosition? ConfirmedAfterNextSettlement { get; set; }

        public ValueTask<PostgresLogicalReplicationDeployment> InspectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCount++;
            return ValueTask.FromResult(Deployment);
        }

        public ValueTask<PostgresLogicalReplicationReadBatch> ReadAsync(
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
            Reads.Add(new(
                afterPosition,
                upperBoundary,
                maximumTransactions,
                preferredMaximumMutations,
                preferredMaximumBytes,
                maximumTransactionMutations,
                maximumTransactionBytes,
                inactivityTimeout));
            if (ReadFailures.TryDequeue(out var failure))
                throw failure;
            return ValueTask.FromResult(Batch);
        }

        public ValueTask<PostgresLogicalReplicationFeedback> SettleAsync(
            PostgresLogicalReplicationWalPosition position,
            TimeSpan confirmationTimeout,
            TimeSpan confirmationPollInterval,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Settlements.Add(position);
            if (ConfirmedBeforeNextSettlement is { } concurrentlyConfirmed)
            {
                Deployment = Deployment with { ConfirmedFlushPosition = concurrentlyConfirmed };
                ConfirmedBeforeNextSettlement = null;
            }
            var prior = Deployment.ConfirmedFlushPosition;
            var alreadyConfirmed = prior >= position;
            if (!alreadyConfirmed)
                Deployment = Deployment with { ConfirmedFlushPosition = position };
            if (ConfirmedAfterNextSettlement is { } postSendConfirmed)
            {
                Deployment = Deployment with { ConfirmedFlushPosition = postSendConfirmed };
                ConfirmedAfterNextSettlement = null;
            }
            var confirmed = Deployment.ConfirmedFlushPosition;
            var exactConfirmation = confirmed == position;
            return ValueTask.FromResult(new PostgresLogicalReplicationFeedback(
                alreadyConfirmed || !exactConfirmation
                    ? PostgresLogicalReplicationFeedbackDisposition.AlreadyConfirmed
                    : PostgresLogicalReplicationFeedbackDisposition.Confirmed,
                exactConfirmation ? prior : confirmed,
                confirmed));
        }

        public ValueTask<IPostgresLogicalReplicationSnapshotExport> CreateSnapshotExportAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Snapshot handoff belongs to its independent test fixture.");
    }

    sealed class CollectingObserver : IPostgresLogicalReplicationObserver
    {
        internal List<PostgresLogicalReplicationOperationObservation> Operations { get; } = [];

        internal List<PostgresLogicalReplicationHealthObservation> Health { get; } = [];

        public void Observe(PostgresLogicalReplicationOperationObservation observation) =>
            Operations.Add(observation);

        public void Observe(PostgresLogicalReplicationHealthObservation observation) =>
            Health.Add(observation);
    }

    sealed record ReadInvocation(
        PostgresLogicalReplicationWalPosition AfterPosition,
        PostgresLogicalReplicationWalPosition UpperBoundary,
        int MaximumTransactions,
        int PreferredMaximumMutations,
        long PreferredMaximumBytes,
        int MaximumTransactionMutations,
        long MaximumTransactionBytes,
        TimeSpan InactivityTimeout);
}
