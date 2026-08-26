using System.Text.Json;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage.Seeding;

/// <summary>
/// Describes a batch of JSON entity states to seed through registered observation repositories.
/// </summary>
public sealed record RepositorySeedPlan
{
    /// <summary>
    /// Creates an empty repository seed plan.
    /// </summary>
    public RepositorySeedPlan()
    {
    }

    /// <summary>
    /// Creates a repository seed plan.
    /// </summary>
    /// <param name="items">Inline entity states to persist.</param>
    /// <param name="skipExisting">When true, existing entities are left unchanged.</param>
    /// <param name="sources">Registered source/catalog requests to resolve and persist.</param>
    public RepositorySeedPlan(
        IReadOnlyList<RepositorySeedItem> items,
        bool skipExisting = false,
        IReadOnlyList<RepositorySeedSourceRequest>? sources = null
        )
    {
        Items = items ?? [];
        SkipExisting = skipExisting;
        Sources = sources ?? [];
    }

    /// <summary>
    /// Inline entity states to persist.
    /// </summary>
    public IReadOnlyList<RepositorySeedItem> Items { get; init; } = [];

    /// <summary>
    /// When true, existing entities are left unchanged.
    /// </summary>
    public bool SkipExisting { get; init; }

    /// <summary>
    /// Registered source/catalog requests to resolve and persist.
    /// </summary>
    public IReadOnlyList<RepositorySeedSourceRequest> Sources { get; init; } = [];
}

/// <summary>
/// Request to seed entity states from a registered source/catalog instead of inline state payloads.
/// </summary>
/// <param name="SourceId">Registered source identifier.</param>
/// <param name="Keys">Optional source-specific sample or asset keys.</param>
/// <param name="Parameters">Optional source-specific parameters.</param>
public sealed record RepositorySeedSourceRequest(
    string SourceId,
    IReadOnlyList<string>? Keys = null,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null
    );

/// <summary>
/// Resolves fixed or generated repository seed items by source-specific request.
/// </summary>
public interface IRepositorySeedSource
{
    /// <summary>
    /// Stable source identifier used by repository seed plans.
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// Resolves source-specific seed items.
    /// </summary>
    ValueTask<IReadOnlyList<RepositorySeedStateItem>> Resolve(
        OperationContext context,
        RepositorySeedSourceRequest request
        );
}

/// <summary>
/// Describes one JSON entity state to seed into the repository selected by <see cref="Type"/>.
/// </summary>
/// <param name="Type">Entity type or configured alias used to select the target repository.</param>
/// <param name="Id">Stable entity identifier.</param>
/// <param name="State">JSON object containing entity field values.</param>
/// <param name="Version">Optional observation version to write into the state snapshot.</param>
/// <param name="ExpectedConcurrencyToken">Optional optimistic-concurrency token required for replacement.</param>
/// <param name="PartitionKey">Optional partition key used when checking whether the entity already exists.</param>
public sealed record RepositorySeedItem(
    string Type,
    string Id,
    JsonElement State,
    long? Version = null,
    string? ExpectedConcurrencyToken = null,
    string? PartitionKey = null
    );

/// <summary>
/// Describes one CLR/object entity state to seed into the repository selected by <see cref="Type"/>.
/// </summary>
/// <param name="Type">Entity type or configured alias used to select the target repository.</param>
/// <param name="Id">Stable entity identifier.</param>
/// <param name="State">Object or property bag containing entity field values.</param>
/// <param name="Version">Optional observation version to write into the state snapshot.</param>
/// <param name="ExpectedConcurrencyToken">Optional optimistic-concurrency token required for replacement.</param>
/// <param name="PartitionKey">Optional partition key used when checking whether the entity already exists.</param>
public sealed record RepositorySeedStateItem(
    string Type,
    string Id,
    object State,
    long? Version = null,
    EntityConcurrencyToken? ExpectedConcurrencyToken = null,
    string? PartitionKey = null
    );

