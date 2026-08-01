using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildProcessArtifactsTests
{
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    static readonly InteractionAuthorityScope Authority =
        new("authority/tests", "tenant/cohesive");

    [Fact]
    public void Create_ProducesExactlyLinkedCoordinatorWorkerAndStorageRequestArtifacts()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();

        Assert.Equal(15, artifacts.InteractionDocuments.Length);
        Assert.Equal(15, artifacts.InteractionCatalog.Count);
        Assert.Equal(2, artifacts.ProcessDocuments.Length);
        Assert.Equal(3, artifacts.DurableRequestBindings.Length);
        Assert.Equal(ProcessRecoveryPolicy.ContinueAttempt, artifacts.WorkerPlan.Definition.RecoveryPolicy);
        Assert.Equal(ProcessRecoveryPolicy.RestartAttempt, artifacts.CoordinatorPlan.Definition.RecoveryPolicy);
        Assert.Equal(artifacts.InitializationRequest, artifacts.InitializationBinding.Request);
        Assert.Equal(artifacts.WorkerInvocationRequest, artifacts.WorkerInvocationBinding.Request);
        Assert.Equal(artifacts.ShardRebuildRequest, artifacts.ShardRebuildBinding.Request);
        AssertReconciliation(
            artifacts,
            artifacts.InitializationRequest,
            artifacts.InitializationBinding,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId);
        AssertReconciliation(
            artifacts,
            artifacts.WorkerInvocationRequest,
            artifacts.WorkerInvocationBinding,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorPartitionsNodeId);
        AssertReconciliation(
            artifacts,
            artifacts.ShardRebuildRequest,
            artifacts.ShardRebuildBinding,
            artifacts.WorkerPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.WorkerRequestNodeId);

        var workerRequest = Assert.IsType<RequestProcessNode>(
            artifacts.WorkerPlan.GetNode(MaterializationRebuildProcessFactory.WorkerRequestNodeId));
        Assert.Equal(artifacts.ShardRebuildRequest, workerRequest.Contract);
        Assert.Equal(
            [
                MaterializationRebuildProcessFactory.CancelledOutcome,
                MaterializationRebuildProcessFactory.CompletedOutcome,
                MaterializationRebuildProcessFactory.FailedOutcome,
                MaterializationRebuildProcessFactory.TerminatedOutcome
            ],
            workerRequest.Outcomes.Select(static branch => branch.Outcome));
        Assert.IsType<ReturnProcessNode>(
            artifacts.WorkerPlan.GetNode(MaterializationRebuildProcessFactory.WorkerReturnNodeId));
        Assert.IsType<FailProcessNode>(
            artifacts.WorkerPlan.GetNode(MaterializationRebuildProcessFactory.WorkerFailedNodeId));
        Assert.IsType<FailProcessNode>(
            artifacts.WorkerPlan.GetNode(MaterializationRebuildProcessFactory.WorkerCancelledNodeId));
        Assert.IsType<FailProcessNode>(
            artifacts.WorkerPlan.GetNode(MaterializationRebuildProcessFactory.WorkerTerminatedNodeId));

        var initialization = Assert.IsType<RequestProcessNode>(
            artifacts.CoordinatorPlan.GetNode(MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId));
        Assert.Equal(artifacts.InitializationRequest, initialization.Contract);
        Assert.Equal(
            MaterializationRebuildProcessFactory.CoordinatorPartitionsNodeId,
            initialization.Outcomes.Single(static branch =>
                branch.Outcome == MaterializationRebuildProcessFactory.CompletedOutcome).Continuation.Edge.Target);

        var partitions = Assert.IsType<ForEachPartitionProcessNode>(
            artifacts.CoordinatorPlan.GetNode(MaterializationRebuildProcessFactory.CoordinatorPartitionsNodeId));
        Assert.Equal(artifacts.WorkerPlan.DefinitionReference, partitions.Process);
        Assert.Equal(artifacts.WorkerInvocationRequest, partitions.Contract);
        Assert.Equal(MaterializationRebuildProcessFactory.ChildOutcomeMapping, partitions.OutcomeMapping);
        Assert.Equal(MaterializationRebuildProcessFactory.MaximumPartitions, partitions.Limits.MaximumItems);
        Assert.Equal(
            MaterializationRebuildProcessFactory.MaximumStartsPerActivation,
            partitions.Limits.MaximumStartsPerActivation);
        Assert.Equal(MaterializationRebuildProcessFactory.MaximumParallelism, partitions.Limits.MaximumParallelism);
        Assert.Equal(ProcessChildCancellationPolicy.Propagate, partitions.Cancellation);

        var coordinatorReturn = Assert.IsType<ReturnProcessNode>(
            artifacts.CoordinatorPlan.GetNode(MaterializationRebuildProcessFactory.CoordinatorReturnNodeId));
        Assert.Equal(
            Expr.Const(MaterializationRebuildProcessFactory.BaselineCompleteCatchUpRequired),
            coordinatorReturn.Result);
    }

    static void AssertReconciliation(
        MaterializationRebuildProcessArtifacts artifacts,
        RequestContractReference request,
        DurableRequestBinding binding,
        ExecutionDefinitionReference definition,
        ExecutionNodeId node)
    {
        Assert.True(artifacts.InteractionCatalog.TryResolve(request, out var resolved));
        var requestDefinition = Assert.IsType<RequestContractDefinition>(resolved);
        Assert.Equal(RequestRetrySemantics.ReconcileBeforeRetry, requestDefinition.Response.Retry);
        Assert.Equal(RequestResolutionSemantics.Reconcile, requestDefinition.Response.AmbiguousOutcome);
        Assert.Equal(RequestResolutionSemantics.Reconcile, requestDefinition.Response.UnresolvedOutcome);
        Assert.Equal(new DurableOperationResolutionTarget(definition, node), binding.ReconciliationTarget);
        Assert.Null(binding.TerminalFailureOutcome);
    }

    [Fact]
    public void CanonicalDocuments_RoundTripStrictlyAndFactoryReproducesFingerprints()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var reproduced = MaterializationRebuildProcessFactory.Create();

        Assert.Equal(
            artifacts.InteractionDocuments.Select(static document => document.Metadata.Fingerprint),
            reproduced.InteractionDocuments.Select(static document => document.Metadata.Fingerprint));
        Assert.Equal(
            artifacts.ProcessDocuments.Select(static document => document.Metadata.Fingerprint),
            reproduced.ProcessDocuments.Select(static document => document.Metadata.Fingerprint));

        foreach (var document in artifacts.InteractionDocuments)
        {
            var json = ExecutionDefinitionJsonSerializer.Serialize(document);
            var validation = InteractionContractDocuments.TryDeserialize(
                json,
                out var restoredDocument,
                out var restoredDefinition);

            Assert.True(validation.IsValid, FormatDiagnostics(validation));
            Assert.NotNull(restoredDefinition);
            Assert.Equal(document, restoredDocument);
            Assert.Equal(
                ExecutionDefinitionJsonSerializer.GetCanonicalBytes(document),
                ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));
        }

        AssertProcessRoundTrip(artifacts.WorkerPlan);
        AssertProcessRoundTrip(artifacts.CoordinatorPlan);
    }

    [Fact]
    public void Coordinator_AdmitsOnlyTwoOfThreeStableShardsInTheFirstActivation()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var start = Start(
            artifacts.CoordinatorPlan,
            PortableValue.Concrete(
                artifacts.CoordinatorPlan.Definition.Input,
                ObservationValue.FromString("materialization-rebuild-plan/example")));
        var initial = ProcessReferenceInterpreter.Create(artifacts.CoordinatorPlan, start);

        var initialized = ProcessReferenceInterpreter.Activate(
            artifacts.CoordinatorPlan,
            initial,
            Activation(artifacts.CoordinatorProcessDocument.Metadata.Provenance),
            RejectingHost.Instance);
        var initializationRequest = Assert.IsType<RequestEnvelope>(Assert.Single(initialized.Emissions));
        var target = Assert.IsType<ProcessTokenInteractionTarget>(initializationRequest.ResponseTarget);
        var initializationReply = new ReplyEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new("emission/materialization-rebuild/initialized"),
                new ProcessInteractionOrigin(
                    artifacts.CoordinatorPlan.DefinitionReference,
                    new("storage.materialization-rebuild.initialize"),
                    initialized.State.Continuation,
                    new("activation/materialization-rebuild/initialize"),
                    target.Token),
                new("correlation/materialization-rebuild"),
                initializationRequest.Context.EmissionId,
                Authority,
                new("idempotency/materialization-rebuild/initialized"),
                ordering: null,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                Provenance()),
            artifacts.InitializationBinding.FindReply(MaterializationRebuildProcessFactory.CompletedOutcome)!.Reply,
            initializationRequest.Context.EmissionId,
            new RequestResultOutcome(
                MaterializationRebuildProcessFactory.CompletedOutcome,
                Shards("shard-c", "shard-a", "shard-b")));
        var decision = ProcessReferenceInterpreter.Activate(
            artifacts.CoordinatorPlan,
            initialized.State,
            new(
                id: new("activation/materialization-rebuild/partitions"),
                cause: ProcessActivationCause.Interaction,
                observedAtUtc: StartedAtUtc.AddMinutes(1),
                context: new(
                    authorityScope: Authority,
                    correlationId: new("correlation/materialization-rebuild"),
                    delivery: new(
                        InteractionDurabilityDemand.Durable,
                        InteractionVisibilityDemand.AfterOriginCommit),
                    provenance: artifacts.CoordinatorProcessDocument.Metadata.Provenance),
                inputs: [new(target, initializationReply)]),
            RejectingHost.Instance);

        Assert.True(
            decision.Disposition == ProcessActivationDisposition.DurableCut,
            $"Expected a durable cut but observed {decision.Disposition}: {string.Join("; ", decision.Diagnostics.Select(static diagnostic => diagnostic.Message))}");
        Assert.Equal(2, decision.Emissions.Length);
        Assert.Equal(
            2,
            decision.State.Children.Count(static child =>
                child.Disposition == ProcessChildDisposition.Active));
        Assert.Single(decision.State.Children, static child =>
            child.Disposition == ProcessChildDisposition.Pending);
        Assert.Equal(
            ["shard-a", "shard-b", "shard-c"],
            Assert.Single(decision.State.Partitions).Work.Select(static work => work.ProgressIdentity));
        Assert.All(decision.Emissions.OfType<RequestEnvelope>(), request =>
        {
            Assert.Equal(artifacts.WorkerInvocationRequest, request.Contract);
            Assert.Equal(
                artifacts.WorkerPlan.DefinitionReference,
                Assert.IsType<ProcessChildRequestTarget>(request.ChildTarget).Definition);
        });
    }

    static void AssertProcessRoundTrip(Cohesive.Processes.Compilation.CompiledProcessPlan plan)
    {
        var json = ExecutionDefinitionJsonSerializer.Serialize(plan.Document);
        var validation = ProcessDefinitionDocuments.TryDeserialize(
            json,
            plan.ValidationContext,
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        Assert.Equal(plan.Definition, restoredDefinition);
        Assert.Equal(plan.Document, restoredDocument);
        Assert.Equal(
            ExecutionDefinitionJsonSerializer.GetCanonicalBytes(plan.Document),
            ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));
    }

    static ProcessStartReceipt Start(
        Cohesive.Processes.Compilation.CompiledProcessPlan plan,
        PortableValue input)
    {
        ProcessContinuationIdentity continuation = new(
            new("process-instance/materialization-rebuild"),
            new("process-attempt/1"));
        var request = new ProcessStartRequest(
            schemaVersion: ProcessStartRequest.CurrentSchemaVersion,
            definition: plan.DefinitionReference,
            context: new(
                commandId: new("start-command/materialization-rebuild"),
                idempotencyKey: new("start-idempotency/materialization-rebuild"),
                processInstanceId: continuation.ProcessInstanceId,
                authorization: new(
                    actor: "operator/tests",
                    authorityScope: Authority,
                    evidenceReference: "policy/tests/allow"),
                issuedAtUtc: StartedAtUtc,
                provenance: Provenance()),
            initialContinuation: continuation,
            input);
        return new(request, acceptedAtUtc: StartedAtUtc);
    }

    static ProcessActivation Activation(ExecutionProvenance provenance) => new(
        id: new("activation/materialization-rebuild/start"),
        cause: ProcessActivationCause.Start,
        observedAtUtc: StartedAtUtc,
        context: new(
            authorityScope: Authority,
            correlationId: new("correlation/materialization-rebuild"),
            delivery: new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            provenance));

    static PortableValue Shards(params string[] shards) => PortableValue.Concrete(
        new ValueContract(new ScalarTypeRef(ScalarTypeKind.String), cardinality: FieldCardinality.Many),
        ObservationValue.FromArray(
            [.. shards.Select(static shard => ObservationValue.FromString(shard))]));

    static ExecutionProvenance Provenance() => new(
        new ExecutionProducerProvenance("cohesive-tests", "1"),
        new ExecutionSourceProvenance("test:materialization-rebuild-process"),
        DocumentOrigin.Generated);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

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
