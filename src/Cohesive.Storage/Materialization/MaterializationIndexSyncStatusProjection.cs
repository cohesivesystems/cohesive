using System.Collections.Immutable;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage.Materialization;

/// <summary>Derived operational health of one materialization generation in index-sync status.</summary>
public enum MaterializationIndexSyncGenerationHealth
{
    /// <summary>Lifecycle evidence does not yet establish health.</summary>
    Unknown = 0,

    /// <summary>The generation is active or validated with no retained failures.</summary>
    Healthy = 1,

    /// <summary>The generation retains retryable work but no permanent failure.</summary>
    Degraded = 2,

    /// <summary>The generation retains a permanent failure or failed validation.</summary>
    Failed = 3
}

/// <summary>Bounded estimator inputs exposed without claiming an authoritative completion time.</summary>
public sealed record MaterializationIndexSyncEtaInputs
{
    /// <summary>Creates bounded throughput-based ETA inputs.</summary>
    /// <param name="remainingWork">Nonnegative remaining work in <paramref name="unit"/>.</param>
    /// <param name="observedThroughputPerSecond">Positive measured throughput in the same unit per second.</param>
    /// <param name="sampleWindowMilliseconds">Positive observation window in milliseconds.</param>
    /// <param name="sampleCount">Positive number of observations in the window.</param>
    /// <param name="unit">Stable semantic work unit.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative, zero where positive, or nonportable.</exception>
    /// <exception cref="ArgumentException"><paramref name="unit"/> is empty or ill-formed Unicode.</exception>
    public MaterializationIndexSyncEtaInputs(
        long remainingWork,
        long observedThroughputPerSecond,
        long sampleWindowMilliseconds,
        long sampleCount,
        string unit)
    {
        RequirePortable(remainingWork, allowZero: true, nameof(remainingWork));
        RequirePortable(observedThroughputPerSecond, allowZero: false, nameof(observedThroughputPerSecond));
        RequirePortable(sampleWindowMilliseconds, allowZero: false, nameof(sampleWindowMilliseconds));
        RequirePortable(sampleCount, allowZero: false, nameof(sampleCount));
        Unit = MaterializationContract.RequireUnicodeIdentity(unit, nameof(unit));
        RemainingWork = remainingWork;
        ObservedThroughputPerSecond = observedThroughputPerSecond;
        SampleWindowMilliseconds = sampleWindowMilliseconds;
        SampleCount = sampleCount;
    }

    /// <summary>Nonnegative remaining work.</summary>
    public long RemainingWork { get; }

    /// <summary>Positive measured throughput per second.</summary>
    public long ObservedThroughputPerSecond { get; }

    /// <summary>Positive observation window in milliseconds.</summary>
    public long SampleWindowMilliseconds { get; }

    /// <summary>Positive number of observations in the window.</summary>
    public long SampleCount { get; }

    /// <summary>Stable semantic work unit.</summary>
    public string Unit { get; }

    static void RequirePortable(long value, bool allowZero, string parameterName)
    {
        if (value < 0
            || !allowZero && value == 0
            || value > ControlQuantity.MaximumPortableValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                allowZero ? "The value must be nonnegative and portable." : "The value must be positive and portable.");
        }
    }
}

/// <summary>Ephemeral index-sync observations not already owned by durable routing, progress, or Control state.</summary>
public sealed record MaterializationIndexSyncRuntimeObservation
{
    /// <summary>Creates supplemental operational observations.</summary>
    /// <param name="lagMilliseconds">Observed end-to-end lag in milliseconds, or <see langword="null"/> when unknown.</param>
    /// <param name="changeLag">Current provider-owned lag observations, scoped where the source can attribute them.</param>
    /// <param name="failures">Current structured failures not already represented by generation flags.</param>
    /// <param name="etaInputs">Optional bounded estimator inputs; no completion estimate is inferred.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lagMilliseconds"/> is negative or nonportable.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="changeLag"/> contains null or <paramref name="failures"/> contains null or non-error diagnostics.
    /// </exception>
    public MaterializationIndexSyncRuntimeObservation(
        long? lagMilliseconds = null,
        ImmutableArray<MaterializationIndexSyncChangeLagStatus> changeLag = default,
        ImmutableArray<DocumentValidationDiagnostic> failures = default,
        MaterializationIndexSyncEtaInputs? etaInputs = null)
    {
        if (lagMilliseconds is < 0 or > ControlQuantity.MaximumPortableValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lagMilliseconds),
                lagMilliseconds,
                "Observed lag must be nonnegative and portable.");
        }

        var normalizedLag = changeLag.IsDefault ? [] : changeLag;
        if (normalizedLag.Any(static value => value is null))
            throw new ArgumentException("Index-sync change-lag observations must be non-null.", nameof(changeLag));
        var normalizedFailures = failures.IsDefault ? [] : failures;
        if (normalizedFailures.Any(static failure => failure is null || failure.Severity != DiagnosticSeverity.Error))
            throw new ArgumentException("Index-sync failures must be non-null error diagnostics.", nameof(failures));
        LagMilliseconds = lagMilliseconds;
        ChangeLag = normalizedLag;
        Failures =
        [
            .. normalizedFailures.Distinct().OrderBy(static failure => failure.Code, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Location, StringComparer.Ordinal)
        ];
        EtaInputs = etaInputs;
    }

    /// <summary>Observed end-to-end lag in milliseconds, or <see langword="null"/> when unknown.</summary>
    public long? LagMilliseconds { get; }

    /// <summary>Provider-owned source lag observations without invented precision.</summary>
    public ImmutableArray<MaterializationIndexSyncChangeLagStatus> ChangeLag { get; }

    /// <summary>Current structured failures in stable code/location order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Failures { get; }

    /// <summary>Optional bounded estimator inputs without a derived ETA claim.</summary>
    public MaterializationIndexSyncEtaInputs? EtaInputs { get; }
}

/// <summary>Exact backend coordinate paired with its current target-owned generation snapshot.</summary>
public sealed record MaterializationIndexSyncGenerationStatus
{
    /// <summary>Creates one exact backend-generation status source.</summary>
    /// <param name="generation">Exact backend and generation coordinate.</param>
    /// <param name="snapshot">Current bounded target-owned generation snapshot.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The snapshot belongs to another generation or definition.</exception>
    public MaterializationIndexSyncGenerationStatus(
        MaterializationBackendGenerationReference generation,
        MaterializationGenerationSnapshot snapshot)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (generation.GenerationId != snapshot.GenerationId
            || generation.DefinitionFingerprint != snapshot.DefinitionFingerprint)
        {
            throw new ArgumentException(
                "Generation status must pair an exact backend coordinate with its target snapshot.",
                nameof(snapshot));
        }
    }

    /// <summary>Exact backend and generation coordinate.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Current bounded target-owned generation snapshot.</summary>
    public MaterializationGenerationSnapshot Snapshot { get; }
}

/// <summary>Exact backend coordinate paired with one target generation's durable source-feed progress.</summary>
public sealed record MaterializationIndexSyncProgressStatus
{
    /// <summary>Creates one target-qualified durable progress source.</summary>
    /// <param name="generation">Exact backend and generation receiving the source work.</param>
    /// <param name="snapshot">Current durable progress for one source-feed scope.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The progress belongs to another generation or definition.</exception>
    public MaterializationIndexSyncProgressStatus(
        MaterializationBackendGenerationReference generation,
        MaterializationProgressSnapshot snapshot)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (generation.GenerationId != snapshot.Key.Generation
            || generation.DefinitionFingerprint != snapshot.Key.DefinitionFingerprint)
        {
            throw new ArgumentException(
                "Index-sync progress must pair an exact backend coordinate with its generation progress.",
                nameof(snapshot));
        }
    }

    /// <summary>Exact backend and generation receiving the source work.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Current durable progress for one source-feed scope.</summary>
    public MaterializationProgressSnapshot Snapshot { get; }
}

