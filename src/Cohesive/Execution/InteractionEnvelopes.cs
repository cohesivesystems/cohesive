using System.Text.Json.Serialization;

namespace Cohesive.Execution;

/// <summary>Authority and optional tenant boundary within which an interaction is meaningful.</summary>
public sealed record InteractionAuthorityScope
{
    /// <summary>Creates an interaction authority scope.</summary>
    /// <param name="authority">Stable identity of the governing authority boundary.</param>
    /// <param name="tenant">Optional stable tenant identity within the authority.</param>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authority"/> is empty or white-space, or <paramref name="tenant"/> is white-space.
    /// </exception>
    [JsonConstructor]
    public InteractionAuthorityScope(string authority, string? tenant = null)
    {
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        if (tenant is not null && string.IsNullOrWhiteSpace(tenant))
        {
            throw new ArgumentException("An optional tenant identity cannot be empty or white-space.", nameof(tenant));
        }

        Tenant = tenant;
    }

    /// <summary>Stable governing authority identity.</summary>
    public string Authority { get; }

    /// <summary>Optional stable tenant identity.</summary>
    public string? Tenant { get; }
}

/// <summary>Typed ordering partition and key demanded by an interaction.</summary>
public sealed record InteractionOrdering
{
    /// <summary>Creates an interaction ordering declaration.</summary>
    /// <param name="scope">Stable semantic ordering scope.</param>
    /// <param name="key">Portable typed ordering key within the scope.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/> or <paramref name="key"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="scope"/> is empty or white-space, or <paramref name="key"/> is not concrete.
    /// </exception>
    [JsonConstructor]
    public InteractionOrdering(string scope, PortableValue key)
    {
        Scope = Guard.RequireNotNullOrWhiteSpace(scope);
        Key = InteractionValueRequirements.RequireConcrete(key, nameof(key), "An ordering key");
    }

    /// <summary>Stable semantic ordering scope.</summary>
    public string Scope { get; }

    /// <summary>Portable typed ordering key within the scope.</summary>
    public PortableValue Key { get; }
}

/// <summary>Durability required of an emitted interaction.</summary>
public enum InteractionDurabilityDemand
{
    /// <summary>No demand was declared; invalid in a canonical envelope.</summary>
    Unspecified = 0,

    /// <summary>The interaction may exist only for the current finite activation.</summary>
    ActivationLocal = 1,

    /// <summary>The interaction must survive process and host interruption.</summary>
    Durable = 2
}

/// <summary>Visibility boundary required of an emitted interaction.</summary>
public enum InteractionVisibilityDemand
{
    /// <summary>No demand was declared; invalid in a canonical envelope.</summary>
    Unspecified = 0,

    /// <summary>The interaction becomes visible atomically with its authoritative origin commit.</summary>
    AtomicWithOrigin = 1,

    /// <summary>The interaction becomes visible only after its authoritative origin commits.</summary>
    AfterOriginCommit = 2,

    /// <summary>The interaction is activation-local and may be observed immediately.</summary>
    ActivationLocal = 3
}

/// <summary>Protocol-neutral durability and visibility demands for one interaction.</summary>
public sealed record InteractionDeliveryRequirements
{
    /// <summary>Creates interaction delivery requirements.</summary>
    /// <param name="durability">Required durability boundary.</param>
    /// <param name="visibility">Required visibility boundary.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="durability"/> or <paramref name="visibility"/> is unspecified or unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Activation-local durability is combined with non-local visibility, or durable delivery is combined with
    /// activation-local visibility.
    /// </exception>
    [JsonConstructor]
    public InteractionDeliveryRequirements(
        InteractionDurabilityDemand durability,
        InteractionVisibilityDemand visibility)
    {
        if (!Enum.IsDefined(durability) || durability == InteractionDurabilityDemand.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(durability), durability, "Interaction durability must be explicit.");
        }

        if (!Enum.IsDefined(visibility) || visibility == InteractionVisibilityDemand.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "Interaction visibility must be explicit.");
        }

        if (durability == InteractionDurabilityDemand.ActivationLocal
            && visibility != InteractionVisibilityDemand.ActivationLocal)
        {
            throw new ArgumentException(
                "Activation-local interactions require activation-local visibility.",
                nameof(visibility));
        }
        if (durability == InteractionDurabilityDemand.Durable
            && visibility == InteractionVisibilityDemand.ActivationLocal)
        {
            throw new ArgumentException(
                "Durable interactions cannot declare activation-local visibility.",
                nameof(visibility));
        }

        Durability = durability;
        Visibility = visibility;
    }

    /// <summary>Required durability boundary.</summary>
    public InteractionDurabilityDemand Durability { get; }

    /// <summary>Required visibility boundary.</summary>
    public InteractionVisibilityDemand Visibility { get; }
}

