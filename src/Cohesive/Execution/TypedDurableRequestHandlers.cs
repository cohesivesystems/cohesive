using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cohesive.Execution;

/// <summary>Typed domain handler for one exact canonical Request protocol.</summary>
/// <remarks>
/// Implementations receive no portable envelope or Reply schema. They return a protocol-owned outcome selection;
/// the generic adapter projects that selection onto <see cref="IDurableOperationAdapter"/>. Handler instances may
/// be invoked concurrently and must protect any mutable target client state accordingly.
/// </remarks>
/// <typeparam name="TRequest">CLR projection of the canonical Request payload.</typeparam>
/// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
public interface IDurableRequestHandler<TRequest, TOutcome>
    where TOutcome : class
{
    /// <summary>Executes one fenced physical attempt and selects one declared semantic outcome.</summary>
    /// <param name="context">Typed attempt identity, fence, deadline, and outcome-selection surface.</param>
    /// <param name="request">Materialized domain Request payload.</param>
    /// <returns>One outcome selected from the exact registered protocol.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> is canceled.</exception>
    ValueTask<DurableRequestOutcome<TOutcome>> ExecuteAsync(
        DurableRequestExecutionContext<TOutcome> context,
        TRequest request);
}

/// <summary>Typed reconciliation handler for one exact canonical Request protocol.</summary>
/// <remarks>
/// Reconciliation inspects retained failed-attempt evidence without blindly performing the operation again. The
/// deployment registration must declare this capability explicitly; implementing this interface alone does not
/// change adapter capability evidence.
/// </remarks>
/// <typeparam name="TRequest">CLR projection of the canonical Request payload.</typeparam>
/// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
public interface IDurableRequestReconciliationHandler<TRequest, TOutcome>
    where TOutcome : class
{
    /// <summary>Reconciles one exact failed unresolved attempt.</summary>
    /// <param name="context">Typed failed-attempt evidence and reconciliation-result surface.</param>
    /// <param name="request">Materialized original domain Request payload.</param>
    /// <returns>Confirmed protocol outcome, confirmed non-execution, or unresolved evidence.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> is canceled.</exception>
    ValueTask<DurableRequestReconciliationResult<TOutcome>> ReconcileAsync(
        DurableRequestReconciliationContext<TOutcome> context,
        TRequest request);
}

/// <summary>Protocol-owned semantic outcome selected by a typed durable Request handler.</summary>
/// <remarks>
/// The selected canonical descriptor remains authoritative. The CLR payload is retained only until the generic
/// adapter projects it into the descriptor's portable contract; this wrapper is never serialized or persisted.
/// </remarks>
/// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
public sealed class DurableRequestOutcome<TOutcome>
    where TOutcome : class
{
    internal DurableRequestOutcome(
        RequestProtocolCase @case,
        object? payload,
        PortableValue? adapterEvidence,
        InteractionOrigin? replyOrigin)
    {
        Case = Guard.RequireNotNull(@case);
        Payload = payload;
        AdapterEvidence = adapterEvidence;
        ReplyOrigin = replyOrigin;
    }

    /// <summary>Protocol case whose canonical outcome was selected.</summary>
    public RequestProtocolCase Case { get; }

    internal object? Payload { get; }

    internal PortableValue? AdapterEvidence { get; }

    internal InteractionOrigin? ReplyOrigin { get; }
}

