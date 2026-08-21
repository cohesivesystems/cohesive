using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Response and recovery semantics authored once for a canonical Request protocol.</summary>
/// <remarks>
/// Terminal outcomes are supplied by <see cref="RequestProtocolOutcomeBuilder"/>. The resulting
/// <see cref="RequestResponseObligation"/> remains the persisted semantic authority. Physical attempt budgets,
/// leases, idempotency evidence, and recovery targets are deliberately absent because they belong to deployment
/// bindings rather than the Request contract.
/// </remarks>
public sealed class RequestProtocolResponsePolicy
{
    /// <summary>Creates explicit semantic response policy for a typed Request protocol.</summary>
    /// <param name="timeout">Whether timeout is unsupported or represented by one declared terminal outcome.</param>
    /// <param name="cancellation">Whether cancellation is unsupported or represented by one declared terminal outcome.</param>
    /// <param name="lateResult">Disposition for a result arriving after logical completion.</param>
    /// <param name="staleResult">Disposition for a result targeting incompatible continuation state.</param>
    /// <param name="duplicateResult">Disposition for a repeated logical result.</param>
    /// <param name="retry">Semantic retry precondition.</param>
    /// <param name="ambiguousOutcome">Required resolution after an ambiguous external outcome.</param>
    /// <param name="unresolvedOutcome">Required resolution for an otherwise unresolved response obligation.</param>
    /// <param name="retentionHorizon">Minimum positive duration for which the response obligation remains addressable.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A policy is unspecified or outside its known values, or <paramref name="retentionHorizon"/> is not positive.
    /// </exception>
    public RequestProtocolResponsePolicy(
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
                "A Request protocol response policy requires a positive retention horizon.");
        }

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

    /// <summary>Required resolution after an ambiguous external outcome.</summary>
    public RequestResolutionSemantics AmbiguousOutcome { get; }

    /// <summary>Required resolution for an otherwise unresolved response obligation.</summary>
    public RequestResolutionSemantics UnresolvedOutcome { get; }

    /// <summary>Minimum duration for which the response obligation remains addressable.</summary>
    public TimeSpan RetentionHorizon { get; }

    internal RequestResponseObligation CreateObligation(
        ImmutableArray<RequestTerminalOutcomeDefinition> outcomes) => new(
        outcomes,
        Timeout,
        Cancellation,
        LateResult,
        StaleResult,
        DuplicateResult,
        Retry,
        AmbiguousOutcome,
        UnresolvedOutcome,
        RetentionHorizon);

    static void ValidatePolicy<TPolicy>(TPolicy policy, string parameterName)
        where TPolicy : struct, Enum
    {
        if (!Enum.IsDefined(policy)
            || Convert.ToInt32(policy, System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                policy,
                "A Request protocol response policy must be explicit.");
        }
    }
}

/// <summary>Representation-neutral typed descriptor for one canonical Request terminal outcome.</summary>
/// <remarks>
/// <see cref="Definition"/> owns outcome identity, kind, and portable payload schema. CLR case classes, generated
/// tagged unions, and future native C# unions are host-language projections and are deliberately absent.
/// </remarks>
public abstract class RequestProtocolOutcome
{
    private protected RequestProtocolOutcome(
        object owner,
        Type payloadType,
        RequestTerminalOutcomeDefinition definition)
    {
        Owner = Guard.RequireNotNull(owner);
        PayloadType = Guard.RequireNotNull(payloadType);
        Definition = Guard.RequireNotNull(definition);
    }

    internal object Owner { get; }

    /// <summary>CLR payload type projected into the canonical portable outcome schema.</summary>
    public Type PayloadType { get; }

    /// <summary>Canonical outcome definition and sole authority for outcome identity, kind, and schema.</summary>
    public RequestTerminalOutcomeDefinition Definition { get; }

    /// <summary>Stable canonical terminal-outcome identity.</summary>
    public RequestTerminalOutcomeId Id => Definition.Id;

    /// <summary>Versioned portable payload schema.</summary>
    public InteractionValueSchema Schema => Definition.Schema;
}

/// <summary>Typed descriptor for one canonical Request terminal outcome payload.</summary>
/// <typeparam name="TPayload">CLR payload type projected into the portable outcome schema.</typeparam>
public sealed class RequestProtocolOutcome<TPayload> : RequestProtocolOutcome
{
    internal RequestProtocolOutcome(
        object owner,
        RequestTerminalOutcomeDefinition definition)
        : base(owner, typeof(TPayload), definition)
    {
    }
}

/// <summary>Source-only C# case projection for one canonical Request terminal outcome.</summary>
/// <remarks>
/// <see cref="Outcome"/> remains the authority for outcome identity, kind, and portable payload schema.
/// <see cref="CaseType"/> is authoring metadata used to bind an exhaustive C# type switch and is never
/// serialized into canonical Request or Process documents. It may therefore be replaced by a future native C#
/// union case without a canonical-model or runtime migration.
/// </remarks>
public abstract class RequestProtocolCase
{
    private protected RequestProtocolCase(RequestProtocolOutcome outcome, Type caseType)
    {
        Outcome = Guard.RequireNotNull(outcome);
        CaseType = Guard.RequireNotNull(caseType);
    }

    internal object Owner => Outcome.Owner;

    /// <summary>Canonical terminal-outcome descriptor projected by this source-only case.</summary>
    public RequestProtocolOutcome Outcome { get; }

