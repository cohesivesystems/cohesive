using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

/// <summary>Pressure classification derived from a complete valid objective set.</summary>
public enum ControlPressureClassification
{
    /// <summary>Every objective is on the healthy side of its recovery boundary.</summary>
    Healthy = 0,

    /// <summary>No objective is congested and at least one lies inside its hysteresis band.</summary>
    Hysteresis = 1,

    /// <summary>At least one objective is on the congested side of its congestion boundary.</summary>
    Congested = 2
}

/// <summary>Direction of one pending operating-point recommendation.</summary>
public enum ControlRecommendationDirection
{
    /// <summary>Additive increase after sustained healthy evidence.</summary>
    Increase = 0,

    /// <summary>Multiplicative decrease after congestion evidence.</summary>
    Decrease = 1
}

/// <summary>Outcome of one pure controller evaluation.</summary>
public enum ControlDecisionDisposition
{
    /// <summary>A valid observation was accepted without creating an operating-point recommendation.</summary>
    Held = 0,

    /// <summary>A valid observation created a pending recommendation.</summary>
    Recommended = 1,

    /// <summary>An exact observation replay returned the already materialized outcome without mutation.</summary>
    Replayed = 2,

    /// <summary>The observation was invalid, stale, conflicting, or could not be admitted.</summary>
    Rejected = 3
}

/// <summary>Outcome of applying a pending recommendation at a fenced safe point.</summary>
public enum ControlActuationDisposition
{
    /// <summary>The complete recommended operating point was applied atomically.</summary>
    Applied = 0,

    /// <summary>An exact application-point replay returned the prior actuation without mutation.</summary>
    Replayed = 1,

    /// <summary>No recommendation was available or the supplied cut was not yet usable.</summary>
    Deferred = 2,

    /// <summary>The application evidence or recommendation was stale, conflicting, or invalid.</summary>
    Rejected = 3
}

