using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Versioned Durable Task realization-planning profile for canonical Processes.</summary>
/// <remarks>
/// This profile describes how a generic Durable Task interpreter is intended to preserve canonical Process
/// requirements. It is planning evidence, not an executable-capability advertisement. The adapter must not admit
/// execution from this profile until an interpreter has implemented and conformance-tested the selected closure.
/// </remarks>
public static class DurableTaskProcessTargetProfile
{
    /// <summary>Stable Durable Task Scheduler Process interpretation-target identity.</summary>
    public static ProcessInterpreterTargetId Target { get; } = new(
        "cohesive.adapters.durable-task.scheduler");

    /// <summary>Stable identity of the initial realization-planning profile.</summary>
    public static ProcessInterpreterCapabilityProfileId PlanningProfileId { get; } = new(
        "cohesive.adapters.durable-task.scheduler/realization-planning-v1");

    /// <summary>
    /// Boundary requiring every externally visible effect to supply stable idempotency or authored reconciliation.
    /// </summary>
    public static ProcessInterpreterOperatingBoundaryId ExternalEffectDeliveryBoundary { get; } = new(
        "durable-task/boundary/external-effect-idempotency-or-reconciliation/v1");

    /// <summary>Boundary requiring finite fan-out, recurrence, concurrency, capacity, and history growth.</summary>
    public static ProcessInterpreterOperatingBoundaryId FiniteWorkBoundary { get; } = new(
        "durable-task/boundary/finite-work-and-history/v1");

    /// <summary>
    /// Boundary requiring payload limits, redaction, and exact content-addressed externalization to be validated.
    /// </summary>
    public static ProcessInterpreterOperatingBoundaryId PayloadBoundary { get; } = new(
        "durable-task/boundary/payload-limit-redaction-and-externalization/v1");

    /// <summary>
    /// Complete initial planning profile. Every currently declared canonical construct and guarantee has one explicit
    /// native, composed, constrained, or unavailable disposition.
    /// </summary>
    public static ProcessInterpreterCapabilityProfile Planning { get; } = CreatePlanningProfile();

    static ProcessInterpreterCapabilityProfile CreatePlanningProfile() => new(
        PlanningProfileId,
        Target,
        [
            ComposedConstruct(
                ProcessWireNames.InvokeTransitionNode,
                "activity-transition",
                ["activity", "canonical-transition-decision-and-receipt"]),
            ComposedConstruct(
                ProcessWireNames.EvaluateRelationNode,
                "activity-relation",
                ["activity", "canonical-relation-result-and-occurrence"]),
            ComposedConstruct(
                ProcessWireNames.RequestNode,
                "activity-request-protocol",
                ["activity", "canonical-request-state", "external-event"]),
            ComposedConstruct(
                ProcessWireNames.EmitEventNode,
                "activity-outbox-publication",
                ["activity", "canonical-emission-identity", "canonical-outbox"]),
            ComposedConstruct(
                ProcessWireNames.SendSignalNode,
                "event-or-activity-signal",
                ["activity", "canonical-signal-evidence", "external-event"]),
            NativeConstruct(ProcessWireNames.ChoiceNode, "deterministic-orchestrator-decision"),
            NativeConstruct(ProcessWireNames.MatchNode, "deterministic-orchestrator-match"),
            ComposedConstruct(
                ProcessWireNames.ForkNode,
                "task-fan-out",
                ["canonical-fork-membership", "orchestrator-task-scheduling"]),
            ComposedConstruct(
                ProcessWireNames.JoinNode,
                "task-arbitration",
                ["canonical-join-decision", "orchestrator-task-scheduling"]),
            ComposedConstruct(
                ProcessWireNames.AwaitMatchNode,
                "external-event-and-timer-arbitration",
                ["canonical-inbox-arbitration", "durable-timer", "external-event"]),
            ComposedConstruct(
                ProcessWireNames.TimerNode,
                "durable-timer",
                ["canonical-timer-occurrence", "durable-timer"]),
            ComposedConstruct(
                ProcessWireNames.ReplyNode,
                "reply-publication-or-child-completion",
                ["activity", "canonical-request-obligation"]),
            ComposedConstruct(
                ProcessWireNames.DurableCutNode,
                "history-boundary",
                ["canonical-activation-continuation", "continue-as-new"]),
            ComposedConstruct(
                ProcessWireNames.InvokeProcessNode,
                "sub-orchestration",
                ["canonical-child-protocol-and-identity", "sub-orchestration"]),
            ConstrainedConstruct(
                ProcessWireNames.ForEachPartitionNode,
                "bounded-sub-orchestration-fan-out",
                ["canonical-partition-and-capacity-evidence", "orchestrator-task-scheduling", "sub-orchestration"],
                [FiniteWorkBoundary, PayloadBoundary]),
            ComposedConstruct(
                ProcessWireNames.RepeatAcrossActivationNode,
                "continue-as-new-recurrence",
                ["canonical-recurrence-evidence", "continue-as-new"]),
            NativeConstruct(ProcessWireNames.ReturnNode, "orchestration-completion"),
            NativeConstruct(ProcessWireNames.FailNode, "orchestration-failure"),

            ComposedGuarantee(
                ProcessInterpreterGuarantees.ExactDefinitionPinning,
                "pinned-orchestration-input",
                ["canonical-definition-reference", "orchestration-input"]),
            ComposedGuarantee(
                ProcessInterpreterGuarantees.StableExecutionIdentity,
                "canonical-identity-over-instance-history",
                ["canonical-execution-identities", "orchestration-instance-and-history"]),
            ComposedGuarantee(
                ProcessInterpreterGuarantees.DeterministicReplay,
                "canonical-decisions-over-history-replay",
                ["canonical-normalized-decisions", "orchestration-history-replay"]),
            ComposedGuarantee(
                ProcessInterpreterGuarantees.InputAdmissionAndDisposition,
                "canonical-inbox-over-external-events",
                ["canonical-input-dispositions", "external-event"]),
            ComposedGuarantee(
                ProcessInterpreterGuarantees.LifecycleControl,
                "canonical-control-over-client-lifecycle",
                ["canonical-control-receipts-and-safe-points", "client-lifecycle-operations"]),
            ComposedGuarantee(
                ProcessInterpreterGuarantees.DurableRequestRecovery,
                "canonical-request-recovery",
                ["activity", "canonical-request-state", "durable-timer", "external-event"]),
            ConstrainedGuarantee(
                ProcessInterpreterGuarantees.ExternalEffectDelivery,
                "at-least-once-dispatch-with-reconciliation",
                ["activity", "canonical-operation-identity-and-reconciliation"],
                [ExternalEffectDeliveryBoundary]),
            ComposedGuarantee(
                ProcessInterpreterGuarantees.ForkJoinChildLineage,
                "canonical-lineage-over-task-and-child-history",
                ["canonical-token-and-child-lineage", "orchestration-history-replay", "sub-orchestration"]),
            ConstrainedGuarantee(
                ProcessInterpreterGuarantees.BoundedWorkAndRecurrence,
                "bounded-work-and-history",
                ["canonical-work-bounds", "continue-as-new", "orchestrator-task-scheduling"],
                [FiniteWorkBoundary]),
            ComposedGuarantee(
                ProcessInterpreterGuarantees.DefinitionAndWorkerEvolution,
                "canonical-and-worker-version-composition",
                ["canonical-definition-compatibility", "orchestration-and-worker-versioning"]),
            ComposedGuarantee(
                ProcessInterpreterGuarantees.StatusTraceAndExplain,
                "canonical-observability-projection",
                ["canonical-status-trace-and-explain", "custom-status-and-tags", "orchestration-history"]),
            ConstrainedGuarantee(
                ProcessInterpreterGuarantees.SensitiveAndOversizedPayloads,
                "validated-payload-externalization",
                ["canonical-payload-contracts", "content-addressed-payload-reference"],
                [PayloadBoundary]),
            UnavailableGuarantee(
                ProcessInterpreterGuarantees.WholeDefinitionAtomicity,
                "multi-resource-atomicity")
        ]);

