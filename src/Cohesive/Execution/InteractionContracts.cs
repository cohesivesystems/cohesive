using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Exact typed reference to one canonical interaction-contract definition.</summary>
/// <remarks>
/// The derived CLR type carries the semantic interaction family. Link validation still resolves the exact
/// definition revision and fingerprint so a falsely labeled reference fails closed.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = InteractionWireNames.ContractDiscriminator)]
[JsonDerivedType(typeof(DomainEventContractReference), InteractionWireNames.DomainEvent)]
[JsonDerivedType(typeof(RequestContractReference), InteractionWireNames.Request)]
[JsonDerivedType(typeof(SignalContractReference), InteractionWireNames.Signal)]
[JsonDerivedType(typeof(ReplyContractReference), InteractionWireNames.Reply)]
public abstract record InteractionContractReference
{
    /// <summary>Creates a typed interaction-contract reference.</summary>
    /// <param name="definition">Exact interaction definition revision and fingerprint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    private protected InteractionContractReference(ExecutionDefinitionReference definition) =>
        Definition = Guard.RequireNotNull(definition);

    /// <summary>Exact interaction definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Exact reference to a domain-event contract.</summary>
public sealed record DomainEventContractReference : InteractionContractReference
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a domain-event contract reference.</summary>
    /// <param name="definition">Exact domain-event definition revision and fingerprint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public DomainEventContractReference(ExecutionDefinitionReference definition) : base(definition)
    {
    }
}

/// <summary>Exact reference to a Request contract.</summary>
public sealed record RequestContractReference : InteractionContractReference
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Request contract reference.</summary>
    /// <param name="definition">Exact Request definition revision and fingerprint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RequestContractReference(ExecutionDefinitionReference definition) : base(definition)
    {
    }
}

/// <summary>Exact reference to a Signal contract.</summary>
public sealed record SignalContractReference : InteractionContractReference
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Signal contract reference.</summary>
    /// <param name="definition">Exact Signal definition revision and fingerprint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public SignalContractReference(ExecutionDefinitionReference definition) : base(definition)
    {
    }
}

/// <summary>Exact reference to a Reply contract.</summary>
public sealed record ReplyContractReference : InteractionContractReference
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Reply contract reference.</summary>
    /// <param name="definition">Exact Reply definition revision and fingerprint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ReplyContractReference(ExecutionDefinitionReference definition) : base(definition)
    {
    }
}

/// <summary>Portable value contract paired with its explicit semantic schema revision.</summary>
public sealed record InteractionValueSchema
{
    /// <summary>Creates a versioned interaction-value schema.</summary>
    /// <param name="contract">Portable type, shape, presence, nullability, and cardinality contract.</param>
    /// <param name="revision">Exact semantic revision of the value schema.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="revision"/> is a default value.</exception>
    [JsonConstructor]
    public InteractionValueSchema(ValueContract contract, InteractionValueSchemaRevision revision)
    {
        if (string.IsNullOrWhiteSpace(revision.Value))
            throw new ArgumentException("An interaction value schema requires an exact revision.", nameof(revision));

        Contract = Guard.RequireNotNull(contract);
        Revision = revision;
    }

    /// <summary>Portable semantic value contract.</summary>
    public ValueContract Contract { get; }

    /// <summary>Exact semantic schema revision.</summary>
    public InteractionValueSchemaRevision Revision { get; }
}

/// <summary>Closed canonical family of interaction-contract definitions.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = InteractionWireNames.InteractionDiscriminator)]
[JsonDerivedType(typeof(DomainEventContractDefinition), InteractionWireNames.DomainEvent)]
[JsonDerivedType(typeof(RequestContractDefinition), InteractionWireNames.Request)]
[JsonDerivedType(typeof(SignalContractDefinition), InteractionWireNames.Signal)]
[JsonDerivedType(typeof(ReplyContractDefinition), InteractionWireNames.Reply)]
public abstract record InteractionContractDefinition
{
    /// <summary>Restricts the canonical interaction-contract family to this assembly's declared variants.</summary>
    private protected InteractionContractDefinition()
    {
    }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Contract for a domain fact that creates no emitter-side response obligation.</summary>
public sealed record DomainEventContractDefinition : InteractionContractDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a domain-event contract.</summary>
    /// <param name="payload">Versioned portable event payload schema.</param>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public DomainEventContractDefinition(InteractionValueSchema payload) =>
        Payload = Guard.RequireNotNull(payload);

