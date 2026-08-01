using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationSynchronizationWorkStoreTests
{
    static readonly MaterializationSynchronizationWorkKey Key = new(
        new("tests/materialization"),
        new("sha256", "execution-definition/v1", "definition"),
        new("sha256", "materialization-rebuild-plan/v1", "rebuild"),
        new("sha256", "materialization-impact-plan/v1", "abcdef0123456789"),
        new("generation-1"));
    static readonly QualifiedShapeId Shape = new(new("tests"), new("Item"));
    static readonly MaterializationSourceScope Scope = new(
        physicalPlan: new("sha256", "tests/synchronization/v1", "physical-plan"),
        placement: new(
            id: new("placement/items"),
            input: new("input/items"),
            node: new("node/items"),
            binding: new("binding/items"),
            shape: Shape,
            source: new("source/items"),
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new(Shape, "id")),
        partition: new("partition-1"),
        orderingScope: new("ordering-1"));

    [Fact]
    public void WorkIntent_CanonicalizesItemOrderAndRejectsDuplicateItems()
    {
        MaterializationSynchronizationWorkIntent intent = new(
            Page("canonical"),
            [
                new MaterializationSynchronizationDeleteIntent(new("z")),
                new MaterializationSynchronizationUpsertIntent(
                    new("a"),
                    ObservationValue.FromString("value"))
            ]);

        Assert.Equal(["a", "z"], intent.Items.Select(static item => item.ItemId.Value));
        Assert.Throws<ArgumentException>(() => new MaterializationSynchronizationWorkIntent(
            Page("duplicate"),
            [
                new MaterializationSynchronizationDeleteIntent(new("same")),
                new MaterializationSynchronizationUpsertIntent(
                    new("same"),
                    ObservationValue.FromString("value"))
            ]));
    }

    [Fact]
    public async Task Ownership_IsSingleWriterCompareAndSwapFenced()
    {
        IMaterializationSynchronizationWorkStore store =
            new InMemoryMaterializationSynchronizationWorkStore();
        var context = OperationContext.Create();
        var competing = await Task.WhenAll(
            store.AcquireFenceAsync(
                context,
                Key,
                new("claim-a"),
                expectedRevision: null,
                owner: "worker-a"),
            store.AcquireFenceAsync(
                context,
                Key,
                new("claim-b"),
                expectedRevision: null,
                owner: "worker-b"));

        Assert.Single(competing, static result =>
            result.Disposition == MaterializationSynchronizationWorkMutationDisposition.Applied);
        Assert.Single(competing, static result =>
            result.Disposition == MaterializationSynchronizationWorkMutationDisposition.RevisionConflict);
        var initial = Assert.IsType<MaterializationSynchronizationWorkSnapshot>(
            competing.Single(static result =>
                result.Disposition == MaterializationSynchronizationWorkMutationDisposition.Applied).Snapshot);

        var successorOwner = initial.FenceOwner == "worker-a" ? "worker-b" : "worker-a";
        var superseded = await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-successor"),
            initial.Revision,
            successorOwner);
        var current = Assert.IsType<MaterializationSynchronizationWorkSnapshot>(superseded.Snapshot);
        var staleFence = await store.PrepareAsync(
            context,
            Key,
            new("prepare-stale-fence"),
            current.Revision,
            initial.FenceOwner,
            initial.Fence,
            Work("item-a", "value-a"));
        var staleRevision = await store.PrepareAsync(
            context,
            Key,
            new("prepare-stale-revision"),
            initial.Revision,
            current.FenceOwner,
            current.Fence,
            Work("item-a", "value-a"));

        Assert.Equal(MaterializationProgressFence.Initial.Ordinal + 1, current.Fence.Ordinal);
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.StaleFence,
            staleFence.Disposition);
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.RevisionConflict,
            staleRevision.Disposition);
    }

    [Fact]
    public async Task Prepare_AssignsCanonicalReplayStableVersionAndMutationIdentities()
    {
        IMaterializationSynchronizationWorkStore store =
            new InMemoryMaterializationSynchronizationWorkStore();
        var context = OperationContext.Create();
        var claimed = await ClaimAsync(store, context);
        MaterializationSynchronizationWorkIntent intent = new(
            Page("prepare-1"),
            [
                new MaterializationSynchronizationDeleteIntent(new("item-z")),
                new MaterializationSynchronizationUpsertIntent(
                    new("item-a"),
                    ObservationValue.FromString("first"))
            ]);

        var prepared = await store.PrepareAsync(
            context,
            Key,
            new("prepare-1"),
            claimed.Revision,
            claimed.FenceOwner,
            claimed.Fence,
            intent);
        var replayed = await store.PrepareAsync(
            context,
            Key,
            new("prepare-1"),
            claimed.Revision,
            claimed.FenceOwner,
            claimed.Fence,
            new(Page("prepare-1"), [
                new MaterializationSynchronizationUpsertIntent(
                    new("item-a"),
                    ObservationValue.FromString("first")),
                new MaterializationSynchronizationDeleteIntent(new("item-z"))
            ]));
        var identityConflict = await store.PrepareAsync(
            context,
            Key,
            new("prepare-1"),
            claimed.Revision,
            claimed.FenceOwner,
            claimed.Fence,
            Work("item-a", "changed"));

        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Applied, prepared.Disposition);
        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.IdentityConflict,
            identityConflict.Disposition);
        var first = Assert.IsType<MaterializationPreparedSynchronizationWork>(prepared.PreparedWork);
        var replay = Assert.IsType<MaterializationPreparedSynchronizationWork>(replayed.PreparedWork);
        Assert.Equal("2", first.Version?.Value);
        Assert.Equal(["item-a", "item-z"], first.Mutations.Select(static mutation => mutation.ItemId.Value));
        Assert.All(first.Mutations, mutation => Assert.Equal(first.Version, mutation.Version));
        Assert.Equal(
            first.Mutations.Select(static mutation => mutation.MutationId),
            replay.Mutations.Select(static mutation => mutation.MutationId));
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal("3", prepared.Snapshot!.NextItemVersion.Value);
    }

    [Fact]
    public async Task Complete_RequiresExactPendingWorkAndUnlocksNextMonotonicVersion()
    {
        IMaterializationSynchronizationWorkStore store =
            new InMemoryMaterializationSynchronizationWorkStore();
        var context = OperationContext.Create();
        var claimed = await ClaimAsync(store, context);
        var firstPrepare = await store.PrepareAsync(
            context,
            Key,
            new("prepare-1"),
            claimed.Revision,
            claimed.FenceOwner,
            claimed.Fence,
            Work("item-a", "first"));
        var firstPending = Assert.IsType<MaterializationPreparedSynchronizationWork>(firstPrepare.PreparedWork);
        var pendingSnapshot = Assert.IsType<MaterializationSynchronizationWorkSnapshot>(firstPrepare.Snapshot);

        var concurrentPrepare = await store.PrepareAsync(
            context,
            Key,
            new("prepare-2"),
            pendingSnapshot.Revision,
            pendingSnapshot.FenceOwner,
            pendingSnapshot.Fence,
            Work("item-b", "second"));
        var wrongCompletion = await store.CompleteAsync(
            context,
            Key,
            new("complete-wrong"),
            pendingSnapshot.Revision,
            pendingSnapshot.FenceOwner,
            pendingSnapshot.Fence,
            new("different-preparation"),
            firstPending.Version);
        var completed = await store.CompleteAsync(
            context,
            Key,
            new("complete-1"),
            pendingSnapshot.Revision,
            pendingSnapshot.FenceOwner,
            pendingSnapshot.Fence,
            firstPending.PreparationId,
            firstPending.Version);

        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict,
            concurrentPrepare.Disposition);
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict,
            wrongCompletion.Disposition);
        Assert.Null(completed.Snapshot!.PendingWork);

        var secondPrepare = await store.PrepareAsync(
            context,
            Key,
            new("prepare-2"),
            completed.Snapshot.Revision,
            completed.Snapshot.FenceOwner,
            completed.Snapshot.Fence,
            Work("item-b", "second"));

        Assert.Equal("3", secondPrepare.PreparedWork!.Version?.Value);
        Assert.Equal("4", secondPrepare.Snapshot!.NextItemVersion.Value);
        Assert.NotEqual(
            firstPending.Mutations[0].MutationId,
            secondPrepare.PreparedWork.Mutations[0].MutationId);
    }

    [Fact]
    public async Task PendingWork_SurvivesCallerLossAndCanBeRecoveredUnderANewFence()
    {
        IMaterializationSynchronizationWorkStore store =
            new InMemoryMaterializationSynchronizationWorkStore();
        var context = OperationContext.Create();
        var claimed = await ClaimAsync(store, context);
        var prepared = await store.PrepareAsync(
            context,
            Key,
            new("prepare-before-crash"),
            claimed.Revision,
            claimed.FenceOwner,
            claimed.Fence,
            Work("item-a", "value"));
        var original = Assert.IsType<MaterializationPreparedSynchronizationWork>(prepared.PreparedWork);

        var recoveredBeforeClaim = Assert.IsType<MaterializationSynchronizationWorkSnapshot>(
            await store.LoadAsync(context, Key));
        var reclaimed = await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-after-crash"),
            recoveredBeforeClaim.Revision,
            owner: "worker-recovery");
        var recovered = Assert.IsType<MaterializationSynchronizationWorkSnapshot>(reclaimed.Snapshot);

        Assert.Equal(original.Version, recovered.PendingWork!.Version);
        Assert.Equal(
            original.Mutations.Select(static mutation => mutation.MutationId),
            recovered.PendingWork.Mutations.Select(static mutation => mutation.MutationId));

        var completed = await store.CompleteAsync(
            context,
            Key,
            new("complete-after-crash"),
            recovered.Revision,
            recovered.FenceOwner,
            recovered.Fence,
            recovered.PendingWork.PreparationId,
            recovered.PendingWork.Version);
        var latePrepareReplay = await store.PrepareAsync(
            context,
            Key,
            new("prepare-before-crash"),
            claimed.Revision,
            claimed.FenceOwner,
            claimed.Fence,
            Work("item-a", "value"));

        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Applied, completed.Disposition);
        Assert.Null(completed.Snapshot!.PendingWork);
        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Replayed, latePrepareReplay.Disposition);
        Assert.Equal(original.Version, latePrepareReplay.PreparedWork!.Version);
        Assert.Equal(
            original.Mutations[0].MutationId,
            latePrepareReplay.PreparedWork.Mutations[0].MutationId);
    }

    [Fact]
    public async Task EffectFreePendingWork_StillRejectsAConcurrentPreparationUntilExactCompletion()
    {
        IMaterializationSynchronizationWorkStore store =
            new InMemoryMaterializationSynchronizationWorkStore();
        var context = OperationContext.Create();
        var claimed = await ClaimAsync(store, context);
        var first = await store.PrepareAsync(
            context,
            Key,
            new("prepare-effect-free-1"),
            claimed.Revision,
            claimed.FenceOwner,
            claimed.Fence,
            new(Page("effect-free-1"), items: []));
        var pending = Assert.IsType<MaterializationSynchronizationWorkSnapshot>(first.Snapshot);

        var concurrent = await store.PrepareAsync(
            context,
            Key,
            new("prepare-effect-free-2"),
            pending.Revision,
            pending.FenceOwner,
            pending.Fence,
            new(Page("effect-free-2"), items: []));

        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Applied, first.Disposition);
        Assert.NotNull(pending.PendingWork);
        Assert.Null(pending.PendingWork.Version);
        Assert.Empty(pending.PendingWork.Mutations);
        Assert.Equal("2", pending.NextItemVersion.Value);
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict,
            concurrent.Disposition);
        Assert.Equal(pending, concurrent.Snapshot);
    }

    [Fact]
    public async Task Activation_BeginsOnlyWithoutPendingWorkAndBlocksSynchronizationUntilComplete()
    {
        IMaterializationSynchronizationWorkStore store =
            new InMemoryMaterializationSynchronizationWorkStore();
        var context = OperationContext.Create();
        var claimed = await ClaimAsync(store, context);
        var pending = await store.PrepareAsync(
            context,
            Key,
            new("prepare-before-activation"),
            claimed.Revision,
            claimed.FenceOwner,
            claimed.Fence,
            Work("item-before-activation", "value"));
        var prefixes = ActivationPrefixes("blocking");

        var blockedBegin = await SaveActivationAsync(
            store,
            context,
            pending.Snapshot!,
            mutation: "activate-while-pending",
            prefixes.Started);
        var completedPending = await store.CompleteAsync(
            context,
            Key,
            new("complete-before-activation"),
            pending.Snapshot!.Revision,
            pending.Snapshot.FenceOwner,
            pending.Snapshot.Fence,
            pending.PreparedWork!.PreparationId,
            pending.PreparedWork.Version);
        var begun = await SaveActivationAsync(
            store,
            context,
            completedPending.Snapshot!,
            mutation: "activate-start",
            prefixes.Started);
        var blockedPrepare = await store.PrepareAsync(
            context,
            Key,
            new("prepare-during-activation"),
            begun.Snapshot!.Revision,
            begun.Snapshot.FenceOwner,
            begun.Snapshot.Fence,
            Work("item-during-activation", "value"));

        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict,
            blockedBegin.Disposition);
        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Applied, begun.Disposition);
        Assert.Equal(prefixes.Started, begun.Snapshot.Activation);
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.ActivationConflict,
            blockedPrepare.Disposition);
        Assert.Null(blockedPrepare.PreparedWork);
    }

    [Fact]
    public async Task Activation_AdvancesOneExactPrefixAtATimeAndReplaysEveryStageMutation()
    {
        IMaterializationSynchronizationWorkStore store =
            new InMemoryMaterializationSynchronizationWorkStore();
        var context = OperationContext.Create();
        var claimed = await ClaimAsync(store, context);
        var prefixes = ActivationPrefixes("ordered");
        var divergent = ActivationPrefixes("divergent");

        var skipped = await SaveActivationAsync(
            store,
            context,
            claimed,
            mutation: "activate-skipped-seal",
            prefixes.Sealed);
        var begun = await SaveActivationAsync(
            store,
            context,
            claimed,
            mutation: "activate-start",
            prefixes.Started);
        var replayedBegin = await SaveActivationAsync(
            store,
            context,
            claimed,
            mutation: "activate-start",
            prefixes.Started);
        var conflictingIdentity = await SaveActivationAsync(
            store,
            context,
            claimed,
            mutation: "activate-start",
            divergent.Started);
        var divergentPrefix = await SaveActivationAsync(
            store,
            context,
            begun.Snapshot!,
            mutation: "activate-divergent-prefix",
            divergent.Started);

        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.ActivationConflict, skipped.Disposition);
        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Applied, begun.Disposition);
        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Replayed, replayedBegin.Disposition);
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.IdentityConflict,
            conflictingIdentity.Disposition);
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.ActivationConflict,
            divergentPrefix.Disposition);

        var current = begun.Snapshot!;
        var stages = new[]
        {
            (Mutation: "activate-sealed", State: prefixes.Sealed),
            (Mutation: "activate-validated", State: prefixes.Validated),
            (Mutation: "activate-promoted", State: prefixes.Promoted)
        };
        foreach (var stage in stages)
        {
            var prior = current;
            var applied = await SaveActivationAsync(
                store,
                context,
                prior,
                stage.Mutation,
                stage.State);
            var replayed = await SaveActivationAsync(
                store,
                context,
                prior,
                stage.Mutation,
                stage.State);

            Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Applied, applied.Disposition);
            Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Replayed, replayed.Disposition);
            Assert.Equal(stage.State, applied.Snapshot!.Activation);
            current = applied.Snapshot;
        }

        Assert.True(current.Activation!.IsComplete);
    }

    [Fact]
    public async Task CompletedActivation_PermitsSameGenerationMaintenance()
    {
        IMaterializationSynchronizationWorkStore store =
            new InMemoryMaterializationSynchronizationWorkStore();
        var context = OperationContext.Create();
        var current = await ClaimAsync(store, context);
        var prefixes = ActivationPrefixes("maintenance");
        foreach (var stage in new[] { prefixes.Started, prefixes.Sealed, prefixes.Validated, prefixes.Promoted })
        {
            var saved = await SaveActivationAsync(
                store,
                context,
                current,
                mutation: $"activate-maintenance-{current.Revision.Value}",
                stage);
            Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Applied, saved.Disposition);
            current = saved.Snapshot!;
        }

        var prepared = await store.PrepareAsync(
            context,
            Key,
            new("prepare-after-activation"),
            current.Revision,
            current.FenceOwner,
            current.Fence,
            Work("item-after-activation", "maintained"));

        Assert.Equal(MaterializationSynchronizationWorkMutationDisposition.Applied, prepared.Disposition);
        Assert.Equal("2", prepared.PreparedWork!.Version?.Value);
        Assert.Equal(prefixes.Promoted, prepared.Snapshot!.Activation);
    }

    static async Task<MaterializationSynchronizationWorkSnapshot> ClaimAsync(
        IMaterializationSynchronizationWorkStore store,
        OperationContext context)
    {
        var claim = await store.AcquireFenceAsync(
            context,
            Key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a");
        return Assert.IsType<MaterializationSynchronizationWorkSnapshot>(claim.Snapshot);
    }

    static Task<MaterializationSynchronizationWorkMutationResult> SaveActivationAsync(
        IMaterializationSynchronizationWorkStore store,
        OperationContext context,
        MaterializationSynchronizationWorkSnapshot snapshot,
        string mutation,
        MaterializationGenerationActivationState activation) =>
        store.SaveActivationAsync(
            context,
            Key,
            new(mutation),
            snapshot.Revision,
            snapshot.FenceOwner,
            snapshot.Fence,
            activation);

    static ActivationPrefixFixture ActivationPrefixes(string suffix)
    {
        var checkpoint = new MaterializationCheckpointId($"checkpoint-activation-{suffix}");
        var position = new MaterializationSourcePosition(
            formatVersion: 1,
            scope: Scope,
            value: $"position-activation-{suffix}");
        var convergence = new MaterializationConvergenceReceipt(
            schemaVersion: MaterializationConvergenceReceipt.CurrentSchemaVersion,
            synchronization: Key,
            feeds:
            [
                new(
                    feed: new($"feed-activation-{suffix}"),
                    scope: Scope,
                    latestChangeCheckpoint: checkpoint,
                    throughPosition: position,
                    caughtUpReadStartedAtUtc: DateTimeOffset.UnixEpoch,
                    caughtUpReadCompletedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(1),
                    checkpointCommittedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(2),
                    settlementRequirement: MaterializationConvergenceSettlementRequirement.NotRequired)
            ],
            evaluatedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(3),
            freshnessDemand: new(maximumLagMilliseconds: 10_000),
            validation: DocumentValidationResult.Valid);
        MaterializationSealGenerationRequest sealRequest = new(
            sealId: new($"seal-activation-{suffix}"),
            generationId: Key.Generation,
            expectedRevision: new("10"),
            workerFence: new("1"),
            sealedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(4));
        MaterializationSealReceipt sealReceipt = new(
            sealId: sealRequest.SealId,
            generationId: Key.Generation,
            generationRevision: new("11"),
            visibleItemCount: 7,
            fingerprint: new($"seal-fingerprint-{suffix}"),
            sealedAtUtc: sealRequest.SealedAtUtc);
        MaterializationValidateGenerationRequest validationRequest = new(
            validationId: new($"validation-activation-{suffix}"),
            generationId: Key.Generation,
            expectedRevision: sealReceipt.GenerationRevision,
            expectedSealFingerprint: sealReceipt.Fingerprint,
            expectedVisibleItemCount: sealReceipt.VisibleItemCount,
            validator: "tests/activation-validator/v1",
            workerFence: sealRequest.WorkerFence,
            validatedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(5));
        MaterializationValidationReceipt validationReceipt = new(
            validationId: validationRequest.ValidationId,
            generationId: Key.Generation,
            generationRevision: new("12"),
            sealFingerprint: sealReceipt.Fingerprint,
            fingerprint: new($"validation-fingerprint-{suffix}"),
            validation: DocumentValidationResult.Valid,
            validatedAtUtc: validationRequest.ValidatedAtUtc);
        MaterializationPromoteGenerationRequest promotionRequest = new(
            promotionId: new($"promotion-activation-{suffix}"),
            generationId: Key.Generation,
            expectedGenerationRevision: validationReceipt.GenerationRevision,
            validationFingerprint: validationReceipt.Fingerprint,
            expectedActiveGenerationId: null,
            expectedTargetRevision: MaterializationTargetRevision.Initial,
            generationWorkerFence: sealRequest.WorkerFence,
            promotionFence: MaterializationPromotionFence.Initial,
            promotedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(6));
        MaterializationPromotionReceipt promotionReceipt = new(
            promotionId: promotionRequest.PromotionId,
            targetId: new("target-activation"),
            generationId: Key.Generation,
            previousGenerationId: null,
            targetRevision: new("1"),
            generationWorkerFence: promotionRequest.GenerationWorkerFence,
            promotionFence: promotionRequest.PromotionFence,
            validationFingerprint: promotionRequest.ValidationFingerprint,
            promotedAtUtc: promotionRequest.PromotedAtUtc);
        var started = new MaterializationGenerationActivationState(convergence, sealRequest);
        var sealedState = new MaterializationGenerationActivationState(
            convergence,
            sealRequest,
            sealReceipt,
            validationRequest);
        var validated = new MaterializationGenerationActivationState(
            convergence,
            sealRequest,
            sealReceipt,
            validationRequest,
            validationReceipt,
            promotionRequest);
        var promoted = new MaterializationGenerationActivationState(
            convergence,
            sealRequest,
            sealReceipt,
            validationRequest,
            validationReceipt,
            promotionRequest,
            promotionReceipt);
        return new(started, sealedState, validated, promoted);
    }

    static MaterializationSynchronizationWorkIntent Work(string itemId, string value) =>
        new(Page(itemId), [
            new MaterializationSynchronizationUpsertIntent(
                new(itemId),
                ObservationValue.FromString(value))
        ]);

    static MaterializationSynchronizationPageIntent Page(string identity) =>
        new(
            feed: new("feed-1"),
            checkpoint: new($"checkpoint-{identity}"),
            throughPosition: new(
                formatVersion: 1,
                scope: Scope,
                value: $"position-{identity}"),
            appliedDeliveries: [new($"delivery-{identity}")],
            state: MaterializationChangePageState.CaughtUp,
            readStartedAtUtc: DateTimeOffset.UnixEpoch,
            readCompletedAtUtc: DateTimeOffset.UnixEpoch);

    sealed record ActivationPrefixFixture(
        MaterializationGenerationActivationState Started,
        MaterializationGenerationActivationState Sealed,
        MaterializationGenerationActivationState Validated,
        MaterializationGenerationActivationState Promoted);
}
