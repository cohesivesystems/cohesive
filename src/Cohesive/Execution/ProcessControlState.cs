using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Canonical lifecycle-control mode of one logical Process instance.</summary>
public enum ProcessControlMode
{
    /// <summary>No mode was supplied; invalid in persisted control state.</summary>
    Unspecified = 0,

    /// <summary>Ordinary Process activation may proceed.</summary>
    Running = 1,

    /// <summary>A pause was accepted and will take effect at the next safe point.</summary>
    PauseRequested = 2,

    /// <summary>Ordinary activation is stopped while Signals may still be buffered.</summary>
    Paused = 3,

    /// <summary>Current-attempt abandonment will take effect at the next safe point.</summary>
    RestartRequested = 4,

    /// <summary>Cooperative semantic cancellation will take effect at the next safe point.</summary>
    CancellationRequested = 5,

    /// <summary>The current attempt ended through cooperative semantic cancellation.</summary>
    Cancelled = 6,

    /// <summary>The current attempt was forcibly and irreversibly stopped.</summary>
    Terminated = 7
}

/// <summary>Lifecycle disposition of one Process attempt retained in control lineage.</summary>
public enum ProcessControlAttemptDisposition
{
    /// <summary>No disposition was supplied; invalid in persisted attempt state.</summary>
    Unspecified = 0,

    /// <summary>The final attempt is the current nonterminal attempt.</summary>
    Current = 1,

    /// <summary>The attempt was explicitly abandoned in favor of a stable replacement.</summary>
    Abandoned = 2,

    /// <summary>The attempt ended through cooperative semantic cancellation.</summary>
    Cancelled = 3,

    /// <summary>The attempt ended through immediate termination.</summary>
    Terminated = 4
}

/// <summary>Execution position relevant to invariant-preserving lifecycle control.</summary>
public enum ProcessControlExecutionPhase
{
    /// <summary>No execution phase was supplied; invalid in persisted attempt state.</summary>
    Unspecified = 0,

    /// <summary>The attempt has not entered an activation or is ready to enter another.</summary>
    Ready = 1,

    /// <summary>A finite activation is currently evaluating between durable cuts.</summary>
    InActivation = 2,

    /// <summary>An explicit invariant-preserving safe point was reached.</summary>
    AtSafePoint = 3,

    /// <summary>The attempt is closed and cannot enter another activation.</summary>
    Stopped = 4
}

/// <summary>Original durable decision retained for an accepted lifecycle command.</summary>
public enum ProcessControlReceiptDisposition
{
    /// <summary>No disposition was supplied; invalid in a durable command receipt.</summary>
    Unspecified = 0,

    /// <summary>The command changed lifecycle state immediately.</summary>
    Applied = 1,

    /// <summary>The command changed state to a pending request awaiting a safe point.</summary>
    DeferredToSafePoint = 2,

    /// <summary>The desired lifecycle state was already satisfied.</summary>
    AlreadySatisfied = 3,

    /// <summary>The desired lifecycle change was already pending at a safe point.</summary>
    AlreadyRequested = 4,

    /// <summary>The Signal was admitted for active Process consumption.</summary>
    SignalAccepted = 5,

    /// <summary>The Signal was admitted for buffering while ordinary activation is stopped.</summary>
    SignalBuffered = 6,

    /// <summary>The same logical Signal was already admitted by another control command.</summary>
    SignalDuplicate = 7
}

/// <summary>How a newly admitted Signal may be used by the targeted Process attempt.</summary>
public enum ProcessSignalAdmissionDisposition
{
    /// <summary>No disposition was supplied; invalid in persisted Signal admission evidence.</summary>
    Unspecified = 0,

    /// <summary>The Signal may participate in current Process input arbitration.</summary>
    Active = 1,

    /// <summary>The Signal is durably buffered but cannot activate work while paused.</summary>
    Buffered = 2
}

/// <summary>Explicit pending lifecycle action that must be applied at a safe point.</summary>
public enum ProcessControlPendingAction
{
    /// <summary>No pending action exists.</summary>
    None = 0,

    /// <summary>Pause at the next invariant-preserving safe point.</summary>
    Pause = 1,

    /// <summary>Abandon and replace the attempt at the next invariant-preserving safe point.</summary>
    RestartAttempt = 2,

    /// <summary>Complete cooperative cancellation at the next invariant-preserving safe point.</summary>
    Cancel = 3
}

/// <summary>Persisted exact evidence of one invariant-preserving Process safe point.</summary>
public sealed record ProcessControlSafePoint
{
    /// <summary>Creates persisted safe-point evidence from its exact activation and cut observations.</summary>
    /// <param name="activation">Exact durable observation that began the completed activation.</param>
    /// <param name="observation">Exact durable-cut observation that completed the activation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="activation"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The observations address different attempts or activations, or the safe point precedes activation start.
    /// </exception>
    [JsonConstructor]
    public ProcessControlSafePoint(
        ProcessActivationStartObservation activation,
        ProcessSafePointObservation observation)
    {
        Activation = Guard.RequireNotNull(activation);
        Observation = Guard.RequireNotNull(observation);
        if (activation.Expectation.Continuation != observation.Expectation.Continuation)
        {
            throw new ArgumentException("Safe-point observations must address one exact Process attempt.", nameof(observation));
        }

        if (activation.ActivationId != observation.ActivationId)
        {
            throw new ArgumentException("A safe point must complete its exact activation.", nameof(observation));
        }

        if (observation.ObservedAtUtc < activation.ObservedAtUtc)
        {
            throw new ArgumentException("A safe point cannot precede activation start.", nameof(observation));
        }

        if (observation.Expectation.Revision.Ordinal <= activation.Expectation.Revision.Ordinal)
        {
            throw new ArgumentException("A safe-point fence must follow its activation-start fence.", nameof(observation));
        }
    }

    /// <summary>Exact activation-start evidence.</summary>
    public ProcessActivationStartObservation Activation { get; }

    /// <summary>Exact invariant-preserving durable-cut evidence.</summary>
    public ProcessSafePointObservation Observation { get; }

    /// <summary>Stable durable-cut identity.</summary>
    [JsonIgnore]
    public ProcessSafePointId SafePointId => Observation.SafePointId;

    /// <summary>Finite activation completed at the cut.</summary>
    [JsonIgnore]
    public ActivationId ActivationId => Observation.ActivationId;

    /// <summary>Stable Process node at the cut.</summary>
    [JsonIgnore]
    public ExecutionNodeId Node => Observation.Node;

    /// <summary>Explicit UTC observation time.</summary>
    [JsonIgnore]
    public DateTimeOffset ObservedAtUtc => Observation.ObservedAtUtc;
}

/// <summary>Minimal command-linked evidence that one Process attempt stopped.</summary>
/// <remarks>
/// Attempt disposition and the referenced command receipt are authoritative for the closure kind, reason,
/// replacement, and cleanup policy. Closure evidence retains only the causal link, the new temporal fact, and any
/// activation interrupted by immediate termination.
/// </remarks>
public sealed record ProcessAttemptClosure
{
    /// <summary>Creates minimal attempt-closure evidence.</summary>
    /// <param name="commandId">Control command that caused the attempt to close.</param>
    /// <param name="occurredAtUtc">Explicit UTC closure time.</param>
    /// <param name="interruptedActivation">
    /// Exact activation interrupted by immediate termination, or <see langword="null"/> for a safe-boundary close.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="commandId"/> is default, a timestamp is not UTC, or interruption chronology is invalid.
    /// </exception>
    [JsonConstructor]
    public ProcessAttemptClosure(
        ProcessControlCommandId commandId,
        DateTimeOffset occurredAtUtc,
        ProcessActivationStartObservation? interruptedActivation = null)
    {
        if (string.IsNullOrWhiteSpace(commandId.Value))
        {
            throw new ArgumentException("Attempt closure requires its control command.", nameof(commandId));
        }

        ExecutionObservationRequirements.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        if (interruptedActivation is not null && interruptedActivation.ObservedAtUtc > occurredAtUtc)
        {
            throw new ArgumentException(
                "An interrupted activation cannot begin after attempt closure.",
                nameof(interruptedActivation));
        }

        CommandId = commandId;
        OccurredAtUtc = occurredAtUtc;
        InterruptedActivation = interruptedActivation;
    }

    /// <summary>Control command that caused the attempt to close.</summary>
    public ProcessControlCommandId CommandId { get; }

    /// <summary>Explicit UTC closure time.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Exact activation interrupted by immediate termination, when applicable.</summary>
    public ProcessActivationStartObservation? InterruptedActivation { get; }
}