/// <summary>Exact backend coordinate paired with one existing provider-owned source lag observation.</summary>
public sealed record MaterializationIndexSyncChangeLagStatus
{
    /// <summary>Creates one target-qualified source lag observation.</summary>
    /// <param name="generation">Exact backend and generation whose source execution was observed.</param>
    /// <param name="observation">Existing provider-owned source lag observation.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The lag observation belongs to another generation or definition.</exception>
    public MaterializationIndexSyncChangeLagStatus(
        MaterializationBackendGenerationReference generation,
        MaterializationChangeLagObservation observation)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
        if (generation.GenerationId != observation.Request.Generation
            || generation.DefinitionFingerprint != observation.Request.DefinitionFingerprint)
        {
            throw new ArgumentException(
                "Index-sync change lag must pair an exact backend coordinate with its source observation.",
                nameof(observation));
        }
    }

    /// <summary>Exact backend and generation whose source execution was observed.</summary>
    public MaterializationBackendGenerationReference Generation { get; }

    /// <summary>Existing provider-owned source lag observation.</summary>
    public MaterializationChangeLagObservation Observation { get; }
}

/// <summary>Projects existing index-sync authorities into the common typed execution-status extension point.</summary>
public static class MaterializationIndexSyncStatusProjector
{
    static readonly ScalarTypeRef StringType = new(ScalarTypeKind.String);
    static readonly ScalarTypeRef IntegerType = new(ScalarTypeKind.Int64);
    static readonly ScalarTypeRef BooleanType = new(ScalarTypeKind.Bool);
    static readonly ScalarTypeRef InstantType = new(ScalarTypeKind.Instant);
    static readonly EnumTypeRef CheckpointKindType = EnumType<MaterializationCheckpointKind>();
    static readonly EnumTypeRef SourceReadStateType = EnumType<RelationQuerySourceReadState>();
    static readonly EnumTypeRef SettlementKindType = EnumType<ChannelSettlementKind>();
    static readonly EnumTypeRef ChangeLagEstimateStateType = EnumType<MaterializationChangeLagEstimateState>();
    static readonly EnumTypeRef GenerationStateType = EnumType<MaterializationGenerationState>();
    static readonly EnumTypeRef GenerationHealthType = EnumType<MaterializationIndexSyncGenerationHealth>();
    static readonly EnumTypeRef ControlActuatorType = EnumType<ControlActuatorKind>();
    static readonly EnumTypeRef ControlUnitType = EnumType<ControlUnit>();
    static readonly EnumTypeRef ControlStageType = EnumType<ControlStageKind>();
    static readonly EnumTypeRef ControlWorkloadType = EnumType<MaterializationIndexSyncWorkloadKind>();
    static readonly EnumTypeRef ControlHardLimitOriginType = EnumType<ControlHardLimitOrigin>();
    static readonly EnumTypeRef ControlPressureType = EnumType<ControlPressureClassification>();
    static readonly EnumTypeRef ControlMetricType = EnumType<ControlMetricKind>();
    static readonly EnumTypeRef ControlStatisticType = EnumType<ControlStatisticKind>();
    static readonly EnumTypeRef ControlObjectiveDirectionType = EnumType<ControlObjectiveDirection>();
    static readonly EnumTypeRef ControlMeasurementAvailabilityType = EnumType<ControlMeasurementAvailability>();
    static readonly EnumTypeRef ControlApplicationPointType = EnumType<ControlApplicationPointKind>();
    static readonly EnumTypeRef ControlRecommendationDirectionType = EnumType<ControlRecommendationDirection>();
    static readonly EnumTypeRef DocumentOriginType = EnumType<DocumentOrigin>();
    static readonly EnumTypeRef ConfigurationOriginType = EnumType<EffectiveConfigurationOrigin>();
    static readonly EnumTypeRef RoutingSettingType = new(
        "MaterializationBackendRoutingSetting",
        MaterializationBackendRoutingSettingNames.All);

    static readonly ObjectTypeRef BackendGenerationCoordinateType = new(
    [
        new("target", StringType),
        new("generation", StringType)
    ]);

    static readonly ObjectTypeRef DefinitionFingerprintType = new(
    [
        new("algorithm", StringType),
        new("canonicalization", StringType),
        new("value", StringType)
    ]);

    static readonly ObjectTypeRef ShardType = new(
    [
        .. BackendGenerationCoordinateType.Fields,
        new("input", StringType),
        new("source", StringType),
        new("partition", StringType),
        new("orderingScope", StringType),
        new("batchCheckpointId", StringType, nullability: FieldNullability.Nullable),
        new("batchCheckpointKind", CheckpointKindType, nullability: FieldNullability.Nullable),
        new("batchContinuationFormatVersion", IntegerType),
        new("batchContinuation", StringType, nullability: FieldNullability.Nullable),
        new("batchCompletionState", SourceReadStateType, nullability: FieldNullability.Nullable),
        new("batchPageOrdinal", IntegerType),
        new("incrementalCheckpointId", StringType, nullability: FieldNullability.Nullable),
        new("incrementalPositionFormatVersion", IntegerType),
        new("incrementalPosition", StringType, nullability: FieldNullability.Nullable),
        new("incrementalAppliedDeliveryCount", IntegerType),
        new("settlementId", StringType, nullability: FieldNullability.Nullable),
        new("settlementCheckpoint", StringType, nullability: FieldNullability.Nullable),
        new("settlementKind", SettlementKindType, nullability: FieldNullability.Nullable),
        new("settlementPositionFormatVersion", IntegerType),
        new("settlementPosition", StringType, nullability: FieldNullability.Nullable),
        new("settlementDeliveries", StringType, cardinality: FieldCardinality.Many),
        new("settledAtUtc", InstantType, nullability: FieldNullability.Nullable)
    ]);

    static readonly ObjectTypeRef ChangeLagType = new(
    [
        .. BackendGenerationCoordinateType.Fields,
        new("source", StringType),
        new("input", StringType, nullability: FieldNullability.Nullable),
        new("partition", StringType, nullability: FieldNullability.Nullable),
        new("orderingScope", StringType, nullability: FieldNullability.Nullable),
        new("estimateState", ChangeLagEstimateStateType),
        new("estimatedPendingProviderWork", IntegerType, nullability: FieldNullability.Nullable),
        new("observedAtUtc", InstantType),
        new("evidenceReference", StringType, nullability: FieldNullability.Nullable)
    ]);

    static readonly ObjectTypeRef GenerationType = new(
    [
        .. BackendGenerationCoordinateType.Fields,
        new("state", GenerationStateType),
        new("health", GenerationHealthType),
        new("visibleItemCount", IntegerType),
        new("tombstoneCount", IntegerType),
        new("pendingRetryableMutationCount", IntegerType),
        new("hasPermanentFailures", BooleanType)
    ]);

    static readonly ObjectTypeRef ProvenanceType = new(
    [
        new("producer", StringType),
        new("producerVersion", StringType, nullability: FieldNullability.Nullable),
        new("sourceReference", StringType),
        new("sourcePath", StringType, cardinality: FieldCardinality.Many),
        new("sourceDescription", StringType, nullability: FieldNullability.Nullable),
        new("origin", DocumentOriginType)
    ]);

