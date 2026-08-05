using Cohesive.Execution;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;

namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Human-facing expression authoring for the canonical Motion DQ monitoring Process.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class MotionDqMonitoringProcessDefinition
{
    static readonly MotionDqMonitoringInteractionContracts Interactions =
        MotionDqMonitoringInteractionContracts.Version1;

    static async ProcessTask<MotionDqMonitoringOutcome> Run(
        ProcessContext process,
        MotionDqMonitoringCaseReference input)
    {
        async ProcessTask<MotionDqMonitoringObservation> Observe()
        {
            var observation = await process.Query<MotionDqMonitoringObservation>(
                relation: MotionDqMonitoringProcess.ObservationQueryReference,
                input: input,
                id: MotionDqMonitoringProcess.Identities.EvaluateObservation,
                nextRole: "evaluated",
                outputRole: "observation");

            async ProcessTask Continue()
            {
                async ProcessTask Scheduled(MotionDqInterventionWorkReference workReference)
                {
                    async ProcessTask Cancelled(MotionDqCancellation cancellation)
                    {
                        await process.Succeed(
                            MotionDqMonitoringOutcome.Cancelled,
                            MotionDqMonitoringProcess.Identities.Cancelled);
                    }

                    async ProcessTask Superseded(MotionDqMonitoringSupersession supersession)
                    {
                        await process.Succeed(
                            MotionDqMonitoringOutcome.Superseded,
                            MotionDqMonitoringProcess.Identities.Superseded);
                    }

                    async ProcessTask Completed(MotionDqInterventionCompleted completed)
                    {
                    }

                    async ProcessTask EvaluationDue()
                    {
                    }

                    await process.AwaitMatch(
                        clauses:
                        [
                            process.Signal<MotionDqCancellation>(
                                contract: Interactions.CaseCancellationSignal,
                                branch: Cancelled,
                                priority: 100,
                                when: cancellation => cancellation.CaseId == input.CaseId,
                                id: MotionDqMonitoringProcess.Identities.InterventionCancelled,
                                role: "cancelled",
                                edgeOwner: MotionDqMonitoringProcess.Identities.AwaitIntervention,
                                outputRole: "cancelled",
                                outputOwner: MotionDqMonitoringProcess.Identities.AwaitIntervention),
                            process.Signal<MotionDqMonitoringSupersession>(
                                contract: Interactions.CaseSupersessionSignal,
                                branch: Superseded,
                                priority: 90,
                                when: supersession =>
                                    supersession.CaseId == input.CaseId
                                    && supersession.SupersedingCaseId != input.CaseId,
                                id: MotionDqMonitoringProcess.Identities.InterventionSuperseded,
                                role: "superseded",
                                edgeOwner: MotionDqMonitoringProcess.Identities.AwaitIntervention,
                                outputRole: "superseded",
                                outputOwner: MotionDqMonitoringProcess.Identities.AwaitIntervention),
                            process.Signal<MotionDqInterventionCompleted>(
                                contract: Interactions.InterventionCompletedSignal,
                                branch: Completed,
                                priority: 80,
                                when: completed =>
                                    completed.CaseId == input.CaseId
                                    && completed.WorkItemId == workReference.WorkItemId
                                    && completed.CompletionEvidenceId != "",
                                id: MotionDqMonitoringProcess.Identities.InterventionCompleted,
                                role: "completed",
                                edgeOwner: MotionDqMonitoringProcess.Identities.AwaitIntervention,
                                outputRole: "completed",
                                outputOwner: MotionDqMonitoringProcess.Identities.AwaitIntervention),
                            process.Deadline(
                                dueAt: observation.Work.NextEvaluationDueAtUtc,
                                branch: EvaluationDue,
                                priority: 0,
                                id: MotionDqMonitoringProcess.Identities.InterventionEvaluationDue,
                                role: "evaluation-due",
                                edgeOwner: MotionDqMonitoringProcess.Identities.AwaitIntervention)
                        ],
                        arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                        lateInput: ProcessAwaitInputDisposition.Observe,
                        staleInput: ProcessAwaitInputDisposition.Reject,
                        duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
                        missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
                        retentionHorizon: TimeSpan.FromDays(90),
                        id: MotionDqMonitoringProcess.Identities.AwaitIntervention);
                }

                async ProcessTask SchedulingFailed()
                {
                    await process.Terminate(
                        MotionDqMonitoringOutcome.InterventionSchedulingFailed,
                        MotionDqMonitoringProcess.Identities.InterventionSchedulingFailed);
                }

                await process.Effect(
                    contract: Interactions.ScheduleInterventionRequest,
                    input: observation.Work,
                    outcomes:
                    [
                        process.Outcome<MotionDqInterventionWorkReference>(
                            outcome: MotionDqMonitoringInteractionContracts.InterventionScheduledOutcome,
                            branch: Scheduled,
                            id: MotionDqMonitoringProcess.Identities.InterventionScheduled,
                            role: "scheduled",
                            edgeOwner: MotionDqMonitoringProcess.Identities.ScheduleIntervention,
                            outputRole: "scheduled",
                            outputOwner: MotionDqMonitoringProcess.Identities.ScheduleIntervention),
                        process.Outcome(
                            outcome: MotionDqMonitoringInteractionContracts.InterventionSchedulingFailedOutcome,
                            branch: SchedulingFailed,
                            id: MotionDqMonitoringProcess.Identities.InterventionScheduleFailed,
                            role: "failed",
                            edgeOwner: MotionDqMonitoringProcess.Identities.ScheduleIntervention)
                    ],
                    id: MotionDqMonitoringProcess.Identities.ScheduleIntervention);
            }

            async ProcessTask Cleared()
            {
            }

            async ProcessTask Escalated()
            {
            }

            async ProcessTask Cancelled()
            {
            }

            async ProcessTask Superseded()
            {
            }

            async ProcessTask Invalid()
            {
                await process.Terminate(
                    MotionDqMonitoringOutcome.CoordinationRejected,
                    MotionDqMonitoringProcess.Identities.CoordinationRejected);
            }

            await process.Match(
                value: observation.Disposition,
                selection: CaseSelection.OrderedFirstMatch,
                completeness: BranchCompleteness.Fallback,
                cases:
                [
                    process.Case(
                        MotionDqMonitoringDisposition.Continue,
                        Continue,
                        MotionDqMonitoringProcess.Identities.ObservationContinue,
                        role: "continue",
                        edgeOwner: MotionDqMonitoringProcess.Identities.ClassifyObservation),
                    process.Case(
                        MotionDqMonitoringDisposition.Cleared,
                        Cleared,
                        MotionDqMonitoringProcess.Identities.ObservationCleared,
                        role: "cleared",
                        edgeOwner: MotionDqMonitoringProcess.Identities.ClassifyObservation),
                    process.Case(
                        MotionDqMonitoringDisposition.Escalated,
                        Escalated,
                        MotionDqMonitoringProcess.Identities.ObservationEscalated,
                        role: "escalated",
                        edgeOwner: MotionDqMonitoringProcess.Identities.ClassifyObservation),
                    process.Case(
                        MotionDqMonitoringDisposition.Cancelled,
                        Cancelled,
                        MotionDqMonitoringProcess.Identities.ObservationCancelled,
                        role: "cancelled",
                        edgeOwner: MotionDqMonitoringProcess.Identities.ClassifyObservation),
                    process.Case(
                        MotionDqMonitoringDisposition.Superseded,
                        Superseded,
                        MotionDqMonitoringProcess.Identities.ObservationSuperseded,
                        role: "superseded",
                        edgeOwner: MotionDqMonitoringProcess.Identities.ClassifyObservation)
                ],
                fallback: Invalid,
                id: MotionDqMonitoringProcess.Identities.ClassifyObservation,
                fallbackId: MotionDqMonitoringProcess.Identities.ObservationInvalid,
                fallbackRole: "invalid",
                fallbackEdgeOwner: MotionDqMonitoringProcess.Identities.ClassifyObservation);
            return observation;
        }

        async ProcessTask OccurrenceLimitReached()
        {
            await process.Terminate(
                MotionDqMonitoringOutcome.OccurrenceLimitReached,
                MotionDqMonitoringProcess.Identities.OccurrenceLimitReached);
        }

        async ProcessTask EvidenceStalled()
        {
            await process.Terminate(
                MotionDqMonitoringOutcome.EvidenceStalled,
                MotionDqMonitoringProcess.Identities.EvidenceStalled);
        }

        var final = await process.RepeatAcrossActivation(
            occurrence: Observe(),
            continueWhen: observation => observation.Disposition == MotionDqMonitoringDisposition.Continue,
            progress: observation => observation.Work.EvidenceRevision,
            policy: new(maximumOccurrences: 365, maximumUnchangedProgressOccurrences: 2),
            exhausted: OccurrenceLimitReached,
            stalled: EvidenceStalled,
            id: MotionDqMonitoringProcess.Identities.Repeat);

        async ProcessTask ReturnCleared()
        {
            await process.Succeed(
                MotionDqMonitoringOutcome.Cleared,
                MotionDqMonitoringProcess.Identities.Cleared);
        }

        async ProcessTask ReturnEscalated()
        {
            await process.Succeed(
                MotionDqMonitoringOutcome.Escalated,
                MotionDqMonitoringProcess.Identities.Escalated);
        }

        async ProcessTask ReturnCancelled()
        {
            await process.Succeed(
                MotionDqMonitoringOutcome.Cancelled,
                MotionDqMonitoringProcess.Identities.Cancelled);
        }

        async ProcessTask ReturnSuperseded()
        {
            await process.Succeed(
                MotionDqMonitoringOutcome.Superseded,
                MotionDqMonitoringProcess.Identities.Superseded);
        }

        async ProcessTask RejectDisposition()
        {
            await process.Terminate(
                MotionDqMonitoringOutcome.CoordinationRejected,
                MotionDqMonitoringProcess.Identities.CoordinationRejected);
        }

        await process.Match(
            value: final.Disposition,
            selection: CaseSelection.OrderedFirstMatch,
            completeness: BranchCompleteness.Fallback,
            cases:
            [
                process.Case(
                    MotionDqMonitoringDisposition.Cleared,
                    ReturnCleared,
                    MotionDqMonitoringProcess.Identities.TerminalCleared,
                    role: "cleared",
                    edgeOwner: MotionDqMonitoringProcess.Identities.ReturnDisposition),
                process.Case(
                    MotionDqMonitoringDisposition.Escalated,
                    ReturnEscalated,
                    MotionDqMonitoringProcess.Identities.TerminalEscalated,
                    role: "escalated",
                    edgeOwner: MotionDqMonitoringProcess.Identities.ReturnDisposition),
                process.Case(
                    MotionDqMonitoringDisposition.Cancelled,
                    ReturnCancelled,
                    MotionDqMonitoringProcess.Identities.TerminalCancelled,
                    role: "cancelled",
                    edgeOwner: MotionDqMonitoringProcess.Identities.ReturnDisposition),
                process.Case(
                    MotionDqMonitoringDisposition.Superseded,
                    ReturnSuperseded,
                    MotionDqMonitoringProcess.Identities.TerminalSuperseded,
                    role: "superseded",
                    edgeOwner: MotionDqMonitoringProcess.Identities.ReturnDisposition)
            ],
            fallback: RejectDisposition,
            id: MotionDqMonitoringProcess.Identities.ReturnDisposition,
            fallbackId: MotionDqMonitoringProcess.Identities.TerminalInvalid,
            fallbackRole: "invalid",
            fallbackEdgeOwner: MotionDqMonitoringProcess.Identities.ReturnDisposition);
        return process.Unreachable<MotionDqMonitoringOutcome>();
    }
}