/// <summary>Non-authoritative proposal to change one actuator within a complete operating point.</summary>
public sealed record ControlRecommendation
{
    /// <summary>Creates a fenced operating-point recommendation.</summary>
    /// <param name="id">Stable recommendation identity.</param>
    /// <param name="loopId">Addressed control loop.</param>
    /// <param name="definitionFingerprint">Fingerprint of the exact canonical definition content under which the recommendation was derived.</param>
    /// <param name="target">Addressed runtime subject.</param>
    /// <param name="epoch">Addressed attempt, generation, or other epoch.</param>
    /// <param name="expectedRevision">Exact state revision on which application is permitted.</param>
    /// <param name="observationId">Observation that caused the recommendation.</param>
    /// <param name="actuator">Only actuator permitted to differ.</param>
    /// <param name="direction">Increase or decrease classification.</param>
    /// <param name="authorizingHealthyObservationCount">
    /// Consecutive healthy-evidence count authorizing an increase, or zero for a decrease.
    /// </param>
    /// <param name="priorOperatingPoint">Effective point before application.</param>
    /// <param name="proposedOperatingPoint">Complete proposed point.</param>
    /// <param name="issuedAtUtc">Explicit UTC recommendation time.</param>
    /// <param name="priorActuationId">
    /// Identity of the exact latest preceding actuation receipt. <see langword="null"/> asserts that no actuation
    /// precedes this recommendation, in which case the prior point is the definition's initial operating point.
    /// </param>
    /// <param name="priorActuationRevision">
    /// Post-actuation state revision of <paramref name="priorActuationId"/>, or <see langword="null"/> when no
    /// actuation precedes the recommendation.
    /// </param>
    /// <exception cref="ArgumentException">
    /// An identity or revision is default; a time is not UTC; point shapes differ; more than one actuator changes;
    /// direction and authorization-count evidence conflict; or prior-actuation identity and revision evidence are
    /// not paired.
    /// </exception>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="actuator"/> or <paramref name="direction"/> is unsupported, or
    /// <paramref name="authorizingHealthyObservationCount"/> is outside the portable non-negative range.
    /// </exception>
    [JsonConstructor]
    public ControlRecommendation(
        ControlRecommendationId id,
        ControlLoopId loopId,
        ExecutionDefinitionFingerprint definitionFingerprint,
        string target,
        ControlEpochId epoch,
        ControlRevision expectedRevision,
        ControlObservationId observationId,
        ControlActuatorKind actuator,
        ControlRecommendationDirection direction,
        long authorizingHealthyObservationCount,
        ControlOperatingPoint priorOperatingPoint,
        ControlOperatingPoint proposedOperatingPoint,
        DateTimeOffset issuedAtUtc,
        ControlActuationId? priorActuationId = null,
        ControlRevision? priorActuationRevision = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value)
            || string.IsNullOrWhiteSpace(loopId.Value)
            || string.IsNullOrWhiteSpace(epoch.Value)
            || string.IsNullOrWhiteSpace(observationId.Value))
        {
            throw new ArgumentException("A control recommendation requires non-default identities.", nameof(id));
        }
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported recommendation direction.");
        if (authorizingHealthyObservationCount is < 0 or > ControlQuantity.MaximumPortableValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authorizingHealthyObservationCount),
                authorizingHealthyObservationCount,
                "An authorizing healthy-observation count must be non-negative and portable.");
        }
        if (direction == ControlRecommendationDirection.Increase != (authorizingHealthyObservationCount > 0))
        {
            throw new ArgumentException(
                "Only an increase carries a positive authorizing healthy-observation count.",
                nameof(authorizingHealthyObservationCount));
        }
        _ = ControlUnitCatalog.ForActuator(actuator);
        ControlRevision.RequireDefined(expectedRevision, nameof(expectedRevision));
        if ((priorActuationId is null) != (priorActuationRevision is null))
        {
            throw new ArgumentException(
                "Prior-actuation identity and revision evidence must be supplied together.",
                nameof(priorActuationId));
        }
        if (priorActuationId is { } retainedActuationId)
        {
            if (string.IsNullOrWhiteSpace(retainedActuationId.Value))
                throw new ArgumentException("Prior-actuation identity must be defined.", nameof(priorActuationId));
            ControlRevision.RequireDefined(priorActuationRevision!.Value, nameof(priorActuationRevision));
        }
        ControlObservation.RequireUtc(issuedAtUtc, nameof(issuedAtUtc));

        PriorOperatingPoint = Guard.RequireNotNull(priorOperatingPoint);
        ProposedOperatingPoint = Guard.RequireNotNull(proposedOperatingPoint);
        if (PriorOperatingPoint.Values.Length != ProposedOperatingPoint.Values.Length)
            throw new ArgumentException("Recommendation operating-point shapes must match.", nameof(proposedOperatingPoint));

        var changes = 0;
        for (var index = 0; index < PriorOperatingPoint.Values.Length; index++)
        {
            var prior = PriorOperatingPoint.Values[index];
            var proposed = ProposedOperatingPoint.Values[index];
            if (prior.Actuator != proposed.Actuator)
                throw new ArgumentException("Recommendation operating-point shapes must match.", nameof(proposedOperatingPoint));
            if (prior == proposed)
                continue;
            if (prior.Actuator != actuator)
                throw new ArgumentException("A recommendation may change only its declared actuator.", nameof(proposedOperatingPoint));
            changes++;

            var increases = proposed.Quantity.Value > prior.Quantity.Value;
            if (direction == ControlRecommendationDirection.Increase != increases)
                throw new ArgumentException("Recommendation direction conflicts with the proposed value.", nameof(direction));
        }
        if (changes != 1)
            throw new ArgumentException("A recommendation must change exactly one actuator.", nameof(proposedOperatingPoint));

        Id = id;
        LoopId = loopId;
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        Target = Guard.RequireNotNullOrWhiteSpace(target);
        Epoch = epoch;
        ExpectedRevision = expectedRevision;
        ObservationId = observationId;
        PriorActuationId = priorActuationId;
        PriorActuationRevision = priorActuationRevision;
        Actuator = actuator;
        Direction = direction;
        AuthorizingHealthyObservationCount = authorizingHealthyObservationCount;
        IssuedAtUtc = issuedAtUtc;
    }

    /// <summary>Stable recommendation identity.</summary>
    public ControlRecommendationId Id { get; }

    /// <summary>Addressed control loop.</summary>
    public ControlLoopId LoopId { get; }

    /// <summary>Fingerprint of the exact canonical definition content under which this recommendation was derived.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Addressed runtime subject.</summary>
    public string Target { get; }

    /// <summary>Addressed attempt, generation, or other epoch.</summary>
    public ControlEpochId Epoch { get; }

    /// <summary>Exact state revision on which application is permitted.</summary>
    public ControlRevision ExpectedRevision { get; }

    /// <summary>Observation that caused the recommendation.</summary>
    public ControlObservationId ObservationId { get; }

    /// <summary>
    /// Identity of the exact latest preceding actuation receipt. <see langword="null"/> asserts that no actuation
    /// precedes this recommendation, in which case the prior point is the definition's initial operating point.
    /// </summary>
    public ControlActuationId? PriorActuationId { get; }

    /// <summary>
    /// Post-actuation state revision of <see cref="PriorActuationId"/>, or <see langword="null"/> when no actuation
    /// precedes this recommendation.
    /// </summary>
    public ControlRevision? PriorActuationRevision { get; }

    /// <summary>Only actuator permitted to differ.</summary>
    public ControlActuatorKind Actuator { get; }

    /// <summary>Increase or decrease classification.</summary>
    public ControlRecommendationDirection Direction { get; }

    /// <summary>Consecutive healthy-evidence count authorizing an increase, or zero for a decrease.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long AuthorizingHealthyObservationCount { get; }

    /// <summary>Effective point before application.</summary>
    public ControlOperatingPoint PriorOperatingPoint { get; }

    /// <summary>Complete proposed point.</summary>
    public ControlOperatingPoint ProposedOperatingPoint { get; }

    /// <summary>Explicit UTC recommendation time.</summary>
    public DateTimeOffset IssuedAtUtc { get; }
}

