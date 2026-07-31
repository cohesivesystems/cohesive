using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Adapters.Elastic;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using global::Elastic.Clients.Elasticsearch;
using global::Elastic.Transport;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticMaterializationTargetTests
{
    static readonly DateTimeOffset Epoch = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    static readonly MaterializationId DefinitionId = new("materialization/search");
    static readonly ExecutionDefinitionFingerprint DefinitionFingerprint = new(
        "sha256",
        "cohesive-materialization-definition/v1-c14n/v1",
        "0123456789abcdef");
    static readonly MaterializationWorkerFence WorkerFence = new("1");

    [Fact]
    public async Task Candidate_RemainsInvisibleUntilValidatedPromotionPublishesOneFilteredReadAlias()
    {
        var rig = CreateRig();
        var prepared = await PrepareValidatedAsync(rig, "candidate", Epoch);

        var beforePromotion = await InspectReadAliasAsync(rig);
        var beforeSnapshot = await rig.Target.InspectAsync(OperationContext.Create());

        Assert.Empty(beforePromotion.Bindings);
        Assert.Null(beforeSnapshot.ActiveGenerationId);
        Assert.Equal(1, beforeSnapshot.RetainedGenerationCount);
        Assert.Equal(rig.Binding.ReadAlias, rig.Binding.SearchBinding.IndexName);

        var promoted = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/candidate",
                prepared,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                WorkerFence,
                new("1"),
                Epoch.AddMinutes(3)));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, promoted.Disposition);
        Assert.Equal(prepared.GenerationId, promoted.Snapshot.ActiveGenerationId);
        var published = Assert.Single((await InspectReadAliasAsync(rig)).Bindings);
        Assert.Equal(rig.Binding.ReadAlias, published.Alias);
        Assert.Equal(rig.Binding.GetGenerationIndexName(prepared.GenerationId), published.Index);
        Assert.False(published.IsWriteIndex);
        Assert.Equal(
            "{\"term\":{\"_cohesive.deleted\":false}}",
            Encoding.UTF8.GetString(published.Filter));

        var publication = Assert.Single(
            rig.Transport.AliasRequests,
            request => request.ReadAlias == rig.Binding.ReadAlias);
        Assert.Equal(published.Index, publication.NextReadIndex);
        Assert.Equal(rig.Binding.ControlIndexName, publication.MarkerIndex);
        var visibleThroughRelationsAlias = await rig.Transport.CountAsync(
            rig.Binding.SearchBinding.IndexName,
            JsonObject("{\"match_all\":{}}"u8.ToArray()),
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        Assert.Equal(1, visibleThroughRelationsAlias.Count);
    }

    [Fact]
    public async Task SecondPromotion_AtomicallySwapsReadGenerationAndLeavesPriorGenerationRetirable()
    {
        var rig = CreateRig();
        var first = await PrepareValidatedAsync(rig, "first-active", Epoch);
        var firstPromotion = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/first-active",
                first,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                WorkerFence,
                new("1"),
                Epoch.AddMinutes(3)));
        var secondEpoch = Epoch.AddMinutes(10);
        var second = await PrepareValidatedAsync(rig, "second-active", secondEpoch);
        var secondPromotedAt = secondEpoch.AddMinutes(3);

        var secondPromotion = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/second-active",
                second,
                first.GenerationId,
                firstPromotion.Snapshot.Revision,
                WorkerFence,
                new("2"),
                secondPromotedAt));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, secondPromotion.Disposition);
        Assert.Equal(second.GenerationId, secondPromotion.Snapshot.ActiveGenerationId);
        var published = Assert.Single((await InspectReadAliasAsync(rig)).Bindings);
        Assert.Equal(rig.Binding.GetGenerationIndexName(second.GenerationId), published.Index);
        var inactive = Assert.IsType<MaterializationGenerationSnapshot>(
            await rig.Target.InspectGenerationAsync(OperationContext.Create(), first.GenerationId));
        var active = Assert.IsType<MaterializationGenerationSnapshot>(
            await rig.Target.InspectGenerationAsync(OperationContext.Create(), second.GenerationId));
        Assert.Equal(MaterializationGenerationState.Inactive, inactive.State);
        Assert.Equal(secondPromotedAt, inactive.InactivatedAtUtc);
        Assert.Equal(MaterializationGenerationState.Active, active.State);

        var retired = await rig.Target.RetireGenerationAsync(
            OperationContext.Create(),
            new(
                new("retirement/first-active"),
                first.GenerationId,
                inactive.Revision,
                WorkerFence,
                secondPromotedAt.AddMinutes(1)));
        var cleaned = await rig.Target.CleanupGenerationAsync(
            OperationContext.Create(),
            new(
                new("cleanup/first-active"),
                first.GenerationId,
                retired.Generation!.Revision,
                WorkerFence,
                secondPromotedAt.AddMinutes(2)));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, retired.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, cleaned.Disposition);
        Assert.True(cleaned.WasRemoved);
        Assert.Equal(1, cleaned.Snapshot.RetainedGenerationCount);
        Assert.Null(await rig.Target.InspectGenerationAsync(OperationContext.Create(), first.GenerationId));
        Assert.Equal(second.GenerationId, cleaned.Snapshot.ActiveGenerationId);
    }

    [Fact]
    public async Task Promotion_WaitsForAnAdmittedBatchOnTheExpectedActiveGeneration()
    {
        var rig = CreateRig();
        var first = await PrepareValidatedAsync(rig, "concurrent-first", Epoch);
        var firstPromotion = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/concurrent-first",
                first,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                WorkerFence,
                new("1"),
                Epoch.AddMinutes(3)));
        var secondEpoch = Epoch.AddMinutes(10);
        var second = await PrepareValidatedAsync(rig, "concurrent-second", secondEpoch);
        var pause = rig.Transport.PauseNextBulk();
        var activeWrite = rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/concurrent-active"),
                first.GenerationId,
                WorkerFence,
                [
                    new MaterializationUpsert(
                        new("item/concurrent-active"),
                        new("mutation/concurrent-active"),
                        new("1"),
                        ObservationValue.FromString("committed-before-swap"))
                ])).AsTask();
        await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var promotion = rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/concurrent-second",
                second,
                first.GenerationId,
                firstPromotion.Snapshot.Revision,
                WorkerFence,
                new("2"),
                secondEpoch.AddMinutes(3))).AsTask();

        try
        {
            var firstCompletion = await Task.WhenAny(promotion, Task.Delay(100));
            Assert.NotSame(promotion, firstCompletion);
        }
        finally
        {
            pause.Release();
        }

        var written = await activeWrite;
        var promoted = await promotion;
        Assert.Equal(MaterializationItemOutcomeDisposition.Applied, Assert.Single(written.Outcomes).Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, promoted.Disposition);
        Assert.Equal(second.GenerationId, promoted.Snapshot.ActiveGenerationId);
    }

    [Fact]
    public async Task Promotion_RefusesReadAliasOwnersOutsideExpectedTargetState()
    {
        var rig = CreateRig();
        var prepared = await PrepareValidatedAsync(rig, "exclusive-alias", Epoch);
        await CreateStrayReadAliasOwnerAsync(rig, "legacy-visible", hidden: false);
        await CreateStrayReadAliasOwnerAsync(rig, ".legacy-hidden", hidden: true);
        var candidateIndex = rig.Binding.GetGenerationIndexName(prepared.GenerationId);
        var before = await InspectReadAliasAsync(rig);

        Assert.Equal(
            [".legacy-hidden", "legacy-visible"],
            before.Bindings.Select(static binding => binding.Index));
        Assert.DoesNotContain(before.Bindings, binding => binding.Index == candidateIndex);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Target.PromoteGenerationAsync(
                OperationContext.Create(),
                Promotion(
                    "promotion/exclusive-alias",
                    prepared,
                    expectedActiveGenerationId: null,
                    MaterializationTargetRevision.Initial,
                    WorkerFence,
                    new("1"),
                    Epoch.AddMinutes(3))));

        var retainedOwners = await InspectReadAliasAsync(rig);
        Assert.Equal(
            [".legacy-hidden", "legacy-visible"],
            retainedOwners.Bindings.Select(static binding => binding.Index));
        Assert.DoesNotContain(retainedOwners.Bindings, binding => binding.Index == candidateIndex);
    }

    [Fact]
    public async Task ApplyBatch_RetriesOnlyRejectedItemAndExactReplayDoesNotRewriteSuccess()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/partial-retry");
        await BeginAsync(rig, generationId, Epoch);
        MaterializationItemMutation[] mutations =
        [
            new MaterializationUpsert(
                new("item/retry"),
                new("mutation/retry"),
                new("1"),
                ObservationValue.FromString("retry")),
            new MaterializationUpsert(
                new("item/success"),
                new("mutation/success"),
                new("1"),
                ObservationValue.FromString("success"))
        ];
        rig.Transport.EnqueueRetryableBulkItemFailure(itemOrdinal: 0);

        var first = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(new("batch/partial"), generationId, WorkerFence, [.. mutations]));

        Assert.Equal(MaterializationBatchDisposition.Applied, first.Disposition);
        Assert.Equal(
            mutations.Select(static mutation => mutation.ItemId),
            first.Outcomes.Select(static outcome => outcome.ItemId));
        Assert.Equal(MaterializationItemOutcomeDisposition.RetryableRejected, first.Outcomes[0].Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.Applied, first.Outcomes[1].Disposition);
        Assert.Contains("429", first.Outcomes[0].Message, StringComparison.Ordinal);
        Assert.Equal(
            1,
            (await rig.Target.InspectGenerationAsync(OperationContext.Create(), generationId))!
            .PendingRetryableMutationCount);

        MaterializationApplyBatchRequest retryRequest = new(
            new("batch/retry"),
            generationId,
            WorkerFence,
            [mutations[0]]);
        var retried = await rig.Target.ApplyBatchAsync(OperationContext.Create(), retryRequest);
        var bulkCountBeforeReplay = rig.Transport.BulkRequests.Length;
        var replayed = await rig.Target.ApplyBatchAsync(OperationContext.Create(), retryRequest);

        Assert.Equal(MaterializationItemOutcomeDisposition.Applied, Assert.Single(retried.Outcomes).Disposition);
        Assert.Equal(MaterializationBatchDisposition.Replayed, replayed.Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.Replayed, Assert.Single(replayed.Outcomes).Disposition);
        Assert.Equal(bulkCountBeforeReplay, rig.Transport.BulkRequests.Length);
        Assert.Equal(
            0,
            (await rig.Target.InspectGenerationAsync(OperationContext.Create(), generationId))!
            .PendingRetryableMutationCount);

        var generationIndex = rig.Binding.GetGenerationIndexName(generationId);
        var generationBulks = rig.Transport.BulkRequests
            .Select(batch => batch.Where(operation => operation.Index == generationIndex).ToArray())
            .Where(static batch => batch.Length > 0)
            .ToArray();
        Assert.Equal(2, generationBulks.Length);
        Assert.Equal(2, generationBulks[0].Length);
        var retriedWrite = Assert.Single(generationBulks[1]);
        Assert.Equal(generationBulks[0][0].Id, retriedWrite.Id);
        Assert.NotEqual(generationBulks[0][1].Id, retriedWrite.Id);
    }

    [Theory]
    [InlineData(425)]
    [InlineData(500)]
    public async Task ApplyBatch_TreatsProviderBackpressureAndServerErrorsAsRetryable(int statusCode)
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId($"generation/retryable-{statusCode}");
        await BeginAsync(rig, generationId, Epoch);
        MaterializationApplyBatchRequest request = new(
            new($"batch/retryable-{statusCode}"),
            generationId,
            WorkerFence,
            [
                new MaterializationUpsert(
                    new("item/retryable"),
                    new("mutation/retryable"),
                    new("1"),
                    ObservationValue.FromString("value"))
            ]);
        rig.Transport.EnqueueBulkItemFailure(itemOrdinal: 0, statusCode, "injected_retryable_error");

        var rejected = await rig.Target.ApplyBatchAsync(OperationContext.Create(), request);
        var retried = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new($"batch/retryable-{statusCode}/retry"),
                generationId,
                WorkerFence,
                request.Mutations));

        Assert.Equal(
            MaterializationItemOutcomeDisposition.RetryableRejected,
            Assert.Single(rejected.Outcomes).Disposition);
        Assert.Contains(statusCode.ToString(), rejected.Outcomes[0].Message, StringComparison.Ordinal);
        Assert.Equal(
            MaterializationItemOutcomeDisposition.Applied,
            Assert.Single(retried.Outcomes).Disposition);
        Assert.False(
            (await rig.Target.InspectGenerationAsync(OperationContext.Create(), generationId))!
            .HasPermanentFailures);
    }

    [Fact]
    public async Task ApplyBatch_ReportsVersionAndIdempotencyConflictsWithoutAnotherDataWrite()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/conflicts");
        var itemId = new MaterializationItemId("item/conflicts");
        var mutationId = new MaterializationItemMutationId("mutation/original");
        await BeginAsync(rig, generationId, Epoch);
        var original = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/original"),
                generationId,
                WorkerFence,
                [new MaterializationUpsert(itemId, mutationId, new("2"), ObservationValue.FromString("original"))]));

        var staleVersion = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/stale-version"),
                generationId,
                WorkerFence,
                [new MaterializationUpsert(itemId, new("mutation/stale"), new("1"), ObservationValue.FromString("stale"))]));
        var reusedIdentity = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/reused-identity"),
                generationId,
                WorkerFence,
                [new MaterializationUpsert(itemId, mutationId, new("2"), ObservationValue.FromString("different"))]));

        Assert.Equal(MaterializationItemOutcomeDisposition.Applied, Assert.Single(original.Outcomes).Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.VersionConflict, Assert.Single(staleVersion.Outcomes).Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.IdempotencyConflict, Assert.Single(reusedIdentity.Outcomes).Disposition);
        var generationIndex = rig.Binding.GetGenerationIndexName(generationId);
        Assert.Single(
            rig.Transport.BulkRequests.SelectMany(static batch => batch),
            operation => operation.Index == generationIndex);
    }

    [Fact]
    public async Task ApplyBatch_ReloadRecoversCompletedStateWhenReceiptCreationWasInterrupted()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/batch-receipt-recovery");
        await BeginAsync(rig, generationId, Epoch);
        MaterializationApplyBatchRequest request = new(
            new("batch/receipt-recovery"),
            generationId,
            WorkerFence,
            [
                new MaterializationUpsert(
                    new("item/recovery-success"),
                    new("mutation/recovery-success"),
                    new("1"),
                    ObservationValue.FromString("retained")),
                new MaterializationUpsert(
                    new("item/recovery-failure"),
                    new("mutation/recovery-failure"),
                    new("1"),
                    ObservationValue.FromString("rejected"))
            ]);
        rig.Transport.EnqueuePermanentBulkItemFailure(itemOrdinal: 1);
        rig.Transport.FailNextControlDocumentCreate("batch-receipt");

        await Assert.ThrowsAsync<ElasticMaterializationTransportException>(async () =>
            await rig.Target.ApplyBatchAsync(OperationContext.Create(), request));
        var bulkCountBeforeRecovery = rig.Transport.BulkRequests.Length;

        var recovered = await ReloadTarget(rig).ApplyBatchAsync(OperationContext.Create(), request);

        Assert.Equal(MaterializationBatchDisposition.Replayed, recovered.Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.Replayed, recovered.Outcomes[0].Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.PermanentFailure, recovered.Outcomes[1].Disposition);
        Assert.Equal("cohesive.adapters.elastic.materialization.permanentFailure", recovered.Outcomes[1].Code);
        Assert.Contains("mapper_parsing_exception", recovered.Outcomes[1].Message, StringComparison.Ordinal);
        Assert.Equal(bulkCountBeforeRecovery, rig.Transport.BulkRequests.Length);
        var generation = Assert.IsType<MaterializationGenerationSnapshot>(
            await ReloadTarget(rig).InspectGenerationAsync(OperationContext.Create(), generationId));
        Assert.True(generation.HasPermanentFailures);
    }

    [Fact]
    public async Task ApplyBatch_EnforcesItemAndCanonicalByteLimitsBeforeBulkIo()
    {
        var itemLimited = CreateRig(new(
            maximumBatchItems: 1,
            maximumBatchBytes: 1_000_000,
            maximumParallelism: 1,
            maximumDiagnosticBytes: 4_096));
        var itemGeneration = new MaterializationGenerationId("generation/item-limit");
        await BeginAsync(itemLimited, itemGeneration, Epoch);
        var tooMany = await itemLimited.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/item-limit"),
                itemGeneration,
                WorkerFence,
                [
                    new MaterializationDelete(new("item/one"), new("mutation/one"), new("1")),
                    new MaterializationDelete(new("item/two"), new("mutation/two"), new("1"))
                ]));

        AssertLimitExceeded(tooMany, expectedOutcomes: 2);
        Assert.Empty(itemLimited.Transport.BulkRequests);

        var byteLimited = CreateRig(new(
            maximumBatchItems: 10,
            maximumBatchBytes: 1,
            maximumParallelism: 1,
            maximumDiagnosticBytes: 4_096));
        var byteGeneration = new MaterializationGenerationId("generation/byte-limit");
        await BeginAsync(byteLimited, byteGeneration, Epoch);
        var tooLarge = await byteLimited.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/byte-limit"),
                byteGeneration,
                WorkerFence,
                [new MaterializationDelete(new("item/large"), new("mutation/large"), new("1"))]));

        AssertLimitExceeded(tooLarge, expectedOutcomes: 1);
        Assert.Empty(byteLimited.Transport.BulkRequests);
    }

    [Fact]
    public async Task ApplyBatch_OversizedReuseOfAnAdmittedBatchIdentityIsAConflict()
    {
        var rig = CreateRig(new(
            maximumBatchItems: 1,
            maximumBatchBytes: 1_000_000,
            maximumParallelism: 1,
            maximumDiagnosticBytes: 4_096));
        var generationId = new MaterializationGenerationId("generation/oversized-identity-reuse");
        await BeginAsync(rig, generationId, Epoch);
        var admitted = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/reused-before-limit"),
                generationId,
                WorkerFence,
                [new MaterializationDelete(new("item/one"), new("mutation/one"), new("1"))]));
        var bulkCount = rig.Transport.BulkRequests.Length;

        var reused = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/reused-before-limit"),
                generationId,
                WorkerFence,
                [
                    new MaterializationDelete(new("item/one"), new("mutation/one"), new("1")),
                    new MaterializationDelete(new("item/two"), new("mutation/two"), new("1"))
                ]));

        Assert.Equal(MaterializationBatchDisposition.Applied, admitted.Disposition);
        Assert.Equal(MaterializationBatchDisposition.IdentityConflict, reused.Disposition);
        Assert.All(
            reused.Outcomes,
            static outcome => Assert.Equal(
                MaterializationItemOutcomeDisposition.IdempotencyConflict,
                outcome.Disposition));
        Assert.Equal(bulkCount, rig.Transport.BulkRequests.Length);
    }

    [Fact]
    public async Task ReservedIdentityConflicts_StillAcceptHigherGenerationAndPromotionTakeoverFences()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/reservation-takeover");
        var missing = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/reservation-takeover"),
                generationId,
                WorkerFence,
                [new MaterializationDelete(new("item/first"), new("mutation/first"), new("1"))]));
        Assert.Equal(MaterializationBatchDisposition.GenerationNotFound, missing.Disposition);
        var begun = await BeginAsync(rig, generationId, Epoch);
        var takeoverFence = new MaterializationWorkerFence("5");
        var batchConflict = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/reservation-takeover"),
                generationId,
                takeoverFence,
                [new MaterializationDelete(new("item/changed"), new("mutation/changed"), new("1"))]));

        Assert.Equal(MaterializationBatchDisposition.IdentityConflict, batchConflict.Disposition);
        Assert.Equal(
            takeoverFence,
            (await rig.Target.InspectGenerationAsync(OperationContext.Create(), generationId))!.LatestWorkerFence);

        MaterializationPromoteGenerationRequest firstPromotion = new(
            new("promotion/reservation-takeover"),
            generationId,
            begun.Revision,
            new("validation/placeholder-one"),
            expectedActiveGenerationId: null,
            MaterializationTargetRevision.Initial,
            takeoverFence,
            new("1"),
            Epoch.AddMinutes(1));
        var stateConflict = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            firstPromotion);
        var promotionConflict = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            new(
                firstPromotion.PromotionId,
                generationId,
                begun.Revision,
                new("validation/placeholder-two"),
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                new("6"),
                new("6"),
                Epoch.AddMinutes(1)));

        Assert.Equal(MaterializationTargetOperationDisposition.StateConflict, stateConflict.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.IdentityConflict, promotionConflict.Disposition);
        Assert.Equal(new MaterializationPromotionFence("6"), (await rig.Target.InspectAsync(
            OperationContext.Create())).LatestPromotionFence);
        Assert.Equal(new MaterializationWorkerFence("6"), (await rig.Target.InspectGenerationAsync(
            OperationContext.Create(), generationId))!.LatestWorkerFence);
    }

    [Fact]
    public async Task IndexedIdentityBounds_RejectGenerationBeforeIoAndFailOnlyTheOffendingBatchItem()
    {
        var tooLong = new string('x', ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters + 1);
        var generationRig = CreateRig();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await generationRig.Target.BeginGenerationAsync(
                OperationContext.Create(),
                new(
                    DefinitionId,
                    new(tooLong),
                    DefinitionFingerprint,
                    WorkerFence,
                    Epoch)));
        Assert.Empty(generationRig.Transport.Calls);

        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/indexed-identity-limit");
        await BeginAsync(rig, generationId, Epoch);
        var result = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/indexed-identity-limit"),
                generationId,
                WorkerFence,
                [
                    new MaterializationDelete(new(tooLong), new("mutation/too-long"), new("1")),
                    new MaterializationUpsert(
                        new("item/accepted"),
                        new("mutation/accepted"),
                        new("1"),
                        ObservationValue.FromString("accepted"))
                ]));

        Assert.Equal(MaterializationBatchDisposition.Applied, result.Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.PermanentFailure, result.Outcomes[0].Disposition);
        Assert.Equal(MaterializationItemOutcomeDisposition.Applied, result.Outcomes[1].Disposition);
        Assert.Contains("indexed-key bound", result.Outcomes[0].Message, StringComparison.Ordinal);
        Assert.True((await rig.Target.InspectGenerationAsync(
            OperationContext.Create(),
            generationId))!.HasPermanentFailures);
        var generationIndex = rig.Binding.GetGenerationIndexName(generationId);
        var write = Assert.Single(
            rig.Transport.BulkRequests.SelectMany(static operations => operations),
            operation => operation.Index == generationIndex);
        Assert.Equal(ElasticBulkOperationKind.Index, write.Kind);
    }

    [Fact]
    public async Task DurableControlDocumentBound_RejectsOversizedOperationEvidenceWithoutBrickingGeneration()
    {
        var rig = CreateRig(new(
            maximumBatchItems: 1,
            maximumBatchBytes: 4 * 1024,
            maximumParallelism: 1,
            maximumDiagnosticBytes: 1_024));
        var generationId = new MaterializationGenerationId("generation/control-document-bound");
        var begun = await BeginAsync(rig, generationId, Epoch);
        var before = await FindGenerationControlAsync(rig, generationId);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Target.SealGenerationAsync(
                OperationContext.Create(),
                new(
                    new(new string('s', 64 * 1024)),
                    generationId,
                    begun.Revision,
                    WorkerFence,
                    Epoch.AddMinutes(1))));

        var after = await FindGenerationControlAsync(rig, generationId);
        Assert.Equal(before.Source, after.Source);
        var inspected = Assert.IsType<MaterializationGenerationSnapshot>(
            await rig.Target.InspectGenerationAsync(OperationContext.Create(), generationId));
        Assert.Equal(MaterializationGenerationState.Loading, inspected.State);
        Assert.Equal(begun.Revision, inspected.Revision);

        var recovered = await rig.Target.SealGenerationAsync(
            OperationContext.Create(),
            new(
                new("seal/control-document-bound/recovered"),
                generationId,
                begun.Revision,
                WorkerFence,
                Epoch.AddMinutes(1)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, recovered.Disposition);
    }

    [Fact]
    public async Task Promotion_RejectsStalePointerFenceWithoutChangingPublishedGeneration()
    {
        var rig = CreateRig();
        var first = await PrepareValidatedAsync(rig, "first", Epoch);
        var second = await PrepareValidatedAsync(rig, "second", Epoch.AddHours(1));
        var promotedFirst = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/first",
                first,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                WorkerFence,
                new("2"),
                Epoch.AddMinutes(3)));
        var aliasTransactionsBeforeStaleAttempt = rig.Transport.AliasRequests.Length;

        var stale = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/stale",
                second,
                first.GenerationId,
                promotedFirst.Snapshot.Revision,
                WorkerFence,
                new("1"),
                Epoch.AddHours(1).AddMinutes(3)));

        Assert.Equal(MaterializationTargetOperationDisposition.StaleFence, stale.Disposition);
        Assert.Equal(first.GenerationId, stale.Snapshot.ActiveGenerationId);
        Assert.Equal(aliasTransactionsBeforeStaleAttempt, rig.Transport.AliasRequests.Length);
        var published = Assert.Single((await InspectReadAliasAsync(rig)).Bindings);
        Assert.Equal(rig.Binding.GetGenerationIndexName(first.GenerationId), published.Index);
    }

    [Fact]
    public async Task Promotion_ExactRetryRecoversAliasTransactionWhoseAppliedResponseWasLost()
    {
        var rig = CreateRig();
        var prepared = await PrepareValidatedAsync(rig, "ambiguous", Epoch);
        var primedFence = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/prime-fence",
                prepared,
                expectedActiveGenerationId: null,
                new("99"),
                WorkerFence,
                new("1"),
                Epoch.AddMinutes(3)));
        Assert.Equal(MaterializationTargetOperationDisposition.RevisionConflict, primedFence.Disposition);

        var request = Promotion(
            "promotion/ambiguous",
            prepared,
            expectedActiveGenerationId: null,
            MaterializationTargetRevision.Initial,
            WorkerFence,
            new("1"),
            Epoch.AddMinutes(3));
        rig.Transport.ApplyNextAliasExchangeThenFailAmbiguously();

        await Assert.ThrowsAsync<ElasticMaterializationTransportException>(
            async () => await rig.Target.PromoteGenerationAsync(OperationContext.Create(), request));
        var appliedAlias = Assert.Single((await InspectReadAliasAsync(rig)).Bindings);
        Assert.Equal(rig.Binding.GetGenerationIndexName(prepared.GenerationId), appliedAlias.Index);

        var recovered = await rig.Target.PromoteGenerationAsync(OperationContext.Create(), request);

        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, recovered.Disposition);
        Assert.Equal(prepared.GenerationId, recovered.Snapshot.ActiveGenerationId);
        Assert.Contains(
            rig.Transport.Calls,
            call => call.Operation == FakeElasticMaterializationTransport.InspectAliasesOperation);
        Assert.Equal(
            2,
            rig.Transport.AliasRequests.Count(alias => alias.ReadAlias == rig.Binding.ReadAlias));
    }

    [Fact]
    public async Task AbandonedCandidate_CanBeRetiredCleanedAndCleanupReplayed()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/abandoned");
        var begun = await BeginAsync(rig, generationId, Epoch);
        var retired = await rig.Target.RetireGenerationAsync(
            OperationContext.Create(),
            new(
                new("retirement/abandoned"),
                generationId,
                begun.Revision,
                WorkerFence,
                Epoch.AddMinutes(1)));
        var cleanupRequest = new MaterializationCleanupGenerationRequest(
            new("cleanup/abandoned"),
            generationId,
            retired.Generation!.Revision,
            WorkerFence,
            Epoch.AddMinutes(2));

        var cleaned = await rig.Target.CleanupGenerationAsync(OperationContext.Create(), cleanupRequest);
        var replayed = await rig.Target.CleanupGenerationAsync(OperationContext.Create(), cleanupRequest);

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, retired.Disposition);
        Assert.Equal(MaterializationGenerationState.Retired, retired.Generation.State);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, cleaned.Disposition);
        Assert.True(cleaned.WasRemoved);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayed.Disposition);
        Assert.False(replayed.WasRemoved);
        Assert.Null(cleaned.Snapshot.ActiveGenerationId);
        Assert.Equal(0, cleaned.Snapshot.RetainedGenerationCount);
        Assert.Null(await rig.Target.InspectGenerationAsync(OperationContext.Create(), generationId));
        Assert.Empty((await InspectReadAliasAsync(rig)).Bindings);
        Assert.Single(
            rig.Transport.Calls,
            call => call.Operation == FakeElasticMaterializationTransport.DeleteOwnedIndexOperation
                && call.Index == rig.Binding.GetGenerationIndexName(generationId));
    }

    [Fact]
    public async Task Cleanup_CompletesInterruptedRetirementReceiptBeforeAtomicIndexRemoval()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/interrupted-retirement");
        var begun = await BeginAsync(rig, generationId, Epoch);
        MaterializationRetireGenerationRequest retirement = new(
            new("retirement/interrupted"),
            generationId,
            begun.Revision,
            WorkerFence,
            Epoch.AddMinutes(1));
        var retirementFingerprint = MaterializationTargetIntentFingerprinter.Compute(retirement);
        var control = await FindGenerationControlAsync(rig, generationId);
        var interrupted = Assert.IsType<JsonObject>(JsonNode.Parse(control.Source));
        interrupted["state"] = MaterializationGenerationState.Retired.ToString();
        interrupted["revision"] = "2";
        interrupted["retiredAtUtc"] = JsonValue.Create(retirement.RetiredAtUtc);
        interrupted["lastRetirement"] = new JsonObject
        {
            ["retirementId"] = retirement.RetirementId.Value,
            ["requestFingerprint"] = JsonSerializer.SerializeToNode(
                retirementFingerprint,
                MaterializationJsonSerializer.CreateOptions())
        };
        var replaced = await rig.Transport.ReplaceDocumentAsync(
            rig.Binding.ControlIndexName,
            control.Id,
            JsonObject(Encoding.UTF8.GetBytes(interrupted.ToJsonString())),
            control.Token,
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        Assert.Equal(ElasticDocumentWriteDisposition.Applied, replaced.Disposition);

        var cleaned = await rig.Target.CleanupGenerationAsync(
            OperationContext.Create(),
            new(
                new("cleanup/interrupted-retirement"),
                generationId,
                new("2"),
                WorkerFence,
                Epoch.AddMinutes(2)));
        var replayedRetirement = await rig.Target.RetireGenerationAsync(
            OperationContext.Create(),
            retirement);

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, cleaned.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, replayedRetirement.Disposition);
        Assert.Equal(MaterializationGenerationState.Retired, replayedRetirement.Generation!.State);
        var retirementReceiptCreate = await FindControlDocumentCreateOrdinalAsync(
            rig,
            "retirement-receipt");
        var deletion = Assert.Single(
            rig.Transport.Calls,
            call => call.Operation == FakeElasticMaterializationTransport.DeleteOwnedIndexOperation
                && call.Index == rig.Binding.GetGenerationIndexName(generationId));
        Assert.True(retirementReceiptCreate < deletion.Ordinal);
    }

    [Fact]
    public async Task BeginGeneration_DurablyReservesIdentityButDoesNotClaimForeignIndex()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/foreign-index");
        var indexName = rig.Binding.GetGenerationIndexName(generationId);
        _ = await rig.Transport.CreateIndexAsync(
            indexName,
            JsonObject("{\"mappings\":{}}"u8.ToArray()),
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        MaterializationBeginGenerationRequest request = new(
            DefinitionId,
            generationId,
            DefinitionFingerprint,
            WorkerFence,
            Epoch);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Target.BeginGenerationAsync(OperationContext.Create(), request));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Target.BeginGenerationAsync(OperationContext.Create(), request));

        var reserved = await rig.Target.InspectGenerationAsync(OperationContext.Create(), generationId);
        Assert.NotNull(reserved);
        Assert.Equal(MaterializationGenerationState.Loading, reserved.State);
        Assert.Equal(0, reserved.RetainedItemCount);

        var refusedSeal = await rig.Target.SealGenerationAsync(
            OperationContext.Create(),
            new(
                new("seal/foreign-index"),
                generationId,
                reserved.Revision,
                WorkerFence,
                Epoch.AddSeconds(30)));
        Assert.Equal(MaterializationTargetOperationDisposition.StateConflict, refusedSeal.Disposition);
        Assert.DoesNotContain(
            rig.Transport.Calls,
            call => call.Operation == FakeElasticMaterializationTransport.AddWriteBlockOperation
                && call.Index == indexName);

        var retired = await rig.Target.RetireGenerationAsync(
            OperationContext.Create(),
            new(
                new("retirement/foreign-index"),
                generationId,
                reserved.Revision,
                WorkerFence,
                Epoch.AddMinutes(1)));
        var cleaned = await rig.Target.CleanupGenerationAsync(
            OperationContext.Create(),
            new(
                new("cleanup/foreign-index"),
                generationId,
                retired.Generation!.Revision,
                WorkerFence,
                Epoch.AddMinutes(2)));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, cleaned.Disposition);
        Assert.True(cleaned.WasRemoved);
        var stillForeign = await rig.Transport.CreateIndexAsync(
            indexName,
            JsonObject("{\"mappings\":{}}"u8.ToArray()),
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        Assert.Equal(ElasticIndexCreateDisposition.AlreadyExists, stillForeign.Disposition);
        Assert.DoesNotContain(
            rig.Transport.Calls,
            call => call.Operation == FakeElasticMaterializationTransport.DeleteOwnedIndexOperation
                && call.Index == indexName);
    }

    [Fact]
    public async Task ValidateGeneration_LoadingStateConflictDoesNotPoisonLaterValidation()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/early-validation");
        var begun = await BeginAsync(rig, generationId, Epoch);

        var refused = await rig.Target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                new("validation/too-early"),
                generationId,
                begun.Revision,
                new("unsealed-placeholder"),
                expectedVisibleItemCount: 0,
                "tests/elastic-validator-v1",
                WorkerFence,
                Epoch.AddSeconds(30)));

        Assert.Equal(MaterializationTargetOperationDisposition.StateConflict, refused.Disposition);
        var sealedResult = await rig.Target.SealGenerationAsync(
            OperationContext.Create(),
            new(
                new("seal/after-early-validation"),
                generationId,
                begun.Revision,
                WorkerFence,
                Epoch.AddMinutes(1)));
        var validated = await rig.Target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                new("validation/after-seal"),
                generationId,
                sealedResult.Generation!.Revision,
                sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: 0,
                "tests/elastic-validator-v1",
                WorkerFence,
                Epoch.AddMinutes(2)));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, sealedResult.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, validated.Disposition);
        Assert.True(validated.Receipt!.Validation.IsValid);
    }

    [Fact]
    public async Task ValidateGeneration_ReloadRecoversInvalidDiagnosticsWhenReceiptCreationWasInterrupted()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/validation-receipt-recovery");
        await BeginAsync(rig, generationId, Epoch);
        var written = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/validation-receipt-recovery"),
                generationId,
                WorkerFence,
                [
                    new MaterializationUpsert(
                        new("item/validation-receipt-recovery"),
                        new("mutation/validation-receipt-recovery"),
                        new("1"),
                        ObservationValue.FromString("retained"))
                ]));
        var sealedResult = await rig.Target.SealGenerationAsync(
            OperationContext.Create(),
            new(
                new("seal/validation-receipt-recovery"),
                generationId,
                written.GenerationRevision!.Value,
                WorkerFence,
                Epoch.AddMinutes(1)));
        MaterializationValidateGenerationRequest request = new(
            new("validation/receipt-recovery"),
            generationId,
            sealedResult.Generation!.Revision,
            sealedResult.Receipt!.Fingerprint,
            expectedVisibleItemCount: 2,
            "tests/elastic-validator-v1",
            WorkerFence,
            Epoch.AddMinutes(2));
        rig.Transport.FailNextControlDocumentCreate("validation-receipt");

        await Assert.ThrowsAsync<ElasticMaterializationTransportException>(async () =>
            await rig.Target.ValidateGenerationAsync(OperationContext.Create(), request));

        var recovered = await ReloadTarget(rig).ValidateGenerationAsync(OperationContext.Create(), request);

        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, recovered.Disposition);
        var receipt = Assert.IsType<MaterializationValidationReceipt>(recovered.Receipt);
        Assert.False(receipt.Validation.IsValid);
        var diagnostic = Assert.Single(receipt.Validation.Diagnostics);
        Assert.Equal("cohesive.adapters.elastic.materialization.visibleItemCountMismatch", diagnostic.Code);
        Assert.Equal("2", diagnostic.Evidence!.Expected);
        Assert.Equal("1", diagnostic.Evidence.Observed);
        var generation = Assert.IsType<MaterializationGenerationSnapshot>(recovered.Generation);
        Assert.Equal(MaterializationGenerationState.Sealed, generation.State);
    }

    [Fact]
    public async Task Cleanup_DeletesOwnedIndexWhenProvisioningCommitWasLost()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/provisioning-commit-lost");
        var begun = await BeginAsync(rig, generationId, Epoch);
        var control = await FindGenerationControlAsync(rig, generationId);
        var uncommitted = JsonNode.Parse(control.Source)!.AsObject();
        uncommitted["isProvisioned"] = false;
        var replaced = await rig.Transport.ReplaceDocumentAsync(
            rig.Binding.ControlIndexName,
            control.Id,
            JsonObject(JsonSerializer.SerializeToUtf8Bytes(uncommitted)),
            control.Token,
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        Assert.Equal(ElasticDocumentWriteDisposition.Applied, replaced.Disposition);

        var retired = await rig.Target.RetireGenerationAsync(
            OperationContext.Create(),
            new(
                new("retirement/provisioning-commit-lost"),
                generationId,
                begun.Revision,
                WorkerFence,
                Epoch.AddMinutes(1)));
        var cleaned = await rig.Target.CleanupGenerationAsync(
            OperationContext.Create(),
            new(
                new("cleanup/provisioning-commit-lost"),
                generationId,
                retired.Generation!.Revision,
                WorkerFence,
                Epoch.AddMinutes(2)));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, cleaned.Disposition);
        Assert.Contains(
            rig.Transport.Calls,
            call => call.Operation == FakeElasticMaterializationTransport.DeleteOwnedIndexOperation
                && call.Index == rig.Binding.GetGenerationIndexName(generationId));
    }

    [Fact]
    public async Task Seal_RejectsAValidEnvelopeStoredUnderAForeignDocumentIdentity()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/foreign-document-id");
        _ = await BeginAsync(rig, generationId, Epoch);
        var applied = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/foreign-document-id"),
                generationId,
                WorkerFence,
                [
                    new MaterializationUpsert(
                        new("item/foreign-document-id"),
                        new("mutation/foreign-document-id"),
                        new("1"),
                        ObservationValue.FromString("value"))
                ]));

        var index = rig.Binding.GetGenerationIndexName(generationId);
        var expectedId = Assert.Single(
            rig.Transport.BulkRequests.SelectMany(static operations => operations),
            operation => operation.Index == index).Id;
        var stored = Assert.Single((await rig.Transport.MultiGetAsync(
            index,
            [expectedId],
            ElasticMultiGetSourceProjection.Full,
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None)).Documents);
        Assert.True(stored.Found);
        _ = await rig.Transport.DeleteDocumentAsync(
            index,
            expectedId,
            stored.ConcurrencyToken!.Value,
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        _ = await rig.Transport.CreateDocumentAsync(
            index,
            "foreign-document-id",
            JsonObject(stored.Source),
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Target.SealGenerationAsync(
                OperationContext.Create(),
                new(
                    new("seal/foreign-document-id"),
                    generationId,
                    applied.GenerationRevision!.Value,
                    WorkerFence,
                    Epoch.AddMinutes(1))));

        Assert.Contains("adapter-owned identity envelope", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seal_PaginatesCompleteContentAndReducesPageSizeAfterBoundedResponseRejection()
    {
        const int itemCount = 513;
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/paginated-seal");
        await BeginAsync(rig, generationId, Epoch);
        var mutations = ImmutableArray.CreateBuilder<MaterializationItemMutation>(itemCount);
        for (var index = 0; index < itemCount; index++)
        {
            var suffix = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            mutations.Add(new MaterializationUpsert(
                new($"item/paginated/{suffix}"),
                new($"mutation/paginated/{suffix}"),
                new("1"),
                ObservationValue.FromString($"value-{suffix}")));
        }
        var retainedMutations = mutations.MoveToImmutable();
        var written = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(new("batch/paginated-seal"), generationId, WorkerFence, retainedMutations));
        Assert.All(
            written.Outcomes,
            static outcome => Assert.Equal(MaterializationItemOutcomeDisposition.Applied, outcome.Disposition));
        rig.Transport.FailNextScanWithResponseLimit();

        var sealedResult = await rig.Target.SealGenerationAsync(
            OperationContext.Create(),
            new(
                new("seal/paginated"),
                generationId,
                written.GenerationRevision!.Value,
                WorkerFence,
                Epoch.AddMinutes(1)));

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, sealedResult.Disposition);
        Assert.Equal(itemCount, sealedResult.Receipt!.VisibleItemCount);
        Assert.Equal(
            MaterializationSealFingerprinter.Compute(
                [.. retainedMutations.Select(MaterializationSealContentEntry.From)]),
            sealedResult.Receipt.Fingerprint);
        Assert.Equal(
            [512, 256, 256, 256],
            rig.Transport.ScanRequests.Select(static request => request.MaximumItems));
    }

    [Fact]
    public async Task Cleanup_FailsClosedForForeignReplacementAtProvisionedIndexName()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/foreign-replacement");
        var indexName = rig.Binding.GetGenerationIndexName(generationId);
        var begun = await BeginAsync(rig, generationId, Epoch);
        var retired = await rig.Target.RetireGenerationAsync(
            OperationContext.Create(),
            new(
                new("retirement/foreign-replacement"),
                generationId,
                begun.Revision,
                WorkerFence,
                Epoch.AddMinutes(1)));
        _ = await rig.Transport.DeleteIndexAsync(
            indexName,
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        _ = await rig.Transport.CreateIndexAsync(
            indexName,
            JsonObject("{\"mappings\":{}}"u8.ToArray()),
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Target.CleanupGenerationAsync(
                OperationContext.Create(),
                new(
                    new("cleanup/foreign-replacement"),
                    generationId,
                    retired.Generation!.Revision,
                    WorkerFence,
                    Epoch.AddMinutes(2))));

        Assert.True(await rig.Transport.IndexExistsAsync(
            indexName,
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None));
        var durable = JsonNode.Parse((await FindGenerationControlAsync(rig, generationId)).Source)!.AsObject();
        Assert.True(durable["retained"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Cleanup_FailsClosedWhenDurableGenerationIndexNameIsTampered()
    {
        var rig = CreateRig();
        var generationId = new MaterializationGenerationId("generation/tampered-control");
        var begun = await BeginAsync(rig, generationId, Epoch);
        var retired = await rig.Target.RetireGenerationAsync(
            OperationContext.Create(),
            new(
                new("retirement/tampered-control"),
                generationId,
                begun.Revision,
                WorkerFence,
                Epoch.AddMinutes(1)));
        const string victimIndex = "unrelated-user-index";
        _ = await rig.Transport.CreateIndexAsync(
            victimIndex,
            JsonObject("{\"mappings\":{}}"u8.ToArray()),
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        var control = await FindGenerationControlAsync(rig, generationId);
        var tampered = JsonNode.Parse(control.Source)!.AsObject();
        tampered["indexName"] = victimIndex;
        var replaced = await rig.Transport.ReplaceDocumentAsync(
            rig.Binding.ControlIndexName,
            control.Id,
            JsonObject(JsonSerializer.SerializeToUtf8Bytes(tampered)),
            control.Token,
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        Assert.Equal(ElasticDocumentWriteDisposition.Applied, replaced.Disposition);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await rig.Target.CleanupGenerationAsync(
            OperationContext.Create(),
            new(
                new("cleanup/tampered-control"),
                generationId,
                retired.Generation!.Revision,
                WorkerFence,
                Epoch.AddMinutes(2))));

        Assert.DoesNotContain(
            rig.Transport.Calls,
            call => call.Operation == FakeElasticMaterializationTransport.DeleteOwnedIndexOperation
                && call.Index == victimIndex);
        var victimStillExists = await rig.Transport.CreateIndexAsync(
            victimIndex,
            JsonObject("{\"mappings\":{}}"u8.ToArray()),
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        Assert.Equal(ElasticIndexCreateDisposition.AlreadyExists, victimStillExists.Disposition);
    }

    [Fact]
    public async Task Target_DoesNotClaimForeignControlIndexWithoutOwnershipMarker()
    {
        var rig = CreateRig();
        _ = await rig.Transport.CreateIndexAsync(
            rig.Binding.ControlIndexName,
            JsonObject("{\"mappings\":{}}"u8.ToArray()),
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Target.InspectAsync(OperationContext.Create()));

        Assert.DoesNotContain(
            rig.Transport.Calls,
            call => call.Operation == FakeElasticMaterializationTransport.CreateDocumentOperation
                && call.Index == rig.Binding.ControlIndexName);
    }

    [Fact]
    public async Task Target_FailsClosedWhenValidDurableStateLosesItsPublicationMarker()
    {
        var rig = CreateRig();
        _ = await rig.Target.InspectAsync(OperationContext.Create());
        var targetControl = await FindTargetControlAsync(rig);
        var targetState = Assert.IsType<JsonObject>(JsonNode.Parse(targetControl.Source));
        var markerAlias = targetState["markerAlias"]!.GetValue<string>();
        rig.Transport.TamperRemoveAlias(rig.Binding.ControlIndexName, markerAlias);
        var callCountBefore = rig.Transport.Calls.Length;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Target.BeginGenerationAsync(
                OperationContext.Create(),
                new(
                    DefinitionId,
                    new("generation/missing-publication-marker"),
                    DefinitionFingerprint,
                    WorkerFence,
                    Epoch)));

        AssertNoMutationCalls(rig.Transport.Calls[callCountBefore..]);
    }

    [Fact]
    public async Task Target_FailsClosedWhenStableReadAliasDriftsFromDurableActiveGeneration()
    {
        var rig = CreateRig();
        var prepared = await PrepareValidatedAsync(rig, "read-alias-drift", Epoch);
        _ = await rig.Target.PromoteGenerationAsync(
            OperationContext.Create(),
            Promotion(
                "promotion/read-alias-drift",
                prepared,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                WorkerFence,
                new("1"),
                Epoch.AddMinutes(3)));
        rig.Transport.TamperMoveAlias(
            rig.Binding.ReadAlias,
            rig.Binding.GetGenerationIndexName(prepared.GenerationId),
            rig.Binding.ControlIndexName);
        var callCountBefore = rig.Transport.Calls.Length;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Target.ApplyBatchAsync(
                OperationContext.Create(),
                new(
                    new("batch/read-alias-drift"),
                    prepared.GenerationId,
                    WorkerFence,
                    [
                        new MaterializationUpsert(
                            new("item/read-alias-drift/late"),
                            new("mutation/read-alias-drift/late"),
                            new("2"),
                            ObservationValue.FromString("must-not-write"))
                    ])));

        AssertNoMutationCalls(rig.Transport.Calls[callCountBefore..]);
    }

    [Fact]
    public async Task TargetOperations_HonorPreCanceledContextBeforeTransportIo()
    {
        var rig = CreateRig();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        var context = OperationContext.Create(cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rig.Target.InspectAsync(context));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rig.Target.BeginGenerationAsync(
            context,
            new(
                DefinitionId,
                new("generation/canceled"),
                DefinitionFingerprint,
                WorkerFence,
                Epoch)));

        Assert.Empty(rig.Transport.Calls);
    }

    static void AssertLimitExceeded(MaterializationBatchResult result, int expectedOutcomes)
    {
        Assert.Equal(MaterializationBatchDisposition.LimitExceeded, result.Disposition);
        Assert.Equal(expectedOutcomes, result.Outcomes.Length);
        Assert.All(
            result.Outcomes,
            static outcome => Assert.Equal(
                MaterializationItemOutcomeDisposition.RetryableRejected,
                outcome.Disposition));
    }

    static void AssertNoMutationCalls(ImmutableArray<FakeElasticMaterializationCall> calls) =>
        Assert.DoesNotContain(
            calls,
            static call => call.Operation is
                FakeElasticMaterializationTransport.CreateDocumentOperation
                or FakeElasticMaterializationTransport.ReplaceDocumentOperation
                or FakeElasticMaterializationTransport.DeleteDocumentOperation
                or FakeElasticMaterializationTransport.CreateIndexOperation
                or FakeElasticMaterializationTransport.AddWriteBlockOperation
                or FakeElasticMaterializationTransport.RemoveWriteBlockOperation
                or FakeElasticMaterializationTransport.DeleteIndexOperation
                or FakeElasticMaterializationTransport.DeleteOwnedIndexOperation
                or FakeElasticMaterializationTransport.BulkOperation
                or FakeElasticMaterializationTransport.CompareExchangeAliasOperation);

    static async Task<MaterializationGenerationSnapshot> BeginAsync(
        TargetRig rig,
        MaterializationGenerationId generationId,
        DateTimeOffset createdAtUtc)
    {
        var result = await rig.Target.BeginGenerationAsync(
            OperationContext.Create(),
            new(DefinitionId, generationId, DefinitionFingerprint, WorkerFence, createdAtUtc));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, result.Disposition);
        return Assert.IsType<MaterializationGenerationSnapshot>(result.Generation);
    }

    static async Task<PreparedGeneration> PrepareValidatedAsync(
        TargetRig rig,
        string suffix,
        DateTimeOffset createdAtUtc)
    {
        var generationId = new MaterializationGenerationId($"generation/{suffix}");
        await BeginAsync(rig, generationId, createdAtUtc);
        var written = await rig.Target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new($"batch/{suffix}"),
                generationId,
                WorkerFence,
                [
                    new MaterializationUpsert(
                        new($"item/{suffix}"),
                        new($"mutation/{suffix}"),
                        new("1"),
                        ObservationValue.FromString(suffix))
                ]));
        Assert.Equal(MaterializationItemOutcomeDisposition.Applied, Assert.Single(written.Outcomes).Disposition);
        var sealedResult = await rig.Target.SealGenerationAsync(
            OperationContext.Create(),
            new(
                new($"seal/{suffix}"),
                generationId,
                written.GenerationRevision!.Value,
                WorkerFence,
                createdAtUtc.AddMinutes(1)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, sealedResult.Disposition);
        var validated = await rig.Target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                new($"validation/{suffix}"),
                generationId,
                sealedResult.Generation!.Revision,
                sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: 1,
                "tests/elastic-validator-v1",
                WorkerFence,
                createdAtUtc.AddMinutes(2)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, validated.Disposition);
        return new(generationId, validated.Generation!.Revision, validated.Receipt!.Fingerprint);
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

    static ValueTask<ElasticAliasSnapshot> InspectReadAliasAsync(TargetRig rig) =>
        rig.Transport.InspectAliasesAsync(
            [rig.Binding.ReadAlias],
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);

    static async ValueTask<ControlDocument> FindGenerationControlAsync(
        TargetRig rig,
        MaterializationGenerationId generationId)
    {
        foreach (var call in rig.Transport.Calls.Where(call =>
                     call.Operation == FakeElasticMaterializationTransport.CreateDocumentOperation
                     && call.Index == rig.Binding.ControlIndexName
                     && call.Id is not null))
        {
            var document = await rig.Transport.GetDocumentAsync(
                rig.Binding.ControlIndexName,
                call.Id!,
                1_000_000,
                CancellationToken.None);
            if (!document.Found || document.ConcurrencyToken is not { } token)
            {
                continue;
            }
            using var source = JsonDocument.Parse(document.Source);
            if (source.RootElement.TryGetProperty("documentKind", out var kind)
                && kind.GetString() == "generation"
                && source.RootElement.TryGetProperty("generationId", out var id)
                && id.GetString() == generationId.Value)
            {
                return new(call.Id!, document.Source, token);
            }
        }

        throw new InvalidOperationException("The test generation control document was not found.");
    }

    static async ValueTask<ControlDocument> FindTargetControlAsync(TargetRig rig)
    {
        foreach (var call in rig.Transport.Calls.Where(call =>
                     call.Operation == FakeElasticMaterializationTransport.CreateDocumentOperation
                     && call.Index == rig.Binding.ControlIndexName
                     && call.Id is not null))
        {
            var document = await rig.Transport.GetDocumentAsync(
                rig.Binding.ControlIndexName,
                call.Id!,
                1_000_000,
                CancellationToken.None);
            if (!document.Found || document.ConcurrencyToken is not { } token)
                continue;
            using var source = JsonDocument.Parse(document.Source);
            if (source.RootElement.TryGetProperty("documentKind", out var kind)
                && kind.GetString() == "target")
            {
                return new(call.Id!, document.Source, token);
            }
        }

        throw new InvalidOperationException("The test target control document was not found.");
    }

    static async ValueTask<int> FindControlDocumentCreateOrdinalAsync(TargetRig rig, string documentKind)
    {
        foreach (var call in rig.Transport.Calls.Where(call =>
                     call.Operation == FakeElasticMaterializationTransport.CreateDocumentOperation
                     && call.Index == rig.Binding.ControlIndexName
                     && call.Id is not null))
        {
            var document = await rig.Transport.GetDocumentAsync(
                rig.Binding.ControlIndexName,
                call.Id!,
                1_000_000,
                CancellationToken.None);
            if (!document.Found)
                continue;
            using var source = JsonDocument.Parse(document.Source);
            if (source.RootElement.TryGetProperty("documentKind", out var kind)
                && kind.GetString() == documentKind)
            {
                return call.Ordinal;
            }
        }

        throw new InvalidOperationException($"The test control document '{documentKind}' was not found.");
    }

    static async Task CreateStrayReadAliasOwnerAsync(TargetRig rig, string index, bool hidden)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            if (hidden)
            {
                writer.WriteStartObject("settings");
                writer.WriteBoolean("index.hidden", true);
                writer.WriteEndObject();
            }
            writer.WriteStartObject("aliases");
            writer.WriteStartObject(rig.Binding.ReadAlias);
            writer.WriteBoolean("is_write_index", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var created = await rig.Transport.CreateIndexAsync(
            index,
            JsonObject(stream.ToArray()),
            rig.Policy.MaximumDiagnosticBytes,
            CancellationToken.None);
        Assert.Equal(ElasticIndexCreateDisposition.Created, created.Disposition);
        Assert.True(created.Acknowledged);
    }

    static ElasticJsonObject JsonObject(ReadOnlyMemory<byte> value) =>
        ElasticJsonObject.Parse(value, nameof(value));

    static TargetRig CreateRig(ElasticMaterializationTargetPolicy? policy = null)
    {
        var binding = CreateBinding();
        var runtime = new ElasticElasticsearchRuntimeBinding(
            binding.Cluster,
            new ElasticsearchClient(new ElasticsearchClientSettings(new InMemoryRequestInvoker())),
            "tests/elastic-runtime/v1");
        var transport = new FakeElasticMaterializationTransport();
        var effectivePolicy = policy ?? ElasticMaterializationTargetPolicy.Default;
        return new(
            binding,
            effectivePolicy,
            transport,
            new ElasticMaterializationTarget(binding, effectivePolicy, runtime, transport));
    }

    static ElasticMaterializationTarget ReloadTarget(TargetRig rig)
    {
        var runtime = new ElasticElasticsearchRuntimeBinding(
            rig.Binding.Cluster,
            new ElasticsearchClient(new ElasticsearchClientSettings(new InMemoryRequestInvoker())),
            "tests/elastic-runtime/v1");
        return new(rig.Binding, rig.Policy, runtime, rig.Transport);
    }

    static ElasticMaterializationTargetBinding CreateBinding()
    {
        const string readAlias = "loads-read";
        return new(
            new("tests/elastic-materialization-target/v1"),
            new("cluster-uuid"),
            new("target/search"),
            DefinitionId,
            readAlias,
            "loads-generation-",
            ".cohesive-materialization-control",
            new(
                "loads-template",
                new("sha256", "elastic-index-template/v1", new string('a', 64)),
                "tests/elastic-template/v1"),
            new("tests/process-runtime/v1", "search-index/loads"),
            new(
                new("tests/elastic-search-binding/v1"),
                new RelationQuerySourceInstanceId("search/materialized-loads"),
                new RelationQuerySourcePlacementBindingId("search/materialized-loads/placement"),
                ElasticRelationQueryTargetProfile.Target,
                ElasticRelationQueryTargetProfile.ProfileId,
                readAlias,
                []));
    }

    sealed record TargetRig(
        ElasticMaterializationTargetBinding Binding,
        ElasticMaterializationTargetPolicy Policy,
        FakeElasticMaterializationTransport Transport,
        ElasticMaterializationTarget Target);

    sealed record PreparedGeneration(
        MaterializationGenerationId GenerationId,
        MaterializationGenerationRevision Revision,
        MaterializationValidationFingerprint ValidationFingerprint);

    sealed record ControlDocument(
        string Id,
        byte[] Source,
        ElasticDocumentConcurrencyToken Token);
}
