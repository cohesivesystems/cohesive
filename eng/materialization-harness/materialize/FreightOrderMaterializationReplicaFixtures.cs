using System.Collections.Immutable;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Storage.Materialization;
using Microsoft.Azure.Cosmos;
using Npgsql;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>
/// Explicit physical source dialect used to project canonical freight semantics into one adapter placement.
/// </summary>
internal abstract class FreightOrderMaterializationReplicaDialect
{
    /// <summary>Stable provider interpretation identity.</summary>
    internal abstract string Provider { get; }

    /// <summary>Exact Relations source capability profile.</summary>
    internal abstract RelationQueryTargetCapabilityProfile TargetProfile { get; }

    /// <summary>Physical selector carrying the tenant partition.</summary>
    internal abstract string PartitionSelector { get; }

    /// <summary>Physical selector carrying observation identity.</summary>
    internal abstract string IdentitySelector { get; }

    /// <summary>Projects one semantic field path into its exact physical selector.</summary>
    /// <param name="shape">Canonical freight shape.</param>
    /// <param name="path">Canonical semantic field path.</param>
    /// <returns>Provider selector used by the physical placement.</returns>
    internal abstract string FieldSelector(QualifiedShapeId shape, FieldPath path);

    /// <summary>Binds this physical dialect to the configured local resources.</summary>
    /// <param name="options">Exact local environment configuration.</param>
    /// <param name="postgresDataSource">Effective PostgreSQL connection pool.</param>
    /// <param name="cosmosClient">Owning Cosmos client.</param>
    /// <returns>An explicit provider fixture.</returns>
    internal abstract IFreightOrderMaterializationReplicaFixture Bind(
        Program.HarnessOptions options,
        NpgsqlDataSource postgresDataSource,
        CosmosClient cosmosClient);
}

/// <summary>Runtime request for loading one already-begun materialization generation.</summary>
internal sealed record FreightOrderMaterializationReplicaLoadRequest(
    FreightOrderMaterializationSemantics Semantics,
    Program.ProviderPlan Plan,
    IMaterializationTarget Target,
    MaterializationGenerationId GenerationId,
    MaterializationWorkerFence WorkerFence,
    MaterializationGenerationSnapshot Generation,
    OperationContext Context,
    MaterializationHarnessRunOptions Run);

/// <summary>One explicit source-adapter fixture for the canonical freight scenario.</summary>
internal interface IFreightOrderMaterializationReplicaFixture
{
    /// <summary>Physical source dialect projected by this fixture.</summary>
    FreightOrderMaterializationReplicaDialect Dialect { get; }

    /// <summary>Compiles the canonical rebuild against exact provider bindings.</summary>
    /// <param name="semantics">Canonical freight semantics.</param>
    /// <param name="plan">Provider placement projected from <see cref="Dialect"/>.</param>
    /// <param name="target">Exact generational materialization target.</param>
    /// <param name="journal">Canonical scenario authority.</param>
    /// <param name="cancellationToken">Compilation and preflight cancellation.</param>
    /// <returns>Portable plan artifacts linked to exact runtime sources.</returns>
    ValueTask<FreightOrderRebuildPlanCompilation> CompileAsync(
        FreightOrderMaterializationSemantics semantics,
        Program.ProviderPlan plan,
        IMaterializationTarget target,
        FreightScenarioJournal journal,
        CancellationToken cancellationToken);

    /// <summary>Loads one candidate generation through this fixture's source readers.</summary>
    /// <param name="request">Canonical load request plus exact provider plan and target.</param>
    /// <returns>The final observed generation after every tenant page is applied.</returns>
    ValueTask<MaterializationGenerationSnapshot> LoadGenerationAsync(
        FreightOrderMaterializationReplicaLoadRequest request);
}

/// <summary>Closed composition root for the local source fixtures; runners consume the open fixture catalog.</summary>
internal sealed class FreightOrderMaterializationReplicaFixtureCatalog : IAsyncDisposable
{
    readonly CosmosClient cosmosClient;
    readonly NpgsqlDataSource? ownedPostgresDataSource;