    /// <summary>Closed-family CLR case type used only while authoring typed callers and handlers.</summary>
    public Type CaseType { get; }

    /// <summary>Stable canonical terminal-outcome identity.</summary>
    public RequestTerminalOutcomeId Id => Outcome.Id;

    /// <summary>CLR payload type projected into the canonical portable outcome schema.</summary>
    public Type PayloadType => Outcome.PayloadType;
}

/// <summary>Typed source-only C# case projection for one canonical Request terminal outcome.</summary>
/// <typeparam name="TCase">Distinct CLR case in the protocol's closed outcome family.</typeparam>
/// <typeparam name="TPayload">CLR payload carried by the canonical terminal outcome.</typeparam>
public sealed class RequestProtocolCase<TCase, TPayload> : RequestProtocolCase
    where TCase : class
{
    internal RequestProtocolCase(RequestProtocolOutcome<TPayload> outcome)
        : base(outcome, typeof(TCase))
    {
        TypedOutcome = outcome;
    }

    /// <summary>Typed canonical terminal-outcome descriptor projected by this source-only case.</summary>
    public RequestProtocolOutcome<TPayload> TypedOutcome { get; }
}

/// <summary>Finite source-only case-declaration surface for a typed Request outcome family.</summary>
/// <remarks>
/// The builder records only a case type-to-canonical-outcome association. It constructs no case values, retains
/// no callbacks, and contributes no CLR case metadata to canonical Request documents.
/// </remarks>
/// <typeparam name="TOutcome">Closed CLR result-family root returned by typed Process effects and handlers.</typeparam>
public sealed class RequestProtocolCaseBuilder<TOutcome>
    where TOutcome : class
{
    readonly RequestProtocolOutcomeBuilder outcomes = new();
    readonly List<RequestProtocolCase> cases = [];
    bool completed;

    internal RequestProtocolCaseBuilder()
    {
    }

    internal object Owner => outcomes.Owner;

    /// <summary>Declares a typed successful terminal result and its distinct source-only case.</summary>
    /// <typeparam name="TCase">Concrete source-only case assignable to <typeparamref name="TOutcome"/>.</typeparam>
    /// <typeparam name="TPayload">CLR payload type projected into the portable result schema.</typeparam>
    /// <param name="id">Stable canonical result identity.</param>
    /// <param name="payloadRevision">Exact semantic revision of the payload schema.</param>
    /// <returns>A typed case projection over the canonical outcome descriptor.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <typeparamref name="TCase"/> is invalid or duplicated.</exception>
    /// <exception cref="InvalidOperationException">The authoring callback already completed.</exception>
    /// <exception cref="NotSupportedException">The CLR payload cannot be projected into a portable contract.</exception>
    public RequestProtocolCase<TCase, TPayload> Result<TCase, TPayload>(
        RequestTerminalOutcomeId id,
        InteractionValueSchemaRevision payloadRevision)
        where TCase : class, TOutcome => Add<TCase, TPayload>(outcomes.Result<TPayload>(id, payloadRevision));

    /// <summary>Declares a typed terminal failure and its distinct source-only case.</summary>
    /// <typeparam name="TCase">Concrete source-only case assignable to <typeparamref name="TOutcome"/>.</typeparam>
    /// <typeparam name="TPayload">CLR payload type projected into the portable failure schema.</typeparam>
    /// <param name="id">Stable canonical failure identity.</param>
    /// <param name="payloadRevision">Exact semantic revision of the payload schema.</param>
    /// <returns>A typed case projection over the canonical outcome descriptor.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <typeparamref name="TCase"/> is invalid or duplicated.</exception>
    /// <exception cref="InvalidOperationException">The authoring callback already completed.</exception>
    /// <exception cref="NotSupportedException">The CLR payload cannot be projected into a portable contract.</exception>
    public RequestProtocolCase<TCase, TPayload> Failure<TCase, TPayload>(
        RequestTerminalOutcomeId id,
        InteractionValueSchemaRevision payloadRevision)
        where TCase : class, TOutcome => Add<TCase, TPayload>(outcomes.Failure<TPayload>(id, payloadRevision));

    /// <summary>Declares a typed terminal timeout and its distinct source-only case.</summary>
    /// <typeparam name="TCase">Concrete source-only case assignable to <typeparamref name="TOutcome"/>.</typeparam>
    /// <typeparam name="TPayload">CLR payload type projected into the portable timeout schema.</typeparam>
    /// <param name="id">Stable canonical timeout identity.</param>
    /// <param name="payloadRevision">Exact semantic revision of the payload schema.</param>
    /// <returns>A typed case projection over the canonical outcome descriptor.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <typeparamref name="TCase"/> is invalid or duplicated.</exception>
    /// <exception cref="InvalidOperationException">The authoring callback already completed.</exception>
    /// <exception cref="NotSupportedException">The CLR payload cannot be projected into a portable contract.</exception>
    public RequestProtocolCase<TCase, TPayload> Timeout<TCase, TPayload>(
        RequestTerminalOutcomeId id,
        InteractionValueSchemaRevision payloadRevision)
        where TCase : class, TOutcome => Add<TCase, TPayload>(outcomes.Timeout<TPayload>(id, payloadRevision));

    /// <summary>Declares a typed terminal cancellation and its distinct source-only case.</summary>
    /// <typeparam name="TCase">Concrete source-only case assignable to <typeparamref name="TOutcome"/>.</typeparam>
    /// <typeparam name="TPayload">CLR payload type projected into the portable cancellation schema.</typeparam>
    /// <param name="id">Stable canonical cancellation identity.</param>
    /// <param name="payloadRevision">Exact semantic revision of the payload schema.</param>
    /// <returns>A typed case projection over the canonical outcome descriptor.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <typeparamref name="TCase"/> is invalid or duplicated.</exception>
    /// <exception cref="InvalidOperationException">The authoring callback already completed.</exception>
    /// <exception cref="NotSupportedException">The CLR payload cannot be projected into a portable contract.</exception>
    public RequestProtocolCase<TCase, TPayload> Cancellation<TCase, TPayload>(
        RequestTerminalOutcomeId id,
        InteractionValueSchemaRevision payloadRevision)
        where TCase : class, TOutcome => Add<TCase, TPayload>(outcomes.Cancellation<TPayload>(id, payloadRevision));

    internal (ImmutableArray<RequestProtocolOutcome> Outcomes, ImmutableArray<RequestProtocolCase> Cases) Complete()
    {
        completed = true;
        return (outcomes.Complete(), [.. cases]);
    }

    RequestProtocolCase<TCase, TPayload> Add<TCase, TPayload>(RequestProtocolOutcome<TPayload> outcome)
        where TCase : class, TOutcome
    {
        if (completed)
        {
            throw new InvalidOperationException("A completed Request protocol case builder cannot be reused.");
        }
        if (typeof(TCase).IsAbstract || typeof(TCase).IsInterface)
        {
            throw new ArgumentException(
                $"Request protocol case '{typeof(TCase)}' must be a concrete closed-family case.",
                nameof(TCase));
        }
        var payloadProperties = typeof(TCase)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static property =>
                property.GetMethod is { IsStatic: false }
                && property.GetIndexParameters().Length == 0)
            .ToArray();
        if (payloadProperties.Length != 1 || payloadProperties[0].PropertyType != typeof(TPayload))
        {
            throw new ArgumentException(
                $"Request protocol case '{typeof(TCase)}' must declare exactly one public '{typeof(TPayload)}' payload property.",
                nameof(TCase));
        }
        if (cases.Any(candidate => candidate.CaseType == typeof(TCase)))
        {
            throw new ArgumentException(
                $"Request protocol case '{typeof(TCase)}' is declared more than once.",
                nameof(TCase));
        }

        var result = new RequestProtocolCase<TCase, TPayload>(outcome);
        cases.Add(result);
        return result;
    }
}

