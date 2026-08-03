using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>
/// Options for binding declared API operations to an entity repository.
/// </summary>
public sealed class EntityApiEndpointOptions
{
    const string ConventionalActivationPrefix = "aspnet/request";

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
    /// Creates the canonical Transition activation identity for a request. The default combines the ASP.NET request
    /// trace identity with the stable declared endpoint identity.
    /// </summary>
    public Func<HttpContext, ApiOperation, ActivationId> ActivationIdSelector { get; init; } =
        CreateConventionalActivationId;

    /// <summary>
    /// Resolves the base entity repository. Defaults to the standard shape-keyed repository registration.
    /// </summary>
    public Func<IServiceProvider, EntityDefinition, IEntityRepository> RepositoryResolver { get; init; } =
        static (sp, entity) => sp.GetEntityRepository(entity);

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

    /// <summary>Creates the conventional canonical Transition activation identity for one HTTP request.</summary>
    /// <param name="httpContext">Current HTTP request, whose trace identity scopes the activation.</param>
    /// <param name="operation">Declared operation, whose endpoint identity distinguishes activations in one request.</param>
    /// <returns>A deterministic identity that is stable for the operation within the current request.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request trace identity is empty or white space.</exception>
    public static ActivationId CreateConventionalActivationId(HttpContext httpContext, ApiOperation operation)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(operation);
        var traceIdentifier = Guard.RequireNotNullOrWhiteSpace(httpContext.TraceIdentifier);
        return new(
            $"{ConventionalActivationPrefix}/{Uri.EscapeDataString(traceIdentifier)}/operation/{Uri.EscapeDataString(operation.Id.Value)}");
    }

    internal ActivationId CreateActivationId(HttpContext httpContext, ApiOperation operation)
    {
        var activation = ActivationIdSelector(httpContext, operation);
        return string.IsNullOrWhiteSpace(activation.Value)
            ? throw new InvalidOperationException("Entity API endpoint options produced an empty Transition activation identity.")
            : activation;
    }

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
    /// <param name="operationName">Declared API operation name.</param>
    /// <param name="plan">Compiled exact canonical Transition plan referenced by the API operation.</param>
    /// <param name="createTransitionInput">Optional projection from HTTP request data to canonical Transition input.</param>
    /// <param name="createResult">Required projection from commit context and effective snapshot to an HTTP result.</param>
    /// <param name="getExpectedConcurrencyToken">Optional expected-concurrency override.</param>
    /// <param name="createOutboxMessages">
    /// Optional explicit projection of canonical emission intents and application messages into the entity outbox.
    /// </param>
    /// <returns>A binding that interprets <paramref name="plan"/> and commits its decision.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="createResult"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="operationName"/> is empty or white space.</exception>
    public static EntityApiOperationBinding Transition(
        string operationName,
        CompiledTransitionPlan plan,
        Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages = null
        ) =>
        new TransitionEntityApiOperationBinding(
            operationName,
            plan,
            createTransitionInput,
            createResult,
            getExpectedConcurrencyToken,
            createOutboxMessages
            );

    /// <summary>
    /// Creates an entity transition operation binding.
    /// </summary>
    /// <param name="endpoint">Declared endpoint to bind.</param>
    /// <param name="plan">Compiled exact canonical Transition plan referenced by the API operation.</param>
    /// <param name="createTransitionInput">Optional projection from HTTP request data to canonical Transition input.</param>
    /// <param name="createResult">Required projection from commit context and effective snapshot to an HTTP result.</param>
    /// <param name="getExpectedConcurrencyToken">Optional expected-concurrency override.</param>
    /// <param name="createOutboxMessages">
    /// Optional explicit projection of canonical emission intents and application messages into the entity outbox.
    /// </param>
    /// <returns>A binding that interprets <paramref name="plan"/> and commits its decision.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="endpoint"/>, <paramref name="plan"/>, or <paramref name="createResult"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static EntityApiOperationBinding Transition(
        ApiEndpoint endpoint,
        CompiledTransitionPlan plan,
        Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages = null
        ) =>
        new TransitionEntityApiOperationBinding(
            endpoint,
            plan,
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
/// Commit context passed to create and transition response/outbox mappers.
/// </summary>
/// <param name="OperationContext">Current Cohesive operation context.</param>
/// <param name="HttpContext">Current ASP.NET request context.</param>
/// <param name="Operation">Declared API operation.</param>
/// <param name="Entity">Semantic entity definition.</param>
/// <param name="Repository">Resolved entity repository.</param>
/// <param name="EntityId">Stable entity identity.</param>
/// <param name="Request">Bound request payload, when present.</param>
/// <param name="OldSnapshot">Snapshot loaded before the operation, or <see langword="null"/> for creates.</param>
/// <param name="NewState">Candidate state committed or exposed to the result mapper.</param>
/// <param name="Decision">Canonical Transition decision, or <see langword="null"/> for creates.</param>
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
    TransitionDecision? Decision
    );
