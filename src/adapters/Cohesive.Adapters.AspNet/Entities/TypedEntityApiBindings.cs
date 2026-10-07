using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>Typed authoring over existing entity API bindings; owns no request execution or persistence algorithm.</summary>
/// <typeparam name="TEntity">POCO materialized from the canonical entity observation.</typeparam>
/// <remarks>Construct once during registration. Not thread-safe. Endpoint handles can be declared separately
/// or supplied inline; combined overloads create the same ordinary portable endpoint declarations.</remarks>
public sealed class TypedEntityApiBindings<TEntity> where TEntity : notnull
{
    readonly EntityDefinition entity;
    readonly IEntityRepository repository;
    readonly string partition;
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
        this.repository = repository;
        this.partition = partition;
        materializer = ObservationMaterializer.For<TEntity>(entity.StateShape).Compile();
        options = new() { Entity = entity, RepositoryResolver = (_, _) => repository, ReadPartitionKeyResolver = _ => partition };
    }

    /// <summary>Binds a separately declared read endpoint with typed state and success response.</summary>
    /// <typeparam name="TResponse">Declared success body type.</typeparam>
    /// <param name="endpoint">Separately authored bodyless query endpoint for this entity.</param>
    /// <param name="respond">Maps materialized entity state to the declared success response, per request.</param>
    /// <returns>This registration session.</returns>
    /// <exception cref="ArgumentNullException">An endpoint or callback is null.</exception>
    /// <exception cref="ArgumentException">The declaration does not match the entity, operation or response contract.</exception>
    /// <exception cref="InvalidOperationException">The session is mapped or the endpoint is already bound.</exception>
    public TypedEntityApiBindings<TEntity> Get<TResponse>(ApiEndpoint endpoint, Func<TEntity, Ok<TResponse>> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        Validate<TResponse>(endpoint, ApiOperationKind.Query);
        Add(endpoint, EntityApiOperationBinding.Get(endpoint, (_, snapshot) => respond(Read(snapshot))));
        return this;
    }

    /// <summary>Declares and binds a read endpoint using the same handle-based path.</summary>
    /// <typeparam name="TResponse">Declared success body type.</typeparam>
    /// <param name="name">Portable operation name.</param>
    /// <param name="route">GET route template containing the entity identity parameter named id.</param>
    /// <param name="respond">Maps materialized entity state to the declared success response, per request.</param>
    /// <returns>This registration session.</returns>
    /// <exception cref="ArgumentNullException">An endpoint or callback is null.</exception>
    /// <exception cref="ArgumentException">The declaration does not match the entity, operation or response contract.</exception>
    /// <exception cref="InvalidOperationException">The session is mapped or the endpoint is already bound.</exception>
    public TypedEntityApiBindings<TEntity> Get<TResponse>(string name, string route, Func<TEntity, Ok<TResponse>> respond) =>
        Get(Cohesive.Api.Api.Define().Entity<TEntity>().Query(name).Route("GET", route)
            .RouteParameter<string>("id").Returns<TResponse>().Result(ApiResultKind.NotFound).Build(), respond);

    /// <summary>Binds legacy upsert-backed creation with typed initial state and response.</summary>
    /// <remarks>This method may replace existing state. Use CreateIfAbsent for an atomic absence guarantee.</remarks>
    /// <typeparam name="TResponse">Declared success body type.</typeparam>
    /// <param name="endpoint">Separately authored bodyless command declaring a Created response.</param>
    /// <param name="initialize">Creates fresh state using the binding's effective partition; must not reuse mutable state across requests.</param>
    /// <param name="identity">Selects the new entity identity from that initial state.</param>
    /// <param name="respond">Maps materialized entity state to the declared success response, per request.</param>
    /// <returns>This registration session.</returns>
    /// <exception cref="ArgumentNullException">An endpoint or callback is null.</exception>
    /// <exception cref="ArgumentException">The declaration does not match the entity, operation or response contract.</exception>
    /// <exception cref="InvalidOperationException">The session is mapped or the endpoint is already bound.</exception>
    public TypedEntityApiBindings<TEntity> Create<TResponse>(ApiEndpoint endpoint,
        Func<string, TEntity> initialize, Func<TEntity, string> identity, Func<TEntity, Created<TResponse>> respond)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(respond);
        Validate<TResponse>(endpoint, ApiOperationKind.Command, ApiResultKind.Created);
        Add(endpoint, EntityApiOperationBinding.Create(endpoint,
            (_, _) => { var value = initialize(partition); return entity.CreateState(identity(value), value, version: 1); },
            (_, snapshot) => respond(Read(snapshot))));
        return this;
    }

    /// <summary>Binds an implied canonical creation transition with an atomic absence fence.</summary>
    /// <typeparam name="TResponse">Declared created response.</typeparam>
    /// <param name="endpoint">Separately declared bodyless creation endpoint.</param>
    /// <param name="initialize">Materializes input state using the binding's trusted partition.</param>
    /// <param name="identity">Selects the new subject identity.</param>
    /// <param name="respond">Projects committed state onto the HTTP response.</param>
    /// <returns>This registration session.</returns>
    /// <exception cref="NotSupportedException">The repository cannot enforce absence atomically.</exception>
    /// <exception cref="ArgumentException">The endpoint contract is incompatible.</exception>
    public TypedEntityApiBindings<TEntity> CreateIfAbsent<TResponse>(ApiEndpoint endpoint,
        Func<string, TEntity> initialize, Func<TEntity, string> identity, Func<TEntity, Created<TResponse>> respond)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(respond);
        Validate<TResponse>(endpoint, ApiOperationKind.Command, ApiResultKind.Created);
        if (!repository.SupportsCreateIfAbsent)
            throw new NotSupportedException("Creation requires an atomic absence-fenced repository.");
        Add(endpoint, new CreateIfAbsentEntityApiOperationBinding(endpoint,
            (_, _) => { var value = initialize(partition); return entity.CreateState(identity(value), value); },
            (_, snapshot) => respond(Read(snapshot))));
        return this;
    }

    /// <summary>Declares and binds creation using the same handle-based path.</summary>
    /// <typeparam name="TResponse">Declared success body type.</typeparam>
    /// <param name="name">Portable operation name.</param>
    /// <param name="route">POST route template for creation.</param>
    /// <param name="initialize">Creates fresh state using the binding's effective partition; must not reuse mutable state across requests.</param>
    /// <param name="identity">Selects the new entity identity from that initial state.</param>
    /// <param name="respond">Maps materialized entity state to the declared success response, per request.</param>
    /// <returns>This registration session.</returns>
    /// <exception cref="ArgumentNullException">An endpoint or callback is null.</exception>
    /// <exception cref="ArgumentException">The declaration does not match the entity, operation or response contract.</exception>
    /// <exception cref="InvalidOperationException">The session is mapped or the endpoint is already bound.</exception>
    public TypedEntityApiBindings<TEntity> Create<TResponse>(string name, string route,
        Func<string, TEntity> initialize, Func<TEntity, string> identity, Func<TEntity, Created<TResponse>> respond) =>
        Create(Cohesive.Api.Api.Define().Entity<TEntity>().Command(name).Route("POST", route).Returns<TResponse>(ApiResultKind.Created).Build(),
            initialize, identity, respond);

    /// <summary>Starts typed binding of a separately declared exact transition endpoint.</summary>
    /// <typeparam name="TInput">Canonical transition input type.</typeparam>
    /// <typeparam name="TOutcome">Canonical transition outcome type.</typeparam>
    /// <param name="endpoint">Bodyless command referencing this exact transition and its response contracts.</param>
    /// <param name="transition">Exact authored transition to compile once when response binding completes.</param>
    /// <returns>A pending transition binding; complete it before mapping the session.</returns>
    /// <exception cref="ArgumentNullException">The transition is null.</exception>
    /// <exception cref="InvalidOperationException">The session has already been mapped.</exception>
    public TypedEntityTransitionBinding<TEntity, TInput, TOutcome> Transition<TInput, TOutcome>(
        ApiEndpoint endpoint, Transition<TEntity, TInput, TOutcome> transition) => new(this, endpoint, null, transition);

    /// <summary>Starts combined transition declaration/binding; response type is supplied by OnApplied.</summary>
    /// <typeparam name="TInput">Canonical transition input type.</typeparam>
    /// <typeparam name="TOutcome">Canonical transition outcome type.</typeparam>
    /// <param name="name">Portable operation name.</param>
    /// <param name="route">POST route template containing the entity identity parameter named id.</param>
    /// <param name="transition">Exact authored transition to compile once when response binding completes.</param>
    /// <returns>A pending transition binding; complete it before mapping the session.</returns>
    /// <exception cref="ArgumentNullException">The transition is null.</exception>
    /// <exception cref="InvalidOperationException">The session has already been mapped.</exception>
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

    /// <summary>Materializes state through the session's prepared canonical materializer.</summary>
    /// <param name="snapshot">Repository snapshot to materialize.</param>
    /// <returns>The typed state.</returns>
    internal TEntity Read(EntitySnapshot snapshot) => materializer.Materialize(snapshot.Entity.Observation);

    /// <summary>Checks the endpoint against this binding session before registration.</summary>
    /// <typeparam name="TResponse">Required response body type.</typeparam>
    /// <param name="endpoint">Declaration to validate.</param>
    /// <param name="kind">Required query or command kind.</param>
    /// <param name="resultKind">Required primary result kind; defaults to Success.</param>
    internal void Validate<TResponse>(ApiEndpoint endpoint, ApiOperationKind kind, ApiResultKind resultKind = ApiResultKind.Success)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Operation.Entity != EntityTypeName.From<TEntity>()
            || endpoint.Operation.Kind != kind || endpoint.Operation.ResponseType != typeof(TResponse)
            || endpoint.Operation.RequestType != typeof(void) || endpoint.Operation.PrimaryResult.Kind != resultKind)
            throw new ArgumentException("Endpoint entity, kind, primary result, response type or body contract does not match the typed binding. These overloads accept bodyless endpoints.", nameof(endpoint));
        if (bound.Contains(endpoint.Id)) throw new InvalidOperationException($"Endpoint '{endpoint.Id}' is already bound.");
    }

    /// <summary>Registers one validated endpoint and its execution binding.</summary>
    /// <param name="endpoint">Canonical endpoint handle.</param>
    /// <param name="binding">Execution binding for the handle.</param>
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
    Func<EntityApiRequestContext, TInput>? input;
    Func<TEntity, TOutcome, IResult>? applied;
    ApiEndpoint? resolved;
    bool completed;
    Action<ApiEndpoint>? validate;

    /// <summary>Starts one pending transition registration in the parent session.</summary>
    /// <param name="owner">Mutable parent registration session.</param>
    /// <param name="endpoint">Existing endpoint, or null when declaring inline.</param>
    /// <param name="declaration">Inline declaration builder, or null when an endpoint is supplied.</param>
    /// <param name="transition">Canonical transition authority.</param>
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
    /// <param name="create">Per-request conversion from route/request context into canonical input.</param>
    /// <returns>This pending transition binding.</returns>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="InvalidOperationException">Input is already configured or the binding is complete.</exception>
    public TypedEntityTransitionBinding<TEntity, TInput, TOutcome> Input(Func<EntityApiRequestContext, TInput> create)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(create);
        if (input is not null) throw new InvalidOperationException("Input is already configured.");
        input = create;
        return this;
    }

    /// <summary>Projects committed state and typed outcome into the declared success body.</summary>
    /// <typeparam name="TResponse">Declared success body type.</typeparam>
    /// <param name="respond">Maps committed state (or unchanged state for NoChange) and outcome to HTTP 200.</param>
    /// <returns>This pending transition binding.</returns>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="ArgumentException">The endpoint response or exact transition reference differs.</exception>
    /// <exception cref="InvalidOperationException">Success handling is configured or the binding is complete.</exception>
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
    /// <typeparam name="TResponse">Declared conflict body type.</typeparam>
    /// <param name="respond">Maps an admission/domain rejection outcome to the declared HTTP 409 response.</param>
    /// <returns>The parent session after compiling and registering this transition.</returns>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="ArgumentException">The endpoint does not declare the required conflict contract.</exception>
    /// <exception cref="InvalidOperationException">Input or success handling is absent, or registration is already complete.</exception>
    /// <exception cref="TransitionApiPreparationException">Canonical transition compilation failed; diagnostics are retained.</exception>
    /// <remarks>Only admission/domain rejection is routed here. Unexpected decisions fail, rather than masquerading as domain rejection.</remarks>
    public TypedEntityApiBindings<TEntity> OnRejected<TResponse>(Func<TOutcome, Conflict<TResponse>> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        return Complete<TResponse>(outcome => respond(outcome));
    }

    /// <summary>Completes domain-rejection handling with a Problem Details response.</summary>
    /// <param name="respond">Maps an admission/domain rejection outcome to the declared HTTP 409 response.</param>
    /// <returns>The parent session after compiling and registering this transition.</returns>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="ArgumentException">The endpoint does not declare the required conflict contract.</exception>
    /// <exception cref="InvalidOperationException">Input or success handling is absent, or registration is already complete.</exception>
    /// <exception cref="TransitionApiPreparationException">Canonical transition compilation failed; diagnostics are retained.</exception>
    /// <remarks>The callback must return status 409. Concurrency conflicts use the same body shape.</remarks>
    public TypedEntityApiBindings<TEntity> OnRejected(Func<TOutcome, ProblemHttpResult> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        return Complete<ProblemDetails>(outcome =>
        {
            var result = respond(outcome);
            if (result.StatusCode != StatusCodes.Status409Conflict)
                throw new InvalidOperationException("A domain rejection must return HTTP 409.");
            return result;
        });
    }

    /// <summary>Prepares and registers the transition using the selected conflict contract.</summary>
    /// <typeparam name="TResponse">Conflict body type.</typeparam>
    /// <param name="respond">Per-request rejection response factory.</param>
    /// <returns>The parent registration session.</returns>
    TypedEntityApiBindings<TEntity> Complete<TResponse>(Func<TOutcome, IResult> respond)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(respond);
        var createInput = input ?? throw new InvalidOperationException("Configure Input before completing the binding.");
        var onApplied = applied ?? throw new InvalidOperationException("Configure OnApplied before completing the binding.");
        resolved = endpoint ?? declaration!.Result<TResponse>(ApiResultKind.Conflict).Result(ApiResultKind.NotFound).Build();
        validate!(resolved);
        if (!resolved.Operation.Results.Any(result => result.Kind == ApiResultKind.Conflict && result.BodyType == typeof(TResponse)))
            throw new ArgumentException("Endpoint must declare the typed conflict response.");
        var compilation = transition.Compile();
        if (!compilation.IsSuccessful)
            throw new TransitionApiPreparationException(resolved.Name, compilation);
        var binding = EntityApiOperationBinding.Transition(resolved!, compilation.Plan!,
            (context, _) => createInput(context),
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
