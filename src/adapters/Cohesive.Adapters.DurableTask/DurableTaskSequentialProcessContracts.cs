using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Stable task and event names owned by the Durable Task Process interpreter.</summary>
public static class DurableTaskSequentialProcessNames
{
    /// <summary>Generic orchestration that interprets an exact canonical Process plan.</summary>
    public const string Orchestration = "Cohesive.Processes.Sequential.v1";

    /// <summary>Orchestration that durably admits one canonical top-level Process start.</summary>
    public const string StartAdmissionOrchestration = "Cohesive.Processes.StartAdmission.v1";

    /// <summary>Orchestration that durably resolves and awaits one canonical lifecycle command.</summary>
    public const string ControlAdmissionOrchestration = "Cohesive.Processes.ControlAdmission.v1";

    /// <summary>Bounded entity that retains one authority-scoped Process-start index claim.</summary>
    public const string StartAdmissionIndexEntity = "Cohesive.Processes.StartAdmissionIndex.v1";

    /// <summary>Bounded entity retaining one content-addressed safe lifecycle-command response.</summary>
    public const string ControlResponseEntity = "Cohesive.ControlResponse.v1";

    /// <summary>Per-Process canonical control-state authority after semantic terminal handoff.</summary>
    public const string TerminalControlEntity = "Cohesive.TerminalControl.v1";

    /// <summary>Activity that materializes one exact Transition or Relation/Query operation.</summary>
    public const string HostOperationActivity = "Cohesive.Processes.HostOperation.v1";

    /// <summary>Activity that resolves one exact canonical Signal target.</summary>
    public const string SignalTargetResolutionActivity = "Cohesive.Processes.SignalTargetResolution.v1";

    /// <summary>Activity that publishes one exact target-deduplicated canonical domain event.</summary>
    public const string DomainEventPublicationActivity = "Cohesive.Processes.DomainEventPublication.v1";

    /// <summary>Activity that dispatches one fenced canonical durable Request attempt.</summary>
    public const string DurableOperationActivity = "Cohesive.Processes.DurableOperation.v1";

    /// <summary>Activity that reconciles one failed ambiguous canonical durable Request attempt.</summary>
    public const string DurableOperationReconciliationActivity =
        "Cohesive.Processes.DurableOperationReconciliation.v1";

    /// <summary>External event carrying one canonical interaction into a waiting Process.</summary>
    public const string InteractionEvent = "Cohesive.Processes.Interaction.v1";

    /// <summary>Parent-originated event carrying one exact propagated child-cancellation intent.</summary>
    public const string ChildCancellationEvent = "Cohesive.Processes.ChildCancellation.v1";

    /// <summary>External event carrying one canonical Process lifecycle command.</summary>
    public const string ControlEvent = "Cohesive.Processes.Control.v1";
}

