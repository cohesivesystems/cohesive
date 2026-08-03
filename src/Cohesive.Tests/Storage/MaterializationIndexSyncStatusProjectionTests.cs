using System.Collections.Immutable;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage;
using Cohesive.Storage.Materialization;
using Cohesive.Tests.Storage.Control;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationIndexSyncStatusProjectionTests
{
    static readonly DateTimeOffset Epoch = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly QualifiedShapeId Shape = new(new("tests"), new("IndexFact"));
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan =
        new("sha256", "tests/index-sync-status-plan/v1", "0123456789abcdef");

    [Fact]
    public void HealthProjection_UsesRoutingGenerationAndProviderEvidenceWithProvenance()
    {
        var fixture = CreateFixture();
        var observedAtUtc = Epoch.AddMinutes(8);
        var degraded = StorageExecutionTelemetry.ProjectMaterializationHealth(
            fixture.Routing,
            fixture.Generations,
            new(
                lagMilliseconds: fixture.Observation.LagMilliseconds,
                changeLag: fixture.Observation.ChangeLag),
            observedAtUtc,
            fixture.Provenance);
        var failed = StorageExecutionTelemetry.ProjectMaterializationHealth(
            fixture.Routing,
            fixture.Generations,
            fixture.Observation,
            observedAtUtc,
            fixture.Provenance);

        Assert.Equal(ExecutionHealthStatus.Degraded, degraded.Health);
        Assert.Equal(ExecutionReadinessStatus.Ready, degraded.Readiness);
        Assert.Equal(ExecutionHealthStatus.Unhealthy, failed.Health);
        Assert.Equal(ExecutionReadinessStatus.NotReady, failed.Readiness);
        Assert.Equal(observedAtUtc, failed.ObservedAtUtc);
        Assert.Contains("provider/a", failed.EvidenceReferences);
        Assert.Contains(fixture.Provenance.Source.Reference, failed.EvidenceReferences);
        Assert.All(
            fixture.Generations,
            generation => Assert.Contains(generation.Generation.ToString(), failed.EvidenceReferences));
        Assert.Equal(fixture.Observation.Failures, failed.Diagnostics);
    }

    [Fact]
    public void CreateExtension_ProjectsExactAuthoritiesAndNormalizesCollectionOrder()
    {
        var fixture = CreateFixture();

        var extension = MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            [.. fixture.Progress.Reverse()],
            [.. fixture.Generations.Reverse()],
            fixture.Control,
            fixture.Observation,
            fixture.Provenance);
        var root = Root(extension);

        Assert.Equal(MaterializationIndexSyncStatusWireNames.ExtensionId, extension.Id);
        Assert.Equal(MaterializationIndexSyncStatusWireNames.SchemaVersion, extension.SchemaVersion);
        Assert.Equal(ExecutionStatusDisclosure.Disclosed, extension.Value.Disclosure);
        Assert.True(PortableExecutionValidator.Validate(extension.Value.Value!).IsValid);
        Assert.Equal("pool/status", root.GetProperty("pool").GetRequiredString());
        Assert.Equal(
            fixture.Routing.PlacementSlice.Id.Value,
            root.GetProperty("placementSlice").GetRequiredString());
        var placementSliceFingerprint = root.GetProperty("placementSliceFingerprint");
        Assert.Equal(
            fixture.Routing.PlacementSlice.Fingerprint.Algorithm,
            placementSliceFingerprint.GetProperty("algorithm").GetRequiredString());
        Assert.Equal(
            fixture.Routing.PlacementSlice.Fingerprint.Canonicalization,
            placementSliceFingerprint.GetProperty("canonicalization").GetRequiredString());
        Assert.Equal(
            fixture.Routing.PlacementSlice.Fingerprint.Value,
            placementSliceFingerprint.GetProperty("value").GetRequiredString());
        var poolDefinitionFingerprint = root.GetProperty("poolDefinitionFingerprint");
        Assert.Equal(
            fixture.Routing.PoolDefinitionFingerprint.Algorithm,
            poolDefinitionFingerprint.GetProperty("algorithm").GetRequiredString());
        Assert.Equal(
            fixture.Routing.PoolDefinitionFingerprint.Canonicalization,
            poolDefinitionFingerprint.GetProperty("canonicalization").GetRequiredString());
        Assert.Equal(
            fixture.Routing.PoolDefinitionFingerprint.Value,
            poolDefinitionFingerprint.GetProperty("value").GetRequiredString());
        Assert.Equal(7L, root.GetProperty("routingRevision").Int64);
        Assert.Equal("3", root.GetProperty("routingFence").GetRequiredString());
        Assert.Equal("target/z", root.GetProperty("activeReadTarget").GetRequiredString());
        Assert.Equal("generation/z", root.GetProperty("activeReadGeneration").GetRequiredString());
        Assert.Equal("target/z", root.GetProperty("activeWriteTarget").GetRequiredString());
        Assert.Equal("target/a", root.GetProperty("candidateTarget").GetRequiredString());
        var draining = Assert.Single(root.GetProperty("draining").Array);
        Assert.Equal("target/d", draining.GetProperty("target").GetRequiredString());
        Assert.Equal("generation/d", draining.GetProperty("generation").GetRequiredString());
        var retirement = Assert.Single(root.GetProperty("retired").Array);
        Assert.Equal("target/r", retirement.GetProperty("target").GetRequiredString());
        Assert.Equal("generation/r", retirement.GetProperty("generation").GetRequiredString());
        Assert.Equal(5L, retirement.GetProperty("retiredAtRevision").Int64);
        var cleaned = Assert.Single(root.GetProperty("cleaned").Array);
        Assert.Equal("target/c", cleaned.GetProperty("target").GetRequiredString());
        Assert.Equal("generation/c", cleaned.GetProperty("generation").GetRequiredString());

        var configuration = root.GetProperty("configuration");
        Assert.Equal("target/z", configuration.GetProperty("readTarget").GetRequiredString());
        Assert.Equal("target/z", configuration.GetProperty("writeTarget").GetRequiredString());
        var decisions = configuration.GetProperty("decisions").Array;
        Assert.Equal(["readTarget", "writeTarget"], decisions.Select(static value => value.GetProperty("setting").GetRequiredString()));
        Assert.Equal(
            ["ScopedProfile", "Explicit"],
            decisions.Select(static value => value.GetProperty("origin").GetRequiredString()));
        Assert.Equal(
            ["tests/profile-routing/v1", "tests/explicit-routing/v1"],
            decisions.Select(static value => value.GetProperty("authority").GetRequiredString()));

        Assert.Equal(125L, root.GetProperty("lagMilliseconds").Int64);
        var lag = root.GetProperty("changeLag").Array;
        Assert.Equal(["target/a", "target/z"], lag.Select(static value => value.GetProperty("target").GetRequiredString()));
        Assert.Equal("input/a", lag[0].GetProperty("input").GetRequiredString());
        Assert.Equal("Estimated", lag[0].GetProperty("estimateState").GetRequiredString());
        Assert.Equal(17L, lag[0].GetProperty("estimatedPendingProviderWork").Int64);
        Assert.Equal(ObservationValueKind.Null, lag[1].GetProperty("input").Kind);
        Assert.Equal("Unavailable", lag[1].GetProperty("estimateState").GetRequiredString());
        Assert.Equal(ObservationValueKind.Null, lag[1].GetProperty("estimatedPendingProviderWork").Kind);

        var shards = root.GetProperty("shards").Array;
        Assert.Equal(["input/a", "input/z"], shards.Select(static value => value.GetProperty("input").GetRequiredString()));
        Assert.Equal(["target/a", "target/z"], shards.Select(static value => value.GetProperty("target").GetRequiredString()));
        Assert.Equal(["generation/a", "generation/z"], shards.Select(static value => value.GetProperty("generation").GetRequiredString()));
        Assert.Equal("BatchContinuation", shards[0].GetProperty("batchCheckpointKind").GetRequiredString());
        Assert.Equal("continuation/a", shards[0].GetProperty("batchContinuation").GetRequiredString());
        Assert.Equal(4L, shards[0].GetProperty("batchPageOrdinal").Int64);
        Assert.Equal("checkpoint/change/a", shards[0].GetProperty("incrementalCheckpointId").GetRequiredString());
        Assert.Equal("position/a/9", shards[0].GetProperty("incrementalPosition").GetRequiredString());
        Assert.Equal(1L, shards[0].GetProperty("incrementalAppliedDeliveryCount").Int64);
        Assert.Equal("checkpoint/change/a", shards[0].GetProperty("settlementCheckpoint").GetRequiredString());
        Assert.Equal("CumulativePrefix", shards[0].GetProperty("settlementKind").GetRequiredString());
        Assert.Equal("position/a/9", shards[0].GetProperty("settlementPosition").GetRequiredString());
        Assert.Empty(shards[0].GetProperty("settlementDeliveries").Array);

        var generations = root.GetProperty("generations").Array;
        Assert.Equal(["target/a", "target/z"], generations.Select(static value => value.GetProperty("target").GetRequiredString()));
        Assert.Equal("generation/a", generations[0].GetProperty("generation").GetRequiredString());
        Assert.Equal("Loading", generations[0].GetProperty("state").GetRequiredString());
        Assert.Equal("Degraded", generations[0].GetProperty("health").GetRequiredString());
        Assert.Equal(3L, generations[0].GetProperty("visibleItemCount").Int64);
        Assert.Equal(2L, generations[0].GetProperty("pendingRetryableMutationCount").Int64);
        Assert.Equal("generation/z", generations[1].GetProperty("generation").GetRequiredString());
        Assert.Equal("Healthy", generations[1].GetProperty("health").GetRequiredString());

        var control = Assert.Single(root.GetProperty("controls").Array);
        Assert.Equal("index-sync/target-write", control.GetProperty("loop").GetRequiredString());
        Assert.Equal("Target", control.GetProperty("stage").GetRequiredString());
        Assert.Equal("Rebuild", control.GetProperty("workload").GetRequiredString());
        Assert.Equal("target/a", control.GetProperty("backendTarget").GetRequiredString());
        Assert.Equal("generation/a", control.GetProperty("generation").GetRequiredString());
        Assert.Equal(2L, control.GetProperty("revision").Int64);
        var materializationFingerprint = control.GetProperty("materializationDefinitionFingerprint");
        Assert.Equal(
            fixture.Control[0].Key.DefinitionFingerprint.Algorithm,
            materializationFingerprint.GetProperty("algorithm").GetRequiredString());
        Assert.Equal(
            fixture.Control[0].Key.DefinitionFingerprint.Canonicalization,
            materializationFingerprint.GetProperty("canonicalization").GetRequiredString());
        Assert.Equal(
            fixture.Control[0].Key.DefinitionFingerprint.Value,
            materializationFingerprint.GetProperty("value").GetRequiredString());
        var planFingerprint = control.GetProperty("planFingerprint");
        Assert.Equal(
            fixture.Control[0].Key.PlanFingerprint.Algorithm,
            planFingerprint.GetProperty("algorithm").GetRequiredString());
        Assert.Equal(
            fixture.Control[0].Key.PlanFingerprint.Canonicalization,
            planFingerprint.GetProperty("canonicalization").GetRequiredString());
        Assert.Equal(
            fixture.Control[0].Key.PlanFingerprint.Value,
            planFingerprint.GetProperty("value").GetRequiredString());
        Assert.Equal(
            fixture.Control[0].Realization.AuthoredDefinitionFingerprint.Value,
            control.GetProperty("authoredDefinitionFingerprint").GetProperty("value").GetRequiredString());
        var appliedPoint = control.GetProperty("applied").GetProperty("operatingPoint").Array;
        Assert.Contains(appliedPoint, static value =>
            value.GetProperty("actuator").GetRequiredString() == ControlActuatorKind.Concurrency.ToString()
            && value.GetProperty("value").Int64 == 4);
        Assert.Contains(appliedPoint, static value =>
            value.GetProperty("actuator").GetRequiredString() == ControlActuatorKind.BatchItems.ToString()
            && value.GetProperty("value").Int64 == 20);
        var objective = Assert.Single(control.GetProperty("objectives").Array);
        Assert.Equal("Latency", objective.GetProperty("metric").GetRequiredString());
        Assert.Equal("P95", objective.GetProperty("statistic").GetRequiredString());
        Assert.Equal("HigherIsCongested", objective.GetProperty("direction").GetRequiredString());
        Assert.Equal(100L, objective.GetProperty("recoveryBoundary").Int64);
        Assert.Equal(200L, objective.GetProperty("congestionBoundary").Int64);
        Assert.Equal("Milliseconds", objective.GetProperty("unit").GetRequiredString());
        var lastObservation = control.GetProperty("lastObservation");
        Assert.Equal("runtime/status-sampler/v1", lastObservation.GetProperty("source").GetRequiredString());
        var measurement = Assert.Single(lastObservation.GetProperty("measurements").Array);
        Assert.Equal("Latency", measurement.GetProperty("metric").GetRequiredString());
        Assert.Equal(250L, measurement.GetProperty("value").Int64);
        var recommendation = control.GetProperty("pendingRecommendation");
        Assert.Equal("Decrease", recommendation.GetProperty("direction").GetRequiredString());
        Assert.Equal(2L, recommendation.GetProperty("expectedRevision").Int64);
        Assert.Equal(0L, recommendation.GetProperty("authorizingHealthyObservationCount").Int64);
        Assert.Contains(
            recommendation.GetProperty("priorOperatingPoint").Array,
            static value => value.GetProperty("actuator").GetRequiredString() == "Concurrency"
                && value.GetProperty("value").Int64 == 4);
        Assert.Equal(ObservationValueKind.Null, control.GetProperty("pendingOperatorOverride").Kind);

        var failures = root.GetProperty("failures").Array;
        Assert.Equal(["status.a", "status.z"], failures.Select(static failure => failure.GetProperty("code").GetRequiredString()));
        Assert.Equal(ObservationValueKind.Null, failures[0].GetProperty("location").Kind);
        Assert.Equal("/sync/z", failures[1].GetProperty("location").GetRequiredString());
        var eta = root.GetProperty("etaInputs");
        Assert.Equal(900L, eta.GetProperty("remainingWork").Int64);
        Assert.Equal(45L, eta.GetProperty("observedThroughputPerSecond").Int64);
        Assert.Equal(10_000L, eta.GetProperty("sampleWindowMilliseconds").Int64);
        Assert.Equal(5L, eta.GetProperty("sampleCount").Int64);
        Assert.Equal("facts", eta.GetProperty("unit").GetRequiredString());

        var canonical = MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            fixture.Progress,
            fixture.Generations,
            fixture.Control,
            new(
                lagMilliseconds: fixture.Observation.LagMilliseconds,
                changeLag: [.. fixture.Observation.ChangeLag.Reverse()],
                failures: [.. fixture.Observation.Failures.Reverse()],
                etaInputs: fixture.Observation.EtaInputs),
            fixture.Provenance);
        Assert.Equal(root, Root(canonical));
    }

    [Fact]
    public void CreateExtension_UsesCanonicalClosedEnumContractsAndRejectsUnknownMembers()
    {
        var fixture = CreateFixture();
        var extension = MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            fixture.Progress,
            fixture.Generations,
            fixture.Control,
            fixture.Observation,
            fixture.Provenance);
        var contract = Assert.IsType<ObjectTypeRef>(extension.Value.Contract.Type);
        var shard = ObjectField(contract, "shards");
        var changeLag = ObjectField(contract, "changeLag");
        var generation = ObjectField(contract, "generations");
        var control = ObjectField(contract, "controls");
        var hardLimit = ObjectField(control, "hardLimits");
        var objective = ObjectField(control, "objectives");
        var applied = ObjectField(control, "applied");
        var appliedValue = ObjectField(applied, "operatingPoint");
        var configuration = ObjectField(contract, "configuration");
        var decision = ObjectField(configuration, "decisions");

        AssertEnum<MaterializationCheckpointKind>(shard, "batchCheckpointKind");
        AssertEnum<RelationQuerySourceReadState>(shard, "batchCompletionState");
        AssertEnum<ChannelSettlementKind>(shard, "settlementKind");
        AssertEnum<MaterializationChangeLagEstimateState>(changeLag, "estimateState");
        AssertEnum<MaterializationGenerationState>(generation, "state");
        AssertEnum<MaterializationIndexSyncGenerationHealth>(generation, "health");
        AssertEnum<ControlStageKind>(control, "stage");
        AssertEnum<MaterializationIndexSyncWorkloadKind>(control, "workload");
        AssertEnum<ControlActuatorKind>(hardLimit, "actuator");
        AssertEnum<ControlHardLimitOrigin>(hardLimit, "origin");
        AssertEnum<ControlMetricKind>(objective, "metric");
        AssertEnum<ControlStatisticKind>(objective, "statistic");
        AssertEnum<ControlObjectiveDirection>(objective, "direction");
        AssertEnum<ControlUnit>(objective, "unit");
        AssertEnum<ControlActuatorKind>(appliedValue, "actuator");
        AssertEnum<ControlUnit>(appliedValue, "unit");
        AssertEnum<ControlApplicationPointKind>(applied, "applicationPointKind");
        AssertEnum<EffectiveConfigurationOrigin>(decision, "origin");
        Assert.Equal<string>(
            [
                MaterializationBackendRoutingSettingNames.ReadTarget,
                MaterializationBackendRoutingSettingNames.WriteTarget
            ],
            Assert.IsType<EnumTypeRef>(Field(decision, "setting").Type).Members);

        var root = Root(extension);
        (string Field, ObservationValue Payload)[] unknownMembers =
        [
            ("shards.batchCheckpointKind", ReplaceArrayField(root, "shards", "batchCheckpointKind", "FutureCheckpoint")),
            ("shards.batchCompletionState", ReplaceArrayField(root, "shards", "batchCompletionState", "FutureCompletion")),
            ("shards.settlementKind", ReplaceArrayField(root, "shards", "settlementKind", "FutureSettlement")),
            ("changeLag.estimateState", ReplaceArrayField(root, "changeLag", "estimateState", "FutureLagState")),
            ("generations.state", ReplaceArrayField(root, "generations", "state", "FutureGenerationState")),
            ("generations.health", ReplaceArrayField(root, "generations", "health", "FutureHealth")),
            ("controls.stage", ReplaceArrayField(root, "controls", "stage", "FutureStage")),
            ("controls.workload", ReplaceArrayField(root, "controls", "workload", "FutureWorkload")),
            ("controls.hardLimits.actuator", ReplaceNestedArrayField(root, "controls", "hardLimits", "actuator", "FutureActuator")),
            ("controls.applied.operatingPoint.unit", ReplaceAppliedPointField(root, "unit", "FutureUnit")),
            ("configuration.decisions.setting", ReplaceConfigurationDecisionField(root, "setting", "futureTarget")),
            ("configuration.decisions.origin", ReplaceConfigurationDecisionField(root, "origin", "FutureOrigin"))
        ];

        foreach (var (field, payload) in unknownMembers)
        {
            var invalid = PortableExecutionValidator.Validate(
                PortableValue.Concrete(extension.Value.Contract, payload));

            Assert.False(invalid.IsValid, $"Unknown enum member at '{field}' unexpectedly validated.");
            Assert.Contains(
                invalid.Diagnostics,
                static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.ConcreteTypeMismatch);
        }
    }

    [Fact]
    public void CreateExtension_ExplainsPendingAndAppliedOperatorOverrideWithSupersededAdaptiveAdvice()
    {
        var fixture = CreateFixture();
        var snapshot = Assert.Single(fixture.Control);
        var state = snapshot.State;
        var authority = Assert.IsType<InteractionAuthorityScope>(state.AuthorityScope);
        ControlLimitUpdateCommand command = new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("command/status/operator"),
            new("idempotency/status/operator"),
            state.LoopId,
            state.DefinitionFingerprint,
            state.Target,
            state.Epoch,
            state.Revision,
            ControlTestFixture.Point(
                (ControlActuatorKind.Concurrency, 3),
                (ControlActuatorKind.BatchItems, 20)),
            new("operator/status", authority, "authorization/status/operator"),
            Epoch.AddSeconds(5),
            fixture.Provenance);
        var decision = ControlLimitUpdateReferenceReducer.Submit(
            snapshot.Realization.EffectiveDefinition,
            state,
            command,
            Epoch.AddSeconds(6));
        Assert.Equal(ControlLimitUpdateDecisionDisposition.Accepted, decision.Disposition);

        MaterializationIndexSyncControlSnapshot updated = new(
            snapshot.Key,
            snapshot.Realization,
            decision.State);
        var extension = MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            fixture.Progress,
            fixture.Generations,
            [updated],
            fixture.Observation,
            fixture.Provenance);
        var control = Assert.Single(Root(extension).GetProperty("controls").Array);

        Assert.Equal(3L, control.GetProperty("revision").Int64);
        Assert.Equal(ObservationValueKind.Null, control.GetProperty("pendingRecommendation").Kind);
        var pending = control.GetProperty("pendingOperatorOverride");
        Assert.Equal("command/status/operator", pending.GetProperty("commandId").GetRequiredString());
        Assert.Equal("cohesive/control", pending.GetProperty("authority").GetRequiredString());
        Assert.Equal(3L, pending.GetProperty("acceptedRevision").Int64);
        Assert.Equal(
            "tests/index-sync-status-projection",
            pending.GetProperty("provenance").GetProperty("sourceReference").GetRequiredString());
        Assert.Contains(
            pending.GetProperty("requestedOperatingPoint").Array,
            static value => value.GetProperty("actuator").GetRequiredString() == "Concurrency"
                && value.GetProperty("value").Int64 == 3);
        Assert.Contains(
            control.GetProperty("applied").GetProperty("operatingPoint").Array,
            static value => value.GetProperty("actuator").GetRequiredString() == "Concurrency"
                && value.GetProperty("value").Int64 == 4);

        ControlApplicationPoint applicationPoint = new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("application/status/operator"),
            decision.State.LoopId,
            decision.State.DefinitionFingerprint,
            decision.State.Target,
            decision.State.Epoch,
            decision.State.Revision,
            new("1"),
            ControlApplicationPointKind.WorkAdmissionBoundary,
            Epoch.AddSeconds(7),
            snapshot.Realization.EffectiveDefinition.ApplicationAuthority,
            "tests/status/operator-safe-point");
        var appliedDecision = ControlLimitUpdateReferenceReducer.Apply(
            snapshot.Realization.EffectiveDefinition,
            decision.State,
            applicationPoint,
            Epoch.AddSeconds(7));
        Assert.Equal(ControlActuationDisposition.Applied, appliedDecision.Disposition);

        var appliedExtension = MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            fixture.Progress,
            fixture.Generations,
            [new(snapshot.Key, snapshot.Realization, appliedDecision.State)],
            fixture.Observation,
            fixture.Provenance);
        var applied = Assert.Single(Root(appliedExtension).GetProperty("controls").Array)
            .GetProperty("applied");
        Assert.Equal("Operator", applied.GetProperty("actuationSource").GetRequiredString());
        Assert.Equal(ObservationValueKind.Null, applied.GetProperty("adaptiveRecommendation").Kind);
        Assert.Equal(ObservationValueKind.Null, applied.GetProperty("adaptiveObservation").Kind);
        Assert.Equal(
            "command/status/operator",
            applied.GetProperty("operatorOverride").GetProperty("commandId").GetRequiredString());
    }

    [Fact]
    public void CreateExtension_ExplainsExactAppliedAdaptiveRecommendationAndObservation()
    {
        var fixture = CreateFixture();
        var snapshot = Assert.Single(fixture.Control);
        var recommendation = Assert.IsType<ControlRecommendation>(snapshot.State.PendingRecommendation);
        ControlApplicationPoint applicationPoint = new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("application/status/adaptive"),
            snapshot.State.LoopId,
            snapshot.State.DefinitionFingerprint,
            snapshot.State.Target,
            snapshot.State.Epoch,
            snapshot.State.Revision,
            new("1"),
            ControlApplicationPointKind.WorkAdmissionBoundary,
            Epoch.AddSeconds(5),
            snapshot.Realization.EffectiveDefinition.ApplicationAuthority,
            "tests/status/adaptive-safe-point");
        var decision = AimdControlReferenceRegulator.Apply(
            snapshot.Realization.EffectiveDefinition,
            snapshot.State,
            applicationPoint,
            Epoch.AddSeconds(5));
        Assert.Equal(ControlActuationDisposition.Applied, decision.Disposition);

        var extension = MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            fixture.Progress,
            fixture.Generations,
            [new(snapshot.Key, snapshot.Realization, decision.State)],
            fixture.Observation,
            fixture.Provenance);
        var applied = Assert.Single(Root(extension).GetProperty("controls").Array)
            .GetProperty("applied");

        Assert.Equal("Adaptive", applied.GetProperty("actuationSource").GetRequiredString());
        Assert.Equal(
            recommendation.Id.Value,
            applied.GetProperty("adaptiveRecommendation").GetProperty("id").GetRequiredString());
        Assert.Equal(
            snapshot.State.LastObservation!.Id.Value,
            applied.GetProperty("adaptiveObservation").GetProperty("id").GetRequiredString());
        Assert.Equal(ObservationValueKind.Null, applied.GetProperty("operatorOverride").Kind);
    }

    [Fact]
    public void CreateExtension_RejectsProgressGenerationAndLagEvidenceForAnotherDefinition()
    {
        var fixture = CreateFixture();
        ExecutionDefinitionFingerprint foreignFingerprint = new(
            "sha256",
            "tests/foreign-definition/v1",
            "fedcba9876543210");
        var existingProgress = fixture.Progress[0];
        MaterializationBackendGenerationReference foreignGeneration = new(
            new("target/foreign"),
            new("generation/foreign"),
            foreignFingerprint);
        MaterializationProgressSnapshot foreignProgress = new(
            new(
                existingProgress.Snapshot.Key.Materialization,
                foreignFingerprint,
                foreignGeneration.GenerationId,
                existingProgress.Snapshot.Key.Scope),
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            fenceOwner: "worker/foreign");
        MaterializationIndexSyncGenerationStatus foreignGenerationStatus = new(
            foreignGeneration,
            LoadingSnapshot(
                foreignGeneration.GenerationId,
                foreignFingerprint,
                pendingRetryableMutationCount: 0,
                visibleItemCount: 0,
                tombstoneCount: 0));
        MaterializationChangeLagObservation foreignLag = new(
            new(
                MaterializationBackendPoolTestFixture.Materialization,
                foreignFingerprint,
                foreignGeneration.GenerationId),
            existingProgress.Snapshot.Key.Scope.Source,
            existingProgress.Snapshot.Key.Scope,
            MaterializationChangeLagEstimateState.Estimated,
            estimatedPendingProviderWork: 1,
            Epoch);

        Assert.Throws<ArgumentException>(() => new MaterializationIndexSyncProgressStatus(
            fixture.Routing.Candidate!,
            foreignProgress));
        Assert.Throws<ArgumentException>(() => new MaterializationIndexSyncChangeLagStatus(
            fixture.Routing.Candidate!,
            foreignLag));
        Assert.Throws<ArgumentException>(() => MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            [new(foreignGeneration, foreignProgress)],
            fixture.Generations,
            fixture.Control,
            new(),
            fixture.Provenance));
        Assert.Throws<ArgumentException>(() => MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            [],
            [foreignGenerationStatus],
            fixture.Control,
            new(),
            fixture.Provenance));
        Assert.Throws<ArgumentException>(() => MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            [],
            fixture.Generations,
            fixture.Control,
            new(changeLag: [new(foreignGeneration, foreignLag)]),
            fixture.Provenance));

        var existingControl = Assert.Single(fixture.Control);
        MaterializationIndexSyncControlStateKey unroutedControlKey = new(
            existingControl.Key.MaterializationId,
            existingControl.Key.DefinitionFingerprint,
            existingControl.Key.ControlDefinitionFingerprint,
            existingControl.Key.PlanFingerprint,
            existingControl.Key.TargetId,
            new("generation/not-routed"),
            existingControl.Key.Workload,
            existingControl.Key.LoopId);
        MaterializationIndexSyncControlSnapshot unroutedControl = new(
            unroutedControlKey,
            existingControl.Realization,
            ControlLoopState.Create(
                existingControl.Realization.EffectiveDefinition,
                unroutedControlKey.Epoch,
                Epoch));
        Assert.Throws<ArgumentException>(() => MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            fixture.Progress,
            fixture.Generations,
            [unroutedControl],
            fixture.Observation,
            fixture.Provenance));
    }

    [Fact]
    public void CreateExtension_RejectsControlForAnUnroutedBackendWithTheSameGenerationAndDefinition()
    {
        var fixture = CreateFixture();
        var existing = Assert.Single(fixture.Control);
        MaterializationIndexSyncControlStateKey wrongBackendKey = new(
            existing.Key.MaterializationId,
            existing.Key.DefinitionFingerprint,
            existing.Key.ControlDefinitionFingerprint,
            existing.Key.PlanFingerprint,
            new("target/not-routed"),
            existing.Key.GenerationId,
            existing.Key.Workload,
            existing.Key.LoopId);
        MaterializationIndexSyncControlSnapshot wrongBackend = new(
            wrongBackendKey,
            existing.Realization,
            ControlLoopState.Create(
                existing.Realization.EffectiveDefinition,
                wrongBackendKey.Epoch,
                Epoch));

        Assert.Throws<ArgumentException>(() => MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            fixture.Progress,
            fixture.Generations,
            [wrongBackend],
            fixture.Observation,
            fixture.Provenance));
    }

    [Fact]
    public void CreateExtension_RejectsMultiplePlansForTheSameBackendGenerationWorkloadLoop()
    {
        var fixture = CreateFixture();
        var existing = Assert.Single(fixture.Control);
        MaterializationIndexSyncControlStateKey conflictingPlanKey = new(
            existing.Key.MaterializationId,
            existing.Key.DefinitionFingerprint,
            existing.Key.ControlDefinitionFingerprint,
            new("sha256", "tests/index-sync-control-plan/v1", new string('d', 64)),
            existing.Key.TargetId,
            existing.Key.GenerationId,
            existing.Key.Workload,
            existing.Key.LoopId);
        MaterializationIndexSyncControlSnapshot conflictingPlan = new(
            conflictingPlanKey,
            existing.Realization,
            ControlLoopState.Create(
                existing.Realization.EffectiveDefinition,
                conflictingPlanKey.Epoch,
                Epoch));

        Assert.Throws<ArgumentException>(() => MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            fixture.Progress,
            fixture.Generations,
            [existing, conflictingPlan],
            fixture.Observation,
            fixture.Provenance));
    }

    [Fact]
    public void CreateExtension_DoesNotAdmitObservationsForProjectedCleanupTombstones()
    {
        var fixture = CreateFixture();
        var cleaned = Assert.Single(fixture.Routing.Cleaned);
        MaterializationIndexSyncGenerationStatus cleanedGeneration = new(
            cleaned,
            LoadingSnapshot(
                cleaned.GenerationId,
                cleaned.DefinitionFingerprint,
                pendingRetryableMutationCount: 0,
                visibleItemCount: 0,
                tombstoneCount: 0));
        MaterializationIndexSyncProgressStatus cleanedProgress = new(
            cleaned,
            Progress(
                cleaned.GenerationId,
                cleaned.DefinitionFingerprint,
                ScopeFor("cleaned"),
                suffix: "cleaned",
                batchPageOrdinal: 1));

        Assert.Throws<ArgumentException>(() => MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            [],
            [cleanedGeneration],
            [],
            new(),
            fixture.Provenance));
        Assert.Throws<ArgumentException>(() => MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            [cleanedProgress],
            fixture.Generations,
            [],
            new(),
            fixture.Provenance));

        var extension = MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            fixture.Progress,
            fixture.Generations,
            fixture.Control,
            fixture.Observation,
            fixture.Provenance);
        var projected = Assert.Single(Root(extension).GetProperty("cleaned").Array);
        Assert.Equal(cleaned.TargetId.Value, projected.GetProperty("target").GetRequiredString());
        Assert.Equal(cleaned.GenerationId.Value, projected.GetProperty("generation").GetRequiredString());
    }

    [Fact]
    public void CreateExtension_QualifiesSharedScopesAndAcceptsCandidateOnlyRoutingEvidence()
    {
        var fixture = CreateFixture();
        var candidate = fixture.Routing.Candidate!;
        var candidateProgress = fixture.Progress.Single(status => status.Generation == candidate);
        var candidateGeneration = fixture.Generations.Single(status => status.Generation == candidate);
        var candidateLag = fixture.Observation.ChangeLag.Single(
            lag => lag.Observation.Request.Generation == candidate.GenerationId);
        MaterializationBackendRoutingSnapshot candidateOnly = new(
            fixture.Routing.PlacementSlice,
            new("1"),
            MaterializationBackendRoutingFence.Initial,
            activeRead: null,
            activeWrite: null,
            candidate: candidate,
            draining: [],
            retired: [],
            cleaned: []);

        var candidateExtension = MaterializationIndexSyncStatusProjector.CreateExtension(
            candidateOnly,
            [candidateProgress],
            [candidateGeneration],
            [],
            new(changeLag: [candidateLag]),
            fixture.Provenance);
        var candidateRoot = Root(candidateExtension);

        Assert.Equal(ObservationValueKind.Null, candidateRoot.GetProperty("configuration").Kind);
        Assert.Equal(ObservationValueKind.Null, candidateRoot.GetProperty("activeReadTarget").Kind);
        Assert.Equal("target/a", Assert.Single(candidateRoot.GetProperty("shards").Array).GetProperty("target").GetRequiredString());
        Assert.Equal("target/a", Assert.Single(candidateRoot.GetProperty("changeLag").Array).GetProperty("target").GetRequiredString());

        var sharedScope = candidateProgress.Snapshot.Key.Scope;
        MaterializationIndexSyncProgressStatus activeAtSameScope = new(
            fixture.Routing.ActiveWrite!,
            new(
                new(
                    MaterializationBackendPoolTestFixture.Materialization,
                    fixture.Routing.ActiveWrite!.DefinitionFingerprint,
                    fixture.Routing.ActiveWrite.GenerationId,
                    sharedScope),
                MaterializationProgressRevision.Initial,
                MaterializationProgressFence.Initial,
                fenceOwner: "worker/shared-active"));
        var sharedScopeExtension = MaterializationIndexSyncStatusProjector.CreateExtension(
            fixture.Routing,
            [candidateProgress, activeAtSameScope],
            fixture.Generations,
            [],
            new(),
            fixture.Provenance);

        Assert.Equal(
            ["target/a", "target/z"],
            Root(sharedScopeExtension).GetProperty("shards").Array
                .Select(static shard => shard.GetProperty("target").GetRequiredString()));
    }

    [Fact]
    public void CreateExtension_DistinguishesSamePoolAndSliceIdentityByExactPlacementFingerprint()
    {
        var fixture = CreateFixture();
        var original = fixture.Routing.PlacementSlice;
        MaterializationPlacementSliceReference changedContent = new(
            schemaVersion: MaterializationPlacementSliceReference.CurrentSchemaVersion,
            id: original.Id,
            materialization: original.Materialization,
            membership: original.Membership,
            pool: original.Pool,
            target: original.Target,
            subjects: [new("placement/status/changed")]);
        Assert.Equal(original.Id, changedContent.Id);
        Assert.NotEqual(original.Fingerprint, changedContent.Fingerprint);

        var candidate = fixture.Routing.Candidate;
        MaterializationBackendRoutingSnapshot originalRouting = new(
            placementSlice: original,
            revision: new("1"),
            latestFence: MaterializationBackendRoutingFence.Initial,
            activeRead: null,
            activeWrite: null,
            candidate: candidate,
            draining: [],
            retired: [],
            cleaned: []);
        MaterializationBackendRoutingSnapshot changedRouting = new(
            placementSlice: changedContent,
            revision: new("1"),
            latestFence: MaterializationBackendRoutingFence.Initial,
            activeRead: null,
            activeWrite: null,
            candidate: candidate,
            draining: [],
            retired: [],
            cleaned: []);

        var originalExtension = MaterializationIndexSyncStatusProjector.CreateExtension(
            originalRouting,
            progress: [],
            generations: [],
            control: [],
            observation: new(),
            provenance: fixture.Provenance);
        var changedExtension = MaterializationIndexSyncStatusProjector.CreateExtension(
            changedRouting,
            progress: [],
            generations: [],
            control: [],
            observation: new(),
            provenance: fixture.Provenance);
        var originalRoot = Root(originalExtension);
        var changedRoot = Root(changedExtension);

        Assert.Equal(
            originalRoot.GetProperty("placementSlice").GetRequiredString(),
            changedRoot.GetProperty("placementSlice").GetRequiredString());
        Assert.NotEqual(
            originalRoot.GetProperty("placementSliceFingerprint").GetProperty("value").GetRequiredString(),
            changedRoot.GetProperty("placementSliceFingerprint").GetProperty("value").GetRequiredString());
        Assert.NotEqual(
            MaterializationIndexSyncStatusWireNames.PlacementStatusPath(original),
            MaterializationIndexSyncStatusWireNames.PlacementStatusPath(changedContent));
    }

    static StatusFixture CreateFixture()
    {
        var candidateDescriptor = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var activeDescriptor = MaterializationBackendPoolTestFixture.Descriptor("target/z");
        var retiredDescriptor = MaterializationBackendPoolTestFixture.Descriptor("target/r");
        var drainingDescriptor = MaterializationBackendPoolTestFixture.Descriptor("target/d");
        var cleanedDescriptor = MaterializationBackendPoolTestFixture.Descriptor("target/c");
        var pool = MaterializationBackendPoolTestFixture.Definition(
            [activeDescriptor, candidateDescriptor, retiredDescriptor, drainingDescriptor, cleanedDescriptor],
            defaultTarget: candidateDescriptor.Id,
            poolId: "pool/status");
        var poolDocument = MaterializationBackendPoolDocument.FromDefinition(pool);
        var placementSlice = CreatePlacementSlice(pool, poolDocument, candidateDescriptor.Id);
        var activeGenerationPlacementSlice = CreatePlacementSlice(pool, poolDocument, activeDescriptor.Id);
        MaterializationBackendGenerationReference candidate = new(
            candidateDescriptor.Id,
            new("generation/a"),
            pool.DefinitionFingerprint);
        MaterializationBackendGenerationReference active = new(
            activeDescriptor.Id,
            new("generation/z"),
            pool.DefinitionFingerprint);
        MaterializationBackendGenerationReference retired = new(
            retiredDescriptor.Id,
            new("generation/r"),
            pool.DefinitionFingerprint);
        MaterializationBackendGenerationReference draining = new(
            drainingDescriptor.Id,
            new("generation/d"),
            pool.DefinitionFingerprint);
        MaterializationBackendGenerationReference cleaned = new(
            cleanedDescriptor.Id,
            new("generation/c"),
            pool.DefinitionFingerprint);
        MaterializationReadableBackendReference read = new(
            placementSlice,
            active,
            new MaterializationActiveGenerationReference(
                schemaVersion: MaterializationActiveGenerationReference.CurrentSchemaVersion,
                authority: CreateAuthority(activeGenerationPlacementSlice),
                generation: active.GenerationId,
                targetRevision: new("4"),
                promotion: new("promotion/z"),
                promotionFence: new("2"),
                validation: new("validation/z"),
                activatedAtUtc: Epoch));
        var configuration = MaterializationBackendRoutingConfigurationResolver.Resolve(
            pool,
            new(
                EffectiveConfigurationOrigin.Explicit,
                "tests/explicit-routing/v1",
                new(writeTarget: active.TargetId)),
            new(
                EffectiveConfigurationOrigin.ScopedProfile,
                "tests/profile-routing/v1",
                new(readTarget: active.TargetId)));
        MaterializationBackendRoutingSnapshot routing = new(
            placementSlice,
            revision: new("7"),
            latestFence: new("3"),
            activeRead: read,
            activeWrite: active,
            candidate: candidate,
            draining: [new(draining, new("6"))],
            retired: [new(retired, new("5"))],
            cleaned: [cleaned],
            configuration: configuration);

        MaterializationIndexSyncGenerationStatus candidateStatus = new(
            candidate,
            LoadingSnapshot(
                candidate.GenerationId,
                pool.DefinitionFingerprint,
                pendingRetryableMutationCount: 2,
                visibleItemCount: 3,
                tombstoneCount: 1));
        MaterializationIndexSyncGenerationStatus activeStatus = new(
            active,
            ActiveSnapshot(active.GenerationId, pool.DefinitionFingerprint));

        var scopeA = ScopeFor("a");
        var scopeZ = ScopeFor("z");
        ImmutableArray<MaterializationIndexSyncProgressStatus> progress =
        [
            new(active, Progress(active.GenerationId, pool.DefinitionFingerprint, scopeZ, "z", batchPageOrdinal: 2)),
            new(candidate, Progress(candidate.GenerationId, pool.DefinitionFingerprint, scopeA, "a", batchPageOrdinal: 4))
        ];

        var controlTemplate = ControlTestFixture.Definition(
            ControlTestFixture.Limits(
                ControlTestFixture.Limit(
                    ControlActuatorKind.Concurrency,
                    minimum: 1,
                    maximum: 10,
                    ControlHardLimitOrigin.Adapter,
                    "adapter/concurrency"),
                ControlTestFixture.Limit(
                    ControlActuatorKind.BatchItems,
                    minimum: 1,
                    maximum: 100,
                    ControlHardLimitOrigin.Semantic,
                    "process/batch")),
            ControlTestFixture.Point(
                (ControlActuatorKind.Concurrency, 4),
                (ControlActuatorKind.BatchItems, 20)));
        ControlLoopDefinition controlDefinition = new(
            controlTemplate.SchemaVersion,
            controlTemplate.Id,
            pool.MaterializationId.Value,
            MaterializationIndexSyncControlCompiler.ApplicationAuthority,
            controlTemplate.Stage,
            controlTemplate.HardLimits,
            controlTemplate.InitialOperatingPoint,
            controlTemplate.Objectives,
            controlTemplate.Policy,
            controlTemplate.Budgets,
            controlTemplate.Provenance);
        MaterializationIndexSyncControlRealization controlRealization = new(
            controlDefinition.Fingerprint,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            controlDefinition);
        MaterializationIndexSyncControlStateKey controlKey = new(
            pool.MaterializationId,
            pool.DefinitionFingerprint,
            controlDefinition.Fingerprint,
            new("sha256", "tests/index-sync-control-plan/v1", new string('c', 64)),
            candidate.TargetId,
            candidate.GenerationId,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            controlDefinition.Id);
        ControlLoopState controlState = ControlLoopState.Create(
            controlDefinition,
            controlKey.Epoch,
            new("cohesive/control", "tenant-a"),
            Epoch);
        ControlObservation controlObservation = new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("observation/status/congested"),
            controlDefinition.Id,
            controlDefinition.Fingerprint,
            controlDefinition.Target,
            controlKey.Epoch,
            controlState.Revision,
            Epoch.AddSeconds(1),
            Epoch.AddSeconds(2),
            Epoch.AddSeconds(3),
            "runtime/status-sampler/v1",
            [
                new(
                    ControlMetricKind.Latency,
                    ControlStatisticKind.P95,
                    ControlMeasurementAvailability.Available,
                    new(250, ControlUnit.Milliseconds),
                    sampleCount: 3)
            ]);
        var controlDecision = AimdControlReferenceRegulator.Evaluate(
            controlDefinition,
            controlState,
            controlObservation,
            Epoch.AddSeconds(4));
        Assert.Equal(ControlDecisionDisposition.Recommended, controlDecision.Disposition);
        controlState = controlDecision.State;
        MaterializationIndexSyncControlSnapshot control = new(
            controlKey,
            controlRealization,
            controlState);

        MaterializationChangeLagObservation candidateLag = new(
            new(pool.MaterializationId, pool.DefinitionFingerprint, candidate.GenerationId),
            scopeA.Source,
            scopeA,
            MaterializationChangeLagEstimateState.Estimated,
            estimatedPendingProviderWork: 17,
            observedAtUtc: Epoch.AddMinutes(6),
            evidenceReference: "provider/a");
        MaterializationChangeLagObservation activeLag = new(
            new(pool.MaterializationId, pool.DefinitionFingerprint, active.GenerationId),
            scopeZ.Source,
            scope: null,
            MaterializationChangeLagEstimateState.Unavailable,
            estimatedPendingProviderWork: null,
            observedAtUtc: Epoch.AddMinutes(7));
        MaterializationIndexSyncRuntimeObservation observation = new(
            lagMilliseconds: 125,
            changeLag: [new(active, activeLag), new(candidate, candidateLag)],
            failures:
            [
                new("status.z", DiagnosticSeverity.Error, "later failure", "/sync/z"),
                new("status.a", DiagnosticSeverity.Error, "earlier failure")
            ],
            etaInputs: new(
                remainingWork: 900,
                observedThroughputPerSecond: 45,
                sampleWindowMilliseconds: 10_000,
                sampleCount: 5,
                unit: "facts"));
        var provenance = MaterializationBackendPoolTestFixture.Provenance("tests/index-sync-status-projection");
        return new(
            routing,
            progress,
            [activeStatus, candidateStatus],
            [control],
            observation,
            provenance);
    }

    static MaterializationPlacementSliceReference CreatePlacementSlice(
        MaterializationBackendPoolDefinition pool,
        MaterializationBackendPoolDocument poolDocument,
        MaterializationTargetId target) =>
        MaterializationPlacementSliceReference.Create(
            materialization: new(
                MaterializationDefinitionReference.CurrentSchemaVersion,
                pool.MaterializationId,
                pool.DefinitionFingerprint),
            membership: new(
                algorithm: "sha256",
                canonicalization: "tests/index-sync-status-membership/v1",
                value: new string('d', 64)),
            pool: MaterializationBackendPoolReference.FromDocument(poolDocument),
            target: target,
            subjects: [new("placement/status")]);

    static MaterializationRebuildLeafExecutionAuthority CreateAuthority(
        MaterializationPlacementSliceReference placementSlice)
    {
        MaterializationRebuildRequestReference request = new(
            schemaVersion: MaterializationRebuildRequestReference.CurrentSchemaVersion,
            materialization: placementSlice.Materialization,
            request: new(
                algorithm: "sha256",
                canonicalization: "tests/index-sync-status-request/v1",
                value: placementSlice.Fingerprint.Value));
        MaterializationRebuildPlanSetReference planSet = new(
            schemaVersion: MaterializationRebuildPlanSetReference.CurrentSchemaVersion,
            request,
            planSet: new(
                algorithm: "sha256",
                canonicalization: "tests/index-sync-status-plan-set/v1",
                value: placementSlice.Fingerprint.Value));
        MaterializationRebuildPlanReference leaf = new(
            plan: new(
                algorithm: "sha256",
                canonicalization: "tests/index-sync-status-leaf/v1",
                value: placementSlice.Fingerprint.Value),
            placementSlice: placementSlice.Fingerprint);
        return new(
            schemaVersion: MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            planSet,
            binding: new(placementSlice, leaf));
    }

    static MaterializationProgressSnapshot Progress(
        MaterializationGenerationId generation,
        ExecutionDefinitionFingerprint definitionFingerprint,
        MaterializationSourceScope scope,
        string suffix,
        long batchPageOrdinal)
    {
        MaterializationSourceContinuation continuation = new(
            formatVersion: 2,
            new("sha256", "tests/read/v1", suffix == "a" ? "aa" : "ff"),
            scope,
            $"continuation/{suffix}");
        MaterializationApplicationCheckpoint batch = new(
            id: new($"checkpoint/batch/{suffix}"),
            kind: MaterializationCheckpointKind.BatchContinuation,
            continuation,
            completion: null,
            position: null,
            appliedDeliveries: [],
            committedAtUtc: Epoch.AddMinutes(1),
            evidenceReference: $"batch/{suffix}",
            batchPageOrdinal: batchPageOrdinal);
        MaterializationSourcePosition position = new(
            formatVersion: 3,
            scope,
            $"position/{suffix}/9");
        MaterializationDeliveryId delivery = new($"delivery/{suffix}");
        MaterializationApplicationCheckpoint change = new(
            id: new($"checkpoint/change/{suffix}"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position,
            appliedDeliveries: [delivery],
            committedAtUtc: Epoch.AddMinutes(2),
            evidenceReference: $"change/{suffix}",
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(position));
        MaterializationSourceSettlement settlement = new(
            id: new($"settlement/{suffix}"),
            checkpoint: change.Id,
            position,
            settledAtUtc: Epoch.AddMinutes(3),
            evidenceReference: $"settlement/{suffix}");
        return new(
            new(
                MaterializationBackendPoolTestFixture.Materialization,
                definitionFingerprint,
                generation,
                scope),
            MaterializationProgressRevision.Initial,
            MaterializationProgressFence.Initial,
            fenceOwner: $"worker/{suffix}",
            latestBatchCheckpoint: batch,
            latestChangeCheckpoint: change,
            latestSettlement: settlement);
    }

    static MaterializationSourceScope ScopeFor(string suffix)
    {
        RelationQuerySourcePlacementBinding placement = new(
            new($"placement/{suffix}"),
            new($"input/{suffix}"),
            new($"node/{suffix}"),
            new($"binding/{suffix}"),
            Shape,
            new($"source/{suffix}"),
            RelationQuerySourcePlacementBindingKind.SourceSet,
            RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            RelationQuerySourcePlacementOrigin.Explicit,
            new(Shape, "id"));
        return new(
            PhysicalPlan,
            placement,
            new($"partition/{suffix}"),
            new($"ordering/{suffix}"));
    }

    static MaterializationGenerationSnapshot LoadingSnapshot(
        MaterializationGenerationId generation,
        ExecutionDefinitionFingerprint definitionFingerprint,
        long pendingRetryableMutationCount,
        long visibleItemCount,
        long tombstoneCount) =>
        new(
            MaterializationBackendPoolTestFixture.Materialization,
            generation,
            definitionFingerprint,
            MaterializationGenerationState.Loading,
            MaterializationGenerationRevision.Initial,
            MaterializationWorkerFence.Initial,
            hasPermanentFailures: false,
            pendingRetryableMutationCount,
            visibleItemCount,
            tombstoneCount,
            sealReceipt: null,
            validationReceipt: null,
            createdAtUtc: Epoch,
            inactivatedAtUtc: null,
            retiredAtUtc: null);

    static MaterializationGenerationSnapshot ActiveSnapshot(
        MaterializationGenerationId generation,
        ExecutionDefinitionFingerprint definitionFingerprint)
    {
        MaterializationSealReceipt seal = new(
            new("seal/z"),
            generation,
            new("2"),
            visibleItemCount: 5,
            new("seal/fingerprint/z"),
            Epoch.AddMinutes(1));
        MaterializationValidationReceipt validation = new(
            new("validation/z"),
            generation,
            new("3"),
            seal.Fingerprint,
            new("validation/fingerprint/z"),
            DocumentValidationResult.Valid,
            Epoch.AddMinutes(2));
        return new(
            MaterializationBackendPoolTestFixture.Materialization,
            generation,
            definitionFingerprint,
            MaterializationGenerationState.Active,
            new("3"),
            MaterializationWorkerFence.Initial,
            hasPermanentFailures: false,
            pendingRetryableMutationCount: 0,
            visibleItemCount: 5,
            tombstoneCount: 2,
            seal,
            validation,
            createdAtUtc: Epoch,
            inactivatedAtUtc: null,
            retiredAtUtc: null);
    }

    static ObservationValue Root(ExecutionRuntimeStatusExtension extension) =>
        Assert.IsType<ObservationValue>(extension.Value.Value!.Value);

    static ObjectTypeRef ObjectField(ObjectTypeRef owner, string fieldName) =>
        Assert.IsType<ObjectTypeRef>(Field(owner, fieldName).Type);

    static ObjectFieldTypeDef Field(ObjectTypeRef owner, string fieldName) =>
        Assert.Single(owner.Fields, field => field.Name == fieldName);

    static void AssertEnum<TEnum>(ObjectTypeRef owner, string fieldName)
        where TEnum : struct, Enum
    {
        var enumeration = Assert.IsType<EnumTypeRef>(Field(owner, fieldName).Type);
        Assert.Equal(typeof(TEnum).Name, enumeration.Name);
        Assert.Equal(Enum.GetNames<TEnum>(), enumeration.Members);
    }

    static ObservationValue ReplaceArrayField(
        ObservationValue root,
        string arrayField,
        string itemField,
        string value)
    {
        var items = root.GetProperty(arrayField).Array;
        var item = items[0];
        var changedItem = ReplaceObjectField(item, itemField, ObservationValue.FromString(value));
        return ReplaceObjectField(
            root,
            arrayField,
            ObservationValue.FromImmutableArray(items.SetItem(0, changedItem)));
    }

    static ObservationValue ReplaceConfigurationDecisionField(
        ObservationValue root,
        string itemField,
        string value)
    {
        var configuration = root.GetProperty("configuration");
        var decisions = configuration.GetProperty("decisions").Array;
        var changedDecision = ReplaceObjectField(
            decisions[0],
            itemField,
            ObservationValue.FromString(value));
        var changedConfiguration = ReplaceObjectField(
            configuration,
            "decisions",
            ObservationValue.FromImmutableArray(decisions.SetItem(0, changedDecision)));
        return ReplaceObjectField(root, "configuration", changedConfiguration);
    }

    static ObservationValue ReplaceNestedArrayField(
        ObservationValue root,
        string outerArrayField,
        string innerArrayField,
        string itemField,
        string value)
    {
        var outerItems = root.GetProperty(outerArrayField).Array;
        var outerItem = outerItems[0];
        var innerItems = outerItem.GetProperty(innerArrayField).Array;
        var changedInnerItem = ReplaceObjectField(
            innerItems[0],
            itemField,
            ObservationValue.FromString(value));
        var changedOuterItem = ReplaceObjectField(
            outerItem,
            innerArrayField,
            ObservationValue.FromImmutableArray(innerItems.SetItem(0, changedInnerItem)));
        return ReplaceObjectField(
            root,
            outerArrayField,
            ObservationValue.FromImmutableArray(outerItems.SetItem(0, changedOuterItem)));
    }

    static ObservationValue ReplaceAppliedPointField(
        ObservationValue root,
        string itemField,
        string value)
    {
        var controls = root.GetProperty("controls").Array;
        var control = controls[0];
        var applied = control.GetProperty("applied");
        var operatingPoint = applied.GetProperty("operatingPoint").Array;
        var changedValue = ReplaceObjectField(
            operatingPoint[0],
            itemField,
            ObservationValue.FromString(value));
        var changedApplied = ReplaceObjectField(
            applied,
            "operatingPoint",
            ObservationValue.FromImmutableArray(operatingPoint.SetItem(0, changedValue)));
        var changedControl = ReplaceObjectField(control, "applied", changedApplied);
        return ReplaceObjectField(
            root,
            "controls",
            ObservationValue.FromImmutableArray(controls.SetItem(0, changedControl)));
    }

    static ObservationValue ReplaceObjectField(
        ObservationValue value,
        string fieldName,
        ObservationValue replacement)
    {
        var fields = ImmutableDictionary.CreateRange(StringComparer.Ordinal, value.Fields!);
        return ObservationValue.FromObject(fields.SetItem(fieldName, replacement));
    }

    sealed record StatusFixture(
        MaterializationBackendRoutingSnapshot Routing,
        ImmutableArray<MaterializationIndexSyncProgressStatus> Progress,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> Generations,
        ImmutableArray<MaterializationIndexSyncControlSnapshot> Control,
        MaterializationIndexSyncRuntimeObservation Observation,
        ExecutionProvenance Provenance);
}
