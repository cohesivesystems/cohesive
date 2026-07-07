using Cohesive.Api;
using Cohesive.Relations.Queries;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>
/// Options for binding declared API operations to an entity repository.
/// </summary>
public sealed class EntityApiEndpointOptions
{
    readonly Dictionary<ApiEndpointId, EntityApiOperationBinding> bindingsByEndpointId = new();
    readonly Dictionary<string, EntityApiOperationBinding> bindingsByOperationName = new(StringComparer.Ordinal);

    /// <summary>
    /// Semantic entity served by this API binding.
    /// </summary>
    public required EntityDefinition Entity { get; init; }

    /// <summary>
    /// Route value name used for entity id lookups and transition targets.
    /// </summary>
    public string EntityIdRouteParameter { get; init; } = "id";

    /// <summary>
    /// Optional operation selector used when a shared API definition contains multiple entity surfaces
    /// with reused logical operation names.
    /// </summary>
    public Func<ApiOperation, bool>? OperationFilter { get; init; }

    /// <summary>
    /// Optional ASP.NET endpoint name selector. Defaults to the declared operation name.
    /// Use this when multiple mapped operations intentionally reuse the same logical operation name.
    /// </summary>
    public Func<ApiOperation, string>? EndpointNameSelector { get; init; }

    /// <summary>
    /// Resolves the base entity repository. Defaults to the standard shape-keyed repository registration.
    /// </summary>
    public Func<IServiceProvider, EntityDefinition, IEntityRepository> RepositoryResolver { get; init; } =
        static (sp, entity) => sp.GetEntityRepository(entity);

    /// <summary>
    /// Resolves the entity query repository. Defaults to the standard shape-keyed query repository registration.
    /// </summary>
    public Func<IServiceProvider, EntityDefinition, IEntityQueryRepository> QueryRepositoryResolver { get; init; } =
        static (sp, entity) => sp.GetEntityQueryRepository(entity);

    /// <summary>
    /// Resolves the outbox-capable entity repository. Defaults to the standard shape-keyed outbox repository registration.
    /// </summary>
    public Func<IServiceProvider, EntityDefinition, IEntityOutboxRepository> OutboxRepositoryResolver { get; init; } =
        static (sp, entity) => sp.GetEntityOutboxRepository(entity);

    /// <summary>
    /// Optional repository partition policy used to resolve exact partition keys for entity point reads and transition loads.
    /// </summary>
    public EntityPartitionKeyPolicy? PartitionKeyPolicy { get; init; }

    /// <summary>
    /// Optional repository partition policy resolver used to defer policy selection to request services.
    /// Ignored when <see cref="PartitionKeyPolicy"/> is configured.
    /// </summary>
    public Func<IServiceProvider, EntityDefinition, EntityPartitionKeyPolicy?>? PartitionKeyPolicyResolver { get; init; }

    /// <summary>
    /// Optional legacy partition-key resolver for entity point reads and transition loads.
    /// Takes precedence over <see cref="PartitionKeyPolicy"/> and <see cref="PartitionKeyPolicyResolver"/> when configured.
    /// </summary>
    public Func<EntityApiRequestContext, string?>? ReadPartitionKeyResolver { get; init; }

    internal EntityReadOptions? ResolveReadOptions(EntityApiRequestContext context, EntityReadOptions? readOptions)
    {
        ArgumentNullException.ThrowIfNull(context);
        var partitionKey = ResolveReadPartitionKey(context);
        return string.IsNullOrWhiteSpace(partitionKey)
            ? readOptions
            : (readOptions ?? EntityReadOptions.Full).WithPartitionKey(partitionKey);
    }

    string? ResolveReadPartitionKey(EntityApiRequestContext context)
    {
        if (NormalizePartitionKey(ReadPartitionKeyResolver?.Invoke(context)) is { } explicitPartitionKey)
            return explicitPartitionKey;

        return string.IsNullOrWhiteSpace(context.EntityId)
            ? null
            : ResolvePartitionKeyPolicy(context)?.TryResolvePointReadPartitionKey(context.OperationContext, context.EntityId);
    }

    EntityPartitionKeyPolicy? ResolvePartitionKeyPolicy(EntityApiRequestContext context) =>
        PartitionKeyPolicy ?? PartitionKeyPolicyResolver?.Invoke(context.HttpContext.RequestServices, Entity);

    static string? NormalizePartitionKey(string? partitionKey) =>
        string.IsNullOrWhiteSpace(partitionKey) ? null : partitionKey.Trim();

