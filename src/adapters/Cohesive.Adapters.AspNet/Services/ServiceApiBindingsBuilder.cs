using Cohesive.Relations.Execution;
using Cohesive.Api.Services;
using Cohesive.Transitions.Authoring;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Execution;
using Cohesive.Host.Services;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Microsoft.AspNetCore.Http;
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
public sealed class ServiceApiBindingsBuilder
{
    /// <summary>Portable admission and query evidence; can be inspected without persistence.</summary>
    public ServiceImplementationPlan Plan { get; } = new();
    readonly object buildGate = new();
    ServiceApiBindings? built;
    IServiceCollection? builtServices;
    IServicePersistence? builtPersistence;
    readonly List<Func<IServiceCollection, IServicePersistence, PreparedApiContribution>> contributions = [];
    readonly AuthoringState<ServiceApiBindingsBuilder> state;

    /// <summary>Starts an empty, host-local implementation composition.</summary>
    public ServiceApiBindingsBuilder() => state = new(() => this);

    /// <summary>Contributes implementations to a catalog that owns its definition; repeated contributions compose the same contract.</summary>
    /// <typeparam name="TApi">Application API declaration type.</typeparam>
    /// <param name="api">Shared instance-owned contract.</param><param name="configure">Setup-only contribution.</param>
    /// <returns>This builder.</returns>
    public ServiceApiBindingsBuilder Implement<TApi>(TApi api, Action<ServiceApiImplementation<TApi>> configure) where TApi : IApiDeclaration
    {
        ArgumentNullException.ThrowIfNull(api);
        return Implement(api, api.Definition, configure);
    }

    /// <summary>Contributes to a separately declared API using an arbitrary typed catalog.</summary>
    /// <typeparam name="TApi">Typed endpoint catalog.</typeparam><param name="api">Catalog used by selectors.</param>
    /// <param name="definition">Explicit contract authority.</param><param name="configure">Setup-only contribution.</param>
    /// <returns>This builder.</returns>
    public ServiceApiBindingsBuilder Implement<TApi>(TApi api, ApiDefinition definition, Action<ServiceApiImplementation<TApi>> configure)
    {
        ArgumentNullException.ThrowIfNull(api); ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(configure);
        return state.Edit(() =>
        {
            var count = contributions.Count;
            try { Plan.Contribute(definition, () => configure(new(this, api, definition))); }
            catch { contributions.RemoveRange(count, contributions.Count - count); throw; }
            return this;
        });
    }

    internal void Add(Func<IServicePersistence, PreparedApiContribution> prepare) => state.Edit(() => { contributions.Add((_, persistence) => prepare(persistence)); return this; });
    internal void Add(Func<IServiceCollection, IServicePersistence, PreparedApiContribution> prepare) =>
        state.Edit(() => { contributions.Add(prepare); return this; });
    /// <summary>Freezes authoring and validates offline endpoint coverage without preparing native resources.</summary>
    /// <exception cref="ServiceBindingValidationException">Coverage is incomplete or inconsistent.</exception>
    public void Complete()
    {
        _ = state.Value;
        _ = Plan.Definition;
    }

    /// <summary>Freezes all contributions, resolves storage once and checks complete, unique implementation coverage.</summary>
    /// <param name="services">Host services owning optional logging subscriptions.</param>
    /// <param name="persistence">Native storage resolution; resources remain caller-owned.</param>
    /// <returns>A prepared HTTP model. Mapping performs no storage resolution.</returns>
    /// <exception cref="ServiceBindingValidationException">Contract coverage or implementation ownership is invalid.</exception>
    public ServiceApiBindings Build(IServiceCollection services, IServicePersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(persistence);
        _ = state.Value;
        lock (buildGate)
        {
            if (built is not null)
            {
                if (!ReferenceEquals(services, builtServices) || !ReferenceEquals(persistence, builtPersistence))
                    throw ServiceImplementationPlan.Error("services.api.hostMismatch", "Build a separate model for a different host or persistence scope.");
                return built;
            }
            var definition = Plan.Definition;
            var prepared = contributions.Select(prepare => prepare(services, persistence)).ToArray();
            foreach (var process in prepared.SelectMany(item => item.LoggedProcesses)) services.AddCohesiveTransitionFailureLogging(process);
            builtServices = services;
            builtPersistence = persistence;
            return built = new(definition, prepared);
        }
    }
}

internal sealed record PreparedApiContribution(IReadOnlyList<ApiEndpoint> Endpoints,
    Action<IEndpointRouteBuilder> Map, IReadOnlyList<HostedServiceProcess> LoggedProcesses);

