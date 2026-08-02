using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;

namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Finite disposition projected by the authoritative Motion DQ monitoring query.</summary>
public enum MotionDqMonitoringDisposition
{
    /// <summary>The case requires another finite monitoring occurrence.</summary>
    Continue,

    /// <summary>The evidence authority cleared the monitored subject.</summary>
    Cleared,

    /// <summary>The evidence authority requires escalation.</summary>
    Escalated,

    /// <summary>The independently owned monitoring case was cancelled.</summary>
    Cancelled,

    /// <summary>A newer monitoring case superseded this process.</summary>
    Superseded
}

/// <summary>Finite intervention selected from authoritative monitoring evidence.</summary>
public enum MotionDqInterventionKind
{
    /// <summary>Coaching is required.</summary>
    Coaching,

    /// <summary>A verbal warning is required.</summary>
    VerbalWarning,

    /// <summary>A monitoring interval is required.</summary>
    Monitoring,

    /// <summary>A probation interval is required.</summary>
    Probation,

    /// <summary>Training is required.</summary>
    Training,

    /// <summary>A post-training inspection is required.</summary>
    PostTrainingInspection,

    /// <summary>A road test is required.</summary>
    RoadTest,

    /// <summary>A ride-along is required.</summary>
    RideAlong
}

/// <summary>Stable reference to an independently owned monitoring case.</summary>
/// <param name="CaseId">Stable case identity.</param>
public sealed record MotionDqMonitoringCaseReference(string CaseId);

