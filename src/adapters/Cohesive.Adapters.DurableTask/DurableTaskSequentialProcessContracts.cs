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

    /// <summary>Activity that materializes one exact Transition or Relation/Query operation.</summary>
    public const string HostOperationActivity = "Cohesive.Processes.HostOperation.v1";

    /// <summary>Activity that dispatches one fenced canonical durable Request attempt.</summary>
    public const string DurableOperationActivity = "Cohesive.Processes.DurableOperation.v1";

    /// <summary>Activity that reconciles one failed ambiguous canonical durable Request attempt.</summary>
    public const string DurableOperationReconciliationActivity =
        "Cohesive.Processes.DurableOperationReconciliation.v1";

    /// <summary>External event carrying one canonical interaction into a waiting Process.</summary>
    public const string InteractionEvent = "Cohesive.Processes.Interaction.v1";

    /// <summary>Parent-originated event carrying one exact propagated child-cancellation intent.</summary>
    public const string ChildCancellationEvent = "Cohesive.Processes.ChildCancellation.v1";
}

/// <summary>Exact canonical start evidence supplied to one Durable Task Process orchestration.</summary>
/// <remarks>
/// The Process document is not copied into this transport value. The receipt pins its exact identity, revision, and
/// fingerprint, which must resolve to a precompiled plan in the worker's immutable plan catalog. A target-owned
/// <see cref="Resume"/> snapshot may carry the complete canonical continuation across Continue-as-new history
/// rollover; it is derived evidence and never replaces the pinned plan as semantic authority.
/// </remarks>
public sealed record DurableTaskSequentialProcessStart
{
    /// <summary>Creates one exact Process start input.</summary>
    /// <param name="receipt">Durably admitted canonical Process-start evidence.</param>
    /// <param name="activationContext">Explicit authority, correlation, delivery, and provenance for emissions.</param>
    /// <param name="resume">Optional exact canonical state retained across one target history rollover.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="receipt"/> or <paramref name="activationContext"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The start and activation contexts have different authority scopes.</exception>
    [JsonConstructor]
    public DurableTaskSequentialProcessStart(
        ProcessStartReceipt receipt,
        ProcessActivationContext activationContext,
        DurableTaskSequentialProcessResume? resume = null)
    {
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        ActivationContext = activationContext ?? throw new ArgumentNullException(nameof(activationContext));
        if (receipt.Request.Context.Authorization.AuthorityScope != activationContext.AuthorityScope)
        {
            throw new ArgumentException(
                "Process-start and activation contexts must have the same authority scope.",
                nameof(activationContext));
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
                || resume.Result.State.Continuation != receipt.Request.InitialContinuation)
            {
                throw new ArgumentException(
                    "A Durable Task resume snapshot must retain the exact started definition and continuation.",
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
    }

    /// <summary>Durably admitted exact Process-start evidence.</summary>
    public ProcessStartReceipt Receipt { get; }

    /// <summary>Explicit context used for canonical interaction emissions.</summary>
    public ProcessActivationContext ActivationContext { get; }

    /// <summary>Optional complete canonical result retained at the preceding target history boundary.</summary>
    public DurableTaskSequentialProcessResume? Resume { get; }

    internal DurableTaskSequentialProcessStart ContinueFrom(DurableTaskSequentialProcessResult result) =>
        new(Receipt, ActivationContext, new(result));
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

/// <summary>Canonical semantic result and accumulated evidence from a Durable Task execution.</summary>
public sealed record DurableTaskSequentialProcessResult
{
    /// <summary>Creates an immutable Process execution projection.</summary>
    /// <param name="disposition">Latest canonical activation disposition.</param>
    /// <param name="state">Complete canonical replacement continuation.</param>
    /// <param name="emissions">All canonical interactions emitted in activation order.</param>
    /// <param name="inputAdmissions">All canonical input dispositions in activation order.</param>
    /// <param name="diagnostics">All canonical interpreter diagnostics in activation order.</param>
    /// <param name="evidence">Canonical evidence for every completed finite activation.</param>
    /// <param name="durableOperations">Canonical durable Request ledgers in logical operation identity order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unspecified.</exception>
    [JsonConstructor]
    public DurableTaskSequentialProcessResult(
        ProcessActivationDisposition disposition,
        ProcessContinuationState state,
        ImmutableArray<InteractionEnvelope> emissions = default,
        ImmutableArray<ProcessInputReceipt> inputAdmissions = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default,
        ImmutableArray<ProcessExecutionEvidence> evidence = default,
        ImmutableArray<DurableTaskDurableOperationResult> durableOperations = default)
    {
        if (!Enum.IsDefined(disposition) || disposition == ProcessActivationDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "A Process execution result requires an explicit disposition.");
        }

        State = state ?? throw new ArgumentNullException(nameof(state));
        Disposition = disposition;
        Emissions = emissions.IsDefault ? [] : emissions;
        InputAdmissions = inputAdmissions.IsDefault ? [] : inputAdmissions;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
        Evidence = evidence.IsDefault ? [] : evidence;
        DurableOperations = durableOperations.IsDefault ? [] : durableOperations;
    }

    /// <summary>Latest canonical activation disposition.</summary>
    public ProcessActivationDisposition Disposition { get; }

    /// <summary>Complete canonical Process continuation.</summary>
    public ProcessContinuationState State { get; }

    /// <summary>All canonical interactions emitted in activation order.</summary>
    public ImmutableArray<InteractionEnvelope> Emissions { get; }

    /// <summary>All canonical input dispositions in activation order.</summary>
    public ImmutableArray<ProcessInputReceipt> InputAdmissions { get; }

    /// <summary>All canonical interpreter diagnostics in activation order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Canonical evidence for every completed finite activation.</summary>
    public ImmutableArray<ProcessExecutionEvidence> Evidence { get; }

    /// <summary>Canonical durable Request results and complete ledgers in logical operation identity order.</summary>
    public ImmutableArray<DurableTaskDurableOperationResult> DurableOperations { get; }
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

    internal static ActivationId Activation(ProcessContinuationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("durable-task-activation:v1:sha256:" + Hash(
            "activation",
            state.Continuation.ProcessInstanceId.Value,
            state.Continuation.ProcessAttemptId.Value,
            state.CompletedActivationCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

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
