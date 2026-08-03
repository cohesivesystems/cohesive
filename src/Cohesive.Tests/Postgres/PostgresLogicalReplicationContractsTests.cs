using System.Reflection;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Npgsql;
using Npgsql.Replication;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresLogicalReplicationContractsTests
{
    static readonly QualifiedShapeId Shape = new(new("tests/postgres/logical"), new("Item"));
    static readonly RelationQuerySourcePlacementBinding Placement = new(
        id: new("placement/postgres-logical"),
        input: new("input/postgres-logical"),
        node: new("node/postgres-logical"),
        binding: new("binding/postgres-logical"),
        shape: Shape,
        source: new("source/postgres-logical"),
        kind: RelationQuerySourcePlacementBindingKind.SourceSet,
        acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
        origin: RelationQuerySourcePlacementOrigin.Explicit,
        identity: new(Shape, "id"));
    static readonly MaterializationSourceScope Scope = new(
        physicalPlan: new(
            algorithm: "sha256",
            canonicalization: "tests/physical-plan/v1",
            value: "0123456789abcdef"),
        placement: Placement,
        partition: new("partition-a"),
        orderingScope: new("partition-a/logical-slot"));
    static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Binding_PreservesExactDedicatedSlotGenerationAndFullBeforeImageRequirement()
    {
        var replicaIdentity = new PostgresLogicalReplicationReplicaIdentityBinding(
            kind: PostgresLogicalReplicationReplicaIdentityKind.Full);

        var binding = new PostgresLogicalReplicationBinding(
            publicationName: "cohesive_items_publication",
            slotName: "cohesive_items_01",
            slotGeneration: "deployment/items-slot@generation-3",
            expectedReplicaIdentity: replicaIdentity,
            beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required);

        Assert.Equal("cohesive_items_publication", binding.PublicationName);
        Assert.Equal("cohesive_items_01", binding.SlotName);
        Assert.Equal("deployment/items-slot@generation-3", binding.SlotGeneration);
        Assert.Same(replicaIdentity, binding.ExpectedReplicaIdentity);
        Assert.Equal(
            PostgresLogicalReplicationBeforeImageRequirement.Required,
            binding.BeforeImageRequirement);
        Assert.True(binding.ExpectedReplicaIdentity.ProvidesCompleteBeforeImage);
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("slot_123")]
    [InlineData("0_slot")]
    [InlineData("_")]
    public void Binding_AcceptsExactPostgresReplicationSlotGrammar(string slotName)
    {
        var binding = Binding(slotName: slotName);

        Assert.Equal(slotName, binding.SlotName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Slot")]
    [InlineData("slot-name")]
    [InlineData("slot.name")]
    [InlineData("slöt")]
    public void Binding_RejectsValuesOutsideExactPostgresReplicationSlotGrammar(string slotName)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => Binding(slotName: slotName));

        Assert.Equal("slotName", exception.ParamName);
    }

    [Fact]
    public void Binding_EnforcesPostgresSlotLengthAndCanonicalGenerationGrammar()
    {
        var maximumSlotName = new string('a', 63);
        Assert.Equal(maximumSlotName, Binding(slotName: maximumSlotName).SlotName);

        Assert.Equal(
            "slotName",
            Assert.Throws<ArgumentException>(() => Binding(slotName: new string('a', 64))).ParamName);
        Assert.Equal(
            "slotName",
            Assert.Throws<ArgumentNullException>(() => Binding(slotName: null!)).ParamName);
        Assert.Equal(
            "slotGeneration",
            Assert.Throws<ArgumentException>(() => Binding(slotGeneration: "generation with space")).ParamName);
        Assert.Equal(
            "slotGeneration",
            Assert.Throws<ArgumentException>(() => Binding(slotGeneration: "génération")).ParamName);
        Assert.Equal(
            "slotGeneration",
            Assert.Throws<ArgumentException>(() => Binding(
                slotGeneration: new string('a', PostgresLogicalReplicationBinding.MaximumSlotGenerationCharacters + 1)))
                .ParamName);
    }

    [Fact]
    public void ReplicaIdentity_RequiresOneExactIndexOnlyForIndexMode()
    {
        var index = new PostgresLogicalReplicationReplicaIdentityBinding(
            kind: PostgresLogicalReplicationReplicaIdentityKind.Index,
            indexName: "uq_items_replica_identity");

        Assert.Equal(PostgresLogicalReplicationReplicaIdentityKind.Index, index.Kind);
        Assert.Equal("uq_items_replica_identity", index.IndexName);
        Assert.False(index.ProvidesCompleteBeforeImage);
        Assert.Equal(
            "indexName",
            Assert.Throws<ArgumentNullException>(() =>
                new PostgresLogicalReplicationReplicaIdentityBinding(
                    kind: PostgresLogicalReplicationReplicaIdentityKind.Index,
                    indexName: null))
                .ParamName);
        Assert.Equal(
            "indexName",
            Assert.Throws<ArgumentException>(() =>
                new PostgresLogicalReplicationReplicaIdentityBinding(
                    kind: PostgresLogicalReplicationReplicaIdentityKind.Full,
                    indexName: "uq_items_replica_identity"))
                .ParamName);
        Assert.Equal(
            "kind",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new PostgresLogicalReplicationReplicaIdentityBinding(
                    kind: (PostgresLogicalReplicationReplicaIdentityKind)int.MaxValue))
                .ParamName);
    }

    [Fact]
    public void Binding_RejectsRequiredBeforeImagesWithoutFullReplicaIdentity()
    {
        var exception = Assert.Throws<ArgumentException>(() => new PostgresLogicalReplicationBinding(
            publicationName: "cohesive_items_publication",
            slotName: "cohesive_items",
            slotGeneration: "deployment/items-slot@generation-1",
            expectedReplicaIdentity: new(
                kind: PostgresLogicalReplicationReplicaIdentityKind.Default),
            beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required));

        Assert.Equal("expectedReplicaIdentity", exception.ParamName);
    }

    [Fact]
    public void SourcePolicy_ProvidesBoundedTransactionReadRecoverySettlementAndRetentionDefaults()
    {
        var policy = PostgresLogicalReplicationSourcePolicy.Default;

        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultMaximumTransactionChanges, policy.MaximumTransactionChanges);
        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultMaximumTransactionBytes, policy.MaximumTransactionBytes);
        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultMaximumTransactionsPerRead, policy.MaximumTransactionsPerRead);
        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultMaximumReconnectAttempts, policy.MaximumReconnectAttempts);
        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultReconnectDelay, policy.ReconnectDelay);
        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultReadInactivityTimeout, policy.ReadInactivityTimeout);
        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultSettlementConfirmationTimeout, policy.SettlementConfirmationTimeout);
        Assert.Equal(
            PostgresLogicalReplicationSourcePolicy.DefaultSettlementConfirmationPollInterval,
            policy.SettlementConfirmationPollInterval);
        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultRetentionDangerBytes, policy.RetentionDangerBytes);
        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultRetentionDangerTime, policy.RetentionDangerTime);
        Assert.Equal(PostgresLogicalReplicationSourcePolicy.DefaultMaximumPositionCharacters, policy.MaximumPositionCharacters);
    }

    [Fact]
    public void SourcePolicy_RejectsUnboundedOrIncoherentOperatingLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(maximumTransactionChanges: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(maximumTransactionChanges: Array.MaxLength));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(maximumTransactionBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(maximumTransactionBytes: (long)Array.MaxLength + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(maximumTransactionsPerRead: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(maximumReconnectAttempts: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(reconnectDelay: TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(readInactivityTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(settlementConfirmationTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(settlementConfirmationPollInterval: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresLogicalReplicationSourcePolicy(
            settlementConfirmationTimeout: TimeSpan.FromSeconds(1),
            settlementConfirmationPollInterval: TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(retentionDangerBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(retentionDangerTime: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgresLogicalReplicationSourcePolicy(maximumPositionCharacters: 0));
    }

    [Fact]
    public void ObservationsAndException_KeepTypedFailureHealthAndScopeEvidenceAligned()
    {
        var health = new PostgresLogicalReplicationHealthObservation(
            state: PostgresLogicalReplicationHealthState.RetentionDanger,
            scope: Scope,
            observedAtUtc: ObservedAtUtc,
            estimatedPendingWalBytes: 1_024,
            retainedWalBytes: 2_048,
            remainingSafeWalBytes: 4_096,
            estimatedLag: TimeSpan.FromSeconds(3),
            inactivity: TimeSpan.FromSeconds(2),
            evidenceReference: "postgres/slot-health/v1");
        var failed = OperationObservation(
            disposition: PostgresLogicalReplicationOperationDisposition.Failed,
            failureKind: PostgresLogicalReplicationFailureKind.SettlementUnconfirmed);

        var exception = new PostgresLogicalReplicationException(
            message: "PostgreSQL slot settlement could not be confirmed.",
            failureKind: PostgresLogicalReplicationFailureKind.SettlementUnconfirmed,
            observation: failed,
            health: health);

        Assert.Equal(PostgresLogicalReplicationHealthState.RetentionDanger, health.State);
        Assert.Equal(PostgresLogicalReplicationFailureKind.SettlementUnconfirmed, exception.FailureKind);
        Assert.Same(failed, exception.Observation);
        Assert.Same(health, exception.Health);
    }

    [Fact]
    public void HealthProjection_PreservesAdapterEvidenceAndCommonReadinessSemantics()
    {
        var observation = new PostgresLogicalReplicationHealthObservation(
            state: PostgresLogicalReplicationHealthState.RetentionDanger,
            scope: Scope,
            observedAtUtc: ObservedAtUtc,
            estimatedPendingWalBytes: 1_024,
            retainedWalBytes: 2_048,
            remainingSafeWalBytes: 4_096,
            estimatedLag: TimeSpan.FromSeconds(3),
            inactivity: TimeSpan.FromSeconds(2),
            evidenceReference: "postgres/slot-health/v1");
        var provenance = new ExecutionProvenance(
            new("cohesive.adapters.postgres", "1"),
            new("postgres/logical-replication/health"),
            DocumentOrigin.Generated);

        var health = PostgresLogicalReplicationHealthProjector.Project(observation, provenance);

        Assert.Equal(ExecutionHealthStatus.Degraded, health.Health);
        Assert.Equal(ExecutionReadinessStatus.Ready, health.Readiness);
        Assert.Equal(ObservedAtUtc, health.ObservedAtUtc);
        Assert.True(health.EvidenceReferences.SequenceEqual(["postgres/slot-health/v1"]));
        Assert.Equal(provenance, health.Provenance);
    }

    [Fact]
    public void Observations_RejectInvalidTimeMeasurementsAndDispositionEvidence()
    {
        Assert.Throws<ArgumentException>(() => new PostgresLogicalReplicationHealthObservation(
            state: PostgresLogicalReplicationHealthState.Healthy,
            scope: Scope,
            observedAtUtc: ObservedAtUtc.ToOffset(TimeSpan.FromHours(1)),
            estimatedPendingWalBytes: null,
            retainedWalBytes: null,
            remainingSafeWalBytes: null,
            estimatedLag: null,
            inactivity: null,
            evidenceReference: "postgres/slot-health/v1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresLogicalReplicationHealthObservation(
            state: PostgresLogicalReplicationHealthState.Healthy,
            scope: Scope,
            observedAtUtc: ObservedAtUtc,
            estimatedPendingWalBytes: -1,
            retainedWalBytes: null,
            remainingSafeWalBytes: null,
            estimatedLag: null,
            inactivity: null,
            evidenceReference: "postgres/slot-health/v1"));
        Assert.Throws<ArgumentException>(() => OperationObservation(
            disposition: PostgresLogicalReplicationOperationDisposition.Failed,
            failureKind: null));
        Assert.Throws<ArgumentException>(() => OperationObservation(
            disposition: PostgresLogicalReplicationOperationDisposition.Complete,
            failureKind: PostgresLogicalReplicationFailureKind.Transient));
        Assert.Throws<ArgumentException>(() => OperationObservation(
            disposition: PostgresLogicalReplicationOperationDisposition.Retrying,
            failureKind: PostgresLogicalReplicationFailureKind.Transient,
            retryAfter: null));
        Assert.Throws<ArgumentException>(() => OperationObservation(
            disposition: PostgresLogicalReplicationOperationDisposition.Failed,
            failureKind: PostgresLogicalReplicationFailureKind.Transient,
            retryAfter: TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public async Task RuntimeBinding_RequiresFreshMatchingLogicalReplicationConnections()
    {
        const string connectionString =
            "Host=localhost;Port=5432;Database=cohesive;Username=cohesive;Password=not-used;Pooling=true;Enlist=true;Multiplexing=false;Keepalive=7";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var binding = new PostgresNpgsqlRuntimeBinding(
            database: new("tests/postgres/logical"),
            dataSource: dataSource,
            authority: "cohesive.tests/postgres/logical-runtime/v1",
            logicalReplicationConnectionFactory: () => new LogicalReplicationConnection(connectionString));

        Assert.True(binding.SupportsLogicalReplication);
        Assert.Same(dataSource, binding.DataSource);
        await using var first = await binding.CreateLogicalReplicationConnectionAsync();
        await using var second = await binding.CreateLogicalReplicationConnectionAsync();
        Assert.NotSame(first, second);

        await using var dataOnlySource = NpgsqlDataSource.Create(connectionString);
        var dataOnlyBinding = new PostgresNpgsqlRuntimeBinding(
            database: new("tests/postgres/logical"),
            dataSource: dataOnlySource,
            authority: "cohesive.tests/postgres/data-runtime/v1");
        Assert.False(dataOnlyBinding.SupportsLogicalReplication);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dataOnlyBinding.CreateLogicalReplicationConnectionAsync().AsTask());
    }

    [Fact]
    public async Task RuntimeBinding_RejectsWrongOrReusedLogicalReplicationConnections()
    {
        const string connectionString =
            "Host=localhost;Port=5432;Database=cohesive;Username=cohesive;Password=not-used";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        LogicalReplicationConnection? wrongConnection = null;
        var wrongBinding = new PostgresNpgsqlRuntimeBinding(
            database: new("tests/postgres/logical"),
            dataSource: dataSource,
            authority: "cohesive.tests/postgres/logical-runtime/v1",
            logicalReplicationConnectionFactory: () => wrongConnection = new LogicalReplicationConnection(
                "Host=other;Port=5432;Database=cohesive;Username=cohesive;Password=not-used"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => wrongBinding.CreateLogicalReplicationConnectionAsync().AsTask());
        await wrongConnection!.DisposeAsync();

        await using var reusedConnection = new LogicalReplicationConnection(connectionString);
        var reusedBinding = new PostgresNpgsqlRuntimeBinding(
            database: new("tests/postgres/logical"),
            dataSource: dataSource,
            authority: "cohesive.tests/postgres/logical-runtime/v1",
            logicalReplicationConnectionFactory: () => reusedConnection);
        Assert.Same(
            reusedConnection,
            await reusedBinding.CreateLogicalReplicationConnectionAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reusedBinding.CreateLogicalReplicationConnectionAsync().AsTask());
    }

    [Fact]
    public void CorePublicApisDoNotExposeNpgsqlTypes()
    {
        var coreAssemblyNames = new[]
        {
            "Cohesive",
            "Cohesive.Relations",
            "Cohesive.Transitions",
            "Cohesive.Processes",
            "Cohesive.Storage"
        };

        var leaked = coreAssemblyNames
            .Select(Assembly.Load)
            .SelectMany(static assembly => assembly.ExportedTypes)
            .SelectMany(PublicContractTypes)
            .SelectMany(ExpandType)
            .FirstOrDefault(static type =>
                type.Namespace?.StartsWith("Npgsql", StringComparison.Ordinal) == true);

        Assert.True(leaked is null, $"Core public API leaked Npgsql type '{leaked}'.");
    }

    static PostgresLogicalReplicationBinding Binding(
        string slotName = "cohesive_items",
        string slotGeneration = "deployment/items-slot@generation-1") => new(
        publicationName: "cohesive_items_publication",
        slotName: slotName,
        slotGeneration: slotGeneration,
        expectedReplicaIdentity: new(
            kind: PostgresLogicalReplicationReplicaIdentityKind.Full),
        beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required);

    static PostgresLogicalReplicationOperationObservation OperationObservation(
        PostgresLogicalReplicationOperationDisposition disposition,
        PostgresLogicalReplicationFailureKind? failureKind,
        TimeSpan? retryAfter = null) => new(
        operation: PostgresLogicalReplicationOperationKind.ChangeRead,
        disposition: disposition,
        scope: Scope,
        startedAtUtc: ObservedAtUtc,
        completedAtUtc: ObservedAtUtc.AddMilliseconds(5),
        attempt: 1,
        transactionCount: 2,
        changeCount: 3,
        canonicalByteCount: 256,
        evidenceReference: "postgres/logical-read/v1",
        failureKind: failureKind,
        retryAfter: retryAfter);

    static IEnumerable<Type> PublicContractTypes(Type type)
    {
        yield return type;
        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (var implementedInterface in type.GetInterfaces())
        {
            yield return implementedInterface;
        }

        foreach (var genericParameter in type.GetGenericArguments())
        {
            foreach (var constraint in genericParameter.GetGenericParameterConstraints())
            {
                yield return constraint;
            }
        }

        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            foreach (var signatureType in PublicSignatureTypes(member))
            {
                yield return signatureType;
            }
        }
    }

    static IEnumerable<Type> PublicSignatureTypes(MemberInfo member) => member switch
    {
        MethodInfo method =>
        [
            method.ReturnType,
            .. method.GetParameters().Select(static parameter => parameter.ParameterType),
            .. method.GetGenericArguments().SelectMany(static parameter => parameter.GetGenericParameterConstraints())
        ],
        ConstructorInfo constructor =>
            constructor.GetParameters().Select(static parameter => parameter.ParameterType),
        PropertyInfo property =>
        [
            property.PropertyType,
            .. property.GetIndexParameters().Select(static parameter => parameter.ParameterType)
        ],
        FieldInfo field => [field.FieldType],
        EventInfo @event when @event.EventHandlerType is { } eventType => [eventType],
        _ => []
    };

    static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var expandedElementType in ExpandType(elementType))
            {
                yield return expandedElementType;
            }
        }
        foreach (var genericArgument in type.GetGenericArguments())
        {
            foreach (var expandedGenericArgument in ExpandType(genericArgument))
            {
                yield return expandedGenericArgument;
            }
        }
    }
}