    static readonly ObjectTypeRef OperatingValueType = new(
    [
        new("actuator", ControlActuatorType),
        new("value", IntegerType),
        new("unit", ControlUnitType)
    ]);

    static readonly ObjectTypeRef HardLimitType = new(
    [
        new("actuator", ControlActuatorType),
        new("minimum", IntegerType),
        new("maximum", IntegerType),
        new("unit", ControlUnitType),
        new("origin", ControlHardLimitOriginType),
        new("authority", StringType)
    ]);

    static readonly ObjectTypeRef EffectiveRangeType = new(
    [
        new("actuator", ControlActuatorType),
        new("minimum", IntegerType),
        new("maximum", IntegerType),
        new("unit", ControlUnitType)
    ]);

    static readonly ObjectTypeRef WorkloadBudgetType = new(
    [
        new("actuator", ControlActuatorType),
        new("capacity", IntegerType),
        new("reserved", IntegerType),
        new("available", IntegerType),
        new("unit", ControlUnitType),
        new("origin", ControlHardLimitOriginType),
        new("authority", StringType)
    ]);

    static readonly ObjectTypeRef PolicyConfigurationType = new(
    [
        new("setting", StringType),
        new("origin", ConfigurationOriginType),
        new("authority", StringType)
    ]);

    static readonly ObjectTypeRef ControlPolicyType = new(
    [
        new("actuator", ControlActuatorType),
        new("additiveIncrease", IntegerType),
        new("unit", ControlUnitType),
        new("multiplicativeDecreaseBasisPoints", IntegerType),
        new("healthyObservationCount", IntegerType),
        new("recoveryCooldownMilliseconds", IntegerType),
        new("minimumDwellMilliseconds", IntegerType),
        new("maximumObservationAgeMilliseconds", IntegerType),
        new("minimumSampleCount", IntegerType),
        new("configuration", PolicyConfigurationType, cardinality: FieldCardinality.Many)
    ]);

    static readonly ObjectTypeRef ControlObjectiveType = new(
    [
        new("metric", ControlMetricType),
        new("statistic", ControlStatisticType),
        new("direction", ControlObjectiveDirectionType),
        new("recoveryBoundary", IntegerType),
        new("congestionBoundary", IntegerType),
        new("unit", ControlUnitType)
    ]);

    static readonly ObjectTypeRef MeasurementType = new(
    [
        new("metric", ControlMetricType),
        new("statistic", ControlStatisticType),
        new("availability", ControlMeasurementAvailabilityType),
        new("value", IntegerType, nullability: FieldNullability.Nullable),
        new("unit", ControlUnitType, nullability: FieldNullability.Nullable),
        new("sampleCount", IntegerType),
        new("failureCode", StringType, nullability: FieldNullability.Nullable)
    ]);

    static readonly ObjectTypeRef ControlObservationType = new(
    [
        new("id", StringType),
        new("source", StringType),
        new("expectedRevision", IntegerType),
        new("windowStartedAtUtc", InstantType),
        new("windowEndedAtUtc", InstantType),
        new("observedAtUtc", InstantType),
        new("measurements", MeasurementType, cardinality: FieldCardinality.Many)
    ]);

    static readonly ObjectTypeRef ControlRecommendationType = new(
    [
        new("id", StringType),
        new("direction", ControlRecommendationDirectionType),
        new("actuator", ControlActuatorType),
        new("expectedRevision", IntegerType),
        new("observationId", StringType),
        new("issuedAtUtc", InstantType),
        new("priorActuationId", StringType, nullability: FieldNullability.Nullable),
        new("priorActuationRevision", IntegerType, nullability: FieldNullability.Nullable),
        new("authorizingHealthyObservationCount", IntegerType),
        new("priorOperatingPoint", OperatingValueType, cardinality: FieldCardinality.Many),
        new("proposedOperatingPoint", OperatingValueType, cardinality: FieldCardinality.Many)
    ]);

    static readonly ObjectTypeRef OperatorOverrideType = new(
    [
        new("commandId", StringType),
        new("idempotencyKey", StringType),
        new("expectedRevision", IntegerType),
        new("issuedAtUtc", InstantType),
        new("acceptedRevision", IntegerType),
        new("acceptedAtUtc", InstantType),
        new("authority", StringType),
        new("provenance", ProvenanceType),
        new("requestedOperatingPoint", OperatingValueType, cardinality: FieldCardinality.Many)
    ]);

    static readonly EnumTypeRef ControlActuationSourceType = new(
        "MaterializationIndexSyncControlActuationSource",
        ["Adaptive", "Operator"]);

    static readonly ObjectTypeRef AppliedControlType = new(
    [
        new("operatingPoint", OperatingValueType, cardinality: FieldCardinality.Many),
        new("actuationSource", ControlActuationSourceType, nullability: FieldNullability.Nullable),
        new("actuationId", StringType, nullability: FieldNullability.Nullable),
        new("actuationRevision", IntegerType, nullability: FieldNullability.Nullable),
        new("appliedAtUtc", InstantType, nullability: FieldNullability.Nullable),
        new("applicationFence", StringType, nullability: FieldNullability.Nullable),
        new("applicationPointId", StringType, nullability: FieldNullability.Nullable),
        new("applicationPointKind", ControlApplicationPointType, nullability: FieldNullability.Nullable),
        new("applicationPointObservedAtUtc", InstantType, nullability: FieldNullability.Nullable),
        new("applicationAuthority", StringType, nullability: FieldNullability.Nullable),
        new("applicationSourceReference", StringType, nullability: FieldNullability.Nullable),
        new("adaptiveRecommendation", ControlRecommendationType, nullability: FieldNullability.Nullable),
        new("adaptiveObservation", ControlObservationType, nullability: FieldNullability.Nullable),
        new("operatorOverride", OperatorOverrideType, nullability: FieldNullability.Nullable)
    ]);

    static readonly ObjectTypeRef ControlType = new(
    [
        new("loop", StringType),
        new("stage", ControlStageType),
        new("workload", ControlWorkloadType),
        new("target", StringType),
        new("backendTarget", StringType),
        new("generation", StringType),
        new("epoch", StringType),
        new("revision", IntegerType),
        new("materializationDefinitionFingerprint", DefinitionFingerprintType),
        new("planFingerprint", DefinitionFingerprintType),
        new("authoredDefinitionFingerprint", DefinitionFingerprintType),
        new("effectiveDefinitionFingerprint", DefinitionFingerprintType),
        new("definitionProvenance", ProvenanceType),
        new("hardLimits", HardLimitType, cardinality: FieldCardinality.Many),
        new("effectiveRanges", EffectiveRangeType, cardinality: FieldCardinality.Many),
        new("initialOperatingPoint", OperatingValueType, cardinality: FieldCardinality.Many),
        new("workloadBudgets", WorkloadBudgetType, cardinality: FieldCardinality.Many),
        new("objectives", ControlObjectiveType, cardinality: FieldCardinality.Many),
        new("policy", ControlPolicyType),
        new("lastObservation", ControlObservationType, nullability: FieldNullability.Nullable),
        new("lastClassification", ControlPressureType, nullability: FieldNullability.Nullable),
        new("pendingRecommendation", ControlRecommendationType, nullability: FieldNullability.Nullable),
        new("pendingOperatorOverride", OperatorOverrideType, nullability: FieldNullability.Nullable),
        new("applied", AppliedControlType),
        new("healthyObservationCount", IntegerType),
        new("cooldownUntilUtc", InstantType, nullability: FieldNullability.Nullable)
    ]);

    static readonly ObjectTypeRef FailureType = new(
    [
        new("code", StringType),
        new("message", StringType),
        new("location", StringType, nullability: FieldNullability.Nullable)
    ]);

