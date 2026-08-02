using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using static Cohesive.Storage.Materialization.MaterializationRebuildPlanSetPortableContracts;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Storage.Materialization;

/// <summary>Canonical Process and interaction artifacts for one exact linked rebuild plan set.</summary>
public sealed class MaterializationRebuildPlanSetProcessArtifacts
{
    internal MaterializationRebuildPlanSetProcessArtifacts(
        MaterializationRebuildPlanSetReference planSet,
        MaterializationRebuildProcessArtifacts leaf,
        ImmutableArray<ExecutionDefinitionDocument> interactionDocuments,
        InteractionContractCatalog interactionCatalog,
        RequestContractReference initializationRequest,
        DurableRequestBinding initializationBinding,
        RequestContractReference leafInvocationRequest,
        DurableRequestBinding leafInvocationBinding,
        RequestContractReference readinessBarrierRequest,
        DurableRequestBinding readinessBarrierBinding,
        RequestContractReference promotionInvocationRequest,
        DurableRequestBinding promotionInvocationBinding,
        RequestContractReference activateReadyRequest,
        DurableRequestBinding activateReadyBinding,
        RequestContractReference preparePromotionRequest,
        DurableRequestBinding preparePromotionBinding,
        RequestContractReference applyPromotionRequest,
        DurableRequestBinding applyPromotionBinding,
        RequestContractReference finalizeRequest,
        DurableRequestBinding finalizeBinding,
        ExecutionDefinitionDocument promotionWorkerProcessDocument,
        CompiledProcessPlan promotionWorkerPlan,
        ExecutionDefinitionDocument parentProcessDocument,
        CompiledProcessPlan parentPlan)
    {
        PlanSet = planSet;
        Leaf = leaf;
        InteractionDocuments = interactionDocuments;
        InteractionCatalog = interactionCatalog;
        InitializationRequest = initializationRequest;
        InitializationBinding = initializationBinding;
        LeafInvocationRequest = leafInvocationRequest;
        LeafInvocationBinding = leafInvocationBinding;
        ReadinessBarrierRequest = readinessBarrierRequest;
        ReadinessBarrierBinding = readinessBarrierBinding;
        PromotionInvocationRequest = promotionInvocationRequest;
        PromotionInvocationBinding = promotionInvocationBinding;
        ActivateReadyRequest = activateReadyRequest;
        ActivateReadyBinding = activateReadyBinding;
        PreparePromotionRequest = preparePromotionRequest;
        PreparePromotionBinding = preparePromotionBinding;
        ApplyPromotionRequest = applyPromotionRequest;
        ApplyPromotionBinding = applyPromotionBinding;
        FinalizeRequest = finalizeRequest;
        FinalizeBinding = finalizeBinding;
        PromotionWorkerProcessDocument = promotionWorkerProcessDocument;
        PromotionWorkerPlan = promotionWorkerPlan;
        ParentProcessDocument = parentProcessDocument;
        ParentPlan = parentPlan;
        ProcessDocuments =
        [
            .. leaf.ProcessDocuments,
            promotionWorkerProcessDocument,
            parentProcessDocument
        ];
        DurableRequestBindings =
        [
            .. leaf.DurableRequestBindings,
            initializationBinding,
            leafInvocationBinding,
            readinessBarrierBinding,
            promotionInvocationBinding,
            activateReadyBinding,
            preparePromotionBinding,
            applyPromotionBinding,
            finalizeBinding
        ];
    }

    /// <summary>Exact content-addressed plan-set authority embedded by the parent Process.</summary>
    public MaterializationRebuildPlanSetReference PlanSet { get; }

    /// <summary>Canonical ContinueAttempt leaf build/validation artifacts.</summary>
    public MaterializationRebuildProcessArtifacts Leaf { get; }

    /// <summary>Complete interaction documents required by parent and descendant Processes.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> InteractionDocuments { get; }

    /// <summary>Validated exact interaction catalog for the complete Process graph.</summary>
    public InteractionContractCatalog InteractionCatalog { get; }

    /// <summary>Parent initialization Request.</summary>
    public RequestContractReference InitializationRequest { get; }

    /// <summary>Durable binding for parent initialization.</summary>
    public DurableRequestBinding InitializationBinding { get; }