/// <summary>
/// Binds a seed type and aliases to an entity repository.
/// </summary>
public sealed class GenericRepositorySeedBinding
{
    /// <summary>
    /// Creates a repository seed binding.
    /// </summary>
    /// <param name="type">Primary seed type.</param>
    /// <param name="repository">Target entity repository.</param>
    /// <param name="aliases">Additional seed type aliases.</param>
    public GenericRepositorySeedBinding(
        string type,
        IEntityRepository repository,
        IReadOnlyList<string>? aliases = null
        )
    {
        Type = Guard.RequireNotNullOrWhiteSpace(type);
        Repository = Guard.RequireNotNull(repository);
        Aliases = aliases ?? [];
    }

    /// <summary>
    /// Primary seed type.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Target entity repository.
    /// </summary>
    public IEntityRepository Repository { get; }

    /// <summary>
    /// Additional seed type aliases.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>
    /// Creates a binding for the supplied entity definition and repository.
    /// </summary>
    public static GenericRepositorySeedBinding For(
        EntityDefinition entity,
        IEntityRepository repository,
        params string[] aliases
        )
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(repository);

        return new(
            type: entity.Name.Value,
            repository: repository,
            aliases:
            [
                entity.Shape.Id.Value,
                .. aliases.WhereNotNullOrWhiteSpace()
            ]
        );
    }
}

/// <summary>
/// Seeds arbitrary entity repositories by dispatching seed items on a semantic type or alias.
/// </summary>
public sealed class GenericRepositorySeedDataService
{
    readonly Dictionary<string, GenericRepositorySeedBinding> bindingsByType = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IRepositorySeedSource> sourcesById = new(StringComparer.OrdinalIgnoreCase);
    readonly RepositorySeedWriter seedWriter;

    /// <summary>
    /// Creates a generic repository seeder.
    /// </summary>
    public GenericRepositorySeedDataService(
        IReadOnlyList<GenericRepositorySeedBinding> bindings,
        RepositorySeedWriter seedWriter,
        IReadOnlyList<IRepositorySeedSource>? seedSources = null
        )
    {
        ArgumentNullException.ThrowIfNull(bindings);
        this.seedWriter = Guard.RequireNotNull(seedWriter);

        foreach (var binding in bindings)
            Register(binding);

        if (seedSources is null)
            return;

        foreach (var source in seedSources)
            Register(source);
    }

    /// <summary>
    /// Registered seed type aliases.
    /// </summary>
    public IReadOnlyCollection<string> SupportedTypes => bindingsByType.Keys;

    /// <summary>
    /// Registered source/catalog identifiers.
    /// </summary>
    public IReadOnlyCollection<string> SupportedSources => sourcesById.Keys;

    /// <summary>
    /// Seeds all JSON-state items in the supplied plan through repositories resolved from their seed types.
    /// </summary>
    public async Task<RepositorySeedResult> Seed(OperationContext context, RepositorySeedPlan plan)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.ThrowIfCancellationRequested();

        if (plan.Items.Count == 0 && plan.Sources.Count == 0)
            return new([]);

        List<RepositorySeedWrite> writes = new(plan.Items.Count);
        foreach (var item in plan.Items)
            writes.Add(CreateWrite(item));

        foreach (var sourceRequest in plan.Sources)
        {
            var sourceItems = await ResolveSource(context, sourceRequest).ConfigureAwait(false);
            foreach (var item in sourceItems)
                writes.Add(CreateWrite(item));
        }

