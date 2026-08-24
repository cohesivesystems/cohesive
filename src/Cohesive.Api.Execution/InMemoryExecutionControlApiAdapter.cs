using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Processes.Runtime;

namespace Cohesive.Api.Execution;

/// <summary>Asynchronously reduces one trusted canonical limit update through its authoritative runtime store.</summary>
/// <remarks>
/// <paramref name="command"/> carries the current trusted invocation evidence. An implementation MUST authorize
/// that current authority scope against the addressed durable state before restoring retained occurrence evidence.
/// After admission, an exact command-identity retry must restore only its retained authorization, issuance, and
/// provenance before canonical reduction. The implementation must use <paramref name="decidedAtUtc"/> unchanged
/// and return state for the command's exact loop, target, and epoch.
/// </remarks>
/// <param name="context">Explicit cancellation, time, and tracing context.</param>
/// <param name="command">Command rebound to trusted API authority, issuance, and provenance evidence.</param>
/// <param name="decidedAtUtc">Exact trusted API observation time used to linearize the command decision.</param>
/// <returns>The canonical decision returned after any required authoritative durable mutation.</returns>
/// <exception cref="ArgumentNullException">
/// <paramref name="context"/> or <paramref name="command"/> is <see langword="null"/>.
/// </exception>
/// <exception cref="ArgumentException">
/// <paramref name="decidedAtUtc"/> is not UTC, or another command value is structurally invalid.
/// </exception>
/// <exception cref="KeyNotFoundException">No exact runtime target exists for the command address.</exception>
/// <exception cref="InvalidOperationException">The authoritative store reports an incoherent mutation result.</exception>
/// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
public delegate ValueTask<ControlLimitUpdateDecision> ExecutionControlLimitUpdateDispatcher(
    OperationContext context,
    ControlLimitUpdateCommand command,
    DateTimeOffset decidedAtUtc);

/// <summary>Asynchronously admits one canonical Process start through an authoritative durable runtime.</summary>
/// <remarks>
/// The dispatcher receives the caller request and trusted invocation separately because exact command replay may
/// restore previously retained occurrence evidence. It must rebind first-time authority, issuance, and provenance,
/// apply canonical <see cref="ProcessStartReferenceEvaluator"/> semantics, and return only after the winning receipt
/// and required identity indexes are durably recoverable.
/// </remarks>
/// <param name="context">Explicit cancellation, time, and tracing context.</param>
/// <param name="request">Canonical caller request whose authority evidence is not trusted.</param>
/// <param name="invocation">Trusted API authorization, issuance, observation, and provenance evidence.</param>
/// <returns>The canonical accepted, replayed, or precise conflict result.</returns>
public delegate ValueTask<Cohesive.Execution.ProcessStartResult> ExecutionProcessStartDispatcher(
    OperationContext context,
    ProcessStartRequest request,
    ExecutionApiInvocationContext invocation);

/// <summary>Asynchronously admits one canonical Process lifecycle mutation through an authoritative runtime.</summary>
/// <remarks>
/// The dispatcher receives the caller request and trusted invocation separately. It must resolve the retained
/// Process by the trusted authority scope plus logical Process identity, restore retained occurrence evidence for
/// replay, and return only after the exact safe canonical result is durably recoverable. Provider event admission
/// alone is not a successful return condition.
/// </remarks>
/// <param name="context">Explicit cancellation, time, and tracing context.</param>
/// <param name="request">Canonical caller request whose authority evidence is not trusted.</param>
/// <param name="invocation">Trusted API authorization, issuance, observation, and provenance evidence.</param>
/// <returns>The exact safe result of canonical Process-control reduction.</returns>
/// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException"><paramref name="request"/> is not a lifecycle mutation.</exception>
/// <exception cref="UnauthorizedAccessException">Canonical authorization rejects the command.</exception>
/// <exception cref="KeyNotFoundException">No Process is visible at the trusted logical address.</exception>
/// <exception cref="OperationCanceledException">Waiting for the durable result is cancelled.</exception>
public delegate ValueTask<ExecutionControlResult> ExecutionProcessControlDispatcher(
    OperationContext context,
    ProcessControlCommand request,
    ExecutionApiInvocationContext invocation);

/// <summary>Trusted server-side evidence used to admit one execution-control API invocation.</summary>
/// <remarks>
/// This value is materialized by an API, identity, or test adapter after authentication and authorization. It is
/// never a client request contract. <see cref="InMemoryExecutionControlApiAdapter"/> replaces all caller-supplied
/// authorization, issuance, and provenance fields with this evidence before dispatch. After admitting the current
/// authority scope, an authoritative dispatcher may restore the retained occurrence evidence for exact replay.
/// </remarks>
public sealed class ExecutionApiInvocationContext
{
    [JsonConstructor]
    internal ExecutionApiInvocationContext(
        ProcessControlAuthorizationContext authorization,
        ExecutionProvenance provenance,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset observedAtUtc,
        ImmutableArray<string> grantedRequirements,
        InteractionEnvelopeContext? signalContext = null)
        : this(
            authorization,
            provenance,
            issuedAtUtc,
            observedAtUtc,
            grantedRequirements.IsDefault
                ? throw new ArgumentException(
                    "Execution API authorization requirement grants must be materialized.",
                    nameof(grantedRequirements))
                : (IReadOnlyList<string>)grantedRequirements,
            signalContext)
    {
    }

