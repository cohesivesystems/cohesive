using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

/// <summary>Stable diagnostic codes emitted by the portable Control contracts and reference interpretations.</summary>
public static class ControlDiagnosticCodes
{
    /// <summary>A manual limit-update command was not authorized for the loop's authority boundary.</summary>
    public const string LimitUpdateUnauthorized = "control.limitUpdate.unauthorized";

    /// <summary>A manual limit-update command addressed stale or different loop state.</summary>
    public const string LimitUpdateStaleFence = "control.limitUpdate.staleFence";

    /// <summary>A manual limit-update command identity was reused with different canonical content.</summary>
    public const string LimitUpdateIdentityConflict = "control.limitUpdate.identityConflict";

    /// <summary>A manual limit-update idempotency key was reused for a different semantic intent.</summary>
    public const string LimitUpdateIdempotencyConflict = "control.limitUpdate.idempotencyConflict";

    /// <summary>Another accepted manual limit update is still awaiting its safe application point.</summary>
    public const string LimitUpdatePending = "control.limitUpdate.pending";

    /// <summary>A manual limit-update command cannot be applied as one meaningful bounded transition.</summary>
    public const string LimitUpdateInvalid = "control.limitUpdate.invalid";

    /// <summary>A manual limit-update application point conflicts with retained evidence under the same identity.</summary>
    public const string LimitUpdateApplicationPointConflict = "control.limitUpdate.applicationPointConflict";

    /// <summary>No accepted manual limit update is awaiting a safe application point.</summary>
    public const string LimitUpdateAbsent = "control.limitUpdate.absent";

    /// <summary>Durable controller state cannot be produced by the reference state-transition contract.</summary>
    public const string StateInvalid = "control.state.invalid";

    /// <summary>An operating point does not contain exactly the definition's bounded actuators.</summary>
    public const string OperatingPointShapeMismatch = "control.operatingPoint.shapeMismatch";

    /// <summary>An operating value is outside the intersection of hard constraints.</summary>
    public const string HardLimitExceeded = "control.hardLimit.exceeded";

    /// <summary>An operating value consumes capacity reserved for another workload.</summary>
    public const string WorkloadBudgetExceeded = "control.workloadBudget.exceeded";

    /// <summary>Durable controller state or evidence belongs to different exact loop-definition content.</summary>
    public const string DefinitionFingerprintMismatch = "control.definition.fingerprintMismatch";

    /// <summary>An observation addresses another loop, target, epoch, or revision.</summary>
    public const string ObservationFenceMismatch = "control.observation.fenceMismatch";

    /// <summary>An observation is too old, from the future, or nonmonotonic.</summary>
    public const string ObservationTimeInvalid = "control.observation.timeInvalid";

    /// <summary>A required objective measurement is absent.</summary>
    public const string MeasurementMissing = "control.measurement.missing";

    /// <summary>A required objective measurement was explicitly unavailable.</summary>
    public const string MeasurementUnavailable = "control.measurement.unavailable";

    /// <summary>A required objective measurement contains too few samples.</summary>
    public const string MeasurementInsufficient = "control.measurement.insufficient";

    /// <summary>An observation identity was reused with conflicting content.</summary>
    public const string ObservationConflict = "control.observation.conflict";

    /// <summary>A new observation arrived while a prior recommendation remained pending.</summary>
    public const string RecommendationPending = "control.recommendation.pending";

    /// <summary>An application point does not match the pending recommendation fence.</summary>
    public const string ApplicationFenceMismatch = "control.application.fenceMismatch";

    /// <summary>An application point is not a valid later invariant-preserving cut.</summary>
    public const string ApplicationPointInvalid = "control.application.pointInvalid";

    /// <summary>An application-point identity was reused with conflicting evidence.</summary>
    public const string ApplicationPointConflict = "control.application.pointConflict";

