using Cohesive.Transitions.Authoring;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Execution;
using Cohesive.Host.Services;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Processes.Authoring;
using Cohesive.Relations.Authoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.AspNet.Services;

/// <summary>Composes partial implementations of instance-owned HTTP API contracts.</summary>
/// <remarks>Single-threaded authoring; Build freezes contributions. Existing API, query and service documents
/// remain authorities. Native route configuration is retained as host attachment, never portable semantics.</remarks>
public sealed class ServiceModelBuilder
{
    readonly List<ApiDefinition> definitions = [];
    readonly List<Func<IServicePersistence, PreparedApiContribution>> contributions = [];
    readonly AuthoringState<ServiceModelBuilder> state;
    ServiceDependencies queries = new();
    /// <summary>Starts an empty, host-local implementation composition.</summary>
    public ServiceModelBuilder() => state = new(() => this);

    /// <summary>Contributes implementations to an API instance; repeated contributions compose the same contract.</summary>
    /// <typeparam name="TApi">Application API declaration type.</typeparam>
    /// <param name="api">Shared instance-owned contract.</param><param name="configure">Setup-only contribution.</param>
    /// <returns>This builder.</returns>
    public ServiceModelBuilder Implement<TApi>(TApi api, Action<ServiceApiImplementation<TApi>> configure) where TApi : ApiModel
    {
        ArgumentNullException.ThrowIfNull(api);
        return Implement(api, api.Definition, configure);
    }

    /// <summary>Contributes to a separately declared API using an arbitrary typed catalog.</summary>
    /// <typeparam name="TApi">Typed endpoint catalog.</typeparam><param name="api">Catalog used by selectors.</param>
    /// <param name="definition">Explicit contract authority.</param><param name="configure">Setup-only contribution.</param>
    /// <returns>This builder.</returns>
    public ServiceModelBuilder Implement<TApi>(TApi api, ApiDefinition definition, Action<ServiceApiImplementation<TApi>> configure)
    {
        ArgumentNullException.ThrowIfNull(api); ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(configure);
        return state.Edit(() =>
        {
            if (!definitions.Contains(definition)) definitions.Add(definition);
            configure(new(this, api, definition));
            return this;
        });
    }

    internal void Add(Func<IServicePersistence, PreparedApiContribution> prepare) => state.Edit(() => { contributions.Add(prepare); return this; });
    internal void Query<TInput, TResult>(RelationQuery<TInput, TResult> query, ExecutionRevisionId revision, int rows, long bytes) =>
        state.Edit(() =>
        {
            var existing = queries.Queries.FirstOrDefault(item => ReferenceEquals(item.Request, query.CompilationRequest));
            if (existing is null) queries = queries.Query(query, revision, rows, bytes);
            else if (existing.Reference.RevisionId != revision || existing.MaximumRows != rows || existing.MaximumBytes != bytes)
                throw new ArgumentException("One query authority must have one revision and preparation bound in a service model.");
            return this;
        });

    /// <summary>Offline query selections derived from endpoint implementations, for native capability preparation.</summary>
    public ServiceDependencies QueryDependencies { get { _ = state.Value; return queries; } }

    /// <summary>Freezes all contributions, resolves storage once and checks complete, unique implementation coverage.</summary>
    /// <param name="services">Host services owning optional logging subscriptions.</param>
    /// <param name="persistence">Native storage resolution; resources remain caller-owned.</param>
    /// <returns>A prepared HTTP model. Mapping performs no storage resolution.</returns>
    /// <exception cref="ServiceBindingValidationException">Contract coverage or implementation ownership is invalid.</exception>
    public ServiceModel Build(IServiceCollection services, IServicePersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(persistence);
        _ = state.Value;
        var definition = ApiDefinition.Combine(definitions);
        var prepared = contributions.Select(prepare => prepare(persistence)).ToArray();
        var supplied = new HashSet<ApiEndpointId>();
        var errors = new List<Cohesive.Model.Serialization.DocumentValidationDiagnostic>();
        foreach (var contribution in prepared)
        foreach (var endpoint in contribution.Endpoints)
        {
            if (!definition.Endpoints.Any(item => ReferenceEquals(item.Operation, endpoint.Operation)))
                errors.Add(new("services.api.foreignEndpoint", DiagnosticSeverity.Error, $"Endpoint '{endpoint.Id}' is not from the declared API.", "/api/implementations"));
            if (!supplied.Add(endpoint.Id)) errors.Add(new("services.api.duplicateImplementation", DiagnosticSeverity.Error,
                $"Endpoint '{endpoint.Id}' has competing implementations.", "/api/implementations"));
        }
        foreach (var endpoint in definition.Endpoints)
            if (!supplied.Contains(endpoint.Id)) errors.Add(new("services.api.missingImplementation", DiagnosticSeverity.Error,
                $"Endpoint '{endpoint.Id}' has no implementation.", "/api/implementations"));
        if (errors.Count != 0) throw new ServiceBindingValidationException(new([.. errors]));
        foreach (var process in prepared.SelectMany(item => item.LoggedProcesses)) services.AddCohesiveTransitionFailureLogging(process);
        return new(definition, prepared);
    }
}

internal sealed record PreparedApiContribution(IReadOnlyList<ApiEndpoint> Endpoints,
    Action<IEndpointRouteBuilder> Map, IReadOnlyList<HostedServiceProcess> LoggedProcesses);