    /// <summary>Creates trusted invocation evidence.</summary>
    /// <param name="authorization">Attributable authorization decision for the admitted caller.</param>
    /// <param name="provenance">Trusted adapter and semantic-source attribution.</param>
    /// <param name="issuedAtUtc">UTC time assigned to the canonical command occurrence.</param>
    /// <param name="observedAtUtc">UTC time at which the adapter linearizes the operation.</param>
    /// <param name="grantedRequirements">Stable API authorization requirement identities granted to the caller.</param>
    /// <param name="signalContext">
    /// Optional complete trusted envelope context for a first-time Signal admission. API callers never supply this
    /// value; the transport or identity integration derives it from an authoritative ingress occurrence.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authorization"/>, <paramref name="provenance"/>, or
    /// <paramref name="grantedRequirements"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A grant is empty; either timestamp is not UTC; observation precedes issuance; or
    /// <paramref name="signalContext"/> disagrees with trusted authority/provenance or claims delivery semantics
    /// that this in-memory API boundary cannot realize.
    /// </exception>
    public ExecutionApiInvocationContext(
        ProcessControlAuthorizationContext authorization,
        ExecutionProvenance provenance,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset observedAtUtc,
        IReadOnlyList<string> grantedRequirements,
        InteractionEnvelopeContext? signalContext = null)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(grantedRequirements);
        RequireUtc(issuedAtUtc, nameof(issuedAtUtc));
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (observedAtUtc < issuedAtUtc)
            throw new ArgumentException("Execution API observation cannot precede command issuance.", nameof(observedAtUtc));
        if (signalContext is not null
            && (signalContext.AuthorityScope != authorization.AuthorityScope
                || signalContext.Provenance != provenance))
        {
            throw new ArgumentException(
                "Trusted Signal context must use the invocation's exact authority scope and provenance.",
                nameof(signalContext));
        }
        if (signalContext is not null
            && (signalContext.Delivery.Durability != InteractionDurabilityDemand.Durable
                || signalContext.Delivery.Visibility != InteractionVisibilityDemand.AfterOriginCommit))
        {
            throw new ArgumentException(
                "This API boundary admits Signals only after an authoritative origin commit with durable delivery.",
                nameof(signalContext));
        }

        var grants = ImmutableArray.CreateBuilder<string>(grantedRequirements.Count);
        for (var i = 0; i < grantedRequirements.Count; i++)
        {
            var grant = grantedRequirements[i];
            if (string.IsNullOrWhiteSpace(grant))
                throw new ArgumentException("Authorization requirement grants cannot be empty.", nameof(grantedRequirements));
            grants.Add(grant);
        }

        Authorization = authorization;
        Provenance = provenance;
        IssuedAtUtc = issuedAtUtc;
        ObservedAtUtc = observedAtUtc;
        GrantedRequirements = [.. grants.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        SignalContext = signalContext;
    }

    /// <summary>Attributable authorization decision for the admitted caller.</summary>
    public ProcessControlAuthorizationContext Authorization { get; }

    /// <summary>Trusted adapter and semantic-source attribution.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>UTC time assigned to the canonical command occurrence.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>UTC time at which the adapter linearizes the operation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Granted API authorization requirement identities in deterministic ordinal order.</summary>
    public ImmutableArray<string> GrantedRequirements { get; }

    /// <summary>Complete trusted context for first-time Signal admission, when this invocation carries a Signal.</summary>
    public InteractionEnvelopeContext? SignalContext { get; }

