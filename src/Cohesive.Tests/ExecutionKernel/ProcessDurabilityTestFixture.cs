using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

internal sealed class ProcessDurabilityTestFixture
{
    internal static readonly DateTimeOffset IssuedAtUtc =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    internal static readonly DateTimeOffset AcceptedAtUtc = IssuedAtUtc.AddSeconds(1);

    internal static readonly DateTimeOffset ActivatedAtUtc = IssuedAtUtc.AddMinutes(1);

    internal static readonly DateTimeOffset CheckpointedAtUtc = IssuedAtUtc.AddMinutes(2);

    internal static readonly InteractionAuthorityScope Authority =
        new("authority/tests", "tenant/cohesive");

    internal static readonly ValueContract StringContract =
        new(new ScalarTypeRef(ScalarTypeKind.String));

    ProcessDurabilityTestFixture(
        CompiledProcessPlan plan,
        ProcessStartReceipt start,
        ProcessActivation activation,
        ProcessActivationDecision decision,
        ProcessControlState control,
        ProcessRelationEvaluation operation,
        ProcessOperationResult operationResult,
        RequestEnvelope request,
        ProcessActivationInput pendingReply,
        DurableOperationState durableOperation,
        ProcessDurableCheckpoint checkpoint)
    {
        Plan = plan;
        Start = start;
        Activation = activation;
        Decision = decision;
        Control = control;
        Operation = operation;
        OperationResult = operationResult;
        Request = request;
        PendingReply = pendingReply;
        DurableOperation = durableOperation;
        Checkpoint = checkpoint;
    }

    internal CompiledProcessPlan Plan { get; }

    internal ProcessStartReceipt Start { get; }

    internal ProcessActivation Activation { get; }

    internal ProcessActivationDecision Decision { get; }

    internal ProcessControlState Control { get; }

    internal ProcessRelationEvaluation Operation { get; }

    internal ProcessOperationResult OperationResult { get; }

    internal RequestEnvelope Request { get; }

    internal ProcessActivationInput PendingReply { get; }

    internal DurableOperationState DurableOperation { get; }

    internal ProcessDurableCheckpoint Checkpoint { get; }

