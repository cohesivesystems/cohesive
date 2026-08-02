using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationAtomicRoutingManifestTests
{
    [Fact]
    public void Compile_RequiresOneExactNativeAuthorityForBothRoutesAndCompleteScope()
    {
        var (planSet, _) = AtomicScenario();
        var requirement = MaterializationAtomicRoutingManifestRequirement.FromPlanSet(planSet, "routing-authority/a");
        var successful = MaterializationAtomicRoutingManifestCompiler.Compile(
            planSet,
            requirement,
            Capability(requirement));

        Assert.True(successful.IsSuccessful);
        Assert.NotNull(successful.Artifact);

        var cases = new[]
        {
            (
                Capability(requirement, authority: "routing-authority/b"),
                MaterializationAtomicRoutingManifestDiagnosticCodes.AuthorityMismatch),
            (
                Capability(requirement, scope: [requirement.Scope[0]]),
                MaterializationAtomicRoutingManifestDiagnosticCodes.ScopeMismatch),
            (
                Capability(requirement, settings: [MaterializationBackendRoutingSettingNames.ReadTarget]),
                MaterializationAtomicRoutingManifestDiagnosticCodes.RoutingSettingsIncomplete),
            (
                Capability(requirement, settings: [MaterializationBackendRoutingSettingNames.WriteTarget]),
                MaterializationAtomicRoutingManifestDiagnosticCodes.RoutingSettingsIncomplete),
            (
                Capability(
                    requirement,
                    guarantees:
                    [
                        MaterializationGuaranteeKind.Reconciliation,
                        MaterializationGuaranteeKind.IdempotentWrite,
                        MaterializationGuaranteeKind.AtomicPromotion
                    ]),
                MaterializationAtomicRoutingManifestDiagnosticCodes.GuaranteeUnavailable),
            (
                Capability(requirement, realization: CapabilityRealizationKind.Composed),
                MaterializationAtomicRoutingManifestDiagnosticCodes.RealizationWeaker)
        };
        foreach (var (capability, code) in cases)
        {
            var rejected = MaterializationAtomicRoutingManifestCompiler.Compile(planSet, requirement, capability);
            Assert.False(rejected.IsSuccessful);
            Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == code);
        }
    }

    [Fact]
    public void ProcessFactory_UsesOneAtomicParentRequestAndNeverClaimsPerTargetSwaps()
    {
        var (planSet, _) = AtomicScenario();
        var realization = Realization(planSet);

        Assert.Throws<ArgumentException>(() => MaterializationRebuildPlanSetProcessFactory.Create(planSet));
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet, realization);

        Assert.Equal(realization, artifacts.AtomicRoutingManifestRealization);
        Assert.NotNull(artifacts.PrepareAtomicRoutingManifestBinding);
        Assert.NotNull(artifacts.ApplyAtomicRoutingManifestBinding);
        Assert.DoesNotContain(
            artifacts.ParentPlan.Definition.Nodes,
            node => node.Id == MaterializationRebuildPlanSetProcessFactory.PromoteLeavesNodeId);
        Assert.DoesNotContain(
            artifacts.ProcessDocuments,
            document => document.Metadata.DefinitionId == artifacts.PromotionWorkerProcessDocument.Metadata.DefinitionId
                || document.Metadata.DefinitionId == artifacts.CompensationWorkerProcessDocument.Metadata.DefinitionId);
        Assert.DoesNotContain(
            artifacts.DurableRequestBindings,
            binding => binding.Request == artifacts.ApplyPromotionRequest
                || binding.Request == artifacts.ApplyCompensationRequest);
        var barrier = Assert.IsType<RequestProcessNode>(artifacts.ParentPlan.GetNode(
            MaterializationRebuildPlanSetProcessFactory.ReadinessBarrierNodeId));
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.PrepareAtomicRoutingManifestNodeId,
            barrier.Outcomes.Single(outcome =>
                outcome.Outcome == MaterializationRebuildPlanSetProcessFactory.ReadyOutcome).Continuation.Edge.Target);
        Assert.IsType<RequestProcessNode>(artifacts.ParentPlan.GetNode(
            MaterializationRebuildPlanSetProcessFactory.ApplyAtomicRoutingManifestNodeId));
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.GetParentDefinitionId(artifacts.PlanSet, realization),
            artifacts.ParentPlan.DefinitionReference.DefinitionId);
    }

    [Fact]
    public async Task InMemoryAuthority_CommitsCompleteManifestOnceAndReconcilesExactReplay()
    {
        var (planSet, leaves) = AtomicScenario();
        var realization = Realization(planSet);
        using var authority = new InMemoryMaterializationAtomicRoutingManifestAuthority(realization);
        var context = OperationContext.Create();
        var prior = await authority.InspectAsync(context, realization.Requirement.PlanSet);
        var barrier = ReadyBarrier(planSet, leaves);
        var issuedAtUtc = barrier.ReadyGenerations.Max(static ready => ready.ReadyAtUtc).AddTicks(1);
        var executor = new MaterializationAtomicRoutingManifestExecutor(planSet, realization);
        var request = executor.CreateRequest(
            barrier: barrier,
            prior: prior,
            fence: MaterializationBackendRoutingFence.Initial,
            issuedAtUtc: issuedAtUtc);

        var applied = await executor.ExecuteAsync(context, request, authority);
        var replayed = await executor.ExecuteAsync(context, request, authority);

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, applied.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, replayed.Disposition);
        Assert.True(applied.IsApplied);
        Assert.True(replayed.IsApplied);
        Assert.Equal(1, applied.Snapshot.Revision.Ordinal);
        Assert.Equal(planSet.LeafPlans.Length, applied.Snapshot.Entries.Length);
        Assert.All(applied.Snapshot.Entries, static entry =>
        {
            Assert.True(entry.IsInitialized);
            Assert.Equal(entry.Read, entry.Write);
        });
        Assert.Equal(applied.Receipt, replayed.Receipt);
        Assert.Equal(
            MaterializationAtomicRoutingManifestJsonSerializer.SerializeRequest(request),
            MaterializationAtomicRoutingManifestJsonSerializer.SerializeRequest(
                MaterializationAtomicRoutingManifestJsonSerializer.DeserializeRequest(
                    MaterializationAtomicRoutingManifestJsonSerializer.SerializeRequest(request))));

        var conflicting = new MaterializationAtomicRoutingManifestRequest(
            schemaVersion: request.SchemaVersion,
            realization: request.Realization,
            prior: request.Prior,
            desiredEntries: request.DesiredEntries,
            fence: new("2"),
            commandId: request.CommandId,
            issuedAtUtc: request.IssuedAtUtc);
        var conflict = await authority.CompareExchangeAsync(context, conflicting);
        Assert.Equal(MaterializationBackendRoutingDisposition.IdentityConflict, conflict.Disposition);
        Assert.Equal(1, conflict.Snapshot.Revision.Ordinal);
        Assert.Equal(
            request.DesiredEntries.Select(static entry =>
                (entry.PlacementSlice.Fingerprint, entry.Read, entry.Write)),
            conflict.Snapshot.Entries.Select(static entry =>
                (entry.PlacementSlice.Fingerprint, entry.Read, entry.Write)));
    }

    [Fact]
    public async Task InMemoryAuthority_PrecommitRejectionLeavesEveryReadAndWriteRouteUnchanged()
    {
        var (planSet, leaves) = AtomicScenario();
        var realization = Realization(planSet);
        using var authority = new InMemoryMaterializationAtomicRoutingManifestAuthority(realization);
        var context = OperationContext.Create();
        var prior = await authority.InspectAsync(context, realization.Requirement.PlanSet);
        var barrier = ReadyBarrier(planSet, leaves);
        var executor = new MaterializationAtomicRoutingManifestExecutor(planSet, realization);
        var newerFence = executor.CreateRequest(
            barrier: barrier,
            prior: prior,
            fence: new("2"),
            issuedAtUtc: barrier.ReadyGenerations.Max(static ready => ready.ReadyAtUtc).AddTicks(1));
        var stalePrior = new MaterializationRoutingManifestSnapshot(
            schemaVersion: MaterializationRoutingManifestSnapshot.CurrentSchemaVersion,
            authority: prior.Authority,
            planSet: prior.PlanSet,
            revision: new("1"),
            latestFence: new("1"),
            entries: newerFence.DesiredEntries);
        var conflictingRequest = new MaterializationAtomicRoutingManifestRequest(
            schemaVersion: newerFence.SchemaVersion,
            realization: newerFence.Realization,
            prior: stalePrior,
            desiredEntries: newerFence.DesiredEntries,
            fence: new("3"),
            commandId: new("command/advance-fence-without-commit"),
            issuedAtUtc: newerFence.IssuedAtUtc);
        var rejected = await authority.CompareExchangeAsync(context, conflictingRequest);
        var retained = await authority.InspectAsync(context, realization.Requirement.PlanSet);

        Assert.Equal(MaterializationBackendRoutingDisposition.RevisionConflict, rejected.Disposition);
        Assert.True(MaterializationContract.CanonicalEquals(prior, rejected.Snapshot));
        Assert.True(MaterializationContract.CanonicalEquals(prior, retained));
    }

    [Fact]
    public async Task DurableAdapter_AcknowledgementLossReconcilesTheCommittedManifestWithoutAnotherCommit()
    {
        var (planSet, leaves) = AtomicScenario();
        var realization = Realization(planSet);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet, realization);
        using var authority = new InMemoryMaterializationAtomicRoutingManifestAuthority(realization);
        var barrier = ReadyBarrier(planSet, leaves);
        var operationAtUtc = barrier.ReadyGenerations.Max(static ready => ready.ReadyAtUtc).AddTicks(1);
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(operationAtUtc));
        var executor = new MaterializationAtomicRoutingManifestExecutor(planSet, realization);
        var intent = executor.CreateRequest(
            barrier: barrier,
            prior: await authority.InspectAsync(context, realization.Requirement.PlanSet),
            fence: MaterializationBackendRoutingFence.Initial,
            issuedAtUtc: operationAtUtc);
        var request = Request(
            artifacts.ApplyAtomicRoutingManifestRequest!,
            MaterializationAtomicRoutingManifestJsonSerializer.SerializeRequest(intent),
            artifacts.ParentPlan.DefinitionReference,
            MaterializationRebuildPlanSetProcessFactory.ApplyAtomicRoutingManifestNodeId);
        var adapter = new MaterializationAtomicRoutingManifestDurableOperationAdapter(
            artifacts.ApplyAtomicRoutingManifestRequest!,
            new ExactResolver(planSet),
            authority,
            realization,
            artifacts.ParentPlan);
        var operationExecutor = new DurableOperationReferenceExecutor(artifacts.InteractionCatalog);
        var validation = operationExecutor.TryCreate(
            request,
            artifacts.ApplyAtomicRoutingManifestBinding!,
            operationAtUtc,
            out var created);
        Assert.True(validation.IsValid);
        var claimed = operationExecutor.Claim(
            Assert.IsType<DurableOperationState>(created),
            new("operation-attempt/atomic-manifest/1"),
            claimant: "worker/atomic-manifest",
            observedAtUtc: operationAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = operationExecutor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            operationAtUtc.AddTicks(1));
        var invocation = Assert.IsType<DurableOperationInvocation>(dispatched.Invocation);

        var first = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(context, invocation));
        var failure = new DurableOperationFailure(
            DurableOperationFailurePhase.PostCommitPreAcknowledgement,
            DurableOperationEffectEvidence.Ambiguous,
            DurableOperationFailureDisposition.Terminal,
            code: "tests.acknowledgementLost");
        var failed = operationExecutor.RecordObservation(
            dispatched.State,
            invocation.AttemptId,
            invocation.Fence,
            new DurableOperationFailureObservation(failure),
            operationAtUtc.AddTicks(2));
        var reconciled = Assert.IsType<DurableOperationReconciledOutcome>(
            await DurableOperationReferenceExecutor.ReconcileAsync(
                OperationContext.Create(timeProvider: new FixedTimeProvider(operationAtUtc.AddTicks(3))),
                failed.State,
                adapter));
        var firstResult = MaterializationAtomicRoutingManifestJsonSerializer.DeserializeResult(
            Assert.IsType<ObservationValue>(first.Outcome.Value.Value).GetRequiredString());
        var replayedResult = MaterializationAtomicRoutingManifestJsonSerializer.DeserializeResult(
            Assert.IsType<ObservationValue>(reconciled.Outcome.Value.Value).GetRequiredString());

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, firstResult.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, replayedResult.Disposition);
        Assert.Equal(firstResult.Receipt, replayedResult.Receipt);
        Assert.Equal(1, replayedResult.Snapshot.Revision.Ordinal);
    }

    [Fact]
    public async Task AggregateReceipt_RetainsOneManifestReceiptAndNoSyntheticLeafOrder()
    {
        var (planSet, leaves) = AtomicScenario();
        var realization = Realization(planSet);
        using var authority = new InMemoryMaterializationAtomicRoutingManifestAuthority(realization);
        var barrier = ReadyBarrier(planSet, leaves);
        var context = OperationContext.Create();
        var executor = new MaterializationAtomicRoutingManifestExecutor(planSet, realization);
        var result = await executor.ExecuteAsync(
            context,
            executor.CreateRequest(
                barrier: barrier,
                prior: await authority.InspectAsync(context, realization.Requirement.PlanSet),
                fence: MaterializationBackendRoutingFence.Initial,
                issuedAtUtc: barrier.ReadyGenerations.Max(static ready => ready.ReadyAtUtc).AddTicks(1)),
            authority);
        var leafReceipts = barrier.ReadyGenerations.Select(ready =>
            new MaterializationRebuildPlanSetLeafReceipt(
                authority: ready.Authority,
                buildChild: ready.Attempt.Continuation,
                outcome: MaterializationRebuildPlanSetLeafOutcome.Promoted,
                ready: ready,
                atomicManifestCommand: result.Request.CommandId)).ToImmutableArray();
        var receipt = MaterializationRebuildPlanSetReceipt.Create(
            planSet: planSet,
            parentContinuation: barrier.ParentContinuation,
            outcome: MaterializationRebuildPlanSetOutcome.Completed,
            leaves: leafReceipts,
            readyBarrier: barrier,
            completedAtUtc: result.Receipt!.CommittedAtUtc,
            atomicRoutingManifest: result);
        var restored = MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(
            MaterializationRebuildPlanSetReceiptJsonSerializer.Serialize(receipt));

        Assert.Empty(restored.PromotionOrder);
        Assert.Null(restored.ProgressiveFailurePolicy);
        Assert.NotNull(restored.AtomicRoutingManifest);
        Assert.Equal(result.Request.CommandId, restored.AtomicRoutingManifest.Request.CommandId);
        Assert.Equal(result.Receipt.Revision, restored.AtomicRoutingManifest.Receipt!.Revision);
        Assert.All(restored.Leaves, leaf => Assert.Equal(result.Request.CommandId, leaf.AtomicManifestCommand));
    }

    static (MaterializationRebuildPlanSet PlanSet, ImmutableArray<MaterializationRebuildPlan> Leaves) AtomicScenario()
    {
        var first = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var scenario = MaterializationRebuildPlanSetTests.CreateIndependentTwoLeafScenario(first);
        var independent = scenario.PlanSet;
        MaterializationRebuildPlanSet planSet = new(
            schemaVersion: independent.SchemaVersion,
            request: independent.Request,
            membership: independent.Membership,
            placement: independent.Placement,
            scheduling: independent.Scheduling,
            promotion: new(MaterializationRebuildPromotionMode.AtomicVisibility),
            leafPlans: independent.LeafPlans,
            provenance: independent.Provenance);
        return (planSet, scenario.Leaves);
    }

    static MaterializationAtomicRoutingManifestRealization Realization(MaterializationRebuildPlanSet planSet)
    {
        var requirement = MaterializationAtomicRoutingManifestRequirement.FromPlanSet(planSet, "routing-authority/a");
        return Assert.IsType<MaterializationAtomicRoutingManifestRealization>(
            MaterializationAtomicRoutingManifestCompiler.Compile(
                planSet,
                requirement,
                Capability(requirement)).Artifact);
    }

    static MaterializationAtomicRoutingManifestCapability Capability(
        MaterializationAtomicRoutingManifestRequirement requirement,
        string? authority = null,
        ImmutableArray<MaterializationPlacementSliceReference> scope = default,
        ImmutableArray<string> settings = default,
        ImmutableArray<MaterializationGuaranteeKind> guarantees = default,
        CapabilityRealizationKind realization = CapabilityRealizationKind.Native) =>
        new(
            schemaVersion: MaterializationAtomicRoutingManifestCapability.CurrentSchemaVersion,
            authority: authority ?? requirement.Authority,
            scope: scope.IsDefault ? requirement.Scope : scope,
            routingSettings: settings.IsDefault ? requirement.RoutingSettings : settings,
            guarantees: guarantees.IsDefault ? requirement.Guarantees : guarantees,
            realization: realization,
            evidenceReferences: ["tests/atomic-routing-manifest-authority"],
            provenance: new(
                new ExecutionProducerProvenance("cohesive-tests", "1"),
                new ExecutionSourceProvenance("tests/atomic-routing-manifest"),
                DocumentOrigin.Generated));

    static MaterializationRebuildReadyBarrier ReadyBarrier(
        MaterializationRebuildPlanSet planSet,
        ImmutableArray<MaterializationRebuildPlan> leaves)
    {
        ProcessContinuationIdentity parent = new(
            processInstanceId: new("process/atomic-manifest-parent"),
            processAttemptId: new("attempt/atomic-manifest-parent/1"));
        return MaterializationRebuildReadyBarrier.Create(
            planSet,
            parent,
            [
                .. leaves.Select((leaf, index) =>
                    MaterializationRebuildPlanSetReceiptTests.CreateReady(
                        planSet,
                        leaf,
                        new(
                            processInstanceId: new($"process/atomic-manifest-leaf/{index}"),
                            processAttemptId: new($"attempt/atomic-manifest-leaf/{index}/1")),
                        $"atomic-manifest/{index}"))
            ]);
    }

    static RequestEnvelope Request(
        RequestContractReference contract,
        string payload,
        ExecutionDefinitionReference definition,
        ExecutionNodeId node)
    {
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process/atomic-manifest-adapter"),
            processAttemptId: new("attempt/atomic-manifest-adapter/1"));
        return new(
            schemaVersion: InteractionEnvelope.CurrentSchemaVersion,
            context: new(
                emissionId: new("emission/atomic-manifest-adapter"),
                origin: new ProcessInteractionOrigin(
                    definition,
                    node,
                    continuation,
                    new("activation/atomic-manifest-adapter"),
                    new("token/atomic-manifest-adapter")),
                correlationId: new("correlation/atomic-manifest-adapter"),
                causationId: null,
                authorityScope: new("authority/atomic-manifest-adapter", "tenant/tests"),
                idempotencyKey: new("idempotency/atomic-manifest-adapter"),
                ordering: null,
                delivery: new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                provenance: new(
                    new ExecutionProducerProvenance("cohesive-tests", "1"),
                    new ExecutionSourceProvenance("tests/atomic-manifest-adapter"),
                    DocumentOrigin.Generated)),
            contract: contract,
            payload: PortableValue.Concrete(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                ObservationValue.FromString(payload)),
            responseTarget: new ProcessTokenInteractionTarget(
                continuation,
                new("token/atomic-manifest-adapter/response")));
    }

    sealed class ExactResolver(MaterializationRebuildPlanSet planSet)
        : IMaterializationRebuildPlanSetExecutionResolver
    {
        public bool TryResolve(
            MaterializationRebuildPlanSetReference reference,
            out MaterializationRebuildPlanSet? resolvedPlanSet)
        {
            var exact = reference == MaterializationRebuildPlanSetReference.FromPlanSet(planSet);
            resolvedPlanSet = exact ? planSet : null;
            return exact;
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