    /// <summary>Determines whether this trusted invocation grants one stable API requirement identity.</summary>
    /// <param name="requirement">Stable authorization requirement identity to test.</param>
    /// <returns><see langword="true"/> when the exact requirement was granted.</returns>
    /// <exception cref="ArgumentException"><paramref name="requirement"/> is empty.</exception>
    public bool GrantsRequirement(string requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
            throw new ArgumentException("An API authorization requirement identity cannot be empty.", nameof(requirement));

        for (var i = 0; i < GrantedRequirements.Length; i++)
        {
            if (string.Equals(GrantedRequirements[i], requirement, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Execution API observations must be expressed in UTC.", parameterName);
    }
}

/// <summary>
/// Linearizable in-memory binding of the canonical transport-neutral execution-control API.
/// </summary>
/// <remarks>
/// This adapter is the package's reference integration and test realization, not a durable production store. Each admitted
/// operation is linearized under the addressed resource lock. Start registry insertion is atomic across instance,
/// command, and idempotency indexes. Canonical reducers remain the sole owners of replay, optimistic-fence, and
/// lifecycle semantics. When an authoritative Start, lifecycle-mutation, or limit-update dispatcher is configured,
/// that operation bypasses its local registry entirely; the adapter only authorizes and projects the authoritative
/// decision. External dispatchers remain responsible for trusted-context rebinding and durable occurrence replay.
/// </remarks>
public sealed class InMemoryExecutionControlApiAdapter : IExecutionControlApiDispatcher
{
    readonly object processRegistryGate = new();
    readonly object controlRegistryGate = new();
    readonly ExecutionControlApiCatalog catalog;
    readonly ProcessStartReferenceEvaluator startEvaluator = new();
    readonly ProcessControlReferenceExecutor processExecutor;
    readonly Func<ProcessControlState, ExecutionRuntimeStatusDetails?>? runtimeStatus;
    readonly Func<ProcessControlState, ExecutionTerminalOutcome?>? terminalOutcome;
    readonly Func<InspectProcessCommand, ExecutionExplainArtifact?>? explain;
    readonly Func<InspectProcessCommand, ProcessExecutionTraceReadResult>? traces;
    readonly ExecutionControlLimitUpdateDispatcher? limitUpdateDispatcher;
    readonly ExecutionProcessStartDispatcher? startDispatcher;
    readonly ExecutionProcessControlDispatcher? processControlDispatcher;
    readonly Dictionary<ProcessKey, ProcessEntry> processes = [];
    readonly Dictionary<StartCommandKey, ProcessStartReceipt> startsByCommand = [];
    readonly Dictionary<StartIdempotencyKey, ProcessStartReceipt> startsByIdempotency = [];
    readonly Dictionary<ControlKey, ControlEntry> controls = [];

    /// <summary>Creates an in-memory binding over exact canonical interaction contracts.</summary>
    /// <param name="contracts">Catalog used by the Process-control reference executor to validate typed interactions.</param>
    /// <param name="catalog">Optional canonical API catalog; a fresh canonical catalog is used when omitted.</param>
    /// <param name="runtimeStatus">Optional safe runtime-status projection for token, wait, demand, and extension facets.</param>
    /// <param name="terminalOutcome">Optional safe terminal-outcome projection.</param>
    /// <param name="explain">
    /// Optional canonical explanation projection over a trusted read-only Process inspection request.
    /// </param>
    /// <param name="limitUpdateDispatcher">
    /// Optional authoritative asynchronous dispatcher. When supplied, callers MUST use <see cref="DispatchAsync"/>
    /// for <c>updateLimits</c>; the local Control registry is not consulted.
    /// </param>
    /// <param name="traces">
    /// Optional canonical retained-trace projection over a trusted read-only Process inspection request.
    /// </param>
    /// <param name="startDispatcher">
    /// Optional authoritative asynchronous Process-start admission. When supplied, callers MUST use
    /// <see cref="DispatchAsync"/> for Start; the local Process-start registry is not consulted.
    /// </param>
    /// <param name="processControlDispatcher">
    /// Optional authoritative asynchronous lifecycle-mutation admission. When supplied, callers MUST use
    /// <see cref="DispatchAsync"/> for Pause, Continue, RestartAttempt, Cancel, and Terminate; the local Process
    /// registry is not consulted for those operations.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="contracts"/> is <see langword="null"/>.</exception>
    public InMemoryExecutionControlApiAdapter(
        InteractionContractCatalog contracts,
        ExecutionControlApiCatalog? catalog = null,
        Func<ProcessControlState, ExecutionRuntimeStatusDetails?>? runtimeStatus = null,
        Func<ProcessControlState, ExecutionTerminalOutcome?>? terminalOutcome = null,
        Func<InspectProcessCommand, ExecutionExplainArtifact?>? explain = null,
        ExecutionControlLimitUpdateDispatcher? limitUpdateDispatcher = null,
        Func<InspectProcessCommand, ProcessExecutionTraceReadResult>? traces = null,
        ExecutionProcessStartDispatcher? startDispatcher = null,
        ExecutionProcessControlDispatcher? processControlDispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        this.catalog = catalog ?? ExecutionControlApiCatalog.Create();
        processExecutor = new(contracts);
        this.runtimeStatus = runtimeStatus;
        this.terminalOutcome = terminalOutcome;
        this.explain = explain;
        this.traces = traces;
        this.limitUpdateDispatcher = limitUpdateDispatcher;
        this.startDispatcher = startDispatcher;
        this.processControlDispatcher = processControlDispatcher;
    }

    /// <summary>Canonical semantic endpoint catalog bound by this adapter.</summary>
    public ExecutionControlApiCatalog Catalog => catalog;

    /// <summary>Dispatches one request through an exact endpoint handle from <see cref="Catalog"/>.</summary>
    /// <param name="endpoint">Exact typed endpoint handle owned by <see cref="Catalog"/>.</param>
    /// <param name="request">Request value whose runtime type must match the endpoint declaration.</param>
    /// <param name="invocation">Trusted server-side authorization, timing, and provenance evidence.</param>
    /// <returns>The exact declared result variant and its structurally safe response body.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="endpoint"/>, <paramref name="request"/>, or <paramref name="invocation"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="endpoint"/> is not owned by this adapter, or a first-time Signal invocation lacks a trusted
    /// <see cref="ExecutionApiInvocationContext.SignalContext"/>, or synchronous Start, lifecycle mutation, or
    /// <c>updateLimits</c> dispatch is attempted while an authoritative asynchronous dispatcher is configured, or
    /// an explanation projection returns evidence for another Process instance or attempt.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Trusted observation time precedes durable state or first-time command issuance.
    /// </exception>
    /// <exception cref="OverflowException">A canonical reducer exhausts its semantic revision space.</exception>
    public ExecutionApiDispatchResult Dispatch(
        ApiEndpoint endpoint,
        object request,
        ExecutionApiInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocation);
        EnsureOwnedEndpoint(endpoint);

        if (startDispatcher is not null && ReferenceEquals(endpoint, catalog.Start))
        {
            throw new InvalidOperationException(
                "An authoritative asynchronous Process-start dispatcher requires DispatchAsync.");
        }

        if (processControlDispatcher is not null && IsLifecycleMutationEndpoint(endpoint))
        {
            throw new InvalidOperationException(
                "An authoritative asynchronous Process-control dispatcher requires DispatchAsync.");
        }

        if (!IsAuthorized(endpoint, invocation))
            return Problem(endpoint, ApiResultKind.Forbidden, ExecutionApiProblemCodes.Forbidden);

        if (ReferenceEquals(endpoint, catalog.Start))
        {
            return request is ProcessStartRequest start
                ? DispatchStart(start, invocation)
                : TypeMismatch(endpoint);
        }

        if (ReferenceEquals(endpoint, catalog.Explain))
        {
            if (request is not InspectProcessCommand inspect)
                return TypeMismatch(endpoint);
            var canonical = (InspectProcessCommand)ExecutionProcessControlCommandAdmission.Rebind(
                inspect,
                invocation,
                prior: null);
            var artifact = explain?.Invoke(canonical);
            if (artifact is null)
                return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
            if (artifact.RuntimeStatus?.ProcessInstanceId != canonical.Context.ProcessInstanceId
                || canonical.Expectation is { } expectation
                    && artifact.RuntimeStatus.CurrentAttemptId != expectation.Continuation.ProcessAttemptId)
            {
                throw new InvalidOperationException(
                    "The explanation projection returned evidence for another Process instance or attempt.");
            }
            return Result(endpoint, ApiResultKind.Success, artifact);
        }

        if (ReferenceEquals(endpoint, catalog.Traces))
        {
            if (request is not InspectProcessCommand inspect)
                return TypeMismatch(endpoint);
            var canonical = (InspectProcessCommand)ExecutionProcessControlCommandAdmission.Rebind(
                inspect,
                invocation,
                prior: null);
            var read = traces?.Invoke(canonical) ?? ProcessExecutionTraceReadResult.NotFound();
            var result = catalog.GetTraceResult(read.State);
            if (read.State != ProcessExecutionTraceReadState.Available)
            {
                return new(
                    endpoint,
                    result,
                    new ExecutionApiProblem(ExecutionApiProblemCodes.ForTraceReadState(read.State)));
            }

            var artifact = read.Artifact!;
            if (artifact.ProcessInstanceId != canonical.Context.ProcessInstanceId)
            {
                throw new InvalidOperationException(
                    "The retained-trace projection returned evidence for another Process instance.");
            }

            return new(endpoint, result, artifact);
        }

        if (ReferenceEquals(endpoint, catalog.UpdateLimits))
        {
            if (limitUpdateDispatcher is not null)
            {
                throw new InvalidOperationException(
                    "An authoritative asynchronous limit-update dispatcher requires DispatchAsync.");
            }
            return request is ControlLimitUpdateCommand update
                ? DispatchLimitUpdate(update, invocation)
                : TypeMismatch(endpoint);
        }

        if (ReferenceEquals(endpoint, catalog.Inspect) && request is not InspectProcessCommand
            || ReferenceEquals(endpoint, catalog.Signal) && request is not SignalProcessCommand
            || ReferenceEquals(endpoint, catalog.Pause) && request is not PauseProcessCommand
            || ReferenceEquals(endpoint, catalog.Continue) && request is not ContinueProcessCommand
            || ReferenceEquals(endpoint, catalog.RestartAttempt) && request is not RestartProcessAttemptCommand
            || ReferenceEquals(endpoint, catalog.Cancel) && request is not CancelProcessCommand
            || ReferenceEquals(endpoint, catalog.Terminate) && request is not TerminateProcessCommand)
        {
            return TypeMismatch(endpoint);
        }

        return DispatchProcessControl(endpoint, (ProcessControlCommand)request, invocation);
    }

    /// <summary>
    /// Dispatches through an authoritative asynchronous Start or limit-update binding when configured, and
    /// otherwise reuses the synchronous reference path.
    /// </summary>
    /// <param name="context">Explicit cancellation, time, and tracing context.</param>
    /// <param name="endpoint">Exact typed endpoint handle owned by <see cref="Catalog"/>.</param>
    /// <param name="request">Request value whose runtime type must match the endpoint declaration.</param>
    /// <param name="invocation">Trusted server-side authorization, timing, and provenance evidence.</param>
    /// <returns>The exact declared result variant and its structurally safe response body.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Trusted evidence is chronologically invalid or a command value is structurally invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="endpoint"/> is not owned by this adapter; a first-time Signal invocation lacks a trusted
    /// <see cref="ExecutionApiInvocationContext.SignalContext"/>; or the authoritative dispatcher or store returns
    /// incoherent evidence.
    /// </exception>
    /// <exception cref="OverflowException">A canonical reducer exhausts its semantic revision space.</exception>
    /// <exception cref="OperationCanceledException">The authoritative dispatch is cancelled.</exception>
    public async ValueTask<ExecutionApiDispatchResult> DispatchAsync(
        OperationContext context,
        ApiEndpoint endpoint,
        object request,
        ExecutionApiInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocation);
        context.ThrowIfCancellationRequested();
        if (startDispatcher is not null && ReferenceEquals(endpoint, catalog.Start))
        {
            EnsureOwnedEndpoint(endpoint);
            if (!IsAuthorized(endpoint, invocation))
                return Problem(endpoint, ApiResultKind.Forbidden, ExecutionApiProblemCodes.Forbidden);
            if (request is not ProcessStartRequest start)
                return TypeMismatch(endpoint);

            var startResult = await startDispatcher(context, start, invocation).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The authoritative Process-start dispatcher returned no canonical result.");
            return Result(
                endpoint,
                startResult.IsConflict ? ApiResultKind.Conflict : ApiResultKind.Success,
                startResult);
        }


        if (processControlDispatcher is not null && IsLifecycleMutationEndpoint(endpoint))
        {
            EnsureOwnedEndpoint(endpoint);
            if (!IsAuthorized(endpoint, invocation))
                return Problem(endpoint, ApiResultKind.Forbidden, ExecutionApiProblemCodes.Forbidden);
            if (!RequestMatchesLifecycleEndpoint(endpoint, request))
                return TypeMismatch(endpoint);

            ExecutionControlResult controlResult;
            try
            {
                controlResult = await processControlDispatcher(
                        context,
                        (ProcessControlCommand)request,
                        invocation)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "The authoritative Process-control dispatcher returned no canonical result.");
            }
            catch (UnauthorizedAccessException)
            {
                return Problem(endpoint, ApiResultKind.Forbidden, ExecutionApiProblemCodes.Forbidden);
            }
            catch (KeyNotFoundException)
            {
                return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
            }
            return Result(endpoint, controlResult.ResultKind, controlResult);
        }

        if (limitUpdateDispatcher is null || !ReferenceEquals(endpoint, catalog.UpdateLimits))
            return Dispatch(endpoint, request, invocation);

        EnsureOwnedEndpoint(endpoint);
        if (!IsAuthorized(endpoint, invocation))
            return Problem(endpoint, ApiResultKind.Forbidden, ExecutionApiProblemCodes.Forbidden);
        if (request is not ControlLimitUpdateCommand update)
            return TypeMismatch(endpoint);

        var canonical = Rebind(update, invocation, prior: null);
        ControlLimitUpdateDecision decision;
        try
        {
            decision = await limitUpdateDispatcher(
                        context,
                        canonical,
                        invocation.ObservedAtUtc)
                    .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The authoritative limit-update dispatcher returned no canonical decision.");
        }
        catch (KeyNotFoundException)
        {
            return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        }
        return LimitUpdateResult(decision, canonical, invocation);
    }