/// <summary>Typed execution context for one physical durable Request attempt.</summary>
/// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
public sealed class DurableRequestExecutionContext<TOutcome> : ICancellationTokenContext
    where TOutcome : class
{
    readonly Func<RequestProtocolCase, bool> declares;

    internal DurableRequestExecutionContext(
        OperationContext operation,
        DurableOperationInvocation invocation,
        Func<RequestProtocolCase, bool> declares)
    {
        Operation = Guard.RequireNotNull(operation);
        ArgumentNullException.ThrowIfNull(invocation);
        this.declares = Guard.RequireNotNull(declares);
        EmissionId = invocation.Request.Context.EmissionId;
        CorrelationId = invocation.Request.Context.CorrelationId;
        AuthorityScope = invocation.Request.Context.AuthorityScope;
        AttemptId = invocation.AttemptId;
        AttemptOrdinal = invocation.AttemptOrdinal;
        Fence = invocation.Fence;
        DeduplicationKey = invocation.DeduplicationKey;
        DeadlineUtc = invocation.DeadlineUtc;
    }

    /// <summary>Operation-scoped cancellation, time, principal, trace, and metadata.</summary>
    public OperationContext Operation { get; }

    /// <summary>Stable logical canonical Request identity.</summary>
    public EmissionId EmissionId { get; }

    /// <summary>Stable Request correlation identity.</summary>
    public InteractionCorrelationId CorrelationId { get; }

    /// <summary>Tenant and authority boundary of the canonical Request.</summary>
    public InteractionAuthorityScope AuthorityScope { get; }

    /// <summary>Stable physical attempt identity.</summary>
    public OperationAttemptId AttemptId { get; }

    /// <summary>One-based physical attempt ordinal.</summary>
    public int AttemptOrdinal { get; }

    /// <summary>Current durable ownership fence.</summary>
    public OperationFence Fence { get; }

    /// <summary>Scoped target-deduplication key derived from the exact canonical Request.</summary>
    public DurableOperationDeduplicationKey DeduplicationKey { get; }

    /// <summary>Optional semantic Request deadline.</summary>
    public DateTimeOffset? DeadlineUtc { get; }

    /// <summary>Caller cancellation for this physical adapter invocation.</summary>
    public CancellationToken CancellationToken => Operation.CancellationToken;

    /// <summary>Selects one exact protocol-owned semantic outcome.</summary>
    /// <typeparam name="TCase">Distinct source-only case assignable to the registered outcome family.</typeparam>
    /// <typeparam name="TPayload">CLR payload projected by the canonical outcome descriptor.</typeparam>
    /// <param name="outcome">Case descriptor declared by the registered exact protocol.</param>
    /// <param name="payload">Typed domain outcome payload.</param>
    /// <param name="adapterEvidence">Optional materially known portable target receipt.</param>
    /// <param name="replyOrigin">Optional exact semantic origin that produced the Reply.</param>
    /// <returns>A noncanonical selection projected by the generic adapter after the handler returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> belongs to another Request protocol.</exception>
    public DurableRequestOutcome<TOutcome> Outcome<TCase, TPayload>(
        RequestProtocolCase<TCase, TPayload> outcome,
        TPayload payload,
        PortableValue? adapterEvidence = null,
        InteractionOrigin? replyOrigin = null)
        where TCase : class, TOutcome
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!declares(outcome))
        {
            throw new ArgumentException(
                "The selected typed outcome belongs to another exact Request protocol.",
                nameof(outcome));
        }

        return new(outcome, payload, adapterEvidence, replyOrigin);
    }
}

/// <summary>Typed reconciliation result projected onto the canonical durable reconciliation evidence family.</summary>
/// <remarks>
/// The wrapper introduces no second kind enum. It contains either a protocol-owned typed outcome selection or one
/// of the existing canonical reconciliation observations and is discarded after adapter projection.
/// </remarks>
/// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
public sealed class DurableRequestReconciliationResult<TOutcome>
    where TOutcome : class
{
    internal DurableRequestReconciliationResult(
        DurableRequestOutcome<TOutcome>? outcome,
        DurableOperationReconciliationObservation? observation)
    {
        if ((outcome is null) == (observation is null))
        {
            throw new ArgumentException("A typed reconciliation result requires exactly one evidence variant.");
        }

        Outcome = outcome;
        Observation = observation;
    }

    internal DurableRequestOutcome<TOutcome>? Outcome { get; }

    internal DurableOperationReconciliationObservation? Observation { get; }
}

