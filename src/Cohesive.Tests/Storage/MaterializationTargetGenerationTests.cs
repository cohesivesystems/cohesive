using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationTargetGenerationTests
{
    static readonly DateTimeOffset Epoch = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    static readonly MaterializationId DefinitionId = new("materialization/search");
    static readonly ExecutionDefinitionFingerprint DefinitionFingerprint = new(
        "sha256",
        "cohesive-materialization-definition/v1-c14n/v1",
        "0123456789abcdef");
    static readonly MaterializationWorkerFence FenceOne = new("1");
    static readonly MaterializationWorkerFence FenceTwo = new("2");
    static readonly MaterializationPromotionFence PromotionFenceOne = new("1");
    static readonly MaterializationPromotionFence PromotionFenceTwo = new("2");

    [Fact]
    public void TargetContracts_RejectDefaultValueIdentitiesAsArgumentException()
    {
        var descriptor = Descriptor();
        MaterializationItemMutation mutation = new MaterializationDelete(
            new("item/default-boundary"),
            new("mutation/default-boundary"),
            new("1"));

        Assert.Throws<ArgumentException>(() => new MaterializationTargetDescriptor(
            default,
            DefinitionId,
            descriptor.Capabilities));
        Assert.Throws<ArgumentException>(() => new MaterializationTargetDescriptor(
            descriptor.Id,
            default,
            descriptor.Capabilities));
        Assert.Throws<ArgumentException>(() => new MaterializationApplyBatchRequest(
            default,
            new("generation/default-boundary"),
            FenceOne,
            [mutation]));
        Assert.Throws<ArgumentException>(() => new MaterializationApplyBatchRequest(
            new("batch/default-boundary"),
            default,
            FenceOne,
            [mutation]));
        Assert.Throws<ArgumentException>(() => new MaterializationApplyBatchRequest(
            new("batch/default-boundary"),
            new("generation/default-boundary"),
            default,
            [mutation]));
    }

    [Fact]
    public async Task BeginGeneration_ReplaysExactIntentAndSupersedingFenceRetainsNewOwner()
    {
        var target = Target();
        var generationId = new MaterializationGenerationId("generation/provenance");
        MaterializationBeginGenerationRequest request = new(
            DefinitionId,
            generationId,
            DefinitionFingerprint,
            FenceOne,
            Epoch);

        var begun = await target.BeginGenerationAsync(OperationContext.Create(), request);
        var replayed = await target.BeginGenerationAsync(OperationContext.Create(), request);
        var superseded = await target.BeginGenerationAsync(
            OperationContext.Create(),
            new(DefinitionId, generationId, DefinitionFingerprint, FenceTwo, Epoch));
        var stale = await target.BeginGenerationAsync(OperationContext.Create(), request);
        var differentDefinition = await target.BeginGenerationAsync(
            OperationContext.Create(),
            new(
                DefinitionId,
                generationId,
                new("sha256", "cohesive-materialization-definition/v1-c14n/v1", "different"),
                new("3"),
                Epoch));
        var differentMaterialization = await target.BeginGenerationAsync(
            OperationContext.Create(),
            new(new("materialization/other"), generationId, DefinitionFingerprint, new("4"), Epoch));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, begun.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, superseded.Disposition);
        Assert.Equal(FenceTwo, superseded.Generation!.LatestWorkerFence);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, stale.Disposition);
        Assert.Equal(FenceTwo, stale.Generation!.LatestWorkerFence);
        Assert.Equal(MaterializationTargetOperationDisposition.IdentityConflict, differentDefinition.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.MaterializationConflict, differentMaterialization.Disposition);
        Assert.Equal(DefinitionId, differentMaterialization.Generation!.MaterializationId);
        Assert.Equal(DefinitionFingerprint, differentMaterialization.Generation.DefinitionFingerprint);
    }

    [Fact]
    public async Task ApplyBatch_PreservesPerItemCorrespondenceAndSupportsPartialRetry()
    {
        var retryItem = new MaterializationItemId("item-b");
        var target = Target(new InMemoryMaterializationTargetFaultPlan(
            retryableRejections: [KeyValuePair.Create(retryItem, 1)]));
        var generationId = new MaterializationGenerationId("generation/partial-retry");
        await Begin(target, generationId, Epoch, FenceOne);
        MaterializationItemMutation[] mutations =
        [
            new MaterializationUpsert(
                retryItem,
                new("mutation-b"),
                new("1"),
                ObservationValue.FromString("bravo")),
            new MaterializationUpsert(
                new("item-a"),
                new("mutation-a"),
                new("1"),
                ObservationValue.FromString("alpha"))
        ];

        var first = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(new("batch/first"), generationId, FenceOne, [.. mutations]));

        Assert.Equal(MaterializationBatchDisposition.Applied, first.Disposition);
        first.ValidateAgainst(new(new("batch/first"), generationId, FenceOne, [.. mutations]));
        Assert.Equal(mutations.Select(static mutation => mutation.ItemId), first.Outcomes.Select(static outcome => outcome.ItemId));
        Assert.Equal(MaterializationItemOutcomeDisposition.RetryableRejected, first.Outcomes[0].Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.Applied, first.Outcomes[1].Disposition);
        var partial = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), generationId));
        Assert.Equal(1, partial.VisibleItemCount);
        Assert.Equal(1, partial.PendingRetryableMutationCount);

        var retryRequest = new MaterializationApplyBatchRequest(
            new("batch/retry"),
            generationId,
            FenceOne,
            [mutations[0]]);
        var retried = await target.ApplyBatchAsync(OperationContext.Create(), retryRequest);
        var replayed = await target.ApplyBatchAsync(OperationContext.Create(), retryRequest);

        Assert.Equal(MaterializationItemOutcomeDisposition.Applied, Assert.Single(retried.Outcomes).Disposition);
        Assert.Equal(MaterializationBatchDisposition.Replayed, replayed.Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.Replayed, Assert.Single(replayed.Outcomes).Disposition);
        Assert.Equal(
            0,
            (await target.InspectGenerationAsync(OperationContext.Create(), generationId))!.PendingRetryableMutationCount);
        var items = Assert.IsType<InMemoryMaterializationTargetItemPage>(
            await target.InspectItemsAsync(OperationContext.Create(), generationId, afterItemId: null, maximumItems: 10));
        Assert.Equal(["item-a", "item-b"], items.Items.Select(static item => item.ItemId.Value));
    }

    [Fact]
    public async Task UnresolvedRetryableMutation_PreventsValidationAndPromotion()
    {
        var itemId = new MaterializationItemId("item/unresolved");
        var generationId = new MaterializationGenerationId("generation/unresolved");
        var target = Target(new InMemoryMaterializationTargetFaultPlan(
            retryableRejections: [KeyValuePair.Create(itemId, 1)]));
        await Begin(target, generationId, Epoch, FenceOne);
        var written = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/unresolved"),
                generationId,
                FenceOne,
                [new MaterializationUpsert(itemId, new("mutation/unresolved"), new("1"), ObservationValue.FromString("value"))]));
        var sealedResult = await target.SealGenerationAsync(
            OperationContext.Create(),
            new(new("seal/unresolved"), generationId, written.GenerationRevision!.Value, FenceOne, Epoch.AddMinutes(1)));
        var validated = await target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                new("validation/unresolved"),
                generationId,
                sealedResult.Generation!.Revision,
                sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: null,
                "tests/validator-v1",
                FenceOne,
                Epoch.AddMinutes(2)));
        var promoted = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            new(
                new("promotion/unresolved"),
                generationId,
                validated.Generation!.Revision,
                validated.Receipt!.Fingerprint,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                FenceOne,
                PromotionFenceOne,
                Epoch.AddMinutes(3)));

        Assert.Equal(MaterializationTargetOperationDisposition.ValidationFailed, validated.Disposition);
        Assert.Equal(1, validated.Generation.PendingRetryableMutationCount);
        var diagnostic = Assert.Single(
            validated.Receipt.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == "materialization.target.validation.pendingRetryableItems");
        Assert.Equal("/writes", diagnostic.Location);
        Assert.Equal("materialization-target-validation", diagnostic.Evidence?.Stage);
        Assert.Equal(generationId.Value, diagnostic.Evidence?.Subject);
        Assert.False(diagnostic.Evidence?.SourceReferences.IsDefaultOrEmpty);
        Assert.Equal("0 pending retryable item mutations", diagnostic.Evidence?.Expected);
        Assert.Equal("1", diagnostic.Evidence?.Observed);
        Assert.Equal(MaterializationTargetOperationDisposition.StateConflict, promoted.Disposition);
        Assert.Null(promoted.Snapshot.ActiveGenerationId);
    }

    [Fact]
    public async Task UnrelatedMutationForSameItem_DoesNotResolvePendingRetryableMutation()
    {
        var itemId = new MaterializationItemId("item/pending-identity");
        var generationId = new MaterializationGenerationId("generation/pending-identity");
        var target = Target(new InMemoryMaterializationTargetFaultPlan(
            retryableRejections: [KeyValuePair.Create(itemId, 1)]));
        await Begin(target, generationId, Epoch, FenceOne);

        var first = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/pending-a"),
                generationId,
                FenceOne,
                [new MaterializationUpsert(itemId, new("mutation/pending-a"), new("1"), ObservationValue.FromString("a"))]));
        var unrelated = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/pending-b"),
                generationId,
                FenceOne,
                [new MaterializationUpsert(itemId, new("mutation/pending-b"), new("2"), ObservationValue.FromString("b"))]));

        Assert.Equal(MaterializationItemOutcomeDisposition.RetryableRejected, Assert.Single(first.Outcomes).Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.Applied, Assert.Single(unrelated.Outcomes).Disposition);
        var incomplete = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), generationId));
        Assert.Equal(1, incomplete.PendingRetryableMutationCount);

        var sealedResult = await target.SealGenerationAsync(
            OperationContext.Create(),
            new(new("seal/pending-identity"), generationId, incomplete.Revision, FenceOne, Epoch.AddMinutes(1)));
        var validated = await target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                new("validation/pending-identity"),
                generationId,
                sealedResult.Generation!.Revision,
                sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: 1,
                "tests/validator-v1",
                FenceOne,
                Epoch.AddMinutes(2)));

        Assert.Equal(MaterializationTargetOperationDisposition.ValidationFailed, validated.Disposition);
        Assert.Equal(1, validated.Generation!.PendingRetryableMutationCount);
        Assert.Contains(
            validated.Receipt!.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == "materialization.target.validation.pendingRetryableItems");
    }

    [Fact]
    public async Task EqualVersionConflict_CannotResolveDifferentPendingMutationContent()
    {
        var itemId = new MaterializationItemId("item/equal-version-conflict");
        var generationId = new MaterializationGenerationId("generation/equal-version-conflict");
        var target = Target(new InMemoryMaterializationTargetFaultPlan(
            retryableRejections: [KeyValuePair.Create(itemId, 1)]));
        await Begin(target, generationId, Epoch, FenceOne);
        MaterializationUpsert pending = new(
            itemId,
            new("mutation/equal-pending"),
            new("1"),
            ObservationValue.FromString("expected"));

        await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(new("batch/equal-pending"), generationId, FenceOne, [pending]));
        await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/equal-other"),
                generationId,
                FenceOne,
                [
                    new MaterializationUpsert(
                        itemId,
                        new("mutation/equal-other"),
                        new("1"),
                        ObservationValue.FromString("different"))
                ]));
        var conflicted = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(new("batch/equal-retry"), generationId, FenceOne, [pending]));

        Assert.Equal(MaterializationItemOutcomeDisposition.VersionConflict, Assert.Single(conflicted.Outcomes).Disposition);
        var incomplete = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), generationId));
        Assert.True(incomplete.HasPermanentFailures);
        Assert.Equal(1, incomplete.PendingRetryableMutationCount);

        var sealedResult = await target.SealGenerationAsync(
            OperationContext.Create(),
            new(new("seal/equal-conflict"), generationId, incomplete.Revision, FenceOne, Epoch.AddMinutes(1)));
        var validated = await target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                new("validation/equal-conflict"),
                generationId,
                sealedResult.Generation!.Revision,
                sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: 1,
                "tests/validator-v1",
                FenceOne,
                Epoch.AddMinutes(2)));
        Assert.Equal(MaterializationTargetOperationDisposition.ValidationFailed, validated.Disposition);
    }

    [Fact]
    public async Task Seal_MakesCandidateContentImmutableAndReplaysTheSameReceipt()
    {
        var target = Target();
        var generationId = new MaterializationGenerationId("generation/seal");
        await Begin(target, generationId, Epoch, FenceOne);
        var written = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/seal"),
                generationId,
                FenceOne,
                [new MaterializationUpsert(new("item"), new("mutation"), new("1"), ObservationValue.FromInt64(42))]));
        var sealRequest = new MaterializationSealGenerationRequest(
            new("seal/one"),
            generationId,
            written.GenerationRevision!.Value,
            FenceOne,
            Epoch.AddMinutes(1));

        var sealedResult = await target.SealGenerationAsync(OperationContext.Create(), sealRequest);
        var replayed = await target.SealGenerationAsync(OperationContext.Create(), sealRequest);
        var rejectedWrite = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/after-seal"),
                generationId,
                FenceOne,
                [new MaterializationDelete(new("item"), new("delete"), new("2"))]));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, sealedResult.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(sealedResult.Receipt, replayed.Receipt);
        Assert.Equal(MaterializationBatchDisposition.GenerationNotWritable, rejectedWrite.Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.PermanentFailure, Assert.Single(rejectedWrite.Outcomes).Disposition);
        var retained = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), generationId));
        Assert.Equal(MaterializationGenerationState.Sealed, retained.State);
        Assert.Equal(sealedResult.Receipt, retained.SealReceipt);
        Assert.Equal(1, retained.VisibleItemCount);
        var page = Assert.IsType<InMemoryMaterializationTargetItemPage>(
            await target.InspectItemsAsync(OperationContext.Create(), generationId, afterItemId: null, maximumItems: 1));
        Assert.Equal(42, Assert.Single(page.Items).Value!.Value.Int64);
    }

    [Fact]
    public async Task GenerationLifecycle_NewerFenceSupersedesEveryOlderOperation()
    {
        var target = Target();
        var generationId = new MaterializationGenerationId("generation/fenced-lifecycle");
        MaterializationBeginGenerationRequest beginOne = new(
            DefinitionId,
            generationId,
            DefinitionFingerprint,
            FenceOne,
            Epoch);
        await target.BeginGenerationAsync(OperationContext.Create(), beginOne);
        Assert.Equal(
            MaterializationTargetOperationDisposition.Replayed,
            (await target.BeginGenerationAsync(
                OperationContext.Create(),
                new(DefinitionId, generationId, DefinitionFingerprint, FenceTwo, Epoch))).Disposition);
        var replayedOlderBegin = await target.BeginGenerationAsync(OperationContext.Create(), beginOne);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayedOlderBegin.Disposition);
        Assert.Equal(FenceTwo, replayedOlderBegin.Generation!.LatestWorkerFence);

        MaterializationApplyBatchRequest writeRequest = new(
            new("batch/fenced"),
            generationId,
            FenceTwo,
            [new MaterializationUpsert(new("item"), new("mutation"), new("1"), ObservationValue.FromString("value"))]);
        var written = await target.ApplyBatchAsync(OperationContext.Create(), writeRequest);
        var replayedWriteWithTakeoverFence = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(writeRequest.BatchId, generationId, new("3"), writeRequest.Mutations));
        Assert.Equal(MaterializationBatchDisposition.Replayed, replayedWriteWithTakeoverFence.Disposition);
        Assert.Equal(
            new MaterializationWorkerFence("3"),
            (await target.InspectGenerationAsync(OperationContext.Create(), generationId))!.LatestWorkerFence);
        var staleWrite = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/stale"),
                generationId,
                FenceOne,
                [new MaterializationDelete(new("item"), new("delete/stale"), new("2"))]));
        Assert.Equal(MaterializationBatchDisposition.StaleFence, staleWrite.Disposition);

        MaterializationSealGenerationRequest seal = new(
            new("seal/fenced"),
            generationId,
            written.GenerationRevision!.Value,
            new("3"),
            Epoch.AddMinutes(1));
        var sealedResult = await target.SealGenerationAsync(OperationContext.Create(), seal);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, sealedResult.Disposition);
        var replayedSealWithTakeoverFence = await target.SealGenerationAsync(
            OperationContext.Create(),
            new(seal.SealId, generationId, seal.ExpectedRevision, new("4"), seal.SealedAtUtc));
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayedSealWithTakeoverFence.Disposition);
        Assert.Equal(new MaterializationWorkerFence("4"), replayedSealWithTakeoverFence.Generation!.LatestWorkerFence);
        var lateBatchReplay = await target.ApplyBatchAsync(OperationContext.Create(), writeRequest);
        Assert.Equal(MaterializationBatchDisposition.Replayed, lateBatchReplay.Disposition);
        Assert.Equal(
            MaterializationItemOutcomeDisposition.Replayed,
            Assert.Single(lateBatchReplay.Outcomes).Disposition);
        Assert.Equal(
            MaterializationBatchDisposition.StaleFence,
            (await target.ApplyBatchAsync(
                OperationContext.Create(),
                new(
                    new("batch/pre-seal-worker"),
                    generationId,
                    FenceTwo,
                    [new MaterializationDelete(new("item"), new("delete/pre-seal-worker"), new("2"))]))).Disposition);

        MaterializationValidateGenerationRequest validation = new(
            new("validation/fenced"),
            generationId,
            sealedResult.Generation!.Revision,
            sealedResult.Receipt!.Fingerprint,
            expectedVisibleItemCount: 1,
            "tests/validator-v1",
            new("4"),
            Epoch.AddMinutes(2));
        var validated = await target.ValidateGenerationAsync(OperationContext.Create(), validation);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, validated.Disposition);
        var replayedValidationWithTakeoverFence = await target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                validation.ValidationId,
                generationId,
                validation.ExpectedRevision,
                validation.ExpectedSealFingerprint,
                validation.ExpectedVisibleItemCount,
                validation.Validator,
                new("5"),
                validation.ValidatedAtUtc));
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayedValidationWithTakeoverFence.Disposition);
        Assert.Equal(new MaterializationWorkerFence("5"), replayedValidationWithTakeoverFence.Generation!.LatestWorkerFence);
        Assert.Equal(
            MaterializationTargetOperationDisposition.Replayed,
            (await target.SealGenerationAsync(OperationContext.Create(), seal)).Disposition);

        MaterializationRetireGenerationRequest retirement = new(
            new("retirement/fenced"),
            generationId,
            validated.Generation!.Revision,
            new("5"),
            Epoch.AddMinutes(3));
        var retired = await target.RetireGenerationAsync(OperationContext.Create(), retirement);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, retired.Disposition);
        var replayedRetirementWithTakeoverFence = await target.RetireGenerationAsync(
            OperationContext.Create(),
            new(retirement.RetirementId, generationId, retirement.ExpectedRevision, new("6"), retirement.RetiredAtUtc));
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayedRetirementWithTakeoverFence.Disposition);
        Assert.Equal(new MaterializationWorkerFence("6"), replayedRetirementWithTakeoverFence.Generation!.LatestWorkerFence);
        Assert.Equal(
            MaterializationTargetOperationDisposition.Replayed,
            (await target.ValidateGenerationAsync(OperationContext.Create(), validation)).Disposition);

        var staleCleanup = await target.CleanupGenerationAsync(
            OperationContext.Create(),
            new(new("cleanup/stale"), generationId, retired.Generation!.Revision, new("4"), Epoch.AddMinutes(4)));
        Assert.Equal(MaterializationTargetOperationDisposition.StaleFence, staleCleanup.Disposition);
        MaterializationCleanupGenerationRequest cleanup = new(
            new("cleanup/fenced"),
            generationId,
            retired.Generation.Revision,
            new("6"),
            Epoch.AddMinutes(4));
        var cleaned = await target.CleanupGenerationAsync(OperationContext.Create(), cleanup);
        var replayedCleanup = await target.CleanupGenerationAsync(
            OperationContext.Create(),
            new(cleanup.CleanupId, generationId, cleanup.ExpectedRevision, new("7"), cleanup.CleanedAtUtc));
        var postCleanupSeal = await target.SealGenerationAsync(OperationContext.Create(), seal);
        var postCleanupValidation = await target.ValidateGenerationAsync(OperationContext.Create(), validation);
        var postCleanupRetirement = await target.RetireGenerationAsync(OperationContext.Create(), retirement);

        Assert.True(cleaned.WasRemoved);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayedCleanup.Disposition);
        Assert.False(replayedCleanup.WasRemoved);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, postCleanupSeal.Disposition);
        Assert.Equal(sealedResult.Receipt, postCleanupSeal.Receipt);
        Assert.Equal(new MaterializationWorkerFence("7"), postCleanupSeal.Generation!.LatestWorkerFence);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, postCleanupValidation.Disposition);
        Assert.Equal(validated.Receipt, postCleanupValidation.Receipt);
        Assert.Equal(new MaterializationWorkerFence("7"), postCleanupValidation.Generation!.LatestWorkerFence);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, postCleanupRetirement.Disposition);
        Assert.Equal(new MaterializationWorkerFence("7"), postCleanupRetirement.Generation!.LatestWorkerFence);
    }

    [Fact]
    public async Task IdentityConflict_WithHigherFenceStillTakesOverGenerationScope()
    {
        var target = Target();
        var generationId = new MaterializationGenerationId("generation/identity-takeover");
        await Begin(target, generationId, Epoch, FenceOne);
        var original = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/identity-takeover"),
                generationId,
                FenceOne,
                [
                    new MaterializationUpsert(
                        new("item/identity-takeover"),
                        new("mutation/identity-original"),
                        new("1"),
                        ObservationValue.FromString("original"))
                ]));
        var conflict = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/identity-takeover"),
                generationId,
                FenceTwo,
                [
                    new MaterializationUpsert(
                        new("item/identity-takeover"),
                        new("mutation/identity-conflict"),
                        new("2"),
                        ObservationValue.FromString("conflict"))
                ]));
        var stale = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/identity-takeover-stale"),
                generationId,
                FenceOne,
                [
                    new MaterializationDelete(
                        new("item/identity-takeover"),
                        new("mutation/identity-stale"),
                        new("3"))
                ]));

        Assert.Equal(MaterializationBatchDisposition.Applied, original.Disposition);
        Assert.Equal(MaterializationBatchDisposition.IdentityConflict, conflict.Disposition);
        Assert.Equal(MaterializationBatchDisposition.StaleFence, stale.Disposition);
        Assert.Equal(
            FenceTwo,
            (await target.InspectGenerationAsync(OperationContext.Create(), generationId))!.LatestWorkerFence);
    }

    [Fact]
    public async Task ActiveGeneration_AcceptsIdempotentVersionedIncrementalMutations()
    {
        var target = Target();
        var prepared = await PrepareValidated(target, "active-incremental", Epoch, FenceOne);
        var promoted = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/active-incremental",
                prepared,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                FenceOne,
                PromotionFenceOne,
                Epoch.AddMinutes(3)));
        var request = new MaterializationApplyBatchRequest(
            new("batch/active-v2"),
            prepared.GenerationId,
            FenceTwo,
            [
                new MaterializationUpsert(
                    prepared.ItemId,
                    new("mutation/active-v2"),
                    new("2"),
                    ObservationValue.FromString("updated"))
            ]);

        var applied = await target.ApplyBatchAsync(OperationContext.Create(), request);
        var replayed = await target.ApplyBatchAsync(OperationContext.Create(), request);
        var stale = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/active-stale"),
                prepared.GenerationId,
                FenceOne,
                [new MaterializationDelete(prepared.ItemId, new("mutation/active-stale"), new("3"))]));

        Assert.Equal(MaterializationBatchDisposition.Applied, applied.Disposition);
        Assert.Equal(MaterializationBatchDisposition.Replayed, replayed.Disposition);
        Assert.Equal(MaterializationBatchDisposition.StaleFence, stale.Disposition);
        var generation = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), prepared.GenerationId));
        Assert.Equal(MaterializationGenerationState.Active, generation.State);
        Assert.Equal(FenceTwo, generation.LatestWorkerFence);
        Assert.NotNull(generation.SealReceipt);
        Assert.NotNull(generation.ValidationReceipt);
        var page = Assert.IsType<InMemoryMaterializationTargetItemPage>(
            await target.InspectItemsAsync(OperationContext.Create(), prepared.GenerationId, afterItemId: null, maximumItems: 1));
        Assert.Equal("2", Assert.Single(page.Items).Version.Value);
        Assert.Equal("updated", Assert.Single(page.Items).Value!.Value.String);
        Assert.Equal(PromotionFenceOne, promoted.Snapshot.LatestPromotionFence);
    }

    [Fact]
    public async Task ActiveGeneration_RetainsIncrementalFailureEvidenceWithoutBecomingUninspectable()
    {
        var retryableItem = new MaterializationItemId("item/active-retryable");
        var permanentItem = new MaterializationItemId("item/active-permanent");
        var target = Target(new InMemoryMaterializationTargetFaultPlan(
            retryableRejections: [KeyValuePair.Create(retryableItem, 1)],
            permanentFailures: [permanentItem]));
        var prepared = await PrepareValidated(target, "active-failures", Epoch, FenceOne);
        await target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/active-failures",
                prepared,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                FenceOne,
                PromotionFenceOne,
                Epoch.AddMinutes(3)));

        var result = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/active-incremental-failures"),
                prepared.GenerationId,
                FenceTwo,
                [
                    new MaterializationUpsert(
                        retryableItem,
                        new("mutation/active-retryable"),
                        new("1"),
                        ObservationValue.FromString("retry")),
                    new MaterializationUpsert(
                        permanentItem,
                        new("mutation/active-permanent"),
                        new("1"),
                        ObservationValue.FromString("permanent"))
                ]));

        Assert.Equal(MaterializationItemOutcomeDisposition.RetryableRejected, result.Outcomes[0].Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.PermanentFailure, result.Outcomes[1].Disposition);
        var active = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), prepared.GenerationId));
        Assert.Equal(MaterializationGenerationState.Active, active.State);
        Assert.True(active.HasPermanentFailures);
        Assert.Equal(1, active.PendingRetryableMutationCount);
    }

    [Fact]
    public async Task ApplyBatch_RejectsCanonicalPayloadAboveEffectiveByteLimit()
    {
        var target = Target(maximumWriteBytes: 1);
        var generationId = new MaterializationGenerationId("generation/byte-limit");
        await Begin(target, generationId, Epoch, FenceOne);
        MaterializationApplyBatchRequest request = new(
            new("batch/byte-limit"),
            generationId,
            FenceOne,
            [
                new MaterializationUpsert(
                    new("item/byte-limit"),
                    new("mutation/byte-limit"),
                    new("1"),
                    ObservationValue.FromString("payload"))
            ]);

        var rejected = await target.ApplyBatchAsync(OperationContext.Create(), request);

        Assert.Equal(MaterializationBatchDisposition.LimitExceeded, rejected.Disposition);
        Assert.All(
            rejected.Outcomes,
            static outcome => Assert.Equal(
                MaterializationItemOutcomeDisposition.RetryableRejected,
                outcome.Disposition));
        var generation = await target.InspectGenerationAsync(
            OperationContext.Create(),
            generationId);
        Assert.Equal(MaterializationGenerationRevision.Initial, generation!.Revision);
        Assert.Equal(0, generation.VisibleItemCount);
    }

    [Fact]
    public async Task ApplyBatch_UsesOnlyOperationApplicableBulkLimits()
    {
        var target = Target(maximumWriteBytes: 1_000_000, maximumDeleteWriteBytes: 1);
        var generationId = new MaterializationGenerationId("generation/applicable-limit");
        await Begin(target, generationId, Epoch, FenceOne);

        var upserted = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/applicable-upsert"),
                generationId,
                FenceOne,
                [
                    new MaterializationUpsert(
                        new("item/applicable-limit"),
                        new("mutation/applicable-upsert"),
                        new("1"),
                        ObservationValue.FromString("payload"))
                ]));
        var deleted = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/applicable-delete"),
                generationId,
                FenceOne,
                [
                    new MaterializationDelete(
                        new("item/applicable-limit"),
                        new("mutation/applicable-delete"),
                        new("2"))
                ]));

        Assert.Equal(MaterializationBatchDisposition.Applied, upserted.Disposition);
        Assert.Equal(MaterializationBatchDisposition.LimitExceeded, deleted.Disposition);
    }

    [Fact]
    public async Task ApplyBatch_UsesOneSufficientRealizationPerRequiredCapability()
    {
        var descriptor = Descriptor();
        MaterializationCapabilityEvidence constrainedAlternative = new(
            new("upsert/constrained-alternative"),
            MaterializationCapabilityKind.TargetBulkUpsert,
            MaterializationCapabilityRealizationKind.Constrained,
            [
                MaterializationGuaranteeKind.FencedMutation,
                MaterializationGuaranteeKind.IdempotentWrite,
                MaterializationGuaranteeKind.VersionConditionalWrite
            ],
            [
                new(MaterializationLimitKind.WriteItems, 1),
                new(MaterializationLimitKind.WriteBytes, 1)
            ],
            ["tests/constrained-alternative"]);
        MaterializationCapabilityProfile profile = new(
            new("profile/target-multiple-realizations-v1"),
            MaterializationEndpointRole.Target,
            descriptor.Id.Value,
            [.. descriptor.Capabilities.Evidence, constrainedAlternative]);
        var target = new InMemoryMaterializationTarget(new(descriptor.Id, DefinitionId, profile));
        var generationId = new MaterializationGenerationId("generation/multiple-realizations");
        await Begin(target, generationId, Epoch, FenceOne);

        var applied = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/multiple-realizations"),
                generationId,
                FenceOne,
                [
                    new MaterializationUpsert(
                        new("item/multiple-a"),
                        new("mutation/multiple-a"),
                        new("1"),
                        ObservationValue.FromString("a")),
                    new MaterializationUpsert(
                        new("item/multiple-b"),
                        new("mutation/multiple-b"),
                        new("1"),
                        ObservationValue.FromString("b"))
                ]));

        Assert.Equal(MaterializationBatchDisposition.Applied, applied.Disposition);
        Assert.All(
            applied.Outcomes,
            static outcome => Assert.Equal(MaterializationItemOutcomeDisposition.Applied, outcome.Disposition));
    }

    [Fact]
    public async Task Promotion_DisplacesPriorActiveToInactiveUntilExplicitRetirement()
    {
        var target = Target();
        var first = await PrepareValidated(target, "first", Epoch, FenceOne);
        var second = await PrepareValidated(target, "second", Epoch.AddHours(1), FenceOne);
        var firstRequest = Promotion(
            "promotion/first",
            first,
            expectedActiveGenerationId: null,
            MaterializationTargetRevision.Initial,
            FenceOne,
            PromotionFenceOne,
            Epoch.AddHours(2));

        var promotedFirst = await target.PromoteGenerationAsync(OperationContext.Create(), firstRequest);
        var replayedFirst = await target.PromoteGenerationAsync(OperationContext.Create(), firstRequest);
        var conflictingTakeover = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/first",
                first,
                first.GenerationId,
                promotedFirst.Receipt!.TargetRevision,
                FenceTwo,
                PromotionFenceTwo,
                Epoch.AddHours(2)));
        var conflictedGeneration = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), first.GenerationId));
        var promotedSecond = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/second",
                second,
                first.GenerationId,
                promotedFirst.Receipt!.TargetRevision,
                FenceOne,
                new("3"),
                Epoch.AddHours(3)));
        var lateReplay = await target.PromoteGenerationAsync(OperationContext.Create(), firstRequest);
        var takeoverReplay = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/first",
                first,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                new("4"),
                new("4"),
                Epoch.AddHours(2)));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, promotedFirst.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayedFirst.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.IdentityConflict, conflictingTakeover.Disposition);
        Assert.Equal(PromotionFenceTwo, conflictingTakeover.Snapshot.LatestPromotionFence);
        Assert.Equal(FenceTwo, conflictedGeneration.LatestWorkerFence);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, promotedSecond.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, lateReplay.Disposition);
        Assert.Equal(promotedFirst.Receipt, lateReplay.Receipt);
        Assert.Equal(second.GenerationId, lateReplay.Snapshot.ActiveGenerationId);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, takeoverReplay.Disposition);
        Assert.Equal(second.GenerationId, takeoverReplay.Snapshot.ActiveGenerationId);
        Assert.Equal(new MaterializationPromotionFence("4"), takeoverReplay.Snapshot.LatestPromotionFence);
        Assert.Equal(
            new MaterializationWorkerFence("4"),
            (await target.InspectGenerationAsync(OperationContext.Create(), first.GenerationId))!.LatestWorkerFence);
        Assert.Equal(first.GenerationId, promotedSecond.Receipt!.PreviousGenerationId);
        Assert.Equal(FenceOne, promotedSecond.Receipt.GenerationWorkerFence);
        Assert.Equal(new MaterializationPromotionFence("3"), promotedSecond.Receipt.PromotionFence);
        Assert.Equal(second.GenerationId, promotedSecond.Snapshot.ActiveGenerationId);
        Assert.Equal(new MaterializationPromotionFence("3"), promotedSecond.Snapshot.LatestPromotionFence);

        var inactive = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), first.GenerationId));
        var active = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), second.GenerationId));
        Assert.Equal(MaterializationGenerationState.Inactive, inactive.State);
        Assert.Null(inactive.RetiredAtUtc);
        Assert.Equal(MaterializationGenerationState.Active, active.State);
        Assert.Equal(FenceOne, active.LatestWorkerFence);

        var retired = await target.RetireGenerationAsync(
            OperationContext.Create(),
            new(
                new("retirement/first"),
                first.GenerationId,
                inactive.Revision,
                new("5"),
                Epoch.AddHours(4)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, retired.Disposition);
        Assert.Equal(MaterializationGenerationState.Retired, retired.Generation!.State);
    }

    [Fact]
    public async Task PromotionAndRetirement_RejectBackdatedPointerLifecycleBoundaries()
    {
        var target = Target();
        var first = await PrepareValidated(target, "chronology-first", Epoch, FenceOne);
        var second = await PrepareValidated(target, "chronology-second", Epoch.AddMinutes(1), FenceOne);
        var firstValidated = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), first.GenerationId));
        await Assert.ThrowsAsync<ArgumentException>(async () => await target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                new("validation/chronology-backdated"),
                first.GenerationId,
                firstValidated.Revision,
                firstValidated.SealReceipt!.Fingerprint,
                expectedVisibleItemCount: 1,
                "tests/validator-v1",
                FenceOne,
                firstValidated.ValidationReceipt!.ValidatedAtUtc.AddTicks(-1))));
        var firstPromotion = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/chronology-first",
                first,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                FenceOne,
                PromotionFenceOne,
                Epoch.AddHours(10)));

        await Assert.ThrowsAsync<ArgumentException>(async () => await target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/chronology-second-backdated",
                second,
                first.GenerationId,
                firstPromotion.Receipt!.TargetRevision,
                FenceOne,
                PromotionFenceTwo,
                Epoch.AddHours(5))));

        var secondPromotion = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/chronology-second",
                second,
                first.GenerationId,
                firstPromotion.Receipt!.TargetRevision,
                FenceOne,
                PromotionFenceTwo,
                Epoch.AddHours(11)));
        var inactive = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), first.GenerationId));
        Assert.Equal(Epoch.AddHours(11), inactive.InactivatedAtUtc);

        await Assert.ThrowsAsync<ArgumentException>(async () => await target.RetireGenerationAsync(
            OperationContext.Create(),
            new(
                new("retirement/chronology-backdated"),
                first.GenerationId,
                inactive.Revision,
                FenceTwo,
                Epoch.AddHours(10))));
        Assert.Equal(second.GenerationId, secondPromotion.Snapshot.ActiveGenerationId);
        Assert.Equal(MaterializationGenerationState.Inactive, inactive.State);
    }

    [Fact]
    public async Task Promotion_AdvancesIndependentFenceScopesEvenWhenTheOtherScopeRejects()
    {
        var target = Target();
        var generationId = new MaterializationGenerationId("generation/independent-fences");
        await Begin(target, generationId, Epoch, FenceOne);

        var missing = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            new(
                new("promotion/missing-candidate"),
                new("generation/missing-candidate"),
                MaterializationGenerationRevision.Initial,
                new("validation/missing-candidate"),
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                FenceOne,
                PromotionFenceTwo,
                Epoch.AddMinutes(1)));
        var stalePointer = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            new(
                new("promotion/stale-pointer"),
                generationId,
                MaterializationGenerationRevision.Initial,
                new("validation/stale-pointer"),
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                FenceTwo,
                PromotionFenceOne,
                Epoch.AddMinutes(2)));
        var staleGeneration = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            new(
                new("promotion/stale-generation"),
                generationId,
                MaterializationGenerationRevision.Initial,
                new("validation/stale-generation"),
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                FenceOne,
                new("3"),
                Epoch.AddMinutes(3)));

        Assert.Equal(MaterializationTargetOperationDisposition.NotFound, missing.Disposition);
        Assert.Equal(PromotionFenceTwo, missing.Snapshot.LatestPromotionFence);
        Assert.Equal(MaterializationTargetOperationDisposition.StaleFence, stalePointer.Disposition);
        Assert.Equal(FenceTwo, (await target.InspectGenerationAsync(OperationContext.Create(), generationId))!.LatestWorkerFence);
        Assert.Equal(MaterializationTargetOperationDisposition.StaleFence, staleGeneration.Disposition);
        Assert.Equal(new MaterializationPromotionFence("3"), staleGeneration.Snapshot.LatestPromotionFence);
    }

    [Fact]
    public async Task ConcurrentPromotion_AdmitsExactlyOneCandidateAtTheSamePointerRevision()
    {
        var target = Target();
        var first = await PrepareValidated(target, "concurrent-first", Epoch, FenceOne);
        var second = await PrepareValidated(target, "concurrent-second", Epoch.AddHours(1), FenceOne);
        MaterializationPromoteGenerationRequest firstRequest = Promotion(
            "promotion/concurrent-first",
            first,
            expectedActiveGenerationId: null,
            MaterializationTargetRevision.Initial,
            FenceOne,
            PromotionFenceOne,
            Epoch.AddHours(2));
        MaterializationPromoteGenerationRequest secondRequest = Promotion(
            "promotion/concurrent-second",
            second,
            expectedActiveGenerationId: null,
            MaterializationTargetRevision.Initial,
            FenceOne,
            PromotionFenceTwo,
            Epoch.AddHours(2));

        var results = await Task.WhenAll(
            Task.Run(async () => await target.PromoteGenerationAsync(OperationContext.Create(), firstRequest)),
            Task.Run(async () => await target.PromoteGenerationAsync(OperationContext.Create(), secondRequest)));

        var applied = Assert.Single(results, result => result.Disposition == MaterializationTargetOperationDisposition.Applied);
        var rejected = Assert.Single(results, result => result.Disposition != MaterializationTargetOperationDisposition.Applied);
        Assert.Contains(
            rejected.Disposition,
            new[]
            {
                MaterializationTargetOperationDisposition.RevisionConflict,
                MaterializationTargetOperationDisposition.StaleFence
            });
        Assert.Equal(applied.Receipt!.GenerationId, (await target.InspectAsync(OperationContext.Create())).ActiveGenerationId);
        var firstState = (await target.InspectGenerationAsync(OperationContext.Create(), first.GenerationId))!.State;
        var secondState = (await target.InspectGenerationAsync(OperationContext.Create(), second.GenerationId))!.State;
        Assert.Single(new[] { firstState, secondState }, state => state == MaterializationGenerationState.Active);
    }

    [Fact]
    public async Task GenerationFences_AreIndependentAcrossActiveAndCandidateScopes()
    {
        var target = Target();
        var highFenceGeneration = new MaterializationGenerationId("generation/high-fence");
        var lowFenceGeneration = new MaterializationGenerationId("generation/low-fence");
        await Begin(target, highFenceGeneration, Epoch, new("10"));
        await Begin(target, lowFenceGeneration, Epoch, FenceOne);

        var lowApplied = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/low-independent"),
                lowFenceGeneration,
                FenceTwo,
                [new MaterializationDelete(new("item/low"), new("mutation/low"), new("1"))]));
        var highStale = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/high-stale"),
                highFenceGeneration,
                new("9"),
                [new MaterializationDelete(new("item/high"), new("mutation/high"), new("1"))]));

        Assert.Equal(MaterializationBatchDisposition.Applied, lowApplied.Disposition);
        Assert.Equal(MaterializationBatchDisposition.StaleFence, highStale.Disposition);
        Assert.Equal(new MaterializationWorkerFence("10"), (await target.InspectGenerationAsync(OperationContext.Create(), highFenceGeneration))!.LatestWorkerFence);
        Assert.Equal(FenceTwo, (await target.InspectGenerationAsync(OperationContext.Create(), lowFenceGeneration))!.LatestWorkerFence);
        Assert.Null((await target.InspectAsync(OperationContext.Create())).LatestPromotionFence);
    }

    [Fact]
    public async Task Cleanup_PermanentlyTombstonesGenerationIdentityAndRemainsIdempotent()
    {
        var target = Target();
        var generationId = new MaterializationGenerationId("generation/tombstoned");
        var begun = await Begin(target, generationId, Epoch, FenceOne);
        var retired = await target.RetireGenerationAsync(
            OperationContext.Create(),
            new(new("retirement/tombstoned"), generationId, begun.Revision, FenceTwo, Epoch.AddMinutes(1)));
        MaterializationCleanupGenerationRequest cleanup = new(
            new("cleanup/tombstoned"),
            generationId,
            retired.Generation!.Revision,
            new("3"),
            Epoch.AddMinutes(2));

        var cleaned = await target.CleanupGenerationAsync(OperationContext.Create(), cleanup);
        var replayed = await target.CleanupGenerationAsync(OperationContext.Create(), cleanup);
        var reusedSameIntent = await target.BeginGenerationAsync(
            OperationContext.Create(),
            new(DefinitionId, generationId, DefinitionFingerprint, new("4"), Epoch));
        var reusedDifferentIntent = await target.BeginGenerationAsync(
            OperationContext.Create(),
            new(
                DefinitionId,
                generationId,
                new("sha256", "cohesive-materialization-definition/v1-c14n/v1", "replacement"),
                new("5"),
                Epoch));

        Assert.True(cleaned.WasRemoved);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.AlreadyExists, reusedSameIntent.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.IdentityConflict, reusedDifferentIntent.Disposition);
        Assert.Null(await target.InspectGenerationAsync(OperationContext.Create(), generationId));
        Assert.Equal(0, (await target.InspectAsync(OperationContext.Create())).RetainedGenerationCount);
    }

    [Fact]
    public async Task FailedOrUnvalidatedGeneration_CannotBecomeActive()
    {
        var generationId = new MaterializationGenerationId("generation/failed-validation");
        var target = Target(new InMemoryMaterializationTargetFaultPlan(validationFailures: [generationId]));
        var begun = await Begin(target, generationId, Epoch, FenceOne);
        var loadingPromotion = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            new(
                new("promotion/loading"),
                generationId,
                begun.Revision,
                new("sha256-v1:not-validated"),
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                FenceOne,
                PromotionFenceOne,
                Epoch.AddMinutes(1)));
        var written = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/validation"),
                generationId,
                FenceOne,
                [new MaterializationUpsert(new("item"), new("mutation"), new("1"), ObservationValue.FromString("value"))]));
        var sealedResult = await target.SealGenerationAsync(
            OperationContext.Create(),
            new(new("seal/validation"), generationId, written.GenerationRevision!.Value, FenceOne, Epoch.AddMinutes(2)));
        var failed = await target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                new("validation/failure"),
                generationId,
                sealedResult.Generation!.Revision,
                sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: 1,
                "tests/validator-v1",
                FenceOne,
                Epoch.AddMinutes(3)));
        var failedPromotion = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            new(
                new("promotion/failed"),
                generationId,
                failed.Generation!.Revision,
                failed.Receipt!.Fingerprint,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                FenceTwo,
                PromotionFenceTwo,
                Epoch.AddMinutes(4)));

        Assert.Equal(MaterializationTargetOperationDisposition.StateConflict, loadingPromotion.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.ValidationFailed, failed.Disposition);
        Assert.False(failed.Receipt.Validation.IsValid);
        Assert.Equal(MaterializationTargetOperationDisposition.StateConflict, failedPromotion.Disposition);
        Assert.Null(failedPromotion.Snapshot.ActiveGenerationId);
        Assert.Equal(PromotionFenceTwo, failedPromotion.Snapshot.LatestPromotionFence);
    }

    [Fact]
    public async Task OrdinarySnapshotsAreBounded_AndFakeInspectionPagesItems()
    {
        var target = Target();
        var generationId = new MaterializationGenerationId("generation/bounded-inspection");
        await Begin(target, generationId, Epoch, FenceOne);
        await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/bounded-one"),
                generationId,
                FenceOne,
                [
                    new MaterializationUpsert(new("item-b"), new("mutation-b"), new("1"), ObservationValue.FromString("b")),
                    new MaterializationUpsert(new("item-a"), new("mutation-a"), new("1"), ObservationValue.FromString("a"))
                ]));
        await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/bounded-two"),
                generationId,
                FenceOne,
                [new MaterializationDelete(new("item-c"), new("mutation-c"), new("1"))]));

        var targetSnapshot = await target.InspectAsync(OperationContext.Create());
        var generation = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), generationId));
        var first = Assert.IsType<InMemoryMaterializationTargetItemPage>(
            await target.InspectItemsAsync(OperationContext.Create(), generationId, afterItemId: null, maximumItems: 2));
        var second = Assert.IsType<InMemoryMaterializationTargetItemPage>(
            await target.InspectItemsAsync(OperationContext.Create(), generationId, first.NextAfterItemId, maximumItems: 2));

        Assert.Equal(1, targetSnapshot.RetainedGenerationCount);
        Assert.Equal(2, generation.VisibleItemCount);
        Assert.Equal(1, generation.TombstoneCount);
        Assert.Equal(["item-a", "item-b"], first.Items.Select(static item => item.ItemId.Value));
        Assert.Equal(new MaterializationItemId("item-b"), first.NextAfterItemId);
        Assert.Equal("item-c", Assert.Single(second.Items).ItemId.Value);
        Assert.Null(Assert.Single(second.Items).Value);
        Assert.Null(second.NextAfterItemId);
        Assert.Null(typeof(MaterializationTargetSnapshot).GetProperty("Generations"));
        Assert.Null(typeof(MaterializationGenerationSnapshot).GetProperty("Items"));
    }

    [Fact]
    public void BatchResultFactory_RejectsMissingReorderedOrSubstitutedOutcomes()
    {
        MaterializationApplyBatchRequest request = new(
            new("batch/correspondence"),
            new("generation/correspondence"),
            FenceOne,
            [
                new MaterializationDelete(new("item-a"), new("mutation-a"), new("1")),
                new MaterializationDelete(new("item-b"), new("mutation-b"), new("1"))
            ]);
        ImmutableArray<MaterializationItemOutcome> valid =
        [
            new(new("item-a"), new("mutation-a"), MaterializationItemOutcomeDisposition.Applied),
            new(new("item-b"), new("mutation-b"), MaterializationItemOutcomeDisposition.Applied)
        ];

        var result = MaterializationBatchResult.ForRequest(
            request,
            MaterializationBatchDisposition.Applied,
            MaterializationGenerationRevision.Initial,
            valid);

        result.ValidateAgainst(request);
        Assert.Throws<ArgumentException>(() => MaterializationBatchResult.ForRequest(
            request,
            MaterializationBatchDisposition.Applied,
            MaterializationGenerationRevision.Initial,
            [valid[0]]));
        Assert.Throws<ArgumentException>(() => MaterializationBatchResult.ForRequest(
            request,
            MaterializationBatchDisposition.Applied,
            MaterializationGenerationRevision.Initial,
            [valid[1], valid[0]]));
        Assert.Throws<ArgumentException>(() => MaterializationBatchResult.ForRequest(
            request,
            MaterializationBatchDisposition.Applied,
            MaterializationGenerationRevision.Initial,
            [valid[0], new(new("item-b"), new("mutation/substituted"), MaterializationItemOutcomeDisposition.Applied)]));
        Assert.Throws<ArgumentException>(() => MaterializationBatchResult.ForRequest(
            request,
            MaterializationBatchDisposition.StaleFence,
            MaterializationGenerationRevision.Initial,
            valid));
        Assert.Throws<ArgumentException>(() => MaterializationBatchResult.ForRequest(
            request,
            MaterializationBatchDisposition.IdentityConflict,
            MaterializationGenerationRevision.Initial,
            [
                new(new("item-a"), new("mutation-a"), MaterializationItemOutcomeDisposition.RetryableRejected, "stale", "Stale."),
                new(new("item-b"), new("mutation-b"), MaterializationItemOutcomeDisposition.RetryableRejected, "stale", "Stale.")
            ]));
    }

    [Fact]
    public void MutationWire_UsesSubtypeAsAuthorityAndStringEncodesOperationalEnums()
    {
        MaterializationApplyBatchRequest request = new(
            new("batch/portable"),
            new("generation/portable"),
            FenceOne,
            [
                new MaterializationUpsert(
                    new("item/upsert"),
                    new("mutation/upsert"),
                    new("1"),
                    ObservationValue.FromString("portable")),
                new MaterializationDelete(
                    new("item/delete"),
                    new("mutation/delete"),
                    new("2"))
            ]);
        var options = StrictDocumentJson.CreateOptions();

        var json = JsonSerializer.Serialize(request, options);
        var restored = JsonSerializer.Deserialize<MaterializationApplyBatchRequest>(json, options);

        Assert.Contains("\"$mutation\":\"upsert\"", json);
        Assert.Contains("\"$mutation\":\"delete\"", json);
        Assert.DoesNotContain("\"kind\"", json.ToLowerInvariant());
        var upsert = Assert.IsType<MaterializationUpsert>(restored!.Mutations[0]);
        Assert.Equal(MaterializationItemMutationKind.Upsert, upsert.Kind);
        Assert.Equal("portable", upsert.Value.String);
        Assert.Equal(MaterializationItemMutationKind.Delete, Assert.IsType<MaterializationDelete>(restored.Mutations[1]).Kind);
        Assert.Equal("\"StaleFence\"", JsonSerializer.Serialize(MaterializationBatchDisposition.StaleFence, options));
        Assert.Equal("\"Inactive\"", JsonSerializer.Serialize(MaterializationGenerationState.Inactive, options));
    }

    [Fact]
    public void PublicReceiptsSnapshotsAndResults_RejectContradictoryStates()
    {
        Assert.Throws<ArgumentException>(() => new MaterializationApplyBatchRequest(
            new("batch/duplicate-mutation"),
            new("generation/duplicate-mutation"),
            FenceOne,
            [
                new MaterializationDelete(new("item/duplicate-a"), new("mutation/duplicate"), new("1")),
                new MaterializationDelete(new("item/duplicate-b"), new("mutation/duplicate"), new("1"))
            ]));
        Assert.ThrowsAny<ArgumentException>(() => new MaterializationSealGenerationRequest(
            default,
            new("generation/invalid"),
            MaterializationGenerationRevision.Initial,
            FenceOne,
            Epoch));
        Assert.ThrowsAny<ArgumentException>(() => new MaterializationSealReceipt(
            default,
            new("generation/invalid"),
            MaterializationGenerationRevision.Initial,
            visibleItemCount: 0,
            new("seal/fingerprint"),
            Epoch));
        Assert.Throws<ArgumentException>(() => new MaterializationGenerationSnapshot(
            DefinitionId,
            new("generation/invalid"),
            DefinitionFingerprint,
            MaterializationGenerationState.Active,
            MaterializationGenerationRevision.Initial,
            FenceOne,
            hasPermanentFailures: false,
            pendingRetryableMutationCount: 0,
            visibleItemCount: 0,
            tombstoneCount: 0,
            sealReceipt: null,
            validationReceipt: null,
            Epoch,
            inactivatedAtUtc: null,
            retiredAtUtc: null));
        Assert.Throws<ArgumentException>(() => new MaterializationGenerationSnapshot(
            DefinitionId,
            new("generation/retired-without-promotion-evidence"),
            DefinitionFingerprint,
            MaterializationGenerationState.Retired,
            new("2"),
            FenceOne,
            hasPermanentFailures: false,
            pendingRetryableMutationCount: 0,
            visibleItemCount: 0,
            tombstoneCount: 0,
            sealReceipt: null,
            validationReceipt: null,
            Epoch,
            inactivatedAtUtc: Epoch.AddMinutes(1),
            retiredAtUtc: Epoch.AddMinutes(2)));
        Assert.Throws<ArgumentException>(() => new MaterializationTargetSnapshot(
            new("target/invalid"),
            DefinitionId,
            MaterializationTargetRevision.Initial,
            new MaterializationGenerationId("generation/active"),
            PromotionFenceOne,
            retainedGenerationCount: 1));
        Assert.Throws<ArgumentException>(() => new MaterializationSealResult(
            MaterializationTargetOperationDisposition.Applied,
            generation: null,
            receipt: null));
        MaterializationTargetSnapshot emptyTarget = new(
            new("target/empty"),
            DefinitionId,
            MaterializationTargetRevision.Initial,
            activeGenerationId: null,
            latestPromotionFence: null,
            retainedGenerationCount: 0);
        Assert.Throws<ArgumentException>(() => new MaterializationCleanupResult(
            MaterializationTargetOperationDisposition.Replayed,
            emptyTarget,
            wasRemoved: true));

        var correlatedGenerationId = new MaterializationGenerationId("generation/correlated");
        MaterializationSealReceipt retainedSeal = new(
            new("seal/correlated"),
            correlatedGenerationId,
            new("2"),
            visibleItemCount: 0,
            new("seal/correlated-fingerprint"),
            Epoch.AddMinutes(1));
        MaterializationGenerationSnapshot sealedGeneration = new(
            DefinitionId,
            correlatedGenerationId,
            DefinitionFingerprint,
            MaterializationGenerationState.Sealed,
            new("2"),
            FenceOne,
            hasPermanentFailures: false,
            pendingRetryableMutationCount: 0,
            visibleItemCount: 0,
            tombstoneCount: 0,
            retainedSeal,
            validationReceipt: null,
            Epoch,
            inactivatedAtUtc: null,
            retiredAtUtc: null);
        MaterializationSealReceipt unrelatedSeal = new(
            new("seal/unrelated"),
            correlatedGenerationId,
            new("2"),
            visibleItemCount: 0,
            new("seal/unrelated-fingerprint"),
            Epoch.AddMinutes(1));
        Assert.Throws<ArgumentException>(() => new MaterializationSealResult(
            MaterializationTargetOperationDisposition.Applied,
            sealedGeneration,
            unrelatedSeal));

        MaterializationValidationReceipt retainedValidation = new(
            new("validation/correlated"),
            correlatedGenerationId,
            new("3"),
            retainedSeal.Fingerprint,
            new("validation/correlated-fingerprint"),
            DocumentValidationResult.Valid,
            Epoch.AddMinutes(2));
        MaterializationGenerationSnapshot validatedGeneration = new(
            DefinitionId,
            correlatedGenerationId,
            DefinitionFingerprint,
            MaterializationGenerationState.Validated,
            new("3"),
            FenceOne,
            hasPermanentFailures: false,
            pendingRetryableMutationCount: 0,
            visibleItemCount: 0,
            tombstoneCount: 0,
            retainedSeal,
            retainedValidation,
            Epoch,
            inactivatedAtUtc: null,
            retiredAtUtc: null);
        MaterializationValidationReceipt unrelatedValidation = new(
            new("validation/unrelated"),
            correlatedGenerationId,
            new("3"),
            retainedSeal.Fingerprint,
            new("validation/unrelated-fingerprint"),
            DocumentValidationResult.Valid,
            Epoch.AddMinutes(2));
        Assert.Throws<ArgumentException>(() => new MaterializationValidationResult(
            MaterializationTargetOperationDisposition.Applied,
            validatedGeneration,
            unrelatedValidation));

        MaterializationValidationReceipt nonAdvancingValidation = new(
            new("validation/non-advancing"),
            correlatedGenerationId,
            MaterializationGenerationRevision.Initial,
            retainedSeal.Fingerprint,
            new("validation/non-advancing-fingerprint"),
            DocumentValidationResult.Valid,
            Epoch.AddMinutes(2));
        Assert.Throws<ArgumentException>(() => new MaterializationGenerationSnapshot(
            DefinitionId,
            correlatedGenerationId,
            DefinitionFingerprint,
            MaterializationGenerationState.Validated,
            new("2"),
            FenceOne,
            hasPermanentFailures: false,
            pendingRetryableMutationCount: 0,
            visibleItemCount: 0,
            tombstoneCount: 0,
            retainedSeal,
            nonAdvancingValidation,
            Epoch,
            inactivatedAtUtc: null,
            retiredAtUtc: null));

        MaterializationPromotionReceipt promotionReceipt = new(
            new("promotion/correlated"),
            emptyTarget.TargetId,
            correlatedGenerationId,
            previousGenerationId: null,
            new("1"),
            FenceOne,
            PromotionFenceOne,
            retainedValidation.Fingerprint,
            Epoch.AddMinutes(3));
        Assert.Throws<ArgumentException>(() => new MaterializationPromotionResult(
            MaterializationTargetOperationDisposition.Replayed,
            emptyTarget,
            promotionReceipt));
        MaterializationTargetSnapshot wrongAppliedFence = new(
            emptyTarget.TargetId,
            DefinitionId,
            new("1"),
            correlatedGenerationId,
            PromotionFenceTwo,
            retainedGenerationCount: 1);
        Assert.Throws<ArgumentException>(() => new MaterializationPromotionResult(
            MaterializationTargetOperationDisposition.Applied,
            wrongAppliedFence,
            promotionReceipt));

        MaterializationValidationReceipt normalized = new(
            new("validation/normalized"),
            new("generation/normalized"),
            MaterializationGenerationRevision.Initial,
            new("seal/normalized"),
            new("validation/fingerprint"),
            new DocumentValidationResult(
            [
                CompleteDiagnostic("z-diagnostic", "z"),
                CompleteDiagnostic("a-diagnostic", "a")
            ]),
            Epoch);
        Assert.Equal(
            ["a-diagnostic", "z-diagnostic"],
            normalized.Validation.Diagnostics.Select(static diagnostic => diagnostic.Code));
        Assert.Throws<ArgumentException>(() => new MaterializationValidationReceipt(
            new("validation/null-diagnostic"),
            new("generation/normalized"),
            MaterializationGenerationRevision.Initial,
            new("seal/normalized"),
            new("validation/null-fingerprint"),
            new DocumentValidationResult([null!]),
            Epoch));
        Assert.Throws<ArgumentException>(() => new MaterializationValidationReceipt(
            new("validation/incomplete-diagnostic"),
            new("generation/normalized"),
            MaterializationGenerationRevision.Initial,
            new("seal/normalized"),
            new("validation/incomplete-fingerprint"),
            new DocumentValidationResult(
            [
                new("incomplete", DiagnosticSeverity.Error, "missing normative evidence", "/validation")
            ]),
            Epoch));
        Assert.Empty(new InMemoryMaterializationTargetItemPage(
            new("generation/normalized-page"),
            items: default,
            nextAfterItemId: null).Items);
    }

    [Fact]
    public async Task TargetOperations_HonorPreCanceledContext()
    {
        var target = Target();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        var context = OperationContext.Create(cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await target.InspectAsync(context));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await target.BeginGenerationAsync(
            context,
            new(DefinitionId, new("generation/canceled"), DefinitionFingerprint, FenceOne, Epoch)));
    }

    static InMemoryMaterializationTarget Target(
        InMemoryMaterializationTargetFaultPlan? faultPlan = null,
        long maximumWriteBytes = 1_000_000,
        long? maximumDeleteWriteBytes = null) =>
        new(Descriptor(maximumWriteBytes, maximumDeleteWriteBytes ?? maximumWriteBytes), faultPlan);

    static MaterializationTargetDescriptor Descriptor(
        long maximumWriteBytes = 1_000_000,
        long maximumDeleteWriteBytes = 1_000_000)
    {
        MaterializationCapabilityEvidence Evidence(
            string id,
            MaterializationCapabilityKind capability,
            ImmutableArray<MaterializationGuaranteeKind> guarantees = default,
            ImmutableArray<MaterializationOperatingLimit> limits = default) =>
            new(
                new(id),
                capability,
                MaterializationCapabilityRealizationKind.Native,
                guarantees.IsDefault ? [] : guarantees,
                limits.IsDefault ? [] : limits,
                ["cohesive.storage.in-memory/v1"]);

        var targetId = new MaterializationTargetId("target/search");
        MaterializationCapabilityProfile profile = new(
            new("profile/target-search-v1"),
            MaterializationEndpointRole.Target,
            targetId.Value,
            [
                Evidence("cleanup", MaterializationCapabilityKind.TargetCleanup, [MaterializationGuaranteeKind.FencedMutation]),
                Evidence(
                    "delete",
                    MaterializationCapabilityKind.TargetBulkDelete,
                    [
                        MaterializationGuaranteeKind.FencedMutation,
                        MaterializationGuaranteeKind.IdempotentWrite,
                        MaterializationGuaranteeKind.VersionConditionalWrite
                    ],
                    [
                        new(MaterializationLimitKind.WriteItems, 2),
                        new(MaterializationLimitKind.WriteBytes, maximumDeleteWriteBytes)
                    ]),
                Evidence(
                    "isolation",
                    MaterializationCapabilityKind.TargetGenerationIsolation,
                    [MaterializationGuaranteeKind.FencedMutation, MaterializationGuaranteeKind.GenerationIsolation]),
                Evidence(
                    "outcomes",
                    MaterializationCapabilityKind.TargetPerItemOutcomes,
                    [MaterializationGuaranteeKind.ExactPerItemOutcome],
                    [
                        new(MaterializationLimitKind.WriteItems, 2),
                        new(MaterializationLimitKind.WriteBytes, maximumWriteBytes)
                    ]),
                Evidence("promotion", MaterializationCapabilityKind.TargetFencedPromotion, [MaterializationGuaranteeKind.AtomicPromotion, MaterializationGuaranteeKind.FencedPromotion]),
                Evidence("retirement", MaterializationCapabilityKind.TargetRetirement, [MaterializationGuaranteeKind.FencedMutation]),
                Evidence("seal", MaterializationCapabilityKind.TargetSeal, [MaterializationGuaranteeKind.FencedMutation]),
                Evidence(
                    "upsert",
                    MaterializationCapabilityKind.TargetBulkUpsert,
                    [
                        MaterializationGuaranteeKind.FencedMutation,
                        MaterializationGuaranteeKind.IdempotentWrite,
                        MaterializationGuaranteeKind.VersionConditionalWrite
                    ],
                    [
                        new(MaterializationLimitKind.WriteItems, 2),
                        new(MaterializationLimitKind.WriteBytes, maximumWriteBytes)
                    ]),
                Evidence("validation", MaterializationCapabilityKind.TargetValidation, [MaterializationGuaranteeKind.FencedMutation])
            ]);
        return new(targetId, DefinitionId, profile);
    }

    static DocumentValidationDiagnostic CompleteDiagnostic(string code, string message) =>
        new(
            code,
            DiagnosticSeverity.Warning,
            message,
            "/validation",
            Evidence: new(
                stage: "materialization-target-validation",
                subject: "generation/normalized",
                sourceReferences: ["tests/validator-v1"],
                expected: "valid generation",
                observed: message));

    static async Task<MaterializationGenerationSnapshot> Begin(
        InMemoryMaterializationTarget target,
        MaterializationGenerationId generationId,
        DateTimeOffset createdAtUtc,
        MaterializationWorkerFence workerFence)
    {
        var result = await target.BeginGenerationAsync(
            OperationContext.Create(),
            new(DefinitionId, generationId, DefinitionFingerprint, workerFence, createdAtUtc));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, result.Disposition);
        return Assert.IsType<MaterializationGenerationSnapshot>(result.Generation);
    }

    static async Task<PreparedGeneration> PrepareValidated(
        InMemoryMaterializationTarget target,
        string suffix,
        DateTimeOffset createdAtUtc,
        MaterializationWorkerFence workerFence)
    {
        var generationId = new MaterializationGenerationId($"generation/{suffix}");
        var itemId = new MaterializationItemId($"item/{suffix}");
        await Begin(target, generationId, createdAtUtc, workerFence);
        var written = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new($"batch/{suffix}"),
                generationId,
                workerFence,
                [new MaterializationUpsert(itemId, new($"mutation/{suffix}"), new("1"), ObservationValue.FromString(suffix))]));
        var sealedResult = await target.SealGenerationAsync(
            OperationContext.Create(),
            new(
                new($"seal/{suffix}"),
                generationId,
                written.GenerationRevision!.Value,
                workerFence,
                createdAtUtc.AddMinutes(1)));
        var validated = await target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                new($"validation/{suffix}"),
                generationId,
                sealedResult.Generation!.Revision,
                sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: 1,
                "tests/validator-v1",
                workerFence,
                createdAtUtc.AddMinutes(2)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, validated.Disposition);
        return new(generationId, itemId, validated.Generation!.Revision, validated.Receipt!.Fingerprint);
    }

    static MaterializationPromoteGenerationRequest Promotion(
        string id,
        PreparedGeneration generation,
        MaterializationGenerationId? expectedActiveGenerationId,
        MaterializationTargetRevision expectedTargetRevision,
        MaterializationWorkerFence generationWorkerFence,
        MaterializationPromotionFence promotionFence,
        DateTimeOffset promotedAtUtc) =>
        new(
            new(id),
            generation.GenerationId,
            generation.Revision,
            generation.ValidationFingerprint,
            expectedActiveGenerationId,
            expectedTargetRevision,
            generationWorkerFence,
            promotionFence,
            promotedAtUtc);

    sealed record PreparedGeneration(
        MaterializationGenerationId GenerationId,
        MaterializationItemId ItemId,
        MaterializationGenerationRevision Revision,
        MaterializationValidationFingerprint ValidationFingerprint);
}
