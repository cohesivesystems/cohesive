using Cohesive.Model;
using Cohesive.Execution;
using Cohesive.Transitions.Model;

namespace Cohesive.Api;

/// <summary>
/// Entry point for fluent API definition authoring.
/// </summary>
public static class Api
{
    /// <summary>
    /// Starts a new API definition.
    /// </summary>
    public static ApiBuilder Define(string? name = null) => new(name);
}

/// <summary>
/// Root API definition builder.
/// </summary>
public sealed class ApiBuilder
{
    readonly List<ApiEndpoint> endpoints = [];
    readonly HashSet<ApiEndpointId> endpointIds = [];
    readonly string? name;

    internal ApiBuilder(string? name = null)
    {
        this.name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    /// Starts an entity-oriented group.
    /// </summary>
    public EntityApiBuilder<TEntity> Entity<TEntity>() => new(this, EntityTypeName.From<TEntity>());

    /// <summary>
    /// Starts a root-scoped read operation.
    /// </summary>
    /// <param name="name">Stable logical operation name.</param>
    /// <returns>A builder for the root-scoped query.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    public RootOperationBuilder Query(string name) => new(this, this, name, ApiOperationKind.Query, entity: null);

    /// <summary>
    /// Starts a root-scoped write operation.
    /// </summary>
    /// <param name="name">Stable logical operation name.</param>
    /// <returns>A builder for the root-scoped command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    public RootOperationBuilder Command(string name) => new(this, this, name, ApiOperationKind.Command, entity: null);

    /// <summary>
    /// Starts a generic action operation.
    /// </summary>
    public RootOperationBuilder Action(string name) => new(this, this, name, ApiOperationKind.Action, entity: null);

    internal ApiEndpoint Add(ApiOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var endpoint = new ApiEndpoint(operation);
        if (!endpointIds.Add(endpoint.Id))
            throw new InvalidOperationException($"API definition already contains endpoint '{endpoint.Id}'.");

        endpoints.Add(endpoint);
        return endpoint;
    }

    internal ApiEndpointId CreateEndpointId(string operationName, EntityTypeName? entity)
    {
        var segments = new List<string>(capacity: 3);
        if (!string.IsNullOrWhiteSpace(name))
            segments.Add(name!);
        if (entity is { } entityName)
            segments.Add(entityName.Value);
        segments.Add(Guard.RequireNotNullOrWhiteSpace(operationName));
        return new ApiEndpointId(string.Join(".", segments));
    }

    /// <summary>
    /// Builds the immutable API definition.
    /// </summary>
    public ApiDefinition Build() => new(endpoints);
}

/// <summary>
/// Entity-scoped API definition builder.
/// </summary>
public sealed class EntityApiBuilder<TEntity>
{
    readonly ApiBuilder root;
    readonly EntityTypeName entity;

    internal EntityApiBuilder(ApiBuilder root, EntityTypeName entity)
    {
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        this.entity = entity;
    }

    /// <summary>
    /// Starts a read operation for the current entity.
    /// </summary>
    public EntityOperationBuilder<TEntity> Query(string name) => new(root, this, name, ApiOperationKind.Query, entity);

    /// <summary>
    /// Starts a write operation for the current entity.
    /// </summary>
    public EntityOperationBuilder<TEntity> Command(string name) => new(root, this, name, ApiOperationKind.Command, entity);

    /// <summary>
    /// Starts a root-scoped action from the current fluent chain.
    /// </summary>
    public RootOperationBuilder Action(string name) => root.Action(name);

    /// <summary>
    /// Starts another entity group from the current fluent chain.
    /// </summary>
    public EntityApiBuilder<TNext> Entity<TNext>() => root.Entity<TNext>();

    /// <summary>
    /// Builds the accumulated API definition.
    /// </summary>
    public ApiDefinition Build() => root.Build();
}

/// <summary>
/// Entity operation builder.
/// </summary>
public sealed class EntityOperationBuilder<TEntity> : OperationBuilder<EntityApiBuilder<TEntity>>
{
    internal EntityOperationBuilder(
        ApiBuilder root,
        EntityApiBuilder<TEntity> parent,
        string name,
        ApiOperationKind kind,
        EntityTypeName entity)
        : base(root, parent, name, kind, entity)
    {
    }
}

/// <summary>
/// Root operation builder.
/// </summary>
public sealed class RootOperationBuilder : OperationBuilder<ApiBuilder>
{
    internal RootOperationBuilder(
        ApiBuilder root,
        ApiBuilder parent,
        string name,
        ApiOperationKind kind,
        EntityTypeName? entity)
        : base(root, parent, name, kind, entity)
    {
    }
}

/// <summary>
/// Shared operation builder implementation.
/// </summary>
public abstract class OperationBuilder<TParent>
{
    readonly ApiBuilder root;
    readonly TParent parent;
    readonly string name;
    readonly ApiOperationKind kind;
    readonly EntityTypeName? entity;
    readonly List<HttpParameter> parameters = [];
    readonly List<ApiResultDefinition> additionalResults = [];
    readonly List<string> tags = [];
    readonly List<ApiScopePolicy> scopePolicies = [];
    readonly List<ApiAuthorizationRequirement> authorizationRequirements = [];
    readonly List<ApiSemanticReference> semanticReferences = [];

