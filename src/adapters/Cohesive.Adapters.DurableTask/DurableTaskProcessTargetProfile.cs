using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Versioned Durable Task planning and executable capability profiles for canonical Processes.</summary>
/// <remarks>
/// The planning profile describes intended target realizations without advertising worker support. The executable
/// profile separately describes the bounded, conformance-tested closure that may authorize worker admission.
/// </remarks>
public static class DurableTaskProcessTargetProfile
{
    /// <summary>Stable Durable Task Scheduler Process interpretation-target identity.</summary>
    public static ProcessInterpreterTargetId Target { get; } = new(
        "cohesive.adapters.durable-task.scheduler");

    /// <summary>Stable identity of the current realization-planning profile.</summary>
    public static ProcessInterpreterCapabilityProfileId PlanningProfileId { get; } = new(
        "cohesive.adapters.durable-task.scheduler/realization-planning-v2");

    /// <summary>Stable identity of the bounded, conformance-tested executable profile.</summary>
    public static ProcessInterpreterCapabilityProfileId ExecutableProfileId { get; } = new(
        "cohesive.adapters.durable-task.scheduler/executable-v3");

    /// <summary>
    /// Boundary requiring every externally visible effect to supply stable idempotency or authored reconciliation.
    /// </summary>
    public static ProcessInterpreterOperatingBoundaryId ExternalEffectDeliveryBoundary { get; } = new(
        "durable-task/boundary/external-effect-idempotency-or-reconciliation/v1");

    /// <summary>
    /// Boundary requiring durable after-origin event visibility and target deduplication by the canonical scoped key.
    /// </summary>
    public static ProcessInterpreterOperatingBoundaryId DomainEventPublicationBoundary { get; } = new(
        "durable-task/boundary/domain-event-after-origin-target-deduplication/v1");

    /// <summary>Boundary requiring finite fan-out, recurrence, concurrency, capacity, and history growth.</summary>
    public static ProcessInterpreterOperatingBoundaryId FiniteWorkBoundary { get; } = new(
        "durable-task/boundary/finite-work-and-history/v1");

    /// <summary>
    /// Boundary requiring payload limits, redaction, and exact content-addressed externalization to be validated.
    /// </summary>
    public static ProcessInterpreterOperatingBoundaryId PayloadBoundary { get; } = new(
        "durable-task/boundary/payload-limit-redaction-and-externalization/v1");

    /// <summary>
    /// Complete planning profile. Every currently declared canonical construct and guarantee has one explicit
    /// native, composed, constrained, or unavailable disposition.
    /// </summary>
    public static ProcessInterpreterCapabilityProfile Planning { get; } = CreatePlanningProfile();

    /// <summary>
    /// Complete bounded executable profile. Every currently declared canonical construct and guarantee has one
    /// explicit executable or unavailable disposition.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Planning"/>, this profile may authorize worker admission. Its available dispositions must
    /// remain backed by executable conformance evidence. Unsupported protocol members remain present as unavailable
    /// evidence so omission can never imply support.
    /// </remarks>
    public static ProcessInterpreterCapabilityProfile Executable { get; } = CreateExecutableProfile();

    static ProcessInterpreterCapabilityProfile CreatePlanningProfile() => CreateCompleteProfile(
        PlanningProfileId,
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
            ComposedConstruct(
                ProcessWireNames.CancellationFinalizerNode,
                "authored-cancellation-finalization",
                ["canonical-cancellation-evidence", "sub-orchestration", "external-event", "orchestration-history-replay"]),
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
                "canonical-control-over-external-events",
                ["canonical-control-state-receipts-intents", "external-event", "custom-status"]),
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

