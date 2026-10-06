using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>Typed authoring over existing entity API bindings; owns no request execution or persistence algorithm.</summary>
/// <typeparam name="TEntity">POCO materialized from the canonical entity observation.</typeparam>
/// <remarks>Construct once during registration. Not thread-safe. Endpoint handles can be declared separately
/// or supplied inline; combined overloads create the same ordinary portable endpoint declarations.</remarks>
public sealed class TypedEntityApiBindings<TEntity> where TEntity : notnull
{
    readonly EntityDefinition entity;
    readonly ObservationMaterializer<TEntity> materializer;
    readonly EntityApiEndpointOptions options;
    readonly List<ApiEndpoint> endpoints = [];
    readonly HashSet<ApiEndpointId> bound = [];
    bool mapped;
    int pendingTransitions;

    /// <summary>Creates a registration-scoped typed binding session.</summary>
    /// <param name="entity">Canonical entity authority.</param>
    /// <param name="repository">Caller-owned repository for that exact definition.</param>
    /// <param name="partition">Explicit point-read partition; not an authorization policy.</param>
    public TypedEntityApiBindings(EntityDefinition entity, IEntityRepository repository, string partition)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (!ReferenceEquals(entity, repository.EntityDefinition))
            throw new ArgumentException("Repository must use the supplied canonical entity definition.", nameof(repository));
        this.entity = entity;
        materializer = ObservationMaterializer.For<TEntity>(entity.StateShape).Compile();
        options = new() { Entity = entity, RepositoryResolver = (_, _) => repository, ReadPartitionKeyResolver = _ => partition };
    }

    /// <summary>Binds a separately declared read endpoint with typed state and success response.</summary>
    public TypedEntityApiBindings<TEntity> Get<TResponse>(ApiEndpoint endpoint, Func<TEntity, Ok<TResponse>> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        Validate<TResponse>(endpoint, ApiOperationKind.Query);
        Add(endpoint, EntityApiOperationBinding.Get(endpoint, (_, snapshot) => respond(Read(snapshot))));
        return this;
    }

    /// <summary>Declares and binds a read endpoint using the same handle-based path.</summary>
    public TypedEntityApiBindings<TEntity> Get<TResponse>(string name, string route, Func<TEntity, Ok<TResponse>> respond) =>
        Get(Cohesive.Api.Api.Define().Entity<TEntity>().Query(name).Route("GET", route)
            .RouteParameter<string>("id").Returns<TResponse>().Build(), respond);

    /// <summary>Binds creation with typed initial state and response; identity is selected explicitly from the new state.</summary>
    public TypedEntityApiBindings<TEntity> Create<TResponse>(ApiEndpoint endpoint,
        Func<TEntity> initialize, Func<TEntity, string> identity, Func<TEntity, Created<TResponse>> respond)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(respond);
        Validate<TResponse>(endpoint, ApiOperationKind.Command);
        Add(endpoint, EntityApiOperationBinding.Create(endpoint,
            (_, _) => { var value = initialize(); return entity.CreateState(identity(value), value, version: 1); },
            (_, snapshot) => respond(Read(snapshot))));
        return this;
    }

    /// <summary>Declares and binds creation using the same handle-based path.</summary>
    public TypedEntityApiBindings<TEntity> Create<TResponse>(string name, string route,
        Func<TEntity> initialize, Func<TEntity, string> identity, Func<TEntity, Created<TResponse>> respond) =>
        Create(Cohesive.Api.Api.Define().Entity<TEntity>().Command(name).Route("POST", route).Returns<TResponse>().Build(),
            initialize, identity, respond);

    /// <summary>Starts typed binding of a separately declared exact transition endpoint.</summary>
    public TypedEntityTransitionBinding<TEntity, TInput, TOutcome> Transition<TInput, TOutcome>(
        ApiEndpoint endpoint, Transition<TEntity, TInput, TOutcome> transition) => new(this, endpoint, null, transition);

    /// <summary>Starts combined transition declaration/binding; response type is supplied by OnApplied.</summary>
    public TypedEntityTransitionBinding<TEntity, TInput, TOutcome> Transition<TInput, TOutcome>(
        string name, string route, Transition<TEntity, TInput, TOutcome> transition) =>
        new(this, null, Cohesive.Api.Api.Define().Entity<TEntity>().Command(name).Route("POST", route)
            .RouteParameter<string>("id").Transition(transition.Reference), transition);

    /// <summary>Maps the collected declarations once through the existing entity API execution bindings.</summary>
    /// <param name="routes">Native ASP.NET endpoint builder.</param>
    public void Map(IEndpointRouteBuilder routes)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(routes);
        if (pendingTransitions != 0) throw new InvalidOperationException("Complete every transition binding before mapping.");
        routes.MapEntityApiDefinition(new ApiDefinition(endpoints), options);
        mapped = true;
    }

    internal void BeginTransition() { EnsureMutable(); pendingTransitions++; }
    internal void CompleteTransition() => pendingTransitions--;

    internal TEntity Read(EntitySnapshot snapshot) => materializer.Materialize(snapshot.Entity.Observation);

    internal void Validate<TResponse>(ApiEndpoint endpoint, ApiOperationKind kind)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Operation.Entity != EntityTypeName.From<TEntity>()
            || endpoint.Operation.Kind != kind || endpoint.Operation.ResponseType != typeof(TResponse)
            || endpoint.Operation.RequestType != typeof(void))
            throw new ArgumentException("Endpoint entity, kind, response type or body contract does not match the typed binding. These overloads accept bodyless endpoints.", nameof(endpoint));
        if (bound.Contains(endpoint.Id)) throw new InvalidOperationException($"Endpoint '{endpoint.Id}' is already bound.");
    }

    internal void Add(ApiEndpoint endpoint, EntityApiOperationBinding binding)
    {
        EnsureMutable();
        if (!bound.Add(endpoint.Id)) throw new InvalidOperationException($"Endpoint '{endpoint.Id}' is already bound.");
        options.Bind(binding);
        endpoints.Add(endpoint);
    }

    void EnsureMutable()
    {
        if (mapped) throw new InvalidOperationException("The binding session has already been mapped.");
    }
}