/// <summary>Typed implementation contribution to one separately owned API contract.</summary>
/// <typeparam name="TApi">Endpoint catalog type.</typeparam>
public sealed class ServiceApiImplementation<TApi>
{
    readonly ServiceModelBuilder model;
    readonly TApi api;
    readonly ApiDefinition definition;
    internal ServiceApiImplementation(ServiceModelBuilder model, TApi api, ApiDefinition definition)
    { this.model = model; this.api = api; this.definition = definition; }

    TEndpoint Select<TEndpoint>(Func<TApi, TEndpoint> select) where TEndpoint : ApiEndpoint
    {
        ArgumentNullException.ThrowIfNull(select);
        var endpoint = select(api) ?? throw new ArgumentException("Endpoint selector returned null.", nameof(select));
        if (!definition.Endpoints.Any(item => ReferenceEquals(item.Operation, endpoint.Operation)))
            throw new ArgumentException("Select an endpoint belonging to this API instance.", nameof(select));
        return endpoint;
    }

    /// <summary>Binds a typed query and records its bounded storage requirement once.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Nullable reference result.</typeparam>
    /// <param name="select">Typed API endpoint selector.</param><param name="query">Canonical query.</param>
    /// <param name="revision">Semantic query revision.</param><param name="maximumRows">Result row bound.</param>
    /// <param name="maximumBytes">Decoded-value bound.</param><param name="configure">Native input and result binding.</param>
    /// <returns>This contribution.</returns>
    public ServiceApiImplementation<TApi> Bind<TInput, TResult>(Func<TApi, ApiEndpoint<TResult>> select,
        RelationQuery<TInput, TResult?> query, ExecutionRevisionId revision, int maximumRows, long maximumBytes,
        Action<ApiQueryBinding<TInput, TResult>> configure) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var endpoint = Select(select);
        model.Query(query, revision, maximumRows, maximumBytes);
        model.Add(persistence =>
        {
            var reader = persistence.Query(query, maximumRows, maximumBytes);
            return new([endpoint], routes => configure(routes.MapApiQuery(endpoint, reader)), []);
        });
        return this;
    }

    /// <summary>Binds entity endpoints using the existing concurrency and transition pipeline.</summary>
    /// <typeparam name="TEntity">Canonical entity state.</typeparam><param name="entity">Exact domain entity.</param>
    /// <param name="partition">Trusted read partition; not authorization.</param>
    /// <param name="configure">Native response and transition policies using this API instance.</param>
    /// <returns>This contribution.</returns>
    public ServiceApiImplementation<TApi> Entity<TEntity>(DomainEntity<TEntity> entity, string partition,
        Action<TApi, TypedEntityApiBindings<TEntity>> configure) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(entity); ArgumentNullException.ThrowIfNull(configure);
        model.Add(persistence =>
        {
            var binding = new TypedEntityApiBindings<TEntity>(entity.Definition,
                persistence.Repository(ValueContract.FromShape(entity.Definition.Shape)), partition);
            configure(api, binding);
            if (binding.BoundEndpoints.Any(endpoint => !definition.Endpoints.Any(item => ReferenceEquals(item.Operation, endpoint.Operation))))
                throw new ArgumentException("Entity contribution must use endpoints from its declared API instance.");
            return new(binding.BoundEndpoints.ToArray(), binding.Map, []);
        });
        return this;
    }

    /// <summary>Binds an API projection to an exact ephemeral service process and its generated dependencies.</summary>
    /// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process output.</typeparam>
    /// <param name="select">Endpoint projected from this service operation.</param><param name="service">Separate service declaration.</param>
    /// <param name="process">Generated process definition.</param><param name="authority">Execution authority.</param>
    /// <param name="authorization">Invocation authorization.</param>
    /// <param name="logPrivateFailures">Explicit opt-in to protected host logging of native failure details.</param>
    /// <returns>This contribution.</returns>
    public ServiceApiImplementation<TApi> Bind<TInput, TResult>(Func<TApi, ApiEndpoint<TInput, TResult>> select, ServiceBuilder service,
        ProcessModel<TInput, TResult> process, string authority, IServiceInvocationAuthorization authorization,
        bool logPrivateFailures = false)
    {
        ArgumentNullException.ThrowIfNull(service); ArgumentNullException.ThrowIfNull(process);
        var endpoint = Select(select);
        model.Add(persistence =>
        {
            var hosted = service.Bind(process.Definition, persistence.Repository).Build(authority, authorization);
            var http = endpoint.Operation.Http ?? throw new ArgumentException("Process endpoint requires HTTP binding.");
            var projected = ServiceApiProjection.ProjectEphemeralProcess(hosted.Declaration, hosted.OperationId, hosted.Process, http,
                endpoint.Operation.ScopePolicies);
            if (projected.Id != endpoint.Id || endpoint.Operation.RequestType != typeof(TInput) || endpoint.Operation.ResponseType != typeof(TResult)
                || !projected.Operation.AuthorizationRequirements.SequenceEqual(endpoint.Operation.AuthorizationRequirements)
                || !projected.Operation.SemanticReferences.SequenceEqual(endpoint.Operation.SemanticReferences)
                || !projected.Operation.Results.Select(result => (result.Kind, result.BodyType, result.Http?.StatusCode, result.IsPrimary))
                    .SequenceEqual(endpoint.Operation.Results.Select(result => (result.Kind, result.BodyType, result.Http?.StatusCode, result.IsPrimary))))
                throw new ArgumentException("Endpoint must declare the exact terminal service operation and its result contracts.");
            return new([endpoint], routes => routes.MapServiceEphemeralProcess(hosted, http,
                scopePolicies: endpoint.Operation.ScopePolicies), logPrivateFailures ? [hosted] : []);
        });
        return this;
    }
}
