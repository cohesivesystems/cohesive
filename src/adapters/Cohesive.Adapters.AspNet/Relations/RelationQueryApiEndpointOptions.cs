using Cohesive.Api;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.AspNet.Relations;

/// <summary>Options for binding declared API operations to canonical relation/query evaluations.</summary>
public sealed class RelationQueryApiEndpointOptions
{
    const string ConventionalEvaluationPrefix = "aspnet/request";

    readonly Dictionary<ApiEndpointId, RelationQueryApiOperationBinding> bindingsByEndpointId = new();
    readonly Dictionary<string, RelationQueryApiOperationBinding> bindingsByOperationName = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional operation selector used when a shared API definition contains multiple relation/query surfaces
    /// with reused logical operation names.
    /// </summary>
    public Func<ApiOperation, bool>? OperationFilter { get; init; }

    /// <summary>
    /// Optional ASP.NET endpoint name selector. Defaults to the declared operation name. Use this when multiple
    /// mapped operations intentionally reuse the same logical operation name.
    /// </summary>
    public Func<ApiOperation, string>? EndpointNameSelector { get; init; }

    /// <summary>Resolves the request-scoped canonical relation/query evaluator.</summary>
    /// <remarks>The default resolves <see cref="IRelationQueryEvaluator"/> from request services.</remarks>
    public Func<IServiceProvider, IRelationQueryEvaluator> EvaluatorResolver { get; init; } =
        static services => services.GetRequiredService<IRelationQueryEvaluator>();

    /// <summary>
    /// Creates the evaluation identity exposed to the per-request evaluation factory. The default combines the
    /// ASP.NET request trace identity with the stable declared endpoint identity.
    /// </summary>
    public Func<HttpContext, ApiOperation, RelationQueryEvaluationId> EvaluationIdSelector { get; init; } =
        CreateConventionalEvaluationId;

    /// <summary>Adds an operation binding. Only bound operations are mapped.</summary>
    /// <param name="binding">Binding to associate with its endpoint identity or operation name.</param>
    /// <returns>This options instance for continued configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    public RelationQueryApiEndpointOptions Bind(RelationQueryApiOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.EndpointId is { } endpointId)
            bindingsByEndpointId[endpointId] = binding;
        else
            bindingsByOperationName[Guard.RequireNotNullOrWhiteSpace(binding.OperationName)] = binding;
        return this;
    }

    /// <summary>Creates the conventional evaluation identity for one HTTP request and declared operation.</summary>
    /// <param name="httpContext">Current HTTP request, whose trace identity scopes the evaluation.</param>
    /// <param name="operation">Declared operation, whose endpoint identity distinguishes evaluations in one request.</param>
    /// <returns>A deterministic identity that is stable for the operation within the current request.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request trace identity is empty or white space.</exception>
    public static RelationQueryEvaluationId CreateConventionalEvaluationId(
        HttpContext httpContext,
        ApiOperation operation)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(operation);
        var traceIdentifier = Guard.RequireNotNullOrWhiteSpace(httpContext.TraceIdentifier);
        return new(
            $"{ConventionalEvaluationPrefix}/{Uri.EscapeDataString(traceIdentifier)}/operation/{Uri.EscapeDataString(operation.Id.Value)}");
    }

    internal bool TryGetBinding(ApiOperation operation, out RelationQueryApiOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (bindingsByEndpointId.TryGetValue(operation.Id, out binding!))
            return true;

        return bindingsByOperationName.TryGetValue(operation.Name, out binding!);
    }

    internal IRelationQueryEvaluator ResolveEvaluator(IServiceProvider services) =>
        EvaluatorResolver(services)
        ?? throw new InvalidOperationException(
            "Relation/query API endpoint options resolved a null canonical evaluator.");

    internal RelationQueryEvaluationId CreateEvaluationId(HttpContext httpContext, ApiOperation operation)
    {
        var evaluation = EvaluationIdSelector(httpContext, operation);
        return string.IsNullOrWhiteSpace(evaluation.Value)
            ? throw new InvalidOperationException(
                "Relation/query API endpoint options produced an empty evaluation identity.")
            : evaluation;
    }
}

/// <summary>Binding behavior for one declared relation/query API operation.</summary>
public abstract class RelationQueryApiOperationBinding
{
    /// <summary>Creates a binding addressed by declared operation name.</summary>
    /// <param name="operationName">Non-empty declared operation name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operationName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="operationName"/> is empty or white space.</exception>
    private protected RelationQueryApiOperationBinding(string operationName)
    {
        OperationName = Guard.RequireNotNullOrWhiteSpace(operationName);
    }