/// <summary>Typed input and result-construction surface for explicit durable Request reconciliation.</summary>
/// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
public sealed class DurableRequestReconciliationContext<TOutcome> : ICancellationTokenContext
    where TOutcome : class
{
    readonly Func<RequestProtocolCase, bool> declares;

    internal DurableRequestReconciliationContext(
        OperationContext operation,
        DurableOperationReconciliationRequest request,
        Func<RequestProtocolCase, bool> declares)
    {
        Operation = Guard.RequireNotNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        this.declares = Guard.RequireNotNull(declares);
        EmissionId = request.Request.Context.EmissionId;
        CorrelationId = request.Request.Context.CorrelationId;
        AuthorityScope = request.Request.Context.AuthorityScope;
        AttemptId = request.Attempt.Claim.AttemptId;
        AttemptOrdinal = request.Attempt.Ordinal;
        Fence = request.Attempt.Claim.Fence;
        DeduplicationKey = request.DeduplicationKey;
        Failure = request.Attempt.Failure!;
        Target = request.Target;
        Identity = request.Identity;
    }

    /// <summary>Operation-scoped cancellation, time, principal, trace, and metadata.</summary>
    public OperationContext Operation { get; }

    /// <summary>Stable logical canonical Request identity.</summary>
    public EmissionId EmissionId { get; }

    /// <summary>Stable Request correlation identity.</summary>
    public InteractionCorrelationId CorrelationId { get; }

    /// <summary>Tenant and authority boundary of the canonical Request.</summary>
    public InteractionAuthorityScope AuthorityScope { get; }

    /// <summary>Failed physical attempt identity being reconciled.</summary>
    public OperationAttemptId AttemptId { get; }

    /// <summary>One-based failed attempt ordinal.</summary>
    public int AttemptOrdinal { get; }

    /// <summary>Ownership fence under which the failed attempt ran.</summary>
    public OperationFence Fence { get; }

    /// <summary>Scoped target-deduplication key retained from the exact canonical Request.</summary>
    public DurableOperationDeduplicationKey DeduplicationKey { get; }

    /// <summary>Explicit failed-attempt phase, effect, retry, and diagnostic evidence.</summary>
    public DurableOperationFailure Failure { get; }

    /// <summary>Exact semantic definition node that realizes reconciliation.</summary>
    public DurableOperationResolutionTarget Target { get; }

    /// <summary>Stable logical reconciliation-obligation identity.</summary>
    public DurableOperationRecoveryIdentity Identity { get; }

    /// <summary>Caller cancellation for this reconciliation interaction.</summary>
    public CancellationToken CancellationToken => Operation.CancellationToken;

    /// <summary>Confirms one exact protocol-owned semantic outcome.</summary>
    /// <typeparam name="TCase">Distinct source-only case assignable to the registered outcome family.</typeparam>
    /// <typeparam name="TPayload">CLR payload projected by the canonical outcome descriptor.</typeparam>
    /// <param name="outcome">Case descriptor declared by the registered exact protocol.</param>
    /// <param name="payload">Typed domain outcome payload confirmed by reconciliation.</param>
    /// <param name="adapterEvidence">Optional materially known portable reconciliation receipt.</param>
    /// <param name="replyOrigin">Optional exact semantic origin that produced the reconciled Reply.</param>
    /// <returns>A typed confirmed-outcome reconciliation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> belongs to another Request protocol.</exception>
    public DurableRequestReconciliationResult<TOutcome> Confirmed<TCase, TPayload>(
        RequestProtocolCase<TCase, TPayload> outcome,
        TPayload payload,
        PortableValue? adapterEvidence = null,
        InteractionOrigin? replyOrigin = null)
        where TCase : class, TOutcome
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!declares(outcome))
        {
            throw new ArgumentException(
                "The reconciled typed outcome belongs to another exact Request protocol.",
                nameof(outcome));
        }

        return new(
            new DurableRequestOutcome<TOutcome>(outcome, payload, adapterEvidence, replyOrigin),
            observation: null);
    }

    /// <summary>Confirms that the external consequence never executed.</summary>
    /// <returns>Canonical confirmed-not-executed evidence.</returns>
    public DurableRequestReconciliationResult<TOutcome> ConfirmedNotExecuted() =>
        new(outcome: null, new DurableOperationConfirmedNotExecuted());

    /// <summary>Reports that reconciliation could not determine the external outcome.</summary>
    /// <param name="detail">Optional materially known portable evidence.</param>
    /// <returns>Canonical unresolved reconciliation evidence.</returns>
    /// <exception cref="ArgumentException"><paramref name="detail"/> is unknown or failed.</exception>
    public DurableRequestReconciliationResult<TOutcome> Unresolved(PortableValue? detail = null) =>
        new(outcome: null, new DurableOperationUnresolved(detail));
}

/// <summary>Factory for typed durable Request handler projections over the raw operation-adapter boundary.</summary>
public static class DurableRequestHandlerAdapter
{
    /// <summary>Creates an adapter that explicitly does not support reconciliation.</summary>
    /// <typeparam name="TRequest">CLR Request payload projection.</typeparam>
    /// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
    /// <typeparam name="TOutcomes">Protocol-owned named case-descriptor set.</typeparam>
    /// <param name="protocol">Exact typed canonical Request protocol.</param>
    /// <param name="handler">Typed domain handler.</param>
    /// <param name="idempotencyEvidence">Explicit target repeat-execution evidence.</param>
    /// <param name="serializerOptions">Optional CLR materialization options.</param>
    /// <returns>A raw durable-operation adapter projected from the typed handler.</returns>
    public static IDurableOperationAdapter Create<TRequest, TOutcome, TOutcomes>(
        RequestProtocol<TRequest, TOutcome, TOutcomes> protocol,
        IDurableRequestHandler<TRequest, TOutcome> handler,
        DurableOperationIdempotencyEvidence idempotencyEvidence,
        JsonSerializerOptions? serializerOptions = null)
        where TOutcome : class
        where TOutcomes : notnull =>
        new TypedDurableRequestHandlerAdapter<TRequest, TOutcome, TOutcomes>(
            protocol,
            handler,
            reconciliationHandler: null,
            idempotencyEvidence,
            serializerOptions);

