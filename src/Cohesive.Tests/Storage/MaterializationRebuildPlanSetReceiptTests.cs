using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildPlanSetReceiptTests
{
    static readonly DateTimeOffset CompletedAtUtc = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MaterializationRebuildPlanSetLeafOutcome.Failed)]
    [InlineData(MaterializationRebuildPlanSetLeafOutcome.Cancelled)]
    [InlineData(MaterializationRebuildPlanSetLeafOutcome.Terminated)]
    public void TerminalLeafOutcome_RequiresExactChildTerminalEvidence(
        MaterializationRebuildPlanSetLeafOutcome outcome)
    {
        var planSet = PlanSet();
        var authority = Authority(planSet);
        var child = Child("terminal-required");
        var failure = outcome == MaterializationRebuildPlanSetLeafOutcome.Failed
            ? Error("tests.plan-set.leaf.failed")
            : null;

        var exception = Assert.Throws<ArgumentException>(() => new MaterializationRebuildPlanSetLeafReceipt(
            authority: authority,
            buildChild: child,
            outcome: outcome,
            failure: failure));

        Assert.Contains("terminal", exception.Message, StringComparison.OrdinalIgnoreCase);

        var terminalOutcome = outcome switch
        {
            MaterializationRebuildPlanSetLeafOutcome.Cancelled =>
                MaterializationRebuildPlanSetProcessFactory.CancelledOutcome,
            MaterializationRebuildPlanSetLeafOutcome.Terminated =>
                MaterializationRebuildPlanSetProcessFactory.TerminatedOutcome,
            _ => MaterializationRebuildPlanSetProcessFactory.FailedOutcome
        };
        var receipt = new MaterializationRebuildPlanSetLeafReceipt(
            authority: authority,
            buildChild: child,
            outcome: outcome,
            terminalEvidence: new(
                phase: MaterializationRebuildPlanSetLeafPhase.Build,
                child: child,
                terminalOutcome: terminalOutcome,
                terminalResult: StringValue($"{outcome} payload")),
            failure: failure);

        Assert.Equal(outcome, receipt.Outcome);
        Assert.Equal(terminalOutcome, receipt.TerminalEvidence?.TerminalOutcome);
    }

    [Fact]
    public void TerminalEvidence_RejectsAChildOutsideItsDeclaredCoordinationPhase()
    {
        var planSet = PlanSet();
        var buildChild = Child("build");
        var foreignPromotionChild = Child("foreign-promotion");
        var terminal = new MaterializationRebuildPlanSetChildTerminalEvidence(
            phase: MaterializationRebuildPlanSetLeafPhase.Promotion,
            child: foreignPromotionChild,
            terminalOutcome: MaterializationRebuildPlanSetProcessFactory.FailedOutcome,
            terminalResult: StringValue("promotion failed"));

        var exception = Assert.Throws<ArgumentException>(() => new MaterializationRebuildPlanSetLeafReceipt(
            authority: Authority(planSet),
            buildChild: buildChild,
            outcome: MaterializationRebuildPlanSetLeafOutcome.Failed,
            promotionChild: Child("promotion"),
            terminalEvidence: terminal,
            failure: Error("tests.plan-set.promotion.failed")));

        Assert.Contains("exact linked child", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateReceiptJson_RoundTripsExactChildTerminalOutcomeAndPortableResult()
    {
        var planSet = PlanSet();
        var authority = Authority(planSet);
        var buildChild = Child("round-trip");
        var terminalResult = StringValue("adapter-specific failure payload");
        var leaf = new MaterializationRebuildPlanSetLeafReceipt(
            authority: authority,
            buildChild: buildChild,
            outcome: MaterializationRebuildPlanSetLeafOutcome.Failed,
            terminalEvidence: new(
                phase: MaterializationRebuildPlanSetLeafPhase.Build,
                child: buildChild,
                terminalOutcome: new("source-failed"),
                terminalResult: terminalResult),
            failure: Error("tests.plan-set.leaf.not-ready"));
        var receipt = MaterializationRebuildPlanSetReceipt.Create(
            planSet: planSet,
            parentContinuation: Child("parent"),
            outcome: MaterializationRebuildPlanSetOutcome.Failed,
            leaves: [leaf],
            readyBarrier: null,
            completedAtUtc: CompletedAtUtc);

        var json = MaterializationRebuildPlanSetReceiptJsonSerializer.Serialize(receipt);
        var restored = MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(json);
        var restoredTerminal = Assert.Single(restored.Leaves).TerminalEvidence;

        Assert.Equal(receipt, restored);
        Assert.NotNull(restoredTerminal);
        Assert.Equal(MaterializationRebuildPlanSetLeafPhase.Build, restoredTerminal.Phase);
        Assert.Equal(new RequestTerminalOutcomeId("source-failed"), restoredTerminal.TerminalOutcome);
        Assert.Equal(terminalResult, restoredTerminal.TerminalResult);
        Assert.Equal(
            "adapter-specific failure payload",
            Assert.IsType<ObservationValue>(restoredTerminal.TerminalResult.Value).String);
    }

    [Theory]
    [InlineData("targetRevision")]
    [InlineData("promotion")]
    [InlineData("promotionFence")]
    [InlineData("validation")]
    [InlineData("activatedAtUtc")]
    public void PromotedLeaf_RejectsEveryActiveGenerationCoordinateThatDiffersFromReadyIntent(
        string changedCoordinate)
    {
        var (planSet, leafPlan) = Scenario();
        var buildChild = Child($"active-coordinate/{changedCoordinate}/build");
        var promotionChild = Child($"active-coordinate/{changedCoordinate}/promotion");
        var ready = CreateReady(planSet, leafPlan, buildChild, changedCoordinate);
        var exactActive = ActiveFromReady(ready);
        var changedActive = changedCoordinate switch
        {
            "targetRevision" => new MaterializationActiveGenerationReference(
                exactActive.SchemaVersion,
                exactActive.Authority,
                exactActive.Generation,
                new((exactActive.TargetRevision.Ordinal + 1).ToString(CultureInfo.InvariantCulture)),
                exactActive.Promotion,
                exactActive.PromotionFence,
                exactActive.Validation,
                exactActive.ActivatedAtUtc),
            "promotion" => new MaterializationActiveGenerationReference(
                exactActive.SchemaVersion,
                exactActive.Authority,
                exactActive.Generation,
                exactActive.TargetRevision,
                new(exactActive.Promotion.Value + "/changed"),
                exactActive.PromotionFence,
                exactActive.Validation,
                exactActive.ActivatedAtUtc),
            "promotionFence" => new MaterializationActiveGenerationReference(
                exactActive.SchemaVersion,
                exactActive.Authority,
                exactActive.Generation,
                exactActive.TargetRevision,
                exactActive.Promotion,
                new((exactActive.PromotionFence.Ordinal + 1).ToString(CultureInfo.InvariantCulture)),
                exactActive.Validation,
                exactActive.ActivatedAtUtc),
            "validation" => new MaterializationActiveGenerationReference(
                exactActive.SchemaVersion,
                exactActive.Authority,
                exactActive.Generation,
                exactActive.TargetRevision,
                exactActive.Promotion,
                exactActive.PromotionFence,
                new(exactActive.Validation.Value + "/changed"),
                exactActive.ActivatedAtUtc),
            "activatedAtUtc" => new MaterializationActiveGenerationReference(
                exactActive.SchemaVersion,
                exactActive.Authority,
                exactActive.Generation,
                exactActive.TargetRevision,
                exactActive.Promotion,
                exactActive.PromotionFence,
                exactActive.Validation,
                exactActive.ActivatedAtUtc.AddTicks(1)),
            _ => throw new InvalidOperationException($"Unsupported active-generation coordinate '{changedCoordinate}'.")
        };
        var promotion = SelectedPromotion(planSet, changedActive, changedCoordinate);

        Assert.True(ready.MatchesActiveGeneration(exactActive));
        Assert.False(ready.MatchesActiveGeneration(changedActive));
        Assert.Throws<ArgumentException>(() => new MaterializationRebuildPlanSetLeafReceipt(
            authority: ready.Authority,
            buildChild: buildChild,
            outcome: MaterializationRebuildPlanSetLeafOutcome.Promoted,
            ready: ready,
            promotionChild: promotionChild,
            promotion: promotion));
    }

    [Fact]
    public void AggregateReceipt_RejectsOnePromotionChildCreditedToMultiplePlacementSlices()
    {
        var firstLeaf = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var scenario = MaterializationRebuildPlanSetTests.CreateIndependentTwoLeafScenario(firstLeaf);
        var sharedPromotionChild = Child("duplicate-promotion-child");
        var leaves = ImmutableArray.CreateBuilder<MaterializationRebuildPlanSetLeafReceipt>(scenario.Leaves.Length);
        var ready = ImmutableArray.CreateBuilder<MaterializationReadyGenerationReference>(scenario.Leaves.Length);
        for (var index = 0; index < scenario.Leaves.Length; index++)
        {
            var buildChild = Child($"duplicate-promotion-child/build/{index}");
            var readyLeaf = CreateReady(
                scenario.PlanSet,
                scenario.Leaves[index],
                buildChild,
                $"duplicate-promotion-child/{index}");
            ready.Add(readyLeaf);
            leaves.Add(new(
                authority: readyLeaf.Authority,
                buildChild: buildChild,
                outcome: MaterializationRebuildPlanSetLeafOutcome.Promoted,
                ready: readyLeaf,
                promotionChild: sharedPromotionChild,
                promotion: SelectedPromotion(scenario.PlanSet, ActiveFromReady(readyLeaf), $"duplicate/{index}")));
        }
        var parent = Child("duplicate-promotion-child/parent");
        var barrier = MaterializationRebuildReadyBarrier.Create(scenario.PlanSet, parent, ready.MoveToImmutable());

        Assert.Throws<ArgumentException>(() => MaterializationRebuildPlanSetReceipt.Create(
            planSet: scenario.PlanSet,
            parentContinuation: parent,
            outcome: MaterializationRebuildPlanSetOutcome.Completed,
            leaves: leaves.MoveToImmutable(),
            readyBarrier: barrier,
            completedAtUtc: CompletedAtUtc));
    }

    [Fact]
    public void FailedLeaf_RejectsPromotionEvidenceThatIsCurrentlySelected()
    {
        var (planSet, leafPlan) = Scenario();
        var buildChild = Child("failed-selected/build");
        var promotionChild = Child("failed-selected/promotion");
        var ready = CreateReady(planSet, leafPlan, buildChild, "failed-selected");
        var promotion = SelectedPromotion(planSet, ActiveFromReady(ready), "failed-selected");

        Assert.True(promotion.IsCurrentlySelected);
        Assert.Throws<ArgumentException>(() => new MaterializationRebuildPlanSetLeafReceipt(
            authority: ready.Authority,
            buildChild: buildChild,
            outcome: MaterializationRebuildPlanSetLeafOutcome.Failed,
            ready: ready,
            promotionChild: promotionChild,
            promotion: promotion,
            terminalEvidence: new(
                phase: MaterializationRebuildPlanSetLeafPhase.Promotion,
                child: promotionChild,
                terminalOutcome: MaterializationRebuildPlanSetProcessFactory.FailedOutcome,
                terminalResult: StringValue("contradictory selected result")),
            failure: Error("tests.plan-set.promotion.selected-but-failed")));
    }

    [Fact]
    public void AggregateReceipt_ContextualCreateRejectsStructurallyValidDeserializedLeafSubset()
    {
        var firstLeaf = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var scenario = MaterializationRebuildPlanSetTests.CreateIndependentTwoLeafScenario(firstLeaf);
        var authority = MaterializationRebuildLeafExecutionAuthority.FromPlanSet(
            scenario.PlanSet,
            scenario.Leaves[0]);
        var buildChild = Child("structural-subset/build");
        var failed = new MaterializationRebuildPlanSetLeafReceipt(
            authority: authority,
            buildChild: buildChild,
            outcome: MaterializationRebuildPlanSetLeafOutcome.Failed,
            terminalEvidence: new(
                phase: MaterializationRebuildPlanSetLeafPhase.Build,
                child: buildChild,
                terminalOutcome: MaterializationRebuildPlanSetProcessFactory.FailedOutcome,
                terminalResult: StringValue("one of two leaves only")),
            failure: Error("tests.plan-set.leaf.subset"));
        var structural = new MaterializationRebuildPlanSetReceipt(
            MaterializationRebuildPlanSetReceipt.CurrentSchemaVersion,
            MaterializationRebuildPlanSetReference.FromPlanSet(scenario.PlanSet),
            Child("structural-subset/parent"),
            MaterializationRebuildPlanSetOutcome.Failed,
            [failed],
            readyBarrier: null,
            completedAtUtc: CompletedAtUtc);

        var restored = MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(
            MaterializationRebuildPlanSetReceiptJsonSerializer.Serialize(structural));

        Assert.Single(restored.Leaves);
        Assert.Throws<ArgumentException>(() => MaterializationRebuildPlanSetReceipt.Create(
            scenario.PlanSet,
            restored.ParentContinuation,
            restored.Outcome,
            restored.Leaves,
            restored.ReadyBarrier,
            restored.CompletedAtUtc));
    }

    [Fact]
    public void BuildProjection_PreservesExactFailedChildPayloadAlongsideNormalizedDiagnostic()
    {
        var planSet = PlanSet();
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var continuation = Child("projection-parent");
        var start = Start(artifacts, planSet, continuation);
        var initial = ProcessReferenceInterpreter.Create(artifacts.ParentPlan, start);
        var authority = Authority(planSet);
        var sliceId = authority.PlacementSlice.Id.Value;
        var capacityDomain = planSet.Placement.CapacityBindings.Single().CapacityDomain.Value;
        var buildNode = Assert.IsType<ForEachPartitionProcessNode>(
            artifacts.ParentPlan.GetNode(MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId));
        const string registrationId = "child/build/failure-payload";
        var childContinuation = Child("projection-build-child");
        var childResult = StringValue("cosmos change feed lease was lost");
        var child = NewChild(
            registrationId,
            initial.Tokens.Single().Id,
            new("token/build/failure-payload"),
            MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId,
            occurrence: 0,
            progressIdentity: sliceId,
            artifacts.Leaf.CoordinatorPlan.DefinitionReference,
            childContinuation,
            ProcessChildPurpose.Work,
            ProcessChildCancellationPolicy.Propagate,
            ProcessChildDisposition.Failed,
            new("emission/build/failure-payload"),
            MaterializationRebuildPlanSetProcessFactory.FailedOutcome,
            childResult);
        var work = new ProcessPartitionWorkState(
            ProgressIdentity: PlanSetProjection.ProgressIdentity(0, authority),
            CapacityIdentity: capacityDomain,
            Partition: PortableValue.Concrete(
                buildNode.Partition.Contract,
                ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["progressId"] = ObservationValue.FromString(
                        PlanSetProjection.ProgressIdentity(0, authority)),
                    ["sliceId"] = ObservationValue.FromString(sliceId),
                    ["capacityDomain"] = ObservationValue.FromString(capacityDomain),
                    ["payload"] = ObservationValue.FromString(
                        MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(authority))
                })),
            ChildRegistrationId: registrationId);
        var partition = NewPartition(
            "partition/build/failure-payload",
            initial.Tokens.Single().Id,
            MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId,
            occurrence: 0,
            work: [work],
            resolved: true);
        var projected = NewContinuation(
            initial.Definition,
            initial.Continuation,
            initial.CompletedActivationCount,
            initial.Tokens,
            initial.Forks,
            [child],
            [partition],
            initial.Recurrences,
            initial.Waits,
            initial.BufferedInputs,
            initial.InputReceipts,
            initial.OutstandingRequests,
            initial.Terminal);
        var checkpoint = new ProcessDurableCheckpoint(
            ProcessDurableCheckpoint.CurrentSchemaVersion,
            start,
            projected,
            start.CreateInitialState(),
            createdAtUtc: CompletedAtUtc,
            updatedAtUtc: CompletedAtUtc);

        var receipt = Assert.Single(PlanSetProjection.ProjectBuildLeaves(planSet, checkpoint, out var allReady));

        Assert.False(allReady);
        Assert.Equal(MaterializationRebuildPlanSetLeafOutcome.Failed, receipt.Outcome);
        Assert.Equal(
            MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.LeafNotReady,
            receipt.Failure?.Code);
        Assert.NotNull(receipt.TerminalEvidence);
        Assert.Equal(MaterializationRebuildPlanSetLeafPhase.Build, receipt.TerminalEvidence.Phase);
        Assert.Equal(childContinuation, receipt.TerminalEvidence.Child);
        Assert.Equal(MaterializationRebuildPlanSetProcessFactory.FailedOutcome, receipt.TerminalEvidence.TerminalOutcome);
        Assert.Equal(childResult, receipt.TerminalEvidence.TerminalResult);
        Assert.Equal(
            "cosmos change feed lease was lost",
            Assert.IsType<ObservationValue>(receipt.TerminalEvidence.TerminalResult.Value).String);
    }

    static MaterializationRebuildPlanSet PlanSet() => Scenario().PlanSet;

    static (MaterializationRebuildPlanSet PlanSet, MaterializationRebuildPlan LeafPlan) Scenario()
    {
        var leafPlan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        return (MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(leafPlan), leafPlan);
    }

    static MaterializationRebuildLeafExecutionAuthority Authority(MaterializationRebuildPlanSet planSet) =>
        MaterializationRebuildLeafExecutionAuthority.FromPlanSet(planSet, planSet.LeafPlans.Single());

    internal static ProcessContinuationIdentity Child(string suffix) => new(
        new($"process/plan-set-receipt-tests/{suffix}"),
        new($"attempt/plan-set-receipt-tests/{suffix}/1"));

    static PortableValue StringValue(string value) => PortableValue.Concrete(
        new(new ScalarTypeRef(ScalarTypeKind.String)),
        ObservationValue.FromString(value));

    static DocumentValidationDiagnostic Error(string code) => new(
        code,
        DiagnosticSeverity.Error,
        "The exact child did not satisfy its parent phase contract.",
        "/leaves/test");

    internal static ProcessStartReceipt Start(
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        MaterializationRebuildPlanSet planSet,
        ProcessContinuationIdentity continuation)
    {
        var request = new ProcessStartRequest(
            ProcessStartRequest.CurrentSchemaVersion,
            artifacts.ParentPlan.DefinitionReference,
            new(
                commandId: new("command/plan-set-receipt-tests/start"),
                idempotencyKey: new("idempotency/plan-set-receipt-tests/start"),
                processInstanceId: continuation.ProcessInstanceId,
                authorization: new(
                    actor: "operator/plan-set-receipt-tests",
                    authorityScope: new("authority/plan-set-receipt-tests", "tenant/tests"),
                    evidenceReference: "policy/plan-set-receipt-tests/allow"),
                issuedAtUtc: CompletedAtUtc,
                provenance: Provenance()),
            continuation,
            PortableValue.Concrete(
                artifacts.ParentPlan.Definition.Input,
                ObservationValue.FromString(MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
                    MaterializationRebuildPlanSetReference.FromPlanSet(planSet)))));
        return new(request, CompletedAtUtc);
    }

    static ExecutionProvenance Provenance() => new(
        new ExecutionProducerProvenance("cohesive-tests", "1"),
        new ExecutionSourceProvenance("tests:materialization-rebuild-plan-set-receipt"),
        DocumentOrigin.Generated);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    internal static extern ProcessContinuationState NewContinuation(
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
    internal static extern ProcessChildState NewChild(
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
    internal static extern ProcessPartitionState NewPartition(
        string registrationId,
        TokenId owner,
        ExecutionNodeId node,
        long occurrence,
        ImmutableArray<ProcessPartitionWorkState> work,
        bool resolved);

    internal static MaterializationReadyGenerationReference CreateReady(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlan leafPlan,
        ProcessContinuationIdentity buildChild,
        string suffix)
    {
        var authority = MaterializationRebuildLeafExecutionAuthority.FromPlanSet(planSet, leafPlan);
        var startedAtUtc = CompletedAtUtc.AddMinutes(-1);
        MaterializationRebuildAttempt attempt = new(buildChild, startedAtUtc);
        var generation = MaterializationRebuildIdentities.Generation(authority, attempt);
        var feeds = ImmutableArray.CreateBuilder<MaterializationCatchUpFeedEvidence>(leafPlan.ChangeFeeds.Length);
        for (var index = 0; index < leafPlan.ChangeFeeds.Length; index++)
        {
            var feed = leafPlan.ChangeFeeds[index];
            MaterializationSourcePosition position = new(
                formatVersion: 1,
                feed.Scope,
                value: $"position/{suffix}/{index}");
            feeds.Add(new(
                feed: feed.Id,
                scope: feed.Scope,
                latestChangeCheckpoint: new($"checkpoint/{suffix}/{index}"),
                throughPosition: position,
                caughtUpReadStartedAtUtc: startedAtUtc,
                caughtUpReadCompletedAtUtc: startedAtUtc.AddSeconds(1),
                checkpointCommittedAtUtc: startedAtUtc.AddSeconds(2),
                settlementRequirement: MaterializationConvergenceSettlementRequirement.NotRequired));
        }
        MaterializationSynchronizationWorkKey synchronization = new(
            materialization: leafPlan.Materialization.Definition.Id,
            definitionFingerprint: leafPlan.Materialization.DefinitionFingerprint,
            rebuildPlanFingerprint: leafPlan.Fingerprint,
            impactPlanFingerprint: leafPlan.ImpactPlan.Fingerprint,
            generation: generation);
        MaterializationConvergenceReceipt convergence = new(
            schemaVersion: MaterializationConvergenceReceipt.CurrentSchemaVersion,
            synchronization,
            feeds: feeds.MoveToImmutable(),
            evaluatedAtUtc: startedAtUtc.AddSeconds(3),
            freshnessDemand: leafPlan.Materialization.Definition.FreshnessPolicy,
            validation: DocumentValidationResult.Valid);
        MaterializationSealGenerationRequest sealRequest = new(
            sealId: new($"seal/{suffix}"),
            generationId: generation,
            expectedRevision: new("10"),
            workerFence: new("1"),
            sealedAtUtc: startedAtUtc.AddSeconds(4));
        MaterializationSealReceipt sealReceipt = new(
            sealId: sealRequest.SealId,
            generationId: generation,
            generationRevision: new("11"),
            visibleItemCount: 7,
            fingerprint: new($"seal-fingerprint/{suffix}"),
            sealedAtUtc: sealRequest.SealedAtUtc);
        MaterializationValidateGenerationRequest validationRequest = new(
            validationId: new($"validation/{suffix}"),
            generationId: generation,
            expectedRevision: sealReceipt.GenerationRevision,
            expectedSealFingerprint: sealReceipt.Fingerprint,
            expectedVisibleItemCount: sealReceipt.VisibleItemCount,
            validator: "tests/plan-set-receipt/activation-validator/v1",
            workerFence: sealRequest.WorkerFence,
            validatedAtUtc: startedAtUtc.AddSeconds(5));
        MaterializationValidationReceipt validationReceipt = new(
            validationId: validationRequest.ValidationId,
            generationId: generation,
            generationRevision: new("12"),
            sealFingerprint: sealReceipt.Fingerprint,
            fingerprint: new($"validation-fingerprint/{suffix}"),
            validation: DocumentValidationResult.Valid,
            validatedAtUtc: validationRequest.ValidatedAtUtc);
        MaterializationPromoteGenerationRequest promotionRequest = new(
            promotionId: new($"promotion/{suffix}"),
            generationId: generation,
            expectedGenerationRevision: validationReceipt.GenerationRevision,
            validationFingerprint: validationReceipt.Fingerprint,
            expectedActiveGenerationId: null,
            expectedTargetRevision: MaterializationTargetRevision.Initial,
            generationWorkerFence: sealRequest.WorkerFence,
            promotionFence: MaterializationPromotionFence.Initial,
            promotedAtUtc: startedAtUtc.AddSeconds(6));
        return new(
            MaterializationReadyGenerationReference.CurrentSchemaVersion,
            authority,
            attempt,
            generation,
            new(
                convergence,
                sealRequest,
                sealReceipt,
                validationRequest,
                validationReceipt,
                promotionRequest));
    }

    static MaterializationActiveGenerationReference ActiveFromReady(
        MaterializationReadyGenerationReference ready)
    {
        var intent = ready.PromotionIntent;
        return new(
            MaterializationActiveGenerationReference.CurrentSchemaVersion,
            ready.Authority,
            ready.Generation,
            new((intent.ExpectedTargetRevision.Ordinal + 1).ToString(CultureInfo.InvariantCulture)),
            intent.PromotionId,
            intent.PromotionFence,
            intent.ValidationFingerprint,
            intent.PromotedAtUtc);
    }

    static MaterializationIndependentPromotionResult SelectedPromotion(
        MaterializationRebuildPlanSet planSet,
        MaterializationActiveGenerationReference active,
        string suffix)
    {
        var authority = active.Authority;
        var configuration = MaterializationBackendRoutingConfigurationResolver.Resolve(
            planSet.Placement.BackendPool.Definition,
            new MaterializationBackendRoutingConfigurationLayer(
                EffectiveConfigurationOrigin.Explicit,
                MaterializationIndependentPromotionExecutor.ConfigurationAuthority(authority.PlanSet),
                new(readTarget: authority.PlacementSlice.Target, writeTarget: authority.PlacementSlice.Target)));
        MaterializationIndependentPromotionRequest request = new(
            MaterializationIndependentPromotionRequest.CurrentSchemaVersion,
            active,
            configuration,
            MaterializationBackendRoutingRevision.Initial,
            MaterializationBackendRoutingFence.Initial,
            new($"routing/{suffix}/admit"),
            new($"routing/{suffix}/swap"),
            active.ActivatedAtUtc.AddSeconds(1),
            active.ActivatedAtUtc.AddSeconds(2));
        MaterializationBackendGenerationReference generation = new(
            authority.PlacementSlice.Target,
            active.Generation,
            authority.PlacementSlice.Materialization.DefinitionFingerprint);
        MaterializationBackendRoutingReceipt admissionReceipt = new(
            request.AdmitCommandId,
            authority.PlacementSlice,
            MaterializationBackendRoutingOperation.AdmitCandidate,
            new("1"),
            request.Fence,
            request.AdmitIssuedAtUtc);
        MaterializationBackendRoutingResult admission = new(
            MaterializationBackendRoutingDisposition.Applied,
            new(
                authority.PlacementSlice,
                admissionReceipt.Revision,
                request.Fence,
                activeRead: null,
                activeWrite: null,
                candidate: generation,
                draining: [],
                retired: [],
                cleaned: []),
            admissionReceipt);
        MaterializationBackendRoutingReceipt routingReceipt = new(
            request.SwapCommandId,
            authority.PlacementSlice,
            MaterializationBackendRoutingOperation.Swap,
            new("2"),
            request.Fence,
            request.SwapIssuedAtUtc);
        MaterializationBackendRoutingResult routing = new(
            MaterializationBackendRoutingDisposition.Applied,
            new(
                authority.PlacementSlice,
                routingReceipt.Revision,
                request.Fence,
                new(authority.PlacementSlice, generation, active),
                generation,
                candidate: null,
                draining: [],
                retired: [],
                cleaned: [],
                configuration),
            routingReceipt);
        return new(
            MaterializationIndependentPromotionResult.CurrentSchemaVersion,
            request,
            admission,
            routing);
    }
}