    FreightOrderMaterializationReplicaFixtureCatalog(
        ImmutableArray<IFreightOrderMaterializationReplicaFixture> fixtures,
        CosmosClient cosmosClient,
        NpgsqlDataSource? ownedPostgresDataSource)
    {
        Fixtures = fixtures;
        this.cosmosClient = cosmosClient;
        this.ownedPostgresDataSource = ownedPostgresDataSource;
    }

    /// <summary>Explicit fixtures in canonical provider-identity order.</summary>
    internal ImmutableArray<IFreightOrderMaterializationReplicaFixture> Fixtures { get; }

    /// <summary>Creates local PostgreSQL and Cosmos fixtures while preserving explicit resource ownership.</summary>
    /// <param name="options">Exact local environment configuration.</param>
    /// <param name="postgresDataSource">Optional caller-owned PostgreSQL pool.</param>
    /// <returns>An owning fixture catalog. The supplied pool remains caller-owned.</returns>
    internal static FreightOrderMaterializationReplicaFixtureCatalog Create(
        Program.HarnessOptions options,
        NpgsqlDataSource? postgresDataSource = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var owned = postgresDataSource is null
            ? NpgsqlDataSource.Create(options.PostgresConnectionString)
            : null;
        var effectivePostgres = postgresDataSource ?? owned!;
        var cosmos = Program.CreateCosmosClient(options.CosmosConnectionString);
        try
        {
            var fixtures = FreightOrderMaterializationReplicaDialects.All
                .Select(dialect => dialect.Bind(
                    options: options,
                    postgresDataSource: effectivePostgres,
                    cosmosClient: cosmos))
                .ToImmutableArray();
            return new(
                fixtures: [.. fixtures.OrderBy(static fixture => fixture.Dialect.Provider, StringComparer.Ordinal)],
                cosmosClient: cosmos,
                ownedPostgresDataSource: owned);
        }
        catch
        {
            cosmos.Dispose();
            if (owned is not null)
                owned.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        cosmosClient.Dispose();
        if (ownedPostgresDataSource is not null)
            await ownedPostgresDataSource.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Provider dialect catalog used by validation and target-only lifecycle operations.</summary>
internal static class FreightOrderMaterializationReplicaDialects
{
    /// <summary>Configured local source dialects in canonical provider order.</summary>
    internal static ImmutableArray<FreightOrderMaterializationReplicaDialect> All { get; } =
    [
        CosmosFreightOrderMaterializationReplicaDialect.Instance,
        PostgresFreightOrderMaterializationReplicaDialect.Instance
    ];

    /// <summary>Gets one configured dialect by exact provider identity.</summary>
    /// <param name="provider">Stable provider identity.</param>
    /// <returns>The exact configured physical source dialect.</returns>
    /// <exception cref="ArgumentException"><paramref name="provider"/> is empty.</exception>
    /// <exception cref="KeyNotFoundException">The provider is not configured.</exception>
    internal static FreightOrderMaterializationReplicaDialect Get(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        foreach (var dialect in All)
        {
            if (string.Equals(dialect.Provider, provider, StringComparison.Ordinal))
                return dialect;
        }
        throw new KeyNotFoundException($"No freight materialization replica dialect is configured for '{provider}'.");
    }
}

sealed class PostgresFreightOrderMaterializationReplicaDialect : FreightOrderMaterializationReplicaDialect
{
    internal static PostgresFreightOrderMaterializationReplicaDialect Instance { get; } = new();

    PostgresFreightOrderMaterializationReplicaDialect()
    {
    }

    internal override string Provider => "postgres";

    internal override RelationQueryTargetCapabilityProfile TargetProfile =>
        PostgresRelationQuerySourceTargetProfile.Default;

    internal override string PartitionSelector => "tenantId";

    internal override string IdentitySelector => "id";

    internal override string FieldSelector(QualifiedShapeId shape, FieldPath path) =>
        Program.PostgresColumn(shape, path);

    internal override IFreightOrderMaterializationReplicaFixture Bind(
        Program.HarnessOptions options,
        NpgsqlDataSource postgresDataSource,
        CosmosClient cosmosClient) =>
        new PostgresFreightOrderMaterializationReplicaFixture(
            dataSource: postgresDataSource,
            connectionString: options.PostgresConnectionString,
            tenants: options.Tenants);
}

sealed class CosmosFreightOrderMaterializationReplicaDialect : FreightOrderMaterializationReplicaDialect
{
    internal static CosmosFreightOrderMaterializationReplicaDialect Instance { get; } = new();

    CosmosFreightOrderMaterializationReplicaDialect()
    {
    }

    internal override string Provider => "cosmos";

    internal override RelationQueryTargetCapabilityProfile TargetProfile =>
        CosmosRelationQuerySourceReader.TargetProfile;

    internal override string PartitionSelector =>
        CosmosRelationQuerySourceReader.ObservationPartitionSourceSelector;

    internal override string IdentitySelector =>
        CosmosRelationQuerySourceReader.ObservationIdentitySourceSelector;

    internal override string FieldSelector(QualifiedShapeId shape, FieldPath path) =>
        CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(path);

    internal override IFreightOrderMaterializationReplicaFixture Bind(
        Program.HarnessOptions options,
        NpgsqlDataSource postgresDataSource,
        CosmosClient cosmosClient) =>
        new CosmosFreightOrderMaterializationReplicaFixture(
            database: cosmosClient.GetDatabase(options.CosmosDatabase),
            databaseId: options.CosmosDatabase,
            tenants: options.Tenants);
}

sealed class PostgresFreightOrderMaterializationReplicaFixture : IFreightOrderMaterializationReplicaFixture
{
    readonly NpgsqlDataSource dataSource;
    readonly string connectionString;
    readonly ImmutableArray<string> tenants;

    internal PostgresFreightOrderMaterializationReplicaFixture(
        NpgsqlDataSource dataSource,
        string connectionString,
        ImmutableArray<string> tenants)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.connectionString = connectionString;
        this.tenants = tenants;
    }

    public FreightOrderMaterializationReplicaDialect Dialect =>
        PostgresFreightOrderMaterializationReplicaDialect.Instance;

    public async ValueTask<FreightOrderRebuildPlanCompilation> CompileAsync(
        FreightOrderMaterializationSemantics semantics,
        Program.ProviderPlan plan,
        IMaterializationTarget target,
        FreightScenarioJournal journal,
        CancellationToken cancellationToken) =>
        await Program.CompilePostgresRebuildPlanAsync(
                semantics: semantics,
                plan: plan,
                target: target,
                dataSource: dataSource,
                connectionString: connectionString,
                tenants: tenants,
                journal: journal,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<MaterializationGenerationSnapshot> LoadGenerationAsync(
        FreightOrderMaterializationReplicaLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hydrationStorage = Program.CreatePostgresStorageBinding(
            placement: request.Plan.HydrationPlacement,
            plan: request.Semantics.Plan,
            purpose: "standalone-hydration");
        var scanStorage = Program.CreatePostgresStorageBinding(
            placement: request.Plan.ScanPlacement,
            plan: request.Semantics.Plan,
            purpose: "standalone-scan");
        var generation = request.Generation;
        var pageOrdinal = 0;
        foreach (var tenant in tenants)
        {
            request.Context.ThrowIfCancellationRequested();
            var policy = Program.PostgresPolicy(tenant);
            var scanRuntime = new PostgresNpgsqlRuntimeBinding(
                database: scanStorage.Database,
                dataSource: dataSource,
                authority: "materialization-harness/postgres/standalone-scan");
            var rootReader = new PostgresRelationQuerySourceReader(
                plan: request.Semantics.Plan,
                physicalPlan: request.Plan.ScanPhysicalPlan,
                source: request.Plan.OrderSource,
                storage: scanStorage,
                dataSource: dataSource,
                runtimeBinding: scanRuntime,
                policy: policy);
            var source = Program.CreatePostgresBaselineSource(rootReader, request.Plan.ScanRoot);
            var readers = request.Plan.HydrationSources.Select(sourceId =>
            {
                var runtime = new PostgresNpgsqlRuntimeBinding(
                    database: hydrationStorage.Database,
                    dataSource: dataSource,
                    authority: "materialization-harness/postgres/standalone-hydration");
                return (IRelationQuerySourceReader)new PostgresRelationQuerySourceReader(
                    plan: request.Semantics.Plan,
                    physicalPlan: request.Plan.HydrationPhysicalPlan,
                    source: sourceId,
                    storage: hydrationStorage,
                    dataSource: dataSource,
                    runtimeBinding: runtime,
                    policy: policy);
            }).ToImmutableArray();
            await Program.MaterializeTenantAsync(
                    provider: Dialect.Provider,
                    tenant: tenant,
                    semantics: request.Semantics,
                    plan: request.Plan,
                    source: source,
                    sourceScope: source.Scope,
                    readers: readers,
                    target: request.Target,
                    generationId: request.GenerationId,
                    workerFence: request.WorkerFence,
                    initialRevision: generation.Revision,
                    pageOrdinalBase: pageOrdinal,
                    context: request.Context,
                    run: request.Run)
                .ConfigureAwait(false);
            pageOrdinal += 1_000;
            generation = await RequireGenerationAsync(request).ConfigureAwait(false);
        }
        return generation;
    }

    static async ValueTask<MaterializationGenerationSnapshot> RequireGenerationAsync(
        FreightOrderMaterializationReplicaLoadRequest request) =>
        await request.Target.InspectGenerationAsync(request.Context, request.GenerationId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The PostgreSQL candidate generation disappeared while loading.");
}

sealed class CosmosFreightOrderMaterializationReplicaFixture : IFreightOrderMaterializationReplicaFixture
{
    readonly Database database;
    readonly string databaseId;
    readonly ImmutableArray<string> tenants;

    internal CosmosFreightOrderMaterializationReplicaFixture(
        Database database,
        string databaseId,
        ImmutableArray<string> tenants)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.databaseId = databaseId;
        this.tenants = tenants;
    }

    public FreightOrderMaterializationReplicaDialect Dialect =>
        CosmosFreightOrderMaterializationReplicaDialect.Instance;

    public ValueTask<FreightOrderRebuildPlanCompilation> CompileAsync(
        FreightOrderMaterializationSemantics semantics,
        Program.ProviderPlan plan,
        IMaterializationTarget target,
        FreightScenarioJournal journal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Program.CompileCosmosRebuildPlan(
            semantics: semantics,
            plan: plan,
            target: target,
            database: database,
            databaseId: databaseId,
            tenants: tenants,
            journal: journal));
    }

    public async ValueTask<MaterializationGenerationSnapshot> LoadGenerationAsync(
        FreightOrderMaterializationReplicaLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generation = request.Generation;
        var pageOrdinal = 0;
        foreach (var tenant in tenants)
        {
            request.Context.ThrowIfCancellationRequested();
            var policy = Program.CosmosPolicy(tenant);
            var rootReader = Program.CreateCosmosReader(
                shape: request.Semantics.Root.Shape,
                sourceId: request.Plan.OrderSource,
                container: database.GetContainer("orders"),
                databaseId: databaseId,
                containerId: "orders",
                policy: policy);
            var source = Program.CreateCosmosReconciliationSource(rootReader);
            var sourceScope = new MaterializationSourceScope(
                physicalPlan: request.Plan.ScanPhysicalPlan.Fingerprint,
                placement: request.Plan.ScanRoot,
                logicalPartition: Program.LogicalPartition(tenant),
                partition: new($"cosmos/reconciliation/{tenant}"),
                orderingScope: new($"cosmos/reconciliation/{tenant}/canonical-order"));
            var readers = request.Plan.HydrationSources
                .Select(sourceId => Program.CreateCosmosHydrationReader(
                    semantics: request.Semantics,
                    plan: request.Plan,
                    sourceId: sourceId,
                    database: database,
                    databaseId: databaseId,
                    policy: policy))
                .Cast<IRelationQuerySourceReader>()
                .ToImmutableArray();
            await Program.MaterializeTenantAsync(
                    provider: Dialect.Provider,
                    tenant: tenant,
                    semantics: request.Semantics,
                    plan: request.Plan,
                    source: source,
                    sourceScope: sourceScope,
                    readers: readers,
                    target: request.Target,
                    generationId: request.GenerationId,
                    workerFence: request.WorkerFence,
                    initialRevision: generation.Revision,
                    pageOrdinalBase: pageOrdinal,
                    context: request.Context,
                    run: request.Run)
                .ConfigureAwait(false);
            pageOrdinal += 1_000;
            generation = await request.Target
                .InspectGenerationAsync(request.Context, request.GenerationId)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Cosmos candidate generation disappeared while loading.");
        }
        return generation;
    }
}