/// <summary>Finite typed outcome-declaration surface used while authoring a Request protocol.</summary>
/// <remarks>
/// The builder emits immutable typed descriptors and is closed after the authoring callback returns. It is an
/// authoring producer only; no builder, callback, CLR outcome set, or mapper survives into canonical documents.
/// </remarks>
public sealed class RequestProtocolOutcomeBuilder
{
    static readonly IClrTypeRefMapper TypeMapper = new DefaultClrTypeRefMapper();

    readonly object owner = new();
    readonly List<RequestProtocolOutcome> outcomes = [];
    bool completed;

    internal object Owner => owner;

    /// <summary>Declares a typed successful terminal result.</summary>
    /// <typeparam name="TPayload">CLR payload type projected into the portable result schema.</typeparam>
    /// <param name="id">Stable canonical result identity.</param>
    /// <param name="payloadRevision">Exact semantic revision of the payload schema.</param>
    /// <returns>A typed representation-neutral outcome descriptor.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default or already declared.</exception>
    /// <exception cref="InvalidOperationException">The authoring callback already completed.</exception>
    /// <exception cref="NotSupportedException">The CLR payload cannot be projected into a portable contract.</exception>
    public RequestProtocolOutcome<TPayload> Result<TPayload>(
        RequestTerminalOutcomeId id,
        InteractionValueSchemaRevision payloadRevision) => Add<TPayload>(
        id,
        payloadRevision,
        static (outcomeId, schema) => new RequestResultDefinition(outcomeId, schema));

    /// <summary>Declares a typed terminal failure.</summary>
    /// <typeparam name="TPayload">CLR payload type projected into the portable failure schema.</typeparam>
    /// <param name="id">Stable canonical failure identity.</param>
    /// <param name="payloadRevision">Exact semantic revision of the payload schema.</param>
    /// <returns>A typed representation-neutral outcome descriptor.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default or already declared.</exception>
    /// <exception cref="InvalidOperationException">The authoring callback already completed.</exception>
    /// <exception cref="NotSupportedException">The CLR payload cannot be projected into a portable contract.</exception>
    public RequestProtocolOutcome<TPayload> Failure<TPayload>(
        RequestTerminalOutcomeId id,
        InteractionValueSchemaRevision payloadRevision) => Add<TPayload>(
        id,
        payloadRevision,
        static (outcomeId, schema) => new RequestFailureDefinition(outcomeId, schema));

    /// <summary>Declares a typed terminal timeout.</summary>
    /// <typeparam name="TPayload">CLR payload type projected into the portable timeout schema.</typeparam>
    /// <param name="id">Stable canonical timeout identity.</param>
    /// <param name="payloadRevision">Exact semantic revision of the payload schema.</param>
    /// <returns>A typed representation-neutral outcome descriptor.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default or already declared.</exception>
    /// <exception cref="InvalidOperationException">The authoring callback already completed.</exception>
    /// <exception cref="NotSupportedException">The CLR payload cannot be projected into a portable contract.</exception>
    public RequestProtocolOutcome<TPayload> Timeout<TPayload>(
        RequestTerminalOutcomeId id,
        InteractionValueSchemaRevision payloadRevision) => Add<TPayload>(
        id,
        payloadRevision,
        static (outcomeId, schema) => new RequestTimeoutDefinition(outcomeId, schema));