    /// <summary>Parent-to-leaf child Process Request.</summary>
    public RequestContractReference LeafInvocationRequest { get; }

    /// <summary>Durable child binding for leaf invocation.</summary>
    public DurableRequestBinding LeafInvocationBinding { get; }

    /// <summary>Request that verifies and emits the reusable all-leaf readiness barrier.</summary>
    public RequestContractReference ReadinessBarrierRequest { get; }

    /// <summary>Durable binding for exact readiness-barrier projection.</summary>
    public DurableRequestBinding ReadinessBarrierBinding { get; }

    /// <summary>Parent-to-independent-promotion child Process Request.</summary>
    public RequestContractReference PromotionInvocationRequest { get; }

    /// <summary>Durable child binding for independent promotion invocation.</summary>
    public DurableRequestBinding PromotionInvocationBinding { get; }

    /// <summary>Request that consumes one exact ready generation and applies its retained target activation.</summary>
    public RequestContractReference ActivateReadyRequest { get; }

    /// <summary>Durable binding for target activation from exact readiness evidence.</summary>
    public DurableRequestBinding ActivateReadyBinding { get; }

    /// <summary>Read-only Request that captures an exact independent routing intent.</summary>
    public RequestContractReference PreparePromotionRequest { get; }

    /// <summary>Durable binding for independent routing-intent preparation.</summary>
    public DurableRequestBinding PreparePromotionBinding { get; }

    /// <summary>Request that applies one already-persisted independent routing intent.</summary>
    public RequestContractReference ApplyPromotionRequest { get; }

    /// <summary>Durable binding for independent routing execution and reconciliation.</summary>
    public DurableRequestBinding ApplyPromotionBinding { get; }

    /// <summary>Request that projects exact child receipts into the terminal aggregate receipt.</summary>
    public RequestContractReference FinalizeRequest { get; }

    /// <summary>Durable binding for aggregate finalization.</summary>
    public DurableRequestBinding FinalizeBinding { get; }

    /// <summary>Canonical independent-promotion worker Process document.</summary>
    public ExecutionDefinitionDocument PromotionWorkerProcessDocument { get; }

    /// <summary>Compiled independent-promotion worker plan.</summary>
    public CompiledProcessPlan PromotionWorkerPlan { get; }

    /// <summary>Canonical exact plan-set parent Process document.</summary>
    public ExecutionDefinitionDocument ParentProcessDocument { get; }

    /// <summary>Compiled exact plan-set parent Process.</summary>
    public CompiledProcessPlan ParentPlan { get; }

    /// <summary>Leaf worker, leaf coordinator, promotion worker, then parent documents.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> ProcessDocuments { get; }

    /// <summary>Complete durable Request bindings required by the Process graph.</summary>
    public ImmutableArray<DurableRequestBinding> DurableRequestBindings { get; }
}

/// <summary>Single semantic authority for the parent plan-set Process portable coordination shapes.</summary>
internal static class MaterializationRebuildPlanSetPortableContracts
{
    internal static ScalarTypeRef StringType { get; } = new(ScalarTypeKind.String);

    internal static ValueContract StringContract { get; } = new(StringType);

    internal static ObjectTypeRef WorkItemType { get; } = new(
    [
        new("sliceId", StringType),
        new("capacityDomain", StringType),
        new("payload", StringType)
    ]);

    internal static ValueContract WorkItemsContract { get; } =
        new(WorkItemType, cardinality: FieldCardinality.Many);

    internal static ValueContract BarrierResultContract { get; } = new(new ObjectTypeRef(
    [
        new("barrier", StringType),
        new("work", WorkItemType, cardinality: FieldCardinality.Many)
    ]));
}

/// <summary>Builds one exact durable parent Process for a linked independent-promotion plan set.</summary>
public static class MaterializationRebuildPlanSetProcessFactory
{
    const string ProtocolPrefix = "interaction/request/cohesive-storage-materialization-rebuild-plan-set";

    /// <summary>Exact semantic revision of the parent plan-set protocol.</summary>
    public static ExecutionRevisionId RevisionId { get; } = new("revision/1");

    /// <summary>Stable identity prefix of the exact plan-set parent Process family.</summary>
    public static ExecutionDefinitionId ParentDefinitionFamilyId { get; } =
        new("process/cohesive-storage-materialization-rebuild-plan-set");