/// <summary>Typed implementation contribution to one separately owned API contract.</summary>
/// <typeparam name="TApi">Endpoint catalog type.</typeparam>
public sealed class ServiceApiImplementation<TApi>
{
    readonly ServiceApiBindingsBuilder model;
    readonly TApi api;
    readonly ApiDefinition definition;
    internal ServiceApiImplementation(ServiceApiBindingsBuilder model, TApi api, ApiDefinition definition)
    { this.model = model; this.api = api; this.definition = definition; }

    TEndpoint Select<TEndpoint>(Func<TApi, TEndpoint> select) where TEndpoint : ApiEndpoint
    {
        ArgumentNullException.ThrowIfNull(select);
        var endpoint = select(api) ?? throw ServiceImplementationPlan.Error("services.api.foreignEndpoint", "Endpoint selector returned null.");
        if (!definition.Endpoints.Any(item => ReferenceEquals(item.Operation, endpoint.Operation)))
            throw ServiceImplementationPlan.Error("services.api.foreignEndpoint", "Select an endpoint belonging to this API instance.");
        return endpoint;
    }

    /// <summary>Binds a typed query and records its bounded storage requirement once.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Declared result.</typeparam>
    /// <param name="select">Typed API endpoint selector.</param><param name="query">Canonical query.</param>
    /// <param name="revision">Semantic query revision.</param><param name="maximumRows">Result row bound.</param>
    /// <param name="consistency">Required consistency across every read contributing to the result.</param>
    /// <param name="maximumBytes">Decoded-value bound.</param><param name="configure">Native input and result binding.</param>
    /// <returns>This contribution.</returns>
    public ServiceApiImplementation<TApi> Bind<TInput, TResult>(Func<TApi, ApiEndpoint<TResult>> select,
        RelationQuery<TInput, TResult?> query, ExecutionRevisionId revision, int maximumRows, long maximumBytes,
        Action<ApiQueryBinding<TInput, TResult>> configure, RelationQueryConsistency consistency)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var endpoint = Select(select);
        model.Plan.Query(query, revision, maximumRows, maximumBytes, consistency);
        model.Plan.Implement(definition, endpoint);
        model.Add(persistence =>
        {
            var reader = ServiceQueryConsistencyValidation.Require(persistence.Query(query, maximumRows, maximumBytes, consistency), consistency);
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
        var binding = new TypedEntityApiBindings<TEntity>(entity.Definition, partition);
        configure(api, binding);
        foreach (var endpoint in binding.BoundEndpoints) model.Plan.Implement(definition, endpoint);
        model.Add(persistence =>
        {
            binding.Attach(persistence.Repository(ValueContract.FromShape(entity.Definition.Shape)));
            return new(binding.BoundEndpoints.ToArray(), binding.Map, []);
        });
        return this;
    }

    /// <summary>Binds a declared admission/poll pair to a complete native service runtime.</summary>
    /// <typeparam name="TRequest">Caller-owned command input.</typeparam><typeparam name="TResult">Terminal response view.</typeparam>
    /// <param name="select">Linked endpoints belonging to this API contribution.</param>
    /// <param name="runtime">Complete service runtime; owns admission and protected result reads.</param>
    /// <param name="bind">Pure input and retry-identity projection.</param><param name="project">Pure authorized terminal projection.</param>
    /// <returns>This contribution.</returns>
    /// <exception cref="ServiceBindingValidationException">Endpoints are foreign, duplicated, or reference a different service.</exception>
    public ServiceApiImplementation<TApi> Bind<TRequest, TResult>(Func<TApi, ServiceAcceptedProcessApi<TRequest, TResult>> select,
        ServiceRuntime runtime,
        Func<HttpContext, TRequest, (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
            ProcessContinuationIdentity Continuation, ObservationValue Input)> bind, Func<PortableValue, TResult> project)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(select); ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(bind); ArgumentNullException.ThrowIfNull(project);
        var pair = select(api) ?? throw ServiceImplementationPlan.Error("services.api.foreignEndpoint", "Select a linked API contract.");
        var start = Select(_ => pair.Start);
        var result = Select(_ => pair.Result);
        if (runtime.Declaration.Reference != pair.Declaration.Reference)
            throw ServiceImplementationPlan.Error("services.api.processMismatch", "The linked API and runtime require the same exact service.");
        model.Plan.Contribute(definition, () =>
        {
            model.Plan.Implement(definition, start);
            model.Plan.Implement(definition, result);
            model.Add(_ => new([start, result], routes => routes.MapServiceAcceptedProcess(pair, runtime, bind, project), []));
        });
        return this;
    }