/// <summary>Portable control-relevant state for one Process attempt in the retained lineage.</summary>
public sealed record ProcessControlAttemptState
{
    /// <summary>Creates one attempt-lineage entry.</summary>
    /// <param name="attemptId">Stable attempt identity.</param>
    /// <param name="startedAtUtc">Explicit UTC attempt start time.</param>
    /// <param name="disposition">Current or terminal attempt disposition.</param>
    /// <param name="phase">Current control-relevant execution phase.</param>
    /// <param name="activeActivation">Exact activation-start evidence while an activation is in flight.</param>
    /// <param name="safePoints">Complete chronological safe-point evidence for this attempt.</param>
    /// <param name="affinityBindings">Exact write-once attempt-affinity binding evidence.</param>
    /// <param name="closure">Minimal command-linked evidence only for a closed attempt.</param>
    /// <exception cref="ArgumentException">
    /// Identities, timestamps, lifecycle fields, observations, closure, or affinity slots form invalid state.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="disposition"/> or <paramref name="phase"/> is unspecified or unsupported.
    /// </exception>
    [JsonConstructor]
    public ProcessControlAttemptState(
        ProcessAttemptId attemptId,
        DateTimeOffset startedAtUtc,
        ProcessControlAttemptDisposition disposition,
        ProcessControlExecutionPhase phase,
        ProcessActivationStartObservation? activeActivation = null,
        ImmutableArray<ProcessControlSafePoint> safePoints = default,
        ImmutableArray<ProcessAttemptAffinityObservation> affinityBindings = default,
        ProcessAttemptClosure? closure = null)
    {
        if (string.IsNullOrWhiteSpace(attemptId.Value))
        {
            throw new ArgumentException("An attempt-lineage entry requires a stable identity.", nameof(attemptId));
        }

        ExecutionObservationRequirements.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        if (!Enum.IsDefined(disposition) || disposition == ProcessControlAttemptDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Attempt disposition must be explicit.");
        }

        if (!Enum.IsDefined(phase) || phase == ProcessControlExecutionPhase.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Attempt execution phase must be explicit.");
        }

        var normalizedSafePoints = NormalizeSafePoints(attemptId, startedAtUtc, safePoints);
        if (activeActivation is not null)
        {
            if (activeActivation.Expectation.Continuation.ProcessAttemptId != attemptId)
            {
                throw new ArgumentException("Active activation evidence addresses another attempt.", nameof(activeActivation));
            }

            if (activeActivation.ObservedAtUtc < startedAtUtc
                || (!normalizedSafePoints.IsEmpty
                    && activeActivation.ObservedAtUtc < normalizedSafePoints[^1].ObservedAtUtc))
            {
                throw new ArgumentException("Active activation evidence violates attempt chronology.", nameof(activeActivation));
            }
            foreach (var safePoint in normalizedSafePoints)
            {
                if (safePoint.ActivationId == activeActivation.ActivationId)
                {
                    throw new ArgumentException(
                        "A completed activation identity cannot become active again.",
                        nameof(activeActivation));
                }
            }
            if (!normalizedSafePoints.IsEmpty
                && activeActivation.Expectation.Revision.Ordinal
                    <= normalizedSafePoints[^1].Observation.Expectation.Revision.Ordinal)
            {
                throw new ArgumentException(
                    "Active activation evidence must follow the preceding safe-point fence.",
                    nameof(activeActivation));
            }
        }
        if ((phase == ProcessControlExecutionPhase.InActivation) != (activeActivation is not null))
        {
            throw new ArgumentException("Only an in-flight activation phase carries active evidence.", nameof(activeActivation));
        }

        if (phase == ProcessControlExecutionPhase.AtSafePoint && normalizedSafePoints.IsEmpty)
        {
            throw new ArgumentException("An at-safe-point attempt requires exact safe-point evidence.", nameof(safePoints));
        }

        if (phase == ProcessControlExecutionPhase.Ready && !normalizedSafePoints.IsEmpty)
        {
            throw new ArgumentException("A ready attempt cannot retain completed safe points.", nameof(safePoints));
        }

        if ((disposition == ProcessControlAttemptDisposition.Current) != (closure is null))
        {
            throw new ArgumentException("Attempt disposition and closure evidence contradict each other.", nameof(closure));
        }

        if (disposition == ProcessControlAttemptDisposition.Current
            && phase == ProcessControlExecutionPhase.Stopped)
        {
            throw new ArgumentException("A current attempt cannot be stopped.", nameof(disposition));
        }
        if (disposition != ProcessControlAttemptDisposition.Current
            && phase != ProcessControlExecutionPhase.Stopped)
        {
            throw new ArgumentException("A closed attempt requires stopped phase.", nameof(disposition));
        }
        if (closure is not null
            && (closure.OccurredAtUtc < startedAtUtc
                || (!normalizedSafePoints.IsEmpty
                    && normalizedSafePoints[^1].ObservedAtUtc > closure.OccurredAtUtc)))
        {
            throw new ArgumentException("Attempt closure violates attempt chronology.", nameof(closure));
        }
        if (closure?.InterruptedActivation is { } interrupted)
        {
            if (disposition != ProcessControlAttemptDisposition.Terminated)
            {
                throw new ArgumentException("Only termination may interrupt an active activation.", nameof(closure));
            }

            if (interrupted.Expectation.Continuation.ProcessAttemptId != attemptId)
            {
                throw new ArgumentException("Interrupted activation evidence addresses another attempt.", nameof(closure));
            }

            if (interrupted.ObservedAtUtc < startedAtUtc)
            {
                throw new ArgumentException("Interrupted activation evidence predates its attempt.", nameof(closure));
            }

            if (!normalizedSafePoints.IsEmpty
                && (interrupted.ObservedAtUtc < normalizedSafePoints[^1].ObservedAtUtc
                    || interrupted.Expectation.Revision.Ordinal
                        <= normalizedSafePoints[^1].Observation.Expectation.Revision.Ordinal))
            {
                throw new ArgumentException(
                    "Interrupted activation evidence must follow the preceding safe point.",
                    nameof(closure));
            }
            foreach (var safePoint in normalizedSafePoints)
            {
                if (safePoint.ActivationId == interrupted.ActivationId)
                {
                    throw new ArgumentException(
                        "An interrupted activation cannot also be completed at a safe point.",
                        nameof(closure));
                }
            }
        }

        AttemptId = attemptId;
        StartedAtUtc = startedAtUtc;
        Disposition = disposition;
        Phase = phase;
        ActiveActivation = activeActivation;
        SafePoints = normalizedSafePoints;
        AffinityBindings = NormalizeAffinityBindings(attemptId, startedAtUtc, affinityBindings);
        Closure = closure;
    }

    /// <summary>Stable attempt identity.</summary>
    public ProcessAttemptId AttemptId { get; }

    /// <summary>Explicit UTC attempt start time.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Current or terminal attempt disposition.</summary>
    public ProcessControlAttemptDisposition Disposition { get; }

    /// <summary>Current control-relevant execution phase.</summary>
    public ProcessControlExecutionPhase Phase { get; }

    /// <summary>Exact active activation-start evidence, or <see langword="null"/> outside an activation.</summary>
    public ProcessActivationStartObservation? ActiveActivation { get; }

    /// <summary>Complete chronological safe-point evidence retained for exact replay and conflict detection.</summary>
    public ImmutableArray<ProcessControlSafePoint> SafePoints { get; }

    /// <summary>Canonical exact affinity-binding observations ordered by stable semantic slot.</summary>
    public ImmutableArray<ProcessAttemptAffinityObservation> AffinityBindings { get; }

    /// <summary>Minimal command-linked evidence when this attempt is closed.</summary>
    public ProcessAttemptClosure? Closure { get; }

    /// <summary>Activation in flight, or <see langword="null"/> outside an activation.</summary>
    [JsonIgnore]
    public ActivationId? ActiveActivationId => ActiveActivation?.ActivationId;

    /// <summary>Latest safe-point evidence retained for replay, when available.</summary>
    [JsonIgnore]
    public ProcessControlSafePoint? LastSafePoint => SafePoints.IsEmpty ? null : SafePoints[^1];

    /// <summary>Explicit UTC closure time for a closed attempt.</summary>
    [JsonIgnore]
    public DateTimeOffset? EndedAtUtc => Closure?.OccurredAtUtc;

    /// <summary>Finds a bound affinity by stable Process slot.</summary>
    /// <param name="slot">Stable affinity slot to inspect.</param>
    /// <returns>The bound affinity, or <see langword="null"/> when the slot is unbound.</returns>
    /// <exception cref="ArgumentException"><paramref name="slot"/> is default.</exception>
    public ProcessAttemptAffinity? FindAffinity(ExecutionNodeId slot)
    {
        if (string.IsNullOrWhiteSpace(slot.Value))
        {
            throw new ArgumentException("An affinity lookup requires a stable slot.", nameof(slot));
        }

        var index = CanonicalDocumentCollections.BinarySearchIndex(
            AffinityBindings,
            slot,
            static (candidate, requested) =>
                StringComparer.Ordinal.Compare(candidate.Affinity.Slot.Value, requested.Value));
        return index >= 0 ? AffinityBindings[index].Affinity : null;
    }

    internal ProcessAttemptAffinityObservation? FindAffinityBinding(ExecutionNodeId slot)
    {
        var index = CanonicalDocumentCollections.BinarySearchIndex(
            AffinityBindings,
            slot,
            static (candidate, requested) =>
                StringComparer.Ordinal.Compare(candidate.Affinity.Slot.Value, requested.Value));
        return index >= 0 ? AffinityBindings[index] : null;
    }