/// <summary>Exact canonical start evidence supplied to one Durable Task Process orchestration.</summary>
/// <remarks>
/// The Process document is not copied into this transport value. The receipt pins its exact identity, revision, and
/// fingerprint, which must resolve to a precompiled plan in the worker's immutable plan catalog. A target-owned
/// <see cref="Resume"/> snapshot may carry the complete canonical continuation across Continue-as-new history
/// rollover. <see cref="ChildRequest"/> retains the exact canonical parent Request only for child executions so
/// the adapter does not depend on optional backend parent metadata. Both are derived execution evidence and never
/// replace the pinned plan or Request as semantic authority.
/// </remarks>
public sealed record DurableTaskSequentialProcessStart
{
    /// <summary>Creates one exact Process start input.</summary>
    /// <param name="receipt">Durably admitted canonical Process-start evidence.</param>
    /// <param name="activationContext">Explicit authority, correlation, delivery, and provenance for emissions.</param>
    /// <param name="resume">Optional exact canonical state retained across one target history rollover.</param>
    /// <param name="childRequest">
    /// Optional exact parent Request from which this child start was projected; absent for top-level starts.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="receipt"/> or <paramref name="activationContext"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The start and activation contexts have different authority scopes; child evidence is not an exact projection
    /// of the supplied Request; or child activation context does not retain its Request correlation, causation,
    /// delivery, and ordering evidence.
    /// </exception>
    [JsonConstructor]
    public DurableTaskSequentialProcessStart(
        ProcessStartReceipt receipt,
        ProcessActivationContext activationContext,
        DurableTaskSequentialProcessResume? resume = null,
        RequestEnvelope? childRequest = null)
    {
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        ActivationContext = activationContext ?? throw new ArgumentNullException(nameof(activationContext));
        if (receipt.Request.Context.Authorization.AuthorityScope != activationContext.AuthorityScope)
        {
            throw new ArgumentException(
                "Process-start and activation contexts must have the same authority scope.",
                nameof(activationContext));
        }
        if (childRequest is not null)
        {
            var target = childRequest.ChildTarget
                ?? throw new ArgumentException(
                    "Durable Task child-start evidence requires a canonical Request with an exact child target.",
                    nameof(childRequest));
            if (!ProcessChildStartProjection.Matches(
                    receipt,
                    childRequest,
                    target,
                    receipt.Request.Context.Authorization))
            {
                throw new ArgumentException(
                    "Durable Task child-start evidence does not match the exact projected Process start.",
                    nameof(childRequest));
            }
            if (activationContext.CorrelationId != childRequest.Context.CorrelationId
                || activationContext.CausationId != childRequest.Context.EmissionId
                || activationContext.Delivery != childRequest.Context.Delivery
                || activationContext.Ordering != childRequest.Context.Ordering)
            {
                throw new ArgumentException(
                    "Durable Task child activation must retain the parent Request correlation, causation, delivery, and ordering evidence.",
                    nameof(activationContext));
            }
        }
        if (resume is not null)
        {
            if (resume.Result.Disposition != ProcessActivationDisposition.DurableCut)
            {
                throw new ArgumentException(
                    "A Durable Task resume snapshot must close at a canonical durable cut.",
                    nameof(resume));
            }
            if (resume.Result.State.Definition != receipt.Request.Definition
                || resume.Result.State.Continuation.ProcessInstanceId
                    != receipt.Request.InitialContinuation.ProcessInstanceId
                || resume.Result.Control.AuthorityScope != activationContext.AuthorityScope)
            {
                throw new ArgumentException(
                    "A Durable Task resume snapshot must retain the exact started definition, Process instance, and authority scope.",
                    nameof(resume));
            }
            if (resume.Result.DurableOperations.Any(static operation =>
                    operation.State.Status != DurableOperationStatus.Dispositioned))
            {
                throw new ArgumentException(
                    "Continue-as-new cannot discard an incomplete durable Request task.",
                    nameof(resume));
            }
        }
        Resume = resume;
        ChildRequest = childRequest;
    }

    /// <summary>Durably admitted exact Process-start evidence.</summary>
    public ProcessStartReceipt Receipt { get; }

    /// <summary>Explicit context used for canonical interaction emissions.</summary>
    public ProcessActivationContext ActivationContext { get; }

    /// <summary>Optional complete canonical result retained at the preceding target history boundary.</summary>
    public DurableTaskSequentialProcessResume? Resume { get; }

    /// <summary>
    /// Exact canonical parent Request from which this child start was projected, or <see langword="null"/> for a
    /// top-level Process start.
    /// </summary>
    public RequestEnvelope? ChildRequest { get; }

    internal DurableTaskSequentialProcessStart ContinueFrom(DurableTaskSequentialProcessResult result) =>
        new(Receipt, ActivationContext, new(result), ChildRequest);
}

/// <summary>Target-owned carrier for exact canonical state across Durable Task Continue-as-new.</summary>
public sealed record DurableTaskSequentialProcessResume
{
    /// <summary>Creates a history-rollover carrier from one canonical result.</summary>
    /// <param name="result">Complete accumulated canonical result at the preceding durable activation boundary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public DurableTaskSequentialProcessResume(DurableTaskSequentialProcessResult result) =>
        Result = result ?? throw new ArgumentNullException(nameof(result));

    /// <summary>Complete accumulated canonical result at the preceding durable activation boundary.</summary>
    public DurableTaskSequentialProcessResult Result { get; }
}

/// <summary>Kind of one activity-bound canonical Process host operation.</summary>
public enum DurableTaskProcessHostOperationKind
{
    /// <summary>No operation was selected; invalid in an activity request.</summary>
    Unspecified = 0,

    /// <summary>Invoke one exact canonical Transition.</summary>
    Transition = 1,

    /// <summary>Evaluate one exact canonical Relation or Query.</summary>
    RelationQuery = 2
}