    /// <summary>Versioned portable event payload schema.</summary>
    public InteractionValueSchema Payload { get; }
}

/// <summary>Contract for an addressed one-way input with no response obligation.</summary>
public sealed record SignalContractDefinition : InteractionContractDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Signal contract.</summary>
    /// <param name="payload">Versioned portable signal payload schema.</param>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public SignalContractDefinition(InteractionValueSchema payload) =>
        Payload = Guard.RequireNotNull(payload);

    /// <summary>Versioned portable signal payload schema.</summary>
    public InteractionValueSchema Payload { get; }
}

/// <summary>Whether an optional timeout or cancellation terminal result is admitted.</summary>
public enum RequestOptionalTerminalSemantics
{
    /// <summary>No semantics were declared; invalid in canonical Request IR.</summary>
    Unspecified = 0,

    /// <summary>The optional terminal condition is not part of this Request contract.</summary>
    Unsupported = 1,

    /// <summary>The optional condition terminates the Request through its declared typed outcome.</summary>
    TerminalOutcome = 2
}

/// <summary>Disposition applied to a late, stale, or duplicate request result.</summary>
public enum RequestResultDisposition
{
    /// <summary>No disposition was declared; invalid in canonical Request IR.</summary>
    Unspecified = 0,

    /// <summary>Reject the result without admitting it as the Request outcome.</summary>
    Reject = 1,

    /// <summary>Retain the result as observable evidence without changing the accepted outcome.</summary>
    Observe = 2,

    /// <summary>Return or reuse the previously accepted logical disposition.</summary>
    ReusePriorDisposition = 3
}

/// <summary>Semantic conditions under which a Request operation may be retried.</summary>
public enum RequestRetrySemantics
{
    /// <summary>No retry semantics were declared; invalid in canonical Request IR.</summary>
    Unspecified = 0,

    /// <summary>The Request must not be retried.</summary>
    Never = 1,

    /// <summary>Retries reuse stable logical identity and require target idempotency evidence.</summary>
    StableIdentity = 2,

    /// <summary>An ambiguous prior attempt must be reconciled before another physical call.</summary>
    ReconcileBeforeRetry = 3
}

/// <summary>Required resolution for an ambiguous or otherwise unresolved Request outcome.</summary>
public enum RequestResolutionSemantics
{
    /// <summary>No resolution semantics were declared; invalid in canonical Request IR.</summary>
    Unspecified = 0,

    /// <summary>Resolve the condition as a declared terminal failure.</summary>
    TerminalFailure = 1,

    /// <summary>Require the durable operation model to bind a reconciliation interaction before completion.</summary>
    Reconcile = 2,

    /// <summary>Require the durable operation model to bind an explicit escalation path.</summary>
    Escalate = 3
}

/// <summary>One typed terminal result variant declared by a Request contract.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = InteractionWireNames.OutcomeDiscriminator)]
[JsonDerivedType(typeof(RequestResultDefinition), InteractionWireNames.ResultOutcome)]
[JsonDerivedType(typeof(RequestFailureDefinition), InteractionWireNames.FailureOutcome)]
[JsonDerivedType(typeof(RequestTimeoutDefinition), InteractionWireNames.TimeoutOutcome)]
[JsonDerivedType(typeof(RequestCancellationDefinition), InteractionWireNames.CancellationOutcome)]
public abstract record RequestTerminalOutcomeDefinition
{
    /// <summary>Creates a typed terminal-outcome definition.</summary>
    /// <param name="id">Stable outcome-variant identity.</param>
    /// <param name="schema">Versioned portable outcome-value schema.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <see langword="null"/>.</exception>
    private protected RequestTerminalOutcomeDefinition(
        RequestTerminalOutcomeId id,
        InteractionValueSchema schema)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A Request terminal outcome requires a stable identity.", nameof(id));

        Id = id;
        Schema = Guard.RequireNotNull(schema);
    }

    /// <summary>Stable outcome-variant identity.</summary>
    public RequestTerminalOutcomeId Id { get; }

    /// <summary>Versioned portable outcome-value schema.</summary>
    public InteractionValueSchema Schema { get; }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Typed successful Request result variant.</summary>
