using Cohesive.Model;
using Cohesive.Transitions.Model;

namespace Cohesive.Api;

/// <summary>
/// Immutable API surface definition.
/// </summary>
public sealed class ApiDefinition
{
    /// <summary>
    /// Creates an API definition.
    /// </summary>
    public ApiDefinition(IReadOnlyList<ApiOperation> operations)
        : this(CreateEndpoints(operations))
    {
    }

    /// <summary>
    /// Creates an API definition from endpoint handles.
    /// </summary>
    public ApiDefinition(IReadOnlyList<ApiEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Endpoints = endpoints.Count == 0 ? [] : [.. endpoints];
        Operations = Endpoints.Count == 0 ? [] : [.. Endpoints.Select(static endpoint => endpoint.Operation)];
        OperationsById = BuildOperationIndex(Endpoints);
    }

    /// <summary>
    /// Exposed endpoints in stable definition order.
    /// </summary>
    public IReadOnlyList<ApiEndpoint> Endpoints { get; }

    /// <summary>
    /// Exposed operations in stable definition order.
    /// </summary>
    public IReadOnlyList<ApiOperation> Operations { get; }

    /// <summary>
    /// Operations keyed by stable endpoint id.
    /// </summary>
    public IReadOnlyDictionary<ApiEndpointId, ApiOperation> OperationsById { get; }

    /// <summary>
    /// Finds an operation by endpoint id.
    /// </summary>
    public bool TryGetOperation(ApiEndpointId id, out ApiOperation operation) =>
        OperationsById.TryGetValue(id, out operation!);

    /// <summary>
    /// Gets the operation declared by an endpoint handle.
    /// </summary>
    public ApiOperation GetOperation(ApiEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return TryGetOperation(endpoint.Id, out var operation)
            ? operation
            : throw new InvalidOperationException($"API definition does not contain endpoint '{endpoint.Id}'.");
    }

    /// <summary>
    /// Combines multiple definitions into one.
    /// </summary>
    public static ApiDefinition Combine(IEnumerable<ApiDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var endpoints = new List<ApiEndpoint>();
        foreach (var definition in definitions)
        {
            if (definition is null)
                continue;

            for (var i = 0; i < definition.Endpoints.Count; i++)
                endpoints.Add(definition.Endpoints[i]);
        }

        return new ApiDefinition(endpoints);
    }

    /// <summary>
    /// Creates a definition from endpoint handles.
    /// </summary>
    public static ApiDefinition From(params ApiEndpoint[] endpoints) => new(endpoints);

    static IReadOnlyList<ApiEndpoint> CreateEndpoints(IReadOnlyList<ApiOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
            return [];

        var endpoints = new ApiEndpoint[operations.Count];
        for (var i = 0; i < operations.Count; i++)
            endpoints[i] = new ApiEndpoint(operations[i]);

        return endpoints;
    }

    static IReadOnlyDictionary<ApiEndpointId, ApiOperation> BuildOperationIndex(IReadOnlyList<ApiEndpoint> endpoints)
    {
        Dictionary<ApiEndpointId, ApiOperation> index = new();
        for (var i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i] ?? throw new ArgumentException("API definition endpoints must not contain null values.", nameof(endpoints));
            if (!index.TryAdd(endpoint.Id, endpoint.Operation))
                throw new InvalidOperationException($"API definition contains duplicate endpoint id '{endpoint.Id}'.");
        }

        return index;
    }
}

/// <summary>
/// Stable logical endpoint id used to bind implementations and project API surfaces.
/// </summary>
public readonly record struct ApiEndpointId
{
    /// <summary>
    /// Creates an endpoint id.
    /// </summary>
    public ApiEndpointId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>
    /// Endpoint id value.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    /// Converts an endpoint id to its string value.
    /// </summary>
    public static implicit operator string(ApiEndpointId id) => id.Value;
}