    /// <summary>Compares attempt state by complete structural semantic value.</summary>
    /// <param name="other">Attempt state to compare.</param>
    /// <returns><see langword="true"/> when every field and chronological collection is equal.</returns>
    public bool Equals(ProcessControlAttemptState? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && AttemptId == other.AttemptId
        && StartedAtUtc == other.StartedAtUtc
        && Disposition == other.Disposition
        && Phase == other.Phase
        && Equals(ActiveActivation, other.ActiveActivation)
        && SafePoints.SequenceEqual(other.SafePoints)
        && AffinityBindings.SequenceEqual(other.AffinityBindings)
        && Equals(Closure, other.Closure);

    /// <summary>Returns a structural hash over complete attempt state.</summary>
    /// <returns>A hash code derived from every scalar and chronological collection entry.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(AttemptId);
        hash.Add(StartedAtUtc);
        hash.Add(Disposition);
        hash.Add(Phase);
        hash.Add(ActiveActivation);
        foreach (var safePoint in SafePoints)
        {
            hash.Add(safePoint);
        }

        foreach (var binding in AffinityBindings)
        {
            hash.Add(binding);
        }

        hash.Add(Closure);
        return hash.ToHashCode();
    }

    static ImmutableArray<ProcessControlSafePoint> NormalizeSafePoints(
        ProcessAttemptId attemptId,
        DateTimeOffset startedAtUtc,
        ImmutableArray<ProcessControlSafePoint> safePoints)
    {
        if (safePoints.IsDefaultOrEmpty)
        {
            return [];
        }

        HashSet<ProcessSafePointId> identities = [];
        HashSet<ActivationId> activations = [];
        var priorCut = startedAtUtc;
        ProcessControlRevision? priorCutRevision = null;
        foreach (var safePoint in safePoints)
        {
            if (safePoint is null)
            {
                throw new ArgumentException("Safe-point history cannot contain null entries.", nameof(safePoints));
            }

            if (safePoint.Activation.Expectation.Continuation.ProcessAttemptId != attemptId)
            {
                throw new ArgumentException("Safe-point activation evidence addresses another attempt.", nameof(safePoints));
            }

            if (!identities.Add(safePoint.SafePointId))
            {
                throw new ArgumentException($"Safe-point identity '{safePoint.SafePointId}' is duplicated.", nameof(safePoints));
            }

            if (!activations.Add(safePoint.ActivationId))
            {
                throw new ArgumentException($"Activation identity '{safePoint.ActivationId}' is duplicated.", nameof(safePoints));
            }

            if (safePoint.Activation.ObservedAtUtc < priorCut)
            {
                throw new ArgumentException("Safe-point history violates activation chronology.", nameof(safePoints));
            }

            if (priorCutRevision is { } prior
                && safePoint.Activation.Expectation.Revision.Ordinal <= prior.Ordinal)
            {
                throw new ArgumentException("Safe-point history violates fence chronology.", nameof(safePoints));
            }
            priorCut = safePoint.ObservedAtUtc;
            priorCutRevision = safePoint.Observation.Expectation.Revision;
        }
        return safePoints;
    }

    static ImmutableArray<ProcessAttemptAffinityObservation> NormalizeAffinityBindings(
        ProcessAttemptId attemptId,
        DateTimeOffset startedAtUtc,
        ImmutableArray<ProcessAttemptAffinityObservation> bindings)
    {
        if (bindings.IsDefaultOrEmpty)
        {
            return [];
        }

        HashSet<ExecutionNodeId> slots = [];
        foreach (var binding in bindings)
        {
            if (binding is null)
            {
                throw new ArgumentException("Attempt affinity bindings cannot contain null entries.", nameof(bindings));
            }

            if (binding.Expectation.Continuation.ProcessAttemptId != attemptId)
            {
                throw new ArgumentException("Attempt affinity binding addresses another attempt.", nameof(bindings));
            }

            if (binding.ObservedAtUtc < startedAtUtc)
            {
                throw new ArgumentException("Attempt affinity binding predates its attempt.", nameof(bindings));
            }

            if (!slots.Add(binding.Affinity.Slot))
            {
                throw new ArgumentException(
                    $"Attempt affinity slot '{binding.Affinity.Slot}' is duplicated.",
                    nameof(bindings));
            }
        }

        return CanonicalDocumentCollections.SortIfNeeded(
            bindings,
            static (left, right) =>
                StringComparer.Ordinal.Compare(left.Affinity.Slot.Value, right.Affinity.Slot.Value));
    }
}

/// <summary>Durable replay receipt for one accepted or typed no-op Process control command.</summary>
public sealed record ProcessControlCommandReceipt
{
    /// <summary>Creates one durable command receipt.</summary>
    /// <param name="command">Exact canonical command whose decision is retained.</param>
    /// <param name="disposition">Original accepted decision.</param>
    /// <param name="recordedAtUtc">Explicit UTC receipt time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is invalid.</exception>
    /// <exception cref="ArgumentException">
    /// Command kind, expectation, restart plan, or timestamp evidence is inconsistent.
    /// </exception>
    /// <exception cref="OverflowException">
    /// An applied command's expected successor revision cannot be represented.
    /// </exception>
    [JsonConstructor]
    public ProcessControlCommandReceipt(
        ProcessControlCommand command,
        ProcessControlReceiptDisposition disposition,
        DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.EnsureDeclaredVariant();
        if (command.SchemaVersion != ProcessControlCommand.CurrentSchemaVersion)
        {
            throw new ArgumentException("A command receipt requires the current command schema version.", nameof(command));
        }

        if (!Enum.IsDefined(disposition) || disposition == ProcessControlReceiptDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Command receipt disposition must be explicit.");
        }

        var dispositionMatches = (command, disposition) switch
        {
            (SignalProcessCommand, ProcessControlReceiptDisposition.SignalAccepted
                or ProcessControlReceiptDisposition.SignalBuffered
                or ProcessControlReceiptDisposition.SignalDuplicate) => true,
            (PauseProcessCommand, ProcessControlReceiptDisposition.Applied
                or ProcessControlReceiptDisposition.DeferredToSafePoint
                or ProcessControlReceiptDisposition.AlreadySatisfied
                or ProcessControlReceiptDisposition.AlreadyRequested) => true,
            (ContinueProcessCommand, ProcessControlReceiptDisposition.Applied
                or ProcessControlReceiptDisposition.AlreadySatisfied) => true,
            (RestartProcessAttemptCommand, ProcessControlReceiptDisposition.Applied
                or ProcessControlReceiptDisposition.DeferredToSafePoint) => true,
            (CancelProcessCommand, ProcessControlReceiptDisposition.Applied
                or ProcessControlReceiptDisposition.DeferredToSafePoint
                or ProcessControlReceiptDisposition.AlreadySatisfied
                or ProcessControlReceiptDisposition.AlreadyRequested) => true,
            (TerminateProcessCommand, ProcessControlReceiptDisposition.Applied
                or ProcessControlReceiptDisposition.AlreadySatisfied) => true,
            _ => false
        };
        if (!dispositionMatches)
        {
            throw new ArgumentException("Command kind and receipt disposition contradict each other.", nameof(disposition));
        }

        if (command.Expectation is null)
        {
            throw new ArgumentException("A receipted Process-control command requires an expectation.", nameof(command));
        }

        if (command is RestartProcessAttemptCommand restart
            && restart.Plan.NewAttemptId == command.Expectation.Continuation.ProcessAttemptId)
        {
            throw new ArgumentException("A restart receipt requires a distinct replacement attempt.", nameof(command));
        }
        ExecutionObservationRequirements.RequireUtc(recordedAtUtc, nameof(recordedAtUtc));
        if (recordedAtUtc < command.Context.IssuedAtUtc)
        {
            throw new ArgumentException("A command receipt cannot precede command issuance.", nameof(recordedAtUtc));
        }

        Command = command;
        Disposition = disposition;
        RecordedAtUtc = recordedAtUtc;
        _ = AfterRevision;
    }

    /// <summary>Exact canonical command whose decision is retained.</summary>
    public ProcessControlCommand Command { get; }

    /// <summary>Original accepted command decision.</summary>
    public ProcessControlReceiptDisposition Disposition { get; }

    /// <summary>Semantic revision observed before the decision.</summary>
    [JsonIgnore]
    public ProcessControlRevision BeforeRevision => Command.Expectation!.Revision;

    /// <summary>Semantic revision after the decision.</summary>
    [JsonIgnore]
    public ProcessControlRevision AfterRevision =>
        Disposition is ProcessControlReceiptDisposition.Applied
            or ProcessControlReceiptDisposition.DeferredToSafePoint
            ? BeforeRevision.Next()
            : BeforeRevision;

    /// <summary>Attempt observed before the decision.</summary>
    [JsonIgnore]
    public ProcessAttemptId BeforeAttemptId => Command.Expectation!.Continuation.ProcessAttemptId;

    /// <summary>Attempt current after the decision.</summary>
    [JsonIgnore]
    public ProcessAttemptId AfterAttemptId =>
        Command is RestartProcessAttemptCommand restart
            && Disposition == ProcessControlReceiptDisposition.Applied
            ? restart.Plan.NewAttemptId
            : BeforeAttemptId;

