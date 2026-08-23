using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Whether runtime status evidence is available to an API caller.</summary>
public enum ExecutionStatusDisclosure
{
    /// <summary>The status producer has no authoritative observation for this facet.</summary>
    Unknown = 0,

    /// <summary>The status producer disclosed its authoritative observation.</summary>
    Disclosed = 1,

    /// <summary>The status producer has evidence but intentionally withheld it.</summary>
    Redacted = 2
}

/// <summary>A typed runtime status value with explicit disclosure semantics.</summary>
/// <remarks>
/// <see cref="ExecutionStatusDisclosure.Redacted"/> is intentionally separate from
/// <see cref="ExecutionStatusDisclosure.Unknown"/> and from <see cref="PortableValueState.Unknown"/>. Redaction
/// means that evidence exists but policy withheld it; unknown means that no authoritative observation is
/// available.
/// </remarks>
public sealed record ExecutionStatusValue
{
    /// <summary>Creates one typed runtime status value.</summary>
    /// <param name="contract">Portable semantic contract of the status value.</param>
    /// <param name="disclosure">Whether the value is disclosed, unknown, or redacted.</param>
    /// <param name="value">Typed portable value for disclosed or explicitly unknown status.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disclosure"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Disclosure and value presence disagree, the value contract differs from <paramref name="contract"/>, or
    /// unknown disclosure does not carry a <see cref="PortableValueState.Unknown"/> value.
    /// </exception>
    [JsonConstructor]
    public ExecutionStatusValue(
        ValueContract contract,
        ExecutionStatusDisclosure disclosure,
        PortableValue? value = null)
    {
        Contract = Guard.RequireNotNull(contract);
        if (!Enum.IsDefined(disclosure))
            throw new ArgumentOutOfRangeException(nameof(disclosure), disclosure, "Unsupported status disclosure.");

        if (disclosure == ExecutionStatusDisclosure.Redacted)
        {
            if (value is not null)
            {
                throw new ArgumentException("A redacted status value cannot carry a payload.", nameof(value));
            }
        }
        else
        {
            if (value is null)
            {
                throw new ArgumentException("Disclosed and unknown status values require typed evidence.", nameof(value));
            }

            if (value.Contract != contract)
            {
                throw new ArgumentException("Status value and declared contract differ.", nameof(value));
            }

            if ((disclosure == ExecutionStatusDisclosure.Unknown)
                != (value.State == PortableValueState.Unknown))
            {
                throw new ArgumentException(
                    "Unknown disclosure must carry, and only it may carry, an unknown portable value.",
                    nameof(value));
            }
        }

        Disclosure = disclosure;
        Value = value;
    }

    /// <summary>Portable semantic contract retained even when the payload is redacted.</summary>
    public ValueContract Contract { get; }

    /// <summary>Whether authoritative evidence is disclosed, unavailable, or redacted.</summary>
    public ExecutionStatusDisclosure Disclosure { get; }

    /// <summary>Typed status evidence, or <see langword="null"/> only when redacted.</summary>
    public PortableValue? Value { get; }

    /// <summary>Creates a disclosed typed status value.</summary>
    /// <param name="value">Portable status value whose state is not unknown.</param>
    /// <returns>A disclosed status value retaining the exact portable contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is unknown.</exception>
    public static ExecutionStatusValue Disclose(PortableValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.State == PortableValueState.Unknown)
            throw new ArgumentException("Use Unknown for an unknown portable value.", nameof(value));
        return new(value.Contract, ExecutionStatusDisclosure.Disclosed, value);
    }

    /// <summary>Creates explicitly unknown typed status evidence.</summary>
    /// <param name="contract">Portable semantic contract of the unobserved value.</param>
    /// <returns>An unknown status value distinct from redaction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    public static ExecutionStatusValue Unknown(ValueContract contract) =>
        new(contract, ExecutionStatusDisclosure.Unknown, PortableValue.Unknown(contract));

    /// <summary>Creates explicitly redacted typed status evidence.</summary>
    /// <param name="contract">Portable semantic contract retained without its value.</param>
    /// <returns>A redacted status value with no payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    public static ExecutionStatusValue Redacted(ValueContract contract) =>
        new(contract, ExecutionStatusDisclosure.Redacted);
}

/// <summary>One versioned block-specific extension to common execution status.</summary>
/// <remarks>
/// An index-sync interpreter can publish its generation, source cursor, or backend-pool status here without
/// changing the common status schema. Extension identity and exact schema version retain the same portable
/// authority used by execution IR extensions.
/// </remarks>
public sealed record ExecutionRuntimeStatusExtension
{
    /// <summary>Creates one typed runtime status extension.</summary>
    /// <param name="id">Stable extension authority identity.</param>
    /// <param name="schemaVersion">Exact extension payload schema version.</param>
    /// <param name="value">Typed extension status with explicit disclosure.</param>
    /// <param name="provenance">Attributable producer and source evidence for the runtime observation.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="schemaVersion"/> is default.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ExecutionRuntimeStatusExtension(
        ExecutionExtensionId id,
        ExecutionExtensionSchemaVersion schemaVersion,
        ExecutionStatusValue value,
        ExecutionProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("Runtime status extension identity must be explicit.", nameof(id));
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("Runtime status extension schema version must be explicit.", nameof(schemaVersion));

        Id = id;
        SchemaVersion = schemaVersion;
        Value = Guard.RequireNotNull(value);
        Provenance = Guard.RequireNotNull(provenance);
    }

    /// <summary>Stable extension authority identity.</summary>
    public ExecutionExtensionId Id { get; }

    /// <summary>Exact extension payload schema version.</summary>
    public ExecutionExtensionSchemaVersion SchemaVersion { get; }

    /// <summary>Typed extension status with explicit disclosure.</summary>
    public ExecutionStatusValue Value { get; }

    /// <summary>Attributable producer and source evidence for this runtime observation.</summary>
    public ExecutionProvenance Provenance { get; }
}

