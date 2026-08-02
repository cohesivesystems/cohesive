using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;
using Cohesive.Tests.ExecutionKernel;

namespace Cohesive.Tests.Storage;

public sealed partial class MaterializationRebuildProcessConformanceTests
{
    [Fact]
    public async Task ReadyBarrier_ContextualValidationRejectsStaleChildLineageAndStructuralSubset()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync();
        var snapshot = await fixture.AdvanceToPendingPromotionAsync(StartedAtUtc.AddMinutes(1));
        var leaf = Assert.Single(PlanSetProjection.ProjectBuildLeaves(
            fixture.PlanSet,
            snapshot.Checkpoint,
            out var allReady));
        var ready = Assert.IsType<MaterializationReadyGenerationReference>(leaf.Ready);
        Assert.True(allReady);

        var exact = MaterializationRebuildReadyBarrier.Create(
            fixture.PlanSet,
            snapshot.Checkpoint.ContinuationIdentity,
            [ready]);
        exact.ValidateAgainst(fixture.PlanSet, fixture.Artifacts.ParentPlan, snapshot.Checkpoint);

        var staleReady = MaterializationRebuildPlanSetReceiptTests.CreateReady(
            fixture.PlanSet,
            fixture.Materialization.Plan,
            MaterializationRebuildPlanSetReceiptTests.Child("contextual-barrier/stale-build-child"),
            "contextual-barrier/stale");
        var stale = MaterializationRebuildReadyBarrier.Create(
            fixture.PlanSet,
            snapshot.Checkpoint.ContinuationIdentity,
            [staleReady]);
        Assert.Throws<ArgumentException>(() =>
            stale.ValidateAgainst(fixture.PlanSet, fixture.Artifacts.ParentPlan, snapshot.Checkpoint));