    /// <summary>Registers one bounded Control loop and epoch for subsequent <c>updateLimits</c> dispatch.</summary>
    /// <param name="definition">Canonical immutable bounded loop definition.</param>
    /// <param name="epoch">Current Process attempt, index generation, or other controlled epoch.</param>
    /// <param name="authorityScope">Authority and optional tenant boundary that owns the loop.</param>
    /// <param name="createdAtUtc">Explicit UTC registration time.</param>
    /// <returns>The initial durable Control revision.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="authorityScope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is default or time is not UTC.</exception>
    /// <exception cref="InvalidOperationException">
    /// An authoritative limit-update dispatcher is configured, or the exact scoped loop, target, and epoch are
    /// already registered.
    /// </exception>
    public ControlRevision RegisterControlLoop(
        ControlLoopDefinition definition,
        ControlEpochId epoch,
        InteractionAuthorityScope authorityScope,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(authorityScope);
        EnsureLocalControlRegistryEnabled();
        var state = ControlLoopState.Create(definition, epoch, authorityScope, createdAtUtc);
        var key = new ControlKey(authorityScope, definition.Id, definition.Target, epoch);
        lock (controlRegistryGate)
        {
            if (!controls.TryAdd(key, new(definition, state)))
                throw new InvalidOperationException("The exact scoped Control loop and epoch are already registered.");
        }

        return state.Revision;
    }

