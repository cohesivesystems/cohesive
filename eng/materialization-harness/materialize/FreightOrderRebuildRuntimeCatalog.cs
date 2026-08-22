using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>Exact logical and adapter metadata retained for one visible freight index item.</summary>
/// <param name="ItemId">Stable Cohesive materialization item identity.</param>
/// <param name="Version">Current monotonic item version.</param>
/// <param name="Value">Current portable logical document value.</param>
public sealed record FreightOrderMaterializedItem(
    MaterializationItemId ItemId,
    MaterializationItemVersion Version,
    ObservationValue Value);

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
    readonly FreightOrderMaterializationReplicaFixtureCatalog fixtures;

    FreightOrderRebuildRuntimeCatalog(
        FreightOrderMaterializationSemantics semantics,
        ImmutableDictionary<string, FreightOrderRebuildProviderRuntime> providers,
        HttpClient elasticHttp,
        FreightOrderMaterializationReplicaFixtureCatalog fixtures)
    {
        Semantics = semantics;
        this.providers = providers;
        this.elasticHttp = elasticHttp;
        this.fixtures = fixtures;
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
        FreightOrderMaterializationReplicaFixtureCatalog? fixtures = null;
        try
        {
            var clusterId = await Program.ReadClusterIdAsync(elasticHttp).ConfigureAwait(false);
            fixtures = FreightOrderMaterializationReplicaFixtureCatalog.Create(
                options: options,
                postgresDataSource: dataSource);
            var admission = new MaterializationIndexSyncAdmissionGate();
            var builder = ImmutableDictionary.CreateBuilder<string, FreightOrderRebuildProviderRuntime>(
                StringComparer.Ordinal);
            foreach (var fixture in fixtures.Fixtures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = fixture.Dialect.Provider;
                var providerPlan = Program.CreateProviderPlan(fixture.Dialect, semantics);
                var targetBinding = Program.CreateTargetBinding(name, semantics, clusterId);
                await Program.EnsureLocalElasticTemplatesAsync(elasticHttp, targetBinding, name)
                    .ConfigureAwait(false);
                var target = Program.CreateTarget(
                    binding: targetBinding,
                    endpoint: options.ElasticsearchEndpoint,
                    faultPlan: MaterializationHarnessElasticFaultPlan.FromEnvironment(
                        provider: name,
                        readAlias: targetBinding.ReadAlias));
                var compilation = await fixture.CompileAsync(
                        semantics: semantics,
                        plan: providerPlan,
                        target: target,
                        journal: journal,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
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
                fixtures: fixtures);
        }
        catch
        {
            if (fixtures is not null)
                await fixtures.DisposeAsync().ConfigureAwait(false);
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

    /// <summary>Reads the concrete indexes currently published through one provider's stable read alias.</summary>
    /// <param name="provider">Stable provider name.</param>
    /// <param name="cancellationToken">Cancellation token for the Elasticsearch alias inspection.</param>
    /// <returns>Concrete index names in ordinal order, or an empty result when the alias is absent.</returns>
    /// <exception cref="ArgumentException"><paramref name="provider"/> is empty.</exception>
    /// <exception cref="KeyNotFoundException">The provider is not configured.</exception>
    /// <exception cref="HttpRequestException">Elasticsearch rejects the alias inspection.</exception>
    /// <exception cref="JsonException">Elasticsearch returns invalid alias evidence.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<ImmutableArray<string>> ReadAliasIndicesAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        var runtime = GetProvider(provider);
        using var response = await elasticHttp.GetAsync(
                $"/_alias/{Uri.EscapeDataString(runtime.TargetBinding.ReadAlias)}",
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("An Elasticsearch alias inspection returned a non-object response.");
        return
        [
            .. document.RootElement.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>Reads visible item identities, versions, and logical values through one provider's active alias.</summary>
    /// <param name="provider">Stable provider name.</param>
    /// <param name="cancellationToken">Cancellation token for the Elasticsearch read.</param>
    /// <returns>Visible items in canonical item-identity order.</returns>
    /// <exception cref="ArgumentException"><paramref name="provider"/> is empty.</exception>
    /// <exception cref="KeyNotFoundException">The provider is not configured.</exception>
    /// <exception cref="HttpRequestException">Elasticsearch rejects the read.</exception>
    /// <exception cref="JsonException">Elasticsearch returns an invalid materialization item envelope.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<ImmutableArray<FreightOrderMaterializedItem>> ReadMaterializedItemsAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        var runtime = GetProvider(provider);
        using var response = await elasticHttp.GetAsync(
                $"/{Uri.EscapeDataString(runtime.TargetBinding.ReadAlias)}/_search?size=100&filter_path=hits.hits._source",
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var items = ImmutableArray.CreateBuilder<FreightOrderMaterializedItem>();
        foreach (var hit in document.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            var metadata = source.GetProperty(ElasticMaterializationTargetBinding.MetadataField);
            if (metadata.GetProperty("deleted").GetBoolean())
                continue;
            items.Add(new(
                ItemId: new(metadata.GetProperty("itemId").GetString()
                    ?? throw new JsonException("A materialization item omitted its item identity.")),
                Version: new(metadata.GetProperty("version").GetInt64().ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
                Value: ObservationValue.FromJsonElement(source.GetProperty(ElasticMaterializationTargetBinding.ValueField))));
        }
        items.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.ItemId.Value, right.ItemId.Value));
        return items.ToImmutable();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await fixtures.DisposeAsync().ConfigureAwait(false);
        elasticHttp.Dispose();
    }
}
