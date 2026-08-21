using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class TypedDurableRequestHandlerTests
{
    static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Execute_ProjectsTypedHandlerToTheRawAdapterProtocolWithoutSemanticDrift()
    {
        var protocol = CreateProtocol();
        var handler = new CapturingHandler(protocol, HandlerOutcome.Accepted);
        var typed = DurableRequestHandlerAdapter.Create(
            protocol,
            handler,
            DurableOperationIdempotencyEvidence.TargetDeduplication);
        var raw = new RawAdapter(protocol, HandlerOutcome.Accepted);
        var invocation = CreateInvocation(protocol, new SubmitTraining("dataset/42"));
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(ObservedAtUtc));

        var typedObservation = await DurableOperationReferenceExecutor.ExecuteAsync(context, invocation, typed);
        var rawObservation = await DurableOperationReferenceExecutor.ExecuteAsync(context, invocation, raw);

        Assert.Equal(rawObservation, typedObservation);
        Assert.Equal(protocol.Request, Assert.Single(typed.Capabilities.SupportedRequests));
        Assert.Equal(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            typed.Capabilities.IdempotencyEvidence);
        Assert.Equal(
            DurableOperationReconciliationCapability.Unsupported,
            typed.Capabilities.Reconciliation);
        Assert.Equal(invocation.Request.Context.EmissionId, handler.ExecutionContext?.EmissionId);
        Assert.Equal(invocation.Request.Context.CorrelationId, handler.ExecutionContext?.CorrelationId);
        Assert.Equal(invocation.Request.Context.AuthorityScope, handler.ExecutionContext?.AuthorityScope);
        Assert.Equal(invocation.AttemptId, handler.ExecutionContext?.AttemptId);
        Assert.Equal(invocation.AttemptOrdinal, handler.ExecutionContext?.AttemptOrdinal);
        Assert.Equal(invocation.Fence, handler.ExecutionContext?.Fence);
        Assert.Equal(invocation.DeduplicationKey, handler.ExecutionContext?.DeduplicationKey);
        Assert.Equal(invocation.DeadlineUtc, handler.ExecutionContext?.DeadlineUtc);
        Assert.Equal(new SubmitTraining("dataset/42"), handler.Request);
    }

    [Theory]
    [InlineData(HandlerOutcome.Rejected, "rejected")]
    [InlineData(HandlerOutcome.TimedOut, "timed-out")]
    public async Task Execute_DistinguishesSamePayloadOutcomesByCanonicalCase(
        HandlerOutcome selected,
        string expectedOutcome)
    {
        var protocol = CreateProtocol();
        var typed = DurableRequestHandlerAdapter.Create(
            protocol,
            new CapturingHandler(protocol, selected),
            DurableOperationIdempotencyEvidence.TargetDeduplication);
        var raw = new RawAdapter(protocol, selected);
        var invocation = CreateInvocation(protocol, new SubmitTraining("dataset/42"));
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(ObservedAtUtc));

        var typedObservation = await typed.ExecuteAsync(context, invocation);
        var rawObservation = await raw.ExecuteAsync(context, invocation);

        Assert.Equal(rawObservation, typedObservation);
        Assert.Equal(
            expectedOutcome,
            Assert.IsType<DurableOperationOutcomeObservation>(typedObservation).Outcome.Id.Value);
        Assert.Equal(
            typeof(SubmissionFailure),
            protocol.Outcomes.Rejected.PayloadType);
        Assert.Equal(
            protocol.Outcomes.Rejected.PayloadType,
            protocol.Outcomes.TimedOut.PayloadType);
        Assert.NotEqual(protocol.Outcomes.Rejected.Id, protocol.Outcomes.TimedOut.Id);
    }

    [Fact]
    public async Task Execute_MalformedCanonicalPayloadReturnsStructuredPreCallFailure()
    {
        var protocol = CreateProtocol();
        var adapter = DurableRequestHandlerAdapter.Create(
            protocol,
            new CapturingHandler(protocol, HandlerOutcome.Accepted),
            DurableOperationIdempotencyEvidence.TargetDeduplication);
        var malformed = PortableValue.Concrete(
            protocol.InputContract,
            ObservationValue.FromString("not-an-object"));
        var invocation = CreateInvocation(protocol, malformed);

        var observation = await adapter.ExecuteAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(ObservedAtUtc)),
            invocation);

        var failure = Assert.IsType<DurableOperationFailureObservation>(observation).Failure;
        Assert.Equal(DurableOperationFailureCodes.TypedRequestPayloadInvalid, failure.Code);
        Assert.Equal(DurableOperationFailurePhase.PreCall, failure.Phase);
        Assert.Equal(DurableOperationEffectEvidence.NotExecuted, failure.EffectEvidence);
        Assert.Equal(DurableOperationFailureDisposition.Terminal, failure.Disposition);
        Assert.NotNull(failure.Detail);
        Assert.Equal(PortableValueState.Concrete, failure.Detail.State);
    }

    [Fact]
    public async Task Execute_MalformedTypedResultReturnsStructuredAmbiguousFailure()
    {
        var protocol = CreateProtocol();
        var adapter = DurableRequestHandlerAdapter.Create(
            protocol,
            new NullOutcomeHandler(),
            DurableOperationIdempotencyEvidence.TargetDeduplication);

        var observation = await adapter.ExecuteAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(ObservedAtUtc)),
            CreateInvocation(protocol, new SubmitTraining("dataset/42")));

        var failure = Assert.IsType<DurableOperationFailureObservation>(observation).Failure;
        Assert.Equal(DurableOperationFailureCodes.TypedRequestOutcomeInvalid, failure.Code);
        Assert.Equal(DurableOperationFailurePhase.PostCallPreCommit, failure.Phase);
        Assert.Equal(DurableOperationEffectEvidence.Ambiguous, failure.EffectEvidence);
        Assert.Equal(DurableOperationFailureDisposition.Terminal, failure.Disposition);
        Assert.NotNull(failure.Detail);
    }

    [Theory]
    [InlineData(ReconciliationResult.ConfirmedOutcome)]
    [InlineData(ReconciliationResult.ConfirmedNotExecuted)]
    [InlineData(ReconciliationResult.Unresolved)]
    public async Task Reconcile_ProjectsEveryTypedResultIdenticallyToTheRawAdapter(
        ReconciliationResult result)
    {
        var protocol = CreateProtocol();
        var handler = new CapturingHandler(protocol, HandlerOutcome.Accepted, result);
        var typed = DurableRequestHandlerAdapter.CreateWithReconciliation(
            protocol,
            handler,
            handler,
            DurableOperationIdempotencyEvidence.TargetDeduplication);
        var raw = new RawAdapter(protocol, HandlerOutcome.Accepted, result);
        var (executor, state) = CreateReconciliationState(protocol, new SubmitTraining("dataset/42"));
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(ObservedAtUtc));

        var typedObservation = await DurableOperationReferenceExecutor.ReconcileAsync(context, state, typed);
        var rawObservation = await DurableOperationReferenceExecutor.ReconcileAsync(context, state, raw);

        Assert.Equal(rawObservation, typedObservation);
        Assert.Equal(
            DurableOperationReconciliationCapability.Supported,
            typed.Capabilities.Reconciliation);
        Assert.Equal(state.Request.Context.EmissionId, handler.ReconciliationContext?.EmissionId);
        Assert.Equal(state.CurrentAttempt?.Claim.AttemptId, handler.ReconciliationContext?.AttemptId);
        Assert.Equal(state.CurrentAttempt?.Ordinal, handler.ReconciliationContext?.AttemptOrdinal);
        Assert.Equal(state.CurrentAttempt?.Claim.Fence, handler.ReconciliationContext?.Fence);
        Assert.Equal(state.DeduplicationKey, handler.ReconciliationContext?.DeduplicationKey);
        Assert.Equal(state.CurrentAttempt?.Failure, handler.ReconciliationContext?.Failure);
        Assert.Equal(state.Binding.ReconciliationTarget, handler.ReconciliationContext?.Target);
        Assert.Equal(
            new DurableOperationRecoveryIdentity(
                state.OperationId,
                state.CurrentAttempt!.Claim.AttemptId,
                state.CurrentAttempt.Claim.Fence,
                DurableOperationRecoveryRequirement.Reconcile),
            handler.ReconciliationContext?.Identity);
    }

    [Fact]
    public void Registration_DerivesExactCapabilitiesAndBuildsOneCatalogResolver()
    {
        var protocol = CreateProtocol();
        var handler = new CapturingHandler(protocol, HandlerOutcome.Accepted);
        ServiceCollection services = [];
        services.AddSingleton(handler);

        services
            .AddDurableOperation(protocol)
            .HandledBy<CapturingHandler>()
            .WithIdempotency(DurableOperationIdempotencyEvidence.NaturallyIdempotent)
            .WithReconciliation();

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IDurableOperationAdapterResolver>();
        var request = CreateRequest(protocol, new SubmitTraining("dataset/42"));

        Assert.IsType<DurableOperationAdapterCatalog>(resolver);
        Assert.True(resolver.TryResolve(request, out var adapter));
        Assert.Equal(protocol.Request, Assert.Single(adapter!.Capabilities.SupportedRequests));
        Assert.Equal(
            DurableOperationIdempotencyEvidence.NaturallyIdempotent,
            adapter.Capabilities.IdempotencyEvidence);
        Assert.Equal(
            DurableOperationReconciliationCapability.Supported,
            adapter.Capabilities.Reconciliation);
    }

    [Fact]
    public void Registration_RejectsUnspecifiedIdempotencyEvidenceBeforeMutatingServices()
    {
        var protocol = CreateProtocol();
        ServiceCollection services = [];

        Assert.Throws<ArgumentOutOfRangeException>(() => services
            .AddDurableOperation(protocol)
            .HandledBy<ExecuteOnlyHandler>()
            .WithIdempotency(DurableOperationIdempotencyEvidence.Unspecified));

        Assert.Empty(services);
    }

    [Fact]
    public void Registration_RejectsDeclaredReconciliationWhenTheHandlerCannotProvideIt()
    {
        var protocol = CreateProtocol();
        ServiceCollection services = [];

        services
            .AddDurableOperation(protocol)
            .HandledBy<ExecuteOnlyHandler>()
            .WithIdempotency(DurableOperationIdempotencyEvidence.TargetDeduplication)
            .WithReconciliation();

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IDurableOperationAdapterResolver>());

        Assert.Contains(typeof(ExecuteOnlyHandler).ToString(), exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(protocol.Request.Definition.DefinitionId.Value, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(protocol.Request.Definition.Fingerprint.Value, exception.ToString(), StringComparison.Ordinal);
    }

    static RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> CreateProtocol() =>
        InteractionContractAuthoring.CreateRequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases>(
            new("tests/request/typed-durable-training"),
            new("revision/1"),
            new("tests/request/typed-durable-training/v1"),
            outcomes => new(
                outcomes.Result<SubmissionAcceptedCase, SubmissionAccepted>(
                    new("accepted"),
                    new("tests/request/typed-durable-training/accepted/v1")),
                outcomes.Failure<SubmissionRejectedCase, SubmissionFailure>(
                    new("rejected"),
                    new("tests/request/typed-durable-training/failure/v1")),
                outcomes.Timeout<SubmissionTimedOutCase, SubmissionFailure>(
                    new("timed-out"),
                    new("tests/request/typed-durable-training/failure/v1"))),
            new RequestProtocolResponsePolicy(
                RequestOptionalTerminalSemantics.TerminalOutcome,
                RequestOptionalTerminalSemantics.Unsupported,
                RequestResultDisposition.Observe,
                RequestResultDisposition.Reject,
                RequestResultDisposition.ReusePriorDisposition,
                RequestRetrySemantics.ReconcileBeforeRetry,
                RequestResolutionSemantics.Reconcile,
                RequestResolutionSemantics.Reconcile,
                TimeSpan.FromDays(30)),
            new ExecutionProvenance(
                new("cohesive.tests", "1"),
                new("tests/typed-durable-request-handlers"),
                DocumentOrigin.User));

    static DurableRequestBinding CreateBinding(
        RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> protocol) =>
        protocol.BindDurably(
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            timeoutAfter: TimeSpan.FromHours(1),
            reconciliationTarget: new(
                DefinitionReference("process/reconcile"),
                new("node/reconcile")));

    static DurableOperationInvocation CreateInvocation(
        RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> protocol,
        SubmitTraining request) =>
        CreateInvocation(
            protocol,
            PortableValue.Concrete(protocol.InputContract, ObservationValue.FromObject(request)));

    static DurableOperationInvocation CreateInvocation(
        RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> protocol,
        PortableValue payload)
    {
        var request = CreateRequest(protocol, payload);
        return new(
            request,
            CreateBinding(protocol),
            new("attempt/typed-handler/1"),
            attemptOrdinal: 2,
            new(7),
            new(
                request.Context.AuthorityScope,
                request.Contract,
                request.Context.IdempotencyKey),
            ObservedAtUtc.AddHours(1));
    }

    static (DurableOperationReferenceExecutor Executor, DurableOperationState State) CreateReconciliationState(
        RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> protocol,
        SubmitTraining payload)
    {
        var request = CreateRequest(protocol, payload);
        var binding = CreateBinding(protocol);
        var executor = new DurableOperationReferenceExecutor(protocol.Catalog);
        var validation = executor.TryCreate(
            request,
            binding,
            ObservedAtUtc.AddMinutes(-10),
            out var created);
        Assert.True(validation.IsValid, DurableOperationTestFixture.FormatDiagnostics(validation));
        var claimed = executor.Claim(
            Assert.IsType<DurableOperationState>(created),
            new("attempt/typed-handler/failed"),
            claimant: "worker/tests",
            ObservedAtUtc.AddMinutes(-5));
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            ObservedAtUtc.AddMinutes(-4));
        var failed = executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            new DurableOperationFailureObservation(new DurableOperationFailure(
                DurableOperationFailurePhase.InCall,
                DurableOperationEffectEvidence.Ambiguous,
                DurableOperationFailureDisposition.Retryable,
                "provider.response-lost")),
            ObservedAtUtc.AddMinutes(-1));
        Assert.Equal(DurableOperationRecoveryRequirement.Reconcile, failed.State.RecoveryRequirement);
        return (executor, failed.State);
    }

    static RequestEnvelope CreateRequest(
        RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> protocol,
        SubmitTraining payload) =>
        CreateRequest(
            protocol,
            PortableValue.Concrete(protocol.InputContract, ObservationValue.FromObject(payload)));

    static RequestEnvelope CreateRequest(
        RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> protocol,
        PortableValue payload) => new(
        InteractionEnvelope.CurrentSchemaVersion,
        new InteractionEnvelopeContext(
            new("emission/typed-handler/1"),
            new ProcessInteractionOrigin(
                DefinitionReference("process/training"),
                new("node/submission"),
                new(new("process/training/1"), new("attempt/process/1")),
                new("activation/submission"),
                new("token/submission")),
            new("correlation/training/1"),
            causationId: null,
            new("authority/training", "tenant/acme"),
            new("idempotency/submission/1"),
            ordering: null,
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
            new ExecutionProvenance(
                new("cohesive.tests", "1"),
                new("tests/typed-durable-request-handlers"),
                DocumentOrigin.User)),
        protocol.Request,
        payload,
        new ProcessTokenInteractionTarget(
            new(new("process/training/1"), new("attempt/process/1")),
            new("token/submission")));

    static ExecutionDefinitionReference DefinitionReference(string id) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('a', 64)));

    static RequestTerminalOutcome RawOutcome(
        RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> protocol,
        HandlerOutcome selected)
    {
        var failure = new SubmissionFailure("provider unavailable");
        return selected switch
        {
            HandlerOutcome.Accepted => new RequestResultOutcome(
                protocol.Outcomes.Accepted.Id,
                PortableValue.Concrete(
                    protocol.Outcomes.Accepted.Outcome.Schema.Contract,
                    ObservationValue.FromObject(new SubmissionAccepted("submission/7")))),
            HandlerOutcome.Rejected => new RequestFailureOutcome(
                protocol.Outcomes.Rejected.Id,
                PortableValue.Concrete(
                    protocol.Outcomes.Rejected.Outcome.Schema.Contract,
                    ObservationValue.FromObject(failure))),
            HandlerOutcome.TimedOut => new RequestTimeoutOutcome(
                protocol.Outcomes.TimedOut.Id,
                PortableValue.Concrete(
                    protocol.Outcomes.TimedOut.Outcome.Schema.Contract,
                    ObservationValue.FromObject(failure))),
            _ => throw new ArgumentOutOfRangeException(nameof(selected), selected, "Unknown handler outcome.")
        };
    }

    sealed class CapturingHandler(
        RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> protocol,
        HandlerOutcome selected,
        ReconciliationResult reconciliation = ReconciliationResult.ConfirmedOutcome)
        : IDurableRequestHandler<SubmitTraining, SubmissionOutcome>,
            IDurableRequestReconciliationHandler<SubmitTraining, SubmissionOutcome>
    {
        internal DurableRequestExecutionContext<SubmissionOutcome>? ExecutionContext { get; private set; }

        internal DurableRequestReconciliationContext<SubmissionOutcome>? ReconciliationContext { get; private set; }

        internal SubmitTraining? Request { get; private set; }

        public ValueTask<DurableRequestOutcome<SubmissionOutcome>> ExecuteAsync(
            DurableRequestExecutionContext<SubmissionOutcome> context,
            SubmitTraining request)
        {
            ExecutionContext = context;
            Request = request;
            var failure = new SubmissionFailure("provider unavailable");
            return ValueTask.FromResult(selected switch
            {
                HandlerOutcome.Accepted => context.Outcome(
                    protocol.Outcomes.Accepted,
                    new SubmissionAccepted("submission/7")),
                HandlerOutcome.Rejected => context.Outcome(protocol.Outcomes.Rejected, failure),
                HandlerOutcome.TimedOut => context.Outcome(protocol.Outcomes.TimedOut, failure),
                _ => throw new ArgumentOutOfRangeException(nameof(selected), selected, "Unknown handler outcome.")
            });
        }

        public ValueTask<DurableRequestReconciliationResult<SubmissionOutcome>> ReconcileAsync(
            DurableRequestReconciliationContext<SubmissionOutcome> context,
            SubmitTraining request)
        {
            ReconciliationContext = context;
            Request = request;
            return ValueTask.FromResult(reconciliation switch
            {
                ReconciliationResult.ConfirmedOutcome => context.Confirmed(
                    protocol.Outcomes.Accepted,
                    new SubmissionAccepted("submission/7")),
                ReconciliationResult.ConfirmedNotExecuted => context.ConfirmedNotExecuted(),
                ReconciliationResult.Unresolved => context.Unresolved(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(reconciliation),
                    reconciliation,
                    "Unknown reconciliation result.")
            });
        }
    }

    sealed class ExecuteOnlyHandler : IDurableRequestHandler<SubmitTraining, SubmissionOutcome>
    {
        public ValueTask<DurableRequestOutcome<SubmissionOutcome>> ExecuteAsync(
            DurableRequestExecutionContext<SubmissionOutcome> context,
            SubmitTraining request) =>
            throw new InvalidOperationException("Registration validation must run before handler execution.");
    }

    sealed class NullOutcomeHandler : IDurableRequestHandler<SubmitTraining, SubmissionOutcome>
    {
        public ValueTask<DurableRequestOutcome<SubmissionOutcome>> ExecuteAsync(
            DurableRequestExecutionContext<SubmissionOutcome> context,
            SubmitTraining request) =>
            ValueTask.FromResult<DurableRequestOutcome<SubmissionOutcome>>(null!);
    }

    sealed class RawAdapter(
        RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCases> protocol,
        HandlerOutcome selected,
        ReconciliationResult reconciliation = ReconciliationResult.ConfirmedOutcome)
        : IDurableOperationAdapter
    {
        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [protocol.Request]);

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation) =>
            ValueTask.FromResult<DurableOperationAttemptObservation>(
                new DurableOperationOutcomeObservation(RawOutcome(protocol, selected)));

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            ValueTask.FromResult<DurableOperationReconciliationObservation>(reconciliation switch
            {
                ReconciliationResult.ConfirmedOutcome => new DurableOperationReconciledOutcome(
                    RawOutcome(protocol, selected)),
                ReconciliationResult.ConfirmedNotExecuted => new DurableOperationConfirmedNotExecuted(),
                ReconciliationResult.Unresolved => new DurableOperationUnresolved(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(reconciliation),
                    reconciliation,
                    "Unknown reconciliation result.")
            });
    }

    public enum HandlerOutcome
    {
        Accepted,
        Rejected,
        TimedOut
    }

    public enum ReconciliationResult
    {
        ConfirmedOutcome,
        ConfirmedNotExecuted,
        Unresolved
    }

    sealed record SubmitTraining(string DatasetId);

    sealed record SubmissionAccepted(string SubmissionId);

    sealed record SubmissionFailure(string Reason);

    abstract record SubmissionOutcome;

    sealed record SubmissionAcceptedCase(SubmissionAccepted Payload) : SubmissionOutcome;

    sealed record SubmissionRejectedCase(SubmissionFailure Payload) : SubmissionOutcome;

    sealed record SubmissionTimedOutCase(SubmissionFailure Payload) : SubmissionOutcome;

    sealed record SubmissionCases(
        RequestProtocolCase<SubmissionAcceptedCase, SubmissionAccepted> Accepted,
        RequestProtocolCase<SubmissionRejectedCase, SubmissionFailure> Rejected,
        RequestProtocolCase<SubmissionTimedOutCase, SubmissionFailure> TimedOut);

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