    static readonly ObjectTypeRef ConfigurationDecisionType = new(
    [
        new("setting", RoutingSettingType),
        new("origin", ConfigurationOriginType),
        new("authority", StringType)
    ]);

    static readonly ObjectTypeRef ConfigurationType = new(
    [
        new("readTarget", StringType),
        new("writeTarget", StringType),
        new("decisions", ConfigurationDecisionType, cardinality: FieldCardinality.Many)
    ]);

    static readonly ObjectTypeRef RetirementType = new(
    [
        .. BackendGenerationCoordinateType.Fields,
        new("retiredAtRevision", IntegerType)
    ]);

    static readonly ObjectTypeRef EtaType = new(
    [
        new("remainingWork", IntegerType),
        new("observedThroughputPerSecond", IntegerType),
        new("sampleWindowMilliseconds", IntegerType),
        new("sampleCount", IntegerType),
        new("unit", StringType)
    ]);

    static readonly ValueContract StatusContract = new(new ObjectTypeRef(
    [
        new("pool", StringType),
        new("poolDefinitionFingerprint", DefinitionFingerprintType),
        new("placementSlice", StringType),
        new("placementSliceFingerprint", DefinitionFingerprintType),
        new("routingRevision", IntegerType),
        new("routingFence", StringType, nullability: FieldNullability.Nullable),
        new("activeReadTarget", StringType, nullability: FieldNullability.Nullable),
        new("activeReadGeneration", StringType, nullability: FieldNullability.Nullable),
        new("activeWriteTarget", StringType, nullability: FieldNullability.Nullable),
        new("activeWriteGeneration", StringType, nullability: FieldNullability.Nullable),
        new("candidateTarget", StringType, nullability: FieldNullability.Nullable),
        new("candidateGeneration", StringType, nullability: FieldNullability.Nullable),
        new("configuration", ConfigurationType, nullability: FieldNullability.Nullable),
        new("draining", BackendGenerationCoordinateType, cardinality: FieldCardinality.Many),
        new("retired", RetirementType, cardinality: FieldCardinality.Many),
        new("cleaned", BackendGenerationCoordinateType, cardinality: FieldCardinality.Many),
        new("lagMilliseconds", IntegerType, nullability: FieldNullability.Nullable),
        new("changeLag", ChangeLagType, cardinality: FieldCardinality.Many),
        new("shards", ShardType, cardinality: FieldCardinality.Many),
        new("generations", GenerationType, cardinality: FieldCardinality.Many),
        new("controls", ControlType, cardinality: FieldCardinality.Many),
        new("failures", FailureType, cardinality: FieldCardinality.Many),
        new("etaInputs", EtaType, nullability: FieldNullability.Nullable)
    ]));

    static EnumTypeRef EnumType<TEnum>()
        where TEnum : struct, Enum =>
        new(typeof(TEnum).Name, [.. Enum.GetNames<TEnum>()]);

    /// <summary>Creates a disclosed typed status extension by projecting existing durable state and observations.</summary>
    /// <param name="routing">Current exact placement-scoped routing snapshot.</param>
    /// <param name="progress">Current bounded per-source progress snapshots.</param>
    /// <param name="generations">Current exact backend coordinates and bounded target generation snapshots.</param>
    /// <param name="control">Current compiled Control realizations paired with their exact durable loop state.</param>
    /// <param name="observation">Supplemental lag, failure, and estimator observations.</param>
    /// <param name="provenance">Attributable runtime producer and source evidence.</param>
    /// <returns>A disclosed versioned index-sync status extension.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A collection is default, contains null, repeats an identity, or carries evidence for another definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The internally projected payload violates its portable contract.</exception>
    public static ExecutionRuntimeStatusExtension CreateExtension(
        MaterializationBackendRoutingSnapshot routing,
        ImmutableArray<MaterializationIndexSyncProgressStatus> progress,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        ImmutableArray<MaterializationIndexSyncControlSnapshot> control,
        MaterializationIndexSyncRuntimeObservation observation,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(provenance);
        ValidateInputs(routing, progress, generations, control, observation.ChangeLag);

        var root = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        root.Add("pool", ObservationValue.FromString(routing.PoolId.Value));
        root.Add("poolDefinitionFingerprint", ProjectFingerprint(routing.PoolDefinitionFingerprint));
        root.Add("placementSlice", ObservationValue.FromString(routing.PlacementSlice.Id.Value));
        root.Add(
            "placementSliceFingerprint",
            ProjectFingerprint(routing.PlacementSlice.Fingerprint));
        root.Add("routingRevision", ObservationValue.FromInt64(routing.Revision.Ordinal));
        root.Add("routingFence", StringOrNull(routing.LatestFence?.Value));
        root.Add("activeReadTarget", StringOrNull(routing.ActiveRead?.Generation.TargetId.Value));
        root.Add("activeReadGeneration", StringOrNull(routing.ActiveRead?.Generation.GenerationId.Value));
        root.Add("activeWriteTarget", StringOrNull(routing.ActiveWrite?.TargetId.Value));
        root.Add("activeWriteGeneration", StringOrNull(routing.ActiveWrite?.GenerationId.Value));
        root.Add("candidateTarget", StringOrNull(routing.Candidate?.TargetId.Value));
        root.Add("candidateGeneration", StringOrNull(routing.Candidate?.GenerationId.Value));
        root.Add("configuration", ProjectConfiguration(routing.Configuration));
        root.Add("draining", Coordinates(routing.Draining.Select(static drain => drain.Generation)));
        root.Add("retired", ProjectRetirements(routing.Retired));
        root.Add("cleaned", Coordinates(routing.Cleaned));
        root.Add("lagMilliseconds", observation.LagMilliseconds is { } lag
            ? ObservationValue.FromInt64(lag)
            : ObservationValue.Null);
        root.Add("changeLag", ProjectChangeLag(observation.ChangeLag, generations));
        root.Add("shards", ProjectShards(progress));
        root.Add("generations", ProjectGenerations(generations));
        root.Add("controls", ProjectControls(control));
        root.Add("failures", ProjectFailures(observation.Failures));
        root.Add("etaInputs", ProjectEta(observation.EtaInputs));

        var value = PortableValue.Concrete(StatusContract, ObservationValue.FromObject(root.ToImmutable()));
        var validation = PortableExecutionValidator.Validate(value);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "The projected index-sync status violates its portable contract: "
                + string.Join(" ", validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        }
        StorageExecutionTelemetry.RecordMaterialization(generations, progress, control, observation);
        return new ExecutionRuntimeStatusExtension(
            MaterializationIndexSyncStatusWireNames.ExtensionId,
            MaterializationIndexSyncStatusWireNames.SchemaVersion,
            ExecutionStatusValue.Disclose(value),
            provenance);
    }