    /// <summary>Derives the unique Process definition identity for one content-addressed plan set.</summary>
    /// <param name="planSet">Exact plan-set authority embedded by the generated parent definition.</param>
    /// <returns>An identity that cannot alias a differently fingerprinted specialized parent at this revision.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    public static ExecutionDefinitionId GetParentDefinitionId(MaterializationRebuildPlanSetReference planSet)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        return new($"{ParentDefinitionFamilyId.Value}/{MaterializationRebuildIdentities.PlanSetIdentity(planSet)}");
    }

    /// <summary>Stable identity of the independent-promotion child Process family.</summary>
    public static ExecutionDefinitionId PromotionWorkerDefinitionId { get; } =
        new("process/cohesive-storage-materialization-independent-promotion");

    /// <summary>Stable input-authority admission node.</summary>
    public static ExecutionNodeId AdmissionNodeId { get; } = new("plan-set.admit");

    /// <summary>Stable parent initialization node.</summary>
    public static ExecutionNodeId InitializationNodeId { get; } = new("plan-set.initialize");

    /// <summary>Stable bounded leaf-build node.</summary>
    public static ExecutionNodeId BuildLeavesNodeId { get; } = new("plan-set.build-leaves");

    /// <summary>Stable reusable readiness-barrier node.</summary>
    public static ExecutionNodeId ReadinessBarrierNodeId { get; } = new("plan-set.ready-barrier");

    /// <summary>Stable bounded independent-promotion node.</summary>
    public static ExecutionNodeId PromoteLeavesNodeId { get; } = new("plan-set.promote-leaves");

    /// <summary>Stable aggregate finalization node.</summary>
    public static ExecutionNodeId FinalizeNodeId { get; } = new("plan-set.finalize");

    /// <summary>Stable successful parent terminal node.</summary>
    public static ExecutionNodeId ReturnNodeId { get; } = new("plan-set.return");

    /// <summary>Stable rejected-input parent terminal node.</summary>
    public static ExecutionNodeId AdmissionFailureNodeId { get; } = new("plan-set.fail.admission");

    /// <summary>Stable promotion worker activation node.</summary>
    public static ExecutionNodeId PromotionActivateNodeId { get; } = new("promotion.activate-ready");

    /// <summary>Stable promotion worker intent-preparation node.</summary>
    public static ExecutionNodeId PromotionPrepareNodeId { get; } = new("promotion.prepare-routing");

    /// <summary>Stable promotion worker routing-application node.</summary>
    public static ExecutionNodeId PromotionApplyNodeId { get; } = new("promotion.apply-routing");

    /// <summary>Stable promotion worker successful terminal node.</summary>
    public static ExecutionNodeId PromotionReturnNodeId { get; } = new("promotion.return");

    /// <summary>Stable successful Request or child outcome.</summary>
    public static RequestTerminalOutcomeId CompletedOutcome { get; } = new("completed");

    /// <summary>Stable successful target-activation outcome consumed by the promotion worker.</summary>
    public static RequestTerminalOutcomeId ActiveOutcome { get; } =
        MaterializationRebuildProcessFactory.ActiveOutcome;

    /// <summary>Stable reusable-ready-barrier Request outcome.</summary>
    public static RequestTerminalOutcomeId ReadyOutcome { get; } = new("ready");

    /// <summary>Stable failed Request or child outcome.</summary>
    public static RequestTerminalOutcomeId FailedOutcome { get; } = new("failed");

    /// <summary>Stable cancelled Request or child outcome.</summary>
    public static RequestTerminalOutcomeId CancelledOutcome { get; } = new("cancelled");

    /// <summary>Stable terminated Request or child outcome.</summary>
    public static RequestTerminalOutcomeId TerminatedOutcome { get; } = new("terminated");

    /// <summary>Total child-terminal mapping used by both bounded parent phases.</summary>
    public static ProcessChildOutcomeMapping ChildOutcomeMapping { get; } = new(
        CompletedOutcome,
        FailedOutcome,
        CancelledOutcome,
        TerminatedOutcome);

    /// <summary>Creates, links, and compiles the exact parent Process graph for one independent plan set.</summary>
    /// <param name="planSet">Complete constructor-verified linked plan set.</param>
    /// <returns>Canonical descendant and parent artifacts with complete durable bindings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan set requests a promotion mode outside this first realization.</exception>
    /// <exception cref="InvalidOperationException">A canonical contract, link, or Process compilation is invalid.</exception>
    public static MaterializationRebuildPlanSetProcessArtifacts Create(MaterializationRebuildPlanSet planSet)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        if (planSet.Promotion.Mode != MaterializationRebuildPromotionMode.Independent)
        {
            throw new ArgumentException(
                "The initial parent Process realizes only explicitly independent promotion.",
                nameof(planSet));
        }

        var leaf = MaterializationRebuildProcessFactory.CreateChild();
        var initialization = Protocol(
            $"{ProtocolPrefix}-initialize",
            "plan-set-initialize",
            [
                Success(CompletedOutcome, WorkItemsContract),
                Failure(FailedOutcome),
                Failure(CancelledOutcome),
                Failure(TerminatedOutcome)
            ]);
        var leafInvocation = StringProtocol($"{ProtocolPrefix}-leaf", "plan-set-leaf");
        var barrier = Protocol(
            $"{ProtocolPrefix}-ready-barrier",
            "plan-set-ready-barrier",
            [
                Success(ReadyOutcome, BarrierResultContract),
                Failure(FailedOutcome),
                Failure(CancelledOutcome),
                Failure(TerminatedOutcome)
            ]);
        var promotionInvocation = StringProtocol($"{ProtocolPrefix}-promotion", "plan-set-promotion");
        var activateReady = Protocol(
            $"{ProtocolPrefix}-activate-ready",
            "plan-set-activate-ready",
            [
                Success(ActiveOutcome, StringContract),
                Failure(FailedOutcome),
                Failure(CancelledOutcome),
                Failure(TerminatedOutcome)
            ]);
        var preparePromotion = StringProtocol($"{ProtocolPrefix}-prepare-promotion", "plan-set-prepare-promotion");
        var applyPromotion = StringProtocol($"{ProtocolPrefix}-apply-promotion", "plan-set-apply-promotion");
        var finalize = StringProtocol($"{ProtocolPrefix}-finalize", "plan-set-finalize");

        ImmutableArray<ExecutionDefinitionDocument> ownInteractionDocuments =
        [
            .. initialization.Documents,
            .. leafInvocation.Documents,
            .. barrier.Documents,
            .. promotionInvocation.Documents,
            .. activateReady.Documents,
            .. preparePromotion.Documents,
            .. applyPromotion.Documents,
            .. finalize.Documents
        ];
        ImmutableArray<ExecutionDefinitionDocument> allInteractionDocuments =
        [
            .. leaf.InteractionDocuments,
            .. ownInteractionDocuments
        ];
        var catalogValidation = InteractionContractCatalog.TryCreate(allInteractionDocuments, out var interactionCatalog);
        RequireValid(catalogValidation, "plan-set interaction catalog");
        var exactCatalog = interactionCatalog
            ?? throw new InvalidOperationException("A valid plan-set interaction catalog was not produced.");

        var promotionDefinition = PromotionWorkerDefinition(
            activateReady.Request,
            preparePromotion.Request,
            applyPromotion.Request);
        var promotionDocument = ProcessDefinitionDocuments.Create(
            PromotionWorkerDefinitionId,
            RevisionId,
            promotionDefinition,
            Provenance("ari-194/materialization-independent-promotion-worker"));
        var promotionContext = new ProcessDefinitionValidationContext(interactionContracts: exactCatalog);
        var promotionCompilation = ProcessStaticCompiler.Compile(promotionDocument, promotionContext);
        RequireValid(promotionCompilation.Validation, "independent-promotion worker compilation");
        var promotionPlan = promotionCompilation.Plan
            ?? throw new InvalidOperationException("A valid independent-promotion worker plan was not produced.");

        ProcessDefinitionLink leafWorkerLink = new(
            leaf.WorkerPlan.DefinitionReference,
            ProcessDefinitionLinkKind.Process,
            leaf.WorkerPlan.Definition.Input,
            leaf.WorkerPlan.Definition.Result,
            processDependencies: [],
            recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt);
        ProcessDefinitionLink leafLink = new(
            leaf.CoordinatorPlan.DefinitionReference,
            ProcessDefinitionLinkKind.Process,
            leaf.CoordinatorPlan.Definition.Input,
            leaf.CoordinatorPlan.Definition.Result,
            processDependencies: [leaf.WorkerPlan.DefinitionReference],
            recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt);
        ProcessDefinitionLink promotionLink = new(
            promotionPlan.DefinitionReference,
            ProcessDefinitionLinkKind.Process,
            promotionPlan.Definition.Input,
            promotionPlan.Definition.Result,
            processDependencies: [],
            recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt);
        var parentDefinition = ParentDefinition(
            planSet,
            leaf.CoordinatorPlan.DefinitionReference,
            promotionPlan.DefinitionReference,
            initialization.Request,
            leafInvocation.Request,
            barrier.Request,
            promotionInvocation.Request,
            finalize.Request);
        var parentDocument = ProcessDefinitionDocuments.Create(
            GetParentDefinitionId(MaterializationRebuildPlanSetReference.FromPlanSet(planSet)),
            RevisionId,
            parentDefinition,
            Provenance(
                $"ari-194/materialization-rebuild-plan-set/{MaterializationRebuildIdentities.PlanSetIdentity(
                    MaterializationRebuildPlanSetReference.FromPlanSet(planSet))}"));
        var parentContext = new ProcessDefinitionValidationContext(
            definitions: [leafWorkerLink, leafLink, promotionLink],
            interactionContracts: exactCatalog);
        var parentCompilation = ProcessStaticCompiler.Compile(parentDocument, parentContext);
        RequireValid(parentCompilation.Validation, "rebuild plan-set parent compilation");
        var parentPlan = parentCompilation.Plan
            ?? throw new InvalidOperationException("A valid rebuild plan-set parent plan was not produced.");

        var initializationBinding = DurableBinding(
            initialization,
            parentPlan,
            InitializationNodeId,
            DurableOperationIdempotencyEvidence.NaturallyIdempotent);
        var leafInvocationBinding = DurableBinding(
            leafInvocation,
            parentPlan,
            BuildLeavesNodeId,
            DurableOperationIdempotencyEvidence.TargetDeduplication);
        var barrierBinding = DurableBinding(
            barrier,
            parentPlan,
            ReadinessBarrierNodeId,
            DurableOperationIdempotencyEvidence.NaturallyIdempotent);
        var promotionInvocationBinding = DurableBinding(
            promotionInvocation,
            parentPlan,
            PromoteLeavesNodeId,
            DurableOperationIdempotencyEvidence.TargetDeduplication);
        var activateReadyBinding = DurableBinding(
            activateReady,
            promotionPlan,
            PromotionActivateNodeId,
            DurableOperationIdempotencyEvidence.TargetDeduplication);
        var preparePromotionBinding = DurableBinding(
            preparePromotion,
            promotionPlan,
            PromotionPrepareNodeId,
            DurableOperationIdempotencyEvidence.NaturallyIdempotent);
        var applyPromotionBinding = DurableBinding(
            applyPromotion,
            promotionPlan,
            PromotionApplyNodeId,
            DurableOperationIdempotencyEvidence.TargetDeduplication);
        var finalizeBinding = DurableBinding(
            finalize,
            parentPlan,
            FinalizeNodeId,
            DurableOperationIdempotencyEvidence.NaturallyIdempotent);

        return new(
            MaterializationRebuildPlanSetReference.FromPlanSet(planSet),
            leaf,
            allInteractionDocuments,
            exactCatalog,
            initialization.Request,
            initializationBinding,
            leafInvocation.Request,
            leafInvocationBinding,
            barrier.Request,
            barrierBinding,
            promotionInvocation.Request,
            promotionInvocationBinding,
            activateReady.Request,
            activateReadyBinding,
            preparePromotion.Request,
            preparePromotionBinding,
            applyPromotion.Request,
            applyPromotionBinding,
            finalize.Request,
            finalizeBinding,
            promotionDocument,
            promotionPlan,
            parentDocument,
            parentPlan);
    }

    static CanonicalProcessDefinition ParentDefinition(
        MaterializationRebuildPlanSet planSet,
        ExecutionDefinitionReference leaf,
        ExecutionDefinitionReference promotionWorker,
        RequestContractReference initializationRequest,
        RequestContractReference leafInvocationRequest,
        RequestContractReference barrierRequest,
        RequestContractReference promotionInvocationRequest,
        RequestContractReference finalizeRequest)
    {
        var planSetJson = MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
            MaterializationRebuildPlanSetReference.FromPlanSet(planSet));
        ProcessOutputBinding buildWork = new(new("plan-set.build-work"), WorkItemsContract);
        ProcessOutputBinding buildPartition = new(new("plan-set.build-partition"), new(WorkItemType));
        ProcessOutputBinding barrierResult = new(new("plan-set.barrier-result"), BarrierResultContract);
        ProcessOutputBinding promotionPartition = new(new("plan-set.promotion-partition"), new(WorkItemType));
        ProcessOutputBinding finalReceipt = new(new("plan-set.final-receipt"), StringContract);
        var nodes = ImmutableArray.CreateBuilder<ProcessNode>();
        nodes.Add(new ChoiceProcessNode(
            AdmissionNodeId,
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            [
                new(
                    new("plan-set.admit.exact"),
                    Expr.Eq(Expr.BoundValue(ProcessBindingIds.Input), Expr.Const(planSetJson)),
                    Edge("plan-set.admit.exact.initialize", InitializationNodeId))
            ],
            new(
                new("plan-set.admit.foreign"),
                Edge("plan-set.admit.foreign.fail", AdmissionFailureNodeId))));
        nodes.Add(new RequestProcessNode(
            InitializationNodeId,
            initializationRequest,
            Expr.BoundValue(ProcessBindingIds.Input),
            [
                Branch("plan-set.initialize.completed", CompletedOutcome, BuildLeavesNodeId, buildWork),
                .. FailureBranches("plan-set.initialize", "initialization", nodes)
            ]));

        var limits = WorkLimits(planSet);
        var capacityDomains = CapacityDomains(planSet);
        var hasCapacity = !planSet.LeafPlans.IsEmpty;
        nodes.Add(new ForEachPartitionProcessNode(
            BuildLeavesNodeId,
            Expr.BoundValue(buildWork.Binding),
            buildPartition,
            Expr.Field(buildPartition.Binding, "sliceId"),
            leaf,
            leafInvocationRequest,
            ChildOutcomeMapping,
            Expr.Field(buildPartition.Binding, "payload"),
            limits,
            ProcessPartitionFailurePolicy.AwaitAll,
            hasCapacity ? Expr.Field(buildPartition.Binding, "capacityDomain") : null,
            capacityDomains,
            ProcessChildCancellationPolicy.Propagate,
            Edge("plan-set.build-leaves.settled", ReadinessBarrierNodeId),
            Edge("plan-set.build-leaves.failed-settled", ReadinessBarrierNodeId)));
        nodes.Add(new RequestProcessNode(
            ReadinessBarrierNodeId,
            barrierRequest,
            Expr.BoundValue(ProcessBindingIds.Input),
            [
                Branch("plan-set.ready-barrier.ready", ReadyOutcome, PromoteLeavesNodeId, barrierResult),
                .. FailureBranches("plan-set.ready-barrier", "barrier", nodes)
            ]));
        nodes.Add(new ForEachPartitionProcessNode(
            PromoteLeavesNodeId,
            Expr.Field(barrierResult.Binding, "work"),
            promotionPartition,
            Expr.Field(promotionPartition.Binding, "sliceId"),
            promotionWorker,
            promotionInvocationRequest,
            ChildOutcomeMapping,
            Expr.Field(promotionPartition.Binding, "payload"),
            limits,
            ProcessPartitionFailurePolicy.AwaitAll,
            hasCapacity ? Expr.Field(promotionPartition.Binding, "capacityDomain") : null,
            capacityDomains,
            ProcessChildCancellationPolicy.Propagate,
            Edge("plan-set.promote-leaves.settled", FinalizeNodeId),
            Edge("plan-set.promote-leaves.failed-settled", FinalizeNodeId)));
        nodes.Add(new RequestProcessNode(
            FinalizeNodeId,
            finalizeRequest,
            Expr.Field(barrierResult.Binding, "barrier"),
            [
                Branch("plan-set.finalize.completed", CompletedOutcome, ReturnNodeId, finalReceipt),
                .. FailureBranches("plan-set.finalize", "finalize", nodes)
            ]));
        nodes.Add(new ReturnProcessNode(ReturnNodeId, Expr.BoundValue(finalReceipt.Binding)));
        nodes.Add(new FailProcessNode(AdmissionFailureNodeId, Expr.Const("materialization-rebuild-plan-set-reference-mismatch")));

        return new(
            StringContract,
            StringContract,
            AdmissionNodeId,
            nodes.ToImmutable(),
            ProcessRecoveryPolicy.RestartAttempt);
    }

    static CanonicalProcessDefinition PromotionWorkerDefinition(
        RequestContractReference activateReadyRequest,
        RequestContractReference preparePromotionRequest,
        RequestContractReference applyPromotionRequest)
    {
        ProcessOutputBinding active = new(new("promotion.active-generation"), StringContract);
        ProcessOutputBinding request = new(new("promotion.routing-request"), StringContract);
        ProcessOutputBinding result = new(new("promotion.routing-result"), StringContract);
        var nodes = ImmutableArray.CreateBuilder<ProcessNode>();
        nodes.Add(new RequestProcessNode(
            PromotionActivateNodeId,
            activateReadyRequest,
            Expr.BoundValue(ProcessBindingIds.Input),
            [
                Branch("promotion.activate-ready.active", ActiveOutcome, PromotionPrepareNodeId, active),
                .. FailureBranches("promotion.activate-ready", "activate", nodes)
            ]));
        nodes.Add(new RequestProcessNode(
            PromotionPrepareNodeId,
            preparePromotionRequest,
            Expr.BoundValue(active.Binding),
            [
                Branch("promotion.prepare-routing.completed", CompletedOutcome, PromotionApplyNodeId, request),
                .. FailureBranches("promotion.prepare-routing", "prepare", nodes)
            ]));
        nodes.Add(new RequestProcessNode(
            PromotionApplyNodeId,
            applyPromotionRequest,
            Expr.BoundValue(request.Binding),
            [
                Branch("promotion.apply-routing.completed", CompletedOutcome, PromotionReturnNodeId, result),
                .. FailureBranches("promotion.apply-routing", "apply", nodes)
            ]));
        nodes.Add(new ReturnProcessNode(PromotionReturnNodeId, Expr.BoundValue(result.Binding)));
        return new(
            StringContract,
            StringContract,
            PromotionActivateNodeId,
            nodes.ToImmutable(),
            ProcessRecoveryPolicy.ContinueAttempt);
    }

    static ImmutableArray<ProcessRequestOutcomeBranch> FailureBranches(
        string prefix,
        string role,
        ImmutableArray<ProcessNode>.Builder nodes)
    {
        ImmutableArray<(string Name, RequestTerminalOutcomeId Outcome)> outcomes =
        [
            ("failed", FailedOutcome),
            ("cancelled", CancelledOutcome),
            ("terminated", TerminatedOutcome)
        ];
        var branches = ImmutableArray.CreateBuilder<ProcessRequestOutcomeBranch>(outcomes.Length);
        foreach (var (name, outcome) in outcomes)
        {
            var target = new ExecutionNodeId($"{prefix}.fail.{name}");
            var output = new ProcessOutputBinding(new($"{prefix}.{role}-{name}"), StringContract);
            branches.Add(Branch($"{prefix}.{name}", outcome, target, output));
            nodes.Add(new FailProcessNode(target, Expr.BoundValue(output.Binding)));
        }
        return branches.MoveToImmutable();
    }

    static ProcessRequestOutcomeBranch Branch(
        string id,
        RequestTerminalOutcomeId outcome,
        ExecutionNodeId target,
        ProcessOutputBinding output) =>
        new(new(id), outcome, new(Edge($"{id}.next", target), output));

    static ProcessWorkLimits WorkLimits(MaterializationRebuildPlanSet planSet) => new(
        maximumItems: Math.Max(1, planSet.LeafPlans.Length),
        maximumStartsPerActivation: Math.Max(1, planSet.Scheduling.MaximumStartsPerActivation),
        maximumParallelism: Math.Max(1, planSet.Scheduling.MaximumParallelism));

    static ImmutableArray<ProcessCapacityDomainLimit> CapacityDomains(MaterializationRebuildPlanSet planSet) =>
        planSet.LeafPlans.IsEmpty
            ? []
            :
            [
                .. planSet.Placement.CapacityDomains.Select(static domain =>
                    new ProcessCapacityDomainLimit(domain.Id.Value, domain.MaximumParallelism))
            ];

    static ProtocolArtifacts StringProtocol(string definitionId, string authority) =>
        Protocol(
            definitionId,
            authority,
            [
                Success(CompletedOutcome, StringContract),
                Failure(FailedOutcome),
                Failure(CancelledOutcome),
                Failure(TerminatedOutcome)
            ]);

    static ProtocolArtifacts Protocol(
        string definitionId,
        string authority,
        ImmutableArray<OutcomeContract> outcomes)
    {
        var requestId = new ExecutionDefinitionId(definitionId);
        var terminalOutcomes = ImmutableArray.CreateBuilder<RequestTerminalOutcomeDefinition>(outcomes.Length);
        foreach (var outcome in outcomes)
        {
            var schema = ValueSchema(outcome.Contract, $"{authority}/{outcome.Outcome.Value}/v1");
            terminalOutcomes.Add(outcome.Successful
                ? new RequestResultDefinition(outcome.Outcome, schema)
                : new RequestFailureDefinition(outcome.Outcome, schema));
        }
        var requestDocument = InteractionContractDocuments.Create(
            requestId,
            RevisionId,
            new RequestContractDefinition(
                ValueSchema(StringContract, $"{authority}/payload/v1"),
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
            Provenance($"ari-194/{authority}-request"));
        RequestContractReference request = new(Reference(requestDocument));
        var documents = ImmutableArray.CreateBuilder<ExecutionDefinitionDocument>(outcomes.Length + 1);
        var replies = ImmutableArray.CreateBuilder<DurableReplyBinding>(outcomes.Length);
        documents.Add(requestDocument);
        foreach (var outcome in outcomes)
        {
            var replyDocument = InteractionContractDocuments.Create(
                new($"{definitionId}/reply/{outcome.Outcome.Value}"),
                RevisionId,
                new ReplyContractDefinition(request, outcome.Outcome),
                Provenance($"ari-194/{authority}-reply/{outcome.Outcome.Value}"));
            var reply = new ReplyContractReference(Reference(replyDocument));
            documents.Add(replyDocument);
            replies.Add(new(outcome.Outcome, reply));
        }
        return new(documents.MoveToImmutable(), request, replies.MoveToImmutable());
    }

    static OutcomeContract Success(RequestTerminalOutcomeId outcome, ValueContract contract) =>
        new(outcome, Successful: true, contract);

    static OutcomeContract Failure(RequestTerminalOutcomeId outcome) =>
        new(outcome, Successful: false, StringContract);

    static DurableRequestBinding DurableBinding(
        ProtocolArtifacts protocol,
        CompiledProcessPlan plan,
        ExecutionNodeId node,
        DurableOperationIdempotencyEvidence idempotencyEvidence) =>
        new(
            protocol.Request,
            protocol.Replies,
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            timeoutAfter: null,
            idempotencyEvidence,
            reconciliationTarget: new(plan.DefinitionReference, node));

    static ProcessEdge Edge(string id, ExecutionNodeId target) => new(new(id), target);

    static InteractionValueSchema ValueSchema(ValueContract contract, string revision) => new(contract, new(revision));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance(string source) => new(
        new ExecutionProducerProvenance("cohesive-storage-materialization-rebuild-plan-set", "1"),
        new ExecutionSourceProvenance(source),
        DocumentOrigin.Generated);

    static void RequireValid(DocumentValidationResult validation, string stage)
    {
        if (validation.IsValid)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Canonical {stage} failed: {string.Join("; ", validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"))}");
    }

    readonly record struct OutcomeContract(
        RequestTerminalOutcomeId Outcome,
        bool Successful,
        ValueContract Contract);

    sealed record ProtocolArtifacts(
        ImmutableArray<ExecutionDefinitionDocument> Documents,
        RequestContractReference Request,
        ImmutableArray<DurableReplyBinding> Replies);
}