    /// <summary>Creates a binding anchored to one declared endpoint.</summary>
    /// <param name="endpoint">Declared endpoint to bind.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    private protected RelationQueryApiOperationBinding(ApiEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EndpointId = endpoint.Id;
        OperationName = endpoint.Name;
    }

    /// <summary>Declared endpoint identity, when the binding targets an endpoint handle.</summary>
    public ApiEndpointId? EndpointId { get; }

    /// <summary>Declared API operation name.</summary>
    public string OperationName { get; }

    internal abstract Delegate CreateHandler(ApiOperation operation, RelationQueryApiEndpointOptions options);

    /// <summary>
    /// Creates a binding that authors a fresh canonical evaluation for every request and explicitly maps its
    /// complete evaluation outcome to an HTTP result.
    /// </summary>
    /// <param name="operationName">Name of the declared API operation to bind.</param>
    /// <param name="createEvaluation">
    /// Per-request factory that must assign <see cref="RelationQueryApiRequestContext.EvaluationId"/> to the
    /// returned evaluation.
    /// </param>
    /// <param name="createResult">Explicit projection from the complete canonical outcome to the HTTP response.</param>
    /// <returns>A relation/query operation binding.</returns>
    /// <exception cref="ArgumentNullException">A required argument or delegate is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="operationName"/> is empty or white space.</exception>
    public static RelationQueryApiOperationBinding Evaluate(
        string operationName,
        Func<RelationQueryApiRequestContext, object?, RelationQueryEvaluation> createEvaluation,
        Func<RelationQueryApiResultContext, RelationQueryEvaluationOutcome, IResult> createResult)
    {
        ArgumentNullException.ThrowIfNull(createEvaluation);
        ArgumentNullException.ThrowIfNull(createResult);
        return new RelationQueryEvaluationApiOperationBinding(
            operationName,
            (context, request) => ValueTask.FromResult(createEvaluation(context, request)),
            (context, outcome) => ValueTask.FromResult(createResult(context, outcome)));
    }

    /// <summary>
    /// Creates an endpoint-anchored binding that authors a fresh canonical evaluation for every request and
    /// explicitly maps its complete evaluation outcome to an HTTP result.
    /// </summary>
    /// <param name="endpoint">Declared endpoint handle to bind.</param>
    /// <param name="createEvaluation">
    /// Per-request factory that must assign <see cref="RelationQueryApiRequestContext.EvaluationId"/> to the
    /// returned evaluation.
    /// </param>
    /// <param name="createResult">Explicit projection from the complete canonical outcome to the HTTP response.</param>
    /// <returns>A relation/query operation binding.</returns>
    /// <exception cref="ArgumentNullException">A required argument or delegate is <see langword="null"/>.</exception>
    public static RelationQueryApiOperationBinding Evaluate(
        ApiEndpoint endpoint,
        Func<RelationQueryApiRequestContext, object?, RelationQueryEvaluation> createEvaluation,
        Func<RelationQueryApiResultContext, RelationQueryEvaluationOutcome, IResult> createResult)
    {
        ArgumentNullException.ThrowIfNull(createEvaluation);
        ArgumentNullException.ThrowIfNull(createResult);
        return new RelationQueryEvaluationApiOperationBinding(
            endpoint,
            (context, request) => ValueTask.FromResult(createEvaluation(context, request)),
            (context, outcome) => ValueTask.FromResult(createResult(context, outcome)));
    }

    /// <summary>
    /// Creates a binding that asynchronously authors a fresh canonical evaluation for every request and maps its
    /// complete outcome to an HTTP result.
    /// </summary>
    /// <param name="operationName">Name of the declared API operation to bind.</param>
    /// <param name="createEvaluation">Asynchronous per-request canonical evaluation factory.</param>
    /// <param name="createResult">Asynchronous explicit outcome-to-response projection.</param>
    /// <returns>A relation/query operation binding.</returns>
    /// <exception cref="ArgumentNullException">A required argument or delegate is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="operationName"/> is empty or white space.</exception>
    public static RelationQueryApiOperationBinding Evaluate(
        string operationName,
        Func<RelationQueryApiRequestContext, object?, ValueTask<RelationQueryEvaluation>> createEvaluation,
        Func<RelationQueryApiResultContext, RelationQueryEvaluationOutcome, ValueTask<IResult>> createResult)
    {
        ArgumentNullException.ThrowIfNull(createEvaluation);
        ArgumentNullException.ThrowIfNull(createResult);
        return new RelationQueryEvaluationApiOperationBinding(operationName, createEvaluation, createResult);
    }

    /// <summary>
    /// Creates an endpoint-anchored binding that asynchronously authors a fresh canonical evaluation for every
    /// request and maps its complete outcome to an HTTP result.
    /// </summary>
    /// <param name="endpoint">Declared endpoint handle to bind.</param>
    /// <param name="createEvaluation">Asynchronous per-request canonical evaluation factory.</param>
    /// <param name="createResult">Asynchronous explicit outcome-to-response projection.</param>
    /// <returns>A relation/query operation binding.</returns>
    /// <exception cref="ArgumentNullException">A required argument or delegate is <see langword="null"/>.</exception>
    public static RelationQueryApiOperationBinding Evaluate(
        ApiEndpoint endpoint,
        Func<RelationQueryApiRequestContext, object?, ValueTask<RelationQueryEvaluation>> createEvaluation,
        Func<RelationQueryApiResultContext, RelationQueryEvaluationOutcome, ValueTask<IResult>> createResult)
    {
        ArgumentNullException.ThrowIfNull(createEvaluation);
        ArgumentNullException.ThrowIfNull(createResult);
        return new RelationQueryEvaluationApiOperationBinding(endpoint, createEvaluation, createResult);
    }
}

/// <summary>Request context supplied to canonical relation/query evaluation factories.</summary>
/// <param name="OperationContext">Cohesive operation context for the HTTP request.</param>
/// <param name="HttpContext">Current ASP.NET HTTP context.</param>
/// <param name="Operation">Declared API operation being evaluated.</param>
/// <param name="EvaluationId">
/// Request-scoped identity that the factory must assign to the returned canonical evaluation.
/// </param>
public sealed record RelationQueryApiRequestContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    RelationQueryEvaluationId EvaluationId);

/// <summary>Context supplied to explicit canonical evaluation outcome mappers.</summary>
/// <param name="OperationContext">Cohesive operation context for the HTTP request.</param>
/// <param name="HttpContext">Current ASP.NET HTTP context.</param>
/// <param name="Operation">Declared API operation that produced the outcome.</param>
/// <param name="Request">Bound API request value, or <see langword="null"/> when no input was declared.</param>
/// <param name="Evaluation">Exact canonical evaluation submitted to the evaluator.</param>
public sealed record RelationQueryApiResultContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    object? Request,
    RelationQueryEvaluation Evaluation);