        var structuralSubset = new MaterializationRebuildReadyBarrier(
            MaterializationRebuildReadyBarrier.CurrentSchemaVersion,
            MaterializationRebuildPlanSetReference.FromPlanSet(fixture.PlanSet),
            snapshot.Checkpoint.ContinuationIdentity,
            readyGenerations: []);
        var restoredSubset = MaterializationRebuildReadyBarrierJsonSerializer.DeserializeStructural(
            MaterializationRebuildReadyBarrierJsonSerializer.Serialize(structuralSubset));
        Assert.Empty(restoredSubset.ReadyGenerations);
        Assert.Throws<ArgumentException>(() =>
            restoredSubset.ValidateAgainst(fixture.PlanSet, fixture.Artifacts.ParentPlan, snapshot.Checkpoint));
    }

    [Fact]
    public async Task PlanSetLifecycle_RejectsForeignPlanSetBeforeParentAdmission()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync();
        ProcessContinuationIdentity continuation = new(
            new("process-instance/materialization-plan-set/foreign-lifecycle"),
            new("process-attempt/materialization-plan-set/foreign-lifecycle"));
        var foreignStart = ForeignPlanSetStart(fixture, continuation);

        var rejected = await fixture.Lifecycle.InitializeAsync(
            fixture.Context(StartedAtUtc),
            foreignStart);
        var inspection = await fixture.ParentRuntime.InspectAsync(
            fixture.Context(StartedAtUtc),
            fixture.Artifacts.ParentPlan,
            continuation);

        Assert.Null(rejected.ProcessDisposition);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Rejected, rejected.Realization);
        Assert.Contains(
            rejected.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.PlanSetInexact);
        Assert.Equal(ProcessDurableRuntimeDisposition.NotFound, inspection.Disposition);
    }

    [Fact]
    public async Task PlanSetLifecycle_RejectsForeignControlAndCancellationBeforeMutatingParent()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync();
        ProcessContinuationIdentity continuation = new(
            new("process-instance/materialization-plan-set/foreign-control"),
            new("process-attempt/materialization-plan-set/foreign-control"));
        var foreignStart = ForeignPlanSetStart(fixture, continuation);
        var initialized = await fixture.ParentRuntime.InitializeAsync(
            fixture.Context(StartedAtUtc),
            fixture.Artifacts.ParentPlan,
            foreignStart);
        var original = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var commands = ProcessControlTestFixture.Create();

        var paused = await fixture.Lifecycle.ApplyControlAsync(
            fixture.Context(StartedAtUtc.AddMinutes(1)),
            commands.Pause(
                original.Checkpoint.Control,
                id: "pause/materialization-plan-set/foreign-control",
                issuedAtUtc: StartedAtUtc.AddMinutes(1)));
        var cancelled = await fixture.Lifecycle.CancelAsync(
            fixture.Context(StartedAtUtc.AddMinutes(2)),
            commands.Cancel(
                original.Checkpoint.Control,
                id: "cancel/materialization-plan-set/foreign-control",
                issuedAtUtc: StartedAtUtc.AddMinutes(2)),
            fixture.ActivationContext());
        var after = Assert.IsType<ProcessDurableStoreSnapshot>((await fixture.ParentRuntime.InspectAsync(
            fixture.Context(StartedAtUtc.AddMinutes(2)),
            fixture.Artifacts.ParentPlan,
            continuation)).Snapshot);

        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Rejected, paused.Realization);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Rejected, cancelled.Realization);
        Assert.Null(paused.ProcessDisposition);
        Assert.Null(cancelled.ProcessDisposition);
        Assert.Equal(original.Revision, after.Revision);
        Assert.Equal(original.Checkpoint.Control, after.Checkpoint.Control);
    }

    [Fact]
    public async Task PlanSetLifecycle_PauseAndContinuePreserveExactAttemptChildrenAndGenerationWithoutResolverIo()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync();
        var controls = ProcessControlTestFixture.Create();
        var original = fixture.Snapshot;
        var resolverCalls = fixture.LifecycleResolver.CallCount;

        var paused = await fixture.Lifecycle.ApplyControlAsync(
            fixture.Context(StartedAtUtc.AddMinutes(2)),
            controls.Pause(
                original.Checkpoint.Control,
                id: "pause/materialization-plan-set/lifecycle",
                issuedAtUtc: StartedAtUtc.AddMinutes(2)));
        var pausedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(paused.Snapshot);
        var continued = await fixture.Lifecycle.ApplyControlAsync(
            fixture.Context(StartedAtUtc.AddMinutes(3)),
            controls.Continue(
                pausedSnapshot.Checkpoint.Control,
                id: "continue/materialization-plan-set/lifecycle",
                issuedAtUtc: StartedAtUtc.AddMinutes(3)));
        var continuedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(continued.Snapshot);

        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Preserved, paused.Realization);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Preserved, continued.Realization);
        Assert.Equal(original.Checkpoint.ContinuationIdentity, continuedSnapshot.Checkpoint.ContinuationIdentity);
        Assert.Equal(original.Checkpoint.Continuation.Children, continuedSnapshot.Checkpoint.Continuation.Children);
        Assert.Equal(
            original.Checkpoint.DurableOperations.Select(ProcessStorageContentFingerprints.Value),
            continuedSnapshot.Checkpoint.DurableOperations.Select(ProcessStorageContentFingerprints.Value));
        Assert.Equal(resolverCalls, fixture.LifecycleResolver.CallCount);
        var generation = fixture.ExecutionResolver.Single.Generation;
        Assert.Equal(
            MaterializationGenerationState.Validated,
            Assert.IsType<MaterializationGenerationSnapshot>(await fixture.Materialization.Target.InspectGenerationAsync(
                fixture.Context(StartedAtUtc.AddMinutes(3)),
                generation)).State);
    }

    [Fact]
    public async Task PlanSetLifecycle_RestartClosesOldLeafCandidateAndReplaysBeforeReplacementActivation()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync();
        var restartAtUtc = StartedAtUtc.AddMinutes(2);
        var command = ProcessControlTestFixture.Create().Restart(
            fixture.Snapshot.Checkpoint.Control,
            newAttemptId: "process-attempt/materialization-plan-set/2",
            id: "restart/materialization-plan-set/lifecycle",
            issuedAtUtc: restartAtUtc);

        var restarted = await fixture.Lifecycle.ApplyControlAsync(fixture.Context(restartAtUtc), command);
        var replayed = await fixture.Lifecycle.ApplyControlAsync(
            fixture.Context(restartAtUtc.AddSeconds(1)),
            command);

        Assert.True(
            restarted.ProcessDisposition == ProcessDurableRuntimeDisposition.Applied,
            $"Disposition={restarted.ProcessDisposition}; diagnostics={string.Join(" | ", restarted.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}");
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replayed.ProcessDisposition);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, restarted.Realization);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, replayed.Realization);
        var firstClosure = Assert.Single(restarted.Leaves);
        Assert.Equal(MaterializationRebuildPlanSetLeafClosureDisposition.CandidateAbandoned, firstClosure.Disposition);
        Assert.Equal(firstClosure, Assert.Single(replayed.Leaves));
        Assert.Equal(restartAtUtc, firstClosure.ClosedAtUtc);
        Assert.Equal(fixture.ExecutionResolver.Single.Generation, firstClosure.Generation);
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, firstClosure.ChildTerminal);
        var retired = Assert.IsType<MaterializationGenerationSnapshot>(
            await fixture.Materialization.Target.InspectGenerationAsync(
                fixture.Context(restartAtUtc.AddSeconds(1)),
                firstClosure.Generation!.Value));
        Assert.Equal(MaterializationGenerationState.Retired, retired.State);

        var replacement = Assert.IsType<ProcessDurableStoreSnapshot>(replayed.Snapshot)
            .Checkpoint.ContinuationIdentity;
        Assert.Equal(new ProcessAttemptId("process-attempt/materialization-plan-set/2"), replacement.ProcessAttemptId);
        var activated = await fixture.Lifecycle.ActivateAsync(
            fixture.Context(restartAtUtc.AddSeconds(2)),
            replacement,
            fixture.Activation(
                replacement,
                "activation/materialization-plan-set/replacement/1",
                ProcessActivationCause.Start,
                restartAtUtc.AddSeconds(2)));

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, activated.ProcessDisposition);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Ready, activated.Realization);
        Assert.Contains(
            Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot).Checkpoint.DurableOperations,
            operation => operation.Request.Contract == fixture.Artifacts.InitializationRequest
                && operation.Request.Context.Origin is ProcessInteractionOrigin origin
                && origin.Continuation == replacement);
    }

    [Theory]
    [InlineData("continuation")]
    [InlineData("owner")]
    [InlineData("occurrence")]
    [InlineData("progress")]
    public async Task PlanSetLifecycle_RestartedCheckpointRejectsForgedRetainedChildTarget(string mutation)
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync(advanceLeaf: false);
        var restartAtUtc = StartedAtUtc.AddMinutes(2);
        var restarted = await fixture.Lifecycle.ApplyControlAsync(
            fixture.Context(restartAtUtc),
            ProcessControlTestFixture.Create().Restart(
                fixture.Snapshot.Checkpoint.Control,
                newAttemptId: "process-attempt/materialization-plan-set/forged-child/2",
                id: "restart/materialization-plan-set/forged-child",
                issuedAtUtc: restartAtUtc));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(restarted.Snapshot).Checkpoint;
        var operation = Assert.Single(checkpoint.DurableOperations, candidate =>
            candidate.Request.Contract == fixture.Artifacts.LeafInvocationRequest);
        var target = Assert.IsType<ProcessChildRequestTarget>(operation.Request.ChildTarget);
        ProcessChildRequestTarget forgedTarget = new(
            definition: target.Definition,
            continuation: mutation == "continuation"
                ? new(
                    new("process-instance/materialization-plan-set/foreign-child"),
                    new("process-attempt/materialization-plan-set/foreign-child/1"))
                : target.Continuation,
            outcomeMapping: target.OutcomeMapping,
            ownerToken: mutation == "owner" ? new("token/foreign-child-owner") : target.OwnerToken,
            occurrence: mutation == "occurrence" ? target.Occurrence + 1 : target.Occurrence,
            progressIdentity: mutation == "progress" ? "progress/foreign-child" : target.ProgressIdentity);
        RequestEnvelope forgedRequest = new(
            operation.Request.SchemaVersion,
            operation.Request.Context,
            operation.Request.Contract,
            operation.Request.Payload,
            operation.Request.ResponseTarget,
            forgedTarget);
        DurableOperationState forgedOperation = new(
            operation.SchemaVersion,
            forgedRequest,
            operation.Binding,
            operation.CreatedAtUtc,
            operation.Attempts,
            operation.Reconciliations,
            operation.RecoveryRequirement,
            operation.Acknowledgement,
            operation.Admission);
        var forgedEmissions = checkpoint.Emissions.Select(record =>
            record.EmissionId == operation.OperationId
                ? new ProcessEmissionRecord(
                    forgedRequest,
                    record.EnqueuedAtUtc,
                    record.Attempts,
                    record.Publication)
                : record).ToImmutableArray();
        var forgedCheckpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            checkpoint,
            emissions: forgedEmissions,
            durableOperations: checkpoint.DurableOperations
                .Select(candidate => candidate.OperationId == operation.OperationId
                    ? forgedOperation
                    : candidate)
                .ToImmutableArray());

        var validation = ProcessCheckpointCompatibilityValidator.Validate(
            fixture.Artifacts.ParentPlan,
            forgedCheckpoint);

        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
                && diagnostic.Location?.EndsWith("/envelope/childTarget", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task PlanSetLifecycle_UnresolvedRestartCleanupGatesReplacementUntilCommittedReplayConverges()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync();
        fixture.LifecycleResolver.Enabled = false;
        var restartAtUtc = StartedAtUtc.AddMinutes(2);
        var command = ProcessControlTestFixture.Create().Restart(
            fixture.Snapshot.Checkpoint.Control,
            newAttemptId: "process-attempt/materialization-plan-set/gated",
            id: "restart/materialization-plan-set/gated",
            issuedAtUtc: restartAtUtc);

        var unresolved = await fixture.Lifecycle.ApplyControlAsync(fixture.Context(restartAtUtc), command);
        var replacementSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(unresolved.Snapshot);
        var replacement = replacementSnapshot.Checkpoint.ContinuationIdentity;
        var gated = await fixture.Lifecycle.ActivateAsync(
            fixture.Context(restartAtUtc.AddSeconds(1)),
            replacement,
            fixture.Activation(
                replacement,
                "activation/materialization-plan-set/gated/1",
                ProcessActivationCause.Start,
                restartAtUtc.AddSeconds(1)));

        Assert.True(
            unresolved.ProcessDisposition == ProcessDurableRuntimeDisposition.Applied,
            $"Disposition={unresolved.ProcessDisposition}; diagnostics={string.Join(" | ", unresolved.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}");
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Unresolved, unresolved.Realization);
        Assert.Null(gated.ProcessDisposition);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Unresolved, gated.Realization);
        Assert.Equal(0, replacementSnapshot.Checkpoint.Continuation.CompletedActivationCount);
        Assert.Contains(
            gated.Diagnostics,
            diagnostic => diagnostic.Code
                == MaterializationRebuildPlanSetProcessLifecycleDiagnosticCodes.ReplacementCleanupPending);

        fixture.LifecycleResolver.Enabled = true;
        var recovered = await fixture.Lifecycle.ApplyControlAsync(
            fixture.Context(restartAtUtc.AddSeconds(2)),
            command);
        var activated = await fixture.Lifecycle.ActivateAsync(
            fixture.Context(restartAtUtc.AddSeconds(3)),
            replacement,
            fixture.Activation(
                replacement,
                "activation/materialization-plan-set/gated/1",
                ProcessActivationCause.Start,
                restartAtUtc.AddSeconds(3)));

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, recovered.ProcessDisposition);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, recovered.Realization);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, activated.ProcessDisposition);
    }

    [Fact]
    public async Task PlanSetLifecycle_CancelClosesEveryLeafCandidateAndReplaysStableClosureEvidence()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync();
        var cancelledAtUtc = StartedAtUtc.AddMinutes(2);
        var command = ProcessControlTestFixture.Create().Cancel(
            fixture.Snapshot.Checkpoint.Control,
            id: "cancel/materialization-plan-set/lifecycle",
            issuedAtUtc: cancelledAtUtc);
        var activationContext = fixture.ActivationContext();

        var cancelled = await fixture.Lifecycle.CancelAsync(
            fixture.Context(cancelledAtUtc),
            command,
            activationContext);
        var replayed = await fixture.Lifecycle.CancelAsync(
            fixture.Context(cancelledAtUtc.AddSeconds(1)),
            command,
            activationContext);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, cancelled.ProcessDisposition);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replayed.ProcessDisposition);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, cancelled.Realization);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, replayed.Realization);
        var closure = Assert.Single(cancelled.Leaves);
        Assert.Equal(closure, Assert.Single(replayed.Leaves));
        Assert.Equal(MaterializationRebuildPlanSetLeafClosureDisposition.CandidateAbandoned, closure.Disposition);
        Assert.Equal(cancelledAtUtc, closure.ClosedAtUtc);
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(cancelled.Snapshot);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, snapshot.Checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(ProcessControlMode.Cancelled, snapshot.Checkpoint.Control.Mode);
    }

    [Fact]
    public async Task PlanSetLifecycle_RestartPreservesAnAlreadyPromotedActiveRoute()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync();
        var activationContext = fixture.Context(StartedAtUtc);
        var leafOperation = fixture.Snapshot.Checkpoint.DurableOperations.Single(
            operation => operation.Request.Contract == fixture.Artifacts.LeafInvocationRequest);
        var leafOutcome = Assert.IsType<RequestResultOutcome>(leafOperation.Acknowledgement!.Outcome);
        var ready = MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(
            RequireString(leafOutcome.Value));
        var activation = await fixture.ExecutionResolver.Single.ActivateReadyAsync(activationContext, ready);
        Assert.Equal(MaterializationGenerationActivationDisposition.Active, activation.Disposition);
        var promotionReceipt = Assert.IsType<MaterializationPromotionReceipt>(activation.Activation!.PromotionReceipt);
        var active = new MaterializationActiveGenerationReference(
            MaterializationActiveGenerationReference.CurrentSchemaVersion,
            ready.Authority,
            activation.Generation,
            promotionReceipt.TargetRevision,
            promotionReceipt.PromotionId,
            promotionReceipt.PromotionFence,
            promotionReceipt.ValidationFingerprint,
            promotionReceipt.PromotedAtUtc);
        var promotion = new MaterializationIndependentPromotionExecutor(fixture.PlanSet, ready.Authority);
        var context = fixture.Context(StartedAtUtc.AddMinutes(1));
        var routingBefore = await fixture.Router.InspectAsync(context, ready.PlacementSlice);
        var promoted = await promotion.ExecuteAsync(
            context,
            promotion.CreateRequest(
                active,
                routingBefore,
                new("1"),
                StartedAtUtc.AddMinutes(1)),
            fixture.Router);
        Assert.True(promoted.IsCurrentlySelected);

        var restartAtUtc = StartedAtUtc.AddMinutes(2);
        var command = ProcessControlTestFixture.Create().Restart(
            fixture.Snapshot.Checkpoint.Control,
            newAttemptId: "process-attempt/materialization-plan-set/preserve-active",
            id: "restart/materialization-plan-set/preserve-active",
            issuedAtUtc: restartAtUtc);
        var restarted = await fixture.Lifecycle.ApplyControlAsync(fixture.Context(restartAtUtc), command);

        var closure = Assert.Single(restarted.Leaves);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, restarted.Realization);
        Assert.Equal(MaterializationRebuildPlanSetLeafClosureDisposition.ActiveRoutePreserved, closure.Disposition);
        var read = await fixture.Router.ResolveReadAsync(fixture.Context(restartAtUtc), ready.PlacementSlice);
        var write = await fixture.Router.ResolveWriteAsync(fixture.Context(restartAtUtc), ready.PlacementSlice);
        Assert.Equal(activation.Generation, read.Generation.GenerationId);
        Assert.Equal(read.Generation, write.Generation);
        Assert.Equal(
            MaterializationGenerationState.Active,
            Assert.IsType<MaterializationGenerationSnapshot>(await fixture.Materialization.Target.InspectGenerationAsync(
                fixture.Context(restartAtUtc),
                activation.Generation)).State);
    }

    [Fact]
    public async Task PlanSetLifecycle_RestartPreservesActiveUnroutedTargetAndReplacementSupersedesIt()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync();
        var leafOperation = fixture.Snapshot.Checkpoint.DurableOperations.Single(
            operation => operation.Request.Contract == fixture.Artifacts.LeafInvocationRequest);
        var leafOutcome = Assert.IsType<RequestResultOutcome>(leafOperation.Acknowledgement!.Outcome);
        var ready = MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(
            RequireString(leafOutcome.Value));
        var oldGeneration = fixture.ExecutionResolver.Single.Generation;
        var targetActivation = await fixture.ExecutionResolver.Single.ActivateReadyAsync(
            fixture.Context(StartedAtUtc),
            ready);
        Assert.Equal(MaterializationGenerationActivationDisposition.Active, targetActivation.Disposition);
        var pending = await fixture.AdvanceToPendingPromotionAsync(StartedAtUtc.AddMinutes(1));
        _ = Assert.Single(pending.Checkpoint.DurableOperations, operation =>
            operation.Request.Contract == fixture.Artifacts.PromotionInvocationRequest
            && operation.Status == DurableOperationStatus.Pending);
        Assert.Equal(
            MaterializationGenerationState.Active,
            Assert.IsType<MaterializationGenerationSnapshot>(await fixture.Materialization.Target.InspectGenerationAsync(
                fixture.Context(StartedAtUtc.AddMinutes(1)),
                oldGeneration)).State);
        var unrouted = await fixture.Router.InspectAsync(
            fixture.Context(StartedAtUtc.AddMinutes(1)),
            fixture.PlanSet.Placement.Slices[0]);
        Assert.Null(unrouted.ActiveRead);
        Assert.Null(unrouted.ActiveWrite);

        var restartAtUtc = StartedAtUtc.AddMinutes(2);
        var command = ProcessControlTestFixture.Create().Restart(
            pending.Checkpoint.Control,
            newAttemptId: "process-attempt/materialization-plan-set/active-target-replacement",
            id: "restart/materialization-plan-set/active-target-replacement",
            issuedAtUtc: restartAtUtc);
        var restarted = await fixture.Lifecycle.ApplyControlAsync(fixture.Context(restartAtUtc), command);
        var replayed = await fixture.Lifecycle.ApplyControlAsync(
            fixture.Context(restartAtUtc.AddSeconds(1)),
            command);

        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, restarted.Realization);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, replayed.Realization);
        var closure = Assert.Single(restarted.Leaves);
        Assert.Equal(closure, Assert.Single(replayed.Leaves));
        Assert.Equal(MaterializationRebuildPlanSetLeafClosureDisposition.ActiveTargetPreserved, closure.Disposition);
        Assert.Null(closure.PromotionTerminal);
        Assert.Equal(oldGeneration, closure.Generation);

        var replacement = Assert.IsType<ProcessDurableStoreSnapshot>(restarted.Snapshot)
            .Checkpoint.ContinuationIdentity;
        var replacementSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>((await fixture.Lifecycle.ActivateAsync(
            fixture.Context(restartAtUtc.AddSeconds(2)),
            replacement,
            fixture.Activation(
                replacement,
                "activation/materialization-plan-set/active-target-replacement/1",
                ProcessActivationCause.Start,
                restartAtUtc.AddSeconds(2)))).Snapshot);
        replacementSnapshot = await AdvanceCurrentOperationAsync(
            fixture.ParentRuntime,
            fixture.Context(restartAtUtc.AddSeconds(2)),
            fixture.Artifacts,
            replacement,
            replacementSnapshot);
        replacementSnapshot = await ActivatePlanSetAsync(
            fixture.ParentRuntime,
            fixture.Context(restartAtUtc.AddSeconds(3)),
            fixture.Artifacts,
            replacement,
            replacementSnapshot,
            ProcessActivationCause.Interaction);
        replacementSnapshot = await AdvanceCurrentOperationAsync(
            fixture.ParentRuntime,
            fixture.Context(restartAtUtc.AddSeconds(3)),
            fixture.Artifacts,
            replacement,
            replacementSnapshot);
        var replacementLeaf = Assert.Single(replacementSnapshot.Checkpoint.DurableOperations, operation =>
            operation.Request.Contract == fixture.Artifacts.LeafInvocationRequest
            && operation.Request.Context.Origin is ProcessInteractionOrigin origin
            && origin.Continuation == replacement);
        var replacementChild = replacementLeaf.Request.ChildTarget!.Continuation;
        Assert.True(fixture.ExecutionResolver.TryResolve(
            closure.Authority,
            replacementChild,
            out var replacementExecution));
        Assert.NotNull(replacementExecution);
        Assert.NotEqual(oldGeneration, replacementExecution.Generation);
        Assert.Equal(
            MaterializationGenerationState.Validated,
            Assert.IsType<MaterializationGenerationSnapshot>(await fixture.Materialization.Target.InspectGenerationAsync(
                fixture.Context(restartAtUtc.AddSeconds(3)),
                replacementExecution.Generation)).State);

        replacementSnapshot = await ActivatePlanSetAsync(
            fixture.ParentRuntime,
            fixture.Context(restartAtUtc.AddSeconds(4)),
            fixture.Artifacts,
            replacement,
            replacementSnapshot,
            ProcessActivationCause.Interaction);
        replacementSnapshot = await AdvanceCurrentOperationAsync(
            fixture.ParentRuntime,
            fixture.Context(restartAtUtc.AddSeconds(4)),
            fixture.Artifacts,
            replacement,
            replacementSnapshot);
        replacementSnapshot = await ActivatePlanSetAsync(
            fixture.ParentRuntime,
            fixture.Context(restartAtUtc.AddSeconds(5)),
            fixture.Artifacts,
            replacement,
            replacementSnapshot,
            ProcessActivationCause.Interaction);
        _ = await AdvanceCurrentOperationAsync(
            fixture.ParentRuntime,
            fixture.Context(restartAtUtc.AddSeconds(5)),
            fixture.Artifacts,
            replacement,
            replacementSnapshot);
        var routedReplacement = await fixture.Router.ResolveReadAsync(
            fixture.Context(restartAtUtc.AddSeconds(5)),
            fixture.PlanSet.Placement.Slices[0]);
        Assert.Equal(replacementExecution.Generation, routedReplacement.Generation.GenerationId);
        Assert.Equal(
            MaterializationGenerationState.Inactive,
            Assert.IsType<MaterializationGenerationSnapshot>(await fixture.Materialization.Target.InspectGenerationAsync(
                fixture.Context(restartAtUtc.AddSeconds(5)),
                oldGeneration)).State);
    }

    [Fact]
    public async Task PlanSetLifecycle_RestartTombstonesDispatchedPromotionBeforeDelayedChildStart()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync(delayPromotionInvocation: true);
        var pending = await fixture.AdvanceToPendingPromotionAsync(StartedAtUtc.AddMinutes(1));
        var operation = Assert.Single(pending.Checkpoint.DurableOperations, operation =>
            operation.Request.Contract == fixture.Artifacts.PromotionInvocationRequest
            && operation.Status == DurableOperationStatus.Pending);
        var delay = Assert.IsType<DelayedOperationAdapter>(fixture.PromotionDelay);
        var advanceTask = fixture.ParentRuntime.AdvanceOperationAsync(
            fixture.Context(StartedAtUtc.AddMinutes(2)),
            fixture.Artifacts.ParentPlan,
            pending.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            operation.OperationId);
        await delay.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var dispatched = Assert.IsType<ProcessDurableStoreSnapshot>((await fixture.ParentRuntime.InspectAsync(
            fixture.Context(StartedAtUtc.AddMinutes(2)),
            fixture.Artifacts.ParentPlan,
            pending.Checkpoint.ContinuationIdentity)).Snapshot);
        Assert.Equal(
            DurableOperationStatus.Dispatched,
            dispatched.Checkpoint.DurableOperations.Single(candidate => candidate.OperationId == operation.OperationId).Status);

        var restartAtUtc = StartedAtUtc.AddMinutes(10);
        var command = ProcessControlTestFixture.Create().Restart(
            dispatched.Checkpoint.Control,
            newAttemptId: "process-attempt/materialization-plan-set/dispatched-promotion",
            id: "restart/materialization-plan-set/dispatched-promotion",
            issuedAtUtc: restartAtUtc);
        var restarted = await fixture.Lifecycle.ApplyControlAsync(fixture.Context(restartAtUtc), command);
        var childBeforeRelease = await fixture.PromotionRuntime.InspectAsync(
            fixture.Context(restartAtUtc),
            fixture.Artifacts.PromotionWorkerPlan,
            operation.Request.ChildTarget!.Continuation);

        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, restarted.Realization);
        var closure = Assert.Single(restarted.Leaves);
        Assert.Equal(
            MaterializationRebuildPlanSetLeafClosureDisposition.CandidateAbandoned,
            closure.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, closure.PromotionTerminal);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, childBeforeRelease.Disposition);
        var tombstone = Assert.IsType<ProcessDurableStoreSnapshot>(childBeforeRelease.Snapshot);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, tombstone.Checkpoint.Continuation.Terminal.Kind);
        Assert.Empty(tombstone.Checkpoint.DurableOperations);

        delay.Release();
        _ = await advanceTask;
        var childAfterRelease = await fixture.PromotionRuntime.InspectAsync(
            fixture.Context(restartAtUtc),
            fixture.Artifacts.PromotionWorkerPlan,
            operation.Request.ChildTarget.Continuation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, childAfterRelease.Disposition);
        Assert.Equal(
            tombstone.Checkpoint.Continuation,
            Assert.IsType<ProcessDurableStoreSnapshot>(childAfterRelease.Snapshot).Checkpoint.Continuation);
        var converged = await fixture.Lifecycle.ApplyControlAsync(
            fixture.Context(restartAtUtc.AddSeconds(1)),
            command);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, converged.Realization);
        Assert.Equal(closure, Assert.Single(converged.Leaves));

        var replacement = Assert.IsType<ProcessDurableStoreSnapshot>(converged.Snapshot)
            .Checkpoint.ContinuationIdentity;
        var activated = await fixture.Lifecycle.ActivateAsync(
            fixture.Context(restartAtUtc.AddSeconds(2)),
            replacement,
            fixture.Activation(
                replacement,
                "activation/materialization-plan-set/dispatched-promotion/replacement/1",
                ProcessActivationCause.Start,
                restartAtUtc.AddSeconds(2)));
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, activated.ProcessDisposition);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Ready, activated.Realization);
    }

    [Fact]
    public async Task PlanSetLifecycle_RestartTombstonesDispatchedLeafBeforeDelayedChildStart()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync(
            delayLeafInvocation: true,
            advanceLeaf: false);
        var operation = Assert.Single(fixture.Snapshot.Checkpoint.DurableOperations, operation =>
            operation.Request.Contract == fixture.Artifacts.LeafInvocationRequest);
        var delay = Assert.IsType<DelayedOperationAdapter>(fixture.LeafDelay);
        var advanceTask = fixture.ParentRuntime.AdvanceOperationAsync(
            fixture.Context(StartedAtUtc.AddMinutes(1)),
            fixture.Artifacts.ParentPlan,
            fixture.Snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            operation.OperationId);
        await delay.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var dispatched = Assert.IsType<ProcessDurableStoreSnapshot>((await fixture.ParentRuntime.InspectAsync(
            fixture.Context(StartedAtUtc.AddMinutes(1)),
            fixture.Artifacts.ParentPlan,
            fixture.Snapshot.Checkpoint.ContinuationIdentity)).Snapshot);
        Assert.Equal(
            DurableOperationStatus.Dispatched,
            dispatched.Checkpoint.DurableOperations.Single(candidate => candidate.OperationId == operation.OperationId).Status);

        var restartAtUtc = StartedAtUtc.AddMinutes(10);
        var command = ProcessControlTestFixture.Create().Restart(
            dispatched.Checkpoint.Control,
            newAttemptId: "process-attempt/materialization-plan-set/dispatched-leaf",
            id: "restart/materialization-plan-set/dispatched-leaf",
            issuedAtUtc: restartAtUtc);
        var restarted = await fixture.Lifecycle.ApplyControlAsync(fixture.Context(restartAtUtc), command);

        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, restarted.Realization);
        Assert.Equal(
            MaterializationRebuildPlanSetLeafClosureDisposition.NotStarted,
            Assert.Single(restarted.Leaves).Disposition);
        Assert.Equal(0, fixture.ExecutionResolver.Count);
        var childBeforeRelease = await fixture.LeafRuntime.InspectAsync(
            fixture.Context(restartAtUtc),
            fixture.Artifacts.Leaf.CoordinatorPlan,
            operation.Request.ChildTarget!.Continuation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, childBeforeRelease.Disposition);
        var tombstone = Assert.IsType<ProcessDurableStoreSnapshot>(childBeforeRelease.Snapshot);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, tombstone.Checkpoint.Continuation.Terminal.Kind);
        Assert.Empty(tombstone.Checkpoint.DurableOperations);

        delay.Release();
        _ = await advanceTask;
        var childAfterRelease = await fixture.LeafRuntime.InspectAsync(
            fixture.Context(restartAtUtc),
            fixture.Artifacts.Leaf.CoordinatorPlan,
            operation.Request.ChildTarget.Continuation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, childAfterRelease.Disposition);
        Assert.Equal(
            tombstone.Checkpoint.Continuation,
            Assert.IsType<ProcessDurableStoreSnapshot>(childAfterRelease.Snapshot).Checkpoint.Continuation);
        Assert.Equal(0, fixture.ExecutionResolver.Count);

        var replayed = await fixture.Lifecycle.ApplyControlAsync(
            fixture.Context(restartAtUtc.AddSeconds(1)),
            command);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, replayed.Realization);
        Assert.Equal(
            MaterializationRebuildPlanSetLeafClosureDisposition.NotStarted,
            Assert.Single(replayed.Leaves).Disposition);

        var replacement = Assert.IsType<ProcessDurableStoreSnapshot>(replayed.Snapshot)
            .Checkpoint.ContinuationIdentity;
        var activated = await fixture.Lifecycle.ActivateAsync(
            fixture.Context(restartAtUtc.AddSeconds(2)),
            replacement,
            fixture.Activation(
                replacement,
                "activation/materialization-plan-set/dispatched-leaf/replacement/1",
                ProcessActivationCause.Start,
                restartAtUtc.AddSeconds(2)));
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, activated.ProcessDisposition);
        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Ready, activated.Realization);
    }

    [Fact]
    public async Task PlanSetLifecycle_ExpiredClaimedLeafNormalizesToConclusiveNotStartedWithoutAdapterIo()
    {
        using var fixture = await PlanSetLifecycleFixture.CreateAsync(
            advanceLeaf: false,
            injectParentClaimCrash: true);
        var operation = Assert.Single(fixture.Snapshot.Checkpoint.DurableOperations, operation =>
            operation.Request.Contract == fixture.Artifacts.LeafInvocationRequest);
        Assert.Equal(DurableOperationStatus.Pending, operation.Status);
        var crash = Assert.IsType<ArmableCrashOnce>(fixture.ParentCrash);
        crash.Arm();

        var interrupted = await fixture.ParentRuntime.AdvanceOperationAsync(
            fixture.Context(StartedAtUtc.AddMinutes(1)),
            fixture.Artifacts.ParentPlan,
            fixture.Snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            operation.OperationId);
        Assert.Equal(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, interrupted.Disposition);
        var claimed = Assert.IsType<ProcessDurableStoreSnapshot>((await fixture.ParentRuntime.InspectAsync(
            fixture.Context(StartedAtUtc.AddMinutes(1)),
            fixture.Artifacts.ParentPlan,
            fixture.Snapshot.Checkpoint.ContinuationIdentity)).Snapshot);
        Assert.Equal(
            DurableOperationStatus.Claimed,
            claimed.Checkpoint.DurableOperations.Single(candidate => candidate.OperationId == operation.OperationId).Status);

        var restartAtUtc = StartedAtUtc.AddMinutes(10);
        var command = ProcessControlTestFixture.Create().Restart(
            claimed.Checkpoint.Control,
            newAttemptId: "process-attempt/materialization-plan-set/expired-claimed-leaf",
            id: "restart/materialization-plan-set/expired-claimed-leaf",
            issuedAtUtc: restartAtUtc);
        var restarted = await fixture.Lifecycle.ApplyControlAsync(fixture.Context(restartAtUtc), command);

        Assert.Equal(MaterializationRebuildPlanSetProcessRealization.Closed, restarted.Realization);
        Assert.Equal(
            MaterializationRebuildPlanSetLeafClosureDisposition.NotStarted,
            Assert.Single(restarted.Leaves).Disposition);
        Assert.Equal(0, fixture.ExecutionResolver.Count);
        var normalized = Assert.IsType<ProcessDurableStoreSnapshot>(restarted.Snapshot)
            .Checkpoint.DurableOperations.Single(candidate => candidate.OperationId == operation.OperationId);
        Assert.Equal(DurableOperationStatus.RetryEligible, normalized.Status);
        var failed = Assert.Single(normalized.Attempts);
        Assert.Equal(DurableOperationAttemptStage.Failed, failed.Stage);
        Assert.Equal(DurableOperationFailurePhase.PreCall, failed.Failure?.Phase);
        Assert.Equal(DurableOperationEffectEvidence.NotExecuted, failed.Failure?.EffectEvidence);

        var replacement = Assert.IsType<ProcessDurableStoreSnapshot>(restarted.Snapshot)
            .Checkpoint.ContinuationIdentity;
        var activated = await fixture.Lifecycle.ActivateAsync(
            fixture.Context(restartAtUtc.AddSeconds(1)),
            replacement,
            fixture.Activation(
                replacement,
                "activation/materialization-plan-set/expired-claimed-leaf/replacement/1",
                ProcessActivationCause.Start,
                restartAtUtc.AddSeconds(1)));
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, activated.ProcessDisposition);
    }

    static async Task<ProcessDurableStoreSnapshot> AdvanceCurrentOperationAsync(
        ProcessDurableRuntime runtime,
        OperationContext context,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessContinuationIdentity continuation,
        ProcessDurableStoreSnapshot snapshot)
    {
        var operation = Assert.Single(snapshot.Checkpoint.DurableOperations, operation =>
            operation.Status != DurableOperationStatus.Dispositioned
            && operation.Request.Context.Origin is ProcessInteractionOrigin origin
            && origin.Continuation == continuation);
        var advanced = await runtime.AdvanceOperationAsync(
            context,
            artifacts.ParentPlan,
            continuation.ProcessInstanceId,
            operation.OperationId);
        Assert.True(
            advanced.Disposition is ProcessDurableRuntimeDisposition.Applied
                or ProcessDurableRuntimeDisposition.Replayed,
            string.Join(" | ", advanced.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<ProcessDurableStoreSnapshot>(advanced.Snapshot);
    }

    static ProcessStartReceipt ForeignPlanSetStart(
        PlanSetLifecycleFixture fixture,
        ProcessContinuationIdentity continuation)
    {
        var exact = PlanSetStart(fixture.PlanSet, fixture.Artifacts, continuation, StartedAtUtc);
        var exactReference = MaterializationRebuildPlanSetReference.FromPlanSet(fixture.PlanSet);
        var foreignReference = new MaterializationRebuildPlanSetReference(
            MaterializationRebuildPlanSetReference.CurrentSchemaVersion,
            exactReference.Request,
            new(
                algorithm: exactReference.PlanSet.Algorithm,
                canonicalization: exactReference.PlanSet.Canonicalization,
                value: new string('f', 64)));
        return new(
            new(
                exact.Request.SchemaVersion,
                exact.Request.Definition,
                exact.Request.Context,
                exact.Request.InitialContinuation,
                PortableValue.Concrete(
                    fixture.Artifacts.ParentPlan.Definition.Input,
                    ObservationValue.FromString(
                        MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(foreignReference)))),
            exact.AcceptedAtUtc);
    }

    sealed class PlanSetLifecycleFixture : IDisposable
    {
        PlanSetLifecycleFixture(
            MaterializationFixture materialization,
            MaterializationRebuildPlanSet planSet,
            MaterializationRebuildPlanSetProcessArtifacts artifacts,
            PlanSetLeafExecutionResolver executionResolver,
            GateableExecutionResolver lifecycleResolver,
            InMemoryMaterializationBackendRouter router,
            ProcessDurableRuntime leafRuntime,
            ProcessDurableRuntime promotionRuntime,
            ProcessDurableRuntime parentRuntime,
            ProcessDurableStoreSnapshot snapshot,
            MaterializationRebuildPlanSetProcessLifecycle lifecycle,
            DelayedOperationAdapter? leafDelay,
            DelayedOperationAdapter? promotionDelay,
            ArmableCrashOnce? parentCrash)
        {
            Materialization = materialization;
            PlanSet = planSet;
            Artifacts = artifacts;
            ExecutionResolver = executionResolver;
            LifecycleResolver = lifecycleResolver;
            Router = router;
            LeafRuntime = leafRuntime;
            PromotionRuntime = promotionRuntime;
            ParentRuntime = parentRuntime;
            Snapshot = snapshot;
            Lifecycle = lifecycle;
            LeafDelay = leafDelay;
            PromotionDelay = promotionDelay;
            ParentCrash = parentCrash;
        }

        internal MaterializationFixture Materialization { get; }

        internal MaterializationRebuildPlanSet PlanSet { get; }

        internal MaterializationRebuildPlanSetProcessArtifacts Artifacts { get; }

        internal PlanSetLeafExecutionResolver ExecutionResolver { get; }

        internal GateableExecutionResolver LifecycleResolver { get; }

        internal InMemoryMaterializationBackendRouter Router { get; }

        internal ProcessDurableRuntime LeafRuntime { get; }

        internal ProcessDurableRuntime PromotionRuntime { get; }

        internal ProcessDurableRuntime ParentRuntime { get; }

        internal ProcessDurableStoreSnapshot Snapshot { get; }

        internal MaterializationRebuildPlanSetProcessLifecycle Lifecycle { get; }

        internal DelayedOperationAdapter? LeafDelay { get; }

        internal DelayedOperationAdapter? PromotionDelay { get; }

        internal ArmableCrashOnce? ParentCrash { get; }

        internal static async Task<PlanSetLifecycleFixture> CreateAsync(
            bool delayLeafInvocation = false,
            bool delayPromotionInvocation = false,
            bool advanceLeaf = true,
            bool injectParentClaimCrash = false)
        {
            var materialization = CreateMaterializationFixture();
            var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(materialization.Plan);
            var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
            var executionResolver = new PlanSetLeafExecutionResolver(materialization.Resolved, StartedAtUtc);
            var lifecycleResolver = new GateableExecutionResolver(executionResolver);
            var context = OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc));

            var workerRuntime = new ProcessDurableRuntime(
                store: new InMemoryProcessDurableStore(),
                host: RejectingHost.Instance,
                options: RuntimeOptions("worker/materialization-plan-set/lifecycle/shards"),
                bindingResolver: new ExactBindingResolver([artifacts.Leaf.ShardRebuildBinding]),
                operationAdapterResolver: new ExactAdapterResolver(
                [
                    new MaterializationRebuildShardDurableOperationAdapter(
                        request: artifacts.Leaf.ShardRebuildRequest,
                        resolver: executionResolver)
                ]));
            var leafRuntime = new ProcessDurableRuntime(
                store: new InMemoryProcessDurableStore(),
                host: RejectingHost.Instance,
                options: RuntimeOptions("worker/materialization-plan-set/lifecycle/leaves"),
                bindingResolver: new ExactBindingResolver(
                [
                    artifacts.Leaf.InitializationBinding,
                    artifacts.Leaf.WorkerInvocationBinding,
                    artifacts.Leaf.SynchronizationPreparationBinding
                ]),
                operationAdapterResolver: new ExactAdapterResolver(
                [
                    new MaterializationRebuildInitializationDurableOperationAdapter(
                        request: artifacts.Leaf.InitializationRequest,
                        resolver: executionResolver),
                    new ProcessChildDurableOperationAdapter(
                        runtime: workerRuntime,
                        planResolver: new ExactChildPlanResolver(artifacts.Leaf.WorkerPlan),
                        supportedRequests: [artifacts.Leaf.WorkerInvocationRequest]),
                    new MaterializationSynchronizationPreparationDurableOperationAdapter(
                        request: artifacts.Leaf.SynchronizationPreparationRequest,
                        resolver: executionResolver)
                ]));
            var targetPool = new InMemoryMaterializationTargetPool(
                definition: planSet.Placement.BackendPool.Definition,
                targets: [materialization.Target]);
            var router = new InMemoryMaterializationBackendRouter(
                document: planSet.Placement.BackendPool,
                targets: targetPool,
                timeProvider: new FixedTimeProvider(StartedAtUtc));
            var planSetResolver = new ExactPlanSetResolver(planSet);
            var promotionStore = new InMemoryProcessDurableStore();
            var promotionRuntime = new ProcessDurableRuntime(
                store: promotionStore,
                host: RejectingHost.Instance,
                options: RuntimeOptions("worker/materialization-plan-set/lifecycle/promotions"),
                bindingResolver: new ExactBindingResolver(
                [
                    artifacts.ActivateReadyBinding,
                    artifacts.PreparePromotionBinding,
                    artifacts.ApplyPromotionBinding
                ]),
                operationAdapterResolver: new ExactAdapterResolver(
                [
                    new MaterializationReadyGenerationActivationDurableOperationAdapter(
                        request: artifacts.ActivateReadyRequest,
                        resolver: executionResolver,
                        promotionWorkerPlan: artifacts.PromotionWorkerPlan),
                    new MaterializationIndependentPromotionPreparationDurableOperationAdapter(
                        request: artifacts.PreparePromotionRequest,
                        resolver: planSetResolver,
                        router,
                        store: promotionStore,
                        promotionPlan: artifacts.PromotionWorkerPlan),
                    new MaterializationIndependentPromotionDurableOperationAdapter(
                        request: artifacts.ApplyPromotionRequest,
                        resolver: planSetResolver,
                        router,
                        promotionPlan: artifacts.PromotionWorkerPlan)
                ]));
            var promotionChildAdapter = new ProcessChildDurableOperationAdapter(
                runtime: promotionRuntime,
                planResolver: new ExactChildPlanResolver(artifacts.PromotionWorkerPlan),
                supportedRequests: [artifacts.PromotionInvocationRequest]);
            var promotionDelay = delayPromotionInvocation
                ? new DelayedOperationAdapter(promotionChildAdapter)
                : null;
            var leafChildAdapter = new ProcessChildDurableOperationAdapter(
                runtime: leafRuntime,
                planResolver: new ExactChildPlanResolver(artifacts.Leaf.CoordinatorPlan),
                supportedRequests: [artifacts.LeafInvocationRequest]);
            var leafDelay = delayLeafInvocation
                ? new DelayedOperationAdapter(leafChildAdapter)
                : null;
            var parentCrash = injectParentClaimCrash ? new ArmableCrashOnce() : null;
            var parentStore = parentCrash is null
                ? new InMemoryProcessDurableStore()
                : new InMemoryProcessDurableStore(parentCrash.ShouldCrash);
            var parentRuntime = new ProcessDurableRuntime(
                store: parentStore,
                host: RejectingHost.Instance,
                options: RuntimeOptions(
                    "worker/materialization-plan-set/lifecycle/parent",
                    maxAmbiguousStoreMutationAttempts: injectParentClaimCrash ? 1 : 3),
                bindingResolver: new ExactBindingResolver(
                [
                    artifacts.InitializationBinding,
                    artifacts.LeafInvocationBinding,
                    artifacts.ReadinessBarrierBinding,
                    artifacts.PromotionInvocationBinding
                ]),
                operationAdapterResolver: new ExactAdapterResolver(
                [
                    new MaterializationRebuildPlanSetInitializationDurableOperationAdapter(
                        request: artifacts.InitializationRequest,
                        resolver: planSetResolver,
                        parentPlan: artifacts.ParentPlan),
                    (IDurableOperationAdapter?)leafDelay ?? leafChildAdapter,
                    new MaterializationRebuildReadyBarrierDurableOperationAdapter(
                        request: artifacts.ReadinessBarrierRequest,
                        resolver: planSetResolver,
                        store: parentStore,
                        parentPlan: artifacts.ParentPlan),
                    (IDurableOperationAdapter?)promotionDelay ?? promotionChildAdapter
                ]));
            var lifecycle = new MaterializationRebuildPlanSetProcessLifecycle(
                parentRuntime,
                leafRuntime,
                promotionRuntime,
                artifacts,
                planSet,
                lifecycleResolver,
                router);
            ProcessContinuationIdentity continuation = new(
                new("process-instance/materialization-plan-set/lifecycle"),
                new("process-attempt/materialization-plan-set/1"));
            var initialized = await lifecycle.InitializeAsync(
                context,
                PlanSetStart(planSet, artifacts, continuation, StartedAtUtc));
            var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
            snapshot = await ActivatePlanSetAsync(
                parentRuntime,
                context,
                artifacts,
                continuation,
                snapshot,
                ProcessActivationCause.Start);
            snapshot = await AdvanceOnlyOperationAsync(
                parentRuntime,
                context,
                artifacts,
                continuation,
                snapshot);
            snapshot = await ActivatePlanSetAsync(
                parentRuntime,
                context,
                artifacts,
                continuation,
                snapshot,
                ProcessActivationCause.Interaction);
            if (advanceLeaf)
            {
                snapshot = await AdvanceOnlyOperationAsync(
                    parentRuntime,
                    context,
                    artifacts,
                    continuation,
                    snapshot);
                Assert.Equal(DurableOperationStatus.Dispositioned, snapshot.Checkpoint.DurableOperations.Single(
                    operation => operation.Request.Contract == artifacts.LeafInvocationRequest).Status);
            }
            else
            {
                Assert.Equal(DurableOperationStatus.Pending, snapshot.Checkpoint.DurableOperations.Single(
                    operation => operation.Request.Contract == artifacts.LeafInvocationRequest).Status);
            }
            var compatibility = ProcessCheckpointCompatibilityValidator.Validate(artifacts.ParentPlan, snapshot.Checkpoint);
            Assert.True(
                compatibility.IsValid,
                string.Join(" | ", compatibility.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

            return new(
                materialization,
                planSet,
                artifacts,
                executionResolver,
                lifecycleResolver,
                router,
                leafRuntime,
                promotionRuntime,
                parentRuntime,
                snapshot,
                lifecycle,
                leafDelay,
                promotionDelay,
                parentCrash);
        }

        internal async Task<ProcessDurableStoreSnapshot> AdvanceToPendingPromotionAsync(DateTimeOffset observedAtUtc)
        {
            var continuation = Snapshot.Checkpoint.ContinuationIdentity;
            var context = Context(observedAtUtc);
            var snapshot = await ActivatePlanSetAsync(
                ParentRuntime,
                context,
                Artifacts,
                continuation,
                Snapshot,
                ProcessActivationCause.Interaction);
            snapshot = await AdvanceOnlyOperationAsync(
                ParentRuntime,
                context,
                Artifacts,
                continuation,
                snapshot);
            snapshot = await ActivatePlanSetAsync(
                ParentRuntime,
                context,
                Artifacts,
                continuation,
                snapshot,
                ProcessActivationCause.Interaction);
            Assert.Contains(snapshot.Checkpoint.DurableOperations, operation =>
                operation.Request.Contract == Artifacts.PromotionInvocationRequest
                && operation.Status == DurableOperationStatus.Pending);
            return snapshot;
        }

        internal OperationContext Context(DateTimeOffset utcNow) =>
            OperationContext.Create(timeProvider: new FixedTimeProvider(utcNow));

        internal ProcessActivationContext ActivationContext() =>
            new(
                Authority,
                new("correlation/materialization-plan-set/lifecycle"),
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                Artifacts.ParentProcessDocument.Metadata.Provenance);

        internal ProcessActivation Activation(
            ProcessContinuationIdentity continuation,
            string id,
            ProcessActivationCause cause,
            DateTimeOffset observedAtUtc) =>
            new(
                new(id),
                cause,
                observedAtUtc,
                ActivationContext(),
                inputs: []);

        public void Dispose() => Router.Dispose();
    }

    sealed class DelayedOperationAdapter(IDurableOperationAdapter inner) : IDurableOperationAdapter
    {
        readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => entered.Task;

        public DurableOperationAdapterCapabilities Capabilities => inner.Capabilities;

        internal void Release() => release.TrySetResult();

        public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(context.CancellationToken);
            return await inner.ExecuteAsync(context, invocation);
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            inner.ReconcileAsync(context, request);
    }

    sealed class ArmableCrashOnce
    {
        bool armed;
        bool crashed;

        internal void Arm() => armed = true;

        internal bool Crashed => crashed;

        internal bool ShouldCrash(ProcessStoreCrashContext context)
        {
            if (!armed
                || crashed
                || context.MutationKind != ProcessStoreMutationKind.AggregateCommit
                || context.Phase != ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn)
            {
                return false;
            }
            crashed = true;
            return true;
        }
    }

    sealed class GateableExecutionResolver(IMaterializationRebuildExecutionResolver inner)
        : IMaterializationRebuildExecutionResolver
    {
        internal bool Enabled { get; set; } = true;

        internal int CallCount { get; private set; }

        public bool TryResolve(
            MaterializationRebuildLeafExecutionAuthority authority,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? execution)
        {
            CallCount++;
            if (Enabled)
                return inner.TryResolve(authority, continuation, out execution);
            execution = null;
            return false;
        }
    }
}