/// <summary>Explicit evidence window used by one monitoring occurrence.</summary>
/// <param name="StartsAtUtc">Inclusive authoritative start instant.</param>
/// <param name="EndsAtUtc">Exclusive authoritative end instant.</param>
public sealed record MotionDqMonitoringWindow(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

/// <summary>Requests one human intervention selected from authoritative evidence.</summary>
/// <param name="CaseId">Stable monitoring-case identity.</param>
/// <param name="EvidenceRevision">Monotonic revision of the externally retained evidence history.</param>
/// <param name="EvidenceSnapshotId">Reference to the exact external evidence snapshot used for the decision.</param>
/// <param name="LatestTelematicsEventId">Reference to the latest telematics event included in the snapshot.</param>
/// <param name="Intervention">Finite human intervention to perform.</param>
/// <param name="Window">Evidence window evaluated for this occurrence.</param>
/// <param name="NextEvaluationDueAtUtc">Absolute deadline for the next monitoring evaluation.</param>
public sealed record MotionDqInterventionWorkRequest(
    string CaseId,
    long EvidenceRevision,
    string EvidenceSnapshotId,
    string LatestTelematicsEventId,
    MotionDqInterventionKind Intervention,
    MotionDqMonitoringWindow Window,
    DateTimeOffset NextEvaluationDueAtUtc);

/// <summary>Authoritative result of evaluating one monitoring occurrence.</summary>
/// <param name="Disposition">Finite decision over the current evidence history.</param>
/// <param name="Work">Exact occurrence evidence and human work selected by the query.</param>
/// <remarks>
/// <paramref name="Work"/> remains present for terminal decisions so the result has one portable closed shape. It
/// identifies the evidence that produced the terminal disposition; the Process does not issue it as human work.
/// </remarks>
public sealed record MotionDqMonitoringObservation(
    MotionDqMonitoringDisposition Disposition,
    MotionDqInterventionWorkRequest Work);

/// <summary>Reference to human work owned outside Process coordination state.</summary>
/// <param name="WorkItemId">Stable external work-item identity.</param>
public sealed record MotionDqInterventionWorkReference(string WorkItemId);

/// <summary>Endogenous evidence that one external human-work item completed.</summary>
/// <param name="CaseId">Monitoring case addressed by the work item.</param>
/// <param name="WorkItemId">Exact external work item that completed.</param>
/// <param name="CompletionEvidenceId">Reference to completion evidence retained by its owning module.</param>
/// <param name="CompletedAtUtc">Authoritative completion instant.</param>
public sealed record MotionDqInterventionCompleted(
    string CaseId,
    string WorkItemId,
    string CompletionEvidenceId,
    DateTimeOffset CompletedAtUtc);

/// <summary>Endogenous evidence that a newer monitoring case superseded this one.</summary>
/// <param name="CaseId">Monitoring case being superseded.</param>
/// <param name="SupersedingCaseId">New authoritative monitoring-case identity.</param>
/// <param name="EvidenceId">Reference to the retained supersession evidence.</param>
public sealed record MotionDqMonitoringSupersession(
    string CaseId,
    string SupersedingCaseId,
    string EvidenceId);

/// <summary>Canonical interactions used by the Motion DQ monitoring Process fixture.</summary>
public sealed class MotionDqMonitoringInteractionContracts
{
    static readonly ExecutionRevisionId Revision = new("revision/1");
    static readonly IClrTypeRefMapper TypeMapper = new DefaultClrTypeRefMapper();

    MotionDqMonitoringInteractionContracts(
        SignalContractReference interventionCompleted,
        SignalContractReference caseCancellation,
        SignalContractReference caseSupersession,
        RequestContractReference scheduleIntervention,
        DurableRequestBinding scheduleInterventionBinding,
        ImmutableArray<ExecutionDefinitionDocument> documents,
        InteractionContractCatalog catalog)
    {
        InterventionCompletedSignal = interventionCompleted;
        CaseCancellationSignal = caseCancellation;
        CaseSupersessionSignal = caseSupersession;
        ScheduleInterventionRequest = scheduleIntervention;
        ScheduleInterventionBinding = scheduleInterventionBinding;
        Documents = documents;
        Catalog = catalog;
    }

    /// <summary>Human-work creation completed with an external work-item reference.</summary>
    public static RequestTerminalOutcomeId InterventionScheduledOutcome { get; } = new("scheduled");

    /// <summary>Human-work creation failed terminally.</summary>
    public static RequestTerminalOutcomeId InterventionSchedulingFailedOutcome { get; } = new("failed");

    /// <summary>Canonical version-one monitoring interactions.</summary>
    public static MotionDqMonitoringInteractionContracts Version1 { get; } = CreateVersion1();

    /// <summary>Typed endogenous evidence that external human work completed.</summary>
    public SignalContractReference InterventionCompletedSignal { get; }

    /// <summary>Typed endogenous evidence that the monitoring case was cancelled.</summary>
    public SignalContractReference CaseCancellationSignal { get; }

    /// <summary>Typed endogenous evidence that a newer monitoring case superseded this one.</summary>
    public SignalContractReference CaseSupersessionSignal { get; }

    /// <summary>Typed Request that creates one external human-work item.</summary>
    public RequestContractReference ScheduleInterventionRequest { get; }

    /// <summary>Durable execution policy for <see cref="ScheduleInterventionRequest"/>.</summary>
    public DurableRequestBinding ScheduleInterventionBinding { get; }

    /// <summary>Canonical Signal, Request, and Reply documents in deterministic order.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Documents { get; }

    /// <summary>Validated exact-reference catalog assembled from <see cref="Documents"/>.</summary>
    public InteractionContractCatalog Catalog { get; }

    static MotionDqMonitoringInteractionContracts CreateVersion1()
    {
        var completed = Signal(
            definitionId: "interaction/motion-dq/monitoring/intervention-completed",
            payload: Schema<MotionDqInterventionCompleted>("motion-dq/monitoring/intervention-completed/v1"));
        var cancellation = Signal(
            definitionId: "interaction/motion-dq/monitoring/cancelled",
            payload: Schema<MotionDqCancellation>("motion-dq/monitoring/cancelled/v1"));
        var supersession = Signal(
            definitionId: "interaction/motion-dq/monitoring/superseded",
            payload: Schema<MotionDqMonitoringSupersession>("motion-dq/monitoring/superseded/v1"));

        var scheduled = Schema<MotionDqInterventionWorkReference>(
            "motion-dq/monitoring/intervention-reference/v1");
        var failure = Schema<string>("motion-dq/monitoring/intervention-failure/v1");
        var requestDocument = InteractionContractDocuments.Create(
            new("interaction/motion-dq/monitoring/schedule-intervention"),
            Revision,
            new RequestContractDefinition(
                Schema<MotionDqInterventionWorkRequest>("motion-dq/monitoring/intervention-request/v1"),
                new(
                    terminalOutcomes:
                    [
                        new RequestResultDefinition(InterventionScheduledOutcome, scheduled),
                        new RequestFailureDefinition(InterventionSchedulingFailedOutcome, failure)
                    ],
                    timeout: RequestOptionalTerminalSemantics.Unsupported,
                    cancellation: RequestOptionalTerminalSemantics.Unsupported,
                    lateResult: RequestResultDisposition.Observe,
                    staleResult: RequestResultDisposition.Reject,
                    duplicateResult: RequestResultDisposition.ReusePriorDisposition,
                    retry: RequestRetrySemantics.StableIdentity,
                    ambiguousOutcome: RequestResolutionSemantics.TerminalFailure,
                    unresolvedOutcome: RequestResolutionSemantics.TerminalFailure,
                    retentionHorizon: TimeSpan.FromDays(90))),
            Provenance("schedule-intervention/request"));
        RequestContractReference request = new(Reference(requestDocument));
        var scheduledReply = Reply(
            definitionId: "interaction/motion-dq/monitoring/schedule-intervention/reply/scheduled",
            request,
            InterventionScheduledOutcome);
        var failedReply = Reply(
            definitionId: "interaction/motion-dq/monitoring/schedule-intervention/reply/failed",
            request,
            InterventionSchedulingFailedOutcome);
        var binding = new DurableRequestBinding(
            request: request,
            replies:
            [
                new(InterventionScheduledOutcome, new(Reference(scheduledReply))),
                new(InterventionSchedulingFailedOutcome, new(Reference(failedReply)))
            ],
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            timeoutAfter: null,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            terminalFailureOutcome: InterventionSchedulingFailedOutcome);

        ImmutableArray<ExecutionDefinitionDocument> documents =
        [
            completed,
            cancellation,
            supersession,
            requestDocument,
            scheduledReply,
            failedReply
        ];
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        if (!validation.IsValid || catalog is null)
        {
            throw new InvalidOperationException(
                $"Motion DQ monitoring interaction contracts are invalid: {Format(validation)}");
        }

        return new(
            interventionCompleted: new(Reference(completed)),
            caseCancellation: new(Reference(cancellation)),
            caseSupersession: new(Reference(supersession)),
            scheduleIntervention: request,
            scheduleInterventionBinding: binding,
            documents: documents,
            catalog: catalog);
    }

    static ExecutionDefinitionDocument Signal(string definitionId, InteractionValueSchema payload) =>
        InteractionContractDocuments.Create(
            new(definitionId),
            Revision,
            new SignalContractDefinition(payload),
            Provenance(definitionId));

    static ExecutionDefinitionDocument Reply(
        string definitionId,
        RequestContractReference request,
        RequestTerminalOutcomeId outcome) =>
        InteractionContractDocuments.Create(
            new(definitionId),
            Revision,
            new ReplyContractDefinition(request, outcome),
            Provenance(definitionId));

    static InteractionValueSchema Schema<TValue>(string revision) => new(
        new ValueContract(TypeMapper.Map(typeof(TValue), null)),
        new(revision));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance(string source) => new(
        new("cohesive-motion-dq-fixture", "1"),
        new($"ari-182/{source}"),
        DocumentOrigin.User);

    static string Format(DocumentValidationResult validation) => string.Join(
        "; ",
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