    /// <summary>Declares a typed terminal cancellation.</summary>
    /// <typeparam name="TPayload">CLR payload type projected into the portable cancellation schema.</typeparam>
    /// <param name="id">Stable canonical cancellation identity.</param>
    /// <param name="payloadRevision">Exact semantic revision of the payload schema.</param>
    /// <returns>A typed representation-neutral outcome descriptor.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default or already declared.</exception>
    /// <exception cref="InvalidOperationException">The authoring callback already completed.</exception>
    /// <exception cref="NotSupportedException">The CLR payload cannot be projected into a portable contract.</exception>
    public RequestProtocolOutcome<TPayload> Cancellation<TPayload>(
        RequestTerminalOutcomeId id,
        InteractionValueSchemaRevision payloadRevision) => Add<TPayload>(
        id,
        payloadRevision,
        static (outcomeId, schema) => new RequestCancellationDefinition(outcomeId, schema));

    internal ImmutableArray<RequestProtocolOutcome> Complete()
    {
        completed = true;
        return [.. outcomes];
    }

    RequestProtocolOutcome<TPayload> Add<TPayload>(
        RequestTerminalOutcomeId id,
        InteractionValueSchemaRevision payloadRevision,
        Func<RequestTerminalOutcomeId, InteractionValueSchema, RequestTerminalOutcomeDefinition> create)
    {
        if (completed)
            throw new InvalidOperationException("A completed Request protocol outcome builder cannot be reused.");
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A Request protocol outcome requires a stable identity.", nameof(id));
        foreach (var outcome in outcomes)
        {
            if (outcome.Id == id)
            {
                throw new ArgumentException(
                    $"Request protocol outcome '{id.Value}' is declared more than once.",
                    nameof(id));
            }
        }

        var schema = new InteractionValueSchema(
            new ValueContract(TypeMapper.Map(typeof(TPayload), null)),
            payloadRevision);
        var result = new RequestProtocolOutcome<TPayload>(owner, create(id, schema));
        outcomes.Add(result);
        return result;
    }
}

