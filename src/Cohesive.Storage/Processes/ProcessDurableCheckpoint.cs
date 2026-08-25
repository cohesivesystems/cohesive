using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Leased and fenced ownership of one stored Process aggregate.</summary>
public sealed record ProcessWorkerLease
{
    /// <summary>Creates worker ownership evidence.</summary>
    /// <param name="owner">Stable physical worker identity.</param>
    /// <param name="fence">Monotonic fence that supersedes earlier owners.</param>
    /// <param name="claimedAtUtc">UTC time at which ownership was acquired.</param>
    /// <param name="renewedAtUtc">UTC time of the latest successful renewal.</param>
    /// <param name="expiresAtUtc">Exclusive UTC lease expiry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> or <paramref name="fence"/> is empty; a time is not UTC; or lease chronology is
    /// invalid.
    /// </exception>
    [JsonConstructor]
    public ProcessWorkerLease(
        string owner,
        ProcessWorkerFence fence,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Owner = Guard.RequireNotNullOrWhiteSpace(owner);
        ProcessCheckpointRequirements.RequireIdentity(fence.Value, nameof(fence));
        ProcessCheckpointRequirements.RequireUtc(claimedAtUtc, nameof(claimedAtUtc));
        ProcessCheckpointRequirements.RequireUtc(renewedAtUtc, nameof(renewedAtUtc));
        ProcessCheckpointRequirements.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (renewedAtUtc < claimedAtUtc || expiresAtUtc <= renewedAtUtc)
        {
            throw new ArgumentException("Worker lease chronology is invalid.", nameof(expiresAtUtc));
        }

        Fence = fence;
        ClaimedAtUtc = claimedAtUtc;
        RenewedAtUtc = renewedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Stable physical worker identity.</summary>
    public string Owner { get; }

    /// <summary>Monotonic ownership fence.</summary>
    public ProcessWorkerFence Fence { get; }

    /// <summary>UTC time at which ownership was acquired.</summary>
    public DateTimeOffset ClaimedAtUtc { get; }

    /// <summary>UTC time of the latest successful renewal.</summary>
    public DateTimeOffset RenewedAtUtc { get; }

    /// <summary>Exclusive UTC lease expiry.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Determines whether this claim is live at an explicit observation time.</summary>
    /// <param name="observedAtUtc">UTC time to test.</param>
    /// <returns>
    /// <see langword="true"/> from the inclusive claim time until the exclusive expiry; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC.</exception>
    public bool IsLive(DateTimeOffset observedAtUtc)
    {
        ProcessCheckpointRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        return observedAtUtc >= ClaimedAtUtc && observedAtUtc < ExpiresAtUtc;
    }
}

/// <summary>One exact canonical activation input retained by the durable inbox.</summary>
public sealed record ProcessDurableInboxEntry
{
    /// <summary>Creates one durable inbox entry.</summary>
    /// <param name="input">Exact canonical input and Process target.</param>
    /// <param name="admittedAtUtc">UTC durable-admission time.</param>
    /// <param name="receipt">Latest semantic disposition, or null while pending.</param>
    /// <param name="dispositionContinuation">
    /// Process attempt that decided <paramref name="receipt"/>; null exactly while the input is pending.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="admittedAtUtc"/> is not UTC, <paramref name="receipt"/> describes another input, or
    /// receipt and deciding-continuation evidence are not both present or both absent.
    /// </exception>
    [JsonConstructor]
    public ProcessDurableInboxEntry(
        ProcessActivationInput input,
        DateTimeOffset admittedAtUtc,
        ProcessInputReceipt? receipt = null,
        ProcessContinuationIdentity? dispositionContinuation = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        ProcessCheckpointRequirements.RequireUtc(admittedAtUtc, nameof(admittedAtUtc));
        if (receipt is not null)
        {
            ProcessCheckpointRequirements.RequireUtc(receipt.ObservedAtUtc, nameof(receipt));
            if (receipt.Input != input)
            {
                throw new ArgumentException("An inbox receipt must describe the exact retained input.", nameof(receipt));
            }

            if (receipt.ObservedAtUtc < admittedAtUtc)
            {
                throw new ArgumentException(
                    "An inbox disposition cannot precede durable admission.",
                    nameof(receipt));
            }
        }

        if ((receipt is null) != (dispositionContinuation is null))
        {
            throw new ArgumentException(
                "Inbox disposition evidence must identify exactly the Process attempt that decided it.",
                nameof(dispositionContinuation));
        }
        if (dispositionContinuation is not null)
        {
            ProcessCheckpointRequirements.RequireIdentity(
                dispositionContinuation.ProcessInstanceId.Value,
                nameof(dispositionContinuation));
            ProcessCheckpointRequirements.RequireIdentity(
                dispositionContinuation.ProcessAttemptId.Value,
                nameof(dispositionContinuation));
        }

        AdmittedAtUtc = admittedAtUtc;
        Receipt = receipt;
        DispositionContinuation = dispositionContinuation;
    }

    /// <summary>Exact canonical input and Process target.</summary>
    public ProcessActivationInput Input { get; }