/// <summary>Closed semantic origin of a canonical interaction emission.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = InteractionWireNames.OriginDiscriminator)]
[JsonDerivedType(typeof(TransitionInteractionOrigin), InteractionWireNames.TransitionOrigin)]
[JsonDerivedType(typeof(ProcessInteractionOrigin), InteractionWireNames.ProcessOrigin)]
public abstract record InteractionOrigin
{
    /// <summary>Creates an interaction origin.</summary>
    /// <param name="definition">Exact originating Transition or Process definition.</param>
    /// <param name="node">Stable originating node identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="node"/> is a default value.</exception>
    private protected InteractionOrigin(ExecutionDefinitionReference definition, ExecutionNodeId node)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
        {
            throw new ArgumentException("An interaction origin requires a stable node identity.", nameof(node));
        }

        Definition = Guard.RequireNotNull(definition);
        Node = node;
    }

    /// <summary>Exact originating Transition or Process definition.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Stable originating node identity.</summary>
    public ExecutionNodeId Node { get; }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Originating node, subject, and outcome of a direct Transition activation.</summary>
public sealed record TransitionInteractionOrigin : InteractionOrigin
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Transition interaction origin.</summary>
    /// <param name="definition">Exact originating Transition definition.</param>
    /// <param name="node">Stable emission-node identity.</param>
    /// <param name="entity">Authoritative aggregate subject.</param>
    /// <param name="outcome">Stable terminal Transition outcome-node identity.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="entity"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="node"/> or <paramref name="outcome"/> is a default value.
    /// </exception>
    [JsonConstructor]
    public TransitionInteractionOrigin(
        ExecutionDefinitionReference definition,
        ExecutionNodeId node,
        InteractionEntityReference entity,
        ExecutionNodeId outcome)
        : base(definition, node)
    {
        if (string.IsNullOrWhiteSpace(outcome.Value))
        {
            throw new ArgumentException("A Transition interaction origin requires an outcome identity.", nameof(outcome));
        }

        Entity = Guard.RequireNotNull(entity);
        Outcome = outcome;
    }

    /// <summary>Authoritative aggregate subject.</summary>
    public InteractionEntityReference Entity { get; }

    /// <summary>Stable terminal Transition outcome-node identity.</summary>
    public ExecutionNodeId Outcome { get; }
}

/// <summary>Originating node and durable token of a Process activation.</summary>
public sealed record ProcessInteractionOrigin : InteractionOrigin
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Process interaction origin.</summary>
    /// <param name="definition">Exact originating Process definition.</param>
    /// <param name="node">Stable originating Process node identity.</param>
    /// <param name="continuation">Logical Process instance and current attempt.</param>
    /// <param name="activation">Finite activation that produced the interaction.</param>
    /// <param name="token">Durable control-flow token that produced the interaction.</param>
    /// <param name="entity">Optional authoritative entity subject involved in the origin.</param>
    /// <param name="transition">Optional exact Transition invoked by the Process origin.</param>
    /// <param name="outcome">Optional stable Transition or Process outcome-node identity.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="node"/>, <paramref name="activation"/>, <paramref name="token"/>, or a present
    /// <paramref name="outcome"/> is a default value; or <paramref name="transition"/> and
    /// <paramref name="entity"/> are not either both present or both absent.
    /// </exception>
    [JsonConstructor]
    public ProcessInteractionOrigin(
        ExecutionDefinitionReference definition,
        ExecutionNodeId node,
        ProcessContinuationIdentity continuation,
        ActivationId activation,
        TokenId token,
        InteractionEntityReference? entity = null,
        ExecutionDefinitionReference? transition = null,
        ExecutionNodeId? outcome = null)
        : base(definition, node)
    {
        if (string.IsNullOrWhiteSpace(activation.Value))
        {
            throw new ArgumentException("A Process interaction origin requires an activation identity.", nameof(activation));
        }

        if (string.IsNullOrWhiteSpace(token.Value))
        {
            throw new ArgumentException("A Process interaction origin requires a token identity.", nameof(token));
        }

        if ((entity is null) != (transition is null))
        {
            throw new ArgumentException(
                "A Process Transition origin must declare both its entity and exact Transition reference.",
                nameof(transition));
        }
        if (outcome is { } outcomeId && string.IsNullOrWhiteSpace(outcomeId.Value))
        {
            throw new ArgumentException("An optional Process outcome identity cannot be default.", nameof(outcome));
        }

        Continuation = Guard.RequireNotNull(continuation);
        Activation = activation;
        Token = token;
        Entity = entity;
        Transition = transition;
        Outcome = outcome;
    }

    /// <summary>Logical Process instance and current attempt.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Finite activation that produced the interaction.</summary>
    public ActivationId Activation { get; }

    /// <summary>Durable control-flow token that produced the interaction.</summary>
    public TokenId Token { get; }

    /// <summary>Optional authoritative entity subject involved in the origin.</summary>
    public InteractionEntityReference? Entity { get; }

    /// <summary>Optional exact Transition invoked by the Process origin.</summary>
    public ExecutionDefinitionReference? Transition { get; }

    /// <summary>Optional stable Transition or Process outcome-node identity.</summary>
    public ExecutionNodeId? Outcome { get; }
}