    /// <summary>Explicit UTC receipt time.</summary>
    public DateTimeOffset RecordedAtUtc { get; }
}

/// <summary>Reference-protocol projection that one canonical Signal was admitted exactly once.</summary>
public sealed record ProcessSignalAdmission
{
    /// <summary>Creates Signal-admission evidence projected from an authoritative accepted receipt.</summary>
    /// <param name="commandId">Control command that first admitted the Signal.</param>
    /// <param name="signal">Exact canonical Signal admitted.</param>
    /// <param name="disposition">Active or buffered admission disposition.</param>
    /// <param name="admittedAtUtc">Explicit UTC admission time.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="commandId"/> is default or <paramref name="admittedAtUtc"/> is not UTC.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="signal"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is invalid.</exception>
    [JsonConstructor]
    public ProcessSignalAdmission(
        ProcessControlCommandId commandId,
        SignalEnvelope signal,
        ProcessSignalAdmissionDisposition disposition,
        DateTimeOffset admittedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(commandId.Value))
        {
            throw new ArgumentException("Signal admission requires its control command.", nameof(commandId));
        }

        if (!Enum.IsDefined(disposition) || disposition == ProcessSignalAdmissionDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Signal disposition must be explicit.");
        }

        ExecutionObservationRequirements.RequireUtc(admittedAtUtc, nameof(admittedAtUtc));

        CommandId = commandId;
        Signal = Guard.RequireNotNull(signal);
        Disposition = disposition;
        AdmittedAtUtc = admittedAtUtc;
    }

    /// <summary>Control command that first admitted the Signal.</summary>
    public ProcessControlCommandId CommandId { get; }

    /// <summary>Exact canonical Signal admitted.</summary>
    public SignalEnvelope Signal { get; }

    /// <summary>Active or buffered admission disposition.</summary>
    public ProcessSignalAdmissionDisposition Disposition { get; }

    /// <summary>Explicit UTC admission time.</summary>
    public DateTimeOffset AdmittedAtUtc { get; }
}

/// <summary>Versioned portable reference state for protocol-neutral Process lifecycle control.</summary>
/// <remarks>
/// This value is a deterministic semantic state, not a claim that an atomic Storage checkpoint, CAS record,
/// inbox, or worker fence already exists. Construction deliberately validates the complete retained history;
/// ARI-166 and ARI-168 realize physical cuts and indexed runtime access paths.
/// </remarks>
public sealed record ProcessControlState
{
    /// <summary>Current canonical Process-control state schema version.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-process-control-state/v1");

    /// <summary>Creates complete persisted Process-control state.</summary>
    /// <param name="schemaVersion">Exact control-state schema version.</param>
    /// <param name="definition">Exact pinned Process definition revision and fingerprint.</param>
    /// <param name="authorityScope">Authority and optional tenant boundary governing the Process.</param>
    /// <param name="processInstanceId">Logical Process instance across all attempts.</param>
    /// <param name="revision">Current semantic control revision and optimistic fence.</param>
    /// <param name="mode">Current lifecycle-control mode.</param>
    /// <param name="attempts">Non-empty ordered attempt lineage.</param>
    /// <param name="pendingCommandId">Command awaiting an invariant-preserving safe point.</param>
    /// <param name="receipts">Chronological accepted command receipts.</param>
    /// <param name="createdAtUtc">Explicit UTC control-state creation time.</param>
    /// <param name="updatedAtUtc">Latest explicit UTC observation retained by the state.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="authorityScope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Schema, identity, lineage, pending action, receipt, Signal, revision, or timestamp invariants are violated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is invalid.</exception>
    [JsonConstructor]
    public ProcessControlState(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionReference definition,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId,
        ProcessControlRevision revision,
        ProcessControlMode mode,
        ImmutableArray<ProcessControlAttemptState> attempts,
        ProcessControlCommandId? pendingCommandId,
        ImmutableArray<ProcessControlCommandReceipt> receipts,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported Process-control state schema version.", nameof(schemaVersion));
        }

        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
        {
            throw new ArgumentException("Process-control state requires a stable Process instance.", nameof(processInstanceId));
        }

        if (string.IsNullOrWhiteSpace(revision.Value))
        {
            throw new ArgumentException("Process-control state requires a semantic revision.", nameof(revision));
        }

        if (!Enum.IsDefined(mode) || mode == ProcessControlMode.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Process-control mode must be explicit.");
        }

        if (attempts.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Process-control state requires a non-empty attempt lineage.", nameof(attempts));
        }

        if (receipts.IsDefault)
        {
            throw new ArgumentException("Process-control receipts must be initialized.", nameof(receipts));
        }