        return await seedWriter.Seed(context, writes, new(SkipExisting: plan.SkipExisting)).ConfigureAwait(false);
    }

    async ValueTask<IReadOnlyList<RepositorySeedStateItem>> ResolveSource(
        OperationContext context,
        RepositorySeedSourceRequest request
        )
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceId = Guard.RequireNotNullOrWhiteSpace(request.SourceId);
        if (!sourcesById.TryGetValue(sourceId, out var source))
            throw new RepositorySeedException($"No repository seed source is registered for source '{sourceId}'.");

        return await source.Resolve(context, request).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds all object-state items through repositories resolved from their seed types.
    /// </summary>
    public async Task<RepositorySeedResult> Seed(
        OperationContext context,
        IReadOnlyList<RepositorySeedStateItem> items,
        RepositorySeedWriteOptions? options = null
        )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(items);
        context.ThrowIfCancellationRequested();

        if (items.Count == 0)
            return new([]);

        List<RepositorySeedWrite> writes = new(items.Count);
        foreach (var item in items)
            writes.Add(CreateWrite(item));

        return await seedWriter.Seed(context, writes, options).ConfigureAwait(false);
    }

    RepositorySeedWrite CreateWrite(RepositorySeedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var type = Guard.RequireNotNullOrWhiteSpace(item.Type);
        _ = Guard.RequireNotNullOrWhiteSpace(item.Id);
        if (!bindingsByType.TryGetValue(type, out var binding))
            throw new RepositorySeedException($"No entity repository seed binding is registered for type '{type}'.");

        var repository = binding.Repository;
        var state = CreateState(repository.EntityDefinition, item);
        EntityConcurrencyToken? expectedConcurrencyToken = string.IsNullOrWhiteSpace(item.ExpectedConcurrencyToken)
            ? null
            : new EntityConcurrencyToken(item.ExpectedConcurrencyToken);

        return new(
            Type: type,
            Repository: repository,
            Write: new(state.Snapshot, expectedConcurrencyToken),
            ExistingReadOptions: CreateExistingReadOptions(item.PartitionKey)
            );
    }

    RepositorySeedWrite CreateWrite(RepositorySeedStateItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var type = Guard.RequireNotNullOrWhiteSpace(item.Type);
        _ = Guard.RequireNotNullOrWhiteSpace(item.Id);
        ArgumentNullException.ThrowIfNull(item.State);

        if (!bindingsByType.TryGetValue(type, out var binding))
            throw new RepositorySeedException($"No entity repository seed binding is registered for type '{type}'.");

        var repository = binding.Repository;
        var state = repository.EntityDefinition.CreateState(entityId: item.Id, stateObject: item.State, version: item.Version ?? 0);
        return new(
            Type: type,
            Repository: repository,
            Write: new(state.Snapshot, item.ExpectedConcurrencyToken),
            ExistingReadOptions: CreateExistingReadOptions(item.PartitionKey)
            );
    }

    static EntityReadOptions? CreateExistingReadOptions(string? partitionKey) =>
        string.IsNullOrWhiteSpace(partitionKey)
            ? null
            : EntityReadOptions.Full.WithPartitionKey(partitionKey.Trim());

    static EntityState CreateState(EntityDefinition entity, RepositorySeedItem item)
    {
        if (item.State.ValueKind is not JsonValueKind.Object)
            throw new RepositorySeedException($"Seed state for '{item.Type}:{item.Id}' must be a JSON object.");

        var value = ObservationValue.FromJsonElement(item.State);
        var fields = value.Fields ?? throw new RepositorySeedException($"Seed state for '{item.Type}:{item.Id}' must be a JSON object.");
        return entity.CreateState(entityId: item.Id, fields, version: item.Version ?? 0);
    }

    void Register(GenericRepositorySeedBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        RegisterAlias(binding.Type, binding);
        RegisterAlias(binding.Repository.EntityDefinition.Name.Value, binding);
        RegisterAlias(binding.Repository.EntityDefinition.Shape.Id.Value, binding);

        foreach (var alias in binding.Aliases)
            RegisterAlias(alias, binding);
    }

    void RegisterAlias(string? type, GenericRepositorySeedBinding binding)
    {
        if (string.IsNullOrWhiteSpace(type))
            return;

        var normalized = type.Trim();
        if (bindingsByType.TryGetValue(normalized, out var existing)
            && !ReferenceEquals(existing.Repository, binding.Repository))
        {
            throw new InvalidOperationException(
                $"Seed type alias '{normalized}' is already bound to entity '{existing.Repository.EntityDefinition.Name.Value}'.");
        }

        bindingsByType[normalized] = binding;
    }

    void Register(IRepositorySeedSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sourceId = Guard.RequireNotNullOrWhiteSpace(source.SourceId);
        if (!sourcesById.TryAdd(sourceId, source))
            throw new InvalidOperationException($"Repository seed source '{sourceId}' is already registered.");
    }
}