/// <summary>Closed semantic address for a Signal or Request response.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = InteractionWireNames.TargetDiscriminator)]
[JsonDerivedType(typeof(ProcessTokenInteractionTarget), InteractionWireNames.ProcessTokenTarget)]
[JsonDerivedType(typeof(TransitionInteractionTarget), InteractionWireNames.TransitionTarget)]
public abstract record InteractionTarget
{
    /// <summary>Restricts the canonical interaction-target family to this assembly's declared variants.</summary>
    private protected InteractionTarget()
    {
    }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Address of one durable Process control-flow token and, optionally, one exact wait occurrence.</summary>
public sealed record ProcessTokenInteractionTarget : InteractionTarget
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Process-token interaction target.</summary>
    /// <param name="continuation">Logical Process instance and exact current attempt.</param>
    /// <param name="token">Durable target token.</param>
    /// <param name="waitRegistrationId">
    /// Optional exact wait occurrence. A null value deliberately leaves the interaction unscoped so it may be
    /// buffered before a compatible wait is registered.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="token"/> or a present <paramref name="waitRegistrationId"/> is a default value.
    /// </exception>
    [JsonConstructor]
    public ProcessTokenInteractionTarget(
        ProcessContinuationIdentity continuation,
        TokenId token,
        ProcessWaitRegistrationId? waitRegistrationId = null)
    {
        if (string.IsNullOrWhiteSpace(token.Value))
        {
            throw new ArgumentException("A Process interaction target requires a token identity.", nameof(token));
        }

        if (waitRegistrationId is { } registration && string.IsNullOrWhiteSpace(registration.Value))
        {
            throw new ArgumentException(
                "A present Process wait target requires a wait-registration identity.",
                nameof(waitRegistrationId));
        }

        Continuation = Guard.RequireNotNull(continuation);
        Token = token;
        WaitRegistrationId = waitRegistrationId;
    }

    /// <summary>Logical Process instance and exact current attempt.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Durable target token.</summary>
    public TokenId Token { get; }

    /// <summary>
    /// Exact wait occurrence addressed by the interaction, or <see langword="null"/> for deliberately unscoped
    /// early delivery to the token.
    /// </summary>
    public ProcessWaitRegistrationId? WaitRegistrationId { get; }
}

/// <summary>Exact semantic address of a typed Transition continuation.</summary>
public sealed record TransitionInteractionTarget : InteractionTarget
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Transition-continuation target.</summary>
    /// <param name="transition">Exact target Transition definition.</param>
    /// <param name="continuation">Stable declaration node that binds the response.</param>
    /// <param name="entity">Authoritative aggregate subject to which the continuation applies.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="transition"/> or <paramref name="entity"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="continuation"/> is a default value.</exception>
    [JsonConstructor]
    public TransitionInteractionTarget(
        ExecutionDefinitionReference transition,
        ExecutionNodeId continuation,
        InteractionEntityReference entity)
    {
        if (string.IsNullOrWhiteSpace(continuation.Value))
        {
            throw new ArgumentException("A Transition target requires a continuation identity.", nameof(continuation));
        }

        Transition = Guard.RequireNotNull(transition);
        Continuation = continuation;
        Entity = Guard.RequireNotNull(entity);
    }

    /// <summary>Exact target Transition definition.</summary>
    public ExecutionDefinitionReference Transition { get; }

    /// <summary>Stable declaration node that binds the response.</summary>
    public ExecutionNodeId Continuation { get; }

    /// <summary>Authoritative aggregate subject to which the continuation applies.</summary>
    public InteractionEntityReference Entity { get; }
}