    string? method;
    string? route;
    Type? requestType;
    ApiResultDefinition? primaryResult;
    ExecutionDefinitionReference? transitionReference;
    HttpBodyBinding? body;
    HttpQueryBinding? query;
    string? summary;
    string? description;
    ApiEndpoint? endpoint;

    internal OperationBuilder(
        ApiBuilder root,
        TParent parent,
        string name,
        ApiOperationKind kind,
        EntityTypeName? entity
        )
    {
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        this.parent = parent;
        this.name = Guard.RequireNotNullOrWhiteSpace(name);
        this.kind = kind;
        this.entity = entity;
    }

    /// <summary>
    /// Sets the HTTP method and route template.
    /// </summary>
    public OperationBuilder<TParent> Route(string method, string route)
    {
        this.method = Guard.RequireNotNullOrWhiteSpace(method);
        this.route = Guard.RequireNotNullOrWhiteSpace(route);
        return this;
    }

    /// <summary>
    /// Declares the logical request type.
    /// </summary>
    public OperationBuilder<TParent> Accepts<TRequest>()
    {
        requestType = typeof(TRequest);
        return this;
    }

    /// <summary>
    /// Declares the JSON request body type.
    /// </summary>
    public OperationBuilder<TParent> Body<TRequest>()
    {
        requestType = typeof(TRequest);
        body = new(bodyType: typeof(TRequest));
        return this;
    }

    /// <summary>
    /// Declares a DTO whose readable properties are bound from the query string.
    /// </summary>
    public OperationBuilder<TParent> Query<TRequest>()
    {
        requestType = typeof(TRequest);
        query = new(queryType: typeof(TRequest));
        return this;
    }

    /// <summary>
    /// Declares the primary success response payload type.
    /// </summary>
    public OperationBuilder<TParent> Returns<TResponse>()
    {
        primaryResult = CreateResult(
            kind: ApiResultKind.Success,
            bodyType: typeof(TResponse),
            isPrimary: true,
            httpStatusCode: 200,
            id: "success",
            description: null);
        return this;
    }

    /// <summary>
    /// Declares an additional semantic result variant with a response body.
    /// </summary>
    public OperationBuilder<TParent> Result<TResponse>(
        ApiResultKind kind,
        int? httpStatusCode = null,
        string? id = null,
        string? description = null)
    {
        additionalResults.Add(CreateResult(
            kind: kind,
            bodyType: typeof(TResponse),
            isPrimary: false,
            httpStatusCode: httpStatusCode ?? ApiHttpResultConventions.DefaultStatusCode(kind, typeof(TResponse)),
            id: id,
            description: description
            )
        );
        return this;
    }

    /// <summary>
    /// Declares an additional semantic result variant without a response body.
    /// </summary>
    public OperationBuilder<TParent> Result(
        ApiResultKind kind,
        int? httpStatusCode = null,
        string? id = null,
        string? description = null
        )
    {
        additionalResults.Add(CreateResult(
            kind: kind,
            bodyType: typeof(void),
            isPrimary: false,
            httpStatusCode: httpStatusCode ?? ApiHttpResultConventions.DefaultStatusCode(kind, typeof(void)),
            id: id,
            description: description
            )
        );
        return this;
    }

    /// <summary>
    /// Associates an exact canonical Transition definition with the operation.
    /// </summary>
    /// <param name="transitionReference">Fingerprint-pinned canonical Transition definition.</param>
    /// <returns>The current operation builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transitionReference"/> is <see langword="null"/>.</exception>
    public OperationBuilder<TParent> Transition(ExecutionDefinitionReference transitionReference)
    {
        this.transitionReference = transitionReference ?? throw new ArgumentNullException(nameof(transitionReference));
        return this;
    }