/// <summary>Typed handle for one authored canonical Request/Reply protocol.</summary>
/// <remarks>
/// Canonical documents remain the sole durable semantic authority. <typeparamref name="TRequest"/>,
/// <typeparamref name="TOutcomes"/>, and typed outcome descriptors are host-language projections used by
/// authoring, Process lowering, and adapter registration. They are not serialized or required by interpreters.
/// </remarks>
/// <typeparam name="TRequest">CLR request payload type projected into the canonical Request schema.</typeparam>
/// <typeparam name="TOutcomes">Caller-owned typed descriptor set returned by the outcome authoring callback.</typeparam>
public class RequestProtocol<TRequest, TOutcomes>
    where TOutcomes : notnull
{
    readonly object outcomeOwner;
    readonly InteractionContractCatalog? catalog;

    internal RequestProtocol(
        object outcomeOwner,
        TOutcomes outcomes,
        ImmutableArray<RequestProtocolOutcome> terminalOutcomes,
        ExecutionDefinitionDocument requestDocument,
        RequestContractReference request,
        ImmutableArray<DurableReplyBinding> replies,
        ImmutableArray<ExecutionDefinitionDocument> documents,
        DocumentValidationResult validation,
        InteractionContractCatalog? catalog)
    {
        this.outcomeOwner = Guard.RequireNotNull(outcomeOwner);
        this.catalog = catalog;
        Outcomes = outcomes;
        TerminalOutcomes = terminalOutcomes.IsDefault ? [] : terminalOutcomes;
        RequestDocument = Guard.RequireNotNull(requestDocument);
        Request = Guard.RequireNotNull(request);
        Replies = replies.IsDefault ? [] : replies;
        Documents = documents.IsDefault ? [] : documents;
        Validation = Guard.RequireNotNull(validation);
    }

    /// <summary>Caller-owned typed names for the protocol's heterogeneous terminal-outcome descriptors.</summary>
    public TOutcomes Outcomes { get; }

    /// <summary>All representation-neutral typed outcome descriptors in declaration order.</summary>
    public ImmutableArray<RequestProtocolOutcome> TerminalOutcomes { get; }

    /// <summary>Canonical Request document and sole authority for payload, outcomes, and response policy.</summary>
    public ExecutionDefinitionDocument RequestDocument { get; }

    /// <summary>Exact typed reference to the canonical Request contract.</summary>
    public RequestContractReference Request { get; }

    /// <summary>One exact Reply binding for every terminal outcome, in declaration order.</summary>
    public ImmutableArray<DurableReplyBinding> Replies { get; }

    /// <summary>Request followed by every generated Reply document in declaration order.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Documents { get; }

    /// <summary>Complete document and exact-reference linking diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether every generated document and Reply link is valid.</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>Typed projection of the canonical Request definition.</summary>
    /// <exception cref="System.Text.Json.JsonException">The canonical payload cannot be projected as Request IR.</exception>
    /// <exception cref="NotSupportedException">The strict serializer does not support a payload value.</exception>
    /// <exception cref="InvalidOperationException">The document does not contain a Request contract.</exception>
    public RequestContractDefinition Definition =>
        RequestDocument.GetDefinition<InteractionContractDefinition>() as RequestContractDefinition
        ?? throw new InvalidOperationException("The authored protocol document does not contain a Request contract.");

    /// <summary>Portable request payload contract projected from the canonical Request definition.</summary>
    public ValueContract InputContract => Definition.Payload.Contract;

    /// <summary>Validated exact-reference catalog containing the Request and every generated Reply.</summary>
    /// <exception cref="InvalidOperationException">Protocol validation failed; inspect <see cref="Validation"/>.</exception>
    public InteractionContractCatalog Catalog => catalog
        ?? throw new InvalidOperationException(
            "The Request protocol is invalid; inspect Validation before accessing its interaction catalog.");

    /// <summary>Attempts to obtain the validated exact-reference catalog.</summary>
    /// <param name="resolved">Receives the catalog when the protocol is valid.</param>
    /// <returns><see langword="true"/> when the protocol is valid and its catalog is available.</returns>
    public bool TryGetCatalog([NotNullWhen(true)] out InteractionContractCatalog? resolved)
    {
        resolved = catalog;
        return resolved is not null;
    }

    /// <summary>Returns whether a typed descriptor was declared by this exact protocol authoring operation.</summary>
    /// <typeparam name="TPayload">CLR payload type carried by the outcome descriptor.</typeparam>
    /// <param name="outcome">Typed outcome descriptor to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="outcome"/> belongs to this protocol.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    public bool Declares<TPayload>(RequestProtocolOutcome<TPayload> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return ReferenceEquals(outcomeOwner, outcome.Owner);
    }

    /// <summary>Resolves the exact Reply contract paired with a typed protocol outcome.</summary>
    /// <typeparam name="TPayload">CLR payload type carried by the outcome descriptor.</typeparam>
    /// <param name="outcome">Typed outcome declared by this protocol.</param>
    /// <returns>The exact Reply contract for <paramref name="outcome"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> belongs to another protocol.</exception>
    /// <exception cref="InvalidOperationException">The generated Reply mapping is incomplete.</exception>
    public ReplyContractReference ReplyFor<TPayload>(RequestProtocolOutcome<TPayload> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!Declares(outcome))
            throw new ArgumentException("The typed outcome belongs to another Request protocol.", nameof(outcome));
        foreach (var reply in Replies)
        {
            if (reply.Outcome == outcome.Id)
                return reply.Reply;
        }

        throw new InvalidOperationException(
            $"Request protocol outcome '{outcome.Id.Value}' has no generated Reply mapping.");
    }

    /// <summary>Derives a durable execution binding from this exact protocol.</summary>
    /// <remarks>
    /// Request identity and Reply mappings are projected from canonical protocol documents. The supplied values
    /// remain deployment-specific physical execution and recovery policy and are not added to the protocol.
    /// </remarks>
    /// <param name="maxAttempts">Maximum physical attempts, including the first.</param>
    /// <param name="claimLease">Positive ownership-lease duration for each attempt.</param>
    /// <param name="idempotencyEvidence">Target evidence supporting repeated physical execution.</param>
    /// <param name="timeoutAfter">Optional positive semantic timeout measured from operation creation.</param>
    /// <param name="reconciliationTarget">Exact semantic reconciliation target when required.</param>
    /// <param name="escalationTarget">Exact semantic escalation target when required.</param>
    /// <returns>A durable binding derived from the exact Request and generated Reply mappings.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A physical bound is invalid.</exception>
    /// <exception cref="ArgumentException">A supplied recovery target is invalid.</exception>
    public DurableRequestBinding BindDurably(
        int maxAttempts,
        TimeSpan claimLease,
        DurableOperationIdempotencyEvidence idempotencyEvidence,
        TimeSpan? timeoutAfter = null,
        DurableOperationResolutionTarget? reconciliationTarget = null,
        DurableOperationResolutionTarget? escalationTarget = null) => new(
        Request,
        Replies,
        maxAttempts,
        claimLease,
        timeoutAfter,
        idempotencyEvidence,
        terminalFailureOutcome: null,
        reconciliationTarget,
        escalationTarget);

    /// <summary>Derives a durable execution binding with one protocol-owned typed terminal failure.</summary>
    /// <typeparam name="TFailure">CLR payload type carried by the selected failure outcome.</typeparam>
    /// <param name="maxAttempts">Maximum physical attempts, including the first.</param>
    /// <param name="claimLease">Positive ownership-lease duration for each attempt.</param>
    /// <param name="idempotencyEvidence">Target evidence supporting repeated physical execution.</param>
    /// <param name="terminalFailureOutcome">Typed failure selected by terminal-failure resolution policy.</param>
    /// <param name="timeoutAfter">Optional positive semantic timeout measured from operation creation.</param>
    /// <param name="reconciliationTarget">Exact semantic reconciliation target when required.</param>
    /// <param name="escalationTarget">Exact semantic escalation target when required.</param>
    /// <returns>A durable binding derived from the exact Request and generated Reply mappings.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="terminalFailureOutcome"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="terminalFailureOutcome"/> belongs to another protocol or is not a failure outcome.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A physical bound is invalid.</exception>
    public DurableRequestBinding BindDurably<TFailure>(
        int maxAttempts,
        TimeSpan claimLease,
        DurableOperationIdempotencyEvidence idempotencyEvidence,
        RequestProtocolOutcome<TFailure> terminalFailureOutcome,
        TimeSpan? timeoutAfter = null,
        DurableOperationResolutionTarget? reconciliationTarget = null,
        DurableOperationResolutionTarget? escalationTarget = null)
    {
        ArgumentNullException.ThrowIfNull(terminalFailureOutcome);
        if (!Declares(terminalFailureOutcome))
        {
            throw new ArgumentException(
                "The typed terminal failure belongs to another Request protocol.",
                nameof(terminalFailureOutcome));
        }
        if (terminalFailureOutcome.Definition is not RequestFailureDefinition)
        {
            throw new ArgumentException(
                "A durable terminal-failure binding requires a declared failure outcome.",
                nameof(terminalFailureOutcome));
        }

        return new(
            Request,
            Replies,
            maxAttempts,
            claimLease,
            timeoutAfter,
            idempotencyEvidence,
            terminalFailureOutcome.Id,
            reconciliationTarget,
            escalationTarget);
    }
}