/// <summary>Canonical mapping from child Process terminal status to exact Request outcome identity.</summary>
public sealed record ProcessChildOutcomeMapping
{
    /// <summary>Creates an explicit total child-terminal mapping.</summary>
    /// <param name="completed">Outcome emitted when the child completes successfully.</param>
    /// <param name="failed">Outcome emitted when the child fails.</param>
    /// <param name="cancelled">Outcome emitted when the child is cancelled.</param>
    /// <param name="terminated">Outcome emitted when the child is forcibly terminated.</param>
    /// <exception cref="ArgumentException">Any outcome identity is default or repeats another terminal status.</exception>
    [JsonConstructor]
    public ProcessChildOutcomeMapping(
        RequestTerminalOutcomeId completed,
        RequestTerminalOutcomeId failed,
        RequestTerminalOutcomeId cancelled,
        RequestTerminalOutcomeId terminated)
    {
        HashSet<RequestTerminalOutcomeId> observed = [];
        RequireOutcome(completed, nameof(completed), observed);
        RequireOutcome(failed, nameof(failed), observed);
        RequireOutcome(cancelled, nameof(cancelled), observed);
        RequireOutcome(terminated, nameof(terminated), observed);
        Completed = completed;
        Failed = failed;
        Cancelled = cancelled;
        Terminated = terminated;
    }

    /// <summary>Exact Request outcome for successful child completion.</summary>
    public RequestTerminalOutcomeId Completed { get; }

    /// <summary>Exact Request outcome for child failure.</summary>
    public RequestTerminalOutcomeId Failed { get; }

    /// <summary>Exact Request outcome for child cancellation.</summary>
    public RequestTerminalOutcomeId Cancelled { get; }

    /// <summary>Exact Request outcome for forced child termination.</summary>
    public RequestTerminalOutcomeId Terminated { get; }

    /// <summary>Maps one terminal child status to its authored Request outcome.</summary>
    /// <param name="terminal">Exact terminal child status.</param>
    /// <returns>The authored Request outcome identity for <paramref name="terminal"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="terminal"/> is non-terminal, unspecified, or unsupported.
    /// </exception>
    public RequestTerminalOutcomeId For(ExecutionTerminalOutcomeKind terminal) => terminal switch
    {
        ExecutionTerminalOutcomeKind.Completed => Completed,
        ExecutionTerminalOutcomeKind.Failed => Failed,
        ExecutionTerminalOutcomeKind.Cancelled => Cancelled,
        ExecutionTerminalOutcomeKind.Terminated => Terminated,
        _ => throw new ArgumentOutOfRangeException(
            nameof(terminal),
            terminal,
            "A child Request outcome can be selected only for a terminal Process status.")
    };

    /// <summary>Returns whether an outcome identity belongs to this total terminal mapping.</summary>
    /// <param name="outcome">Request outcome identity to inspect.</param>
    /// <returns><see langword="true"/> when at least one child terminal status maps to the outcome.</returns>
    public bool Contains(RequestTerminalOutcomeId outcome) =>
        outcome == Completed || outcome == Failed || outcome == Cancelled || outcome == Terminated;

    static void RequireOutcome(
        RequestTerminalOutcomeId outcome,
        string parameterName,
        ISet<RequestTerminalOutcomeId> observed)
    {
        if (string.IsNullOrWhiteSpace(outcome.Value))
        {
            throw new ArgumentException(
                "A child terminal mapping requires an exact Request outcome identity.",
                parameterName);
        }
        if (!observed.Add(outcome))
        {
            throw new ArgumentException(
                "Each child terminal status requires a distinct Request outcome identity.",
                parameterName);
        }
    }
}