/// <summary>Execution-facing disposition of one Process attempt.</summary>
/// <remarks>
/// This status vocabulary extends lifecycle-control lineage with authoritative Process completion and failure,
/// which are execution outcomes rather than control commands.
/// </remarks>
public enum ExecutionAttemptDisposition
{
    /// <summary>No disposition was supplied; invalid in execution status.</summary>
    Unspecified = 0,

    /// <summary>The final attempt remains current and nonterminal.</summary>
    Current = 1,

    /// <summary>The attempt was abandoned in favor of a replacement attempt.</summary>
    Abandoned = 2,

    /// <summary>The attempt completed its Process definition successfully.</summary>
    Completed = 3,

    /// <summary>The attempt ended in semantic Process failure.</summary>
    Failed = 4,

    /// <summary>The attempt ended through cooperative cancellation.</summary>
    Cancelled = 5,

    /// <summary>The attempt was forcibly and irreversibly terminated.</summary>
    Terminated = 6
}

/// <summary>Safe summary of one Process attempt without command, payload, or affinity evidence.</summary>
public sealed record ExecutionAttemptStatus
{
    /// <summary>Creates one attempt summary.</summary>
    /// <param name="attemptId">Stable Process-attempt identity.</param>
    /// <param name="startedAtUtc">Explicit UTC attempt start time.</param>
    /// <param name="endedAtUtc">Explicit UTC attempt end time for a closed attempt.</param>
    /// <param name="disposition">Current or terminal attempt disposition.</param>
    /// <param name="phase">Current control-relevant execution phase.</param>
    /// <param name="completedActivationCount">Number of activations completed at durable safe points.</param>
    /// <param name="lastSafePointId">Latest durable safe-point identity when one exists.</param>
    /// <param name="lastSafePointNode">Stable semantic node of the latest durable safe point.</param>
    /// <exception cref="ArgumentException">Identity, chronology, or safe-point summary is inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An enum value is unsupported or <paramref name="completedActivationCount"/> is negative.
    /// </exception>
    [JsonConstructor]
    public ExecutionAttemptStatus(
        ProcessAttemptId attemptId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? endedAtUtc,
        ExecutionAttemptDisposition disposition,
        ProcessControlExecutionPhase phase,
        long completedActivationCount,
        ProcessSafePointId? lastSafePointId = null,
        ExecutionNodeId? lastSafePointNode = null)
    {
        if (string.IsNullOrWhiteSpace(attemptId.Value))
            throw new ArgumentException("Attempt status requires a stable identity.", nameof(attemptId));
        ExecutionStatusValidation.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        if (endedAtUtc is { } ended)
        {
            ExecutionStatusValidation.RequireUtc(ended, nameof(endedAtUtc));
            if (ended < startedAtUtc)
                throw new ArgumentException("Attempt status cannot end before it starts.", nameof(endedAtUtc));
        }

        if (!Enum.IsDefined(disposition) || disposition == ExecutionAttemptDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Attempt disposition must be explicit.");
        if (!Enum.IsDefined(phase) || phase == ProcessControlExecutionPhase.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Attempt phase must be explicit.");
        if ((disposition == ExecutionAttemptDisposition.Current) != (endedAtUtc is null))
            throw new ArgumentException("Attempt disposition and end time contradict each other.", nameof(endedAtUtc));
        if ((disposition == ExecutionAttemptDisposition.Current) == (phase == ProcessControlExecutionPhase.Stopped))
            throw new ArgumentException("Only a closed attempt uses the stopped execution phase.", nameof(phase));
        if (completedActivationCount < 0)
            throw new ArgumentOutOfRangeException(nameof(completedActivationCount));
        if (lastSafePointId.HasValue != lastSafePointNode.HasValue
            || (completedActivationCount == 0) == lastSafePointId.HasValue)
        {
            throw new ArgumentException(
                "Latest safe-point identity and node must exist exactly when completed activations exist.",
                nameof(lastSafePointId));
        }

        AttemptId = attemptId;
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        Disposition = disposition;
        Phase = phase;
        CompletedActivationCount = completedActivationCount;
        LastSafePointId = lastSafePointId;
        LastSafePointNode = lastSafePointNode;
    }

    /// <summary>Stable Process-attempt identity.</summary>
    public ProcessAttemptId AttemptId { get; }

    /// <summary>Explicit UTC attempt start time.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Explicit UTC attempt end time, or <see langword="null"/> for the current attempt.</summary>
    public DateTimeOffset? EndedAtUtc { get; }

    /// <summary>Current or terminal attempt disposition.</summary>
    public ExecutionAttemptDisposition Disposition { get; }

    /// <summary>Current control-relevant execution phase.</summary>
    public ProcessControlExecutionPhase Phase { get; }

    /// <summary>Number of activations completed at durable safe points.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long CompletedActivationCount { get; }

    /// <summary>Latest durable safe-point identity.</summary>
    public ProcessSafePointId? LastSafePointId { get; }

    /// <summary>Stable semantic node of the latest durable safe point.</summary>
    public ExecutionNodeId? LastSafePointNode { get; }
}

/// <summary>Safe summary of the finite activation currently in flight.</summary>
public sealed record ExecutionActivationStatus
{
    /// <summary>Creates an active activation summary.</summary>
    /// <param name="activationId">Stable finite activation identity.</param>
    /// <param name="attemptId">Owning Process-attempt identity.</param>
    /// <param name="startedUnderRevision">Control revision observed before activation began.</param>
    /// <param name="startedAtUtc">Explicit UTC activation-start time.</param>
    /// <exception cref="ArgumentException">An identity is default or the timestamp is not UTC.</exception>
    [JsonConstructor]
    public ExecutionActivationStatus(
        ActivationId activationId,
        ProcessAttemptId attemptId,
        ProcessControlRevision startedUnderRevision,
        DateTimeOffset startedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(activationId.Value))
            throw new ArgumentException("Activation status requires a stable activation.", nameof(activationId));
        if (string.IsNullOrWhiteSpace(attemptId.Value))
            throw new ArgumentException("Activation status requires its owning attempt.", nameof(attemptId));
        if (string.IsNullOrWhiteSpace(startedUnderRevision.Value))
            throw new ArgumentException("Activation status requires its starting control revision.", nameof(startedUnderRevision));
        ExecutionStatusValidation.RequireUtc(startedAtUtc, nameof(startedAtUtc));

        ActivationId = activationId;
        AttemptId = attemptId;
        StartedUnderRevision = startedUnderRevision;
        StartedAtUtc = startedAtUtc;
    }

    /// <summary>Stable finite activation identity.</summary>
    public ActivationId ActivationId { get; }

    /// <summary>Owning Process-attempt identity.</summary>
    public ProcessAttemptId AttemptId { get; }

    /// <summary>Control revision observed before activation began.</summary>
    public ProcessControlRevision StartedUnderRevision { get; }

    /// <summary>Explicit UTC activation-start time.</summary>
    public DateTimeOffset StartedAtUtc { get; }
}

/// <summary>Observable lifecycle state of a durable Process token.</summary>
public enum ExecutionTokenDisposition
{
    /// <summary>No token disposition was supplied.</summary>
    Unspecified = 0,

