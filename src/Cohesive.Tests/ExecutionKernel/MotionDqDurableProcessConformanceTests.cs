using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.MotionDq;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class MotionDqDurableProcessConformanceTests
{
    static readonly InteractionAuthorityScope Authority = new(
        authority: "authority/motion-dq",
        tenant: "tenant/test");
    static readonly InteractionDeliveryRequirements DurableDelivery = new(
        InteractionDurabilityDemand.Durable,
        InteractionVisibilityDemand.AfterOriginCommit);

    [Fact]
    public async Task HappyPath_RestoresInsidePostTermsFork_AndRemainsReferenceEquivalent()
    {
        var fixture = MotionDqProcess.Version1;
        var restoredPlan = CanonicalExecutionDocumentRestoration.RestoreProcessPlan(fixture.Documents);
        var clock = new ScenarioClock(new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        var input = Input(clock.Peek.AddDays(1));
        var start = Start(fixture, input, clock.Next(), clock.Next());
        var durableHost = new StatefulTransitionHost(fixture, input);
        var adapter = new MotionDqScenarioAdapter(fixture);
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, durableHost, adapter);
        var initialized = await runtime.InitializeAsync(Context(clock.Next()), restoredPlan, start);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        checkpoint = await ReachPostTermsVendorFanOutAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "happy",
            plan: restoredPlan);
        var vendorOperations = PendingVendorOperations(checkpoint);
        foreach (var operation in vendorOperations[..3])
        {
            checkpoint = await AdvanceOperationAsync(
                runtime,
                fixture,
                checkpoint,
                operation.OperationId,
                clock.Next(),
                restoredPlan);
        }

        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new ActivationId("activation/motion-dq/post-terms-partial"),
            ProcessActivationCause.Interaction,
            clock.Next(),
            PendingInputs(checkpoint),
            restoredPlan);
        Assert.Equal(4, PendingVendorOperations(checkpoint).Length);
        Assert.Equal(3, checkpoint.Continuation.Forks
            .Single(static fork => fork.Fork.Value == "motion-dq/post-terms/fork")
            .Branches.Count(static branch => branch.Disposition == ExecutionTokenDisposition.Completed));

        var historicalOperationKeys = checkpoint.Operations
            .Select(static receipt => OperationKey(receipt.Key))
            .ToHashSet(StringComparer.Ordinal);
        var hostCallsBeforeRestore = durableHost.InvocationKeys.Count;
        var adapterCallsBeforeRestore = adapter.Invocations.Count;
        var operationReceiptsBeforeRestore = checkpoint.Operations.Length;
        var checkpointJson = ProcessDurableCheckpointJsonSerializer.Serialize(checkpoint);
        var restoreValidation = ProcessDurableCheckpointJsonSerializer.TryDeserialize(
            checkpointJson,
            restoredPlan,
            out var restored);
        Assert.True(restoreValidation.IsValid, Format(restoreValidation));
        var restoredCheckpoint = Assert.IsType<ProcessDurableCheckpoint>(restored);
        Assert.Equivalent(checkpoint.Continuation, restoredCheckpoint.Continuation, strict: true);

        store = new InMemoryProcessDurableStore();
        var restoredStore = await store.InitializeAsync(
            Context(clock.Next()),
            new("commit/motion-dq/restore-mid-fork"),
            restoredCheckpoint);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, restoredStore.Disposition);
        checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(restoredStore.Snapshot).Checkpoint;
        runtime = Runtime(store, fixture, durableHost, adapter);
        foreach (var operation in PendingVendorOperations(checkpoint))
        {
            checkpoint = await AdvanceOperationAsync(
                runtime,
                fixture,
                checkpoint,
                operation.OperationId,
                clock.Next(),
                restoredPlan);
        }

        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new ActivationId("activation/motion-dq/post-terms-complete"),
            ProcessActivationCause.Interaction,
            clock.Next(),
            PendingInputs(checkpoint),
            restoredPlan);

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(
            MotionDqOnboardingOutcome.Completed.ToString(),
            checkpoint.Continuation.Terminal.Detail?.Value?.Value?.GetRequiredString());
        Assert.Empty(checkpoint.Continuation.OutstandingRequests);
        Assert.Empty(PendingInputs(checkpoint));
        Assert.Equal(9, adapter.Invocations.Count);
        Assert.Equal(9, adapter.Invocations.Select(static invocation => invocation.Request.Context.EmissionId).Distinct().Count());
        Assert.Equal(
            7,
            adapter.Invocations.Count(static invocation =>
                invocation.Request.Context.Origin.Node.Value.EndsWith("/vendor", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            adapter.Invocations,
            static invocation => invocation.Request.Context.Origin.Node.Value.EndsWith("/manual", StringComparison.Ordinal));
        Assert.Equal(5, adapterCallsBeforeRestore);

        var postRestoreHostCalls = durableHost.InvocationKeys.Skip(hostCallsBeforeRestore).ToArray();
        Assert.DoesNotContain(postRestoreHostCalls, historicalOperationKeys.Contains);
        Assert.Equal(
            checkpoint.Operations.Length - operationReceiptsBeforeRestore,
            postRestoreHostCalls.Length);
        Assert.Equal(
            durableHost.InvocationKeys.Count,
            durableHost.InvocationKeys.Distinct(StringComparer.Ordinal).Count());
        AssertAuthoritativeCompletedState(durableHost, input);
    }

    [Fact]
    public async Task PostTermsCompletionOrder_IsSemanticallyUnobservableAndReplayStable()
    {
        var ascending = await CompletePostTermsInOrderAsync(descending: false);
        var descending = await CompletePostTermsInOrderAsync(descending: true);

        Assert.Equivalent(
            ascending.Checkpoint.Continuation,
            descending.Checkpoint.Continuation,
            strict: true);
        Assert.Equal(
            ascending.Checkpoint.Operations.Select(static receipt => OperationKey(receipt.Key)),
            descending.Checkpoint.Operations.Select(static receipt => OperationKey(receipt.Key)));
        Assert.Equivalent(
            ascending.Checkpoint.Operations.Select(static receipt => receipt.Result).ToArray(),
            descending.Checkpoint.Operations.Select(static receipt => receipt.Result).ToArray(),
            strict: true);
        Assert.Equivalent(
            ascending.Checkpoint.Emissions.Select(static emission => emission.Envelope).ToArray(),
            descending.Checkpoint.Emissions.Select(static emission => emission.Envelope).ToArray(),
            strict: true);
        Assert.Equal(
            ascending.Checkpoint.DurableOperations.Select(static operation => operation.OperationId),
            descending.Checkpoint.DurableOperations.Select(static operation => operation.OperationId));
        ascending.Host.AssertAuthoritativeStateEquals(descending.Host);
    }

    [Fact]
    public async Task HoldCycle_RestoresFreshWait_ThenHigherPriorityHireBeatsDueTimer()
    {
        var fixture = MotionDqProcess.Version1;
        var clock = new ScenarioClock(new(2026, 8, 2, 8, 0, 0, TimeSpan.Zero));
        var input = Input(clock.Peek.AddDays(1));
        var durableHost = new StatefulTransitionHost(fixture, input);
        var adapter = new MotionDqScenarioAdapter(fixture);
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, durableHost, adapter);
        var initialized = await runtime.InitializeAsync(
            Context(clock.Next()),
            fixture.Plan,
            Start(fixture, input, clock.Next(), clock.Next()));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        checkpoint = await ReachReviewWaitAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "hold-cycle");

        var initialWait = Assert.Single(
            checkpoint.Continuation.Waits,
            static wait => wait.Active && wait.Node.Value == "motion-dq/review/await-match");
        var initialTarget = new ProcessTokenInteractionTarget(
            checkpoint.ContinuationIdentity,
            initialWait.Token,
            initialWait.RegistrationId);
        var holdInput = new ProcessActivationInput(
            initialTarget,
            ReviewDecisionSignal(
                fixture,
                initialTarget,
                MotionDqReviewDecisionKind.Hold));
        var holdAdmission = await store.AdmitInputAsync(
            Context(clock.Next()),
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            holdInput,
            clock.Peek);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, holdAdmission.Disposition);
        checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(holdAdmission.Snapshot).Checkpoint;
        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new("activation/motion-dq/hold-cycle/hold"),
            ProcessActivationCause.Interaction,
            clock.Next(),
            [holdInput]);

        Assert.Equal(
            MotionDqCaseMilestone.Held,
            durableHost.CaseMilestone(input.Prequalification.CaseId));
        var consumedInitialWait = Assert.Single(
            checkpoint.Continuation.Waits,
            wait => wait.RegistrationId == initialWait.RegistrationId);
        Assert.False(consumedInitialWait.Active);
        Assert.Equal("motion-dq/review/hold", consumedInitialWait.WinnerClause?.Value);
        var freshWait = Assert.Single(
            checkpoint.Continuation.Waits,
            static wait => wait.Active && wait.Node.Value == "motion-dq/review/await-match");
        Assert.NotEqual(initialWait.RegistrationId, freshWait.RegistrationId);

        var checkpointJson = ProcessDurableCheckpointJsonSerializer.Serialize(checkpoint);
        var restoreValidation = ProcessDurableCheckpointJsonSerializer.TryDeserialize(
            checkpointJson,
            fixture.Plan,
            out var restored);
        Assert.True(restoreValidation.IsValid, Format(restoreValidation));
        var restoredCheckpoint = Assert.IsType<ProcessDurableCheckpoint>(restored);
        Assert.Equivalent(checkpoint.Continuation, restoredCheckpoint.Continuation, strict: true);
        store = new InMemoryProcessDurableStore();
        var restoredStore = await store.InitializeAsync(
            Context(clock.Next()),
            new("commit/motion-dq/restore-after-hold"),
            restoredCheckpoint);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, restoredStore.Disposition);
        checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(restoredStore.Snapshot).Checkpoint;
        runtime = Runtime(store, fixture, durableHost, adapter);

        var restoredWait = Assert.Single(
            checkpoint.Continuation.Waits,
            static wait => wait.Active && wait.Node.Value == "motion-dq/review/await-match");
        Assert.Equal(freshWait.RegistrationId, restoredWait.RegistrationId);
        Assert.NotEqual(initialWait.RegistrationId, restoredWait.RegistrationId);
        var restoredTarget = new ProcessTokenInteractionTarget(
            checkpoint.ContinuationIdentity,
            restoredWait.Token,
            restoredWait.RegistrationId);
        var hireInput = new ProcessActivationInput(
            restoredTarget,
            ReviewDecisionSignal(
                fixture,
                restoredTarget,
                MotionDqReviewDecisionKind.Hire));
        var arbitrationAtUtc = clock.AdvanceTo(input.ReviewDueAtUtc);
        var hireAdmission = await store.AdmitInputAsync(
            Context(arbitrationAtUtc),
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            hireInput,
            arbitrationAtUtc);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, hireAdmission.Disposition);
        checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(hireAdmission.Snapshot).Checkpoint;
        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new("activation/motion-dq/hold-cycle/hire"),
            ProcessActivationCause.Interaction,
            clock.Next(),
            [hireInput]);

        Assert.Equal(
            MotionDqCaseMilestone.InsuranceTerms,
            durableHost.CaseMilestone(input.Prequalification.CaseId));
        var resolvedRestoredWait = Assert.Single(
            checkpoint.Continuation.Waits,
            wait => wait.RegistrationId == restoredWait.RegistrationId);
        Assert.False(resolvedRestoredWait.Active);
        Assert.Equal(
            "motion-dq/review/hire",
            resolvedRestoredWait.WinnerClause?.Value);
        Assert.Equal(hireInput.Envelope.Context.EmissionId, resolvedRestoredWait.WinnerInput);
        var hireReceipt = Assert.Single(
            checkpoint.Continuation.InputReceipts,
            receipt => receipt.Emission == hireInput.Envelope.Context.EmissionId);
        Assert.Equal(ProcessInputAdmissionDisposition.Consumed, hireReceipt.Disposition);
        Assert.Equal(ProcessInputAdmissionReason.Consumed, hireReceipt.Reason);
        Assert.Equal(restoredWait.RegistrationId, hireReceipt.WaitRegistrationId);
        checkpoint = await AdvanceAtNodeAsync(
            runtime,
            fixture,
            checkpoint,
            "motion-dq/insurance-terms/request",
            clock.Next());
        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new("activation/motion-dq/hold-cycle/insurance-accepted"),
            ProcessActivationCause.Interaction,
            clock.Next(),
            PendingInputs(checkpoint));
        checkpoint = await DriveToTerminalAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "hold-cycle");

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, checkpoint.Continuation.Terminal.Kind);
        AssertAuthoritativeCompletedState(durableHost, input);
    }

    [Fact]
    public async Task ReviewTimeout_RestoresExactWait_AndConvergesWithoutLiveExecution()
    {
        var fixture = MotionDqProcess.Version1;
        var clock = new ScenarioClock(new(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        var input = Input(clock.Peek.AddDays(1));
        var durableHost = new StatefulTransitionHost(fixture, input);
        var adapter = new MotionDqScenarioAdapter(fixture);
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, durableHost, adapter);
        var initialized = await runtime.InitializeAsync(
            Context(clock.Next()),
            fixture.Plan,
            Start(fixture, input, clock.Next(), clock.Next()));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        checkpoint = await ReachReviewWaitAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "review-timeout");
        var wait = Assert.Single(
            checkpoint.Continuation.Waits,
            static candidate => candidate.Active && candidate.Node.Value == "motion-dq/review/await-match");
        var timer = Assert.Single(wait.Timers);
        Assert.Equal("motion-dq/review/timed-out", timer.Clause.Value);
        Assert.Equal(input.ReviewDueAtUtc, timer.DueAtUtc);
        var inputReceiptCount = checkpoint.Continuation.InputReceipts.Length;

        var checkpointJson = ProcessDurableCheckpointJsonSerializer.Serialize(checkpoint);
        var restoreValidation = ProcessDurableCheckpointJsonSerializer.TryDeserialize(
            checkpointJson,
            fixture.Plan,
            out var restored);
        Assert.True(restoreValidation.IsValid, Format(restoreValidation));
        var restoredCheckpoint = Assert.IsType<ProcessDurableCheckpoint>(restored);
        Assert.Equivalent(checkpoint.Continuation, restoredCheckpoint.Continuation, strict: true);

        store = new InMemoryProcessDurableStore();
        var restoredStore = await store.InitializeAsync(
            Context(clock.Next()),
            new("commit/motion-dq/restore-before-review-timeout"),
            restoredCheckpoint);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, restoredStore.Disposition);
        checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(restoredStore.Snapshot).Checkpoint;
        runtime = Runtime(store, fixture, durableHost, adapter);

        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new("activation/motion-dq/review-timeout/timer"),
            ProcessActivationCause.Timer,
            clock.AdvanceTo(input.ReviewDueAtUtc));

        var resolvedWait = Assert.Single(
            checkpoint.Continuation.Waits,
            candidate => candidate.RegistrationId == wait.RegistrationId);
        Assert.False(resolvedWait.Active);
        Assert.Equal("motion-dq/review/timed-out", resolvedWait.WinnerClause?.Value);
        Assert.Null(resolvedWait.WinnerInput);
        Assert.Equal(inputReceiptCount, checkpoint.Continuation.InputReceipts.Length);
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(
            MotionDqOnboardingOutcome.ReviewTimedOut.ToString(),
            checkpoint.Continuation.Terminal.Detail?.Value?.Value?.GetRequiredString());
        Assert.Equal(
            MotionDqCaseMilestone.Cancelled,
            durableHost.CaseMilestone(input.Prequalification.CaseId));
    }

    [Fact]
    public async Task VendorFailure_DoesNotSettleRequirement_AndManualFallbackAppliesExactlyOnce()
    {
        var fixture = MotionDqProcess.Version1;
        var clock = new ScenarioClock(new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        var input = Input(clock.Peek.AddDays(1));
        var durableHost = new StatefulTransitionHost(fixture, input);
        var adapter = new MotionDqScenarioAdapter(fixture, vendorFailureSuffix: "/drug-test/vendor");
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, durableHost, adapter);
        var initialized = await runtime.InitializeAsync(
            Context(clock.Next()),
            fixture.Plan,
            Start(fixture, input, clock.Next(), clock.Next()));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        checkpoint = await ReachPostTermsVendorFanOutAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "manual-fallback");

        var failedRequirement = input.PostTerms.DrugTest.Requirement;
        var failedVendor = Assert.Single(
            PendingVendorOperations(checkpoint),
            static operation => operation.Request.Context.Origin.Node.Value.EndsWith(
                "/drug-test/vendor",
                StringComparison.Ordinal));
        checkpoint = await AdvanceOperationAsync(
            runtime,
            fixture,
            checkpoint,
            failedVendor.OperationId,
            clock.Next());
        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new("activation/motion-dq/manual-fallback/vendor-failed"),
            ProcessActivationCause.Interaction,
            clock.Next(),
            PendingInputs(checkpoint));

        Assert.Equal(MotionDqRequirementStatus.Pending, durableHost.RequirementStatus(failedRequirement));
        Assert.Equal(0, durableHost.RequirementEvaluationCount(failedRequirement));
        var manual = Assert.Single(
            checkpoint.DurableOperations,
            static operation => operation.Status == DurableOperationStatus.Pending
                && operation.Request.Context.Origin.Node.Value.EndsWith("/drug-test/manual", StringComparison.Ordinal));
        Assert.Equal(failedVendor.Request.Contract, manual.Request.Contract);
        Assert.Equivalent(failedVendor.Request.Payload, manual.Request.Payload, strict: true);
        Assert.Equal(failedVendor.Request.Context.AuthorityScope, manual.Request.Context.AuthorityScope);
        Assert.Equal(failedVendor.Request.Context.CorrelationId, manual.Request.Context.CorrelationId);
        Assert.NotEqual(failedVendor.OperationId, manual.OperationId);
        checkpoint = await AdvanceOperationAsync(
            runtime,
            fixture,
            checkpoint,
            manual.OperationId,
            clock.Next());
        var manualAdapterCalls = adapter.Invocations.Count;
        var manualReplay = await runtime.AdvanceOperationAsync(
            Context(clock.Next()),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            manual.OperationId);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, manualReplay.Disposition);
        Assert.Equal(manualAdapterCalls, adapter.Invocations.Count);
        Assert.Equal(manual.OperationId, manualReplay.Operation?.OperationId);
        checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(manualReplay.Snapshot).Checkpoint;
        var manualInputs = PendingInputs(checkpoint);
        var manualActivationId = new ActivationId("activation/motion-dq/manual-fallback/manual-fulfilled");
        var manualActivationAtUtc = clock.Next();
        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            manualActivationId,
            ProcessActivationCause.Interaction,
            manualActivationAtUtc,
            manualInputs);

        Assert.Equal(MotionDqRequirementStatus.Satisfied, durableHost.RequirementStatus(failedRequirement));
        Assert.Equal(1, durableHost.RequirementEvaluationCount(failedRequirement));
        var hostCallsAfterManual = durableHost.InvocationKeys.Count;
        var exactActivationReplay = await runtime.ActivateAsync(
            Context(clock.Next()),
            fixture.Plan,
            checkpoint.ContinuationIdentity,
            new(
                manualActivationId,
                ProcessActivationCause.Interaction,
                manualActivationAtUtc,
                ActivationContext(fixture),
                manualInputs));
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, exactActivationReplay.Disposition);
        Assert.Equal(hostCallsAfterManual, durableHost.InvocationKeys.Count);
        Assert.Equal(manualAdapterCalls, adapter.Invocations.Count);

        var lateVendorReplay = await runtime.AdvanceOperationAsync(
            Context(clock.Next()),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            failedVendor.OperationId);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, lateVendorReplay.Disposition);
        Assert.Equal(failedVendor.OperationId, lateVendorReplay.Operation?.OperationId);
        Assert.Equal(manualAdapterCalls, adapter.Invocations.Count);
        Assert.Equal(MotionDqRequirementStatus.Satisfied, durableHost.RequirementStatus(failedRequirement));
        Assert.Equal(1, durableHost.RequirementEvaluationCount(failedRequirement));
        checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(lateVendorReplay.Snapshot).Checkpoint;
        checkpoint = await DriveToTerminalAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "manual-fallback");

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, checkpoint.Continuation.Terminal.Kind);
        AssertAuthoritativeCompletedState(durableHost, input);
        Assert.Single(
            adapter.Invocations,
            static invocation => invocation.Request.Context.Origin.Node.Value.EndsWith(
                "/drug-test/manual",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task MismatchedFulfillmentReceipt_FailsForkWithoutMutatingRequirementAuthority()
    {
        var fixture = MotionDqProcess.Version1;
        var clock = new ScenarioClock(new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var input = Input(clock.Peek.AddDays(1));
        var durableHost = new StatefulTransitionHost(fixture, input);
        var adapter = new MotionDqScenarioAdapter(
            fixture,
            vendorMismatchedReceiptSuffix: "/drug-test/vendor");
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, durableHost, adapter);
        var initialized = await runtime.InitializeAsync(
            Context(clock.Next()),
            fixture.Plan,
            Start(fixture, input, clock.Next(), clock.Next()));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        checkpoint = await ReachPostTermsVendorFanOutAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "mismatched-receipt");
        checkpoint = await DriveToTerminalAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "mismatched-receipt");

        Assert.Equal(ExecutionTerminalOutcomeKind.Failed, checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(
            MotionDqCaseMilestone.PostTerms,
            durableHost.CaseMilestone(input.Prequalification.CaseId));
        Assert.Equal(MotionDqRequirementStatus.Pending, durableHost.RequirementStatus(input.PostTerms.DrugTest.Requirement));
        Assert.Equal(0, durableHost.RequirementEvaluationCount(input.PostTerms.DrugTest.Requirement));
    }

    [Fact]
    public async Task ConcurrentSubjectActivation_PreservesIndependentAuthorityAndFailsDifferentially()
    {
        var fixture = MotionDqProcess.Version1;
        var clock = new ScenarioClock(new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
        var input = Input(clock.Peek.AddDays(1));
        var durableHost = new StatefulTransitionHost(fixture, input);
        durableHost.ActivateSubjectExternally(input.Activations.Truck);
        var adapter = new MotionDqScenarioAdapter(fixture);
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, durableHost, adapter);
        var initialized = await runtime.InitializeAsync(
            Context(clock.Next()),
            fixture.Plan,
            Start(fixture, input, clock.Next(), clock.Next()));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        checkpoint = await ReachPostTermsVendorFanOutAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "activation-rejected");
        checkpoint = await DriveToTerminalAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario: "activation-rejected");

        Assert.Equal(ExecutionTerminalOutcomeKind.Failed, checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(
            MotionDqCaseMilestone.Activation,
            durableHost.CaseMilestone(input.Prequalification.CaseId));
        Assert.Equal(
            MotionDqActivationStatus.Active,
            durableHost.SubjectStatus(input.Activations.CarrierOwnerOperator.Subject));
        Assert.Equal(
            MotionDqActivationStatus.Active,
            durableHost.SubjectStatus(input.Activations.Truck.Subject));
        MotionDqSubjectReference[] independentlyCompleted =
        [
            input.Activations.Applicant.Subject,
            input.Activations.Driver.Subject,
            input.Activations.Trailer.Subject
        ];
        Assert.All(
            independentlyCompleted,
            subject => Assert.Equal(
                MotionDqActivationStatus.Active,
                durableHost.SubjectStatus(subject)));
        Assert.DoesNotContain(
            durableHost.Invocations,
            invocation => invocation.Node.Value == "motion-dq/case/advance-activation");
        var rejectedOperation = Assert.Single(
            checkpoint.Operations,
            static receipt => receipt.Key.Node.Value == "motion-dq/activation/truck");
        Assert.NotNull(rejectedOperation.Result.Failure);
        Assert.Empty(rejectedOperation.Result.Emissions);
    }

    static async Task<ProcessDurableCheckpoint> ReachPostTermsVendorFanOutAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableRuntime runtime,
        MotionDqProcess fixture,
        StatefulTransitionHost durableHost,
        ProcessDurableCheckpoint checkpoint,
        ScenarioClock clock,
        string scenario,
        CompiledProcessPlan? plan = null)
    {
        plan ??= fixture.Plan;
        checkpoint = await ReachReviewWaitAsync(
            store,
            runtime,
            fixture,
            durableHost,
            checkpoint,
            clock,
            scenario,
            plan);

        var reviewWait = Assert.Single(
            checkpoint.Continuation.Waits,
            static wait => wait.Active && wait.Node.Value == "motion-dq/review/await-match");
        var reviewTarget = new ProcessTokenInteractionTarget(
            checkpoint.ContinuationIdentity,
            reviewWait.Token,
            reviewWait.RegistrationId);
        var hire = ReviewDecisionSignal(
            fixture,
            reviewTarget,
            MotionDqReviewDecisionKind.Hire);
        var hireInput = new ProcessActivationInput(reviewTarget, hire);
        var admitted = await store.AdmitInputAsync(
            Context(clock.Next()),
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            hireInput,
            clock.Peek);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, admitted.Disposition);
        checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot).Checkpoint;
        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new($"activation/motion-dq/{scenario}/hire"),
            ProcessActivationCause.Interaction,
            clock.Next(),
            [hireInput],
            plan);

        checkpoint = await AdvanceAtNodeAsync(
            runtime,
            fixture,
            checkpoint,
            "motion-dq/insurance-terms/request",
            clock.Next(),
            plan);
        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new($"activation/motion-dq/{scenario}/insurance-accepted"),
            ProcessActivationCause.Interaction,
            clock.Next(),
            PendingInputs(checkpoint),
            plan);

        for (var index = PendingVendorOperations(checkpoint).Length; index < 7; index++)
        {
            checkpoint = await ActivateAndCompareAsync(
                store,
                runtime,
                fixture,
                durableHost,
                new($"activation/motion-dq/{scenario}/post-terms-admit-{index + 1}"),
                ProcessActivationCause.Continue,
                clock.Next(),
                plan: plan);
        }

        Assert.Equal(7, PendingVendorOperations(checkpoint).Length);
        return checkpoint;
    }

    static async Task<(ProcessDurableCheckpoint Checkpoint, StatefulTransitionHost Host)>
        CompletePostTermsInOrderAsync(bool descending)
    {
        var fixture = MotionDqProcess.Version1;
        var clock = new ScenarioClock(new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var input = Input(clock.Peek.AddDays(1));
        var host = new StatefulTransitionHost(fixture, input);
        var adapter = new MotionDqScenarioAdapter(fixture);
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, host, adapter);
        var initialized = await runtime.InitializeAsync(
            Context(clock.Next()),
            fixture.Plan,
            Start(fixture, input, clock.Next(), clock.Next()));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        checkpoint = await ReachPostTermsVendorFanOutAsync(
            store,
            runtime,
            fixture,
            host,
            checkpoint,
            clock,
            scenario: "completion-order");
        var operations = PendingVendorOperations(checkpoint);
        for (var index = 0; index < operations.Length; index++)
        {
            var operation = operations[descending ? ^(index + 1) : index];
            checkpoint = await AdvanceOperationAsync(
                runtime,
                fixture,
                checkpoint,
                operation.OperationId,
                clock.Next());
        }

        checkpoint = await DriveToTerminalAsync(
            store,
            runtime,
            fixture,
            host,
            checkpoint,
            clock,
            scenario: "completion-order");
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, checkpoint.Continuation.Terminal.Kind);
        AssertAuthoritativeCompletedState(host, input);
        return (checkpoint, host);
    }

    static async Task<ProcessDurableCheckpoint> ReachReviewWaitAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableRuntime runtime,
        MotionDqProcess fixture,
        StatefulTransitionHost durableHost,
        ProcessDurableCheckpoint checkpoint,
        ScenarioClock clock,
        string scenario,
        CompiledProcessPlan? plan = null)
    {
        plan ??= fixture.Plan;
        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new($"activation/motion-dq/{scenario}/start"),
            ProcessActivationCause.Start,
            clock.Next(),
            plan: plan);
        checkpoint = await AdvanceAtNodeAsync(
            runtime,
            fixture,
            checkpoint,
            "motion-dq/review/create-task",
            clock.Next(),
            plan);
        checkpoint = await ActivateAndCompareAsync(
            store,
            runtime,
            fixture,
            durableHost,
            new($"activation/motion-dq/{scenario}/review-task-created"),
            ProcessActivationCause.Interaction,
            clock.Next(),
            PendingInputs(checkpoint),
            plan);

        _ = Assert.Single(
            checkpoint.Continuation.Waits,
            static wait => wait.Active && wait.Node.Value == "motion-dq/review/await-match");
        return checkpoint;
    }

    static async Task<ProcessDurableCheckpoint> DriveToTerminalAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableRuntime runtime,
        MotionDqProcess fixture,
        StatefulTransitionHost durableHost,
        ProcessDurableCheckpoint checkpoint,
        ScenarioClock clock,
        string scenario,
        CompiledProcessPlan? plan = null)
    {
        plan ??= fixture.Plan;
        for (var index = 0;
             index < 32 && checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None;
             index++)
        {
            foreach (var operation in checkpoint.DurableOperations.Where(
                         static operation => operation.Status == DurableOperationStatus.Pending))
            {
                checkpoint = await AdvanceOperationAsync(
                    runtime,
                    fixture,
                    checkpoint,
                    operation.OperationId,
                    clock.Next(),
                    plan);
            }

            var inputs = PendingInputs(checkpoint);
            checkpoint = await ActivateAndCompareAsync(
                store,
                runtime,
                fixture,
                durableHost,
                new($"activation/motion-dq/{scenario}/drain-{index + 1}"),
                inputs.IsDefaultOrEmpty ? ProcessActivationCause.Continue : ProcessActivationCause.Interaction,
                clock.Next(),
                inputs,
                plan);
        }

        Assert.NotEqual(ExecutionTerminalOutcomeKind.None, checkpoint.Continuation.Terminal.Kind);
        return checkpoint;
    }

    static async Task<ProcessDurableCheckpoint> ActivateAndCompareAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableRuntime runtime,
        MotionDqProcess fixture,
        StatefulTransitionHost durableHost,
        ActivationId id,
        ProcessActivationCause cause,
        DateTimeOffset observedAtUtc,
        ImmutableArray<ProcessActivationInput> inputs = default,
        CompiledProcessPlan? plan = null)
    {
        plan ??= fixture.Plan;
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(observedAtUtc),
            instanceId: InstanceId())).Checkpoint;
        var activation = new ProcessActivation(
            id,
            cause,
            observedAtUtc,
            ActivationContext(fixture),
            inputs);
        var oracleState = durableHost.Clone();
        var oracleHost = new ProcessOperationReplayHost(oracleState, before.Operations);
        var expected = ProcessReferenceInterpreter.Activate(
            plan,
            before.Continuation,
            activation,
            oracleHost);

        var result = await runtime.ActivateAsync(
            Context(observedAtUtc),
            plan,
            before.ContinuationIdentity,
            activation);
        var actual = Assert.IsType<ProcessActivationDecision>(result.Decision);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint;

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equivalent(expected, actual, strict: true);
        Assert.Equivalent(expected.State, checkpoint.Continuation, strict: true);
        oracleState.AssertAuthoritativeStateEquals(durableHost);
        return checkpoint;
    }

    static async Task<ProcessDurableCheckpoint> AdvanceAtNodeAsync(
        ProcessDurableRuntime runtime,
        MotionDqProcess fixture,
        ProcessDurableCheckpoint checkpoint,
        string node,
        DateTimeOffset observedAtUtc,
        CompiledProcessPlan? plan = null)
    {
        var operation = Assert.Single(
            checkpoint.DurableOperations,
            candidate => candidate.Status == DurableOperationStatus.Pending
                && candidate.Request.Context.Origin.Node.Value == node);
        return await AdvanceOperationAsync(
            runtime,
            fixture,
            checkpoint,
            operation.OperationId,
            observedAtUtc,
            plan);
    }

    static async Task<ProcessDurableCheckpoint> AdvanceOperationAsync(
        ProcessDurableRuntime runtime,
        MotionDqProcess fixture,
        ProcessDurableCheckpoint checkpoint,
        EmissionId operationId,
        DateTimeOffset observedAtUtc,
        CompiledProcessPlan? plan = null)
    {
        plan ??= fixture.Plan;
        var result = await runtime.AdvanceOperationAsync(
            Context(observedAtUtc),
            plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            operationId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status);
        return Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint;
    }

    static ImmutableArray<DurableOperationState> PendingVendorOperations(ProcessDurableCheckpoint checkpoint) =>
        [.. checkpoint.DurableOperations
            .Where(static operation => operation.Status == DurableOperationStatus.Pending
                && operation.Request.Context.Origin.Node.Value.StartsWith("motion-dq/post-terms/", StringComparison.Ordinal)
                && operation.Request.Context.Origin.Node.Value.EndsWith("/vendor", StringComparison.Ordinal))
            .OrderBy(static operation => operation.Request.Context.Origin.Node.Value, StringComparer.Ordinal)];

    static ImmutableArray<ProcessActivationInput> PendingInputs(ProcessDurableCheckpoint checkpoint) =>
        [.. checkpoint.Inbox
            .Where(static entry => entry.Receipt is null)
            .Select(static entry => entry.Input)
            .OrderBy(static input => input.Envelope.Context.EmissionId.Value, StringComparer.Ordinal)];

    static ProcessStartReceipt Start(
        MotionDqProcess fixture,
        MotionDqOnboardingInput input,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset acceptedAtUtc)
    {
        var continuation = new ProcessContinuationIdentity(InstanceId(), new("process-attempt/1"));
        return new(
            new(
                ProcessStartRequest.CurrentSchemaVersion,
                fixture.Reference,
                new(
                    new("start-command/motion-dq/happy-path"),
                    new("start-idempotency/motion-dq/happy-path"),
                    continuation.ProcessInstanceId,
                    new("operator/tests", Authority, "policy/tests/allow"),
                    issuedAtUtc,
                    fixture.Document.Metadata.Provenance),
                continuation,
                PortableValue.Concrete(fixture.Definition.Input, ObservationValue.FromObject(input))),
            acceptedAtUtc);
    }

    static MotionDqOnboardingInput Input(DateTimeOffset reviewDueAtUtc)
    {
        const string caseId = "case/motion-dq/1";
        const string applicationId = "application/motion-dq/1";
        const string carrierDecisionId = "activation-decision/carrier/1";
        var profile = MotionDqProfileCatalog.Version1;
        var carrierSubject = new MotionDqSubjectReference(
            ApplicationId: applicationId,
            Kind: MotionDqSubjectKind.CarrierOwnerOperator,
            SubjectId: "carrier/1",
            ParentApplicationId: null);
        var carrierProof = new MotionDqCarrierActivationProof(
            carrierSubject,
            activationDecisionId: carrierDecisionId,
            evidenceId: "activation-evidence/carrier/1");

        return new(
            Prequalification: new(
                CaseId: caseId,
                ApplicationId: applicationId,
                ProfileId: profile.ProfileId,
                ProfileRevision: profile.Revision,
                RequirementGate: MotionDqGateDisposition.Satisfied),
            FullApplication: new(
                CaseId: caseId,
                ApplicationId: applicationId,
                RequirementGate: MotionDqGateDisposition.Satisfied),
            ReviewTask: new(
                CaseId: caseId,
                ApplicationId: applicationId),
            ReviewDueAtUtc: reviewDueAtUtc,
            ReviewTimeoutCancellation: new(
                CancellationId: "cancellation/review-timeout/1",
                CaseId: caseId,
                ReasonCode: "review-timeout"),
            InsuranceTerms: new(
                CaseId: caseId,
                TermsRevision: "insurance-terms/2026-08"),
            InsuranceTermsAdmission: new(
                DecisionId: "milestone-decision/insurance-terms/1",
                CaseId: caseId,
                ExpectedMilestone: MotionDqCaseMilestone.InsuranceTerms,
                NextMilestone: MotionDqCaseMilestone.PostTerms,
                GateId: MotionDqProfileCatalog.InsuranceTermsAcceptedGate.Id,
                GateDisposition: MotionDqGateDisposition.Satisfied),
            PostTerms: new(
                DrugTest: Fulfillment(profile: profile, caseId: caseId, suffix: "drug-test"),
                Clearinghouse: Fulfillment(profile: profile, caseId: caseId, suffix: "clearinghouse"),
                Vehicle: Fulfillment(profile: profile, caseId: caseId, suffix: "vehicle"),
                Business: Fulfillment(profile: profile, caseId: caseId, suffix: "business"),
                Equipment: Fulfillment(profile: profile, caseId: caseId, suffix: "equipment"),
                Permit: Fulfillment(profile: profile, caseId: caseId, suffix: "permit"),
                RandomPool: Fulfillment(profile: profile, caseId: caseId, suffix: "random-pool")),
            PostTermsAdmission: new(
                DecisionId: "milestone-decision/post-terms/1",
                CaseId: caseId,
                ExpectedMilestone: MotionDqCaseMilestone.PostTerms,
                NextMilestone: MotionDqCaseMilestone.Activation,
                GateId: MotionDqProfileCatalog.PostTermsCompleteGate.Id,
                GateDisposition: MotionDqGateDisposition.Satisfied),
            Activations: new(
                Applicant: Activation(
                    profile: profile,
                    applicationId: applicationId,
                    kind: MotionDqSubjectKind.Applicant,
                    subjectId: "applicant/1",
                    decisionId: "activation-decision/applicant/1"),
                CarrierOwnerOperator: Activation(
                    profile: profile,
                    applicationId: applicationId,
                    kind: MotionDqSubjectKind.CarrierOwnerOperator,
                    subjectId: "carrier/1",
                    decisionId: carrierDecisionId),
                Driver: Activation(
                    profile: profile,
                    applicationId: applicationId,
                    kind: MotionDqSubjectKind.Driver,
                    subjectId: "driver/1",
                    decisionId: "activation-decision/driver/1",
                    parentApplicationId: applicationId,
                    parentCarrierProof: carrierProof),
                Truck: Activation(
                    profile: profile,
                    applicationId: applicationId,
                    kind: MotionDqSubjectKind.Truck,
                    subjectId: "truck/1",
                    decisionId: "activation-decision/truck/1"),
                Trailer: Activation(
                    profile: profile,
                    applicationId: applicationId,
                    kind: MotionDqSubjectKind.Trailer,
                    subjectId: "trailer/1",
                    decisionId: "activation-decision/trailer/1")),
            ActivationAdmission: new(
                DecisionId: "milestone-decision/activation/1",
                CaseId: caseId,
                ExpectedMilestone: MotionDqCaseMilestone.Activation,
                NextMilestone: MotionDqCaseMilestone.Completed,
                GateId: MotionDqProfileCatalog.ActivationCompleteGate.Id,
                GateDisposition: MotionDqGateDisposition.Satisfied));
    }

    static ImmutableArray<MotionDqCaseRequirementReference> RequirementReferences(MotionDqOnboardingInput input) =>
    [
        MotionDqProfileCatalog.ScopeRequirement(
            caseId: input.Prequalification.CaseId,
            requirement: MotionDqProfileCatalog.InsuranceTermsRequirement),
        input.PostTerms.DrugTest.Requirement,
        input.PostTerms.Clearinghouse.Requirement,
        input.PostTerms.Vehicle.Requirement,
        input.PostTerms.Business.Requirement,
        input.PostTerms.Equipment.Requirement,
        input.PostTerms.Permit.Requirement,
        input.PostTerms.RandomPool.Requirement
    ];

    static ImmutableArray<MotionDqSubjectActivationInvocation> Subjects(MotionDqOnboardingInput input) =>
    [
        input.Activations.Applicant,
        input.Activations.CarrierOwnerOperator,
        input.Activations.Driver,
        input.Activations.Truck,
        input.Activations.Trailer
    ];

    static void AssertAuthoritativeCompletedState(
        StatefulTransitionHost host,
        MotionDqOnboardingInput input)
    {
        Assert.Equal(
            MotionDqCaseMilestone.Completed,
            host.CaseMilestone(input.Prequalification.CaseId));
        foreach (var requirement in RequirementReferences(input))
        {
            Assert.Equal(MotionDqRequirementStatus.Satisfied, host.RequirementStatus(requirement));
            Assert.Equal(1, host.RequirementEvaluationCount(requirement));
        }

        foreach (var activation in Subjects(input))
        {
            Assert.Equal(MotionDqActivationStatus.Active, host.SubjectStatus(activation.Subject));
        }
    }

    static MotionDqRequirementFulfillmentRequest Fulfillment(
        MotionDqResolvedProfile profile,
        string caseId,
        string suffix) =>
        new(
            Requirement: MotionDqProfileCatalog.ScopeRequirement(
                caseId: caseId,
                requirement: Assert.Single(
                    profile.Requirements,
                    requirement => requirement.Id.EndsWith($"/{suffix}", StringComparison.Ordinal))),
            EvidenceNeedId: Assert.Single(
                profile.EvidenceNeeds,
                evidence => evidence.Id.EndsWith($"/{suffix}", StringComparison.Ordinal)).Id);

    static MotionDqSubjectActivationInvocation Activation(
        MotionDqResolvedProfile profile,
        string applicationId,
        MotionDqSubjectKind kind,
        string subjectId,
        string decisionId,
        string? parentApplicationId = null,
        MotionDqCarrierActivationProof? parentCarrierProof = null)
    {
        var slot = Assert.Single(profile.SubjectSlots, candidate => candidate.Kind == kind);
        return new(
            Subject: new(
                ApplicationId: applicationId,
                Kind: kind,
                SubjectId: subjectId,
                ParentApplicationId: parentApplicationId),
            Admission: new(
                DecisionId: decisionId,
                Kind: kind,
                GateId: slot.ActivationGate.Id,
                GateDisposition: MotionDqGateDisposition.Satisfied,
                ParentCarrierProof: parentCarrierProof));
    }

    static SignalEnvelope ReviewDecisionSignal(
        MotionDqProcess fixture,
        ProcessTokenInteractionTarget target,
        MotionDqReviewDecisionKind kind)
    {
        Assert.True(fixture.InteractionCatalog.TryResolve(
            fixture.Interactions.ReviewDecisionSignal,
            out var resolved));
        var contract = Assert.IsType<SignalContractDefinition>(resolved).Payload.Contract;
        var (suffix, reasonCode) = kind switch
        {
            MotionDqReviewDecisionKind.Hire => ("hire", "eligible"),
            MotionDqReviewDecisionKind.Hold => ("hold", "pending-review"),
            MotionDqReviewDecisionKind.NotEligible => ("not-eligible", "not-eligible"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported review decision kind.")
        };
        var decision = new MotionDqReviewDecision(
            DecisionId: $"review-decision/{suffix}/1",
            CaseId: "case/motion-dq/1",
            ApplicationId: "application/motion-dq/1",
            Kind: kind,
            ReasonCode: reasonCode);
        return new(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new($"emission/motion-dq/review-decision/{suffix}/1"),
                new ProcessInteractionOrigin(
                    fixture.Reference,
                    new("source/motion-dq/caseworker"),
                    target.Continuation,
                    new("activation/motion-dq/caseworker-source"),
                    target.Token),
                new("correlation/motion-dq/happy-path"),
                causationId: null,
                Authority,
                new($"idempotency/motion-dq/review-decision/{suffix}/1"),
                ordering: null,
                DurableDelivery,
                fixture.Document.Metadata.Provenance),
            fixture.Interactions.ReviewDecisionSignal,
            PortableValue.Concrete(contract, ObservationValue.FromObject(decision)),
            target);
    }

    static ProcessActivationContext ActivationContext(MotionDqProcess fixture) => new(
        Authority,
        new("correlation/motion-dq/happy-path"),
        DurableDelivery,
        fixture.Document.Metadata.Provenance);

    static ProcessDurableRuntime Runtime(
        IProcessDurableStore store,
        MotionDqProcess fixture,
        StatefulTransitionHost host,
        MotionDqScenarioAdapter adapter) =>
        new(
            store,
            host,
            new("worker/motion-dq-conformance", TimeSpan.FromMinutes(5)),
            new ExactBindingResolver(fixture.RequestBindings),
            operationAdapterResolver: new ExactAdapterResolver(adapter));

    static ProcessInstanceId InstanceId() => new("process-instance/motion-dq/happy-path");

    static OperationContext Context(DateTimeOffset utcNow) =>
        OperationContext.Create(timeProvider: new FixedTimeProvider(utcNow));

    static string OperationKey(ProcessOperationOccurrence occurrence) => string.Join(
        '|',
        occurrence.Continuation.ProcessInstanceId.Value,
        occurrence.Continuation.ProcessAttemptId.Value,
        occurrence.Activation.Value,
        occurrence.Token.Value,
        occurrence.Node.Value,
        occurrence.Occurrence);

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed class ExactBindingResolver(ImmutableArray<DurableRequestBinding> bindings)
        : IProcessDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? binding)
        {
            binding = bindings.FirstOrDefault(candidate => candidate.Request == request.Contract);
            return binding is not null;
        }
    }

    sealed class ExactAdapterResolver(MotionDqScenarioAdapter adapter)
        : IProcessDurableOperationAdapterResolver
    {
        public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? resolved)
        {
            resolved = adapter.Capabilities.SupportedRequests.Contains(request.Contract) ? adapter : null;
            return resolved is not null;
        }
    }

    sealed class MotionDqScenarioAdapter : IDurableOperationAdapter
    {
        readonly MotionDqProcess fixture;
        readonly string? vendorFailureSuffix;
        readonly string? vendorMismatchedReceiptSuffix;
        readonly List<DurableOperationInvocation> invocations = [];

        internal MotionDqScenarioAdapter(
            MotionDqProcess fixture,
            string? vendorFailureSuffix = null,
            string? vendorMismatchedReceiptSuffix = null)
        {
            this.fixture = fixture;
            this.vendorFailureSuffix = vendorFailureSuffix;
            this.vendorMismatchedReceiptSuffix = vendorMismatchedReceiptSuffix;
            Capabilities = new(
                DurableOperationIdempotencyEvidence.TargetDeduplication,
                DurableOperationReconciliationCapability.Supported,
                [.. fixture.RequestBindings.Select(static binding => binding.Request)]);
        }

        public DurableOperationAdapterCapabilities Capabilities { get; }

        internal IReadOnlyList<DurableOperationInvocation> Invocations => invocations;

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            context.ThrowIfCancellationRequested();
            invocations.Add(invocation);
            var request = invocation.Request;
            RequestTerminalOutcome outcome;
            if (request.Contract == fixture.Interactions.ReviewTaskRequest)
            {
                outcome = Result(
                    request,
                    MotionDqInteractionContracts.ReviewTaskCreatedOutcome,
                    new MotionDqReviewTaskReference("task/motion-dq/review/1"));
            }
            else if (request.Contract == fixture.Interactions.InsuranceTermsRequest)
            {
                var caseId = Field(request, nameof(MotionDqInsuranceTermsRequest.CaseId));
                var revision = Field(request, nameof(MotionDqInsuranceTermsRequest.TermsRevision));
                outcome = Result(
                    request,
                    MotionDqInteractionContracts.InsuranceTermsAcceptedOutcome,
                    new MotionDqInsuranceTermsResult(
                        CaseId: caseId,
                        TermsRevision: revision,
                        DecidedAtUtc: context.UtcNow,
                        Evaluation: new(
                            EvaluationId: "evaluation/insurance-terms/1",
                            Requirement: MotionDqProfileCatalog.ScopeRequirement(
                                caseId: caseId,
                                requirement: MotionDqProfileCatalog.InsuranceTermsRequirement),
                            Disposition: MotionDqGateDisposition.Satisfied,
                            EvidenceId: "evidence/insurance-terms/1")));
            }
            else if (request.Contract == fixture.Interactions.FulfillRequirementRequest)
            {
                var origin = Assert.IsType<ProcessInteractionOrigin>(request.Context.Origin);
                var requirement = Requirement(request);
                if (vendorFailureSuffix is not null
                    && origin.Node.Value.EndsWith(vendorFailureSuffix, StringComparison.Ordinal))
                {
                    outcome = Terminal(
                        request,
                        MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                        new MotionDqRequirementFulfillmentFailure(
                            ProviderAttemptId: $"provider-attempt/{origin.Node.Value}",
                            Requirement: requirement,
                            EvidenceNeedId: Field(
                                request,
                                nameof(MotionDqRequirementFulfillmentRequest.EvidenceNeedId)),
                            ReasonCode: "provider-unavailable",
                            ObservedAtUtc: context.UtcNow));
                }
                else
                {
                    var evaluatedRequirement = vendorMismatchedReceiptSuffix is not null
                        && origin.Node.Value.EndsWith(vendorMismatchedReceiptSuffix, StringComparison.Ordinal)
                            ? new MotionDqCaseRequirementReference(
                                caseId: requirement.CaseId,
                                requirementId: $"{requirement.RequirementId}/mismatch")
                            : requirement;
                    outcome = Result(
                        request,
                        MotionDqInteractionContracts.RequirementFulfilledOutcome,
                        new MotionDqRequirementEvaluationReceipt(
                            EvaluationId: $"evaluation/{origin.Node.Value}",
                            Requirement: evaluatedRequirement,
                            Disposition: MotionDqGateDisposition.Satisfied,
                            EvidenceId: $"evidence-result/{origin.Node.Value}"));
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unexpected Motion DQ Request '{request.Contract.Definition.DefinitionId.Value}'.");
            }

            return ValueTask.FromResult<DurableOperationAttemptObservation>(
                new DurableOperationOutcomeObservation(outcome));
        }

        RequestResultOutcome Result(RequestEnvelope request, RequestTerminalOutcomeId id, object value)
        {
            Assert.True(fixture.InteractionCatalog.TryResolve(request.Contract, out var resolved));
            var definition = Assert.IsType<RequestContractDefinition>(resolved);
            var outcome = Assert.IsAssignableFrom<RequestResultDefinition>(definition.Response.Find(id));
            return new(id, PortableValue.Concrete(outcome.Schema.Contract, ObservationValue.FromObject(value)));
        }

        RequestTerminalOutcome Terminal(RequestEnvelope request, RequestTerminalOutcomeId id, object value)
        {
            Assert.True(fixture.InteractionCatalog.TryResolve(request.Contract, out var resolved));
            var definition = Assert.IsType<RequestContractDefinition>(resolved);
            var outcome = Assert.IsType<RequestFailureDefinition>(definition.Response.Find(id));
            return new RequestFailureOutcome(
                id,
                PortableValue.Concrete(outcome.Schema.Contract, ObservationValue.FromObject(value)));
        }

        static string Field(RequestEnvelope request, string name)
        {
            var payload = Assert.IsType<ObservationValue>(request.Payload.Value);
            Assert.True(payload.TryGetProperty(name, out var value));
            return value.GetRequiredString();
        }

        static MotionDqCaseRequirementReference Requirement(RequestEnvelope request)
        {
            var payload = Assert.IsType<ObservationValue>(request.Payload.Value);
            Assert.True(payload.TryGetProperty(nameof(MotionDqRequirementFulfillmentRequest.Requirement), out var value));
            Assert.True(value.TryGetProperty(nameof(MotionDqCaseRequirementReference.CaseId), out var caseId));
            Assert.True(value.TryGetProperty(nameof(MotionDqCaseRequirementReference.RequirementId), out var requirementId));
            return new(
                caseId: caseId.GetRequiredString(),
                requirementId: requirementId.GetRequiredString());
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            throw new InvalidOperationException("The successful Motion DQ scenario never reconciles an operation.");
    }

    sealed class StatefulTransitionHost : IProcessReferenceHost
    {
        static readonly IReadOnlyDictionary<ExecutionDefinitionReference, CompiledTransitionPlan> Plans =
            Compile(MotionDqProcess.Version1.Transitions.Documents);

        readonly MotionDqTransitionDefinitions transitions;
        readonly Dictionary<ObservationValue, ObservationValue> caseStates;
        readonly Dictionary<ObservationValue, ObservationValue> requirementStates;
        readonly Dictionary<ObservationValue, ObservationValue> subjectStates;
        readonly List<string> invocationKeys = [];
        readonly List<ProcessTransitionInvocation> invocations = [];

        internal StatefulTransitionHost(MotionDqProcess fixture, MotionDqOnboardingInput input)
        {
            transitions = fixture.Transitions;
            caseStates = [];
            requirementStates = [];
            subjectStates = [];

            var caseId = input.Prequalification.CaseId;
            var caseKey = ObservationValue.FromString(caseId);
            caseStates.Add(caseKey, InitialCaseState());
            DecideAndCommit(
                Plans[transitions.ResolveCaseProfile.Reference],
                MotionDqProfileCatalog.CreateCaseProfileResolution(caseId: caseId),
                caseStates,
                caseKey,
                new("transition-activation/motion-dq/resolve-profile"));

            foreach (var requirement in RequirementReferences(input))
            {
                var key = ObservationValue.FromObject(requirement);
                requirementStates.Add(key, InitialRequirementState(requirement));
            }

            var carrier = input.Activations.CarrierOwnerOperator;
            var driverProof = input.Activations.Driver.Admission.ParentCarrierProof;
            foreach (var activation in Subjects(input))
            {
                var key = ObservationValue.FromObject(activation.Subject);
                var requiredParentCarrierProof = activation.Subject.Kind == MotionDqSubjectKind.Driver
                    ? new MotionDqCarrierActivationProof(
                        carrierSubject: carrier.Subject,
                        activationDecisionId: carrier.Admission.DecisionId,
                        evidenceId: Assert.IsType<MotionDqCarrierActivationProof>(driverProof).EvidenceId)
                    : null;
                subjectStates.Add(key, InitialSubjectState(activation, requiredParentCarrierProof));
            }
        }

        StatefulTransitionHost(StatefulTransitionHost source)
        {
            transitions = source.transitions;
            caseStates = new(source.caseStates);
            requirementStates = new(source.requirementStates);
            subjectStates = new(source.subjectStates);
            invocationKeys = [.. source.invocationKeys];
            invocations = [.. source.invocations];
        }

        internal IReadOnlyList<string> InvocationKeys => invocationKeys;

        internal IReadOnlyList<ProcessTransitionInvocation> Invocations => invocations;

        internal StatefulTransitionHost Clone() => new(this);

        internal void AssertAuthoritativeStateEquals(StatefulTransitionHost other)
        {
            AssertStateMapEquals(caseStates, other.caseStates);
            AssertStateMapEquals(requirementStates, other.requirementStates);
            AssertStateMapEquals(subjectStates, other.subjectStates);
        }

        internal MotionDqCaseMilestone CaseMilestone(string caseId) => Enum.Parse<MotionDqCaseMilestone>(
            RequiredField(caseStates[ObservationValue.FromString(caseId)], nameof(MotionDqOnboardingCaseEntity.Milestone))
                .GetRequiredString());

        internal MotionDqRequirementStatus RequirementStatus(MotionDqCaseRequirementReference requirement) =>
            Enum.Parse<MotionDqRequirementStatus>(
                RequiredField(
                    requirementStates[ObservationValue.FromObject(requirement)],
                    nameof(MotionDqCaseRequirementEntity.Status))
                .GetRequiredString());

        internal int RequirementEvaluationCount(MotionDqCaseRequirementReference requirement) =>
            RequiredField(
                requirementStates[ObservationValue.FromObject(requirement)],
                nameof(MotionDqCaseRequirementEntity.Evaluations))
            .Array.Length;

        internal MotionDqActivationStatus SubjectStatus(MotionDqSubjectReference subject) =>
            Enum.Parse<MotionDqActivationStatus>(
                RequiredField(
                    subjectStates[ObservationValue.FromObject(subject)],
                    nameof(MotionDqSubjectActivationEntity.Status))
                .GetRequiredString());

        internal void ActivateSubjectExternally(MotionDqSubjectActivationInvocation activation)
        {
            var subject = ObservationValue.FromObject(activation.Subject);
            DecideAndCommit(
                Plans[transitions.ActivateSubject.Reference],
                activation.Admission,
                subjectStates,
                subject,
                new($"transition-activation/motion-dq/external/{activation.Subject.SubjectId}"));
        }

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
        {
            invocations.Add(invocation);
            invocationKeys.Add(string.Join(
                '|',
                invocation.Continuation.ProcessInstanceId.Value,
                invocation.Continuation.ProcessAttemptId.Value,
                invocation.Activation.Value,
                invocation.Token.Value,
                invocation.Node.Value,
                invocation.Occurrence));
            if (!Plans.TryGetValue(invocation.Definition, out var plan))
            {
                throw new InvalidOperationException(
                    $"Unexpected Motion DQ Transition '{invocation.Definition.DefinitionId.Value}'.");
            }

            var states = States(invocation.Definition);
            var subject = Assert.IsType<ObservationValue>(invocation.Subject.Value);
            if (!states.TryGetValue(subject, out var state))
            {
                return ProcessOperationResult.Failed(Failure(
                    code: "tests.motion-dq.transition.subject-missing",
                    message: $"No authoritative state exists for Transition subject '{subject}'."));
            }

            var decision = TransitionReferenceInterpreter.DecideFullState(
                plan,
                new($"{invocation.Activation.Value}/{invocation.Token.Value}/{invocation.Node.Value}/{invocation.Occurrence}"),
                invocation.Input,
                PortableValue.Concrete(plan.Definition.Observation, state));
            if (decision.Kind is not (TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange))
            {
                var rejection = decision.Diagnostics.FirstOrDefault()
                    ?? Failure(
                        code: "tests.motion-dq.transition.rejected",
                        message: $"Transition '{invocation.Definition.DefinitionId.Value}' returned '{decision.Kind}'.");
                return ProcessOperationResult.Failed(rejection);
            }

            states[subject] = Apply(state, decision.Patch);
            if (decision.Outcome is not null)
            {
                Assert.Empty(decision.Emissions);
                return ProcessOperationResult.Completed(decision.Outcome);
            }

            var diagnostic = decision.Diagnostics.FirstOrDefault()
                ?? Failure(
                    code: "tests.motion-dq.transition.no-outcome",
                    message: $"Transition '{invocation.Definition.DefinitionId.Value}' returned '{decision.Kind}' without an outcome.");
            return ProcessOperationResult.Failed(diagnostic);
        }

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation operation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal target resolution at '{resolution.Node.Value}'.");

        Dictionary<ObservationValue, ObservationValue> States(ExecutionDefinitionReference definition)
        {
            if (definition == transitions.ResolveCaseProfile.Reference
                || definition == transitions.SubmitPrequalification.Reference
                || definition == transitions.SubmitFullApplication.Reference
                || definition == transitions.RecordReviewDecision.Reference
                || definition == transitions.AdvanceCaseMilestone.Reference
                || definition == transitions.CancelCase.Reference)
            {
                return caseStates;
            }

            if (definition == transitions.ApplyRequirementEvaluation.Reference)
            {
                return requirementStates;
            }

            if (definition == transitions.ActivateSubject.Reference)
            {
                return subjectStates;
            }

            throw new InvalidOperationException(
                $"Unexpected Motion DQ Transition '{definition.DefinitionId.Value}'.");
        }

        static Dictionary<ExecutionDefinitionReference, CompiledTransitionPlan> Compile(
            ImmutableArray<ExecutionDefinitionDocument> documents)
        {
            Dictionary<ExecutionDefinitionReference, CompiledTransitionPlan> result = [];
            foreach (var document in documents)
            {
                var compilation = TransitionStaticCompiler.Compile(document);
                Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
                result.Add(
                    new(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint),
                    Assert.IsType<CompiledTransitionPlan>(compilation.Plan));
            }

            return result;
        }

        static void DecideAndCommit<TInput>(
            CompiledTransitionPlan plan,
            TInput input,
            Dictionary<ObservationValue, ObservationValue> states,
            ObservationValue subject,
            ActivationId activation)
        {
            var decision = TransitionReferenceInterpreter.DecideFullState(
                plan,
                activation,
                PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(input)),
                PortableValue.Concrete(plan.Definition.Observation, states[subject]));
            Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
            states[subject] = Apply(states[subject], decision.Patch);
        }

        static ObservationValue Apply(
            ObservationValue state,
            ImmutableArray<TransitionExecutedPatch> patches)
        {
            foreach (var patch in patches)
            {
                var value = patch.After.State switch
                {
                    PortableValueState.Concrete => patch.After.Value!.Value,
                    PortableValueState.Null => ObservationValue.Null,
                    PortableValueState.Absent or PortableValueState.Missing => ObservationValue.Undefined,
                    _ => throw new InvalidOperationException(
                        $"Transition patch '{patch.Node.Value}' produced unsupported state '{patch.After.State}'.")
                };
                state = state.WithField(patch.Path, value);
            }

            return state;
        }

        static ObservationValue InitialCaseState() => ObservationValue.FromObject(
            new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(MotionDqOnboardingCaseEntity.CaseId)] = ObservationValue.FromString(""),
                [nameof(MotionDqOnboardingCaseEntity.SchemaId)] = ObservationValue.FromString(""),
                [nameof(MotionDqOnboardingCaseEntity.ProfileId)] = ObservationValue.FromString(""),
                [nameof(MotionDqOnboardingCaseEntity.ProfileRevision)] = ObservationValue.FromString(""),
                [nameof(MotionDqOnboardingCaseEntity.ApplicationId)] = ObservationValue.FromString(""),
                [nameof(MotionDqOnboardingCaseEntity.ResolvedBlocks)] = ObservationValue.FromArray([]),
                [nameof(MotionDqOnboardingCaseEntity.ResolvedRequirements)] = ObservationValue.FromArray([]),
                [nameof(MotionDqOnboardingCaseEntity.ResolvedEvidenceNeeds)] = ObservationValue.FromArray([]),
                [nameof(MotionDqOnboardingCaseEntity.ResolvedGates)] = ObservationValue.FromArray([]),
                [nameof(MotionDqOnboardingCaseEntity.ResolvedSubjectSlots)] = ObservationValue.FromArray([]),
                [nameof(MotionDqOnboardingCaseEntity.Milestone)] = ObservationValue.FromString(MotionDqCaseMilestone.Uninitialized.ToString()),
                [nameof(MotionDqOnboardingCaseEntity.LastReviewDecisionId)] = ObservationValue.FromString(""),
                [nameof(MotionDqOnboardingCaseEntity.LastMilestoneDecisionId)] = ObservationValue.FromString(""),
                [nameof(MotionDqOnboardingCaseEntity.CancellationId)] = ObservationValue.FromString("")
            });

        static ObservationValue InitialRequirementState(MotionDqCaseRequirementReference requirement) =>
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(MotionDqCaseRequirementEntity.CaseId)] = ObservationValue.FromString(requirement.CaseId),
                [nameof(MotionDqCaseRequirementEntity.RequirementId)] = ObservationValue.FromString(requirement.RequirementId),
                [nameof(MotionDqCaseRequirementEntity.Status)] = ObservationValue.FromString(MotionDqRequirementStatus.Pending.ToString()),
                [nameof(MotionDqCaseRequirementEntity.AuthoritativeEvaluationId)] = ObservationValue.FromString(""),
                [nameof(MotionDqCaseRequirementEntity.ObservedEvaluationIds)] = ObservationValue.FromArray([]),
                [nameof(MotionDqCaseRequirementEntity.Evaluations)] = ObservationValue.FromArray([])
            });

        static ObservationValue InitialSubjectState(
            MotionDqSubjectActivationInvocation activation,
            MotionDqCarrierActivationProof? requiredParentCarrierProof) =>
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(MotionDqSubjectActivationEntity.Kind)] = ObservationValue.FromString(activation.Subject.Kind.ToString()),
                [nameof(MotionDqSubjectActivationEntity.ActivationGateId)] = ObservationValue.FromString(activation.Admission.GateId),
                [nameof(MotionDqSubjectActivationEntity.Status)] = ObservationValue.FromString(MotionDqActivationStatus.Pending.ToString()),
                [nameof(MotionDqSubjectActivationEntity.LastActivationDecisionId)] = ObservationValue.FromString(""),
                [nameof(MotionDqSubjectActivationEntity.RequiredParentCarrierProof)] =
                    requiredParentCarrierProof is null
                        ? ObservationValue.Null
                        : ObservationValue.FromObject(requiredParentCarrierProof),
                [nameof(MotionDqSubjectActivationEntity.AdmittedParentCarrierProof)] = ObservationValue.Null
            });

        static ObservationValue RequiredField(ObservationValue state, string name)
        {
            Assert.True(state.TryGetProperty(name, out var value));
            return value;
        }

        static void AssertStateMapEquals(
            Dictionary<ObservationValue, ObservationValue> expected,
            Dictionary<ObservationValue, ObservationValue> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            foreach (var (subject, state) in expected)
            {
                Assert.True(actual.TryGetValue(subject, out var actualState));
                Assert.Equal(state, actualState);
            }
        }

        static DocumentValidationDiagnostic Failure(string code, string message) => new(
            code,
            DiagnosticSeverity.Error,
            message);
    }

    sealed class ScenarioClock(DateTimeOffset initial)
    {
        DateTimeOffset current = initial;

        internal DateTimeOffset Peek => current;

        internal DateTimeOffset Next()
        {
            current = current.AddSeconds(1);
            return current;
        }

        internal DateTimeOffset AdvanceTo(DateTimeOffset value)
        {
            if (value > current)
                current = value;

            return Next();
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
