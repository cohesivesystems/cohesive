using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// The exact canonical interaction and Process artifacts that define the reference materialization-rebuild
/// coordination protocol.
/// </summary>
/// <remarks>
/// The coordinator owns only bounded partition-to-child coordination. The worker's durable Request delegates one
/// shard to the Storage-owned rebuild interpreter, which remains responsible for pages, cursors, item progress,
/// generation state, and convergence evidence.
/// </remarks>
public sealed class MaterializationRebuildProcessArtifacts
{
    internal MaterializationRebuildProcessArtifacts(
        ImmutableArray<ExecutionDefinitionDocument> interactionDocuments,
        InteractionContractCatalog interactionCatalog,
        RequestContractReference initializationRequest,
        DurableRequestBinding initializationBinding,
        RequestContractReference workerInvocationRequest,
        DurableRequestBinding workerInvocationBinding,
        RequestContractReference shardRebuildRequest,
        DurableRequestBinding shardRebuildBinding,
        ExecutionDefinitionDocument workerProcessDocument,
        CompiledProcessPlan workerPlan,
        ExecutionDefinitionDocument coordinatorProcessDocument,
        CompiledProcessPlan coordinatorPlan)
    {
        InteractionDocuments = interactionDocuments;
        InteractionCatalog = interactionCatalog;
        InitializationRequest = initializationRequest;
        InitializationBinding = initializationBinding;
        WorkerInvocationRequest = workerInvocationRequest;
        WorkerInvocationBinding = workerInvocationBinding;
        ShardRebuildRequest = shardRebuildRequest;
        ShardRebuildBinding = shardRebuildBinding;
        WorkerProcessDocument = workerProcessDocument;
        WorkerPlan = workerPlan;
        CoordinatorProcessDocument = coordinatorProcessDocument;
        CoordinatorPlan = coordinatorPlan;
        ProcessDocuments = [workerProcessDocument, coordinatorProcessDocument];
        DurableRequestBindings = [initializationBinding, workerInvocationBinding, shardRebuildBinding];
    }

    /// <summary>Exact Request and Reply contract documents used by both Processes.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> InteractionDocuments { get; }

    /// <summary>Validated exact-reference catalog assembled from <see cref="InteractionDocuments"/>.</summary>
    public InteractionContractCatalog InteractionCatalog { get; }

    /// <summary>Request contract through which each coordinator attempt resolves and initializes its rebuild.</summary>
    public RequestContractReference InitializationRequest { get; }

    /// <summary>Durable execution refinement for attempt-scoped rebuild initialization.</summary>
    public DurableRequestBinding InitializationBinding { get; }

    /// <summary>Request contract through which the coordinator starts and joins worker Processes.</summary>
    public RequestContractReference WorkerInvocationRequest { get; }

    /// <summary>Durable execution refinement for the coordinator-to-worker Request.</summary>
    public DurableRequestBinding WorkerInvocationBinding { get; }

    /// <summary>Request contract through which a worker delegates one shard to Storage.</summary>
    public RequestContractReference ShardRebuildRequest { get; }

    /// <summary>Durable execution refinement for the Storage-owned shard rebuild Request.</summary>
    public DurableRequestBinding ShardRebuildBinding { get; }

    /// <summary>Canonical worker Process document.</summary>
    public ExecutionDefinitionDocument WorkerProcessDocument { get; }

    /// <summary>Validated target-independent plan for <see cref="WorkerProcessDocument"/>.</summary>
    public CompiledProcessPlan WorkerPlan { get; }

    /// <summary>Canonical coordinator Process document.</summary>
    public ExecutionDefinitionDocument CoordinatorProcessDocument { get; }

    /// <summary>Validated target-independent plan for <see cref="CoordinatorProcessDocument"/>.</summary>
    public CompiledProcessPlan CoordinatorPlan { get; }

    /// <summary>Worker then coordinator Process documents in dependency order.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> ProcessDocuments { get; }

    /// <summary>Initialization, coordinator-to-worker, and worker-to-Storage durable Request bindings.</summary>
    public ImmutableArray<DurableRequestBinding> DurableRequestBindings { get; }
}

