using Cohesive.Api;
using Cohesive.Relations.Queries;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.AspNet.Relations;

/// <summary>
/// Options for binding declared API operations to Cohesive relation queries.
/// </summary>
public sealed class RelationQueryApiEndpointOptions
{
    readonly Dictionary<ApiEndpointId, RelationQueryApiOperationBinding> bindingsByEndpointId = new();
    readonly Dictionary<string, RelationQueryApiOperationBinding> bindingsByOperationName = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional operation selector used when a shared API definition contains multiple query surfaces
    /// with reused logical operation names.
    /// </summary>
    public Func<ApiOperation, bool>? OperationFilter { get; init; }

    /// <summary>
    /// Optional ASP.NET endpoint name selector. Defaults to the declared operation name.
    /// Use this when multiple mapped operations intentionally reuse the same logical operation name.
    /// </summary>
    public Func<ApiOperation, string>? EndpointNameSelector { get; init; }

    /// <summary>
    /// Resolves the read-repository registry used by executable relation queries.
    /// </summary>
    public Func<IServiceProvider, IReadRepositoryRegistry> RepositoryRegistryResolver { get; init; } =
        static services => services.GetRequiredService<IReadRepositoryRegistry>();

    /// <summary>
    /// Adds an operation binding. Only bound operations are mapped.
    /// </summary>
    public RelationQueryApiEndpointOptions Bind(RelationQueryApiOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.EndpointId is { } endpointId)
            bindingsByEndpointId[endpointId] = binding;
        else
            bindingsByOperationName[Guard.RequireNotNullOrWhiteSpace(binding.OperationName)] = binding;
        return this;
    }

    internal bool TryGetBinding(ApiOperation operation, out RelationQueryApiOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (bindingsByEndpointId.TryGetValue(operation.Id, out binding!))
            return true;

        return bindingsByOperationName.TryGetValue(operation.Name, out binding!);
    }
}

/// <summary>
/// Binding behavior for one declared relation-query API operation.
/// </summary>
public abstract class RelationQueryApiOperationBinding
{
    private protected RelationQueryApiOperationBinding(string operationName)
    {
        OperationName = Guard.RequireNotNullOrWhiteSpace(operationName);
    }

    private protected RelationQueryApiOperationBinding(ApiEndpoint endpoint)
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

    internal abstract Delegate CreateHandler(ApiOperation operation, RelationQueryApiEndpointOptions options);

    /// <summary>
    /// Creates a binding for a fixed executable relation query.
    /// </summary>
    public static RelationQueryApiOperationBinding Query(
        string operationName,
        IExecutableQuery query,
        Func<RelationQueryApiResultContext, object?, IResult>? createResult = null
        ) =>
        new ExecutableRelationQueryApiOperationBinding(
            operationName: operationName,
            (_, _) => ValueTask.FromResult(Guard.RequireNotNull(query)),
            ToResultFactory(createResult)
            );

    /// <summary>
    /// Creates a binding for a fixed executable relation query.
    /// </summary>
    public static RelationQueryApiOperationBinding Query(
        ApiEndpoint endpoint,
        IExecutableQuery query,
        Func<RelationQueryApiResultContext, object?, IResult>? createResult = null
        ) =>
        new ExecutableRelationQueryApiOperationBinding(
            endpoint: endpoint,
            (_, _) => ValueTask.FromResult(Guard.RequireNotNull(query)),
            ToResultFactory(createResult)
            );

    /// <summary>
    /// Creates a binding that translates the API request into an executable relation query.
    /// </summary>
    public static RelationQueryApiOperationBinding Query(
        string operationName,
        Func<RelationQueryApiRequestContext, object?, IExecutableQuery> createQuery,
        Func<RelationQueryApiResultContext, object?, IResult>? createResult = null
        ) =>
        new ExecutableRelationQueryApiOperationBinding(
            operationName: operationName,
            (context, request) => ValueTask.FromResult(createQuery(context, request)),
            ToResultFactory(createResult)
            );