    /// <summary>Creates an adapter with explicit typed reconciliation support.</summary>
    /// <typeparam name="TRequest">CLR Request payload projection.</typeparam>
    /// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
    /// <typeparam name="TOutcomes">Protocol-owned named case-descriptor set.</typeparam>
    /// <param name="protocol">Exact typed canonical Request protocol.</param>
    /// <param name="handler">Typed domain handler.</param>
    /// <param name="reconciliationHandler">Typed reconciliation handler.</param>
    /// <param name="idempotencyEvidence">Explicit target repeat-execution evidence.</param>
    /// <param name="serializerOptions">Optional CLR materialization options.</param>
    /// <returns>A raw durable-operation adapter projected from the typed handlers.</returns>
    public static IDurableOperationAdapter CreateWithReconciliation<TRequest, TOutcome, TOutcomes>(
        RequestProtocol<TRequest, TOutcome, TOutcomes> protocol,
        IDurableRequestHandler<TRequest, TOutcome> handler,
        IDurableRequestReconciliationHandler<TRequest, TOutcome> reconciliationHandler,
        DurableOperationIdempotencyEvidence idempotencyEvidence,
        JsonSerializerOptions? serializerOptions = null)
        where TOutcome : class
        where TOutcomes : notnull =>
        new TypedDurableRequestHandlerAdapter<TRequest, TOutcome, TOutcomes>(
            protocol,
            handler,
            reconciliationHandler,
            idempotencyEvidence,
            serializerOptions);
}