    /// <summary>
    /// Adds an operation binding. Only bound operations are mapped.
    /// </summary>
    public EntityApiEndpointOptions Bind(EntityApiOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.EndpointId is { } endpointId)
            bindingsByEndpointId[endpointId] = binding;
        else
            bindingsByOperationName[Guard.RequireNotNullOrWhiteSpace(binding.OperationName)] = binding;

        return this;
    }

    internal bool TryGetBinding(ApiOperation operation, out EntityApiOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (bindingsByEndpointId.TryGetValue(operation.Id, out binding!))
            return true;

        return bindingsByOperationName.TryGetValue(operation.Name, out binding!);
    }
}

/// <summary>
/// Binding behavior for one declared API operation.
/// </summary>
public abstract class EntityApiOperationBinding
{
    private protected EntityApiOperationBinding(string operationName)
    {
        OperationName = Guard.RequireNotNullOrWhiteSpace(operationName);
    }

    private protected EntityApiOperationBinding(ApiEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EndpointId = endpoint.Id;
        OperationName = endpoint.Name;
    }

    /// <summary>
    /// Declared endpoint id, when the binding targets an endpoint handle.
    /// </summary>
    public ApiEndpointId? EndpointId { get; }

    /// <summary>
    /// Declared API operation name.
    /// </summary>
    public string OperationName { get; }

    internal abstract Delegate CreateHandler(ApiOperation operation, EntityApiEndpointOptions options);

    /// <summary>
    /// Creates a read-by-id operation binding.
    /// </summary>
    public static EntityApiOperationBinding Get(string operationName, Func<EntityApiLoadedContext, EntitySnapshot, IResult> createResult, EntityReadOptions? readOptions = null) =>
        new GetEntityApiOperationBinding(operationName, createResult, readOptions);

    /// <summary>
    /// Creates a read-by-id operation binding.
    /// </summary>
    public static EntityApiOperationBinding Get(ApiEndpoint endpoint, Func<EntityApiLoadedContext, EntitySnapshot, IResult> createResult, EntityReadOptions? readOptions = null) =>
        new GetEntityApiOperationBinding(endpoint, createResult, readOptions);

    /// <summary>
    /// Creates a read-by-id operation binding with asynchronous response projection.
    /// </summary>
    public static EntityApiOperationBinding Get(string operationName, Func<EntityApiLoadedContext, EntitySnapshot, ValueTask<IResult>> createResult, EntityReadOptions? readOptions = null) =>
        new GetEntityApiOperationBinding(operationName, createResult, readOptions);

    /// <summary>
    /// Creates a read-by-id operation binding with asynchronous response projection.
    /// </summary>
    public static EntityApiOperationBinding Get(ApiEndpoint endpoint, Func<EntityApiLoadedContext, EntitySnapshot, ValueTask<IResult>> createResult, EntityReadOptions? readOptions = null) =>
        new GetEntityApiOperationBinding(endpoint, createResult, readOptions);

    /// <summary>
    /// Creates a read-by-id operation binding that also exposes the operation request payload.
    /// </summary>
    public static EntityApiOperationBinding Load(string operationName, Func<EntityApiLoadedRequestContext, EntitySnapshot, object?, IResult> createResult) =>
        new LoadEntityApiOperationBinding(operationName, (context, snapshot, request) => ValueTask.FromResult(createResult(context, snapshot, request)));

    /// <summary>
    /// Creates a read-by-id operation binding that also exposes the operation request payload.
    /// </summary>
    public static EntityApiOperationBinding Load(ApiEndpoint endpoint, Func<EntityApiLoadedRequestContext, EntitySnapshot, object?, IResult> createResult) =>
        new LoadEntityApiOperationBinding(endpoint, (context, snapshot, request) => ValueTask.FromResult(createResult(context, snapshot, request)));

    /// <summary>
    /// Creates a read-by-id operation binding that also exposes the operation request payload.
    /// </summary>
    public static EntityApiOperationBinding Load(string operationName, Func<EntityApiLoadedRequestContext, EntitySnapshot, object?, ValueTask<IResult>> createResult) =>
        new LoadEntityApiOperationBinding(operationName, createResult);

    /// <summary>
    /// Creates a read-by-id operation binding that also exposes the operation request payload.
    /// </summary>
    public static EntityApiOperationBinding Load(ApiEndpoint endpoint, Func<EntityApiLoadedRequestContext, EntitySnapshot, object?, ValueTask<IResult>> createResult) =>
        new LoadEntityApiOperationBinding(endpoint, createResult);