/// <summary>One exact host operation scheduled as a bounded Durable Task activity.</summary>
public sealed record DurableTaskProcessHostOperation
{
    /// <summary>Creates a closed Transition or Relation/Query activity request.</summary>
    /// <param name="kind">Selected operation family.</param>
    /// <param name="transition">Exact Transition invocation for <see cref="DurableTaskProcessHostOperationKind.Transition"/>.</param>
    /// <param name="relationQuery">Exact evaluation for <see cref="DurableTaskProcessHostOperationKind.RelationQuery"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">The selected family does not contain exactly its required payload.</exception>
    [JsonConstructor]
    public DurableTaskProcessHostOperation(
        DurableTaskProcessHostOperationKind kind,
        ProcessTransitionInvocation? transition = null,
        ProcessRelationEvaluation? relationQuery = null)
    {
        if (!Enum.IsDefined(kind) || kind == DurableTaskProcessHostOperationKind.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A host operation kind must be explicit.");
        }
        if ((kind == DurableTaskProcessHostOperationKind.Transition) != (transition is not null)
            || (kind == DurableTaskProcessHostOperationKind.RelationQuery) != (relationQuery is not null))
        {
            throw new ArgumentException("A host operation must carry exactly the payload selected by its kind.");
        }

        Kind = kind;
        Transition = transition;
        RelationQuery = relationQuery;
    }

    /// <summary>Selected host-operation family.</summary>
    public DurableTaskProcessHostOperationKind Kind { get; }

    /// <summary>Exact Transition invocation when <see cref="Kind"/> is Transition.</summary>
    public ProcessTransitionInvocation? Transition { get; }

    /// <summary>Exact Relation or Query evaluation when <see cref="Kind"/> is RelationQuery.</summary>
    public ProcessRelationEvaluation? RelationQuery { get; }

    internal static DurableTaskProcessHostOperation For(ProcessTransitionInvocation invocation) =>
        new(DurableTaskProcessHostOperationKind.Transition, transition: invocation);

    internal static DurableTaskProcessHostOperation For(ProcessRelationEvaluation evaluation) =>
        new(DurableTaskProcessHostOperationKind.RelationQuery, relationQuery: evaluation);
}

/// <summary>Durable Task acknowledgement of one exact canonical domain-event publication.</summary>
public sealed record DurableTaskDomainEventPublication
{
    /// <summary>Creates one attributable publication acknowledgement.</summary>
    /// <param name="emissionId">Canonical logical emission identity.</param>
    /// <param name="deduplicationKey">Stable target-deduplication key supplied to the publisher.</param>
    /// <param name="contentFingerprint">Fingerprint of the exact published canonical envelope.</param>
    /// <param name="publishedAtUtc">UTC time at which the publication activity observed acknowledgement.</param>
    /// <param name="acknowledgement">Bounded publisher-supplied acknowledgement evidence.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="deduplicationKey"/>, <paramref name="contentFingerprint"/>, or
    /// <paramref name="acknowledgement"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="emissionId"/> or <paramref name="contentFingerprint"/> is default, or
    /// <paramref name="publishedAtUtc"/> is not UTC.
    /// </exception>
    [JsonConstructor]
    public DurableTaskDomainEventPublication(
        EmissionId emissionId,
        DomainEventPublicationDeduplicationKey deduplicationKey,
        InteractionEnvelopeContentFingerprint contentFingerprint,
        DateTimeOffset publishedAtUtc,
        DomainEventPublicationAcknowledgement acknowledgement)
    {
        if (string.IsNullOrWhiteSpace(emissionId.Value))
        {
            throw new ArgumentException("Domain-event publication requires an emission identity.", nameof(emissionId));
        }
        if (string.IsNullOrWhiteSpace(contentFingerprint.Value))
        {
            throw new ArgumentException(
                "Domain-event publication requires an envelope content fingerprint.",
                nameof(contentFingerprint));
        }
        if (publishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain-event publication time must use the UTC offset.", nameof(publishedAtUtc));
        }

        EmissionId = emissionId;
        DeduplicationKey = Guard.RequireNotNull(deduplicationKey);
        ContentFingerprint = contentFingerprint;
        PublishedAtUtc = publishedAtUtc;
        Acknowledgement = Guard.RequireNotNull(acknowledgement);
    }

    /// <summary>Canonical logical emission identity.</summary>
    public EmissionId EmissionId { get; }

