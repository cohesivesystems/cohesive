using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed partial class MaterializationRebuildExecutorTests
{
    [Fact]
    public async Task Activation_ConvergesSealsValidatesAndPromotesOneExactGeneration()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var executor = ActivationExecutor(harness, harness.Rebuild.Target, workStore);
        var now = DateTimeOffset.UtcNow;

        var result = await executor.ActivateAsync(
            OperationContext.Create(new FixedTimeProvider(now)),
            harness.Attempt,
            Invocation("activation/happy"),
            Worker("activation/happy"));
        var candidate = Assert.IsType<MaterializationGenerationSnapshot>(
            await harness.Rebuild.Target.InspectGenerationAsync(OperationContext.Create(), harness.Generation));
        var target = await harness.Rebuild.Target.InspectAsync(OperationContext.Create());
        var durable = Assert.IsType<MaterializationSynchronizationWorkSnapshot>(
            await workStore.LoadAsync(OperationContext.Create(), executor.GetWorkKey(harness.Attempt)));

        Assert.Equal(MaterializationGenerationActivationDisposition.Active, result.Disposition);
        Assert.Equal(MaterializationSynchronizationRunDisposition.Converged, result.Synchronization!.Disposition);
        Assert.True(result.Activation!.IsComplete);
        Assert.Equal(MaterializationGenerationState.Active, candidate.State);
        Assert.Equal(harness.Generation, target.ActiveGenerationId);
        Assert.Equal(harness.Generation, result.Target!.ActiveGenerationId);
        Assert.True(durable.Activation!.IsComplete);
        Assert.Null(durable.PendingWork);
    }

    [Fact]
    public async Task Activation_CallerCancellationPropagatesWithoutStartingDurableWork()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var executor = ActivationExecutor(harness, harness.Rebuild.Target, workStore);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ActivateAsync(
                OperationContext.Create(cancellationToken: cancellation.Token),
                harness.Attempt,
                Invocation("activation/cancelled"),
                Worker("activation/cancelled")));

        Assert.Null(await workStore.LoadAsync(OperationContext.Create(), executor.GetWorkKey(harness.Attempt)));
    }

    [Theory]
    [InlineData(ActivationEffect.Seal)]
    [InlineData(ActivationEffect.Validation)]
    [InlineData(ActivationEffect.Promotion)]
    public async Task Activation_CrashAfterTargetEffectReplaysExactIntentAfterProofAges(
        ActivationEffect crashAfter)
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var time = new MutableActivationTimeProvider(DateTimeOffset.UtcNow);
        var target = new ObservedActivationTarget(harness.Rebuild.Target, time, crashAfter: crashAfter);
        var executor = ActivationExecutor(harness, target, workStore);
        var invocation = Invocation($"activation/crash/{crashAfter}");
        var worker = Worker($"activation/crash/{crashAfter}");

        await Assert.ThrowsAsync<InjectedActivationCrashException>(async () =>
            await executor.ActivateAsync(
                OperationContext.Create(time),
                harness.Attempt,
                invocation,
                worker));
        var afterCrash = Assert.IsType<MaterializationSynchronizationWorkSnapshot>(
            await workStore.LoadAsync(OperationContext.Create(), executor.GetWorkKey(harness.Attempt)));

        Assert.NotNull(afterCrash.Activation);
        var pointerAfterCrash = await target.InspectAsync(OperationContext.Create());
        if (crashAfter != ActivationEffect.Promotion)
            Assert.Null(pointerAfterCrash.ActiveGenerationId);

        time.Advance(TimeSpan.FromMinutes(1));
        var resumed = await executor.ActivateAsync(
            OperationContext.Create(time),
            harness.Attempt,
            invocation,
            worker);
        var durable = Assert.IsType<MaterializationSynchronizationWorkSnapshot>(
            await workStore.LoadAsync(OperationContext.Create(), executor.GetWorkKey(harness.Attempt)));

        switch (crashAfter)
        {
            case ActivationEffect.Seal:
                Assert.Equal(MaterializationGenerationActivationDisposition.RestartRequired, resumed.Disposition);
                Assert.NotNull(durable.Activation!.SealReceipt);
                Assert.Null(durable.Activation.ValidationReceipt);
                Assert.Equal(2, target.SealCalls);
                Assert.Equal(0, target.ValidationCalls);
                break;
            case ActivationEffect.Validation:
                Assert.Equal(MaterializationGenerationActivationDisposition.RestartRequired, resumed.Disposition);
                Assert.NotNull(durable.Activation!.ValidationReceipt);
                Assert.Null(durable.Activation.PromotionReceipt);
                Assert.Equal(2, target.ValidationCalls);
                Assert.Equal(0, target.PromotionCalls);
                break;
            case ActivationEffect.Promotion:
                Assert.Equal(MaterializationGenerationActivationDisposition.Active, resumed.Disposition);
                Assert.True(durable.Activation!.IsComplete);
                Assert.Equal(2, target.PromotionCalls);
                Assert.Equal(harness.Generation, resumed.Target!.ActiveGenerationId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(crashAfter), crashAfter, "Unsupported activation effect.");
        }
    }

    [Theory]
    [InlineData(ActivationEffect.Seal)]
    [InlineData(ActivationEffect.Validation)]
    [InlineData(ActivationEffect.Promotion)]
    public async Task Activation_StaleProofBlocksEveryFirstTimeTargetEffect(ActivationEffect blockedEffect)
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var time = new MutableActivationTimeProvider(DateTimeOffset.UtcNow);
        var feedInspections = harness.Rebuild.Plan.ChangeFeeds.Length;
        var target = new ObservedActivationTarget(
            harness.Rebuild.Target,
            time,
            advanceAtGenerationInspection: feedInspections + (blockedEffect switch
            {
                ActivationEffect.Seal => 2,
                ActivationEffect.Validation => 3,
                ActivationEffect.Promotion => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(blockedEffect), blockedEffect, "Unsupported activation effect.")
            }));
        var executor = ActivationExecutor(harness, target, workStore);

        var result = await executor.ActivateAsync(
            OperationContext.Create(time),
            harness.Attempt,
            Invocation($"activation/stale/{blockedEffect}"),
            Worker($"activation/stale/{blockedEffect}"));
        var candidate = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), harness.Generation));
        var pointer = await target.InspectAsync(OperationContext.Create());

        Assert.Equal(MaterializationGenerationActivationDisposition.RestartRequired, result.Disposition);
        Assert.Null(pointer.ActiveGenerationId);
        switch (blockedEffect)
        {
            case ActivationEffect.Seal:
                Assert.Equal(0, target.SealCalls);
                Assert.Equal(MaterializationGenerationState.Loading, candidate.State);
                break;
            case ActivationEffect.Validation:
                Assert.Equal(1, target.SealCalls);
                Assert.Equal(0, target.ValidationCalls);
                Assert.Equal(MaterializationGenerationState.Sealed, candidate.State);
                break;
            case ActivationEffect.Promotion:
                Assert.Equal(1, target.ValidationCalls);
                Assert.Equal(0, target.PromotionCalls);
                Assert.Equal(MaterializationGenerationState.Validated, candidate.State);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(blockedEffect), blockedEffect, "Unsupported activation effect.");
        }
    }

    [Fact]
    public async Task Activation_CompletedReceiptWhoseTargetWasDisplacedRequiresRestart()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var executor = ActivationExecutor(harness, harness.Rebuild.Target, workStore);
        var now = DateTimeOffset.UtcNow;
        var invocation = Invocation("activation/displaced");
        var worker = Worker("activation/displaced");
        var activated = await executor.ActivateAsync(
            OperationContext.Create(new FixedTimeProvider(now)),
            harness.Attempt,
            invocation,
            worker);
        Assert.Equal(MaterializationGenerationActivationDisposition.Active, activated.Disposition);

        var displacement = await PromoteReplacementAsync(
            harness.Rebuild,
            previous: harness.Generation,
            now.AddMinutes(1));
        var resumed = await executor.ActivateAsync(
            OperationContext.Create(new FixedTimeProvider(now.AddMinutes(2))),
            harness.Attempt,
            invocation,
            worker);

        Assert.Equal(displacement, (await harness.Rebuild.Target.InspectAsync(OperationContext.Create())).ActiveGenerationId);
        Assert.Equal(MaterializationGenerationActivationDisposition.RestartRequired, resumed.Disposition);
        Assert.True(resumed.Activation!.IsComplete);
        Assert.Null(resumed.Target);
    }

    static MaterializationGenerationActivationExecutor ActivationExecutor(
        SynchronizationHarness harness,
        IMaterializationTarget target,
        IMaterializationSynchronizationWorkStore workStore)
    {
        var retained = harness.Rebuild.Resolved;
        var resolved = new ResolvedMaterializationRebuildPlan(
            plan: harness.Rebuild.Plan,
            target: target,
            progressStore: retained.ProgressStore,
            shardBindings: harness.Rebuild.Plan.Shards.Select(shard => retained.GetShard(shard.Id)),
            changeFeedBindings: harness.Rebuild.Plan.ChangeFeeds.Select(feed => retained.GetChangeFeed(feed.Id)));
        return new(resolved, workStore);
    }

    static async Task<MaterializationGenerationId> PromoteReplacementAsync(
        RebuildFixture fixture,
        MaterializationGenerationId previous,
        DateTimeOffset promotedAtUtc)
    {
        var context = OperationContext.Create(new FixedTimeProvider(promotedAtUtc));
        var generation = new MaterializationGenerationId("tests/activation/displacing-generation");
        var workerFence = new MaterializationWorkerFence("100");
        var begun = await fixture.Target.BeginGenerationAsync(
            context,
            new(
                materializationId: fixture.Plan.Materialization.Definition.Id,
                generationId: generation,
                definitionFingerprint: fixture.Plan.Materialization.DefinitionFingerprint,
                workerFence: workerFence,
                createdAtUtc: promotedAtUtc));
        var sealedResult = await fixture.Target.SealGenerationAsync(
            context,
            new(
                sealId: new("tests/activation/displacing-seal"),
                generationId: generation,
                expectedRevision: begun.Generation!.Revision,
                workerFence: workerFence,
                sealedAtUtc: promotedAtUtc));
        var validation = await fixture.Target.ValidateGenerationAsync(
            context,
            new(
                validationId: new("tests/activation/displacing-validation"),
                generationId: generation,
                expectedRevision: sealedResult.Receipt!.GenerationRevision,
                expectedSealFingerprint: sealedResult.Receipt.Fingerprint,
                expectedVisibleItemCount: 0,
                validator: fixture.Target.Descriptor.Capabilities.Id.Value,
                workerFence: workerFence,
                validatedAtUtc: promotedAtUtc));
        var pointer = await fixture.Target.InspectAsync(context);
        Assert.Equal(previous, pointer.ActiveGenerationId);
        var promotionFence = new MaterializationPromotionFence(
            checked(pointer.LatestPromotionFence!.Value.Ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var promoted = await fixture.Target.PromoteGenerationAsync(
            context,
            new(
                promotionId: new("tests/activation/displacing-promotion"),
                generationId: generation,
                expectedGenerationRevision: validation.Receipt!.GenerationRevision,
                validationFingerprint: validation.Receipt.Fingerprint,
                expectedActiveGenerationId: previous,
                expectedTargetRevision: pointer.Revision,
                generationWorkerFence: workerFence,
                promotionFence: promotionFence,
                promotedAtUtc: promotedAtUtc));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, promoted.Disposition);
        return generation;
    }

    public enum ActivationEffect
    {
        Seal,
        Validation,
        Promotion
    }

    sealed class MutableActivationTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        DateTimeOffset currentUtcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => currentUtcNow;

        public void Advance(TimeSpan elapsed) => currentUtcNow += elapsed;
    }

    sealed class ObservedActivationTarget(
        IMaterializationTarget inner,
        MutableActivationTimeProvider time,
        ActivationEffect? crashAfter = null,
        int? advanceAtGenerationInspection = null) : IMaterializationTarget
    {
        bool crashPending = crashAfter is not null;
        bool advancePending = advanceAtGenerationInspection is not null;
        int generationInspections;

        public MaterializationTargetDescriptor Descriptor => inner.Descriptor;

        public int SealCalls { get; private set; }

        public int ValidationCalls { get; private set; }

        public int PromotionCalls { get; private set; }

        public ValueTask<MaterializationTargetSnapshot> InspectAsync(OperationContext context) =>
            inner.InspectAsync(context);

        public async ValueTask<MaterializationGenerationSnapshot?> InspectGenerationAsync(
            OperationContext context,
            MaterializationGenerationId generationId)
        {
            generationInspections++;
            if (advancePending && generationInspections == advanceAtGenerationInspection)
            {
                advancePending = false;
                time.Advance(TimeSpan.FromMinutes(1));
            }
            return await inner.InspectGenerationAsync(context, generationId);
        }

        public ValueTask<MaterializationGenerationOperationResult> BeginGenerationAsync(
            OperationContext context,
            MaterializationBeginGenerationRequest request) =>
            inner.BeginGenerationAsync(context, request);

        public ValueTask<MaterializationBatchResult> ApplyBatchAsync(
            OperationContext context,
            MaterializationApplyBatchRequest request) =>
            inner.ApplyBatchAsync(context, request);

        public async ValueTask<MaterializationSealResult> SealGenerationAsync(
            OperationContext context,
            MaterializationSealGenerationRequest request)
        {
            SealCalls++;
            var result = await inner.SealGenerationAsync(context, request);
            CrashIfRequested(ActivationEffect.Seal);
            return result;
        }

        public async ValueTask<MaterializationValidationResult> ValidateGenerationAsync(
            OperationContext context,
            MaterializationValidateGenerationRequest request)
        {
            ValidationCalls++;
            var result = await inner.ValidateGenerationAsync(context, request);
            CrashIfRequested(ActivationEffect.Validation);
            return result;
        }

        public async ValueTask<MaterializationPromotionResult> PromoteGenerationAsync(
            OperationContext context,
            MaterializationPromoteGenerationRequest request)
        {
            PromotionCalls++;
            var result = await inner.PromoteGenerationAsync(context, request);
            CrashIfRequested(ActivationEffect.Promotion);
            return result;
        }

        public ValueTask<MaterializationAbandonmentResult> AbandonGenerationAsync(
            OperationContext context,
            MaterializationAbandonGenerationRequest request) =>
            inner.AbandonGenerationAsync(context, request);

        public ValueTask<MaterializationGenerationOperationResult> RetireGenerationAsync(
            OperationContext context,
            MaterializationRetireGenerationRequest request) =>
            inner.RetireGenerationAsync(context, request);

        public ValueTask<MaterializationCleanupResult> CleanupGenerationAsync(
            OperationContext context,
            MaterializationCleanupGenerationRequest request) =>
            inner.CleanupGenerationAsync(context, request);

        void CrashIfRequested(ActivationEffect effect)
        {
            if (!crashPending || crashAfter != effect)
                return;
            crashPending = false;
            throw new InjectedActivationCrashException(effect);
        }
    }

    sealed class InjectedActivationCrashException(ActivationEffect effect)
        : Exception($"Injected crash after the {effect} effect and before durable activation receipt persistence.");
}