    static void ValidateInputs(
        MaterializationBackendRoutingSnapshot routing,
        ImmutableArray<MaterializationIndexSyncProgressStatus> progress,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        ImmutableArray<MaterializationIndexSyncControlSnapshot> control,
        ImmutableArray<MaterializationIndexSyncChangeLagStatus> changeLag)
    {
        if (progress.IsDefault || progress.Any(static value => value is null))
            throw new ArgumentException("Progress snapshots must be initialized and non-null.", nameof(progress));
        if (generations.IsDefault || generations.Any(static value => value is null))
            throw new ArgumentException("Generation snapshots must be initialized and non-null.", nameof(generations));
        if (control.IsDefault || control.Any(static value => value is null))
            throw new ArgumentException("Control snapshots must be initialized and non-null.", nameof(control));
        if (changeLag.IsDefault || changeLag.Any(static value => value is null))
            throw new ArgumentException("Change-lag observations must be initialized and non-null.", nameof(changeLag));
        var routedGenerations = RoutedGenerations(routing);
        var routedDefinitionFingerprints = routedGenerations
            .Select(static value => value.DefinitionFingerprint)
            .ToHashSet();
        if (progress.Any(value => !routedDefinitionFingerprints.Contains(value.Generation.DefinitionFingerprint))
            || generations.Any(value => !routedDefinitionFingerprints.Contains(value.Generation.DefinitionFingerprint))
            || changeLag.Any(value => !routedDefinitionFingerprints.Contains(value.Generation.DefinitionFingerprint)))
        {
            throw new ArgumentException("Index-sync status inputs must implement the routed materialization definition.");
        }
        if (progress.Any(value => !routedGenerations.Contains(value.Generation))
            || generations.Any(value => !routedGenerations.Contains(value.Generation))
            || changeLag.Any(value => !routedGenerations.Contains(value.Generation)))
        {
            throw new ArgumentException("Index-sync status inputs must address an exact routed backend generation.");
        }
        if (progress.GroupBy(static value => (value.Generation, value.Snapshot.Key.Scope))
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Index-sync status cannot repeat a backend-generation source-feed scope.",
                nameof(progress));
        }
        if (generations.GroupBy(static value => value.Generation).Any(static group => group.Count() > 1))
            throw new ArgumentException("Index-sync status cannot repeat a backend-generation coordinate.", nameof(generations));
        if (control.GroupBy(static value => (
                value.Key.TargetId,
                value.Key.GenerationId,
                value.Key.Workload,
                value.Key.LoopId))
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Index-sync status cannot attribute one backend-generation workload loop to multiple plans.",
                nameof(control));
        }

        foreach (var snapshot in control)
        {
            MaterializationBackendGenerationReference coordinate = new(
                snapshot.Key.TargetId,
                snapshot.Key.GenerationId,
                snapshot.Key.DefinitionFingerprint);
            var routed = routedGenerations.Contains(coordinate);
            var described = generations.Any(generation =>
                generation.Generation == coordinate
                && generation.Snapshot.MaterializationId == snapshot.Key.MaterializationId);
            if (!routed || !described)
            {
                throw new ArgumentException(
                    "Control status must belong to an exact routed and described materialization generation.",
                    nameof(control));
            }
        }

        foreach (var item in progress)
        {
            var generation = ResolveGenerationStatus(item.Generation, generations, nameof(progress));
            if (generation.Snapshot.MaterializationId != item.Snapshot.Key.Materialization)
            {
                throw new ArgumentException(
                    "Target-qualified progress must belong to the exact routed materialization generation.",
                    nameof(progress));
            }
        }

        var lagKeys = new HashSet<(
            MaterializationBackendGenerationReference Generation,
            RelationQuerySourceInstanceId Source,
            MaterializationSourceScope? Scope)>();
        foreach (var lag in changeLag)
        {
            var generation = ResolveLagGeneration(lag, generations, nameof(changeLag));
            if (!lagKeys.Add((generation.Generation, lag.Observation.Source, lag.Observation.Scope)))
            {
                throw new ArgumentException(
                    "Index-sync status cannot repeat a change-lag backend source scope.",
                    nameof(changeLag));
            }
        }
    }

    static MaterializationIndexSyncGenerationStatus ResolveGenerationStatus(
        MaterializationBackendGenerationReference generation,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        string parameterName)
    {
        foreach (var status in generations)
        {
            if (status.Generation == generation)
                return status;
        }

        throw new ArgumentException(
            "Target-qualified progress requires its exact backend generation status.",
            parameterName);
    }

    static MaterializationIndexSyncGenerationStatus ResolveLagGeneration(
        MaterializationIndexSyncChangeLagStatus lag,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations,
        string parameterName)
    {
        var generation = ResolveGenerationStatus(lag.Generation, generations, parameterName);
        if (generation.Snapshot.MaterializationId != lag.Observation.Request.Materialization)
        {
            throw new ArgumentException(
                "Target-qualified change lag must belong to the exact routed materialization generation.",
                parameterName);
        }
        return generation;
    }

    static HashSet<MaterializationBackendGenerationReference> RoutedGenerations(
        MaterializationBackendRoutingSnapshot routing)
    {
        var generations = new HashSet<MaterializationBackendGenerationReference>();
        if (routing.ActiveRead is { } read)
            generations.Add(read.Generation);
        if (routing.ActiveWrite is { } write)
            generations.Add(write);
        if (routing.Candidate is { } candidate)
            generations.Add(candidate);
        foreach (var drain in routing.Draining)
            generations.Add(drain.Generation);
        foreach (var retired in routing.Retired)
            generations.Add(retired.Generation);
        return generations;
    }

    static ObservationValue ProjectShards(ImmutableArray<MaterializationIndexSyncProgressStatus> progress)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(progress.Length);
        foreach (var status in progress.OrderBy(static value => value.Generation.TargetId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Generation.GenerationId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Snapshot.Key.Scope.Input.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Snapshot.Key.Scope.Source.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Snapshot.Key.Scope.Partition.Value, StringComparer.Ordinal))
        {
            var snapshot = status.Snapshot;
            var batch = snapshot.LatestBatchCheckpoint;
            var incremental = snapshot.LatestChangeCheckpoint;
            var settlement = snapshot.LatestSettlement;
            var fields = CoordinateFields(status.Generation);
            fields.Add("input", ObservationValue.FromString(snapshot.Key.Scope.Input.Value));
            fields.Add("source", ObservationValue.FromString(snapshot.Key.Scope.Source.Value));
            fields.Add("partition", ObservationValue.FromString(snapshot.Key.Scope.Partition.Value));
            fields.Add("orderingScope", ObservationValue.FromString(snapshot.Key.Scope.OrderingScope.Value));
            fields.Add("batchCheckpointId", StringOrNull(batch?.Id.Value));
            fields.Add("batchCheckpointKind", StringOrNull(batch?.Kind.ToString()));
            fields.Add(
                "batchContinuationFormatVersion",
                ObservationValue.FromInt64(batch?.Continuation?.FormatVersion ?? 0));
            fields.Add("batchContinuation", StringOrNull(batch?.Continuation?.Value));
            fields.Add("batchCompletionState", StringOrNull(batch?.Completion?.EvidenceState.ToString()));
            fields.Add("batchPageOrdinal", ObservationValue.FromInt64(batch?.BatchPageOrdinal ?? 0));
            fields.Add("incrementalCheckpointId", StringOrNull(incremental?.Id.Value));
            fields.Add(
                "incrementalPositionFormatVersion",
                ObservationValue.FromInt64(incremental?.Position?.FormatVersion ?? 0));
            fields.Add("incrementalPosition", StringOrNull(incremental?.Position?.Value));
            fields.Add(
                "incrementalAppliedDeliveryCount",
                ObservationValue.FromInt64(incremental?.AppliedDeliveries.Length ?? 0));
            fields.Add("settlementId", StringOrNull(settlement?.Id.Value));
            fields.Add("settlementCheckpoint", StringOrNull(settlement?.Checkpoint.Value));
            fields.Add("settlementKind", StringOrNull(settlement?.Kind.ToString()));
            fields.Add(
                "settlementPositionFormatVersion",
                ObservationValue.FromInt64(settlement?.Position?.FormatVersion ?? 0));
            fields.Add("settlementPosition", StringOrNull(settlement?.Position?.Value));
            fields.Add(
                "settlementDeliveries",
                ObservationValue.FromImmutableArray(
                    settlement is null
                        ? []
                        : [.. settlement.Deliveries.Select(static delivery => ObservationValue.FromString(delivery.Value))]));
            fields.Add(
                "settledAtUtc",
                settlement is null
                    ? ObservationValue.Null
                    : ObservationValue.FromDateTimeOffset(settlement.SettledAtUtc));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectChangeLag(
        ImmutableArray<MaterializationIndexSyncChangeLagStatus> observations,
        ImmutableArray<MaterializationIndexSyncGenerationStatus> generations)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(observations.Length);
        foreach (var lag in observations.Select(value =>
                 {
                     _ = ResolveLagGeneration(value, generations, nameof(observations));
                     return value;
                 }).OrderBy(static value => value.Generation.TargetId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Generation.GenerationId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Observation.Source.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Observation.Scope?.Input.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Observation.Scope?.Partition.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Observation.Scope?.OrderingScope.Value, StringComparer.Ordinal))
        {
            var fields = CoordinateFields(lag.Generation);
            fields.Add("source", ObservationValue.FromString(lag.Observation.Source.Value));
            fields.Add("input", StringOrNull(lag.Observation.Scope?.Input.Value));
            fields.Add("partition", StringOrNull(lag.Observation.Scope?.Partition.Value));
            fields.Add("orderingScope", StringOrNull(lag.Observation.Scope?.OrderingScope.Value));
            fields.Add("estimateState", ObservationValue.FromString(lag.Observation.EstimateState.ToString()));
            fields.Add(
                "estimatedPendingProviderWork",
                lag.Observation.EstimatedPendingProviderWork is { } estimate
                    ? ObservationValue.FromInt64(estimate)
                    : ObservationValue.Null);
            fields.Add("observedAtUtc", ObservationValue.FromDateTimeOffset(lag.Observation.ObservedAtUtc));
            fields.Add("evidenceReference", StringOrNull(lag.Observation.EvidenceReference));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectGenerations(ImmutableArray<MaterializationIndexSyncGenerationStatus> generations)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(generations.Length);
        foreach (var status in generations.OrderBy(static value => value.Generation.TargetId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Generation.GenerationId.Value, StringComparer.Ordinal))
        {
            var generation = status.Snapshot;
            var fields = CoordinateFields(status.Generation);
            fields.Add("state", ObservationValue.FromString(generation.State.ToString()));
            fields.Add("health", ObservationValue.FromString(Health(generation).ToString()));
            fields.Add("visibleItemCount", ObservationValue.FromInt64(generation.VisibleItemCount));
            fields.Add("tombstoneCount", ObservationValue.FromInt64(generation.TombstoneCount));
            fields.Add("pendingRetryableMutationCount", ObservationValue.FromInt64(generation.PendingRetryableMutationCount));
            fields.Add("hasPermanentFailures", ObservationValue.FromBool(generation.HasPermanentFailures));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectControls(ImmutableArray<MaterializationIndexSyncControlSnapshot> control)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(control.Length);
        foreach (var snapshot in control
                     .OrderBy(static value => value.Key.TargetId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Key.GenerationId.Value, StringComparer.Ordinal)
                     .ThenBy(static value => value.Key.Workload)
                     .ThenBy(static value => value.Key.LoopId.Value, StringComparer.Ordinal))
        {
            var realization = snapshot.Realization;
            var definition = realization.EffectiveDefinition;
            var state = snapshot.State;
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("loop", ObservationValue.FromString(state.LoopId.Value));
            fields.Add("stage", ObservationValue.FromString(definition.Stage.ToString()));
            fields.Add("workload", ObservationValue.FromString(realization.Workload.ToString()));
            fields.Add("target", ObservationValue.FromString(state.Target));
            fields.Add("backendTarget", ObservationValue.FromString(snapshot.Key.TargetId.Value));
            fields.Add("generation", ObservationValue.FromString(snapshot.Key.GenerationId.Value));
            fields.Add("epoch", ObservationValue.FromString(state.Epoch.Value));
            fields.Add("revision", ObservationValue.FromInt64(state.Revision.Ordinal));
            fields.Add(
                "materializationDefinitionFingerprint",
                ProjectFingerprint(snapshot.Key.DefinitionFingerprint));
            fields.Add("planFingerprint", ProjectFingerprint(snapshot.Key.PlanFingerprint));
            fields.Add(
                "authoredDefinitionFingerprint",
                ProjectFingerprint(realization.AuthoredDefinitionFingerprint));
            fields.Add("effectiveDefinitionFingerprint", ProjectFingerprint(definition.Fingerprint));
            fields.Add("definitionProvenance", ProjectProvenance(definition.Provenance));
            fields.Add("hardLimits", ProjectHardLimits(definition.HardLimits));
            fields.Add("effectiveRanges", ProjectEffectiveRanges(definition));
            fields.Add("initialOperatingPoint", ProjectOperatingPoint(definition.InitialOperatingPoint));
            fields.Add("workloadBudgets", ProjectWorkloadBudgets(definition.Budgets));
            fields.Add("objectives", ProjectObjectives(definition.Objectives));
            fields.Add("policy", ProjectPolicy(definition.Policy));
            fields.Add("lastObservation", ProjectControlObservation(state.LastObservation));
            fields.Add("lastClassification", StringOrNull(state.LastClassification?.ToString()));
            fields.Add("pendingRecommendation", ProjectRecommendation(state.PendingRecommendation));
            fields.Add("pendingOperatorOverride", ProjectOperatorOverride(state.PendingLimitUpdate));
            fields.Add("applied", ProjectAppliedControl(state));
            fields.Add("healthyObservationCount", ObservationValue.FromInt64(state.HealthyObservationCount));
            fields.Add("cooldownUntilUtc", InstantOrNull(state.CooldownUntilUtc));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }

        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectHardLimits(ControlHardLimits hardLimits)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(hardLimits.Constraints.Length);
        foreach (var constraint in hardLimits.Constraints)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("actuator", ObservationValue.FromString(constraint.Range.Actuator.ToString()));
            fields.Add("minimum", ObservationValue.FromInt64(constraint.Range.Minimum.Value));
            fields.Add("maximum", ObservationValue.FromInt64(constraint.Range.Maximum.Value));
            fields.Add("unit", ObservationValue.FromString(constraint.Range.Minimum.Unit.ToString()));
            fields.Add("origin", ObservationValue.FromString(constraint.Origin.ToString()));
            fields.Add("authority", ObservationValue.FromString(constraint.Authority));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectEffectiveRanges(ControlLoopDefinition definition)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(definition.InitialOperatingPoint.Values.Length);
        foreach (var operatingValue in definition.InitialOperatingPoint.Values)
        {
            var range = definition.GetEffectiveRange(operatingValue.Actuator);
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("actuator", ObservationValue.FromString(range.Actuator.ToString()));
            fields.Add("minimum", ObservationValue.FromInt64(range.Minimum.Value));
            fields.Add("maximum", ObservationValue.FromInt64(range.Maximum.Value));
            fields.Add("unit", ObservationValue.FromString(range.Minimum.Unit.ToString()));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectOperatingPoint(ControlOperatingPoint point)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(point.Values.Length);
        foreach (var value in point.Values)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("actuator", ObservationValue.FromString(value.Actuator.ToString()));
            fields.Add("value", ObservationValue.FromInt64(value.Quantity.Value));
            fields.Add("unit", ObservationValue.FromString(value.Quantity.Unit.ToString()));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectWorkloadBudgets(ImmutableArray<ControlWorkloadBudget> budgets)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(budgets.Length);
        foreach (var budget in budgets)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("actuator", ObservationValue.FromString(budget.Actuator.ToString()));
            fields.Add("capacity", ObservationValue.FromInt64(budget.Capacity.Value));
            fields.Add("reserved", ObservationValue.FromInt64(budget.Reserved.Value));
            fields.Add("available", ObservationValue.FromInt64(budget.Available.Value));
            fields.Add("unit", ObservationValue.FromString(budget.Capacity.Unit.ToString()));
            fields.Add("origin", ObservationValue.FromString(budget.Origin.ToString()));
            fields.Add("authority", ObservationValue.FromString(budget.Authority));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectPolicy(AimdControlPolicy policy)
    {
        var configuration = ImmutableArray.CreateBuilder<ObservationValue>(policy.Configuration.Length);
        foreach (var decision in policy.Configuration)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("setting", ObservationValue.FromString(decision.Setting));
            fields.Add("origin", ObservationValue.FromString(decision.Origin.ToString()));
            fields.Add("authority", ObservationValue.FromString(decision.Authority));
            configuration.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }

        var result = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        result.Add("actuator", ObservationValue.FromString(policy.Actuator.ToString()));
        result.Add("additiveIncrease", ObservationValue.FromInt64(policy.AdditiveIncrease.Value));
        result.Add("unit", ObservationValue.FromString(policy.AdditiveIncrease.Unit.ToString()));
        result.Add(
            "multiplicativeDecreaseBasisPoints",
            ObservationValue.FromInt64(policy.MultiplicativeDecreaseBasisPoints));
        result.Add("healthyObservationCount", ObservationValue.FromInt64(policy.HealthyObservationCount));
        result.Add(
            "recoveryCooldownMilliseconds",
            ObservationValue.FromInt64(policy.RecoveryCooldownMilliseconds));
        result.Add("minimumDwellMilliseconds", ObservationValue.FromInt64(policy.MinimumDwellMilliseconds));
        result.Add(
            "maximumObservationAgeMilliseconds",
            ObservationValue.FromInt64(policy.MaximumObservationAgeMilliseconds));
        result.Add("minimumSampleCount", ObservationValue.FromInt64(policy.MinimumSampleCount));
        result.Add("configuration", ObservationValue.FromImmutableArray(configuration.MoveToImmutable()));
        return ObservationValue.FromObject(result.ToImmutable());
    }

    static ObservationValue ProjectObjectives(ImmutableArray<ControlObjective> objectives)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(objectives.Length);
        foreach (var objective in objectives)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("metric", ObservationValue.FromString(objective.Metric.ToString()));
            fields.Add("statistic", ObservationValue.FromString(objective.Statistic.ToString()));
            fields.Add("direction", ObservationValue.FromString(objective.Direction.ToString()));
            fields.Add("recoveryBoundary", ObservationValue.FromInt64(objective.RecoveryBoundary.Value));
            fields.Add("congestionBoundary", ObservationValue.FromInt64(objective.CongestionBoundary.Value));
            fields.Add("unit", ObservationValue.FromString(objective.RecoveryBoundary.Unit.ToString()));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectControlObservation(ControlObservation? observation)
    {
        if (observation is null)
            return ObservationValue.Null;

        var measurements = ImmutableArray.CreateBuilder<ObservationValue>(observation.Measurements.Length);
        foreach (var measurement in observation.Measurements)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("metric", ObservationValue.FromString(measurement.Metric.ToString()));
            fields.Add("statistic", ObservationValue.FromString(measurement.Statistic.ToString()));
            fields.Add("availability", ObservationValue.FromString(measurement.Availability.ToString()));
            fields.Add(
                "value",
                measurement.Value is { } value
                    ? ObservationValue.FromInt64(value.Value)
                    : ObservationValue.Null);
            fields.Add("unit", StringOrNull(measurement.Value?.Unit.ToString()));
            fields.Add("sampleCount", ObservationValue.FromInt64(measurement.SampleCount));
            fields.Add("failureCode", StringOrNull(measurement.FailureCode));
            measurements.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }

        var result = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        result.Add("id", ObservationValue.FromString(observation.Id.Value));
        result.Add("source", ObservationValue.FromString(observation.Source));
        result.Add("expectedRevision", ObservationValue.FromInt64(observation.ExpectedRevision.Ordinal));
        result.Add("windowStartedAtUtc", ObservationValue.FromDateTimeOffset(observation.WindowStartedAtUtc));
        result.Add("windowEndedAtUtc", ObservationValue.FromDateTimeOffset(observation.WindowEndedAtUtc));
        result.Add("observedAtUtc", ObservationValue.FromDateTimeOffset(observation.ObservedAtUtc));
        result.Add("measurements", ObservationValue.FromImmutableArray(measurements.MoveToImmutable()));
        return ObservationValue.FromObject(result.ToImmutable());
    }

    static ObservationValue ProjectRecommendation(ControlRecommendation? recommendation)
    {
        if (recommendation is null)
            return ObservationValue.Null;

        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("id", ObservationValue.FromString(recommendation.Id.Value));
        fields.Add("direction", ObservationValue.FromString(recommendation.Direction.ToString()));
        fields.Add("actuator", ObservationValue.FromString(recommendation.Actuator.ToString()));
        fields.Add("expectedRevision", ObservationValue.FromInt64(recommendation.ExpectedRevision.Ordinal));
        fields.Add("observationId", ObservationValue.FromString(recommendation.ObservationId.Value));
        fields.Add("issuedAtUtc", ObservationValue.FromDateTimeOffset(recommendation.IssuedAtUtc));
        fields.Add("priorActuationId", StringOrNull(recommendation.PriorActuationId?.Value));
        fields.Add("priorActuationRevision", IntOrNull(recommendation.PriorActuationRevision?.Ordinal));
        fields.Add(
            "authorizingHealthyObservationCount",
            ObservationValue.FromInt64(recommendation.AuthorizingHealthyObservationCount));
        fields.Add("priorOperatingPoint", ProjectOperatingPoint(recommendation.PriorOperatingPoint));
        fields.Add("proposedOperatingPoint", ProjectOperatingPoint(recommendation.ProposedOperatingPoint));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectOperatorOverride(ControlLimitUpdateReceipt? receipt)
    {
        if (receipt is null)
            return ObservationValue.Null;

        var command = receipt.Command;
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("commandId", ObservationValue.FromString(command.CommandId.Value));
        fields.Add("idempotencyKey", ObservationValue.FromString(command.IdempotencyKey.Value));
        fields.Add("expectedRevision", ObservationValue.FromInt64(command.ExpectedRevision.Ordinal));
        fields.Add("issuedAtUtc", ObservationValue.FromDateTimeOffset(command.IssuedAtUtc));
        fields.Add("acceptedRevision", ObservationValue.FromInt64(receipt.AcceptedRevision.Ordinal));
        fields.Add("acceptedAtUtc", ObservationValue.FromDateTimeOffset(receipt.AcceptedAtUtc));
        fields.Add(
            "authority",
            ObservationValue.FromString(command.Authorization.AuthorityScope.Authority));
        fields.Add("provenance", ProjectProvenance(command.Provenance));
        fields.Add("requestedOperatingPoint", ProjectOperatingPoint(command.RequestedOperatingPoint));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectAppliedControl(ControlLoopState state)
    {
        ControlApplicationPoint? applicationPoint = null;
        string? source = null;
        if (state.LastAppliedActuationRevision is { } lastRevision)
        {
            if (state.LastActuation?.Revision == lastRevision)
            {
                source = "Adaptive";
                applicationPoint = state.LastActuation.ApplicationPoint;
            }
            else if (state.LastLimitUpdateActuation?.Revision == lastRevision)
            {
                source = "Operator";
                applicationPoint = state.LastLimitUpdateActuation.ApplicationPoint;
            }
        }

        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("operatingPoint", ProjectOperatingPoint(state.OperatingPoint));
        fields.Add("actuationSource", StringOrNull(source));
        fields.Add("actuationId", StringOrNull(state.LastAppliedActuationId?.Value));
        fields.Add("actuationRevision", IntOrNull(state.LastAppliedActuationRevision?.Ordinal));
        fields.Add("appliedAtUtc", InstantOrNull(state.LastAppliedAtUtc));
        fields.Add("applicationFence", StringOrNull(applicationPoint?.Fence.Value));
        fields.Add("applicationPointId", StringOrNull(applicationPoint?.Id.Value));
        fields.Add("applicationPointKind", StringOrNull(applicationPoint?.Kind.ToString()));
        fields.Add("applicationPointObservedAtUtc", InstantOrNull(applicationPoint?.ObservedAtUtc));
        fields.Add("applicationAuthority", StringOrNull(applicationPoint?.Authority));
        fields.Add("applicationSourceReference", StringOrNull(applicationPoint?.SourceReference));
        fields.Add(
            "adaptiveRecommendation",
            source == "Adaptive" ? ProjectRecommendation(state.LastActuation?.Recommendation) : ObservationValue.Null);
        fields.Add(
            "adaptiveObservation",
            source == "Adaptive" ? ProjectControlObservation(state.LastActuation?.Observation) : ObservationValue.Null);
        fields.Add(
            "operatorOverride",
            source == "Operator" ? ProjectOperatorOverride(state.LastLimitUpdateActuation?.Receipt) : ObservationValue.Null);
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectProvenance(ExecutionProvenance provenance)
    {
        var path = provenance.Source.SemanticPath?.Segments ?? [];
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("producer", ObservationValue.FromString(provenance.Producer.Producer));
        fields.Add("producerVersion", StringOrNull(provenance.Producer.Version));
        fields.Add("sourceReference", ObservationValue.FromString(provenance.Source.Reference));
        fields.Add(
            "sourcePath",
            ObservationValue.FromImmutableArray(
                [.. path.Select(static segment => ObservationValue.FromString(segment))]));
        fields.Add("sourceDescription", StringOrNull(provenance.Source.Description));
        fields.Add("origin", ObservationValue.FromString(provenance.Origin.ToString()));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectFailures(ImmutableArray<DocumentValidationDiagnostic> failures)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(failures.Length);
        foreach (var failure in failures)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("code", ObservationValue.FromString(failure.Code));
            fields.Add("message", ObservationValue.FromString(failure.Message));
            fields.Add("location", ObservationValue.FromString(failure.Location));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectConfiguration(MaterializationBackendRoutingConfiguration? configuration)
    {
        if (configuration is null)
            return ObservationValue.Null;

        var decisions = ImmutableArray.CreateBuilder<ObservationValue>(configuration.Configuration.Length);
        foreach (var decision in configuration.Configuration.OrderBy(
                     static value => value.Setting,
                     StringComparer.Ordinal))
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("setting", ObservationValue.FromString(decision.Setting));
            fields.Add("origin", ObservationValue.FromString(decision.Origin.ToString()));
            fields.Add("authority", ObservationValue.FromString(decision.Authority));
            decisions.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }

        var configurationFields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        configurationFields.Add("readTarget", ObservationValue.FromString(configuration.ReadTarget.Value));
        configurationFields.Add("writeTarget", ObservationValue.FromString(configuration.WriteTarget.Value));
        configurationFields.Add("decisions", ObservationValue.FromImmutableArray(decisions.MoveToImmutable()));
        return ObservationValue.FromObject(configurationFields.ToImmutable());
    }

    static ObservationValue ProjectRetirements(ImmutableArray<MaterializationBackendRetirementState> retirements)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(retirements.Length);
        foreach (var retirement in retirements)
        {
            var fields = CoordinateFields(retirement.Generation);
            fields.Add("retiredAtRevision", ObservationValue.FromInt64(retirement.RetiredAtRevision.Ordinal));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectEta(MaterializationIndexSyncEtaInputs? eta)
    {
        if (eta is null)
            return ObservationValue.Null;
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("remainingWork", ObservationValue.FromInt64(eta.RemainingWork));
        fields.Add("observedThroughputPerSecond", ObservationValue.FromInt64(eta.ObservedThroughputPerSecond));
        fields.Add("sampleWindowMilliseconds", ObservationValue.FromInt64(eta.SampleWindowMilliseconds));
        fields.Add("sampleCount", ObservationValue.FromInt64(eta.SampleCount));
        fields.Add("unit", ObservationValue.FromString(eta.Unit));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectFingerprint(ExecutionDefinitionFingerprint fingerprint)
        => ProjectFingerprint(fingerprint.Algorithm, fingerprint.Canonicalization, fingerprint.Value);

    static ObservationValue ProjectFingerprint(MaterializationRebuildPlanFingerprint fingerprint)
        => ProjectFingerprint(fingerprint.Algorithm, fingerprint.Canonicalization, fingerprint.Value);

    static ObservationValue ProjectFingerprint(MaterializationPlacementSliceFingerprint fingerprint)
        => ProjectFingerprint(fingerprint.Algorithm, fingerprint.Canonicalization, fingerprint.Value);

    static ObservationValue ProjectFingerprint(string algorithm, string canonicalization, string value)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("algorithm", ObservationValue.FromString(algorithm));
        fields.Add("canonicalization", ObservationValue.FromString(canonicalization));
        fields.Add("value", ObservationValue.FromString(value));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue Coordinates(IEnumerable<MaterializationBackendGenerationReference> generations)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>();
        foreach (var generation in generations)
            values.Add(Coordinate(generation));
        return ObservationValue.FromImmutableArray(values.ToImmutable());
    }

    static ObservationValue Coordinate(MaterializationBackendGenerationReference generation) =>
        ObservationValue.FromObject(CoordinateFields(generation).ToImmutable());

    static ImmutableDictionary<string, ObservationValue>.Builder CoordinateFields(
        MaterializationBackendGenerationReference generation)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("target", ObservationValue.FromString(generation.TargetId.Value));
        fields.Add("generation", ObservationValue.FromString(generation.GenerationId.Value));
        return fields;
    }

    static ObservationValue StringOrNull(string? value) =>
        value is null ? ObservationValue.Null : ObservationValue.FromString(value);

    static ObservationValue IntOrNull(long? value) =>
        value is null ? ObservationValue.Null : ObservationValue.FromInt64(value.Value);

    static ObservationValue InstantOrNull(DateTimeOffset? value) =>
        value is null ? ObservationValue.Null : ObservationValue.FromDateTimeOffset(value.Value);

    static MaterializationIndexSyncGenerationHealth Health(MaterializationGenerationSnapshot generation)
    {
        if (generation.HasPermanentFailures || generation.ValidationReceipt is { Validation.IsValid: false })
            return MaterializationIndexSyncGenerationHealth.Failed;
        if (generation.PendingRetryableMutationCount != 0)
            return MaterializationIndexSyncGenerationHealth.Degraded;
        return generation.State is MaterializationGenerationState.Active or MaterializationGenerationState.Validated
            ? MaterializationIndexSyncGenerationHealth.Healthy
            : MaterializationIndexSyncGenerationHealth.Unknown;
    }
}