    /// <summary>Stable target-deduplication key supplied to the publisher.</summary>
    public DomainEventPublicationDeduplicationKey DeduplicationKey { get; }

    /// <summary>Fingerprint of the exact published canonical envelope.</summary>
    public InteractionEnvelopeContentFingerprint ContentFingerprint { get; }

    /// <summary>UTC time at which the publication activity observed acknowledgement.</summary>
    public DateTimeOffset PublishedAtUtc { get; }

    /// <summary>Bounded publisher-supplied acknowledgement evidence.</summary>
    public DomainEventPublicationAcknowledgement Acknowledgement { get; }

    internal static DurableTaskDomainEventPublication From(
        DomainEventPublicationInvocation invocation,
        DateTimeOffset publishedAtUtc,
        DomainEventPublicationAcknowledgement acknowledgement) => new(
        invocation.DomainEvent.Context.EmissionId,
        invocation.DeduplicationKey,
        InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(invocation.DomainEvent),
        publishedAtUtc,
        acknowledgement);
}

/// <summary>Canonical semantic result and accumulated evidence from a Durable Task execution.</summary>
public sealed record DurableTaskSequentialProcessResult
{
    /// <summary>Creates an immutable Process execution projection.</summary>
    /// <param name="disposition">Latest canonical activation disposition.</param>
    /// <param name="state">Complete canonical replacement continuation.</param>
    /// <param name="control">Complete canonical lifecycle-control state governing <paramref name="state"/>.</param>
    /// <param name="latestControlDecision">Latest command or execution observation projected for operators.</param>
    /// <param name="emissions">All canonical interactions emitted in activation order.</param>
    /// <param name="inputAdmissions">All canonical input dispositions in activation order.</param>
    /// <param name="diagnostics">All canonical interpreter diagnostics in activation order.</param>
    /// <param name="evidence">Canonical evidence for every completed finite activation.</param>
    /// <param name="durableOperations">Canonical durable Request ledgers in logical operation identity order.</param>
    /// <param name="traces">Payload-safe normalized traces for activations executed after trace retention began.</param>
    /// <param name="domainEventPublications">
    /// Target acknowledgements for exact canonical domain events in logical emission order.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unspecified.</exception>
    [JsonConstructor]
    public DurableTaskSequentialProcessResult(
        ProcessActivationDisposition disposition,
        ProcessContinuationState state,
        ProcessControlState control,
        ProcessControlDecision? latestControlDecision = null,
        ImmutableArray<InteractionEnvelope> emissions = default,
        ImmutableArray<ProcessInputReceipt> inputAdmissions = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default,
        ImmutableArray<ProcessExecutionEvidence> evidence = default,
        ImmutableArray<DurableTaskDurableOperationResult> durableOperations = default,
        ImmutableArray<NormalizedExecutionTrace> traces = default,
        ImmutableArray<DurableTaskDomainEventPublication> domainEventPublications = default)
    {
        if (!Enum.IsDefined(disposition) || disposition == ProcessActivationDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "A Process execution result requires an explicit disposition.");
        }