    /// <summary>An actuation receipt is not the exact definition-authorized transition it claims.</summary>
    public const string ActuationInvalid = "control.actuation.invalid";

    /// <summary>A recommendation attempts an unsupported or out-of-bounds operating change.</summary>
    public const string RecommendationInvalid = "control.recommendation.invalid";

    /// <summary>No recommendation is currently available to apply.</summary>
    public const string RecommendationAbsent = "control.recommendation.absent";

    /// <summary>The durable revision space is exhausted and the runtime must begin a new control epoch.</summary>
    public const string RevisionExhausted = "control.revision.exhausted";
}

/// <summary>
/// Canonical definition of one independent bounded control loop.
/// </summary>
/// <remarks>
/// The definition declares only operational policy. It cannot alter entity, relation, transition, Process,
/// materialization, ordering, retry, or delivery semantics. Runtime interpretations may select an operating point
/// only within <see cref="HardLimits"/> and <see cref="Budgets"/> and may apply it only through a fenced application point.
/// </remarks>
public sealed record ControlLoopDefinition
{
    /// <summary>Current exact schema version of portable Control contracts.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } = new("cohesive-control/v1");

    /// <summary>Creates a canonical bounded control-loop definition.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="id">Stable loop identity.</param>
    /// <param name="target">Stable Process, materialization, or runtime subject identity.</param>
    /// <param name="applicationAuthority">Exact Process or materialization interpreter authorized to attest safe points.</param>
    /// <param name="stage">Independently regulated source, transform, or target stage.</param>
    /// <param name="hardLimits">Intersected semantic, compiler, adapter, and deployment bounds.</param>
    /// <param name="initialOperatingPoint">Initial effective multidimensional operating point.</param>
    /// <param name="objectives">One or more soft pressure objectives with hysteresis.</param>
    /// <param name="policy">Resolved attributable AIMD policy.</param>
    /// <param name="budgets">Optional reservations that further narrow surplus capacity.</param>
    /// <param name="provenance">Producer and source attribution for the canonical definition.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, collections contain null/duplicate/inconsistent entries, the policy axis is absent,
    /// or the initial operating point violates a hard limit or workload budget.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stage"/> is unsupported.</exception>
    [JsonConstructor]
    public ControlLoopDefinition(
        ExecutionIrSchemaVersion schemaVersion,
        ControlLoopId id,
        string target,
        string applicationAuthority,
        ControlStageKind stage,
        ControlHardLimits hardLimits,
        ControlOperatingPoint initialOperatingPoint,
        ImmutableArray<ControlObjective> objectives,
        AimdControlPolicy policy,
        ImmutableArray<ControlWorkloadBudget> budgets,
        ExecutionProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("A control definition requires a schema version.", nameof(schemaVersion));
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A control definition requires a loop identity.", nameof(id));
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported control stage.");

        SchemaVersion = schemaVersion;
        Id = id;
        Target = Guard.RequireNotNullOrWhiteSpace(target);
        ApplicationAuthority = Guard.RequireNotNullOrWhiteSpace(applicationAuthority);
        Stage = stage;
        HardLimits = Guard.RequireNotNull(hardLimits);
        InitialOperatingPoint = Guard.RequireNotNull(initialOperatingPoint);
        Policy = Guard.RequireNotNull(policy);
        Provenance = Guard.RequireNotNull(provenance);

        if (objectives.IsDefaultOrEmpty || objectives.Any(static objective => objective is null))
            throw new ArgumentException("A control definition requires one or more non-null objectives.", nameof(objectives));
        if (objectives.GroupBy(static objective => (objective.Metric, objective.Statistic)).Any(static group => group.Count() > 1))
            throw new ArgumentException("A control definition cannot repeat an objective measurement.", nameof(objectives));
        Objectives = [.. objectives
            .OrderBy(static objective => objective.Metric)
            .ThenBy(static objective => objective.Statistic)];

        var normalizedBudgets = budgets.IsDefault ? [] : budgets;
        if (normalizedBudgets.Any(static budget => budget is null))
            throw new ArgumentException("Control workload budgets cannot contain null entries.", nameof(budgets));
        if (normalizedBudgets.GroupBy(static budget => budget.Actuator).Any(static group => group.Count() > 1))
            throw new ArgumentException("A control definition cannot repeat a workload-budget actuator.", nameof(budgets));
        Budgets = [.. normalizedBudgets.OrderBy(static budget => budget.Actuator)];

        var pointActuators = InitialOperatingPoint.Values.Select(static value => value.Actuator).ToArray();
        var boundedActuators = HardLimits.Constraints
            .Select(static constraint => constraint.Range.Actuator)
            .Distinct()
            .Order()
            .ToArray();
        if (!pointActuators.SequenceEqual(boundedActuators))
        {
            throw new ArgumentException(
                "The initial operating point must contain exactly every hard-bounded actuator.",
                nameof(initialOperatingPoint));
        }
        if (!pointActuators.Contains(Policy.Actuator))
            throw new ArgumentException("The AIMD policy actuator must be present in the operating point.", nameof(policy));
        if (Budgets.Any(budget => !pointActuators.Contains(budget.Actuator)))
            throw new ArgumentException("A workload budget must address a bounded operating actuator.", nameof(budgets));

        var validation = ValidateOperatingPoint(InitialOperatingPoint);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join(" ", validation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                nameof(initialOperatingPoint));
        }