public sealed record RequestResultDefinition : RequestTerminalOutcomeDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a typed successful result variant.</summary>
    /// <param name="id">Stable result-variant identity.</param>
    /// <param name="schema">Versioned portable result schema.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RequestResultDefinition(RequestTerminalOutcomeId id, InteractionValueSchema schema) : base(id, schema)
    {
    }
}

/// <summary>Typed terminal Request failure variant.</summary>
public sealed record RequestFailureDefinition : RequestTerminalOutcomeDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a typed terminal failure variant.</summary>
    /// <param name="id">Stable failure-variant identity.</param>
    /// <param name="schema">Versioned portable failure schema.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RequestFailureDefinition(RequestTerminalOutcomeId id, InteractionValueSchema schema) : base(id, schema)
    {
    }
}

/// <summary>Typed Request timeout variant.</summary>
public sealed record RequestTimeoutDefinition : RequestTerminalOutcomeDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a typed timeout variant.</summary>
    /// <param name="id">Stable timeout-variant identity.</param>
    /// <param name="schema">Versioned portable timeout-detail schema.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RequestTimeoutDefinition(RequestTerminalOutcomeId id, InteractionValueSchema schema) : base(id, schema)
    {
    }
}

/// <summary>Typed Request cancellation variant.</summary>
public sealed record RequestCancellationDefinition : RequestTerminalOutcomeDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a typed cancellation variant.</summary>
    /// <param name="id">Stable cancellation-variant identity.</param>
    /// <param name="schema">Versioned portable cancellation-detail schema.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RequestCancellationDefinition(RequestTerminalOutcomeId id, InteractionValueSchema schema) : base(id, schema)
    {
    }
}