    static ProcessInterpreterCapabilityProfile CreateExecutableProfile() => CreateCompleteProfile(
        ExecutableProfileId,
        [
            ExecutableComposedConstruct(
                ProcessWireNames.InvokeTransitionNode,
                "canonical-transition-activity",
                ["sequential-interpreter", "durable-operation-reference-executor"]),
            ExecutableComposedConstruct(
                ProcessWireNames.EvaluateRelationNode,
                "canonical-relation-activity",
                ["sequential-interpreter", "durable-operation-reference-executor"]),
            ExecutableComposedConstruct(
                ProcessWireNames.RequestNode,
                "canonical-request-protocol",
                ["sequential-interpreter", "durable-operation-reference-executor", "external-event"]),
            ExecutableConstrainedConstruct(
                ProcessWireNames.EmitEventNode,
                "target-deduplicated-domain-event-activity",
                ["domain-event-publication-activity", "canonical-envelope-and-idempotency-key"],
                [DomainEventPublicationBoundary]),
            ExecutableComposedConstruct(
                ProcessWireNames.SendSignalNode,
                "canonical-process-signal",
                ["sequential-interpreter", "canonical-signal-admission", "external-event"]),
            ExecutableNativeConstruct(ProcessWireNames.ChoiceNode, "canonical-choice-decision"),
            ExecutableNativeConstruct(ProcessWireNames.MatchNode, "canonical-match-decision"),
            ExecutableComposedConstruct(
                ProcessWireNames.ForkNode,
                "canonical-bounded-fork",
                ["sequential-interpreter", "canonical-fork-membership"]),
            ExecutableComposedConstruct(
                ProcessWireNames.JoinNode,
                "canonical-join-arbitration",
                ["sequential-interpreter", "canonical-join-decision"]),
            ExecutableComposedConstruct(
                ProcessWireNames.AwaitMatchNode,
                "canonical-input-timer-arbitration",
                ["sequential-interpreter", "canonical-inbox-arbitration", "durable-timer", "external-event"]),
            ExecutableComposedConstruct(
                ProcessWireNames.TimerNode,
                "canonical-durable-timer",
                ["sequential-interpreter", "canonical-timer-occurrence", "durable-timer"]),
            ExecutableUnavailableConstruct(ProcessWireNames.ReplyNode, "request-reply-discharge"),
            ExecutableComposedConstruct(
                ProcessWireNames.DurableCutNode,
                "canonical-history-boundary",
                ["sequential-interpreter", "canonical-activation-continuation", "continue-as-new"]),
            ExecutableComposedConstruct(
                ProcessWireNames.InvokeProcessNode,
                "canonical-child-process",
                ["sequential-interpreter", "canonical-child-lineage", "sub-orchestration"]),
            ExecutableConstrainedConstruct(
                ProcessWireNames.ForEachPartitionNode,
                "bounded-partition-fan-out",
                ["sequential-interpreter", "canonical-partition-capacity", "sub-orchestration"],
                [FiniteWorkBoundary, PayloadBoundary]),
            ExecutableComposedConstruct(
                ProcessWireNames.RepeatAcrossActivationNode,
                "canonical-bounded-recurrence",
                ["sequential-interpreter", "canonical-recurrence-evidence", "continue-as-new"]),
            ExecutableComposedConstruct(
                ProcessWireNames.CancellationFinalizerNode,
                "canonical-authored-cancellation-finalization",
                ["sequential-interpreter", "canonical-cancellation-evidence", "sub-orchestration", "external-event"]),
            ExecutableNativeConstruct(ProcessWireNames.ReturnNode, "canonical-return"),
            ExecutableNativeConstruct(ProcessWireNames.FailNode, "canonical-failure"),

            ExecutableComposedGuarantee(
                ProcessInterpreterGuarantees.ExactDefinitionPinning,
                "exact-definition-catalog",
                ["exact-plan-catalog", "orchestration-input"]),
            ExecutableComposedGuarantee(
                ProcessInterpreterGuarantees.StableExecutionIdentity,
                "canonical-execution-identities",
                ["sequential-interpreter", "orchestration-instance-and-history"]),
            ExecutableComposedGuarantee(
                ProcessInterpreterGuarantees.DeterministicReplay,
                "canonical-replay-decisions",
                ["sequential-interpreter", "orchestration-history-replay"]),
            ExecutableComposedGuarantee(
                ProcessInterpreterGuarantees.InputAdmissionAndDisposition,
                "canonical-input-arbitration",
                ["sequential-interpreter", "canonical-input-dispositions", "external-event"]),
            ExecutableComposedGuarantee(
                ProcessInterpreterGuarantees.LifecycleControl,
                "canonical-lifecycle-control",
                ["sequential-interpreter", "canonical-control-state", "external-event", "custom-status"]),
            ExecutableComposedGuarantee(
                ProcessInterpreterGuarantees.DurableRequestRecovery,
                "canonical-request-recovery",
                ["durable-operation-reference-executor", "durable-timer", "external-event"]),
            ExecutableConstrainedGuarantee(
                ProcessInterpreterGuarantees.ExternalEffectDelivery,
                "idempotent-or-reconciled-activity-dispatch",
                ["durable-operation-reference-executor", "canonical-operation-identity"],
                [ExternalEffectDeliveryBoundary]),
            ExecutableComposedGuarantee(
                ProcessInterpreterGuarantees.ForkJoinChildLineage,
                "canonical-fork-child-lineage",
                ["sequential-interpreter", "canonical-token-and-child-lineage", "sub-orchestration"]),
            ExecutableConstrainedGuarantee(
                ProcessInterpreterGuarantees.BoundedWorkAndRecurrence,
                "canonical-bounded-work",
                ["sequential-interpreter", "canonical-work-bounds", "continue-as-new"],
                [FiniteWorkBoundary]),
            ExecutableComposedGuarantee(
                ProcessInterpreterGuarantees.DefinitionAndWorkerEvolution,
                "canonical-worker-version-composition",
                ["exact-plan-catalog", "canonical-definition-compatibility", "worker-versioning"]),
            ExecutableComposedGuarantee(
                ProcessInterpreterGuarantees.StatusTraceAndExplain,
                "canonical-observability-projection",
                ["canonical-status", "canonical-normalized-trace", "canonical-explain"]),
            ExecutableConstrainedGuarantee(
                ProcessInterpreterGuarantees.SensitiveAndOversizedPayloads,
                "validated-payload-boundary",
                ["canonical-payload-contracts", "content-addressed-payload-reference"],
                [PayloadBoundary]),
            ExecutableUnavailableGuarantee(
                ProcessInterpreterGuarantees.WholeDefinitionAtomicity,
                "multi-resource-atomicity")
        ]);

