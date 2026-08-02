using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationIndexSyncControlRuntimeTests
{
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Runtime_DeterministicallySaturatesRecoversAndResistsOscillation()
    {
        var fixture = CreateFixture();
        var initial = Assert.Single(await fixture.Runtime.GetSnapshotsAsync(fixture.Context));
        Assert.Equal(80, BatchItems(initial));
        Assert.Equal(100, initial.Realization.EffectiveDefinition.GetEffectiveRange(
            ControlActuatorKind.BatchItems).Maximum.Value);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var saturated = await fixture.Runtime.ObserveAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            Observation(initial, "saturated", value: 250, fixture.Clock.GetUtcNow()));
        Assert.Equal(ControlPressureClassification.Congested, saturated.State.LastClassification);
        Assert.Equal(ControlRecommendationDirection.Decrease, saturated.State.PendingRecommendation?.Direction);

        var wrongCut = await fixture.Runtime.AtSafePointAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            ControlApplicationPointKind.WorkAdmissionBoundary,
            "tests/runtime/wrong-cut");
        Assert.Equal(saturated.State.Revision, Assert.Single(wrongCut.Snapshots).State.Revision);
        Assert.NotNull(Assert.Single(wrongCut.Snapshots).State.PendingRecommendation);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var decreased = await fixture.Runtime.AtSafePointAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            ControlApplicationPointKind.BatchBoundary,
            "tests/runtime/decrease-cut");
        var decreasedSnapshot = Assert.Single(decreased.Snapshots);
        Assert.Equal(40, decreased.MaximumBatchItems);
        Assert.Null(decreasedSnapshot.State.PendingRecommendation);
        Assert.NotNull(decreasedSnapshot.State.CooldownUntilUtc);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var cooldown = await fixture.Runtime.ObserveAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            Observation(decreasedSnapshot, "healthy-during-cooldown", value: 50, fixture.Clock.GetUtcNow()));
        Assert.Equal(ControlPressureClassification.Healthy, cooldown.State.LastClassification);
        Assert.Equal(0, cooldown.State.HealthyObservationCount);
        Assert.Null(cooldown.State.PendingRecommendation);

        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        var firstHealthy = await fixture.Runtime.ObserveAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            Observation(cooldown, "healthy-after-cooldown-1", value: 50, fixture.Clock.GetUtcNow()));
        Assert.Equal(1, firstHealthy.State.HealthyObservationCount);
        Assert.Null(firstHealthy.State.PendingRecommendation);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var secondHealthy = await fixture.Runtime.ObserveAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            Observation(firstHealthy, "healthy-after-cooldown-2", value: 50, fixture.Clock.GetUtcNow()));
        Assert.Equal(ControlRecommendationDirection.Increase, secondHealthy.State.PendingRecommendation?.Direction);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var increased = await fixture.Runtime.AtSafePointAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            ControlApplicationPointKind.BatchBoundary,
            "tests/runtime/increase-cut");
        var increasedSnapshot = Assert.Single(increased.Snapshots);
        Assert.Equal(50, increased.MaximumBatchItems);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var hysteresis = await fixture.Runtime.ObserveAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            Observation(increasedSnapshot, "hysteresis", value: 150, fixture.Clock.GetUtcNow()));
        Assert.Equal(ControlPressureClassification.Hysteresis, hysteresis.State.LastClassification);
        Assert.Null(hysteresis.State.PendingRecommendation);
        Assert.Equal(50, BatchItems(hysteresis));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var dwellProtected = await fixture.Runtime.ObserveAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            Observation(hysteresis, "congested-inside-dwell", value: 250, fixture.Clock.GetUtcNow()));
        Assert.Equal(ControlPressureClassification.Congested, dwellProtected.State.LastClassification);
        Assert.Null(dwellProtected.State.PendingRecommendation);
        Assert.Equal(50, BatchItems(dwellProtected));
    }

    [Fact]
    public async Task Runtime_RejectsInvalidObservationWithoutChangingDurableState()
    {
        var fixture = CreateFixture();
        var initial = Assert.Single(await fixture.Runtime.GetSnapshotsAsync(fixture.Context));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var invalid = Observation(
            initial,
            "missing-objective",
            value: 5_000,
            fixture.Clock.GetUtcNow(),
            metric: ControlMetricKind.ProcessorUtilization);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Runtime.ObserveAsync(
                fixture.Context,
                MaterializationIndexSyncWorkloadKind.Rebuild,
                invalid));
        Assert.Contains(ControlDiagnosticCodes.MeasurementMissing, exception.Message, StringComparison.Ordinal);

        var retained = Assert.Single(await fixture.Runtime.GetSnapshotsAsync(fixture.Context));
        Assert.Equal(initial.State, retained.State);
    }

    [Fact]
    public async Task Provider_RetainsEpochForSameGenerationAndStartsFreshEpochForRestartGeneration()
    {
        var fixture = CreateFixture();
        var initial = Assert.Single(await fixture.Runtime.GetSnapshotsAsync(fixture.Context));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var observed = await fixture.Runtime.ObserveAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            Observation(initial, "resume-state", value: 150, fixture.Clock.GetUtcNow()));

        var continuedRuntime = fixture.Provider.ForGeneration(fixture.Generation);
        var continued = Assert.Single(await continuedRuntime.GetSnapshotsAsync(fixture.Context));
        Assert.Equal(observed.Key.Epoch, continued.Key.Epoch);
        Assert.Equal(observed.State, continued.State);

        MaterializationGenerationId restartedGeneration = new("generation/restarted");
        var restartedRuntime = fixture.Provider.ForGeneration(restartedGeneration);
        var restarted = Assert.Single(await restartedRuntime.GetSnapshotsAsync(fixture.Context));
        Assert.NotEqual(continued.Key.Epoch, restarted.Key.Epoch);
        Assert.Equal(ControlRevision.Initial, restarted.State.Revision);
        Assert.Equal(80, BatchItems(restarted));

        var staleObservation = Observation(continued, "stale-prior-generation", value: 250, fixture.Clock.GetUtcNow());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await restartedRuntime.ObserveAsync(
                fixture.Context,
                MaterializationIndexSyncWorkloadKind.Rebuild,
                staleObservation));
        Assert.Contains(ControlDiagnosticCodes.ObservationFenceMismatch, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_RejectsForgedOutOfBoundsDurableState()
    {
        var fixture = CreateFixture();
        var snapshot = Assert.Single(await fixture.Runtime.GetSnapshotsAsync(fixture.Context));
        var state = snapshot.State;
        ControlLoopState forged = new(
            state.SchemaVersion,
            state.LoopId,
            state.Target,
            state.Epoch,
            state.Revision,
            state.DefinitionFingerprint,
            new([
                new(
                    ControlActuatorKind.BatchItems,
                    new(101, ControlUnit.Count))
            ]),
            healthyObservationCount: 0,
            createdAtUtc: state.CreatedAtUtc,
            updatedAtUtc: state.UpdatedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => new MaterializationIndexSyncControlSnapshot(
            snapshot.Key,
            snapshot.Realization,
            forged));
        Assert.Contains(ControlDiagnosticCodes.HardLimitExceeded, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_RejectsEmbeddedControlLoopWithStaleNestedSchema()
    {
        var current = Definition();
        ControlLoopDefinition stale = new(
            new("cohesive-control/v1"),
            current.Id,
            current.Target,
            current.ApplicationAuthority,
            current.Stage,
            current.HardLimits,
            current.InitialOperatingPoint,
            current.Objectives,
            current.Policy,
            current.Budgets,
            current.Provenance);

        var exception = Assert.Throws<ArgumentException>(() =>
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
                [stale],
                [new(stale.Id, MaterializationIndexSyncWorkloadKind.Rebuild)]));

        Assert.Contains("current portable Control schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlledPlan_StrictJsonRoundTripRetainsLinkedRealizationsAndRejectsTampering()
    {
        var authored = Definition();
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
            [authored],
            [new(authored.Id, MaterializationIndexSyncWorkloadKind.Rebuild)]);

        var restored = MaterializationRebuildPlanJsonSerializer.Deserialize(
            MaterializationRebuildPlanJsonSerializer.Serialize(plan));

        var realization = Assert.Single(restored.ControlRealizations);
        var expected = Assert.Single(plan.ControlRealizations);
        Assert.Equal(expected.AuthoredDefinitionFingerprint, realization.AuthoredDefinitionFingerprint);
        Assert.Equal(expected.EffectiveDefinition.Fingerprint, realization.EffectiveDefinition.Fingerprint);
        Assert.Equal(expected.Workload, realization.Workload);
        Assert.Equal(plan.Fingerprint, restored.Fingerprint);

        var root = JsonNode.Parse(MaterializationRebuildPlanJsonSerializer.Serialize(plan))!.AsObject();
        Assert.True(root.Remove("controlRealizations"));
        Assert.Throws<JsonException>(() =>
            MaterializationRebuildPlanJsonSerializer.Deserialize(root.ToJsonString()));

        MaterializationIndexSyncControlRealization tampered = new(
            realization.AuthoredDefinitionFingerprint,
            MaterializationIndexSyncWorkloadKind.Realtime,
            realization.EffectiveDefinition);
        Assert.Throws<ArgumentException>(() => new MaterializationRebuildPlan(
            schemaVersion: plan.SchemaVersion,
            materialization: plan.Materialization,
            placementSlice: plan.PlacementSlice,
            impactPlan: plan.ImpactPlan,
            sources: plan.Sources,
            target: plan.Target,
            targetCapabilityMatch: plan.TargetCapabilityMatch,
            shards: plan.Shards,
            changeFeedCatalogs: plan.ChangeFeedCatalogs,
            changeFeeds: plan.ChangeFeeds,
            limits: plan.Limits,
            provenance: plan.Provenance,
            controlRealizations: [tampered],
            fingerprint: plan.Fingerprint));
    }

    [Fact]
    public async Task ObserveStage_ExactOldEvidenceReplayDoesNotAdvanceAfterNewerEvidence()
    {
        var fixture = CreateFixture();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var firstAtUtc = fixture.Clock.GetUtcNow();
        var first = await fixture.Runtime.ObserveStageAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            firstAtUtc.AddSeconds(-1),
            firstAtUtc.AddMilliseconds(-1),
            firstAtUtc,
            "tests/adapter-sampler/v1",
            "evidence/first",
            Measurements(150));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var secondAtUtc = fixture.Clock.GetUtcNow();
        var second = await fixture.Runtime.ObserveStageAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            secondAtUtc.AddSeconds(-1),
            secondAtUtc.AddMilliseconds(-1),
            secondAtUtc,
            "tests/adapter-sampler/v1",
            "evidence/second",
            Measurements(150));
        var beforeReplay = Assert.Single(second);

        var replay = Assert.Single(await fixture.Runtime.ObserveStageAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            firstAtUtc.AddSeconds(-1),
            firstAtUtc.AddMilliseconds(-1),
            firstAtUtc,
            "tests/adapter-sampler/v1",
            "evidence/first",
            Measurements(150)));

        Assert.Equal(beforeReplay.State, replay.State);
        Assert.Equal(2, Assert.Single(first).State.Revision.Ordinal);
        Assert.Equal(3, replay.State.Revision.Ordinal);
    }

    [Fact]
    public async Task SubmitLimitUpdate_UsesSharedStateAndCanonicalizesInvocationBoundReplayEvidence()
    {
        InteractionAuthorityScope authority = new("cohesive/control", "tenant-a");
        var fixture = CreateFixture(authority);
        var initial = Assert.Single(await fixture.Runtime.GetSnapshotsAsync(fixture.Context));
        var command = LimitUpdateCommand(
            initial,
            authority,
            new("command/runtime/operator"),
            new("idempotency/runtime/operator"),
            issuedAtUtc: fixture.Clock.GetUtcNow(),
            actor: "operator/original",
            evidenceReference: "authorization/original");

        var accepted = await fixture.Runtime.SubmitLimitUpdateAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            command,
            fixture.Clock.GetUtcNow());
        Assert.Equal(ControlLimitUpdateDecisionDisposition.Accepted, accepted.Disposition);
        Assert.Equal(2, accepted.State.Revision.Ordinal);
        Assert.Equal(80, BatchItems(new(initial.Key, initial.Realization, accepted.State)));

        var invocationReplay = LimitUpdateCommand(
            initial,
            authority,
            command.CommandId,
            command.IdempotencyKey,
            issuedAtUtc: fixture.Clock.GetUtcNow().AddMinutes(1),
            actor: "operator/rebound",
            evidenceReference: "authorization/rebound");
        var replayed = await fixture.Runtime.SubmitLimitUpdateAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            invocationReplay,
            fixture.Clock.GetUtcNow().AddMinutes(1));
        Assert.Equal(ControlLimitUpdateDecisionDisposition.Replayed, replayed.Disposition);
        Assert.Equal(command.Authorization, replayed.Receipt!.Command.Authorization);
        Assert.Equal(command.IssuedAtUtc, replayed.Receipt.Command.IssuedAtUtc);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var applied = await fixture.Runtime.AtSafePointAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            ControlApplicationPointKind.BatchBoundary,
            "tests/runtime/operator-cut");
        Assert.Equal(60, applied.MaximumBatchItems);
        Assert.Null(Assert.Single(applied.Snapshots).State.PendingLimitUpdate);
    }

    [Fact]
    public async Task SubmitLimitUpdate_UsesExplicitDecisionTimeAndRejectsCrossScopeReplayBeforeCanonicalization()
    {
        InteractionAuthorityScope authority = new("cohesive/control", "tenant-a");
        var fixture = CreateFixture(authority);
        var initial = Assert.Single(await fixture.Runtime.GetSnapshotsAsync(fixture.Context));
        var decidedAtUtc = fixture.Clock.GetUtcNow().AddSeconds(10);
        var command = LimitUpdateCommand(
            initial,
            authority,
            new("command/runtime/trusted-time"),
            new("idempotency/runtime/trusted-time"),
            fixture.Clock.GetUtcNow(),
            actor: "operator/tenant-a",
            evidenceReference: "authorization/tenant-a");

        var accepted = await fixture.Runtime.SubmitLimitUpdateAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            command,
            decidedAtUtc);

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Accepted, accepted.Disposition);
        Assert.Equal(decidedAtUtc, accepted.Receipt!.AcceptedAtUtc);
        InteractionAuthorityScope otherAuthority = new("cohesive/control", "tenant-b");
        var crossScopeReplay = Reauthorize(
            command,
            new("operator/tenant-b", otherAuthority, "authorization/tenant-b"));
        var unauthorized = await fixture.Runtime.SubmitLimitUpdateAsync(
            fixture.Context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            crossScopeReplay,
            decidedAtUtc.AddSeconds(1));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Unauthorized, unauthorized.Disposition);
        Assert.Null(unauthorized.Receipt);
        Assert.Equal(accepted.State, unauthorized.State);
    }

    [Fact]
    public async Task SubmitLimitUpdate_UnknownTargetOrEpochIsOpaqueNotFound()
    {
        InteractionAuthorityScope authority = new("cohesive/control", "tenant-a");
        var fixture = CreateFixture(authority);
        var initial = Assert.Single(await fixture.Runtime.GetSnapshotsAsync(fixture.Context));
        var command = LimitUpdateCommand(
            initial,
            authority,
            new("command/runtime/not-found"),
            new("idempotency/runtime/not-found"),
            fixture.Clock.GetUtcNow(),
            actor: "operator/tenant-a",
            evidenceReference: "authorization/tenant-a");

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await fixture.Runtime.SubmitLimitUpdateAsync(
                fixture.Context,
                MaterializationIndexSyncWorkloadKind.Rebuild,
                Readdress(command, target: "other-target", epoch: command.Epoch),
                fixture.Clock.GetUtcNow()));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await fixture.Runtime.SubmitLimitUpdateAsync(
                fixture.Context,
                MaterializationIndexSyncWorkloadKind.Rebuild,
                Readdress(command, target: command.Target, epoch: new("other-epoch")),
                fixture.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task SubmitLimitUpdate_ConcurrentInvocationRebindingReturnsAcceptedAndReplayed()
    {
        InteractionAuthorityScope authority = new("cohesive/control", "tenant-a");
        var authored = Definition();
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
            [authored],
            [new(authored.Id, MaterializationIndexSyncWorkloadKind.Rebuild)]);
        BarrierControlStateStore store = new("limit-update/");
        var provider = new MaterializationIndexSyncControlRuntimeProvider(
            plan,
            store,
            new MaterializationIndexSyncAdmissionGate(),
            authority);
        var runtime = provider.ForGeneration(new("generation/concurrent-command"));
        MutableTimeProvider clock = new(StartedAtUtc);
        var context = OperationContext.Create(timeProvider: clock);
        var initial = Assert.Single(await runtime.GetSnapshotsAsync(context));
        var first = LimitUpdateCommand(
            initial,
            authority,
            new("command/runtime/concurrent"),
            new("idempotency/runtime/concurrent"),
            clock.GetUtcNow(),
            actor: "operator/first-invocation",
            evidenceReference: "authorization/first-invocation");
        var second = Reauthorize(
            first,
            new("operator/second-invocation", authority, "authorization/second-invocation"));

        var decisions = await Task.WhenAll(
            runtime.SubmitLimitUpdateAsync(
                context,
                MaterializationIndexSyncWorkloadKind.Rebuild,
                first,
                clock.GetUtcNow()).AsTask(),
            runtime.SubmitLimitUpdateAsync(
                context,
                MaterializationIndexSyncWorkloadKind.Rebuild,
                second,
                clock.GetUtcNow()).AsTask());

        Assert.Contains(decisions, static decision =>
            decision.Disposition == ControlLimitUpdateDecisionDisposition.Accepted);
        Assert.Contains(decisions, static decision =>
            decision.Disposition == ControlLimitUpdateDecisionDisposition.Replayed);
        Assert.Equal(decisions[0].State, decisions[1].State);
    }

    [Fact]
    public async Task SafePoint_ConcurrentSameCutWithDifferentClocksReconcilesOneApplication()
    {
        InteractionAuthorityScope authority = new("cohesive/control", "tenant-a");
        var authored = Definition();
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
            [authored],
            [new(authored.Id, MaterializationIndexSyncWorkloadKind.Rebuild)]);
        BarrierControlStateStore store = new("application/");
        var provider = new MaterializationIndexSyncControlRuntimeProvider(
            plan,
            store,
            new MaterializationIndexSyncAdmissionGate(),
            authority);
        var runtime = provider.ForGeneration(new("generation/concurrent-safe-point"));
        var initialContext = OperationContext.Create(timeProvider: new MutableTimeProvider(StartedAtUtc));
        var initial = Assert.Single(await runtime.GetSnapshotsAsync(initialContext));
        var command = LimitUpdateCommand(
            initial,
            authority,
            new("command/runtime/concurrent-safe-point"),
            new("idempotency/runtime/concurrent-safe-point"),
            StartedAtUtc,
            actor: "operator/safe-point",
            evidenceReference: "authorization/safe-point");
        var accepted = await runtime.SubmitLimitUpdateAsync(
            initialContext,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            command,
            StartedAtUtc);
        Assert.Equal(ControlLimitUpdateDecisionDisposition.Accepted, accepted.Disposition);
        var firstContext = OperationContext.Create(
            timeProvider: new MutableTimeProvider(StartedAtUtc.AddSeconds(1)));
        var secondContext = OperationContext.Create(
            timeProvider: new MutableTimeProvider(StartedAtUtc.AddSeconds(2)));

        var points = await Task.WhenAll(
            runtime.AtSafePointAsync(
                firstContext,
                MaterializationIndexSyncWorkloadKind.Rebuild,
                ControlStageKind.Target,
                ControlApplicationPointKind.BatchBoundary,
                "tests/runtime/concurrent-safe-point").AsTask(),
            runtime.AtSafePointAsync(
                secondContext,
                MaterializationIndexSyncWorkloadKind.Rebuild,
                ControlStageKind.Target,
                ControlApplicationPointKind.BatchBoundary,
                "tests/runtime/concurrent-safe-point").AsTask());

        Assert.Equal(60, points[0].MaximumBatchItems);
        Assert.Equal(60, points[1].MaximumBatchItems);
        Assert.Equal(points[0].Snapshots[0].State, points[1].Snapshots[0].State);
        Assert.Equal(3, points[0].Snapshots[0].State.Revision.Ordinal);
    }

    [Fact]
    public async Task Admission_DerivesExplicitRealtimeReservationFromRebuildConcurrencyBudget()
    {
        var rebuild = ConcurrencyDefinition(
            "index-sync/rebuild-target-concurrency",
            initial: 1,
            budgets:
            [
                new(
                    ControlActuatorKind.Concurrency,
                    new(2, ControlUnit.Count),
                    new(1, ControlUnit.Count),
                    ControlHardLimitOrigin.Deployment,
                    "tests/realtime-reservation/v1")
            ]);
        var realtime = ConcurrencyDefinition(
            "index-sync/realtime-target-concurrency",
            initial: 2,
            budgets: []);
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
            [rebuild, realtime],
            [
                new(rebuild.Id, MaterializationIndexSyncWorkloadKind.Rebuild),
                new(realtime.Id, MaterializationIndexSyncWorkloadKind.Realtime)
            ]);
        var provider = new MaterializationIndexSyncControlRuntimeProvider(
            plan,
            new InMemoryMaterializationIndexSyncControlStateStore(),
            new MaterializationIndexSyncAdmissionGate());
        var runtime = provider.ForGeneration(new("generation/reservation"));
        var context = OperationContext.Create(timeProvider: new MutableTimeProvider(StartedAtUtc));

        var firstRebuild = await runtime.AcquireStageAsync(
            context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            plan.Target.Id.Value,
            "tests/reservation/rebuild-1");
        var queuedRebuild = runtime.AcquireStageAsync(
            context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            plan.Target.Id.Value,
            "tests/reservation/rebuild-2").AsTask();
        Assert.False(queuedRebuild.IsCompleted);

        var realtimeLease = await runtime.AcquireStageAsync(
            context,
            MaterializationIndexSyncWorkloadKind.Realtime,
            ControlStageKind.Target,
            plan.Target.Id.Value,
            "tests/reservation/realtime");
        Assert.False(queuedRebuild.IsCompleted);

        realtimeLease.Dispose();
        firstRebuild.Dispose();
        var secondRebuild = await queuedRebuild.WaitAsync(TimeSpan.FromSeconds(5));
        secondRebuild.Dispose();
    }

    [Fact]
    public async Task Admission_PreservesRealtimeContributionAcrossCandidateRebuildAndFencedGenerationReplacement()
    {
        var rebuild = ConcurrencyDefinition("index-sync/rebuild-contribution", initial: 2, budgets: []);
        var realtime = ConcurrencyDefinition("index-sync/realtime-contribution", initial: 2, budgets: []);
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
            [rebuild, realtime],
            [
                new(rebuild.Id, MaterializationIndexSyncWorkloadKind.Rebuild),
                new(realtime.Id, MaterializationIndexSyncWorkloadKind.Realtime)
            ]);
        InMemoryMaterializationIndexSyncControlStateStore store = new();
        MaterializationIndexSyncAdmissionGate admission = new();
        var provider = new MaterializationIndexSyncControlRuntimeProvider(plan, store, admission);
        MaterializationGenerationId activeGeneration = new("generation/active-a");
        var active = provider.ForGeneration(activeGeneration);
        MutableTimeProvider clock = new(StartedAtUtc);
        var context = OperationContext.Create(timeProvider: clock);
        var initialRealtime = Assert.Single(
            await active.GetSnapshotsAsync(context),
            static snapshot => snapshot.Realization.Workload == MaterializationIndexSyncWorkloadKind.Realtime);
        clock.Advance(TimeSpan.FromMinutes(1));
        var congested = await active.ObserveAsync(
            context,
            MaterializationIndexSyncWorkloadKind.Realtime,
            Observation(initialRealtime, "realtime-lower", 250, clock.GetUtcNow()));
        Assert.Equal(ControlRecommendationDirection.Decrease, congested.State.PendingRecommendation?.Direction);
        var activeLease = await active.AcquireStageAsync(
            context,
            MaterializationIndexSyncWorkloadKind.Realtime,
            ControlStageKind.Target,
            plan.Target.Id.Value,
            "tests/admission/active-realtime");
        activeLease.Dispose();
        MaterializationIndexSyncAdmissionResource resource = new(ControlStageKind.Target, plan.Target.Id.Value);
        Assert.Equal(1, admission.GetSnapshot(resource)!.Value.Limits.RealtimeMaximum);

        MaterializationGenerationId candidateGeneration = new("generation/candidate-b");
        var candidate = provider.ForGeneration(candidateGeneration);
        var rebuildLease = await candidate.AcquireStageAsync(
            context,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            ControlStageKind.Target,
            plan.Target.Id.Value,
            "tests/admission/candidate-rebuild");
        rebuildLease.Dispose();
        Assert.Equal(1, admission.GetSnapshot(resource)!.Value.Limits.RealtimeMaximum);

        var delayedWider = new MaterializationIndexSyncAdmissionContribution(
            plan.Fingerprint,
            plan.Target.Id,
            activeGeneration,
            MaterializationIndexSyncWorkloadKind.Realtime,
            ControlStageKind.Target,
            totalMaximum: plan.Limits.MaximumParallelism,
            maximumConcurrency: 2,
            realtimeReservation: 0,
            snapshots: [initialRealtime]);
        admission.ApplyContribution(resource, delayedWider);
        Assert.Equal(1, admission.GetSnapshot(resource)!.Value.Limits.RealtimeMaximum);

        var retainedInFlight = await active.AcquireStageAsync(
            context,
            MaterializationIndexSyncWorkloadKind.Realtime,
            ControlStageKind.Target,
            plan.Target.Id.Value,
            "tests/admission/retained-in-flight");
        var retiredWaiter = active.AcquireStageAsync(
            context,
            MaterializationIndexSyncWorkloadKind.Realtime,
            ControlStageKind.Target,
            plan.Target.Id.Value,
            "tests/admission/retired-waiter").AsTask();
        Assert.False(retiredWaiter.IsCompleted);

        provider.RetireAdmissionContributions(
            activeGeneration,
            MaterializationIndexSyncWorkloadKind.Realtime);
        var retirementError = await Assert.ThrowsAsync<InvalidOperationException>(() => retiredWaiter);
        Assert.Contains("retired", retirementError.Message, StringComparison.OrdinalIgnoreCase);
        retainedInFlight.Dispose();
        var replacement = provider.ForGeneration(new("generation/active-c"));
        var replacementLease = await replacement.AcquireStageAsync(
            context,
            MaterializationIndexSyncWorkloadKind.Realtime,
            ControlStageKind.Target,
            plan.Target.Id.Value,
            "tests/admission/replacement-realtime");
        replacementLease.Dispose();
        Assert.Equal(2, admission.GetSnapshot(resource)!.Value.Limits.RealtimeMaximum);
        Assert.Throws<InvalidOperationException>(() => admission.ApplyContribution(resource, delayedWider));
    }

    [Fact]
    public void Compiler_RejectsBudgetsThatCannotBeHonestlyRealized()
    {
        var oversizedConcurrency = ConcurrencyDefinition(
            "index-sync/rebuild-oversized-concurrency-budget",
            initial: 1,
            budgets:
            [
                new(
                    ControlActuatorKind.Concurrency,
                    new(100, ControlUnit.Count),
                    new(99, ControlUnit.Count),
                    ControlHardLimitOrigin.Deployment,
                    "tests/oversized-concurrency-budget/v1")
            ]);
        var concurrencyError = Assert.Throws<ArgumentException>(() =>
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
                [oversizedConcurrency],
                [new(oversizedConcurrency.Id, MaterializationIndexSyncWorkloadKind.Rebuild)]));
        Assert.Contains("exceeds exact physical capacity", concurrencyError.Message, StringComparison.Ordinal);

        var undersizedConcurrency = ConcurrencyDefinition(
            "index-sync/rebuild-undersized-concurrency-budget",
            initial: 1,
            budgets:
            [
                new(
                    ControlActuatorKind.Concurrency,
                    new(1, ControlUnit.Count),
                    new(0, ControlUnit.Count),
                    ControlHardLimitOrigin.Deployment,
                    "tests/undersized-concurrency-budget/v1")
            ]);
        var undersizedConcurrencyError = Assert.Throws<ArgumentException>(() =>
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
                [undersizedConcurrency],
                [new(undersizedConcurrency.Id, MaterializationIndexSyncWorkloadKind.Rebuild)]));
        Assert.Contains(
            "requires the exact physical capacity 2",
            undersizedConcurrencyError.Message,
            StringComparison.Ordinal);

        var realtimeReservation = ConcurrencyDefinition(
            "index-sync/realtime-unsupported-reservation",
            initial: 1,
            budgets:
            [
                new(
                    ControlActuatorKind.Concurrency,
                    new(2, ControlUnit.Count),
                    new(1, ControlUnit.Count),
                    ControlHardLimitOrigin.Deployment,
                    "tests/realtime-reservation/v1")
            ]);
        var realtimeError = Assert.Throws<ArgumentException>(() =>
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
                [realtimeReservation],
                [new(realtimeReservation.Id, MaterializationIndexSyncWorkloadKind.Realtime)]));
        Assert.Contains("realizes only realtime capacity reserved by the rebuild workload", realtimeError.Message, StringComparison.Ordinal);

        var batchDefinition = Definition();
        var oversizedBatch = new ControlLoopDefinition(
            batchDefinition.SchemaVersion,
            batchDefinition.Id,
            batchDefinition.Target,
            batchDefinition.ApplicationAuthority,
            batchDefinition.Stage,
            batchDefinition.HardLimits,
            batchDefinition.InitialOperatingPoint,
            batchDefinition.Objectives,
            batchDefinition.Policy,
            budgets:
            [
                new(
                    ControlActuatorKind.BatchItems,
                    new(101, ControlUnit.Count),
                    new(1, ControlUnit.Count),
                    ControlHardLimitOrigin.Deployment,
                    "tests/oversized-batch-budget/v1")
            ],
            batchDefinition.Provenance);
        var batchError = Assert.Throws<ArgumentException>(() =>
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
                [oversizedBatch],
                [new(oversizedBatch.Id, MaterializationIndexSyncWorkloadKind.Rebuild)]));
        Assert.Contains("exceeds exact physical capacity", batchError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Epoch_IsPurposeSeparatedFixedLengthDigestOfExactKey()
    {
        var fixture = CreateFixture();
        var first = Assert.Single(await fixture.Runtime.GetSnapshotsAsync(fixture.Context));
        var same = Assert.Single(await fixture.Provider.ForGeneration(fixture.Generation).GetSnapshotsAsync(fixture.Context));
        var other = Assert.Single(await fixture.Provider.ForGeneration(new("generation/other")).GetSnapshotsAsync(fixture.Context));

        Assert.Equal(first.Key.Epoch, same.Key.Epoch);
        Assert.NotEqual(first.Key.Epoch, other.Key.Epoch);
        Assert.StartsWith("materialization-control-epoch/v2/", first.Key.Epoch.Value, StringComparison.Ordinal);
        Assert.Equal("materialization-control-epoch/v2/".Length + 64, first.Key.Epoch.Value.Length);
        Assert.DoesNotContain(fixture.Generation.Value, first.Key.Epoch.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Key.MaterializationId.Value, first.Key.Epoch.Value, StringComparison.Ordinal);
    }

    static RuntimeFixture CreateFixture(InteractionAuthorityScope? authorityScope = null)
    {
        var authored = Definition();
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
            [authored],
            [new(authored.Id, MaterializationIndexSyncWorkloadKind.Rebuild)]);
        var store = new InMemoryMaterializationIndexSyncControlStateStore();
        var admission = new MaterializationIndexSyncAdmissionGate();
        var provider = new MaterializationIndexSyncControlRuntimeProvider(
            plan,
            store,
            admission,
            authorityScope);
        MaterializationGenerationId generation = new("generation/current");
        MutableTimeProvider clock = new(StartedAtUtc);
        return new(
            provider,
            provider.ForGeneration(generation),
            generation,
            clock,
            OperationContext.Create(timeProvider: clock));
    }

    static ControlLoopDefinition Definition()
    {
        var policy = AimdControlPolicyResolver.Resolve(
            ControlActuatorKind.BatchItems,
            new AimdControlPolicyLayer(
                EffectiveConfigurationOrigin.Explicit,
                "tests/index-sync-control-policy/v1",
                new AimdControlPolicySettings(
                    additiveIncrease: 10,
                    multiplicativeDecreaseBasisPoints: 5_000,
                    healthyObservationCount: 2,
                    recoveryCooldownMilliseconds: 10_000,
                    minimumDwellMilliseconds: 5_000,
                    maximumObservationAgeMilliseconds: 60_000,
                    minimumSampleCount: 3)));
        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("index-sync/target-batch"),
            target: "loads/search-json",
            applicationAuthority: MaterializationIndexSyncControlCompiler.ApplicationAuthority,
            stage: ControlStageKind.Target,
            hardLimits: new([
                new(
                    new(
                        ControlActuatorKind.BatchItems,
                        new(1, ControlUnit.Count),
                        new(120, ControlUnit.Count)),
                    ControlHardLimitOrigin.Semantic,
                    "tests/materialization-definition/v1")
            ]),
            initialOperatingPoint: new([
                new(
                    ControlActuatorKind.BatchItems,
                    new(80, ControlUnit.Count))
            ]),
            objectives: [
                new(
                    ControlMetricKind.Latency,
                    ControlStatisticKind.P95,
                    ControlObjectiveDirection.HigherIsCongested,
                    new(100, ControlUnit.Milliseconds),
                    new(200, ControlUnit.Milliseconds))
            ],
            policy,
            budgets: [],
            provenance: new(
                new("cohesive-tests", "1"),
                new("tests:index-sync-control-runtime"),
                DocumentOrigin.Generated));
    }

    static ControlLoopDefinition ConcurrencyDefinition(
        string id,
        long initial,
        ImmutableArray<ControlWorkloadBudget> budgets) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new(id),
            target: "loads/search-json",
            applicationAuthority: MaterializationIndexSyncControlCompiler.ApplicationAuthority,
            stage: ControlStageKind.Target,
            hardLimits: new([
                new(
                    new(
                        ControlActuatorKind.Concurrency,
                        new(1, ControlUnit.Count),
                        new(2, ControlUnit.Count)),
                    ControlHardLimitOrigin.Semantic,
                    "tests/concurrency-definition/v1")
            ]),
            initialOperatingPoint: new([
                new(
                    ControlActuatorKind.Concurrency,
                    new(initial, ControlUnit.Count))
            ]),
            objectives:
            [
                new(
                    ControlMetricKind.Latency,
                    ControlStatisticKind.P95,
                    ControlObjectiveDirection.HigherIsCongested,
                    new(100, ControlUnit.Milliseconds),
                    new(200, ControlUnit.Milliseconds))
            ],
            policy: AimdControlPolicyResolver.Resolve(ControlActuatorKind.Concurrency),
            budgets,
            provenance: new(
                new("cohesive-tests", "1"),
                new("tests:index-sync-control-runtime"),
                DocumentOrigin.Generated));

    static ControlObservation Observation(
        MaterializationIndexSyncControlSnapshot snapshot,
        string suffix,
        long value,
        DateTimeOffset observedAtUtc,
        ControlMetricKind metric = ControlMetricKind.Latency)
    {
        var windowEndedAtUtc = observedAtUtc.AddMilliseconds(-1);
        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new($"observation/{suffix}"),
            snapshot.State.LoopId,
            snapshot.State.DefinitionFingerprint,
            snapshot.State.Target,
            snapshot.State.Epoch,
            snapshot.State.Revision,
            windowEndedAtUtc.AddSeconds(-1),
            windowEndedAtUtc,
            observedAtUtc,
            "tests/index-sync-control-sampler/v1",
            [
                new(
                    metric,
                    ControlStatisticKind.P95,
                    ControlMeasurementAvailability.Available,
                    new(value, ControlUnitCatalog.ForMetric(metric)),
                    sampleCount: 3)
            ]);
    }

    static long BatchItems(MaterializationIndexSyncControlSnapshot snapshot) =>
        snapshot.State.OperatingPoint.Get(ControlActuatorKind.BatchItems).Quantity.Value;

    static ImmutableArray<ControlMeasurement> Measurements(long value) =>
    [
        new(
            ControlMetricKind.Latency,
            ControlStatisticKind.P95,
            ControlMeasurementAvailability.Available,
            new(value, ControlUnit.Milliseconds),
            sampleCount: 3)
    ];

    static ControlLimitUpdateCommand LimitUpdateCommand(
        MaterializationIndexSyncControlSnapshot snapshot,
        InteractionAuthorityScope authorityScope,
        EmissionId commandId,
        InteractionIdempotencyKey idempotencyKey,
        DateTimeOffset issuedAtUtc,
        string actor,
        string evidenceReference) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            commandId,
            idempotencyKey,
            snapshot.State.LoopId,
            snapshot.State.DefinitionFingerprint,
            snapshot.State.Target,
            snapshot.State.Epoch,
            snapshot.State.Revision,
            new([
                new(
                    ControlActuatorKind.BatchItems,
                    new(60, ControlUnit.Count))
            ]),
            new(actor, authorityScope, evidenceReference),
            issuedAtUtc,
            snapshot.Realization.EffectiveDefinition.Provenance);

    static ControlLimitUpdateCommand Reauthorize(
        ControlLimitUpdateCommand command,
        ProcessControlAuthorizationContext authorization) =>
        new(
            command.SchemaVersion,
            command.CommandId,
            command.IdempotencyKey,
            command.LoopId,
            command.DefinitionFingerprint,
            command.Target,
            command.Epoch,
            command.ExpectedRevision,
            command.RequestedOperatingPoint,
            authorization,
            command.IssuedAtUtc,
            command.Provenance);

    static ControlLimitUpdateCommand Readdress(
        ControlLimitUpdateCommand command,
        string target,
        ControlEpochId epoch) =>
        new(
            command.SchemaVersion,
            command.CommandId,
            command.IdempotencyKey,
            command.LoopId,
            command.DefinitionFingerprint,
            target,
            epoch,
            command.ExpectedRevision,
            command.RequestedOperatingPoint,
            command.Authorization,
            command.IssuedAtUtc,
            command.Provenance);

    sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        internal void Advance(TimeSpan duration) => current = current.Add(duration);
    }

    sealed class BarrierControlStateStore(string mutationPrefix) : IMaterializationIndexSyncControlStateStore
    {
        readonly InMemoryMaterializationIndexSyncControlStateStore inner = new();
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals;

        public ValueTask<ControlLoopState?> ReadAsync(
            OperationContext context,
            MaterializationIndexSyncControlStateKey key) =>
            inner.ReadAsync(context, key);

        public ValueTask<MaterializationIndexSyncControlWriteResult> ReadMutationAsync(
            OperationContext context,
            MaterializationIndexSyncControlStateKey key,
            string mutationId,
            string mutationFingerprint) =>
            inner.ReadMutationAsync(context, key, mutationId, mutationFingerprint);

        public ValueTask<MaterializationIndexSyncControlWriteResult> CreateAsync(
            OperationContext context,
            MaterializationIndexSyncControlStateKey key,
            string mutationId,
            string mutationFingerprint,
            ControlLoopState state) =>
            inner.CreateAsync(context, key, mutationId, mutationFingerprint, state);

        public async ValueTask<MaterializationIndexSyncControlWriteResult> CompareExchangeAsync(
            OperationContext context,
            MaterializationIndexSyncControlStateKey key,
            string mutationId,
            string mutationFingerprint,
            ControlRevision expectedRevision,
            ControlLoopState state)
        {
            if (mutationId.StartsWith(mutationPrefix, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == 2)
                    release.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
            }
            return await inner.CompareExchangeAsync(
                context,
                key,
                mutationId,
                mutationFingerprint,
                expectedRevision,
                state);
        }
    }

    sealed record RuntimeFixture(
        MaterializationIndexSyncControlRuntimeProvider Provider,
        MaterializationIndexSyncControlRuntime Runtime,
        MaterializationGenerationId Generation,
        MutableTimeProvider Clock,
        OperationContext Context);
}
