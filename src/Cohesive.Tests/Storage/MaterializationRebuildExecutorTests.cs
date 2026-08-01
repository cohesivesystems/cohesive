using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.TestFixtures;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed partial class MaterializationRebuildExecutorTests
{
    const long ReadBytes = 1_000_000;
    const long WriteBytes = 1_000_000;
    const int ReadItems = 100;
    const int WriteItems = 100;

    static readonly DateTimeOffset Epoch = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_CanonicalizesSourcesAndShardsAndRejectsStaleCapabilityMatch()
    {
        var forward = CreateFixture(reversePlanDeclarations: false);
        var reverse = CreateFixture(reversePlanDeclarations: true);

        Assert.Equal(forward.Plan.Fingerprint, reverse.Plan.Fingerprint);
        Assert.Equal(
            ["shard-a", "shard-b", "shard-c"],
            reverse.Plan.Shards.Select(static shard => shard.Id.Value));
        Assert.Equal(
            forward.Plan.Sources.Select(static source => source.Input.Value).Order(StringComparer.Ordinal),
            reverse.Plan.Sources.Select(static source => source.Input.Value));
        Assert.Equal(
            forward.Plan.ChangeFeedCatalogs.Select(static catalog => catalog.Input.Value).Order(StringComparer.Ordinal),
            reverse.Plan.ChangeFeedCatalogs.Select(static catalog => catalog.Input.Value));
        Assert.All(
            forward.Plan.ChangeFeedCatalogs,
            catalog => Assert.Equal(
                catalog.Scopes.Select(scope => MaterializationChannelSemantics.ToChannelScopeId(scope).Value)
                    .Order(StringComparer.Ordinal),
                catalog.Scopes.Select(scope => MaterializationChannelSemantics.ToChannelScopeId(scope).Value)));
        Assert.Equal(5, forward.Plan.ChangeFeeds.Length);
        Assert.Equal(
            forward.Plan.ChangeFeeds.Select(static feed => feed.Id.Value).Order(StringComparer.Ordinal),
            reverse.Plan.ChangeFeeds.Select(static feed => feed.Id.Value));
        Assert.All(
            forward.Plan.ImpactPlan.Routes,
            route => Assert.Contains(forward.Plan.ChangeFeeds, feed => feed.Scope.Input == route.ChangeInput));
        Assert.All(
            forward.Plan.Shards,
            shard => Assert.Contains(forward.Plan.ChangeFeeds, feed => feed.Scope == shard.Scope));
        Assert.Equal(
            forward.Plan.Fingerprint,
            MaterializationRebuildPlanFingerprinter.Compute(forward.Plan));

        var staleProfile = CapabilityProfile(
            id: "tests/rebuild-target/stale/v1",
            role: MaterializationEndpointRole.Target,
            subject: forward.Plan.Target.Id.Value,
            requirements: forward.Plan.Materialization.Definition.TargetCapabilities,
            realization: CapabilityRealizationKind.Constrained);
        var staleMatch = MaterializationCapabilityMatcher.MatchForMode(
            forward.Plan.Materialization.Definition.TargetCapabilities,
            staleProfile,
            MaterializationSynchronizationMode.Rebuild);

        Assert.True(staleMatch.IsSatisfied);
        Assert.Throws<ArgumentException>(() => new MaterializationRebuildPlan(
            materialization: forward.Plan.Materialization,
            impactPlan: forward.Plan.ImpactPlan,
            sources: forward.Plan.Sources,
            target: forward.Plan.Target,
            targetCapabilityMatch: staleMatch,
            shards: forward.Plan.Shards,
            changeFeedCatalogs: forward.Plan.ChangeFeedCatalogs,
            changeFeeds: forward.Plan.ChangeFeeds,
            limits: forward.Plan.Limits,
            provenance: forward.Plan.Provenance));
    }

    [Fact]
    public async Task BeginAttempt_AllocatesOneDeterministicLoadingCandidateAndPersistsEveryDependencyFeedCut()
    {
        var fixture = CreateFixture();
        var attempt = Attempt("attempt-1");

        var begun = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);
        var replayed = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);

        var expectedGeneration = MaterializationRebuildIdentities.Generation(fixture.Plan, attempt);
        Assert.Equal(MaterializationRebuildInitializationDisposition.Ready, begun.Disposition);
        Assert.Equal(expectedGeneration, begun.Generation);
        Assert.Equal(expectedGeneration, replayed.Generation);
        Assert.Equal(MaterializationGenerationState.Loading, begun.GenerationSnapshot!.State);
        Assert.Equal(fixture.Plan.ChangeFeeds.Length, begun.Progress.Length);
        Assert.All(begun.Progress, static progress =>
        {
            Assert.Null(progress.LatestBatchCheckpoint);
            Assert.Equal(
                MaterializationCheckpointKind.ChangeProgress,
                progress.LatestChangeCheckpoint?.Kind);
            Assert.NotNull(progress.LatestChangeCheckpoint?.ChannelProgress);
            Assert.NotNull(progress.LatestChangeCheckpoint?.Position);
        });
        Assert.Equal(
            begun.Progress.Select(static progress => progress.LatestChangeCheckpoint!.Id),
            replayed.Progress.Select(static progress => progress.LatestChangeCheckpoint!.Id));

        var target = await fixture.Target.InspectAsync(OperationContext.Create());
        Assert.Null(target.ActiveGenerationId);
        Assert.Equal(1, target.RetainedGenerationCount);
    }

    [Fact]
    public async Task RunShard_PartialInitializationCannotReadOrMutateBeforeEveryFeedCutExists()
    {
        var fixture = CreateFixture();
        var failingFeed = fixture.Plan.ChangeFeeds[^1];
        var failingIndex = fixture.Plan.ChangeFeeds.Length - 1;
        var runnableShard = fixture.Plan.Shards.First(shard => fixture.Plan.ChangeFeeds
            .Take(failingIndex)
            .Any(feed => feed.Scope == shard.Scope));
        var resolved = new ResolvedMaterializationRebuildPlan(
            plan: fixture.Plan,
            target: fixture.Target,
            progressStore: fixture.Resolved.ProgressStore,
            shardBindings: fixture.Plan.Shards.Select(shard => fixture.Resolved.GetShard(shard.Id)),
            changeFeedBindings: System.Linq.Enumerable.Select(fixture.Plan.ChangeFeeds, feed =>
            {
                var binding = fixture.Resolved.GetChangeFeed(feed.Id);
                return feed.Id == failingFeed.Id
                    ? new MaterializationChangeFeedBinding(
                        feed,
                        feed.Channel,
                        new CaptureFailingChangeSource(binding.Source),
                        binding.Interpreter)
                    : binding;
            }));
        var executor = new MaterializationRebuildExecutor(resolved);
        var attempt = Attempt("attempt-partial-initialization");

        await Assert.ThrowsAsync<InjectedChangeCutException>(() =>
            executor.BeginAttemptAsync(OperationContext.Create(), attempt));
        var result = await executor.RunShardAsync(
            OperationContext.Create(),
            attempt,
            runnableShard.Id);

        Assert.Equal(MaterializationRebuildShardDisposition.NotReady, result.Disposition);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == MaterializationRebuildDiagnosticCodes.InitializationIncomplete);
        Assert.All(fixture.ScanReaders.Values, static reader => Assert.Equal(0, reader.ReadCalls));
        Assert.Empty(await Items(
            fixture.Target,
            MaterializationRebuildIdentities.Generation(fixture.Plan, attempt)));
    }

    [Fact]
    public async Task RunShard_UsesStableIdempotentBulksRetainsChannelProgressAndBecomesReadyOnlyAfterAllShards()
    {
        var fixture = CreateFixture();
        var attempt = Attempt("attempt-1");
        var initialization = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);

        Assert.Null(await fixture.Executor.InspectReadinessAsync(OperationContext.Create(), attempt));
        var first = await fixture.Executor.RunShardAsync(
            OperationContext.Create(),
            attempt,
            new("shard-a"));
        var firstItems = await Items(fixture.Target, initialization.Generation);
        var replayed = await fixture.Executor.RunShardAsync(
            OperationContext.Create(),
            attempt,
            new("shard-a"));
        var replayedItems = await Items(fixture.Target, initialization.Generation);

        Assert.Equal(
            MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
            first.Disposition);
        Assert.Equal(2, first.Pages);
        Assert.Equal(3, first.Outputs);
        Assert.Equal(MaterializationCheckpointKind.BatchCompleted, first.Progress.LatestBatchCheckpoint?.Kind);
        Assert.Equal(
            initialization.Progress.Single(static progress => progress.Key.Scope.Partition.Value == "partition-a")
                .LatestChangeCheckpoint,
            first.Progress.LatestChangeCheckpoint);
        Assert.Equal(0, replayed.Pages);
        Assert.Equal(0, replayed.Outputs);
        Assert.Equal(
            firstItems.Select(static item => item.MutationId),
            replayedItems.Select(static item => item.MutationId));
        Assert.Null(await fixture.Executor.InspectReadinessAsync(OperationContext.Create(), attempt));

        await fixture.Executor.RunShardAsync(OperationContext.Create(), attempt, new("shard-b"));
        Assert.Null(await fixture.Executor.InspectReadinessAsync(OperationContext.Create(), attempt));
        await fixture.Executor.RunShardAsync(OperationContext.Create(), attempt, new("shard-c"));
        var readiness = await fixture.Executor.InspectReadinessAsync(OperationContext.Create(), attempt);

        Assert.NotNull(readiness);
        Assert.Equal(initialization.Generation, readiness.Generation);
        Assert.Equal(fixture.Plan.Fingerprint, readiness.Plan);
        Assert.Equal(3, readiness.Shards.Length);
        Assert.All(readiness.Shards, static progress =>
        {
            Assert.Equal(MaterializationCheckpointKind.BatchCompleted, progress.LatestBatchCheckpoint?.Kind);
            Assert.Equal(MaterializationCheckpointKind.ChangeProgress, progress.LatestChangeCheckpoint?.Kind);
            Assert.NotNull(progress.LatestChangeCheckpoint?.ChannelProgress);
        });
        Assert.Equal(9, (await Items(fixture.Target, initialization.Generation)).Length);
        var generation = await fixture.Target.InspectGenerationAsync(
            OperationContext.Create(),
            initialization.Generation);
        Assert.Equal(MaterializationGenerationState.Loading, generation!.State);
        Assert.Null((await fixture.Target.InspectAsync(OperationContext.Create())).ActiveGenerationId);
    }

    [Fact]
    public async Task RunShard_RejectsRetainedCompletionForAnotherExactReadIntent()
    {
        var fixture = CreateFixture();
        var attempt = Attempt("attempt-inexact-completion");
        var initialized = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);
        MaterializationRebuildShardId shardId = new("shard-a");
        var shard = fixture.Plan.Shards.Single(candidate => candidate.Id == shardId);
        var progress = initialized.Progress.Single(snapshot => snapshot.Key.Scope == shard.Scope);
        MaterializationApplicationCheckpoint forgedCompletion = new(
            id: new("checkpoint/forged-read-completion"),
            kind: MaterializationCheckpointKind.BatchCompleted,
            continuation: null,
            completion: new(
                shard.Scope,
                readFingerprint: new(
                    algorithm: "sha256",
                    canonicalization: "tests/another-rebuild-read/v1",
                    value: new string('f', 64)),
                evidenceState: RelationQuerySourceReadState.Complete,
                evidenceReference: "tests/forged-read-completion"),
            position: null,
            appliedDeliveries: [],
            committedAtUtc: Epoch.AddSeconds(1),
            batchPageOrdinal: 1);
        var saved = await fixture.Resolved.ProgressStore.SaveCheckpointAsync(
            OperationContext.Create(),
            progress.Key,
            mutationId: new("mutation/forged-read-completion"),
            expectedRevision: progress.Revision,
            owner: Assert.IsType<string>(progress.FenceOwner),
            fence: progress.Fence,
            checkpoint: forgedCompletion);

        var result = await fixture.Executor.RunShardAsync(
            OperationContext.Create(),
            attempt,
            shardId);

        Assert.Equal(MaterializationProgressMutationDisposition.Applied, saved.Disposition);
        Assert.Equal(MaterializationRebuildShardDisposition.Fenced, result.Disposition);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationRebuildDiagnosticCodes.ProgressFenced);
        Assert.Null(await fixture.Executor.InspectReadinessAsync(OperationContext.Create(), attempt));
        Assert.Empty(await Items(fixture.Target, initialized.Generation));
    }

    [Theory]
    [InlineData(MaterializationRebuildCrashPoint.AfterScan)]
    [InlineData(MaterializationRebuildCrashPoint.AfterHydration)]
    [InlineData(MaterializationRebuildCrashPoint.AfterBulk)]
    [InlineData(MaterializationRebuildCrashPoint.AfterCheckpoint)]
    public async Task CrashAtEveryPageBoundary_ResumesSameGenerationWithoutDuplicateEffects(
        MaterializationRebuildCrashPoint crashPoint)
    {
        var crash = new ThrowOnceCrashInjector(crashPoint);
        var fixture = CreateFixture(crashInjector: crash);
        var attempt = Attempt("attempt-crash");
        var begun = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);

        await Assert.ThrowsAsync<InjectedCrashException>(async () => await fixture.Executor.RunShardAsync(
            OperationContext.Create(),
            attempt,
            new("shard-a")));

        var resumedExecutor = new MaterializationRebuildExecutor(fixture.Resolved);
        var resumed = await resumedExecutor.RunShardAsync(
            OperationContext.Create(),
            attempt,
            new("shard-a"));
        var items = await Items(fixture.Target, begun.Generation);
        var replayed = await resumedExecutor.RunShardAsync(
            OperationContext.Create(),
            attempt,
            new("shard-a"));
        var replayedItems = await Items(fixture.Target, begun.Generation);

        Assert.Equal(
            MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
            resumed.Disposition);
        Assert.Equal(begun.Generation, resumed.Generation);
        Assert.Equal(3, items.Length);
        Assert.Equal(3, items.Select(static item => item.ItemId).Distinct().Count());
        Assert.Equal(3, items.Select(static item => item.MutationId).Distinct().Count());
        Assert.Equal(
            items.Select(static item => item.MutationId),
            replayedItems.Select(static item => item.MutationId));
        Assert.Equal(0, replayed.Pages);
        Assert.Equal(0, replayed.Outputs);
        Assert.Equal(1, crash.ThrownCount);
    }

    [Fact]
    public async Task CrashAfterBulk_WithChangedLivePage_RequiresFreshAttemptAndGeneration()
    {
        var crash = new ThrowOnceCrashInjector(MaterializationRebuildCrashPoint.AfterBulk);
        var fixture = CreateFixture(crashInjector: crash);
        crash.BeforeThrow = () => fixture.ScanReaders[new("shard-a")]
            .ReplaceFirstObservationIdentity("load/replaced-after-uncheckpointed-bulk");
        var firstAttempt = Attempt("attempt-live-page-drift");
        var first = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), firstAttempt);

        await Assert.ThrowsAsync<InjectedCrashException>(async () => await fixture.Executor.RunShardAsync(
            OperationContext.Create(),
            firstAttempt,
            new("shard-a")));

        var resumedExecutor = new MaterializationRebuildExecutor(fixture.Resolved);
        var unsafeResume = await resumedExecutor.RunShardAsync(
            OperationContext.Create(),
            firstAttempt,
            new("shard-a"));

        Assert.Equal(MaterializationRebuildShardDisposition.RestartRequired, unsafeResume.Disposition);
        Assert.Contains(
            unsafeResume.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationRebuildDiagnosticCodes.SourceReplayDrift);
        Assert.Null(unsafeResume.Progress.LatestBatchCheckpoint);
        Assert.Equal(
            MaterializationGenerationState.Loading,
            (await fixture.Target.InspectGenerationAsync(OperationContext.Create(), first.Generation))!.State);
        Assert.Null((await fixture.Target.InspectAsync(OperationContext.Create())).ActiveGenerationId);

        Assert.True(await resumedExecutor.AbandonAttemptAsync(
            OperationContext.Create(),
            firstAttempt,
            Epoch.AddMinutes(1)));
        var replacementAttempt = Attempt("attempt-live-page-drift-replacement");
        var replacement = await resumedExecutor.BeginAttemptAsync(OperationContext.Create(), replacementAttempt);
        var completed = await resumedExecutor.RunShardAsync(
            OperationContext.Create(),
            replacementAttempt,
            new("shard-a"));

        Assert.NotEqual(first.Generation, replacement.Generation);
        Assert.Equal(
            MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
            completed.Disposition);
        Assert.Equal(
            MaterializationGenerationState.Retired,
            (await fixture.Target.InspectGenerationAsync(OperationContext.Create(), first.Generation))!.State);
        Assert.Equal(
            MaterializationGenerationState.Loading,
            (await fixture.Target.InspectGenerationAsync(OperationContext.Create(), replacement.Generation))!.State);
        Assert.Null((await fixture.Target.InspectAsync(OperationContext.Create())).ActiveGenerationId);
    }

    [Fact]
    public async Task MaximumPagesPerShard_RemainsCumulativeAcrossCheckpointedCrashResumes()
    {
        var firstCrash = new ThrowOnceCrashInjector(MaterializationRebuildCrashPoint.AfterCheckpoint);
        var fixture = CreateFixture(
            crashInjector: firstCrash,
            maximumPageItems: 1,
            maximumPagesPerShard: 2);
        var attempt = Attempt("attempt-cumulative-page-boundary");
        var initialized = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);

        await Assert.ThrowsAsync<InjectedCrashException>(async () => await fixture.Executor.RunShardAsync(
            OperationContext.Create(),
            attempt,
            new("shard-a")));
        var secondCrash = new ThrowOnceCrashInjector(MaterializationRebuildCrashPoint.AfterCheckpoint);
        var secondInvocation = new MaterializationRebuildExecutor(fixture.Resolved, secondCrash);
        await Assert.ThrowsAsync<InjectedCrashException>(async () => await secondInvocation.RunShardAsync(
            OperationContext.Create(),
            attempt,
            new("shard-a")));

        var bounded = await new MaterializationRebuildExecutor(fixture.Resolved).RunShardAsync(
            OperationContext.Create(),
            attempt,
            new("shard-a"));

        Assert.Equal(MaterializationRebuildShardDisposition.BoundaryExceeded, bounded.Disposition);
        Assert.Equal(2, bounded.Progress.LatestBatchCheckpoint?.BatchPageOrdinal);
        Assert.Equal(
            MaterializationCheckpointKind.BatchContinuation,
            bounded.Progress.LatestBatchCheckpoint?.Kind);
        Assert.Equal(2, (await Items(fixture.Target, initialized.Generation)).Length);
        Assert.Equal(
            MaterializationGenerationState.Loading,
            (await fixture.Target.InspectGenerationAsync(OperationContext.Create(), initialized.Generation))!.State);
        Assert.Null((await fixture.Target.InspectAsync(OperationContext.Create())).ActiveGenerationId);
    }

    [Theory]
    [InlineData(RelationQuerySourceReadState.Partial, MaterializationRebuildShardDisposition.BoundaryExceeded)]
    [InlineData(RelationQuerySourceReadState.Failed, MaterializationRebuildShardDisposition.SourceOrHydrationFailed)]
    [InlineData(RelationQuerySourceReadState.Inconclusive, MaterializationRebuildShardDisposition.SourceOrHydrationFailed)]
    public async Task NonAuthoritativeExhaustedPage_FailsBeforeHydrationOrTargetWrites(
        RelationQuerySourceReadState sourceState,
        MaterializationRebuildShardDisposition expectedDisposition)
    {
        var fixture = CreateFixture();
        var attempt = Attempt($"attempt-terminal-{sourceState}");
        var initialized = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);
        MaterializationRebuildShardId selectedShard = new("shard-a");
        const string sourceDiagnosticCode = "tests.rebuild.source.boundary";
        var bindings = fixture.Plan.Shards.Select(shard =>
        {
            var retained = fixture.Resolved.GetShard(shard.Id);
            return shard.Id == selectedShard
                ? new MaterializationRebuildShardBinding(
                    shard,
                    new NonAuthoritativeTerminalSource(
                        retained.Source,
                        sourceState,
                        sourceDiagnosticCode),
                    retained.Hydrator)
                : retained;
        });
        var executor = new MaterializationRebuildExecutor(new(
            fixture.Plan,
            fixture.Target,
            fixture.Resolved.ProgressStore,
            bindings,
            fixture.Plan.ChangeFeeds.Select(feed => fixture.Resolved.GetChangeFeed(feed.Id))));

        var result = await executor.RunShardAsync(
            OperationContext.Create(),
            attempt,
            selectedShard);

        Assert.Equal(expectedDisposition, result.Disposition);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == sourceDiagnosticCode);
        Assert.Empty(await Items(fixture.Target, initialized.Generation));
        Assert.Null(result.Progress.LatestBatchCheckpoint);
        Assert.Equal(
            MaterializationGenerationState.Loading,
            (await fixture.Target.InspectGenerationAsync(OperationContext.Create(), initialized.Generation))!.State);
        Assert.Null((await fixture.Target.InspectAsync(OperationContext.Create())).ActiveGenerationId);
    }

    [Fact]
    public async Task AbandonAndRestart_RetiresOldCandidateAndCreatesExactlyOneReplacementGeneration()
    {
        var fixture = CreateFixture();
        var firstAttempt = Attempt("attempt-1");
        var first = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), firstAttempt);
        await fixture.Executor.RunShardAsync(OperationContext.Create(), firstAttempt, new("shard-a"));

        Assert.True(await fixture.Executor.AbandonAttemptAsync(
            OperationContext.Create(),
            firstAttempt,
            Epoch.AddMinutes(1)));
        Assert.True(await fixture.Executor.AbandonAttemptAsync(
            OperationContext.Create(),
            firstAttempt,
            Epoch.AddMinutes(1)));
        Assert.Equal(
            MaterializationGenerationState.Retired,
            (await fixture.Target.InspectGenerationAsync(OperationContext.Create(), first.Generation))!.State);

        var replacementAttempt = Attempt("attempt-2");
        var replacement = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), replacementAttempt);
        var replacementReplay = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), replacementAttempt);

        Assert.NotEqual(first.Generation, replacement.Generation);
        Assert.Equal(replacement.Generation, replacementReplay.Generation);
        Assert.Equal(MaterializationGenerationState.Loading, replacement.GenerationSnapshot!.State);
        var target = await fixture.Target.InspectAsync(OperationContext.Create());
        Assert.Null(target.ActiveGenerationId);
        Assert.Equal(2, target.RetainedGenerationCount);
    }

    [Fact]
    public async Task AbandonBeforeBegin_TombstonesAttemptAndRejectsDelayedInitialization()
    {
        var fixture = CreateFixture();
        var attempt = Attempt("attempt-abandoned-before-begin");
        var generation = MaterializationRebuildIdentities.Generation(fixture.Plan, attempt);
        var abandonedAtUtc = Epoch.AddMinutes(1);

        var abandoned = await fixture.Executor.AbandonAttemptAsync(
            OperationContext.Create(),
            attempt,
            abandonedAtUtc);
        var replayed = await fixture.Executor.AbandonAttemptAsync(
            OperationContext.Create(),
            attempt,
            abandonedAtUtc);
        var delayedInitialization = await fixture.Executor.BeginAttemptAsync(
            OperationContext.Create(),
            attempt);

        Assert.True(abandoned);
        Assert.True(replayed);
        Assert.Equal(MaterializationRebuildInitializationDisposition.TargetRejected, delayedInitialization.Disposition);
        Assert.Equal(generation, delayedInitialization.Generation);
        Assert.Null(delayedInitialization.GenerationSnapshot);
        Assert.NotEmpty(delayedInitialization.Diagnostics);
        Assert.Null(await fixture.Target.InspectGenerationAsync(OperationContext.Create(), generation));
        Assert.Equal(0, (await fixture.Target.InspectAsync(OperationContext.Create())).RetainedGenerationCount);
    }

    [Fact]
    public async Task BeginAfterCandidateAbandonment_DoesNotProjectHistoricalBeginReceiptAsReady()
    {
        var fixture = CreateFixture();
        var attempt = Attempt("attempt-abandoned-after-begin");
        var initialized = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);

        Assert.True(await fixture.Executor.AbandonAttemptAsync(
            OperationContext.Create(),
            attempt,
            Epoch.AddMinutes(1)));
        var delayedReplay = await fixture.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);

        Assert.Equal(MaterializationRebuildInitializationDisposition.TargetRejected, delayedReplay.Disposition);
        Assert.Equal(initialized.Generation, delayedReplay.Generation);
        Assert.Equal(MaterializationGenerationState.Retired, delayedReplay.GenerationSnapshot?.State);
        Assert.NotEmpty(delayedReplay.Diagnostics);
    }

    [Fact]
    public void ProductionHydrator_RejectsOutputFromMismatchedPlan()
    {
        var fixture = CreateFixture();
        var selected = fixture.Plan.Materialization.Definition.Relation.Output;
        var mismatched = new RelationQueryOutputReference(
            id: new("tests/mismatched-output"),
            kind: RelationQueryOutputReferenceKind.Relation,
            node: selected.Node,
            shape: selected.Shape,
            relation: new("tests/mismatched-relation"));

        Assert.Throws<ArgumentException>(() => new RelationQueryMaterializationRebuildHydrator(
            plan: fixture.Semantic.Plan,
            physicalPlan: fixture.Semantic.PhysicalPlan,
            realization: fixture.Semantic.Realization,
            suppliedRoot: fixture.Root.Input.Id,
            output: mismatched,
            sourceReaders: fixture.Readers));
    }

    [Fact]
    public void ProductionHydrator_RejectsMismatchedRealizationAndPhysicalPlanChain()
    {
        var fixture = CreateFixture();
        var mismatched = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.AggregationDocument);

        Assert.Throws<ArgumentException>(() => new RelationQueryMaterializationRebuildHydrator(
            plan: fixture.Semantic.Plan,
            physicalPlan: mismatched.PhysicalPlan,
            realization: mismatched.Realization,
            suppliedRoot: fixture.Root.Input.Id,
            output: fixture.Plan.Materialization.Definition.Relation.Output,
            sourceReaders: fixture.Readers));
    }

    [Fact]
    public void RuntimeBinding_RejectsHydratorForAnotherPersistedPhysicalLowering()
    {
        var fixture = CreateFixture();
        var retained = fixture.Resolved.GetShard(new("shard-a"));
        var mismatchedHydrator = new TestHydrator(
            retained.Hydrator.Plan,
            new(
                algorithm: "sha256",
                canonicalization: retained.Hydrator.PhysicalPlan.Canonicalization,
                value: new string('f', 64)),
            fixture.Plan.Materialization.Definition.Relation.Output.Shape);

        Assert.Throws<ArgumentException>(() => new MaterializationRebuildShardBinding(
            retained.Shard,
            retained.Source,
            mismatchedHydrator));
    }

    static RebuildFixture CreateFixture(
        bool reversePlanDeclarations = false,
        IMaterializationRebuildCrashInjector? crashInjector = null,
        int maximumPageItems = 2,
        int maximumPagesPerShard = 10,
        bool transactionAlignedChangeDelivery = false)
    {
        RelationQueryCompilationRequest compilationRequest = new(
            FederatedLoadRelationFixture.RelationDocument,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument);
        var semantic = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RelationDocument);
        var root = Assert.Single(semantic.Plan.InputContract.Sources);
        var output = Assert.Single(
            semantic.Plan.RequirementGraph.Outputs,
            static candidate => candidate.Field is null);
        var relation = MaterializationRelationReference.From(compilationRequest, output.Id);
        var definition = Definition(semantic.Plan, relation);
        var materialization = MaterializationDocument.FromDefinition(definition);
        var sourceIds = SourceIdentities(semantic.Plan, root);
        var sourcePlans = definition.Sources.Select(requirement =>
        {
            var source = sourceIds[requirement.Input];
            var profile = CapabilityProfile(
                id: $"tests/rebuild-source/{Uri.EscapeDataString(requirement.Input.Value)}/v1",
                role: MaterializationEndpointRole.Source,
                subject: source.Value,
                requirements: requirement.Capabilities,
                transactionAlignedChangeDelivery: transactionAlignedChangeDelivery);
            return new MaterializationRebuildSourcePlan(
                input: requirement.Input,
                source: source,
                profile: profile,
                capabilityMatch: MaterializationCapabilityMatcher.MatchForMode(
                    requirement.Capabilities,
                    profile,
                    MaterializationSynchronizationMode.Rebuild));
        }).ToImmutableArray();
        var rootSourcePlan = sourcePlans.Single(source => source.Input == root.Input.Id);
        var targetId = new MaterializationTargetId("tests/rebuild-target");
        var targetProfile = CapabilityProfile(
            id: "tests/rebuild-target/v1",
            role: MaterializationEndpointRole.Target,
            subject: targetId.Value,
            requirements: definition.TargetCapabilities);
        var targetDescriptor = new MaterializationTargetDescriptor(
            id: targetId,
            materializationId: definition.Id,
            capabilities: targetProfile);
        var targetMatch = MaterializationCapabilityMatcher.MatchForMode(
            definition.TargetCapabilities,
            targetProfile,
            MaterializationSynchronizationMode.Rebuild);
        var scanPlacement = new RelationQuerySourcePlacementBinding(
            id: new("tests/rebuild-scan-placement"),
            input: root.Input.Id,
            node: root.Node,
            binding: root.Binding,
            shape: root.Shape,
            source: rootSourcePlan.Source,
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new RelationQuerySourceIdentityBinding(root.Shape, "id"));
        var scanFingerprint = new RelationQueryPhysicalPlanFingerprint(
            algorithm: "sha256",
            canonicalization: "tests/rebuild-scan/v1",
            value: "0123456789abcdef");
        var shardIds = new[] { "shard-a", "shard-b", "shard-c" };
        var shards = shardIds.Select(shardId =>
        {
            var suffix = shardId[^1];
            var scope = new MaterializationSourceScope(
                physicalPlan: scanFingerprint,
                placement: scanPlacement,
                partition: new($"partition-{suffix}"),
                orderingScope: new($"ordering-{suffix}"));
            var read = new RelationQuerySourceReadRequest(
                physicalPlan: scanFingerprint,
                stage: new("tests/rebuild-scan"),
                placementBinding: scanPlacement.Id,
                source: rootSourcePlan.Source,
                shape: root.Shape,
                identitySelector: "id",
                fields: [],
                constraint: new RelationQueryBoundedEnumeration(maximumRows: ReadItems),
                maximumBufferedRows: ReadItems);
            return new MaterializationRebuildShardPlan(
                id: new(shardId),
                scope: scope,
                read: read,
                hydrationPhysicalPlan: semantic.PhysicalPlan.Fingerprint);
        }).ToImmutableArray();
        var impactPlan = MaterializationRebuildTestPlan.CompileImpactPlan(
            materialization,
            policyId: "tests/materialization-rebuild-impact/v1",
            maximumAffectedRoots: ReadItems,
            maximumReadBytes: ReadBytes);
        var changeFeedCatalog = MaterializationRebuildTestPlan.CreateChangeFeedCatalog(
            semantic.Plan,
            semantic.PhysicalPlan.Fingerprint,
            impactPlan,
            sourcePlans,
            shards,
            contributorPlacement: route => semantic.Placement.Bindings.Single(candidate =>
                candidate.Input == route.ChangeInput),
            channelCanonicalization: "tests/materialization-rebuild-channel/v1");
        var changeFeedCatalogs = changeFeedCatalog.Evidence;
        var changeFeeds = changeFeedCatalog.Feeds;
        if (reversePlanDeclarations)
        {
            sourcePlans = sourcePlans.Reverse().ToImmutableArray();
            shards = shards.Reverse().ToImmutableArray();
            changeFeedCatalogs = changeFeedCatalogs.Reverse().ToImmutableArray();
            changeFeeds = changeFeeds.Reverse().ToImmutableArray();
        }
        var limits = new MaterializationRebuildLimits(
            maximumPageItems: maximumPageItems,
            maximumPageBytes: ReadBytes,
            maximumBulkItems: 2,
            maximumBulkBytes: WriteBytes,
            maximumPagesPerShard: maximumPagesPerShard,
            maximumStartsPerActivation: 2,
            maximumParallelism: 2,
            maximumChangeFeedsPerConvergenceActivation: 16);
        var plan = new MaterializationRebuildPlan(
            materialization: materialization,
            impactPlan: impactPlan,
            sources: sourcePlans,
            target: targetDescriptor,
            targetCapabilityMatch: targetMatch,
            shards: shards,
            changeFeedCatalogs: changeFeedCatalogs,
            changeFeeds: changeFeeds,
            limits: limits,
            provenance: new(
                producer: new("tests/materialization-rebuild-plan"),
                source: new("tests/ari-176"),
                origin: DocumentOrigin.User));
        var physicalScenario = FederatedLoadConformanceData.CreatePhysicalScenario(
            semantic,
            rootCount: 9,
            distinctCustomerCount: 3,
            distinctEquipmentCount: 3);
        var hydrator = new TestHydrator(
            plan: RelationQueryCompiledPlanReference.From(semantic.Plan),
            physicalPlan: semantic.PhysicalPlan.Fingerprint,
            outputShape: output.Shape);
        var rootPhysicalSource = FederatedLoadPhysicalExecutionFixture.Source(
            semantic,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var sourceByShard = new Dictionary<MaterializationRebuildShardId, InMemoryMaterializationSource>();
        var scanReaders = ImmutableDictionary.CreateBuilder<MaterializationRebuildShardId, StaticReader>();
        for (var index = 0; index < shardIds.Length; index++)
        {
            var observations = physicalScenario.SuppliedLoads.Observations.Slice(index * 3, 3);
            var reader = new StaticReader(
                new RelationQuerySourceReaderDescriptor(
                    source: rootPhysicalSource.Id,
                    executionDomain: rootPhysicalSource.ExecutionDomain,
                    targetProfile: rootPhysicalSource.TargetProfile),
                new RelationQuerySourceReadResult(
                    state: RelationQuerySourceReadState.Complete,
                    observations: observations,
                    evidenceReference: $"tests/rebuild-source/{shardIds[index]}"));
            var shardId = new MaterializationRebuildShardId(shardIds[index]);
            scanReaders.Add(shardId, reader);
            sourceByShard.Add(
                shardId,
                new InMemoryMaterializationSource(
                    new MaterializationQuerySourceDescriptor(reader, rootSourcePlan.Profile)));
        }
        var target = new InMemoryMaterializationTarget(targetDescriptor);
        var progressStore = new InMemoryMaterializationProgressStore();
        var impactInterpreter = new MaterializationImpactPlanInterpreter(
            plan.ImpactPlan,
            definition,
            new MaterializationTestImpactRuntime(plan.ImpactPlan.Fingerprint));
        var sourceByFeed = plan.ChangeFeeds.ToDictionary(
            static feed => feed.Id,
            feed =>
            {
                var matchingShard = plan.Shards.SingleOrDefault(shard => shard.Scope == feed.Scope);
                if (matchingShard is not null)
                    return sourceByShard[matchingShard.Id];

                var sourcePlan = plan.Sources.Single(source => source.Input == feed.Scope.Input);
                var reader = physicalScenario.Readers.Single(candidate =>
                    candidate.Descriptor.Source == sourcePlan.Source);
                return new InMemoryMaterializationSource(
                    new MaterializationQuerySourceDescriptor(reader, sourcePlan.Profile));
            });
        var resolved = new ResolvedMaterializationRebuildPlan(
            plan: plan,
            target: target,
            progressStore: progressStore,
            shardBindings: plan.Shards.Select(shard => new MaterializationRebuildShardBinding(
                shard: shard,
                source: sourceByShard[shard.Id],
                hydrator: hydrator)),
            changeFeedBindings: plan.ChangeFeeds.Select(feed => new MaterializationChangeFeedBinding(
                feed: feed,
                channel: feed.Channel,
                source: sourceByFeed[feed.Id],
                interpreter: impactInterpreter)));
        return new(
            plan,
            resolved,
            new MaterializationRebuildExecutor(resolved, crashInjector),
            target,
            semantic,
            root,
            physicalScenario.Readers,
            scanReaders.ToImmutable());
    }

    static MaterializationDefinition Definition(
        CompiledRelationQueryPlan plan,
        MaterializationRelationReference relation)
    {
        ImmutableArray<MaterializationSourceRequirement> sources =
        [
            .. plan.InputContract.Sources.Select(source => SourceRequirement(
                source.Input.Id,
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                isRoot: source.Role == RelationQuerySourceInputRole.RelationRoot)),
            .. plan.InputContract.Traversals.Select(traversal => SourceRequirement(
                traversal.Input.Id,
                traversal.Input.Direction == RelationshipTraversalDirection.Forward
                    ? MaterializationCapabilityKind.SourceBatchedPointRead
                    : MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                isRoot: false))
        ];
        ImmutableArray<MaterializationCapabilityRequirement> target =
        [
            Requirement("target/isolation", MaterializationCapabilityKind.TargetGenerationIsolation, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/upsert", MaterializationCapabilityKind.TargetBulkUpsert, MaterializationSynchronizationMode.All),
            Requirement("target/delete", MaterializationCapabilityKind.TargetBulkDelete, MaterializationSynchronizationMode.All),
            Requirement("target/outcomes", MaterializationCapabilityKind.TargetPerItemOutcomes, MaterializationSynchronizationMode.All),
            Requirement("target/seal", MaterializationCapabilityKind.TargetSeal, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/validation", MaterializationCapabilityKind.TargetValidation, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/promotion", MaterializationCapabilityKind.TargetFencedPromotion, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/retirement", MaterializationCapabilityKind.TargetRetirement, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/abandonment", MaterializationCapabilityKind.TargetGenerationAbandonment, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/cleanup", MaterializationCapabilityKind.TargetCleanup, MaterializationSynchronizationMode.Rebuild)
        ];
        return new(
            id: new("tests/load-search"),
            relation: relation,
            sources: sources,
            targetCapabilities: target,
            updatePolicy: new(
                supportedModes: MaterializationSynchronizationMode.All,
                consistency: MaterializationConsistencyKind.BaselinePlusCatchUp,
                idempotency: MaterializationIdempotencyKind.StableOutputIdentityAndVersion),
            failurePolicy: new(
                maximumAttempts: 3,
                exhaustedDisposition: MaterializationFailureDisposition.Stop),
            freshnessPolicy: new(
                maximumLagMilliseconds: 30_000,
                maximumUnsettledMilliseconds: 10_000),
            controlLoops: [],
            provenance: new(
                producer: new("tests/materialization-rebuild"),
                source: new("tests/ari-176"),
                origin: DocumentOrigin.User));
    }

    static MaterializationSourceRequirement SourceRequirement(
        RelationQueryInputId input,
        MaterializationCapabilityKind readCapability,
        bool isRoot)
    {
        ImmutableArray<MaterializationCapabilityRequirement> capabilities =
        [
            Requirement($"{input.Value}/read", readCapability, MaterializationSynchronizationMode.Rebuild),
            Requirement($"{input.Value}/continuation", MaterializationCapabilityKind.SourceContinuation, MaterializationSynchronizationMode.Rebuild),
            Requirement($"{input.Value}/changes", MaterializationCapabilityKind.SourceChangeDelivery, MaterializationSynchronizationMode.All),
            Requirement($"{input.Value}/settlement", MaterializationCapabilityKind.SourceSettlement, MaterializationSynchronizationMode.All)
        ];
        if (isRoot)
        {
            capabilities = capabilities.Add(Requirement(
                $"{input.Value}/inverse",
                MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                MaterializationSynchronizationMode.Incremental));
        }

        return new(input, capabilities);
    }

    static MaterializationCapabilityRequirement Requirement(
        string id,
        MaterializationCapabilityKind capability,
        MaterializationSynchronizationMode modes) =>
        new(
            id: new(id),
            capability: capability,
            guarantees: Guarantees(capability),
            operatingLimits: Limits(capability),
            modes: modes);

    static MaterializationCapabilityProfile CapabilityProfile(
        string id,
        MaterializationEndpointRole role,
        string subject,
        ImmutableArray<MaterializationCapabilityRequirement> requirements,
        CapabilityRealizationKind realization = CapabilityRealizationKind.Native,
        bool transactionAlignedChangeDelivery = false) =>
        new(
            id: new(id),
            role: role,
            subject: subject,
            evidence:
            [
                .. requirements.Select(requirement => new MaterializationCapabilityEvidence(
                    id: new($"evidence/{Uri.EscapeDataString(requirement.Id.Value)}"),
                    capability: requirement.Capability,
                    realization: realization,
                    guarantees: transactionAlignedChangeDelivery
                        && requirement.Capability == MaterializationCapabilityKind.SourceChangeDelivery
                            ? requirement.Guarantees.Add(MaterializationGuaranteeKind.TransactionAlignedDelivery)
                            : requirement.Guarantees,
                    operatingLimits: transactionAlignedChangeDelivery
                        && requirement.Capability == MaterializationCapabilityKind.SourceChangeDelivery
                            ? requirement.OperatingLimits
                                .Add(new(MaterializationLimitKind.TransactionItems, 2))
                                .Add(new(MaterializationLimitKind.TransactionBytes, 100_000))
                            : requirement.OperatingLimits,
                    sourceReferences: ["tests/ari-176-reference-adapter/v1"]))
            ]);

    static ImmutableArray<MaterializationGuaranteeKind> Guarantees(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.RequestLocalCompleteness
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.AtLeastOnceDelivery,
                    MaterializationGuaranteeKind.BaselinePlusCatchUp,
                    MaterializationGuaranteeKind.CompleteMutationDelivery
                ],
            MaterializationCapabilityKind.SourceSettlement =>
                [MaterializationGuaranteeKind.ExplicitSettlement],
            MaterializationCapabilityKind.TargetGenerationIsolation =>
                [
                    MaterializationGuaranteeKind.GenerationIsolation,
                    MaterializationGuaranteeKind.FencedMutation
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete =>
                [
                    MaterializationGuaranteeKind.IdempotentWrite,
                    MaterializationGuaranteeKind.FencedMutation,
                    MaterializationGuaranteeKind.VersionConditionalWrite
                ],
            MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [MaterializationGuaranteeKind.ExactPerItemOutcome],
            MaterializationCapabilityKind.TargetFencedPromotion =>
                [
                    MaterializationGuaranteeKind.AtomicPromotion,
                    MaterializationGuaranteeKind.FencedPromotion
                ],
            MaterializationCapabilityKind.TargetGenerationAbandonment =>
                [MaterializationGuaranteeKind.AtomicDurableGenerationExclusion],
            MaterializationCapabilityKind.TargetSeal
                or MaterializationCapabilityKind.TargetValidation
                or MaterializationCapabilityKind.TargetRetirement
                or MaterializationCapabilityKind.TargetCleanup =>
                [MaterializationGuaranteeKind.FencedMutation],
            _ => []
        };

    static ImmutableArray<MaterializationOperatingLimit> Limits(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    new(MaterializationLimitKind.ReadItems, ReadItems),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    new(MaterializationLimitKind.ChangeItems, ReadItems),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [
                    new(MaterializationLimitKind.WriteItems, WriteItems),
                    new(MaterializationLimitKind.WriteBytes, WriteBytes)
                ],
            _ => []
        };

    static ImmutableDictionary<RelationQueryInputId, RelationQuerySourceInstanceId> SourceIdentities(
        CompiledRelationQueryPlan plan,
        RelationQuerySourceInputContract root)
    {
        var builder = ImmutableDictionary.CreateBuilder<RelationQueryInputId, RelationQuerySourceInstanceId>();
        builder.Add(root.Input.Id, FederatedLoadPhysicalExecutionFixture.LoadsSource);
        foreach (var traversal in plan.InputContract.Traversals)
        {
            var source = traversal.ResultShape == FederatedLoadRelationFixture.CustomerShapeId
                ? FederatedLoadPhysicalExecutionFixture.CustomersSource
                : FederatedLoadPhysicalExecutionFixture.EquipmentSource;
            builder.Add(traversal.Input.Id, source);
        }
        return builder.ToImmutable();
    }

    static MaterializationRebuildAttempt Attempt(string attempt) =>
        new(
            continuation: new(
                processInstanceId: new("process/rebuild-load-search"),
                processAttemptId: new(attempt)),
            startedAtUtc: Epoch);

    static async Task<ImmutableArray<InMemoryMaterializationTargetItemSnapshot>> Items(
        InMemoryMaterializationTarget target,
        MaterializationGenerationId generation)
    {
        var page = await target.InspectItemsAsync(
            OperationContext.Create(),
            generation,
            afterItemId: null,
            maximumItems: 100);
        return page?.Items ?? [];
    }

    sealed record RebuildFixture(
        MaterializationRebuildPlan Plan,
        ResolvedMaterializationRebuildPlan Resolved,
        MaterializationRebuildExecutor Executor,
        InMemoryMaterializationTarget Target,
        FederatedLoadPhysicalExecutionFixture.Compilation Semantic,
        RelationQuerySourceInputContract Root,
        ImmutableArray<IRelationQuerySourceReader> Readers,
        ImmutableDictionary<MaterializationRebuildShardId, StaticReader> ScanReaders);

    sealed class StaticReader(
        RelationQuerySourceReaderDescriptor descriptor,
        RelationQuerySourceReadResult result) : IRelationQuerySourceReader
    {
        RelationQuerySourceReadResult current = result;

        public RelationQuerySourceReaderDescriptor Descriptor { get; } = descriptor;

        public string FirstObservationIdentity => current.Observations[0].Identity;

        public int ReadCalls { get; private set; }

        public void ReplaceFirstObservationIdentity(string identity)
        {
            if (current.Observations.IsDefaultOrEmpty)
                throw new InvalidOperationException("A mutable test reader requires at least one observation.");
            var first = current.Observations[0];
            var replacement = new RelationQuerySourceReadObservation(
                identity,
                first.Shape,
                first.Fields);
            current = new(
                current.State,
                current.Observations.SetItem(index: 0, item: replacement),
                current.EvidenceReference);
        }

        public ValueTask<RelationQuerySourceReadResult> ReadAsync(
            RelationQuerySourceReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            return ValueTask.FromResult(current);
        }
    }

    sealed class CaptureFailingChangeSource(IMaterializationPullChangeSource inner)
        : IMaterializationPullChangeSource
    {
        public MaterializationQuerySourceDescriptor Descriptor => inner.Descriptor;

        public ValueTask<MaterializationSourcePage> ReadPageAsync(
            OperationContext context,
            MaterializationSourcePageRequest request) =>
            inner.ReadPageAsync(context, request);

        public ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
            OperationContext context,
            MaterializationSourceScope scope) =>
            ValueTask.FromException<MaterializationSourcePosition>(new InjectedChangeCutException());

        public ValueTask<MaterializationChangePage> ReadChangesAsync(
            OperationContext context,
            MaterializationChangeReadRequest request) =>
            inner.ReadChangesAsync(context, request);
    }

    sealed class InjectedChangeCutException()
        : Exception("Injected change-feed cut capture failure.");

    sealed class TestHydrator(
        RelationQueryCompiledPlanReference plan,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        QualifiedShapeId outputShape) : IMaterializationRebuildHydrator
    {
        public RelationQueryCompiledPlanReference Plan { get; } = plan;

        public RelationQueryPhysicalPlanFingerprint PhysicalPlan { get; } = physicalPlan;

        public ValueTask<MaterializationRebuildHydrationResult> HydrateAsync(
            OperationContext context,
            MaterializationRebuildHydrationRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            ImmutableArray<RelationQueryOutputRow> rows =
            [
                .. request.Page.Read.Observations.Select(observation => new RelationQueryOutputRow(
                    shape: outputShape,
                    value: ObservationValue.FromObject(
                        new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                        {
                            ["id"] = ObservationValue.FromString(observation.Identity)
                        }),
                    identity: ObservationValue.FromString(observation.Identity),
                    root: null,
                    inputOccurrences: [],
                    unresolvedGaps: []))
            ];
            return ValueTask.FromResult(new MaterializationRebuildHydrationResult(
                rows: rows,
                evidenceReference: request.Evaluation.Value));
        }
    }

    sealed class NonAuthoritativeTerminalSource(
        IMaterializationPullChangeSource inner,
        RelationQuerySourceReadState state,
        string diagnosticCode) : IMaterializationPullChangeSource
    {
        public MaterializationQuerySourceDescriptor Descriptor => inner.Descriptor;

        public async ValueTask<MaterializationSourcePage> ReadPageAsync(
            OperationContext context,
            MaterializationSourcePageRequest request)
        {
            var page = await inner.ReadPageAsync(context, request);
            var observations = state == RelationQuerySourceReadState.Partial
                ? page.Read.Observations
                : [];
            return new(
                page.Scope,
                page.ReadFingerprint,
                new(state, observations, page.Read.EvidenceReference),
                MaterializationSourcePageState.Exhausted,
                continuation: null,
                diagnostics:
                [
                    MaterializationContract.CreateDiagnostic(
                        diagnosticCode,
                        DiagnosticSeverity.Error,
                        "The source reached a configured terminal boundary.",
                        "/source/page",
                        "tests-materialization-rebuild-source",
                        request.Scope.Partition.Value,
                        ["tests/non-authoritative-terminal-source"],
                        "authoritative terminal source evidence",
                        state.ToString())
                ]);
        }

        public ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
            OperationContext context,
            MaterializationSourceScope scope) =>
            inner.CaptureCurrentPositionAsync(context, scope);

        public ValueTask<MaterializationChangePage> ReadChangesAsync(
            OperationContext context,
            MaterializationChangeReadRequest request) =>
            inner.ReadChangesAsync(context, request);
    }

    sealed class ThrowOnceCrashInjector(MaterializationRebuildCrashPoint point)
        : IMaterializationRebuildCrashInjector
    {
        bool thrown;

        public int ThrownCount { get; private set; }

        public Action? BeforeThrow { get; set; }

        public ValueTask ObserveAsync(
            OperationContext context,
            MaterializationRebuildCrashObservation observation)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(observation);
            context.ThrowIfCancellationRequested();
            if (!thrown && observation.Point == point)
            {
                thrown = true;
                BeforeThrow?.Invoke();
                ThrownCount++;
                throw new InjectedCrashException(point);
            }
            return ValueTask.CompletedTask;
        }
    }

    sealed class InjectedCrashException(MaterializationRebuildCrashPoint point)
        : Exception($"Injected rebuild crash at '{point}'.");
}