    /// <summary>Applies a pending limit update at an exact invariant-preserving runtime safe point.</summary>
    /// <param name="authorityScope">Authority and optional tenant boundary owning the registered loop.</param>
    /// <param name="applicationPoint">Exact safe-point evidence supplied by the controlled runtime.</param>
    /// <param name="appliedAtUtc">Explicit UTC application observation.</param>
    /// <returns>The canonical applied, replayed, deferred, or rejected actuation disposition.</returns>
    /// <remarks>This runtime hook is intentionally not one of the nine client-facing API operations.</remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authorityScope"/> or <paramref name="applicationPoint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">An authoritative limit-update dispatcher is configured.</exception>
    /// <exception cref="KeyNotFoundException">No exact scoped loop, target, and epoch are registered.</exception>
    /// <exception cref="ArgumentException"><paramref name="appliedAtUtc"/> is not UTC.</exception>
    public ControlActuationDisposition ApplyLimitsAtSafePoint(
        InteractionAuthorityScope authorityScope,
        ControlApplicationPoint applicationPoint,
        DateTimeOffset appliedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(authorityScope);
        ArgumentNullException.ThrowIfNull(applicationPoint);
        EnsureLocalControlRegistryEnabled();
        var key = new ControlKey(
            authorityScope,
            applicationPoint.LoopId,
            applicationPoint.Target,
            applicationPoint.Epoch);
        ControlEntry entry;
        lock (controlRegistryGate)
        {
            if (!controls.TryGetValue(key, out entry!))
                throw new KeyNotFoundException("No exact scoped Control loop and epoch are registered.");
        }

        lock (entry.Gate)
        {
            var result = ControlLimitUpdateReferenceReducer.Apply(
                entry.Definition,
                entry.State,
                applicationPoint,
                appliedAtUtc);
            entry.State = result.State;
            return result.Disposition;
        }
    }