    /// <summary>Binds a standard typed command envelope and decodes the declared process result automatically.</summary>
    /// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Declared process result.</typeparam>
    /// <param name="select">Colocated API pair using the standard command envelope.</param>
    /// <param name="authority">Execution authority.</param><param name="authorization">Shared HTTP and runtime admission policy.</param>
    /// <param name="maximumExecutions">Bound on retained host instances.</param>
    /// <param name="logPrivateFailures">Opt in to scoped private operator diagnostics.</param>
    /// <param name="authorizationPolicyResolver">Optional existing native policies; otherwise scoped policies use authorization.</param>
    /// <returns>This contribution; preparation remains host-scoped.</returns>
    public ServiceApiImplementation<TApi> BindHosted<TInput, TResult>(
        Func<TApi, ColocatedProcessApi<ServiceProcessCommand<TInput>, TInput, TResult>> select,
        string authority, IServiceInvocationAuthorization authorization, int maximumExecutions,
        bool logPrivateFailures = false, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null) where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(select);
        var pair = select(api) ?? throw ServiceImplementationPlan.Error("services.api.foreignEndpoint", "Select a linked API contract.");
        var resultContract = pair.Process.Definition.Definition.Result;
        return BindHosted(_ => pair, authority, authorization, maximumExecutions,
            bind: (_, command) =>
            {
                if (string.IsNullOrWhiteSpace(command.CommandId) || string.IsNullOrWhiteSpace(command.IdempotencyKey)
                    || string.IsNullOrWhiteSpace(command.InstanceId) || string.IsNullOrWhiteSpace(command.AttemptId) || command.Input is null)
                    throw new BadHttpRequestException("A process command requires retry identities and input.");
                return (new(command.CommandId), new(command.IdempotencyKey), new(new(command.InstanceId), new(command.AttemptId)),
                    ObservationValue.FromObject(command.Input));
            }, project: value =>
            {
                var decoded = Cohesive.Relations.Execution.HostedQueryValueAdapter.Decode<TResult>(value, resultContract);
                return decoded.Type == ResultType.Success ? decoded.Success!
                    : throw new InvalidOperationException("The retained process result does not match its declared result contract.");
            }, logPrivateFailures, authorizationPolicyResolver);
    }

    /// <summary>Binds a colocated process API using its retained service and generated dependency recipe.</summary>
    /// <typeparam name="TRequest">HTTP command.</typeparam><typeparam name="TInput">Process input.</typeparam>
    /// <typeparam name="TResult">Process result.</typeparam>
    /// <param name="select">Pair owned by this API.</param><param name="authority">Execution authority.</param>
    /// <param name="authorization">Per-operation authorization.</param><param name="maximumExecutions">Retained instance bound.</param>
    /// <param name="bind">Retry identities and input projection.</param><param name="project">Authorized result projection.</param>
    /// <param name="logPrivateFailures">Opt in to scoped operator diagnostics.</param>
    /// <param name="authorizationPolicyResolver">HTTP policy mapping.</param><returns>This contribution.</returns>
    public ServiceApiImplementation<TApi> BindHosted<TRequest, TInput, TResult>(
        Func<TApi, ColocatedProcessApi<TRequest, TInput, TResult>> select,
        string authority, IServiceInvocationAuthorization authorization, int maximumExecutions,
        Func<HttpContext, TRequest, (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
            ProcessContinuationIdentity Continuation, ObservationValue Input)> bind, Func<PortableValue, TResult> project,
        bool logPrivateFailures = false, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(select);
        var pair = select(api) ?? throw ServiceImplementationPlan.Error("services.api.foreignEndpoint", "Select a linked API contract.");
        return BindHosted(_ => pair, pair.Service, pair.Process, authority, authorization, maximumExecutions,
            bind, project, logPrivateFailures, authorizationPolicyResolver);
    }

    /// <summary>Attaches bounded host-owned execution to a declared admission/poll pair, preparing only at Build.</summary>
    /// <typeparam name="TRequest">HTTP command.</typeparam><typeparam name="TInput">Process input.</typeparam>
    /// <typeparam name="TResult">Process and response result.</typeparam>
    /// <param name="select">Pair owned by this API.</param><param name="service">Exact service declaration.</param>
    /// <param name="process">Generated process with dependency recipes.</param><param name="authority">Execution authority.</param>
    /// <param name="authorization">Per-operation authorization.</param><param name="maximumExecutions">Bound on retained host instances.</param>
    /// <param name="bind">Caller-owned retry identity and input projection.</param><param name="project">Authorized result projection.</param>
    /// <param name="logPrivateFailures">Opt in to scoped private operator diagnostics.</param>
    /// <param name="authorizationPolicyResolver">Native HTTP policy mapping for declared authorization requirements.</param>
    /// <returns>This contribution; no worker or storage is resolved during authoring.</returns>
    public ServiceApiImplementation<TApi> BindHosted<TRequest, TInput, TResult>(
        Func<TApi, ServiceAcceptedProcessApi<TRequest, TResult>> select, ServiceBuilder service,
        ProcessModel<TInput, TResult> process, string authority, IServiceInvocationAuthorization authorization, int maximumExecutions,
        Func<HttpContext, TRequest, (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
            ProcessContinuationIdentity Continuation, ObservationValue Input)> bind, Func<PortableValue, TResult> project,
        bool logPrivateFailures = false, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(select); ArgumentNullException.ThrowIfNull(service); ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(bind); ArgumentNullException.ThrowIfNull(project);
        if (maximumExecutions <= 0) throw new ArgumentOutOfRangeException(nameof(maximumExecutions));
        var pair = select(api) ?? throw ServiceImplementationPlan.Error("services.api.foreignEndpoint", "Select a linked API contract.");
        var start = Select(_ => pair.Start);
        var result = Select(_ => pair.Result);
        var declaration = service.Build();
        if (pair.Declaration.Reference != declaration.Reference
            || declaration.GetDefinition<ServiceDefinition>().Operations.Any(operation => operation switch
            {
                ServiceProcessOperation entry => entry.Process != process.Definition.Reference
                    || entry.Execution is not { Lifetime: ProcessExecutionLifetime.Host, Completion: ServiceProcessCompletion.Admission },
                ServiceProcessResultOperation read => read.Process != process.Definition.Reference,
                _ => true
            }))
            throw ServiceImplementationPlan.Error("services.api.processMismatch", "Host binding requires exact process references and host-owned admission.");
        model.Plan.Contribute(definition, () =>
        {
            model.Plan.Implement(definition, start);
            model.Plan.Implement(definition, result);
            model.Add((services, persistence) =>
            {
                var prepared = service.PrepareProcess(process.Definition, persistence.Repository);
                if (logPrivateFailures) services.AddCohesiveTransitionFailureLogging(prepared);
                var worker = services.AddCohesiveHostProcessWorker(prepared, maximumExecutions);
                var runtime = worker.Bind(declaration, authority, authorization);
                var policies = authorizationPolicyResolver ?? ServiceProcessHttpAuthorization.Register(services, pair, authorization);
                return new([start, result], routes => routes.MapServiceAcceptedProcess(pair, runtime, bind, project, policies), []);
            });
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
    public ServiceApiImplementation<TApi> Bind<TInput, TResult>(Func<TApi, ServiceProcessEndpoint<TInput, TResult>> select, ServiceBuilder service,
        ProcessModel<TInput, TResult> process, string authority, IServiceInvocationAuthorization authorization,
        bool logPrivateFailures = false)
    {
        ArgumentNullException.ThrowIfNull(service); ArgumentNullException.ThrowIfNull(process);
        var endpoint = Select(select);
        var declaration = service.Build();
        var reference = declaration.Reference;
        if (endpoint.Service != reference || endpoint.Process != process.Definition.Reference)
            throw ServiceImplementationPlan.Error("services.api.processMismatch", "Endpoint must project this exact service and process authority.");
        var operations = declaration.GetDefinition<ServiceDefinition>().Operations;
        if (operations.Length != 1 || operations[0].Id != endpoint.OperationId)
            throw ServiceImplementationPlan.Error("services.api.operationMismatch", "This finite host requires the endpoint's single declared service operation.");
        var http = endpoint.Operation.Http ?? throw ServiceImplementationPlan.Error("services.api.httpMissing", "Process endpoint requires HTTP binding.");
        model.Plan.Implement(definition, endpoint);
        model.Add(persistence =>
        {
            var hosted = service.Bind(process.Definition, persistence.Repository).Build(authority, authorization);
            if (hosted.OperationId != endpoint.OperationId)
                throw ServiceImplementationPlan.Error("services.api.operationMismatch", "The endpoint and hosted service select different operations.");
            return new([endpoint], routes => routes.MapServiceEphemeralProcess(hosted, http,
                scopePolicies: endpoint.Operation.ScopePolicies), logPrivateFailures ? [hosted] : []);
        });
        return this;
    }
}