        ExecutionObservationRequirements.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        ExecutionObservationRequirements.RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Control state cannot be updated before it is created.", nameof(updatedAtUtc));
        }

        Definition = Guard.RequireNotNull(definition);
        AuthorityScope = Guard.RequireNotNull(authorityScope);
        SchemaVersion = schemaVersion;
        ProcessInstanceId = processInstanceId;
        Revision = revision;
        Mode = mode;
        Attempts = attempts;
        PendingCommandId = pendingCommandId;
        Receipts = receipts;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;

        var attemptsById = ValidateLineage();
        var receiptsByCommand = ValidateReceipts(attemptsById);
        ValidateClosures(receiptsByCommand);
        ValidatePending(receiptsByCommand);
        ValidateDeferredReceipts();
        ValidateRevisionReachability();
        ValidateTerminalRevision(receiptsByCommand);
        ValidateLatestEvidence();
    }

    /// <summary>Exact Process-control state schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Exact pinned Process definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Authority and optional tenant boundary governing the Process.</summary>
    public InteractionAuthorityScope AuthorityScope { get; }

    /// <summary>Logical Process instance across all attempts.</summary>
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Current semantic control revision and optimistic fence.</summary>
    public ProcessControlRevision Revision { get; }

    /// <summary>Current lifecycle-control mode.</summary>
    public ProcessControlMode Mode { get; }

    /// <summary>Ordered attempt lineage; the final entry is current or terminal.</summary>
    public ImmutableArray<ProcessControlAttemptState> Attempts { get; }

    /// <summary>Command awaiting a safe point, or <see langword="null"/> when none is pending.</summary>
    public ProcessControlCommandId? PendingCommandId { get; }

    /// <summary>Chronological accepted command receipts used for deterministic replay.</summary>
    public ImmutableArray<ProcessControlCommandReceipt> Receipts { get; }

    /// <summary>
    /// Gets a chronological value snapshot of Signal admissions derived from authoritative accepted receipts.
    /// </summary>
    /// <remarks>Each access may produce new structurally equal records; reference identity is not stable.</remarks>
    [JsonIgnore]
    public ImmutableArray<ProcessSignalAdmission> SignalAdmissions => DeriveSignalAdmissions();

    /// <summary>Explicit UTC control-state creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Latest explicit UTC observation retained by the state.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Final current or terminal attempt in the lineage.</summary>
    [JsonIgnore]
    public ProcessControlAttemptState CurrentAttempt => Attempts[^1];

    /// <summary>Whether lifecycle control is irreversibly terminal.</summary>
    [JsonIgnore]
    public bool IsTerminal => Mode is ProcessControlMode.Cancelled or ProcessControlMode.Terminated;

    /// <summary>Creates initial running control state for one exact Process attempt.</summary>
    /// <param name="definition">Exact pinned Process definition.</param>
    /// <param name="authorityScope">Authority and optional tenant boundary.</param>
    /// <param name="processInstanceId">Logical Process instance.</param>
    /// <param name="processAttemptId">Initial stable Process attempt.</param>
    /// <param name="createdAtUtc">Explicit UTC creation time.</param>
    /// <returns>Initial running state at the first invariant-preserving ready boundary.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="authorityScope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity or timestamp is invalid.</exception>
    public static ProcessControlState Create(
        ExecutionDefinitionReference definition,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId,
        ProcessAttemptId processAttemptId,
        DateTimeOffset createdAtUtc)
    {
        ExecutionObservationRequirements.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        return new(
            CurrentSchemaVersion,
            definition,
            authorityScope,
            processInstanceId,
            ProcessControlRevision.Initial,
            ProcessControlMode.Running,
            [new(
                processAttemptId,
                createdAtUtc,
                ProcessControlAttemptDisposition.Current,
                ProcessControlExecutionPhase.Ready)],
            pendingCommandId: null,
            receipts: [],
            createdAtUtc,
            createdAtUtc);
    }

    /// <summary>Finds a receipt by stable command identity.</summary>
    /// <param name="commandId">Stable command identity to inspect.</param>
    /// <returns>The prior receipt, or <see langword="null"/> when the command is unknown.</returns>
    /// <exception cref="ArgumentException"><paramref name="commandId"/> is default.</exception>
    public ProcessControlCommandReceipt? FindReceipt(ProcessControlCommandId commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId.Value))
        {
            throw new ArgumentException("A receipt lookup requires a stable command identity.", nameof(commandId));
        }

        foreach (var receipt in Receipts)
        {
            if (receipt.Command.Context.CommandId == commandId)
            {
                return receipt;
            }
        }
        return null;
    }

    ProcessControlAttemptState? FindAttempt(ProcessAttemptId attemptId)
    {
        foreach (var attempt in Attempts)
        {
            if (attempt.AttemptId == attemptId)
            {
                return attempt;
            }
        }
        return null;
    }

    /// <summary>Compares control state by complete persisted semantic value.</summary>
    /// <param name="other">Control state to compare.</param>
    /// <returns><see langword="true"/> when every scalar and chronological collection is equal.</returns>
    public bool Equals(ProcessControlState? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Equals(Definition, other.Definition)
        && Equals(AuthorityScope, other.AuthorityScope)
        && ProcessInstanceId == other.ProcessInstanceId
        && Revision == other.Revision
        && Mode == other.Mode
        && Attempts.SequenceEqual(other.Attempts)
        && PendingCommandId == other.PendingCommandId
        && Receipts.SequenceEqual(other.Receipts)
        && CreatedAtUtc == other.CreatedAtUtc
        && UpdatedAtUtc == other.UpdatedAtUtc;

    /// <summary>Returns a structural hash over complete persisted control state.</summary>
    /// <returns>A hash code derived from every scalar and chronological collection entry.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Definition);
        hash.Add(AuthorityScope);
        hash.Add(ProcessInstanceId);
        hash.Add(Revision);
        hash.Add(Mode);
        foreach (var attempt in Attempts)
        {
            hash.Add(attempt);
        }

        hash.Add(PendingCommandId);
        foreach (var receipt in Receipts)
        {
            hash.Add(receipt);
        }

        hash.Add(CreatedAtUtc);
        hash.Add(UpdatedAtUtc);
        return hash.ToHashCode();
    }

    Dictionary<ProcessAttemptId, ProcessControlAttemptState> ValidateLineage()
    {
        Dictionary<ProcessAttemptId, ProcessControlAttemptState> attemptsById = [];
        for (var index = 0; index < Attempts.Length; index++)
        {
            var attempt = Attempts[index]
                ?? throw new ArgumentException("Attempt lineage cannot contain null entries.", nameof(Attempts));
            if (!attemptsById.TryAdd(attempt.AttemptId, attempt))
            {
                throw new ArgumentException($"Attempt '{attempt.AttemptId}' is duplicated.", nameof(Attempts));
            }

            if (attempt.StartedAtUtc < CreatedAtUtc || attempt.StartedAtUtc > UpdatedAtUtc)
            {
                throw new ArgumentException("Attempt time is outside control-state chronology.", nameof(Attempts));
            }

            if (index == 0 && attempt.StartedAtUtc != CreatedAtUtc)
            {
                throw new ArgumentException("The initial attempt must begin at control-state creation.", nameof(Attempts));
            }

            if (index > 0 && attempt.StartedAtUtc != Attempts[index - 1].EndedAtUtc)
            {
                throw new ArgumentException("A replacement attempt must start at its abandonment cut.", nameof(Attempts));
            }

            if (attempt.ActiveActivation is { } active
                && active.Expectation.Continuation.ProcessInstanceId != ProcessInstanceId)
            {
                throw new ArgumentException("Active activation evidence targets another Process instance.", nameof(Attempts));
            }
            foreach (var safePoint in attempt.SafePoints)
            {
                if (safePoint.Activation.Expectation.Continuation.ProcessInstanceId != ProcessInstanceId)
                {
                    throw new ArgumentException("Safe-point evidence targets another Process instance.", nameof(Attempts));
                }
            }
            foreach (var binding in attempt.AffinityBindings)
            {
                if (binding.Expectation.Continuation.ProcessInstanceId != ProcessInstanceId)
                {
                    throw new ArgumentException("Affinity evidence targets another Process instance.", nameof(Attempts));
                }
            }
            if (attempt.Closure?.InterruptedActivation is { } interrupted
                && interrupted.Expectation.Continuation.ProcessInstanceId != ProcessInstanceId)
            {
                throw new ArgumentException("Interrupted activation targets another Process instance.", nameof(Attempts));
            }

            if (index < Attempts.Length - 1)
            {
                if (attempt.Disposition != ProcessControlAttemptDisposition.Abandoned)
                {
                    throw new ArgumentException(
                        "Every prior attempt must be abandoned for the immediately following replacement.",
                        nameof(Attempts));
                }
            }
        }

        var expectedFinalDisposition = Mode switch
        {
            ProcessControlMode.Cancelled => ProcessControlAttemptDisposition.Cancelled,
            ProcessControlMode.Terminated => ProcessControlAttemptDisposition.Terminated,
            _ => ProcessControlAttemptDisposition.Current
        };
        if (CurrentAttempt.Disposition != expectedFinalDisposition)
        {
            throw new ArgumentException("Final attempt disposition contradicts control mode.", nameof(Attempts));
        }

        if (Mode == ProcessControlMode.Paused
            && CurrentAttempt.Phase is not (ProcessControlExecutionPhase.Ready or ProcessControlExecutionPhase.AtSafePoint))
        {
            throw new ArgumentException("A paused Process must be held at an invariant-preserving boundary.", nameof(Mode));
        }
        if ((Mode is ProcessControlMode.PauseRequested
                or ProcessControlMode.RestartRequested
                or ProcessControlMode.CancellationRequested)
            && CurrentAttempt.Phase != ProcessControlExecutionPhase.InActivation)
        {
            throw new ArgumentException("A pending safe-point action requires an activation in flight.", nameof(Mode));
        }

        return attemptsById;
    }

    Dictionary<ProcessControlCommandId, ProcessControlCommandReceipt> ValidateReceipts(
        Dictionary<ProcessAttemptId, ProcessControlAttemptState> attemptsById)
    {
        Dictionary<ProcessControlCommandId, ProcessControlCommandReceipt> commands = [];
        HashSet<ProcessControlIdempotencyKey> idempotencyKeys = [];
        Dictionary<EmissionId, SignalEnvelope> signalEmissions = [];
        Dictionary<(SignalContractReference Contract, InteractionIdempotencyKey Key), SignalEnvelope> logicalSignals = [];
        HashSet<ProcessAttemptId> observedClosureCuts = [];
        ProcessControlRevision? priorAfter = null;
        DateTimeOffset priorTime = CreatedAtUtc;
        foreach (var receipt in Receipts)
        {
            if (receipt is null)
            {
                throw new ArgumentException("Command receipts cannot contain null entries.", nameof(Receipts));
            }

            if (!commands.TryAdd(receipt.Command.Context.CommandId, receipt))
            {
                throw new ArgumentException("Command receipt identities must be unique.", nameof(Receipts));
            }

            if (!idempotencyKeys.Add(receipt.Command.Context.IdempotencyKey))
            {
                throw new ArgumentException("Command receipt idempotency keys must be unique.", nameof(Receipts));
            }

            if (receipt.Command.Context.ProcessInstanceId != ProcessInstanceId)
            {
                throw new ArgumentException("A command receipt targets another Process instance.", nameof(Receipts));
            }

            if (receipt.Command.Context.Authorization.AuthorityScope != AuthorityScope)
            {
                throw new ArgumentException("A command receipt carries another authority scope.", nameof(Receipts));
            }

            if (!attemptsById.TryGetValue(receipt.BeforeAttemptId, out var beforeAttempt)
                || !attemptsById.TryGetValue(receipt.AfterAttemptId, out var afterAttempt))
            {
                throw new ArgumentException("A command receipt references an unknown attempt.", nameof(Receipts));
            }
            if (receipt.Command is RestartProcessAttemptCommand restart)
            {
                if (!beforeAttempt.AffinityBindings.IsEmpty
                    && restart.Plan.Cleanup
                        != ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources)
                {
                    throw new ArgumentException(
                        "An affinity-bearing restart receipt requires explicit affinity cleanup.",
                        nameof(Receipts));
                }
                if (attemptsById.ContainsKey(restart.Plan.NewAttemptId)
                    && beforeAttempt.Closure?.CommandId != receipt.Command.Context.CommandId)
                {
                    throw new ArgumentException(
                        "An unrealized restart receipt cannot select an attempt identity already present in lineage.",
                        nameof(Receipts));
                }
            }
            if (receipt.RecordedAtUtc < beforeAttempt.StartedAtUtc)
            {
                throw new ArgumentException("A command receipt predates its current attempt.", nameof(Receipts));
            }

            if (beforeAttempt.EndedAtUtc is { } ended)
            {
                var isCausalClosure = receipt.Command.Context.CommandId == beforeAttempt.Closure?.CommandId;
                var isTerminalNoOp = IsAllowedTerminalNoOp(beforeAttempt, receipt);
                var closureWasDeferred = beforeAttempt.Closure is { } closure
                    && commands.TryGetValue(closure.CommandId, out var closureReceipt)
                    && closureReceipt.Disposition == ProcessControlReceiptDisposition.DeferredToSafePoint;
                var deferredCutRevision = closureWasDeferred
                    ? beforeAttempt.LastSafePoint?.Observation.Expectation.Revision
                    : null;
                if (isCausalClosure
                    && receipt.Disposition == ProcessControlReceiptDisposition.Applied
                    && receipt.RecordedAtUtc == ended)
                {
                    observedClosureCuts.Add(beforeAttempt.AttemptId);
                }
                if (isTerminalNoOp
                    && (receipt.RecordedAtUtc < ended
                        || (receipt.RecordedAtUtc == ended
                            && (closureWasDeferred
                                ? deferredCutRevision is null
                                    || receipt.BeforeRevision.Ordinal <= deferredCutRevision.Value.Ordinal
                                : !observedClosureCuts.Contains(beforeAttempt.AttemptId)))))
                {
                    throw new ArgumentException(
                        "A terminal typed no-op requires the preceding terminal cut.",
                        nameof(Receipts));
                }
                if (receipt.RecordedAtUtc > ended && !isTerminalNoOp)
                {
                    throw new ArgumentException(
                        "A command receipt follows the lifetime of its current attempt.",
                        nameof(Receipts));
                }
                if (receipt.RecordedAtUtc == ended
                    && !isCausalClosure
                    && !isTerminalNoOp
                    && (closureWasDeferred
                        ? deferredCutRevision is null
                            || receipt.AfterRevision.Ordinal > deferredCutRevision.Value.Ordinal
                        : observedClosureCuts.Contains(beforeAttempt.AttemptId)))
                {
                    throw new ArgumentException(
                        "Only the causal closing command may be recorded at an attempt closure cut.",
                        nameof(Receipts));
                }
            }
            if (receipt.AfterAttemptId != receipt.BeforeAttemptId
                && afterAttempt.StartedAtUtc != receipt.RecordedAtUtc)
            {
                throw new ArgumentException(
                    "A replacement attempt must begin at its applied restart receipt.",
                    nameof(Receipts));
            }
            if (receipt.Command is SignalProcessCommand signalCommand)
            {
                var signal = signalCommand.Signal;
                if (signal.Context.AuthorityScope != AuthorityScope)
                {
                    throw new ArgumentException("A Signal receipt carries another authority scope.", nameof(Receipts));
                }

                if (signal.Context.Delivery.Durability != InteractionDurabilityDemand.Durable)
                {
                    throw new ArgumentException("A Signal receipt requires durable delivery semantics.", nameof(Receipts));
                }

                if (signal.Target is not ProcessTokenInteractionTarget target
                    || target.Continuation.ProcessInstanceId != ProcessInstanceId
                    || target.Continuation.ProcessAttemptId != receipt.BeforeAttemptId)
                {
                    throw new ArgumentException("A Signal receipt targets another Process attempt.", nameof(Receipts));
                }

                var logicalKey = (signal.Contract, signal.Context.IdempotencyKey);
                signalEmissions.TryGetValue(signal.Context.EmissionId, out var priorEmission);
                logicalSignals.TryGetValue(logicalKey, out var priorLogical);
                if (receipt.Disposition == ProcessControlReceiptDisposition.SignalDuplicate)
                {
                    if ((priorEmission is null && priorLogical is null)
                        || (priorEmission is not null && priorEmission != signal)
                        || (priorLogical is not null && priorLogical != signal))
                    {
                        throw new ArgumentException("A duplicate Signal receipt requires exact prior admission.", nameof(Receipts));
                    }
                }
                else
                {
                    if (priorEmission is not null || priorLogical is not null)
                    {
                        throw new ArgumentException("A logical Signal can be admitted only once.", nameof(Receipts));
                    }

                    signalEmissions.Add(signal.Context.EmissionId, signal);
                    logicalSignals.Add(logicalKey, signal);
                }
            }
            if (receipt.RecordedAtUtc < priorTime || receipt.RecordedAtUtc > UpdatedAtUtc)
            {
                throw new ArgumentException("Command receipts must be chronological.", nameof(Receipts));
            }

            if (priorAfter is { } previous && receipt.BeforeRevision.Ordinal < previous.Ordinal)
            {
                throw new ArgumentException("Command receipt revisions must be monotonic.", nameof(Receipts));
            }

            if (receipt.AfterRevision.Ordinal > Revision.Ordinal)
            {
                throw new ArgumentException("A command receipt cannot exceed current control revision.", nameof(Receipts));
            }

            priorAfter = receipt.AfterRevision;
            priorTime = receipt.RecordedAtUtc;
        }

        return commands;
    }

    static bool IsAllowedTerminalNoOp(
        ProcessControlAttemptState attempt,
        ProcessControlCommandReceipt receipt) =>
        (attempt.Disposition, receipt.Command, receipt.Disposition) switch
        {
            (ProcessControlAttemptDisposition.Cancelled, CancelProcessCommand,
                ProcessControlReceiptDisposition.AlreadySatisfied) => true,
            (ProcessControlAttemptDisposition.Terminated, TerminateProcessCommand,
                ProcessControlReceiptDisposition.AlreadySatisfied) => true,
            _ => false
        };

    void ValidateClosures(
        Dictionary<ProcessControlCommandId, ProcessControlCommandReceipt> receiptsByCommand)
    {
        for (var index = 0; index < Attempts.Length; index++)
        {
            var attempt = Attempts[index];
            if (attempt.Closure is not { } closure)
            {
                continue;
            }

            if (!receiptsByCommand.TryGetValue(closure.CommandId, out var receipt))
            {
                throw new ArgumentException("Attempt closure has no durable command receipt.", nameof(Attempts));
            }

            if (receipt.RecordedAtUtc > closure.OccurredAtUtc
                || receipt.BeforeAttemptId != attempt.AttemptId)
            {
                throw new ArgumentException("Attempt closure contradicts its command chronology.", nameof(Attempts));
            }

            var matches = (attempt.Disposition, receipt.Command, receipt.Disposition) switch
            {
                (ProcessControlAttemptDisposition.Abandoned, RestartProcessAttemptCommand,
                    ProcessControlReceiptDisposition.Applied
                        or ProcessControlReceiptDisposition.DeferredToSafePoint) => true,
                (ProcessControlAttemptDisposition.Cancelled, CancelProcessCommand,
                    ProcessControlReceiptDisposition.Applied
                        or ProcessControlReceiptDisposition.DeferredToSafePoint) => true,
                (ProcessControlAttemptDisposition.Terminated, TerminateProcessCommand,
                    ProcessControlReceiptDisposition.Applied) => true,
                _ => false
            };
            if (!matches)
            {
                throw new ArgumentException("Attempt closure contradicts its causal command receipt.", nameof(Attempts));
            }

            if (receipt.Disposition == ProcessControlReceiptDisposition.Applied
                && receipt.RecordedAtUtc != closure.OccurredAtUtc)
            {
                throw new ArgumentException("An applied command must close its attempt at the receipt cut.", nameof(Attempts));
            }
            if (receipt.Disposition == ProcessControlReceiptDisposition.DeferredToSafePoint
                && attempt.LastSafePoint?.ObservedAtUtc != closure.OccurredAtUtc)
            {
                throw new ArgumentException(
                    "A deferred command must close its attempt at the resolving safe point.",
                    nameof(Attempts));
            }

            if (attempt.Disposition == ProcessControlAttemptDisposition.Abandoned)
            {
                if (index + 1 >= Attempts.Length
                    || receipt.Command is not RestartProcessAttemptCommand restart
                    || restart.Plan.NewAttemptId != Attempts[index + 1].AttemptId
                    || (receipt.Disposition == ProcessControlReceiptDisposition.Applied
                        && receipt.AfterAttemptId != Attempts[index + 1].AttemptId))
                {
                    throw new ArgumentException(
                        "Attempt abandonment must select the immediately following replacement.",
                        nameof(Attempts));
                }
                if (!attempt.AffinityBindings.IsEmpty
                    && restart.Plan.Cleanup
                        != ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources)
                {
                    throw new ArgumentException(
                        "An affinity-bearing abandoned attempt requires explicit affinity cleanup.",
                        nameof(Attempts));
                }
            }
            if (attempt.Disposition == ProcessControlAttemptDisposition.Terminated
                && receipt.Command is TerminateProcessCommand terminate
                && !attempt.AffinityBindings.IsEmpty
                && terminate.Cleanup != ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources)
            {
                throw new ArgumentException(
                    "An affinity-bearing terminated attempt requires explicit affinity cleanup.",
                    nameof(Attempts));
            }
        }

        foreach (var receipt in Receipts)
        {
            if (receipt.Disposition != ProcessControlReceiptDisposition.Applied
                || receipt.Command is not (RestartProcessAttemptCommand
                    or CancelProcessCommand
                    or TerminateProcessCommand))
            {
                continue;
            }
            var attempt = FindAttempt(receipt.BeforeAttemptId);
            if (attempt?.Closure?.CommandId != receipt.Command.Context.CommandId)
            {
                throw new ArgumentException(
                    "An applied closing command requires its exact attempt-lineage effect.",
                    nameof(Receipts));
            }
        }
    }

    ImmutableArray<ProcessSignalAdmission> DeriveSignalAdmissions()
    {
        var count = 0;
        foreach (var receipt in Receipts)
        {
            if (receipt.Disposition is ProcessControlReceiptDisposition.SignalAccepted
                or ProcessControlReceiptDisposition.SignalBuffered)
            {
                count++;
            }
        }
        if (count == 0)
        {
            return [];
        }

        var admissions = ImmutableArray.CreateBuilder<ProcessSignalAdmission>(count);
        foreach (var receipt in Receipts)
        {
            if (receipt.Command is not SignalProcessCommand signal)
            {
                continue;
            }

            var disposition = receipt.Disposition switch
            {
                ProcessControlReceiptDisposition.SignalAccepted => ProcessSignalAdmissionDisposition.Active,
                ProcessControlReceiptDisposition.SignalBuffered => ProcessSignalAdmissionDisposition.Buffered,
                _ => ProcessSignalAdmissionDisposition.Unspecified
            };
            if (disposition == ProcessSignalAdmissionDisposition.Unspecified)
            {
                continue;
            }

            admissions.Add(new(
                receipt.Command.Context.CommandId,
                signal.Signal,
                disposition,
                receipt.RecordedAtUtc));
        }
        return admissions.MoveToImmutable();
    }

    void ValidatePending(
        Dictionary<ProcessControlCommandId, ProcessControlCommandReceipt> receiptsByCommand)
    {
        var expectedAction = Mode switch
        {
            ProcessControlMode.PauseRequested => ProcessControlPendingAction.Pause,
            ProcessControlMode.RestartRequested => ProcessControlPendingAction.RestartAttempt,
            ProcessControlMode.CancellationRequested => ProcessControlPendingAction.Cancel,
            _ => ProcessControlPendingAction.None
        };
        if ((expectedAction != ProcessControlPendingAction.None) != PendingCommandId.HasValue)
        {
            throw new ArgumentException("Pending command presence contradicts control mode.", nameof(PendingCommandId));
        }

        if (PendingCommandId is not { } pending)
        {
            return;
        }

        if (!receiptsByCommand.TryGetValue(pending, out var receipt))
        {
            throw new ArgumentException("Pending command has no durable receipt.", nameof(PendingCommandId));
        }

        if (receipt.Disposition != ProcessControlReceiptDisposition.DeferredToSafePoint)
        {
            throw new ArgumentException("Pending command receipt must be deferred to a safe point.", nameof(PendingCommandId));
        }

        var active = CurrentAttempt.ActiveActivation
            ?? throw new ArgumentException("A pending command requires exact active activation evidence.", nameof(PendingCommandId));
        if (receipt.BeforeAttemptId != CurrentAttempt.AttemptId
            || receipt.AfterAttemptId != CurrentAttempt.AttemptId
            || receipt.RecordedAtUtc < active.ObservedAtUtc
            || receipt.BeforeRevision.Ordinal <= active.Expectation.Revision.Ordinal)
        {
            throw new ArgumentException(
                "Pending command evidence does not belong to the current active attempt.",
                nameof(PendingCommandId));
        }
        var matches = (expectedAction, receipt.Command) switch
        {
            (ProcessControlPendingAction.Pause, PauseProcessCommand) => true,
            (ProcessControlPendingAction.RestartAttempt, RestartProcessAttemptCommand) => true,
            (ProcessControlPendingAction.Cancel, CancelProcessCommand) => true,
            _ => false
        };
        if (!matches)
        {
            throw new ArgumentException("Pending command kind contradicts control mode.", nameof(PendingCommandId));
        }
    }

    void ValidateDeferredReceipts()
    {
        foreach (var receipt in Receipts)
        {
            if (receipt.Disposition != ProcessControlReceiptDisposition.DeferredToSafePoint)
            {
                continue;
            }

            if (PendingCommandId == receipt.Command.Context.CommandId)
            {
                continue;
            }

            var attempt = FindAttempt(receipt.BeforeAttemptId)
                ?? throw new ArgumentException("A deferred receipt targets an unknown attempt.", nameof(Receipts));
            if (attempt.Closure?.CommandId == receipt.Command.Context.CommandId)
            {
                continue;
            }

            if (attempt.Disposition == ProcessControlAttemptDisposition.Terminated
                && attempt.Closure is { OccurredAtUtc: var terminatedAt }
                && terminatedAt >= receipt.RecordedAtUtc)
            {
                continue;
            }
            if (receipt.Command is PauseProcessCommand
                && FindResolvingSafePoint(attempt, receipt) is not null)
            {
                continue;
            }

            throw new ArgumentException(
                "A deferred command receipt is neither pending, resolved, nor explicitly preempted.",
                nameof(Receipts));
        }
    }

    static ProcessControlSafePoint? FindResolvingSafePoint(
        ProcessControlAttemptState attempt,
        ProcessControlCommandReceipt receipt)
    {
        foreach (var safePoint in attempt.SafePoints)
        {
            if (safePoint.Activation.ObservedAtUtc <= receipt.RecordedAtUtc
                && receipt.RecordedAtUtc <= safePoint.ObservedAtUtc
                && safePoint.Activation.Expectation.Revision.Ordinal < receipt.BeforeRevision.Ordinal
                && receipt.AfterRevision.Ordinal <= safePoint.Observation.Expectation.Revision.Ordinal)
            {
                return safePoint;
            }
        }
        return null;
    }

    void ValidateTerminalRevision(
        Dictionary<ProcessControlCommandId, ProcessControlCommandReceipt> receiptsByCommand)
    {
        if (!IsTerminal)
        {
            return;
        }

        var closure = CurrentAttempt.Closure
            ?? throw new ArgumentException("Terminal state requires attempt-closure evidence.", nameof(Attempts));
        var receipt = receiptsByCommand[closure.CommandId];
        var expected = receipt.Disposition == ProcessControlReceiptDisposition.DeferredToSafePoint
            ? receipt.AfterRevision.Next()
            : receipt.AfterRevision;
        if (Revision != expected)
        {
            throw new ArgumentException("Terminal control revision must equal its causal durable cut.", nameof(Revision));
        }
    }

    void ValidateRevisionReachability()
    {
        Dictionary<long, object> steps = [];
        foreach (var receipt in Receipts)
        {
            if (receipt.Disposition is ProcessControlReceiptDisposition.Applied
                or ProcessControlReceiptDisposition.DeferredToSafePoint)
            {
                AddRevisionStep(steps, receipt.BeforeRevision, "command receipt", receipt);
            }
        }
        foreach (var attempt in Attempts)
        {
            foreach (var safePoint in attempt.SafePoints)
            {
                AddRevisionStep(
                    steps,
                    safePoint.Activation.Expectation.Revision,
                    "activation start",
                    safePoint.Activation);
                AddRevisionStep(
                    steps,
                    safePoint.Observation.Expectation.Revision,
                    "safe point",
                    safePoint);
            }
            foreach (var binding in attempt.AffinityBindings)
            {
                AddRevisionStep(steps, binding.Expectation.Revision, "affinity binding", binding);
            }

            if (attempt.ActiveActivation is { } active)
            {
                AddRevisionStep(steps, active.Expectation.Revision, "active activation start", active);
            }

            if (attempt.Closure?.InterruptedActivation is { } interrupted)
            {
                AddRevisionStep(
                    steps,
                    interrupted.Expectation.Revision,
                    "interrupted activation start",
                    interrupted);
            }
        }
        if (steps.Count != Revision.Ordinal - ProcessControlRevision.Initial.Ordinal)
        {
            throw new ArgumentException(
                "Control revision is not reachable from retained incrementing evidence.",
                nameof(Revision));
        }
        for (long ordinal = ProcessControlRevision.Initial.Ordinal; ordinal < Revision.Ordinal; ordinal++)
        {
            if (!steps.ContainsKey(ordinal))
            {
                throw new ArgumentException("Control revision history contains a missing durable step.", nameof(Revision));
            }
        }

        ValidateLifecycleHistory(steps);
    }

    void AddRevisionStep(
        Dictionary<long, object> steps,
        ProcessControlRevision before,
        string evidenceKind,
        object evidence)
    {
        if (before.Ordinal >= Revision.Ordinal || !steps.TryAdd(before.Ordinal, evidence))
        {
            throw new ArgumentException(
                $"The {evidenceKind} carries a duplicate or out-of-range revision step.",
                nameof(Revision));
        }
    }

    void ValidateLifecycleHistory(Dictionary<long, object> steps)
    {
        Dictionary<long, List<ProcessControlCommandReceipt>> receiptsByRevision = [];
        foreach (var receipt in Receipts)
        {
            if (!receiptsByRevision.TryGetValue(receipt.BeforeRevision.Ordinal, out var revisionReceipts))
            {
                revisionReceipts = [];
                receiptsByRevision.Add(receipt.BeforeRevision.Ordinal, revisionReceipts);
            }
            revisionReceipts.Add(receipt);
        }

        var position = new ProcessControlLifecycleSemantics.Position(
            ProcessControlMode.Running,
            ProcessControlExecutionPhase.Ready,
            Attempts[0].AttemptId);
        var evidenceTime = CreatedAtUtc;
        for (var ordinal = ProcessControlRevision.Initial.Ordinal; ordinal < Revision.Ordinal; ordinal++)
        {
            var step = steps[ordinal];
            var stepReceipt = step as ProcessControlCommandReceipt;
            var stepTime = RevisionEvidenceTime(step);
            if (receiptsByRevision.TryGetValue(ordinal, out var revisionReceipts))
            {
                foreach (var receipt in revisionReceipts)
                {
                    ValidateHistoricalReceipt(receipt, position);
                    if (ReferenceEquals(receipt, stepReceipt))
                    {
                        continue;
                    }

                    if (receipt.Disposition is ProcessControlReceiptDisposition.Applied
                        or ProcessControlReceiptDisposition.DeferredToSafePoint)
                    {
                        throw new ArgumentException(
                            "A revision can contain only its single authoritative lifecycle step.",
                            nameof(Receipts));
                    }
                    if (receipt.RecordedAtUtc < evidenceTime || receipt.RecordedAtUtc > stepTime)
                    {
                        throw new ArgumentException(
                            "A non-advancing receipt is outside its retained revision interval.",
                            nameof(Receipts));
                    }
                    evidenceTime = receipt.RecordedAtUtc;
                }
            }
            if (stepTime < evidenceTime)
            {
                throw new ArgumentException("Control revision evidence is not chronological.", nameof(Revision));
            }

            position = ApplyHistoricalStep(step, position);
            evidenceTime = stepTime;
        }

        if (receiptsByRevision.TryGetValue(Revision.Ordinal, out var finalReceipts))
        {
            foreach (var receipt in finalReceipts)
            {
                ValidateHistoricalReceipt(receipt, position);
                if (receipt.Disposition is ProcessControlReceiptDisposition.Applied
                    or ProcessControlReceiptDisposition.DeferredToSafePoint)
                {
                    throw new ArgumentException(
                        "A final-revision receipt cannot advance beyond retained state.",
                        nameof(Receipts));
                }
                if (receipt.RecordedAtUtc < evidenceTime)
                {
                    throw new ArgumentException(
                        "A final-revision receipt predates its retained lifecycle cut.",
                        nameof(Receipts));
                }
                evidenceTime = receipt.RecordedAtUtc;
            }
        }

        if (position.Mode != Mode
            || position.Phase != CurrentAttempt.Phase
            || position.AttemptId != CurrentAttempt.AttemptId)
        {
            throw new ArgumentException(
                "Retained lifecycle state is not the deterministic result of its revision history.",
                nameof(Revision));
        }
    }

    static void ValidateHistoricalReceipt(
        ProcessControlCommandReceipt receipt,
        ProcessControlLifecycleSemantics.Position position)
    {
        if (!ProcessControlLifecycleSemantics.TryApplyReceipt(
                position,
                receipt.Command,
                receipt.Disposition,
                out _))
        {
            throw new ArgumentException(
                "A command receipt is not legal in its retained lifecycle mode and execution phase.",
                nameof(Receipts));
        }
    }

    ProcessControlLifecycleSemantics.Position ApplyHistoricalStep(
        object step,
        ProcessControlLifecycleSemantics.Position position)
    {
        switch (step)
        {
            case ProcessActivationStartObservation activation:
                if (!ProcessControlLifecycleSemantics.TryBeginActivation(
                        position,
                        activation.Expectation.Continuation.ProcessAttemptId,
                        out var activated))
                {
                    throw new ArgumentException(
                        "Activation-start evidence is not legal at its retained lifecycle cut.",
                        nameof(Revision));
                }
                return activated;
            case ProcessControlSafePoint safePoint:
                ProcessAttemptId? replacementAttemptId = null;
                if (position.Mode == ProcessControlMode.RestartRequested)
                {
                    var currentIndex = FindAttemptIndex(position.AttemptId);
                    if (currentIndex >= 0 && currentIndex + 1 < Attempts.Length)
                    {
                        replacementAttemptId = Attempts[currentIndex + 1].AttemptId;
                    }
                }
                if (!ProcessControlLifecycleSemantics.TryReachSafePoint(
                        position,
                        safePoint.Observation.Expectation.Continuation.ProcessAttemptId,
                        replacementAttemptId,
                        out var reached))
                {
                    throw new ArgumentException(
                        "Safe-point evidence is not legal at its retained lifecycle cut.",
                        nameof(Revision));
                }
                return reached;
            case ProcessAttemptAffinityObservation affinity:
                if (!ProcessControlLifecycleSemantics.TryBindAttemptAffinity(
                        position,
                        affinity.Expectation.Continuation.ProcessAttemptId,
                        out var affinityPosition))
                {
                    throw new ArgumentException(
                        "Affinity evidence is not legal at its retained lifecycle cut.",
                        nameof(Revision));
                }
                return affinityPosition;
            case ProcessControlCommandReceipt receipt:
                if (!ProcessControlLifecycleSemantics.TryApplyReceipt(
                        position,
                        receipt.Command,
                        receipt.Disposition,
                        out var commanded))
                {
                    throw new ArgumentException(
                        "An advancing command receipt has no lifecycle transition.",
                        nameof(Receipts));
                }
                return commanded;
            default:
                throw new ArgumentException("Control revision evidence has an unsupported variant.", nameof(Revision));
        }
    }

    int FindAttemptIndex(ProcessAttemptId attemptId)
    {
        for (var index = 0; index < Attempts.Length; index++)
        {
            if (Attempts[index].AttemptId == attemptId)
            {
                return index;
            }
        }
        return -1;
    }

    static DateTimeOffset RevisionEvidenceTime(object evidence) =>
        evidence switch
        {
            ProcessControlCommandReceipt receipt => receipt.RecordedAtUtc,
            ProcessActivationStartObservation activation => activation.ObservedAtUtc,
            ProcessControlSafePoint safePoint => safePoint.ObservedAtUtc,
            ProcessAttemptAffinityObservation affinity => affinity.ObservedAtUtc,
            _ => throw new ArgumentException("Control revision evidence has an unsupported variant.", nameof(evidence))
        };

    void ValidateLatestEvidence()
    {
        var latest = CreatedAtUtc;
        foreach (var receipt in Receipts)
        {
            if (receipt.RecordedAtUtc > latest)
            {
                latest = receipt.RecordedAtUtc;
            }
        }
        foreach (var attempt in Attempts)
        {
            if (attempt.StartedAtUtc > latest)
            {
                latest = attempt.StartedAtUtc;
            }

            if (attempt.EndedAtUtc is { } ended && ended > UpdatedAtUtc)
            {
                throw new ArgumentException("Attempt closure cannot follow latest control evidence.", nameof(UpdatedAtUtc));
            }

            if (attempt.EndedAtUtc is { } closureTime && closureTime > latest)
            {
                latest = closureTime;
            }

            if (attempt.ActiveActivation is { } active)
            {
                ValidateObservationBounds(active.Expectation, active.ObservedAtUtc, attempt, "Active activation");
                if (active.ObservedAtUtc > latest)
                {
                    latest = active.ObservedAtUtc;
                }
            }
            if (attempt.Closure?.InterruptedActivation is { } interrupted)
            {
                ValidateObservationBounds(
                    interrupted.Expectation,
                    interrupted.ObservedAtUtc,
                    attempt,
                    "Interrupted activation");
                if (interrupted.ObservedAtUtc > latest)
                {
                    latest = interrupted.ObservedAtUtc;
                }
            }
            foreach (var safePoint in attempt.SafePoints)
            {
                ValidateObservationBounds(
                    safePoint.Activation.Expectation,
                    safePoint.Activation.ObservedAtUtc,
                    attempt,
                    "Safe-point activation");
                ValidateObservationBounds(
                    safePoint.Observation.Expectation,
                    safePoint.ObservedAtUtc,
                    attempt,
                    "Safe point");
                if (safePoint.ObservedAtUtc > latest)
                {
                    latest = safePoint.ObservedAtUtc;
                }
            }
            foreach (var binding in attempt.AffinityBindings)
            {
                ValidateObservationBounds(
                    binding.Expectation,
                    binding.ObservedAtUtc,
                    attempt,
                    "Affinity binding");
                if (binding.ObservedAtUtc > latest)
                {
                    latest = binding.ObservedAtUtc;
                }
            }
        }
        if (latest != UpdatedAtUtc)
        {
            throw new ArgumentException("Latest control evidence must determine the state update time.", nameof(UpdatedAtUtc));
        }
    }

    void ValidateObservationBounds(
        ProcessControlExpectation expectation,
        DateTimeOffset observedAtUtc,
        ProcessControlAttemptState attempt,
        string evidenceKind)
    {
        if (expectation.Revision.Ordinal >= Revision.Ordinal)
        {
            throw new ArgumentException($"{evidenceKind} fence must precede current control revision.", nameof(Revision));
        }

        if (observedAtUtc > UpdatedAtUtc
            || (attempt.EndedAtUtc is { } ended && observedAtUtc > ended))
        {
            throw new ArgumentException($"{evidenceKind} is outside retained state chronology.", nameof(UpdatedAtUtc));
        }
    }
}
