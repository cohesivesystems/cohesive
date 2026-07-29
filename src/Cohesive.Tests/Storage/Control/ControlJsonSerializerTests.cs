using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Control;

namespace Cohesive.Tests.Storage.Control;

public sealed class ControlJsonSerializerTests
{
    static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PortableContracts_RoundTripStructurallyToIdenticalCanonicalBytes()
    {
        var scenario = CreateAppliedScenario();
        var recommendation = Assert.IsType<ControlRecommendation>(scenario.Decision.Recommendation);
        var actuation = Assert.IsType<ControlActuation>(scenario.ActuationResult.Actuation);

        AssertCanonicalRoundTrip(
            scenario.Definition,
            static value => ControlJsonSerializer.GetCanonicalBytes(value),
            ControlJsonSerializer.DeserializeDefinition);
        AssertCanonicalRoundTrip(
            scenario.Observation,
            static value => ControlJsonSerializer.GetCanonicalBytes(value),
            ControlJsonSerializer.DeserializeObservation);
        AssertCanonicalRoundTrip(
            recommendation,
            static value => CanonicalBytes(ControlJsonSerializer.Serialize(value)),
            json => ControlJsonSerializer.DeserializeRecommendation(json, scenario.Definition));
        AssertCanonicalRoundTrip(
            scenario.ActuationResult.State,
            static value => ControlJsonSerializer.GetCanonicalBytes(value),
            json => ControlJsonSerializer.DeserializeState(json, scenario.Definition));
        AssertCanonicalRoundTrip(
            scenario.Decision,
            static value => ControlJsonSerializer.GetCanonicalBytes(value),
            json => ControlJsonSerializer.DeserializeDecision(json, scenario.Definition));
        AssertCanonicalRoundTrip(
            scenario.ApplicationPoint,
            static value => CanonicalBytes(ControlJsonSerializer.Serialize(value)),
            ControlJsonSerializer.DeserializeApplicationPoint);
        AssertCanonicalRoundTrip(
            actuation,
            static value => CanonicalBytes(ControlJsonSerializer.Serialize(value)),
            json => ControlJsonSerializer.DeserializeActuation(json, scenario.Definition));
        AssertCanonicalRoundTrip(
            scenario.ActuationResult,
            static value => ControlJsonSerializer.GetCanonicalBytes(value),
            json => ControlJsonSerializer.DeserializeActuationResult(json, scenario.Definition));
    }

    [Fact]
    public void DurableState_RoundTripProducesTheSameNextCanonicalDecision()
    {
        var scenario = CreateAppliedScenario();
        var stateJson = ControlJsonSerializer.Serialize(scenario.ActuationResult.State);
        var restoredState = ControlJsonSerializer.DeserializeState(stateJson, scenario.Definition);
        var observedAtUtc = CreatedAtUtc.AddMinutes(3);
        var observation = Observation(
            scenario.Definition,
            scenario.ActuationResult.State,
            id: "observation/after-resume",
            observedAtUtc,
            processorBasisPoints: 9_000);

        var uninterrupted = AimdControlReferenceRegulator.Evaluate(
            scenario.Definition,
            scenario.ActuationResult.State,
            observation,
            evaluatedAtUtc: observedAtUtc);
        var resumed = AimdControlReferenceRegulator.Evaluate(
            scenario.Definition,
            restoredState,
            observation,
            evaluatedAtUtc: observedAtUtc);

        Assert.Equal(uninterrupted, resumed);
        Assert.Equal(
            ControlJsonSerializer.GetCanonicalBytes(uninterrupted),
            ControlJsonSerializer.GetCanonicalBytes(resumed));
    }

    [Fact]
    public void DurableResultRoots_RejectCrossWiredRecommendationAndReceiptContent()
    {
        var scenario = CreateAppliedScenario();
        var recommendation = Assert.IsType<ControlRecommendation>(scenario.Decision.Recommendation);
        var actuation = Assert.IsType<ControlActuation>(scenario.ActuationResult.Actuation);

        Assert.Throws<ArgumentException>(() => new ControlDecision(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlDecisionDisposition.Replayed,
            scenario.ActuationResult.State.UpdatedAtUtc,
            scenario.ActuationResult.State,
            recommendation));
        Assert.Throws<ArgumentException>(() => new ControlActuationResult(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlActuationDisposition.Replayed,
            scenario.Decision.State,
            actuation));
    }