/// <summary>Typed Request protocol with a closed source-only C# outcome-family projection.</summary>
/// <remarks>
/// The inherited canonical documents and terminal outcomes remain the sole durable authority. <see cref="Cases"/>
/// only associates each canonical outcome with one distinct CLR case for exhaustive caller and handler authoring.
/// </remarks>
/// <typeparam name="TRequest">CLR request payload type projected into the canonical Request schema.</typeparam>
/// <typeparam name="TOutcome">Closed CLR result-family root selected by the Request.</typeparam>
/// <typeparam name="TOutcomes">Caller-owned typed case-descriptor set.</typeparam>
public sealed class RequestProtocol<TRequest, TOutcome, TOutcomes> : RequestProtocol<TRequest, TOutcomes>
    where TOutcome : class
    where TOutcomes : notnull
{
    internal RequestProtocol(
        object outcomeOwner,
        TOutcomes outcomes,
        ImmutableArray<RequestProtocolOutcome> terminalOutcomes,
        ImmutableArray<RequestProtocolCase> cases,
        ExecutionDefinitionDocument requestDocument,
        RequestContractReference request,
        ImmutableArray<DurableReplyBinding> replies,
        ImmutableArray<ExecutionDefinitionDocument> documents,
        DocumentValidationResult validation,
        InteractionContractCatalog? catalog)
        : base(
            outcomeOwner,
            outcomes,
            terminalOutcomes,
            requestDocument,
            request,
            replies,
            documents,
            validation,
            catalog)
    {
        Cases = cases.IsDefault ? [] : cases;
    }

    /// <summary>Complete case type-to-canonical-outcome projection in protocol declaration order.</summary>
    public ImmutableArray<RequestProtocolCase> Cases { get; }

    /// <summary>Resolves the exact source-only case projection for one closed-family case type.</summary>
    /// <typeparam name="TCase">Concrete case assignable to <typeparamref name="TOutcome"/>.</typeparam>
    /// <returns>The unique case projection declared by this protocol.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="TCase"/> is absent or duplicated.</exception>
    public RequestProtocolCase CaseFor<TCase>()
        where TCase : class, TOutcome
    {
        RequestProtocolCase? resolved = null;
        foreach (var candidate in Cases)
        {
            if (candidate.CaseType != typeof(TCase))
            {
                continue;
            }
            if (resolved is not null)
            {
                throw new InvalidOperationException(
                    $"Request protocol case '{typeof(TCase)}' is declared more than once.");
            }
            resolved = candidate;
        }

        return resolved
            ?? throw new InvalidOperationException(
                $"Request protocol does not declare source-only case '{typeof(TCase)}'.");
    }
}

