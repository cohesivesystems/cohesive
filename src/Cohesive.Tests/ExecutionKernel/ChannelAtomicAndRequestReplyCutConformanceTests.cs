using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ChannelAtomicAndRequestReplyCutConformanceTests
{
    static readonly ChannelDirectionId Outbound = new("outbound");
    static readonly ChannelDirectionId Request = new("request");
    static readonly ChannelDirectionId Reply = new("reply");
    static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AtomicRequirement_HasDistinctNativeComposedAndUnavailableRealizations()
    {
        var definition = AtomicCheckpointSettlementChannel();
        var native = Compile("atomic/native", definition, Profile(definition, AtomicProfileMode.Native));
        var composed = Compile("atomic/composed", definition, Profile(definition, AtomicProfileMode.Composed));
        var unavailable = Compile("atomic/unavailable", definition, Profile(definition, AtomicProfileMode.Unavailable));

        Assert.True(native.Validation.IsValid, Format(native.Validation));
        Assert.True(composed.Validation.IsValid, Format(composed.Validation));
        Assert.False(unavailable.Validation.IsValid);
        Assert.Equal(CapabilityRealizationKind.Native, AtomicDecision(native).Realization);
        Assert.Equal(CapabilityRealizationKind.Composed, AtomicDecision(composed).Realization);
        Assert.Equal(CapabilityRealizationKind.Unavailable, AtomicDecision(unavailable).Realization);
        Assert.Equal(
            ["evidence/progress/outbound", "evidence/settlement/outbound"],
            AtomicDecision(composed).Auxiliaries.Select(static id => id.Value));
    }

    [Theory]
    [InlineData(CapabilityRealizationKind.Native)]
    [InlineData(CapabilityRealizationKind.Composed)]
    public void AtomicRealizations_AreAllOrNothingAcrossEveryCrashCut(CapabilityRealizationKind realization)
    {
        AtomicCutReference reference = new(realization);

        var beforeCommit = reference.Execute(crash: AtomicCrashCut.BeforeCommit);
        var afterCommit = reference.Execute(crash: AtomicCrashCut.AfterCommit);

        Assert.False(beforeCommit.CheckpointDurable);
        Assert.False(beforeCommit.ProviderSettled);
        Assert.True(afterCommit.CheckpointDurable);
        Assert.True(afterCommit.ProviderSettled);
        Assert.Equal(afterCommit.CheckpointDurable, afterCommit.ProviderSettled);
    }

    [Fact]
    public void UnavailableAtomicityCannotBeExecutedAndASequentialCutExposesTheForbiddenTornState()
    {
        Assert.Throws<InvalidOperationException>(() => new AtomicCutReference(CapabilityRealizationKind.Unavailable));

        var torn = AtomicCutReference.ExecuteWithoutAtomicBoundary(AtomicCrashCut.BetweenOperations);

        Assert.True(torn.CheckpointDurable);
        Assert.False(torn.ProviderSettled);
    }

    [Fact]
    public void RequestAdmissionAndReplyObligationCanBeRequiredAsOneAtomicBoundary()
    {
        var definition = UnaryChannel(
            additional:
            [
                new ChannelAtomicityRequirement(
                    id: new("atomic/request-admission"),
                    scope: ChannelRequirementScope.Exchange,
                    atomicScope: new("request-admission-and-reply-obligation"),
                    operations:
                    [
                        ChannelAtomicOperationKind.RequestAdmission,
                        ChannelAtomicOperationKind.ReplyObligation
                    ])
            ]);
        var native = Compile("request-admission/native", definition, Profile(definition, AtomicProfileMode.Native));
        var unavailable = Compile(
            "request-admission/unavailable",
            definition,
            Profile(definition, AtomicProfileMode.Unavailable));

        Assert.True(native.Validation.IsValid, Format(native.Validation));
        Assert.False(unavailable.Validation.IsValid);
        var atomic = Assert.Single(definition.Requirements.OfType<ChannelAtomicityRequirement>());
        Assert.True(atomic.Operations.SequenceEqual(
        [
            ChannelAtomicOperationKind.RequestAdmission,
            ChannelAtomicOperationKind.ReplyObligation
        ]));
    }

    [Fact]
    public void CoupledAndPairedRequestReplyBindingsRequireExactResponseRoutingProof()
    {
        var fixture = DurableOperationTestFixture.Create();
        var coupledDocument = Document("binding/coupled", UnaryChannel());
        var coupledReference = DurableOperationTestFixture.Reference(coupledDocument);
        ChannelRequestReplyBinding coupled = new(
            kind: ChannelRequestReplyBindingKind.CoupledExchange,
            request: fixture.RequestContract,
            reply: fixture.ResultReplyContract,
            requestDirection: new(coupledReference, Request),
            replyDirection: new(coupledReference, Reply));

        var requestDocument = Document(
            "binding/request",
            OneWayInvocationDirection(
                direction: Request,
                routing: ChannelRoutingKind.OperationEndpoint,
                isolation: ChannelRoutingIsolationKind.None));
        var safeReplyDocument = Document(
            "binding/reply/safe",
            OneWayInvocationDirection(
                direction: Reply,
                routing: ChannelRoutingKind.ExplicitResponseTarget,
                isolation: ChannelRoutingIsolationKind.DedicatedTarget));
        var unsafeReplyDocument = Document(
            "binding/reply/unsafe",
            OneWayInvocationDirection(
                direction: Reply,
                routing: ChannelRoutingKind.TopicOrFilter,
                isolation: ChannelRoutingIsolationKind.None));
        ChannelRequestReplyBinding safePaired = new(
            ChannelRequestReplyBindingKind.PairedChannels,
            fixture.RequestContract,
            fixture.ResultReplyContract,
            new(DurableOperationTestFixture.Reference(requestDocument), Request),
            new(DurableOperationTestFixture.Reference(safeReplyDocument), Reply));
        ChannelRequestReplyBinding unsafePaired = new(
            ChannelRequestReplyBindingKind.PairedChannels,
            fixture.RequestContract,
            fixture.ResultReplyContract,
            new(DurableOperationTestFixture.Reference(requestDocument), Request),
            new(DurableOperationTestFixture.Reference(unsafeReplyDocument), Reply));

        Assert.True(ChannelInteractionBindingValidator.Validate(coupled, fixture.Catalog, [coupledDocument]).IsValid);
        Assert.True(ChannelInteractionBindingValidator.Validate(
            safePaired,
            fixture.Catalog,
            [requestDocument, safeReplyDocument]).IsValid);
        var unsafeValidation = ChannelInteractionBindingValidator.Validate(
            unsafePaired,
            fixture.Catalog,
            [requestDocument, unsafeReplyDocument]);
        Assert.False(unsafeValidation.IsValid);
        Assert.Contains(
            unsafeValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelInteractionBindingDiagnosticCodes.PairedExchangeInvalid);
    }

    [Fact]
    public void RequestReply_DuplicateLateWrongTargetAndTimeoutAdmissionsAreDurableAndNonAdvancing()
    {
        var fixture = DurableOperationTestFixture.Create(
            timeoutAfter: TimeSpan.FromMinutes(2),
            lateResult: RequestResultDisposition.Observe,
            duplicateResult: RequestResultDisposition.ReusePriorDisposition);
        var acknowledged = AcknowledgeSuccess(fixture, "emission/request/admission");

        var wrongTarget = fixture.Executor.AdmitResult(
            acknowledged,
            new(
                DurableOperationTestFixture.ProcessTarget(attempt: "process-attempt/wrong"),
                DurableOperationResultArrival.Eligible));
        var admitted = fixture.Executor.AdmitResult(
            acknowledged,
            new(acknowledged.Request.ResponseTarget, DurableOperationResultArrival.Eligible));
        var duplicate = fixture.Executor.AdmitResult(
            admitted.State,
            new(acknowledged.Request.ResponseTarget, DurableOperationResultArrival.Duplicate));

        var timedOut = fixture.Executor.ResolveTimeout(
            fixture.CreateState("emission/request/timeout"),
            fixture.Timeout(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var late = fixture.Executor.AdmitResult(
            timedOut.State,
            new(timedOut.State.Request.ResponseTarget, DurableOperationResultArrival.Late));

        Assert.Equal(DurableOperationAdmissionResultKind.TargetMismatch, wrongTarget.Kind);
        Assert.Null(wrongTarget.Admission);
        Assert.Equal(DurableOperationAdmissionDisposition.Accepted, admitted.Admission?.Disposition);
        Assert.True(admitted.Admission?.AdvancesTarget);
        Assert.Equal(DurableOperationAdmissionResultKind.Duplicate, duplicate.Kind);
        Assert.Equal(admitted.Admission, duplicate.Admission);
        Assert.Same(admitted.State, duplicate.State);
        Assert.Equal(DurableOperationAdmissionDisposition.Observed, late.Admission?.Disposition);
        Assert.False(late.Admission?.AdvancesTarget);
        Assert.IsType<RequestTimeoutOutcome>(late.State.Acknowledgement?.Outcome);
    }

    [Fact]
    public void RequestReply_SettlementCannotCrossTheAcknowledgementAdmissionCheckpointCuts()
    {
        var fixture = DurableOperationTestFixture.Create();
        var acknowledged = AcknowledgeSuccess(fixture, "emission/request/settlement");
        InvocationSettlementReference settlement = new(new("request/reply/settlement"));

        Assert.Throws<InvalidOperationException>(() => settlement.Settle(acknowledged, Now));

        var admitted = fixture.Executor.AdmitResult(
            acknowledged,
            new(acknowledged.Request.ResponseTarget, DurableOperationResultArrival.Eligible));
        var receipt = settlement.Settle(admitted.State, Now.AddSeconds(1));
        var duplicateAdmission = fixture.Executor.AdmitResult(
            admitted.State,
            new(admitted.State.Request.ResponseTarget, DurableOperationResultArrival.Duplicate));
        var replayedReceipt = settlement.Settle(duplicateAdmission.State, Now.AddSeconds(2));

        Assert.Equal(ChannelSettlementKind.InvocationCoupled, receipt.Kind);
        Assert.Equal("checkpoint/emission/request/settlement", receipt.ApplicationProgress.Value);
        Assert.Equal(receipt, replayedReceipt);
        Assert.Equal(DurableOperationAdmissionResultKind.Duplicate, duplicateAdmission.Kind);
    }

    [Fact]
    public void RequestReply_CrashCutsRetainOneLogicalRequestAndClassifyAReplyOnlyAfterDurableAdmission()
    {
        var fixture = DurableOperationTestFixture.Create();
        var initial = fixture.CreateState("emission/request/crash-cuts");
        var claimed = fixture.Executor.Claim(
            initial,
            new("attempt/request/crash-cuts"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));

        var recoveredBeforeReply = new DurableOperationReferenceExecutor(fixture.Catalog).Claim(
            dispatched.State,
            new("attempt/request/recovered"),
            claimant: "worker/b",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(6));
        var recoveredClaim = Assert.IsType<DurableOperationClaim>(recoveredBeforeReply.Claim);
        var recoveredDispatch = fixture.Executor.BeginDispatch(
            recoveredBeforeReply.State,
            recoveredClaim.AttemptId,
            recoveredClaim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(6));
        var acknowledged = fixture.Executor.RecordObservation(
            recoveredDispatch.State,
            recoveredClaim.AttemptId,
            recoveredClaim.Fence,
            fixture.Success(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(7));
        var recoveredAfterReply = new DurableOperationReferenceExecutor(fixture.Catalog).Claim(
            acknowledged.State,
            new("attempt/request/forbidden"),
            claimant: "worker/c",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(8));
        var admitted = fixture.Executor.AdmitResult(
            acknowledged.State,
            new(initial.Request.ResponseTarget, DurableOperationResultArrival.Eligible));

        Assert.Equal(initial.Request, recoveredDispatch.Invocation?.Request);
        Assert.Equal(initial.OperationId, recoveredDispatch.Invocation?.Request.Context.EmissionId);
        Assert.Equal(initial.Request.Context.CorrelationId, recoveredDispatch.Invocation?.Request.Context.CorrelationId);
        Assert.Equal(DurableOperationClaimDisposition.Completed, recoveredAfterReply.Disposition);
        Assert.Null(recoveredAfterReply.Claim);
        Assert.Null(acknowledged.State.Admission);
        Assert.Equal(DurableOperationAdmissionDisposition.Accepted, admitted.State.Admission?.Disposition);
    }

    static DurableOperationState AcknowledgeSuccess(DurableOperationTestFixture fixture, string requestId)
    {
        var claimed = fixture.Executor.Claim(
            fixture.CreateState(requestId),
            new($"attempt/{requestId}"),
            claimant: "worker/admission",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddSeconds(30));
        return fixture.Executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            fixture.Success(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1)).State;
    }

    static ChannelDefinition AtomicCheckpointSettlementChannel()
    {
        var scope = ChannelRequirementScope.ForDirection(Outbound);
        return new(
            new OneWayChannelExchange(Outbound),
            [
                new ChannelTopologyRequirement(
                    id: new("topology"),
                    scope: ChannelRequirementScope.Exchange,
                    distribution: ChannelDistributionKind.CompetingConsumers,
                    interaction: ChannelInteractionShape.FireAndForget),
                new ChannelRoutingRequirement(
                    id: new("routing/outbound"),
                    scope: scope,
                    routing: ChannelRoutingKind.OperationEndpoint,
                    isolation: ChannelRoutingIsolationKind.SelectiveAcquisition),
                new ChannelFramingRequirement(
                    id: new("framing/outbound"),
                    scope: scope,
                    framing: ChannelFramingKind.TypedMessage,
                    boundaries: ChannelBoundarySemantics.Preserved),
                new ChannelPersistenceRequirement(
                    id: new("persistence/outbound"),
                    scope: scope,
                    retention: ChannelRetentionKind.DurableUntilSettled,
                    replay: ChannelReplayKind.None),
                new ChannelProgressRequirement(
                    id: new("progress/outbound"),
                    scope: scope,
                    floor: ChannelProgressFloorKind.None,
                    pending: ChannelPendingProgressKind.ExactStableDeliverySet),
                new ChannelDeliveryRequirement(
                    id: new("delivery/outbound"),
                    scope: scope,
                    guarantee: ChannelDeliveryGuaranteeKind.AtLeastOnce,
                    ordering: ChannelOrderingScopeKind.None),
                new ChannelReliabilityRequirement(
                    id: new("reliability/outbound"),
                    scope: scope,
                    reliability: ChannelReliabilityKind.Reliable),
                new ChannelSettlementRequirement(
                    id: new("settlement/outbound"),
                    scope: scope,
                    coupling: ChannelSettlementCouplingKind.PerDelivery,
                    operation: ChannelSettlementKind.Individual),
                new ChannelAtomicityRequirement(
                    id: new("atomic/checkpoint-settlement"),
                    scope: ChannelRequirementScope.Exchange,
                    atomicScope: new("checkpoint-and-settlement"),
                    operations:
                    [
                        ChannelAtomicOperationKind.ApplicationCheckpoint,
                        ChannelAtomicOperationKind.Settlement
                    ])
            ]);
    }

    static ChannelDefinition UnaryChannel(ImmutableArray<ChannelRequirement> additional = default)
    {
        List<ChannelRequirement> requirements =
        [
            new ChannelTopologyRequirement(
                id: new("topology"),
                scope: ChannelRequirementScope.Exchange,
                distribution: ChannelDistributionKind.PointToPoint,
                interaction: ChannelInteractionShape.UnaryInvocation)
        ];
        AddDirection(Request, "request", ChannelRoutingKind.OperationEndpoint, ChannelRoutingIsolationKind.InvocationScoped);
        AddDirection(Reply, "reply", ChannelRoutingKind.ConnectionOrStream, ChannelRoutingIsolationKind.InvocationScoped);
        if (!additional.IsDefaultOrEmpty)
            requirements.AddRange(additional);
        return new(new RequestReplyChannelExchange(Request, Reply), [.. requirements]);

        void AddDirection(
            ChannelDirectionId direction,
            string suffix,
            ChannelRoutingKind routing,
            ChannelRoutingIsolationKind isolation)
        {
            var scope = ChannelRequirementScope.ForDirection(direction);
            requirements.Add(new ChannelRoutingRequirement(new($"routing/{suffix}"), scope, routing, isolation));
            requirements.Add(new ChannelFramingRequirement(
                new($"framing/{suffix}"),
                scope,
                ChannelFramingKind.TypedMessage,
                ChannelBoundarySemantics.Preserved));
            requirements.Add(new ChannelPersistenceRequirement(
                new($"persistence/{suffix}"),
                scope,
                ChannelRetentionKind.ActivationLocal,
                ChannelReplayKind.None));
            requirements.Add(new ChannelDeliveryRequirement(
                new($"delivery/{suffix}"),
                scope,
                ChannelDeliveryGuaranteeKind.InvocationAttempt,
                ChannelOrderingScopeKind.Connection));
            requirements.Add(new ChannelReliabilityRequirement(
                new($"reliability/{suffix}"),
                scope,
                ChannelReliabilityKind.Reliable));
        }
    }

    static ChannelDefinition OneWayInvocationDirection(
        ChannelDirectionId direction,
        ChannelRoutingKind routing,
        ChannelRoutingIsolationKind isolation)
    {
        var scope = ChannelRequirementScope.ForDirection(direction);
        return new(
            new OneWayChannelExchange(direction),
            [
                new ChannelTopologyRequirement(
                    new("topology"),
                    ChannelRequirementScope.Exchange,
                    ChannelDistributionKind.PointToPoint,
                    ChannelInteractionShape.FireAndForget),
                new ChannelRoutingRequirement(new($"routing/{direction.Value}"), scope, routing, isolation),
                new ChannelFramingRequirement(
                    new($"framing/{direction.Value}"),
                    scope,
                    ChannelFramingKind.TypedMessage,
                    ChannelBoundarySemantics.Preserved),
                new ChannelPersistenceRequirement(
                    new($"persistence/{direction.Value}"),
                    scope,
                    ChannelRetentionKind.ActivationLocal,
                    ChannelReplayKind.None),
                new ChannelDeliveryRequirement(
                    new($"delivery/{direction.Value}"),
                    scope,
                    ChannelDeliveryGuaranteeKind.InvocationAttempt,
                    ChannelOrderingScopeKind.None),
                new ChannelReliabilityRequirement(
                    new($"reliability/{direction.Value}"),
                    scope,
                    ChannelReliabilityKind.Reliable)
            ]);
    }

    static ChannelCapabilityProfile Profile(ChannelDefinition definition, AtomicProfileMode mode)
    {
        List<ChannelCapabilityEvidence> evidence = [];
        foreach (var requirement in definition.Requirements)
        {
            if (requirement is ChannelAtomicityRequirement && mode == AtomicProfileMode.Unavailable)
                continue;

            var realization = requirement is ChannelAtomicityRequirement && mode == AtomicProfileMode.Composed
                ? CapabilityRealizationKind.Composed
                : CapabilityRealizationKind.Native;
            var auxiliaries = requirement is ChannelAtomicityRequirement && mode == AtomicProfileMode.Composed
                ? definition.Requirements.OfType<ChannelProgressRequirement>().Any()
                    ? ImmutableArray.Create(
                        new ChannelCapabilityEvidenceId("evidence/progress/outbound"),
                        new ChannelCapabilityEvidenceId("evidence/settlement/outbound"))
                    : ImmutableArray.Create(
                        new ChannelCapabilityEvidenceId("evidence/routing/request"),
                        new ChannelCapabilityEvidenceId("evidence/routing/reply"))
                : [];
            evidence.Add(new(
                id: new($"evidence/{requirement.Id.Value}"),
                capability: requirement,
                realization: realization,
                auxiliaries: auxiliaries,
                sourceReferences: [$"tests://atomic/{mode}/{requirement.Id.Value}"]));
        }

        return new(
            id: new($"profile/atomic/{mode}"),
            subject: $"tests/atomic/{mode}",
            variants: [new ChannelCapabilityVariant(new("default"), [.. evidence])],
            provenance: Provenance("profile"));
    }

    static ChannelRealizationPlan Compile(
        string id,
        ChannelDefinition definition,
        ChannelCapabilityProfile profile)
    {
        var validation = ChannelDefinitionValidator.Validate(definition);
        Assert.True(validation.IsValid, Format(validation));
        return ChannelRealizationCompiler.Compile(Document(id, definition), profile, Provenance("compiler"));
    }

    static ExecutionDefinitionDocument Document(string id, ChannelDefinition definition) =>
        ChannelDefinitionDocuments.Create(
            definitionId: new($"channel/{id}"),
            revisionId: new("1"),
            definition: definition,
            provenance: Provenance("definition"));

    static ChannelRealizationDecision AtomicDecision(ChannelRealizationPlan plan) =>
        Assert.Single(plan.Decisions, static decision => decision.Requirement.Value.StartsWith("atomic/", StringComparison.Ordinal));

    static ExecutionProvenance Provenance(string stage) => new(
        producer: new($"tests/channel-cuts-{stage}", "1"),
        source: new($"tests://execution-kernel/channel-cuts/{stage}"),
        origin: DocumentOrigin.Generated);

    static string Format(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    enum AtomicProfileMode
    {
        Native,
        Composed,
        Unavailable
    }

    enum AtomicCrashCut
    {
        BeforeCommit,
        BetweenOperations,
        AfterCommit
    }

    readonly record struct AtomicCutState(bool CheckpointDurable, bool ProviderSettled);

    sealed class AtomicCutReference
    {
        public AtomicCutReference(CapabilityRealizationKind realization)
        {
            if (realization is not (CapabilityRealizationKind.Native or CapabilityRealizationKind.Composed))
                throw new InvalidOperationException("Execution requires a resolved native or composed atomic boundary.");
            Realization = realization;
        }

        public CapabilityRealizationKind Realization { get; }

        public AtomicCutState Execute(AtomicCrashCut crash) => crash switch
        {
            AtomicCrashCut.BeforeCommit => new(false, false),
            AtomicCrashCut.AfterCommit => new(true, true),
            AtomicCrashCut.BetweenOperations => throw new InvalidOperationException(
                "An atomic realization has no observable cut between its coupled operations."),
            _ => throw new ArgumentOutOfRangeException(nameof(crash), crash, null)
        };

        public static AtomicCutState ExecuteWithoutAtomicBoundary(AtomicCrashCut crash) => crash switch
        {
            AtomicCrashCut.BeforeCommit => new(false, false),
            AtomicCrashCut.BetweenOperations => new(true, false),
            AtomicCrashCut.AfterCommit => new(true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(crash), crash, null)
        };
    }

    sealed class InvocationSettlementReference
    {
        readonly ChannelScopeId scope;
        readonly Dictionary<EmissionId, ChannelSettlementReceipt> receipts = [];

        public InvocationSettlementReference(ChannelScopeId scope) => this.scope = scope;

        public ChannelSettlementReceipt Settle(DurableOperationState operation, DateTimeOffset settledAtUtc)
        {
            if (operation.Admission is not { Disposition: DurableOperationAdmissionDisposition.Accepted } admission
                || !admission.AdvancesTarget)
            {
                throw new InvalidOperationException(
                    "Invocation settlement requires an accepted durable result admission and continuation checkpoint.");
            }
            if (receipts.TryGetValue(operation.OperationId, out var existing))
                return existing;

            ChannelSettlementReceipt receipt = new(
                kind: ChannelSettlementKind.InvocationCoupled,
                couplingKind: ChannelSettlementCouplingKind.Invocation,
                coupling: new($"invocation/{operation.OperationId.Value}"),
                applicationProgress: new(scope, $"checkpoint/{operation.OperationId.Value}"),
                settledAtUtc: settledAtUtc,
                evidenceReference: $"tests://invocation/{operation.OperationId.Value}/settled");
            receipts.Add(operation.OperationId, receipt);
            return receipt;
        }
    }
}