sealed class TypedDurableRequestHandlerAdapter<TRequest, TOutcome, TOutcomes> : IDurableOperationAdapter
    where TOutcome : class
    where TOutcomes : notnull
{
    readonly RequestProtocol<TRequest, TOutcome, TOutcomes> protocol;
    readonly IDurableRequestHandler<TRequest, TOutcome> handler;
    readonly IDurableRequestReconciliationHandler<TRequest, TOutcome>? reconciliationHandler;
    readonly DurableRequestJsonValueProjector projector;

    internal TypedDurableRequestHandlerAdapter(
        RequestProtocol<TRequest, TOutcome, TOutcomes> protocol,
        IDurableRequestHandler<TRequest, TOutcome> handler,
        IDurableRequestReconciliationHandler<TRequest, TOutcome>? reconciliationHandler,
        DurableOperationIdempotencyEvidence idempotencyEvidence,
        JsonSerializerOptions? serializerOptions)
    {
        this.protocol = Guard.RequireNotNull(protocol);
        this.handler = Guard.RequireNotNull(handler);
        this.reconciliationHandler = reconciliationHandler;
        if (!protocol.IsValid)
        {
            throw new ArgumentException(
                $"Typed durable handler protocol '{Format(protocol.Request)}' is invalid: "
                + string.Join("; ", protocol.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                nameof(protocol));
        }

        projector = new(serializerOptions);
        Capabilities = new(
            idempotencyEvidence,
            reconciliationHandler is null
                ? DurableOperationReconciliationCapability.Unsupported
                : DurableOperationReconciliationCapability.Supported,
            [protocol.Request]);
    }

    public DurableOperationAdapterCapabilities Capabilities { get; }

    public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        context.ThrowIfCancellationRequested();
        if (invocation.Request.Contract != protocol.Request)
        {
            return InvalidAttempt(
                DurableOperationFailureCodes.TypedRequestPayloadInvalid,
                $"Typed durable handler for '{Format(protocol.Request)}' received exact Request "
                + $"'{Format(invocation.Request.Contract)}'.",
                "/request/contract");
        }

        TRequest request;
        try
        {
            request = projector.Decode<TRequest>(invocation.Request.Payload, protocol.InputContract);
        }
        catch (DurableRequestProjectionException exception)
        {
            return InvalidAttempt(
                DurableOperationFailureCodes.TypedRequestPayloadInvalid,
                exception.Diagnostic);
        }

        var typedContext = new DurableRequestExecutionContext<TOutcome>(context, invocation, protocol.Declares);
        var selected = await handler.ExecuteAsync(typedContext, request).ConfigureAwait(false);
        if (selected is null)
        {
            return InvalidOutcome("A typed durable Request handler returned no semantic outcome.");
        }

        try
        {
            return new DurableOperationOutcomeObservation(Project(selected), selected.AdapterEvidence, selected.ReplyOrigin);
        }
        catch (DurableRequestProjectionException exception)
        {
            return InvalidOutcome(exception.Diagnostic);
        }
        catch (ArgumentException exception)
        {
            return InvalidOutcome(
                $"The selected typed outcome contains invalid adapter evidence: {exception.Message}");
        }
    }

    public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        if (reconciliationHandler is null)
        {
            throw new NotSupportedException(
                $"Typed durable handler for '{Format(protocol.Request)}' does not declare reconciliation capability.");
        }
        if (request.Request.Contract != protocol.Request)
        {
            return InvalidReconciliation(
                $"Typed durable reconciliation handler for '{Format(protocol.Request)}' received exact Request "
                + $"'{Format(request.Request.Contract)}'.",
                "/request/contract");
        }

        TRequest typedRequest;
        try
        {
            typedRequest = projector.Decode<TRequest>(request.Request.Payload, protocol.InputContract);
        }
        catch (DurableRequestProjectionException exception)
        {
            return new DurableOperationUnresolved(PortableDiagnostic(exception.Diagnostic));
        }

        var typedContext = new DurableRequestReconciliationContext<TOutcome>(context, request, protocol.Declares);
        var result = await reconciliationHandler.ReconcileAsync(typedContext, typedRequest).ConfigureAwait(false);
        if (result is null)
        {
            return InvalidReconciliation("A typed durable reconciliation handler returned no evidence.");
        }

        if (result.Observation is { } observation)
        {
            return observation;
        }

        try
        {
            var selected = result.Outcome!;
            return new DurableOperationReconciledOutcome(
                Project(selected),
                selected.AdapterEvidence,
                selected.ReplyOrigin);
        }
        catch (DurableRequestProjectionException exception)
        {
            return new DurableOperationUnresolved(PortableDiagnostic(exception.Diagnostic));
        }
        catch (ArgumentException exception)
        {
            return InvalidReconciliation(
                $"The reconciled typed outcome contains invalid adapter evidence: {exception.Message}");
        }
    }

    RequestTerminalOutcome Project(DurableRequestOutcome<TOutcome> selected)
    {
        if (!protocol.Declares(selected.Case))
        {
            throw DurableRequestProjectionException.Create(
                DurableOperationFailureCodes.TypedRequestOutcomeInvalid,
                $"Selected outcome '{selected.Case.Id.Value}' belongs to another exact Request protocol.",
                "/outcome/id",
                protocol.Request);
        }

        var value = projector.Encode(selected.Payload, selected.Case.Outcome.Schema.Contract, protocol.Request);
        return selected.Case.Outcome.Definition switch
        {
            RequestResultDefinition => new RequestResultOutcome(selected.Case.Id, value),
            RequestFailureDefinition => new RequestFailureOutcome(selected.Case.Id, value),
            RequestTimeoutDefinition => new RequestTimeoutOutcome(selected.Case.Id, value),
            RequestCancellationDefinition => new RequestCancellationOutcome(selected.Case.Id, value),
            _ => throw DurableRequestProjectionException.Create(
                DurableOperationFailureCodes.TypedRequestOutcomeInvalid,
                $"Outcome '{selected.Case.Id.Value}' has an unsupported canonical definition kind.",
                "/outcome/id",
                protocol.Request)
        };
    }

    DurableOperationFailureObservation InvalidOutcome(string message) => InvalidOutcome(
        Diagnostic(
            DurableOperationFailureCodes.TypedRequestOutcomeInvalid,
            message,
            "/outcome",
            protocol.Request));

    static DurableOperationFailureObservation InvalidOutcome(DocumentValidationDiagnostic diagnostic) => new(new(
        DurableOperationFailurePhase.PostCallPreCommit,
        DurableOperationEffectEvidence.Ambiguous,
        DurableOperationFailureDisposition.Terminal,
        DurableOperationFailureCodes.TypedRequestOutcomeInvalid,
        PortableDiagnostic(diagnostic)));

    DurableOperationFailureObservation InvalidAttempt(string code, string message, string location) =>
        InvalidAttempt(code, Diagnostic(code, message, location, protocol.Request));

    static DurableOperationFailureObservation InvalidAttempt(
        string code,
        DocumentValidationDiagnostic diagnostic) => new(new(
        DurableOperationFailurePhase.PreCall,
        DurableOperationEffectEvidence.NotExecuted,
        DurableOperationFailureDisposition.Terminal,
        code,
        PortableDiagnostic(diagnostic)));

    DurableOperationReconciliationObservation InvalidReconciliation(string message, string location = "/reconciliation") =>
        new DurableOperationUnresolved(PortableDiagnostic(Diagnostic(
            DurableOperationFailureCodes.TypedRequestReconciliationInvalid,
            message,
            location,
            protocol.Request)));

    static DocumentValidationDiagnostic Diagnostic(
        string code,
        string message,
        string location,
        RequestContractReference protocol) => new(
        code,
        DiagnosticSeverity.Error,
        message,
        location,
        Evidence: new DocumentDiagnosticEvidence(
            stage: "durableRequestHandlerProjection",
            subject: Format(protocol)));

    static PortableValue PortableDiagnostic(DocumentValidationDiagnostic diagnostic) =>
        DurableRequestJsonValueProjector.EncodeDiagnostic(diagnostic);

    static string Format(RequestContractReference request) =>
        $"{request.Definition.DefinitionId.Value}@{request.Definition.RevisionId.Value}"
        + $"#{request.Definition.Fingerprint.Value}";
}