/// <summary>Typed transition response authoring, lowered to the existing exact-plan HTTP binding.</summary>
/// <typeparam name="TEntity">Entity POCO.</typeparam>
/// <typeparam name="TInput">Canonical transition input.</typeparam>
/// <typeparam name="TOutcome">Canonical transition outcome.</typeparam>
public sealed class TypedEntityTransitionBinding<TEntity, TInput, TOutcome> where TEntity : notnull
{
    readonly TypedEntityApiBindings<TEntity> owner;
    readonly ApiEndpoint? endpoint;
    readonly OperationBuilder<EntityApiBuilder<TEntity>>? declaration;
    readonly Transition<TEntity, TInput, TOutcome> transition;
    Func<string, TInput>? input;
    Func<TEntity, TOutcome, IResult>? applied;
    ApiEndpoint? resolved;
    bool completed;
    Action<ApiEndpoint>? validate;

    internal TypedEntityTransitionBinding(TypedEntityApiBindings<TEntity> owner, ApiEndpoint? endpoint,
        OperationBuilder<EntityApiBuilder<TEntity>>? declaration, Transition<TEntity, TInput, TOutcome> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        this.owner = owner;
        this.endpoint = endpoint;
        this.declaration = declaration;
        this.transition = transition;
        owner.BeginTransition();
    }

    /// <summary>Projects the required route identity into the exact transition input type.</summary>
    public TypedEntityTransitionBinding<TEntity, TInput, TOutcome> Input(Func<string, TInput> create)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(create);
        input = create;
        return this;
    }

    /// <summary>Projects committed state and typed outcome into the declared success body.</summary>
    public TypedEntityTransitionBinding<TEntity, TInput, TOutcome> OnApplied<TResponse>(Func<TEntity, TOutcome, Ok<TResponse>> respond)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(respond);
        if (applied is not null) throw new InvalidOperationException("Success response is already configured.");
        validate = handle =>
        {
            owner.Validate<TResponse>(handle, ApiOperationKind.Command);
            if (handle.Operation.TransitionReference != transition.Reference)
                throw new ArgumentException("Endpoint must reference the exact authored transition.");
        };
        if (endpoint is not null) validate(endpoint);
        else declaration!.Returns<TResponse>();
        applied = (state, outcome) => respond(state, outcome);
        return this;
    }

    /// <summary>Completes the binding with typed domain-rejection handling and returns the parent session.</summary>
    /// <remarks>Only admission/domain rejection is routed here. Unexpected decisions fail, rather than masquerading as domain rejection.</remarks>
    public TypedEntityApiBindings<TEntity> OnRejected<TResponse>(Func<TOutcome, Conflict<TResponse>> respond)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(respond);
        var createInput = input ?? throw new InvalidOperationException("Configure Input before completing the binding.");
        var onApplied = applied ?? throw new InvalidOperationException("Configure OnApplied before completing the binding.");
        resolved = endpoint ?? declaration!.Result<TResponse>(ApiResultKind.Conflict).Build();
        validate!(resolved);
        if (!resolved.Operation.Results.Any(result => result.Kind == ApiResultKind.Conflict && result.BodyType == typeof(TResponse)))
            throw new ArgumentException("Endpoint must declare the typed conflict response.");
        var compilation = transition.Compile();
        if (!compilation.IsSuccessful)
            throw new InvalidOperationException(string.Join("; ", compilation.Validation.Diagnostics));
        var binding = EntityApiOperationBinding.Transition(resolved!, compilation.Plan!,
            (context, _) => createInput(context.EntityId ?? throw new InvalidOperationException("Route identity is required.")),
            (context, snapshot) =>
            {
                var decision = context.Decision ?? throw new InvalidOperationException("Transition decision is required.");
                var value = decision.Outcome?.Value ?? throw new InvalidOperationException("Typed transition outcome is required.");
                var outcome = value.Deserialize<TOutcome>() ?? throw new InvalidOperationException("Typed transition outcome cannot be null.");
                return decision.Kind switch
                {
                    TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange => onApplied(owner.Read(snapshot), outcome),
                    TransitionDecisionKind.AdmissionRejected or TransitionDecisionKind.DomainRejected => respond(outcome),
                    _ => throw new InvalidOperationException($"Unexpected transition decision '{decision.Kind}'.")
                };
            });
        owner.Add(resolved!, binding);
        completed = true;
        owner.CompleteTransition();
        return owner;
    }

    void EnsureMutable()
    {
        if (completed) throw new InvalidOperationException("Transition binding is already complete.");
    }
}