    /// <summary>The token is ready to activate its semantic node.</summary>
    Ready = 1,

    /// <summary>The token is participating in the current activation.</summary>
    Active = 2,

    /// <summary>The token is durably waiting for an endogenous or external input.</summary>
    Waiting = 3,

    /// <summary>The token completed its semantic path.</summary>
    Completed = 4,

    /// <summary>The token ended because its semantic path failed.</summary>
    Failed = 5,

    /// <summary>The token ended through Process cancellation.</summary>
    Cancelled = 6,

    /// <summary>The token is durably retained but has not yet been admitted to execute its semantic node.</summary>
    Pending = 7
}

/// <summary>Safe identity and lifecycle status of one durable Process token.</summary>
public sealed record ExecutionTokenStatus
{
    /// <summary>Creates a durable token status.</summary>
    /// <param name="tokenId">Stable durable token identity.</param>
    /// <param name="node">Current stable semantic Process node.</param>
    /// <param name="disposition">Current observable token disposition.</param>
    /// <exception cref="ArgumentException"><paramref name="tokenId"/> or <paramref name="node"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    [JsonConstructor]
    public ExecutionTokenStatus(
        TokenId tokenId,
        ExecutionNodeId node,
        ExecutionTokenDisposition disposition)
    {
        if (string.IsNullOrWhiteSpace(tokenId.Value))
            throw new ArgumentException("Token status requires a stable token.", nameof(tokenId));
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("Token status requires a stable semantic node.", nameof(node));
        if (!Enum.IsDefined(disposition) || disposition == ExecutionTokenDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Token disposition must be explicit.");

        TokenId = tokenId;
        Node = node;
        Disposition = disposition;
    }

    /// <summary>Stable durable token identity.</summary>
    public TokenId TokenId { get; }

    /// <summary>Current stable semantic Process node.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Current observable token disposition.</summary>
    public ExecutionTokenDisposition Disposition { get; }
}

/// <summary>Safe status of one durable wait without its key or resume payload.</summary>
public sealed record ExecutionWaitStatus
{
    /// <summary>Creates a safe durable-wait status.</summary>
    /// <param name="tokenId">Stable token held by the wait.</param>
    /// <param name="node">Stable semantic wait node.</param>
    /// <param name="waitingSinceUtc">Explicit UTC time at which waiting began.</param>
    /// <param name="deadlineUtc">Optional explicit UTC wait deadline.</param>
    /// <exception cref="ArgumentException">Identity or chronology is invalid.</exception>
    [JsonConstructor]
    public ExecutionWaitStatus(
        TokenId tokenId,
        ExecutionNodeId node,
        DateTimeOffset waitingSinceUtc,
        DateTimeOffset? deadlineUtc = null)
    {
        if (string.IsNullOrWhiteSpace(tokenId.Value))
            throw new ArgumentException("Wait status requires a stable token.", nameof(tokenId));
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("Wait status requires a stable semantic node.", nameof(node));
        ExecutionStatusValidation.RequireUtc(waitingSinceUtc, nameof(waitingSinceUtc));
        if (deadlineUtc is { } deadline)
        {
            ExecutionStatusValidation.RequireUtc(deadline, nameof(deadlineUtc));
            if (deadline < waitingSinceUtc)
                throw new ArgumentException("A wait deadline cannot precede wait admission.", nameof(deadlineUtc));
        }

        TokenId = tokenId;
        Node = node;
        WaitingSinceUtc = waitingSinceUtc;
        DeadlineUtc = deadlineUtc;
    }

    /// <summary>Stable token held by the wait.</summary>
    public TokenId TokenId { get; }

    /// <summary>Stable semantic wait node.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Explicit UTC time at which waiting began.</summary>
    public DateTimeOffset WaitingSinceUtc { get; }

    /// <summary>Optional explicit UTC wait deadline.</summary>
    public DateTimeOffset? DeadlineUtc { get; }
}

/// <summary>Unit-bearing progress through a finite or open-ended body of work.</summary>
public sealed record ExecutionProgressStatus
{
    /// <summary>Creates a progress observation.</summary>
    /// <param name="completed">Non-negative completed work count.</param>
    /// <param name="total">Optional non-negative total work count.</param>
    /// <param name="unit">Stable semantic work unit.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative or completed work exceeds total work.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="unit"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="unit"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ExecutionProgressStatus(long completed, long? total, string unit)
    {
        if (completed < 0)
            throw new ArgumentOutOfRangeException(nameof(completed));
        if (total is < 0 || total is { } knownTotal && completed > knownTotal)
            throw new ArgumentOutOfRangeException(nameof(total));
        Completed = completed;
        Total = total;
        Unit = Guard.RequireNotNullOrWhiteSpace(unit);
    }

    /// <summary>Non-negative completed work count.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Completed { get; }

    /// <summary>Optional non-negative total work count.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? Total { get; }