/// <summary>Exact child Process instance that a canonical Request initializes.</summary>
/// <remarks>
/// This metadata lets the existing Request binding and adapter pipeline realize a child start without introducing
/// a second operation model. The Request's <see cref="RequestEnvelope.ResponseTarget"/> remains the parent wait that
/// consumes the eventual Reply.
/// </remarks>
public sealed record ProcessChildRequestTarget
{
    /// <summary>Creates an exact child Process Request target.</summary>
    /// <param name="definition">Pinned child Process definition, revision, and fingerprint.</param>
    /// <param name="continuation">Interpreter-derived child Process instance and first attempt.</param>
    /// <param name="outcomeMapping">Authored total mapping from child terminal status to Request outcome.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ProcessChildRequestTarget(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation,
        ProcessChildOutcomeMapping outcomeMapping)
    {
        Definition = Guard.RequireNotNull(definition);
        Continuation = Guard.RequireNotNull(continuation);
        OutcomeMapping = Guard.RequireNotNull(outcomeMapping);
    }

    /// <summary>Pinned child Process definition, revision, and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Interpreter-derived child Process instance and first attempt.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Authored total mapping from child terminal status to Request outcome.</summary>
    public ProcessChildOutcomeMapping OutcomeMapping { get; }
}

/// <summary>Common immutable identity, causality, scope, ordering, delivery, and provenance context.</summary>
public sealed record InteractionEnvelopeContext
{
    /// <summary>Creates common interaction envelope context.</summary>
    /// <param name="emissionId">Stable logical emission identity.</param>
    /// <param name="origin">Closed Transition or Process origin.</param>
    /// <param name="correlationId">Stable correlation identity.</param>
    /// <param name="causationId">Optional logical emission that directly caused this interaction.</param>
    /// <param name="authorityScope">Tenant and authority boundary.</param>
    /// <param name="idempotencyKey">Stable logical deduplication basis.</param>
    /// <param name="ordering">Optional explicit scoped ordering key; null declares no cross-emission ordering.</param>
    /// <param name="delivery">Durability and visibility demands.</param>
    /// <param name="provenance">Producer and semantic source attribution.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="emissionId"/>, <paramref name="correlationId"/>, or
    /// <paramref name="idempotencyKey"/> is a default value; <paramref name="causationId"/> is a present default
    /// value; or <paramref name="causationId"/> equals <paramref name="emissionId"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="origin"/>, <paramref name="authorityScope"/>, <paramref name="delivery"/>, or
    /// <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public InteractionEnvelopeContext(
        EmissionId emissionId,
        InteractionOrigin origin,
        InteractionCorrelationId correlationId,
        EmissionId? causationId,
        InteractionAuthorityScope authorityScope,
        InteractionIdempotencyKey idempotencyKey,
        InteractionOrdering? ordering,
        InteractionDeliveryRequirements delivery,
        ExecutionProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(emissionId.Value))
        {
            throw new ArgumentException("An interaction requires a stable emission identity.", nameof(emissionId));
        }

        if (string.IsNullOrWhiteSpace(correlationId.Value))
        {
            throw new ArgumentException("An interaction requires a stable correlation identity.", nameof(correlationId));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey.Value))
        {
            throw new ArgumentException("An interaction requires a stable idempotency key.", nameof(idempotencyKey));
        }

        if (causationId is { } cause && string.IsNullOrWhiteSpace(cause.Value))
        {
            throw new ArgumentException("A present interaction causation identity cannot be default.", nameof(causationId));
        }

        if (causationId == emissionId)
        {
            throw new ArgumentException("An interaction cannot directly cause itself.", nameof(causationId));
        }

        EmissionId = emissionId;
        Origin = Guard.RequireNotNull(origin);
        CorrelationId = correlationId;
        CausationId = causationId;
        AuthorityScope = Guard.RequireNotNull(authorityScope);
        IdempotencyKey = idempotencyKey;
        Ordering = ordering;
        Delivery = Guard.RequireNotNull(delivery);
        Provenance = Guard.RequireNotNull(provenance);
    }

    /// <summary>Stable logical emission identity.</summary>
    public EmissionId EmissionId { get; }

    /// <summary>Closed Transition or Process origin.</summary>
    public InteractionOrigin Origin { get; }

    /// <summary>Stable correlation identity.</summary>
    public InteractionCorrelationId CorrelationId { get; }

    /// <summary>Optional logical emission that directly caused this interaction.</summary>
    public EmissionId? CausationId { get; }

    /// <summary>Tenant and authority boundary.</summary>
    public InteractionAuthorityScope AuthorityScope { get; }

    /// <summary>Stable logical deduplication basis.</summary>
    public InteractionIdempotencyKey IdempotencyKey { get; }

    /// <summary>Optional explicit scoped ordering key; null means unordered across emissions.</summary>
    public InteractionOrdering? Ordering { get; }

    /// <summary>Durability and visibility demands.</summary>
    public InteractionDeliveryRequirements Delivery { get; }

    /// <summary>Producer and semantic source attribution.</summary>
    public ExecutionProvenance Provenance { get; }
}