/// <summary>Creates the exact canonical reference Process protocol for a durable partitioned index rebuild.</summary>
public static class MaterializationRebuildProcessFactory
{
    const string InitializationRequestDefinitionValue =
        "interaction/request/cohesive-storage-materialization-rebuild-initialize";
    const string WorkerInvocationRequestDefinitionValue =
        "interaction/request/cohesive-storage-materialization-rebuild-worker";
    const string ShardRebuildRequestDefinitionValue =
        "interaction/request/cohesive-storage-materialization-rebuild-shard";

    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract StringCollectionContract = new(
        new ScalarTypeRef(ScalarTypeKind.String),
        cardinality: FieldCardinality.Many);

    /// <summary>Exact semantic revision shared by the coordinated reference protocol.</summary>
    public static ExecutionRevisionId RevisionId { get; } = new("revision/1");

    /// <summary>Stable identity of the coordinator Process definition.</summary>
    public static ExecutionDefinitionId CoordinatorDefinitionId { get; } =
        new("process/cohesive-storage-materialization-rebuild-coordinator");

    /// <summary>Stable identity of the per-shard worker Process definition.</summary>
    public static ExecutionDefinitionId WorkerDefinitionId { get; } =
        new("process/cohesive-storage-materialization-rebuild-worker");

    /// <summary>Stable coordinator partition node identity.</summary>
    public static ExecutionNodeId CoordinatorPartitionsNodeId { get; } = new("coordinator.partitions");

    /// <summary>Stable coordinator attempt-initialization Request node identity.</summary>
    public static ExecutionNodeId CoordinatorInitializationNodeId { get; } = new("coordinator.initialize");

    /// <summary>Stable coordinator successful terminal node identity.</summary>
    public static ExecutionNodeId CoordinatorReturnNodeId { get; } = new("coordinator.return");

    /// <summary>Stable coordinator failed terminal node identity.</summary>
    public static ExecutionNodeId CoordinatorFailNodeId { get; } = new("coordinator.fail");

    /// <summary>Stable Storage-owned worker Request node identity.</summary>
    public static ExecutionNodeId WorkerRequestNodeId { get; } = new("worker.request");

    /// <summary>Stable worker successful terminal node identity.</summary>
    public static ExecutionNodeId WorkerReturnNodeId { get; } = new("worker.return");

    /// <summary>Stable worker failed-outcome terminal node identity.</summary>
    public static ExecutionNodeId WorkerFailedNodeId { get; } = new("worker.fail.failed");

    /// <summary>Stable worker cancelled-outcome terminal node identity.</summary>
    public static ExecutionNodeId WorkerCancelledNodeId { get; } = new("worker.fail.cancelled");

    /// <summary>Stable worker terminated-outcome terminal node identity.</summary>
    public static ExecutionNodeId WorkerTerminatedNodeId { get; } = new("worker.fail.terminated");

    /// <summary>Stable successful Request outcome identity.</summary>
    public static RequestTerminalOutcomeId CompletedOutcome { get; } = new("completed");

    /// <summary>Stable failed Request outcome identity.</summary>
    public static RequestTerminalOutcomeId FailedOutcome { get; } = new("failed");

    /// <summary>Stable cancelled Request outcome identity.</summary>
    public static RequestTerminalOutcomeId CancelledOutcome { get; } = new("cancelled");

    /// <summary>Stable terminated Request outcome identity.</summary>
    public static RequestTerminalOutcomeId TerminatedOutcome { get; } = new("terminated");

    /// <summary>Total mapping between child Process terminal states and Request outcome identities.</summary>
    public static ProcessChildOutcomeMapping ChildOutcomeMapping { get; } = new(
        CompletedOutcome,
        FailedOutcome,
        CancelledOutcome,
        TerminatedOutcome);

    static readonly ImmutableArray<RebuildOutcomeSemantics> Outcomes =
    [
        new(
            CompletedOutcome,
            Successful: true,
            WorkerReturnNodeId,
            new("worker.request.completed")),
        new(
            FailedOutcome,
            Successful: false,
            WorkerFailedNodeId,
            new("worker.request.failed")),
        new(
            CancelledOutcome,
            Successful: false,
            WorkerCancelledNodeId,
            new("worker.request.cancelled")),
        new(
            TerminatedOutcome,
            Successful: false,
            WorkerTerminatedNodeId,
            new("worker.request.terminated"))
    ];