    /// <summary>
    /// Creates an entity query operation binding.
    /// </summary>
    public static EntityApiOperationBinding Query(string operationName, Func<EntityApiRequestContext, object?, EntityQuery> createQuery, Func<EntityApiQueryResultContext, IReadOnlyList<EntitySnapshot>, IResult> createResult) =>
        new QueryEntityApiOperationBinding(operationName, createQuery, createResult);

    /// <summary>
    /// Creates an entity query operation binding.
    /// </summary>
    public static EntityApiOperationBinding Query(ApiEndpoint endpoint, Func<EntityApiRequestContext, object?, EntityQuery> createQuery, Func<EntityApiQueryResultContext, IReadOnlyList<EntitySnapshot>, IResult> createResult) =>
        new QueryEntityApiOperationBinding(endpoint, createQuery, createResult);

    /// <summary>
    /// Creates an entity query operation binding with asynchronous response projection.
    /// </summary>
    public static EntityApiOperationBinding Query(string operationName, Func<EntityApiRequestContext, object?, EntityQuery> createQuery, Func<EntityApiQueryResultContext, IReadOnlyList<EntitySnapshot>, ValueTask<IResult>> createResult) =>
        new QueryEntityApiOperationBinding(operationName, createQuery, createResult);

    /// <summary>
    /// Creates an entity query operation binding with asynchronous response projection.
    /// </summary>
    public static EntityApiOperationBinding Query(ApiEndpoint endpoint, Func<EntityApiRequestContext, object?, EntityQuery> createQuery, Func<EntityApiQueryResultContext, IReadOnlyList<EntitySnapshot>, ValueTask<IResult>> createResult) =>
        new QueryEntityApiOperationBinding(endpoint, createQuery, createResult);

    /// <summary>
    /// Creates an entity create operation binding.
    /// </summary>
    public static EntityApiOperationBinding Create(
        string operationName,
        Func<EntityApiRequestContext, object?, EntityState> createState,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages = null
        ) =>
        new CreateEntityApiOperationBinding(operationName, createState, createResult, getExpectedConcurrencyToken, createOutboxMessages);

    /// <summary>
    /// Creates an entity create operation binding.
    /// </summary>
    public static EntityApiOperationBinding Create(
        ApiEndpoint endpoint,
        Func<EntityApiRequestContext, object?, EntityState> createState,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages = null
        ) =>
        new CreateEntityApiOperationBinding(endpoint, createState, createResult, getExpectedConcurrencyToken, createOutboxMessages);

    /// <summary>
    /// Creates an entity transition operation binding.
    /// </summary>
    public static EntityApiOperationBinding Transition(
        string operationName,
        string transitionName,
        Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages = null
        ) =>
        new TransitionEntityApiOperationBinding(
            operationName,
            transitionName,
            createTransitionInput,
            createResult,
            getExpectedConcurrencyToken,
            createOutboxMessages
            );

    /// <summary>
    /// Creates an entity transition operation binding.
    /// </summary>
    public static EntityApiOperationBinding Transition(
        ApiEndpoint endpoint,
        string transitionName,
        Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages = null
        ) =>
        new TransitionEntityApiOperationBinding(
            endpoint,
            transitionName,
            createTransitionInput,
            createResult,
            getExpectedConcurrencyToken,
            createOutboxMessages
            );
}

/// <summary>
/// Request context passed to entity API operation bindings.
/// </summary>
public sealed record EntityApiRequestContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    EntityDefinition Entity,
    IEntityRepository Repository,
    string? EntityId
    )
{
    /// <summary>
    /// Loaded snapshot for read and transition operations, when available.
    /// </summary>
    public EntitySnapshot? Snapshot { get; init; }
}

/// <summary>
/// Loaded entity context passed to read result mappers.
/// </summary>
public sealed record EntityApiLoadedContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    EntityDefinition Entity,
    IEntityRepository Repository,
    string EntityId
    );

/// <summary>
/// Loaded entity context passed to read-with-request result mappers.
/// </summary>
public sealed record EntityApiLoadedRequestContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    EntityDefinition Entity,
    IEntityRepository Repository,
    string EntityId,
    object? Request
    );

/// <summary>
/// Query result context passed to query response mappers.
/// </summary>
public sealed record EntityApiQueryResultContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    EntityDefinition Entity,
    IEntityQueryRepository Repository,
    object? Request
    );

/// <summary>
/// Commit context passed to create and transition response/outbox mappers.
/// </summary>
public sealed record EntityApiCommitContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    EntityDefinition Entity,
    IEntityRepository Repository,
    string EntityId,
    object? Request,
    EntitySnapshot? OldSnapshot,
    EntityState NewState,
    TransitionResult? Transition
    );
