using System.Collections.Immutable;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Storage.Control;

internal static class ControlTestFixture
{
    public static ControlQuantity Quantity(ControlActuatorKind actuator, long value) =>
        new(value, ControlUnitCatalog.ForActuator(actuator));

    public static ControlQuantity Quantity(ControlMetricKind metric, long value) =>
        new(value, ControlUnitCatalog.ForMetric(metric));

    public static ControlActuatorValue Value(ControlActuatorKind actuator, long value) =>
        new(actuator, Quantity(actuator, value));

    public static ControlRange Range(ControlActuatorKind actuator, long minimum, long maximum) =>
        new(actuator, Quantity(actuator, minimum), Quantity(actuator, maximum));

    public static ControlHardLimit Limit(
        ControlActuatorKind actuator,
        long minimum,
        long maximum,
        ControlHardLimitOrigin origin,
        string authority) =>
        new(Range(actuator, minimum, maximum), origin, authority);

    public static ControlHardLimits Limits(params ControlHardLimit[] constraints) =>
        new([.. constraints]);

    public static ControlOperatingPoint Point(
        params (ControlActuatorKind Actuator, long Value)[] values)
    {
        var builder = ImmutableArray.CreateBuilder<ControlActuatorValue>(values.Length);
        foreach (var (actuator, value) in values)
            builder.Add(Value(actuator, value));

        return new(builder.MoveToImmutable());
    }

    public static ControlObjective Objective(
        ControlMetricKind metric = ControlMetricKind.Latency,
        ControlStatisticKind statistic = ControlStatisticKind.P95,
        ControlObjectiveDirection direction = ControlObjectiveDirection.HigherIsCongested,
        long recoveryBoundary = 100,
        long congestionBoundary = 200) =>
        new(
            metric,
            statistic,
            direction,
            Quantity(metric, recoveryBoundary),
            Quantity(metric, congestionBoundary));

    public static ControlWorkloadBudget Budget(
        ControlActuatorKind actuator,
        long capacity,
        long reserved,
        ControlHardLimitOrigin origin = ControlHardLimitOrigin.Deployment,
        string authority = "deployment/capacity-v1") =>
        new(
            actuator,
            Quantity(actuator, capacity),
            Quantity(actuator, reserved),
            origin,
            authority);

    public static ControlLoopDefinition Definition(
        ControlHardLimits hardLimits,
        ControlOperatingPoint initialOperatingPoint,
        ImmutableArray<ControlWorkloadBudget> budgets = default,
        AimdControlPolicy? policy = null) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new ControlLoopId("index-sync/target-write"),
            target: "materialization/search-index",
            applicationAuthority: "cohesive.processes/reference-v1",
            ControlStageKind.Target,
            hardLimits,
            initialOperatingPoint,
            [Objective()],
            policy ?? AimdControlPolicyResolver.Resolve(ControlActuatorKind.Concurrency),
            budgets,
            Provenance());

    public static ExecutionProvenance Provenance() =>
        new(
            new ExecutionProducerProvenance("cohesive-tests", "1"),
            new ExecutionSourceProvenance("test:control-definition"),
            DocumentOrigin.Generated);
}
