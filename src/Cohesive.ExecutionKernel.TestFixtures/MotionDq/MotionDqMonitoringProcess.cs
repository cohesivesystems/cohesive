using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using AuthoredMonitoringProcess = Cohesive.Processes.Authoring.Process<
    Cohesive.ExecutionKernel.TestFixtures.MotionDq.MotionDqMonitoringCaseReference,
    Cohesive.ExecutionKernel.TestFixtures.MotionDq.MotionDqMonitoringOutcome>;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Finite terminal outcomes of the Motion DQ monitoring Process fixture.</summary>
public enum MotionDqMonitoringOutcome
{
    /// <summary>The authoritative evidence query cleared the monitored subject.</summary>
    Cleared,

    /// <summary>The authoritative evidence query required escalation.</summary>
    Escalated,

    /// <summary>The monitoring authority or an endogenous signal cancelled the case.</summary>
    Cancelled,

    /// <summary>A newer monitoring case superseded this process.</summary>
    Superseded,

    /// <summary>The finite occurrence budget was exhausted.</summary>
    OccurrenceLimitReached,

    /// <summary>The authoritative evidence revision stopped advancing.</summary>
    EvidenceStalled,

    /// <summary>External human-work creation failed terminally.</summary>
    InterventionSchedulingFailed,

    /// <summary>An observation or interaction violated the declared coordination contract.</summary>
    CoordinationRejected
}