    ExecutionApiDispatchResult DispatchStart(
        ProcessStartRequest request,
        ExecutionApiInvocationContext invocation)
    {
        lock (processRegistryGate)
        {
            var scope = invocation.Authorization.AuthorityScope;
            var commandKey = new StartCommandKey(scope, request.Context.CommandId);
            startsByCommand.TryGetValue(commandKey, out var sameCommand);
            var trustedContext = Rebind(request.Context, invocation, sameCommand?.Request.Context);
            var canonical = new ProcessStartRequest(
                request.SchemaVersion,
                request.Definition,
                trustedContext,
                request.InitialContinuation,
                request.Input);
            startsByIdempotency.TryGetValue(
                new(scope, canonical.Context.IdempotencyKey),
                out var sameIdempotency);
            processes.TryGetValue(
                new(scope, canonical.Context.ProcessInstanceId),
                out var existing);
            var decision = startEvaluator.Evaluate(
                canonical,
                new(
                    sameCommand,
                    sameIdempotency,
                    existing?.Receipt,
                    existing?.State),
                invocation.ObservedAtUtc);

            if (decision.RequiresPersistence)
            {
                var receipt = decision.Receipt!;
                var state = decision.State!;
                processes.Add(new(scope, state.ProcessInstanceId), new(receipt, state));
                startsByCommand.Add(commandKey, receipt);
                startsByIdempotency.Add(new(scope, canonical.Context.IdempotencyKey), receipt);
            }

            var kind = decision.Result.IsConflict ? ApiResultKind.Conflict : ApiResultKind.Success;
            return Result(catalog.Start, kind, decision.Result);
        }
    }