/// <summary>One typed terminal outcome carried by a Reply.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = InteractionWireNames.OutcomeDiscriminator)]
[JsonDerivedType(typeof(RequestResultOutcome), InteractionWireNames.ResultOutcome)]
[JsonDerivedType(typeof(RequestFailureOutcome), InteractionWireNames.FailureOutcome)]
[JsonDerivedType(typeof(RequestTimeoutOutcome), InteractionWireNames.TimeoutOutcome)]
[JsonDerivedType(typeof(RequestCancellationOutcome), InteractionWireNames.CancellationOutcome)]
public abstract record RequestTerminalOutcome
{
    /// <summary>Creates a typed terminal outcome.</summary>
    /// <param name="id">Stable Request-owned outcome identity.</param>
    /// <param name="value">Portable typed result or terminal detail.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is a default value, or <paramref name="value"/> is unknown or failed.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    private protected RequestTerminalOutcome(RequestTerminalOutcomeId id, PortableValue value)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A terminal outcome requires a stable identity.", nameof(id));
        }

        Id = id;
        Value = InteractionValueRequirements.RequireMaterialized(value, nameof(value), "A terminal outcome");
    }

    /// <summary>Stable Request-owned outcome identity.</summary>
    public RequestTerminalOutcomeId Id { get; }

    /// <summary>Portable typed result or terminal detail.</summary>
    public PortableValue Value { get; }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Successful typed Request outcome.</summary>
public sealed record RequestResultOutcome : RequestTerminalOutcome
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a successful typed Request outcome.</summary>
    /// <param name="id">Stable Request-owned result identity.</param>
    /// <param name="value">Portable typed result.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is a default value, or <paramref name="value"/> is unknown or failed.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RequestResultOutcome(RequestTerminalOutcomeId id, PortableValue value) : base(id, value)
    {
    }
}

/// <summary>Typed terminal Request failure outcome.</summary>
public sealed record RequestFailureOutcome : RequestTerminalOutcome
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a typed terminal failure outcome.</summary>
    /// <param name="id">Stable Request-owned failure identity.</param>
    /// <param name="value">Portable typed failure value.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is a default value, or <paramref name="value"/> is unknown or failed.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RequestFailureOutcome(RequestTerminalOutcomeId id, PortableValue value) : base(id, value)
    {
    }
}

/// <summary>Typed Request timeout outcome.</summary>
public sealed record RequestTimeoutOutcome : RequestTerminalOutcome
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a typed timeout outcome.</summary>
    /// <param name="id">Stable Request-owned timeout identity.</param>
    /// <param name="value">Portable typed timeout detail.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is a default value, or <paramref name="value"/> is unknown or failed.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RequestTimeoutOutcome(RequestTerminalOutcomeId id, PortableValue value) : base(id, value)
    {
    }
}

/// <summary>Typed Request cancellation outcome.</summary>
public sealed record RequestCancellationOutcome : RequestTerminalOutcome
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a typed cancellation outcome.</summary>
    /// <param name="id">Stable Request-owned cancellation identity.</param>
    /// <param name="value">Portable typed cancellation detail.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is a default value, or <paramref name="value"/> is unknown or failed.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RequestCancellationOutcome(RequestTerminalOutcomeId id, PortableValue value) : base(id, value)
    {
    }
}

/// <summary>Closed versioned family of canonical runtime interaction envelopes.</summary>
/// <remarks>Persistence and audit events are deliberately not members of this union.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = InteractionWireNames.InteractionDiscriminator)]
[JsonDerivedType(typeof(DomainEventEnvelope), InteractionWireNames.DomainEvent)]
[JsonDerivedType(typeof(RequestEnvelope), InteractionWireNames.Request)]
[JsonDerivedType(typeof(SignalEnvelope), InteractionWireNames.Signal)]
[JsonDerivedType(typeof(ReplyEnvelope), InteractionWireNames.Reply)]
public abstract record InteractionEnvelope
{
    /// <summary>Current canonical interaction-envelope schema version.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-interaction-envelope/v2");