    /// <summary>UTC durable-admission time.</summary>
    public DateTimeOffset AdmittedAtUtc { get; }

    /// <summary>Latest semantic disposition, or null while pending.</summary>
    public ProcessInputReceipt? Receipt { get; }

    /// <summary>Process attempt that decided <see cref="Receipt"/>, or null while pending.</summary>
    public ProcessContinuationIdentity? DispositionContinuation { get; }

    /// <summary>Stable logical input identity.</summary>
    [JsonIgnore]
    public EmissionId EmissionId => Input.Envelope.Context.EmissionId;
}

/// <summary>Durable evidence that one finite activation was atomically checkpointed.</summary>
public sealed record ProcessActivationCommitReceipt
{
    /// <summary>Creates activation commit evidence.</summary>
    /// <param name="sequence">Positive committed activation sequence.</param>
    /// <param name="continuation">Logical Process instance and attempt whose continuation was committed.</param>
    /// <param name="beforeContinuation">Fingerprint of the exact continuation consumed by the activation.</param>
    /// <param name="afterContinuation">Fingerprint of the exact continuation atomically published by the activation.</param>
    /// <param name="activation">Exact activation request.</param>
    /// <param name="disposition">Finite semantic activation disposition.</param>
    /// <param name="evidence">Definition-bound activation trace and safe-point evidence.</param>
    /// <param name="committedAtUtc">UTC physical commit observation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="continuation"/>, <paramref name="activation"/>, or <paramref name="evidence"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sequence"/> is not positive or <paramref name="disposition"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A continuation fingerprint is empty, activation identity/cause evidence differs, or
    /// <paramref name="committedAtUtc"/> is not UTC.
    /// </exception>
    [JsonConstructor]
    public ProcessActivationCommitReceipt(
        long sequence,
        ProcessContinuationIdentity continuation,
        ProcessContinuationFingerprint beforeContinuation,
        ProcessContinuationFingerprint afterContinuation,
        ProcessActivation activation,
        ProcessActivationDisposition disposition,
        ProcessExecutionEvidence evidence,
        DateTimeOffset committedAtUtc)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Activation sequence must be positive.");
        }

        if (!Enum.IsDefined(disposition) || disposition == ProcessActivationDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Activation disposition must be explicit.");
        }

        Continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));
        ProcessCheckpointRequirements.RequireIdentity(
            continuation.ProcessInstanceId.Value,
            nameof(continuation));
        ProcessCheckpointRequirements.RequireIdentity(
            continuation.ProcessAttemptId.Value,
            nameof(continuation));
        ProcessCheckpointRequirements.RequireIdentity(
            beforeContinuation.Value,
            nameof(beforeContinuation));
        ProcessCheckpointRequirements.RequireIdentity(
            afterContinuation.Value,
            nameof(afterContinuation));
        BeforeContinuation = beforeContinuation;
        AfterContinuation = afterContinuation;
        Activation = activation ?? throw new ArgumentNullException(nameof(activation));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        if (evidence.Activation != activation.Id || evidence.Cause != activation.Cause)
        {
            throw new ArgumentException("Activation evidence must describe the exact committed request.", nameof(evidence));
        }

        ProcessCheckpointRequirements.RequireUtc(activation.ObservedAtUtc, nameof(activation));
        ProcessCheckpointRequirements.RequireUtc(committedAtUtc, nameof(committedAtUtc));
        if (committedAtUtc < activation.ObservedAtUtc)
        {
            throw new ArgumentException(
                "An activation commit cannot precede its semantic observation.",
                nameof(committedAtUtc));
        }

        Sequence = sequence;
        Disposition = disposition;
        CommittedAtUtc = committedAtUtc;
    }

    /// <summary>Positive committed activation sequence.</summary>
    public long Sequence { get; }

    /// <summary>Logical Process instance and attempt whose continuation was committed.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Fingerprint of the exact continuation consumed by the activation.</summary>
    public ProcessContinuationFingerprint BeforeContinuation { get; }

    /// <summary>Fingerprint of the exact continuation atomically published by the activation.</summary>
    public ProcessContinuationFingerprint AfterContinuation { get; }

    /// <summary>Exact activation request.</summary>
    public ProcessActivation Activation { get; }

    /// <summary>Finite semantic activation disposition.</summary>
    public ProcessActivationDisposition Disposition { get; }

    /// <summary>Definition-bound activation trace and safe-point evidence.</summary>
    public ProcessExecutionEvidence Evidence { get; }

    /// <summary>UTC physical commit observation.</summary>
    public DateTimeOffset CommittedAtUtc { get; }
}