    static ProcessInterpreterCapabilityEvidence NativeConstruct(string wireName, string strategy) =>
        Evidence(
            ProcessInterpreterRequirementKey.ForConstruct(wireName),
            CapabilityRealizationKind.Native,
            strategy);

    static ProcessInterpreterCapabilityEvidence ComposedConstruct(
        string wireName,
        string strategy,
        ImmutableArray<string> supportingFacilities) => Evidence(
            ProcessInterpreterRequirementKey.ForConstruct(wireName),
            CapabilityRealizationKind.Composed,
            strategy,
            supportingFacilities);

    static ProcessInterpreterCapabilityEvidence ConstrainedConstruct(
        string wireName,
        string strategy,
        ImmutableArray<string> supportingFacilities,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> boundaries) => Evidence(
            ProcessInterpreterRequirementKey.ForConstruct(wireName),
            CapabilityRealizationKind.Constrained,
            strategy,
            supportingFacilities,
            boundaries);

    static ProcessInterpreterCapabilityEvidence ComposedGuarantee(
        ProcessInterpreterRequirementKey guarantee,
        string strategy,
        ImmutableArray<string> supportingFacilities) => Evidence(
            guarantee,
            CapabilityRealizationKind.Composed,
            strategy,
            supportingFacilities);

    static ProcessInterpreterCapabilityEvidence ConstrainedGuarantee(
        ProcessInterpreterRequirementKey guarantee,
        string strategy,
        ImmutableArray<string> supportingFacilities,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> boundaries) => Evidence(
            guarantee,
            CapabilityRealizationKind.Constrained,
            strategy,
            supportingFacilities,
            boundaries);

    static ProcessInterpreterCapabilityEvidence UnavailableGuarantee(
        ProcessInterpreterRequirementKey guarantee,
        string strategy) => Evidence(
            guarantee,
            CapabilityRealizationKind.Unavailable,
            strategy);

    static ProcessInterpreterCapabilityEvidence Evidence(
        ProcessInterpreterRequirementKey requirement,
        CapabilityRealizationKind realization,
        string strategy,
        ImmutableArray<string> supportingFacilities = default,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> boundaries = default)
    {
        var category = requirement.Category == ProcessInterpreterRequirementCategory.Construct
            ? "construct"
            : "guarantee";
        return new(
            new($"durable-task/realization/{category}/{requirement.Name}/{strategy}/v1"),
            requirement,
            realization,
            supportingFacilities.IsDefault
                ? []
                : [.. supportingFacilities.Select(FacilityEvidence)],
            boundaries);
    }

    static ProcessInterpreterCapabilityEvidenceId FacilityEvidence(string facility) => new(
        $"durable-task/facility/{facility}/v1");
}