    /// <summary>Creates a canonical interaction envelope.</summary>
    /// <param name="schemaVersion">Exact interaction-envelope schema version.</param>
    /// <param name="context">Common immutable interaction context.</param>
    /// <exception cref="ArgumentException"><paramref name="schemaVersion"/> is a default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    private protected InteractionEnvelope(
        ExecutionIrSchemaVersion schemaVersion,
        InteractionEnvelopeContext context)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
        {
            throw new ArgumentException("An interaction envelope requires an exact schema version.", nameof(schemaVersion));
        }

        SchemaVersion = schemaVersion;
        Context = Guard.RequireNotNull(context);
    }

    /// <summary>Exact interaction-envelope schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Common immutable interaction context.</summary>
    public InteractionEnvelopeContext Context { get; }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Canonical occurrence of a domain fact with no response obligation.</summary>
public sealed record DomainEventEnvelope : InteractionEnvelope
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a domain-event envelope.</summary>
    /// <param name="schemaVersion">Exact interaction-envelope schema version.</param>
    /// <param name="context">Common immutable interaction context.</param>
    /// <param name="contract">Exact typed domain-event contract.</param>
    /// <param name="payload">Portable typed event payload.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="contract"/>, or <paramref name="payload"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is a default value, or <paramref name="payload"/> is unknown or failed.
    /// </exception>
    [JsonConstructor]
    public DomainEventEnvelope(
        ExecutionIrSchemaVersion schemaVersion,
        InteractionEnvelopeContext context,
        DomainEventContractReference contract,
        PortableValue payload)
        : base(schemaVersion, context)
    {
        Contract = Guard.RequireNotNull(contract);
        Payload = InteractionValueRequirements.RequireMaterialized(payload, nameof(payload), "A domain-event payload");
    }

    /// <summary>Exact typed domain-event contract.</summary>
    public DomainEventContractReference Contract { get; }

    /// <summary>Portable typed event payload.</summary>
    public PortableValue Payload { get; }
}

/// <summary>Canonical occurrence of a Request and its typed response destination.</summary>
public sealed record RequestEnvelope : InteractionEnvelope
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Request envelope.</summary>
    /// <param name="schemaVersion">Exact interaction-envelope schema version.</param>
    /// <param name="context">Common immutable interaction context.</param>
    /// <param name="contract">Exact typed Request contract.</param>
    /// <param name="payload">Portable typed request payload.</param>
    /// <param name="responseTarget">Process token or declared Transition continuation consuming the response.</param>
    /// <param name="childTarget">
    /// Optional exact child Process instance initialized by this Request; null for ordinary Requests.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="contract"/>, <paramref name="payload"/>, or
    /// <paramref name="responseTarget"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is a default value; <paramref name="payload"/> is unknown or failed; or a
    /// child target is combined with a non-Process origin or non-Process-token response target.
    /// </exception>
    [JsonConstructor]
    public RequestEnvelope(
        ExecutionIrSchemaVersion schemaVersion,
        InteractionEnvelopeContext context,
        RequestContractReference contract,
        PortableValue payload,
        InteractionTarget responseTarget,
        ProcessChildRequestTarget? childTarget = null)
        : base(schemaVersion, context)
    {
        Contract = Guard.RequireNotNull(contract);
        Payload = InteractionValueRequirements.RequireMaterialized(payload, nameof(payload), "A Request payload");
        ResponseTarget = Guard.RequireNotNull(responseTarget);
        if (childTarget is not null
            && (context.Origin is not ProcessInteractionOrigin
                || responseTarget is not ProcessTokenInteractionTarget))
        {
            throw new ArgumentException(
                "A child Process Request target requires a Process origin and Process-token response target.",
                nameof(childTarget));
        }
        ChildTarget = childTarget;
    }

    /// <summary>Exact typed Request contract.</summary>
    public RequestContractReference Contract { get; }

    /// <summary>Portable typed request payload.</summary>
    public PortableValue Payload { get; }

    /// <summary>Process token or declared Transition continuation consuming the response.</summary>
    public InteractionTarget ResponseTarget { get; }

    /// <summary>
    /// Exact child Process instance initialized by this Request, or <see langword="null"/> for an ordinary Request.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProcessChildRequestTarget? ChildTarget { get; }
}