    /// <summary>Stable semantic work unit.</summary>
    public string Unit { get; }
}

/// <summary>Current schedulable work demand.</summary>
public sealed record ExecutionDemandStatus
{
    /// <summary>Creates a work-demand observation.</summary>
    /// <param name="ready">Non-negative work items ready for admission.</param>
    /// <param name="delayed">Non-negative work items intentionally delayed or throttled.</param>
    /// <exception cref="ArgumentOutOfRangeException">A demand count is negative.</exception>
    [JsonConstructor]
    public ExecutionDemandStatus(long ready, long delayed)
    {
        if (ready < 0)
            throw new ArgumentOutOfRangeException(nameof(ready));
        if (delayed < 0)
            throw new ArgumentOutOfRangeException(nameof(delayed));
        Ready = ready;
        Delayed = delayed;
    }

    /// <summary>Non-negative work items ready for admission.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Ready { get; }

    /// <summary>Non-negative work items intentionally delayed or throttled.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Delayed { get; }
}

/// <summary>Current bounded execution capacity.</summary>
public sealed record ExecutionCapacityStatus
{
    /// <summary>Creates an execution-capacity observation.</summary>
    /// <param name="active">Non-negative capacity currently in use.</param>
    /// <param name="limit">Non-negative effective capacity limit.</param>
    /// <exception cref="ArgumentOutOfRangeException">A capacity count is negative or active use exceeds the limit.</exception>
    [JsonConstructor]
    public ExecutionCapacityStatus(long active, long limit)
    {
        if (active < 0)
            throw new ArgumentOutOfRangeException(nameof(active));
        if (limit < 0 || active > limit)
            throw new ArgumentOutOfRangeException(nameof(limit));
        Active = active;
        Limit = limit;
    }

    /// <summary>Non-negative capacity currently in use.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Active { get; }

    /// <summary>Non-negative effective capacity limit.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Limit { get; }
}

/// <summary>Operational health reported by the selected execution interpretation.</summary>
public enum ExecutionHealthStatus
{
    /// <summary>No authoritative health observation is available.</summary>
    Unknown = 0,

    /// <summary>The execution interpretation is operating within declared bounds.</summary>
    Healthy = 1,

    /// <summary>The execution interpretation is operating with reduced capability or elevated risk.</summary>
    Degraded = 2,

    /// <summary>The execution interpretation cannot currently preserve its declared operating contract.</summary>
    Unhealthy = 3
}

/// <summary>Terminal semantic outcome of a Process instance.</summary>
public enum ExecutionTerminalOutcomeKind
{
    /// <summary>The Process has no terminal outcome.</summary>
    None = 0,

    /// <summary>The Process completed successfully.</summary>
    Completed = 1,

    /// <summary>The Process ended in semantic failure.</summary>
    Failed = 2,

    /// <summary>The Process ended through cooperative cancellation.</summary>
    Cancelled = 3,

    /// <summary>The Process was forcibly and irreversibly terminated.</summary>
    Terminated = 4
}

/// <summary>Terminal Process outcome with optional policy-controlled typed detail.</summary>
public sealed record ExecutionTerminalOutcome
{
    /// <summary>Creates a terminal or nonterminal outcome status.</summary>
    /// <param name="kind">Terminal outcome kind, or <see cref="ExecutionTerminalOutcomeKind.None"/>.</param>
    /// <param name="occurredAtUtc">Explicit UTC terminal time.</param>
    /// <param name="detail">Optional typed disclosed, unknown, or redacted outcome detail.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Terminal time presence contradicts <paramref name="kind"/>.</exception>
    [JsonConstructor]
    public ExecutionTerminalOutcome(
        ExecutionTerminalOutcomeKind kind,
        DateTimeOffset? occurredAtUtc = null,
        ExecutionStatusValue? detail = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported terminal outcome kind.");
        if ((kind == ExecutionTerminalOutcomeKind.None) != (occurredAtUtc is null))
            throw new ArgumentException("Only a terminal outcome carries an occurrence time.", nameof(occurredAtUtc));
        if (occurredAtUtc is { } occurred)
            ExecutionStatusValidation.RequireUtc(occurred, nameof(occurredAtUtc));
        if (kind == ExecutionTerminalOutcomeKind.None && detail is not null)
            throw new ArgumentException("A nonterminal Process cannot carry outcome detail.", nameof(detail));

        Kind = kind;
        OccurredAtUtc = occurredAtUtc;
        Detail = detail;
    }

    /// <summary>Shared nonterminal outcome value.</summary>
    public static ExecutionTerminalOutcome None { get; } = new(ExecutionTerminalOutcomeKind.None);

    /// <summary>Terminal outcome kind.</summary>
    public ExecutionTerminalOutcomeKind Kind { get; }

    /// <summary>Explicit UTC terminal time.</summary>
    public DateTimeOffset? OccurredAtUtc { get; }

    /// <summary>Optional policy-controlled typed outcome detail.</summary>
    public ExecutionStatusValue? Detail { get; }
}

/// <summary>Runtime-supplied status facets layered onto canonical Process lifecycle status.</summary>
public sealed record ExecutionRuntimeStatusDetails
{
    static readonly IComparer<ExecutionTokenStatus> TokenComparer =
        Comparer<ExecutionTokenStatus>.Create(static (left, right) =>
            StringComparer.Ordinal.Compare(left.TokenId.Value, right.TokenId.Value));