        Fingerprint = ControlLoopDefinitionFingerprinter.Compute(this);
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Stable loop identity.</summary>
    public ControlLoopId Id { get; }

    /// <summary>Stable Process, materialization, or runtime subject identity.</summary>
    public string Target { get; }

    /// <summary>Exact Process or materialization interpreter authorized to attest safe points.</summary>
    public string ApplicationAuthority { get; }

    /// <summary>Independently regulated pipeline stage.</summary>
    public ControlStageKind Stage { get; }

    /// <summary>Non-overridable hard constraints and their evidence.</summary>
    public ControlHardLimits HardLimits { get; }

    /// <summary>Initial effective multidimensional operating point.</summary>
    public ControlOperatingPoint InitialOperatingPoint { get; }

    /// <summary>Soft pressure objectives in deterministic measurement order.</summary>
    public ImmutableArray<ControlObjective> Objectives { get; }

    /// <summary>Resolved attributable AIMD policy.</summary>
    public AimdControlPolicy Policy { get; }

    /// <summary>Surplus-capacity reservations in deterministic actuator order.</summary>
    public ImmutableArray<ControlWorkloadBudget> Budgets { get; }

    /// <summary>Producer and source attribution for the canonical definition.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Derived exact canonical-content fence for durable controller state and recommendations.</summary>
    [JsonIgnore]
    public ExecutionDefinitionFingerprint Fingerprint { get; }

    /// <summary>Gets the effective hard range after intersecting capability limits and workload reservation.</summary>
    /// <param name="actuator">Actuator whose available range is requested.</param>
    /// <returns>The effective inclusive range available to the controlled workload.</returns>
    /// <exception cref="ArgumentException">The actuator has no hard limit or reservation leaves no valid range.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="actuator"/> is unsupported.</exception>
    public ControlRange GetEffectiveRange(ControlActuatorKind actuator)
    {
        var hard = HardLimits.GetEffectiveRange(actuator);
        ControlWorkloadBudget? budget = null;
        foreach (var candidate in Budgets)
        {
            if (candidate.Actuator == actuator)
            {
                budget = candidate;
                break;
            }
        }

        if (budget is null)
            return hard;
        var maximum = Math.Min(hard.Maximum.Value, budget.Available.Value);
        if (maximum < hard.Minimum.Value)
        {
            throw new ArgumentException(
                $"Workload reservation for actuator '{actuator}' leaves no value inside its hard range.",
                nameof(actuator));
        }

        return new(actuator, hard.Minimum, new(maximum, hard.Maximum.Unit));
    }