/// <summary>Canonical occurrence of an addressed one-way Signal.</summary>
public sealed record SignalEnvelope : InteractionEnvelope
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Signal envelope.</summary>
    /// <param name="schemaVersion">Exact interaction-envelope schema version.</param>
    /// <param name="context">Common immutable interaction context.</param>
    /// <param name="contract">Exact typed Signal contract.</param>
    /// <param name="payload">Portable typed signal payload.</param>
    /// <param name="target">Semantic address receiving the one-way Signal.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="contract"/>, <paramref name="payload"/>, or
    /// <paramref name="target"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is a default value, or <paramref name="payload"/> is unknown or failed.
    /// </exception>
    [JsonConstructor]
    public SignalEnvelope(
        ExecutionIrSchemaVersion schemaVersion,
        InteractionEnvelopeContext context,
        SignalContractReference contract,
        PortableValue payload,
        InteractionTarget target)
        : base(schemaVersion, context)
    {
        Contract = Guard.RequireNotNull(contract);
        Payload = InteractionValueRequirements.RequireMaterialized(payload, nameof(payload), "A Signal payload");
        Target = Guard.RequireNotNull(target);
    }

    /// <summary>Exact typed Signal contract.</summary>
    public SignalContractReference Contract { get; }

    /// <summary>Portable typed signal payload.</summary>
    public PortableValue Payload { get; }

    /// <summary>Semantic address receiving the one-way Signal.</summary>
    public InteractionTarget Target { get; }
}

/// <summary>Canonical Reply that discharges one admitted Request with one typed terminal outcome.</summary>
public sealed record ReplyEnvelope : InteractionEnvelope
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Reply envelope.</summary>
    /// <param name="schemaVersion">Exact interaction-envelope schema version.</param>
    /// <param name="context">Common immutable interaction context.</param>
    /// <param name="contract">Exact typed Reply contract.</param>
    /// <param name="inReplyTo">Stable logical Request emission discharged by this Reply.</param>
    /// <param name="outcome">Typed terminal Request outcome.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="contract"/>, or <paramref name="outcome"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> or <paramref name="inReplyTo"/> is a default value; the Reply discharges
    /// itself; or its direct causation identity does not equal the Request emission it discharges.
    /// </exception>
    [JsonConstructor]
    public ReplyEnvelope(
        ExecutionIrSchemaVersion schemaVersion,
        InteractionEnvelopeContext context,
        ReplyContractReference contract,
        EmissionId inReplyTo,
        RequestTerminalOutcome outcome)
        : base(schemaVersion, context)
    {
        if (string.IsNullOrWhiteSpace(inReplyTo.Value))
        {
            throw new ArgumentException("A Reply requires the stable Request emission it discharges.", nameof(inReplyTo));
        }

        if (context.EmissionId == inReplyTo)
        {
            throw new ArgumentException("A Reply cannot discharge itself.", nameof(inReplyTo));
        }

        if (context.CausationId != inReplyTo)
        {
            throw new ArgumentException(
                "A Reply's direct causation identity must be the Request it discharges.",
                nameof(inReplyTo));
        }

        Contract = Guard.RequireNotNull(contract);
        InReplyTo = inReplyTo;
        Outcome = Guard.RequireNotNull(outcome);
    }

    /// <summary>Exact typed Reply contract.</summary>
    public ReplyContractReference Contract { get; }

    /// <summary>Stable logical Request emission discharged by this Reply.</summary>
    public EmissionId InReplyTo { get; }

    /// <summary>Typed terminal Request outcome.</summary>
    public RequestTerminalOutcome Outcome { get; }
}

static class InteractionValueRequirements
{
    public static PortableValue RequireMaterialized(
        PortableValue value,
        string parameterName,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.State is PortableValueState.Unknown or PortableValueState.Failed)
        {
            throw new ArgumentException(
                $"{subject} must be materially known before it crosses an interaction boundary.",
                parameterName);
        }

        return value;
    }

    public static PortableValue RequireConcrete(
        PortableValue value,
        string parameterName,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.State != PortableValueState.Concrete)
        {
            throw new ArgumentException(
                $"{subject} must be a concrete portable value.",
                parameterName);
        }

        return value;
    }
}
