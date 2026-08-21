using System.Collections.Immutable;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Storage.Materialization;
using Microsoft.Azure.Cosmos;
using Npgsql;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>Exact provider runtime needed to interpret one canonical freight rebuild plan set.</summary>
public sealed class FreightOrderRebuildProviderRuntime
{
    internal FreightOrderRebuildProviderRuntime(
        string provider,
        FreightOrderRebuildPlanCompilation compilation,
        ElasticMaterializationTargetBinding targetBinding,
        ElasticMaterializationTarget target,
        InMemoryMaterializationTargetPool targetPool,
        MaterializationIndexSyncControlRuntimeProvider controlRuntimeProvider,
        ResolvedMaterializationRebuildPlan resolvedPlan)
    {
        Provider = provider;
        Compilation = compilation;
        TargetBinding = targetBinding;
        Target = target;
        TargetPool = targetPool;
        ControlRuntimeProvider = controlRuntimeProvider;
        ResolvedPlan = resolvedPlan;
    }

    /// <summary>Stable physical source-provider identity.</summary>
    public string Provider { get; }

    /// <summary>Canonical request, placement, leaf plan, plan set, and exact source bindings.</summary>
    public FreightOrderRebuildPlanCompilation Compilation { get; }

    /// <summary>Exact Elasticsearch target binding for this provider interpretation.</summary>
    public ElasticMaterializationTargetBinding TargetBinding { get; }

    /// <summary>Generational Elasticsearch target implementing the compiled target descriptor.</summary>
    public ElasticMaterializationTarget Target { get; }

    /// <summary>Exact backend pool used by the provider's durable routing authority.</summary>
    public InMemoryMaterializationTargetPool TargetPool { get; }

    /// <summary>Durable safe-point Control runtime factory for the exact rebuild plan.</summary>
    public MaterializationIndexSyncControlRuntimeProvider ControlRuntimeProvider { get; }

    /// <summary>Canonical plan resolved against real provider sources, Elasticsearch, and durable state.</summary>
    public ResolvedMaterializationRebuildPlan ResolvedPlan { get; }
}

/// <summary>Owns the real provider resources and exact runtime bindings for the local freight harness.</summary>
/// <remarks>
/// Provider adapters are runtime interpretations only. The shared
/// <see cref="FreightOrderMaterializationSemantics"/> and each persisted rebuild plan remain semantic authority.
/// </remarks>
public sealed class FreightOrderRebuildRuntimeCatalog : IAsyncDisposable
{
    readonly ImmutableDictionary<string, FreightOrderRebuildProviderRuntime> providers;
    readonly HttpClient elasticHttp;
    readonly CosmosClient cosmosClient;

    FreightOrderRebuildRuntimeCatalog(
        FreightOrderMaterializationSemantics semantics,
        ImmutableDictionary<string, FreightOrderRebuildProviderRuntime> providers,
        HttpClient elasticHttp,
        CosmosClient cosmosClient)
    {
        Semantics = semantics;
        this.providers = providers;
        this.elasticHttp = elasticHttp;
        this.cosmosClient = cosmosClient;
    }

    /// <summary>Canonical provider-neutral freight materialization semantics.</summary>
    public FreightOrderMaterializationSemantics Semantics { get; }

    /// <summary>Provider runtimes in canonical provider-name order.</summary>
    public ImmutableArray<FreightOrderRebuildProviderRuntime> Providers =>
        [.. providers.Values.OrderBy(static provider => provider.Provider, StringComparer.Ordinal)];