/// <summary>Replay key for one explicit Process host-operation occurrence.</summary>
public sealed record ProcessOperationOccurrence
{
    /// <summary>Creates a Process operation occurrence.</summary>
    /// <param name="continuation">Logical Process instance and attempt.</param>
    /// <param name="activation">Finite activation invoking the operation.</param>
    /// <param name="token">Durable token invoking the operation.</param>
    /// <param name="node">Canonical Process operation node.</param>
    /// <param name="occurrence">Zero-based occurrence in the token history.</param>
    /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="occurrence"/> is negative.</exception>
    [JsonConstructor]
    public ProcessOperationOccurrence(
        ProcessContinuationIdentity continuation,
        ActivationId activation,
        TokenId token,
        ExecutionNodeId node,
        long occurrence)
    {
        Continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));
        ProcessCheckpointRequirements.RequireIdentity(activation.Value, nameof(activation));
        ProcessCheckpointRequirements.RequireIdentity(token.Value, nameof(token));
        ProcessCheckpointRequirements.RequireIdentity(node.Value, nameof(node));
        if (occurrence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrence), occurrence, "Operation occurrence cannot be negative.");
        }

        Activation = activation;
        Token = token;
        Node = node;
        Occurrence = occurrence;
    }

    /// <summary>Logical Process instance and attempt.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Finite activation invoking the operation.</summary>
    public ActivationId Activation { get; }

    /// <summary>Durable token invoking the operation.</summary>
    public TokenId Token { get; }

    /// <summary>Canonical Process operation node.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Zero-based occurrence in the token history.</summary>
    public long Occurrence { get; }
}

/// <summary>Durable replay evidence for one Process host-operation occurrence.</summary>
public sealed record ProcessOperationReceipt
{
    /// <summary>Creates one host-operation receipt.</summary>
    /// <param name="key">Exact replay key.</param>
    /// <param name="operationDefinition">Exact Transition, Relation, or Query definition invoked.</param>
    /// <param name="result">Exact typed value, emissions, or failure evidence returned by the host.</param>
    /// <param name="recordedAtUtc">UTC durable recording time.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="key"/>, <paramref name="operationDefinition"/>, or <paramref name="result"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="recordedAtUtc"/> is not UTC.</exception>
    [JsonConstructor]
    public ProcessOperationReceipt(
        ProcessOperationOccurrence key,
        ExecutionDefinitionReference operationDefinition,
        ProcessOperationResult result,
        DateTimeOffset recordedAtUtc)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        OperationDefinition = operationDefinition ?? throw new ArgumentNullException(nameof(operationDefinition));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        ProcessCheckpointRequirements.RequireUtc(recordedAtUtc, nameof(recordedAtUtc));
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Exact replay key.</summary>
    public ProcessOperationOccurrence Key { get; }

    /// <summary>Exact Transition, Relation, or Query definition invoked.</summary>
    public ExecutionDefinitionReference OperationDefinition { get; }

    /// <summary>Exact typed value, emissions, or failure evidence returned by the host.</summary>
    public ProcessOperationResult Result { get; }

    /// <summary>UTC durable recording time.</summary>
    public DateTimeOffset RecordedAtUtc { get; }
}

/// <summary>Durable acknowledgement that one logical outbox emission was published.</summary>
public sealed record ProcessEmissionPublication
{
    /// <summary>Creates publication acknowledgement evidence.</summary>
    /// <param name="attemptId">Physical publication attempt.</param>
    /// <param name="fence">Fence under which publication was acknowledged.</param>
    /// <param name="publishedAtUtc">UTC acknowledgement time.</param>
    /// <param name="evidence">Optional materialized adapter receipt.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default, <paramref name="publishedAtUtc"/> is not UTC, or <paramref name="evidence"/> is
    /// unknown or failed.
    /// </exception>
    [JsonConstructor]
    public ProcessEmissionPublication(
        OperationAttemptId attemptId,
        OperationFence fence,
        DateTimeOffset publishedAtUtc,
        PortableValue? evidence = null)
    {
        ProcessCheckpointRequirements.RequireIdentity(attemptId.Value, nameof(attemptId));
        if (fence.Value <= 0)
        {
            throw new ArgumentException("A publication acknowledgement requires a positive fence.", nameof(fence));
        }

        ProcessCheckpointRequirements.RequireUtc(publishedAtUtc, nameof(publishedAtUtc));
        if (evidence is { State: PortableValueState.Unknown or PortableValueState.Failed })
        {
            throw new ArgumentException("Publication evidence must be materialized.", nameof(evidence));
        }

        AttemptId = attemptId;
        Fence = fence;
        PublishedAtUtc = publishedAtUtc;
        Evidence = evidence;
    }

    /// <summary>Physical publication attempt.</summary>
    public OperationAttemptId AttemptId { get; }

    /// <summary>Fence under which publication was acknowledged.</summary>
    public OperationFence Fence { get; }

    /// <summary>UTC acknowledgement time.</summary>
    public DateTimeOffset PublishedAtUtc { get; }

    /// <summary>Optional materialized adapter receipt.</summary>
    public PortableValue? Evidence { get; }
}