    /// <summary>Successful coordinator result reached after every baseline shard completes.</summary>
    public const string BaselineCompleteCatchUpRequired = "baseline-complete/catch-up-required";

    /// <summary>Maximum partition count admitted by this exact canonical Process revision.</summary>
    public const int MaximumPartitions = 1024;

    /// <summary>Maximum child starts admitted by one finite coordinator activation.</summary>
    public const int MaximumStartsPerActivation = 2;

    /// <summary>Maximum concurrently active child workers admitted by the coordinator.</summary>
    public const int MaximumParallelism = 2;

    /// <summary>Creates, links, and compiles the deterministic canonical reference artifacts.</summary>
    /// <returns>
    /// Exact interaction documents and catalog, durable Request bindings, Process documents, and compiled plans.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The framework rejects an internally authored contract, Process link, or Process compilation.
    /// </exception>
    public static MaterializationRebuildProcessArtifacts Create()
    {
        var initialization = CreateRequestProtocol(
            definitionId: new(InitializationRequestDefinitionValue),
            schemaAuthority: "materialization-rebuild-initialize",
            completedContract: StringCollectionContract);
        var workerInvocation = CreateRequestProtocol(
            definitionId: new(WorkerInvocationRequestDefinitionValue),
            schemaAuthority: "materialization-rebuild-worker",
            completedContract: StringContract);
        var shardRebuild = CreateRequestProtocol(
            definitionId: new(ShardRebuildRequestDefinitionValue),
            schemaAuthority: "materialization-rebuild-shard",
            completedContract: StringContract);
        var interactionDocuments = initialization.Documents
            .AddRange(workerInvocation.Documents)
            .AddRange(shardRebuild.Documents);
        var catalogValidation = InteractionContractCatalog.TryCreate(
            interactionDocuments,
            out var interactionCatalog);
        RequireValid(catalogValidation, "interaction contract catalog");
        var exactCatalog = interactionCatalog
            ?? throw new InvalidOperationException("A valid interaction contract catalog was not produced.");

        var workerDefinition = WorkerDefinition(shardRebuild.Request);
        var workerDocument = ProcessDefinitionDocuments.Create(
            WorkerDefinitionId,
            RevisionId,
            workerDefinition,
            Provenance(source: "ari-176/materialization-rebuild-worker"));
        var workerContext = new ProcessDefinitionValidationContext(
            interactionContracts: exactCatalog);
        var workerCompilation = ProcessStaticCompiler.Compile(workerDocument, workerContext);
        RequireValid(workerCompilation.Validation, "worker Process compilation");
        var workerPlan = workerCompilation.Plan
            ?? throw new InvalidOperationException("A valid worker Process plan was not produced.");

        var workerLinkValidation = ProcessDefinitionLink.TryCreateProcess(
            workerDocument,
            workerContext,
            out var workerLink);
        RequireValid(workerLinkValidation, "worker Process link");
        var exactWorkerLink = workerLink
            ?? throw new InvalidOperationException("A valid worker Process link was not produced.");

        var coordinatorDefinition = CoordinatorDefinition(
            workerPlan.DefinitionReference,
            initialization.Request,
            workerInvocation.Request);
        var coordinatorDocument = ProcessDefinitionDocuments.Create(
            CoordinatorDefinitionId,
            RevisionId,
            coordinatorDefinition,
            Provenance(source: "ari-176/materialization-rebuild-coordinator"));
        var coordinatorContext = new ProcessDefinitionValidationContext(
            definitions: [exactWorkerLink],
            interactionContracts: exactCatalog);
        var coordinatorCompilation = ProcessStaticCompiler.Compile(coordinatorDocument, coordinatorContext);
        RequireValid(coordinatorCompilation.Validation, "coordinator Process compilation");
        var coordinatorPlan = coordinatorCompilation.Plan
            ?? throw new InvalidOperationException("A valid coordinator Process plan was not produced.");

        var initializationBinding = CreateDurableBinding(
            initialization,
            new(coordinatorPlan.DefinitionReference, CoordinatorInitializationNodeId));
        var workerInvocationBinding = CreateDurableBinding(
            workerInvocation,
            new(coordinatorPlan.DefinitionReference, CoordinatorPartitionsNodeId));
        var shardRebuildBinding = CreateDurableBinding(
            shardRebuild,
            new(workerPlan.DefinitionReference, WorkerRequestNodeId));

        return new(
            interactionDocuments,
            exactCatalog,
            initialization.Request,
            initializationBinding,
            workerInvocation.Request,
            workerInvocationBinding,
            shardRebuild.Request,
            shardRebuildBinding,
            workerDocument,
            workerPlan,
            coordinatorDocument,
            coordinatorPlan);
    }