    /// <summary>
    /// Adds a semantic scope policy to the operation.
    /// </summary>
    public OperationBuilder<TParent> Scope(ApiScopePolicy policy)
    {
        scopePolicies.Add(policy ?? throw new ArgumentNullException(nameof(policy)));
        return this;
    }

    /// <summary>
    /// Adds a transport-neutral authorization requirement to the operation.
    /// </summary>
    /// <param name="requirement">Requirement interpreted by authorization projections.</param>
    /// <returns>The current operation builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requirement"/> is <see langword="null"/>.</exception>
    public OperationBuilder<TParent> Requirement(ApiAuthorizationRequirement requirement)
    {
        authorizationRequirements.Add(requirement ?? throw new ArgumentNullException(nameof(requirement)));
        return this;
    }

    /// <summary>
    /// Adds an exact reference to a construct owned by another semantic authority.
    /// </summary>
    /// <param name="reference">Semantic authority, schema, path, and optional producer attribution.</param>
    /// <returns>The current operation builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    public OperationBuilder<TParent> SemanticReference(ApiSemanticReference reference)
    {
        semanticReferences.Add(reference ?? throw new ArgumentNullException(nameof(reference)));
        return this;
    }

    /// <summary>
    /// Adds a route-bound parameter.
    /// </summary>
    public OperationBuilder<TParent> RouteParameter<T>(string name)
    {
        parameters.Add(new(name, HttpParameterSource.Route, typeof(T)));
        return this;
    }

    /// <summary>
    /// Adds a query-bound parameter.
    /// </summary>
    public OperationBuilder<TParent> QueryParameter<T>(string name)
    {
        parameters.Add(new(name, HttpParameterSource.Query, typeof(T)));
        return this;
    }

    /// <summary>
    /// Adds an optional query-bound parameter.
    /// </summary>
    public OperationBuilder<TParent> OptionalQueryParameter<T>(string name)
    {
        parameters.Add(new(name, HttpParameterSource.Query, typeof(T), isOptional: true));
        return this;
    }

    /// <summary>
    /// Adds a header-bound parameter.
    /// </summary>
    public OperationBuilder<TParent> HeaderParameter<T>(string name)
    {
        parameters.Add(new(name, HttpParameterSource.Header, typeof(T)));
        return this;
    }

    /// <summary>
    /// Adds an optional header-bound parameter.
    /// </summary>
    public OperationBuilder<TParent> OptionalHeaderParameter<T>(string name)
    {
        parameters.Add(new(name, HttpParameterSource.Header, typeof(T), isOptional: true));
        return this;
    }

    /// <summary>
    /// Sets the human-readable operation summary projected by compatible transports.
    /// </summary>
    public OperationBuilder<TParent> Summary(string value)
    {
        summary = Guard.RequireNotNullOrWhiteSpace(value);
        return this;
    }

    /// <summary>
    /// Sets the human-readable operation description projected by compatible transports.
    /// </summary>
    public OperationBuilder<TParent> Description(string value)
    {
        description = Guard.RequireNotNullOrWhiteSpace(value);
        return this;
    }

    /// <summary>
    /// Adds a logical grouping tag projected by compatible transports.
    /// </summary>
    public OperationBuilder<TParent> Tag(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            tags.Add(value);

        return this;
    }

    /// <summary>
    /// Completes the operation, adds it to the root definition builder, and returns an endpoint handle.
    /// </summary>
    public ApiEndpoint Build()
    {
        if (endpoint is not null)
            return endpoint;

        var http = FinalizeHttpBinding();
        var finalizedRequestType = http?.Body?.BodyType ?? http?.Query?.QueryType ?? requestType ?? typeof(void);
        var finalizedResults = FinalizeResults(hasHttpProjection: http is not null);
        var finalizedResponseType = finalizedResults[0].BodyType;
        var finalizedTags = FinalizeTags(kind, entity, tags);

        var operation = new ApiOperation(
            name: name,
            kind: kind,
            requestType: finalizedRequestType,
            responseType: finalizedResponseType,
            http: http,
            id: root.CreateEndpointId(name, entity),
            entity: entity,
            transitionReference: transitionReference,
            summary: summary,
            description: description,
            tags: finalizedTags,
            results: finalizedResults,
            scopePolicies: scopePolicies,
            authorizationRequirements: authorizationRequirements,
            semanticReferences: semanticReferences
            );

        endpoint = root.Add(operation);
        return endpoint;
    }

    /// <summary>
    /// Completes the operation and returns to the parent builder.
    /// </summary>
    public TParent Done()
    {
        Build();
        return parent;
    }