public static partial class InteractionContractAuthoring
{
    /// <summary>Authors a canonical typed Request and one exact Reply contract per declared terminal outcome.</summary>
    /// <typeparam name="TRequest">CLR request payload type projected into the portable Request schema.</typeparam>
    /// <typeparam name="TOutcomes">Typed caller-owned outcome descriptor set.</typeparam>
    /// <param name="definitionId">Stable identity shared by revisions of the Request contract.</param>
    /// <param name="revisionId">Exact semantic Request revision.</param>
    /// <param name="payloadRevision">Exact semantic revision of the request payload schema.</param>
    /// <param name="createOutcomes">Finite callback declaring and naming the complete terminal-outcome set.</param>
    /// <param name="responsePolicy">Explicit semantic response, retry, resolution, and retention policy.</param>
    /// <param name="provenance">Producer and root-source attribution for the Request document.</param>
    /// <param name="replyDefinitionPrefix">
    /// Optional stable Reply identity prefix; defaults to the Request identity followed by <c>/reply</c>.
    /// </param>
    /// <param name="replyRevisionId">Optional Reply revision; defaults to the Request revision.</param>
    /// <param name="replyProvenance">
    /// Optional finite attribution projection for Reply documents; defaults to <paramref name="provenance"/>.
    /// The callback is evaluated during authoring and is not retained.
    /// </param>
    /// <param name="extensions">Optional exact-versioned Request extensions.</param>
    /// <param name="displayName">Optional human-facing Request name excluded from fingerprinting.</param>
    /// <param name="description">Optional human-facing Request description excluded from fingerprinting.</param>
    /// <returns>
    /// A typed immutable protocol handle containing canonical documents, exact references, typed outcomes, Reply
    /// mappings, retained validation, and validated catalog evidence when valid.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="createOutcomes"/>, <paramref name="responsePolicy"/>, <paramref name="provenance"/>, or the
    /// returned outcome descriptor set is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity, revision, outcome, extension, or descriptive metadata value is invalid; or the outcome set and
    /// response policy are incompatible.
    /// </exception>
    /// <exception cref="InvalidOperationException">Canonical content has no stable representation.</exception>
    /// <exception cref="NotSupportedException">A CLR request or outcome type cannot be represented portably.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical content cannot be encoded by the strict serializer.</exception>
    public static RequestProtocol<TRequest, TOutcomes> CreateRequestProtocol<TRequest, TOutcomes>(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        InteractionValueSchemaRevision payloadRevision,
        Func<RequestProtocolOutcomeBuilder, TOutcomes> createOutcomes,
        RequestProtocolResponsePolicy responsePolicy,
        ExecutionProvenance provenance,
        ExecutionDefinitionId? replyDefinitionPrefix = null,
        ExecutionRevisionId? replyRevisionId = null,
        Func<RequestTerminalOutcomeId, ExecutionProvenance>? replyProvenance = null,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default,
        string? displayName = null,
        string? description = null)
        where TOutcomes : notnull
    {
        ArgumentNullException.ThrowIfNull(createOutcomes);
        ArgumentNullException.ThrowIfNull(responsePolicy);
        ArgumentNullException.ThrowIfNull(provenance);

        var outcomeBuilder = new RequestProtocolOutcomeBuilder();
        var typedOutcomes = createOutcomes(outcomeBuilder);
        ArgumentNullException.ThrowIfNull(typedOutcomes);
        var outcomes = outcomeBuilder.Complete();
        var authored = CreateRequestProtocolDocuments<TRequest>(
            definitionId,
            revisionId,
            payloadRevision,
            outcomes,
            responsePolicy,
            provenance,
            replyDefinitionPrefix,
            replyRevisionId,
            replyProvenance,
            extensions,
            displayName,
            description);
        return new(
            outcomeBuilder.Owner,
            typedOutcomes,
            outcomes,
            authored.RequestDocument,
            authored.Request,
            authored.Replies,
            authored.Documents,
            authored.Validation,
            authored.Catalog);
    }

    /// <summary>Authors a canonical typed Request with a closed source-only C# outcome-family projection.</summary>
    /// <typeparam name="TRequest">CLR request payload type projected into the portable Request schema.</typeparam>
    /// <typeparam name="TOutcome">Closed CLR result-family root selected by typed callers and handlers.</typeparam>
    /// <typeparam name="TOutcomes">Typed caller-owned case-descriptor set.</typeparam>
    /// <param name="definitionId">Stable identity shared by revisions of the Request contract.</param>
    /// <param name="revisionId">Exact semantic Request revision.</param>
    /// <param name="payloadRevision">Exact semantic revision of the request payload schema.</param>
    /// <param name="createOutcomes">Finite callback declaring every canonical outcome and source-only case.</param>
    /// <param name="responsePolicy">Explicit semantic response, retry, resolution, and retention policy.</param>
    /// <param name="provenance">Producer and root-source attribution for the Request document.</param>
    /// <param name="replyDefinitionPrefix">
    /// Optional stable Reply identity prefix; defaults to the Request identity followed by <c>/reply</c>.
    /// </param>
    /// <param name="replyRevisionId">Optional Reply revision; defaults to the Request revision.</param>
    /// <param name="replyProvenance">
    /// Optional finite attribution projection for Reply documents; defaults to <paramref name="provenance"/>.
    /// The callback is evaluated during authoring and is not retained.
    /// </param>
    /// <param name="extensions">Optional exact-versioned Request extensions.</param>
    /// <param name="displayName">Optional human-facing Request name excluded from fingerprinting.</param>
    /// <param name="description">Optional human-facing Request description excluded from fingerprinting.</param>
    /// <returns>
    /// A typed immutable protocol handle containing canonical documents plus a non-canonical exhaustive C# case
    /// projection.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="createOutcomes"/>, <paramref name="responsePolicy"/>, <paramref name="provenance"/>, or the
    /// returned case descriptor set is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity, revision, outcome, case projection, extension, or descriptive metadata value is invalid; the
    /// response policy is incompatible; or <typeparamref name="TOutcomes"/> does not publicly expose each case
    /// descriptor exactly once in protocol declaration order.
    /// </exception>
    /// <exception cref="InvalidOperationException">Canonical content has no stable representation.</exception>
    /// <exception cref="NotSupportedException">A CLR request or outcome type cannot be represented portably.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical content cannot be encoded by the strict serializer.</exception>
    public static RequestProtocol<TRequest, TOutcome, TOutcomes> CreateRequestProtocol<TRequest, TOutcome, TOutcomes>(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        InteractionValueSchemaRevision payloadRevision,
        Func<RequestProtocolCaseBuilder<TOutcome>, TOutcomes> createOutcomes,
        RequestProtocolResponsePolicy responsePolicy,
        ExecutionProvenance provenance,
        ExecutionDefinitionId? replyDefinitionPrefix = null,
        ExecutionRevisionId? replyRevisionId = null,
        Func<RequestTerminalOutcomeId, ExecutionProvenance>? replyProvenance = null,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default,
        string? displayName = null,
        string? description = null)
        where TOutcome : class
        where TOutcomes : notnull
    {
        ArgumentNullException.ThrowIfNull(createOutcomes);
        ArgumentNullException.ThrowIfNull(responsePolicy);
        ArgumentNullException.ThrowIfNull(provenance);

        var caseBuilder = new RequestProtocolCaseBuilder<TOutcome>();
        var typedOutcomes = createOutcomes(caseBuilder);
        ArgumentNullException.ThrowIfNull(typedOutcomes);
        var (outcomes, cases) = caseBuilder.Complete();
        ValidateCaseSet(typedOutcomes, cases);
        var authored = CreateRequestProtocolDocuments<TRequest>(
            definitionId,
            revisionId,
            payloadRevision,
            outcomes,
            responsePolicy,
            provenance,
            replyDefinitionPrefix,
            replyRevisionId,
            replyProvenance,
            extensions,
            displayName,
            description);
        return new(
            caseBuilder.Owner,
            typedOutcomes,
            outcomes,
            cases,
            authored.RequestDocument,
            authored.Request,
            authored.Replies,
            authored.Documents,
            authored.Validation,
            authored.Catalog);
    }