    static readonly IComparer<ExecutionWaitStatus> WaitComparer =
        Comparer<ExecutionWaitStatus>.Create(static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.TokenId.Value, right.TokenId.Value);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Node.Value, right.Node.Value);
        });

    static readonly IComparer<ExecutionRuntimeStatusExtension> ExtensionComparer =
        Comparer<ExecutionRuntimeStatusExtension>.Create(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value));

    /// <summary>Creates runtime-supplied execution status details.</summary>
    /// <param name="tokensDisclosure">Disclosure state of token observations.</param>
    /// <param name="tokens">Disclosed token observations.</param>
    /// <param name="waitsDisclosure">Disclosure state of wait observations.</param>
    /// <param name="waits">Disclosed wait observations.</param>
    /// <param name="progressDisclosure">Disclosure state of progress.</param>
    /// <param name="progress">Disclosed progress observation.</param>
    /// <param name="demandDisclosure">Disclosure state of work demand.</param>
    /// <param name="demand">Disclosed work-demand observation.</param>
    /// <param name="capacityDisclosure">Disclosure state of execution capacity.</param>
    /// <param name="capacity">Disclosed capacity observation.</param>
    /// <param name="health">Current operational health.</param>
    /// <param name="extensions">Typed block-specific runtime status extensions.</param>
    /// <exception cref="ArgumentOutOfRangeException">A disclosure or health value is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// A hidden facet carries content, a disclosed scalar facet has no content, or a collection contains null,
    /// default, or duplicate identities.
    /// </exception>
    [JsonConstructor]
    public ExecutionRuntimeStatusDetails(
        ExecutionStatusDisclosure tokensDisclosure = ExecutionStatusDisclosure.Unknown,
        ImmutableArray<ExecutionTokenStatus> tokens = default,
        ExecutionStatusDisclosure waitsDisclosure = ExecutionStatusDisclosure.Unknown,
        ImmutableArray<ExecutionWaitStatus> waits = default,
        ExecutionStatusDisclosure progressDisclosure = ExecutionStatusDisclosure.Unknown,
        ExecutionProgressStatus? progress = null,
        ExecutionStatusDisclosure demandDisclosure = ExecutionStatusDisclosure.Unknown,
        ExecutionDemandStatus? demand = null,
        ExecutionStatusDisclosure capacityDisclosure = ExecutionStatusDisclosure.Unknown,
        ExecutionCapacityStatus? capacity = null,
        ExecutionHealthStatus health = ExecutionHealthStatus.Unknown,
        ImmutableArray<ExecutionRuntimeStatusExtension> extensions = default)
    {
        ValidateCollectionDisclosure(tokensDisclosure, tokens.IsDefaultOrEmpty, nameof(tokens));
        ValidateCollectionDisclosure(waitsDisclosure, waits.IsDefaultOrEmpty, nameof(waits));
        ValidateScalarDisclosure(progressDisclosure, progress, nameof(progress));
        ValidateScalarDisclosure(demandDisclosure, demand, nameof(demand));
        ValidateScalarDisclosure(capacityDisclosure, capacity, nameof(capacity));
        if (!Enum.IsDefined(health))
            throw new ArgumentOutOfRangeException(nameof(health), health, "Unsupported execution health.");

        TokensDisclosure = tokensDisclosure;
        Tokens = NormalizeTokens(tokens);
        WaitsDisclosure = waitsDisclosure;
        Waits = NormalizeWaits(waits);
        ProgressDisclosure = progressDisclosure;
        Progress = progress;
        DemandDisclosure = demandDisclosure;
        Demand = demand;
        CapacityDisclosure = capacityDisclosure;
        Capacity = capacity;
        Health = health;
        Extensions = NormalizeExtensions(extensions);
    }

    /// <summary>Shared runtime details with every interpretation-specific facet explicitly unknown.</summary>
    public static ExecutionRuntimeStatusDetails Unknown { get; } = new();

    /// <summary>Disclosure state of token observations.</summary>
    public ExecutionStatusDisclosure TokensDisclosure { get; }

    /// <summary>Canonical token observations ordered by stable token identity.</summary>
    public ImmutableArray<ExecutionTokenStatus> Tokens { get; }

    /// <summary>Disclosure state of wait observations.</summary>
    public ExecutionStatusDisclosure WaitsDisclosure { get; }

    /// <summary>Canonical wait observations ordered by token and semantic node.</summary>
    public ImmutableArray<ExecutionWaitStatus> Waits { get; }

    /// <summary>Disclosure state of progress.</summary>
    public ExecutionStatusDisclosure ProgressDisclosure { get; }

    /// <summary>Disclosed progress observation.</summary>
    public ExecutionProgressStatus? Progress { get; }

    /// <summary>Disclosure state of work demand.</summary>
    public ExecutionStatusDisclosure DemandDisclosure { get; }

    /// <summary>Disclosed work-demand observation.</summary>
    public ExecutionDemandStatus? Demand { get; }

    /// <summary>Disclosure state of execution capacity.</summary>
    public ExecutionStatusDisclosure CapacityDisclosure { get; }

    /// <summary>Disclosed capacity observation.</summary>
    public ExecutionCapacityStatus? Capacity { get; }

    /// <summary>Current operational health.</summary>
    public ExecutionHealthStatus Health { get; }

    /// <summary>Canonical typed extensions ordered by stable extension identity.</summary>
    public ImmutableArray<ExecutionRuntimeStatusExtension> Extensions { get; }

    static void ValidateCollectionDisclosure(
        ExecutionStatusDisclosure disclosure,
        bool isEmpty,
        string parameterName)
    {
        ValidateDisclosure(disclosure, parameterName);
        if (disclosure != ExecutionStatusDisclosure.Disclosed && !isEmpty)
            throw new ArgumentException("Unknown or redacted status collections cannot disclose entries.", parameterName);
    }

    static void ValidateScalarDisclosure<T>(
        ExecutionStatusDisclosure disclosure,
        T? value,
        string parameterName)
        where T : class
    {
        ValidateDisclosure(disclosure, parameterName);
        if ((disclosure == ExecutionStatusDisclosure.Disclosed) != (value is not null))
            throw new ArgumentException("Only a disclosed status facet carries a value.", parameterName);
    }

    static void ValidateDisclosure(ExecutionStatusDisclosure disclosure, string parameterName)
    {
        if (!Enum.IsDefined(disclosure))
            throw new ArgumentOutOfRangeException(parameterName, disclosure, "Unsupported status disclosure.");
    }

    static ImmutableArray<ExecutionTokenStatus> NormalizeTokens(ImmutableArray<ExecutionTokenStatus> tokens)
    {
        if (tokens.IsDefaultOrEmpty)
            return [];

        HashSet<TokenId> identities = [];
        foreach (var token in tokens)
        {
            if (token is null || !identities.Add(token.TokenId))
                throw new ArgumentException("Token status contains a null or duplicate identity.", nameof(tokens));
        }

        return IsSorted(tokens, TokenComparer) ? tokens : tokens.Sort(TokenComparer);
    }

    static ImmutableArray<ExecutionWaitStatus> NormalizeWaits(ImmutableArray<ExecutionWaitStatus> waits)
    {
        if (waits.IsDefaultOrEmpty)
            return [];

        HashSet<(TokenId, ExecutionNodeId)> identities = [];
        foreach (var wait in waits)
        {
            if (wait is null || !identities.Add((wait.TokenId, wait.Node)))
                throw new ArgumentException("Wait status contains a null or duplicate token-node identity.", nameof(waits));
        }

        return IsSorted(waits, WaitComparer) ? waits : waits.Sort(WaitComparer);
    }

    static ImmutableArray<ExecutionRuntimeStatusExtension> NormalizeExtensions(
        ImmutableArray<ExecutionRuntimeStatusExtension> extensions)
    {
        if (extensions.IsDefaultOrEmpty)
            return [];

        HashSet<ExecutionExtensionId> identities = [];
        foreach (var extension in extensions)
        {
            if (extension is null || !identities.Add(extension.Id))
                throw new ArgumentException("Runtime status extensions contain a null or duplicate identity.", nameof(extensions));
        }

        return IsSorted(extensions, ExtensionComparer) ? extensions : extensions.Sort(ExtensionComparer);
    }

    static bool IsSorted<T>(ImmutableArray<T> values, IComparer<T> comparer)
    {
        for (var index = 1; index < values.Length; index++)
        {
            if (comparer.Compare(values[index - 1], values[index]) > 0)
                return false;
        }

        return true;
    }
}