    HttpBinding? FinalizeHttpBinding()
    {
        if (method is null && route is null)
        {
            if (body is not null || query is not null || parameters.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Operation '{name}' declares HTTP request bindings but does not declare an HTTP route.");
            }

            return null;
        }

        if (method is null || route is null)
        {
            throw new InvalidOperationException(
                $"Operation '{name}' must declare both an HTTP method and route.");
        }

        var finalizedMethod = Guard.RequireNotNullOrWhiteSpace(method);
        var finalizedRoute = Guard.RequireNotNullOrWhiteSpace(route);
        return new HttpBinding(
            method: finalizedMethod,
            route: finalizedRoute,
            parameters: FinalizeParameters(finalizedRoute, parameters),
            body: FinalizeBody(finalizedMethod),
            query: FinalizeQuery());
    }

    HttpBodyBinding? FinalizeBody(string finalizedMethod)
    {
        if (query is not null)
        {
            if (body is not null)
                throw new InvalidOperationException($"Operation '{name}' cannot declare both a query DTO and a JSON body.");

            return null;
        }

        if (body is not null)
            return body;

        if (requestType is null)
            return null;

        if (ShouldInferJsonBody(finalizedMethod))
            return new(requestType);

        return null;
    }

    HttpQueryBinding? FinalizeQuery() => query;

    IReadOnlyList<ApiResultDefinition> FinalizeResults(bool hasHttpProjection)
    {
        var primary = primaryResult ?? CreateResult(
            kind: ApiResultKind.NoContent,
            bodyType: typeof(void),
            isPrimary: true,
            httpStatusCode: 204,
            id: "noContent",
            description: null);

        if (additionalResults.Count == 0)
            return [hasHttpProjection ? primary : primary.WithHttp(http: null)];

        var results = new ApiResultDefinition[additionalResults.Count + 1];
        results[0] = hasHttpProjection ? primary : primary.WithHttp(http: null);
        for (var i = 0; i < additionalResults.Count; i++)
        {
            results[i + 1] = hasHttpProjection
                ? additionalResults[i]
                : additionalResults[i].WithHttp(http: null);
        }

        return results;
    }

    static ApiResultDefinition CreateResult(
        ApiResultKind kind,
        Type bodyType,
        bool isPrimary,
        int? httpStatusCode,
        string? id,
        string? description)
    {
        var binding = httpStatusCode is null ? null : new ApiHttpResultBinding(httpStatusCode.Value);
        return new(
            kind: kind,
            bodyType: bodyType,
            isPrimary: isPrimary,
            id: id,
            description: description,
            http: binding
            );
    }

    static bool ShouldInferJsonBody(string method) =>
        !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);

    static IReadOnlyList<HttpParameter> FinalizeParameters(string route, List<HttpParameter> explicitParameters)
    {
        var finalized = explicitParameters.Count == 0
            ? new List<HttpParameter>()
            : [.. explicitParameters];

        var routeParameters = ParseRouteParameters(route);
        for (var i = 0; i < routeParameters.Count; i++)
        {
            var routeParameter = routeParameters[i];
            if (ContainsParameter(finalized, routeParameter))
                continue;

            finalized.Add(new(name: routeParameter, HttpParameterSource.Route, typeof(string)));
        }

        return finalized;
    }

    static IReadOnlyList<string> FinalizeTags(ApiOperationKind kind, EntityTypeName? entity, List<string> tags)
    {
        if (tags.Count > 0)
            return [.. tags];

        if (entity is not null)
            return [entity.Value];

        return [kind.ToString()];
    }

    static bool ContainsParameter(IEnumerable<HttpParameter> parameters, string candidate)
    {
        foreach (var parameter in parameters)
        {
            if (string.Equals(parameter.Name, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static IReadOnlyList<string> ParseRouteParameters(string route)
    {
        var values = new List<string>();
        for (var index = 0; index < route.Length; index += 1)
        {
            if (route[index] != '{')
                continue;

            var end = route.IndexOf('}', index + 1);
            if (end <= index + 1)
                break;

            var raw = route.Substring(index + 1, end - index - 1);
            var normalized = NormalizeRouteParameter(raw);
            if (!string.IsNullOrWhiteSpace(normalized) && !values.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                values.Add(normalized);

            index = end;
        }
        return values;
    }

    static string NormalizeRouteParameter(string value)
    {
        var separatorIndex = value.IndexOfAny([':', '=', '?']);
        return separatorIndex >= 0 ? value[..separatorIndex] : value;
    }
}