    /// <summary>
    /// Creates a binding that translates the API request into an executable relation query.
    /// </summary>
    public static RelationQueryApiOperationBinding Query(
        ApiEndpoint endpoint,
        Func<RelationQueryApiRequestContext, object?, IExecutableQuery> createQuery,
        Func<RelationQueryApiResultContext, object?, IResult>? createResult = null
        ) =>
        new ExecutableRelationQueryApiOperationBinding(
            endpoint,
            (context, request) => ValueTask.FromResult(createQuery(context, request)),
            ToResultFactory(createResult)
            );

    /// <summary>
    /// Creates a binding that asynchronously translates the API request into an executable relation query.
    /// </summary>
    public static RelationQueryApiOperationBinding Query(
        string operationName,
        Func<RelationQueryApiRequestContext, object?, ValueTask<IExecutableQuery>> createQuery,
        Func<RelationQueryApiResultContext, object?, ValueTask<IResult>>? createResult = null
        ) =>
        new ExecutableRelationQueryApiOperationBinding(operationName, createQuery, createResult ?? DefaultCreateResultAsync);

    /// <summary>
    /// Creates a binding that asynchronously translates the API request into an executable relation query.
    /// </summary>
    public static RelationQueryApiOperationBinding Query(
        ApiEndpoint endpoint,
        Func<RelationQueryApiRequestContext, object?, ValueTask<IExecutableQuery>> createQuery,
        Func<RelationQueryApiResultContext, object?, ValueTask<IResult>>? createResult = null
        ) =>
        new ExecutableRelationQueryApiOperationBinding(endpoint, createQuery, createResult ?? DefaultCreateResultAsync);

    /// <summary>
    /// Creates a strongly typed binding that translates the API request into an executable relation query.
    /// </summary>
    public static RelationQueryApiOperationBinding Query<TResult>(
        string operationName,
        Func<RelationQueryApiRequestContext, object?, ExecutableQuery<TResult>> createQuery,
        Func<RelationQueryApiResultContext, TResult, IResult> createResult
        ) =>
        new ExecutableRelationQueryApiOperationBinding(
            operationName,
            (context, request) => ValueTask.FromResult<IExecutableQuery>(createQuery(context, request)),
            (context, result) => ValueTask.FromResult(createResult(context, (TResult)result!))
            );

    /// <summary>
    /// Creates a strongly typed binding that translates the API request into an executable relation query.
    /// </summary>
    public static RelationQueryApiOperationBinding Query<TResult>(
        ApiEndpoint endpoint,
        Func<RelationQueryApiRequestContext, object?, ExecutableQuery<TResult>> createQuery,
        Func<RelationQueryApiResultContext, TResult, IResult> createResult
        ) =>
        new ExecutableRelationQueryApiOperationBinding(
            endpoint,
            (context, request) => ValueTask.FromResult<IExecutableQuery>(createQuery(context, request)),
            (context, result) => ValueTask.FromResult(createResult(context, (TResult)result!))
            );

    static Func<RelationQueryApiResultContext, object?, ValueTask<IResult>> ToResultFactory(Func<RelationQueryApiResultContext, object?, IResult>? createResult) =>
        createResult is null
            ? DefaultCreateResultAsync
            : (context, result) => ValueTask.FromResult(createResult(context, result));

    static ValueTask<IResult> DefaultCreateResultAsync(RelationQueryApiResultContext _, object? result) =>
        ValueTask.FromResult(Results.Ok(result));
}

/// <summary>
/// Request context supplied to relation-query factories.
/// </summary>
public sealed record RelationQueryApiRequestContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    IReadRepositoryRegistry RepositoryRegistry
);

/// <summary>
/// Result context supplied to relation-query response mappers.
/// </summary>
public sealed record RelationQueryApiResultContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    IReadRepositoryRegistry RepositoryRegistry,
    object? Request,
    IExecutableQuery Query
);