/// <summary>Protocol-neutral, structurally safe status of one logical Process instance.</summary>
public sealed record ExecutionStatus
{
    /// <summary>Current common execution-status schema version.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-execution-status/v1");

    /// <summary>Creates common execution status.</summary>
    /// <param name="schemaVersion">Exact common execution-status schema version.</param>
    /// <param name="definition">Exact pinned execution definition revision and fingerprint.</param>
    /// <param name="processInstanceId">Stable logical Process-instance identity.</param>
    /// <param name="controlRevision">Current semantic Process-control revision and fence.</param>
    /// <param name="controlMode">Current lifecycle-control mode.</param>
    /// <param name="attempts">Non-empty chronological safe attempt summaries.</param>
    /// <param name="activeActivation">Safe active activation summary.</param>
    /// <param name="runtime">Runtime-supplied tokens, waits, progress, demand, capacity, health, and extensions.</param>
    /// <param name="terminalOutcome">Terminal outcome, or the explicit nonterminal outcome.</param>
    /// <param name="createdAtUtc">Explicit UTC instance creation time.</param>
    /// <param name="updatedAtUtc">Latest explicit UTC status observation time.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="runtime"/>, or <paramref name="terminalOutcome"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="controlMode"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Schema, identities, attempt lineage, activation, terminal outcome, or chronology is inconsistent.
    /// </exception>
    [JsonConstructor]
    public ExecutionStatus(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionReference definition,
        ProcessInstanceId processInstanceId,
        ProcessControlRevision controlRevision,
        ProcessControlMode controlMode,
        ImmutableArray<ExecutionAttemptStatus> attempts,
        ExecutionActivationStatus? activeActivation,
        ExecutionRuntimeStatusDetails runtime,
        ExecutionTerminalOutcome terminalOutcome,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("Unsupported common execution-status schema version.", nameof(schemaVersion));
        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
            throw new ArgumentException("Execution status requires a stable Process instance.", nameof(processInstanceId));
        if (string.IsNullOrWhiteSpace(controlRevision.Value))
            throw new ArgumentException("Execution status requires a semantic control revision.", nameof(controlRevision));
        if (!Enum.IsDefined(controlMode) || controlMode == ProcessControlMode.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(controlMode), controlMode, "Control mode must be explicit.");
        if (attempts.IsDefaultOrEmpty)
            throw new ArgumentException("Execution status requires at least one attempt summary.", nameof(attempts));

        runtime = Guard.RequireNotNull(runtime);
        terminalOutcome = Guard.RequireNotNull(terminalOutcome);

        ExecutionStatusValidation.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        ExecutionStatusValidation.RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        if (updatedAtUtc < createdAtUtc)
            throw new ArgumentException("Execution status cannot be updated before creation.", nameof(updatedAtUtc));

        ValidateAttempts(attempts, createdAtUtc, updatedAtUtc);
        ValidateRuntimeStatus(runtime, updatedAtUtc);
        var current = attempts[^1];
        if ((activeActivation is not null) != (current.Phase == ProcessControlExecutionPhase.InActivation))
            throw new ArgumentException("Only an in-activation attempt carries active activation status.", nameof(activeActivation));
        if (activeActivation is not null
            && (activeActivation.AttemptId != current.AttemptId
                || activeActivation.StartedAtUtc < current.StartedAtUtc
                || activeActivation.StartedAtUtc > updatedAtUtc
                || activeActivation.StartedUnderRevision.Ordinal >= controlRevision.Ordinal))
        {
            throw new ArgumentException(
                "Active activation status is outside the current attempt or current control-revision cut.",
                nameof(activeActivation));
        }

        ValidateTerminalOutcome(controlMode, terminalOutcome, current, updatedAtUtc);

        SchemaVersion = schemaVersion;
        Definition = Guard.RequireNotNull(definition);
        ProcessInstanceId = processInstanceId;
        ControlRevision = controlRevision;
        ControlMode = controlMode;
        Attempts = attempts;
        ActiveActivation = activeActivation;
        Runtime = runtime;
        TerminalOutcome = terminalOutcome;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Exact common execution-status schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Exact pinned execution definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Stable logical Process-instance identity.</summary>
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Current semantic Process-control revision and optimistic fence.</summary>
    public ProcessControlRevision ControlRevision { get; }

    /// <summary>Current lifecycle-control mode.</summary>
    public ProcessControlMode ControlMode { get; }

    /// <summary>Chronological safe attempt summaries.</summary>
    public ImmutableArray<ExecutionAttemptStatus> Attempts { get; }

    /// <summary>Stable identity of the final current or terminal attempt.</summary>
    public ProcessAttemptId CurrentAttemptId => Attempts[^1].AttemptId;

    /// <summary>Final current or terminal attempt summary.</summary>
    [JsonIgnore]
    public ExecutionAttemptStatus CurrentAttempt => Attempts[^1];

    /// <summary>Safe active activation summary.</summary>
    public ExecutionActivationStatus? ActiveActivation { get; }

    /// <summary>Runtime-supplied token, wait, work, health, and extension status.</summary>
    public ExecutionRuntimeStatusDetails Runtime { get; }

    /// <summary>Explicit terminal or nonterminal outcome.</summary>
    public ExecutionTerminalOutcome TerminalOutcome { get; }

    /// <summary>Explicit UTC instance creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Latest explicit UTC status observation time.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    static void ValidateAttempts(
        ImmutableArray<ExecutionAttemptStatus> attempts,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        HashSet<ProcessAttemptId> identities = [];
        DateTimeOffset? priorEnd = null;
        for (var index = 0; index < attempts.Length; index++)
        {
            var attempt = attempts[index]
                ?? throw new ArgumentException("Attempt status cannot contain null entries.", nameof(attempts));
            if (!identities.Add(attempt.AttemptId))
                throw new ArgumentException("Attempt status contains a duplicate identity.", nameof(attempts));
            var earliestStart = priorEnd ?? createdAtUtc;
            if (attempt.StartedAtUtc < earliestStart || attempt.StartedAtUtc > updatedAtUtc)
                throw new ArgumentException("Attempt status violates instance chronology.", nameof(attempts));
            if (attempt.EndedAtUtc > updatedAtUtc)
                throw new ArgumentException("Attempt status ends after the status observation.", nameof(attempts));
            if (index < attempts.Length - 1
                && attempt.Disposition != ExecutionAttemptDisposition.Abandoned)
            {
                throw new ArgumentException(
                    "Only abandoned attempts may precede the final attempt.",
                    nameof(attempts));
            }
            if (index == attempts.Length - 1 && attempt.Disposition == ExecutionAttemptDisposition.Abandoned)
                throw new ArgumentException("The final attempt cannot be abandoned without a replacement.", nameof(attempts));

            priorEnd = attempt.EndedAtUtc;
        }
    }