    /// <summary>Validates a complete operating point against definition shape, hard limits, and reservations.</summary>
    /// <param name="operatingPoint">Candidate point.</param>
    /// <returns>Structured diagnostics; an empty result authorizes the point.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operatingPoint"/> is <see langword="null"/>.</exception>
    public DocumentValidationResult ValidateOperatingPoint(ControlOperatingPoint operatingPoint)
    {
        ArgumentNullException.ThrowIfNull(operatingPoint);
        var expected = InitialOperatingPoint.Values.Select(static value => value.Actuator).ToArray();
        var observed = operatingPoint.Values.Select(static value => value.Actuator).ToArray();
        if (!expected.SequenceEqual(observed))
        {
            return Error(
                ControlDiagnosticCodes.OperatingPointShapeMismatch,
                "The operating point does not contain exactly the definition's bounded actuators.",
                "/operatingPoint",
                expected: string.Join(",", expected),
                observed: string.Join(",", observed));
        }

        List<DocumentValidationDiagnostic> diagnostics = [];
        foreach (var value in operatingPoint.Values)
        {
            var hard = HardLimits.GetEffectiveRange(value.Actuator);
            if (!hard.Contains(value))
            {
                diagnostics.Add(new(
                    ControlDiagnosticCodes.HardLimitExceeded,
                    DiagnosticSeverity.Error,
                    $"Actuator '{value.Actuator}' value {value.Quantity.Value} is outside hard range [{hard.Minimum.Value}, {hard.Maximum.Value}].",
                    $"/operatingPoint/{value.Actuator}",
                    Evidence: new(
                        stage: "control-validation",
                        subject: value.Actuator.ToString(),
                        sourceReferences: [.. HardLimits.Constraints
                            .Where(constraint => constraint.Range.Actuator == value.Actuator)
                            .Select(static constraint => constraint.Authority)],
                        expected: $"[{hard.Minimum.Value},{hard.Maximum.Value}] {hard.Minimum.Unit}",
                        observed: $"{value.Quantity.Value} {value.Quantity.Unit}")));
                continue;
            }

            foreach (var budget in Budgets)
            {
                if (budget.Actuator != value.Actuator || value.Quantity.Value <= budget.Available.Value)
                    continue;
                diagnostics.Add(new(
                    ControlDiagnosticCodes.WorkloadBudgetExceeded,
                    DiagnosticSeverity.Error,
                    $"Actuator '{value.Actuator}' value {value.Quantity.Value} exceeds unreserved capacity {budget.Available.Value}.",
                    $"/operatingPoint/{value.Actuator}",
                    Evidence: new(
                        stage: "control-validation",
                        subject: value.Actuator.ToString(),
                        sourceReferences: [budget.Authority],
                        expected: $"<= {budget.Available.Value} {budget.Available.Unit}",
                        observed: $"{value.Quantity.Value} {value.Quantity.Unit}")));
            }
        }

        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    /// <summary>Compares canonical definitions structurally.</summary>
    /// <param name="other">Definition to compare.</param>
    /// <returns><see langword="true"/> when all normalized semantic and policy content is equal.</returns>
    public bool Equals(ControlLoopDefinition? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Id == other.Id
        && string.Equals(Target, other.Target, StringComparison.Ordinal)
        && string.Equals(ApplicationAuthority, other.ApplicationAuthority, StringComparison.Ordinal)
        && Stage == other.Stage
        && HardLimits == other.HardLimits
        && InitialOperatingPoint == other.InitialOperatingPoint
        && Objectives.SequenceEqual(other.Objectives)
        && Policy == other.Policy
        && Budgets.SequenceEqual(other.Budgets)
        && Provenance == other.Provenance;

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from all normalized semantic and policy content.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Id);
        hash.Add(Target, StringComparer.Ordinal);
        hash.Add(ApplicationAuthority, StringComparer.Ordinal);
        hash.Add(Stage);
        hash.Add(HardLimits);
        hash.Add(InitialOperatingPoint);
        foreach (var objective in Objectives)
            hash.Add(objective);
        hash.Add(Policy);
        foreach (var budget in Budgets)
            hash.Add(budget);
        hash.Add(Provenance);
        return hash.ToHashCode();
    }