    static CanonicalProcessDefinition CoordinatorDefinition(
        ExecutionDefinitionReference worker,
        RequestContractReference initializationRequest,
        RequestContractReference workerInvocationRequest)
    {
        ProcessOutputBinding shards = new(
            new("coordinator.initialization.completed"),
            StringCollectionContract);
        ProcessOutputBinding partition = new(
            new("coordinator.partition"),
            StringContract);
        return new(
            StringContract,
            StringContract,
            CoordinatorInitializationNodeId,
            [
                new RequestProcessNode(
                    CoordinatorInitializationNodeId,
                    initializationRequest,
                    Expr.BoundValue(ProcessBindingIds.Input),
                    [
                        new(
                            new("coordinator.initialize.completed"),
                            CompletedOutcome,
                            new(
                                Edge(
                                    id: "coordinator.initialize.completed.partitions",
                                    target: CoordinatorPartitionsNodeId),
                                shards)),
                        FailureBranch("failed", FailedOutcome),
                        FailureBranch("cancelled", CancelledOutcome),
                        FailureBranch("terminated", TerminatedOutcome)
                    ]),
                new ForEachPartitionProcessNode(
                    CoordinatorPartitionsNodeId,
                    Expr.BoundValue(shards.Binding),
                    partition,
                    Expr.BoundValue(partition.Binding),
                    worker,
                    workerInvocationRequest,
                    ChildOutcomeMapping,
                    Expr.BoundValue(partition.Binding),
                    new(
                        maximumItems: MaximumPartitions,
                        maximumStartsPerActivation: MaximumStartsPerActivation,
                        maximumParallelism: MaximumParallelism),
                    ProcessChildCancellationPolicy.Propagate,
                    Edge(id: "coordinator.partitions.completed", target: CoordinatorReturnNodeId),
                    Edge(id: "coordinator.partitions.failed", target: CoordinatorFailNodeId)),
                new ReturnProcessNode(
                    CoordinatorReturnNodeId,
                    Expr.Const(BaselineCompleteCatchUpRequired)),
                new FailProcessNode(
                    CoordinatorFailNodeId,
                    Expr.Const("materialization-rebuild-failed"))
            ],
            ProcessRecoveryPolicy.RestartAttempt);

        ProcessRequestOutcomeBranch FailureBranch(string name, RequestTerminalOutcomeId outcome) => new(
            new($"coordinator.initialize.{name}"),
            outcome,
            new(
                Edge(
                    id: $"coordinator.initialize.{name}.failed",
                    target: CoordinatorFailNodeId),
                new(
                    new($"coordinator.initialization.{name}"),
                    StringContract)));
    }

    static CanonicalProcessDefinition WorkerDefinition(RequestContractReference shardRebuildRequest)
    {
        var branches = ImmutableArray.CreateBuilder<ProcessRequestOutcomeBranch>(Outcomes.Length);
        var nodes = ImmutableArray.CreateBuilder<ProcessNode>(Outcomes.Length + 1);
        foreach (var outcome in Outcomes)
        {
            var output = new ProcessOutputBinding(outcome.Output, StringContract);
            branches.Add(new(
                id: new($"worker.request.{outcome.Outcome.Value}"),
                outcome: outcome.Outcome,
                continuation: new(
                    Edge(
                        id: $"worker.request.{outcome.Outcome.Value}.terminal",
                        target: outcome.Target),
                    output)));
            if (outcome.Successful)
                nodes.Add(new ReturnProcessNode(outcome.Target, Expr.BoundValue(outcome.Output)));
            else
                nodes.Add(new FailProcessNode(outcome.Target, Expr.BoundValue(outcome.Output)));
        }

        nodes.Add(new RequestProcessNode(
            WorkerRequestNodeId,
            shardRebuildRequest,
            Expr.BoundValue(ProcessBindingIds.Input),
            branches.MoveToImmutable()));
        return new(
            StringContract,
            StringContract,
            WorkerRequestNodeId,
            nodes.MoveToImmutable(),
            ProcessRecoveryPolicy.ContinueAttempt);
    }