sealed class DurableRequestJsonValueProjector
{
    static readonly ValueContract DiagnosticContract = new(
        new DefaultClrTypeRefMapper().Map(typeof(DocumentValidationDiagnostic), null));

    readonly JsonSerializerOptions serializerOptions;

    internal DurableRequestJsonValueProjector(JsonSerializerOptions? serializerOptions)
    {
        this.serializerOptions = new(serializerOptions ?? JsonSerializerOptions.Default);
        this.serializerOptions.Converters.Insert(
            0,
            new ObservationValueJsonConverter(ObservationBytesJsonEncoding.Base64String));
        this.serializerOptions.Converters.Add(new JsonStringEnumConverter());
        this.serializerOptions.MakeReadOnly();
    }

    internal T Decode<T>(PortableValue value, ValueContract expectedContract)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(expectedContract);
        if (value.Contract != expectedContract)
        {
            throw DurableRequestProjectionException.Create(
                DurableOperationFailureCodes.TypedRequestPayloadInvalid,
                "The canonical Request payload contract differs from the registered typed protocol.",
                "/request/payload/contract");
        }

        var validation = PortableExecutionValidator.Validate(value);
        if (!validation.IsValid)
        {
            var diagnostic = validation.Diagnostics.First(static candidate =>
                candidate.Severity == DiagnosticSeverity.Error);
            throw new DurableRequestProjectionException(diagnostic with
            {
                Code = DurableOperationFailureCodes.TypedRequestPayloadInvalid,
                Location = string.IsNullOrEmpty(diagnostic.Location)
                    ? "/request/payload"
                    : "/request/payload" + diagnostic.Location
            });
        }

        try
        {
            return value.State switch
            {
                PortableValueState.Concrete => JsonSerializer.Deserialize<T>(
                    JsonSerializer.SerializeToUtf8Bytes(value.Value!.Value, serializerOptions),
                    serializerOptions)!,
                PortableValueState.Null => JsonSerializer.Deserialize<T>("null", serializerOptions)!,
                _ => throw DurableRequestProjectionException.Create(
                    DurableOperationFailureCodes.TypedRequestPayloadInvalid,
                    $"A typed durable handler requires a concrete or null Request payload, not '{value.State}'.",
                    "/request/payload/state")
            };
        }
        catch (DurableRequestProjectionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw DurableRequestProjectionException.Create(
                DurableOperationFailureCodes.TypedRequestPayloadInvalid,
                $"The canonical Request payload could not be materialized as '{typeof(T)}': {exception.Message}",
                "/request/payload");
        }
    }

    internal PortableValue Encode(
        object? payload,
        ValueContract contract,
        RequestContractReference protocol)
    {
        ArgumentNullException.ThrowIfNull(contract);
        try
        {
            var projected = payload is null
                ? PortableValue.Null(contract)
                : PortableValue.Concrete(contract, ObservationValue.FromObject(payload));
            var validation = PortableExecutionValidator.Validate(projected);
            if (!validation.IsValid)
            {
                var diagnostic = validation.Diagnostics.First(static candidate =>
                    candidate.Severity == DiagnosticSeverity.Error);
                throw new DurableRequestProjectionException(diagnostic with
                {
                    Code = DurableOperationFailureCodes.TypedRequestOutcomeInvalid,
                    Location = string.IsNullOrEmpty(diagnostic.Location)
                        ? "/outcome/value"
                        : "/outcome/value" + diagnostic.Location,
                    Evidence = diagnostic.Evidence ?? new DocumentDiagnosticEvidence(
                        stage: "durableRequestHandlerProjection",
                        subject: $"{protocol.Definition.DefinitionId.Value}@{protocol.Definition.RevisionId.Value}"
                            + $"#{protocol.Definition.Fingerprint.Value}")
                });
            }
            return projected;
        }
        catch (DurableRequestProjectionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw DurableRequestProjectionException.Create(
                DurableOperationFailureCodes.TypedRequestOutcomeInvalid,
                $"The selected outcome payload could not be projected portably: {exception.Message}",
                "/outcome/value",
                protocol);
        }
    }

    internal static PortableValue EncodeDiagnostic(DocumentValidationDiagnostic diagnostic) =>
        PortableValue.Concrete(DiagnosticContract, ObservationValue.FromObject(diagnostic));
}

sealed class DurableRequestProjectionException : Exception
{
    internal DurableRequestProjectionException(DocumentValidationDiagnostic diagnostic)
        : base(Guard.RequireNotNull(diagnostic).Message)
    {
        Diagnostic = diagnostic;
    }

    internal DocumentValidationDiagnostic Diagnostic { get; }