/// <summary>Complete typed terminal response and recovery obligation created by a Request.</summary>
/// <remarks>
/// This contract declares required dispositions and terminal variants. Timer triggers, retry budgets, and the
/// concrete interaction paths required by reconciliation or escalation are bound by the durable operation model;
/// they are not inferred or executed by the interaction vocabulary.
/// </remarks>
public sealed record RequestResponseObligation
{
    /// <summary>Creates a normalized Request response obligation.</summary>
    /// <param name="terminalOutcomes">Non-empty set of typed terminal result variants.</param>
    /// <param name="timeout">Whether timeout is unsupported or a declared terminal outcome.</param>
    /// <param name="cancellation">Whether cancellation is unsupported or a declared terminal outcome.</param>
    /// <param name="lateResult">Disposition for a result arriving after logical completion.</param>
    /// <param name="staleResult">Disposition for a result targeting incompatible continuation state.</param>
    /// <param name="duplicateResult">Disposition for a repeated logical result.</param>
    /// <param name="retry">Semantic retry precondition.</param>
    /// <param name="ambiguousOutcome">Required resolution after an ambiguous external outcome.</param>
    /// <param name="unresolvedOutcome">Required reconciliation or escalation for an unresolved obligation.</param>
    /// <param name="retentionHorizon">Minimum duration for which the response obligation must remain addressable.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="terminalOutcomes"/> is default or empty, contains a null or duplicate identity, contains
    /// no result or failure variant, cannot realize a terminal-failure resolution policy, or disagrees with timeout
    /// or cancellation semantics.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A policy is unspecified or unsupported, or <paramref name="retentionHorizon"/> is not positive.
    /// </exception>
    [JsonConstructor]
    public RequestResponseObligation(
        ImmutableArray<RequestTerminalOutcomeDefinition> terminalOutcomes,
        RequestOptionalTerminalSemantics timeout,
        RequestOptionalTerminalSemantics cancellation,
        RequestResultDisposition lateResult,
        RequestResultDisposition staleResult,
        RequestResultDisposition duplicateResult,
        RequestRetrySemantics retry,
        RequestResolutionSemantics ambiguousOutcome,
        RequestResolutionSemantics unresolvedOutcome,
        TimeSpan retentionHorizon)
    {
        if (terminalOutcomes.IsDefaultOrEmpty)
            throw new ArgumentException("A Request must declare at least one terminal outcome.", nameof(terminalOutcomes));
        ValidatePolicy(timeout, nameof(timeout));
        ValidatePolicy(cancellation, nameof(cancellation));
        ValidatePolicy(lateResult, nameof(lateResult));
        ValidatePolicy(staleResult, nameof(staleResult));
        ValidatePolicy(duplicateResult, nameof(duplicateResult));
        ValidatePolicy(retry, nameof(retry));
        ValidatePolicy(ambiguousOutcome, nameof(ambiguousOutcome));
        ValidatePolicy(unresolvedOutcome, nameof(unresolvedOutcome));
        if (retentionHorizon <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionHorizon),
                retentionHorizon,
                "A Request response obligation requires a positive retention horizon.");
        }

        var observed = new HashSet<RequestTerminalOutcomeId>();
        var hasResponse = false;
        var hasFailure = false;
        var timeoutCount = 0;
        var cancellationCount = 0;
        foreach (var outcome in terminalOutcomes)
        {
            if (outcome is null)
                throw new ArgumentException("Request terminal outcomes cannot contain null entries.", nameof(terminalOutcomes));
            if (!observed.Add(outcome.Id))
            {
                throw new ArgumentException(
                    $"Request terminal outcome '{outcome.Id.Value}' is declared more than once.",
                    nameof(terminalOutcomes));
            }

            hasResponse |= outcome is RequestResultDefinition or RequestFailureDefinition;
            hasFailure |= outcome is RequestFailureDefinition;
            timeoutCount += outcome is RequestTimeoutDefinition ? 1 : 0;
            cancellationCount += outcome is RequestCancellationDefinition ? 1 : 0;
        }

        if (!hasResponse)
            throw new ArgumentException("A Request must declare a typed result or terminal failure.", nameof(terminalOutcomes));
        if (!hasFailure
            && (ambiguousOutcome == RequestResolutionSemantics.TerminalFailure
                || unresolvedOutcome == RequestResolutionSemantics.TerminalFailure))
        {
            throw new ArgumentException(
                "A terminal-failure resolution policy requires at least one declared failure outcome.",
                nameof(terminalOutcomes));
        }
        ValidateOptionalOutcome(timeout, timeoutCount, "timeout", nameof(terminalOutcomes));
        ValidateOptionalOutcome(cancellation, cancellationCount, "cancellation", nameof(terminalOutcomes));

        TerminalOutcomes = CanonicalDocumentCollections.SortIfNeeded(
            terminalOutcomes,
            static (left, right) => StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value));
        Timeout = timeout;
        Cancellation = cancellation;
        LateResult = lateResult;
        StaleResult = staleResult;
        DuplicateResult = duplicateResult;
        Retry = retry;
        AmbiguousOutcome = ambiguousOutcome;
        UnresolvedOutcome = unresolvedOutcome;
        RetentionHorizon = retentionHorizon;
    }

    /// <summary>Typed terminal variants in deterministic stable-identity order.</summary>
    public ImmutableArray<RequestTerminalOutcomeDefinition> TerminalOutcomes { get; }

    /// <summary>Declared timeout semantics.</summary>
    public RequestOptionalTerminalSemantics Timeout { get; }

    /// <summary>Declared cancellation semantics.</summary>
    public RequestOptionalTerminalSemantics Cancellation { get; }

    /// <summary>Disposition for results arriving after logical completion.</summary>
    public RequestResultDisposition LateResult { get; }

    /// <summary>Disposition for results targeting incompatible continuation state.</summary>
    public RequestResultDisposition StaleResult { get; }

    /// <summary>Disposition for repeated logical results.</summary>
    public RequestResultDisposition DuplicateResult { get; }

    /// <summary>Semantic retry precondition.</summary>
    public RequestRetrySemantics Retry { get; }

    /// <summary>Required resolution for an ambiguous external outcome.</summary>
    public RequestResolutionSemantics AmbiguousOutcome { get; }

    /// <summary>Required resolution for an otherwise unresolved obligation.</summary>
    public RequestResolutionSemantics UnresolvedOutcome { get; }

    /// <summary>Minimum duration for which the response obligation remains addressable.</summary>
    public TimeSpan RetentionHorizon { get; }

    /// <summary>Finds one terminal variant by stable identity.</summary>
    /// <param name="id">Stable terminal-outcome identity.</param>
    /// <returns>The matching outcome, or <see langword="null"/> when it is not declared.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default value.</exception>
    public RequestTerminalOutcomeDefinition? Find(RequestTerminalOutcomeId id)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A default terminal-outcome identity cannot be resolved.", nameof(id));

        var index = CanonicalDocumentCollections.BinarySearchIndex(
            TerminalOutcomes,
            id,
            static (candidate, requested) =>
                StringComparer.Ordinal.Compare(candidate.Id.Value, requested.Value));
        return index >= 0 ? TerminalOutcomes[index] : null;
    }

    /// <summary>Compares obligations by their complete normalized semantic value.</summary>
    /// <param name="other">Obligation to compare.</param>
    /// <returns><see langword="true"/> when all terminal variants and policies are equal.</returns>
    public bool Equals(RequestResponseObligation? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Timeout == other.Timeout
        && Cancellation == other.Cancellation
        && LateResult == other.LateResult
        && StaleResult == other.StaleResult
        && DuplicateResult == other.DuplicateResult
        && Retry == other.Retry
        && AmbiguousOutcome == other.AmbiguousOutcome
        && UnresolvedOutcome == other.UnresolvedOutcome
        && RetentionHorizon == other.RetentionHorizon
        && TerminalOutcomes.SequenceEqual(other.TerminalOutcomes);

    /// <summary>Returns a structural hash code for all terminal variants and policies.</summary>
    /// <returns>A hash code aligned with <see cref="Equals(RequestResponseObligation?)"/>.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Timeout);
        hash.Add(Cancellation);
        hash.Add(LateResult);
        hash.Add(StaleResult);
        hash.Add(DuplicateResult);
        hash.Add(Retry);
        hash.Add(AmbiguousOutcome);
        hash.Add(UnresolvedOutcome);
        hash.Add(RetentionHorizon);
        foreach (var outcome in TerminalOutcomes)
            hash.Add(outcome);
        return hash.ToHashCode();
    }

    static void ValidatePolicy<T>(T policy, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(policy) || Convert.ToInt32(policy, System.Globalization.CultureInfo.InvariantCulture) == 0)
            throw new ArgumentOutOfRangeException(parameterName, policy, "A Request policy must be explicitly declared.");
    }

    static void ValidateOptionalOutcome(
        RequestOptionalTerminalSemantics semantics,
        int count,
        string name,
        string parameterName)
    {
        var expected = semantics == RequestOptionalTerminalSemantics.TerminalOutcome ? 1 : 0;
        if (count != expected)
        {
            throw new ArgumentException(
                $"Request {name} semantics require exactly {expected} matching terminal outcome(s), but {count} were declared.",
                parameterName);
        }
    }
}