/// <summary>
/// A declared API endpoint handle.
/// </summary>
public class ApiEndpoint
{
    internal ApiEndpoint(ApiOperation operation)
    {
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    /// <summary>
    /// Stable endpoint id.
    /// </summary>
    public ApiEndpointId Id => Operation.Id;

    /// <summary>
    /// Human-readable endpoint name.
    /// </summary>
    public string Name => Operation.Name;

    /// <summary>
    /// Immutable operation definition.
    /// </summary>
    public ApiOperation Operation { get; }

    /// <inheritdoc />
    public override string ToString() => Id.Value;
}

/// <summary>
/// Logical API operation category.
/// </summary>
public enum ApiOperationKind
{
    /// <summary>
    /// Read-oriented operation that returns data without intentionally changing server state.
    /// </summary>
    Query = 0,

    /// <summary>
    /// Write-oriented operation that creates, revises, deletes, archives, or otherwise changes server state.
    /// </summary>
    Command = 1,

    /// <summary>
    /// Root-scoped operation that is not owned by one declared entity surface.
    /// </summary>
    Action = 2
}

/// <summary>
/// Semantic category for an API operation result variant.
/// </summary>
public enum ApiResultKind
{
    /// <summary>
    /// The operation completed successfully and returned its primary body.
    /// </summary>
    Success = 0,

    /// <summary>
    /// The operation created a resource.
    /// </summary>
    Created = 1,

    /// <summary>
    /// The operation was accepted for asynchronous processing.
    /// </summary>
    Accepted = 2,

    /// <summary>
    /// The operation completed successfully without a response body.
    /// </summary>
    NoContent = 3,

    /// <summary>
    /// The request did not satisfy validation rules.
    /// </summary>
    ValidationFailed = 4,

    /// <summary>
    /// The caller is not authenticated.
    /// </summary>
    Unauthorized = 5,

    /// <summary>
    /// The caller is authenticated but not permitted to perform the operation.
    /// </summary>
    Forbidden = 6,

    /// <summary>
    /// The target entity or resource was not found.
    /// </summary>
    NotFound = 7,

    /// <summary>
    /// The operation conflicts with current server state.
    /// </summary>
    Conflict = 8,

    /// <summary>
    /// The operation failed a precondition such as a concurrency check.
    /// </summary>
    PreconditionFailed = 9,

    /// <summary>
    /// The caller is currently rate limited.
    /// </summary>
    RateLimited = 10,

    /// <summary>
    /// A domain-level error prevented successful completion.
    /// </summary>
    DomainError = 11,

    /// <summary>
    /// An unexpected infrastructure or system error occurred.
    /// </summary>
    InfrastructureError = 12
}

/// <summary>
/// Optional HTTP projection metadata for an API result variant.
/// </summary>
public sealed class ApiHttpResultBinding
{
    /// <summary>
    /// Creates an HTTP result binding.
    /// </summary>
    public ApiHttpResultBinding(int statusCode, string? contentType = "application/json")
    {
        if (statusCode is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "HTTP status codes must be between 100 and 599.");

        StatusCode = statusCode;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType.Trim();
    }

    /// <summary>
    /// HTTP status code used when projecting this result to HTTP.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// Response content type when the result has a body.
    /// </summary>
    public string? ContentType { get; }
}

/// <summary>
/// Declares one semantic result variant for an API operation.
/// </summary>
public sealed class ApiResultDefinition
{
    /// <summary>
    /// Creates an API result variant.
    /// </summary>
    public ApiResultDefinition(
        ApiResultKind kind,
        Type bodyType,
        bool isPrimary = false,
        string? id = null,
        string? description = null,
        ApiHttpResultBinding? http = null)
    {
        Kind = kind;
        BodyType = bodyType ?? throw new ArgumentNullException(nameof(bodyType));
        IsPrimary = isPrimary;
        Id = string.IsNullOrWhiteSpace(id) ? GetDefaultId(kind, isPrimary) : id.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Http = http;
    }

    /// <summary>
    /// Stable result id within the operation.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Semantic result kind.
    /// </summary>
    public ApiResultKind Kind { get; }

    /// <summary>
    /// CLR body type for the result, or <see cref="void"/> when no body is emitted.
    /// </summary>
    public Type BodyType { get; }

    /// <summary>
    /// Indicates that this is the compatibility result exposed through <see cref="ApiOperation.ResponseType"/>.
    /// </summary>
    public bool IsPrimary { get; }

    /// <summary>
    /// Optional human-readable result description.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Optional HTTP binding for transports that use status codes.
    /// </summary>
    public ApiHttpResultBinding? Http { get; }