    internal static DurableRequestProjectionException Create(
        string code,
        string message,
        string location,
        RequestContractReference? protocol = null) => new(new(
        code,
        DiagnosticSeverity.Error,
        message,
        location,
        Evidence: protocol is null
            ? null
            : new DocumentDiagnosticEvidence(
                stage: "durableRequestHandlerProjection",
                subject: $"{protocol.Definition.DefinitionId.Value}@{protocol.Definition.RevisionId.Value}"
                    + $"#{protocol.Definition.Fingerprint.Value}")));
}

/// <summary>Dependency-injection registration entry point for one exact typed durable Request protocol.</summary>
public static class DurableRequestHandlerServiceCollectionExtensions
{
    /// <summary>Begins explicit typed durable-operation registration for one exact protocol.</summary>
    /// <typeparam name="TRequest">CLR Request payload projection.</typeparam>
    /// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
    /// <typeparam name="TOutcomes">Protocol-owned named case-descriptor set.</typeparam>
    /// <param name="services">Application service collection.</param>
    /// <param name="protocol">Exact typed canonical Request protocol.</param>
    /// <param name="serializerOptions">Optional CLR materialization options.</param>
    /// <returns>A registration stage requiring one typed handler.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="protocol"/> is <see langword="null"/>.
    /// </exception>
    public static DurableRequestHandlerRegistration<TRequest, TOutcome, TOutcomes> AddDurableOperation<
        TRequest,
        TOutcome,
        TOutcomes>(
        this IServiceCollection services,
        RequestProtocol<TRequest, TOutcome, TOutcomes> protocol,
        JsonSerializerOptions? serializerOptions = null)
        where TOutcome : class
        where TOutcomes : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(protocol);
        return new(services, protocol, serializerOptions);
    }
}

/// <summary>Typed durable-operation registration stage requiring a handler type.</summary>
/// <typeparam name="TRequest">CLR Request payload projection.</typeparam>
/// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
/// <typeparam name="TOutcomes">Protocol-owned named case-descriptor set.</typeparam>
public sealed class DurableRequestHandlerRegistration<TRequest, TOutcome, TOutcomes>
    where TOutcome : class
    where TOutcomes : notnull
{
    readonly IServiceCollection services;
    readonly RequestProtocol<TRequest, TOutcome, TOutcomes> protocol;
    readonly JsonSerializerOptions? serializerOptions;

    internal DurableRequestHandlerRegistration(
        IServiceCollection services,
        RequestProtocol<TRequest, TOutcome, TOutcomes> protocol,
        JsonSerializerOptions? serializerOptions)
    {
        this.services = services;
        this.protocol = protocol;
        this.serializerOptions = serializerOptions;
    }

    /// <summary>Selects the singleton typed domain handler for the exact protocol.</summary>
    /// <typeparam name="THandler">Thread-safe typed Request handler implementation.</typeparam>
    /// <returns>A registration stage requiring explicit target idempotency evidence.</returns>
    public DurableRequestIdempotencyRegistration<TRequest, TOutcome, TOutcomes, THandler> HandledBy<THandler>()
        where THandler : class, IDurableRequestHandler<TRequest, TOutcome> =>
        new(services, protocol, serializerOptions);
}

/// <summary>Typed durable-operation registration stage requiring explicit idempotency evidence.</summary>
/// <typeparam name="TRequest">CLR Request payload projection.</typeparam>
/// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
/// <typeparam name="TOutcomes">Protocol-owned named case-descriptor set.</typeparam>
/// <typeparam name="THandler">Typed domain handler implementation.</typeparam>
public sealed class DurableRequestIdempotencyRegistration<TRequest, TOutcome, TOutcomes, THandler>
    where TOutcome : class
    where TOutcomes : notnull
    where THandler : class, IDurableRequestHandler<TRequest, TOutcome>
{
    readonly IServiceCollection services;
    readonly RequestProtocol<TRequest, TOutcome, TOutcomes> protocol;
    readonly JsonSerializerOptions? serializerOptions;

    internal DurableRequestIdempotencyRegistration(
        IServiceCollection services,
        RequestProtocol<TRequest, TOutcome, TOutcomes> protocol,
        JsonSerializerOptions? serializerOptions)
    {
        this.services = services;
        this.protocol = protocol;
        this.serializerOptions = serializerOptions;
    }

    /// <summary>Declares explicit target repeat-execution evidence.</summary>
    /// <param name="evidence">Target idempotency evidence; unspecified values are rejected.</param>
    /// <returns>A final registration stage requiring an explicit reconciliation-capability choice.</returns>
    public DurableRequestReconciliationRegistration<TRequest, TOutcome, TOutcomes, THandler> WithIdempotency(
        DurableOperationIdempotencyEvidence evidence)
    {
        if (!Enum.IsDefined(evidence) || evidence == DurableOperationIdempotencyEvidence.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence,
                "Typed durable handler registration requires explicit idempotency evidence.");
        }

        return new(services, protocol, serializerOptions, evidence);
    }
}

