using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Compilation;

namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Exact pairing of one compiler-acquired Process requirement with the selected Durable Task realization evidence.
/// </summary>
public sealed record DurableTaskProcessRequirementRealization
{
    internal DurableTaskProcessRequirementRealization(
        ProcessInterpreterRequirement requirement,
        ProcessInterpreterRealizationDecision decision)
    {
        if (requirement.Key != decision.Requirement)
        {
            throw new ArgumentException(
                "A Durable Task requirement realization must pair evidence with the exact source requirement.",
                nameof(decision));
        }

        Requirement = requirement;
        Decision = decision;
    }

    /// <summary>Exact compiler-acquired requirement, including canonical node and linked-definition provenance.</summary>
    public ProcessInterpreterRequirement Requirement { get; }

    /// <summary>Selected Durable Task disposition and attributable target evidence.</summary>
    public ProcessInterpreterRealizationDecision Decision { get; }
}

/// <summary>
/// Deterministic Durable Task physical realization plan over one exact canonical <see cref="CompiledProcessPlan"/>.
/// </summary>
/// <remarks>
/// This value does not contain an independently authored Durable Task workflow. The canonical plan remains semantic
/// authority; the requirement realizations are target-owned physical evidence consumed by the generic
/// interpreter. A plan admits worker execution only when its realization carries the exact executable profile.
/// </remarks>
public sealed class DurableTaskProcessRealizationPlan
{
    internal DurableTaskProcessRealizationPlan(
        CompiledProcessPlan canonicalPlan,
        ProcessInterpreterRealizationReport realization)
    {
        if (!realization.IsRealizable)
        {
            throw new ArgumentException(
                "A Durable Task physical plan requires a successful exhaustive realization report.",
                nameof(realization));
        }
        if (canonicalPlan.DefinitionReference != realization.Inventory.Definition)
        {
            throw new ArgumentException(
                "A Durable Task physical plan and realization report must reference the same exact definition.",
                nameof(realization));
        }
        if (realization.TargetProfile.Target != DurableTaskProcessTargetProfile.Target)
        {
            throw new ArgumentException(
                "A Durable Task physical plan requires Durable Task target evidence.",
                nameof(realization));
        }

        var decisions = realization.Decisions.ToDictionary(static decision => decision.Requirement);
        CanonicalPlan = canonicalPlan;
        Realization = realization;
        Requirements =
        [
            .. realization.Inventory.Requirements.Select(requirement =>
                new DurableTaskProcessRequirementRealization(requirement, decisions[requirement.Key]))
        ];
    }

    /// <summary>Exact compiled canonical Process plan that remains semantic authority.</summary>
    public CompiledProcessPlan CanonicalPlan { get; }

    /// <summary>Exact definition identity, revision, and semantic fingerprint retained by this plan.</summary>
    public ExecutionDefinitionReference Definition => CanonicalPlan.DefinitionReference;

    /// <summary>Complete target-neutral report that authorized physical planning.</summary>
    public ProcessInterpreterRealizationReport Realization { get; }

    /// <summary>
    /// Every source requirement paired with exactly one Durable Task decision in deterministic inventory order.
    /// </summary>
    public ImmutableArray<DurableTaskProcessRequirementRealization> Requirements { get; }
}

/// <summary>Result of compiling one exact canonical Process against a Durable Task capability profile.</summary>
public sealed class DurableTaskProcessPlanningResult
{
    internal DurableTaskProcessPlanningResult(
        ProcessInterpreterRealizationReport realization,
        DurableTaskProcessRealizationPlan? plan)
    {
        Realization = realization;
        Plan = plan;
    }

    /// <summary>Exhaustive target-neutral disposition ledger and structured diagnostics.</summary>
    public ProcessInterpreterRealizationReport Realization { get; }

    /// <summary>Deterministic physical plan, or null when any demanded semantic is unavailable or invalid.</summary>
    public DurableTaskProcessRealizationPlan? Plan { get; }

    /// <summary>Whether compilation produced a complete physical realization plan.</summary>
    public bool IsSuccessful => Plan is not null;
}

/// <summary>Compiles exact canonical Process plans against Durable Task planning or executable profiles.</summary>
public static class DurableTaskProcessRealizationCompiler
{
    /// <summary>
    /// Matches the exact plan to the versioned Durable Task planning profile and produces a physical plan only when
    /// every compiler-acquired requirement has one exact available realization.
    /// </summary>
    /// <param name="plan">Successfully compiled canonical Process plan.</param>
    /// <returns>A realization report and, when semantically feasible, a deterministic physical plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static DurableTaskProcessPlanningResult Compile(CompiledProcessPlan plan)
        => Compile(plan, DurableTaskProcessTargetProfile.Planning);

    /// <summary>
    /// Matches the exact plan to the versioned, bounded executable profile and produces a physical plan only when
    /// every demanded semantic has conformance-backed executable evidence.
    /// </summary>
    /// <param name="plan">Successfully compiled canonical Process plan.</param>
    /// <returns>An executable realization report and, when supported, a deployable physical plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static DurableTaskProcessPlanningResult CompileExecutable(CompiledProcessPlan plan)
        => Compile(plan, DurableTaskProcessTargetProfile.Executable);

    static DurableTaskProcessPlanningResult Compile(
        CompiledProcessPlan plan,
        ProcessInterpreterCapabilityProfile targetProfile)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var realization = ProcessInterpreterRealizationCompiler.Compile(
            plan,
            targetProfile);
        var physicalPlan = realization.IsRealizable
            ? new DurableTaskProcessRealizationPlan(plan, realization)
            : null;
        return new(realization, physicalPlan);
    }
}
