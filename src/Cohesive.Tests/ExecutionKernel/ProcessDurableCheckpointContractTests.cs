using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessDurableCheckpointContractTests
{
    [Fact]
    public void DurableTraceProjection_MatchesReferenceSemanticsAndRetainsCommitEvidence()
    {
        var fixture = ProcessDurabilityTestFixture.Create();

        var reference = ProcessExecutionTraceProjector.Project(fixture.Decision);
        var durable = Assert.Single(ProcessDurableExecutionTraceProjector.Project(fixture.Checkpoint));

        Assert.True(reference.IsSuccessful);
        Assert.True(durable.IsSuccessful);
        Assert.Null(reference.Trace!.DurableCommitSequence);
        Assert.Equal(Assert.Single(fixture.Checkpoint.Activations).Sequence, durable.Trace!.DurableCommitSequence);
        Assert.Equal(
            ExecutionTraceFingerprinter.ComputeSemantic(reference.Trace),
            ExecutionTraceFingerprinter.ComputeSemantic(durable.Trace));
        var emitted = Assert.Single(
            durable.Trace.Events,
            static item => item.Kind == "interactionEmitted");
        Assert.NotNull(emitted.Correlation);
        Assert.NotNull(emitted.IdempotencyKey);
        Assert.NotNull(emitted.EmissionFingerprint);
        Assert.Equal(
            reference.Trace.Events.Select(static item => (
                item.Sequence,
                item.Kind,
                item.Node,
                item.Token,
                item.BranchOrClause,
                item.Emission,
                item.Detail,
                Sources: string.Join('|', item.SourceReferences))),
            durable.Trace.Events.Select(static item => (
                item.Sequence,
                item.Kind,
                item.Node,
                item.Token,
                item.BranchOrClause,
                item.Emission,
                item.Detail,
                Sources: string.Join('|', item.SourceReferences))));
        Assert.NotEqual(
            ExecutionTraceJsonSerializer.Serialize(reference.Trace),
            ExecutionTraceJsonSerializer.Serialize(durable.Trace));
    }

    [Fact]
    public void DurableTraceProjection_InvalidEventSequenceFailsWithStructuredDiagnostic()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var receipt = Assert.Single(fixture.Checkpoint.Activations);
        var malformedTrace = receipt.Evidence.Trace.SetItem(
            0,
            receipt.Evidence.Trace[0] with { Sequence = 7 });
        var malformedEvidence = receipt.Evidence with { Trace = malformedTrace };
        var envelopes = fixture.Checkpoint.Emissions.Select(static item => item.Envelope)
            .Concat(fixture.Checkpoint.Inbox.Select(static item => item.Input.Envelope));

        var result = ProcessExecutionTraceProjector.ProjectCommitted(
            malformedEvidence,
            receipt.Disposition,
            receipt.Sequence,
            fixture.Checkpoint.Definition,
            receipt.Continuation,
            envelopes);

        Assert.False(result.IsSuccessful);
        var diagnostic = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal(ExecutionTraceDiagnosticCodes.EventInvalid, diagnostic.Code);
        Assert.Equal("processTraceProjection", diagnostic.Evidence?.Stage);
        Assert.NotEmpty(diagnostic.Evidence?.SourceReferences ?? []);
    }

    [Fact]
    public void Checkpoint_ComposesEveryLiveDurabilityAuthorityAndRoundTripsExactly()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var checkpoint = fixture.Checkpoint;
        var wait = Assert.Single(checkpoint.Continuation.Waits, static candidate => candidate.Active);
        var outstanding = Assert.Single(checkpoint.Continuation.OutstandingRequests);
        var target = Assert.IsType<ProcessTokenInteractionTarget>(fixture.Request.ResponseTarget);

        Assert.Equal(checkpoint.Start.Request.Definition, checkpoint.Definition);
        Assert.Equal(checkpoint.Control.Definition, checkpoint.Definition);
        Assert.Equal(
            checkpoint.Start.Request.InitialContinuation.ProcessInstanceId,
            checkpoint.ContinuationIdentity.ProcessInstanceId);
        Assert.Equal(checkpoint.Control.ProcessInstanceId, checkpoint.ContinuationIdentity.ProcessInstanceId);
        Assert.Equal(checkpoint.Control.CurrentAttempt.AttemptId, checkpoint.ContinuationIdentity.ProcessAttemptId);
        Assert.Equal(ProcessControlExecutionPhase.AtSafePoint, checkpoint.Control.CurrentAttempt.Phase);
        Assert.Equal(fixture.Activation.Id, checkpoint.Control.CurrentAttempt.LastSafePoint?.ActivationId);

        Assert.Equal(3, checkpoint.Continuation.Tokens.Length);
        Assert.NotEmpty(checkpoint.Continuation.Forks);
        Assert.Equal(ProcessWaitKind.Request, wait.Kind);
        Assert.Equal(wait.RegistrationId, target.WaitRegistrationId);
        Assert.Equal(fixture.Request.Context.EmissionId, outstanding.Emission);
        Assert.Equal(target.Token, outstanding.Token);

        var activation = Assert.Single(checkpoint.Activations);
        Assert.Equal(fixture.Activation, activation.Activation);
        Assert.Equal(fixture.Decision.Evidence, activation.Evidence);
        var operation = Assert.Single(checkpoint.Operations);
        Assert.Equal(fixture.Operation.Definition, operation.OperationDefinition);
        Assert.Equal(fixture.Operation.Node, operation.Key.Node);
        Assert.Equal(fixture.OperationResult, operation.Result);
        var inbox = Assert.Single(checkpoint.Inbox);
        Assert.Equal(fixture.PendingReply, inbox.Input);
        Assert.Null(inbox.Receipt);
        var emission = Assert.Single(checkpoint.Emissions);
        Assert.Equal(fixture.Request, emission.Envelope);
        var durableOperation = Assert.Single(checkpoint.DurableOperations);
        Assert.Equal(fixture.Request.Context.EmissionId, durableOperation.OperationId);
        Assert.Equal(fixture.Request, durableOperation.Request);
        Assert.Same(fixture.DurableOperation, durableOperation);

        var compatibility = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);
        Assert.True(compatibility.IsValid, FormatDiagnostics(compatibility));

        var json = ProcessDurableCheckpointJsonSerializer.Serialize(checkpoint);
        var restored = ProcessDurableCheckpointJsonSerializer.Deserialize(json, fixture.Plan);

        Assert.Equal(checkpoint.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(checkpoint.Start, restored.Start);
        Assert.Equal(checkpoint.Definition, restored.Definition);
        Assert.Equal(checkpoint.ContinuationIdentity, restored.ContinuationIdentity);
        Assert.Equal(
            checkpoint.Continuation.CompletedActivationCount,
            restored.Continuation.CompletedActivationCount);
        Assert.Equal(checkpoint.Continuation.Tokens.Length, restored.Continuation.Tokens.Length);
        Assert.Equal(checkpoint.Continuation.Forks.Length, restored.Continuation.Forks.Length);
        Assert.Equal(checkpoint.Continuation.Waits.Length, restored.Continuation.Waits.Length);
        Assert.Equal(
            checkpoint.Continuation.OutstandingRequests.Length,
            restored.Continuation.OutstandingRequests.Length);
        Assert.Equal(checkpoint.Control, restored.Control);
        Assert.Equal(checkpoint.Activations.Length, restored.Activations.Length);
        Assert.Equal(checkpoint.Operations.Length, restored.Operations.Length);
        Assert.Equal(checkpoint.Inbox.Length, restored.Inbox.Length);
        Assert.Equal(checkpoint.Emissions.Length, restored.Emissions.Length);
        Assert.Equal(checkpoint.DurableOperations.Length, restored.DurableOperations.Length);
        Assert.Equal(json, ProcessDurableCheckpointJsonSerializer.Serialize(restored));
    }

    [Fact]
    public void StrictCheckpointRead_RejectsAnUnknownWireMember()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var json = ProcessDurableCheckpointJsonSerializer.Serialize(fixture.Checkpoint);
        var unknownMember = $"{json[..^1]},\"unknown\":true}}";

        var validation = ProcessDurableCheckpointJsonSerializer.TryDeserialize(
            unknownMember,
            fixture.Plan,
            out var checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ProcessCheckpointJsonDiagnosticCodes.DeserializationInvalid, diagnostic.Code);
        Assert.Null(checkpoint);
    }

    [Theory]
    [InlineData(OperationResultWireMutation.BothNull)]
    [InlineData(OperationResultWireMutation.BothPresent)]
    [InlineData(OperationResultWireMutation.NonErrorFailure)]
    [InlineData(OperationResultWireMutation.EmissionsOnFailure)]
    public void StrictCheckpointRead_RejectsMalformedOperationResultUnion(
        OperationResultWireMutation mutation)
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var root = JsonNode.Parse(ProcessDurableCheckpointJsonSerializer.Serialize(fixture.Checkpoint))!
            .AsObject();
        var result = root["operations"]![0]!["result"]!.AsObject();
        var error = FailureNode(DiagnosticSeverity.Error);
        switch (mutation)
        {
            case OperationResultWireMutation.BothNull:
                result["value"] = null;
                result["failure"] = null;
                break;
            case OperationResultWireMutation.BothPresent:
                result["failure"] = error;
                break;
            case OperationResultWireMutation.NonErrorFailure:
                result["value"] = null;
                result["failure"] = FailureNode(DiagnosticSeverity.Warning);
                break;
            case OperationResultWireMutation.EmissionsOnFailure:
                result["value"] = null;
                result["failure"] = error;
                JsonArray emissions = [];
                emissions.Add(root["emissions"]![0]!["envelope"]!.DeepClone());
                result["emissions"] = emissions;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown wire mutation.");
        }

        var validation = ProcessDurableCheckpointJsonSerializer.TryDeserialize(
            root.ToJsonString(),
            fixture.Plan,
            out var checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ProcessCheckpointJsonDiagnosticCodes.DeserializationInvalid, diagnostic.Code);
        Assert.Null(checkpoint);
    }

    [Fact]
    public void OperationReceipt_ErrorFailureIsAValidClosedOutcome()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Operations);
        var failure = new DocumentValidationDiagnostic(
            "tests.operation.failed",
            DiagnosticSeverity.Error,
            "The operation failed deterministically.");
        var result = ProcessOperationResult.Failed(failure);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            operations:
            [new(
                original.Key,
                original.OperationDefinition,
                result,
                original.RecordedAtUtc)]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        Assert.True(result.IsValidOutcome());
        Assert.False(result.IsSuccessful);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));
    }

    [Fact]
    public void CompatibilityValidation_RejectsAnOperationResultThatBypassedConstruction()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Operations);
        var malformed = Assert.IsType<ProcessOperationResult>(
            RuntimeHelpers.GetUninitializedObject(typeof(ProcessOperationResult)));
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            operations:
            [new(
                original.Key,
                original.OperationDefinition,
                malformed,
                original.RecordedAtUtc)]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible
            && candidate.Location == "/operations/0/result");
        Assert.Equal("processCheckpointRecovery", diagnostic.Evidence?.Stage);
    }

    [Fact]
    public void Checkpoint_RejectsPhysicalInboxDispositionWithoutCanonicalSemanticReceipt()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var entry = Assert.Single(fixture.Checkpoint.Inbox);
        var physicalOnlyReceipt = new ProcessInputReceipt(
            entry.Input,
            ProcessInputAdmissionDisposition.Buffered,
            ProcessInputAdmissionReason.Early,
            fixture.Checkpoint.UpdatedAtUtc);

        Assert.Throws<ArgumentException>(() => ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            inbox: [new(
                entry.Input,
                entry.AdmittedAtUtc,
                physicalOnlyReceipt,
                fixture.Checkpoint.ContinuationIdentity)]));
    }

    [Fact]
    public void CommittedActivationInput_RequiresItsExactInboxAndAdmissionTrace()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Activations);
        var activationWithUnwitnessedInput = new ProcessActivation(
            original.Activation.Id,
            original.Activation.Cause,
            original.Activation.ObservedAtUtc,
            original.Activation.Context,
            [fixture.PendingReply]);
        var incompatibleReceipt = new ProcessActivationCommitReceipt(
            original.Sequence,
            original.Continuation,
            original.BeforeContinuation,
            original.AfterContinuation,
            activationWithUnwitnessedInput,
            original.Disposition,
            original.Evidence,
            original.CommittedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            activations: [incompatibleReceipt]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible
            && candidate.Location == "/activations/0/activation/inputs/0");
        Assert.Equal("processCheckpointRecovery", diagnostic.Evidence?.Stage);
    }

    [Fact]
    public void Checkpoint_NormalizersRejectNullEntriesWithTheDocumentedBoundaryException()
    {
        var fixture = ProcessDurabilityTestFixture.Create();

        Assert.Throws<ArgumentException>(() => ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            emissions: [null!]));
    }

    [Fact]
    public void DurableCommit_RejectsDefaultPhysicalIdentities()
    {
        var fixture = ProcessDurabilityTestFixture.Create();

        Assert.Throws<ArgumentException>(() => new ProcessDurableCommit(
            default,
            ProcessStorageRevision.Initial,
            "worker/default-id",
            new("1"),
            fixture.Checkpoint,
            [],
            fixture.Checkpoint.UpdatedAtUtc));
        Assert.Throws<ArgumentException>(() => new ProcessDurableCommit(
            new("commit/default-revision"),
            default,
            "worker/default-revision",
            new("1"),
            fixture.Checkpoint,
            [],
            fixture.Checkpoint.UpdatedAtUtc));
        Assert.Throws<ArgumentException>(() => new ProcessDurableCommit(
            new("commit/default-fence"),
            ProcessStorageRevision.Initial,
            "worker/default-fence",
            default,
            fixture.Checkpoint,
            [],
            fixture.Checkpoint.UpdatedAtUtc));
    }

    [Fact]
    public void Checkpoint_RejectsEverySplitAuthorityDimension()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var checkpoint = fixture.Checkpoint;
        var anotherDefinition = ProcessDurabilityTestFixture.DefinitionReference("process/other", 'f');
        var anotherInstance = new ProcessInstanceId("process-instance/other");
        var anotherAttempt = new ProcessAttemptId("process-attempt/other");
        var anotherAuthority = new InteractionAuthorityScope("authority/other", "tenant/other");

        Assert.Throws<ArgumentException>(() => ProcessDurabilityTestFixture.CopyCheckpoint(
            checkpoint,
            start: ProcessDurabilityTestFixture.StartFor(fixture, definition: anotherDefinition)));
        Assert.Throws<ArgumentException>(() => ProcessDurabilityTestFixture.CopyCheckpoint(
            checkpoint,
            start: ProcessDurabilityTestFixture.StartFor(
                fixture,
                continuation: new(checkpoint.ContinuationIdentity.ProcessInstanceId, anotherAttempt))));
        Assert.Throws<ArgumentException>(() => ProcessDurabilityTestFixture.CopyCheckpoint(
            checkpoint,
            control: ProcessControlState.Create(
                checkpoint.Definition,
                checkpoint.Control.AuthorityScope,
                anotherInstance,
                checkpoint.ContinuationIdentity.ProcessAttemptId,
                checkpoint.Start.AcceptedAtUtc)));
        Assert.Throws<ArgumentException>(() => ProcessDurabilityTestFixture.CopyCheckpoint(
            checkpoint,
            control: ProcessControlState.Create(
                checkpoint.Definition,
                checkpoint.Control.AuthorityScope,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                anotherAttempt,
                checkpoint.Start.AcceptedAtUtc)));
        Assert.Throws<ArgumentException>(() => ProcessDurabilityTestFixture.CopyCheckpoint(
            checkpoint,
            control: ProcessControlState.Create(
                checkpoint.Definition,
                anotherAuthority,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                checkpoint.ContinuationIdentity.ProcessAttemptId,
                checkpoint.Start.AcceptedAtUtc)));
        Assert.Throws<ArgumentException>(() => ProcessDurabilityTestFixture.CopyCheckpoint(
            checkpoint,
            control: ProcessControlState.Create(
                checkpoint.Definition,
                checkpoint.Control.AuthorityScope,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                checkpoint.ContinuationIdentity.ProcessAttemptId,
                checkpoint.Start.AcceptedAtUtc.AddSeconds(1))));
    }

    [Fact]
    public void CommitFingerprint_IsCanonicalAcrossEquivalentConstructionAndMutationOrder()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var equivalentFixture = ProcessDurabilityTestFixture.Create();
        ProcessLocalMutation alpha = new(
            "mutation/alpha",
            "local/alpha",
            ProcessDurabilityTestFixture.StringValue("alpha"),
            expectedVersion: 1);
        ProcessLocalMutation beta = new(
            "mutation/beta",
            "local/beta",
            ProcessDurabilityTestFixture.StringValue("beta"),
            expectedVersion: 2);

        var first = Commit(fixture.Checkpoint, [beta, alpha]);
        var equivalent = Commit(equivalentFixture.Checkpoint, [alpha, beta]);
        var changed = Commit(
            fixture.Checkpoint,
            [
                new(
                    "mutation/alpha",
                    "local/alpha",
                    ProcessDurabilityTestFixture.StringValue("changed"),
                    expectedVersion: 1),
                beta
            ]);

        Assert.Equal(["local/alpha", "local/beta"], first.LocalMutations.Select(static value => value.Resource));
        Assert.Equal(first.Fingerprint, equivalent.Fingerprint);
        Assert.StartsWith("sha256-v1:", first.Fingerprint.Value, StringComparison.Ordinal);
        Assert.Equal(74, first.Fingerprint.Value.Length);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void UnsupportedCheckpointSchema_IsARecoveryDiagnosticRatherThanAConstructionFailure()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var checkpoint = fixture.Checkpoint;
        var unsupported = new ProcessDurableCheckpoint(
            new("cohesive-process-durable-checkpoint/v999"),
            checkpoint.Start,
            checkpoint.Continuation,
            checkpoint.Control,
            checkpoint.Activations,
            checkpoint.Operations,
            checkpoint.Inbox,
            checkpoint.Emissions,
            checkpoint.DurableOperations,
            checkpoint.CreatedAtUtc,
            checkpoint.UpdatedAtUtc);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, unsupported);

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ProcessCheckpointDiagnosticCodes.SchemaVersionUnsupported, diagnostic.Code);
        Assert.Equal("/schemaVersion", diagnostic.Location);
        Assert.Equal("processCheckpointRecovery", diagnostic.Evidence?.Stage);
    }

    [Theory]
    [InlineData("definition", "/operations/0/operationDefinition")]
    [InlineData("result", "/operations/0/result/value")]
    [InlineData("recorded-at", "/operations/0/key")]
    public void OperationReceipt_MustMatchTheCompiledNodeAndResultContract(
        string mismatch,
        string expectedLocation)
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Operations);
        var incompatible = mismatch switch
        {
            "definition" => new ProcessOperationReceipt(
                original.Key,
                ProcessDurabilityTestFixture.DefinitionReference("relation/other", '9'),
                original.Result,
                original.RecordedAtUtc),
            "result" => new ProcessOperationReceipt(
                original.Key,
                original.OperationDefinition,
                ProcessOperationResult.Completed(
                    PortableValue.Concrete(
                        new(new ScalarTypeRef(ScalarTypeKind.Bool)),
                        ObservationValue.FromBool(true))),
                original.RecordedAtUtc),
            "recorded-at" => new ProcessOperationReceipt(
                original.Key,
                original.OperationDefinition,
                original.Result,
                original.RecordedAtUtc.AddTicks(-1)),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, "Unknown mismatch kind.")
        };
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            operations: [incompatible]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible);
        Assert.Equal(expectedLocation, diagnostic.Location);
        Assert.Equal("processCheckpointRecovery", diagnostic.Evidence?.Stage);
    }

    [Fact]
    public void ProcessOutboxEntry_RequiresTheExactActivationCommitTime()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Emissions);
        var incompatible = new ProcessEmissionRecord(
            original.Envelope,
            original.EnqueuedAtUtc.AddTicks(-1),
            original.Attempts,
            original.Publication);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            emissions: [incompatible]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        Assert.Contains(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
            && candidate.Location is not null
            && candidate.Location.StartsWith(
                "/activations/0/evidence/trace/",
                StringComparison.Ordinal));
    }

    [Fact]
    public void OperationCompletedTrace_RequiresItsExactOccurrenceKeyedReceipt()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var operation = Assert.Single(fixture.Checkpoint.Operations);
        var incompatible = new ProcessOperationReceipt(
            new(
                operation.Key.Continuation,
                operation.Key.Activation,
                operation.Key.Token,
                operation.Key.Node,
                operation.Key.Occurrence + 1),
            operation.OperationDefinition,
            operation.Result,
            operation.RecordedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            operations: [incompatible]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        Assert.Contains(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible
            && candidate.Location is not null
            && candidate.Location.StartsWith(
                "/activations/0/evidence/trace/",
                StringComparison.Ordinal)
            && candidate.Location.EndsWith(
                "/operationOccurrence",
                StringComparison.Ordinal));
        Assert.Contains(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible
            && candidate.Location == "/operations/0/key");
    }

    [Fact]
    public void PriorAttemptOperationReceipt_RequiresItsExactCommittedCompletionTrace()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-checkpoint/prior-operation",
            semanticVariant: "prior-operation",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var activation = Assert.Single(fixture.Checkpoint.Activations);
        var operation = Assert.Single(fixture.Checkpoint.Operations);
        var matchingTrace = Assert.Single(
            activation.Evidence.Trace,
            trace => trace.Kind == ProcessTraceEventKind.OperationCompleted
                     && trace.Continuation == operation.Key.Continuation
                     && trace.Activation == operation.Key.Activation
                     && trace.Token == operation.Key.Token
                     && trace.Node == operation.Key.Node);
        var traceWithoutCompletion = activation.Evidence.Trace
            .Where(trace => !ReferenceEquals(trace, matchingTrace))
            .Select(static (trace, index) => trace with { Sequence = index })
            .ToImmutableArray();
        Assert.DoesNotContain(
            traceWithoutCompletion,
            static trace => trace.Kind == ProcessTraceEventKind.OperationCompleted);
        var activationWithoutCompletion = new ProcessActivationCommitReceipt(
            activation.Sequence,
            activation.Continuation,
            activation.BeforeContinuation,
            activation.AfterContinuation,
            activation.Activation,
            activation.Disposition,
            activation.Evidence with { Trace = traceWithoutCompletion },
            activation.CommittedAtUtc);

        ProcessAttemptId replacementAttempt = new("process-attempt/2");
        var restartedAtUtc = fixture.Checkpoint.UpdatedAtUtc.AddMinutes(1);
        var restartCommand = new RestartProcessAttemptCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new("control/restart-prior-operation"),
                new("idempotency/control/restart-prior-operation"),
                fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
                fixture.Start.Request.Context.Authorization,
                restartedAtUtc,
                fixture.Start.Request.Context.Provenance),
            new(fixture.Checkpoint.ContinuationIdentity, fixture.Control.Revision),
            new(
                replacementAttempt,
                ProcessAttemptCleanupRequirement.RetainEvidence,
                new("tests.prior-operation-trace")));
        var restart = new ProcessControlReferenceExecutor(
                Assert.IsType<InteractionContractCatalog>(
                    fixture.Plan.ValidationContext.InteractionContracts))
            .Apply(fixture.Control, restartCommand, restartedAtUtc);
        Assert.Equal(ProcessControlDecisionDisposition.Applied, restart.Disposition);
        var continuation = ProcessReferenceInterpreter.RestartAttempt(
            fixture.Plan,
            fixture.Checkpoint.Continuation,
            replacementAttempt);
        var checkpoint = new ProcessDurableCheckpoint(
            fixture.Checkpoint.SchemaVersion,
            fixture.Checkpoint.Start,
            continuation,
            restart.State,
            [activationWithoutCompletion],
            fixture.Checkpoint.Operations,
            fixture.Checkpoint.Inbox,
            fixture.Checkpoint.Emissions,
            fixture.Checkpoint.DurableOperations,
            fixture.Checkpoint.CreatedAtUtc,
            restartedAtUtc);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible);
        Assert.Equal("/operations/0/key", diagnostic.Location);
        Assert.Equal("processCheckpointRecovery", diagnostic.Evidence?.Stage);
    }

    [Fact]
    public void RestartReducer_ClosesPendingInboxUnderAbandonedContinuation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-checkpoint/restart-pending-inbox",
            semanticVariant: "restart-pending-inbox",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var checkpoint = fixture.Checkpoint;
        var pending = Assert.Single(checkpoint.Inbox);
        Assert.Null(pending.Receipt);
        var closingContinuation = checkpoint.ContinuationIdentity;
        ProcessAttemptId replacementAttempt = new("process-attempt/restart-pending-inbox");
        var restartedAtUtc = checkpoint.UpdatedAtUtc.AddMinutes(1);
        var command = new RestartProcessAttemptCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new("control/restart-pending-inbox"),
                new("idempotency/control/restart-pending-inbox"),
                closingContinuation.ProcessInstanceId,
                fixture.Start.Request.Context.Authorization,
                restartedAtUtc,
                fixture.Start.Request.Context.Provenance),
            new(closingContinuation, checkpoint.Control.Revision),
            new(
                replacementAttempt,
                ProcessAttemptCleanupRequirement.RetainEvidence,
                new("tests.restart-pending-inbox")));
        var decision = new ProcessControlReferenceExecutor(
                Assert.IsType<InteractionContractCatalog>(
                    fixture.Plan.ValidationContext.InteractionContracts))
            .Apply(checkpoint.Control, command, restartedAtUtc);

        var replacement = ProcessDurableCheckpointReducer.ApplyControl(
            fixture.Plan,
            checkpoint,
            decision,
            restartedAtUtc);
        var compatibility = ProcessCheckpointCompatibilityValidator.Validate(
            fixture.Plan,
            replacement);

        Assert.Equal(ProcessControlDecisionDisposition.Applied, decision.Disposition);
        Assert.Equal(replacementAttempt, replacement.ContinuationIdentity.ProcessAttemptId);
        var closed = Assert.Single(replacement.Inbox);
        var receipt = Assert.IsType<ProcessInputReceipt>(closed.Receipt);
        Assert.Equal(
            ProcessStorageContentFingerprints.Input(pending.Input),
            ProcessStorageContentFingerprints.Input(closed.Input));
        Assert.Equal(ProcessInputAdmissionDisposition.Stale, receipt.Disposition);
        Assert.Equal(ProcessInputAdmissionReason.Stale, receipt.Reason);
        Assert.Equal(restartedAtUtc, receipt.ObservedAtUtc);
        Assert.Equal(closingContinuation, closed.DispositionContinuation);
        Assert.Empty(replacement.Continuation.InputReceipts);
        Assert.Empty(replacement.Continuation.BufferedInputs);
        Assert.Empty(replacement.Continuation.OutstandingRequests);
        Assert.True(compatibility.IsValid, FormatDiagnostics(compatibility));

        var json = ProcessDurableCheckpointJsonSerializer.Serialize(replacement);
        var restored = ProcessDurableCheckpointJsonSerializer.Deserialize(json, fixture.Plan);
        Assert.Equal(
            ProcessInputAdmissionReason.Stale,
            Assert.IsType<ProcessInputReceipt>(Assert.Single(restored.Inbox).Receipt).Reason);

        var missingReason = JsonNode.Parse(json)!.AsObject();
        Assert.True(missingReason["inbox"]![0]!["receipt"]!.AsObject().Remove("reason"));
        var missingReasonValidation = ProcessDurableCheckpointJsonSerializer.TryDeserialize(
            missingReason.ToJsonString(),
            fixture.Plan,
            out var missingReasonCheckpoint);
        Assert.Null(missingReasonCheckpoint);
        Assert.Contains(missingReasonValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessCheckpointJsonDiagnosticCodes.DeserializationInvalid);
    }

    [Fact]
    public void OperationResultEmission_MustBeRetainedByTheExactDurableOutbox()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Operations);
        var operationWithEmission = new ProcessOperationReceipt(
            original.Key,
            original.OperationDefinition,
            ProcessOperationResult.Completed(original.Result.Value!, [fixture.Request]),
            original.RecordedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            operations: [operationWithEmission],
            emissions: [],
            durableOperations: []);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible);
        Assert.Equal("/operations/0/result/emissions/0", diagnostic.Location);
        Assert.Equal("processCheckpointRecovery", diagnostic.Evidence?.Stage);
    }

    [Fact]
    public void OperationResultEmission_ExactOccurrenceOriginIsCompatible()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Operations);
        var emission = HostOperationReply(fixture);
        var operationWithEmission = new ProcessOperationReceipt(
            original.Key,
            original.OperationDefinition,
            ProcessOperationResult.Completed(original.Result.Value!, [emission]),
            original.RecordedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            operations: [operationWithEmission],
            emissions: [.. fixture.Checkpoint.Emissions, new(emission, original.RecordedAtUtc)]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
    }

    [Fact]
    public void OperationResultEmission_MustCarryExactOperationOccurrenceOrigin()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Operations);
        var incompatibleOrigin = new ProcessInteractionOrigin(
            fixture.Plan.DefinitionReference,
            new("request"),
            original.Key.Continuation,
            original.Key.Activation,
            original.Key.Token);
        var emission = HostOperationReply(fixture, incompatibleOrigin);
        var operationWithEmission = new ProcessOperationReceipt(
            original.Key,
            original.OperationDefinition,
            ProcessOperationResult.Completed(original.Result.Value!, [emission]),
            original.RecordedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            operations: [operationWithEmission],
            emissions: [.. fixture.Checkpoint.Emissions, new(emission, original.RecordedAtUtc)]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible);
        Assert.Equal("/operations/0/result/emissions/0", diagnostic.Location);
    }

    [Fact]
    public void OperationResultEmission_RequiresExactlyOneProducingOccurrence()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Operations);
        var emission = HostOperationReply(fixture);
        var operationWithDuplicateEmission = new ProcessOperationReceipt(
            original.Key,
            original.OperationDefinition,
            ProcessOperationResult.Completed(original.Result.Value!, [emission, emission]),
            original.RecordedAtUtc);
        var outbox = new ProcessEmissionRecord(emission, original.RecordedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            operations: [operationWithDuplicateEmission],
            emissions: [.. fixture.Checkpoint.Emissions, outbox]);
        var outboxIndex = checkpoint.Emissions.IndexOf(outbox);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        Assert.Contains(validation.Diagnostics, candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
            && candidate.Location == $"/emissions/{outboxIndex}");
    }

    [Fact]
    public void InteractionTraceAndOutstandingRequest_RequireReciprocalDurableLedgers()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            emissions: [],
            durableOperations: []);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
            && diagnostic.Location!.Contains("/evidence/trace/", StringComparison.Ordinal));
        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
            && diagnostic.Location == "/continuation/outstandingRequests/0");
    }

    [Fact]
    public void RequestOutbox_RequiresItsExactDurableOperationState()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: []);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
            && diagnostic.Location == "/emissions/0");
        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
            && diagnostic.Location == "/continuation/outstandingRequests/0");
    }

    [Fact]
    public void DurableRequestOperation_CreationTimeMustEqualItsExactOriginActivationObservation()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = fixture.DurableOperation;
        var moved = new DurableOperationState(
            original.SchemaVersion,
            original.Request,
            original.Binding,
            original.CreatedAtUtc.AddTicks(1),
            original.Attempts,
            original.Reconciliations,
            original.RecoveryRequirement,
            original.Acknowledgement,
            original.Admission);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [moved]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible
            && candidate.Location == "/durableOperations/0/createdAtUtc");
        Assert.Equal("processCheckpointRecovery", diagnostic.Evidence?.Stage);
    }

    [Fact]
    public void ActivationTraceMismatch_UsesItsDistinctCompatibilityDiagnostic()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Activations);
        var firstTrace = original.Evidence.Trace[0];
        var incompatibleEvidence = original.Evidence with
        {
            Trace = original.Evidence.Trace.SetItem(
                0,
                firstTrace with
                {
                    Activation = new("activation/other")
                })
        };
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            activations:
            [new(
                original.Sequence,
                original.Continuation,
                original.BeforeContinuation,
                original.AfterContinuation,
                original.Activation,
                original.Disposition,
                incompatibleEvidence,
                original.CommittedAtUtc)]);

        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessCheckpointDiagnosticCodes.ActivationReceiptIncompatible);
        Assert.Equal("/activations/0/evidence/trace/0", diagnostic.Location);
        Assert.DoesNotContain(
            validation.Diagnostics,
            static candidate => candidate.Code == ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible);
    }

    [Theory]
    [InlineData("identity", ExecutionDefinitionDiagnosticCodes.DefinitionIdentityUnknown, "/continuation/definition/definitionId")]
    [InlineData("revision", ExecutionDefinitionDiagnosticCodes.RevisionUnsupported, "/continuation/definition/revisionId")]
    [InlineData("fingerprint", ExecutionDefinitionDiagnosticCodes.FingerprintIncompatible, "/continuation/definition/fingerprint")]
    public void DefinitionMismatch_IsDistinctAndRejectedBeforeHostExecution(
        string mismatch,
        string expectedCode,
        string expectedLocation)
    {
        var expected = ProcessDurabilityTestFixture.Create();
        var observed = mismatch switch
        {
            "identity" => ProcessDurabilityTestFixture.Create(
                definitionId: "process/durable-checkpoint-tests/other"),
            "revision" => ProcessDurabilityTestFixture.Create(revisionId: "revision/2"),
            "fingerprint" => ProcessDurabilityTestFixture.Create(semanticVariant: "changed"),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, "Unknown mismatch kind.")
        };
        var host = new CountingHost();

        var validation = ProcessCheckpointCompatibilityValidator.Validate(expected.Plan, observed.Checkpoint);
        var decision = ProcessReferenceInterpreter.Activate(
            expected.Plan,
            observed.Checkpoint.Continuation,
            RecoveryActivation(expected.Plan),
            host);

        Assert.False(validation.IsValid);
        var diagnostic = Assert.Single(validation.Diagnostics, candidate => candidate.Code == expectedCode);
        Assert.Equal(expectedLocation, diagnostic.Location);
        Assert.Equal(
            1,
            validation.Diagnostics.Count(static candidate =>
                candidate.Code is ExecutionDefinitionDiagnosticCodes.DefinitionIdentityUnknown
                    or ExecutionDefinitionDiagnosticCodes.RevisionUnsupported
                    or ExecutionDefinitionDiagnosticCodes.FingerprintIncompatible));
        Assert.Equal(ProcessActivationDisposition.Rejected, decision.Disposition);
        Assert.Equal(0, host.InvocationCount);
    }

    static ProcessDurableCommit Commit(
        ProcessDurableCheckpoint checkpoint,
        ImmutableArray<ProcessLocalMutation> mutations) =>
        new(
            new("commit/fingerprint"),
            ProcessStorageRevision.Initial,
            "worker/fingerprint",
            new("1"),
            checkpoint,
            mutations,
            checkpoint.UpdatedAtUtc);

    static ProcessActivation RecoveryActivation(CompiledProcessPlan plan) =>
        new(
            new("activation/recovery"),
            ProcessActivationCause.Recovery,
            ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1),
            new(
                ProcessDurabilityTestFixture.Authority,
                new("correlation/recovery"),
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                plan.Document.Metadata.Provenance));

    static ReplyEnvelope HostOperationReply(
        ProcessDurabilityTestFixture fixture,
        ProcessInteractionOrigin? origin = null)
    {
        var receipt = Assert.Single(fixture.Checkpoint.Operations);
        var pending = Assert.IsType<ReplyEnvelope>(fixture.PendingReply.Envelope);
        var selectedOrigin = origin ?? new ProcessInteractionOrigin(
            fixture.Plan.DefinitionReference,
            receipt.Key.Node,
            receipt.Key.Continuation,
            receipt.Key.Activation,
            receipt.Key.Token);
        return new(
            pending.SchemaVersion,
            new(
                new("emission/reply/host-operation"),
                selectedOrigin,
                pending.Context.CorrelationId,
                pending.InReplyTo,
                pending.Context.AuthorityScope,
                new("idempotency/reply/host-operation"),
                pending.Context.Ordering,
                pending.Context.Delivery,
                pending.Context.Provenance),
            pending.Contract,
            pending.InReplyTo,
            pending.Outcome);
    }

    static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static JsonNode FailureNode(DiagnosticSeverity severity) =>
        JsonSerializer.SerializeToNode(
            new DocumentValidationDiagnostic(
                "tests.operation.failed",
                severity,
                "The operation failed deterministically."),
            ProcessDurableCheckpointJsonSerializer.CreateOptions())!;

    public enum OperationResultWireMutation
    {
        BothNull,
        BothPresent,
        NonErrorFailure,
        EmissionsOnFailure
    }

    sealed class CountingHost : IProcessReferenceHost
    {
        internal int InvocationCount { get; private set; }

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
        {
            InvocationCount++;
            return ProcessOperationResult.Completed(invocation.Input);
        }

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            InvocationCount++;
            return ProcessOperationResult.Completed(evaluation.Input);
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution)
        {
            InvocationCount++;
            return ProcessSignalTargetResult.Resolved(
                new ProcessTokenInteractionTarget(resolution.Continuation, resolution.Token));
        }
    }
}
