using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed partial class PostgresRelationQuerySourceReaderTests
{
    [Fact]
    public async Task LogicalReplicationBaselineHandoff_BaselinePagesShareImportedSnapshotLease()
    {
        await using var fixture = CreateBaselineHandoffFixture();
        await using var handoff = await CreateBaselineHandoffAsync(fixture);
        var read = CanonicalSourceRead(fixture.Canonical);
        var context = OperationContext.Create();

        var first = await handoff.ReadPageAsync(
            context,
            new(
                read,
                handoff.Scope,
                continuation: null,
                maximumItems: 1,
                maximumBytes: 1_000_000));
        var second = await handoff.ReadPageAsync(
            context,
            new(
                read,
                handoff.Scope,
                first.Continuation,
                maximumItems: 1,
                maximumBytes: 1_000_000));

        Assert.Equal(MaterializationSourcePageState.MoreAvailable, first.State);
        Assert.Equal(MaterializationSourcePageState.Exhausted, second.State);
        Assert.Equal("item-a", Assert.Single(first.Read.Observations).Identity);
        Assert.Equal("item-b", Assert.Single(second.Read.Observations).Identity);
        Assert.Equal(2, fixture.SnapshotImport.InvocationLeases.Count);
        Assert.All(
            fixture.SnapshotImport.InvocationLeases,
            lease => Assert.Same(fixture.SnapshotImport.Lease, lease));
        Assert.Equal(1, fixture.SnapshotExport.ImportCount);
        Assert.Equal(0, fixture.SnapshotImport.DisposeCount);
        var readEvidence = handoff.Descriptor.CapabilityProfile.Evidence
            .Where(static evidence => evidence.Capability is
                MaterializationCapabilityKind.SourceBoundedEnumeration
                or MaterializationCapabilityKind.SourceContinuation)
            .ToArray();
        Assert.Equal(2, readEvidence.Length);
        Assert.All(readEvidence, static evidence =>
        {
            Assert.Contains(MaterializationGuaranteeKind.CoordinatedSnapshot, evidence.Guarantees);
            Assert.DoesNotContain(MaterializationGuaranteeKind.Reconciliation, evidence.Guarantees);
            Assert.Contains(
                evidence.OperatingLimits,
                static limit => limit.Kind == MaterializationLimitKind.Parallelism
                    && limit.Maximum == 1);
        });
    }

    [Fact]
    public async Task LogicalReplicationBaselineHandoff_ChangeStartIsExportPointAndReadsLaterCommit()
    {
        await using var fixture = CreateBaselineHandoffFixture();
        fixture.Protocol.Deployment = fixture.Protocol.Deployment with
        {
            CurrentWalPosition = new(120)
        };
        fixture.Protocol.Batch = new(
        [
            Transaction(
                transactionId: 41,
                endPosition: 120,
                new PostgresLogicalReplicationMutation(
                    Ordinal: 0,
                    Kind: PostgresLogicalReplicationMutationKind.Insert,
                    ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Full,
                    OldRow: null,
                    NewRow: Row(Value("load_id", "item-c"), Value("load_name", "Gamma"))))
        ],
            ScannedThrough: new(120),
            ReachedUpperBoundary: true);
        await using var handoff = await CreateBaselineHandoffAsync(fixture);

        Assert.Equal(
            handoff.ChangeSource.CreatePosition(
                PostgresLogicalReplicationPositionKind.WalCut,
                fixture.SnapshotExport.ConsistentPosition),
            handoff.ChangeStartPosition);

        var page = await handoff.ChangeSource.ReadChangesAsync(
            OperationContext.Create(),
            new(
                handoff.Scope,
                handoff.ChangeStartPosition,
                maximumDeliveries: 10,
                maximumBytes: fixture.Policy.MaximumTransactionBytes));

        var delivery = Assert.Single(page.Deliveries);
        Assert.Equal("item-c", delivery.Change.SubjectIdentity);
        Assert.Equal(MaterializationChangeKind.Create, delivery.Change.Kind);
        Assert.Equal(
            fixture.SnapshotExport.ConsistentPosition,
            Assert.Single(fixture.Protocol.Reads).AfterPosition);
    }

    [Fact]
    public async Task LogicalReplicationBaselineHandoff_DisposalEndsBaselineButLeavesChangeSourceUsable()
    {
        await using var fixture = CreateBaselineHandoffFixture();
        var handoff = await CreateBaselineHandoffAsync(fixture);
        var read = CanonicalSourceRead(fixture.Canonical);

        await handoff.DisposeAsync();

        Assert.Equal(1, fixture.SnapshotImport.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handoff.ReadPageAsync(
            OperationContext.Create(),
            new(
                read,
                handoff.Scope,
                continuation: null,
                maximumItems: 1,
                maximumBytes: 1_000_000)).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handoff.Descriptor.RelationReader
            .ReadAsync(read)
            .AsTask());

        var current = await handoff.ChangeSource.CaptureCurrentPositionAsync(
            OperationContext.Create(),
            handoff.Scope);

        Assert.Equal(
            handoff.ChangeSource.CreatePosition(
                PostgresLogicalReplicationPositionKind.WalCut,
                fixture.Protocol.Deployment.CurrentWalPosition),
            current);
        Assert.Equal(2, fixture.Protocol.InspectCount);
    }

    [Fact]
    public async Task LogicalReplicationBaselineHandoff_FailedPostSlotPreflightDisposesImportWithoutSettlement()
    {
        await using var fixture = CreateBaselineHandoffFixture(failPostSlotPreflight: true);

        var exception = await Assert.ThrowsAsync<PostgresLogicalReplicationException>(() =>
            CreateBaselineHandoffAsync(fixture).AsTask());

        Assert.Equal(PostgresLogicalReplicationFailureKind.Terminal, exception.FailureKind);
        Assert.Equal(1, fixture.Protocol.CreateSnapshotExportCount);
        Assert.Equal(1, fixture.SnapshotExport.ImportCount);
        Assert.Equal(1, fixture.SnapshotImport.DisposeCount);
        Assert.Equal(1, fixture.SnapshotExport.DisposeCount);
        Assert.Empty(fixture.Protocol.Settlements);
    }

    [Fact]
    public async Task LogicalReplicationBaselineHandoff_ObservationCompletionDoesNotRegress()
    {
        await using var fixture = CreateBaselineHandoffFixture();
        var startedAtUtc = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var observer = new CollectingObserver();
        await using var handoff = await PostgresLogicalReplicationBaselineHandoff.CreateAsync(
            OperationContext.Create(
                timeProvider: new RegressingTimeProvider(startedAtUtc)),
            fixture.Reader,
            fixture.Placement,
            fixture.Runtime,
            fixture.Binding,
            fixture.Protocol,
            ContinuationAuthenticationKey,
            fixture.Policy,
            observer);

        var observation = Assert.Single(
            observer.Operations,
            static candidate => candidate.Operation
                == PostgresLogicalReplicationOperationKind.SnapshotHandoff);
        Assert.True(observation.CompletedAtUtc >= observation.StartedAtUtc);
        Assert.Equal(observation.StartedAtUtc, observation.CompletedAtUtc);
    }

    static BaselineHandoffFixture CreateBaselineHandoffFixture(
        bool failPostSlotPreflight = false)
    {
        var canonical = CreateCanonicalExecutionFixture(
            static (command, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException(
                    $"Baseline handoff bypassed the imported snapshot executor for '{command.Text}'.");
            });
        var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=cohesive_tests;Username=postgres;Password=not-used;Timeout=1");
        try
        {
            var runtime = new PostgresNpgsqlRuntimeBinding(
                canonical.Storage.Database,
                dataSource,
                "tests/postgres/logical-replication-baseline-handoff/v1");
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
            var replicaIdentity = new PostgresLogicalReplicationReplicaIdentityBinding(
                PostgresLogicalReplicationReplicaIdentityKind.Full);
            var binding = new PostgresLogicalReplicationBinding(
                publicationName: "cohesive_items",
                slotName: "cohesive_items_slot",
                slotGeneration: "generation-1",
                expectedReplicaIdentity: replicaIdentity,
                beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required);
            var tableExecutor = new TableExecutor(
            [
                new("item-a", "Alpha", null, "parent-1"),
                new("item-b", "Beta", null, "parent-1")
            ]);
            var snapshotImport = new FakeSnapshotImport(tableExecutor.ExecuteAsync);
            var snapshotExport = new FakeSnapshotExport(
                snapshotImport,
                consistentPosition: new(100));
            var protocol = new FakeSnapshotHandoffProtocol(
                Deployment(table, binding),
                snapshotExport,
                failPostSlotPreflight);
            var policy = new PostgresLogicalReplicationSourcePolicy(
                maximumTransactionChanges: 10,
                maximumTransactionBytes: 1_000_000,
                maximumTransactionsPerRead: 4,
                maximumReconnectAttempts: 0);
            return new(
                dataSource,
                canonical,
                reader,
                runtime,
                placement,
                binding,
                protocol,
                snapshotExport,
                snapshotImport,
                policy);
        }
        catch
        {
            dataSource.Dispose();
            throw;
        }
    }

    static ValueTask<PostgresLogicalReplicationBaselineHandoff> CreateBaselineHandoffAsync(
        BaselineHandoffFixture fixture) => PostgresLogicalReplicationBaselineHandoff.CreateAsync(
            OperationContext.Create(),
            fixture.Reader,
            fixture.Placement,
            fixture.Runtime,
            fixture.Binding,
            fixture.Protocol,
            ContinuationAuthenticationKey,
            fixture.Policy);

    sealed record BaselineHandoffFixture(
        NpgsqlDataSource DataSource,
        CanonicalExecutionFixture Canonical,
        PostgresRelationQuerySourceReader Reader,
        PostgresNpgsqlRuntimeBinding Runtime,
        RelationQuerySourcePlacementBinding Placement,
        PostgresLogicalReplicationBinding Binding,
        FakeSnapshotHandoffProtocol Protocol,
        FakeSnapshotExport SnapshotExport,
        FakeSnapshotImport SnapshotImport,
        PostgresLogicalReplicationSourcePolicy Policy) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }

    sealed class FakeSnapshotImport(
        PostgresNpgsqlCommandExecutor executeCommand) : IPostgresLogicalReplicationSnapshotImport
    {
        readonly PostgresNpgsqlCommandExecutor innerExecuteCommand = executeCommand;
        int disposed;

        public PostgresNpgsqlCommandExecutor ExecuteCommand => ExecuteAsync;

        internal object Lease { get; } = new();

        internal List<object> InvocationLeases { get; } = [];

        internal int DisposeCount { get; private set; }

        async ValueTask<PostgresNpgsqlCommandResult> ExecuteAsync(
            PostgresNpgsqlCommand command,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            InvocationLeases.Add(Lease);
            return await innerExecuteCommand(command, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    sealed class FakeSnapshotExport(
        FakeSnapshotImport snapshotImport,
        PostgresLogicalReplicationWalPosition consistentPosition)
        : IPostgresLogicalReplicationSnapshotExport
    {
        bool imported;

        public string SnapshotName => "cohesive-snapshot-1";

        public PostgresLogicalReplicationWalPosition ConsistentPosition { get; } = consistentPosition;

        internal int ImportCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public ValueTask<IPostgresLogicalReplicationSnapshotImport> ImportAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (imported)
                throw new InvalidOperationException("The fake exported snapshot was already imported.");
            imported = true;
            ImportCount++;
            return ValueTask.FromResult<IPostgresLogicalReplicationSnapshotImport>(snapshotImport);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    sealed class FakeSnapshotHandoffProtocol(
        PostgresLogicalReplicationDeployment deployment,
        FakeSnapshotExport snapshotExport,
        bool failPostSlotPreflight) : IPostgresLogicalReplicationProtocol
    {
        internal PostgresLogicalReplicationDeployment Deployment { get; set; } = deployment;

        internal PostgresLogicalReplicationReadBatch Batch { get; set; } = new(
            [],
            deployment.CurrentWalPosition,
            ReachedUpperBoundary: true);

        internal int InspectCount { get; private set; }

        internal int CreateSnapshotExportCount { get; private set; }

        internal List<ReadInvocation> Reads { get; } = [];

        internal List<PostgresLogicalReplicationWalPosition> Settlements { get; } = [];

        public ValueTask<PostgresLogicalReplicationDeployment> InspectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCount++;
            if (failPostSlotPreflight)
            {
                throw new PostgresLogicalReplicationProtocolException(
                    PostgresLogicalReplicationFailureKind.Terminal,
                    isTransient: false,
                    evidenceReference: "tests/post-slot-preflight");
            }
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
            return ValueTask.FromResult(new PostgresLogicalReplicationFeedback(
                PostgresLogicalReplicationFeedbackDisposition.Confirmed,
                Deployment.ConfirmedFlushPosition,
                position));
        }

        public ValueTask<IPostgresLogicalReplicationSnapshotExport> CreateSnapshotExportAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateSnapshotExportCount++;
            return ValueTask.FromResult<IPostgresLogicalReplicationSnapshotExport>(snapshotExport);
        }
    }

    sealed class RegressingTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        int readCount;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref readCount) == 1
                ? initialUtcNow
                : initialUtcNow.AddMinutes(-1);
    }
}