/// <summary>Generic invariant-preserving Process, materialization, or runtime application point.</summary>
public sealed record ControlApplicationPoint
{
    /// <summary>Creates a fenced application point.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="id">Stable safe-point identity.</param>
    /// <param name="loopId">Addressed control loop.</param>
    /// <param name="definitionFingerprint">Fingerprint of the exact canonical definition content under which the cut was observed.</param>
    /// <param name="target">Addressed runtime subject.</param>
    /// <param name="epoch">Addressed attempt, generation, or other epoch.</param>
    /// <param name="expectedRevision">Exact controller revision observed at the cut.</param>
    /// <param name="fence">Monotonic safe-point fence supplied by the runtime authority.</param>
    /// <param name="kind">Invariant-preserving cut kind attested by the runtime.</param>
    /// <param name="observedAtUtc">Explicit UTC safe-point time.</param>
    /// <param name="authority">Stable identity and version of the Process or materialization interpreter.</param>
    /// <param name="sourceReference">Stable reference to the interpreter's exact safe-point evidence.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default, a string value is empty or white space, or <paramref name="observedAtUtc"/> is not UTC.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definitionFingerprint"/>, <paramref name="target"/>, <paramref name="authority"/>, or
    /// <paramref name="sourceReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public ControlApplicationPoint(
        ExecutionIrSchemaVersion schemaVersion,
        ControlApplicationPointId id,
        ControlLoopId loopId,
        ExecutionDefinitionFingerprint definitionFingerprint,
        string target,
        ControlEpochId epoch,
        ControlRevision expectedRevision,
        ControlApplicationFence fence,
        ControlApplicationPointKind kind,
        DateTimeOffset observedAtUtc,
        string authority,
        string sourceReference)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value)
            || string.IsNullOrWhiteSpace(id.Value)
            || string.IsNullOrWhiteSpace(loopId.Value)
            || string.IsNullOrWhiteSpace(epoch.Value))
        {
            throw new ArgumentException("A control application point requires non-default identities and schema.", nameof(id));
        }
        ControlRevision.RequireDefined(expectedRevision, nameof(expectedRevision));
        ControlApplicationFence.RequireDefined(fence, nameof(fence));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported control application-point kind.");
        ControlObservation.RequireUtc(observedAtUtc, nameof(observedAtUtc));

        SchemaVersion = schemaVersion;
        Id = id;
        LoopId = loopId;
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        Target = Guard.RequireNotNullOrWhiteSpace(target);
        Epoch = epoch;
        ExpectedRevision = expectedRevision;
        Fence = fence;
        Kind = kind;
        ObservedAtUtc = observedAtUtc;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        SourceReference = Guard.RequireNotNullOrWhiteSpace(sourceReference);
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Stable safe-point identity.</summary>
    public ControlApplicationPointId Id { get; }

    /// <summary>Addressed control loop.</summary>
    public ControlLoopId LoopId { get; }

    /// <summary>Fingerprint of the exact canonical definition content under which this cut was observed.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Addressed runtime subject.</summary>
    public string Target { get; }

    /// <summary>Addressed attempt, generation, or other epoch.</summary>
    public ControlEpochId Epoch { get; }

    /// <summary>Exact controller revision observed at the cut.</summary>
    public ControlRevision ExpectedRevision { get; }

    /// <summary>Monotonic safe-point fence supplied by the runtime authority.</summary>
    public ControlApplicationFence Fence { get; }

    /// <summary>Invariant-preserving cut kind attested by the runtime.</summary>
    public ControlApplicationPointKind Kind { get; }

    /// <summary>Explicit UTC safe-point time.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Stable identity and version of the Process or materialization interpreter.</summary>
    public string Authority { get; }

    /// <summary>Stable reference to the interpreter's exact safe-point evidence.</summary>
    public string SourceReference { get; }
}

