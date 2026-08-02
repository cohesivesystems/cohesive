using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.MotionDq;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class MotionDqMonitoringDurableConformanceTests
{
    static readonly InteractionAuthorityScope Authority = new(
        authority: "authority/motion-dq",
        tenant: "tenant/test");
    static readonly InteractionDeliveryRequirements DurableDelivery = new(
        InteractionDurabilityDemand.Durable,
        InteractionVisibilityDemand.AfterOriginCommit);

    [Fact]
    public async Task FullMonitoringTimeline_RestoresRecursAndCreatesEachHumanWorkItemOnce()
    {
        var fixture = MotionDqMonitoringProcess.Version1;
        var startedAtUtc = new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero);
        MotionDqInterventionKind[] interventions =
        [
            MotionDqInterventionKind.Coaching,
            MotionDqInterventionKind.PostTrainingInspection,
            MotionDqInterventionKind.RideAlong
        ];
        var observations = interventions
            .Select((intervention, index) => Observation(
                startedAtUtc,
                evidenceRevision: index + 1,
                MotionDqMonitoringDisposition.Continue,
                intervention))
            .Append(Observation(
                startedAtUtc,
                evidenceRevision: interventions.Length + 1,
                MotionDqMonitoringDisposition.Cleared,
                MotionDqInterventionKind.RideAlong))
            .ToImmutableArray();
        var host = new MonitoringHost(fixture, observations);
        var adapter = new MonitoringWorkAdapter(fixture);
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, host, adapter);
        var clock = new ScenarioClock(startedAtUtc);
        var initialized = await runtime.InitializeAsync(
            Context(clock.Next()),
            fixture.Plan,
            Start(fixture, clock.Next(), clock.Next()));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;

        var firstActivation = Activation(
            fixture,
            id: "activation/motion-dq/monitoring/start",
            cause: ProcessActivationCause.Start,
            observedAtUtc: clock.Next());
        var first = await ActivateAsync(runtime, fixture, checkpoint, firstActivation, clock.Peek);
        checkpoint = first.Checkpoint;
        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Decision.Disposition);
        Assert.Single(host.Evaluations);

        var replay = await runtime.ActivateAsync(
            Context(clock.Peek),
            fixture.Plan,
            checkpoint.ContinuationIdentity,
            firstActivation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Single(host.Evaluations);
        Assert.Single(checkpoint.DurableOperations, static operation => operation.Status == DurableOperationStatus.Pending);

        for (var index = 0; index < interventions.Length; index++)
        {
            var operation = Assert.Single(
                checkpoint.DurableOperations,
                static candidate => candidate.Status == DurableOperationStatus.Pending);
            checkpoint = await AdvanceOperationAsync(runtime, fixture, checkpoint, operation.OperationId, clock.Next());
            checkpoint = (await ActivateAsync(
                runtime,
                fixture,
                checkpoint,
                Activation(
                    fixture,
                    id: $"activation/motion-dq/monitoring/work-created/{index + 1}",
                    cause: ProcessActivationCause.Interaction,
                    observedAtUtc: clock.Next(),
                    inputs: PendingInputs(checkpoint)),
                clock.Peek)).Checkpoint;

            var wait = Assert.Single(
                checkpoint.Continuation.Waits,
                static candidate => candidate.Active
                    && candidate.Node.Value == "motion-dq/monitoring/await-intervention");
            var target = new ProcessTokenInteractionTarget(
                checkpoint.ContinuationIdentity,
                wait.Token,
                wait.RegistrationId);
            var completion = CompletionSignal(
                fixture,
                target,
                evidenceRevision: index + 1,
                suffix: $"timeline-{index + 1}");
            var nextOccurrenceAlreadyStarted = false;

            if (index == 1)
            {
                var dueAtUtc = observations[index].Work.NextEvaluationDueAtUtc;
                checkpoint = (await ActivateAsync(
                    runtime,
                    fixture,
                    checkpoint,
                    Activation(
                        fixture,
                        id: "activation/motion-dq/monitoring/evaluation-timer",
                        cause: ProcessActivationCause.Timer,
                        observedAtUtc: dueAtUtc),
                    dueAtUtc)).Checkpoint;
                Assert.Single(
                    checkpoint.Continuation.Waits,
                    static candidate => candidate.Active
                        && candidate.Kind == ProcessWaitKind.RepeatAcrossActivation);
                clock.AdvanceTo(dueAtUtc.AddMinutes(1));

                var json = ProcessDurableCheckpointJsonSerializer.Serialize(checkpoint);
                var validation = ProcessDurableCheckpointJsonSerializer.TryDeserialize(
                    json,
                    fixture.Plan,
                    out var restored);
                Assert.True(validation.IsValid, Format(validation));
                var restoredCheckpoint = Assert.IsType<ProcessDurableCheckpoint>(restored);
                store = new InMemoryProcessDurableStore();
                var restoredStore = await store.InitializeAsync(
                    Context(clock.Next()),
                    new("commit/motion-dq/monitoring/restore"),
                    restoredCheckpoint);
                Assert.Equal(ProcessStoreMutationDisposition.Applied, restoredStore.Disposition);
                checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(restoredStore.Snapshot).Checkpoint;
                runtime = Runtime(store, fixture, host, adapter);

                var lateInput = new ProcessActivationInput(target, completion);
                checkpoint = await AdmitAsync(store, checkpoint, lateInput, dueAtUtc.AddMinutes(1));
                var late = await ActivateAsync(
                    runtime,
                    fixture,
                    checkpoint,
                    Activation(
                        fixture,
                        id: "activation/motion-dq/monitoring/late-completion",
                        cause: ProcessActivationCause.Interaction,
                        observedAtUtc: dueAtUtc.AddMinutes(1),
                        inputs: [lateInput]),
                    dueAtUtc.AddMinutes(1));
                checkpoint = late.Checkpoint;
                var receipt = Assert.Single(late.Decision.InputAdmissions);
                Assert.Equal(ProcessInputAdmissionDisposition.Observed, receipt.Disposition);
                Assert.Equal(ProcessInputAdmissionReason.Late, receipt.Reason);
                nextOccurrenceAlreadyStarted = true;
            }
            else
            {
                var input = new ProcessActivationInput(target, completion);
                checkpoint = await AdmitAsync(store, checkpoint, input, clock.Next());
                checkpoint = (await ActivateAsync(
                    runtime,
                    fixture,
                    checkpoint,
                    Activation(
                        fixture,
                        id: $"activation/motion-dq/monitoring/work-completed/{index + 1}",
                        cause: ProcessActivationCause.Interaction,
                        observedAtUtc: clock.Next(),
                        inputs: [input]),
                    clock.Peek)).Checkpoint;
            }

            if (!nextOccurrenceAlreadyStarted)
            {
                Assert.Single(
                    checkpoint.Continuation.Waits,
                    static candidate => candidate.Active
                        && candidate.Kind == ProcessWaitKind.RepeatAcrossActivation);
                var continued = await ActivateAsync(
                    runtime,
                    fixture,
                    checkpoint,
                    Activation(
                        fixture,
                        id: $"activation/motion-dq/monitoring/continue/{index + 1}",
                        cause: ProcessActivationCause.Continue,
                        observedAtUtc: clock.Next()),
                    clock.Peek);
                checkpoint = continued.Checkpoint;
            }
        }

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(
            MotionDqMonitoringOutcome.Cleared.ToString(),
            checkpoint.Continuation.Terminal.Detail?.Value?.Value?.GetRequiredString());
        Assert.Equal(observations.Length, host.Evaluations.Count);
        Assert.Equal(interventions.Length, adapter.Invocations.Count);
        Assert.Equal(
            interventions,
            adapter.Invocations.Select(static invocation => Intervention(invocation.Request)).ToArray());
        Assert.Equal(
            adapter.Invocations.Count,
            adapter.Invocations.Select(static invocation => invocation.Request.Context.EmissionId).Distinct().Count());
        Assert.Empty(checkpoint.Continuation.OutstandingRequests);
        Assert.Empty(PendingInputs(checkpoint));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAndSupersession_EndTheActiveWaitWithoutAnotherQuery(bool cancel)
    {
        var fixture = MotionDqMonitoringProcess.Version1;
        var startedAtUtc = new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.Zero);
        var host = new MonitoringHost(
            fixture,
            [Observation(
                startedAtUtc,
                evidenceRevision: 1,
                MotionDqMonitoringDisposition.Continue,
                MotionDqInterventionKind.Coaching)]);
        var adapter = new MonitoringWorkAdapter(fixture);
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, host, adapter);
        var clock = new ScenarioClock(startedAtUtc);
        var initialized = await runtime.InitializeAsync(
            Context(clock.Next()),
            fixture.Plan,
            Start(fixture, clock.Next(), clock.Next(), suffix: cancel ? "cancel" : "supersede"));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        checkpoint = (await ActivateAsync(
            runtime,
            fixture,
            checkpoint,
            Activation(
                fixture,
                id: $"activation/motion-dq/monitoring/{(cancel ? "cancel" : "supersede")}/start",
                cause: ProcessActivationCause.Start,
                observedAtUtc: clock.Next()),
            clock.Peek)).Checkpoint;
        var operation = Assert.Single(checkpoint.DurableOperations);
        checkpoint = await AdvanceOperationAsync(runtime, fixture, checkpoint, operation.OperationId, clock.Next());
        checkpoint = (await ActivateAsync(
            runtime,
            fixture,
            checkpoint,
            Activation(
                fixture,
                id: $"activation/motion-dq/monitoring/{(cancel ? "cancel" : "supersede")}/work-created",
                cause: ProcessActivationCause.Interaction,
                observedAtUtc: clock.Next(),
                inputs: PendingInputs(checkpoint)),
            clock.Peek)).Checkpoint;
        var wait = Assert.Single(checkpoint.Continuation.Waits, static candidate => candidate.Active);
        var target = new ProcessTokenInteractionTarget(
            checkpoint.ContinuationIdentity,
            wait.Token,
            wait.RegistrationId);
        var signal = cancel
            ? CancellationSignal(fixture, target)
            : SupersessionSignal(fixture, target);
        var input = new ProcessActivationInput(target, signal);
        checkpoint = await AdmitAsync(store, checkpoint, input, clock.Next());
        checkpoint = (await ActivateAsync(
            runtime,
            fixture,
            checkpoint,
            Activation(
                fixture,
                id: $"activation/motion-dq/monitoring/{(cancel ? "cancel" : "supersede")}/signal",
                cause: ProcessActivationCause.Interaction,
                observedAtUtc: clock.Next(),
                inputs: [input]),
            clock.Peek)).Checkpoint;

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(
            (cancel ? MotionDqMonitoringOutcome.Cancelled : MotionDqMonitoringOutcome.Superseded).ToString(),
            checkpoint.Continuation.Terminal.Detail?.Value?.Value?.GetRequiredString());
        Assert.Single(host.Evaluations);
        Assert.Single(adapter.Invocations);
    }

    static MotionDqMonitoringObservation Observation(
        DateTimeOffset startedAtUtc,
        long evidenceRevision,
        MotionDqMonitoringDisposition disposition,
        MotionDqInterventionKind intervention) => new(
        Disposition: disposition,
        Work: new(
            CaseId: "case/motion-dq/monitoring/1",
            EvidenceRevision: evidenceRevision,
            EvidenceSnapshotId: $"evidence-snapshot/{evidenceRevision}",
            LatestTelematicsEventId: $"telematics-event/{evidenceRevision}",
            Intervention: intervention,
            Window: new(
                StartsAtUtc: startedAtUtc.AddDays(evidenceRevision - 1),
                EndsAtUtc: startedAtUtc.AddDays(evidenceRevision)),
            NextEvaluationDueAtUtc: startedAtUtc.AddDays(evidenceRevision)));

    static ProcessStartReceipt Start(
        MotionDqMonitoringProcess fixture,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset acceptedAtUtc,
        string suffix = "timeline")
    {
        var continuation = new ProcessContinuationIdentity(
            new($"process-instance/motion-dq/monitoring/{suffix}"),
            new("process-attempt/1"));
        return new(
            new(
                ProcessStartRequest.CurrentSchemaVersion,
                fixture.Reference,
                new(
                    new($"start-command/motion-dq/monitoring/{suffix}"),
                    new($"start-idempotency/motion-dq/monitoring/{suffix}"),
                    continuation.ProcessInstanceId,
                    new("operator/tests", Authority, "policy/tests/allow"),
                    issuedAtUtc,
                    fixture.Document.Metadata.Provenance),
                continuation,
                PortableValue.Concrete(
                    fixture.Definition.Input,
                    ObservationValue.FromObject(new MotionDqMonitoringCaseReference(
                        CaseId: "case/motion-dq/monitoring/1")))),
            acceptedAtUtc);
    }

    static ProcessActivation Activation(
        MotionDqMonitoringProcess fixture,
        string id,
        ProcessActivationCause cause,
        DateTimeOffset observedAtUtc,
        ImmutableArray<ProcessActivationInput> inputs = default) => new(
        new(id),
        cause,
        observedAtUtc,
        new(
            Authority,
            new("correlation/motion-dq/monitoring"),
            DurableDelivery,
            fixture.Document.Metadata.Provenance),
        inputs);

    static async Task<ActivationResult> ActivateAsync(
        ProcessDurableRuntime runtime,
        MotionDqMonitoringProcess fixture,
        ProcessDurableCheckpoint checkpoint,
        ProcessActivation activation,
        DateTimeOffset observedAtUtc)
    {
        var result = await runtime.ActivateAsync(
            Context(observedAtUtc),
            fixture.Plan,
            checkpoint.ContinuationIdentity,
            activation);
        return new(
            Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint,
            Assert.IsType<ProcessActivationDecision>(result.Decision));
    }

    static async Task<ProcessDurableCheckpoint> AdvanceOperationAsync(
        ProcessDurableRuntime runtime,
        MotionDqMonitoringProcess fixture,
        ProcessDurableCheckpoint checkpoint,
        EmissionId operationId,
        DateTimeOffset observedAtUtc)
    {
        var result = await runtime.AdvanceOperationAsync(
            Context(observedAtUtc),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            operationId);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        return Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint;
    }

    static async Task<ProcessDurableCheckpoint> AdmitAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableCheckpoint checkpoint,
        ProcessActivationInput input,
        DateTimeOffset observedAtUtc)
    {
        var result = await store.AdmitInputAsync(
            Context(observedAtUtc),
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            input,
            observedAtUtc);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, result.Disposition);
        return Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint;
    }

    static ImmutableArray<ProcessActivationInput> PendingInputs(ProcessDurableCheckpoint checkpoint) =>
        [.. checkpoint.Inbox
            .Where(static entry => entry.Receipt is null)
            .Select(static entry => entry.Input)
            .OrderBy(static input => input.Envelope.Context.EmissionId.Value, StringComparer.Ordinal)];

    static SignalEnvelope CompletionSignal(
        MotionDqMonitoringProcess fixture,
        ProcessTokenInteractionTarget target,
        long evidenceRevision,
        string suffix) => Signal(
        fixture,
        target,
        fixture.Interactions.InterventionCompletedSignal,
        new MotionDqInterventionCompleted(
            CaseId: "case/motion-dq/monitoring/1",
            WorkItemId: $"work-item/{evidenceRevision}",
            CompletionEvidenceId: $"completion-evidence/{evidenceRevision}",
            CompletedAtUtc: new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)
                .AddDays(evidenceRevision)),
        suffix);

    static SignalEnvelope CancellationSignal(
        MotionDqMonitoringProcess fixture,
        ProcessTokenInteractionTarget target) => Signal(
        fixture,
        target,
        fixture.Interactions.CaseCancellationSignal,
        new MotionDqCancellation(
            CancellationId: "cancellation/monitoring/1",
            CaseId: "case/motion-dq/monitoring/1",
            ReasonCode: "operator-cancelled"),
        "cancelled");

    static SignalEnvelope SupersessionSignal(
        MotionDqMonitoringProcess fixture,
        ProcessTokenInteractionTarget target) => Signal(
        fixture,
        target,
        fixture.Interactions.CaseSupersessionSignal,
        new MotionDqMonitoringSupersession(
            CaseId: "case/motion-dq/monitoring/1",
            SupersedingCaseId: "case/motion-dq/monitoring/2",
            EvidenceId: "supersession-evidence/1"),
        "superseded");

    static SignalEnvelope Signal(
        MotionDqMonitoringProcess fixture,
        ProcessTokenInteractionTarget target,
        SignalContractReference signal,
        object payload,
        string suffix)
    {
        Assert.True(fixture.Interactions.Catalog.TryResolve(signal, out var resolved));
        var contract = Assert.IsType<SignalContractDefinition>(resolved).Payload.Contract;
        return new(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new($"emission/motion-dq/monitoring/{suffix}"),
                new ProcessInteractionOrigin(
                    fixture.Reference,
                    new("source/motion-dq/monitoring"),
                    target.Continuation,
                    new($"activation/motion-dq/monitoring/source/{suffix}"),
                    target.Token),
                new("correlation/motion-dq/monitoring"),
                causationId: null,
                Authority,
                new($"idempotency/motion-dq/monitoring/{suffix}"),
                ordering: null,
                DurableDelivery,
                fixture.Document.Metadata.Provenance),
            signal,
            PortableValue.Concrete(contract, ObservationValue.FromObject(payload)),
            target);
    }

    static ProcessDurableRuntime Runtime(
        IProcessDurableStore store,
        MotionDqMonitoringProcess fixture,
        MonitoringHost host,
        MonitoringWorkAdapter adapter) => new(
        store,
        host,
        new("worker/motion-dq-monitoring-conformance", TimeSpan.FromMinutes(5)),
        bindingResolver: new BindingResolver(fixture.ScheduleInterventionBinding),
        operationAdapterResolver: new AdapterResolver(adapter));

    static MotionDqInterventionKind Intervention(RequestEnvelope request)
    {
        var payload = Assert.IsType<ObservationValue>(request.Payload.Value);
        Assert.True(payload.TryGetProperty(nameof(MotionDqInterventionWorkRequest.Intervention), out var value));
        return Enum.Parse<MotionDqInterventionKind>(value.GetRequiredString());
    }

    static OperationContext Context(DateTimeOffset utcNow) =>
        OperationContext.Create(timeProvider: new FixedTimeProvider(utcNow));

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed record ActivationResult(
        ProcessDurableCheckpoint Checkpoint,
        ProcessActivationDecision Decision);

    sealed class MonitoringHost(
        MotionDqMonitoringProcess fixture,
        ImmutableArray<MotionDqMonitoringObservation> observations) : IProcessReferenceHost
    {
        internal List<ProcessRelationEvaluation> Evaluations { get; } = [];

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            var index = Evaluations.Count;
            Evaluations.Add(evaluation);
            if ((uint)index >= (uint)observations.Length)
            {
                throw new InvalidOperationException($"No monitoring observation exists for evaluation {index}.");
            }

            return ProcessOperationResult.Completed(PortableValue.Concrete(
                fixture.ObservationQuery.Result,
                ObservationValue.FromObject(observations[index])));
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal target resolution at '{resolution.Node.Value}'.");
    }

    sealed class BindingResolver(DurableRequestBinding binding) : IProcessDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved)
        {
            resolved = request.Contract == binding.Request ? binding : null;
            return resolved is not null;
        }
    }

    sealed class AdapterResolver(MonitoringWorkAdapter adapter) : IProcessDurableOperationAdapterResolver
    {
        public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? resolved)
        {
            resolved = adapter.Capabilities.SupportedRequests.Contains(request.Contract) ? adapter : null;
            return resolved is not null;
        }
    }

    sealed class MonitoringWorkAdapter : IDurableOperationAdapter
    {
        readonly MotionDqMonitoringProcess fixture;

        internal MonitoringWorkAdapter(MotionDqMonitoringProcess fixture)
        {
            this.fixture = fixture;
            Capabilities = new(
                DurableOperationIdempotencyEvidence.TargetDeduplication,
                DurableOperationReconciliationCapability.Supported,
                [fixture.Interactions.ScheduleInterventionRequest]);
        }

        public DurableOperationAdapterCapabilities Capabilities { get; }

        internal List<DurableOperationInvocation> Invocations { get; } = [];

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            context.ThrowIfCancellationRequested();
            Invocations.Add(invocation);
            var request = invocation.Request;
            var payload = Assert.IsType<ObservationValue>(request.Payload.Value);
            Assert.True(payload.TryGetProperty(
                nameof(MotionDqInterventionWorkRequest.EvidenceRevision),
                out var evidenceRevision));
            Assert.True(fixture.Interactions.Catalog.TryResolve(request.Contract, out var resolved));
            var definition = Assert.IsType<RequestContractDefinition>(resolved);
            var outcome = Assert.IsType<RequestResultDefinition>(definition.Response.Find(
                MotionDqMonitoringInteractionContracts.InterventionScheduledOutcome));
            RequestTerminalOutcome result = new RequestResultOutcome(
                MotionDqMonitoringInteractionContracts.InterventionScheduledOutcome,
                PortableValue.Concrete(
                    outcome.Schema.Contract,
                    ObservationValue.FromObject(new MotionDqInterventionWorkReference(
                        WorkItemId: $"work-item/{evidenceRevision.GetInt64()}"))));
            return ValueTask.FromResult<DurableOperationAttemptObservation>(
                new DurableOperationOutcomeObservation(result));
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            throw new InvalidOperationException("The deterministic monitoring scenario never reconciles an operation.");
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class ScenarioClock(DateTimeOffset initialValue)
    {
        DateTimeOffset value = initialValue;

        internal DateTimeOffset Peek => value;

        internal DateTimeOffset Next()
        {
            var result = value;
            value = value.AddMinutes(1);
            return result;
        }

        internal void AdvanceTo(DateTimeOffset next)
        {
            if (next > value)
            {
                value = next;
            }
        }
    }
}