    static RequestProtocolDocuments CreateRequestProtocolDocuments<TRequest>(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        InteractionValueSchemaRevision payloadRevision,
        ImmutableArray<RequestProtocolOutcome> outcomes,
        RequestProtocolResponsePolicy responsePolicy,
        ExecutionProvenance provenance,
        ExecutionDefinitionId? replyDefinitionPrefix,
        ExecutionRevisionId? replyRevisionId,
        Func<RequestTerminalOutcomeId, ExecutionProvenance>? replyProvenance,
        ImmutableArray<ExecutionDefinitionExtension> extensions,
        string? displayName,
        string? description)
    {
        var definitions = ImmutableArray.CreateBuilder<RequestTerminalOutcomeDefinition>(outcomes.Length);
        foreach (var outcome in outcomes)
        {
            definitions.Add(outcome.Definition);
        }

        var requestDefinition = new RequestContractDefinition(
            new(
                new ValueContract(TypeMapper.Map(typeof(TRequest), null)),
                payloadRevision),
            responsePolicy.CreateObligation(definitions.MoveToImmutable()));
        var initialRequest = InteractionContractDocuments.Create(
            definitionId,
            revisionId,
            requestDefinition,
            provenance,
            extensions,
            displayName,
            description);
        var requestValidation = InteractionContractDocuments.Validate(initialRequest);
        var requestDocument = InteractionContractDocuments.Create(
            definitionId,
            revisionId,
            requestDefinition,
            provenance,
            extensions,
            displayName,
            description,
            diagnostics: requestValidation.Diagnostics);
        RequestContractReference request = new(Reference(requestDocument));

        var replyPrefix = replyDefinitionPrefix ?? new($"{definitionId.Value}/reply");
        var exactReplyRevision = replyRevisionId ?? revisionId;
        var documents = ImmutableArray.CreateBuilder<ExecutionDefinitionDocument>(outcomes.Length + 1);
        var replies = ImmutableArray.CreateBuilder<DurableReplyBinding>(outcomes.Length);
        documents.Add(requestDocument);
        foreach (var outcome in outcomes)
        {
            var replyDefinitionId = new ExecutionDefinitionId($"{replyPrefix.Value}/{outcome.Id.Value}");
            var attribution = replyProvenance?.Invoke(outcome.Id) ?? provenance;
            ArgumentNullException.ThrowIfNull(attribution);
            var replyDefinition = new ReplyContractDefinition(request, outcome.Id);
            var initialReply = InteractionContractDocuments.Create(
                replyDefinitionId,
                exactReplyRevision,
                replyDefinition,
                attribution);
            var replyValidation = InteractionContractDocuments.Validate(initialReply);
            var replyDocument = InteractionContractDocuments.Create(
                replyDefinitionId,
                exactReplyRevision,
                replyDefinition,
                attribution,
                diagnostics: replyValidation.Diagnostics);
            documents.Add(replyDocument);
            replies.Add(new(outcome.Id, new(Reference(replyDocument))));
        }

        var exactDocuments = documents.MoveToImmutable();
        var validation = InteractionContractCatalog.TryCreate(exactDocuments, out var catalog);
        return new RequestProtocolDocuments(
            requestDocument,
            request,
            replies.MoveToImmutable(),
            exactDocuments,
            validation,
            catalog);
    }

    static void ValidateCaseSet<TOutcomes>(
        TOutcomes typedOutcomes,
        ImmutableArray<RequestProtocolCase> cases)
        where TOutcomes : notnull
    {
        var exposed = typeof(TOutcomes)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property =>
                property.GetMethod is { IsStatic: false }
                && property.GetIndexParameters().Length == 0
                && property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(RequestProtocolCase<,>))
            .Select(property => property.GetValue(typedOutcomes) as RequestProtocolCase
                ?? throw new ArgumentException(
                    $"Request protocol case property '{property.Name}' returned null or an incompatible value.",
                    nameof(typedOutcomes)))
            .ToArray();
        if (exposed.Length != cases.Length
            || exposed.Where((candidate, index) => ReferenceEquals(candidate, cases[index])).Count() != cases.Length)
        {
            throw new ArgumentException(
                "The typed Request outcome set must expose every authored RequestProtocolCase as one public instance property in declaration order.",
                nameof(typedOutcomes));
        }
    }

    sealed record RequestProtocolDocuments(
        ExecutionDefinitionDocument RequestDocument,
        RequestContractReference Request,
        ImmutableArray<DurableReplyBinding> Replies,
        ImmutableArray<ExecutionDefinitionDocument> Documents,
        DocumentValidationResult Validation,
        InteractionContractCatalog? Catalog);

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);
}