    /// <summary>
    /// Creates a copy of the result with updated primary selection.
    /// </summary>
    public ApiResultDefinition WithPrimary(bool isPrimary) =>
        isPrimary == IsPrimary
            ? this
            : new ApiResultDefinition(Kind, BodyType, isPrimary, Id, Description, Http);

    static string GetDefaultId(ApiResultKind kind, bool isPrimary) =>
        isPrimary && kind == ApiResultKind.Success
            ? "success"
            : ToCamelCase(kind.ToString());

    static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "result";

        return value.Length == 1
            ? char.ToLowerInvariant(value[0]).ToString()
            : char.ToLowerInvariant(value[0]) + value[1..];
    }
}

/// <summary>
/// Immutable API operation definition.
/// </summary>
public sealed class ApiOperation
{
    /// <summary>
    /// Creates an API operation.
    /// </summary>
    public ApiOperation(
        string name,
        ApiOperationKind kind,
        Type requestType,
        Type responseType,
        HttpBinding http,
        ApiEndpointId? id = null,
        EntityTypeName? entity = null,
        TransitionDefinition? transition = null,
        string? summary = null,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<ApiResultDefinition>? results = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Id = id ?? new ApiEndpointId(Name);
        Kind = kind;
        RequestType = requestType ?? throw new ArgumentNullException(nameof(requestType));
        Http = http ?? throw new ArgumentNullException(nameof(http));
        Entity = entity;
        Transition = transition;
        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary;
        Description = string.IsNullOrWhiteSpace(description) ? null : description;
        Tags = tags is null || tags.Count == 0 ? [] : [.. tags];
        ScopePolicies = scopePolicies is null || scopePolicies.Count == 0 ? [] : [.. scopePolicies];
        Results = NormalizeResults(responseType ?? throw new ArgumentNullException(nameof(responseType)), results);
        PrimaryResult = SelectPrimaryResult(Results);
        ResponseType = PrimaryResult.BodyType;
    }

    /// <summary>
    /// Stable logical name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Stable endpoint id used for binding and projection.
    /// </summary>
    public ApiEndpointId Id { get; }

    /// <summary>
    /// Operation kind.
    /// </summary>
    public ApiOperationKind Kind { get; }

    /// <summary>
    /// Declared request payload or envelope type.
    /// </summary>
    public Type RequestType { get; }

    /// <summary>
    /// Declared primary response payload type.
    /// </summary>
    public Type ResponseType { get; }

    /// <summary>
    /// Declared semantic result variants in stable operation order.
    /// HTTP projections may bind variants through status codes, GraphQL projections may expose them as
    /// operation-specific unions, and a future gRPC projection can map multi-result operations to response oneof fields.
    /// </summary>
    public IReadOnlyList<ApiResultDefinition> Results { get; }

    /// <summary>
    /// Primary result used for compatibility projections and generated clients that expose one success type.
    /// </summary>
    public ApiResultDefinition PrimaryResult { get; }

    /// <summary>
    /// Owning entity, when the operation is entity-oriented.
    /// </summary>
    public EntityTypeName? Entity { get; }

    /// <summary>
    /// Related transition, when the operation represents a transition command.
    /// </summary>
    public TransitionDefinition? Transition { get; }

    /// <summary>
    /// HTTP binding metadata.
    /// </summary>
    public HttpBinding Http { get; }

    /// <summary>
    /// Optional OpenAPI summary text.
    /// </summary>
    public string? Summary { get; }

    /// <summary>
    /// Optional OpenAPI description text.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Optional OpenAPI tags.
    /// </summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Semantic scope policies that describe how the operation is bound to caller or resource scope.
    /// </summary>
    public IReadOnlyList<ApiScopePolicy> ScopePolicies { get; }