    static (
        ImmutableArray<ExecutionDefinitionDocument> Documents,
        RequestContractReference Request,
        ImmutableArray<DurableReplyBinding> Replies) CreateRequestProtocol(
        ExecutionDefinitionId definitionId,
        string schemaAuthority,
        ValueContract completedContract)
    {
        var terminalOutcomes = ImmutableArray.CreateBuilder<RequestTerminalOutcomeDefinition>(Outcomes.Length);
        foreach (var outcome in Outcomes)
        {
            var schema = ValueSchema(
                outcome.Successful ? completedContract : StringContract,
                revision: $"{schemaAuthority}/{outcome.Outcome.Value}/v1");
            terminalOutcomes.Add(outcome.Successful
                ? new RequestResultDefinition(outcome.Outcome, schema)
                : new RequestFailureDefinition(outcome.Outcome, schema));
        }

        var requestDocument = InteractionContractDocuments.Create(
            definitionId,
            RevisionId,
            new RequestContractDefinition(
                ValueSchema(StringContract, revision: $"{schemaAuthority}/payload/v1"),
                new(
                    terminalOutcomes.MoveToImmutable(),
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestResultDisposition.Observe,
                    RequestResultDisposition.Reject,
                    RequestResultDisposition.ReusePriorDisposition,
                    RequestRetrySemantics.ReconcileBeforeRetry,
                    RequestResolutionSemantics.Reconcile,
                    RequestResolutionSemantics.Reconcile,
                    retentionHorizon: TimeSpan.FromDays(30))),
            Provenance(source: $"ari-176/{schemaAuthority}-request"));
        RequestContractReference request = new(Reference(requestDocument));
        var documents = ImmutableArray.CreateBuilder<ExecutionDefinitionDocument>(Outcomes.Length + 1);
        var replies = ImmutableArray.CreateBuilder<DurableReplyBinding>(Outcomes.Length);
        documents.Add(requestDocument);
        foreach (var outcome in Outcomes)
        {
            var replyDocument = InteractionContractDocuments.Create(
                definitionId: new($"{definitionId.Value}/reply/{outcome.Outcome.Value}"),
                RevisionId,
                new ReplyContractDefinition(request, outcome.Outcome),
                Provenance(source: $"ari-176/{schemaAuthority}-reply/{outcome.Outcome.Value}"));
            var reply = new ReplyContractReference(Reference(replyDocument));
            documents.Add(replyDocument);
            replies.Add(new(outcome.Outcome, reply));
        }

        return (
            documents.MoveToImmutable(),
            request,
            replies.MoveToImmutable());
    }

    static DurableRequestBinding CreateDurableBinding(
        (ImmutableArray<ExecutionDefinitionDocument> Documents,
            RequestContractReference Request,
            ImmutableArray<DurableReplyBinding> Replies) protocol,
        DurableOperationResolutionTarget reconciliationTarget) => new(
            protocol.Request,
            protocol.Replies,
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            timeoutAfter: null,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            reconciliationTarget: reconciliationTarget);

    static ProcessEdge Edge(string id, ExecutionNodeId target) => new(new(id), target);

    static InteractionValueSchema ValueSchema(ValueContract contract, string revision) => new(
        contract,
        new(revision));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance(string source) => new(
        new ExecutionProducerProvenance("cohesive-storage-materialization-rebuild", "1"),
        new ExecutionSourceProvenance(source),
        DocumentOrigin.Generated);

    static void RequireValid(DocumentValidationResult validation, string stage)
    {
        if (validation.IsValid)
            return;

        throw new InvalidOperationException(
            $"Canonical {stage} failed: {string.Join("; ", validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"))}");
    }

    readonly record struct RebuildOutcomeSemantics(
        RequestTerminalOutcomeId Outcome,
        bool Successful,
        ExecutionNodeId Target,
        ValueBindingId Output);
}