/// <summary>Immutable logical outbox entry and its physical publication history.</summary>
public sealed record ProcessEmissionRecord
{
    /// <summary>Creates one durable emission-ledger entry.</summary>
    /// <param name="envelope">Exact canonical logical emission.</param>
    /// <param name="enqueuedAtUtc">UTC origin-commit time.</param>
    /// <param name="attempts">Ordered physical publication-attempt history.</param>
    /// <param name="publication">One durable publication acknowledgement, when present.</param>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Time, attempt ordering, fence ordering, or acknowledgement evidence is inconsistent.
    /// </exception>
    [JsonConstructor]
    public ProcessEmissionRecord(
        InteractionEnvelope envelope,
        DateTimeOffset enqueuedAtUtc,
        ImmutableArray<DurableOperationAttempt> attempts = default,
        ProcessEmissionPublication? publication = null)
    {
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        ProcessCheckpointRequirements.RequireUtc(enqueuedAtUtc, nameof(enqueuedAtUtc));
        var normalized = attempts.IsDefault ? [] : attempts;
        long priorFence = 0;
        for (var index = 0; index < normalized.Length; index++)
        {
            var attempt = normalized[index]
                ?? throw new ArgumentException("Publication attempts cannot contain null entries.", nameof(attempts));
            if (attempt.Ordinal != index + 1 || attempt.Claim.Fence.Value <= priorFence)
            {
                throw new ArgumentException("Publication attempts require contiguous ordinals and increasing fences.", nameof(attempts));
            }

            if (attempt.Claim.ClaimedAtUtc < enqueuedAtUtc)
            {
                throw new ArgumentException("Publication attempts cannot predate the origin commit.", nameof(attempts));
            }

            if (index < normalized.Length - 1
                && attempt.Stage != DurableOperationAttemptStage.Failed)
            {
                throw new ArgumentException(
                    "Only a failed publication attempt may precede another physical attempt.",
                    nameof(attempts));
            }
            priorFence = attempt.Claim.Fence.Value;
        }

        if (publication is not null)
        {
            var matching = normalized.IsEmpty ? null : normalized[^1];
            if (matching is null
                || matching.Claim.AttemptId != publication.AttemptId
                || matching.Claim.Fence != publication.Fence
                || matching.Stage is not (DurableOperationAttemptStage.Acknowledged or DurableOperationAttemptStage.Resolved)
                || matching.CompletedAtUtc != publication.PublishedAtUtc)
            {
                throw new ArgumentException(
                    "Publication acknowledgement must match one successfully completed fenced attempt.",
                    nameof(publication));
            }
        }

        EnqueuedAtUtc = enqueuedAtUtc;
        Attempts = normalized;
        Publication = publication;
    }

    /// <summary>Exact canonical logical emission.</summary>
    public InteractionEnvelope Envelope { get; }

    /// <summary>UTC origin-commit time.</summary>
    public DateTimeOffset EnqueuedAtUtc { get; }

    /// <summary>
    /// Ordered physical publication-attempt history whose latest attempt may advance monotonically in place.
    /// </summary>
    public ImmutableArray<DurableOperationAttempt> Attempts { get; }

    /// <summary>One durable publication acknowledgement, when present.</summary>
    public ProcessEmissionPublication? Publication { get; }

    /// <summary>Stable logical emission identity.</summary>
    [JsonIgnore]
    public EmissionId EmissionId => Envelope.Context.EmissionId;
}

/// <summary>Complete versioned physical checkpoint for one logical Process aggregate.</summary>
/// <remarks>
/// Canonical continuation, control, operation, and interaction values remain owned by their semantic blocks.
/// This Storage envelope composes them into one atomic durability boundary and does not mirror their fields.
/// </remarks>
public sealed class ProcessDurableCheckpoint
{
    /// <summary>Current exact physical checkpoint schema.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-process-durable-checkpoint/v6");

