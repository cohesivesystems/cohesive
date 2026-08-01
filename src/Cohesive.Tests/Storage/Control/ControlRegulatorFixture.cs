using System.Collections.Immutable;
using Cohesive.Control;
using Cohesive.Execution;

namespace Cohesive.Tests.Storage.Control;

internal static class ControlRegulatorFixture
{
    internal static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    internal static ControlLoopDefinition Definition(
        long initial = 6,
        long minimum = 2,
        long maximum = 10,
        long additiveIncrease = 3,
        long multiplicativeDecreaseBasisPoints = 5_000,
        long healthyObservationCount = 2,
        long recoveryCooldownMilliseconds = 10_000,
        long minimumDwellMilliseconds = 5_000,
        long maximumObservationAgeMilliseconds = 60_000,
        long minimumSampleCount = 3,
        long? availableBudget = null,
        ControlMetricKind objectiveMetric = ControlMetricKind.ProcessorUtilization,
        ControlObjectiveDirection objectiveDirection = ControlObjectiveDirection.HigherIsCongested,
        long recoveryBoundary = 6_000,
        long congestionBoundary = 8_000)
    {
        var budgets = availableBudget is { } available
            ? ImmutableArray.Create(
                ControlTestFixture.Budget(
                    ControlActuatorKind.Concurrency,
                    capacity: maximum,
                    reserved: maximum - available,
                    authority: "deployment/realtime-reservation-v1"))
            : ImmutableArray<ControlWorkloadBudget>.Empty;

        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new ControlLoopId("index-target-writes"),
            "materialization:catalog-search",
            "cohesive.processes/reference-v1",
            ControlStageKind.Target,
            ControlTestFixture.Limits(
                ControlTestFixture.Limit(
                    ControlActuatorKind.Concurrency,
                    minimum,
                    maximum,
                    ControlHardLimitOrigin.Adapter,
                    "elastic/capabilities-v1")),
            Point(initial),
            [
                new ControlObjective(
                    objectiveMetric,
                    ControlStatisticKind.P95,
                    objectiveDirection,
                    new ControlQuantity(recoveryBoundary, ControlUnitCatalog.ForMetric(objectiveMetric)),
                    new ControlQuantity(congestionBoundary, ControlUnitCatalog.ForMetric(objectiveMetric)))
            ],
            AimdControlPolicyResolver.Resolve(
                ControlActuatorKind.Concurrency,
                new AimdControlPolicyLayer(
                    EffectiveConfigurationOrigin.Explicit,
                    "test/policy-v1",
                    new AimdControlPolicySettings(
                        additiveIncrease,
                        multiplicativeDecreaseBasisPoints,
                        healthyObservationCount,
                        recoveryCooldownMilliseconds,
                        minimumDwellMilliseconds,
                        maximumObservationAgeMilliseconds,
                        minimumSampleCount))),
            budgets,
            ControlTestFixture.Provenance());
    }

    internal static ControlOperatingPoint Point(long concurrency) =>
        ControlTestFixture.Point((ControlActuatorKind.Concurrency, concurrency));

    internal static ControlLoopState InitialState(
        ControlLoopDefinition definition,
        string epoch = "generation-1") =>
        ControlLoopState.Create(definition, new ControlEpochId(epoch), StartedAtUtc);

    internal static ControlObservation Observation(
        ControlLoopState state,
        string id,
        long value = 5_000,
        DateTimeOffset? observedAtUtc = null,
        long sampleCount = 3,
        ControlMeasurementAvailability availability = ControlMeasurementAvailability.Available,
        bool includeObjective = true,
        ControlEpochId? epoch = null,
        ControlRevision? expectedRevision = null,
        ControlMetricKind metric = ControlMetricKind.ProcessorUtilization,
        DateTimeOffset? windowEndedAtUtc = null,
        ExecutionDefinitionFingerprint? definitionFingerprint = null)
    {
        var observedAt = observedAtUtc ?? state.UpdatedAtUtc.AddSeconds(1);
        var windowEndedAt = windowEndedAtUtc ?? observedAt.AddMilliseconds(-1);
        ImmutableArray<ControlMeasurement> measurements = includeObjective
            ? [Measurement(metric, value, sampleCount, availability)]
            : [
                new ControlMeasurement(
                    ControlMetricKind.MemoryUtilization,
                    ControlStatisticKind.P95,
                    ControlMeasurementAvailability.Available,
                    new ControlQuantity(value, ControlUnit.BasisPoints),
                    sampleCount)
            ];

        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new ControlObservationId(id),
            state.LoopId,
            definitionFingerprint ?? state.DefinitionFingerprint,
            state.Target,
            epoch ?? state.Epoch,
            expectedRevision ?? state.Revision,
            windowEndedAt.AddSeconds(-1),
            windowEndedAt,
            observedAt,
            "runtime/sampler-v1",
            measurements);
    }

    internal static ControlApplicationPoint ApplicationPoint(
        ControlLoopState state,
        string id,
        long fence,
        DateTimeOffset observedAtUtc,
        ControlEpochId? epoch = null,
        ControlRevision? expectedRevision = null,
        string sourceReference = "process:safe-point",
        string authority = "cohesive.processes/reference-v1",
        ControlApplicationPointKind kind = ControlApplicationPointKind.WorkAdmissionBoundary,
        ExecutionDefinitionFingerprint? definitionFingerprint = null) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new ControlApplicationPointId(id),
            state.LoopId,
            definitionFingerprint ?? state.DefinitionFingerprint,
            state.Target,
            epoch ?? state.Epoch,
            expectedRevision ?? state.Revision,
            new ControlApplicationFence(fence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            kind,
            observedAtUtc,
            authority,
            sourceReference);

    static ControlMeasurement Measurement(
        ControlMetricKind metric,
        long value,
        long sampleCount,
        ControlMeasurementAvailability availability) =>
        availability == ControlMeasurementAvailability.Available
            ? new(
                metric,
                ControlStatisticKind.P95,
                availability,
                new ControlQuantity(value, ControlUnitCatalog.ForMetric(metric)),
                sampleCount)
            : new(
                metric,
                ControlStatisticKind.P95,
                availability,
                failureCode: "sampler/unavailable");
}