/// <summary>Durable evidence that one complete recommendation was applied at an exact safe point.</summary>
public sealed record ControlActuation
{
    /// <summary>Creates an applied actuation receipt.</summary>
    /// <param name="id">Stable actuation identity.</param>
    /// <param name="recommendation">Exact recommendation applied.</param>
    /// <param name="observation">Exact typed observation that derived the recommendation.</param>
    /// <param name="applicationPoint">Exact safe point authorizing the application.</param>
    /// <param name="priorRevision">State revision before application.</param>
    /// <param name="revision">State revision after application.</param>
    /// <param name="appliedAtUtc">Explicit UTC application time.</param>
    /// <exception cref="ArgumentException">Identity, fence, revision, point, or chronology invariants conflict.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="recommendation"/>, <paramref name="observation"/>, or <paramref name="applicationPoint"/> is
    /// <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ControlActuation(
        ControlActuationId id,
        ControlRecommendation recommendation,
        ControlObservation observation,
        ControlApplicationPoint applicationPoint,
        ControlRevision priorRevision,
        ControlRevision revision,
        DateTimeOffset appliedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A control actuation requires a stable identity.", nameof(id));
        Recommendation = Guard.RequireNotNull(recommendation);
        Observation = Guard.RequireNotNull(observation);
        ApplicationPoint = Guard.RequireNotNull(applicationPoint);
        ControlRevision.RequireDefined(priorRevision, nameof(priorRevision));
        ControlRevision.RequireDefined(revision, nameof(revision));
        ControlObservation.RequireUtc(appliedAtUtc, nameof(appliedAtUtc));
        if (recommendation.LoopId != observation.LoopId
            || recommendation.LoopId != applicationPoint.LoopId
            || recommendation.DefinitionFingerprint != observation.DefinitionFingerprint
            || recommendation.DefinitionFingerprint != applicationPoint.DefinitionFingerprint
            || recommendation.Epoch != observation.Epoch
            || recommendation.Epoch != applicationPoint.Epoch
            || !string.Equals(recommendation.Target, observation.Target, StringComparison.Ordinal)
            || !string.Equals(recommendation.Target, applicationPoint.Target, StringComparison.Ordinal)
            || recommendation.ObservationId != observation.Id
            || observation.ExpectedRevision.Ordinal != recommendation.ExpectedRevision.Ordinal - 1
            || recommendation.ExpectedRevision != applicationPoint.ExpectedRevision
            || priorRevision != recommendation.ExpectedRevision
            || revision.Ordinal != priorRevision.Ordinal + 1)
        {
            throw new ArgumentException("Actuation recommendation, application point, and revisions must share one exact fence.", nameof(applicationPoint));
        }
        if (applicationPoint.ObservedAtUtc < recommendation.IssuedAtUtc || appliedAtUtc < applicationPoint.ObservedAtUtc)
            throw new ArgumentException("An actuation must follow its recommendation and safe point.", nameof(appliedAtUtc));