    /// <summary>Creates exact PostgreSQL and Cosmos rebuild interpretations over the local real containers.</summary>
    /// <param name="dataSource">Caller-owned PostgreSQL connection pool.</param>
    /// <param name="stateStore">Shared durable progress, synchronization, and Control authority.</param>
    /// <param name="authorityScope">Operator authority allowed to submit Control limit updates.</param>
    /// <param name="cancellationToken">Bootstrap cancellation.</param>
    /// <returns>An owning runtime catalog ready for Process graph composition.</returns>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Environment configuration or provider bootstrap is invalid.</exception>
    /// <exception cref="OperationCanceledException">Bootstrap is cancelled.</exception>
    public static async Task<FreightOrderRebuildRuntimeCatalog> CreateAsync(
        NpgsqlDataSource dataSource,
        PostgresMaterializationStateStore stateStore,
        InteractionAuthorityScope authorityScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(authorityScope);
        var options = Program.HarnessOptions.FromEnvironment();
        var semantics = FreightOrderMaterializationModel.Create();
        var journal = await FreightScenarioJournal.LoadAsync(
                path: options.ScenarioPath,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        HttpClient elasticHttp = new() { BaseAddress = options.ElasticsearchEndpoint };
        CosmosClient? cosmosClient = null;
        try
        {
            var clusterId = await Program.ReadClusterIdAsync(elasticHttp).ConfigureAwait(false);
            cosmosClient = Program.CreateCosmosClient(options.CosmosConnectionString);
            var cosmosDatabase = cosmosClient.GetDatabase(options.CosmosDatabase);
            var admission = new MaterializationIndexSyncAdmissionGate();
            var builder = ImmutableDictionary.CreateBuilder<string, FreightOrderRebuildProviderRuntime>(
                StringComparer.Ordinal);
            foreach (var provider in new[] { Program.ProviderKind.Postgres, Program.ProviderKind.Cosmos })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Program.ProviderName(provider);
                var providerPlan = Program.CreateProviderPlan(provider, semantics);
                var targetBinding = Program.CreateTargetBinding(provider, semantics, clusterId);
                await Program.EnsureLocalElasticTemplatesAsync(elasticHttp, targetBinding, name)
                    .ConfigureAwait(false);
                var target = Program.CreateTarget(targetBinding, options.ElasticsearchEndpoint);
                FreightOrderRebuildPlanCompilation compilation;
                if (provider == Program.ProviderKind.Postgres)
                {
                    compilation = await Program.CompilePostgresRebuildPlanAsync(
                        semantics: semantics,
                        plan: providerPlan,
                        target: target,
                        dataSource: dataSource,
                        connectionString: options.PostgresConnectionString,
                        tenants: options.Tenants,
                        journal: journal,
                        cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    compilation = Program.CompileCosmosRebuildPlan(
                        semantics: semantics,
                        plan: providerPlan,
                        target: target,
                        database: cosmosDatabase,
                        databaseId: options.CosmosDatabase,
                        tenants: options.Tenants,
                        journal: journal);
                }
                var controlProvider = new MaterializationIndexSyncControlRuntimeProvider(
                    plan: compilation.Plan,
                    store: stateStore,
                    admission: admission,
                    authorityScope: authorityScope);
                var resolved = compilation.Resolve(
                    target: target,
                    progressStore: stateStore,
                    controlRuntimeProvider: controlProvider);
                var targetPool = new InMemoryMaterializationTargetPool(
                    definition: compilation.Placement.BackendPool.Definition,
                    targets: [target]);
                builder.Add(
                    name,
                    new(
                        provider: name,
                        compilation: compilation,
                        targetBinding: targetBinding,
                        target: target,
                        targetPool: targetPool,
                        controlRuntimeProvider: controlProvider,
                        resolvedPlan: resolved));
            }

            return new(
                semantics: semantics,
                providers: builder.ToImmutable(),
                elasticHttp: elasticHttp,
                cosmosClient: cosmosClient);
        }
        catch
        {
            cosmosClient?.Dispose();
            elasticHttp.Dispose();
            throw;
        }
    }

    /// <summary>Gets one exact provider runtime.</summary>
    /// <param name="provider">Stable provider name.</param>
    /// <returns>The exact provider runtime.</returns>
    /// <exception cref="ArgumentException"><paramref name="provider"/> is empty.</exception>
    /// <exception cref="KeyNotFoundException">The provider is not configured.</exception>
    public FreightOrderRebuildProviderRuntime GetProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return providers[provider];
    }

    /// <summary>Reads canonical materialized values through one provider's active Elasticsearch alias.</summary>
    /// <param name="provider">Stable provider name.</param>
    /// <returns>Canonical JSON values in ordinal order.</returns>
    /// <exception cref="ArgumentException"><paramref name="provider"/> is empty.</exception>
    /// <exception cref="KeyNotFoundException">The provider is not configured.</exception>
    public Task<ImmutableArray<string>> ReadCanonicalDocumentsAsync(string provider)
    {
        var runtime = GetProvider(provider);
        return Program.ReadCanonicalDocumentsAsync(elasticHttp, runtime.TargetBinding.ReadAlias);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        cosmosClient.Dispose();
        elasticHttp.Dispose();
        return ValueTask.CompletedTask;
    }
}
