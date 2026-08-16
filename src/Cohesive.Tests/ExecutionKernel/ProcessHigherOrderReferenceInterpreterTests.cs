using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessHigherOrderReferenceInterpreterTests
{
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    static readonly InteractionAuthorityScope Authority =
        new("authority/tests", "tenant/cohesive");

    static readonly ValueContract StringContract =
        new(new ScalarTypeRef(ScalarTypeKind.String));

    static readonly ValueContract StringCollectionContract =
        new(new ScalarTypeRef(ScalarTypeKind.String), cardinality: FieldCardinality.Many);

    static readonly ProcessChildOutcomeMapping ChildOutcomeMapping = new(
        new("completed"),
        new("failed"),
        new("cancelled"),
        new("terminated"));

    [Fact]
    public async Task ForEachPartition_BoundsStartsAndParallelismAcrossReplayCheckpointRestoreAndLaterAdmission()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/index-shard", 'a');
        var plan = Compile(
            Definition(
                StringCollectionContract,
                "partitions",
                [
                    PartitionNode(child, contracts.Request, ProcessChildCancellationPolicy.Propagate),
                    new ReturnProcessNode(new("return"), Expr.Const("index-generation-ready")),
                    new FailProcessNode(new("fail"), Expr.Const("index-generation-failed"))
                ]),
            contracts.Catalog,
            [new(child, ProcessDefinitionLinkKind.Process, StringContract, StringContract, [], ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: "process/index-coordinator");
        var start = Start(plan, CollectionValue("shard-c", "shard-a", "shard-b"));
        var initial = ProcessReferenceInterpreter.Create(plan, start);
        var activation = Activation("activation/partition-start", ProcessActivationCause.Start);

        var first = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            activation,
            RejectingHost.Instance);
        var replay = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            activation,
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Disposition);
        Assert.Equal(2, first.Emissions.Length);
        Assert.Equal(2, first.State.Children.Count(
            static child => child.Disposition == ProcessChildDisposition.Active));
        Assert.Single(first.State.Children, static child => child.Disposition == ProcessChildDisposition.Pending);
        Assert.Equal(
            ["shard-a", "shard-b", "shard-c"],
            Assert.Single(first.State.Partitions).Work.Select(static work => work.ProgressIdentity));
        var retainedPartition = Assert.Single(first.State.Partitions);
        var firstWork = retainedPartition.Work[0];
        var missingPartitionValue = firstWork with
        {
            Partition = PortableValue.Missing(firstWork.Partition.Contract)
        };
        var malformedPartition = NewPartition(
            retainedPartition.RegistrationId,
            retainedPartition.Owner,
            retainedPartition.Node,
            retainedPartition.Occurrence,
            retainedPartition.Work.SetItem(0, missingPartitionValue),
            retainedPartition.Resolved);
        var malformedPartitionState = CopyState(first.State, partitions: [malformedPartition]);
        Assert.Contains(
            ProcessContinuationValidator.Validate(plan, malformedPartitionState).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.PartitionStateMismatch
                                 && diagnostic.Location == "/partitions/0/work/0");
        Assert.Equal(
            ChildIdentityEvidence(first.State),
            ChildIdentityEvidence(replay.State));
        Assert.Equal(
            RequestIdentityEvidence(first.Emissions),
            RequestIdentityEvidence(replay.Emissions));
        Assert.All(first.Emissions.OfType<RequestEnvelope>(), request =>
        {
            var childState = first.State.Children.Single(candidate =>
                candidate.RequestEmission == request.Context.EmissionId);
            Assert.Equal(
                new ProcessChildRequestTarget(
                    childState.Process,
                    childState.Continuation,
                    ChildOutcomeMapping,
                    childState.Owner,
                    childState.Occurrence,
                    childState.ProgressIdentity),
                request.ChildTarget);
        });

        var store = new InMemoryProcessDurableStore();
        var adapter = new ChildCompletionAdapter(contracts.Request);
        var runtime = Runtime(store, contracts.Binding, adapter);
        var initialized = await runtime.InitializeAsync(Context(StartedAtUtc), plan, start);
        var initializedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var committed = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(1)),
            plan,
            initializedSnapshot.Checkpoint.ContinuationIdentity,
            activation);
        var committedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(committed.Snapshot).Checkpoint;

        Assert.True(
            committed.Disposition == ProcessDurableRuntimeDisposition.Applied,
            FormatDiagnostics(committed.Diagnostics));
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(first.State),
            ProcessStorageContentFingerprints.Continuation(committedCheckpoint.Continuation));
        Assert.Equal(2, committedCheckpoint.DurableOperations.Length);

        var json = ProcessDurableCheckpointJsonSerializer.Serialize(committedCheckpoint);
        var restored = ProcessDurableCheckpointJsonSerializer.Deserialize(json, plan);

        Assert.Equal(json, ProcessDurableCheckpointJsonSerializer.Serialize(restored));
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(committedCheckpoint.Continuation),
            ProcessStorageContentFingerprints.Continuation(restored.Continuation));
        Assert.Equal(
            ChildIdentityEvidence(committedCheckpoint.Continuation),
            ChildIdentityEvidence(restored.Continuation));
        Assert.All(restored.Emissions.Select(static record => record.Envelope).OfType<RequestEnvelope>(), request =>
        {
            var childState = restored.Continuation.Children.Single(candidate =>
                candidate.RequestEmission == request.Context.EmissionId);
            Assert.Equal(
                new ProcessChildRequestTarget(
                    childState.Process,
                    childState.Continuation,
                    ChildOutcomeMapping,
                    childState.Owner,
                    childState.Occurrence,
                    childState.ProgressIdentity),
                request.ChildTarget);
        });

        var recoveredStore = new InMemoryProcessDurableStore();
        var recovered = await recoveredStore.InitializeAsync(
            Context(restored.UpdatedAtUtc),
            new("commit/restore-index-coordinator"),
            restored);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, recovered.Disposition);
        var recoveredRuntime = Runtime(recoveredStore, contracts.Binding, adapter);
        var activeRequests = restored.Emissions
            .Select(static record => record.Envelope)
            .OfType<RequestEnvelope>()
            .OrderBy(static request => request.Context.EmissionId.Value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, activeRequests.Length);
        var operationAt = restored.UpdatedAtUtc.AddSeconds(1);
        var operationCheckpoint = restored;
        foreach (var request in activeRequests)
        {
            var advanced = await recoveredRuntime.AdvanceOperationAsync(
                Context(operationAt),
                plan,
                restored.ContinuationIdentity.ProcessInstanceId,
                request.Context.EmissionId);
            Assert.Equal(ProcessDurableRuntimeDisposition.Applied, advanced.Disposition);
            operationCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(advanced.Snapshot).Checkpoint;
            operationAt = operationAt.AddSeconds(1);
        }
        var firstReplies = activeRequests
            .Select(request => new
            {
                Request = request,
                Input = operationCheckpoint.Inbox.Single(entry =>
                    entry.EmissionId == ProcessDurableRuntimeIdentities.OperationReply(
                        request.Context.EmissionId)).Input
            })
            .ToArray();
        var firstReplyInputs = firstReplies
            .Select(static pair => pair.Input)
            .ToImmutableArray();

        var admittedLater = await recoveredRuntime.ActivateAsync(
            Context(operationAt),
            plan,
            restored.ContinuationIdentity,
            Activation(
                "activation/partition-first-replies",
                ProcessActivationCause.Interaction,
                operationAt,
                inputs: firstReplyInputs));
        var laterCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(admittedLater.Snapshot).Checkpoint;
        var laterRequest = Assert.IsType<RequestEnvelope>(Assert.Single(admittedLater.Decision!.Emissions));

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, admittedLater.Disposition);
        Assert.Equal(ProcessActivationDisposition.DurableCut, admittedLater.Decision.Disposition);
        Assert.Equal(2, laterCheckpoint.Continuation.Children.Count(
            static child => child.Disposition == ProcessChildDisposition.Completed));
        Assert.Single(laterCheckpoint.Continuation.Children,
            static child => child.Disposition == ProcessChildDisposition.Active);
        Assert.DoesNotContain(laterCheckpoint.Continuation.Children,
            static child => child.Disposition == ProcessChildDisposition.Pending);
        foreach (var pair in firstReplies)
        {
            var request = pair.Request;
            var reply = Assert.IsType<ReplyEnvelope>(pair.Input.Envelope);
            Assert.Equal(request.Context.EmissionId, reply.InReplyTo);
            Assert.Equal(
                request.ResponseTarget,
                firstReplyInputs.Single(input =>
                    input.Envelope.Context.EmissionId == reply.Context.EmissionId).Target);
            var childState = laterCheckpoint.Continuation.Children.Single(candidate =>
                candidate.RequestEmission == request.Context.EmissionId);
            Assert.Equal(ProcessChildDisposition.Completed, childState.Disposition);
            Assert.Equal(reply.Outcome.Id, childState.TerminalOutcome);
            Assert.Equal(reply.Outcome.Value, childState.Result);
        }

        var finalAt = operationAt.AddSeconds(1);
        var finalOperation = await recoveredRuntime.AdvanceOperationAsync(
            Context(finalAt),
            plan,
            restored.ContinuationIdentity.ProcessInstanceId,
            laterRequest.Context.EmissionId);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, finalOperation.Disposition);
        var finalOperationCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(
            finalOperation.Snapshot).Checkpoint;
        var finalInput = finalOperationCheckpoint.Inbox.Single(entry =>
            entry.EmissionId == ProcessDurableRuntimeIdentities.OperationReply(
                laterRequest.Context.EmissionId)).Input;
        var completed = await recoveredRuntime.ActivateAsync(
            Context(finalAt.AddSeconds(1)),
            plan,
            restored.ContinuationIdentity,
            Activation(
                "activation/partition-final-reply",
                ProcessActivationCause.Interaction,
                finalAt.AddSeconds(1),
                inputs: [finalInput]));
        var completedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(completed.Snapshot).Checkpoint;

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, completed.Disposition);
        Assert.Equal(ProcessActivationDisposition.Completed, completed.Decision?.Disposition);
        Assert.Equal(StringValue("index-generation-ready"), completedCheckpoint.Continuation.Terminal.Detail?.Value);
        Assert.True(Assert.Single(completedCheckpoint.Continuation.Partitions).Resolved);
        Assert.All(completedCheckpoint.Continuation.Children, static child =>
            Assert.Equal(ProcessChildDisposition.Completed, child.Disposition));
        var missingPartitionTombstone = CopyState(
            completedCheckpoint.Continuation,
            waits:
            [
                .. completedCheckpoint.Continuation.Waits.Where(static wait =>
                    wait.Kind != ProcessWaitKind.PartitionBatch)
            ]);
        Assert.Contains(
            ProcessContinuationValidator.Validate(plan, missingPartitionTombstone).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.PartitionStateMismatch);
    }

    [Fact]
    public void ForEachPartition_RestoreRejectsSelfConsistentSubstitutedPendingChildProgressIdentity()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/progress-identity-index-shard", '9');
        var plan = Compile(
            Definition(
                StringCollectionContract,
                "partitions",
                [
                    PartitionNode(
                        child,
                        contracts.Request,
                        ProcessChildCancellationPolicy.Propagate,
                        limits: new(
                            maximumItems: 3,
                            maximumStartsPerActivation: 2,
                            maximumParallelism: 2)),
                    new ReturnProcessNode(new("return"), Expr.Const("index-generation-ready")),
                    new FailProcessNode(new("fail"), Expr.Const("index-generation-failed"))
                ]),
            contracts.Catalog,
            [new(
                child,
                ProcessDefinitionLinkKind.Process,
                StringContract,
                StringContract,
                [],
                ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: "process/progress-identity-index-coordinator");
        var continuation = Continuation("process-instance/progress-identity-index-coordinator");
        var activation = Activation("activation/progress-identity-index-start", ProcessActivationCause.Start);
        var retained = ProcessReferenceInterpreter.Activate(
            plan,
            ProcessReferenceInterpreter.Create(
                plan,
                continuation,
                CollectionValue("shard-a", "shard-b", "shard-c")),
            activation,
            RejectingHost.Instance);
        var substituted = ProcessReferenceInterpreter.Activate(
            plan,
            ProcessReferenceInterpreter.Create(
                plan,
                continuation,
                CollectionValue("shard-a", "shard-b", "shard-z")),
            activation,
            RejectingHost.Instance);
        var retainedPartition = Assert.Single(retained.State.Partitions);
        var substitutedPartition = Assert.Single(substituted.State.Partitions);
        var retainedPending = Assert.Single(
            retained.State.Children,
            static candidate => candidate.Disposition == ProcessChildDisposition.Pending);
        var substitutedPending = Assert.Single(
            substituted.State.Children,
            static candidate => candidate.Disposition == ProcessChildDisposition.Pending);
        var retainedWorkIndex = Enumerable.Range(0, retainedPartition.Work.Length).Single(index =>
            retainedPartition.Work[index].ChildRegistrationId == retainedPending.RegistrationId);
        var retainedWork = retainedPartition.Work[retainedWorkIndex];
        var substitutedWork = substitutedPartition.Work.Single(candidate =>
            candidate.ChildRegistrationId == substitutedPending.RegistrationId);
        var hostileWork = retainedWork with
        {
            ProgressIdentity = substitutedWork.ProgressIdentity,
            ChildRegistrationId = substitutedWork.ChildRegistrationId
        };
        var hostilePartition = NewPartition(
            retainedPartition.RegistrationId,
            retainedPartition.Owner,
            retainedPartition.Node,
            retainedPartition.Occurrence,
            retainedPartition.Work.SetItem(retainedWorkIndex, hostileWork),
            retainedPartition.Resolved);
        var hostileChildren = retained.State.Children
            .Where(candidate => candidate.RegistrationId != retainedPending.RegistrationId)
            .Append(substitutedPending)
            .OrderBy(static candidate => candidate.RegistrationId, StringComparer.Ordinal)
            .ToImmutableArray();
        var hostileState = CopyState(
            retained.State,
            children: hostileChildren,
            partitions: [hostilePartition]);

        Assert.NotEqual(retainedPending.RegistrationId, substitutedPending.RegistrationId);
        Assert.NotEqual(retainedPending.Token, substitutedPending.Token);
        Assert.NotEqual(retainedPending.Continuation, substitutedPending.Continuation);
        Assert.Equal(StringValue("shard-c"), hostileWork.Partition);
        Assert.Equal("shard-z", hostileWork.ProgressIdentity);
        var validation = ProcessContinuationValidator.Validate(plan, hostileState);
        Assert.DoesNotContain(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.ChildStateMismatch);
        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.PartitionStateMismatch
                          && diagnostic.Location == $"/partitions/0/work/{retainedWorkIndex}");
    }

    [Fact]
    public void ForEachPartition_AwaitAllContinuesAdmissionAfterFailureAndRetainsEveryTerminalOutcome()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/await-all-index-shard", 'b');
        var plan = Compile(
            Definition(
                StringCollectionContract,
                "partitions",
                [
                    PartitionNode(
                        child,
                        contracts.Request,
                        ProcessChildCancellationPolicy.Propagate,
                        limits: new(
                            maximumItems: 3,
                            maximumStartsPerActivation: 2,
                            maximumParallelism: 2),
                        failure: ProcessPartitionFailurePolicy.AwaitAll),
                    new ReturnProcessNode(new("return"), Expr.Const("index-generation-ready")),
                    new FailProcessNode(new("fail"), Expr.Const("index-generation-failed"))
                ]),
            contracts.Catalog,
            [new(
                child,
                ProcessDefinitionLinkKind.Process,
                StringContract,
                StringContract,
                [],
                ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: "process/await-all-index-coordinator");
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            Continuation("process-instance/await-all-index-coordinator"),
            CollectionValue("shard-c", "shard-a", "shard-b"));
        var started = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/await-all-start", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var shardARequest = started.Emissions
            .OfType<RequestEnvelope>()
            .Single(request => request.Payload == StringValue("shard-a"));
        var shardBRequest = started.Emissions
            .OfType<RequestEnvelope>()
            .Single(request => request.Payload == StringValue("shard-b"));
        var shardAChild = started.State.Children.Single(childState =>
            childState.RequestEmission == shardARequest.Context.EmissionId);
        var shardAFailure = Reply(
            shardAChild,
            shardARequest,
            contracts.FailedReply,
            new RequestFailureOutcome(ChildOutcomeMapping.Failed, StringValue("failed/shard-a")),
            "emission/await-all-shard-a-failed");

        var continued = ProcessReferenceInterpreter.Activate(
            plan,
            started.State,
            Activation(
                "activation/await-all-after-failure",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs:
                [
                    new(
                        Assert.IsType<ProcessTokenInteractionTarget>(shardARequest.ResponseTarget),
                        shardAFailure)
                ]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, continued.Disposition);
        var shardCRequest = Assert.IsType<RequestEnvelope>(Assert.Single(continued.Emissions));
        Assert.Equal(StringValue("shard-c"), shardCRequest.Payload);
        Assert.False(Assert.Single(continued.State.Partitions).Resolved);
        Assert.Single(continued.State.Children,
            static childState => childState.Disposition == ProcessChildDisposition.Failed);
        Assert.Equal(2, continued.State.Children.Count(
            static childState => childState.Disposition == ProcessChildDisposition.Active));
        Assert.DoesNotContain(continued.State.Children, static childState => childState.Disposition is
            ProcessChildDisposition.CancellationRequested
            or ProcessChildDisposition.CancelledBeforeStart
            or ProcessChildDisposition.Detached);
        var continuedValidation = ProcessContinuationValidator.Validate(plan, continued.State);
        Assert.True(continuedValidation.IsValid, FormatDiagnostics(continuedValidation));

        var shardBChild = continued.State.Children.Single(childState =>
            childState.RequestEmission == shardBRequest.Context.EmissionId);
        var shardCChild = continued.State.Children.Single(childState =>
            childState.RequestEmission == shardCRequest.Context.EmissionId);
        var shardBReply = Reply(
            shardBChild,
            shardBRequest,
            contracts.CompletedReply,
            ChildOutcomeMapping.Completed,
            "indexed/shard-b",
            "emission/await-all-shard-b-completed");
        var shardCReply = Reply(
            shardCChild,
            shardCRequest,
            contracts.CompletedReply,
            ChildOutcomeMapping.Completed,
            "indexed/shard-c",
            "emission/await-all-shard-c-completed");
        var settled = ProcessReferenceInterpreter.Activate(
            plan,
            continued.State,
            Activation(
                "activation/await-all-settled",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(2),
                inputs:
                [
                    new(
                        Assert.IsType<ProcessTokenInteractionTarget>(shardBRequest.ResponseTarget),
                        shardBReply),
                    new(
                        Assert.IsType<ProcessTokenInteractionTarget>(shardCRequest.ResponseTarget),
                        shardCReply)
                ]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Failed, settled.Disposition);
        Assert.True(Assert.Single(settled.State.Partitions).Resolved);
        Assert.Equal(StringValue("index-generation-failed"), settled.State.Terminal.Detail?.Value);
        Assert.Contains(settled.State.Tokens, static token =>
            token.Disposition == ExecutionTokenDisposition.Failed
            && token.Failure?.Code == ProcessExecutionDiagnosticCodes.AuthoredFailure);
        var failedChild = Assert.Single(settled.State.Children,
            static childState => childState.Disposition == ProcessChildDisposition.Failed);
        Assert.Equal(ChildOutcomeMapping.Failed, failedChild.TerminalOutcome);
        Assert.Equal(StringValue("failed/shard-a"), failedChild.Result);
        Assert.Equal(2, settled.State.Children.Count(
            static childState => childState.Disposition == ProcessChildDisposition.Completed));
        var settledValidation = ProcessContinuationValidator.Validate(plan, settled.State);
        Assert.True(settledValidation.IsValid, FormatDiagnostics(settledValidation));
    }

    [Fact]
    public void ForEachPartition_CapacityDomainsSkipSaturatedTargetsDeterministicallyAndValidateRestoreBounds()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/capacity-bound-index-shard", 'c');
        var plan = Compile(
            Definition(
                StringCollectionContract,
                "partitions",
                [
                    PartitionNode(
                        child,
                        contracts.Request,
                        ProcessChildCancellationPolicy.Propagate,
                        limits: new(
                            maximumItems: 4,
                            maximumStartsPerActivation: 3,
                            maximumParallelism: 3),
                        capacityIdentity: CapacityDomainByShard,
                        capacityDomains:
                        [
                            new("target/a", maximumParallelism: 1),
                            new("target/b", maximumParallelism: 1)
                        ]),
                    new ReturnProcessNode(new("return"), Expr.Const("index-generation-ready")),
                    new FailProcessNode(new("fail"), Expr.Const("index-generation-failed"))
                ]),
            contracts.Catalog,
            [new(
                child,
                ProcessDefinitionLinkKind.Process,
                StringContract,
                StringContract,
                [],
                ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: "process/capacity-bound-index-coordinator");
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            Continuation("process-instance/capacity-bound-index-coordinator"),
            CollectionValue("shard-d", "shard-b", "shard-c", "shard-a"));

        var started = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/capacity-bound-start", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, started.Disposition);
        Assert.Equal(
            ["shard-a", "shard-c"],
            started.Emissions
                .OfType<RequestEnvelope>()
                .Select(static request => request.Payload.Value.GetValueOrDefault().String)
                .Order(StringComparer.Ordinal));
        var partition = Assert.Single(started.State.Partitions);
        Assert.Equal(
            ["target/a", "target/a", "target/b", "target/b"],
            partition.Work.Select(static work => work.CapacityIdentity));
        Assert.Equal(2, started.State.Children.Count(
            static childState => childState.Disposition == ProcessChildDisposition.Active));
        Assert.Equal(2, started.State.Children.Count(
            static childState => childState.Disposition == ProcessChildDisposition.Pending));
        var startedValidation = ProcessContinuationValidator.Validate(plan, started.State);
        Assert.True(startedValidation.IsValid, FormatDiagnostics(startedValidation));

        var shardDWorkIndex = Enumerable.Range(0, partition.Work.Length).Single(index =>
            partition.Work[index].ProgressIdentity == "shard-d");
        var capacityForgedWork = partition.Work.SetItem(
            shardDWorkIndex,
            partition.Work[shardDWorkIndex] with { CapacityIdentity = "target/a" });
        var capacityForgedPartition = NewPartition(
            partition.RegistrationId,
            partition.Owner,
            partition.Node,
            partition.Occurrence,
            capacityForgedWork,
            partition.Resolved);
        var capacityForgedState = CopyState(started.State, partitions: [capacityForgedPartition]);
        var capacityForgedValidation = ProcessContinuationValidator.Validate(
            plan,
            capacityForgedState);
        Assert.Contains(
            capacityForgedValidation.Diagnostics,
            diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.PartitionStateMismatch
                          && diagnostic.Location == $"/partitions/0/work/{shardDWorkIndex}");

        var shardCWorkIndex = Enumerable.Range(0, partition.Work.Length).Single(index =>
            partition.Work[index].ProgressIdentity == "shard-c");
        var forgedWork = partition.Work.SetItem(
            shardCWorkIndex,
            partition.Work[shardCWorkIndex] with { CapacityIdentity = "target/a" });
        var forgedPartition = NewPartition(
            partition.RegistrationId,
            partition.Owner,
            partition.Node,
            partition.Occurrence,
            forgedWork,
            partition.Resolved);
        var forgedState = CopyState(started.State, partitions: [forgedPartition]);
        Assert.NotEqual(
            ProcessStorageContentFingerprints.Continuation(started.State),
            ProcessStorageContentFingerprints.Continuation(forgedState));
        var forgedValidation = ProcessContinuationValidator.Validate(
            plan,
            forgedState);
        Assert.Contains(
            forgedValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.PartitionStateMismatch
                                 && diagnostic.Location == "/partitions/0/resolved");

        var shardARequest = started.Emissions
            .OfType<RequestEnvelope>()
            .Single(request => request.Payload == StringValue("shard-a"));
        var shardAChild = started.State.Children.Single(childState =>
            childState.RequestEmission == shardARequest.Context.EmissionId);
        var shardAReply = Reply(
            shardAChild,
            shardARequest,
            contracts.CompletedReply,
            ChildOutcomeMapping.Completed,
            "indexed/shard-a",
            "emission/capacity-bound-shard-a-completed");

        var admitted = ProcessReferenceInterpreter.Activate(
            plan,
            started.State,
            Activation(
                "activation/capacity-bound-admit-next",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs:
                [
                    new(
                        Assert.IsType<ProcessTokenInteractionTarget>(shardARequest.ResponseTarget),
                        shardAReply)
                ]),
            RejectingHost.Instance);

        var nextRequest = Assert.IsType<RequestEnvelope>(Assert.Single(admitted.Emissions));
        Assert.Equal(StringValue("shard-b"), nextRequest.Payload);
        Assert.Single(admitted.State.Children,
            childState => childState.ProgressIdentity == "shard-d"
                          && childState.Disposition == ProcessChildDisposition.Pending);
        var admittedValidation = ProcessContinuationValidator.Validate(plan, admitted.State);
        Assert.True(admittedValidation.IsValid, FormatDiagnostics(admittedValidation));
    }

    [Fact]
    public async Task InvokeProcess_DurableAdapterSeesSelfContainedChildTargetAndItsTruthfulReplyJoins()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/durable-child-worker", '8');
        var plan = DirectChildPlan(contracts, child, "process/durable-child-parent");
        var store = new InMemoryProcessDurableStore();
        var adapter = new ChildCompletionAdapter(contracts.Request);
        var runtime = Runtime(store, contracts.Binding, adapter);
        var initialized = await runtime.InitializeAsync(
            Context(StartedAtUtc),
            plan,
            Start(plan, StringValue("child-input")));
        var initializedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var started = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(1)),
            plan,
            initializedCheckpoint.ContinuationIdentity,
            Activation("activation/durable-child-start", ProcessActivationCause.Start));
        var startedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(started.Snapshot).Checkpoint;
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(started.Decision!.Emissions));
        var childTarget = Assert.IsType<ProcessChildRequestTarget>(request.ChildTarget);
        ProcessInteractionOrigin truthfulChildOrigin = new(
            childTarget.Definition,
            new("return"),
            childTarget.Continuation,
            new("activation/child-terminal"),
            new("token/child-terminal"),
            outcome: new("return"));
        ProcessInteractionOrigin wrongChildOrigin = new(
            childTarget.Definition,
            new("return"),
            new(new("process-instance/wrong-child"), new("process-attempt/wrong-child")),
            new("activation/wrong-child-terminal"),
            new("token/wrong-child-terminal"),
            outcome: new("return"));
        var executor = new DurableOperationReferenceExecutor(contracts.Catalog);

        var pendingOperation = Assert.Single(startedCheckpoint.DurableOperations);
        var reconciliationClaim = executor.Claim(
            pendingOperation,
            new("operation-attempt/reconciliation-origin"),
            "worker/reconciliation-origin",
            StartedAtUtc.AddMinutes(2));
        var reconciliationDispatch = executor.BeginDispatch(
            reconciliationClaim.State,
            Assert.IsType<DurableOperationClaim>(reconciliationClaim.Claim).AttemptId,
            reconciliationClaim.Claim.Fence,
            StartedAtUtc.AddMinutes(2));
        var ambiguous = executor.RecordObservation(
            reconciliationDispatch.State,
            reconciliationClaim.Claim.AttemptId,
            reconciliationClaim.Claim.Fence,
            new DurableOperationFailureObservation(new(
                DurableOperationFailurePhase.InCall,
                DurableOperationEffectEvidence.Ambiguous,
                DurableOperationFailureDisposition.Retryable,
                "adapter.child.ambiguous")),
            StartedAtUtc.AddMinutes(3));
        Assert.Equal(DurableOperationRecoveryRequirement.Reconcile, ambiguous.State.RecoveryRequirement);
        DurableOperationReconciledOutcome reconciliationWithoutOrigin = new(
            new RequestResultOutcome(ChildOutcomeMapping.Completed, StringValue("child-result")));
        DurableOperationReconciledOutcome reconciliationWithWrongOrigin = new(
            new RequestResultOutcome(ChildOutcomeMapping.Completed, StringValue("child-result")),
            replyOrigin: wrongChildOrigin);
        DurableOperationReconciledOutcome reconciliationWithTruthfulOrigin = new(
            new RequestResultOutcome(ChildOutcomeMapping.Completed, StringValue("child-result")),
            replyOrigin: truthfulChildOrigin);
        var missingOrigin = executor.RecordReconciliation(
            ambiguous.State,
            reconciliationClaim.Claim.AttemptId,
            reconciliationClaim.Claim.Fence,
            reconciliationWithoutOrigin,
            StartedAtUtc.AddMinutes(4));
        var wrongOrigin = executor.RecordReconciliation(
            ambiguous.State,
            reconciliationClaim.Claim.AttemptId,
            reconciliationClaim.Claim.Fence,
            reconciliationWithWrongOrigin,
            StartedAtUtc.AddMinutes(4));
        var reconciled = executor.RecordReconciliation(
            ambiguous.State,
            reconciliationClaim.Claim.AttemptId,
            reconciliationClaim.Claim.Fence,
            reconciliationWithTruthfulOrigin,
            StartedAtUtc.AddMinutes(4));
        var reconciliationReplay = executor.RecordReconciliation(
            reconciled.State,
            reconciliationClaim.Claim.AttemptId,
            reconciliationClaim.Claim.Fence,
            reconciliationWithTruthfulOrigin,
            StartedAtUtc.AddMinutes(5));

        Assert.Equal(DurableOperationObservationDisposition.InvalidEvidence, missingOrigin.Disposition);
        Assert.Same(ambiguous.State, missingOrigin.State);
        Assert.Equal(DurableOperationObservationDisposition.InvalidEvidence, wrongOrigin.Disposition);
        Assert.Same(ambiguous.State, wrongOrigin.State);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, reconciled.Disposition);
        Assert.Equal(truthfulChildOrigin, reconciled.State.Acknowledgement?.ReplyOrigin);
        Assert.Equal(DurableOperationObservationDisposition.Replayed, reconciliationReplay.Disposition);
        Assert.Same(reconciled.State, reconciliationReplay.State);

        var executed = await runtime.AdvanceOperationAsync(
            Context(StartedAtUtc.AddMinutes(2)),
            plan,
            startedCheckpoint.ContinuationIdentity.ProcessInstanceId,
            request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, executed.Disposition);
        var invocation = Assert.Single(adapter.Invocations);
        Assert.Equal(request.ChildTarget, invocation.Request.ChildTarget);
        Assert.Equal(ChildOutcomeMapping, invocation.Request.ChildTarget?.OutcomeMapping);
        var operation = Assert.IsType<DurableOperationState>(executed.Operation);
        Assert.Equal(adapter.ReplyOrigin, operation.Acknowledgement?.ReplyOrigin);
        var acknowledgedAttempt = Assert.IsType<DurableOperationAttempt>(operation.CurrentAttempt);
        var acknowledgedObservation = new DurableOperationOutcomeObservation(
            Assert.IsType<RequestResultOutcome>(operation.Acknowledgement?.Outcome),
            operation.Acknowledgement?.AdapterEvidence,
            operation.Acknowledgement?.ReplyOrigin);
        var acknowledgementReplay = executor.RecordObservation(
            operation,
            acknowledgedAttempt.Claim.AttemptId,
            acknowledgedAttempt.Claim.Fence,
            acknowledgedObservation,
            StartedAtUtc.AddMinutes(2).AddSeconds(1));
        var conflictingOrigin = executor.RecordObservation(
            operation,
            acknowledgedAttempt.Claim.AttemptId,
            acknowledgedAttempt.Claim.Fence,
            new DurableOperationOutcomeObservation(
                Assert.IsType<RequestResultOutcome>(operation.Acknowledgement?.Outcome),
                operation.Acknowledgement?.AdapterEvidence,
                wrongChildOrigin),
            StartedAtUtc.AddMinutes(2).AddSeconds(1));
        Assert.Equal(DurableOperationObservationDisposition.Replayed, acknowledgementReplay.Disposition);
        Assert.Same(operation, acknowledgementReplay.State);
        Assert.Equal(DurableOperationObservationDisposition.ConflictingOutcome, conflictingOrigin.Disposition);
        Assert.Same(operation, conflictingOrigin.State);
        var operationCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(executed.Snapshot).Checkpoint;
        var replyId = ProcessDurableRuntimeIdentities.OperationReply(request.Context.EmissionId);
        var replyInput = Assert.Single(operationCheckpoint.Inbox, entry => entry.EmissionId == replyId).Input;
        var reply = Assert.IsType<ReplyEnvelope>(replyInput.Envelope);
        Assert.Equal(adapter.ReplyOrigin, reply.Context.Origin);
        Assert.NotEqual(request.Context.Origin, reply.Context.Origin);

        var joined = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(3)),
            plan,
            operationCheckpoint.ContinuationIdentity,
            Activation(
                "activation/durable-child-join",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(3),
                inputs: [replyInput]));

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, joined.Disposition);
        Assert.Equal(ProcessActivationDisposition.Completed, joined.Decision?.Disposition);
        Assert.Equal(StringValue("child-result"), joined.Decision?.State.Terminal.Detail?.Value);
        Assert.Equal(
            ProcessChildDisposition.Completed,
            Assert.Single(joined.Decision!.State.Children).Disposition);
    }

    [Fact]
    public async Task CheckpointRestore_TerminalChildRequiresItsAcceptedOperationAndDeterministicReply()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/terminal-closure-child", '7');
        var plan = DirectChildPlan(contracts, child, "process/terminal-child-reverse-closure");
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, contracts.Binding, new ChildCompletionAdapter(contracts.Request));
        var initialized = await runtime.InitializeAsync(
            Context(StartedAtUtc),
            plan,
            Start(plan, StringValue("child-input")));
        var initializedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var started = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(1)),
            plan,
            initializedCheckpoint.ContinuationIdentity,
            Activation("activation/terminal-closure-start", ProcessActivationCause.Start));
        var startedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(started.Snapshot).Checkpoint;
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(started.Decision!.Emissions));
        var pendingOperation = Assert.Single(startedCheckpoint.DurableOperations);
        var advanced = await runtime.AdvanceOperationAsync(
            Context(StartedAtUtc.AddMinutes(2)),
            plan,
            startedCheckpoint.ContinuationIdentity.ProcessInstanceId,
            request.Context.EmissionId);
        var operationCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(advanced.Snapshot).Checkpoint;
        var replyInput = operationCheckpoint.Inbox.Single(entry =>
            entry.EmissionId == ProcessDurableRuntimeIdentities.OperationReply(
                request.Context.EmissionId)).Input;
        var joined = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(3)),
            plan,
            operationCheckpoint.ContinuationIdentity,
            Activation(
                "activation/terminal-closure-join",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(3),
                inputs: [replyInput]));
        var joinedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(joined.Snapshot).Checkpoint;
        var forgedCheckpoint = new ProcessDurableCheckpoint(
            joinedCheckpoint.SchemaVersion,
            joinedCheckpoint.Start,
            joinedCheckpoint.Continuation,
            joinedCheckpoint.Control,
            joinedCheckpoint.Activations,
            joinedCheckpoint.Operations,
            joinedCheckpoint.Inbox,
            joinedCheckpoint.Emissions,
            durableOperations: [pendingOperation],
            joinedCheckpoint.CreatedAtUtc,
            joinedCheckpoint.UpdatedAtUtc);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(plan, forgedCheckpoint);

        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
                                 && diagnostic.Location == "/continuation/children/0/result");
    }

    [Fact]
    public void ForEachPartition_InitialChildInputFailureClosesAllOwnedWorkWithoutPartialStarts()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/failing-initial-index-shard", '1');
        var plan = Compile(
            Definition(
                StringCollectionContract,
                "partitions",
                [
                    PartitionNode(
                        child,
                        contracts.Request,
                        ProcessChildCancellationPolicy.Propagate,
                        childInput: RuntimeFailingShardInput),
                    new ReturnProcessNode(new("return"), Expr.Const("unexpected")),
                    new FailProcessNode(new("fail"), Expr.Const("unexpected"))
                ]),
            contracts.Catalog,
            [new(child, ProcessDefinitionLinkKind.Process, StringContract, StringContract, [], ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: "process/failing-initial-partition-input");
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            Continuation("process-instance/failing-initial-partition-input"),
            CollectionValue("shard-c", "shard-a", "shard-b"));

        var failed = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/failing-initial-partition-input", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.Empty(failed.Emissions);
        Assert.Empty(failed.State.OutstandingRequests);
        Assert.True(Assert.Single(failed.State.Partitions).Resolved);
        Assert.All(failed.State.Children, static childState =>
            Assert.Equal(ProcessChildDisposition.CancelledBeforeStart, childState.Disposition));
        Assert.DoesNotContain(failed.State.Waits, static wait => wait.Active);
        Assert.Contains(
            failed.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.ExpressionFailed);
        Assert.True(
            ProcessContinuationValidator.Validate(plan, failed.State).IsValid,
            FormatDiagnostics(ProcessContinuationValidator.Validate(plan, failed.State)));
    }

    [Fact]
    public void ForEachPartition_LaterChildInputFailureClosesPendingWorkAfterCompletedMembers()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/failing-later-index-shard", '2');
        var plan = Compile(
            Definition(
                StringCollectionContract,
                "partitions",
                [
                    PartitionNode(
                        child,
                        contracts.Request,
                        ProcessChildCancellationPolicy.Propagate,
                        childInput: RuntimeFailingShardInput,
                        limits: new(maximumItems: 3, maximumStartsPerActivation: 1, maximumParallelism: 1)),
                    new ReturnProcessNode(new("return"), Expr.Const("unexpected")),
                    new FailProcessNode(new("fail"), Expr.Const("unexpected"))
                ]),
            contracts.Catalog,
            [new(child, ProcessDefinitionLinkKind.Process, StringContract, StringContract, [], ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: "process/failing-later-partition-input");
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            Continuation("process-instance/failing-later-partition-input"),
            CollectionValue("shard-b", "shard-a"));
        var started = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/failing-later-partition-start", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(started.Emissions));
        var activeChild = Assert.Single(
            started.State.Children,
            static childState => childState.Disposition == ProcessChildDisposition.Active);
        var reply = Reply(
            activeChild,
            request,
            contracts.CompletedReply,
            new("completed"),
            "indexed/shard-a",
            "emission/failing-later-first-reply");

        var failed = ProcessReferenceInterpreter.Activate(
            plan,
            started.State,
            Activation(
                "activation/failing-later-partition-resume",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs:
                [
                    new(
                        Assert.IsType<ProcessTokenInteractionTarget>(request.ResponseTarget),
                        reply)
                ]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.Empty(failed.Emissions);
        Assert.Empty(failed.State.OutstandingRequests);
        Assert.True(Assert.Single(failed.State.Partitions).Resolved);
        Assert.Single(failed.State.Children,
            static childState => childState.Disposition == ProcessChildDisposition.Completed);
        Assert.Single(failed.State.Children,
            static childState => childState.Disposition == ProcessChildDisposition.CancelledBeforeStart);
        Assert.DoesNotContain(failed.State.Waits, static wait => wait.Active);
        Assert.Contains(
            failed.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.ExpressionFailed);
        var validation = ProcessContinuationValidator.Validate(plan, failed.State);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));
    }

    [Fact]
    public void PartitionBatchWait_RequiresOneExactUnresolvedPartitionOnRestore()
    {
        var (plan, state) = CreatePartitionWaitingState("orphan-partition-wait", '3');
        var partitionWait = state.Waits.Single(static wait =>
            wait.Kind == ProcessWaitKind.PartitionBatch && wait.Active);
        var partitionWaitIndex = state.Waits.IndexOf(partitionWait);
        var malformed = CopyState(state, partitions: []);

        var validation = ProcessContinuationValidator.Validate(plan, malformed);

        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.PartitionStateMismatch
                          && diagnostic.Location == $"/waits/{partitionWaitIndex}");
    }

    [Fact]
    public async Task ForEachPartition_ResolverStopsAfterFirstDeterministicOccurrenceStartsItsNextBatch()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/forked-partition-child", '6');
        var partition = PartitionNode(
            child,
            contracts.Request,
            ProcessChildCancellationPolicy.Propagate,
            limits: new(maximumItems: 2, maximumStartsPerActivation: 1, maximumParallelism: 1));
        var plan = Compile(
            Definition(
                StringCollectionContract,
                "fork",
                [
                    new ForkProcessNode(
                        new("fork"),
                        [
                            new(new("branch/a"), Edge("edge/fork-a", "partitions")),
                            new(new("branch/b"), Edge("edge/fork-b", "partitions"))
                        ],
                        new("join")),
                    new ForEachPartitionProcessNode(
                        partition.Id,
                        partition.Partitions,
                        partition.Partition,
                        partition.ProgressIdentity,
                        partition.Process,
                        partition.Contract,
                        partition.OutcomeMapping,
                        partition.ChildInput,
                        partition.Limits,
                        partition.Failure,
                        partition.CapacityIdentity,
                        partition.CapacityDomains,
                        partition.Cancellation,
                        Edge("edge/partitions-join", "join"),
                        Edge("edge/partitions-failed-join", "join")),
                    new JoinProcessNode(
                        new("join"),
                        new("fork"),
                        new(
                            ProcessJoinMode.All,
                            requiredCount: 0,
                            ProcessJoinFailurePolicy.FailFast,
                            ProcessJoinCancellationPolicy.AwaitRemaining,
                            ProcessJoinCompletionOrder.Unobservable,
                            ProcessJoinTieBreak.BranchIdentity),
                        Edge("edge/join-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.Const("done"))
                ]),
            contracts.Catalog,
            [new(child, ProcessDefinitionLinkKind.Process, StringContract, StringContract, [], ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: "process/forked-partition-first-cut");
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            Continuation("process-instance/forked-partition-first-cut"),
            CollectionValue("item-a", "item-b"));

        var first = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/forked-partition-first", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var second = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            Activation(
                "activation/forked-partition-second",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(1)),
            RejectingHost.Instance);

        Assert.Single(first.Emissions);
        Assert.Single(second.Emissions);
        Assert.Equal(2, second.State.Partitions.Length);
        var activeRequests = second.State.OutstandingRequests
            .OrderBy(static request => request.Emission.Value, StringComparer.Ordinal)
            .Select(request => new
            {
                Request = Assert.IsType<RequestEnvelope>(
                    first.Emissions.Concat(second.Emissions).Single(envelope =>
                        envelope.Context.EmissionId == request.Emission)),
                Child = second.State.Children.Single(candidate =>
                    candidate.RequestEmission == request.Emission)
            })
            .ToArray();
        Assert.Equal(2, activeRequests.Length);
        var replies = activeRequests.Select((pair, index) => Reply(
            pair.Child,
            pair.Request,
            contracts.CompletedReply,
            ChildOutcomeMapping.Completed,
            $"completed/{index}",
            $"emission/forked-partition-reply-{index}")).ToArray();
        var inputs = replies.Select(reply => new ProcessActivationInput(
            Assert.IsType<ProcessTokenInteractionTarget>(
                activeRequests.Single(pair => pair.Request.Context.EmissionId == reply.InReplyTo)
                    .Request.ResponseTarget),
            reply)).ToImmutableArray();

        var resumed = ProcessReferenceInterpreter.Activate(
            plan,
            second.State,
            Activation(
                "activation/forked-partition-resolve",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(2),
                inputs: inputs),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, resumed.Disposition);
        Assert.Single(resumed.Emissions);
        var newlyStartedRequest = Assert.IsType<RequestEnvelope>(Assert.Single(resumed.Emissions));
        var newlyStartedChild = resumed.State.Children.Single(candidate =>
            candidate.RequestEmission == newlyStartedRequest.Context.EmissionId);
        var firstPartition = resumed.State.Partitions
            .OrderBy(static candidate => candidate.RegistrationId, StringComparer.Ordinal)
            .First();
        Assert.Equal(firstPartition.Owner, newlyStartedChild.Owner);
        Assert.Single(resumed.State.Children, static candidate =>
            candidate.Disposition == ProcessChildDisposition.Pending);

        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, contracts.Binding);
        var start = Start(plan, CollectionValue("item-a", "item-b"));
        var initialized = await runtime.InitializeAsync(Context(StartedAtUtc), plan, start);
        var initializedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var durableFirst = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(1)),
            plan,
            initializedCheckpoint.ContinuationIdentity,
            Activation("activation/forked-partition-durable-first", ProcessActivationCause.Start));
        var firstCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(durableFirst.Snapshot).Checkpoint;
        var durableSecond = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(2)),
            plan,
            firstCheckpoint.ContinuationIdentity,
            Activation(
                "activation/forked-partition-durable-second",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(1)));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(durableSecond.Snapshot).Checkpoint;
        var requestRecords = checkpoint.Emissions
            .Where(static record => record.Envelope is RequestEnvelope)
            .OrderBy(static record => record.EmissionId.Value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, requestRecords.Length);
        var firstOrigin = Assert.IsType<ProcessInteractionOrigin>(requestRecords[0].Envelope.Context.Origin);
        var forgedRequest = Assert.IsType<RequestEnvelope>(requestRecords[1].Envelope);
        var forgedOrigin = Assert.IsType<ProcessInteractionOrigin>(forgedRequest.Context.Origin);
        var forgedContext = new InteractionEnvelopeContext(
            forgedRequest.Context.EmissionId,
            new ProcessInteractionOrigin(
                forgedOrigin.Definition,
                forgedOrigin.Node,
                forgedOrigin.Continuation,
                firstOrigin.Activation,
                forgedOrigin.Token,
                forgedOrigin.Entity,
                forgedOrigin.Transition,
                forgedOrigin.Outcome),
            forgedRequest.Context.CorrelationId,
            forgedRequest.Context.CausationId,
            forgedRequest.Context.AuthorityScope,
            forgedRequest.Context.IdempotencyKey,
            forgedRequest.Context.Ordering,
            forgedRequest.Context.Delivery,
            forgedRequest.Context.Provenance);
        var forgedEnvelope = new RequestEnvelope(
            forgedRequest.SchemaVersion,
            forgedContext,
            forgedRequest.Contract,
            forgedRequest.Payload,
            forgedRequest.ResponseTarget,
            forgedRequest.ChildTarget);
        var forgedEmissions = checkpoint.Emissions
            .Select(record => record.EmissionId == forgedRequest.Context.EmissionId
                ? new ProcessEmissionRecord(
                    forgedEnvelope,
                    record.EnqueuedAtUtc,
                    record.Attempts,
                    record.Publication)
                : record)
            .ToImmutableArray();
        var forgedCheckpoint = new ProcessDurableCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Start,
            checkpoint.Continuation,
            checkpoint.Control,
            checkpoint.Activations,
            checkpoint.Operations,
            checkpoint.Inbox,
            forgedEmissions,
            durableOperations: [],
            checkpoint.CreatedAtUtc,
            checkpoint.UpdatedAtUtc);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(plan, forgedCheckpoint);

        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
                                 && diagnostic.Location == "/continuation/partitions/0/work"
                                 && diagnostic.Message.Contains(
                                     "one reachable occurrence per node and activation",
                                     StringComparison.Ordinal));
    }

    [Fact]
    public void PartitionCoordinator_CannotRestoreDuplicateUnresolvedOccurrences()
    {
        var (plan, state) = CreatePartitionWaitingState("duplicate-partition-occurrence", '4');
        var partition = Assert.Single(state.Partitions);
        var duplicate = NewPartition(
            $"zz/{partition.RegistrationId}",
            partition.Owner,
            partition.Node,
            partition.Occurrence,
            partition.Work,
            resolved: false);
        var malformedPartitions = new[] { partition, duplicate }
            .OrderBy(static candidate => candidate.RegistrationId, StringComparer.Ordinal)
            .ToImmutableArray();
        var duplicateIndex = malformedPartitions.IndexOf(duplicate);
        var malformed = CopyState(state, partitions: malformedPartitions);

        var validation = ProcessContinuationValidator.Validate(plan, malformed);

        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.PartitionStateMismatch
                          && diagnostic.Location == $"/partitions/{duplicateIndex}"
                          && diagnostic.Message.Contains(
                              "at most one unresolved partition occurrence",
                              StringComparison.Ordinal));
    }

    [Fact]
    public void ForEachPartition_MalformedOccurrenceAndProgressIdentityAreDiagnosedWithoutThrowing()
    {
        var (plan, state) = CreatePartitionWaitingState("malformed-partition-identity", '5');
        var partition = Assert.Single(state.Partitions);
        var negativeOccurrence = NewPartition(
            partition.RegistrationId,
            partition.Owner,
            partition.Node,
            occurrence: -1,
            partition.Work,
            partition.Resolved);

        var occurrenceValidation = ProcessContinuationValidator.Validate(
            plan,
            CopyState(state, partitions: [negativeOccurrence]));

        Assert.Contains(
            occurrenceValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.PartitionStateMismatch
                                 && diagnostic.Location == "/partitions/0");
        var defaultOwner = NewPartition(
            partition.RegistrationId,
            default,
            partition.Node,
            partition.Occurrence,
            partition.Work,
            partition.Resolved);
        Assert.Contains(
            ProcessContinuationValidator.Validate(
                plan,
                CopyState(state, partitions: [defaultOwner])).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.PartitionStateMismatch
                                 && diagnostic.Location == "/partitions/0");

        var child = state.Children[0];
        var blankProgress = NewChild(
            child.RegistrationId,
            child.Owner,
            child.Token,
            child.Node,
            child.Occurrence,
            progressIdentity: string.Empty,
            child.Process,
            child.Continuation,
            child.Purpose,
            child.Cancellation,
            child.Disposition,
            child.RequestEmission,
            child.TerminalOutcome,
            child.Result);
        var childIndex = state.Children.IndexOf(child);
        var children = state.Children.SetItem(childIndex, blankProgress);

        var childValidation = ProcessContinuationValidator.Validate(
            plan,
            CopyState(state, children: children));

        Assert.Contains(
            childValidation.Diagnostics,
            diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.ChildStateMismatch
                          && diagnostic.Location == $"/children/{childIndex}");
    }

    [Fact]
    public void ForEachPartition_EffectSummaryDrivesWholeDefinitionAtomicScopeRejection()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/effect-summary-index-shard", 'f');
        var definition = Definition(
            StringCollectionContract,
            "partitions",
            [
                PartitionNode(child, contracts.Request, ProcessChildCancellationPolicy.Propagate),
                new ReturnProcessNode(new("return"), Expr.Const("done")),
                new FailProcessNode(new("fail"), Expr.Const("failed"))
            ]);
        var document = ProcessDefinitionDocuments.Create(
            new("process/index-coordinator-effect-summary"),
            new("revision/1"),
            definition,
            Provenance());
        var context = new ProcessDefinitionValidationContext(
            definitions:
            [new(child, ProcessDefinitionLinkKind.Process, StringContract, StringContract, [], ProcessRecoveryPolicy.ContinueAttempt)],
            interactionContracts: contracts.Catalog);

        var ordinary = ProcessStaticCompiler.Compile(document, context);
        var plan = Assert.IsType<CompiledProcessPlan>(ordinary.Plan);

        Assert.True(ordinary.IsSuccessful, FormatDiagnostics(ordinary.Validation));
        Assert.Equal(ProcessAtomicScopeDemand.None, plan.Options.AtomicScope);
        Assert.Equal(
            [
                ProcessEffectKind.DurableWait,
                ProcessEffectKind.ExternalInteraction,
                ProcessEffectKind.ChildProcess,
                ProcessEffectKind.BoundedParallelWork
            ],
            plan.EffectSummary.Effects
                .Where(static effect => effect.Node == new ExecutionNodeId("partitions"))
                .Select(static effect => effect.Kind));
        Assert.Equal(
            [contracts.Request.Definition, child],
            plan.EffectSummary.Resources
                .Where(static resource => resource.Node == new ExecutionNodeId("partitions"))
                .Select(static resource => resource.Resource));

        var atomic = ProcessStaticCompiler.Compile(
            document,
            context,
            new(ProcessAtomicScopeDemand.WholeDefinition));

        Assert.False(atomic.IsSuccessful);
        Assert.Null(atomic.Plan);
        var partitionIndex = plan.Definition.Nodes.IndexOf(
            plan.Definition.Nodes.Single(static node => node.Id == new ExecutionNodeId("partitions")));
        var partitionLocation = $"/definition/nodes/{partitionIndex}";
        var durableDiagnostic = Assert.Single(
            atomic.Validation.Diagnostics,
            static diagnostic => diagnostic.Code
                == ProcessCompilationDiagnosticCodes.AtomicScopeCrossesDurableBoundary);
        var interactionDiagnostic = Assert.Single(
            atomic.Validation.Diagnostics,
            static diagnostic => diagnostic.Code
                == ProcessCompilationDiagnosticCodes.AtomicScopeContainsExternalInteraction);
        Assert.Equal(partitionLocation, durableDiagnostic.Location);
        Assert.Equal(partitionLocation, interactionDiagnostic.Location);
        foreach (var diagnostic in new[] { durableDiagnostic, interactionDiagnostic })
        {
            var evidence = Assert.IsType<DocumentDiagnosticEvidence>(diagnostic.Evidence);
            Assert.Equal("partitions", evidence.Subject);
            Assert.Equal(Provenance().Source.Reference, Assert.Single(evidence.SourceReferences));
            Assert.Contains("exact resources:", evidence.Observed, StringComparison.Ordinal);
            Assert.Contains(
                contracts.Request.Definition.DefinitionId.Value,
                evidence.Observed,
                StringComparison.Ordinal);
            Assert.Contains(child.DefinitionId.Value, evidence.Observed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InvokeProcess_UsesTheCanonicalRequestReplyPathAndRetainsAuthoredCompensationSemantics()
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/compensate-index-generation", '9');
        ValueBindingId completedValue = new("compensation.completed");
        var plan = Compile(
            Definition(
                StringContract,
                "compensate",
                [
                    new InvokeProcessProcessNode(
                        new("compensate"),
                        child,
                        contracts.Request,
                        ChildOutcomeMapping,
                        Expr.BoundValue(ProcessBindingIds.Input),
                        ProcessChildPurpose.Compensation,
                        ProcessChildCancellationPolicy.Propagate,
                        [
                            new(
                                new("outcome/completed"),
                                new("completed"),
                                new(
                                    Edge("edge/compensation-completed", "return"),
                                    new(completedValue, StringContract))),
                            new(
                                new("outcome/failed"),
                                new("failed"),
                                new(Edge("edge/compensation-failed", "fail"))),
                            new(
                                new("outcome/cancelled"),
                                new("cancelled"),
                                new(Edge("edge/compensation-cancelled", "fail"))),
                            new(
                                new("outcome/terminated"),
                                new("terminated"),
                                new(Edge("edge/compensation-terminated", "fail")))
                        ]),
                    new ReturnProcessNode(new("return"), Expr.BoundValue(completedValue)),
                    new FailProcessNode(new("fail"), Expr.Const("compensation-failed"))
                ]),
            contracts.Catalog,
            [new(child, ProcessDefinitionLinkKind.Process, StringContract, StringContract, [], ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: "process/direct-compensation");
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            Continuation("process-instance/direct-compensation"),
            StringValue("generation/old"));
        var activation = Activation("activation/compensation-start", ProcessActivationCause.Start);

        var requested = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            activation,
            RejectingHost.Instance);
        var replay = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            activation,
            RejectingHost.Instance);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(requested.Emissions));
        var childState = Assert.Single(requested.State.Children);
        var replayChild = Assert.Single(replay.State.Children);
        var target = Assert.IsType<ProcessTokenInteractionTarget>(request.ResponseTarget);

        Assert.Equal(ProcessActivationDisposition.DurableCut, requested.Disposition);
        Assert.Equal(ProcessChildPurpose.Compensation, childState.Purpose);
        Assert.Equal(child, childState.Process);
        Assert.NotEqual(requested.State.Continuation, childState.Continuation);
        Assert.Equal(childState.Continuation, replayChild.Continuation);
        Assert.Equal(childState.RegistrationId, replayChild.RegistrationId);
        Assert.Equal(request.Context.EmissionId, childState.RequestEmission);
        Assert.Equal(
            new ProcessChildRequestTarget(
                childState.Process,
                childState.Continuation,
                ChildOutcomeMapping,
                childState.Owner,
                childState.Occurrence,
                childState.ProgressIdentity),
            request.ChildTarget);
        Assert.Equal(
            request.Context.EmissionId,
            Assert.Single(requested.State.OutstandingRequests).Emission);
        Assert.Contains(
            plan.EffectSummary.Effects,
            static effect => effect.Node == new ExecutionNodeId("compensate")
                             && effect.Kind == ProcessEffectKind.Compensation);

        var directPending = CopyChild(
            childState,
            disposition: ProcessChildDisposition.Pending,
            requestEmission: null,
            replaceRequestEmission: true);
        var pendingValidation = ProcessContinuationValidator.Validate(
            plan,
            CopyState(
                requested.State,
                children: [directPending],
                waits: [],
                outstandingRequests: []));
        Assert.Contains(
            pendingValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.ChildStateMismatch
                                 && diagnostic.Location == "/children/0/disposition"
                                 && diagnostic.Message.Contains("starts atomically", StringComparison.Ordinal));

        var cancellingWhileOwnerIsLive = CopyChild(
            childState,
            disposition: ProcessChildDisposition.CancellationRequested);
        var cancellationValidation = ProcessContinuationValidator.Validate(
            plan,
            CopyState(
                requested.State,
                children: [cancellingWhileOwnerIsLive],
                waits: [],
                outstandingRequests: []));
        Assert.Contains(
            cancellationValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.ChildStateMismatch
                                 && diagnostic.Location == "/children/0/token"
                                 && diagnostic.Message.Contains("owner token remains live", StringComparison.Ordinal));

        var wrongContractReply = Reply(
            childState,
            request,
            contracts.UnrelatedCompletedReply,
            new("completed"),
            "must-not-win",
            "emission/a-reply-wrong-contract");
        var wrongChildReply = Reply(
            childState,
            request,
            contracts.CompletedReply,
            new("completed"),
            "must-not-win-either",
            "emission/aa-reply-wrong-child",
            originContinuation: Continuation("process-instance/wrong-child"));
        var exactReply = Reply(
            childState,
            request,
            contracts.CompletedReply,
            new("completed"),
            "compensation-complete",
            "emission/b-reply-exact");
        var resumed = ProcessReferenceInterpreter.Activate(
            plan,
            requested.State,
            Activation(
                "activation/compensation-replies",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs:
                [
                    new(target, wrongContractReply),
                    new(target, wrongChildReply),
                    new(target, exactReply)
                ]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, resumed.Disposition);
        Assert.Equal(StringValue("compensation-complete"), resumed.State.Terminal.Detail?.Value);
        Assert.Empty(resumed.State.OutstandingRequests);
        var resolvedChild = Assert.Single(resumed.State.Children);
        Assert.Equal(ProcessChildDisposition.Completed, resolvedChild.Disposition);
        Assert.Equal(exactReply.Outcome.Id, resolvedChild.TerminalOutcome);
        Assert.Equal(exactReply.Outcome.Value, resolvedChild.Result);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Rejected,
            resumed.InputAdmissions.Single(admission =>
                admission.Emission == wrongContractReply.Context.EmissionId).Disposition);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Rejected,
            resumed.InputAdmissions.Single(admission =>
                admission.Emission == wrongChildReply.Context.EmissionId).Disposition);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            resumed.InputAdmissions.Single(admission =>
                admission.Emission == exactReply.Context.EmissionId).Disposition);
    }

    [Theory]
    [InlineData(
        ProcessChildCancellationPolicy.Propagate,
        ProcessChildDisposition.CancellationRequested,
        ProcessTraceEventKind.ChildCancellationRequested)]
    [InlineData(
        ProcessChildCancellationPolicy.Detach,
        ProcessChildDisposition.Detached,
        ProcessTraceEventKind.ChildDetached)]
    public void ForEachPartition_CancellationRetainsOnlyTheAuthoredSemanticChildDisposition(
        ProcessChildCancellationPolicy policy,
        ProcessChildDisposition expectedActiveDisposition,
        ProcessTraceEventKind expectedTrace)
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference("process/cancellable-index-shard", 'b');
        var plan = Compile(
            Definition(
                StringCollectionContract,
                "partitions",
                [
                    PartitionNode(child, contracts.Request, policy),
                    new ReturnProcessNode(new("return"), Expr.Const("done")),
                    new FailProcessNode(new("fail"), Expr.Const("failed"))
                ]),
            contracts.Catalog,
            [new(child, ProcessDefinitionLinkKind.Process, StringContract, StringContract, [], ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: $"process/cancellation-{policy}");
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            Continuation($"process-instance/cancellation-{policy}"),
            CollectionValue("shard-c", "shard-a", "shard-b"));
        var waiting = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/cancellation-start", ProcessActivationCause.Start),
            RejectingHost.Instance);

        var restartIntent = new ProcessAttemptRestartIntent(
            waiting.State.Continuation.ProcessInstanceId,
            waiting.State.Continuation.ProcessAttemptId,
            new("process-attempt/replacement"),
            ProcessAttemptCleanupRequirement.RetainEvidence);
        var restartIntents = ProcessChildCancellationIntents.ProjectAttemptRestart(
            waiting.State,
            restartIntent);
        Assert.Equal(
            policy == ProcessChildCancellationPolicy.Propagate ? 2 : 0,
            restartIntents.Length);
        Assert.All(restartIntents, intent =>
        {
            Assert.Equal(waiting.State.Definition, intent.ParentDefinition);
            Assert.Equal(waiting.State.Continuation, intent.ParentContinuation);
            Assert.Equal(ProcessChildCancellationPolicy.Propagate, waiting.State.Children.Single(childState =>
                childState.RegistrationId == intent.ChildRegistrationId).Cancellation);
        });
        Assert.Throws<ArgumentException>(() => ProcessChildCancellationIntents.ProjectAttemptRestart(
            waiting.State,
            new(
                new("process-instance/not-the-parent"),
                waiting.State.Continuation.ProcessAttemptId,
                new("process-attempt/replacement"),
                ProcessAttemptCleanupRequirement.RetainEvidence)));

        var cancelled = ProcessReferenceInterpreter.Activate(
            plan,
            waiting.State,
            Activation(
                "activation/cancel",
                ProcessActivationCause.Control,
                StartedAtUtc.AddMinutes(1),
                new(
                    waiting.State.Continuation.ProcessAttemptId,
                    new("operator.cancel"))),
            RejectingHost.Instance);
        var replay = ProcessReferenceInterpreter.Activate(
            plan,
            waiting.State,
            Activation(
                "activation/cancel",
                ProcessActivationCause.Control,
                StartedAtUtc.AddMinutes(1),
                new(
                    waiting.State.Continuation.ProcessAttemptId,
                    new("operator.cancel"))),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Cancelled, cancelled.Disposition);
        Assert.Empty(cancelled.Emissions);
        Assert.Empty(cancelled.State.OutstandingRequests);
        Assert.Equal(2, cancelled.State.Children.Count(
            childState => childState.Disposition == expectedActiveDisposition));
        Assert.Single(cancelled.State.Children,
            static childState => childState.Disposition == ProcessChildDisposition.CancelledBeforeStart);
        Assert.Equal(2, cancelled.Evidence.Trace.Count(item =>
            item.Kind == expectedTrace));
        Assert.Single(cancelled.Evidence.Trace, static item =>
            item.Kind == ProcessTraceEventKind.ChildCancelledBeforeStart);
        Assert.All(cancelled.State.Children, childState => Assert.Equal(policy, childState.Cancellation));
        var intents = ProcessChildCancellationIntents.Project(cancelled.State);
        Assert.Equal(
            intents.AsEnumerable(),
            ProcessChildCancellationIntents.Project(replay.State).AsEnumerable());
        if (policy == ProcessChildCancellationPolicy.Propagate)
        {
            Assert.Equal(2, intents.Length);
            Assert.All(intents, intent =>
            {
                Assert.Equal(cancelled.State.Definition, intent.ParentDefinition);
                Assert.Equal(cancelled.State.Continuation, intent.ParentContinuation);
                var owned = cancelled.State.Children.Single(childState =>
                    childState.RegistrationId == intent.ChildRegistrationId);
                Assert.Equal(owned.RequestEmission, intent.RequestEmission);
                Assert.Equal(owned.Process, intent.ChildDefinition);
                Assert.Equal(owned.Continuation, intent.ChildContinuation);
            });
        }
        else
        {
            Assert.Empty(intents);
        }

        var nullChild = CopyState(cancelled.State, children: [null!]);
        Assert.Throws<InvalidOperationException>(() =>
            ProcessChildCancellationIntents.Project(nullChild));
        var missingParentIdentity = NewContinuation(
            null!,
            null!,
            cancelled.State.CompletedActivationCount,
            cancelled.State.Tokens,
            cancelled.State.Forks,
            cancelled.State.Children,
            cancelled.State.Partitions,
            cancelled.State.Recurrences,
            cancelled.State.Waits,
            cancelled.State.BufferedInputs,
            cancelled.State.InputReceipts,
            cancelled.State.OutstandingRequests,
            cancelled.State.Terminal);
        Assert.Throws<InvalidOperationException>(() =>
            ProcessChildCancellationIntents.Project(missingParentIdentity));
    }

    [Fact]
    public void RepeatAcrossActivation_CompletesAfterOneDeterministicPollingOccurrencePerActivation()
    {
        var relation = DefinitionReference("relation/human-review-status", 'c');
        var plan = PollingPlan(
            relation,
            new(maximumOccurrences: 5, maximumUnchangedProgressOccurrences: 2),
            "process/human-review-completion");
        var host = new PollingHost(static evaluation =>
            evaluation.Occurrence == 0 ? "pending" : "approved");
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("review/42"));

        var first = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/poll-1", ProcessActivationCause.Start),
            host);
        var firstReplay = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/poll-1", ProcessActivationCause.Start),
            new PollingHost(static evaluation =>
                evaluation.Occurrence == 0 ? "pending" : "approved"));

        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Disposition);
        Assert.Single(host.Evaluations);
        Assert.Equal(1, Assert.Single(first.State.Recurrences).RepeatCount);
        Assert.Equal(
            RecurrenceEvidence(first.State),
            RecurrenceEvidence(firstReplay.State));
        var activeRecurrence = Assert.Single(first.State.Recurrences);
        var impossibleCounts = NewRecurrence(
            activeRecurrence.RegistrationId,
            activeRecurrence.Token,
            activeRecurrence.Node,
            activeRecurrence.Occurrence,
            activeRecurrence.RepeatCount,
            activeRecurrence.RepeatCount,
            activeRecurrence.LastProgress,
            activeRecurrence.Active);
        var impossibleCountsState = CopyState(first.State, recurrences: [impossibleCounts]);
        Assert.Contains(
            ProcessContinuationValidator.Validate(plan, impossibleCountsState).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch
                                 && diagnostic.Location == "/recurrences/0");
        var duplicateRecurrence = NewRecurrence(
            $"zz/{activeRecurrence.RegistrationId}",
            activeRecurrence.Token,
            activeRecurrence.Node,
            activeRecurrence.Occurrence,
            activeRecurrence.RepeatCount,
            activeRecurrence.UnchangedProgressCount,
            activeRecurrence.LastProgress,
            active: false);
        var duplicateRecurrences = new[] { activeRecurrence, duplicateRecurrence }
            .OrderBy(static recurrence => recurrence.RegistrationId, StringComparer.Ordinal)
            .ToImmutableArray();
        var duplicateIndex = duplicateRecurrences.IndexOf(duplicateRecurrence);
        Assert.Contains(
            ProcessContinuationValidator.Validate(
                plan,
                CopyState(first.State, recurrences: duplicateRecurrences)).Diagnostics,
            diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch
                          && diagnostic.Location == $"/recurrences/{duplicateIndex}"
                          && diagnostic.Message.Contains(
                              "at most one recurrence registration",
                              StringComparison.Ordinal));
        var missingProgress = NewRecurrence(
            activeRecurrence.RegistrationId,
            activeRecurrence.Token,
            activeRecurrence.Node,
            activeRecurrence.Occurrence,
            activeRecurrence.RepeatCount,
            activeRecurrence.UnchangedProgressCount,
            PortableValue.Missing(activeRecurrence.LastProgress!.Contract),
            activeRecurrence.Active);
        var missingProgressState = CopyState(first.State, recurrences: [missingProgress]);
        Assert.Contains(
            ProcessContinuationValidator.Validate(plan, missingProgressState).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch
                                 && diagnostic.Location == "/recurrences/0");
        var negativeOccurrence = NewRecurrence(
            activeRecurrence.RegistrationId,
            activeRecurrence.Token,
            activeRecurrence.Node,
            occurrence: -1,
            activeRecurrence.RepeatCount,
            activeRecurrence.UnchangedProgressCount,
            activeRecurrence.LastProgress,
            activeRecurrence.Active);
        Assert.Contains(
            ProcessContinuationValidator.Validate(
                plan,
                CopyState(first.State, recurrences: [negativeOccurrence])).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch
                                 && diagnostic.Location == "/recurrences/0");
        var defaultToken = NewRecurrence(
            activeRecurrence.RegistrationId,
            default,
            activeRecurrence.Node,
            activeRecurrence.Occurrence,
            activeRecurrence.RepeatCount,
            activeRecurrence.UnchangedProgressCount,
            activeRecurrence.LastProgress,
            activeRecurrence.Active);
        Assert.Contains(
            ProcessContinuationValidator.Validate(
                plan,
                CopyState(first.State, recurrences: [defaultToken])).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch
                                 && diagnostic.Location == "/recurrences/0");
        var activeWait = Assert.Single(first.State.Waits, static wait =>
            wait is { Active: true, Kind: ProcessWaitKind.RepeatAcrossActivation });
        var tamperedWait = NewWait(
            new("process-wait:v1:sha256:tampered-recurrence-registration"),
            activeWait.Token,
            activeWait.Node,
            activeWait.Occurrence,
            activeWait.Kind,
            activeWait.RegisteredAtUtc,
            activeWait.Timers,
            activeWait.Active,
            activeWait.WinnerClause,
            activeWait.WinnerInput,
            activeWait.ObligationEmission);
        var tampered = CopyState(first.State, waits: [tamperedWait]);
        Assert.Contains(
            ProcessContinuationValidator.Validate(plan, tampered).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch
                                 && diagnostic.Location == "/waits/0");
        var missingContinuation = NewContinuation(
            first.State.Definition,
            null!,
            first.State.CompletedActivationCount,
            first.State.Tokens,
            first.State.Forks,
            first.State.Children,
            first.State.Partitions,
            first.State.Recurrences,
            first.State.Waits,
            first.State.BufferedInputs,
            first.State.InputReceipts,
            first.State.OutstandingRequests,
            first.State.Terminal);
        Assert.Contains(
            ProcessContinuationValidator.Validate(plan, missingContinuation).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.StateMemberInvalid
                                 && diagnostic.Location == "/continuation");

        var completed = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            Activation(
                "activation/poll-2",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(1)),
            host);

        Assert.Equal(ProcessActivationDisposition.Completed, completed.Disposition);
        Assert.Equal(2, host.Evaluations.Count);
        Assert.Equal(StringValue("approved"), completed.State.Terminal.Detail?.Value);
        Assert.False(Assert.Single(completed.State.Recurrences).Active);
        var missingRecurrenceTombstone = CopyState(completed.State, waits: []);
        Assert.Contains(
            ProcessContinuationValidator.Validate(plan, missingRecurrenceTombstone).Diagnostics,
            static diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch);
    }

    [Fact]
    public async Task RepeatAcrossActivation_CheckpointRoundTripPreservesProgressAndRecoveredRuntimeResumesNextOccurrence()
    {
        var relation = DefinitionReference("relation/recovered-human-review-status", 'f');
        var plan = PollingPlan(
            relation,
            new(maximumOccurrences: 5, maximumUnchangedProgressOccurrences: 2),
            "process/recovered-human-review-completion");
        var firstHost = new PollingHost(static _ => "pending");
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, firstHost);
        var initialized = await runtime.InitializeAsync(
            Context(StartedAtUtc),
            plan,
            Start(plan, StringValue("review/42")));
        var initializedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;

        var first = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(1)),
            plan,
            initializedCheckpoint.ContinuationIdentity,
            Activation(
                "activation/recovered-poll-1",
                ProcessActivationCause.Start,
                StartedAtUtc.AddMinutes(1)));

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, first.Disposition);
        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Decision?.Disposition);
        Assert.Equal(0, Assert.Single(firstHost.Evaluations).Occurrence);
        var firstCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(first.Snapshot).Checkpoint;
        var firstRecurrence = Assert.Single(firstCheckpoint.Continuation.Recurrences);
        Assert.True(firstRecurrence.Active);
        Assert.Equal(1, firstRecurrence.RepeatCount);
        Assert.Equal(StringValue("pending"), firstRecurrence.LastProgress);

        var json = ProcessDurableCheckpointJsonSerializer.Serialize(firstCheckpoint);
        var restored = ProcessDurableCheckpointJsonSerializer.Deserialize(json, plan);
        Assert.Equal(json, ProcessDurableCheckpointJsonSerializer.Serialize(restored));
        Assert.Equal(
            RecurrenceEvidence(firstCheckpoint.Continuation),
            RecurrenceEvidence(restored.Continuation));

        var recoveredStore = new InMemoryProcessDurableStore();
        var recovered = await recoveredStore.InitializeAsync(
            Context(restored.UpdatedAtUtc),
            new("commit/restore-recovered-human-review"),
            restored);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, recovered.Disposition);
        var recoveredHost = new PollingHost(static _ => "approved");
        var recoveredRuntime = Runtime(recoveredStore, recoveredHost);

        var completed = await recoveredRuntime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(2)),
            plan,
            restored.ContinuationIdentity,
            Activation(
                "activation/recovered-poll-2",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(2)));

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, completed.Disposition);
        Assert.Equal(ProcessActivationDisposition.Completed, completed.Decision?.Disposition);
        Assert.Equal(2, Assert.Single(recoveredHost.Evaluations).Occurrence);
        var completedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(completed.Snapshot).Checkpoint;
        Assert.Equal(StringValue("approved"), completedCheckpoint.Continuation.Terminal.Detail?.Value);
        Assert.False(Assert.Single(completedCheckpoint.Continuation.Recurrences).Active);
    }

    [Fact]
    public async Task RepeatAcrossActivation_BodyDurableCutRoundTripsWhileRecurrenceWaitIsATombstone()
    {
        var catalogResult = InteractionContractCatalog.TryCreate([], out var catalog);
        Assert.True(catalogResult.IsValid, FormatDiagnostics(catalogResult));
        var plan = Compile(
            Definition(
                StringContract,
                "repeat",
                [
                    new RepeatAcrossActivationProcessNode(
                        new("repeat"),
                        Expr.Const(true),
                        Expr.Const("progress"),
                        StringContract,
                        new(maximumOccurrences: 4, maximumUnchangedProgressOccurrences: 2),
                        Edge("edge/repeat-cut", "body-cut"),
                        Edge("edge/repeat-completed", "return"),
                        Edge("edge/repeat-exhausted", "fail"),
                        Edge("edge/repeat-stalled", "fail")),
                    new DurableCutProcessNode(
                        new("body-cut"),
                        Edge("edge/body-cut-repeat", "repeat")),
                    new ReturnProcessNode(new("return"), Expr.Const("done")),
                    new FailProcessNode(new("fail"), Expr.Const("failed"))
                ]),
            Assert.IsType<InteractionContractCatalog>(catalog),
            definitionId: "process/repeat-body-durable-cut");
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, RejectingHost.Instance);
        var initialized = await runtime.InitializeAsync(
            Context(StartedAtUtc),
            plan,
            Start(plan, StringValue("input")));
        var initialCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var first = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(1)),
            plan,
            initialCheckpoint.ContinuationIdentity,
            Activation(
                "activation/repeat-body-first",
                ProcessActivationCause.Start,
                StartedAtUtc.AddMinutes(1)));
        var firstCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(first.Snapshot).Checkpoint;
        Assert.Single(firstCheckpoint.Continuation.Waits, static wait =>
            wait is { Active: true, Kind: ProcessWaitKind.RepeatAcrossActivation });

        var bodyCut = await runtime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(2)),
            plan,
            firstCheckpoint.ContinuationIdentity,
            Activation(
                "activation/repeat-body-cut",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(2)));
        var bodyCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(bodyCut.Snapshot).Checkpoint;
        Assert.True(Assert.Single(bodyCheckpoint.Continuation.Recurrences).Active);
        Assert.Single(bodyCheckpoint.Continuation.Waits, static wait =>
            wait is { Active: false, Kind: ProcessWaitKind.RepeatAcrossActivation });
        Assert.Single(bodyCheckpoint.Continuation.Waits, static wait =>
            wait is { Active: true, Kind: ProcessWaitKind.DurableCut });
        var bodyValidation = ProcessContinuationValidator.Validate(plan, bodyCheckpoint.Continuation);
        Assert.True(bodyValidation.IsValid, FormatDiagnostics(bodyValidation));

        var json = ProcessDurableCheckpointJsonSerializer.Serialize(bodyCheckpoint);
        var restored = ProcessDurableCheckpointJsonSerializer.Deserialize(json, plan);
        Assert.Equal(json, ProcessDurableCheckpointJsonSerializer.Serialize(restored));
        var recoveredStore = new InMemoryProcessDurableStore();
        var recovered = await recoveredStore.InitializeAsync(
            Context(restored.UpdatedAtUtc),
            new("commit/restore-repeat-body-cut"),
            restored);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, recovered.Disposition);
        var recoveredRuntime = Runtime(recoveredStore, RejectingHost.Instance);

        var repeated = await recoveredRuntime.ActivateAsync(
            Context(StartedAtUtc.AddMinutes(3)),
            plan,
            restored.ContinuationIdentity,
            Activation(
                "activation/repeat-body-recovered",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(3)));
        var repeatedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(repeated.Snapshot).Checkpoint;
        var recurrence = Assert.Single(repeatedCheckpoint.Continuation.Recurrences);
        Assert.True(recurrence.Active);
        Assert.Equal(2, recurrence.RepeatCount);
        Assert.Single(repeatedCheckpoint.Continuation.Waits, static wait =>
            wait is { Active: true, Kind: ProcessWaitKind.RepeatAcrossActivation });
    }

    [Fact]
    public void RepeatAcrossActivation_RoutesToExhaustedAfterTheFiniteOccurrenceBudget()
    {
        var relation = DefinitionReference("relation/exhausting-human-review", 'd');
        var plan = PollingPlan(
            relation,
            new(maximumOccurrences: 2, maximumUnchangedProgressOccurrences: 1),
            "process/human-review-exhaustion");
        var host = new PollingHost(static evaluation => $"pending/{evaluation.Occurrence}");
        var state = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("review/42"));

        var first = ProcessReferenceInterpreter.Activate(
            plan,
            state,
            Activation("activation/exhaustion-1", ProcessActivationCause.Start),
            host);
        var second = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            Activation(
                "activation/exhaustion-2",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(1)),
            host);
        var exhausted = ProcessReferenceInterpreter.Activate(
            plan,
            second.State,
            Activation(
                "activation/exhaustion-3",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(2)),
            host);

        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Disposition);
        Assert.Equal(ProcessActivationDisposition.DurableCut, second.Disposition);
        Assert.Equal(ProcessActivationDisposition.Failed, exhausted.Disposition);
        Assert.Equal(3, host.Evaluations.Count);
        Assert.Equal(StringValue("polling-exhausted"), exhausted.State.Terminal.Detail?.Value);
        var recurrence = Assert.Single(exhausted.State.Recurrences);
        Assert.Equal(2, recurrence.RepeatCount);
        Assert.False(recurrence.Active);
        Assert.Equal(2, exhausted.State.Waits.Length);
        var laterTombstone = exhausted.State.Waits
            .OrderBy(static wait => wait.Occurrence)
            .Last();
        var forgedTombstone = NewWait(
            new("process-wait:v1:sha256:forged-later-recurrence-tombstone"),
            laterTombstone.Token,
            laterTombstone.Node,
            laterTombstone.Occurrence,
            laterTombstone.Kind,
            laterTombstone.RegisteredAtUtc,
            laterTombstone.Timers,
            laterTombstone.Active,
            laterTombstone.WinnerClause,
            laterTombstone.WinnerInput,
            laterTombstone.ObligationEmission);
        var forgedWaits = exhausted.State.Waits
            .Select(wait => wait == laterTombstone ? forgedTombstone : wait)
            .OrderBy(static wait => wait.RegistrationId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var forgedIndex = forgedWaits.IndexOf(forgedTombstone);

        var validation = ProcessContinuationValidator.Validate(
            plan,
            CopyState(exhausted.State, waits: forgedWaits));

        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.WaitShapeMismatch
                          && diagnostic.Location == $"/waits/{forgedIndex}/registrationId");
    }

    [Fact]
    public void RepeatAcrossActivation_FirstProgressFailureLeavesNoUnadmittedRecurrenceState()
    {
        var numericFailure = Expr.Eq(
            Expr.Div(Expr.Const(1), Expr.Const(0)),
            Expr.Const(0));
        var progress = new ConditionalExpr(
            numericFailure,
            Expr.Const("progress"),
            Expr.Const("progress"),
            StringContract.Type);
        var plan = Compile(
            Definition(
                StringContract,
                "repeat",
                [
                    new RepeatAcrossActivationProcessNode(
                        new("repeat"),
                        Expr.Const(true),
                        progress,
                        StringContract,
                        new(maximumOccurrences: 4, maximumUnchangedProgressOccurrences: 2),
                        Edge("edge/repeat", "repeat"),
                        Edge("edge/completed", "return"),
                        Edge("edge/exhausted", "fail"),
                        Edge("edge/stalled", "fail")),
                    new ReturnProcessNode(new("return"), Expr.Const("done")),
                    new FailProcessNode(new("fail"), Expr.Const("failed"))
                ]),
            definitionId: "process/recurrence-first-progress-failure");
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));

        var failed = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/recurrence-first-progress-failure", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.Empty(failed.State.Recurrences);
        Assert.Empty(failed.State.Waits);
        Assert.Contains(
            failed.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.ExpressionFailed);
        var validation = ProcessContinuationValidator.Validate(plan, failed.State);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));
    }

    [Fact]
    public void RepeatAcrossActivation_RoutesToStalledWhenAuthoredProgressStopsChanging()
    {
        var relation = DefinitionReference("relation/stalled-human-review", 'e');
        var plan = PollingPlan(
            relation,
            new(maximumOccurrences: 4, maximumUnchangedProgressOccurrences: 1),
            "process/human-review-stall");
        var host = new PollingHost(static _ => "pending");
        var state = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("review/42"));

        var first = ProcessReferenceInterpreter.Activate(
            plan,
            state,
            Activation("activation/stall-1", ProcessActivationCause.Start),
            host);
        var second = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            Activation(
                "activation/stall-2",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(1)),
            host);
        var stalled = ProcessReferenceInterpreter.Activate(
            plan,
            second.State,
            Activation(
                "activation/stall-3",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(2)),
            host);

        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Disposition);
        Assert.Equal(ProcessActivationDisposition.DurableCut, second.Disposition);
        Assert.Equal(ProcessActivationDisposition.Failed, stalled.Disposition);
        Assert.Equal(3, host.Evaluations.Count);
        Assert.Equal(StringValue("polling-stalled"), stalled.State.Terminal.Detail?.Value);
        var recurrence = Assert.Single(stalled.State.Recurrences);
        Assert.Equal(2, recurrence.RepeatCount);
        Assert.Equal(2, recurrence.UnchangedProgressCount);
        Assert.False(recurrence.Active);
    }

    static ForEachPartitionProcessNode PartitionNode(
        ExecutionDefinitionReference child,
        RequestContractReference request,
        ProcessChildCancellationPolicy cancellation,
        Func<ValueBindingId, Expr>? childInput = null,
        ProcessWorkLimits? limits = null,
        ProcessPartitionFailurePolicy failure = ProcessPartitionFailurePolicy.FailFast,
        Func<ValueBindingId, Expr>? capacityIdentity = null,
        ImmutableArray<ProcessCapacityDomainLimit> capacityDomains = default)
    {
        ProcessOutputBinding partition = new(new("partition.item"), StringContract);
        return new(
            new("partitions"),
            Expr.BoundValue(ProcessBindingIds.Input),
            partition,
            Expr.BoundValue(partition.Binding),
            child,
            request,
            ChildOutcomeMapping,
            childInput?.Invoke(partition.Binding) ?? Expr.BoundValue(partition.Binding),
            limits ?? new(maximumItems: 3, maximumStartsPerActivation: 2, maximumParallelism: 2),
            failure,
            capacityIdentity?.Invoke(partition.Binding),
            capacityDomains.IsDefault ? [] : capacityDomains,
            cancellation,
            Edge("edge/partitions-completed", "return"),
            Edge("edge/partitions-failed", "fail"));
    }

    static Expr RuntimeFailingShardInput(ValueBindingId partition)
    {
        var value = Expr.BoundValue(partition);
        var numericFailure = Expr.Eq(
            Expr.Div(Expr.Const(1), Expr.Const(0)),
            Expr.Const(0));
        return new ConditionalExpr(
            Expr.Eq(value, Expr.Const("shard-b")),
            new ConditionalExpr(numericFailure, value, value, StringContract.Type),
            value,
            StringContract.Type);
    }

    static Expr CapacityDomainByShard(ValueBindingId partition)
    {
        var value = Expr.BoundValue(partition);
        return new ConditionalExpr(
            Expr.Eq(value, Expr.Const("shard-a")),
            Expr.Const("target/a"),
            new ConditionalExpr(
                Expr.Eq(value, Expr.Const("shard-b")),
                Expr.Const("target/a"),
                Expr.Const("target/b"),
                StringContract.Type),
            StringContract.Type);
    }

    static (CompiledProcessPlan Plan, ProcessContinuationState State) CreatePartitionWaitingState(
        string identity,
        char fingerprintDigit)
    {
        var contracts = ChildRequestContracts.Create();
        var child = DefinitionReference($"process/{identity}-child", fingerprintDigit);
        var plan = Compile(
            Definition(
                StringCollectionContract,
                "partitions",
                [
                    PartitionNode(child, contracts.Request, ProcessChildCancellationPolicy.Propagate),
                    new ReturnProcessNode(new("return"), Expr.Const("done")),
                    new FailProcessNode(new("fail"), Expr.Const("failed"))
                ]),
            contracts.Catalog,
            [new(child, ProcessDefinitionLinkKind.Process, StringContract, StringContract, [], ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId: $"process/{identity}");
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            Continuation($"process-instance/{identity}"),
            CollectionValue("shard-c", "shard-a", "shard-b"));
        var state = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation($"activation/{identity}", ProcessActivationCause.Start),
            RejectingHost.Instance).State;
        return (plan, state);
    }

    static CompiledProcessPlan PollingPlan(
        ExecutionDefinitionReference relation,
        ProcessRecurrencePolicy policy,
        string definitionId)
    {
        var catalogResult = InteractionContractCatalog.TryCreate([], out var catalog);
        Assert.True(catalogResult.IsValid, FormatDiagnostics(catalogResult));
        ValueBindingId status = new("poll.status");
        return Compile(
            Definition(
                StringContract,
                "poll",
                [
                    new EvaluateRelationProcessNode(
                        new("poll"),
                        relation,
                        Expr.BoundValue(ProcessBindingIds.Input),
                        new(
                            Edge("edge/poll-repeat", "repeat"),
                            new(status, StringContract))),
                    new RepeatAcrossActivationProcessNode(
                        new("repeat"),
                        Expr.Ne(Expr.BoundValue(status), Expr.Const("approved")),
                        Expr.BoundValue(status),
                        StringContract,
                        policy,
                        Edge("edge/repeat-poll", "poll"),
                        Edge("edge/repeat-completed", "completed"),
                        Edge("edge/repeat-exhausted", "exhausted"),
                        Edge("edge/repeat-stalled", "stalled")),
                    new ReturnProcessNode(new("completed"), Expr.BoundValue(status)),
                    new FailProcessNode(new("exhausted"), Expr.Const("polling-exhausted")),
                    new FailProcessNode(new("stalled"), Expr.Const("polling-stalled"))
                ]),
            Assert.IsType<InteractionContractCatalog>(catalog),
            definitions:
            [new(relation, ProcessDefinitionLinkKind.RelationQuery, StringContract, StringContract)],
            definitionId: definitionId);
    }

    static CompiledProcessPlan DirectChildPlan(
        ChildRequestContracts contracts,
        ExecutionDefinitionReference child,
        string definitionId)
    {
        ValueBindingId completedValue = new("child.completed");
        return Compile(
            Definition(
                StringContract,
                "child",
                [
                    new InvokeProcessProcessNode(
                        new("child"),
                        child,
                        contracts.Request,
                        ChildOutcomeMapping,
                        Expr.BoundValue(ProcessBindingIds.Input),
                        ProcessChildPurpose.Work,
                        ProcessChildCancellationPolicy.Propagate,
                        [
                            new(
                                new("child/completed"),
                                ChildOutcomeMapping.Completed,
                                new(Edge("edge/child-completed", "return"), new(completedValue, StringContract))),
                            new(
                                new("child/failed"),
                                ChildOutcomeMapping.Failed,
                                new(Edge("edge/child-failed", "fail"))),
                            new(
                                new("child/cancelled"),
                                ChildOutcomeMapping.Cancelled,
                                new(Edge("edge/child-cancelled", "fail"))),
                            new(
                                new("child/terminated"),
                                ChildOutcomeMapping.Terminated,
                                new(Edge("edge/child-terminated", "fail")))
                        ]),
                    new ReturnProcessNode(new("return"), Expr.BoundValue(completedValue)),
                    new FailProcessNode(new("fail"), Expr.Const("child-failed"))
                ]),
            contracts.Catalog,
            [new(
                child,
                ProcessDefinitionLinkKind.Process,
                StringContract,
                StringContract,
                [],
                ProcessRecoveryPolicy.ContinueAttempt)],
            definitionId);
    }

    static CompiledProcessPlan Compile(
        CanonicalProcessDefinition definition,
        InteractionContractCatalog? contracts = null,
        ImmutableArray<ProcessDefinitionLink> definitions = default,
        string definitionId = "process/higher-order-reference-tests")
    {
        var document = ProcessDefinitionDocuments.Create(
            new(definitionId),
            new("revision/1"),
            definition,
            Provenance());
        var result = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(
                definitions: definitions.IsDefault ? null : definitions,
                interactionContracts: contracts));
        Assert.True(result.IsSuccessful, FormatDiagnostics(result.Validation));
        return Assert.IsType<CompiledProcessPlan>(result.Plan);
    }

    static CanonicalProcessDefinition Definition(
        ValueContract input,
        string entry,
        params ReadOnlySpan<CanonicalProcessNode> nodes) => new(
        input,
        StringContract,
        new(entry),
        [.. nodes],
        ProcessRecoveryPolicy.ContinueAttempt);

    static ProcessStartReceipt Start(CompiledProcessPlan plan, PortableValue input)
    {
        var continuation = Continuation("process-instance/index-coordinator");
        var request = new ProcessStartRequest(
            ProcessStartRequest.CurrentSchemaVersion,
            plan.DefinitionReference,
            new(
                new("start-command/index-coordinator"),
                new("start-idempotency/index-coordinator"),
                continuation.ProcessInstanceId,
                new("operator/tests", Authority, "policy/tests/allow"),
                StartedAtUtc,
                Provenance()),
            continuation,
            input);
        return new(request, StartedAtUtc);
    }

    static ProcessDurableRuntime Runtime(
        IProcessDurableStore store,
        DurableRequestBinding binding,
        IDurableOperationAdapter? adapter = null) => new(
            store,
            RejectingHost.Instance,
            new(
                workerId: "worker/higher-order-reference-tests",
                workerLease: TimeSpan.FromMinutes(5)),
            new BindingResolver(binding),
            operationAdapterResolver: adapter is null ? null : new AdapterResolver(adapter));

    static ProcessDurableRuntime Runtime(
        IProcessDurableStore store,
        IProcessReferenceHost host) => new(
            store,
            host,
            new(
                workerId: "worker/higher-order-reference-tests",
                workerLease: TimeSpan.FromMinutes(5)));

    static OperationContext Context(DateTimeOffset utcNow) =>
        OperationContext.Create(timeProvider: new FixedTimeProvider(utcNow));

    static ProcessContinuationIdentity Continuation(
        string processInstance = "process-instance/higher-order-reference-tests") => new(
        new(processInstance),
        new("process-attempt/1"));

    static ProcessActivation Activation(
        string id,
        ProcessActivationCause cause,
        DateTimeOffset? observedAtUtc = null,
        ProcessCancellationIntent? cancellation = null,
        ImmutableArray<ProcessActivationInput> inputs = default) => new(
        new(id),
        cause,
        observedAtUtc ?? StartedAtUtc,
        new(
            Authority,
            new("correlation/higher-order-reference-tests"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            Provenance()),
        inputs,
        cancellation);

    static ReplyEnvelope Reply(
        ProcessChildState child,
        RequestEnvelope request,
        ReplyContractReference replyContract,
        RequestTerminalOutcomeId outcome,
        string payload,
        string emission,
        ExecutionDefinitionReference? originDefinition = null,
        ProcessContinuationIdentity? originContinuation = null) => Reply(
            child,
            request,
            replyContract,
            new RequestResultOutcome(outcome, StringValue(payload)),
            emission,
            originDefinition,
            originContinuation);

    static ReplyEnvelope Reply(
        ProcessChildState child,
        RequestEnvelope request,
        ReplyContractReference replyContract,
        RequestTerminalOutcome outcome,
        string emission,
        ExecutionDefinitionReference? originDefinition = null,
        ProcessContinuationIdentity? originContinuation = null)
    {
        var target = Assert.IsType<ProcessTokenInteractionTarget>(request.ResponseTarget);
        return new(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new(emission),
                new ProcessInteractionOrigin(
                    originDefinition ?? child.Process,
                    new("source/index-worker"),
                    originContinuation ?? child.Continuation,
                    new("activation/index-worker"),
                    target.Token),
                new("correlation/higher-order-reference-tests"),
                request.Context.EmissionId,
                Authority,
                new($"idempotency/{emission}"),
                ordering: null,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                Provenance()),
            replyContract,
            request.Context.EmissionId,
            outcome);
    }

    static ProcessContinuationState CopyState(
        ProcessContinuationState source,
        ImmutableArray<ProcessChildState> children = default,
        ImmutableArray<ProcessPartitionState> partitions = default,
        ImmutableArray<ProcessRecurrenceState> recurrences = default,
        ImmutableArray<ProcessWaitState> waits = default,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests = default) => NewContinuation(
            source.Definition,
            source.Continuation,
            source.CompletedActivationCount,
            source.Tokens,
            source.Forks,
            children.IsDefault ? source.Children : children,
            partitions.IsDefault ? source.Partitions : partitions,
            recurrences.IsDefault ? source.Recurrences : recurrences,
            waits.IsDefault ? source.Waits : waits,
            source.BufferedInputs,
            source.InputReceipts,
            outstandingRequests.IsDefault ? source.OutstandingRequests : outstandingRequests,
            source.Terminal);

    static ProcessChildState CopyChild(
        ProcessChildState source,
        ProcessChildDisposition? disposition = null,
        EmissionId? requestEmission = null,
        bool replaceRequestEmission = false) => NewChild(
            source.RegistrationId,
            source.Owner,
            source.Token,
            source.Node,
            source.Occurrence,
            source.ProgressIdentity,
            source.Process,
            source.Continuation,
            source.Purpose,
            source.Cancellation,
            disposition ?? source.Disposition,
            replaceRequestEmission ? requestEmission : source.RequestEmission,
            source.TerminalOutcome,
            source.Result);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessContinuationState NewContinuation(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation,
        long completedActivationCount,
        ImmutableArray<ProcessTokenState> tokens,
        ImmutableArray<ProcessForkState> forks,
        ImmutableArray<ProcessChildState> children,
        ImmutableArray<ProcessPartitionState> partitions,
        ImmutableArray<ProcessRecurrenceState> recurrences,
        ImmutableArray<ProcessWaitState> waits,
        ImmutableArray<ProcessBufferedInput> bufferedInputs,
        ImmutableArray<ProcessInputReceipt> inputReceipts,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests,
        ExecutionTerminalOutcome terminal);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessChildState NewChild(
        string registrationId,
        TokenId owner,
        TokenId token,
        ExecutionNodeId node,
        long occurrence,
        string? progressIdentity,
        ExecutionDefinitionReference process,
        ProcessContinuationIdentity continuation,
        ProcessChildPurpose purpose,
        ProcessChildCancellationPolicy cancellation,
        ProcessChildDisposition disposition,
        EmissionId? requestEmission,
        RequestTerminalOutcomeId? terminalOutcome,
        PortableValue? result);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessPartitionState NewPartition(
        string registrationId,
        TokenId owner,
        ExecutionNodeId node,
        long occurrence,
        ImmutableArray<ProcessPartitionWorkState> work,
        bool resolved);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessRecurrenceState NewRecurrence(
        string registrationId,
        TokenId token,
        ExecutionNodeId node,
        long occurrence,
        int repeatCount,
        int unchangedProgressCount,
        PortableValue? lastProgress,
        bool active);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessWaitState NewWait(
        ProcessWaitRegistrationId registrationId,
        TokenId token,
        ExecutionNodeId node,
        long occurrence,
        ProcessWaitKind kind,
        DateTimeOffset registeredAtUtc,
        ImmutableArray<ProcessTimerState> timers,
        bool active,
        ExecutionNodeId? winnerClause,
        EmissionId? winnerInput,
        EmissionId? obligationEmission);

    static object[] ChildIdentityEvidence(ProcessContinuationState state) =>
        [.. state.Children.Select(static child => new
        {
            child.RegistrationId,
            child.Owner,
            child.Token,
            child.Node,
            child.Occurrence,
            child.ProgressIdentity,
            child.Process,
            child.Continuation,
            child.Purpose,
            child.Cancellation,
            child.Disposition,
            child.RequestEmission
        })];

    static object[] RequestIdentityEvidence(ImmutableArray<InteractionEnvelope> emissions) =>
        [.. emissions.OfType<RequestEnvelope>().Select(static request => new
        {
            request.Context.EmissionId,
            request.Context.IdempotencyKey,
            request.ResponseTarget,
            request.ChildTarget,
            request.Payload
        })];

    static object[] RecurrenceEvidence(ProcessContinuationState state) =>
        [.. state.Recurrences.Select(static recurrence => new
        {
            recurrence.RegistrationId,
            recurrence.Token,
            recurrence.Node,
            recurrence.Occurrence,
            recurrence.RepeatCount,
            recurrence.UnchangedProgressCount,
            recurrence.LastProgress,
            recurrence.Active
        })];

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static PortableValue CollectionValue(params string[] values) => PortableValue.Concrete(
        StringCollectionContract,
        ObservationValue.FromArray(
            [.. values.Select(static value => ObservationValue.FromString(value))]));

    static ExecutionDefinitionReference DefinitionReference(string id, char fingerprintDigit) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprintDigit, 64)));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static InteractionValueSchema StringSchema(string revision) =>
        new(StringContract, new(revision));

    static ExecutionProvenance Provenance() => new(
        new("process-higher-order-reference-tests", "1"),
        new("tests/execution-kernel/process-higher-order-reference"),
        DocumentOrigin.Generated);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static string FormatDiagnostics(IEnumerable<DocumentValidationDiagnostic> diagnostics) => string.Join(
        Environment.NewLine,
        diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed class ChildRequestContracts
    {
        ChildRequestContracts(
            InteractionContractCatalog catalog,
            RequestContractReference request,
            ReplyContractReference completedReply,
            ReplyContractReference failedReply,
            ReplyContractReference unrelatedCompletedReply,
            DurableRequestBinding binding)
        {
            Catalog = catalog;
            Request = request;
            CompletedReply = completedReply;
            FailedReply = failedReply;
            UnrelatedCompletedReply = unrelatedCompletedReply;
            Binding = binding;
        }

        internal InteractionContractCatalog Catalog { get; }

        internal RequestContractReference Request { get; }

        internal ReplyContractReference CompletedReply { get; }

        internal ReplyContractReference FailedReply { get; }

        internal ReplyContractReference UnrelatedCompletedReply { get; }

        internal DurableRequestBinding Binding { get; }

        internal static ChildRequestContracts Create()
        {
            var requestDocument = InteractionContractDocuments.Create(
                new("interaction/request/index-shard"),
                new("revision/1"),
                new RequestContractDefinition(
                    StringSchema("index-shard-request/v1"),
                    new(
                        [
                            new RequestResultDefinition(
                                new("completed"),
                                StringSchema("index-shard-completed/v1")),
                            new RequestFailureDefinition(
                                new("failed"),
                                StringSchema("index-shard-failed/v1")),
                            new RequestFailureDefinition(
                                new("cancelled"),
                                StringSchema("index-shard-cancelled/v1")),
                            new RequestFailureDefinition(
                                new("terminated"),
                                StringSchema("index-shard-failed/v1"))
                        ],
                        RequestOptionalTerminalSemantics.Unsupported,
                        RequestOptionalTerminalSemantics.Unsupported,
                        RequestResultDisposition.Observe,
                        RequestResultDisposition.Reject,
                        RequestResultDisposition.ReusePriorDisposition,
                        RequestRetrySemantics.ReconcileBeforeRetry,
                        RequestResolutionSemantics.Reconcile,
                        RequestResolutionSemantics.Reconcile,
                        TimeSpan.FromDays(30))),
                Provenance());
            RequestContractReference request = new(Reference(requestDocument));
            var completedDocument = InteractionContractDocuments.Create(
                new("interaction/reply/index-shard-completed"),
                new("revision/1"),
                new ReplyContractDefinition(request, new("completed")),
                Provenance());
            var failedDocument = InteractionContractDocuments.Create(
                new("interaction/reply/index-shard-failed"),
                new("revision/1"),
                new ReplyContractDefinition(request, new("failed")),
                Provenance());
            var cancelledDocument = InteractionContractDocuments.Create(
                new("interaction/reply/index-shard-cancelled"),
                new("revision/1"),
                new ReplyContractDefinition(request, new("cancelled")),
                Provenance());
            var terminatedDocument = InteractionContractDocuments.Create(
                new("interaction/reply/index-shard-terminated"),
                new("revision/1"),
                new ReplyContractDefinition(request, new("terminated")),
                Provenance());
            var unrelatedRequestDocument = InteractionContractDocuments.Create(
                new("interaction/request/unrelated-index-shard"),
                new("revision/1"),
                new RequestContractDefinition(
                    StringSchema("unrelated-index-shard-request/v1"),
                    new(
                        [
                            new RequestResultDefinition(
                                new("completed"),
                                StringSchema("unrelated-index-shard-completed/v1"))
                        ],
                        RequestOptionalTerminalSemantics.Unsupported,
                        RequestOptionalTerminalSemantics.Unsupported,
                        RequestResultDisposition.Observe,
                        RequestResultDisposition.Reject,
                        RequestResultDisposition.ReusePriorDisposition,
                        RequestRetrySemantics.StableIdentity,
                        RequestResolutionSemantics.Reconcile,
                        RequestResolutionSemantics.Escalate,
                        TimeSpan.FromDays(30))),
                Provenance());
            RequestContractReference unrelatedRequest = new(Reference(unrelatedRequestDocument));
            var unrelatedCompletedDocument = InteractionContractDocuments.Create(
                new("interaction/reply/unrelated-index-shard-completed"),
                new("revision/1"),
                new ReplyContractDefinition(unrelatedRequest, new("completed")),
                Provenance());
            ReplyContractReference completed = new(Reference(completedDocument));
            ReplyContractReference failed = new(Reference(failedDocument));
            ReplyContractReference cancelled = new(Reference(cancelledDocument));
            ReplyContractReference terminated = new(Reference(terminatedDocument));
            ReplyContractReference unrelatedCompleted = new(Reference(unrelatedCompletedDocument));
            var catalogValidation = InteractionContractCatalog.TryCreate(
                [
                    requestDocument,
                    completedDocument,
                    failedDocument,
                    cancelledDocument,
                    terminatedDocument,
                    unrelatedRequestDocument,
                    unrelatedCompletedDocument
                ],
                out var catalog);
            Assert.True(catalogValidation.IsValid, FormatDiagnostics(catalogValidation));
            var binding = new DurableRequestBinding(
                request,
                [
                    new(new("completed"), completed),
                    new(new("failed"), failed),
                    new(new("cancelled"), cancelled),
                    new(new("terminated"), terminated)
                ],
                maxAttempts: 3,
                claimLease: TimeSpan.FromMinutes(5),
                timeoutAfter: null,
                DurableOperationIdempotencyEvidence.TargetDeduplication,
                reconciliationTarget: new(
                    DefinitionReference("process/reconcile-index-shard", '9'),
                    new("reconcile")));
            return new(
                Assert.IsType<InteractionContractCatalog>(catalog),
                request,
                completed,
                failed,
                unrelatedCompleted,
                binding);
        }
    }

    sealed class BindingResolver(DurableRequestBinding binding) : IDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved)
        {
            resolved = binding;
            return true;
        }
    }

    sealed class AdapterResolver(IDurableOperationAdapter adapter) : IDurableOperationAdapterResolver
    {
        public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? resolved)
        {
            resolved = adapter;
            return true;
        }
    }

    sealed class ChildCompletionAdapter(RequestContractReference request) : IDurableOperationAdapter
    {
        readonly List<DurableOperationInvocation> invocations = [];

        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        internal IReadOnlyList<DurableOperationInvocation> Invocations => invocations;

        internal ProcessInteractionOrigin? ReplyOrigin { get; private set; }

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            context.ThrowIfCancellationRequested();
            invocations.Add(invocation);
            var target = invocation.Request.ChildTarget
                ?? throw new InvalidOperationException("The child adapter requires self-contained child target metadata.");
            ReplyOrigin = new(
                target.Definition,
                new("return"),
                target.Continuation,
                new("activation/child-terminal"),
                new("token/child-terminal"),
                outcome: new("return"));
            return ValueTask.FromResult<DurableOperationAttemptObservation>(new DurableOperationOutcomeObservation(
                new RequestResultOutcome(target.OutcomeMapping.For(ExecutionTerminalOutcomeKind.Completed), StringValue("child-result")),
                replyOrigin: ReplyOrigin));
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            throw new InvalidOperationException("The child completion adapter does not support reconciliation.");
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class PollingHost(Func<ProcessRelationEvaluation, string> status) : IProcessReferenceHost
    {
        internal List<ProcessRelationEvaluation> Evaluations { get; } = [];

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            Evaluations.Add(evaluation);
            return ProcessOperationResult.Completed(StringValue(status(evaluation)));
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }

    sealed class RejectingHost : IProcessReferenceHost
    {
        internal static RejectingHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}