    /// <summary>Creates a complete validated physical Process checkpoint.</summary>
    /// <param name="schemaVersion">Exact physical checkpoint schema.</param>
    /// <param name="start">Durable admission evidence for the one logical Process start.</param>
    /// <param name="continuation">Complete canonical Process continuation.</param>
    /// <param name="control">Complete canonical lifecycle-control state.</param>
    /// <param name="activations">Committed activation replay ledger.</param>
    /// <param name="operations">Cached Process host-operation results.</param>
    /// <param name="inbox">Exact durable input inbox and disposition ledger.</param>
    /// <param name="emissions">Logical interaction outbox and publication history.</param>
    /// <param name="durableOperations">Durable Request-operation state keyed by Request emission identity.</param>
    /// <param name="createdAtUtc">UTC checkpoint creation time.</param>
    /// <param name="updatedAtUtc">UTC latest atomic update time.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="start"/>, <paramref name="continuation"/>, or <paramref name="control"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Schema identity, definition, continuation, attempt, chronology, collection identity, Request-operation, or
    /// terminal inbox invariants are violated.
    /// </exception>
    [JsonConstructor]
    public ProcessDurableCheckpoint(
        ExecutionIrSchemaVersion schemaVersion,
        ProcessStartReceipt start,
        ProcessContinuationState continuation,
        ProcessControlState control,
        ImmutableArray<ProcessActivationCommitReceipt> activations = default,
        ImmutableArray<ProcessOperationReceipt> operations = default,
        ImmutableArray<ProcessDurableInboxEntry> inbox = default,
        ImmutableArray<ProcessEmissionRecord> emissions = default,
        ImmutableArray<DurableOperationState> durableOperations = default,
        DateTimeOffset createdAtUtc = default,
        DateTimeOffset updatedAtUtc = default)
    {
        ProcessCheckpointRequirements.RequireIdentity(schemaVersion.Value, nameof(schemaVersion));

        Start = start ?? throw new ArgumentNullException(nameof(start));
        Continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));
        Control = control ?? throw new ArgumentNullException(nameof(control));
        ValidateAuthorities();

        ProcessCheckpointRequirements.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        ProcessCheckpointRequirements.RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        if (createdAtUtc < start.AcceptedAtUtc || updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Checkpoint chronology is invalid.", nameof(updatedAtUtc));
        }

        SchemaVersion = schemaVersion;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Activations = NormalizeActivations(activations);
        Operations = NormalizeOperations(operations);
        Inbox = NormalizeInbox(inbox);
        Emissions = NormalizeEmissions(emissions);
        DurableOperations = NormalizeDurableOperations(durableOperations, Emissions);
        ValidateEvidenceChronology();
    }

    /// <summary>Exact physical checkpoint schema.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Durable admission evidence for the one logical Process start.</summary>
    public ProcessStartReceipt Start { get; }

    /// <summary>Complete canonical Process continuation.</summary>
    public ProcessContinuationState Continuation { get; }

    /// <summary>Complete canonical lifecycle-control state.</summary>
    public ProcessControlState Control { get; }

    /// <summary>Committed activation replay ledger ordered by attempt lineage and attempt-local sequence.</summary>
    public ImmutableArray<ProcessActivationCommitReceipt> Activations { get; }

    /// <summary>Cached Process host-operation results ordered by replay key.</summary>
    public ImmutableArray<ProcessOperationReceipt> Operations { get; }

    /// <summary>Exact durable input inbox and disposition ledger ordered by logical emission identity.</summary>
    public ImmutableArray<ProcessDurableInboxEntry> Inbox { get; }

    /// <summary>Logical interaction outbox and publication history ordered by emission identity.</summary>
    public ImmutableArray<ProcessEmissionRecord> Emissions { get; }

    /// <summary>Durable Request-operation states ordered by Request emission identity.</summary>
    public ImmutableArray<DurableOperationState> DurableOperations { get; }

    /// <summary>UTC checkpoint creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>UTC latest atomic update time.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Exact pinned Process definition projected from canonical authorities.</summary>
    [JsonIgnore]
    public ExecutionDefinitionReference Definition => Continuation.Definition;

    /// <summary>Logical Process instance and current attempt.</summary>
    [JsonIgnore]
    public ProcessContinuationIdentity ContinuationIdentity => Continuation.Continuation;

    internal ProcessDurableCheckpoint WithInbox(
        ImmutableArray<ProcessDurableInboxEntry> inbox,
        DateTimeOffset updatedAtUtc) =>
        new(
            SchemaVersion,
            Start,
            Continuation,
            Control,
            Activations,
            Operations,
            inbox,
            Emissions,
            DurableOperations,
            CreatedAtUtc,
            updatedAtUtc);

    void ValidateEvidenceChronology()
    {
        RequireEvidenceTime(Control.UpdatedAtUtc, nameof(Control), "Lifecycle-control evidence");
        if (Continuation.Terminal?.OccurredAtUtc is { } terminalAtUtc)
        {
            RequireEvidenceTime(terminalAtUtc, nameof(Continuation), "Terminal continuation evidence");
        }

        foreach (var wait in Continuation.Waits)
        {
            if (wait is not null)
            {
                RequireEvidenceTime(wait.RegisteredAtUtc, nameof(Continuation), "Wait registration");
            }
        }
        foreach (var buffered in Continuation.BufferedInputs)
        {
            if (buffered is not null)
            {
                RequireEvidenceTime(buffered.BufferedAtUtc, nameof(Continuation), "Buffered input");
            }
        }
        foreach (var receipt in Continuation.InputReceipts)
        {
            if (receipt is not null)
            {
                RequireEvidenceTime(receipt.ObservedAtUtc, nameof(Continuation), "Input disposition");
            }
        }
        foreach (var request in Continuation.OutstandingRequests)
        {
            if (request is not null)
            {
                RequireEvidenceTime(request.RegisteredAtUtc, nameof(Continuation), "Outstanding Request registration");
            }
        }

        foreach (var activation in Activations)
        {
            RequireEvidenceTime(activation.Activation.ObservedAtUtc, nameof(Activations), "Activation observation");
            RequireEvidenceTime(activation.CommittedAtUtc, nameof(Activations), "Activation commit");
        }
        foreach (var operation in Operations)
        {
            RequireEvidenceTime(operation.RecordedAtUtc, nameof(Operations), "Host-operation receipt");
        }
        foreach (var entry in Inbox)
        {
            RequireEvidenceTime(entry.AdmittedAtUtc, nameof(Inbox), "Inbox admission");
            if (entry.Receipt is { } receipt)
            {
                RequireEvidenceTime(receipt.ObservedAtUtc, nameof(Inbox), "Inbox disposition");
            }
        }
        foreach (var emission in Emissions)
        {
            RequireEvidenceTime(emission.EnqueuedAtUtc, nameof(Emissions), "Outbox enqueue");
            ValidateAttemptChronology(emission.Attempts, nameof(Emissions), "Outbox publication");
            if (emission.Publication is { } publication)
            {
                RequireEvidenceTime(publication.PublishedAtUtc, nameof(Emissions), "Outbox publication acknowledgement");
            }
        }
        foreach (var operation in DurableOperations)
        {
            RequireEvidenceTime(operation.CreatedAtUtc, nameof(DurableOperations), "Durable-operation creation");
            ValidateAttemptChronology(operation.Attempts, nameof(DurableOperations), "Durable operation");
            foreach (var reconciliation in operation.Reconciliations)
            {
                RequireEvidenceTime(
                    reconciliation.ObservedAtUtc,
                    nameof(DurableOperations),
                    "Durable-operation reconciliation");
            }
            if (operation.Acknowledgement is { } acknowledgement)
            {
                RequireEvidenceTime(
                    acknowledgement.AcknowledgedAtUtc,
                    nameof(DurableOperations),
                    "Durable-operation acknowledgement");
            }
        }
    }

    void ValidateAttemptChronology(
        ImmutableArray<DurableOperationAttempt> attempts,
        string parameterName,
        string evidenceKind)
    {
        foreach (var attempt in attempts)
        {
            RequireEvidenceTime(attempt.Claim.ClaimedAtUtc, parameterName, $"{evidenceKind} claim");
            RequireEvidenceTime(attempt.Claim.RenewedAtUtc, parameterName, $"{evidenceKind} renewal");
            if (attempt.DispatchedAtUtc is { } dispatchedAtUtc)
            {
                RequireEvidenceTime(dispatchedAtUtc, parameterName, $"{evidenceKind} dispatch");
            }
            if (attempt.CompletedAtUtc is { } completedAtUtc)
            {
                RequireEvidenceTime(completedAtUtc, parameterName, $"{evidenceKind} completion");
            }
        }
    }

    void RequireEvidenceTime(DateTimeOffset value, string parameterName, string evidenceKind)
    {
        ProcessCheckpointRequirements.RequireUtc(value, parameterName);
        if (value < Start.AcceptedAtUtc || value > UpdatedAtUtc)
        {
            throw new ArgumentException(
                $"{evidenceKind} must fall between Process admission and the checkpoint update.",
                parameterName);
        }
    }

    void ValidateAuthorities()
    {
        var request = Start.Request;
        if (request.Definition != Continuation.Definition
            || request.Definition != Control.Definition)
        {
            throw new ArgumentException("Start, continuation, and control state must pin one exact definition.", nameof(Continuation));
        }

        if (request.InitialContinuation.ProcessInstanceId != Continuation.Continuation.ProcessInstanceId
            || request.InitialContinuation.ProcessInstanceId != Control.ProcessInstanceId)
        {
            throw new ArgumentException("Checkpoint authorities must address one logical Process instance.", nameof(Continuation));
        }

        if (request.Context.Authorization.AuthorityScope != Control.AuthorityScope)
        {
            throw new ArgumentException("Start and control state must retain one authority scope.", nameof(Control));
        }

        if (Control.Attempts[0].AttemptId != request.InitialContinuation.ProcessAttemptId)
        {
            throw new ArgumentException("Control attempt lineage must begin with the admitted start attempt.", nameof(Control));
        }

        if (Control.CreatedAtUtc != Start.AcceptedAtUtc)
        {
            throw new ArgumentException("Control state must begin at the durable start-admission time.", nameof(Control));
        }

        if (Continuation.Continuation.ProcessAttemptId != Control.CurrentAttempt.AttemptId)
        {
            throw new ArgumentException("Continuation must address the current control attempt.", nameof(Continuation));
        }
    }

    ImmutableArray<ProcessActivationCommitReceipt> NormalizeActivations(
        ImmutableArray<ProcessActivationCommitReceipt> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            if (Continuation.CompletedActivationCount != 0)
            {
                throw new ArgumentException("Completed activations require durable replay receipts.", nameof(values));
            }

            return [];
        }

        if (values.Any(static value => value is null))
        {
            throw new ArgumentException("Activation receipts cannot contain null entries.", nameof(values));
        }

        var attemptOrdinals = Control.Attempts
            .Select(static (attempt, index) => (attempt.AttemptId, index))
            .ToDictionary(static pair => pair.AttemptId, static pair => pair.index);
        var normalized = values
            .OrderBy(value => attemptOrdinals.TryGetValue(
                    value.Continuation.ProcessAttemptId,
                    out var ordinal)
                ? ordinal
                : int.MaxValue)
            .ThenBy(static value => value.Sequence)
            .ToImmutableArray();
        HashSet<(ProcessAttemptId Attempt, ActivationId Activation)> identities = [];
        Dictionary<ProcessAttemptId, long> sequenceByAttempt = [];
        Dictionary<ProcessAttemptId, ProcessContinuationFingerprint> afterContinuationByAttempt = [];
        foreach (var receipt in normalized)
        {
            RequireKnownAttempt(receipt.Continuation, nameof(values));
            var priorSequence = sequenceByAttempt.GetValueOrDefault(
                receipt.Continuation.ProcessAttemptId);
            if (receipt.Sequence != priorSequence + 1
                || !identities.Add((receipt.Continuation.ProcessAttemptId, receipt.Activation.Id)))
            {
                throw new ArgumentException(
                    "Activation receipts require contiguous attempt-local sequences and unique identities.",
                    nameof(values));
            }
            if (receipt.Evidence.Definition != Definition)
            {
                throw new ArgumentException("Activation receipts must pin the checkpoint definition.", nameof(values));
            }

            if (afterContinuationByAttempt.TryGetValue(
                    receipt.Continuation.ProcessAttemptId,
                    out var priorAfterContinuation)
                && receipt.BeforeContinuation != priorAfterContinuation)
            {
                throw new ArgumentException(
                    "Activation receipts must form a contiguous before/after continuation fingerprint chain.",
                    nameof(values));
            }

            if (receipt.CommittedAtUtc > UpdatedAtUtc)
            {
                throw new ArgumentException("Activation receipts cannot follow the checkpoint update.", nameof(values));
            }

            sequenceByAttempt[receipt.Continuation.ProcessAttemptId] = receipt.Sequence;
            afterContinuationByAttempt[receipt.Continuation.ProcessAttemptId] = receipt.AfterContinuation;
        }

        var currentSequence = sequenceByAttempt.GetValueOrDefault(
            ContinuationIdentity.ProcessAttemptId);
        if (currentSequence != Continuation.CompletedActivationCount)
        {
            throw new ArgumentException(
                "Current-attempt activation receipts must reach the continuation sequence.",
                nameof(values));
        }

        if (currentSequence > 0
            && afterContinuationByAttempt[ContinuationIdentity.ProcessAttemptId]
                != ProcessStorageContentFingerprints.Continuation(Continuation))
        {
            throw new ArgumentException(
                "The current attempt's final activation receipt must publish the exact checkpoint continuation.",
                nameof(values));
        }

        if (currentSequence > 0)
        {
            var currentReceipt = normalized.Last(receipt =>
                receipt.Continuation.ProcessAttemptId == ContinuationIdentity.ProcessAttemptId);
            var dispositionMatchesTerminal = (currentReceipt.Disposition, Continuation.Terminal.Kind) switch
            {
                (ProcessActivationDisposition.Quiescent or ProcessActivationDisposition.DurableCut,
                    ExecutionTerminalOutcomeKind.None) => true,
                (ProcessActivationDisposition.Completed, ExecutionTerminalOutcomeKind.Completed) => true,
                (ProcessActivationDisposition.Failed,
                    ExecutionTerminalOutcomeKind.Failed or ExecutionTerminalOutcomeKind.Terminated) => true,
                (ProcessActivationDisposition.Cancelled, ExecutionTerminalOutcomeKind.Cancelled) => true,
                _ => false
            };
            if (!dispositionMatchesTerminal)
            {
                throw new ArgumentException(
                    "The current attempt's final activation disposition must match its terminal continuation state.",
                    nameof(values));
            }
        }
        return normalized;
    }

    ImmutableArray<ProcessOperationReceipt> NormalizeOperations(ImmutableArray<ProcessOperationReceipt> values)
    {
        var source = values.IsDefault ? [] : values;
        if (source.Any(static value => value is null))
        {
            throw new ArgumentException("Operation receipts cannot contain null entries.", nameof(values));
        }

        var normalized = source
            .OrderBy(static value => value.Key.Continuation.ProcessAttemptId.Value, StringComparer.Ordinal)
            .ThenBy(static value => value.Key.Activation.Value, StringComparer.Ordinal)
            .ThenBy(static value => value.Key.Token.Value, StringComparer.Ordinal)
            .ThenBy(static value => value.Key.Node.Value, StringComparer.Ordinal)
            .ThenBy(static value => value.Key.Occurrence)
            .ToImmutableArray();
        HashSet<ProcessOperationOccurrence> keys = [];
        foreach (var receipt in normalized)
        {
            if (!keys.Add(receipt.Key))
            {
                throw new ArgumentException("Operation receipts must be uniquely keyed.", nameof(values));
            }

            RequireKnownAttempt(receipt.Key.Continuation, nameof(values));
        }
        return normalized;
    }

    ImmutableArray<ProcessDurableInboxEntry> NormalizeInbox(ImmutableArray<ProcessDurableInboxEntry> values)
    {
        var source = values.IsDefault ? [] : values;
        if (source.Any(static value => value is null))
        {
            throw new ArgumentException("Inbox entries cannot contain null entries.", nameof(values));
        }

        var normalized = source
            .OrderBy(static value => value.EmissionId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        HashSet<EmissionId> identities = [];
        foreach (var entry in normalized)
        {
            if (!identities.Add(entry.EmissionId))
            {
                throw new ArgumentException("Inbox entries must be uniquely keyed by EmissionId.", nameof(values));
            }

            if (entry.Input.Target.Continuation.ProcessInstanceId
                != ContinuationIdentity.ProcessInstanceId)
            {
                throw new ArgumentException(
                    "Inbox evidence must target the checkpoint Process instance.",
                    nameof(values));
            }
            if (entry.DispositionContinuation is { } decidingContinuation)
            {
                RequireKnownAttempt(decidingContinuation, nameof(values));
            }
            if (entry.Receipt is { } receipt && !receipt.IsValidAdmissionEvidence())
            {
                throw new ArgumentException(
                    "Inbox receipts require a closed semantic input-admission reason compatible with the policy disposition.",
                    nameof(values));
            }
        }

        var entriesByEmission = normalized.ToDictionary(static entry => entry.EmissionId);
        foreach (var receipt in Continuation.InputReceipts)
        {
            if (!entriesByEmission.TryGetValue(receipt.Emission, out var entry)
                || ProcessStorageContentFingerprints.Input(entry.Input)
                    != ProcessStorageContentFingerprints.Input(receipt.Input)
                || entry.Receipt != receipt
                || entry.DispositionContinuation != ContinuationIdentity)
            {
                throw new ArgumentException(
                    "Every semantic input receipt must have one exact durable inbox projection.",
                    nameof(values));
            }
        }

        foreach (var buffered in Continuation.BufferedInputs)
        {
            var emission = buffered.Input.Envelope.Context.EmissionId;
            if (!entriesByEmission.TryGetValue(emission, out var entry)
                || ProcessStorageContentFingerprints.Input(entry.Input)
                    != ProcessStorageContentFingerprints.Input(buffered.Input)
                || entry.Receipt is not { Disposition: ProcessInputAdmissionDisposition.Buffered }
                || entry.DispositionContinuation != ContinuationIdentity)
            {
                throw new ArgumentException(
                    "Every semantic buffered input must have one exact pending durable inbox projection.",
                    nameof(values));
            }
        }

        var receiptsByEmission = Continuation.InputReceipts.ToDictionary(static receipt => receipt.Emission);
        foreach (var entry in normalized)
        {
            if (entry.Receipt is not null
                && entry.DispositionContinuation == ContinuationIdentity
                && (!receiptsByEmission.TryGetValue(entry.EmissionId, out var receipt)
                    || receipt != entry.Receipt))
            {
                throw new ArgumentException(
                    "A durable inbox disposition must project the canonical semantic input receipt.",
                    nameof(values));
            }
        }
        return normalized;
    }

    static ImmutableArray<ProcessEmissionRecord> NormalizeEmissions(ImmutableArray<ProcessEmissionRecord> values)
    {
        var source = values.IsDefault ? [] : values;
        if (source.Any(static value => value is null))
        {
            throw new ArgumentException("Emission records cannot contain null entries.", nameof(values));
        }

        var normalized = source
            .OrderBy(static value => value.EmissionId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        HashSet<EmissionId> identities = [];
        foreach (var entry in normalized)
        {
            if (!identities.Add(entry.EmissionId))
            {
                throw new ArgumentException("Emission records must be uniquely keyed by EmissionId.", nameof(values));
            }
        }
        return normalized;
    }

    static ImmutableArray<DurableOperationState> NormalizeDurableOperations(
        ImmutableArray<DurableOperationState> values,
        ImmutableArray<ProcessEmissionRecord> emissions)
    {
        var source = values.IsDefault ? [] : values;
        if (source.Any(static value => value is null))
        {
            throw new ArgumentException("Durable operations cannot contain null entries.", nameof(values));
        }

        var normalized = source
            .OrderBy(static value => value.OperationId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var emissionById = emissions.ToDictionary(static entry => entry.EmissionId);
        HashSet<EmissionId> identities = [];
        foreach (var operation in normalized)
        {
            if (!identities.Add(operation.OperationId))
            {
                throw new ArgumentException("Durable operations must be uniquely keyed by EmissionId.", nameof(values));
            }

            if (!emissionById.TryGetValue(operation.OperationId, out var emission)
                || emission.Envelope != operation.Request)
            {
                throw new ArgumentException(
                    "Every durable Request operation must share the exact canonical Request outbox envelope.",
                    nameof(values));
            }
        }

        return normalized;
    }

    void RequireKnownAttempt(ProcessContinuationIdentity continuation, string parameterName)
    {
        if (continuation.ProcessInstanceId != Continuation.Continuation.ProcessInstanceId
            || !Control.Attempts.Any(attempt => attempt.AttemptId == continuation.ProcessAttemptId))
        {
            throw new ArgumentException("Ledger evidence addresses an unknown Process instance or attempt.", parameterName);
        }
    }
}

static class ProcessCheckpointRequirements
{
    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be explicit and use the UTC offset.", parameterName);
        }
    }

    internal static void RequireIdentity(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A stable non-default identity is required.", parameterName);
        }
    }
}
