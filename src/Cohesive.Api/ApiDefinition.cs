using Cohesive.Model;
using Cohesive.Transitions.Model;
using Cohesive.Execution;

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

/// <summary>Stable host-language-independent wire names for closed API semantic categories.</summary>
public static class ApiWireNames
{
    /// <summary>Gets the canonical wire name of an API operation category.</summary>
    /// <param name="kind">Operation category to name.</param>
    /// <returns>The stable lower-camel semantic name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public static string OperationKind(ApiOperationKind kind) => kind switch
    {
        ApiOperationKind.Query => "query",
        ApiOperationKind.Command => "command",
        ApiOperationKind.Action => "action",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported API operation kind.")
    };

    /// <summary>Gets the canonical wire name of an API result category.</summary>
    /// <param name="kind">Result category to name.</param>
    /// <returns>The stable lower-camel semantic name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public static string ResultKind(ApiResultKind kind) => kind switch
    {
        ApiResultKind.Success => "success",
        ApiResultKind.Created => "created",
        ApiResultKind.Accepted => "accepted",
        ApiResultKind.NoContent => "noContent",
        ApiResultKind.ValidationFailed => "validationFailed",
        ApiResultKind.Unauthorized => "unauthorized",
        ApiResultKind.Forbidden => "forbidden",
        ApiResultKind.NotFound => "notFound",
        ApiResultKind.Conflict => "conflict",
        ApiResultKind.PreconditionFailed => "preconditionFailed",
        ApiResultKind.RateLimited => "rateLimited",
        ApiResultKind.DomainError => "domainError",
        ApiResultKind.InfrastructureError => "infrastructureError",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported API result kind.")
    };
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

    /// <summary>Creates a copy of the result with an updated HTTP projection.</summary>
    /// <param name="http">HTTP projection metadata, or <see langword="null"/> to remove that projection.</param>
    /// <returns>This result when <paramref name="http"/> is unchanged; otherwise, an equivalent result with that projection.</returns>
    public ApiResultDefinition WithHttp(ApiHttpResultBinding? http) =>
        http == Http
            ? this
            : new ApiResultDefinition(Kind, BodyType, IsPrimary, Id, Description, http);

    static string GetDefaultId(ApiResultKind kind, bool isPrimary) =>
        isPrimary && kind == ApiResultKind.Success
            ? "success"
            : ApiWireNames.ResultKind(kind);
}

/// <summary>
/// Immutable API operation definition.
/// </summary>
public sealed class ApiOperation
{
    /// <summary>
    /// Creates an API operation.
    /// </summary>
    /// <param name="name">Stable logical operation name.</param>
    /// <param name="kind">Semantic operation category.</param>
    /// <param name="requestType">Declared request payload or envelope type.</param>
    /// <param name="responseType">Compatibility response type used when <paramref name="results"/> is absent.</param>
    /// <param name="http">Optional HTTP projection metadata.</param>
    /// <param name="id">Optional stable endpoint identity; defaults to <paramref name="name"/>.</param>
    /// <param name="entity">Optional owning entity.</param>
    /// <param name="transition">Optional transition projected by this operation.</param>
    /// <param name="summary">Optional human-readable summary.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="tags">Optional logical grouping tags.</param>
    /// <param name="results">Optional semantic result variants.</param>
    /// <param name="scopePolicies">Optional semantic scope policies.</param>
    /// <param name="authorizationRequirements">Optional transport-neutral authorization requirements.</param>
    /// <param name="semanticReferences">Optional references to exact constructs owned by semantic authorities.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/>, <paramref name="requestType"/>, or <paramref name="responseType"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or white space, or a supplied collection is structurally invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Result identities, primary selection, authorization requirements, or semantic references conflict.
    /// </exception>
    public ApiOperation(
        string name,
        ApiOperationKind kind,
        Type requestType,
        Type responseType,
        HttpBinding? http = null,
        ApiEndpointId? id = null,
        EntityTypeName? entity = null,
        TransitionDefinition? transition = null,
        string? summary = null,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<ApiResultDefinition>? results = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null,
        IReadOnlyList<ApiAuthorizationRequirement>? authorizationRequirements = null,
        IReadOnlyList<ApiSemanticReference>? semanticReferences = null)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Id = id ?? new ApiEndpointId(Name);
        Kind = kind;
        RequestType = requestType ?? throw new ArgumentNullException(nameof(requestType));
        Http = http;
        Entity = entity;
        Transition = transition;
        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary;
        Description = string.IsNullOrWhiteSpace(description) ? null : description;
        Tags = tags is null || tags.Count == 0 ? [] : [.. tags];
        ScopePolicies = scopePolicies is null || scopePolicies.Count == 0 ? [] : [.. scopePolicies];
        AuthorizationRequirements = NormalizeAuthorizationRequirements(authorizationRequirements);
        SemanticReferences = NormalizeSemanticReferences(semanticReferences);
        Results = NormalizeResults(
            responseType ?? throw new ArgumentNullException(nameof(responseType)),
            results,
            inferHttpBinding: http is not null);
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
    /// Optional HTTP projection metadata.
    /// </summary>
    public HttpBinding? Http { get; }

    /// <summary>
    /// Optional human-readable summary projected by compatible transports.
    /// </summary>
    public string? Summary { get; }

    /// <summary>
    /// Optional human-readable description projected by compatible transports.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Logical grouping tags projected by compatible transports.
    /// </summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Semantic scope policies that describe how the operation is bound to caller or resource scope.
    /// </summary>
    public IReadOnlyList<ApiScopePolicy> ScopePolicies { get; }

