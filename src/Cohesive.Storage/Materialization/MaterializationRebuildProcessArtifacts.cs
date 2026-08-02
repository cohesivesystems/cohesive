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
/// The coordinator owns bounded partition-to-child coordination and bounded recurrence across synchronization
/// preparations. Storage-owned durable Requests remain responsible for pages, cursors, item progress, generation
/// lifecycle state, convergence evidence, and the retained promotion intent returned to the owning parent.
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
        RequestContractReference synchronizationPreparationRequest,
        DurableRequestBinding synchronizationPreparationBinding,
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
        SynchronizationPreparationRequest = synchronizationPreparationRequest;
        SynchronizationPreparationBinding = synchronizationPreparationBinding;
        WorkerProcessDocument = workerProcessDocument;
        WorkerPlan = workerPlan;
        CoordinatorProcessDocument = coordinatorProcessDocument;
        CoordinatorPlan = coordinatorPlan;
        ProcessDocuments = [workerProcessDocument, coordinatorProcessDocument];
        DurableRequestBindings =
        [
            initializationBinding,
            workerInvocationBinding,
            shardRebuildBinding,
            synchronizationPreparationBinding
        ];
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

    /// <summary>
    /// Request contract through which the coordinator drives one bounded synchronization-and-readiness occurrence.
    /// </summary>
    public RequestContractReference SynchronizationPreparationRequest { get; }

    /// <summary>Durable execution refinement for the Storage-owned synchronization-and-readiness Request.</summary>
    public DurableRequestBinding SynchronizationPreparationBinding { get; }

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

    /// <summary>
    /// Initialization, coordinator-to-worker, worker-to-Storage, and synchronization-and-readiness bindings.
    /// </summary>
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
    const string SynchronizationPreparationRequestDefinitionValue =
        "interaction/request/cohesive-storage-materialization-synchronize-and-prepare";

    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract StringCollectionContract = new(
        new ScalarTypeRef(ScalarTypeKind.String),
        cardinality: FieldCardinality.Many);

    /// <summary>Exact semantic revision shared by the coordinated reference protocol.</summary>
    public static ExecutionRevisionId RevisionId { get; } = new("revision/3");

    /// <summary>Stable identity of the coordinator Process definition.</summary>
    public static ExecutionDefinitionId CoordinatorDefinitionId { get; } =
        new("process/cohesive-storage-materialization-rebuild-coordinator");

    /// <summary>Stable identity of the parent-owned leaf coordinator using ContinueAttempt recovery.</summary>
    public static ExecutionDefinitionId ChildCoordinatorDefinitionId { get; } =
        new("process/cohesive-storage-materialization-rebuild-leaf");

    /// <summary>Stable identity of the per-shard worker Process definition.</summary>
    public static ExecutionDefinitionId WorkerDefinitionId { get; } =
        new("process/cohesive-storage-materialization-rebuild-worker");

    /// <summary>Stable coordinator partition node identity.</summary>
    public static ExecutionNodeId CoordinatorPartitionsNodeId { get; } = new("coordinator.partitions");

    /// <summary>Stable Storage-owned synchronization-and-readiness Request node identity.</summary>
    public static ExecutionNodeId CoordinatorSynchronizationPreparationNodeId { get; } =
        new("coordinator.synchronize-and-prepare");

    /// <summary>Stable durable recurrence node identity for bounded catch-up continuation.</summary>
    public static ExecutionNodeId CoordinatorSynchronizationRecurrenceNodeId { get; } =
        new("coordinator.synchronization-recurrence");

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

    /// <summary>Successful synchronization outcome proving durable progress while more work remains.</summary>
    public static RequestTerminalOutcomeId WorkRemainingOutcome { get; } = new("workRemaining");

    /// <summary>Successful preparation outcome carrying an exact ready-generation reference.</summary>
    public static RequestTerminalOutcomeId ReadyOutcome { get; } = new("ready");

    /// <summary>Successful activation outcome carrying an exact active-generation reference.</summary>
    public static RequestTerminalOutcomeId ActiveOutcome { get; } = new("active");

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

    static readonly ImmutableArray<RebuildOutcomeSemantics> SynchronizationOutcomes =
    [
        new(
            WorkRemainingOutcome,
            Successful: true,
            CoordinatorSynchronizationRecurrenceNodeId,
            new("coordinator.synchronization.progress")),
        new(
            ReadyOutcome,
            Successful: true,
            CoordinatorReturnNodeId,
            new("coordinator.ready-generation")),
        new(FailedOutcome, Successful: false, CoordinatorFailNodeId, new("coordinator.synchronization.failed")),
        new(CancelledOutcome, Successful: false, CoordinatorFailNodeId, new("coordinator.synchronization.cancelled")),
        new(TerminatedOutcome, Successful: false, CoordinatorFailNodeId, new("coordinator.synchronization.terminated"))
    ];

    /// <summary>Successful coordinator result reached after every baseline shard completes.</summary>
    public const string BaselineCompleteCatchUpRequired = "baseline-complete/catch-up-required";

    /// <summary>Maximum partition count admitted by this exact canonical Process revision.</summary>
    public const int MaximumPartitions = 1024;

    /// <summary>Maximum child starts admitted by one finite coordinator activation.</summary>
    public const int MaximumStartsPerActivation = 2;

    /// <summary>Maximum concurrently active child workers admitted by the coordinator.</summary>
    public const int MaximumParallelism = 2;

    /// <summary>Maximum bounded synchronization occurrences admitted by one coordinator Process attempt.</summary>
    public const int MaximumSynchronizationOccurrences = 1024;

    /// <summary>Maximum consecutive synchronization occurrences admitted without durable progress.</summary>
    public const int MaximumUnchangedSynchronizationOccurrences = 2;

    /// <summary>Creates, links, and compiles the deterministic canonical reference artifacts.</summary>
    /// <returns>
    /// Exact interaction documents and catalog, durable Request bindings, Process documents, and compiled plans.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The framework rejects an internally authored contract, Process link, or Process compilation.
    /// </exception>
    public static MaterializationRebuildProcessArtifacts Create() =>
        CreateCore(
            CoordinatorDefinitionId,
            ProcessRecoveryPolicy.RestartAttempt,
            source: "ari-194/materialization-rebuild-coordinator");

    /// <summary>Creates the parent-owned leaf variant whose recovery continues its exact child attempt.</summary>
    /// <returns>
    /// Exact interaction documents and catalog, durable Request bindings, Process documents, and compiled plans.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The framework rejects an internally authored contract, Process link, or Process compilation.
    /// </exception>
    public static MaterializationRebuildProcessArtifacts CreateChild() =>
        CreateCore(
            ChildCoordinatorDefinitionId,
            ProcessRecoveryPolicy.ContinueAttempt,
            source: "ari-194/materialization-rebuild-leaf");

    static MaterializationRebuildProcessArtifacts CreateCore(
        ExecutionDefinitionId coordinatorDefinitionId,
        ProcessRecoveryPolicy recoveryPolicy,
        string source)
    {
        var initialization = CreateRequestProtocol(
            definitionId: new(InitializationRequestDefinitionValue),
            schemaAuthority: "materialization-rebuild-initialize",
            completedContract: StringCollectionContract,
            outcomes: Outcomes);
        var workerInvocation = CreateRequestProtocol(
            definitionId: new(WorkerInvocationRequestDefinitionValue),
            schemaAuthority: "materialization-rebuild-worker",
            completedContract: StringContract,
            outcomes: Outcomes);
        var shardRebuild = CreateRequestProtocol(
            definitionId: new(ShardRebuildRequestDefinitionValue),
            schemaAuthority: "materialization-rebuild-shard",
            completedContract: StringContract,
            outcomes: Outcomes);
        var synchronizationPreparation = CreateRequestProtocol(
            definitionId: new(SynchronizationPreparationRequestDefinitionValue),
            schemaAuthority: "materialization-synchronize-and-prepare",
            completedContract: StringContract,
            outcomes: SynchronizationOutcomes);
        var interactionDocuments = initialization.Documents
            .AddRange(workerInvocation.Documents)
            .AddRange(shardRebuild.Documents)
            .AddRange(synchronizationPreparation.Documents);
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
            Provenance(source: "ari-177/materialization-rebuild-worker"));
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
            workerInvocation.Request,
            synchronizationPreparation.Request,
            recoveryPolicy);
        var coordinatorDocument = ProcessDefinitionDocuments.Create(
            coordinatorDefinitionId,
            RevisionId,
            coordinatorDefinition,
            Provenance(source));
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
        var synchronizationPreparationBinding = CreateDurableBinding(
            synchronizationPreparation,
            new(coordinatorPlan.DefinitionReference, CoordinatorSynchronizationPreparationNodeId));

        return new(
            interactionDocuments,
            exactCatalog,
            initialization.Request,
            initializationBinding,
            workerInvocation.Request,
            workerInvocationBinding,
            shardRebuild.Request,
            shardRebuildBinding,
            synchronizationPreparation.Request,
            synchronizationPreparationBinding,
            workerDocument,
            workerPlan,
            coordinatorDocument,
            coordinatorPlan);
    }

    static CanonicalProcessDefinition CoordinatorDefinition(
        ExecutionDefinitionReference worker,
        RequestContractReference initializationRequest,
        RequestContractReference workerInvocationRequest,
        RequestContractReference synchronizationPreparationRequest,
        ProcessRecoveryPolicy recoveryPolicy)
    {
        ProcessOutputBinding shards = new(
            new("coordinator.initialization.completed"),
            StringCollectionContract);
        ProcessOutputBinding partition = new(
            new("coordinator.partition"),
            StringContract);
        ProcessOutputBinding progress = new(
            new("coordinator.synchronization.progress"),
            StringContract);
        ProcessOutputBinding readyGeneration = new(
            new("coordinator.ready-generation"),
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
                    ProcessPartitionFailurePolicy.FailFast,
                    capacityIdentity: null,
                    capacityDomains: [],
                    ProcessChildCancellationPolicy.Propagate,
                    Edge(
                        id: "coordinator.partitions.completed.synchronize",
                        target: CoordinatorSynchronizationPreparationNodeId),
                    Edge(id: "coordinator.partitions.failed", target: CoordinatorFailNodeId)),
                new RequestProcessNode(
                    CoordinatorSynchronizationPreparationNodeId,
                    synchronizationPreparationRequest,
                    Expr.BoundValue(ProcessBindingIds.Input),
                    [
                        new(
                            new("coordinator.synchronize.work-remaining"),
                            WorkRemainingOutcome,
                            new(
                                Edge(
                                    id: "coordinator.synchronize.work-remaining.recur",
                                    target: CoordinatorSynchronizationRecurrenceNodeId),
                                progress)),
                        new(
                            new("coordinator.synchronize.ready"),
                            ReadyOutcome,
                            new(
                                Edge(
                                    id: "coordinator.synchronize.ready.return",
                                    target: CoordinatorReturnNodeId),
                                readyGeneration)),
                        SynchronizationFailureBranch("failed", FailedOutcome),
                        SynchronizationFailureBranch("cancelled", CancelledOutcome),
                        SynchronizationFailureBranch("terminated", TerminatedOutcome)
                    ]),
                new RepeatAcrossActivationProcessNode(
                    CoordinatorSynchronizationRecurrenceNodeId,
                    continueWhen: Expr.Const(true),
                    progress: Expr.BoundValue(progress.Binding),
                    progressContract: StringContract,
                    policy: new(
                        maximumOccurrences: MaximumSynchronizationOccurrences,
                        maximumUnchangedProgressOccurrences: MaximumUnchangedSynchronizationOccurrences),
                    repeat: Edge(
                        id: "coordinator.synchronization-recurrence.repeat",
                        target: CoordinatorSynchronizationPreparationNodeId),
                    completed: Edge(
                        id: "coordinator.synchronization-recurrence.completed.invalid",
                        target: CoordinatorFailNodeId),
                    exhausted: Edge(
                        id: "coordinator.synchronization-recurrence.exhausted",
                        target: CoordinatorFailNodeId),
                    stalled: Edge(
                        id: "coordinator.synchronization-recurrence.stalled",
                        target: CoordinatorFailNodeId)),
                new ReturnProcessNode(
                    CoordinatorReturnNodeId,
                    Expr.BoundValue(readyGeneration.Binding)),
                new FailProcessNode(
                    CoordinatorFailNodeId,
                    Expr.Const("materialization-rebuild-failed"))
            ],
            recoveryPolicy);

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

        ProcessRequestOutcomeBranch SynchronizationFailureBranch(
            string name,
            RequestTerminalOutcomeId outcome) => new(
            new($"coordinator.synchronize.{name}"),
            outcome,
            new(
                Edge(
                    id: $"coordinator.synchronize.{name}.failed",
                    target: CoordinatorFailNodeId),
                new(
                    new($"coordinator.synchronization.{name}"),
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
        ValueContract completedContract,
        ImmutableArray<RebuildOutcomeSemantics> outcomes)
    {
        var terminalOutcomes = ImmutableArray.CreateBuilder<RequestTerminalOutcomeDefinition>(outcomes.Length);
        foreach (var outcome in outcomes)
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
            Provenance(source: $"ari-177/{schemaAuthority}-request"));
        RequestContractReference request = new(Reference(requestDocument));
        var documents = ImmutableArray.CreateBuilder<ExecutionDefinitionDocument>(outcomes.Length + 1);
        var replies = ImmutableArray.CreateBuilder<DurableReplyBinding>(outcomes.Length);
        documents.Add(requestDocument);
        foreach (var outcome in outcomes)
        {
            var replyDocument = InteractionContractDocuments.Create(
                definitionId: new($"{definitionId.Value}/reply/{outcome.Outcome.Value}"),
                RevisionId,
                new ReplyContractDefinition(request, outcome.Outcome),
                Provenance(source: $"ari-177/{schemaAuthority}-reply/{outcome.Outcome.Value}"));
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