        Id = id;
        PriorRevision = priorRevision;
        Revision = revision;
        AppliedAtUtc = appliedAtUtc;
    }

    /// <summary>Stable actuation identity.</summary>
    public ControlActuationId Id { get; }

    /// <summary>Exact recommendation applied.</summary>
    public ControlRecommendation Recommendation { get; }

    /// <summary>Exact typed observation that derived the recommendation.</summary>
    public ControlObservation Observation { get; }

    /// <summary>Exact safe point authorizing application.</summary>
    public ControlApplicationPoint ApplicationPoint { get; }

    /// <summary>State revision before application.</summary>
    public ControlRevision PriorRevision { get; }

    /// <summary>State revision after application.</summary>
    public ControlRevision Revision { get; }

    /// <summary>Explicit UTC application time.</summary>
    public DateTimeOffset AppliedAtUtc { get; }
}

/// <summary>Complete durable state of one reference AIMD controller loop.</summary>
public sealed record AimdControlState
{
    /// <summary>Creates complete explicit controller state.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="loopId">Stable loop identity.</param>
    /// <param name="target">Stable controlled runtime subject.</param>
    /// <param name="epoch">Current attempt, generation, or other epoch.</param>
    /// <param name="revision">Current durable state revision.</param>
    /// <param name="definitionFingerprint">Fingerprint of the exact canonical loop-definition content owning the state.</param>
    /// <param name="operatingPoint">Currently effective operating point.</param>
    /// <param name="healthyObservationCount">Accepted consecutive recovery-side observations.</param>
    /// <param name="createdAtUtc">Explicit UTC state creation time.</param>
    /// <param name="updatedAtUtc">Explicit UTC last state-change time.</param>
    /// <param name="lastEvaluatedAtUtc">Last accepted evaluation time.</param>
    /// <param name="lastClassification">Last accepted pressure classification.</param>
    /// <param name="cooldownUntilUtc">Recovery cooldown boundary after a decrease.</param>
    /// <param name="lastObservation">Last accepted observation retained for exact replay.</param>
    /// <param name="pendingRecommendation">Non-authoritative recommendation awaiting a safe point.</param>
    /// <param name="lastActuation">Last applied actuation retained for exact replay.</param>
    /// <param name="lastApplicationFence">Last applied runtime safe-point fence.</param>
    /// <exception cref="ArgumentException">Identity, time, state, replay, recommendation, or actuation invariants conflict.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/>, <paramref name="definitionFingerprint"/>, or <paramref name="operatingPoint"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="healthyObservationCount"/> is outside the portable non-negative range, or
    /// <paramref name="lastClassification"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public AimdControlState(
        ExecutionIrSchemaVersion schemaVersion,
        ControlLoopId loopId,
        string target,
        ControlEpochId epoch,
        ControlRevision revision,
        ExecutionDefinitionFingerprint definitionFingerprint,
        ControlOperatingPoint operatingPoint,
        long healthyObservationCount,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? lastEvaluatedAtUtc = null,
        ControlPressureClassification? lastClassification = null,
        DateTimeOffset? cooldownUntilUtc = null,
        ControlObservation? lastObservation = null,
        ControlRecommendation? pendingRecommendation = null,
        ControlActuation? lastActuation = null,
        ControlApplicationFence? lastApplicationFence = null)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value)
            || string.IsNullOrWhiteSpace(loopId.Value)
            || string.IsNullOrWhiteSpace(epoch.Value))
        {
            throw new ArgumentException("Control state requires non-default schema, loop, and epoch identities.", nameof(loopId));
        }
        if (healthyObservationCount is < 0 or > ControlQuantity.MaximumPortableValue)
            throw new ArgumentOutOfRangeException(nameof(healthyObservationCount), healthyObservationCount, "A healthy-observation count must be non-negative and portable.");
        ControlRevision.RequireDefined(revision, nameof(revision));
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        if (lastClassification is { } classification && !Enum.IsDefined(classification))
            throw new ArgumentOutOfRangeException(nameof(lastClassification), lastClassification, "Unsupported pressure classification.");
        ControlObservation.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        ControlObservation.RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        if (lastEvaluatedAtUtc is { } evaluated)
            ControlObservation.RequireUtc(evaluated, nameof(lastEvaluatedAtUtc));
        if (cooldownUntilUtc is { } cooldown)
            ControlObservation.RequireUtc(cooldown, nameof(cooldownUntilUtc));
        if (updatedAtUtc < createdAtUtc
            || lastEvaluatedAtUtc < createdAtUtc
            || lastEvaluatedAtUtc > updatedAtUtc)
        {
            throw new ArgumentException("Control state timestamps must be chronological.", nameof(updatedAtUtc));
        }
        if ((lastEvaluatedAtUtc is null) != (lastObservation is null)
            || (lastClassification is null) != (lastObservation is null))
        {
            throw new ArgumentException("Accepted observation, evaluation time, and classification must be retained together.", nameof(lastObservation));
        }

        Target = Guard.RequireNotNullOrWhiteSpace(target);
        OperatingPoint = Guard.RequireNotNull(operatingPoint);
        if (lastObservation is not null
            && (lastObservation.LoopId != loopId
                || lastObservation.DefinitionFingerprint != DefinitionFingerprint
                || lastObservation.Epoch != epoch
                || !string.Equals(lastObservation.Target, Target, StringComparison.Ordinal)
                || lastObservation.ObservedAtUtc > lastEvaluatedAtUtc))
        {
            throw new ArgumentException("Last observation does not belong to this state or its evaluation chronology.", nameof(lastObservation));
        }
        if (pendingRecommendation is not null
            && (pendingRecommendation.LoopId != loopId
                || pendingRecommendation.Epoch != epoch
                || pendingRecommendation.DefinitionFingerprint != DefinitionFingerprint
                || !string.Equals(pendingRecommendation.Target, Target, StringComparison.Ordinal)
                || pendingRecommendation.ExpectedRevision != revision
                || pendingRecommendation.PriorOperatingPoint != OperatingPoint
                || lastObservation?.Id != pendingRecommendation.ObservationId))
        {
            throw new ArgumentException("Pending recommendation does not match current controller state.", nameof(pendingRecommendation));
        }
        if ((lastActuation is null) != (lastApplicationFence is null))
            throw new ArgumentException("Last actuation and application fence must be retained together.", nameof(lastActuation));
        if (lastActuation is not null
            && (lastActuation.Recommendation.LoopId != loopId
                || lastActuation.Recommendation.Epoch != epoch
                || lastActuation.Recommendation.DefinitionFingerprint != DefinitionFingerprint
                || lastActuation.Observation.DefinitionFingerprint != DefinitionFingerprint
                || !string.Equals(lastActuation.Recommendation.Target, Target, StringComparison.Ordinal)
                || lastActuation.Revision.Ordinal > revision.Ordinal
                || lastActuation.Recommendation.ProposedOperatingPoint != OperatingPoint
                || lastActuation.ApplicationPoint.Fence != lastApplicationFence))
        {
            throw new ArgumentException("Last actuation does not match current controller state.", nameof(lastActuation));
        }
        if (cooldownUntilUtc is not null
            && (lastActuation is null
                || lastActuation.Recommendation.Direction != ControlRecommendationDirection.Decrease
                || cooldownUntilUtc < lastActuation.AppliedAtUtc))
        {
            throw new ArgumentException("Recovery cooldown must follow a retained decrease actuation.", nameof(cooldownUntilUtc));
        }

        SchemaVersion = schemaVersion;
        LoopId = loopId;
        Epoch = epoch;
        Revision = revision;
        HealthyObservationCount = healthyObservationCount;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        LastEvaluatedAtUtc = lastEvaluatedAtUtc;
        LastClassification = lastClassification;
        CooldownUntilUtc = cooldownUntilUtc;
        LastObservation = lastObservation;
        PendingRecommendation = pendingRecommendation;
        LastActuation = lastActuation;
        LastApplicationFence = lastApplicationFence;
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Stable loop identity.</summary>
    public ControlLoopId LoopId { get; }

    /// <summary>Stable controlled runtime subject.</summary>
    public string Target { get; }

    /// <summary>Current attempt, generation, or other epoch.</summary>
    public ControlEpochId Epoch { get; }

    /// <summary>Current durable state revision.</summary>
    public ControlRevision Revision { get; }

    /// <summary>Fingerprint of the exact canonical loop-definition content owning this durable state.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Currently effective operating point.</summary>
    public ControlOperatingPoint OperatingPoint { get; }

    /// <summary>Accepted consecutive recovery-side observations.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long HealthyObservationCount { get; }

    /// <summary>Explicit UTC state creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Explicit UTC last state-change time.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Last accepted evaluation time.</summary>
    public DateTimeOffset? LastEvaluatedAtUtc { get; }

    /// <summary>Last accepted pressure classification.</summary>
    public ControlPressureClassification? LastClassification { get; }

    /// <summary>Recovery cooldown boundary after a decrease.</summary>
    public DateTimeOffset? CooldownUntilUtc { get; }

    /// <summary>Last accepted observation retained for exact replay.</summary>
    public ControlObservation? LastObservation { get; }

    /// <summary>Non-authoritative recommendation awaiting an exact safe point.</summary>
    public ControlRecommendation? PendingRecommendation { get; }

    /// <summary>Last applied actuation retained for exact replay.</summary>
    public ControlActuation? LastActuation { get; }

    /// <summary>Last applied runtime safe-point fence.</summary>
    public ControlApplicationFence? LastApplicationFence { get; }

    /// <summary>Creates initial state for a definition and new controlled epoch.</summary>
    /// <param name="definition">Canonical loop definition.</param>
    /// <param name="epoch">New Process attempt, materialization generation, or other epoch.</param>
    /// <param name="createdAtUtc">Explicit UTC state creation time.</param>
    /// <returns>Initial state at revision one and the definition's initial operating point.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is default or <paramref name="createdAtUtc"/> is not UTC.</exception>
    public static AimdControlState Create(
        ControlLoopDefinition definition,
        ControlEpochId epoch,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            definition.Id,
            definition.Target,
            epoch,
            ControlRevision.Initial,
            definition.Fingerprint,
            definition.InitialOperatingPoint,
            healthyObservationCount: 0,
            createdAtUtc,
            updatedAtUtc: createdAtUtc);
    }
}