    static ProcessInterpreterCapabilityProfile CreateCompleteProfile(
        ProcessInterpreterCapabilityProfileId id,
        ImmutableArray<ProcessInterpreterCapabilityEvidence> evidence)
    {
        var declared = ProcessNodeConstructCatalog.DeclaredRequirements
            .Concat(ProcessInterpreterGuarantees.All)
            .ToHashSet();
        var groups = evidence.GroupBy(static candidate => candidate.Requirement).ToDictionary(static group => group.Key);
        var missing = declared.Where(requirement => !groups.ContainsKey(requirement)).ToArray();
        var duplicated = groups.Where(static group => group.Value.Count() != 1).Select(static group => group.Key).ToArray();
        var extra = groups.Keys.Where(requirement => !declared.Contains(requirement)).ToArray();
        var duplicatedEvidenceIds = evidence
            .GroupBy(static candidate => candidate.Id)
            .Where(static group => group.Count() != 1)
            .Select(static group => group.Key.Value)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length != 0 || duplicated.Length != 0 || extra.Length != 0 || duplicatedEvidenceIds.Length != 0)
        {
            throw new InvalidOperationException(
                $"Durable Task Process profile '{id.Value}' must dispose every canonical requirement exactly once. "
                + $"Missing: {Describe(missing)}; duplicated: {Describe(duplicated)}; extra: {Describe(extra)}; "
                + $"duplicated evidence identities: {DescribeEvidence(duplicatedEvidenceIds)}.");
        }

        return new(id, Target, evidence);