    /// <summary>
    /// Transport-neutral authorization requirements in stable declaration order.
    /// </summary>
    public IReadOnlyList<ApiAuthorizationRequirement> AuthorizationRequirements { get; }

    /// <summary>
    /// Exact semantic authority references in stable declaration order.
    /// </summary>
    public IReadOnlyList<ApiSemanticReference> SemanticReferences { get; }

    /// <summary>Creates an equivalent operation projected through HTTP.</summary>
    /// <remarks>
    /// Existing explicit result bindings are retained. Results without an HTTP binding receive the
    /// conventional status code for their semantic result kind.
    /// </remarks>
    /// <param name="http">HTTP operation binding to attach.</param>
    /// <returns>A new operation retaining the semantic identity, contracts, policies, and provenance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> is <see langword="null"/>.</exception>
    public ApiOperation WithHttp(HttpBinding http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var results = new ApiResultDefinition[Results.Count];
        for (var i = 0; i < Results.Count; i++)
        {
            var result = Results[i];
            results[i] = result.Http is not null
                ? result
                : result.WithHttp(new ApiHttpResultBinding(
                    ApiHttpResultConventions.DefaultStatusCode(result.Kind, result.BodyType)));
        }

        return new ApiOperation(
            name: Name,
            kind: Kind,
            requestType: RequestType,
            responseType: ResponseType,
            http: http,
            id: Id,
            entity: Entity,
            transition: Transition,
            summary: Summary,
            description: Description,
            tags: Tags,
            results: results,
            scopePolicies: ScopePolicies,
            authorizationRequirements: AuthorizationRequirements,
            semanticReferences: SemanticReferences);
    }

    static IReadOnlyList<ApiAuthorizationRequirement> NormalizeAuthorizationRequirements(
        IReadOnlyList<ApiAuthorizationRequirement>? requirements)
    {
        if (requirements is null || requirements.Count == 0)
            return [];

        var normalized = new List<ApiAuthorizationRequirement>(requirements.Count);
        var byId = new Dictionary<string, ApiAuthorizationRequirement>(StringComparer.Ordinal);
        for (var i = 0; i < requirements.Count; i++)
        {
            var requirement = requirements[i]
                ?? throw new ArgumentException(
                    "API authorization requirements must not contain null values.",
                    nameof(requirements));
            if (!byId.TryGetValue(requirement.Id, out var existing))
            {
                byId.Add(requirement.Id, requirement);
                normalized.Add(requirement);
                continue;
            }

            if (existing != requirement)
            {
                throw new InvalidOperationException(
                    $"API operation declares authorization requirement '{requirement.Id}' with conflicting metadata.");
            }
        }

        return normalized.Count == requirements.Count ? [.. requirements] : [.. normalized];
    }

    static IReadOnlyList<ApiSemanticReference> NormalizeSemanticReferences(
        IReadOnlyList<ApiSemanticReference>? references)
    {
        if (references is null || references.Count == 0)
            return [];

        var normalized = new List<ApiSemanticReference>(references.Count);
        var byCoordinate = new Dictionary<(string Authority, ExecutionIrSchemaVersion Schema, string Path), ApiSemanticReference>();
        for (var i = 0; i < references.Count; i++)
        {
            var reference = references[i]
                ?? throw new ArgumentException(
                    "API semantic references must not contain null values.",
                    nameof(references));
            var coordinate = (reference.Authority, reference.SchemaVersion, reference.Path.ToString());
            if (!byCoordinate.TryGetValue(coordinate, out var existing))
            {
                byCoordinate.Add(coordinate, reference);
                normalized.Add(reference);
                continue;
            }

            if (existing != reference)
            {
                throw new InvalidOperationException(
                    $"API operation declares semantic reference '{reference.Authority}:{reference.Path}' with conflicting provenance.");
            }
        }

        return normalized.Count == references.Count ? [.. references] : [.. normalized];
    }

    static IReadOnlyList<ApiResultDefinition> NormalizeResults(
        Type responseType,
        IReadOnlyList<ApiResultDefinition>? results,
        bool inferHttpBinding)
    {
        if (results is null || results.Count == 0)
        {
            var kind = responseType == typeof(void) ? ApiResultKind.NoContent : ApiResultKind.Success;
            return
            [
                new ApiResultDefinition(
                    kind: kind,
                    bodyType: responseType,
                    isPrimary: true,
                    id: kind == ApiResultKind.NoContent ? "noContent" : "success",
                    http: inferHttpBinding
                        ? new ApiHttpResultBinding(ApiHttpResultConventions.DefaultStatusCode(kind, responseType))
                        : null)
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

            normalized[i] = inferHttpBinding ? result : result.WithHttp(http: null);
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

static class ApiHttpResultConventions
{
    public static int DefaultStatusCode(ApiResultKind kind, Type bodyType) => kind switch
    {
        ApiResultKind.Success when bodyType == typeof(void) => 204,
        ApiResultKind.Success => 200,
        ApiResultKind.Created => 201,
        ApiResultKind.Accepted => 202,
        ApiResultKind.NoContent => 204,
        ApiResultKind.ValidationFailed => 400,
        ApiResultKind.Unauthorized => 401,
        ApiResultKind.Forbidden => 403,
        ApiResultKind.NotFound => 404,
        ApiResultKind.Conflict => 409,
        ApiResultKind.PreconditionFailed => 412,
        ApiResultKind.RateLimited => 429,
        ApiResultKind.DomainError => 422,
        ApiResultKind.InfrastructureError => 500,
        _ => 200
    };
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