/// <summary>Final typed durable-operation registration stage requiring reconciliation capability evidence.</summary>
/// <typeparam name="TRequest">CLR Request payload projection.</typeparam>
/// <typeparam name="TOutcome">Closed source-only outcome-family root.</typeparam>
/// <typeparam name="TOutcomes">Protocol-owned named case-descriptor set.</typeparam>
/// <typeparam name="THandler">Typed domain handler implementation.</typeparam>
public sealed class DurableRequestReconciliationRegistration<TRequest, TOutcome, TOutcomes, THandler>
    where TOutcome : class
    where TOutcomes : notnull
    where THandler : class, IDurableRequestHandler<TRequest, TOutcome>
{
    readonly IServiceCollection services;
    readonly RequestProtocol<TRequest, TOutcome, TOutcomes> protocol;
    readonly JsonSerializerOptions? serializerOptions;
    readonly DurableOperationIdempotencyEvidence idempotencyEvidence;

    internal DurableRequestReconciliationRegistration(
        IServiceCollection services,
        RequestProtocol<TRequest, TOutcome, TOutcomes> protocol,
        JsonSerializerOptions? serializerOptions,
        DurableOperationIdempotencyEvidence idempotencyEvidence)
    {
        this.services = services;
        this.protocol = protocol;
        this.serializerOptions = serializerOptions;
        this.idempotencyEvidence = idempotencyEvidence;
    }

    /// <summary>Completes registration with explicit unsupported reconciliation capability.</summary>
    /// <returns>The application service collection.</returns>
    public IServiceCollection WithoutReconciliation() => Register(reconciliationFactory: null);

    /// <summary>
    /// Completes registration with explicit reconciliation support implemented by the selected handler instance.
    /// </summary>
    /// <returns>The application service collection.</returns>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="THandler"/> does not implement
    /// <see cref="IDurableRequestReconciliationHandler{TRequest,TOutcome}"/>.
    /// </exception>
    public IServiceCollection WithReconciliation() => Register(provider =>
        provider.GetRequiredService<THandler>() as IDurableRequestReconciliationHandler<TRequest, TOutcome>
        ?? throw new InvalidOperationException(
            $"Typed durable handler '{typeof(THandler)}' for '{Format(protocol.Request)}' must implement "
            + $"'{typeof(IDurableRequestReconciliationHandler<TRequest, TOutcome>)}' when reconciliation is declared."));

    /// <summary>Completes registration with a separate typed reconciliation handler.</summary>
    /// <typeparam name="TReconciliationHandler">Thread-safe typed reconciliation handler implementation.</typeparam>
    /// <returns>The application service collection.</returns>
    public IServiceCollection WithReconciliation<TReconciliationHandler>()
        where TReconciliationHandler : class, IDurableRequestReconciliationHandler<TRequest, TOutcome>
    {
        EnsureCatalogResolver(services);
        services.TryAddSingleton<TReconciliationHandler>();
        return Register(static provider => provider.GetRequiredService<TReconciliationHandler>());
    }

    IServiceCollection Register(
        Func<IServiceProvider, IDurableRequestReconciliationHandler<TRequest, TOutcome>>? reconciliationFactory)
    {
        EnsureCatalogResolver(services);
        services.TryAddSingleton<THandler>();
        services.AddSingleton<IDurableOperationAdapter>(provider =>
        {
            var handler = provider.GetRequiredService<THandler>();
            return reconciliationFactory is null
                ? DurableRequestHandlerAdapter.Create(
                    protocol,
                    handler,
                    idempotencyEvidence,
                    serializerOptions)
                : DurableRequestHandlerAdapter.CreateWithReconciliation(
                    protocol,
                    handler,
                    reconciliationFactory(provider),
                    idempotencyEvidence,
                    serializerOptions);
        });
        return services;
    }

    static void EnsureCatalogResolver(IServiceCollection services)
    {
        var existing = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IDurableOperationAdapterResolver))
            .ToArray();
        foreach (var descriptor in existing)
        {
            if (descriptor.ImplementationInstance is EmptyDurableOperationAdapterResolver)
            {
                services.Remove(descriptor);
                continue;
            }
            if (descriptor.ImplementationType != typeof(DurableOperationAdapterCatalog))
            {
                throw new InvalidOperationException(
                    "Typed durable handler registration found a custom IDurableOperationAdapterResolver. "
                    + "Compose the typed adapter into that resolver explicitly instead of relying on registration order.");
            }
        }

        services.TryAddSingleton<IDurableOperationAdapterResolver, DurableOperationAdapterCatalog>();
    }

    static string Format(RequestContractReference request) =>
        $"{request.Definition.DefinitionId.Value}@{request.Definition.RevisionId.Value}"
        + $"#{request.Definition.Fingerprint.Value}";
}