/// <summary>
/// Canonical Motion DQ monitoring Process built from exact query, durable interaction, wait, and recurrence semantics.
/// </summary>
/// <remarks>
/// The Process retains only its case reference, exact interaction references, and recurrence progress. Telematics
/// events, evidence history, human-work lifecycle, and monitoring-case state remain authoritative in their owning
/// modules. Every recurrence pass crosses <see cref="RepeatAcrossActivationProcessNode"/> and is therefore finite and
/// durable; the graph contains no free cycle, host polling loop, or ambient clock access.
/// </remarks>
public sealed class MotionDqMonitoringProcess
{
    static readonly IClrTypeRefMapper TypeMapper = new DefaultClrTypeRefMapper();
    static readonly ExecutionDefinitionReference ObservationQueryReference = new(
        new("relation/motion-dq/monitoring-observation"),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('8', 64)));

    MotionDqMonitoringProcess(
        AuthoredMonitoringProcess authored,
        ProcessDefinitionValidationContext linkingContext,
        ProcessCompilationResult compilation,
        MotionDqMonitoringInteractionContracts interactions,
        ProcessDefinitionLink observationQuery,
        ImmutableArray<ExecutionDefinitionDocument> documents)
    {
        Authored = authored;
        LinkingContext = linkingContext;
        Compilation = compilation;
        Plan = compilation.Plan
            ?? throw new ArgumentException("A Motion DQ monitoring artifact requires a compiled plan.", nameof(compilation));
        Interactions = interactions;
        ObservationQuery = observationQuery;
        Documents = documents;
    }

    /// <summary>Canonical version-one Motion DQ monitoring Process and exact dependencies.</summary>
    public static MotionDqMonitoringProcess Version1 { get; } = AuthorVersion1();

    /// <summary>Authors and links a fresh deterministic version-one fixture.</summary>
    /// <returns>A fresh artifact graph with the same normalized semantic fingerprint as <see cref="Version1"/>.</returns>
    /// <exception cref="InvalidOperationException">Authoring or linked Process compilation is invalid.</exception>
    public static MotionDqMonitoringProcess AuthorVersion1() => CreateVersion1();

    /// <summary>Typed authored Process whose canonical document is the semantic authority.</summary>
    public AuthoredMonitoringProcess Authored { get; }

    /// <summary>Canonical persisted Process document.</summary>
    public ExecutionDefinitionDocument Document => Authored.Document;

    /// <summary>Typed canonical Process IR projected from <see cref="Document"/>.</summary>
    public CanonicalProcessDefinition Definition => Plan.Definition;

    /// <summary>Exact fingerprinted Process reference.</summary>
    public ExecutionDefinitionReference Reference => Authored.Reference;

    /// <summary>Exact query and interaction evidence used for linked validation.</summary>
    public ProcessDefinitionValidationContext LinkingContext { get; }

    /// <summary>Target-independent compilation result.</summary>
    public ProcessCompilationResult Compilation { get; }

    /// <summary>Executable reference plan shared by reference and durable interpretations.</summary>
    public CompiledProcessPlan Plan { get; }

    /// <summary>Exact monitoring interaction contracts.</summary>
    public MotionDqMonitoringInteractionContracts Interactions { get; }

    /// <summary>Exact Relation/Query link that projects authoritative monitoring observations.</summary>
    public ProcessDefinitionLink ObservationQuery { get; }

    /// <summary>Canonical interaction documents followed by the Process document.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Documents { get; }

    /// <summary>Durable binding for external intervention-work creation.</summary>
    public DurableRequestBinding ScheduleInterventionBinding => Interactions.ScheduleInterventionBinding;

    static MotionDqMonitoringProcess CreateVersion1()
    {
        var interactions = MotionDqMonitoringInteractionContracts.Version1;
        var observationQuery = new ProcessDefinitionLink(
            definition: ObservationQueryReference,
            kind: ProcessDefinitionLinkKind.RelationQuery,
            input: Contract<MotionDqMonitoringCaseReference>(),
            result: Contract<MotionDqMonitoringObservation>());
        var linkingContext = new ProcessDefinitionValidationContext(
            definitions: [observationQuery],
            interactionContracts: interactions.Catalog);
        var authored = Author(interactions);
        RequireValid(authored.Validation, "context-free Process authoring");
        var compilation = authored.Compile(linkingContext);
        RequireValid(compilation.Validation, "linked Process compilation");
        if (compilation.Plan is null)
        {
            throw new InvalidOperationException("Linked Motion DQ monitoring compilation produced no reference plan.");
        }

        ImmutableArray<ExecutionDefinitionDocument> documents =
        [
            .. interactions.Documents,
            authored.Document
        ];
        return new(
            authored: authored,
            linkingContext: linkingContext,
            compilation: compilation,
            interactions: interactions,
            observationQuery: observationQuery,
            documents: documents);
    }

    static AuthoredMonitoringProcess Author(MotionDqMonitoringInteractionContracts interactions) =>
        ProcessAuthoring.Create<MotionDqMonitoringCaseReference, MotionDqMonitoringOutcome>(
            new(
                new("process/motion-dq/monitoring"),
                new("revision/1"),
                Identities.EvaluateObservation,
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance(),
                displayName: "Motion DQ monitoring and escalation",
                description: "Coordinates bounded evidence evaluation, human intervention, and durable recurrence."),
            process => AuthorGraph(process, interactions));

    static void AuthorGraph(
        ProcessBuilder<MotionDqMonitoringCaseReference, MotionDqMonitoringOutcome> process,
        MotionDqMonitoringInteractionContracts interactions)
    {
        var observation = process.Output<MotionDqMonitoringObservation>(Identities.EvaluateObservation, "observation");
        process.EvaluateRelation(
            Identities.EvaluateObservation,
            ObservationQueryReference,
            process.Input.Value,
            process.Continuation(
                process.Edge(Identities.EvaluateObservation, "evaluated", Identities.ClassifyObservation),
                observation));

        var disposition = observation.Field(static value => value.Disposition);
        process.Match(
            Identities.ClassifyObservation,
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            disposition,
            [
                process.MatchCase(
                    new("motion-dq/monitoring/observation/continue"),
                    disposition,
                    MotionDqMonitoringDisposition.Continue,
                    process.Edge(Identities.ClassifyObservation, "continue", Identities.ScheduleIntervention)),
                process.MatchCase(
                    new("motion-dq/monitoring/observation/cleared"),
                    disposition,
                    MotionDqMonitoringDisposition.Cleared,
                    process.Edge(Identities.ClassifyObservation, "cleared", Identities.Repeat)),
                process.MatchCase(
                    new("motion-dq/monitoring/observation/escalated"),
                    disposition,
                    MotionDqMonitoringDisposition.Escalated,
                    process.Edge(Identities.ClassifyObservation, "escalated", Identities.Repeat)),
                process.MatchCase(
                    new("motion-dq/monitoring/observation/cancelled"),
                    disposition,
                    MotionDqMonitoringDisposition.Cancelled,
                    process.Edge(Identities.ClassifyObservation, "cancelled", Identities.Repeat)),
                process.MatchCase(
                    new("motion-dq/monitoring/observation/superseded"),
                    disposition,
                    MotionDqMonitoringDisposition.Superseded,
                    process.Edge(Identities.ClassifyObservation, "superseded", Identities.Repeat))
            ],
            process.Fallback(
                new("motion-dq/monitoring/observation/invalid"),
                process.Edge(Identities.ClassifyObservation, "invalid", Identities.CoordinationRejected)));

        var work = observation.Field(static value => value.Work);
        var workReference = process.Output<MotionDqInterventionWorkReference>(Identities.ScheduleIntervention, "scheduled");
        process.Request(
            Identities.ScheduleIntervention,
            interactions.ScheduleInterventionRequest,
            work,
            [
                process.RequestOutcome(
                    new("motion-dq/monitoring/intervention/scheduled"),
                    MotionDqMonitoringInteractionContracts.InterventionScheduledOutcome,
                    process.Continuation(
                        process.Edge(Identities.ScheduleIntervention, "scheduled", Identities.AwaitIntervention),
                        workReference)),
                process.RequestOutcome(
                    new("motion-dq/monitoring/intervention/failed"),
                    MotionDqMonitoringInteractionContracts.InterventionSchedulingFailedOutcome,
                    process.Continuation(process.Edge(
                        Identities.ScheduleIntervention,
                        "failed",
                        Identities.InterventionSchedulingFailed)))
            ]);

        AuthorInterventionAwait(process, interactions, observation, workReference);

        var continueWhen = process.Equal(
            disposition,
            process.Constant(MotionDqMonitoringDisposition.Continue));
        process.RepeatAcrossActivation(
            Identities.Repeat,
            continueWhen,
            work.Field(static value => value.EvidenceRevision),
            new(maximumOccurrences: 365, maximumUnchangedProgressOccurrences: 2),
            process.Edge(Identities.Repeat, "repeat", Identities.EvaluateObservation),
            process.Edge(Identities.Repeat, "completed", Identities.ReturnDisposition),
            process.Edge(Identities.Repeat, "exhausted", Identities.OccurrenceLimitReached),
            process.Edge(Identities.Repeat, "stalled", Identities.EvidenceStalled));

        AuthorTerminalDisposition(process, disposition);
        process.Return(Identities.Cleared, process.Constant(MotionDqMonitoringOutcome.Cleared));
        process.Return(Identities.Escalated, process.Constant(MotionDqMonitoringOutcome.Escalated));
        process.Return(Identities.Cancelled, process.Constant(MotionDqMonitoringOutcome.Cancelled));
        process.Return(Identities.Superseded, process.Constant(MotionDqMonitoringOutcome.Superseded));
        process.Fail(
            Identities.OccurrenceLimitReached,
            process.Constant(MotionDqMonitoringOutcome.OccurrenceLimitReached));
        process.Fail(Identities.EvidenceStalled, process.Constant(MotionDqMonitoringOutcome.EvidenceStalled));
        process.Fail(
            Identities.InterventionSchedulingFailed,
            process.Constant(MotionDqMonitoringOutcome.InterventionSchedulingFailed));
        process.Fail(
            Identities.CoordinationRejected,
            process.Constant(MotionDqMonitoringOutcome.CoordinationRejected));
    }

    static void AuthorInterventionAwait(
        ProcessBuilder<MotionDqMonitoringCaseReference, MotionDqMonitoringOutcome> process,
        MotionDqMonitoringInteractionContracts interactions,
        ProcessBinding<MotionDqMonitoringObservation> observation,
        ProcessBinding<MotionDqInterventionWorkReference> workReference)
    {
        var completed = process.Output<MotionDqInterventionCompleted>(Identities.AwaitIntervention, "completed");
        var cancellation = process.Output<MotionDqCancellation>(Identities.AwaitIntervention, "cancelled");
        var supersession = process.Output<MotionDqMonitoringSupersession>(Identities.AwaitIntervention, "superseded");
        var caseId = process.Input.Field(static value => value.CaseId);
        var completedGuard = process.And(
            process.Equal(completed.Field(static value => value.CaseId), caseId),
            process.Equal(
                completed.Field(static value => value.WorkItemId),
                workReference.Field(static value => value.WorkItemId)));
        completedGuard = process.And(
            completedGuard,
            process.NotEqual(
                completed.Field(static value => value.CompletionEvidenceId),
                process.Constant(string.Empty)));
        var cancellationGuard = process.Equal(
            cancellation.Field(static value => value.CaseId),
            caseId);
        var supersessionGuard = process.And(
            process.Equal(supersession.Field(static value => value.CaseId), caseId),
            process.NotEqual(
                supersession.Field(static value => value.SupersedingCaseId),
                caseId));

        process.AwaitMatch(
            Identities.AwaitIntervention,
            ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
            [
                process.AwaitInteractionClause(
                    new("motion-dq/monitoring/intervention/cancelled"),
                    interactions.CaseCancellationSignal,
                    cancellation,
                    requestObligation: null,
                    cancellationGuard,
                    priority: 100,
                    process.Continuation(process.Edge(
                        Identities.AwaitIntervention,
                        "cancelled",
                        Identities.Cancelled))),
                process.AwaitInteractionClause(
                    new("motion-dq/monitoring/intervention/superseded"),
                    interactions.CaseSupersessionSignal,
                    supersession,
                    requestObligation: null,
                    supersessionGuard,
                    priority: 90,
                    process.Continuation(process.Edge(
                        Identities.AwaitIntervention,
                        "superseded",
                        Identities.Superseded))),
                process.AwaitInteractionClause(
                    new("motion-dq/monitoring/intervention/completed"),
                    interactions.InterventionCompletedSignal,
                    completed,
                    requestObligation: null,
                    completedGuard,
                    priority: 80,
                    process.Continuation(process.Edge(
                        Identities.AwaitIntervention,
                        "completed",
                        Identities.Repeat))),
                process.AwaitTimerClause(
                    new("motion-dq/monitoring/intervention/evaluation-due"),
                    observation.Field(static value => value.Work.NextEvaluationDueAtUtc),
                    priority: 0,
                    process.Continuation(process.Edge(
                        Identities.AwaitIntervention,
                        "evaluation-due",
                        Identities.Repeat)))
            ],
            lateInput: ProcessAwaitInputDisposition.Observe,
            staleInput: ProcessAwaitInputDisposition.Reject,
            duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
            missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
            retentionHorizon: TimeSpan.FromDays(90));
    }

    static void AuthorTerminalDisposition(
        ProcessBuilder<MotionDqMonitoringCaseReference, MotionDqMonitoringOutcome> process,
        ProcessValue<MotionDqMonitoringDisposition> disposition) =>
        process.Match(
            Identities.ReturnDisposition,
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            disposition,
            [
                process.MatchCase(
                    new("motion-dq/monitoring/terminal-disposition/cleared"),
                    disposition,
                    MotionDqMonitoringDisposition.Cleared,
                    process.Edge(Identities.ReturnDisposition, "cleared", Identities.Cleared)),
                process.MatchCase(
                    new("motion-dq/monitoring/terminal-disposition/escalated"),
                    disposition,
                    MotionDqMonitoringDisposition.Escalated,
                    process.Edge(Identities.ReturnDisposition, "escalated", Identities.Escalated)),
                process.MatchCase(
                    new("motion-dq/monitoring/terminal-disposition/cancelled"),
                    disposition,
                    MotionDqMonitoringDisposition.Cancelled,
                    process.Edge(Identities.ReturnDisposition, "cancelled", Identities.Cancelled)),
                process.MatchCase(
                    new("motion-dq/monitoring/terminal-disposition/superseded"),
                    disposition,
                    MotionDqMonitoringDisposition.Superseded,
                    process.Edge(Identities.ReturnDisposition, "superseded", Identities.Superseded))
            ],
            process.Fallback(
                new("motion-dq/monitoring/terminal-disposition/invalid"),
                process.Edge(Identities.ReturnDisposition, "invalid", Identities.CoordinationRejected)));

    static ValueContract Contract<TValue>() => new(TypeMapper.Map(typeof(TValue), null));

    static ExecutionProvenance Provenance() => new(
        new("cohesive-motion-dq-fixture", "1"),
        new("ari-182/motion-dq-monitoring"),
        DocumentOrigin.User);

    static void RequireValid(DocumentValidationResult validation, string stage)
    {
        if (validation.IsValid)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Motion DQ monitoring {stage} failed: {string.Join(
                "; ",
                validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"))}");
    }

    static class Identities
    {
        public static readonly ExecutionNodeId EvaluateObservation = new("motion-dq/monitoring/evaluate-observation");
        public static readonly ExecutionNodeId ClassifyObservation = new("motion-dq/monitoring/classify-observation");
        public static readonly ExecutionNodeId ScheduleIntervention = new("motion-dq/monitoring/schedule-intervention");
        public static readonly ExecutionNodeId AwaitIntervention = new("motion-dq/monitoring/await-intervention");
        public static readonly ExecutionNodeId Repeat = new("motion-dq/monitoring/repeat-across-activation");
        public static readonly ExecutionNodeId ReturnDisposition = new("motion-dq/monitoring/return-disposition");
        public static readonly ExecutionNodeId Cleared = new("motion-dq/monitoring/terminal/cleared");
        public static readonly ExecutionNodeId Escalated = new("motion-dq/monitoring/terminal/escalated");
        public static readonly ExecutionNodeId Cancelled = new("motion-dq/monitoring/terminal/cancelled-by-input");
        public static readonly ExecutionNodeId Superseded = new("motion-dq/monitoring/terminal/superseded-by-input");
        public static readonly ExecutionNodeId OccurrenceLimitReached = new("motion-dq/monitoring/terminal/occurrence-limit");
        public static readonly ExecutionNodeId EvidenceStalled = new("motion-dq/monitoring/terminal/evidence-stalled");
        public static readonly ExecutionNodeId InterventionSchedulingFailed =
            new("motion-dq/monitoring/terminal/intervention-scheduling-failed");
        public static readonly ExecutionNodeId CoordinationRejected =
            new("motion-dq/monitoring/terminal/coordination-rejected");
    }
}