        State = state ?? throw new ArgumentNullException(nameof(state));
        Control = control ?? throw new ArgumentNullException(nameof(control));
        if (control.Definition != state.Definition
            || control.ProcessInstanceId != state.Continuation.ProcessInstanceId
            || control.CurrentAttempt.AttemptId != state.Continuation.ProcessAttemptId)
        {
            throw new ArgumentException(
                "Lifecycle control and canonical continuation must identify the same definition, Process instance, and current attempt.",
                nameof(control));
        }
        if (latestControlDecision is not null && !Equals(latestControlDecision.State, control))
        {
            throw new ArgumentException(
                "The latest lifecycle-control decision must project the retained control state.",
                nameof(latestControlDecision));
        }
        Disposition = disposition;
        LatestControlDecision = latestControlDecision;
        Emissions = emissions.IsDefault ? [] : emissions;
        InputAdmissions = inputAdmissions.IsDefault ? [] : inputAdmissions;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
        Evidence = evidence.IsDefault ? [] : evidence;
        Traces = traces.IsDefault ? [] : traces;
        ValidateTraces(Traces, Evidence, state, control);
        DurableOperations = durableOperations.IsDefault ? [] : durableOperations;
        DomainEventPublications = domainEventPublications.IsDefault ? [] : domainEventPublications;
        ValidateDomainEventPublications(Emissions, DomainEventPublications);
    }

    /// <summary>Latest canonical activation disposition.</summary>
    public ProcessActivationDisposition Disposition { get; }

    /// <summary>Complete canonical Process continuation.</summary>
    public ProcessContinuationState State { get; }

    /// <summary>Complete canonical lifecycle-control state and attempt lineage.</summary>
    public ProcessControlState Control { get; }

    /// <summary>Latest canonical control decision, retained as an operator-facing projection.</summary>
    public ProcessControlDecision? LatestControlDecision { get; }

    /// <summary>All canonical interactions emitted in activation order.</summary>
    public ImmutableArray<InteractionEnvelope> Emissions { get; }

    /// <summary>All canonical input dispositions in activation order.</summary>
    public ImmutableArray<ProcessInputReceipt> InputAdmissions { get; }

    /// <summary>All canonical interpreter diagnostics in activation order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Canonical evidence for every completed finite activation.</summary>
    public ImmutableArray<ProcessExecutionEvidence> Evidence { get; }

    /// <summary>
    /// Payload-safe normalized traces in activation order for decisions executed after trace retention began.
    /// </summary>
    /// <remarks>
    /// An empty or shorter collection can identify a result written before this adapter retained normalized traces;
    /// provider history cannot be used to fabricate the missing semantic evidence.
    /// </remarks>
    public ImmutableArray<NormalizedExecutionTrace> Traces { get; }

    /// <summary>Canonical durable Request results and complete ledgers in logical operation identity order.</summary>
    public ImmutableArray<DurableTaskDurableOperationResult> DurableOperations { get; }

    /// <summary>Target acknowledgements for exact canonical domain events in logical emission order.</summary>
    public ImmutableArray<DurableTaskDomainEventPublication> DomainEventPublications { get; }

    static void ValidateDomainEventPublications(
        ImmutableArray<InteractionEnvelope> emissions,
        ImmutableArray<DurableTaskDomainEventPublication> publications)
    {
        if (publications.Any(static publication => publication is null))
        {
            throw new ArgumentException(
                "Domain-event publication acknowledgements cannot contain null entries.",
                nameof(publications));
        }

        var domainEventEmissions = emissions.OfType<DomainEventEnvelope>().ToImmutableArray();
        var domainEvents = domainEventEmissions.ToDictionary(static domainEvent => domainEvent.Context.EmissionId);
        var byEmission = publications
            .GroupBy(static publication => publication.EmissionId)
            .ToDictionary(static group => group.Key);
        var expectedOrder = domainEventEmissions
            .Select(static domainEvent => domainEvent.Context.EmissionId)
            .Where(byEmission.ContainsKey);
        if (!expectedOrder.SequenceEqual(publications.Select(static publication => publication.EmissionId)))
        {
            throw new ArgumentException(
                "Domain-event publication acknowledgements must retain canonical emission order.",
                nameof(publications));
        }
        foreach (var (emissionId, matches) in byEmission)
        {
            if (matches.Count() != 1 || !domainEvents.TryGetValue(emissionId, out var domainEvent))
            {
                throw new ArgumentException(
                    $"Domain-event publication '{emissionId.Value}' requires one matching canonical emission.",
                    nameof(publications));
            }

            var publication = matches.Single();
            if (publication.DeduplicationKey != DomainEventPublicationDeduplicationKey.From(domainEvent)
                || publication.ContentFingerprint
                    != InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(domainEvent))
            {
                throw new ArgumentException(
                    $"Domain-event publication '{publication.EmissionId.Value}' does not match its canonical envelope.",
                    nameof(publications));
            }
        }
    }

    static void ValidateTraces(
        ImmutableArray<NormalizedExecutionTrace> traces,
        ImmutableArray<ProcessExecutionEvidence> evidence,
        ProcessContinuationState state,
        ProcessControlState control)
    {
        if (traces.Length > evidence.Length)
        {
            throw new ArgumentException(
                "Retained normalized traces cannot outnumber canonical activation evidence.",
                nameof(traces));
        }

        HashSet<(ProcessAttemptId Attempt, ActivationId Activation)> identities = [];
        var attempts = control.Attempts.Select(static attempt => attempt.AttemptId).ToHashSet();
        var evidenceOffset = evidence.Length - traces.Length;
        for (var index = 0; index < traces.Length; index++)
        {
            var trace = traces[index];
            if (trace is null)
            {
                throw new ArgumentException("A retained normalized Process trace cannot be null.", nameof(traces));
            }
            if (trace.Kind != Processes.IR.ProcessDefinitionDocuments.Kind
                || trace.Definition != state.Definition
                || trace.Definition != evidence[evidenceOffset + index].Definition
                || trace.Activation != evidence[evidenceOffset + index].Activation
                || trace.Continuation is not { } continuation
                || continuation.ProcessInstanceId != state.Continuation.ProcessInstanceId
                || !attempts.Contains(continuation.ProcessAttemptId))
            {
                throw new ArgumentException(
                    "Every retained normalized trace must match its ordered canonical activation evidence and identify this exact Process definition, instance, and retained attempt lineage.",
                    nameof(traces));
            }
            if (!identities.Add((continuation.ProcessAttemptId, trace.Activation)))
            {
                throw new ArgumentException(
                    "Retained normalized traces cannot repeat an activation within one Process attempt.",
                    nameof(traces));
            }
        }
    }
}