    static void ValidateRuntimeStatus(ExecutionRuntimeStatusDetails runtime, DateTimeOffset updatedAtUtc)
    {
        for (var waitIndex = 0; waitIndex < runtime.Waits.Length; waitIndex++)
        {
            var wait = runtime.Waits[waitIndex];
            if (wait.WaitingSinceUtc > updatedAtUtc)
                throw new ArgumentException("Wait status cannot begin after the status observation.", nameof(runtime));
            if (runtime.TokensDisclosure != ExecutionStatusDisclosure.Disclosed)
                continue;

            ExecutionTokenStatus? matchingToken = null;
            for (var tokenIndex = 0; tokenIndex < runtime.Tokens.Length; tokenIndex++)
            {
                var token = runtime.Tokens[tokenIndex];
                if (token.TokenId == wait.TokenId)
                {
                    matchingToken = token;
                    break;
                }
            }

            if (matchingToken is null
                || matchingToken.Node != wait.Node
                || matchingToken.Disposition != ExecutionTokenDisposition.Waiting)
            {
                throw new ArgumentException(
                    "Every disclosed wait requires matching disclosed waiting-token evidence.",
                    nameof(runtime));
            }
        }

        if (runtime.TokensDisclosure != ExecutionStatusDisclosure.Disclosed
            || runtime.WaitsDisclosure != ExecutionStatusDisclosure.Disclosed)
        {
            return;
        }

        for (var tokenIndex = 0; tokenIndex < runtime.Tokens.Length; tokenIndex++)
        {
            var token = runtime.Tokens[tokenIndex];
            if (token.Disposition != ExecutionTokenDisposition.Waiting)
                continue;

            var hasMatchingWait = false;
            for (var waitIndex = 0; waitIndex < runtime.Waits.Length; waitIndex++)
            {
                var wait = runtime.Waits[waitIndex];
                if (wait.TokenId == token.TokenId && wait.Node == token.Node)
                {
                    hasMatchingWait = true;
                    break;
                }
            }

            if (!hasMatchingWait)
            {
                throw new ArgumentException(
                    "Every disclosed waiting token requires matching disclosed wait evidence.",
                    nameof(runtime));
            }
        }
    }

    static void ValidateTerminalOutcome(
        ProcessControlMode controlMode,
        ExecutionTerminalOutcome terminalOutcome,
        ExecutionAttemptStatus current,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(terminalOutcome);
        var controlRequired = controlMode switch
        {
            ProcessControlMode.Cancelled => ExecutionTerminalOutcomeKind.Cancelled,
            ProcessControlMode.Terminated => ExecutionTerminalOutcomeKind.Terminated,
            ProcessControlMode.CancellationFailed => ExecutionTerminalOutcomeKind.Failed,
            _ => ExecutionTerminalOutcomeKind.None
        };
        var attemptRequired = current.Disposition switch
        {
            ExecutionAttemptDisposition.Current => ExecutionTerminalOutcomeKind.None,
            ExecutionAttemptDisposition.Completed => ExecutionTerminalOutcomeKind.Completed,
            ExecutionAttemptDisposition.Failed => ExecutionTerminalOutcomeKind.Failed,
            ExecutionAttemptDisposition.Cancelled => ExecutionTerminalOutcomeKind.Cancelled,
            ExecutionAttemptDisposition.Terminated => ExecutionTerminalOutcomeKind.Terminated,
            _ => throw new ArgumentException(
                "The final attempt disposition cannot represent current execution status.",
                nameof(current))
        };
        if (terminalOutcome.Kind != attemptRequired)
            throw new ArgumentException("Terminal outcome contradicts the final attempt disposition.", nameof(terminalOutcome));
        if (controlRequired != ExecutionTerminalOutcomeKind.None && terminalOutcome.Kind != controlRequired)
            throw new ArgumentException("Terminal outcome contradicts lifecycle-control mode.", nameof(terminalOutcome));
        if (controlRequired == ExecutionTerminalOutcomeKind.None
            && terminalOutcome.Kind is ExecutionTerminalOutcomeKind.Cancelled or ExecutionTerminalOutcomeKind.Terminated)
        {
            throw new ArgumentException("Cancellation outcome contradicts lifecycle-control mode.", nameof(terminalOutcome));
        }
        if (terminalOutcome.OccurredAtUtc > updatedAtUtc)
            throw new ArgumentException("Terminal outcome follows the status observation.", nameof(terminalOutcome));
        if (terminalOutcome.Kind != ExecutionTerminalOutcomeKind.None
            && terminalOutcome.OccurredAtUtc != current.EndedAtUtc)
        {
            throw new ArgumentException("Terminal outcome must coincide with current attempt closure.", nameof(terminalOutcome));
        }
    }
}