/// <summary>Complete result of one pure reference-controller evaluation.</summary>
public sealed record ControlDecision
{
    /// <summary>Creates a controller decision.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="disposition">Evaluation outcome.</param>
    /// <param name="evaluatedAtUtc">Explicit UTC evaluation time.</param>
    /// <param name="state">Complete state after the decision.</param>
    /// <param name="recommendation">Pending recommendation for a recommended or replayed outcome.</param>
    /// <param name="diagnostics">Structured diagnostics in any producer order.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Schema, time, recommendation, state, or diagnostics conflict.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ControlDecision(
        ExecutionIrSchemaVersion schemaVersion,
        ControlDecisionDisposition disposition,
        DateTimeOffset evaluatedAtUtc,
        AimdControlState state,
        ControlRecommendation? recommendation = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("A control decision requires a schema version.", nameof(schemaVersion));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported control-decision disposition.");
        ControlObservation.RequireUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));
        State = Guard.RequireNotNull(state);
        if (disposition == ControlDecisionDisposition.Recommended
            && (recommendation is null || state.PendingRecommendation != recommendation))
        {
            throw new ArgumentException("A recommended decision must expose the state's pending recommendation.", nameof(recommendation));
        }
        if (disposition == ControlDecisionDisposition.Replayed
            && recommendation != state.PendingRecommendation)
        {
            throw new ArgumentException(
                "A replayed decision must expose exactly the state's retained pending recommendation, when any.",
                nameof(recommendation));
        }
        if (disposition is ControlDecisionDisposition.Held or ControlDecisionDisposition.Rejected && recommendation is not null)
            throw new ArgumentException("A held or rejected decision cannot create a recommendation.", nameof(recommendation));
        if (diagnostics.IsDefault)
            diagnostics = [];
        if (diagnostics.Any(static diagnostic => diagnostic is null || string.IsNullOrWhiteSpace(diagnostic.Code) || string.IsNullOrWhiteSpace(diagnostic.Message)))
            throw new ArgumentException("Control decision diagnostics must have non-empty code and message.", nameof(diagnostics));

        SchemaVersion = schemaVersion;
        Disposition = disposition;
        EvaluatedAtUtc = evaluatedAtUtc;
        Recommendation = recommendation;
        Diagnostics = [.. diagnostics.OrderBy(static diagnostic => diagnostic, DocumentValidationDiagnosticComparer.Ordinal)];
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Evaluation outcome.</summary>
    public ControlDecisionDisposition Disposition { get; }

    /// <summary>Explicit UTC evaluation time.</summary>
    public DateTimeOffset EvaluatedAtUtc { get; }

    /// <summary>Complete state after the decision.</summary>
    public AimdControlState State { get; }

    /// <summary>Pending recommendation for a recommended or replayed outcome.</summary>
    public ControlRecommendation? Recommendation { get; }

    /// <summary>Structured diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Compares decisions structurally.</summary>
    /// <param name="other">Decision to compare.</param>
    /// <returns><see langword="true"/> when state, outcome, recommendation, and diagnostics are equal.</returns>
    public bool Equals(ControlDecision? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Disposition == other.Disposition
        && EvaluatedAtUtc == other.EvaluatedAtUtc
        && State == other.State
        && Recommendation == other.Recommendation
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from the complete decision.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Disposition);
        hash.Add(EvaluatedAtUtc);
        hash.Add(State);
        hash.Add(Recommendation);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        return hash.ToHashCode();
    }
}