    internal static ProcessDurabilityTestFixture Create(
        string definitionId = "process/durable-checkpoint-tests",
        string revisionId = "revision/1",
        string semanticVariant = "baseline",
        ProcessRecoveryPolicy recoveryPolicy = ProcessRecoveryPolicy.ContinueAttempt,
        RequestRetrySemantics durableOperationRetry = RequestRetrySemantics.StableIdentity)
    {
        var operationContracts = DurableOperationTestFixture.Create(retry: durableOperationRetry);
        var relation = DefinitionReference("relation/checkpoint-enrichment", '4');
        var plan = Compile(
            definitionId,
            revisionId,
            Definition(operationContracts.RequestContract, relation, semanticVariant, recoveryPolicy),
            operationContracts.Catalog,
            [new(
                relation,
                ProcessDefinitionLinkKind.RelationQuery,
                StringContract,
                StringContract)]);
        ProcessContinuationIdentity continuation = new(
            new("process-instance/durable-checkpoint-tests"),
            new("process-attempt/1"));
        var startRequest = new ProcessStartRequest(
            ProcessStartRequest.CurrentSchemaVersion,
            plan.DefinitionReference,
            new(
                new("start-command/durable-checkpoint-tests"),
                new("start-idempotency/durable-checkpoint-tests"),
                continuation.ProcessInstanceId,
                new("operator/tests", Authority, "policy/tests/allow"),
                IssuedAtUtc,
                plan.Document.Metadata.Provenance),
            continuation,
            StringValue("input/durable-checkpoint-tests"));
        var start = new ProcessStartReceipt(startRequest, AcceptedAtUtc);
        var initial = ProcessReferenceInterpreter.Create(plan, start);
        var activation = new ProcessActivation(
            new("activation/start"),
            ProcessActivationCause.Start,
            ActivatedAtUtc,
            new(
                Authority,
                new("correlation/durable-checkpoint-tests"),
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                plan.Document.Metadata.Provenance));
        var host = new RecordingHost();
        var decision = ProcessReferenceInterpreter.Activate(plan, initial, activation, host);

        Assert.Equal(ProcessActivationDisposition.DurableCut, decision.Disposition);
        Assert.Equal(3, decision.State.Tokens.Length);
        Assert.Contains(
            decision.State.Tokens,
            static token => token.Disposition == ExecutionTokenDisposition.Waiting);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions));
        var operation = Assert.Single(host.Relations);
        var operationResult = Assert.Single(host.Results);

        var controlExecutor = new ProcessControlReferenceExecutor(operationContracts.Catalog);
        var control = start.CreateInitialState();
        var begun = controlExecutor.BeginActivation(
            control,
            new(
                Expectation(control),
                activation.Id,
                ActivatedAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.ActivationStarted, begun.Disposition);
        var safePoint = controlExecutor.ReachSafePoint(
            begun.State,
            new(
                new("safe-point/request"),
                Expectation(begun.State),
                activation.Id,
                Assert.IsType<ExecutionNodeId>(decision.Evidence.SafePointNode),
                CheckpointedAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, safePoint.Disposition);

        var operationReceipt = new ProcessOperationReceipt(
            new(
                operation.Continuation,
                operation.Activation,
                operation.Token,
                operation.Node,
                operation.Occurrence),
            operation.Definition,
            operationResult,
            CheckpointedAtUtc);
        var operationValidation = operationContracts.Executor.TryCreate(
            request,
            operationContracts.Binding,
            ActivatedAtUtc,
            out var durableOperation);
        Assert.True(
            operationValidation.IsValid,
            DurableOperationTestFixture.FormatDiagnostics(operationValidation));
        var typedDurableOperation = Assert.IsType<DurableOperationState>(durableOperation);
        var pendingReply = Reply(plan, operationContracts, request);
        var checkpoint = new ProcessDurableCheckpoint(
            ProcessDurableCheckpoint.CurrentSchemaVersion,
            start,
            decision.State,
            safePoint.State,
            [new(
                sequence: 1,
                decision.State.Continuation,
                ProcessStorageContentFingerprints.Continuation(initial),
                ProcessStorageContentFingerprints.Continuation(decision.State),
                activation,
                decision.Disposition,
                decision.Evidence,
                CheckpointedAtUtc)],
            [operationReceipt],
            [new(pendingReply, ActivatedAtUtc.AddSeconds(30))],
            [new(request, CheckpointedAtUtc)],
            [typedDurableOperation],
            AcceptedAtUtc,
            CheckpointedAtUtc);

        return new(
            plan,
            start,
            activation,
            decision,
            safePoint.State,
            operation,
            operationResult,
            request,
            pendingReply,
            typedDurableOperation,
            checkpoint);
    }

    internal static ProcessDurableCheckpoint CopyCheckpoint(
        ProcessDurableCheckpoint source,
        ProcessStartReceipt? start = null,
        ProcessContinuationState? continuation = null,
        ProcessControlState? control = null,
        ImmutableArray<ProcessActivationCommitReceipt> activations = default,
        ImmutableArray<ProcessOperationReceipt> operations = default,
        ImmutableArray<ProcessDurableInboxEntry> inbox = default,
        ImmutableArray<ProcessEmissionRecord> emissions = default,
        ImmutableArray<DurableOperationState> durableOperations = default) =>
        new(
            source.SchemaVersion,
            start ?? source.Start,
            continuation ?? source.Continuation,
            control ?? source.Control,
            activations.IsDefault ? source.Activations : activations,
            operations.IsDefault ? source.Operations : operations,
            inbox.IsDefault ? source.Inbox : inbox,
            emissions.IsDefault ? source.Emissions : emissions,
            durableOperations.IsDefault ? source.DurableOperations : durableOperations,
            source.CreatedAtUtc,
            source.UpdatedAtUtc);

    internal static ProcessStartReceipt StartFor(
        ProcessDurabilityTestFixture source,
        ExecutionDefinitionReference? definition = null,
        ProcessContinuationIdentity? continuation = null)
    {
        var request = source.Start.Request;
        var selectedContinuation = continuation ?? request.InitialContinuation;
        return new(
            new(
                request.SchemaVersion,
                definition ?? request.Definition,
                new(
                    request.Context.CommandId,
                    request.Context.IdempotencyKey,
                    selectedContinuation.ProcessInstanceId,
                    request.Context.Authorization,
                    request.Context.IssuedAtUtc,
                    request.Context.Provenance),
                selectedContinuation,
                request.Input),
            source.Start.AcceptedAtUtc);
    }

    internal static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    internal static ExecutionDefinitionReference DefinitionReference(string id, char fingerprintDigit) =>
        new(
            new(id),
            new("revision/1"),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string(fingerprintDigit, 64)));

    static CanonicalProcessDefinition Definition(
        RequestContractReference request,
        ExecutionDefinitionReference relation,
        string semanticVariant,
        ProcessRecoveryPolicy recoveryPolicy)
    {
        ValueBindingId relationResult = new("relation.result");
        CanonicalProcessNode[] nodes =
        [
            new ForkProcessNode(
                new("fork"),
                [
                    new(new("branch/alpha"), Edge("edge/fork-alpha", "relation")),
                    new(new("branch/beta"), Edge("edge/fork-beta", "join"))
                ],
                new("join")),
            new EvaluateRelationProcessNode(
                new("relation"),
                relation,
                Expr.Const($"operation/{semanticVariant}"),
                new(
                    Edge("edge/relation-request", "request"),
                    new(relationResult, StringContract))),
            new RequestProcessNode(
                new("request"),
                request,
                Expr.BoundValue(relationResult),
                [
                    new(
                        new("outcome/result"),
                        new("result"),
                        new(Edge("edge/request-result-join", "join"))),
                    new(
                        new("outcome/failure"),
                        new("failure"),
                        new(Edge("edge/request-failure-join", "join")))
                ]),
            new JoinProcessNode(new("join"), new("fork"), JoinAll(), Edge("edge/join-return", "return")),
            new ReturnProcessNode(new("return"), Expr.Const($"completed/{semanticVariant}"))
        ];
        return new(
            StringContract,
            StringContract,
            new("fork"),
            [.. nodes],
            recoveryPolicy);
    }

    static ProcessActivationInput Reply(
        CompiledProcessPlan plan,
        DurableOperationTestFixture operationContracts,
        RequestEnvelope request)
    {
        var target = Assert.IsType<ProcessTokenInteractionTarget>(request.ResponseTarget);
        var envelope = new ReplyEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new("emission/reply/pending"),
                new ProcessInteractionOrigin(
                    plan.DefinitionReference,
                    new("source/reviewer"),
                    target.Continuation,
                    new("activation/reviewer"),
                    target.Token),
                new("correlation/durable-checkpoint-tests"),
                request.Context.EmissionId,
                Authority,
                new("idempotency/reply/pending"),
                ordering: null,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                plan.Document.Metadata.Provenance),
            operationContracts.ResultReplyContract,
            request.Context.EmissionId,
            new RequestResultOutcome(new("result"), StringValue("accepted")));
        return new(target, envelope);
    }

    static CompiledProcessPlan Compile(
        string definitionId,
        string revisionId,
        CanonicalProcessDefinition definition,
        InteractionContractCatalog contracts,
        ImmutableArray<ProcessDefinitionLink> definitions)
    {
        var document = ProcessDefinitionDocuments.Create(
            new(definitionId),
            new(revisionId),
            definition,
            Provenance());
        var compilation = ProcessStaticCompiler.Compile(
            document,
            new(
                definitions,
                contracts));
        Assert.True(
            compilation.IsSuccessful,
            DurableOperationTestFixture.FormatDiagnostics(compilation.Validation));
        return Assert.IsType<CompiledProcessPlan>(compilation.Plan);
    }

    static ProcessControlExpectation Expectation(ProcessControlState state) =>
        new(
            new(state.ProcessInstanceId, state.CurrentAttempt.AttemptId),
            state.Revision);

    static ProcessJoinPolicy JoinAll() =>
        new(
            ProcessJoinMode.All,
            requiredCount: 0,
            ProcessJoinFailurePolicy.FailFast,
            ProcessJoinCancellationPolicy.AwaitRemaining,
            ProcessJoinCompletionOrder.Unobservable,
            ProcessJoinTieBreak.BranchIdentity);

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static ExecutionProvenance Provenance() =>
        new(
            new("process-durability-tests", "1"),
            new("tests/execution-kernel/process-durability"),
            DocumentOrigin.Generated);

    sealed class RecordingHost : IProcessReferenceHost
    {
        internal List<ProcessRelationEvaluation> Relations { get; } = [];

        internal List<ProcessOperationResult> Results { get; } = [];

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            Relations.Add(evaluation);
            var result = ProcessOperationResult.Completed(evaluation.Input);
            Results.Add(result);
            return result;
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}