    static DocumentValidationResult Error(
        string code,
        string message,
        string location,
        string expected,
        string observed) =>
        DocumentValidationResult.FromDiagnostics([
            new(
                code,
                DiagnosticSeverity.Error,
                message,
                location,
                Evidence: new(
                    stage: "control-validation",
                    expected: expected,
                    observed: observed))
        ]);
}

/// <summary>Explicit, typed, revision-fenced measurement window supplied to one control loop.</summary>
public sealed record ControlObservation
{
    /// <summary>Creates a canonical control observation.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="id">Stable idempotency identity within the exact loop, epoch, and expected revision.</param>
    /// <param name="loopId">Addressed control loop.</param>
    /// <param name="definitionFingerprint">Fingerprint of the exact canonical definition content under which the evidence was measured.</param>
    /// <param name="target">Addressed Process, materialization, or runtime subject.</param>
    /// <param name="epoch">Addressed attempt, generation, or other control epoch.</param>
    /// <param name="expectedRevision">Exact controller revision observed by the producer.</param>
    /// <param name="windowStartedAtUtc">Inclusive UTC start of the measurement window.</param>
    /// <param name="windowEndedAtUtc">Inclusive UTC end of the measurement window.</param>
    /// <param name="observedAtUtc">UTC time at which the explicit observation was produced.</param>
    /// <param name="source">Stable adapter or sampler identity and version.</param>
    /// <param name="measurements">One or more typed measurement outcomes.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default, a time is not UTC or chronological, or measurements contain null or duplicate entries.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definitionFingerprint"/>, <paramref name="target"/>, or <paramref name="source"/> is
    /// <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ControlObservation(
        ExecutionIrSchemaVersion schemaVersion,
        ControlObservationId id,
        ControlLoopId loopId,
        ExecutionDefinitionFingerprint definitionFingerprint,
        string target,
        ControlEpochId epoch,
        ControlRevision expectedRevision,
        DateTimeOffset windowStartedAtUtc,
        DateTimeOffset windowEndedAtUtc,
        DateTimeOffset observedAtUtc,
        string source,
        ImmutableArray<ControlMeasurement> measurements)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("A control observation requires a schema version.", nameof(schemaVersion));
        if (string.IsNullOrWhiteSpace(id.Value) || string.IsNullOrWhiteSpace(loopId.Value) || string.IsNullOrWhiteSpace(epoch.Value))
            throw new ArgumentException("A control observation requires non-default identities.", nameof(id));
        ControlRevision.RequireDefined(expectedRevision, nameof(expectedRevision));
        RequireUtc(windowStartedAtUtc, nameof(windowStartedAtUtc));
        RequireUtc(windowEndedAtUtc, nameof(windowEndedAtUtc));
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (windowStartedAtUtc > windowEndedAtUtc || windowEndedAtUtc > observedAtUtc)
            throw new ArgumentException("A control observation window and emission time must be chronological.", nameof(windowStartedAtUtc));
        if (measurements.IsDefaultOrEmpty || measurements.Any(static measurement => measurement is null))
            throw new ArgumentException("A control observation requires one or more non-null measurements.", nameof(measurements));
        if (measurements.GroupBy(static measurement => (measurement.Metric, measurement.Statistic)).Any(static group => group.Count() > 1))
            throw new ArgumentException("A control observation cannot repeat a metric/statistic measurement.", nameof(measurements));

        SchemaVersion = schemaVersion;
        Id = id;
        LoopId = loopId;
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        Target = Guard.RequireNotNullOrWhiteSpace(target);
        Epoch = epoch;
        ExpectedRevision = expectedRevision;
        WindowStartedAtUtc = windowStartedAtUtc;
        WindowEndedAtUtc = windowEndedAtUtc;
        ObservedAtUtc = observedAtUtc;
        Source = Guard.RequireNotNullOrWhiteSpace(source);
        Measurements = [.. measurements
            .OrderBy(static measurement => measurement.Metric)
            .ThenBy(static measurement => measurement.Statistic)];
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Stable idempotency identity within the exact loop, epoch, and expected revision.</summary>
    public ControlObservationId Id { get; }

    /// <summary>Addressed control loop.</summary>
    public ControlLoopId LoopId { get; }

    /// <summary>Fingerprint of the exact canonical definition content under which this evidence was measured.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Addressed Process, materialization, or runtime subject.</summary>
    public string Target { get; }

    /// <summary>Addressed attempt, generation, or other control epoch.</summary>
    public ControlEpochId Epoch { get; }

    /// <summary>Exact controller revision observed by the producer.</summary>
    public ControlRevision ExpectedRevision { get; }

    /// <summary>Inclusive UTC start of the measurement window.</summary>
    public DateTimeOffset WindowStartedAtUtc { get; }

    /// <summary>Inclusive UTC end of the measurement window.</summary>
    public DateTimeOffset WindowEndedAtUtc { get; }

    /// <summary>UTC time at which the explicit observation was produced.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Stable adapter or sampler identity and version.</summary>
    public string Source { get; }

    /// <summary>Typed measurement outcomes in deterministic metric/statistic order.</summary>
    public ImmutableArray<ControlMeasurement> Measurements { get; }

    /// <summary>Finds an exact metric/statistic measurement.</summary>
    /// <param name="metric">Required metric.</param>
    /// <param name="statistic">Required statistic.</param>
    /// <returns>The exact measurement, or <see langword="null"/> when absent.</returns>
    public ControlMeasurement? Find(ControlMetricKind metric, ControlStatisticKind statistic)
    {
        foreach (var measurement in Measurements)
        {
            if (measurement.Metric == metric && measurement.Statistic == statistic)
                return measurement;
        }

        return null;
    }

    /// <summary>Compares normalized observations structurally.</summary>
    /// <param name="other">Observation to compare.</param>
    /// <returns><see langword="true"/> when all identity, fence, time, source, and measurement evidence is equal.</returns>
    public bool Equals(ControlObservation? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Id == other.Id
        && LoopId == other.LoopId
        && DefinitionFingerprint == other.DefinitionFingerprint
        && string.Equals(Target, other.Target, StringComparison.Ordinal)
        && Epoch == other.Epoch
        && ExpectedRevision == other.ExpectedRevision
        && WindowStartedAtUtc == other.WindowStartedAtUtc
        && WindowEndedAtUtc == other.WindowEndedAtUtc
        && ObservedAtUtc == other.ObservedAtUtc
        && string.Equals(Source, other.Source, StringComparison.Ordinal)
        && Measurements.SequenceEqual(other.Measurements);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from all normalized observation evidence.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Id);
        hash.Add(LoopId);
        hash.Add(DefinitionFingerprint);
        hash.Add(Target, StringComparer.Ordinal);
        hash.Add(Epoch);
        hash.Add(ExpectedRevision);
        hash.Add(WindowStartedAtUtc);
        hash.Add(WindowEndedAtUtc);
        hash.Add(ObservedAtUtc);
        hash.Add(Source, StringComparer.Ordinal);
        foreach (var measurement in Measurements)
            hash.Add(measurement);
        return hash.ToHashCode();
    }

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Control observations and decisions must be expressed in UTC.", parameterName);
    }
}