    ExecutionApiDispatchResult DispatchProcessControl(
        ApiEndpoint endpoint,
        ProcessControlCommand request,
        ExecutionApiInvocationContext invocation)
    {
        var scope = invocation.Authorization.AuthorityScope;
        ProcessEntry entry;
        lock (processRegistryGate)
        {
            if (!processes.TryGetValue(new(scope, request.Context.ProcessInstanceId), out entry!))
                return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        }

        lock (entry.Gate)
        {
            var canonical = ExecutionProcessControlCommandAdmission.Rebind(request, invocation, entry.State);
            var decision = processExecutor.Apply(entry.State, canonical, invocation.ObservedAtUtc);
            entry.State = decision.State;
            if (decision.Disposition == ProcessControlDecisionDisposition.Unauthorized)
                return Problem(endpoint, ApiResultKind.Forbidden, ExecutionApiProblemCodes.Forbidden);
            if (decision.Disposition == ProcessControlDecisionDisposition.TargetMismatch)
                return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);

            var result = ExecutionControlResult.FromDecision(
                decision,
                runtimeStatus?.Invoke(decision.State),
                terminalOutcome?.Invoke(decision.State));
            return Result(endpoint, result.ResultKind, result);
        }
    }

    ExecutionApiDispatchResult DispatchLimitUpdate(
        ControlLimitUpdateCommand request,
        ExecutionApiInvocationContext invocation)
    {
        var scope = invocation.Authorization.AuthorityScope;
        var key = new ControlKey(scope, request.LoopId, request.Target, request.Epoch);
        ControlEntry entry;
        lock (controlRegistryGate)
        {
            if (!controls.TryGetValue(key, out entry!))
                return Problem(catalog.UpdateLimits, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        }

        lock (entry.Gate)
        {
            var prior = entry.State.FindLimitUpdateReceipt(request.CommandId)?.Command;
            var canonical = Rebind(request, invocation, prior);
            var decision = ControlLimitUpdateReferenceReducer.Submit(
                entry.Definition,
                entry.State,
                canonical,
                invocation.ObservedAtUtc);
            entry.State = decision.State;
            return LimitUpdateResult(decision, canonical, invocation);
        }
    }

    ExecutionApiDispatchResult LimitUpdateResult(
        ControlLimitUpdateDecision decision,
        ControlLimitUpdateCommand command,
        ExecutionApiInvocationContext invocation)
    {
        EnsureCoherentLimitUpdateDecision(decision, command, invocation);
        if (decision.Disposition == ControlLimitUpdateDecisionDisposition.Unauthorized)
        {
            return Problem(
                catalog.UpdateLimits,
                ApiResultKind.Forbidden,
                ExecutionApiProblemCodes.Forbidden);
        }

        var kind = decision.Disposition switch
        {
            ControlLimitUpdateDecisionDisposition.Accepted => ApiResultKind.Accepted,
            ControlLimitUpdateDecisionDisposition.Replayed
                when decision.Receipt is not null
                     && decision.State.PendingLimitUpdate == decision.Receipt => ApiResultKind.Accepted,
            ControlLimitUpdateDecisionDisposition.Stale => ApiResultKind.PreconditionFailed,
            ControlLimitUpdateDecisionDisposition.IdentityConflict
                or ControlLimitUpdateDecisionDisposition.IdempotencyConflict
                or ControlLimitUpdateDecisionDisposition.PendingConflict => ApiResultKind.Conflict,
            ControlLimitUpdateDecisionDisposition.OutOfBounds
                or ControlLimitUpdateDecisionDisposition.Invalid => ApiResultKind.ValidationFailed,
            _ => ApiResultKind.Success
        };
        return Result(catalog.UpdateLimits, kind, ControlLimitUpdateResult.FromDecision(decision));
    }

    static void EnsureCoherentLimitUpdateDecision(
        ControlLimitUpdateDecision decision,
        ControlLimitUpdateCommand command,
        ExecutionApiInvocationContext invocation)
    {
        var state = decision.State;
        if (state.LoopId != command.LoopId
            || !string.Equals(state.Target, command.Target, StringComparison.Ordinal)
            || state.Epoch != command.Epoch)
        {
            throw new InvalidOperationException(
                "The authoritative limit-update dispatcher returned state for a different command address.");
        }

        if (decision.Disposition == ControlLimitUpdateDecisionDisposition.Unauthorized)
            return;
        if (state.AuthorityScope != invocation.Authorization.AuthorityScope)
        {
            throw new InvalidOperationException(
                "The authoritative limit-update dispatcher returned state outside the trusted authority scope.");
        }

        if (decision.Disposition == ControlLimitUpdateDecisionDisposition.Accepted)
        {
            if (decision.Receipt?.Command != command
                || decision.Receipt.AcceptedAtUtc != invocation.ObservedAtUtc)
            {
                throw new InvalidOperationException(
                    "The authoritative limit-update dispatcher returned an incoherent acceptance receipt.");
            }
            return;
        }

        if (decision.Disposition != ControlLimitUpdateDecisionDisposition.Replayed)
            return;

        var retained = decision.Receipt!.Command;
        var exactReplay = retained.CommandId == command.CommandId
            && retained == Rebind(command, invocation, retained);
        var semanticReplay = retained.CommandId != command.CommandId
            && ControlLimitUpdateReferenceReducer.HasSameIdempotentIntent(retained, command);
        if (retained.Authorization.AuthorityScope != invocation.Authorization.AuthorityScope
            || (!exactReplay && !semanticReplay))
        {
            throw new InvalidOperationException(
                "The authoritative limit-update dispatcher returned an incoherent replay receipt.");
        }
    }

    static ControlLimitUpdateCommand Rebind(
        ControlLimitUpdateCommand command,
        ExecutionApiInvocationContext invocation,
        ControlLimitUpdateCommand? prior) =>
        new(
            command.SchemaVersion,
            command.CommandId,
            command.IdempotencyKey,
            command.LoopId,
            command.DefinitionFingerprint,
            command.Target,
            command.Epoch,
            command.ExpectedRevision,
            command.RequestedOperatingPoint,
            prior?.Authorization ?? invocation.Authorization,
            prior?.IssuedAtUtc ?? invocation.IssuedAtUtc,
            prior?.Provenance ?? invocation.Provenance);

    static ProcessControlCommandContext Rebind(
        ProcessControlCommandContext context,
        ExecutionApiInvocationContext invocation,
        ProcessControlCommandContext? prior) =>
        new(
            context.CommandId,
            context.IdempotencyKey,
            context.ProcessInstanceId,
            prior?.Authorization ?? invocation.Authorization,
            prior?.IssuedAtUtc ?? invocation.IssuedAtUtc,
            prior?.Provenance ?? invocation.Provenance);

    bool IsAuthorized(ApiEndpoint endpoint, ExecutionApiInvocationContext invocation)
    {
        var requirements = endpoint.Operation.AuthorizationRequirements;
        for (var i = 0; i < requirements.Count; i++)
        {
            if (!invocation.GrantsRequirement(requirements[i].Id))
                return false;
        }

        return true;
    }

    bool IsLifecycleMutationEndpoint(ApiEndpoint endpoint) =>
        ReferenceEquals(endpoint, catalog.Pause)
        || ReferenceEquals(endpoint, catalog.Continue)
        || ReferenceEquals(endpoint, catalog.RestartAttempt)
        || ReferenceEquals(endpoint, catalog.Cancel)
        || ReferenceEquals(endpoint, catalog.Terminate);

    bool RequestMatchesLifecycleEndpoint(ApiEndpoint endpoint, object request) =>
        ReferenceEquals(endpoint, catalog.Pause) && request is PauseProcessCommand
        || ReferenceEquals(endpoint, catalog.Continue) && request is ContinueProcessCommand
        || ReferenceEquals(endpoint, catalog.RestartAttempt) && request is RestartProcessAttemptCommand
        || ReferenceEquals(endpoint, catalog.Cancel) && request is CancelProcessCommand
        || ReferenceEquals(endpoint, catalog.Terminate) && request is TerminateProcessCommand;

    void EnsureOwnedEndpoint(ApiEndpoint endpoint)
    {
        if (!ReferenceEquals(endpoint, catalog.Start)
            && !ReferenceEquals(endpoint, catalog.Inspect)
            && !ReferenceEquals(endpoint, catalog.Explain)
            && !ReferenceEquals(endpoint, catalog.Traces)
            && !ReferenceEquals(endpoint, catalog.Signal)
            && !ReferenceEquals(endpoint, catalog.Pause)
            && !ReferenceEquals(endpoint, catalog.Continue)
            && !ReferenceEquals(endpoint, catalog.RestartAttempt)
            && !ReferenceEquals(endpoint, catalog.Cancel)
            && !ReferenceEquals(endpoint, catalog.Terminate)
            && !ReferenceEquals(endpoint, catalog.UpdateLimits))
        {
            throw new InvalidOperationException("The endpoint handle is not owned by this execution API adapter.");
        }
    }

    void EnsureLocalControlRegistryEnabled()
    {
        if (limitUpdateDispatcher is not null)
        {
            throw new InvalidOperationException(
                "The local Control registry is disabled while an authoritative limit-update dispatcher is configured.");
        }
    }

    ExecutionApiDispatchResult TypeMismatch(ApiEndpoint endpoint) =>
        Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);

    ExecutionApiDispatchResult Problem(ApiEndpoint endpoint, ApiResultKind kind, string code) =>
        Result(endpoint, kind, new ExecutionApiProblem(code));

    ExecutionApiDispatchResult Result(ApiEndpoint endpoint, ApiResultKind kind, object body) =>
        new(endpoint, catalog.GetResult(endpoint, kind), body);

    readonly record struct ProcessKey(InteractionAuthorityScope Scope, ProcessInstanceId InstanceId);

    readonly record struct StartCommandKey(InteractionAuthorityScope Scope, ProcessControlCommandId CommandId);

    readonly record struct StartIdempotencyKey(
        InteractionAuthorityScope Scope,
        ProcessControlIdempotencyKey IdempotencyKey);

    readonly record struct ControlKey(
        InteractionAuthorityScope Scope,
        ControlLoopId LoopId,
        string Target,
        ControlEpochId Epoch);

    sealed class ProcessEntry(ProcessStartReceipt receipt, ProcessControlState state)
    {
        internal object Gate { get; } = new();

        internal ProcessStartReceipt Receipt { get; } = receipt;

        internal ProcessControlState State { get; set; } = state;
    }

    sealed class ControlEntry(ControlLoopDefinition definition, ControlLoopState state)
    {
        internal object Gate { get; } = new();

        internal ControlLoopDefinition Definition { get; } = definition;

        internal ControlLoopState State { get; set; } = state;
    }
}