    static IReadOnlyList<ApiResultDefinition> NormalizeResults(Type responseType, IReadOnlyList<ApiResultDefinition>? results)
    {
        if (results is null || results.Count == 0)
        {
            var kind = responseType == typeof(void) ? ApiResultKind.NoContent : ApiResultKind.Success;
            var statusCode = responseType == typeof(void) ? 204 : 200;
            return
            [
                new ApiResultDefinition(
                    kind: kind,
                    bodyType: responseType,
                    isPrimary: true,
                    id: kind == ApiResultKind.NoContent ? "noContent" : "success",
                    http: new ApiHttpResultBinding(statusCode))
            ];
        }

        var normalized = new ApiResultDefinition[results.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var primaryCount = 0;
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i] ?? throw new ArgumentException("API operation results must not contain null values.", nameof(results));
            if (!ids.Add(result.Id))
                throw new InvalidOperationException($"API operation declares duplicate result id '{result.Id}'.");

            if (result.IsPrimary)
                primaryCount++;

            normalized[i] = result;
        }

        if (primaryCount == 0)
            normalized[0] = normalized[0].WithPrimary(true);
        else if (primaryCount > 1)
            throw new InvalidOperationException("API operation declares more than one primary result.");

        return normalized;
    }

    static ApiResultDefinition SelectPrimaryResult(IReadOnlyList<ApiResultDefinition> results)
    {
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].IsPrimary)
                return results[i];
        }

        throw new InvalidOperationException("API operation must declare a primary result.");
    }
}

/// <summary>
/// HTTP binding for an API operation.
/// </summary>
public sealed class HttpBinding
{
    /// <summary>
    /// Creates an HTTP binding.
    /// </summary>
    public HttpBinding(
        string method,
        string route,
        IReadOnlyList<HttpParameter>? parameters,
        HttpBodyBinding? body,
        HttpQueryBinding? query = null
        )
    {
        Method = Guard.RequireNotNullOrWhiteSpace(method).ToUpperInvariant();
        Route = Guard.RequireNotNullOrWhiteSpace(route);
        Parameters = parameters is null || parameters.Count == 0 ? [] : [.. parameters];
        Body = body;
        Query = query;
    }

    /// <summary>
    /// HTTP method.
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// Route pattern.
    /// </summary>
    public string Route { get; }

    /// <summary>
    /// Bound route, query, and header parameters.
    /// </summary>
    public IReadOnlyList<HttpParameter> Parameters { get; }

    /// <summary>
    /// Optional request body binding.
    /// </summary>
    public HttpBodyBinding? Body { get; }

    /// <summary>
    /// Optional DTO binding whose readable properties are projected onto the query string.
    /// </summary>
    public HttpQueryBinding? Query { get; }
}

/// <summary>
/// Bound HTTP parameter.
/// </summary>
public sealed class HttpParameter
{
    /// <summary>
    /// Creates a parameter definition.
    /// </summary>
    public HttpParameter(string name, HttpParameterSource source, Type type, bool isOptional = false)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Source = source;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        IsOptional = isOptional;
    }

    /// <summary>
    /// Parameter name as bound in HTTP.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Binding source.
    /// </summary>
    public HttpParameterSource Source { get; }

    /// <summary>
    /// CLR parameter type.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Indicates whether the parameter may be omitted by the caller.
    /// </summary>
    public bool IsOptional { get; }
}

/// <summary>
/// Supported HTTP parameter sources.
/// </summary>
public enum HttpParameterSource
{
    /// <summary>Represents the route option.</summary>
    Route = 0,
    /// <summary>Represents the query option.</summary>
    Query = 1,
    /// <summary>Represents the header option.</summary>
    Header = 2
}

/// <summary>
/// Request body binding metadata.
/// </summary>
public sealed class HttpBodyBinding
{
    /// <summary>
    /// Creates a body binding.
    /// </summary>
    public HttpBodyBinding(Type bodyType)
    {
        BodyType = bodyType ?? throw new ArgumentNullException(nameof(bodyType));
    }

    /// <summary>
    /// CLR body type.
    /// </summary>
    public Type BodyType { get; }
}

/// <summary>
/// Query string DTO binding metadata.
/// </summary>
public sealed class HttpQueryBinding
{
    /// <summary>
    /// Creates a query binding.
    /// </summary>
    public HttpQueryBinding(Type queryType)
    {
        QueryType = queryType ?? throw new ArgumentNullException(nameof(queryType));
    }

    /// <summary>
    /// CLR DTO type whose readable public instance properties are bound from the query string.
    /// </summary>
    public Type QueryType { get; }
}
