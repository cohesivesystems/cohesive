using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Result of one Durable Task activity that attempted canonical Request dispatch.</summary>
public sealed record DurableTaskDurableOperationAttemptResult
{
    /// <summary>Creates completed adapter evidence or an explicit pre-I/O deadline disposition.</summary>
    /// <param name="observation">Typed adapter observation when dispatch completed before its deadline.</param>
    /// <param name="deadlineElapsed">Whether the activity proved the semantic deadline elapsed before adapter I/O.</param>
    /// <exception cref="ArgumentException">Exactly one result form was not supplied.</exception>
    [JsonConstructor]
    public DurableTaskDurableOperationAttemptResult(
        DurableOperationAttemptObservation? observation,
        bool deadlineElapsed)
    {
        if ((observation is null) == !deadlineElapsed)
        {
            throw new ArgumentException(
                "A durable operation activity must return exactly an observation or deadline evidence.");
        }
        Observation = observation;
        DeadlineElapsed = deadlineElapsed;
    }

    /// <summary>Typed adapter outcome or failure evidence.</summary>
    public DurableOperationAttemptObservation? Observation { get; }

    /// <summary>Whether the semantic deadline elapsed before adapter I/O began.</summary>
    public bool DeadlineElapsed { get; }
}

/// <summary>Result of one Durable Task activity that reconciled an ambiguous Request attempt.</summary>
public sealed record DurableTaskDurableOperationReconciliationResult
{
    /// <summary>Creates completed reconciliation evidence or an explicit pre-I/O deadline disposition.</summary>
    /// <param name="observation">Typed reconciliation observation when reconciliation ran before its deadline.</param>
    /// <param name="deadlineElapsed">Whether the activity proved the semantic deadline elapsed before reconciliation.</param>
    /// <exception cref="ArgumentException">Exactly one result form was not supplied.</exception>
    [JsonConstructor]
    public DurableTaskDurableOperationReconciliationResult(
        DurableOperationReconciliationObservation? observation,
        bool deadlineElapsed)
    {
        if ((observation is null) == !deadlineElapsed)
        {
            throw new ArgumentException(
                "A durable reconciliation activity must return exactly an observation or deadline evidence.");
        }
        Observation = observation;
        DeadlineElapsed = deadlineElapsed;
    }

    /// <summary>Confirmed outcome, proof of no execution, or unresolved evidence.</summary>
    public DurableOperationReconciliationObservation? Observation { get; }

    /// <summary>Whether the semantic deadline elapsed before reconciliation I/O began.</summary>
    public bool DeadlineElapsed { get; }
}

/// <summary>Why automatic Durable Task Request execution returned control to its owning Process interpreter.</summary>
public enum DurableTaskDurableOperationDisposition
{
    /// <summary>No disposition was selected; invalid in a completed result.</summary>
    Unspecified = 0,

    /// <summary>The canonical acknowledgement was dispositioned and produced one exact Process interaction input.</summary>
    ReplyReady = 1,

    /// <summary>Canonical policy requires typed terminal or escalation evidence not fabricated by this runtime.</summary>
    RecoveryRequired = 2,

    /// <summary>The semantic deadline elapsed and requires its exact authored typed timeout outcome.</summary>
    DeadlineElapsed = 3
}

/// <summary>Canonical durable Request state and optional Process input produced by the Durable Task interpreter.</summary>
public sealed record DurableTaskDurableOperationResult
{
    /// <summary>Creates one closed durable Request execution result.</summary>
    /// <param name="disposition">Reason automatic execution returned.</param>
    /// <param name="state">Complete canonical durable-operation ledger.</param>
    /// <param name="input">Exact admitted Reply input only for <see cref="DurableTaskDurableOperationDisposition.ReplyReady"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unspecified or unknown.</exception>
    /// <exception cref="ArgumentException">The disposition and optional input disagree.</exception>
    [JsonConstructor]
    public DurableTaskDurableOperationResult(
        DurableTaskDurableOperationDisposition disposition,
        DurableOperationState state,
        ProcessActivationInput? input = null)
    {
        if (!Enum.IsDefined(disposition) || disposition == DurableTaskDurableOperationDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "A durable operation disposition is required.");
        }
        if ((disposition == DurableTaskDurableOperationDisposition.ReplyReady) != (input is not null))
        {
            throw new ArgumentException("Only a Reply-ready durable operation carries a Process input.", nameof(input));
        }
        Disposition = disposition;
        State = state ?? throw new ArgumentNullException(nameof(state));
        Input = input;
    }

    /// <summary>Reason automatic execution returned.</summary>
    public DurableTaskDurableOperationDisposition Disposition { get; }

    /// <summary>Complete canonical durable-operation state and evidence ledger.</summary>
    public DurableOperationState State { get; }

    /// <summary>Exact admitted Reply input when available.</summary>
    public ProcessActivationInput? Input { get; }
}

enum DurableTaskDurableOperationCut
{
    BeforeDispatch,
    AfterDispatch,
    AfterAcknowledgement,
    BeforeReplyAdmission
}