    [Fact]
    public void Reader_RejectsDuplicatePropertiesRecursively()
    {
        var scenario = CreateAppliedScenario();
        var canonical = ControlJsonSerializer.Serialize(scenario.Observation);
        var duplicated = canonical.Replace(
            "\"unit\":\"BasisPoints\"",
            "\"unit\":\"BasisPoints\",\"unit\":\"BasisPoints\"",
            StringComparison.Ordinal);

        Assert.NotEqual(canonical, duplicated);
        var exception = Assert.Throws<JsonException>(() =>
            ControlJsonSerializer.DeserializeObservation(duplicated));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/measurements/0/value/unit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsUnknownNestedMembers()
    {
        var scenario = CreateAppliedScenario();
        var root = ParseObject(ControlJsonSerializer.Serialize(scenario.Definition));
        root["hardLimits"]!["constraints"]![0]!["range"]!["futureLimit"] = true;

        Assert.Throws<JsonException>(() =>
            ControlJsonSerializer.DeserializeDefinition(root.ToJsonString()));
    }

    [Fact]
    public void Reader_RejectsUnsupportedSchemaVersion()
    {
        var scenario = CreateAppliedScenario();
        var root = ParseObject(ControlJsonSerializer.Serialize(scenario.Decision));
        root["schemaVersion"] = "cohesive-control/v2";

        var exception = Assert.Throws<JsonException>(() =>
            ControlJsonSerializer.DeserializeDecision(root.ToJsonString(), scenario.Definition));

        Assert.Contains("Unsupported Control schema", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cohesive-control/v2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsNoncanonicalCollectionOrdering()
    {
        var scenario = CreateAppliedScenario();
        var root = ParseObject(ControlJsonSerializer.Serialize(scenario.Definition));
        var constraints = root["hardLimits"]!["constraints"]!.AsArray();
        Assert.Equal(2, constraints.Count);
        JsonNode[] reversed =
        [
            constraints[1]!.DeepClone(),
            constraints[0]!.DeepClone()
        ];
        constraints.Clear();
        foreach (var constraint in reversed)
            constraints.Add(constraint);

        var exception = Assert.Throws<JsonException>(() =>
            ControlJsonSerializer.DeserializeDefinition(root.ToJsonString()));

        Assert.Contains("canonical typed wire representation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsWrongEnumCasing()
    {
        var scenario = CreateAppliedScenario();
        var root = ParseObject(ControlJsonSerializer.Serialize(scenario.Definition));
        root["stage"] = "target";

        Assert.Throws<JsonException>(() =>
            ControlJsonSerializer.DeserializeDefinition(root.ToJsonString()));
    }

    [Theory]
    [InlineData("explicit")]
    [InlineData(0)]
    public void Reader_RejectsNoncanonicalConfigurationOrigin(object origin)
    {
        var scenario = CreateAppliedScenario();
        var root = ParseObject(ControlJsonSerializer.Serialize(scenario.Definition));
        root["policy"]!["configuration"]![0]!["origin"] = JsonValue.Create(origin);

        Assert.Throws<JsonException>(() =>
            ControlJsonSerializer.DeserializeDefinition(root.ToJsonString()));
    }

    [Fact]
    public void Reader_RejectsNumbersWherePortableIntegerStringsAreRequired()
    {
        var scenario = CreateAppliedScenario();
        var root = ParseObject(ControlJsonSerializer.Serialize(scenario.Definition));
        root["hardLimits"]!["constraints"]![0]!["range"]!["minimum"]!["value"] = 1;

        Assert.Throws<JsonException>(() =>
            ControlJsonSerializer.DeserializeDefinition(root.ToJsonString()));
    }

    static AppliedScenario CreateAppliedScenario()
    {
        var definition = Definition();
        var initialState = AimdControlState.Create(
            definition,
            new("generation/17"),
            CreatedAtUtc);
        var observedAtUtc = CreatedAtUtc.AddMinutes(1);
        var observation = Observation(
            definition,
            initialState,
            id: "observation/congested-1",
            observedAtUtc,
            processorBasisPoints: 9_000);
        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            initialState,
            observation,
            evaluatedAtUtc: observedAtUtc);
        var recommendation = Assert.IsType<ControlRecommendation>(decision.Recommendation);
        Assert.Equal(ControlDecisionDisposition.Recommended, decision.Disposition);

        var applicationPoint = new ControlApplicationPoint(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("safe-point/41"),
            definition.Id,
            definition.Fingerprint,
            definition.Target,
            initialState.Epoch,
            decision.State.Revision,
            new("41"),
            ControlApplicationPointKind.WorkAdmissionBoundary,
            observedAtUtc.AddSeconds(1),
            authority: "cohesive.processes/reference-v1",
            sourceReference: "process/index-sync/attempt/17/cut/41");
        var actuationResult = AimdControlReferenceRegulator.Apply(
            definition,
            decision.State,
            applicationPoint,
            appliedAtUtc: applicationPoint.ObservedAtUtc.AddSeconds(1));

        Assert.Equal(ControlActuationDisposition.Applied, actuationResult.Disposition);
        Assert.Equal(recommendation.ProposedOperatingPoint, actuationResult.State.OperatingPoint);
        return new(definition, observation, decision, applicationPoint, actuationResult);
    }

    static ControlLoopDefinition Definition()
    {
        var limits = ControlTestFixture.Limits(
            ControlTestFixture.Limit(
                ControlActuatorKind.Concurrency,
                minimum: 1,
                maximum: 32,
                ControlHardLimitOrigin.Semantic,
                "materialization/index-sync-v1"),
            ControlTestFixture.Limit(
                ControlActuatorKind.Concurrency,
                minimum: 2,
                maximum: 16,
                ControlHardLimitOrigin.Adapter,
                "elastic/bulk-v8"));
        var point = ControlTestFixture.Point((ControlActuatorKind.Concurrency, 8));
        var policy = AimdControlPolicyResolver.Resolve(
            ControlActuatorKind.Concurrency,
            new AimdControlPolicyLayer(
                EffectiveConfigurationOrigin.ScopedProfile,
                "index-sync/rebuild-v1",
                new(
                    additiveIncrease: 2,
                    healthyObservationCount: 2,
                    recoveryCooldownMilliseconds: 10_000,
                    minimumDwellMilliseconds: 5_000,
                    minimumSampleCount: 10)));

        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("index-sync/target-write"),
            target: "materialization/search-index",
            applicationAuthority: "cohesive.processes/reference-v1",
            ControlStageKind.Target,
            limits,
            point,
            objectives:
            [
                new(
                    ControlMetricKind.ProcessorUtilization,
                    ControlStatisticKind.P95,
                    ControlObjectiveDirection.HigherIsCongested,
                    ControlTestFixture.Quantity(ControlMetricKind.ProcessorUtilization, 6_500),
                    ControlTestFixture.Quantity(ControlMetricKind.ProcessorUtilization, 8_000))
            ],
            policy,
            budgets:
            [
                ControlTestFixture.Budget(
                    ControlActuatorKind.Concurrency,
                    capacity: 16,
                    reserved: 4,
                    ControlHardLimitOrigin.Deployment,
                    "workload-budget/realtime-reservation-v1")
            ],
            ControlTestFixture.Provenance());
    }

    static ControlObservation Observation(
        ControlLoopDefinition definition,
        AimdControlState state,
        string id,
        DateTimeOffset observedAtUtc,
        long processorBasisPoints) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new(id),
            definition.Id,
            definition.Fingerprint,
            definition.Target,
            state.Epoch,
            state.Revision,
            observedAtUtc.AddSeconds(-30),
            observedAtUtc.AddSeconds(-1),
            observedAtUtc,
            source: "sampler/process-cpu-v1",
            measurements:
            [
                new(
                    ControlMetricKind.ProcessorUtilization,
                    ControlStatisticKind.P95,
                    ControlMeasurementAvailability.Available,
                    new(processorBasisPoints, ControlUnit.BasisPoints),
                    sampleCount: 30)
            ]);

    static JsonObject ParseObject(string json) => JsonNode.Parse(json)!.AsObject();

    static byte[] CanonicalBytes(string json) => Encoding.UTF8.GetBytes(json);

    static void AssertCanonicalRoundTrip<T>(
        T expected,
        Func<T, byte[]> getCanonicalBytes,
        Func<string, T> deserialize)
        where T : class
    {
        var canonical = getCanonicalBytes(expected);
        var restored = deserialize(Encoding.UTF8.GetString(canonical));

        Assert.Equal(expected, restored);
        Assert.Equal(expected.GetHashCode(), restored.GetHashCode());
        Assert.Equal(canonical, getCanonicalBytes(restored));
    }

    sealed record AppliedScenario(
        ControlLoopDefinition Definition,
        ControlObservation Observation,
        ControlDecision Decision,
        ControlApplicationPoint ApplicationPoint,
        ControlActuationResult ActuationResult);
}