/// <summary>Result of idempotently scheduling one exact Process orchestration instance.</summary>
/// <param name="InstanceId">Stable Durable Task physical instance identity.</param>
/// <param name="Replayed">Whether an equal existing schedule was reused.</param>
public sealed record DurableTaskProcessScheduleResult(string InstanceId, bool Replayed);

static class DurableTaskSequentialProcessIdentities
{
    const string Version = "cohesive.adapters.durable-task.sequential-identities/v1";

    internal static string OrchestrationInstance(DurableTaskSequentialProcessStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var request = start.Receipt.Request;
        return OrchestrationInstance(
            request.Context.Authorization.AuthorityScope,
            request.InitialContinuation.ProcessInstanceId);
    }

    internal static string OrchestrationInstance(
        InteractionAuthorityScope scope,
        ProcessInstanceId processInstanceId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
        {
            throw new ArgumentException("A physical orchestration identity requires a Process instance.", nameof(processInstanceId));
        }
        return "cohesive-process:v1:sha256:" + Hash(
            "orchestration-instance",
            scope.Authority,
            scope.Tenant ?? string.Empty,
            processInstanceId.Value);
    }

    internal static string StartAdmissionIndex(
        InteractionAuthorityScope scope,
        string indexKind,
        string logicalIdentity)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalIdentity);
        return indexKind + ":v1:sha256:" + Hash(
            "start-admission-index",
            scope.Authority,
            scope.Tenant ?? string.Empty,
            indexKind,
            logicalIdentity);
    }

    internal static string ControlResponse(
        InteractionAuthorityScope scope,
        string requestFingerprint)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        return "cr:v1:" + Hash(
            "control-response",
            scope.Authority,
            scope.Tenant ?? string.Empty,
            requestFingerprint);
    }

    internal static string TerminalControl(
        InteractionAuthorityScope scope,
        ProcessInstanceId processInstanceId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
        {
            throw new ArgumentException(
                "A terminal-control identity requires a Process instance.",
                nameof(processInstanceId));
        }
        return "tc:v1:" + Hash(
            "terminal-control",
            scope.Authority,
            scope.Tenant ?? string.Empty,
            processInstanceId.Value);
    }

    internal static ActivationId Activation(ProcessContinuationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("durable-task-activation:v1:sha256:" + Hash(
            "activation",
            state.Continuation.ProcessInstanceId.Value,
            state.Continuation.ProcessAttemptId.Value,
            state.CompletedActivationCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal static ProcessSafePointId SafePoint(
        ProcessContinuationState before,
        ActivationId activation,
        ExecutionNodeId node) => new("durable-task-safe-point:v1:sha256:" + Hash(
        "safe-point",
        before.Continuation.ProcessInstanceId.Value,
        before.Continuation.ProcessAttemptId.Value,
        activation.Value,
        node.Value));

    internal static ActivationId CancellationActivation(
        ProcessContinuationState state,
        ProcessControlCommandId? commandId) => new("durable-task-cancellation:v1:sha256:" + Hash(
        "cancellation-activation",
        state.Continuation.ProcessInstanceId.Value,
        state.Continuation.ProcessAttemptId.Value,
        commandId?.Value ?? "control-revision-unavailable"));

    static string Hash(string purpose, params string[] fields)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Version);
        Append(hash, purpose);
        foreach (var field in fields)
        {
            Append(hash, field);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