/// <summary>Contract for an interaction that creates one typed terminal response obligation.</summary>
public sealed record RequestContractDefinition : InteractionContractDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Request contract.</summary>
    /// <param name="payload">Versioned portable request payload schema.</param>
    /// <param name="response">Required typed response obligation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="payload"/> or <paramref name="response"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public RequestContractDefinition(InteractionValueSchema payload, RequestResponseObligation response)
    {
        Payload = Guard.RequireNotNull(payload);
        Response = Guard.RequireNotNull(response);
    }

    /// <summary>Versioned portable request payload schema.</summary>
    public InteractionValueSchema Payload { get; }

    /// <summary>Required typed response obligation.</summary>
    public RequestResponseObligation Response { get; }
}

/// <summary>Contract for a Reply that discharges one terminal variant of an exact Request contract.</summary>
public sealed record ReplyContractDefinition : InteractionContractDefinition
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a Reply contract.</summary>
    /// <param name="request">Exact Request contract discharged by this Reply.</param>
    /// <param name="outcome">Request-owned terminal outcome carried by this Reply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is a default value.</exception>
    [JsonConstructor]
    public ReplyContractDefinition(RequestContractReference request, RequestTerminalOutcomeId outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.Value))
            throw new ArgumentException("A Reply contract requires a terminal outcome identity.", nameof(outcome));

        Request = Guard.RequireNotNull(request);
        Outcome = outcome;
    }

    /// <summary>Exact Request contract discharged by this Reply.</summary>
    public RequestContractReference Request { get; }

    /// <summary>Request-owned terminal outcome carried by this Reply.</summary>
    public RequestTerminalOutcomeId Outcome { get; }
}