/// <summary>Projects canonical control state into structurally safe protocol-neutral execution status.</summary>
public static class ExecutionStatusProjector
{
    static readonly ValueContract RedactedOutcomeDetailContract = new();

    /// <summary>Projects canonical Process-control state without exposing sensitive retained evidence.</summary>
    /// <param name="state">Canonical Process-control state to summarize.</param>
    /// <param name="runtime">
    /// Optional runtime facets; omitted facets remain explicitly unknown rather than appearing empty.
    /// </param>
    /// <param name="terminalOutcome">
    /// Optional completed or failed outcome supplied by a Process interpreter. Cancellation and termination are
    /// derived authoritatively from control state and cannot be overridden.
    /// </param>
    /// <returns>
    /// A safe status containing identities, lifecycle summaries, and explicitly disclosed runtime facets, but no
    /// command receipts, Signals, reasons, affinity values, wait keys, bindings, or input/output payloads.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Supplied terminal outcome contradicts canonical lifecycle state.
    /// </exception>
    public static ExecutionStatus Project(
        ProcessControlState state,
        ExecutionRuntimeStatusDetails? runtime = null,
        ExecutionTerminalOutcome? terminalOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var projectedOutcome = state.Mode switch
        {
            ProcessControlMode.Cancelled => new ExecutionTerminalOutcome(
                ExecutionTerminalOutcomeKind.Cancelled,
                state.CurrentAttempt.EndedAtUtc,
                ExecutionStatusValue.Redacted(RedactedOutcomeDetailContract)),
            ProcessControlMode.Terminated => new ExecutionTerminalOutcome(
                ExecutionTerminalOutcomeKind.Terminated,
                state.CurrentAttempt.EndedAtUtc,
                ExecutionStatusValue.Redacted(RedactedOutcomeDetailContract)),
            ProcessControlMode.CancellationFailed => new ExecutionTerminalOutcome(
                ExecutionTerminalOutcomeKind.Failed,
                state.CurrentAttempt.EndedAtUtc,
                ExecutionStatusValue.Redacted(RedactedOutcomeDetailContract)),
            _ => terminalOutcome ?? ExecutionTerminalOutcome.None
        };
        var statusUpdatedAtUtc = projectedOutcome.OccurredAtUtc is { } occurredAtUtc
            && occurredAtUtc > state.UpdatedAtUtc
                ? occurredAtUtc
                : state.UpdatedAtUtc;
        var attempts = ImmutableArray.CreateBuilder<ExecutionAttemptStatus>(state.Attempts.Length);
        for (var index = 0; index < state.Attempts.Length; index++)
        {
            var attempt = state.Attempts[index];
            var isFinal = index == state.Attempts.Length - 1;
            var lastSafePoint = attempt.LastSafePoint;
            var outcomeDisposition = isFinal
                ? projectedOutcome.Kind switch
                {
                    ExecutionTerminalOutcomeKind.Completed => ExecutionAttemptDisposition.Completed,
                    ExecutionTerminalOutcomeKind.Failed => ExecutionAttemptDisposition.Failed,
                    _ => MapDisposition(attempt.Disposition)
                }
                : MapDisposition(attempt.Disposition);
            var isExecutionTerminal = isFinal
                && projectedOutcome.Kind is ExecutionTerminalOutcomeKind.Completed or ExecutionTerminalOutcomeKind.Failed;
            attempts.Add(new(
                attempt.AttemptId,
                attempt.StartedAtUtc,
                isExecutionTerminal ? projectedOutcome.OccurredAtUtc : attempt.EndedAtUtc,
                outcomeDisposition,
                isExecutionTerminal ? ProcessControlExecutionPhase.Stopped : attempt.Phase,
                attempt.SafePoints.Length,
                lastSafePoint?.SafePointId,
                lastSafePoint?.Node));
        }

        var active = state.CurrentAttempt.ActiveActivation is { } activation
            ? new ExecutionActivationStatus(
                activation.ActivationId,
                state.CurrentAttempt.AttemptId,
                activation.Expectation.Revision,
                activation.ObservedAtUtc)
            : null;

        return new(
            ExecutionStatus.CurrentSchemaVersion,
            state.Definition,
            state.ProcessInstanceId,
            state.Revision,
            state.Mode,
            attempts.MoveToImmutable(),
            active,
            runtime ?? ExecutionRuntimeStatusDetails.Unknown,
            projectedOutcome,
            state.CreatedAtUtc,
            statusUpdatedAtUtc);
    }

    static ExecutionAttemptDisposition MapDisposition(ProcessControlAttemptDisposition disposition) =>
        disposition switch
        {
            ProcessControlAttemptDisposition.Current => ExecutionAttemptDisposition.Current,
            ProcessControlAttemptDisposition.Abandoned => ExecutionAttemptDisposition.Abandoned,
            ProcessControlAttemptDisposition.Cancelled => ExecutionAttemptDisposition.Cancelled,
            ProcessControlAttemptDisposition.Terminated => ExecutionAttemptDisposition.Terminated,
            ProcessControlAttemptDisposition.CancellationFailed => ExecutionAttemptDisposition.Failed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported Process-control attempt disposition.")
        };
}

static class ExecutionStatusValidation
{
    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Execution status observations must use an explicit UTC offset.",
                parameterName);
        }
    }
}