        static string Describe(IEnumerable<ProcessInterpreterRequirementKey> requirements)
        {
            var description = string.Join(
                ", ",
                requirements
                    .OrderBy(static requirement => requirement.Category)
                    .ThenBy(static requirement => requirement.Name, StringComparer.Ordinal)
                    .Select(static requirement => requirement.ToString()));
            return description.Length == 0 ? "none" : description;
        }

        static string DescribeEvidence(IEnumerable<string> identities)
        {
            var description = string.Join(", ", identities);
            return description.Length == 0 ? "none" : description;
        }
    }

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

    static ProcessInterpreterCapabilityEvidence UnavailableConstruct(string wireName, string strategy) =>
        Evidence(
            ProcessInterpreterRequirementKey.ForConstruct(wireName),
            CapabilityRealizationKind.Unavailable,
            strategy);

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

    static ProcessInterpreterCapabilityEvidence ExecutableNativeConstruct(string wireName, string strategy) =>
        ExecutableEvidence(
            ProcessInterpreterRequirementKey.ForConstruct(wireName),
            CapabilityRealizationKind.Native,
            strategy);

    static ProcessInterpreterCapabilityEvidence ExecutableComposedConstruct(
        string wireName,
        string strategy,
        ImmutableArray<string> supportingEvidence) => ExecutableEvidence(
            ProcessInterpreterRequirementKey.ForConstruct(wireName),
            CapabilityRealizationKind.Composed,
            strategy,
            supportingEvidence);

    static ProcessInterpreterCapabilityEvidence ExecutableConstrainedConstruct(
        string wireName,
        string strategy,
        ImmutableArray<string> supportingEvidence,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> boundaries) => ExecutableEvidence(
            ProcessInterpreterRequirementKey.ForConstruct(wireName),
            CapabilityRealizationKind.Constrained,
            strategy,
            supportingEvidence,
            boundaries);

    static ProcessInterpreterCapabilityEvidence ExecutableUnavailableConstruct(string wireName, string strategy) =>
        ExecutableEvidence(
            ProcessInterpreterRequirementKey.ForConstruct(wireName),
            CapabilityRealizationKind.Unavailable,
            strategy);

    static ProcessInterpreterCapabilityEvidence ExecutableComposedGuarantee(
        ProcessInterpreterRequirementKey guarantee,
        string strategy,
        ImmutableArray<string> supportingEvidence) => ExecutableEvidence(
            guarantee,
            CapabilityRealizationKind.Composed,
            strategy,
            supportingEvidence);

    static ProcessInterpreterCapabilityEvidence ExecutableConstrainedGuarantee(
        ProcessInterpreterRequirementKey guarantee,
        string strategy,
        ImmutableArray<string> supportingEvidence,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> boundaries) => ExecutableEvidence(
            guarantee,
            CapabilityRealizationKind.Constrained,
            strategy,
            supportingEvidence,
            boundaries);

    static ProcessInterpreterCapabilityEvidence ExecutableUnavailableGuarantee(
        ProcessInterpreterRequirementKey guarantee,
        string strategy) => ExecutableEvidence(
            guarantee,
            CapabilityRealizationKind.Unavailable,
            strategy);

    static ProcessInterpreterCapabilityEvidence ExecutableEvidence(
        ProcessInterpreterRequirementKey requirement,
        CapabilityRealizationKind realization,
        string strategy,
        ImmutableArray<string> supportingEvidence = default,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> boundaries = default)
    {
        var category = requirement.Category == ProcessInterpreterRequirementCategory.Construct
            ? "construct"
            : "guarantee";
        return new(
            new($"durable-task/executable/{category}/{requirement.Name}/{strategy}/v1"),
            requirement,
            realization,
            supportingEvidence.IsDefault
                ? []
                : [.. supportingEvidence.Select(ExecutableSupportingEvidence)],
            boundaries);
    }

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

    static ProcessInterpreterCapabilityEvidenceId ExecutableSupportingEvidence(string evidence) => new(
        $"durable-task/executable-evidence/{evidence}/v1");
}