/// <summary>Complete result of attempting safe-point actuation.</summary>
public sealed record ControlActuationResult
{
    /// <summary>Creates a safe-point actuation result.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="disposition">Application outcome.</param>
    /// <param name="state">Complete state after the attempt.</param>
    /// <param name="actuation">Applied or replayed receipt.</param>
    /// <param name="diagnostics">Structured diagnostics in any producer order.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Schema, disposition, receipt, or diagnostics conflict.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ControlActuationResult(
        ExecutionIrSchemaVersion schemaVersion,
        ControlActuationDisposition disposition,
        AimdControlState state,
        ControlActuation? actuation = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("A control actuation result requires a schema version.", nameof(schemaVersion));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported actuation disposition.");
        State = Guard.RequireNotNull(state);
        if (disposition is ControlActuationDisposition.Applied or ControlActuationDisposition.Replayed && actuation is null)
            throw new ArgumentException("An applied or replayed result requires its actuation receipt.", nameof(actuation));
        if (disposition is ControlActuationDisposition.Applied or ControlActuationDisposition.Replayed
            && state.LastActuation != actuation)
        {
            throw new ArgumentException(
                "An applied or replayed result must expose exactly the state's retained last actuation.",
                nameof(actuation));
        }
        if (disposition == ControlActuationDisposition.Applied
            && (state.Revision != actuation!.Revision || state.UpdatedAtUtc != actuation.AppliedAtUtc))
        {
            throw new ArgumentException(
                "An applied result must expose the exact post-actuation state revision and timestamp.",
                nameof(state));
        }
        if (disposition is ControlActuationDisposition.Deferred or ControlActuationDisposition.Rejected && actuation is not null)
            throw new ArgumentException("A deferred or rejected result cannot contain an actuation receipt.", nameof(actuation));
        if (diagnostics.IsDefault)
            diagnostics = [];
        if (diagnostics.Any(static diagnostic => diagnostic is null || string.IsNullOrWhiteSpace(diagnostic.Code) || string.IsNullOrWhiteSpace(diagnostic.Message)))
            throw new ArgumentException("Actuation diagnostics must have non-empty code and message.", nameof(diagnostics));

        SchemaVersion = schemaVersion;
        Disposition = disposition;
        Actuation = actuation;
        Diagnostics = [.. diagnostics.OrderBy(static diagnostic => diagnostic, DocumentValidationDiagnosticComparer.Ordinal)];
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Application outcome.</summary>
    public ControlActuationDisposition Disposition { get; }

    /// <summary>Complete state after the attempt.</summary>
    public AimdControlState State { get; }

    /// <summary>Applied or replayed receipt.</summary>
    public ControlActuation? Actuation { get; }

    /// <summary>Structured diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Compares results structurally.</summary>
    /// <param name="other">Result to compare.</param>
    /// <returns><see langword="true"/> when disposition, state, receipt, and diagnostics are equal.</returns>
    public bool Equals(ControlActuationResult? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Disposition == other.Disposition
        && State == other.State
        && Actuation == other.Actuation
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from the complete result.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Disposition);
        hash.Add(State);
        hash.Add(Actuation);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        return hash.ToHashCode();
    }
}
